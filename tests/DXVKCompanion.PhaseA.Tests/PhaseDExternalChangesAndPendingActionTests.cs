using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class PhaseDExternalChangesAndPendingActionTests
{
    [Fact]
    public async Task ManagedFileInspector_DetectsMissingFile_MarksExternallyChangedAndAttentionRequired()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var ok = await installer.ApplyToGameAsync(profile, release);
        Assert.True(ok);

        var d3d11Path = Path.Combine(gameDir.RootPath, "d3d11.dll");
        Assert.True(File.Exists(d3d11Path));

        // External action deletes d3d11.dll
        File.Delete(d3d11Path);

        var inspector = new ManagedFileInspector(store);
        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);

        bool changed = inspector.InspectInstallation(installation!);
        Assert.True(changed);

        var d3d11Record = installation!.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record);
        Assert.Equal(ManagedFileState.ExternallyChanged, d3d11Record!.CurrentState);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
    }

    [Fact]
    public async Task ManagedFileInspector_DetectsReplacedFile_MarksExternallyChangedAndAttentionRequired()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var ok = await installer.ApplyToGameAsync(profile, release);
        Assert.True(ok);

        var d3d11Path = Path.Combine(gameDir.RootPath, "d3d11.dll");

        // External game patch overwrites d3d11.dll with different content
        File.WriteAllText(d3d11Path, "game-patch-new-d3d11");

        var inspector = new ManagedFileInspector(store);
        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);

        bool changed = inspector.InspectInstallation(installation!);
        Assert.True(changed);

        var d3d11Record = installation!.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record);
        Assert.Equal(ManagedFileState.ExternallyChanged, d3d11Record!.CurrentState);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
    }

    [Fact]
    public async Task ManagedFileInspector_Section20_StalePendingAction_InvalidatedByExternalChange()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        await installer.ApplyToGameAsync(profile, release);

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);

        // A pending action was queued
        installation!.PendingAction = PendingAction.Update("3.0", "Scheduled upgrade to 3.0");
        store.Save(installation);
        Assert.NotNull(installation.PendingAction);

        // Then an external game update replaces d3d11.dll
        var d3d11Path = Path.Combine(gameDir.RootPath, "d3d11.dll");
        File.WriteAllText(d3d11Path, "game-update-changed-dll");

        var inspector = new ManagedFileInspector(store);
        bool changed = inspector.InspectInstallation(installation);
        Assert.True(changed);

        // Section 20 Rule: Stale pending action MUST be superseded/invalidated!
        Assert.Null(installation.PendingAction);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
    }

    [Fact]
    public async Task DxvkInstaller_ReapplyAsync_UpdateBaseline_ReplacesOriginalBaselineWithNewGameFile()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        // 1. Initial State: Game Version A
        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "native-version-A-d3d11");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        // Install DXVK
        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);
        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));

        // 2. Steam updates the game to Version B: d3d11.dll is overwritten by game update
        File.WriteAllText(d3d11Path, "native-version-B-d3d11");

        // 3. User authorizes Reapply with updateBaseline = true
        var reapplyOk = await installer.ReapplyAsync(profile, updateBaseline: true);
        Assert.True(reapplyOk);
        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        var record = installation!.FindManagedFile("d3d11.dll");
        Assert.NotNull(record);
        Assert.Equal(FileOriginalState.Existing, record!.OriginalState);

        // Verify that the backup now holds Version B, NOT Version A!
        var backupPath = Path.Combine(storageDir.RootPath, "backups", record.BackupRelativePath!);
        Assert.True(File.Exists(backupPath));
        Assert.Equal("native-version-B-d3d11", File.ReadAllText(backupPath));

        // 4. Rollback should restore Version B!
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);
        Assert.Equal("native-version-B-d3d11", File.ReadAllText(d3d11Path));
    }

    [Fact]
    public void DxvkManager_PersistentPendingAction_SavedToLibraryJson_SurvivesRestart()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        var libraryPath = Path.Combine(storageDir.RootPath, "game-library.json");

        var store = new GameLibraryStore(libraryPath, Path.Combine(storageDir.RootPath, "backups"));
        var installation = store.GetOrCreateInstallation(gameDir.RootPath, "Game");
        installation.PendingAction = PendingAction.Install("2.5", "Queued offline");
        store.Save(installation);

        // Simulate application restart: load a fresh GameLibraryStore instance from the same JSON
        var freshStore = new GameLibraryStore(libraryPath, Path.Combine(storageDir.RootPath, "backups"));
        var loaded = freshStore.FindByInstallationPath(gameDir.RootPath);

        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.PendingAction);
        Assert.Equal(PendingActionType.Install, loaded.PendingAction!.Type);
        Assert.Equal("2.5", loaded.PendingAction.TargetDxvkVersion);
        Assert.True(loaded.PendingAction.IsPending);
    }

    [Fact]
    public async Task DxvkManager_ProcessAllPendingActionsAsync_ExecutesPendingActionWhenGameExits()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "games.json"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);
        var github = new DxvkGithubClient(http, new CacheStore(Path.Combine(storageDir.RootPath, "cache.json")));

        var manager = new DxvkManager(installer, rollback, github, profileStore, store);

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profileStore.Save(profile);

        var installation = store.GetOrCreateInstallation(gameDir.RootPath, "Game");
        installation.GetOrAddExecutable("Game.exe", "Game");
        installation.PendingAction = PendingAction.Install("2.5", "Queued while game running");
        store.Save(installation);

        // ProcessAllPendingActionsAsync runs (game process is not running)
        int executed = await manager.ProcessAllPendingActionsAsync();
        Assert.Equal(1, executed);

        // Invariant: PendingAction is completed and cleared
        var updated = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(updated);
        Assert.Null(updated!.PendingAction);

        // Invariant: Files deployed
        Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
        Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
    }
}
