using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;

namespace DXVKCompanion.DXVK
{
    public class DxvkGithubClient
    {
        private const string ApiUrl = "https://api.github.com/repos/doitsujin/dxvk/releases/latest";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

        private readonly HttpClient _httpClient;
        private readonly CacheStore _cacheStore;
        private readonly SemaphoreSlim _gate = new(1, 1);
        // Accessed only under _gate. Disk persistence may fail, but this client
        // must still honor the daily window and coalesce queued callers.
        // Clear after a committed write: persisted state remains authoritative.
        private DateTime? _lastLatestAttempt;

        // Registry lifetime includes queued users; only active participants
        // share unpersisted attempt state. Idle entries retain no cache identity.
        private sealed class LatestCacheGate
        {
            internal readonly SemaphoreSlim Semaphore = new(1, 1);
            internal int Users;
            internal DateTime? UnpersistedAttempt;
        }
        private static readonly object CacheGateRegistryLock = new();
        private static readonly Dictionary<string, LatestCacheGate> CacheGates = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        private static LatestCacheGate RentCacheGate(string key)
        {
            lock (CacheGateRegistryLock)
            {
                if (!CacheGates.TryGetValue(key, out var gate))
                {
                    gate = new LatestCacheGate();
                    CacheGates.Add(key, gate);
                }
                gate.Users++;
                return gate;
            }
        }

        private static void ReturnCacheGate(string key, LatestCacheGate gate, bool entered, DateTime? attempt)
        {
            if (entered)
            {
                gate.UnpersistedAttempt = attempt;
                gate.Semaphore.Release();
            }
            lock (CacheGateRegistryLock)
            {
                if (--gate.Users == 0)
                {
                    CacheGates.Remove(key);
                    gate.Semaphore.Dispose();
                }
            }
        }

        internal Action? BeforeSharedCacheWait { get; set; }
        private IOperationLeaseCoordinator? _processLeaseCoordinator;
        internal IOperationLeaseCoordinator ProcessLeaseCoordinator
        {
            get => _processLeaseCoordinator ??= new OperationLeaseCoordinator();
            set => _processLeaseCoordinator = value;
        }

        public const int CatalogPageSize = 30;
        public const int MaximumCatalogPages = 32;
        internal const int MaximumMetadataBytes = 2 * 1024 * 1024;
        internal HttpClient HttpClient => _httpClient;

        public sealed record OfficialReleasePage(IReadOnlyList<ReleaseInfo> Releases, bool HasMore);

        // Page URLs are constructed locally. An untrusted Link header is never followed.
        public async Task<OfficialReleasePage> FetchOfficialReleasePageAsync(int page, CancellationToken cancellationToken = default)
        {
            if (page < 1 || page > MaximumCatalogPages) throw new ArgumentOutOfRangeException(nameof(page));
            using var document = await ReadOfficialMetadataAsync(
                $"https://api.github.com/repos/doitsujin/dxvk/releases?per_page={CatalogPageSize}&page={page}", cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid official release catalog.");
            var entries = document.RootElement.EnumerateArray().ToArray();
            if (entries.Length > CatalogPageSize) throw new InvalidDataException("Official release page exceeds limit.");
            var releases = new List<ReleaseInfo>();
            foreach (var entry in entries)
            {
                var release = ParseOfficialRelease(entry);
                if (release != null) releases.Add(release);
            }
            return new OfficialReleasePage(releases, entries.Length == CatalogPageSize && page < MaximumCatalogPages);
        }

        public async Task<ReleaseInfo> FetchOfficialReleaseAsync(string tag, CancellationToken cancellationToken = default)
        {
            if (!IsOfficialTag(tag)) throw new InvalidDataException("Invalid official release tag.");
            using var document = await ReadOfficialMetadataAsync(
                $"https://api.github.com/repos/doitsujin/dxvk/releases/tags/{tag}", cancellationToken);
            var release = ParseOfficialRelease(document.RootElement);
            if (release == null || release.Version != tag) throw new InvalidDataException("Official release identity does not match the requested tag.");
            return release;
        }

        private async Task<JsonDocument> ReadOfficialMetadataAsync(string url, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("DXVK-Companion/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = await ReadBoundedAsync(stream, MaximumMetadataBytes, timeout.Token);
            return JsonDocument.Parse(bytes);
        }

        internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
        {
            using var result = new MemoryStream();
            byte[] buffer = new byte[81920];
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), token)) != 0)
            {
                if (result.Length + count > limit) throw new InvalidDataException("Official response exceeds size limit.");
                result.Write(buffer, 0, count);
            }
            return result.ToArray();
        }

        internal static bool IsOfficialTag(string? tag) => tag != null && tag.Length <= 48 &&
            Regex.IsMatch(tag, @"\Av[0-9]+(?:\.[0-9]+){1,3}\z", RegexOptions.CultureInvariant);

        internal static bool IsOfficialAsset(ReleaseInfo release) => IsOfficialTag(release.Version) &&
            release.ReleaseId > 0 && release.AssetId > 0 && release.AssetSize > 0 &&
            release.AssetSize <= DxvkReleaseAcquirer.MaximumArchiveBytes &&
            release.AssetName == $"dxvk-{release.Version[1..]}.tar.gz" &&
            release.DownloadUrl == $"https://github.com/doitsujin/dxvk/releases/download/{release.Version}/{release.AssetName}" &&
            (release.AssetDigest == null || Regex.IsMatch(release.AssetDigest, @"\Asha256:[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant));

        private static ReleaseInfo? ParseOfficialRelease(JsonElement entry)
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("draft", out var draft) ||
                draft.ValueKind != JsonValueKind.False || !entry.TryGetProperty("prerelease", out var prerelease) ||
                prerelease.ValueKind != JsonValueKind.False || !entry.TryGetProperty("tag_name", out var tagValue) ||
                tagValue.ValueKind != JsonValueKind.String) return null;
            string tag = tagValue.GetString()!;
            if (!IsOfficialTag(tag) || !entry.TryGetProperty("id", out var id) || !id.TryGetInt64(out long releaseId) ||
                !entry.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            ReleaseInfo? found = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) ||
                    name.ValueKind != JsonValueKind.String || name.GetString() != $"dxvk-{tag[1..]}.tar.gz") continue;
                // A duplicate matching asset is ambiguous even if just one has usable metadata.
                if (found != null) return null;
                if (!asset.TryGetProperty("id", out var assetId) || !assetId.TryGetInt64(out long aid) ||
                    !asset.TryGetProperty("size", out var size) || !size.TryGetInt64(out long length) ||
                    !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String) return null;
                string? digest = null;
                if (asset.TryGetProperty("digest", out var digestValue) && digestValue.ValueKind != JsonValueKind.Null)
                {
                    if (digestValue.ValueKind != JsonValueKind.String) return null;
                    digest = digestValue.GetString();
                }
                found = new ReleaseInfo { Version = tag, DownloadUrl = url.GetString()!, ReleaseId = releaseId,
                    AssetId = aid, AssetName = name.GetString()!, AssetSize = length, AssetDigest = digest };
                if (!IsOfficialAsset(found)) return null;
            }
            return found;
        }

        public Func<DateTime> UtcNowSeam { get; set; } = () => DateTime.UtcNow;

        public DxvkGithubClient(HttpClient httpClient, CacheStore cacheStore, Func<DateTime>? utcNowSeam = null)
        {
            _httpClient = httpClient;
            _cacheStore = cacheStore;
            if (utcNowSeam != null)
            {
                UtcNowSeam = utcNowSeam;
            }
        }

        public async Task<ReleaseInfo?> FetchLatestReleaseAsync(CancellationToken cancellationToken = default)
        {
            var now = UtcNowSeam();

            // Fast path: if cache exists and is not due, return immediately without acquiring gate
            var cached = _cacheStore.LoadCachedRelease();
            if (cached != null && !cached.IsDue(now))
            {
                if (cached.HasValidRelease)
                {
                    Logger.Log($"DxvkGithubClient: using cached release {cached.Release.Version} (cached at {cached.CachedAt:u}, last attempt {cached.EffectiveLastAttempt:u}).");
                    return cached.Release;
                }

                Logger.Log("DxvkGithubClient: release check throttled within 24h of prior failed attempt; no cached release available.");
                return null;
            }

            await _gate.WaitAsync(cancellationToken);
            LatestCacheGate? shared = null;
            string? cacheKey = null;
            bool sharedEntered = false;
            IOperationLease? processLease = null;
            try
            {
                try { cacheKey = Path.GetFullPath(_cacheStore.CacheFile); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Unusable storage still follows the existing network/cache
                    // failure path, protected by this client's private gate.
                }
                if (cacheKey != null)
                {
                    shared = RentCacheGate(cacheKey);
                    BeforeSharedCacheWait?.Invoke();
                    await shared.Semaphore.WaitAsync(cancellationToken);
                    sharedEntered = true;
                    if (shared.UnpersistedAttempt.HasValue &&
                        (!_lastLatestAttempt.HasValue || shared.UnpersistedAttempt.Value > _lastLatestAttempt.Value))
                        _lastLatestAttempt = shared.UnpersistedAttempt;
                }
                now = UtcNowSeam();
                // Another client/store may have committed an attempt while we waited.
                cached = _cacheStore.LoadCachedRelease();
                if ((cached != null && !cached.IsDue(now)) ||
                    (_lastLatestAttempt.HasValue && now - _lastLatestAttempt.Value < TimeSpan.FromHours(24)))
                {
                    if (cached?.HasValidRelease == true)
                    {
                        Logger.Log($"DxvkGithubClient: using cached release {cached.Release.Version} after gate acquisition.");
                        return cached.Release;
                    }

                    Logger.Log("DxvkGithubClient: release check throttled after gate acquisition; no cached release available.");
                    return null;
                }

                if (cacheKey != null)
                {
                    try
                    {
                        processLease = await ProcessLeaseCoordinator.TryAcquireStoreLeaseAsync(cacheKey,
                            OperationLeaseCoordinator.DefaultTimeout, cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        Logger.Log("DxvkGithubClient: cross-process cache coordination unavailable; no HTTP attempted.");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    now = UtcNowSeam();
                    cached = _cacheStore.LoadCachedRelease();
                    if (processLease == null || (cached != null && !cached.IsDue(now)))
                        return cached?.HasValidRelease == true ? cached.Release : null;
                }

                _lastLatestAttempt = now;
                return await FetchFromNetworkUnderGateAsync(cached, now, cancellationToken);
            }
            finally
            {
                try
                {
                    try { processLease?.Dispose(); }
                    finally
                    {
                        if (shared != null) ReturnCacheGate(cacheKey!, shared, sharedEntered, _lastLatestAttempt);
                    }
                }
                finally { _gate.Release(); }
            }
        }

        private async Task<ReleaseInfo?> FetchFromNetworkUnderGateAsync(CachedRelease? cached, DateTime now, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);

                // A validator is only useful with metadata to revalidate. Corrupt,
                // combined or wildcard cache values must not prevent a normal fetch.
                EntityTagHeaderValue? validator = null;
                bool conditional = cached?.HasValidRelease == true &&
                    EntityTagHeaderValue.TryParse(cached.ETag, out validator) &&
                    validator.Tag != "*";
                if (conditional)
                    request.Headers.IfNoneMatch.Add(validator!);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(RequestTimeout);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified && conditional)
                {
                    _cacheStore.SaveCachedRelease(cached!.Release, cached.ETag, cachedAt: now, lastAttemptAt: now);
                    _lastLatestAttempt = null;
                    return cached.Release;
                }

                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                byte[] bytes = await ReadBoundedAsync(stream, MaximumMetadataBytes, cts.Token);
                using var doc = JsonDocument.Parse(bytes);
                var release = ParseOfficialRelease(doc.RootElement)
                    ?? throw new InvalidDataException("GitHub API response did not identify one stable official Windows release asset.");
                string? etag = response.Headers.ETag?.ToString();

                _cacheStore.SaveCachedRelease(release, etag, cachedAt: now, lastAttemptAt: now);
                _lastLatestAttempt = null;
                return release;
            }
            catch (OperationCanceledException)
            {
                Logger.Log($"DxvkGithubClient: request to GitHub API timed out after {RequestTimeout.TotalSeconds}s or was cancelled.");
                RecordFailedAttemptSafe(now);
                return cached?.HasValidRelease == true ? cached.Release : null;
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkGithubClient: failed to fetch latest release: {ex.GetType().Name} - {ex.Message}");
                RecordFailedAttemptSafe(now);
                return cached?.HasValidRelease == true ? cached.Release : null;
            }
        }

        private void RecordFailedAttemptSafe(DateTime attemptAt)
        {
            try
            {
                _cacheStore.RecordFailedAttempt(attemptAt);
                _lastLatestAttempt = null;
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkGithubClient: failed to record failed attempt in cache: {ex.GetType().Name} - {ex.Message}");
            }
        }
    }
}
