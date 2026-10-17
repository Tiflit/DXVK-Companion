using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests;

public sealed class FirstRunQueuedEnableTests
{
    private sealed class RecordingFakeHttpMessageHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public int ArchiveDownloadRequests => Requests.Count(r => r.RequestUri?.AbsoluteUri.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) == true);
        public int MetadataRequests => Requests.Count(r => r.RequestUri?.AbsoluteUri.Contains("/releases/latest", StringComparison.OrdinalIgnoreCase) == true);

        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingFakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }
            return Task.FromResult(_responder(request));
        }
    }

    private static byte[] CreateSyntheticTarGz(string version, byte[] d3d11Bytes, byte[] dxgiBytes)
    {
        using var memoryStream = new MemoryStream();
        using (var gzStream = new GZipStream(memoryStream, CompressionLevel.Optimal, leaveOpen: true))
        using (var tarWriter = new TarWriter(gzStream))
        {
            var d3d11Entry = new PaxTarEntry(TarEntryType.RegularFile, $"dxvk-{version}/x64/d3d11.dll")
            {
                DataStream = new MemoryStream(d3d11Bytes)
            };
            tarWriter.WriteEntry(d3d11Entry);

            var dxgiEntry = new PaxTarEntry(TarEntryType.RegularFile, $"dxvk-{version}/x64/dxgi.dll")
            {
                DataStream = new MemoryStream(dxgiBytes)
            };
            tarWriter.WriteEntry(dxgiEntry);
        }
        return memoryStream.ToArray();
    }

    private static (DxvkManager Manager, GameLibraryStore LibraryStore, ProfileStore ProfileStore, CacheStore CacheStore, DxvkInstaller Installer) CreateTestManager(
        SyntheticTestDirectory storageDir,
        SyntheticTestDirectory sourceDir,
        HttpClient httpClient)
    {
        string libraryPath = Path.Combine(storageDir.RootPath, "game-library.json");
        string backupsPath = Path.Combine(storageDir.RootPath, "backups");
        string profilesPath = Path.Combine(storageDir.RootPath, "games.json");
        string cachePath = Path.Combine(storageDir.RootPath, "release-cache.json");

        var libraryStore = new GameLibraryStore(libraryPath, backupsPath, profilesPath);
        var profileStore = new ProfileStore(profilesPath);
        var cacheStore = new CacheStore(cachePath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        var installer = new DxvkInstaller(httpClient, engine, libraryStore, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, libraryStore);
        var github = new DxvkGithubClient(httpClient, cacheStore);
        var manager = new DxvkManager(installer, rollback, github, profileStore, libraryStore);

        return (manager, libraryStore, profileStore, cacheStore, installer);
    }

    [Fact]
    public async Task FirstRunQueuedEnable_ApplyPendingAsync_ResolvesLatest_DownloadsOnce_AndDeploysToGame()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        string exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        string nativeD3D11 = Path.Combine(gameDir.RootPath, "d3d11.dll");
        string nativeDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");

        byte[] syntheticD3D11 = Encoding.UTF8.GetBytes("dxvk-3.1.1-d3d11-payload-unique");
        byte[] syntheticDxgi = Encoding.UTF8.GetBytes("dxvk-3.1.1-dxgi-payload-unique");
        byte[] archiveBytes = CreateSyntheticTarGz("v3.1.1", syntheticD3D11, syntheticDxgi);

        string latestUrl = "https://api.github.com/repos/doitsujin/dxvk/releases/latest";
        string downloadUrl = "https://fake.github.com/releases/download/v3.1.1/dxvk-3.1.1.tar.gz";

        var httpHandler = new RecordingFakeHttpMessageHandler(req =>
        {
            if (string.Equals(req.RequestUri?.AbsoluteUri, latestUrl, StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonSerializer.Serialize(new
                {
                    tag_name = "v3.1.1",
                    assets = new[]
                    {
                        new { name = "dxvk-3.1.1.tar.gz", browser_download_url = downloadUrl }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            if (string.Equals(req.RequestUri?.AbsoluteUri, downloadUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(httpHandler);
        var (manager, libraryStore, profileStore, _, _) = CreateTestManager(storageDir, sourceDir, httpClient);

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profile.DxvkVersion = null; // Unspecified / fresh latest intent
        profileStore.Save(profile);

        // 1. Queue request while game is supposedly running (using current test process as live process reference)
        var currentProc = Process.GetCurrentProcess();
        var queueResult = await manager.RequestEnableAsync(profile, currentProc);
        Assert.Equal(DxvkActionResult.Queued, queueResult);

        // Assert: Zero target mutation while queued
        Assert.False(File.Exists(nativeD3D11));
        Assert.False(File.Exists(nativeDxgi));

        // Assert: Durable Install/latest intent
        var inst = libraryStore.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(inst);
        Assert.NotNull(inst.PendingAction);
        Assert.Equal(PendingActionType.Install, inst.PendingAction.Type);
        Assert.Equal("latest", inst.PendingAction.TargetDxvkVersion);

        // 2. Execute ApplyPendingAsync on exit
        bool applyResult = await manager.ApplyPendingAsync(profile.ExePath);
        Assert.True(applyResult);

        // Assert: Network requests were made to official endpoints
        Assert.True(httpHandler.MetadataRequests >= 1);
        Assert.Equal(1, httpHandler.ArchiveDownloadRequests);

        // Assert: Concrete release cache directory created (not literal "latest")
        string concreteSourceDir = Path.Combine(sourceDir.RootPath, "v3.1.1", "x64");
        Assert.True(Directory.Exists(concreteSourceDir));
        Assert.False(Directory.Exists(Path.Combine(sourceDir.RootPath, "latest")));

        // Assert: Exact synthetic bytes deployed to game
        Assert.True(File.Exists(nativeD3D11));
        Assert.Equal(syntheticD3D11, File.ReadAllBytes(nativeD3D11));
        Assert.True(File.Exists(nativeDxgi));
        Assert.Equal(syntheticDxgi, File.ReadAllBytes(nativeDxgi));

        // Assert: Profile and library state updated, pending cleared
        var refreshedProfile = profileStore.GetOrCreate(exePath);
        Assert.True(refreshedProfile.DxvkEnabled);
        Assert.Equal("v3.1.1", refreshedProfile.DxvkVersion);

        var refreshedInst = libraryStore.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(refreshedInst);
        Assert.Null(refreshedInst.PendingAction);
        Assert.Equal("v3.1.1", refreshedInst.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, refreshedInst.RestorationState);
        Assert.Equal(2, refreshedInst.ManagedFiles.Count);
    }

    [Fact]
    public async Task FirstRunQueuedEnable_ProcessAllPendingActionsAsync_PersistedStartup_ResolvesLatestAndDeploys()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        string exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        byte[] syntheticD3D11 = Encoding.UTF8.GetBytes("dxvk-3.1.1-d3d11-startup");
        byte[] syntheticDxgi = Encoding.UTF8.GetBytes("dxvk-3.1.1-dxgi-startup");
        byte[] archiveBytes = CreateSyntheticTarGz("v3.1.1", syntheticD3D11, syntheticDxgi);

        string latestUrl = "https://api.github.com/repos/doitsujin/dxvk/releases/latest";
        string downloadUrl = "https://fake.github.com/releases/download/v3.1.1/dxvk-3.1.1.tar.gz";

        var httpHandler = new RecordingFakeHttpMessageHandler(req =>
        {
            if (string.Equals(req.RequestUri?.AbsoluteUri, latestUrl, StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonSerializer.Serialize(new
                {
                    tag_name = "v3.1.1",
                    assets = new[]
                    {
                        new { name = "dxvk-3.1.1.tar.gz", browser_download_url = downloadUrl }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            if (string.Equals(req.RequestUri?.AbsoluteUri, downloadUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(httpHandler);

        // Preseed stores on disk with persisted Install("latest") intent
        string libraryPath = Path.Combine(storageDir.RootPath, "game-library.json");
        string backupsPath = Path.Combine(storageDir.RootPath, "backups");
        string profilesPath = Path.Combine(storageDir.RootPath, "games.json");

        var setupLibStore = new GameLibraryStore(libraryPath, backupsPath, profilesPath);
        var setupProfStore = new ProfileStore(profilesPath);

        var prof = setupProfStore.GetOrCreate(exePath);
        prof.Api = GraphicsApi.DX11;
        prof.Architecture = "x64";
        prof.DxvkVersion = null;
        setupProfStore.Save(prof);

        var inst = setupLibStore.GetOrCreateInstallation(gameDir.RootPath, "Game");
        inst.GetOrAddExecutable("Game.exe", "Game").LastKnownApi = GraphicsApi.DX11;
        inst.PendingAction = PendingAction.Install("latest", "Queued from previous session");
        setupLibStore.Save(inst);

        // Reconstruct fresh stores and manager (no in-memory _pending state)
        var (manager, libraryStore, profileStore, _, _) = CreateTestManager(storageDir, sourceDir, httpClient);

        int processed = await manager.ProcessAllPendingActionsAsync();
        Assert.Equal(1, processed);

        // Assert deployment succeeded and pending cleared
        string deployedD3D11 = Path.Combine(gameDir.RootPath, "d3d11.dll");
        string deployedDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");
        Assert.True(File.Exists(deployedD3D11));
        Assert.Equal(syntheticD3D11, File.ReadAllBytes(deployedD3D11));
        Assert.True(File.Exists(deployedDxgi));
        Assert.Equal(syntheticDxgi, File.ReadAllBytes(deployedDxgi));

        var refreshedInst = libraryStore.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(refreshedInst);
        Assert.Null(refreshedInst.PendingAction);
        Assert.Equal("v3.1.1", refreshedInst.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, refreshedInst.RestorationState);
    }

    [Fact]
    public async Task FirstRunQueuedEnable_ExplicitPinnedVersion_UsesPreexistingLocalSource_ZeroHttpRequests()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        string exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");

        // Pre-populate explicit version 2.4 in sourceDir
        string v24Dir = Path.Combine(sourceDir.RootPath, "2.4", "x64");
        Directory.CreateDirectory(v24Dir);
        byte[] v24D3D11 = Encoding.UTF8.GetBytes("dxvk-2.4-d3d11");
        byte[] v24Dxgi = Encoding.UTF8.GetBytes("dxvk-2.4-dxgi");
        File.WriteAllBytes(Path.Combine(v24Dir, "d3d11.dll"), v24D3D11);
        File.WriteAllBytes(Path.Combine(v24Dir, "dxgi.dll"), v24Dxgi);

        // Rejecting HTTP handler throws if ANY HTTP request is made
        var httpHandler = new RecordingFakeHttpMessageHandler(req =>
            throw new InvalidOperationException($"Zero HTTP requests allowed for pinned local version, got: {req.RequestUri}"));

        using var httpClient = new HttpClient(httpHandler);
        var (manager, libraryStore, profileStore, _, _) = CreateTestManager(storageDir, sourceDir, httpClient);

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profile.DxvkVersion = "2.4"; // Explicit pinned version
        profileStore.Save(profile);

        var inst = libraryStore.GetOrCreateInstallation(gameDir.RootPath, "Game");
        inst.GetOrAddExecutable("Game.exe", "Game").LastKnownApi = GraphicsApi.DX11;
        inst.PendingAction = PendingAction.Install("2.4", "Queued pinned version");
        libraryStore.Save(inst);

        bool result = await manager.ApplyPendingAsync(profile.ExePath);
        Assert.True(result);
        Assert.Empty(httpHandler.Requests);

        string deployedD3D11 = Path.Combine(gameDir.RootPath, "d3d11.dll");
        Assert.True(File.Exists(deployedD3D11));
        Assert.Equal(v24D3D11, File.ReadAllBytes(deployedD3D11));

        var refreshedInst = libraryStore.FindByInstallationPath(gameDir.RootPath);
        Assert.Equal("2.4", refreshedInst?.ManagedDxvkVersion);
    }

    [Fact]
    public async Task FirstRunQueuedEnable_MetadataUnavailable_FailsWithoutMutatingTarget_RetainsRetryablePendingIntent()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        string exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        string d3d11Path = Path.Combine(gameDir.RootPath, "d3d11.dll");

        // HTTP handler returns 500 Server Error
        var httpHandler = new RecordingFakeHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var httpClient = new HttpClient(httpHandler);
        var (manager, libraryStore, profileStore, _, _) = CreateTestManager(storageDir, sourceDir, httpClient);

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profile.DxvkVersion = null;
        profileStore.Save(profile);

        var inst = libraryStore.GetOrCreateInstallation(gameDir.RootPath, "Game");
        inst.GetOrAddExecutable("Game.exe", "Game").LastKnownApi = GraphicsApi.DX11;
        inst.PendingAction = PendingAction.Install("latest", "Queued while running");
        libraryStore.Save(inst);

        bool result = await manager.ApplyPendingAsync(profile.ExePath);
        Assert.False(result);

        // Assert: No game DLL created, no literal "latest" in sourceDir
        Assert.False(File.Exists(d3d11Path));
        Assert.False(Directory.Exists(Path.Combine(sourceDir.RootPath, "latest")));

        // Assert: Retryable pending intent retained
        var refreshedInst = libraryStore.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(refreshedInst?.PendingAction);
        Assert.Equal("latest", refreshedInst.PendingAction.TargetDxvkVersion);
        Assert.Equal(PendingActionType.Install, refreshedInst.PendingAction.Type);
    }

    [Fact]
    public async Task FirstRunQueuedEnable_LocalLibrary_DownloadsArchiveOnlyOnceAcrossMultipleGamesAndReEnables()
    {
        using var gameDir1 = new SyntheticTestDirectory();
        using var gameDir2 = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        string exe1 = gameDir1.CreateFile("Game1.exe", "bin1");
        string exe2 = gameDir2.CreateFile("Game2.exe", "bin2");

        byte[] d3d11Bytes = Encoding.UTF8.GetBytes("shared-d3d11");
        byte[] dxgiBytes = Encoding.UTF8.GetBytes("shared-dxgi");
        byte[] archiveBytes = CreateSyntheticTarGz("v3.1.1", d3d11Bytes, dxgiBytes);

        string latestUrl = "https://api.github.com/repos/doitsujin/dxvk/releases/latest";
        string downloadUrl = "https://fake.github.com/releases/download/v3.1.1/dxvk-3.1.1.tar.gz";

        var httpHandler = new RecordingFakeHttpMessageHandler(req =>
        {
            if (string.Equals(req.RequestUri?.AbsoluteUri, latestUrl, StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonSerializer.Serialize(new
                {
                    tag_name = "v3.1.1",
                    assets = new[]
                    {
                        new { name = "dxvk-3.1.1.tar.gz", browser_download_url = downloadUrl }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            if (string.Equals(req.RequestUri?.AbsoluteUri, downloadUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(httpHandler);
        var (manager, libraryStore, profileStore, _, _) = CreateTestManager(storageDir, sourceDir, httpClient);

        var prof1 = profileStore.GetOrCreate(exe1);
        prof1.Api = GraphicsApi.DX11;
        prof1.Architecture = "x64";
        profileStore.Save(prof1);

        var prof2 = profileStore.GetOrCreate(exe2);
        prof2.Api = GraphicsApi.DX11;
        prof2.Architecture = "x64";
        profileStore.Save(prof2);

        // 1. Game 1 deploys -> requires initial archive download
        var res1 = await manager.RequestEnableByPathAsync(prof1);
        Assert.Equal(DxvkActionResult.Applied, res1);
        Assert.Equal(1, httpHandler.ArchiveDownloadRequests);

        // 2. Game 2 deploys -> reuses existing local library payload (zero new archive downloads!)
        var res2 = await manager.RequestEnableByPathAsync(prof2);
        Assert.Equal(DxvkActionResult.Applied, res2);
        Assert.Equal(1, httpHandler.ArchiveDownloadRequests);

        // 3. Game 1 restores (disables) and re-enables -> reuses local library payload
        var disableRes = await manager.RequestDisableByPathAsync(prof1);
        Assert.Equal(DxvkActionResult.Applied, disableRes);

        var reEnableRes = await manager.RequestEnableByPathAsync(prof1);
        Assert.Equal(DxvkActionResult.Applied, reEnableRes);

        // Assert: Total archive downloads remain strictly 1 throughout all operations
        Assert.Equal(1, httpHandler.ArchiveDownloadRequests);
    }

    [Fact]
    public async Task FirstRunQueuedEnable_LocalLibrary_FreshCacheAndPayload_DeploysOfflineWithZeroHttp()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        string exePath = gameDir.CreateFile("Game.exe", "bin");
        string v311Dir = Path.Combine(sourceDir.RootPath, "v3.1.1", "x64");
        Directory.CreateDirectory(v311Dir);
        byte[] d3d11 = Encoding.UTF8.GetBytes("offline-d3d11");
        byte[] dxgi = Encoding.UTF8.GetBytes("offline-dxgi");
        File.WriteAllBytes(Path.Combine(v311Dir, "d3d11.dll"), d3d11);
        File.WriteAllBytes(Path.Combine(v311Dir, "dxgi.dll"), dxgi);

        // Strict offline rejecting handler
        var httpHandler = new RecordingFakeHttpMessageHandler(req =>
            throw new HttpRequestException("Offline test: network unreachable"));

        using var httpClient = new HttpClient(httpHandler);
        var (manager, libraryStore, profileStore, cacheStore, _) = CreateTestManager(storageDir, sourceDir, httpClient);

        // Preload fresh cache.json with correct schema
        cacheStore.SaveCachedRelease(new ReleaseInfo
        {
            Version = "v3.1.1",
            DownloadUrl = "https://fake.github.com/dxvk-3.1.1.tar.gz"
        }, "etag-v3.1.1");

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profileStore.Save(profile);

        var result = await manager.RequestEnableByPathAsync(profile);
        Assert.Equal(DxvkActionResult.Applied, result);
        Assert.Empty(httpHandler.Requests);

        Assert.Equal(d3d11, File.ReadAllBytes(Path.Combine(gameDir.RootPath, "d3d11.dll")));
    }

    [Fact]
    public async Task FirstRunQueuedEnable_LocalLibrary_StaleMetadataRefreshFailure_PermitsLocalDeploymentWithoutArchiveDownload()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        string exePath = gameDir.CreateFile("Game.exe", "bin");
        string v311Dir = Path.Combine(sourceDir.RootPath, "v3.1.1", "x64");
        Directory.CreateDirectory(v311Dir);
        byte[] d3d11 = Encoding.UTF8.GetBytes("stale-cache-d3d11");
        byte[] dxgi = Encoding.UTF8.GetBytes("stale-cache-dxgi");
        File.WriteAllBytes(Path.Combine(v311Dir, "d3d11.dll"), d3d11);
        File.WriteAllBytes(Path.Combine(v311Dir, "dxgi.dll"), dxgi);

        // Handler returns 503 for metadata, rejects archive download
        var httpHandler = new RecordingFakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("/releases/latest") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            throw new InvalidOperationException("Archive download should never be attempted!");
        });

        using var httpClient = new HttpClient(httpHandler);
        var (manager, libraryStore, profileStore, cacheStore, _) = CreateTestManager(storageDir, sourceDir, httpClient);

        // Save expired cache metadata (2 days old)
        cacheStore.SaveCachedRelease(new ReleaseInfo
        {
            Version = "v3.1.1",
            DownloadUrl = "https://fake.github.com/dxvk-3.1.1.tar.gz"
        }, "etag-old");

        // Manually age cache to make it expired
        var cached = cacheStore.LoadCachedRelease()!;
        cached.CachedAt = DateTime.UtcNow.AddDays(-2);
        string cacheFile = Path.Combine(storageDir.RootPath, "release-cache.json");
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true }));

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profileStore.Save(profile);

        // Deploy must succeed from local source tree without archive download
        var result = await manager.RequestEnableByPathAsync(profile);
        Assert.Equal(DxvkActionResult.Applied, result);
        Assert.Equal(0, httpHandler.ArchiveDownloadRequests);
        Assert.Equal(d3d11, File.ReadAllBytes(Path.Combine(gameDir.RootPath, "d3d11.dll")));
    }

    [Fact]
    public async Task FirstRunQueuedEnable_LocalLibrary_RetainsOlderReleasePayloadsDuringNewerOperations()
    {
        using var gameDir1 = new SyntheticTestDirectory();
        using var gameDir2 = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        string exe1 = gameDir1.CreateFile("Game1.exe", "bin1");
        string exe2 = gameDir2.CreateFile("Game2.exe", "bin2");

        // 1. Establish older 2.4 release in sourceDir
        string v24Dir = Path.Combine(sourceDir.RootPath, "2.4", "x64");
        Directory.CreateDirectory(v24Dir);
        byte[] v24D3D11 = Encoding.UTF8.GetBytes("retained-2.4-d3d11-content");
        byte[] v24Dxgi = Encoding.UTF8.GetBytes("retained-2.4-dxgi-content");
        string v24D3D11Path = Path.Combine(v24Dir, "d3d11.dll");
        string v24DxgiPath = Path.Combine(v24Dir, "dxgi.dll");
        File.WriteAllBytes(v24D3D11Path, v24D3D11);
        File.WriteAllBytes(v24DxgiPath, v24Dxgi);

        string v24D3D11Hash = Convert.ToHexString(SHA256.HashData(v24D3D11));
        string v24DxgiHash = Convert.ToHexString(SHA256.HashData(v24Dxgi));

        // 2. Setup newer v3.1.1 download
        byte[] v311D3D11 = Encoding.UTF8.GetBytes("newer-3.1.1-d3d11");
        byte[] v311Dxgi = Encoding.UTF8.GetBytes("newer-3.1.1-dxgi");
        byte[] archiveBytes = CreateSyntheticTarGz("v3.1.1", v311D3D11, v311Dxgi);

        string latestUrl = "https://api.github.com/repos/doitsujin/dxvk/releases/latest";
        string downloadUrl = "https://fake.github.com/releases/download/v3.1.1/dxvk-3.1.1.tar.gz";

        var httpHandler = new RecordingFakeHttpMessageHandler(req =>
        {
            if (string.Equals(req.RequestUri?.AbsoluteUri, latestUrl, StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonSerializer.Serialize(new
                {
                    tag_name = "v3.1.1",
                    assets = new[]
                    {
                        new { name = "dxvk-3.1.1.tar.gz", browser_download_url = downloadUrl }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            if (string.Equals(req.RequestUri?.AbsoluteUri, downloadUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(httpHandler);
        var (manager, libraryStore, profileStore, _, _) = CreateTestManager(storageDir, sourceDir, httpClient);

        // 3. Deploy newer v3.1.1 to Game 1
        var prof1 = profileStore.GetOrCreate(exe1);
        prof1.Api = GraphicsApi.DX11;
        prof1.Architecture = "x64";
        profileStore.Save(prof1);

        var res1 = await manager.RequestEnableByPathAsync(prof1);
        Assert.Equal(DxvkActionResult.Applied, res1);

        // 4. Verify older 2.4 payload remains completely intact with identical SHA256 hashes
        Assert.True(File.Exists(v24D3D11Path));
        Assert.True(File.Exists(v24DxgiPath));
        Assert.Equal(v24D3D11Hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(v24D3D11Path))));
        Assert.Equal(v24DxgiHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(v24DxgiPath))));

        // 5. Deploy pinned 2.4 to Game 2 -> deploys without new archive downloads
        var prof2 = profileStore.GetOrCreate(exe2);
        prof2.Api = GraphicsApi.DX11;
        prof2.Architecture = "x64";
        prof2.DxvkVersion = "2.4";
        profileStore.Save(prof2);

        int downloadRequestsBeforeGame2 = httpHandler.ArchiveDownloadRequests;
        var res2 = await manager.RequestEnableByPathAsync(prof2);
        Assert.Equal(DxvkActionResult.Applied, res2);
        Assert.Equal(downloadRequestsBeforeGame2, httpHandler.ArchiveDownloadRequests);
        Assert.Equal(v24D3D11, File.ReadAllBytes(Path.Combine(gameDir2.RootPath, "d3d11.dll")));
    }
}
