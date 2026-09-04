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

        public DxvkGithubClient(HttpClient httpClient, CacheStore cacheStore)
        {
            _httpClient = httpClient;
            _cacheStore = cacheStore;
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

                var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);

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
                using var doc = JsonDocument.Parse(json);

                string tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "unknown";
                string assetUrl = "";

                foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                    {
                        assetUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        break;
                    }
                }

                var release = new ReleaseInfo { Version = tagName, DownloadUrl = assetUrl };

                string? etag = response.Headers.ETag?.Tag;
                _cacheStore.SaveCachedRelease(release, etag);

                return release;
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
    }
}
