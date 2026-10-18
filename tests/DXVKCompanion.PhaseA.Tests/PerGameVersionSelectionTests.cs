using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.UI;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests;

public sealed class PerGameVersionSelectionTests : IDisposable
{
    private readonly SyntheticTestDirectory _storageDir;
    private readonly SyntheticTestDirectory _sourceDir;
    private readonly SyntheticTestDirectory _gameDir;

    private static readonly byte[] V23D3D11 = new byte[] { 0x23, 0x11, 0x01 };
    private static readonly byte[] V23Dxgi = new byte[] { 0x23, 0x00, 0x02 };
    private static readonly byte[] V24D3D11 = new byte[] { 0x24, 0x11, 0x01 };
    private static readonly byte[] V24Dxgi = new byte[] { 0x24, 0x00, 0x02 };
    private static readonly byte[] OriginalD3D11 = new byte[] { 0x99, 0x11, 0x99 };

    public PerGameVersionSelectionTests()
    {
        _storageDir = new SyntheticTestDirectory();
        _sourceDir = new SyntheticTestDirectory();
        _gameDir = new SyntheticTestDirectory();

        // Seed source directory with local releases v2.3 and v2.4 for x64
        SeedLocalRelease(_sourceDir.RootPath, "v2.3", V23D3D11, V23Dxgi);
        SeedLocalRelease(_sourceDir.RootPath, "v2.4", V24D3D11, V24Dxgi);
    }

    public void Dispose()
    {
        _storageDir.Dispose();
        _sourceDir.Dispose();
        _gameDir.Dispose();
    }

    private static void SeedLocalRelease(string sourceRoot, string version, byte[] d3d11, byte[] dxgi)
    {
        string archDir = Path.Combine(sourceRoot, version, "x64");
        Directory.CreateDirectory(archDir);
        File.WriteAllBytes(Path.Combine(archDir, "d3d11.dll"), d3d11);
        File.WriteAllBytes(Path.Combine(archDir, "dxgi.dll"), dxgi);
    }

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

    private (DxvkManager Manager, GameLibraryStore LibraryStore, ProfileStore ProfileStore, CacheStore CacheStore) CreateTestManager(
        HttpClient httpClient)
    {
        string libraryPath = Path.Combine(_storageDir.RootPath, "game-library.json");
        string backupsPath = Path.Combine(_storageDir.RootPath, "backups");
        string profilesPath = Path.Combine(_storageDir.RootPath, "games.json");
        string cachePath = Path.Combine(_storageDir.RootPath, "release-cache.json");

        var libraryStore = new GameLibraryStore(libraryPath, backupsPath, profilesPath);
        var profileStore = new ProfileStore(profilesPath);
        var cacheStore = new CacheStore(cachePath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        var installer = new DxvkInstaller(httpClient, engine, libraryStore, _sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, libraryStore);
        var github = new DxvkGithubClient(httpClient, cacheStore);
        var manager = new DxvkManager(installer, rollback, github, profileStore, libraryStore);

        return (manager, libraryStore, profileStore, cacheStore);
    }

    private GameProfile SetupGame(string gameSubdir, byte[]? initialD3D11 = null)
    {
        string dir = Path.Combine(_gameDir.RootPath, gameSubdir);
        Directory.CreateDirectory(dir);
        string exePath = Path.Combine(dir, "Game.exe");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00 }); // MZ header

        if (initialD3D11 != null)
        {
            File.WriteAllBytes(Path.Combine(dir, "d3d11.dll"), initialD3D11);
        }

        return new GameProfile
        {
            ExePath = exePath,
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            DxvkEnabled = false
        };
    }

    [Fact]
    public async Task ImmediatePinnedDeploy_DeploysExactOlderVersion_WhenNewerLatestMetadataExists()
    {
        var handler = new RecordingFakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("/releases/latest") == true)
            {
                var releaseJson = JsonSerializer.Serialize(new
                {
                    tag_name = "v2.5",
                    assets = new[]
                    {
                        new { name = "dxvk-2.5.tar.gz", browser_download_url = "https://example.com/dxvk-2.5.tar.gz" }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, System.Text.Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        var (manager, libraryStore, profileStore, _) = CreateTestManager(client);

        var profile = SetupGame("GameA", OriginalD3D11);
        profileStore.Save(profile);

        var installation = libraryStore.GetOrCreateInstallation(Path.GetDirectoryName(profile.ExePath)!, "GameA");
        installation.ManagementPolicy = ManagementPolicy.Automatic();
        installation.ManagementPolicy.PinnedDxvkVersion = "v2.3";
        libraryStore.Save(installation);

        var result = await manager.RequestEnableByPathAsync(profile);

        Assert.Equal(DxvkActionResult.Applied, result);
        Assert.True(profile.DxvkEnabled);
        Assert.Equal("v2.3", profile.DxvkVersion);

        // Verify exact v2.3 bytes were deployed
        string deployedD3D11 = Path.Combine(Path.GetDirectoryName(profile.ExePath)!, "d3d11.dll");
        string deployedDxgi = Path.Combine(Path.GetDirectoryName(profile.ExePath)!, "dxgi.dll");
        Assert.Equal(V23D3D11, File.ReadAllBytes(deployedD3D11));
        Assert.Equal(V23Dxgi, File.ReadAllBytes(deployedDxgi));

        // 0 archive download requests were made
        Assert.Equal(0, handler.ArchiveDownloadRequests);

        // Up to date pinned target is not outdated even though latest is v2.5
        var latestRelease = new ReleaseInfo { Version = "v2.5" };
        Assert.False(manager.UpdateAvailable(profile, latestRelease));
    }

    [Fact]
    public async Task OfflinePinnedDeploy_IssuesZeroHttpRequests()
    {
        var handler = new RecordingFakeHttpMessageHandler(_ =>
        {
            throw new InvalidOperationException("Zero HTTP requests expected for offline pinned deploy.");
        });

        using var client = new HttpClient(handler);
        var (manager, libraryStore, profileStore, _) = CreateTestManager(client);

        var profile = SetupGame("OfflineGame", OriginalD3D11);
        profileStore.Save(profile);

        var installation = libraryStore.GetOrCreateInstallation(Path.GetDirectoryName(profile.ExePath)!, "OfflineGame");
        installation.ManagementPolicy.PinnedDxvkVersion = "v2.4";
        libraryStore.Save(installation);

        var result = await manager.RequestEnableByPathAsync(profile);

        Assert.Equal(DxvkActionResult.Applied, result);
        Assert.True(profile.DxvkEnabled);
        Assert.Equal("v2.4", profile.DxvkVersion);

        string deployedD3D11 = Path.Combine(Path.GetDirectoryName(profile.ExePath)!, "d3d11.dll");
        Assert.Equal(V24D3D11, File.ReadAllBytes(deployedD3D11));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AllPinnedBatch_UpdateAllEnabledAsync_IssuesZeroHttpRequests()
    {
        var handler = new RecordingFakeHttpMessageHandler(_ =>
        {
            throw new InvalidOperationException("Zero HTTP requests expected when all enabled games are pinned.");
        });

        using var client = new HttpClient(handler);
        var (manager, libraryStore, profileStore, _) = CreateTestManager(client);

        // Game 1: pinned to v2.4, currently on v2.3 -> needs update to v2.4
        var profile1 = SetupGame("Game1");
        profile1.DxvkEnabled = true;
        profile1.DxvkVersion = "v2.3";
        profileStore.Save(profile1);
        var inst1 = libraryStore.GetOrCreateInstallation(Path.GetDirectoryName(profile1.ExePath)!, "Game1");
        inst1.ManagementPolicy.PinnedDxvkVersion = "v2.4";
        libraryStore.Save(inst1);

        // Game 2: pinned to v2.3, currently on v2.3 -> already up to date
        var profile2 = SetupGame("Game2");
        profile2.DxvkEnabled = true;
        profile2.DxvkVersion = "v2.3";
        profileStore.Save(profile2);
        var inst2 = libraryStore.GetOrCreateInstallation(Path.GetDirectoryName(profile2.ExePath)!, "Game2");
        inst2.ManagementPolicy.PinnedDxvkVersion = "v2.3";
        libraryStore.Save(inst2);

        var results = await manager.UpdateAllEnabledAsync();

        Assert.Single(results);
        Assert.True(results.ContainsKey(profile1.ExePath));
        Assert.Equal(DxvkActionResult.Applied, results[profile1.ExePath]);
        Assert.Equal("v2.4", profile1.DxvkVersion);

        // Zero HTTP requests
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MixedBatch_WhenLatestFails_ProceedsWithPinnedUpdates()
    {
        var handler = new RecordingFakeHttpMessageHandler(req =>
        {
            // Simulate GitHub 500 error for latest release metadata
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        using var client = new HttpClient(handler);
        var (manager, libraryStore, profileStore, _) = CreateTestManager(client);

        // Game 1: Pinned to v2.4, currently on v2.3 -> needs update
        var profile1 = SetupGame("PinnedGame");
        profile1.DxvkEnabled = true;
        profile1.DxvkVersion = "v2.3";
        profileStore.Save(profile1);
        var inst1 = libraryStore.GetOrCreateInstallation(Path.GetDirectoryName(profile1.ExePath)!, "PinnedGame");
        inst1.ManagementPolicy.PinnedDxvkVersion = "v2.4";
        libraryStore.Save(inst1);

        // Game 2: Unpinned (Latest), currently on v2.3
        var profile2 = SetupGame("UnpinnedGame");
        profile2.DxvkEnabled = true;
        profile2.DxvkVersion = "v2.3";
        profileStore.Save(profile2);
        var inst2 = libraryStore.GetOrCreateInstallation(Path.GetDirectoryName(profile2.ExePath)!, "UnpinnedGame");
        inst2.ManagementPolicy.PinnedDxvkVersion = null;
        libraryStore.Save(inst2);

        var results = await manager.UpdateAllEnabledAsync();

        // Pinned game updated successfully despite metadata 500 failure
        Assert.Single(results);
        Assert.True(results.ContainsKey(profile1.ExePath));
        Assert.Equal(DxvkActionResult.Applied, results[profile1.ExePath]);
        Assert.Equal("v2.4", profile1.DxvkVersion);
    }

    [Fact]
    public async Task ExitStartupPendingQueuedApply_PreservesSnapshotTargetAndZeroNetwork()
    {
        var handler = new RecordingFakeHttpMessageHandler(_ =>
        {
            throw new InvalidOperationException("Zero network requests expected.");
        });

        using var client = new HttpClient(handler);
        var (manager1, libraryStore1, profileStore1, _) = CreateTestManager(client);

        var profile = SetupGame("RunningGame");
        profileStore1.Save(profile);

        var inst = libraryStore1.GetOrCreateInstallation(Path.GetDirectoryName(profile.ExePath)!, "RunningGame");
        inst.ManagementPolicy.PinnedDxvkVersion = "v2.3";
        libraryStore1.Save(inst);

        using var fakeProcess = Process.GetCurrentProcess();

        // Queue while running
        var result = await manager1.RequestEnableAsync(profile, fakeProcess);
        Assert.Equal(DxvkActionResult.Queued, result);

        var reloadedInst = libraryStore1.FindByInstallationPath(Path.GetDirectoryName(profile.ExePath)!);
        Assert.NotNull(reloadedInst?.PendingAction);
        Assert.Equal("v2.3", reloadedInst!.PendingAction!.TargetDxvkVersion);

        // Simulate app restart with fresh manager instances
        var (manager2, libraryStore2, profileStore2, _) = CreateTestManager(client);
        var processedCount = await manager2.ProcessAllPendingActionsAsync();

        Assert.Equal(1, processedCount);

        var updatedProfile = profileStore2.GetOrCreate(profile.ExePath);
        Assert.True(updatedProfile.DxvkEnabled);
        Assert.Equal("v2.3", updatedProfile.DxvkVersion);

        string deployedD3D11 = Path.Combine(Path.GetDirectoryName(profile.ExePath)!, "d3d11.dll");
        Assert.Equal(V23D3D11, File.ReadAllBytes(deployedD3D11));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UnpinnedHistoricalOlderProfile_QueuesLatestIntent_NotMistakenForPin()
    {
        using var client = new HttpClient(new RecordingFakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var (manager, libraryStore, profileStore, _) = CreateTestManager(client);

        var profile = SetupGame("HistoricalGame");
        profile.DxvkVersion = "v2.2"; // Historical installed version
        profileStore.Save(profile);

        var inst = libraryStore.GetOrCreateInstallation(Path.GetDirectoryName(profile.ExePath)!, "HistoricalGame");
        inst.ManagementPolicy.PinnedDxvkVersion = null; // Unpinned!
        libraryStore.Save(inst);

        using var fakeProcess = Process.GetCurrentProcess();

        var result = await manager.RequestUpdateAsync(profile, fakeProcess);
        Assert.Equal(DxvkActionResult.Queued, result);

        var reloadedInst = libraryStore.FindByInstallationPath(Path.GetDirectoryName(profile.ExePath)!);
        Assert.NotNull(reloadedInst?.PendingAction);
        // Captured target must be "latest", NOT the historical "v2.2"
        Assert.Equal("latest", reloadedInst!.PendingAction!.TargetDxvkVersion);
    }

    [Fact]
    public async Task UnavailableVersion_FailsClearlyWithoutTargetMutation()
    {
        using var client = new HttpClient(new RecordingFakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var (manager, libraryStore, profileStore, _) = CreateTestManager(client);

        var profile = SetupGame("UnavailableGame", OriginalD3D11);
        profileStore.Save(profile);

        var inst = libraryStore.GetOrCreateInstallation(Path.GetDirectoryName(profile.ExePath)!, "UnavailableGame");
        inst.ManagementPolicy.PinnedDxvkVersion = "v99.0"; // Unavailable release
        libraryStore.Save(inst);

        var result = await manager.RequestEnableByPathAsync(profile);

        Assert.Equal(DxvkActionResult.Failed, result);
        Assert.False(profile.DxvkEnabled);
        Assert.Null(profile.DxvkVersion);
        Assert.Contains("Requested DXVK version 'v99.0' is not available locally.", manager.LastRefusalReason);

        // Target files remain intact
        string gameD3D11 = Path.Combine(Path.GetDirectoryName(profile.ExePath)!, "d3d11.dll");
        Assert.Equal(OriginalD3D11, File.ReadAllBytes(gameD3D11));

        // Pinned version selection is NOT silently reverted
        var reloadedInst = libraryStore.FindByInstallationPath(Path.GetDirectoryName(profile.ExePath)!);
        Assert.Equal("v99.0", reloadedInst?.ManagementPolicy?.PinnedDxvkVersion);
    }

    [Fact]
    public async Task SavePreferenceWithoutMutatingInstalledVersion_AndDeployOnlyOnExplicitAction()
    {
        using var client = new HttpClient(new RecordingFakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var (manager, libraryStore, profileStore, _) = CreateTestManager(client);

        var profile = SetupGame("SaveSettingsGame", OriginalD3D11);
        profileStore.Save(profile);

        // 1. Initial deployment of v2.3
        var inst = libraryStore.GetOrCreateInstallation(Path.GetDirectoryName(profile.ExePath)!, "SaveSettingsGame");
        inst.ManagementPolicy.PinnedDxvkVersion = "v2.3";
        libraryStore.Save(inst);

        var firstResult = await manager.RequestEnableByPathAsync(profile);
        Assert.Equal(DxvkActionResult.Applied, firstResult);
        Assert.Equal("v2.3", profile.DxvkVersion);

        string gameD3D11 = Path.Combine(Path.GetDirectoryName(profile.ExePath)!, "d3d11.dll");
        string gameDxgi = Path.Combine(Path.GetDirectoryName(profile.ExePath)!, "dxgi.dll");
        Assert.Equal(V23D3D11, File.ReadAllBytes(gameD3D11));
        Assert.Equal(V23Dxgi, File.ReadAllBytes(gameDxgi));

        // 2. User changes preference to v2.4 in GameDetails and saves
        GameDetailsVersionSelection.ApplyVersionSelection(inst.ManagementPolicy, "v2.4");
        libraryStore.Save(inst);

        // Verify: disk DLLs and profile recorded version are STILL v2.3! No silent/premature deploy
        var reloadedProfile = profileStore.GetOrCreate(profile.ExePath);
        Assert.Equal("v2.3", reloadedProfile.DxvkVersion);
        Assert.Equal(V23D3D11, File.ReadAllBytes(gameD3D11));
        Assert.Equal(V23Dxgi, File.ReadAllBytes(gameDxgi));

        // Update is available because target is now v2.4 while profile is v2.3
        Assert.True(manager.UpdateAvailable(reloadedProfile));

        // 3. User explicitly updates
        var updateResult = await manager.RequestUpdateByPathAsync(reloadedProfile);
        Assert.Equal(DxvkActionResult.Applied, updateResult);
        Assert.Equal("v2.4", reloadedProfile.DxvkVersion);
        Assert.Equal(V24D3D11, File.ReadAllBytes(gameD3D11));
        Assert.Equal(V24Dxgi, File.ReadAllBytes(gameDxgi));

        // 4. Disable / Restore returns to original baseline cleanly
        var disableResult = await manager.DisableDxvkAsync(reloadedProfile);
        Assert.True(disableResult);
        Assert.False(reloadedProfile.DxvkEnabled);
        Assert.Equal(OriginalD3D11, File.ReadAllBytes(gameD3D11));
        Assert.False(File.Exists(gameDxgi));

        // 5. Older downloaded payload files in source library remain retained intact
        string v23Dir = Path.Combine(_sourceDir.RootPath, "v2.3", "x64");
        string v24Dir = Path.Combine(_sourceDir.RootPath, "v2.4", "x64");
        Assert.True(File.Exists(Path.Combine(v23Dir, "d3d11.dll")));
        Assert.True(File.Exists(Path.Combine(v23Dir, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(v24Dir, "d3d11.dll")));
        Assert.True(File.Exists(Path.Combine(v24Dir, "dxgi.dll")));
    }
}
