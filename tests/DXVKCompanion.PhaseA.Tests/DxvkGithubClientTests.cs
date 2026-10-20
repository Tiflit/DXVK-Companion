using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public sealed class OfficialReleaseCatalogTests
    {
        [Fact]
        public async Task Catalog_UsesOnlyExactWindowsAsset_AndConstructsBoundedOfficialPages()
        {
            using var fixture = new OfficialAcquisitionFixture();
            var windows = OfficialAcquisitionFixture.Metadata("v2.5", fixture.Archives["v2.5"]);
            var assets = windows["assets"]!.AsArray();
            var native = assets[0]!.DeepClone();
            native["name"] = "dxvk-native-2.5-steamrt-sniper.tar.gz";
            native["browser_download_url"] = "https://github.com/doitsujin/dxvk/releases/download/v2.5/dxvk-native-2.5-steamrt-sniper.tar.gz";
            assets.Insert(0, native);
            var source = assets[1]!.DeepClone();
            source["name"] = "Source code.tar.gz";
            assets.Insert(0, source);
            fixture.Override = (request, _) =>
            {
                Assert.Equal("api.github.com", request.RequestUri!.Host);
                var rows = new JsonArray();
                if (request.RequestUri.Query.EndsWith("page=1"))
                    for (int n = 0; n < DxvkGithubClient.CatalogPageSize; n++) rows.Add(windows.DeepClone());
                else rows.Add(OfficialAcquisitionFixture.Metadata("v2.4", fixture.Archives["v2.5"], digest: false));
                var response = OfficialAcquisitionFixture.Json(rows);
                response.Headers.TryAddWithoutValidation("Link", "<https://example.invalid/private>; rel=\"next\"");
                return Task.FromResult(response);
            };
            var first = await fixture.Github.FetchOfficialReleasePageAsync(1);
            Assert.True(first.HasMore);
            Assert.Equal(30, first.Releases.Count);
            Assert.All(first.Releases, r => Assert.Equal("dxvk-2.5.tar.gz", r.AssetName));
            var second = await fixture.Github.FetchOfficialReleasePageAsync(2);
            Assert.False(second.HasMore);
            Assert.Equal("v2.4", Assert.Single(second.Releases).Version);
            Assert.Null(second.Releases[0].AssetDigest);
            Assert.All(fixture.Requests, url => Assert.StartsWith("https://api.github.com/repos/doitsujin/dxvk/releases?per_page=30&page=", url));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Github.FetchOfficialReleasePageAsync(0));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Github.FetchOfficialReleasePageAsync(33));
        }

        [Theory]
        [InlineData("draft")]
        [InlineData("prerelease")]
        [InlineData("tag")]
        [InlineData("url")]
        [InlineData("url-tag")]
        [InlineData("digest")]
        [InlineData("empty-digest")]
        [InlineData("duplicate")]
        [InlineData("native-only")]
        [InlineData("oversize")]
        [InlineData("credentials")]
        public async Task Catalog_RefusesInvalidOrAmbiguousOfficialIdentity(string kind)
        {
            using var fixture = new OfficialAcquisitionFixture();
            var metadata = OfficialAcquisitionFixture.Metadata("v2.5", fixture.Archives["v2.5"]);
            var asset = metadata["assets"]![0]!;
            switch (kind)
            {
                case "draft": metadata["draft"] = true; break;
                case "prerelease": metadata["prerelease"] = true; break;
                case "tag": metadata["tag_name"] = "v2.5/../other"; break;
                case "url": asset["browser_download_url"] = "https://example.invalid/dxvk-2.5.tar.gz"; break;
                case "url-tag": asset["browser_download_url"] = OfficialAcquisitionFixture.AssetUrl("v2.4"); break;
                case "digest": asset["digest"] = "sha256:broken"; break;
                case "empty-digest": asset["digest"] = ""; break;
                case "duplicate": metadata["assets"]!.AsArray().Add(asset.DeepClone()); break;
                case "native-only": asset["name"] = "dxvk-native-2.5.tar.gz"; break;
                case "oversize": asset["size"] = DxvkReleaseAcquirer.MaximumArchiveBytes + 1; break;
                case "credentials": asset["browser_download_url"] = "https://generic:synthetic@github.com/doitsujin/dxvk/releases/download/v2.5/dxvk-2.5.tar.gz"; break;
            }
            fixture.Override = (_, _) => Task.FromResult(OfficialAcquisitionFixture.Json(new JsonArray(metadata.DeepClone())));
            Assert.Empty((await fixture.Github.FetchOfficialReleasePageAsync(1)).Releases);
            fixture.Override = (_, _) => Task.FromResult(OfficialAcquisitionFixture.Json(metadata));
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Github.FetchOfficialReleaseAsync("v2.5"));
        }

        [Fact]
        public async Task OfficialTagLookup_RejectsMismatchedResponse_OversizedMetadataAndCancelledRequest()
        {
            using var fixture = new OfficialAcquisitionFixture();
            fixture.Override = (_, _) => Task.FromResult(OfficialAcquisitionFixture.Json(OfficialAcquisitionFixture.Metadata("v2.4", fixture.Archives["v2.5"])));
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Github.FetchOfficialReleaseAsync("v2.5"));
            fixture.Override = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(new string(' ', DxvkGithubClient.MaximumMetadataBytes + 1)) });
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Github.FetchOfficialReleasePageAsync(1));
            fixture.Override = async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); };
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Github.FetchOfficialReleasePageAsync(1, cancel.Token));
        }
    }

    public class DxvkGithubClientTests
    {
        [Fact]
        public async Task FetchLatestReleaseAsync_WhenCacheIsFresh_ReturnsCachedRelease_DoesNotInvokeNetwork()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var initialRelease = new ReleaseInfo { Version = "v2.4", DownloadUrl = "https://example.com/v2.4.tar.gz" };
            store.SaveCachedRelease(initialRelease, "\"etag-2.4\"");

            int callCount = 0;
            var handler = new FakeHttpMessageHandler(req =>
            {
                callCount++;
                return new TrackingHttpResponseMessage(HttpStatusCode.OK);
            });
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            var result = await client.FetchLatestReleaseAsync();

            Assert.NotNull(result);
            Assert.Equal("v2.4", result.Version);
            Assert.Equal("https://example.com/v2.4.tar.gz", result.DownloadUrl);
            Assert.Equal(0, callCount);
            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task FetchLatestReleaseAsync_On200Success_ParsesRelease_SavesToCache_DisposesRequestAndResponse()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            string json = CreateReleaseJson("v2.5", "dxvk-2.5.tar.gz", "https://github.com/doitsujin/dxvk/releases/download/v2.5/dxvk-2.5.tar.gz");
            var trackingResponse = CreateJsonResponse(HttpStatusCode.OK, json, "\"v2.5-etag\"");
            var trackingContent = (TrackingHttpContent)trackingResponse.Content!;

            var handler = new FakeHttpMessageHandler(req => trackingResponse);
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            var result = await client.FetchLatestReleaseAsync();

            Assert.NotNull(result);
            Assert.Equal("v2.5", result.Version);
            Assert.Equal("https://github.com/doitsujin/dxvk/releases/download/v2.5/dxvk-2.5.tar.gz", result.DownloadUrl);

            // Assert disposal of request and response resources
            Assert.Single(handler.AttachedRequestContents);
            Assert.True(handler.AttachedRequestContents[0].IsDisposed, "Allocated HttpRequestMessage must be disposed.");
            Assert.True(trackingResponse.IsDisposed, "Received HttpResponseMessage must be disposed.");
            Assert.True(trackingContent.IsDisposed, "Received HttpResponseMessage Content must be disposed.");

            // Assert persistence in CacheStore
            var cached = store.LoadCachedRelease();
            Assert.NotNull(cached);
            Assert.Equal("v2.5", cached.Release.Version);
            Assert.Equal("\"v2.5-etag\"", cached.ETag);
            Assert.False(cached.IsExpired());

            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task FetchLatestReleaseAsync_On200Success_WithMultipleAssets_SelectsTarGzArchive_DisposesResources()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            string json = CreateMultiAssetReleaseJson("v2.5.1", "dxvk-2.5.1.tar.gz", "https://example.com/dxvk-2.5.1.tar.gz");
            var trackingResponse = CreateJsonResponse(HttpStatusCode.OK, json, "\"v2.5.1-etag\"");
            var trackingContent = (TrackingHttpContent)trackingResponse.Content!;

            var handler = new FakeHttpMessageHandler(req => trackingResponse);
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            var result = await client.FetchLatestReleaseAsync();

            Assert.NotNull(result);
            Assert.Equal("v2.5.1", result.Version);
            Assert.Equal("https://example.com/dxvk-2.5.1.tar.gz", result.DownloadUrl);

            Assert.True(handler.AttachedRequestContents[0].IsDisposed);
            Assert.True(trackingResponse.IsDisposed);
            Assert.True(trackingContent.IsDisposed);
            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task FetchLatestReleaseAsync_On304NotModified_WithExpiredCache_SendsIfNoneMatch_RefreshesCacheTimestamp_DisposesResources()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var initialRelease = new ReleaseInfo { Version = "v2.4", DownloadUrl = "https://example.com/v2.4.tar.gz" };
            store.SaveCachedRelease(initialRelease, "\"etag-v2.4\"");

            // Synthetically expire cache file by writing expired CachedAt
            var expiredCache = store.LoadCachedRelease()!;
            expiredCache.CachedAt = DateTime.UtcNow.AddDays(-2);
            expiredCache.LastAttemptAt = expiredCache.CachedAt;
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(expiredCache, new JsonSerializerOptions { WriteIndented = true }));
            Assert.True(store.LoadCachedRelease()!.IsExpired());

            var trackingResponse = new TrackingHttpResponseMessage(HttpStatusCode.NotModified);
            var trackingContent = new TrackingHttpContent("");
            trackingResponse.Content = trackingContent;

            HttpRequestHeaders? capturedHeaders = null;
            var handler = new FakeHttpMessageHandler(req =>
            {
                capturedHeaders = req.Headers;
                return trackingResponse;
            });
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            var result = await client.FetchLatestReleaseAsync();

            Assert.NotNull(result);
            Assert.Equal("v2.4", result.Version);
            Assert.NotNull(capturedHeaders);
            Assert.Contains(capturedHeaders.IfNoneMatch, tag => tag.Tag == "\"etag-v2.4\"");

            Assert.True(handler.AttachedRequestContents[0].IsDisposed);
            Assert.True(trackingResponse.IsDisposed);
            Assert.True(trackingContent.IsDisposed);

            // Assert cache timestamp refreshed
            var reloaded = store.LoadCachedRelease();
            Assert.NotNull(reloaded);
            Assert.False(reloaded.IsExpired());
            Assert.Equal("\"etag-v2.4\"", reloaded.ETag);

            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task FetchLatestReleaseAsync_OnHttp500Error_WithPriorCache_FallsBackToPriorCache_DisposesResources()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var initialRelease = new ReleaseInfo { Version = "v2.3", DownloadUrl = "https://example.com/v2.3.tar.gz" };
            store.SaveCachedRelease(initialRelease, "\"etag-v2.3\"");

            // Expire cache
            var expired = store.LoadCachedRelease()!;
            expired.CachedAt = DateTime.UtcNow.AddDays(-3);
            expired.LastAttemptAt = expired.CachedAt;
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(expired));

            var trackingResponse = CreateJsonResponse(HttpStatusCode.InternalServerError, "{\"message\":\"Server Error\"}");
            var trackingContent = (TrackingHttpContent)trackingResponse.Content!;

            var handler = new FakeHttpMessageHandler(req => trackingResponse);
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            var result = await client.FetchLatestReleaseAsync();

            Assert.NotNull(result);
            Assert.Equal("v2.3", result.Version);

            Assert.True(handler.AttachedRequestContents[0].IsDisposed);
            Assert.True(trackingResponse.IsDisposed);
            Assert.True(trackingContent.IsDisposed);
            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task FetchLatestReleaseAsync_OnHttp404Error_WithNoCache_ReturnsNull_DisposesResources()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var trackingResponse = CreateJsonResponse(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
            var trackingContent = (TrackingHttpContent)trackingResponse.Content!;

            var handler = new FakeHttpMessageHandler(req => trackingResponse);
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            var result = await client.FetchLatestReleaseAsync();

            Assert.Null(result);

            Assert.True(handler.AttachedRequestContents[0].IsDisposed);
            Assert.True(trackingResponse.IsDisposed);
            Assert.True(trackingContent.IsDisposed);
            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task FetchLatestReleaseAsync_OnMalformedJson_WithPriorCache_FallsBackToPriorCache_DisposesResources()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var initialRelease = new ReleaseInfo { Version = "v2.3.1", DownloadUrl = "https://example.com/v2.3.1.tar.gz" };
            store.SaveCachedRelease(initialRelease, "\"etag-v2.3.1\"");

            var expired = store.LoadCachedRelease()!;
            expired.CachedAt = DateTime.UtcNow.AddDays(-2);
            expired.LastAttemptAt = expired.CachedAt;
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(expired));

            var trackingResponse = CreateJsonResponse(HttpStatusCode.OK, "<<<NOT JSON>>>");
            var trackingContent = (TrackingHttpContent)trackingResponse.Content!;

            var handler = new FakeHttpMessageHandler(req => trackingResponse);
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            var result = await client.FetchLatestReleaseAsync();

            Assert.NotNull(result);
            Assert.Equal("v2.3.1", result.Version);

            Assert.True(handler.AttachedRequestContents[0].IsDisposed);
            Assert.True(trackingResponse.IsDisposed);
            Assert.True(trackingContent.IsDisposed);
            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task FetchLatestReleaseAsync_OnMalformedJson_WithNoCache_ReturnsNull_DisposesResources()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var trackingResponse = CreateJsonResponse(HttpStatusCode.OK, "{\"unclosed json: ");
            var trackingContent = (TrackingHttpContent)trackingResponse.Content!;

            var handler = new FakeHttpMessageHandler(req => trackingResponse);
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            var result = await client.FetchLatestReleaseAsync();

            Assert.Null(result);

            Assert.True(handler.AttachedRequestContents[0].IsDisposed);
            Assert.True(trackingResponse.IsDisposed);
            Assert.True(trackingContent.IsDisposed);
            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task FetchLatestReleaseAsync_OnMissingJsonProperties_WithPriorCache_FallsBackToPriorCache_DisposesResources()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var initialRelease = new ReleaseInfo { Version = "v2.2", DownloadUrl = "https://example.com/v2.2.tar.gz" };
            store.SaveCachedRelease(initialRelease, "\"etag-v2.2\"");

            var expired = store.LoadCachedRelease()!;
            expired.CachedAt = DateTime.UtcNow.AddDays(-2);
            expired.LastAttemptAt = expired.CachedAt;
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(expired));

            // JSON missing "tag_name" or "assets"
            var trackingResponse = CreateJsonResponse(HttpStatusCode.OK, "{}");
            var trackingContent = (TrackingHttpContent)trackingResponse.Content!;

            var handler = new FakeHttpMessageHandler(req => trackingResponse);
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            var result = await client.FetchLatestReleaseAsync();

            Assert.NotNull(result);
            Assert.Equal("v2.2", result.Version);

            Assert.True(handler.AttachedRequestContents[0].IsDisposed);
            Assert.True(trackingResponse.IsDisposed);
            Assert.True(trackingContent.IsDisposed);
            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task FetchLatestReleaseAsync_OnCacheSaveException_On200Success_FallsBackToPriorCache_DisposesResources()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var initialRelease = new ReleaseInfo { Version = "v2.1", DownloadUrl = "https://example.com/v2.1.tar.gz" };
            store.SaveCachedRelease(initialRelease, "\"etag-v2.1\"");

            var expired = store.LoadCachedRelease()!;
            expired.CachedAt = DateTime.UtcNow.AddDays(-2);
            expired.LastAttemptAt = expired.CachedAt;
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(expired));

            // Inject failure on staging write
            store.CustomTempWriter = (_, _) => throw new IOException("Disk failure during staged cache write");

            string json = CreateReleaseJson("v2.5", "dxvk-2.5.tar.gz", "https://example.com/dxvk-2.5.tar.gz");
            var trackingResponse = CreateJsonResponse(HttpStatusCode.OK, json, "\"v2.5-etag\"");
            var trackingContent = (TrackingHttpContent)trackingResponse.Content!;

            var handler = new FakeHttpMessageHandler(req => trackingResponse);
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            var result = await client.FetchLatestReleaseAsync();

            // When save fails with exception, client logs and falls back to prior cached release
            Assert.NotNull(result);
            Assert.Equal("v2.1", result.Version);

            Assert.True(handler.AttachedRequestContents[0].IsDisposed);
            Assert.True(trackingResponse.IsDisposed);
            Assert.True(trackingContent.IsDisposed);
            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task FetchLatestReleaseAsync_OnCacheSaveException_On304NotModified_FallsBackToPriorCache_DisposesResources()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var initialRelease = new ReleaseInfo { Version = "v2.1", DownloadUrl = "https://example.com/v2.1.tar.gz" };
            store.SaveCachedRelease(initialRelease, "\"etag-v2.1\"");

            var expired = store.LoadCachedRelease()!;
            expired.CachedAt = DateTime.UtcNow.AddDays(-2);
            expired.LastAttemptAt = expired.CachedAt;
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(expired));

            // Inject failure during commit replacement on 304 save
            store.CustomCommitReplace = (_, _) => throw new IOException("Disk failure during commit replace");

            var trackingResponse = new TrackingHttpResponseMessage(HttpStatusCode.NotModified);
            var trackingContent = new TrackingHttpContent("");
            trackingResponse.Content = trackingContent;

            var handler = new FakeHttpMessageHandler(req => trackingResponse);
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            var result = await client.FetchLatestReleaseAsync();

            Assert.NotNull(result);
            Assert.Equal("v2.1", result.Version);

            Assert.True(handler.AttachedRequestContents[0].IsDisposed);
            Assert.True(trackingResponse.IsDisposed);
            Assert.True(trackingContent.IsDisposed);
            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task FetchLatestReleaseAsync_OnTimeout_FallsBackToPriorCache_DisposesRequest()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var initialRelease = new ReleaseInfo { Version = "v2.0", DownloadUrl = "https://example.com/v2.0.tar.gz" };
            store.SaveCachedRelease(initialRelease, "\"etag-v2.0\"");

            var expired = store.LoadCachedRelease()!;
            expired.CachedAt = DateTime.UtcNow.AddDays(-2);
            expired.LastAttemptAt = expired.CachedAt;
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(expired));

            var handler = new FakeHttpMessageHandler(req =>
            {
                throw new OperationCanceledException("Simulated timeout");
            });
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            var result = await client.FetchLatestReleaseAsync();

            Assert.NotNull(result);
            Assert.Equal("v2.0", result.Version);

            Assert.Single(handler.AttachedRequestContents);
            Assert.True(handler.AttachedRequestContents[0].IsDisposed);
            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task FetchLatestReleaseAsync_OnNetworkException_FallsBackToPriorCache_DisposesRequest()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var initialRelease = new ReleaseInfo { Version = "v2.0", DownloadUrl = "https://example.com/v2.0.tar.gz" };
            store.SaveCachedRelease(initialRelease, "\"etag-v2.0\"");

            var expired = store.LoadCachedRelease()!;
            expired.CachedAt = DateTime.UtcNow.AddDays(-2);
            expired.LastAttemptAt = expired.CachedAt;
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(expired));

            var handler = new FakeHttpMessageHandler(req =>
            {
                throw new HttpRequestException("Simulated connection failure");
            });
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            var result = await client.FetchLatestReleaseAsync();

            Assert.NotNull(result);
            Assert.Equal("v2.0", result.Version);

            Assert.Single(handler.AttachedRequestContents);
            Assert.True(handler.AttachedRequestContents[0].IsDisposed);
            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task FetchLatestReleaseAsync_MultipleSequentialFetches_MaintainsInjectedClientLifetime()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            string json25 = CreateReleaseJson("v2.5", "dxvk-2.5.tar.gz", "https://example.com/dxvk-2.5.tar.gz");
            int requestCount = 0;

            var responses = new List<TrackingHttpResponseMessage>();

            var handler = new FakeHttpMessageHandler(req =>
            {
                requestCount++;
                if (requestCount == 1)
                {
                    var resp = CreateJsonResponse(HttpStatusCode.OK, json25, "\"etag-2.5\"");
                    responses.Add(resp);
                    return resp;
                }
                else
                {
                    var resp = new TrackingHttpResponseMessage(HttpStatusCode.NotModified);
                    resp.Content = new TrackingHttpContent("");
                    responses.Add(resp);
                    return resp;
                }
            });
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            // First call: initial 200 OK
            var firstResult = await client.FetchLatestReleaseAsync();
            Assert.NotNull(firstResult);
            Assert.Equal("v2.5", firstResult.Version);
            Assert.True(responses[0].IsDisposed);

            // Synthetically expire cache to force second network call
            var cached = store.LoadCachedRelease()!;
            cached.CachedAt = DateTime.UtcNow.AddDays(-2);
            cached.LastAttemptAt = cached.CachedAt;
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(cached));

            // Second call: 304 Not Modified
            var secondResult = await client.FetchLatestReleaseAsync();
            Assert.NotNull(secondResult);
            Assert.Equal("v2.5", secondResult.Version);
            Assert.True(responses[1].IsDisposed);

            Assert.Equal(2, handler.AttachedRequestContents.Count);
            Assert.True(handler.AttachedRequestContents[0].IsDisposed);
            Assert.True(handler.AttachedRequestContents[1].IsDisposed);

            // Injected client remains functional throughout and afterwards
            await AssertClientUsableAsync(httpClient);
        }

        [Fact]
        public async Task DxvkGithubClient_FetchLatestReleaseAsync_WhenExistingCacheCorrupt_OnHttpFailure_PreservesCorruptFileAndThrottlesRepeatedAndReconstructedCalls()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            string corruptCorpus = "<<<MALFORMED CACHE CORPUS>>>";
            File.WriteAllText(cacheFile, corruptCorpus);

            var store = new CacheStore(cacheFile);
            var fakeNow = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

            int requestCount = 0;
            var handler = new FakeHttpMessageHandler(req =>
            {
                requestCount++;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            });
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store)
            {
                UtcNowSeam = () => fakeNow
            };

            // First call: encounters corrupt cache on disk, tries HTTP, fails with 500
            var firstResult = await client.FetchLatestReleaseAsync();
            Assert.Null(firstResult);
            Assert.Equal(1, requestCount);

            // Corrupt file remains preserved with original bytes
            Assert.Equal(corruptCorpus, File.ReadAllText(cacheFile));
            // Recovery backup preserves the corrupt bytes
            Assert.True(File.Exists(store.CorruptBackupFile));
            Assert.Equal(corruptCorpus, File.ReadAllText(store.CorruptBackupFile));

            // Immediate repeated call within 24h: throttled without network request
            var repeatedResult = await client.FetchLatestReleaseAsync();
            Assert.Null(repeatedResult);
            Assert.Equal(1, requestCount);

            // Reconstructed store & client: throttled without network request
            var reloadedStore = new CacheStore(cacheFile);
            var reloadedClient = new DxvkGithubClient(httpClient, reloadedStore)
            {
                UtcNowSeam = () => fakeNow.AddHours(2)
            };
            var reloadedResult = await reloadedClient.FetchLatestReleaseAsync();
            Assert.Null(reloadedResult);
            Assert.Equal(1, requestCount);

            // After 25 hours, 24h throttle expires: attempts network again
            reloadedClient.UtcNowSeam = () => fakeNow.AddHours(25);
            var expiredResult = await reloadedClient.FetchLatestReleaseAsync();
            Assert.Null(expiredResult);
            Assert.Equal(2, requestCount);

            // Corrupt file is still preserved
            Assert.Equal(corruptCorpus, File.ReadAllText(cacheFile));
        }

        [Fact]
        public async Task DxvkGithubClient_FetchLatestReleaseAsync_WhenExistingCacheCorrupt_OnHttp200_PreservesCorruptBackupAndWritesNewRelease()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            string corruptCorpus = "<<<MALFORMED CACHE CORPUS>>>";
            File.WriteAllText(cacheFile, corruptCorpus);

            var store = new CacheStore(cacheFile);
            var fakeNow = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

            string validJson = CreateReleaseJson("v2.5", "dxvk-2.5.tar.gz", "https://example.com/dxvk-2.5.tar.gz");
            int requestCount = 0;
            var handler = new FakeHttpMessageHandler(req =>
            {
                requestCount++;
                return CreateJsonResponse(HttpStatusCode.OK, validJson, "\"etag-25\"");
            });
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store)
            {
                UtcNowSeam = () => fakeNow
            };

            // Call with corrupt existing cache: succeeds with 200 OK
            var result = await client.FetchLatestReleaseAsync();
            Assert.NotNull(result);
            Assert.Equal("v2.5", result.Version);
            Assert.Equal(1, requestCount);

            // Recovery backup preserves the corrupt bytes
            Assert.True(File.Exists(store.CorruptBackupFile));
            Assert.Equal(corruptCorpus, File.ReadAllText(store.CorruptBackupFile));

            // Primary cache file now contains valid release JSON
            var loaded = store.LoadCachedRelease();
            Assert.NotNull(loaded);
            Assert.True(loaded.HasValidRelease);
            Assert.Equal("v2.5", loaded.Release.Version);

            // Subsequent call is served from cache with zero network calls
            var cachedResult = await client.FetchLatestReleaseAsync();
            Assert.NotNull(cachedResult);
            Assert.Equal("v2.5", cachedResult.Version);
            Assert.Equal(1, requestCount);
        }

        [Fact]
        public async Task DxvkGithubClient_FetchLatestReleaseAsync_WhenPreservationFails_LeavesPrimaryCorruptFileUntouched_AndRecordsThrottledAttempt()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            string corruptCorpus = "<<<MALFORMED CACHE CORPUS - DO NOT OVERWRITE>>>";
            File.WriteAllText(cacheFile, corruptCorpus);

            var store = new CacheStore(cacheFile);
            var fakeNow = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

            // Induce backup-copy failure by creating directory at CorruptBackupFile
            Directory.CreateDirectory(store.CorruptBackupFile);

            string validJson = CreateReleaseJson("v2.5", "dxvk-2.5.tar.gz", "https://example.com/dxvk-2.5.tar.gz");
            int requestCount = 0;
            var handler = new FakeHttpMessageHandler(req =>
            {
                requestCount++;
                return CreateJsonResponse(HttpStatusCode.OK, validJson, "\"etag-25\"");
            });
            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store)
            {
                UtcNowSeam = () => fakeNow
            };

            // Call encounters 200 OK, but cache save fails because corrupt preservation cannot complete.
            var result = await client.FetchLatestReleaseAsync();
            Assert.Null(result);
            Assert.Equal(1, requestCount);

            // Primary corrupt file remains untouched and not overwritten
            Assert.Equal(corruptCorpus, File.ReadAllText(cacheFile));

            // Failed-attempt sidecar is recorded so client is throttled without clobbering corrupt primary
            Assert.True(File.Exists(store.AttemptFile));
            var loadedAttempt = store.LoadCachedRelease();
            Assert.NotNull(loadedAttempt);
            Assert.Equal(fakeNow, loadedAttempt.LastAttemptAt);
            Assert.False(loadedAttempt.HasValidRelease);

            // Subsequent call is throttled for 24h with zero new network calls
            var repeatedResult = await client.FetchLatestReleaseAsync();
            Assert.Null(repeatedResult);
            Assert.Equal(1, requestCount);

            // Primary corrupt file is still completely preserved
            Assert.Equal(corruptCorpus, File.ReadAllText(cacheFile));
        }

        private static async Task AssertClientUsableAsync(HttpClient client)
        {
            using var probeRequest = new HttpRequestMessage(HttpMethod.Get, "http://localhost/probe");
            using var probeResponse = await client.SendAsync(probeRequest);
            Assert.NotNull(probeResponse);
            Assert.Equal(HttpStatusCode.OK, probeResponse.StatusCode);
        }

        private static TrackingHttpResponseMessage CreateJsonResponse(
            HttpStatusCode statusCode,
            string json,
            string? etag = null)
        {
            var response = new TrackingHttpResponseMessage(statusCode);
            var content = new TrackingHttpContent(json, System.Text.Encoding.UTF8, "application/json");
            response.Content = content;

            if (!string.IsNullOrEmpty(etag))
            {
                response.Headers.ETag = new EntityTagHeaderValue(etag);
            }

            return response;
        }

        private static string CreateReleaseJson(string tagName, string assetName, string downloadUrl)
        {
            var payload = new
            {
                tag_name = tagName,
                assets = new[]
                {
                    new
                    {
                        name = assetName,
                        browser_download_url = downloadUrl
                    }
                }
            };
            return JsonSerializer.Serialize(payload);
        }

        private static string CreateMultiAssetReleaseJson(string tagName, string tarGzName, string tarGzUrl)
        {
            var payload = new
            {
                tag_name = tagName,
                assets = new[]
                {
                    new { name = "dxvk-unrelated.zip", browser_download_url = "https://example.com/dxvk-unrelated.zip" },
                    new { name = "notes.txt", browser_download_url = "https://example.com/notes.txt" },
                    new { name = tarGzName, browser_download_url = tarGzUrl }
                }
            };
            return JsonSerializer.Serialize(payload);
        }

        private sealed class TrackingHttpResponseMessage : HttpResponseMessage
        {
            public bool IsDisposed { get; private set; }

            public TrackingHttpResponseMessage(HttpStatusCode statusCode) : base(statusCode)
            {
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    IsDisposed = true;
                }
                base.Dispose(disposing);
            }
        }

        private sealed class TrackingHttpContent : StringContent
        {
            public bool IsDisposed { get; private set; }

            public TrackingHttpContent(string content) : base(content)
            {
            }

            public TrackingHttpContent(string content, System.Text.Encoding encoding, string mediaType)
                : base(content, encoding, mediaType)
            {
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    IsDisposed = true;
                }
                base.Dispose(disposing);
            }
        }

        private sealed class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

            public List<HttpRequestMessage> CapturedRequests { get; } = new();
            public List<TrackingHttpContent> AttachedRequestContents { get; } = new();

            public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            {
                _responseFactory = responseFactory;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CapturedRequests.Add(request);

                // Attach tracking content so request disposal is observable via content disposal
                var trackingRequestContent = new TrackingHttpContent("fake-request-body");
                request.Content = trackingRequestContent;
                AttachedRequestContents.Add(trackingRequestContent);

                if (request.RequestUri?.AbsolutePath == "/probe")
                {
                    var probeResponse = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("probe-ok")
                    };
                    return Task.FromResult(probeResponse);
                }

                var response = _responseFactory(request);
                return Task.FromResult(response);
            }
        }
    }
}
