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
        private const string LatestApiUrl = "https://api.github.com/repos/doitsujin/dxvk/releases/latest";
        private const string TagsApiUrlPrefix = "https://api.github.com/repos/doitsujin/dxvk/releases/tags/";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

        private readonly HttpClient _httpClient;
        private readonly CacheStore _cacheStore;

        public DxvkGithubClient(HttpClient httpClient, CacheStore cacheStore)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
        }

        public async Task<ReleaseInfo?> FetchLatestReleaseAsync()
        {
            var cached = _cacheStore.LoadCachedRelease();

            try
            {
                if (cached != null && !cached.IsExpired())
                {
                    Logger.Log($"DxvkGithubClient: using cached release {cached.Release.Version} (cached at {cached.CachedAt:u}).");
                    return cached.Release;
                }

                var request = new HttpRequestMessage(HttpMethod.Get, LatestApiUrl);

                if (cached != null && !string.IsNullOrWhiteSpace(cached.ETag))
                    request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cached.ETag));

                using var cts = new CancellationTokenSource(RequestTimeout);
                var response = await _httpClient.SendAsync(request, cts.Token);

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cached != null)
                {
                    _cacheStore.SaveCachedRelease(cached.Release, cached.ETag);
                    return cached.Release;
                }

                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync();
                var release = ParseReleaseJson(json);
                if (release != null)
                {
                    string? etag = response.Headers.ETag?.Tag;
                    _cacheStore.SaveCachedRelease(release, etag);
                    return release;
                }

                return cached?.Release;
            }
            catch (OperationCanceledException)
            {
                Logger.Log($"DxvkGithubClient: request to GitHub API timed out after {RequestTimeout.TotalSeconds}s.");
                return cached?.Release;
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkGithubClient: failed to fetch latest release: {ex.GetType().Name} - {ex.Message}");
                return cached?.Release;
            }
        }

        public async Task<ReleaseInfo?> FetchReleaseByVersionAsync(string version)
        {
            if (string.IsNullOrWhiteSpace(version) || string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase))
            {
                return await FetchLatestReleaseAsync();
            }

            var cached = _cacheStore.LoadCachedRelease(version);
            if (cached != null && !cached.IsExpired())
            {
                return cached.Release;
            }

            string cleanVersion = version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version[1..] : version;
            string[] tagCandidates = { "v" + cleanVersion, cleanVersion };

            foreach (var tag in tagCandidates)
            {
                try
                {
                    string url = TagsApiUrlPrefix + tag;
                    var request = new HttpRequestMessage(HttpMethod.Get, url);

                    if (cached != null && !string.IsNullOrWhiteSpace(cached.ETag))
                        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cached.ETag));

                    using var cts = new CancellationTokenSource(RequestTimeout);
                    var response = await _httpClient.SendAsync(request, cts.Token);

                    if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cached != null)
                    {
                        return cached.Release;
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        continue;

                    response.EnsureSuccessStatusCode();

                    string json = await response.Content.ReadAsStringAsync();
                    var release = ParseReleaseJson(json);
                    if (release != null)
                    {
                        string? etag = response.Headers.ETag?.Tag;
                        _cacheStore.SaveCachedRelease(release, etag, cleanVersion);
                        return release;
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Log($"DxvkGithubClient: request for release {version} timed out.");
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Log($"DxvkGithubClient: failed to fetch release {version}: {ex.GetType().Name} - {ex.Message}");
                }
            }

            return cached?.Release;
        }

        private static ReleaseInfo? ParseReleaseJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                string tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                string version = tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tagName[1..] : tagName;

                string assetUrl = "";
                string? archiveSha256 = null;

                if (doc.RootElement.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsElem.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";

                        // The Windows DXVK archive is named dxvk-{version}.tar.gz and strictly excludes "native"
                        if (name.StartsWith("dxvk-", StringComparison.OrdinalIgnoreCase) &&
                            !name.Contains("native", StringComparison.OrdinalIgnoreCase) &&
                            name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                        {
                            assetUrl = asset.GetProperty("browser_download_url").GetString() ?? "";

                            // Extract GitHub's native asset digest if published (e.g. "sha256:...")
                            if (asset.TryGetProperty("digest", out var digestElem))
                            {
                                string? raw = digestElem.GetString();
                                if (!string.IsNullOrWhiteSpace(raw))
                                {
                                    archiveSha256 = raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                                        ? raw["sha256:".Length..]
                                        : raw;
                                }
                            }
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(assetUrl))
                    return null;

                return new ReleaseInfo
                {
                    Version = version,
                    DownloadUrl = assetUrl,
                    ArchiveSha256 = archiveSha256
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
