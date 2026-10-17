using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests;

public sealed class PersistedDeploymentLifecycleAcceptanceTests
{
    private sealed class RejectingCountingHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            throw new InvalidOperationException($"Unexpected HTTP request in synthetic test: {request.Method} {request.RequestUri}");
        }
    }

    [Fact]
    public async Task PersistedLifecycle_InstallUpdateRestore_PreservesBaselinesAndRestoresByteIdentical()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        // 1. Establish isolated storage paths
        string libraryPath = Path.Combine(storageDir.RootPath, "game-library.json");
        string backupsPath = Path.Combine(storageDir.RootPath, "backups");
        string profilesPath = Path.Combine(storageDir.RootPath, "games.json");
        string cachePath = Path.Combine(storageDir.RootPath, "release-cache.json");

        // 2. Establish original game directory: native d3d11.dll, no dxgi.dll, preexisting dxvk.conf with custom options
        string exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        string nativeD3D11Content = "native-d3d11-v1.0-payload-bytes";
        string d3d11Path = gameDir.CreateFile("d3d11.dll", nativeD3D11Content);
        string nativeConfContent = "# Custom preexisting dxvk.conf for Game\n# Distinctive user comments\ndxvk.hud = compiler\ndxgi.syncInterval = 1\n";
        string confPath = gameDir.CreateFile("dxvk.conf", nativeConfContent);
        string dxgiPath = Path.Combine(gameDir.RootPath, "dxgi.dll");

        var originalD3D11Identity = FileIdentity.Capture(d3d11Path);
        var originalConfIdentity = FileIdentity.Capture(confPath);
        Assert.False(File.Exists(dxgiPath));

        // 3. Populate two distinguishable synthetic DXVK release trees (2.5 and 2.6)
        var v25ArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(v25ArchDir);
        File.WriteAllText(Path.Combine(v25ArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-payload");
        File.WriteAllText(Path.Combine(v25ArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-payload");

        var v26ArchDir = Path.Combine(sourceDir.RootPath, "2.6", "x64");
        Directory.CreateDirectory(v26ArchDir);
        File.WriteAllText(Path.Combine(v26ArchDir, "d3d11.dll"), "dxvk-2.6-d3d11-payload");
        File.WriteAllText(Path.Combine(v26ArchDir, "dxgi.dll"), "dxvk-2.6-dxgi-payload");

        var httpHandler = new RejectingCountingHandler();
        using var httpClient = new HttpClient(httpHandler);

        // --- Step 2: Install A (v2.5) through DxvkManager's supported entry point ---
        var cacheStore1 = new CacheStore(cachePath);
        cacheStore1.SaveCachedRelease(new ReleaseInfo { Version = "2.5", DownloadUrl = "" }, "etag-2.5");

        var profileStore1 = new ProfileStore(profilesPath);
        var libraryStore1 = new GameLibraryStore(libraryPath, backupsPath, profilesPath);
        var engine1 = new MultiFileTransactionEngine(backupsPath);
        var installer1 = new DxvkInstaller(httpClient, engine1, libraryStore1, sourceDir.RootPath);
        var rollback1 = new DxvkRollback(engine1, libraryStore1);
        var github1 = new DxvkGithubClient(httpClient, cacheStore1);
        var manager1 = new DxvkManager(installer1, rollback1, github1, profileStore1, libraryStore1);

        var profile1 = profileStore1.GetOrCreate(exePath);
        profile1.Api = GraphicsApi.DX11;
        profile1.Architecture = "x64";
        profile1.HudEnabled = true;
        profileStore1.Save(profile1);

        var installResult = await manager1.RequestEnableByPathAsync(profile1);
        Assert.Equal(DxvkActionResult.Applied, installResult);

        // Verify deployed v2.5 wrapper bytes and active state
        Assert.Equal("dxvk-2.5-d3d11-payload", File.ReadAllText(d3d11Path));
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal("dxvk-2.5-dxgi-payload", File.ReadAllText(dxgiPath));
        Assert.True(File.Exists(confPath));

        Assert.True(profile1.DxvkEnabled);
        Assert.Equal("2.5", profile1.DxvkVersion);

        var inst1 = libraryStore1.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(inst1);
        Assert.Equal("2.5", inst1!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, inst1.RestorationState);

        var d3d11Record1 = inst1.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record1);
        Assert.Equal(FileOriginalState.Existing, d3d11Record1!.OriginalState);
        Assert.Equal(originalD3D11Identity.Sha256, d3d11Record1.OriginalSha256);
        var d3d11BackupPath1 = Path.Combine(backupsPath, d3d11Record1.BackupRelativePath!);
        Assert.True(File.Exists(d3d11BackupPath1));
        Assert.Equal(nativeD3D11Content, File.ReadAllText(d3d11BackupPath1));

        var confRecord1 = inst1.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord1);
        Assert.Equal(FileOriginalState.Existing, confRecord1!.OriginalState);
        Assert.Equal(originalConfIdentity.Sha256, confRecord1.OriginalSha256);
        var confBackupPath1 = Path.Combine(backupsPath, confRecord1.BackupRelativePath!);
        Assert.True(File.Exists(confBackupPath1));
        Assert.Equal(nativeConfContent, File.ReadAllText(confBackupPath1));

        var dxgiRecord1 = inst1.FindManagedFile("dxgi.dll");
        Assert.NotNull(dxgiRecord1);
        Assert.Equal(FileOriginalState.Missing, dxgiRecord1!.OriginalState);
        Assert.Null(dxgiRecord1.OriginalSha256);
        Assert.Null(dxgiRecord1.BackupRelativePath);

        // --- Step 3: Reconstruct fresh stores from disk. Update to B (v2.6) via manager's real update request ---
        var cacheStore2 = new CacheStore(cachePath);
        cacheStore2.SaveCachedRelease(new ReleaseInfo { Version = "2.6", DownloadUrl = "" }, "etag-2.6");

        var profileStore2 = new ProfileStore(profilesPath);
        var libraryStore2 = new GameLibraryStore(libraryPath, backupsPath, profilesPath);
        var engine2 = new MultiFileTransactionEngine(backupsPath);
        var installer2 = new DxvkInstaller(httpClient, engine2, libraryStore2, sourceDir.RootPath);
        var rollback2 = new DxvkRollback(engine2, libraryStore2);
        var github2 = new DxvkGithubClient(httpClient, cacheStore2);
        var manager2 = new DxvkManager(installer2, rollback2, github2, profileStore2, libraryStore2);

        var profile2 = profileStore2.GetOrCreate(exePath);
        var latest2 = await manager2.GetLatestReleaseAsync();
        Assert.NotNull(latest2);
        Assert.True(manager2.UpdateAvailable(profile2, latest2!));

        var updateResult = await manager2.RequestUpdateByPathAsync(profile2);
        Assert.Equal(DxvkActionResult.Applied, updateResult);

        // Assert v2.6 deployed bytes and persisted active version
        Assert.Equal("dxvk-2.6-d3d11-payload", File.ReadAllText(d3d11Path));
        Assert.Equal("dxvk-2.6-dxgi-payload", File.ReadAllText(dxgiPath));
        Assert.Equal("2.6", profile2.DxvkVersion);

        var inst2 = libraryStore2.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal("2.6", inst2.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, inst2.RestorationState);

        // Assert original native/config baselines and backups still identify original content, NEVER A's wrapper bytes
        var d3d11Record2 = inst2.FindManagedFile("d3d11.dll")!;
        Assert.Equal(FileOriginalState.Existing, d3d11Record2.OriginalState);
        Assert.Equal(originalD3D11Identity.Sha256, d3d11Record2.OriginalSha256);
        Assert.NotEqual(FileIdentity.Capture(Path.Combine(v25ArchDir, "d3d11.dll")).Sha256, d3d11Record2.OriginalSha256);
        var d3d11BackupPath2 = Path.Combine(backupsPath, d3d11Record2.BackupRelativePath!);
        Assert.Equal(nativeD3D11Content, File.ReadAllText(d3d11BackupPath2));

        var confRecord2 = inst2.FindManagedFile("dxvk.conf")!;
        Assert.Equal(FileOriginalState.Existing, confRecord2.OriginalState);
        Assert.Equal(originalConfIdentity.Sha256, confRecord2.OriginalSha256);
        var confBackupPath2 = Path.Combine(backupsPath, confRecord2.BackupRelativePath!);
        Assert.Equal(nativeConfContent, File.ReadAllText(confBackupPath2));

        var dxgiRecord2 = inst2.FindManagedFile("dxgi.dll")!;
        Assert.Equal(FileOriginalState.Missing, dxgiRecord2.OriginalState);
        Assert.Null(dxgiRecord2.OriginalSha256);

        // --- Step 4: Reload again and restore original baseline through the manager ---
        var profileStore3 = new ProfileStore(profilesPath);
        var libraryStore3 = new GameLibraryStore(libraryPath, backupsPath, profilesPath);
        var engine3 = new MultiFileTransactionEngine(backupsPath);
        var installer3 = new DxvkInstaller(httpClient, engine3, libraryStore3, sourceDir.RootPath);
        var rollback3 = new DxvkRollback(engine3, libraryStore3);
        var github3 = new DxvkGithubClient(httpClient, new CacheStore(cachePath));
        var manager3 = new DxvkManager(installer3, rollback3, github3, profileStore3, libraryStore3);

        var profile3 = profileStore3.GetOrCreate(exePath);
        var restoreResult = await manager3.RequestDisableByPathAsync(profile3);
        Assert.Equal(DxvkActionResult.Applied, restoreResult);

        // Verify byte-identical original DLL and config restored
        Assert.True(File.Exists(d3d11Path));
        Assert.Equal(nativeD3D11Content, File.ReadAllText(d3d11Path));
        Assert.Equal(originalD3D11Identity, FileIdentity.Capture(d3d11Path));

        Assert.True(File.Exists(confPath));
        Assert.Equal(nativeConfContent, File.ReadAllText(confPath));
        Assert.Equal(originalConfIdentity, FileIdentity.Capture(confPath));

        // Verify removal of originally absent wrapper files
        Assert.False(File.Exists(dxgiPath));

        // Verify cleared installation managed version, Restored state, and disabled profile
        var inst3 = libraryStore3.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Null(inst3.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Restored, inst3.RestorationState);
        Assert.False(profile3.DxvkEnabled);
        Assert.Equal("2.6", profile3.DxvkVersion); // Retained historical profile version allowed

        // --- Step 5: Repeat restore and normal ManagedFileInspector scan after another reload ---
        var profileStore4 = new ProfileStore(profilesPath);
        var libraryStore4 = new GameLibraryStore(libraryPath, backupsPath, profilesPath);
        var engine4 = new MultiFileTransactionEngine(backupsPath);
        var installer4 = new DxvkInstaller(httpClient, engine4, libraryStore4, sourceDir.RootPath);
        var rollback4 = new DxvkRollback(engine4, libraryStore4);
        var github4 = new DxvkGithubClient(httpClient, new CacheStore(cachePath));
        var manager4 = new DxvkManager(installer4, rollback4, github4, profileStore4, libraryStore4);

        var profile4 = profileStore4.GetOrCreate(exePath);
        var repeatRestoreResult = await manager4.RequestDisableByPathAsync(profile4);
        Assert.Equal(DxvkActionResult.Applied, repeatRestoreResult);

        // Verify no target mutation occurred during repeated restore
        Assert.Equal(originalD3D11Identity, FileIdentity.Capture(d3d11Path));
        Assert.Equal(originalConfIdentity, FileIdentity.Capture(confPath));
        Assert.False(File.Exists(dxgiPath));

        // Verify ManagedFileInspector scan verifies original baseline cleanly without attention or refusal
        var inspector = new ManagedFileInspector(libraryStore4);
        var inst4 = libraryStore4.FindByInstallationPath(gameDir.RootPath)!;
        bool scanChanged = inspector.InspectInstallation(inst4);
        Assert.False(scanChanged);
        Assert.Equal(RestorationState.Restored, inst4.RestorationState);
        Assert.Null(inst4.LastRefusalReason);
        Assert.All(inst4.ManagedFiles, f => Assert.Equal(ManagedFileState.Consistent, f.CurrentState));

        // Verify zero external network requests were made across the entire lifecycle
        Assert.Equal(0, httpHandler.RequestCount);
    }

    [Fact]
    public async Task PersistedLifecycle_WhenManagedTargetTamperedBeforeRestore_RefusesWithoutMutation_PreservesStateAndEvidence()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        string libraryPath = Path.Combine(storageDir.RootPath, "game-library.json");
        string backupsPath = Path.Combine(storageDir.RootPath, "backups");
        string profilesPath = Path.Combine(storageDir.RootPath, "games.json");
        string cachePath = Path.Combine(storageDir.RootPath, "release-cache.json");

        string exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        string nativeD3D11Content = "native-d3d11-v1.0-payload-bytes";
        string d3d11Path = gameDir.CreateFile("d3d11.dll", nativeD3D11Content);
        string nativeConfContent = "# Custom preexisting dxvk.conf\ndxvk.hud = compiler\ndxgi.syncInterval = 1\n";
        string confPath = gameDir.CreateFile("dxvk.conf", nativeConfContent);
        string dxgiPath = Path.Combine(gameDir.RootPath, "dxgi.dll");

        var v25ArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(v25ArchDir);
        File.WriteAllText(Path.Combine(v25ArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-payload");
        File.WriteAllText(Path.Combine(v25ArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-payload");

        var v26ArchDir = Path.Combine(sourceDir.RootPath, "2.6", "x64");
        Directory.CreateDirectory(v26ArchDir);
        File.WriteAllText(Path.Combine(v26ArchDir, "d3d11.dll"), "dxvk-2.6-d3d11-payload");
        File.WriteAllText(Path.Combine(v26ArchDir, "dxgi.dll"), "dxvk-2.6-dxgi-payload");

        var httpHandler = new RejectingCountingHandler();
        using var httpClient = new HttpClient(httpHandler);

        // Step 1: Install A (v2.5)
        var cacheStore1 = new CacheStore(cachePath);
        cacheStore1.SaveCachedRelease(new ReleaseInfo { Version = "2.5", DownloadUrl = "" }, "etag-2.5");

        var profileStore1 = new ProfileStore(profilesPath);
        var libraryStore1 = new GameLibraryStore(libraryPath, backupsPath, profilesPath);
        var engine1 = new MultiFileTransactionEngine(backupsPath);
        var installer1 = new DxvkInstaller(httpClient, engine1, libraryStore1, sourceDir.RootPath);
        var rollback1 = new DxvkRollback(engine1, libraryStore1);
        var github1 = new DxvkGithubClient(httpClient, cacheStore1);
        var manager1 = new DxvkManager(installer1, rollback1, github1, profileStore1, libraryStore1);

        var profile1 = profileStore1.GetOrCreate(exePath);
        profile1.Api = GraphicsApi.DX11;
        profile1.Architecture = "x64";
        profile1.HudEnabled = true;
        profileStore1.Save(profile1);

        var installOk = await manager1.RequestEnableByPathAsync(profile1);
        Assert.Equal(DxvkActionResult.Applied, installOk);

        // Step 2: Reload and update to B (v2.6)
        var cacheStore2 = new CacheStore(cachePath);
        cacheStore2.SaveCachedRelease(new ReleaseInfo { Version = "2.6", DownloadUrl = "" }, "etag-2.6");

        var profileStore2 = new ProfileStore(profilesPath);
        var libraryStore2 = new GameLibraryStore(libraryPath, backupsPath, profilesPath);
        var engine2 = new MultiFileTransactionEngine(backupsPath);
        var installer2 = new DxvkInstaller(httpClient, engine2, libraryStore2, sourceDir.RootPath);
        var rollback2 = new DxvkRollback(engine2, libraryStore2);
        var github2 = new DxvkGithubClient(httpClient, cacheStore2);
        var manager2 = new DxvkManager(installer2, rollback2, github2, profileStore2, libraryStore2);

        var profile2 = profileStore2.GetOrCreate(exePath);
        var updateOk = await manager2.RequestUpdateByPathAsync(profile2);
        Assert.Equal(DxvkActionResult.Applied, updateOk);

        // Step 3: Reload again
        var profileStore3 = new ProfileStore(profilesPath);
        var libraryStore3 = new GameLibraryStore(libraryPath, backupsPath, profilesPath);
        var engine3 = new MultiFileTransactionEngine(backupsPath);
        var installer3 = new DxvkInstaller(httpClient, engine3, libraryStore3, sourceDir.RootPath);
        var rollback3 = new DxvkRollback(engine3, libraryStore3);
        var github3 = new DxvkGithubClient(httpClient, new CacheStore(cachePath));
        var manager3 = new DxvkManager(installer3, rollback3, github3, profileStore3, libraryStore3);

        var profile3 = profileStore3.GetOrCreate(exePath);
        var inst3 = libraryStore3.FindByInstallationPath(gameDir.RootPath)!;
        Assert.NotNull(inst3);
        Assert.Equal("2.6", inst3.ManagedDxvkVersion);

        // Step 4: Tamper with one managed target (d3d11.dll) with foreign bytes before restore
        string foreignD3D11Content = "tampered-foreign-d3d11-bytes-outside-companion-control";
        File.WriteAllText(d3d11Path, foreignD3D11Content);

        string dxgiBeforeRestore = File.ReadAllText(dxgiPath);
        string confBeforeRestore = File.ReadAllText(confPath);
        var d3d11Record3 = inst3.FindManagedFile("d3d11.dll")!;
        var d3d11BackupPath = Path.Combine(backupsPath, d3d11Record3.BackupRelativePath!);
        string backupBeforeRestore = File.ReadAllText(d3d11BackupPath);

        // Step 5: Act - attempt restore
        var restoreResult = await manager3.RequestDisableByPathAsync(profile3);

        // Assert: manager must refuse restore
        Assert.Equal(DxvkActionResult.Failed, restoreResult);
        Assert.NotNull(manager3.LastRefusalReason);
        Assert.Contains("Managed target hash mismatch for restore", manager3.LastRefusalReason);

        // Assert: no target mutation (every target, config, and backup byte preserved)
        Assert.Equal(foreignD3D11Content, File.ReadAllText(d3d11Path));
        Assert.Equal(dxgiBeforeRestore, File.ReadAllText(dxgiPath));
        Assert.Equal(confBeforeRestore, File.ReadAllText(confPath));
        Assert.Equal(backupBeforeRestore, File.ReadAllText(d3d11BackupPath));

        // Assert: active management and failure evidence retained
        Assert.True(profile3.DxvkEnabled);
        Assert.Equal("2.6", profile3.DxvkVersion);
        Assert.Equal("2.6", inst3.ManagedDxvkVersion);
        Assert.Equal(RestorationState.AttentionRequired, inst3.RestorationState);
        Assert.Equal(manager3.LastRefusalReason, inst3.LastRefusalReason);

        // Assert: failure state is persisted across fresh reload from disk
        var freshLibraryStore = new GameLibraryStore(libraryPath, backupsPath, profilesPath);
        var reloadedInst = freshLibraryStore.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.AttentionRequired, reloadedInst.RestorationState);
        Assert.Equal("2.6", reloadedInst.ManagedDxvkVersion);
        Assert.Equal(inst3.LastRefusalReason, reloadedInst.LastRefusalReason);

        var freshProfileStore = new ProfileStore(profilesPath);
        var reloadedProfile = freshProfileStore.GetOrCreate(exePath);
        Assert.True(reloadedProfile.DxvkEnabled);
        Assert.Equal("2.6", reloadedProfile.DxvkVersion);

        // Assert zero external network requests
        Assert.Equal(0, httpHandler.RequestCount);
    }
}
