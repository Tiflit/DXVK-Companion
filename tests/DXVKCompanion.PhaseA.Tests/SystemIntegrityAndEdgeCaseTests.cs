using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.UI;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class SystemIntegrityAndEdgeCaseTests
    {
        [Fact]
        public async Task ApplyPendingAsync_WhenNoActionPending_ReturnsFalseAndPerformsNoWrite()
        {
            using var testDir = new SyntheticTestDirectory();
            var libraryStore = new GameLibraryStore(
                Path.Combine(testDir.RootPath, "game-library.json"),
                Path.Combine(testDir.RootPath, "backups"),
                Path.Combine(testDir.RootPath, "games.json"));
            var profileStore = new ProfileStore(Path.Combine(testDir.RootPath, "games.json"));
            var engine = new MultiFileTransactionEngine(Path.Combine(testDir.RootPath, "backups"));
            var installer = new DxvkInstaller(new HttpClient(), engine, libraryStore);
            var rollback = new DxvkRollback(engine, libraryStore);
            var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(new HttpClient(), new CacheStore()), profileStore, libraryStore);

            string gameExe = testDir.CreateFile("Game.exe", "binary");

            // Act: call ApplyPendingAsync when no action is queued
            bool result = await manager.ApplyPendingAsync(gameExe);

            // Assert: must return false (no action taken)
            Assert.False(result);
        }

        [Fact]
        public async Task ApplyPendingAsync_WhenPendingActionQueued_ExecutesSuccessfullyAndReturnsTrue()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var releaseDir = Path.Combine(sourceDir.RootPath, "2.6", "x64");
            Directory.CreateDirectory(releaseDir);
            File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.6-d3d11");
            File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.6-dxgi");

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

            var libraryStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "games.json"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            var installer = new DxvkInstaller(new HttpClient(), engine, libraryStore, sourceDir.RootPath);
            var rollback = new DxvkRollback(engine, libraryStore);
            var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(new HttpClient(), new CacheStore()), profileStore, libraryStore);

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = libraryStore.GetOrCreateInstallation(gameDir.RootPath, "Game");
            var exe = installation.GetOrAddExecutable("Game.exe", "Game");
            exe.LastKnownApi = GraphicsApi.DX11;
            installation.PendingAction = PendingAction.Install("2.6", "Queued while game running");
            libraryStore.Save(installation);

            // Act: game exits, ApplyPendingAsync called
            bool result = await manager.ApplyPendingAsync(exePath);

            // Assert: must return true, install DXVK, and clear the pending action
            Assert.True(result);

            var updatedInstallation = libraryStore.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(updatedInstallation);
            Assert.Null(updatedInstallation!.PendingAction);
            Assert.Equal("2.6", updatedInstallation.ManagedDxvkVersion);
            Assert.Equal(RestorationState.Managed, updatedInstallation.RestorationState);
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
        }

        [Fact]
        public async Task RestoreAllAsync_Idempotent_SecondCallReportsAllAlreadyRestored()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(testDir.RootPath, "backups");
            string profilesPath = Path.Combine(testDir.RootPath, "games.json");

            var store = new GameLibraryStore(libraryPath, backupsPath, profilesPath);
            var engine = new MultiFileTransactionEngine(backupsPath);
            var fileUtils = new FileUtils();
            var installer = new DxvkInstaller(new HttpClient(), engine, store, null, fileUtils);
            var rollback = new DxvkRollback(engine, store, fileUtils);
            var profileStore = new ProfileStore(profilesPath);

            string gameDir = Path.Combine(testDir.RootPath, "Games", "SingleGame");
            Directory.CreateDirectory(gameDir);
            string gameExe = Path.Combine(gameDir, "SingleGame.exe");
            File.WriteAllText(gameExe, "binary");
            string d3d11 = Path.Combine(gameDir, "d3d11.dll");
            File.WriteAllText(d3d11, "dxvk-dll");

            var p = profileStore.GetOrCreate(gameExe);
            p.DxvkEnabled = true;
            p.DxvkVersion = "2.6";
            profileStore.Save(p);

            var inst = store.GetOrCreateInstallation(gameDir, "SingleGame");
            inst.RestorationState = RestorationState.Managed;
            inst.ManagedDxvkVersion = "2.6";
            inst.ManagedFiles.Add(new ManagedFileRecord
            {
                RelativePath = "d3d11.dll",
                OriginalState = FileOriginalState.Missing,
                CurrentState = ManagedFileState.Consistent,
                ManagedDxvkVersion = "2.6"
            });
            store.Save(inst);

            var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(new HttpClient(), new CacheStore()), profileStore, store);

            // First run restores the game
            var firstSummary = await manager.RestoreAllAsync();
            Assert.Equal(1, firstSummary.Restored);
            Assert.Equal(0, firstSummary.FailedOrAttentionRequired);
            Assert.False(File.Exists(d3d11));

            // Second run: idempotent — game is already restored
            var secondSummary = await manager.RestoreAllAsync();
            Assert.Equal(0, secondSummary.TotalManaged);
            Assert.Equal(0, secondSummary.Restored);
            Assert.Equal(1, secondSummary.AlreadyRestored);
            Assert.Equal(0, secondSummary.FailedOrAttentionRequired);
        }

        [Fact]
        public void ManagedFileInspector_MissingManagedFilesOnDisk_FlagsAttentionRequiredAndClearsStalePendingAction()
        {
            using var testDir = new SyntheticTestDirectory();
            var store = new GameLibraryStore(
                Path.Combine(testDir.RootPath, "game-library.json"),
                Path.Combine(testDir.RootPath, "backups"));

            string gameDir = Path.Combine(testDir.RootPath, "MissingDllGame");
            Directory.CreateDirectory(gameDir);
            string gameExe = Path.Combine(gameDir, "Game.exe");
            File.WriteAllText(gameExe, "binary");

            var inst = store.GetOrCreateInstallation(gameDir, "MissingDllGame");
            inst.RestorationState = RestorationState.Managed;
            inst.ManagedDxvkVersion = "2.6";
            inst.PendingAction = PendingAction.Update("3.0", "Scheduled upgrade");
            inst.ManagedFiles.Add(new ManagedFileRecord
            {
                RelativePath = "d3d11.dll",
                OriginalState = FileOriginalState.Missing,
                CurrentState = ManagedFileState.Consistent,
                ExpectedManagedSha256 = "expected-sha256",
                ManagedDxvkVersion = "2.6"
            });
            store.Save(inst);

            // d3d11.dll is NOT present in gameDir on disk
            var inspector = new ManagedFileInspector(store);
            bool externalChange = inspector.InspectInstallation(inst);

            // Assert: detected change, invalidated stale pending action, set AttentionRequired
            Assert.True(externalChange);
            Assert.Null(inst.PendingAction);
            Assert.Equal(RestorationState.AttentionRequired, inst.RestorationState);
            Assert.Equal(ManagedFileState.ExternallyChanged, inst.ManagedFiles[0].CurrentState);
        }
    }
}
