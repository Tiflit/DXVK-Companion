using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.Models;
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
            try
            {
                now = UtcNowSeam();
                // Re-evaluate persisted state after acquiring the gate (concurrent caller might have satisfied it)
                cached = _cacheStore.LoadCachedRelease();
                if (cached != null && !cached.IsDue(now))
                {
                    if (cached.HasValidRelease)
                    {
                        Logger.Log($"DxvkGithubClient: using cached release {cached.Release.Version} after gate acquisition.");
                        return cached.Release;
                    }

                    Logger.Log("DxvkGithubClient: release check throttled after gate acquisition; no cached release available.");
                    return null;
                }

                return await FetchFromNetworkUnderGateAsync(cached, now, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<ReleaseInfo?> FetchFromNetworkUnderGateAsync(CachedRelease? cached, DateTime now, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);

                if (cached != null && !string.IsNullOrWhiteSpace(cached.ETag))
                    request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cached.ETag));

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(RequestTimeout);

                using var response = await _httpClient.SendAsync(request, cts.Token);

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cached != null && cached.HasValidRelease)
                {
                    _cacheStore.SaveCachedRelease(cached.Release, cached.ETag, cachedAt: now, lastAttemptAt: now);
                    return cached.Release;
                }

                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                string tagName = "";
                if (doc.RootElement.TryGetProperty("tag_name", out var tagProp))
                {
                    tagName = tagProp.GetString() ?? "";
                }

                string assetUrl = "";
                if (doc.RootElement.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        string name = "";
                        if (asset.TryGetProperty("name", out var nameProp))
                        {
                            name = nameProp.GetString() ?? "";
                        }

                        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                        {
                            if (asset.TryGetProperty("browser_download_url", out var urlProp))
                            {
                                assetUrl = urlProp.GetString() ?? "";
                            }
                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(tagName) ||
                    string.Equals(tagName, "unknown", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(assetUrl))
                {
                    throw new InvalidOperationException("GitHub API response contained invalid or missing release metadata.");
                }

                var release = new ReleaseInfo { Version = tagName, DownloadUrl = assetUrl };
                string? etag = response.Headers.ETag?.Tag;

                _cacheStore.SaveCachedRelease(release, etag, cachedAt: now, lastAttemptAt: now);
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
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkGithubClient: failed to record failed attempt in cache: {ex.GetType().Name} - {ex.Message}");
            }
        }
    }
}
