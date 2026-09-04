using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;

namespace DXVKCompanion.DXVK
{
    public class DxvkGithubClient
    {
        private const string ApiUrl = "https://api.github.com/repos/doitsujin/dxvk/releases/latest";
        private readonly HttpClient _httpClient;
        private readonly CacheStore _cacheStore;

        public DxvkGithubClient(HttpClient httpClient, CacheStore cacheStore)
        {
            _httpClient = httpClient;
            _cacheStore = cacheStore;
        }

        public async Task<ReleaseInfo?> FetchLatestReleaseAsync()
        {
            try
            {
                var cached = _cacheStore.LoadCachedRelease();
                if (cached != null && !cached.IsExpired())
                    return cached.Release;

                var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);

                if (cached != null && !string.IsNullOrWhiteSpace(cached.ETag))
                    request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cached.ETag));

                var response = await _httpClient.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cached != null)
                {
                    // Refresh the cached timestamp even on a 304 — otherwise the TTL window
                    // never actually extends and every launch after the first expiry hits
                    // the network again despite always getting "not modified" back.
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
            catch
            {
                return null;
            }
        }
    }
}
