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

public sealed class PhaseEUIIntegrationTests
{
    [Fact]
    public void GameLibrary_StatusMapping_ProvidesAccurateHealthStates()
    {
        using var storageDir = new SyntheticTestDirectory();
        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));

        // 1. Conflict state
        var install1 = store.GetOrCreateInstallation(@"C:\Games\Game1", "Game1");
        install1.ConflictFlags = InstallationConflictFlags.DxvkVersion;
        store.Save(install1);
        Assert.Equal(InstallationConflictFlags.DxvkVersion, install1.ConflictFlags);

        // 2. Attention Required
        var install2 = store.GetOrCreateInstallation(@"C:\Games\Game2", "Game2");
        install2.RestorationState = RestorationState.AttentionRequired;
        store.Save(install2);
        Assert.Equal(RestorationState.AttentionRequired, install2.RestorationState);

        // 3. Managed state
        var install3 = store.GetOrCreateInstallation(@"C:\Games\Game3", "Game3");
        install3.ManagedDxvkVersion = "2.5";
        install3.RestorationState = RestorationState.Managed;
        store.Save(install3);
        Assert.Equal(RestorationState.Managed, install3.RestorationState);

        // 4. Pending Action
        var install4 = store.GetOrCreateInstallation(@"C:\Games\Game4", "Game4");
        install4.PendingAction = PendingAction.Reapply("2.5", "Game update detected");
        store.Save(install4);
        Assert.NotNull(install4.PendingAction);
        Assert.Equal(PendingActionType.Reapply, install4.PendingAction!.Type);
    }

    [Fact]
    public async Task DxvkManager_UIOperations_AdoptAndReapply_ExecuteSuccessfully()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        gameDir.CreateFile("d3d11.dll", "dxvk-2.5-d3d11");
        gameDir.CreateFile("dxgi.dll", "dxvk-2.5-dxgi");

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

        // 1. Assess and Adopt via Manager
        var assessment = manager.AssessExistingDxvk(profile);
        Assert.True(assessment.CanBeAdopted);
        Assert.Equal("2.5", assessment.MatchedVersion);

        bool adoptOk = await manager.AdoptExistingAsync(profile);
        Assert.True(adoptOk);
        Assert.True(profile.DxvkEnabled);
        Assert.Equal("2.5", profile.DxvkVersion);

        // 2. Reapply via Manager
        var reapplyRes = await manager.RequestReapplyByPathAsync(profile, updateBaseline: true);
        Assert.Equal(DxvkActionResult.Applied, reapplyRes);

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        Assert.Equal(RestorationState.Managed, installation!.RestorationState);
    }
}
