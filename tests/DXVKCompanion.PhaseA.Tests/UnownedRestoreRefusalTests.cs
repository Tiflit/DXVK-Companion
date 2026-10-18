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

public sealed class UnownedRestoreRefusalTests
{
    private static Dictionary<string, SafetyFileIdentity> SnapshotDirectory(string directory)
    {
        var snapshot = new Dictionary<string, SafetyFileIdentity>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return snapshot;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(directory, file);
            snapshot[rel] = FileIdentity.Capture(file);
        }
        return snapshot;
    }

    private static void AssertDirectoryUnchanged(string directory, Dictionary<string, SafetyFileIdentity> baseline)
    {
        var current = SnapshotDirectory(directory);
        Assert.Equal(baseline.Count, current.Count);
        foreach (var (rel, ident) in baseline)
        {
            Assert.True(current.TryGetValue(rel, out var curIdent), $"Missing file: {rel}");
            Assert.Equal(ident, curIdent);
        }
    }

    [Fact]
    public async Task RestoreOriginalDllsAsync_WhenNoInstallationRecord_AndUnownedDxvkConfPresent_RefusesWithoutMutatingFilesOrCreatingOwnership()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var confPath = gameDir.CreateFile("dxvk.conf", "dxvk.hud = fps\ndxvk.frameRate = 120");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            DxvkEnabled = true
        };

        var baseline = SnapshotDirectory(gameDir.RootPath);

        // Act
        var ok = await rollback.RestoreOriginalDllsAsync(profile);

        // Assert: rollback refused with false and useful reason; files and library records untouched
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(rollback.LastRefusalReason));
        Assert.Contains("missing ownership", rollback.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        AssertDirectoryUnchanged(gameDir.RootPath, baseline);
        Assert.True(File.Exists(confPath));
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public async Task RestoreOriginalDllsAsync_WhenNoInstallationRecord_AndLegacyBakFilesPresent_RefusesWithoutMovingOrDeleting()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        gameDir.CreateFile("d3d9.dll", "active-d3d9-wrapper");
        gameDir.CreateFile("d3d9.dll.bak", "original-native-d3d9");
        gameDir.CreateFile("d3d11.dll", "active-d3d11-wrapper");
        gameDir.CreateFile("d3d11.dll.bak", "original-native-d3d11");
        gameDir.CreateFile("dxgi.dll", "active-dxgi-wrapper");
        gameDir.CreateFile("dxgi.dll.bak", "original-native-dxgi");
        gameDir.CreateFile("dxvk.conf", "dxvk.hud = fps");
        gameDir.CreateFile("unrelated.txt", "unrelated-game-data");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            DxvkEnabled = true
        };

        var baseline = SnapshotDirectory(gameDir.RootPath);

        // Act
        var ok = await rollback.RestoreOriginalDllsAsync(profile);

        // Assert: must refuse rollback without moving .bak or deleting files
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(rollback.LastRefusalReason));
        Assert.Contains("missing ownership", rollback.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        AssertDirectoryUnchanged(gameDir.RootPath, baseline);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public async Task RestoreOriginalDllsAsync_WhenTargetAndBakFilesLocked_RefusesSafelyBeforeAttemptingMutation()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "active-d3d11");
        var bakPath = gameDir.CreateFile("d3d11.dll.bak", "native-d3d11-bak");
        var confPath = gameDir.CreateFile("dxvk.conf", "dxvk.hud = fps");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            DxvkEnabled = true
        };

        var baseline = SnapshotDirectory(gameDir.RootPath);

        // Lock target DLL, bak file, and conf file exclusively to prove refusal occurs before mutation attempts
        FileStream? lockDll = null;
        FileStream? lockBak = null;
        FileStream? lockConf = null;

        try
        {
            lockDll = new FileStream(d3d11Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            lockBak = new FileStream(bakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            lockConf = new FileStream(confPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            Assert.True(lockDll.CanRead);
            Assert.True(lockBak.CanRead);
            Assert.True(lockConf.CanRead);

            // Act: Rollback must refuse at decision boundary before touching files
            var ok = await rollback.RestoreOriginalDllsAsync(profile);

            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(rollback.LastRefusalReason));
            Assert.Contains("missing ownership", rollback.LastRefusalReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            lockDll?.Dispose();
            lockBak?.Dispose();
            lockConf?.Dispose();
        }

        // Post-lock check: directory contents and hashes remain completely unchanged
        AssertDirectoryUnchanged(gameDir.RootPath, baseline);
    }

    [Theory]
    [InlineData(RestorationState.None)]
    [InlineData(RestorationState.Managed)]
    [InlineData(RestorationState.Restored)]
    public async Task RestoreOriginalDllsAsync_WhenInstallationExistsWithEmptyManagedFiles_RefusesAndTransitionsToAttentionRequired(
        RestorationState initialState)
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        gameDir.CreateFile("d3d11.dll", "d3d11-content");
        gameDir.CreateFile("d3d11.dll.bak", "d3d11-bak-content");
        gameDir.CreateFile("dxvk.conf", "dxvk.hud = fps");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var store = new GameLibraryStore(storePath, backupsPath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        var rollback = new DxvkRollback(engine, store);

        var inst = store.GetOrCreateInstallation(gameDir.RootPath, "Game");
        inst.RestorationState = initialState;
        inst.ManagedDxvkVersion = initialState == RestorationState.Managed ? "2.5" : null;
        inst.ManagedFiles = new List<ManagedFileRecord>(); // Empty ownership evidence
        store.Save(inst);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            DxvkEnabled = true
        };

        var baseline = SnapshotDirectory(gameDir.RootPath);

        // Act
        var ok = await rollback.RestoreOriginalDllsAsync(profile);

        // Assert
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(rollback.LastRefusalReason));
        Assert.Contains("missing ownership", rollback.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        AssertDirectoryUnchanged(gameDir.RootPath, baseline);

        // Installation in store must be transitioned to AttentionRequired with refusal reason
        var reloadedInst = store.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.AttentionRequired, reloadedInst.RestorationState);
        Assert.NotNull(reloadedInst.LastRefusalReason);
        Assert.Contains("missing ownership", reloadedInst.LastRefusalReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreOriginalDllsAsync_WhenInstallationHasEmptyManagedFiles_AndStoreHasUncertainPersistence_RefusesWithoutSaving()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        gameDir.CreateFile("d3d11.dll", "d3d11-content");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var store = new GameLibraryStore(storePath, backupsPath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        var rollback = new DxvkRollback(engine, store);

        var inst = store.GetOrCreateInstallation(gameDir.RootPath, "Game");
        inst.RestorationState = RestorationState.Managed;
        inst.ManagedDxvkVersion = "2.5";
        inst.ManagedFiles = new List<ManagedFileRecord>();
        store.Save(inst);

        // Simulate uncertain persistence on store
        store.RecordUnexpectedCommitCallbackException("Simulated uncertain persistence");
        Assert.True(store.HasUncertainPersistence);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            DxvkEnabled = true
        };

        var baseline = SnapshotDirectory(gameDir.RootPath);

        // Act
        var ok = await rollback.RestoreOriginalDllsAsync(profile);

        // Assert
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(rollback.LastRefusalReason));

        // Files untouched
        AssertDirectoryUnchanged(gameDir.RootPath, baseline);

        // Since persistence was uncertain, store file was not modified
        var freshStore = new GameLibraryStore(storePath, backupsPath);
        var persistedInst = freshStore.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.Managed, persistedInst.RestorationState);
    }

    [Fact]
    public async Task DisableDxvkAsync_WhenUnownedProfile_RefusesAndLeavesProfileEnabledWithoutMutatingFiles()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var confPath = gameDir.CreateFile("dxvk.conf", "dxvk.hud = fps");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "synthetic-d3d11");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        var rollback = new DxvkRollback(engine, store);
        var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "profiles.json"));

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profile.DxvkEnabled = true;
        profile.DxvkVersion = "2.5";
        profileStore.Save(profile);

        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(http, new CacheStore()), profileStore, store);

        var baseline = SnapshotDirectory(gameDir.RootPath);

        // Act
        var ok = await manager.DisableDxvkAsync(profile);

        // Assert: must return false, propagate refusal reason, and leave profile enabled
        Assert.False(ok);
        Assert.NotNull(manager.LastRefusalReason);
        Assert.Contains("missing ownership", manager.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Profile remains enabled and unchanged
        var reloadedProfile = profileStore.GetOrCreate(exePath);
        Assert.True(reloadedProfile.DxvkEnabled);
        Assert.Equal("2.5", reloadedProfile.DxvkVersion);

        // Files untouched
        AssertDirectoryUnchanged(gameDir.RootPath, baseline);
        Assert.True(File.Exists(confPath));
        Assert.True(File.Exists(d3d11Path));
    }

    [Fact]
    public async Task RestoreAllAsync_WhenBatchContainsUnownedProfileAndValidManagedProfile_IsolatesFailureAndRestoresValidGame()
    {
        using var testDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        var rollback = new DxvkRollback(engine, store);
        var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "profiles.json"));

        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(http, new CacheStore()), profileStore, store);

        // Game 1: Unowned profile (no installation in store, but profile is DxvkEnabled)
        string g1Dir = Path.Combine(testDir.RootPath, "Game1Unowned");
        Directory.CreateDirectory(g1Dir);
        string g1Exe = Path.Combine(g1Dir, "Game1.exe");
        File.WriteAllText(g1Exe, "g1-exe");
        File.WriteAllText(Path.Combine(g1Dir, "d3d11.dll"), "g1-d3d11");
        File.WriteAllText(Path.Combine(g1Dir, "dxvk.conf"), "dxvk.hud = fps");
        var p1 = profileStore.GetOrCreate(g1Exe);
        p1.Api = GraphicsApi.DX11;
        p1.Architecture = "x64";
        p1.DxvkEnabled = true;
        profileStore.Save(p1);
        var g1Baseline = SnapshotDirectory(g1Dir);

        // Game 2: Validly managed game
        string g2Dir = Path.Combine(testDir.RootPath, "Game2Managed");
        Directory.CreateDirectory(g2Dir);
        string g2Exe = Path.Combine(g2Dir, "Game2.exe");
        File.WriteAllText(g2Exe, "g2-exe");
        File.WriteAllText(Path.Combine(g2Dir, "d3d11.dll"), "native-d3d11-content");
        var p2 = profileStore.GetOrCreate(g2Exe);
        p2.Api = GraphicsApi.DX11;
        p2.Architecture = "x64";
        profileStore.Save(p2);

        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
        Assert.True(await installer.ApplyToGameAsync(p2, release));
        p2.DxvkEnabled = true;
        p2.DxvkVersion = "2.5";
        profileStore.Save(p2);

        // Act: Run RestoreAllAsync across both games
        var summary = await manager.RestoreAllAsync();

        // Assert: TotalManaged = 2, Restored = 1 (Game 2), FailedOrAttentionRequired = 1 (Game 1)
        Assert.Equal(2, summary.TotalManaged);
        Assert.Equal(1, summary.Restored);
        Assert.Equal(1, summary.FailedOrAttentionRequired);

        // Game 1: Untouched and still enabled
        AssertDirectoryUnchanged(g1Dir, g1Baseline);
        Assert.True(profileStore.GetOrCreate(g1Exe).DxvkEnabled);

        // Game 2: Restored to native baseline and disabled
        Assert.Equal("native-d3d11-content", File.ReadAllText(Path.Combine(g2Dir, "d3d11.dll")));
        Assert.False(File.Exists(Path.Combine(g2Dir, "dxgi.dll")));
        Assert.False(profileStore.GetOrCreate(g2Exe).DxvkEnabled);
        var g2Inst = store.FindByInstallationPath(g2Dir)!;
        Assert.Equal(RestorationState.Restored, g2Inst.RestorationState);
        Assert.Null(g2Inst.ManagedDxvkVersion);
    }

    [Fact]
    public async Task RestoreOriginalDllsAsync_WhenLegitimatelyOwned_RestoresBaselineAndRemainsIdempotent()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "native-original-d3d11");
        var customSettingPath = gameDir.CreateFile("settings.ini", "[Settings]\nResolution=1080p");

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            HudEnabled = true
        };

        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
        Assert.True(await installer.ApplyToGameAsync(profile, release));

        // Deployed DXVK files are present
        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));
        Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxvk.conf")));
        Assert.True(File.Exists(customSettingPath));

        // Act 1: Initial rollback on legitimately owned installation
        var ok1 = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(ok1);

        // Native baseline restored, DXVK-added files removed, unrelated config retained
        Assert.Equal("native-original-d3d11", File.ReadAllText(d3d11Path));
        Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxvk.conf")));
        Assert.True(File.Exists(customSettingPath));
        Assert.Equal("[Settings]\nResolution=1080p", File.ReadAllText(customSettingPath));

        var restoredInst = store.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.Restored, restoredInst.RestorationState);
        Assert.Null(restoredInst.ManagedDxvkVersion);

        // Act 2: Idempotent second rollback call on already-restored installation
        var ok2 = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(ok2);
        Assert.Equal(RestorationState.Restored, restoredInst.RestorationState);
        Assert.Equal("native-original-d3d11", File.ReadAllText(d3d11Path));
        Assert.True(File.Exists(customSettingPath));
    }
}
