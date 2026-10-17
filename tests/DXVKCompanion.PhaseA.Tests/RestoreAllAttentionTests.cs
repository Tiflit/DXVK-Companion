using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class RestoreAllAttentionTests
{
    [Fact]
    public async Task RestoreAll_DisabledProfile_AttentionRequiredWithActiveVersion_RestoresSuccessfully()
    {
        // Acceptance 1 & 2:
        // A disabled profile with AttentionRequired plus an active managed version is counted in TotalManaged
        // and restores successfully through existing validation with valid hashes and baselines.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11 = "original-native-d3d11";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalD3D11);
        var dxgiPath = Path.Combine(gameDir.RootPath, "dxgi.dll");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var profilesPath = Path.Combine(storageDir.RootPath, "profiles.json");

        var store = new GameLibraryStore(storePath, backupsPath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);
        var profileStore = new ProfileStore(profilesPath);

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profileStore.Save(profile);

        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
        Assert.True(await installer.ApplyToGameAsync(profile, release));

        // Mark installation AttentionRequired while retaining active version "2.5"
        var inst = store.FindByInstallationPath(gameDir.RootPath)!;
        inst.RestorationState = RestorationState.AttentionRequired;
        store.Save(inst);

        // Profile is disabled (e.g. stale or reconstructed disabled profile)
        profile.DxvkEnabled = false;
        profileStore.Save(profile);

        var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(http, new CacheStore()), profileStore, store);

        // Act: Run RestoreAllAsync
        var summary = await manager.RestoreAllAsync();

        // Assert: Counted in TotalManaged and successfully restored, not AlreadyRestored
        Assert.Equal(1, summary.TotalManaged);
        Assert.Equal(1, summary.Restored);
        Assert.Equal(0, summary.AlreadyRestored);
        Assert.Equal(0, summary.FailedOrAttentionRequired);

        // Disk validation: native d3d11 restored, dxgi deleted
        Assert.Equal(originalD3D11, File.ReadAllText(d3d11Path));
        Assert.False(File.Exists(dxgiPath));

        // Library record validation: Restored, managed version cleared
        var reloadedInst = store.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.Restored, reloadedInst.RestorationState);
        Assert.Null(reloadedInst.ManagedDxvkVersion);
        Assert.False(profileStore.GetOrCreate(exePath).DxvkEnabled);
    }

    [Fact]
    public async Task RestoreAll_DisabledProfile_AttentionRequiredWithActiveVersion_ReplacedTarget_RefusesSafely()
    {
        // Acceptance 3:
        // A replaced managed target refuses safely; current bytes, backups, active management/version
        // and attention evidence remain intact.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11 = "original-native-d3d11";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalD3D11);

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var profilesPath = Path.Combine(storageDir.RootPath, "profiles.json");

        var store = new GameLibraryStore(storePath, backupsPath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);
        var profileStore = new ProfileStore(profilesPath);

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profileStore.Save(profile);

        Assert.True(await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.5", DownloadUrl = "" }));

        // Tamper d3d11.dll with external content and mark AttentionRequired
        var tamperedContent = "externally-replaced-d3d11";
        File.WriteAllText(d3d11Path, tamperedContent);

        var inst = store.FindByInstallationPath(gameDir.RootPath)!;
        inst.RestorationState = RestorationState.AttentionRequired;
        store.Save(inst);

        // Disabled profile flag
        profile.DxvkEnabled = false;
        profileStore.Save(profile);

        var d3d11Record = inst.FindManagedFile("d3d11.dll")!;
        var backupPath = Path.Combine(backupsPath, d3d11Record.BackupRelativePath!);
        Assert.True(File.Exists(backupPath));

        var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(http, new CacheStore()), profileStore, store);

        // Act
        var summary = await manager.RestoreAllAsync();

        // Assert: Counted in TotalManaged, refuses safely as FailedOrAttentionRequired
        Assert.Equal(1, summary.TotalManaged);
        Assert.Equal(0, summary.Restored);
        Assert.Equal(0, summary.AlreadyRestored);
        Assert.Equal(1, summary.FailedOrAttentionRequired);

        // Files on disk and backups are NOT modified or overwritten
        Assert.Equal(tamperedContent, File.ReadAllText(d3d11Path));
        Assert.Equal(originalD3D11, File.ReadAllText(backupPath));

        // State remains AttentionRequired and version remains intact
        var reloadedInst = store.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.AttentionRequired, reloadedInst.RestorationState);
        Assert.Equal("2.5", reloadedInst.ManagedDxvkVersion);
        Assert.NotNull(reloadedInst.LastRefusalReason);
        Assert.Contains("Managed target hash mismatch for restore: d3d11.dll", reloadedInst.LastRefusalReason);
    }

    [Fact]
    public async Task RestoreAll_DisabledProfile_AttentionRequiredWithActiveVersion_MissingTarget_RefusesSafely()
    {
        // Acceptance 3:
        // A missing managed target refuses safely; active management and attention evidence remain intact.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "original-d3d11");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var profilesPath = Path.Combine(storageDir.RootPath, "profiles.json");

        var store = new GameLibraryStore(storePath, backupsPath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);
        var profileStore = new ProfileStore(profilesPath);

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profileStore.Save(profile);

        Assert.True(await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.5", DownloadUrl = "" }));

        // Delete d3d11.dll and set AttentionRequired
        File.Delete(d3d11Path);

        var inst = store.FindByInstallationPath(gameDir.RootPath)!;
        inst.RestorationState = RestorationState.AttentionRequired;
        store.Save(inst);

        profile.DxvkEnabled = false;
        profileStore.Save(profile);

        var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(http, new CacheStore()), profileStore, store);

        // Act
        var summary = await manager.RestoreAllAsync();

        // Assert
        Assert.Equal(1, summary.TotalManaged);
        Assert.Equal(0, summary.Restored);
        Assert.Equal(0, summary.AlreadyRestored);
        Assert.Equal(1, summary.FailedOrAttentionRequired);

        var reloadedInst = store.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.AttentionRequired, reloadedInst.RestorationState);
        Assert.Equal("2.5", reloadedInst.ManagedDxvkVersion);
        Assert.NotNull(reloadedInst.LastRefusalReason);
        Assert.Contains("Managed target file is missing for restore: d3d11.dll", reloadedInst.LastRefusalReason);
    }

    [Fact]
    public async Task RestoreAll_DisabledProfile_AttentionRequiredWithActiveVersion_MissingOwnershipEvidence_RefusesWithoutMutation()
    {
        // Acceptance 3:
        // Missing ownership evidence refuses without file mutation (does NOT fall back to legacy .bak restoration).
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "current-d3d11-content");
        var bakPath = gameDir.CreateFile("d3d11.dll.bak", "legacy-bak-content");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var profilesPath = Path.Combine(storageDir.RootPath, "profiles.json");

        var store = new GameLibraryStore(storePath, backupsPath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        using var http = new HttpClient();
        var rollback = new DxvkRollback(engine, store);
        var profileStore = new ProfileStore(profilesPath);

        var profile = profileStore.GetOrCreate(exePath);
        profile.DxvkEnabled = false;
        profileStore.Save(profile);

        // Actively managed installation marker "2.5" with AttentionRequired, but ManagedFiles is empty (missing evidence)
        var inst = store.GetOrCreateInstallation(gameDir.RootPath, "Game");
        inst.RestorationState = RestorationState.AttentionRequired;
        inst.ManagedDxvkVersion = "2.5";
        inst.ManagedFiles.Clear();
        store.Save(inst);

        var manager = new DxvkManager(null!, rollback, new DxvkGithubClient(http, new CacheStore()), profileStore, store);

        // Act
        var summary = await manager.RestoreAllAsync();

        // Assert: counted in TotalManaged, refuses safely
        Assert.Equal(1, summary.TotalManaged);
        Assert.Equal(0, summary.Restored);
        Assert.Equal(0, summary.AlreadyRestored);
        Assert.Equal(1, summary.FailedOrAttentionRequired);

        // Crucial safety invariant: No file mutation occurred (legacy .bak was NOT restored, current file was NOT overwritten)
        Assert.True(File.Exists(bakPath));
        Assert.Equal("legacy-bak-content", File.ReadAllText(bakPath));
        Assert.Equal("current-d3d11-content", File.ReadAllText(d3d11Path));

        // State remains AttentionRequired and active version remains intact
        var reloadedInst = store.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.AttentionRequired, reloadedInst.RestorationState);
        Assert.Equal("2.5", reloadedInst.ManagedDxvkVersion);
        Assert.NotNull(reloadedInst.LastRefusalReason);
        Assert.Contains("missing ownership evidence for actively managed installation", reloadedInst.LastRefusalReason);
    }

    [Fact]
    public async Task RestoreAll_PersistedAndReloaded_DisabledProfileWithActiveAttention_RestoresSuccessfully()
    {
        // Acceptance 4:
        // Verify a persisted/reloaded disabled-profile and active-attention installation combination.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11 = "original-native-d3d11";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalD3D11);
        var dxgiPath = Path.Combine(gameDir.RootPath, "dxgi.dll");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var profilesPath = Path.Combine(storageDir.RootPath, "profiles.json");

        var store = new GameLibraryStore(storePath, backupsPath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var profileStore = new ProfileStore(profilesPath);

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profileStore.Save(profile);

        Assert.True(await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.5", DownloadUrl = "" }));

        // Mark AttentionRequired and disable profile, then persist
        var inst = store.FindByInstallationPath(gameDir.RootPath)!;
        inst.RestorationState = RestorationState.AttentionRequired;
        store.Save(inst);

        profile.DxvkEnabled = false;
        profileStore.Save(profile);

        // Reload fresh stores from disk files
        var freshStore = new GameLibraryStore(storePath, backupsPath);
        var freshProfileStore = new ProfileStore(profilesPath);
        var freshRollback = new DxvkRollback(engine, freshStore);
        var freshManager = new DxvkManager(installer, freshRollback, new DxvkGithubClient(http, new CacheStore()), freshProfileStore, freshStore);

        // Verify reloaded state before RestoreAll
        var reloadedInstBefore = freshStore.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.AttentionRequired, reloadedInstBefore.RestorationState);
        Assert.Equal("2.5", reloadedInstBefore.ManagedDxvkVersion);
        Assert.False(freshProfileStore.GetOrCreate(exePath).DxvkEnabled);

        // Act: RestoreAll
        var summary = await freshManager.RestoreAllAsync();

        // Assert
        Assert.Equal(1, summary.TotalManaged);
        Assert.Equal(1, summary.Restored);
        Assert.Equal(0, summary.AlreadyRestored);
        Assert.Equal(0, summary.FailedOrAttentionRequired);

        Assert.Equal(originalD3D11, File.ReadAllText(d3d11Path));
        Assert.False(File.Exists(dxgiPath));

        // Reload stores again to confirm persistent outcome
        var storeAfter = new GameLibraryStore(storePath, backupsPath);
        var instAfter = storeAfter.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.Restored, instAfter.RestorationState);
        Assert.Null(instAfter.ManagedDxvkVersion);
    }

    [Fact]
    public async Task RestoreAll_DisabledProfile_RestoredOrNativeWithoutActiveVersion_CountedAsAlreadyRestored()
    {
        // Acceptance 5:
        // Native and healthy restored installations without active management remain already restored.
        using var testDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var profilesPath = Path.Combine(storageDir.RootPath, "profiles.json");

        var store = new GameLibraryStore(storePath, backupsPath);
        var profileStore = new ProfileStore(profilesPath);

        // Game 1: Native game (RestorationState.None, DxvkEnabled = false)
        string g1Dir = Path.Combine(testDir.RootPath, "NativeGame");
        Directory.CreateDirectory(g1Dir);
        string g1Exe = Path.Combine(g1Dir, "Game1.exe");
        File.WriteAllText(g1Exe, "exe");
        var p1 = profileStore.GetOrCreate(g1Exe);
        p1.DxvkEnabled = false;
        profileStore.Save(p1);
        var inst1 = store.GetOrCreateInstallation(g1Dir, "NativeGame");
        inst1.RestorationState = RestorationState.None;
        store.Save(inst1);

        // Game 2: Restored game (RestorationState.Restored, ManagedDxvkVersion = null, DxvkEnabled = false)
        string g2Dir = Path.Combine(testDir.RootPath, "RestoredGame");
        Directory.CreateDirectory(g2Dir);
        string g2Exe = Path.Combine(g2Dir, "Game2.exe");
        File.WriteAllText(g2Exe, "exe");
        var p2 = profileStore.GetOrCreate(g2Exe);
        p2.DxvkEnabled = false;
        profileStore.Save(p2);
        var inst2 = store.GetOrCreateInstallation(g2Dir, "RestoredGame");
        inst2.RestorationState = RestorationState.Restored;
        inst2.ManagedDxvkVersion = null;
        store.Save(inst2);

        // Game 3: Restored game with Attention but no active version (RestorationState.AttentionRequired, ManagedDxvkVersion = null)
        string g3Dir = Path.Combine(testDir.RootPath, "RestoredAttentionGame");
        Directory.CreateDirectory(g3Dir);
        string g3Exe = Path.Combine(g3Dir, "Game3.exe");
        File.WriteAllText(g3Exe, "exe");
        var p3 = profileStore.GetOrCreate(g3Exe);
        p3.DxvkEnabled = false;
        profileStore.Save(p3);
        var inst3 = store.GetOrCreateInstallation(g3Dir, "RestoredAttentionGame");
        inst3.RestorationState = RestorationState.AttentionRequired;
        inst3.ManagedDxvkVersion = null;
        store.Save(inst3);

        using var http = new HttpClient();
        var rollback = new DxvkRollback(new MultiFileTransactionEngine(backupsPath), store);
        var manager = new DxvkManager(null!, rollback, new DxvkGithubClient(http, new CacheStore()), profileStore, store);

        // Act
        var summary = await manager.RestoreAllAsync();

        // Assert: all 3 are counted in AlreadyRestored, none in TotalManaged
        Assert.Equal(0, summary.TotalManaged);
        Assert.Equal(0, summary.Restored);
        Assert.Equal(3, summary.AlreadyRestored);
        Assert.Equal(0, summary.FailedOrAttentionRequired);
    }

    [Fact]
    public async Task RestoreAll_MultipleGames_IsolationBetweenActiveAttentionAndOtherStates()
    {
        // Acceptance 5: Per-entry error isolation across multiple games.
        using var testDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-dxgi");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var profilesPath = Path.Combine(storageDir.RootPath, "profiles.json");

        var store = new GameLibraryStore(storePath, backupsPath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);
        var profileStore = new ProfileStore(profilesPath);

        // Game 1: Disabled profile + AttentionRequired + active version -> valid, will restore successfully
        string g1Dir = Path.Combine(testDir.RootPath, "Game1");
        Directory.CreateDirectory(g1Dir);
        string g1Exe = Path.Combine(g1Dir, "Game1.exe");
        File.WriteAllText(g1Exe, "exe");
        File.WriteAllText(Path.Combine(g1Dir, "d3d11.dll"), "orig-d3d11");
        var p1 = profileStore.GetOrCreate(g1Exe);
        p1.Api = GraphicsApi.DX11;
        p1.Architecture = "x64";
        profileStore.Save(p1);
        Assert.True(await installer.ApplyToGameAsync(p1, new ReleaseInfo { Version = "2.5", DownloadUrl = "" }));
        var inst1 = store.FindByInstallationPath(g1Dir)!;
        inst1.RestorationState = RestorationState.AttentionRequired;
        store.Save(inst1);
        p1.DxvkEnabled = false;
        profileStore.Save(p1);

        // Game 2: Disabled profile + AttentionRequired + active version -> replaced target, will refuse safely
        string g2Dir = Path.Combine(testDir.RootPath, "Game2");
        Directory.CreateDirectory(g2Dir);
        string g2Exe = Path.Combine(g2Dir, "Game2.exe");
        File.WriteAllText(g2Exe, "exe");
        File.WriteAllText(Path.Combine(g2Dir, "d3d11.dll"), "orig-d3d11");
        var p2 = profileStore.GetOrCreate(g2Exe);
        p2.Api = GraphicsApi.DX11;
        p2.Architecture = "x64";
        profileStore.Save(p2);
        Assert.True(await installer.ApplyToGameAsync(p2, new ReleaseInfo { Version = "2.5", DownloadUrl = "" }));
        File.WriteAllText(Path.Combine(g2Dir, "d3d11.dll"), "tampered-target");
        var inst2 = store.FindByInstallationPath(g2Dir)!;
        inst2.RestorationState = RestorationState.AttentionRequired;
        store.Save(inst2);
        p2.DxvkEnabled = false;
        profileStore.Save(p2);

        // Game 3: Disabled profile + Restored state -> already restored
        string g3Dir = Path.Combine(testDir.RootPath, "Game3");
        Directory.CreateDirectory(g3Dir);
        string g3Exe = Path.Combine(g3Dir, "Game3.exe");
        File.WriteAllText(g3Exe, "exe");
        var p3 = profileStore.GetOrCreate(g3Exe);
        p3.DxvkEnabled = false;
        profileStore.Save(p3);
        var inst3 = store.GetOrCreateInstallation(g3Dir, "Game3");
        inst3.RestorationState = RestorationState.Restored;
        inst3.ManagedDxvkVersion = null;
        store.Save(inst3);

        var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(http, new CacheStore()), profileStore, store);

        // Act
        var summary = await manager.RestoreAllAsync();

        // Assert
        Assert.Equal(2, summary.TotalManaged);
        Assert.Equal(1, summary.Restored);
        Assert.Equal(1, summary.AlreadyRestored);
        Assert.Equal(1, summary.FailedOrAttentionRequired);

        // Game 1 restored
        Assert.Equal("orig-d3d11", File.ReadAllText(Path.Combine(g1Dir, "d3d11.dll")));
        Assert.Equal(RestorationState.Restored, store.FindByInstallationPath(g1Dir)!.RestorationState);

        // Game 2 refused, preserved tampered content and AttentionRequired
        Assert.Equal("tampered-target", File.ReadAllText(Path.Combine(g2Dir, "d3d11.dll")));
        Assert.Equal(RestorationState.AttentionRequired, store.FindByInstallationPath(g2Dir)!.RestorationState);
        Assert.Equal("2.5", store.FindByInstallationPath(g2Dir)!.ManagedDxvkVersion);
    }

    [Fact]
    public async Task RestoreAll_SequentialFailures_SecondReasonlessFailure_DoesNotInheritFirstRefusalReason()
    {
        // Codex review finding:
        // A reasonless failure (e.g. non-existent directory) following a refusal must not inherit
        // the previous game's refusal reason. LastRefusalReason must be cleared per attempt.
        using var testDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var profilesPath = Path.Combine(storageDir.RootPath, "profiles.json");

        var store = new GameLibraryStore(storePath, backupsPath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);
        var profileStore = new ProfileStore(profilesPath);

        // Game A: Enabled profile + Managed installation with empty ManagedFiles -> Refusal: "missing ownership evidence"
        string gADir = Path.Combine(testDir.RootPath, "GameA");
        Directory.CreateDirectory(gADir);
        string gAExe = Path.Combine(gADir, "GameA.exe");
        File.WriteAllText(gAExe, "exe");
        var pA = profileStore.GetOrCreate(gAExe);
        pA.DxvkEnabled = true;
        profileStore.Save(pA);
        var instA = store.GetOrCreateInstallation(gADir, "GameA");
        instA.RestorationState = RestorationState.Managed;
        instA.ManagedDxvkVersion = "2.5";
        instA.ManagedFiles = new List<ManagedFileRecord>();
        store.Save(instA);

        // Game B: Enabled profile pointing to a missing directory -> reasonless rollback failure
        string gBDir = Path.Combine(testDir.RootPath, "NonExistentGameB");
        string gBExe = Path.Combine(gBDir, "GameB.exe");
        var pB = profileStore.GetOrCreate(gBExe);
        pB.DxvkEnabled = true;
        profileStore.Save(pB);

        var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(http, new CacheStore()), profileStore, store);

        // Act: Run RestoreAllAsync which sequentially disables Game A then Game B
        var summary = await manager.RestoreAllAsync();

        // Assert: Both failed, but Game B's failure message does not inherit Game A's refusal reason
        Assert.Equal(2, summary.TotalManaged);
        Assert.Equal(0, summary.Restored);
        Assert.Equal(2, summary.FailedOrAttentionRequired);
        Assert.Equal(2, summary.Messages.Count);

        var msgA = Assert.Single(summary.Messages, m => m.StartsWith("GameA.exe:"));
        var msgB = Assert.Single(summary.Messages, m => m.StartsWith("GameB.exe:"));

        Assert.Contains("missing ownership evidence", msgA);
        Assert.Equal("GameB.exe: Restore operation failed.", msgB);
        Assert.DoesNotContain("missing ownership evidence", msgB);

        // Manager's LastRefusalReason is cleared to null following Game B's reasonless failure
        Assert.Null(manager.LastRefusalReason);

        // Also verify direct sequential call to DisableDxvkAsync:
        // First disable Game A (sets refusal reason)
        bool okA = await manager.DisableDxvkAsync(pA);
        Assert.False(okA);
        Assert.NotNull(manager.LastRefusalReason);
        Assert.Contains("missing ownership evidence", manager.LastRefusalReason);

        // Next disable Game B (reasonless failure must clear LastRefusalReason)
        bool okB = await manager.DisableDxvkAsync(pB);
        Assert.False(okB);
        Assert.Null(manager.LastRefusalReason);
    }
}
