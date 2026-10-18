using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Monitoring;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Storage;
using DXVKCompanion.Safety;
using DXVKCompanion.UI;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class DailyReleaseCheckTests
    {
        [Fact]
        public async Task DailyReleaseCheck_FreshCache_DoesNotInvokeNetwork_ReturnsCachedRelease()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var baseTime = new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc);
            var initialRelease = new ReleaseInfo { Version = "v2.5", DownloadUrl = "https://example.com/v2.5.tar.gz" };
            store.SaveCachedRelease(initialRelease, "\"etag-2.5\"", cachedAt: baseTime, lastAttemptAt: baseTime);

            int networkCallCount = 0;
            var handler = new RecordingHttpMessageHandler(_ =>
            {
                Interlocked.Increment(ref networkCallCount);
                return CreateJsonResponse(HttpStatusCode.OK, CreateReleaseJson("v2.6", "v2.6.tar.gz", "https://example.com/v2.6.tar.gz"));
            });

            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store, () => baseTime.AddHours(5));

            var result = await client.FetchLatestReleaseAsync();

            Assert.NotNull(result);
            Assert.Equal("v2.5", result.Version);
            Assert.Equal(0, networkCallCount);
        }

        [Fact]
        public async Task DailyReleaseCheck_InitialDueFetch_InvokesNetwork_PersistsCachedAtAndLastAttemptAt()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var fetchTime = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            int networkCallCount = 0;
            var handler = new RecordingHttpMessageHandler(_ =>
            {
                Interlocked.Increment(ref networkCallCount);
                return CreateJsonResponse(
                    HttpStatusCode.OK,
                    CreateReleaseJson("v3.0", "dxvk-3.0.tar.gz", "https://example.com/dxvk-3.0.tar.gz"),
                    "\"etag-3.0\"");
            });

            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store, () => fetchTime);

            var result = await client.FetchLatestReleaseAsync();

            Assert.NotNull(result);
            Assert.Equal("v3.0", result.Version);
            Assert.Equal(1, networkCallCount);

            var cached = store.LoadCachedRelease();
            Assert.NotNull(cached);
            Assert.Equal("v3.0", cached.Release.Version);
            Assert.Equal("\"etag-3.0\"", cached.ETag);
            Assert.Equal(fetchTime, cached.CachedAt);
            Assert.Equal(fetchTime, cached.LastAttemptAt);
            Assert.True(cached.HasValidRelease);
        }

        [Fact]
        public async Task DailyReleaseCheck_Exact24HourBoundary_DistinguishesBeforeAndAtDueTime()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var baseTime = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);
            store.SaveCachedRelease(
                new ReleaseInfo { Version = "v2.5", DownloadUrl = "https://example.com/v2.5.tar.gz" },
                "\"etag-2.5\"",
                cachedAt: baseTime,
                lastAttemptAt: baseTime);

            int networkCallCount = 0;
            var handler = new RecordingHttpMessageHandler(_ =>
            {
                Interlocked.Increment(ref networkCallCount);
                return CreateJsonResponse(
                    HttpStatusCode.OK,
                    CreateReleaseJson("v2.6", "v2.6.tar.gz", "https://example.com/v2.6.tar.gz"),
                    "\"etag-2.6\"");
            });

            using var httpClient = new HttpClient(handler);
            DateTime currentTime = baseTime.AddHours(23).AddMinutes(59).AddSeconds(59);
            var client = new DxvkGithubClient(httpClient, store, () => currentTime);

            // 1. One second before 24h boundary: NOT due, 0 network calls
            var result1 = await client.FetchLatestReleaseAsync();
            Assert.NotNull(result1);
            Assert.Equal("v2.5", result1.Version);
            Assert.Equal(0, networkCallCount);

            // 2. Exactly at 24h boundary: DUE, exactly 1 network call
            currentTime = baseTime.AddHours(24);
            var result2 = await client.FetchLatestReleaseAsync();
            Assert.NotNull(result2);
            Assert.Equal("v2.6", result2.Version);
            Assert.Equal(1, networkCallCount);
        }

        [Fact]
        public async Task DailyReleaseCheck_ConcurrentCallers_ShareSingleInflightHttpOperation()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            int networkCallCount = 0;
            var handler = RecordingHttpMessageHandler.CreateAsync(async _ =>
            {
                Interlocked.Increment(ref networkCallCount);
                // Introduce artificial latency to ensure all concurrent callers overlap inside gate
                await Task.Delay(50);
                return CreateJsonResponse(
                    HttpStatusCode.OK,
                    CreateReleaseJson("v3.1", "dxvk-3.1.tar.gz", "https://example.com/dxvk-3.1.tar.gz"),
                    "\"etag-3.1\"");
            });

            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            // Launch 10 concurrent requests
            var tasks = new List<Task<ReleaseInfo?>>();
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(() => client.FetchLatestReleaseAsync()));
            }

            var results = await Task.WhenAll(tasks);

            Assert.Equal(1, networkCallCount);
            foreach (var r in results)
            {
                Assert.NotNull(r);
                Assert.Equal("v3.1", r.Version);
            }
        }

        [Fact]
        public async Task DailyReleaseCheck_SuccessAnd304_RefreshTimestampsAndPreserveData()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var t0 = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);
            store.SaveCachedRelease(
                new ReleaseInfo { Version = "v2.5", DownloadUrl = "https://example.com/v2.5.tar.gz" },
                "\"etag-2.5\"",
                cachedAt: t0,
                lastAttemptAt: t0);

            HttpRequestHeaders? capturedHeaders = null;
            var t1 = t0.AddHours(24);
            var handler = new RecordingHttpMessageHandler(req =>
            {
                capturedHeaders = req.Headers;
                var resp = new HttpResponseMessage(HttpStatusCode.NotModified);
                resp.Content = new StringContent("");
                return resp;
            });

            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store, () => t1);

            var result = await client.FetchLatestReleaseAsync();

            Assert.NotNull(result);
            Assert.Equal("v2.5", result.Version);
            Assert.NotNull(capturedHeaders);
            Assert.Contains(capturedHeaders.IfNoneMatch, h => h.Tag == "\"etag-2.5\"");

            // Assert cache reloaded from disk has both CachedAt and LastAttemptAt refreshed to t1
            var reloaded = store.LoadCachedRelease();
            Assert.NotNull(reloaded);
            Assert.Equal("v2.5", reloaded.Release.Version);
            Assert.Equal("\"etag-2.5\"", reloaded.ETag);
            Assert.Equal(t1, reloaded.CachedAt);
            Assert.Equal(t1, reloaded.LastAttemptAt);
        }

        [Fact]
        public async Task DailyReleaseCheck_Failure_PreservesValidReleaseAndETag_WhileRecordingAttempt()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var t0 = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);
            store.SaveCachedRelease(
                new ReleaseInfo { Version = "v2.4", DownloadUrl = "https://example.com/v2.4.tar.gz" },
                "\"etag-2.4\"",
                cachedAt: t0,
                lastAttemptAt: t0);

            var t1 = t0.AddHours(25);
            var handler = new RecordingHttpMessageHandler(_ =>
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("Server Error")
                };
            });

            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store, () => t1);

            var result = await client.FetchLatestReleaseAsync();

            // Falls back to previously known valid release
            Assert.NotNull(result);
            Assert.Equal("v2.4", result.Version);

            // Disk cache preserves valid release, CachedAt from t0, but updates LastAttemptAt to t1
            var reloaded = store.LoadCachedRelease();
            Assert.NotNull(reloaded);
            Assert.Equal("v2.4", reloaded.Release.Version);
            Assert.Equal("\"etag-2.4\"", reloaded.ETag);
            Assert.Equal(t0, reloaded.CachedAt);
            Assert.Equal(t1, reloaded.LastAttemptAt);
            Assert.True(reloaded.HasValidRelease);
        }

        [Fact]
        public async Task DailyReleaseCheck_ReconstructAfterFailedCheck_WithPriorValidMetadata_ThrottlesFor24Hours()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var initialStore = new CacheStore(cacheFile);

            var t0 = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);
            initialStore.SaveCachedRelease(
                new ReleaseInfo { Version = "v2.4", DownloadUrl = "https://example.com/v2.4.tar.gz" },
                "\"etag-2.4\"",
                cachedAt: t0,
                lastAttemptAt: t0);

            // Fail at t1 = t0 + 24h
            var t1 = t0.AddHours(24);
            int networkCallCount = 0;
            var handler = new RecordingHttpMessageHandler(_ =>
            {
                Interlocked.Increment(ref networkCallCount);
                throw new HttpRequestException("Simulated network outage");
            });

            using (var http = new HttpClient(handler))
            {
                var initialClient = new DxvkGithubClient(http, initialStore, () => t1);
                var fallbackResult = await initialClient.FetchLatestReleaseAsync();
                Assert.NotNull(fallbackResult);
                Assert.Equal("v2.4", fallbackResult.Version);
                Assert.Equal(1, networkCallCount);
            }

            // Reconstruct fresh CacheStore and DxvkGithubClient from disk paths
            var reconstructedStore = new CacheStore(cacheFile);

            // At t2 = t1 + 1h (25h from t0): check is throttled!
            var t2 = t1.AddHours(1);
            using (var http = new HttpClient(handler))
            {
                var reconstructedClient = new DxvkGithubClient(http, reconstructedStore, () => t2);
                var throttledResult = await reconstructedClient.FetchLatestReleaseAsync();
                Assert.NotNull(throttledResult);
                Assert.Equal("v2.4", throttledResult.Version);
                Assert.Equal(1, networkCallCount); // Still exactly 1 call from t1
            }

            // At t3 = t1 + 24h (48h from t0): throttle window expired, check is due!
            var t3 = t1.AddHours(24);
            using (var http = new HttpClient(handler))
            {
                var dueClient = new DxvkGithubClient(http, reconstructedStore, () => t3);
                var dueResult = await dueClient.FetchLatestReleaseAsync();
                Assert.NotNull(dueResult);
                Assert.Equal("v2.4", dueResult.Version); // Falls back again
                Assert.Equal(2, networkCallCount); // Attempt count incremented to 2
            }
        }

        [Fact]
        public async Task DailyReleaseCheck_ReconstructAfterFailedCheck_WithoutPriorMetadata_ThrottlesWithoutCreatingBogusRelease()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var initialStore = new CacheStore(cacheFile);

            var t0 = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);
            int networkCallCount = 0;
            var handler = new RecordingHttpMessageHandler(_ =>
            {
                Interlocked.Increment(ref networkCallCount);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("Service Unavailable")
                };
            });

            using (var http = new HttpClient(handler))
            {
                var initialClient = new DxvkGithubClient(http, initialStore, () => t0);
                var result = await initialClient.FetchLatestReleaseAsync();
                Assert.Null(result);
                Assert.Equal(1, networkCallCount);
            }

            // Disk cache has failure record but no valid release
            var reloaded = initialStore.LoadCachedRelease();
            Assert.NotNull(reloaded);
            Assert.Equal(t0, reloaded.LastAttemptAt);
            Assert.False(reloaded.HasValidRelease);

            // Reconstruct fresh client from disk
            var reconstructedStore = new CacheStore(cacheFile);

            // At t1 = t0 + 1h: check is throttled, returns null, no bogus release created!
            var t1 = t0.AddHours(1);
            using (var http = new HttpClient(handler))
            {
                var reconstructedClient = new DxvkGithubClient(http, reconstructedStore, () => t1);
                var throttledResult = await reconstructedClient.FetchLatestReleaseAsync();
                Assert.Null(throttledResult);
                Assert.Equal(1, networkCallCount); // Zero new network calls
            }

            // At t2 = t0 + 24h: check is due again, attempts network call
            var t2 = t0.AddHours(24);
            using (var http = new HttpClient(handler))
            {
                var dueClient = new DxvkGithubClient(http, reconstructedStore, () => t2);
                var dueResult = await dueClient.FetchLatestReleaseAsync();
                Assert.Null(dueResult);
                Assert.Equal(2, networkCallCount);
            }
        }

        [Fact]
        public async Task DailyReleaseCheck_LegacyCacheJson_InfersDueBehaviorFromCachedAt()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            var baseTime = new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc);
            string legacyJson = $@"{{
  ""Release"": {{
    ""Version"": ""v2.4"",
    ""DownloadUrl"": ""https://example.com/v2.4.tar.gz""
  }},
  ""CachedAt"": ""{baseTime:O}"",
  ""ETag"": ""\""etag-legacy\""""
}}";
            File.WriteAllText(cacheFile, legacyJson);

            int networkCallCount = 0;
            var handler = new RecordingHttpMessageHandler(_ =>
            {
                Interlocked.Increment(ref networkCallCount);
                return CreateJsonResponse(HttpStatusCode.OK, CreateReleaseJson("v2.5", "v2.5.tar.gz", "https://example.com/v2.5.tar.gz"));
            });

            using var httpClient = new HttpClient(handler);
            var store = new CacheStore(cacheFile);

            // 1. Within 24h of CachedAt: not due, returns legacy release with 0 calls
            var clientRecent = new DxvkGithubClient(httpClient, store, () => baseTime.AddHours(10));
            var recentResult = await clientRecent.FetchLatestReleaseAsync();
            Assert.NotNull(recentResult);
            Assert.Equal("v2.4", recentResult.Version);
            Assert.Equal(0, networkCallCount);

            // 2. Beyond 24h of CachedAt: due, issues network request
            var clientDue = new DxvkGithubClient(httpClient, store, () => baseTime.AddHours(25));
            var dueResult = await clientDue.FetchLatestReleaseAsync();
            Assert.NotNull(dueResult);
            Assert.Equal("v2.5", dueResult.Version);
            Assert.Equal(1, networkCallCount);
        }

        [Fact]
        public async Task DailyReleaseCheck_OfflineDeployment_SucceedsAfterMetadataFailure_AndSubsequentRestartDoesNotRecheck()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            string gamesFile = Path.Combine(testDir.RootPath, "games.json");
            string gameLibFile = Path.Combine(testDir.RootPath, "game-library.json");
            string sourceDir = Path.Combine(testDir.RootPath, "dxvk-source");
            string gameDir = Path.Combine(testDir.RootPath, "game1");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(backupsDir);

            // Pre-seed local release payload for v3.1.1
            string x64Source = Path.Combine(sourceDir, "v3.1.1", "x64");
            Directory.CreateDirectory(x64Source);
            byte[] d3d11Bytes = new byte[] { 0x44, 0x33, 0x44, 0x31, 0x31 };
            byte[] dxgiBytes = new byte[] { 0x44, 0x58, 0x47, 0x49 };
            File.WriteAllBytes(Path.Combine(x64Source, "d3d11.dll"), d3d11Bytes);
            File.WriteAllBytes(Path.Combine(x64Source, "dxgi.dll"), dxgiBytes);

            // Pre-seed cache at t0
            var t0 = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);
            var initialStore = new CacheStore(cacheFile);
            initialStore.SaveCachedRelease(
                new ReleaseInfo { Version = "v3.1.1", DownloadUrl = "https://example.com/v3.1.1.tar.gz" },
                "\"etag-3.1.1\"",
                cachedAt: t0,
                lastAttemptAt: t0);

            // At t1 = t0 + 24h: check is due, but network is offline (rejecting handler)
            var t1 = t0.AddHours(24);
            int networkCallCount = 0;
            var rejectingHandler = new RecordingHttpMessageHandler(_ =>
            {
                Interlocked.Increment(ref networkCallCount);
                throw new HttpRequestException("Offline - network unreachable");
            });

            using var httpClient = new HttpClient(rejectingHandler);
            var cacheStore = new CacheStore(cacheFile);
            var profileStore = new ProfileStore(gamesFile);
            var gameLibraryStore = new GameLibraryStore(gameLibFile);
            var githubClient = new DxvkGithubClient(httpClient, cacheStore, () => t1);

            var txEngine = new MultiFileTransactionEngine(backupsDir);
            var fileUtils = new FileUtils();
            var installer = new DxvkInstaller(httpClient, txEngine, gameLibraryStore, sourceDir, fileUtils);
            var rollback = new DxvkRollback(txEngine, gameLibraryStore, fileUtils);
            var dxvkManager = new DxvkManager(installer, rollback, githubClient, profileStore, gameLibraryStore);

            string exePath = Path.Combine(gameDir, "game.exe");
            File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A });
            var profile = profileStore.GetOrCreate(exePath);
            profile.Architecture = "x64";
            profile.Api = GraphicsApi.DX11;
            profileStore.Save(profile);

            // Enable profile with targetVersion = null (latest release intent)
            var enableRes = await dxvkManager.EnableDxvkAsync(profile);
            Assert.True(enableRes);
            Assert.Equal(1, networkCallCount); // Exactly 1 failed check attempt

            // Assert deployment succeeded offline using cached v3.1.1 files
            Assert.True(File.Exists(Path.Combine(gameDir, "d3d11.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir, "dxgi.dll")));
            Assert.Equal(d3d11Bytes, File.ReadAllBytes(Path.Combine(gameDir, "d3d11.dll")));
            Assert.Equal(dxgiBytes, File.ReadAllBytes(Path.Combine(gameDir, "dxgi.dll")));
            Assert.Equal("v3.1.1", profile.DxvkVersion);

            // Reconstruct manager from disk at t2 = t1 + 30 minutes
            var t2 = t1.AddMinutes(30);
            var reconstructedCache = new CacheStore(cacheFile);
            var reconstructedProfiles = new ProfileStore(gamesFile);
            var reconstructedLib = new GameLibraryStore(gameLibFile);
            var reconstructedGithub = new DxvkGithubClient(httpClient, reconstructedCache, () => t2);
            var reconstructedManager = new DxvkManager(installer, rollback, reconstructedGithub, reconstructedProfiles, reconstructedLib);

            // Access latest release again: throttled by failed attempt at t1, zero new network calls
            var latest = await reconstructedManager.GetLatestReleaseAsync();
            Assert.NotNull(latest);
            Assert.Equal("v3.1.1", latest.Version);
            Assert.Equal(1, networkCallCount); // Still 1 call
        }

        [Fact]
        public async Task DailyReleaseCheck_Scheduler_StopAndDisposal_CancelsOwnedWorkWithoutCallbacks()
        {
            int checkCallCount = 0;
            int callbackCallCount = 0;
            bool cancellationObserved = false;
            var actionStarted = new TaskCompletionSource<bool>();

            using (var scheduler = new DxvkReleaseCheckScheduler(
                async ct =>
                {
                    Interlocked.Increment(ref checkCallCount);
                    actionStarted.TrySetResult(true);
                    try
                    {
                        // Block waiting for cancellation token from scheduler
                        await Task.Delay(10000, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationObserved = true;
                        throw;
                    }
                    return new ReleaseInfo { Version = "v2.5", DownloadUrl = "" };
                },
                latest =>
                {
                    Interlocked.Increment(ref callbackCallCount);
                    return Task.CompletedTask;
                },
                checkInterval: TimeSpan.FromMinutes(1),
                startImmediately: false))
            {
                // Trigger check asynchronously
                var triggerTask = scheduler.TriggerCheckAsync();

                // Wait until action has actually entered and is waiting
                await actionStarted.Task;

                // Dispose while check is blocked in flight
                scheduler.Dispose();

                var result = await triggerTask;

                // Cancellation must have actually been signaled to the in-flight checkAction
                Assert.True(cancellationObserved);
                Assert.Null(result);

                // After disposal, callback must not be invoked
                Assert.Equal(0, callbackCallCount);

                // Further triggers after disposal return null immediately
                var postDisposalResult = await scheduler.TriggerCheckAsync();
                Assert.Null(postDisposalResult);
            }
        }

        [Fact]
        public async Task DailyReleaseCheck_Scheduler_ConcurrentEntryAndDispose_TerminatesCleanlyWithoutUnobservedExceptions()
        {
            for (int round = 0; round < 3; round++)
            {
                var scheduler = new DxvkReleaseCheckScheduler(
                    async ct =>
                    {
                        await Task.Delay(5, ct);
                        return new ReleaseInfo { Version = "v2.5", DownloadUrl = "" };
                    },
                    latest => Task.CompletedTask,
                    checkInterval: TimeSpan.FromMinutes(1),
                    startImmediately: false);

                var tasks = new List<Task<ReleaseInfo?>>();
                for (int i = 0; i < 20; i++)
                {
                    tasks.Add(Task.Run(async () => await scheduler.TriggerCheckAsync()));
                }

                // Concurrently dispose while tasks are entering or executing
                var disposeTask = Task.Run(async () =>
                {
                    await Task.Delay(2);
                    scheduler.Dispose();
                });

                await Task.WhenAll(tasks.Cast<Task>().Concat(new[] { disposeTask }));

                // Post-disposal check returns null cleanly
                var postDisposal = await scheduler.TriggerCheckAsync();
                Assert.Null(postDisposal);
            }
        }

        [Fact]
        public async Task DailyReleaseCheck_TrayApp_NotificationDecisionMarshaledToSyncContext_AndDeduplicatesVersions()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "profiles.json");
            string libFile = Path.Combine(testDir.RootPath, "library.json");
            string settingsFile = Path.Combine(testDir.RootPath, "settings.json");
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            var profiles = new ProfileStore(profilesFile);
            var libStore = new GameLibraryStore(libFile);
            var settings = SettingsStore.Load(settingsFile);

            var exitHandler = new ProcessExitHandler();
            var scanner = new ModuleScanner();
            var peParser = new PeParser();
            var classifier = new ApiClassifier(scanner, peParser);
            var monitor = new ProcessMonitor(new GameDetector(), exitHandler, classifier, libStore);

            var fileUtils = new FileUtils();
            var txEngine = new MultiFileTransactionEngine(Path.Combine(testDir.RootPath, "backups"));
            var handler = new RecordingHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK));
            using var httpClient = new HttpClient(handler);
            var installer = new DxvkInstaller(httpClient, txEngine, libStore, null, fileUtils);
            var rollback = new DxvkRollback(txEngine, libStore, fileUtils);
            var cacheStore = new CacheStore(cacheFile);
            var github = new DxvkGithubClient(httpClient, cacheStore);
            var dxvk = new DxvkManager(installer, rollback, github, profiles, libStore);

            // Register game with older Dxvk version
            var game = profiles.GetOrCreate(@"C:\Games\Skyrim\SkyrimSE.exe");
            game.DxvkEnabled = true;
            game.Api = GraphicsApi.DX11;
            game.DxvkVersion = "v2.4";
            profiles.Save(game);

            var testSync = new TrackingSyncContext();
            var balloons = new List<(string Title, string Msg)>();

            using var trayApp = new TrayApp(
                monitor, profiles, dxvk, classifier, settings, httpClient,
                gameLibraryStore: libStore,
                githubClient: github,
                syncContext: testSync);

            trayApp.CustomBalloonNotifier = (title, msg) => balloons.Add((title, msg));

            // First check: new release v2.5 available
            bool firstResult = await trayApp.NotifyIfDxvkUpdateAvailableAsync(new ReleaseInfo { Version = "v2.5" });
            Assert.True(firstResult);
            Assert.Single(balloons);
            Assert.Equal("DXVK Update Available", balloons[0].Title);
            Assert.Equal("v2.5", trayApp.LastNotifiedDxvkVersion);
            Assert.True(testSync.PostCount > 0);

            int postCountAfterFirst = testSync.PostCount;

            // Second check: same version v2.5 -> deduplicated on sync context, no balloon spam
            bool secondResult = await trayApp.NotifyIfDxvkUpdateAvailableAsync(new ReleaseInfo { Version = "v2.5" });
            Assert.False(secondResult);
            Assert.Single(balloons);
            Assert.True(testSync.PostCount > postCountAfterFirst);

            // Third check: newer version v2.6 -> notifies again
            bool thirdResult = await trayApp.NotifyIfDxvkUpdateAvailableAsync(new ReleaseInfo { Version = "v2.6" });
            Assert.True(thirdResult);
            Assert.Equal(2, balloons.Count);
            Assert.Equal("v2.6", trayApp.LastNotifiedDxvkVersion);

            // Disposal suppresses notification even if check arrives
            trayApp.Dispose();
            bool fourthResult = await trayApp.NotifyIfDxvkUpdateAvailableAsync(new ReleaseInfo { Version = "v2.7" });
            Assert.False(fourthResult);
            Assert.Equal(2, balloons.Count);
        }

        [Fact]
        public async Task DailyReleaseCheck_DxvkGithubClient_TokenCancellation_AbortsBlockedHttpRequest()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var requestStarted = new TaskCompletionSource<bool>();
            var handler = RecordingHttpMessageHandler.CreateTokenAware(async (req, ct) =>
            {
                requestStarted.TrySetResult(true);
                await Task.Delay(10000, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            using var httpClient = new HttpClient(handler);
            var client = new DxvkGithubClient(httpClient, store);

            using var cts = new CancellationTokenSource();
            var checkTask = client.FetchLatestReleaseAsync(cts.Token);

            await requestStarted.Task;
            cts.Cancel();

            var result = await checkTask;
            Assert.Null(result);
        }

        private sealed class TrackingSyncContext : SynchronizationContext
        {
            public int PostCount { get; private set; }
            public List<int> ExecutionThreads { get; } = new();

            public override void Post(SendOrPostCallback d, object? state)
            {
                PostCount++;
                ExecutionThreads.Add(Environment.CurrentManagedThreadId);
                d(state);
            }
        }

        private static HttpResponseMessage CreateJsonResponse(
            HttpStatusCode statusCode,
            string json,
            string? etag = null)
        {
            var response = new HttpResponseMessage(statusCode);
            response.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

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

        private sealed class RecordingHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

            public List<HttpRequestMessage> CapturedRequests { get; } = new();

            public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = (req, ct) => Task.FromResult(handler(req));
            }

            public static RecordingHttpMessageHandler CreateAsync(Func<HttpRequestMessage, Task<HttpResponseMessage>> asyncHandler)
            {
                return new RecordingHttpMessageHandler((req, ct) => asyncHandler(req));
            }

            public static RecordingHttpMessageHandler CreateTokenAware(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> asyncHandler)
            {
                return new RecordingHttpMessageHandler(asyncHandler);
            }

            public RecordingHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CapturedRequests.Add(request);
                return _handler(request, cancellationToken);
            }
        }
    }
}
