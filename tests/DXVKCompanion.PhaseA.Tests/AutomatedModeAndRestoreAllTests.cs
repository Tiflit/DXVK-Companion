using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class AutomatedModeAndRestoreAllTests
    {
        [Fact]
        public void ManagementPolicy_IsAutomated_ResolvesCorrectlyAcrossGlobalAndPerGame()
        {
            var useGlobal = ManagementPolicy.UseGlobal();
            Assert.False(useGlobal.IsAutomated(GlobalManagementPolicy.Manual));
            Assert.True(useGlobal.IsAutomated(GlobalManagementPolicy.Automated));

            var explicitAuto = ManagementPolicy.Automatic();
            Assert.True(explicitAuto.IsAutomated(GlobalManagementPolicy.Manual));
            Assert.True(explicitAuto.IsAutomated(GlobalManagementPolicy.Automated));

            var disabled = ManagementPolicy.Disabled();
            Assert.False(disabled.IsAutomated(GlobalManagementPolicy.Manual));
            Assert.False(disabled.IsAutomated(GlobalManagementPolicy.Automated));

            var pinned = ManagementPolicy.PinVersion("2.6.2");
            Assert.True(pinned.IsAutomated(GlobalManagementPolicy.Manual));
            Assert.True(pinned.IsAutomated(GlobalManagementPolicy.Automated));
        }

        [Fact]
        public void SettingsStore_GlobalPolicyBackwardCompatibility_Preserved()
        {
            var settings = new SettingsStore();
            Assert.Equal(GlobalManagementPolicy.Manual, settings.GlobalPolicy);
            Assert.False(settings.AutoEnableDxvkForNewGames);

            settings.AutoEnableDxvkForNewGames = true;
            Assert.Equal(GlobalManagementPolicy.Automated, settings.GlobalPolicy);
            Assert.True(settings.AutoEnableDxvkForNewGames);

            settings.GlobalPolicy = GlobalManagementPolicy.Manual;
            Assert.False(settings.AutoEnableDxvkForNewGames);
        }

        [Fact]
        public async Task RestoreAllAsync_RestoresMultipleGamesIndependentlyWithIsolation()
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
            var profileStore = new ProfileStore();

            // Game 1: Enabled DXVK
            string game1Dir = Path.Combine(testDir.RootPath, "Games", "Game1");
            Directory.CreateDirectory(game1Dir);
            string game1Exe = Path.Combine(game1Dir, "Game1.exe");
            File.WriteAllText(game1Exe, "exe");
            string game1D3D11 = Path.Combine(game1Dir, "d3d11.dll");
            File.WriteAllText(game1D3D11, "dxvk-dll");

            var p1 = profileStore.GetOrCreate(game1Exe);
            p1.DxvkEnabled = true;
            p1.DxvkVersion = "2.6";
            profileStore.Save(p1);

            var inst1 = store.GetOrCreateInstallation(game1Dir, "Game1");
            inst1.RestorationState = RestorationState.Managed;
            inst1.ManagedDxvkVersion = "2.6";
            inst1.ManagedFiles.Add(new ManagedFileRecord
            {
                RelativePath = "d3d11.dll",
                OriginalState = FileOriginalState.Missing,
                CurrentState = ManagedFileState.Consistent,
                ManagedDxvkVersion = "2.6"
            });
            store.Save(inst1);

            // Game 2: Native / not managed
            string game2Dir = Path.Combine(testDir.RootPath, "Games", "Game2");
            Directory.CreateDirectory(game2Dir);
            string game2Exe = Path.Combine(game2Dir, "Game2.exe");
            File.WriteAllText(game2Exe, "exe");

            var p2 = profileStore.GetOrCreate(game2Exe);
            p2.DxvkEnabled = false;
            profileStore.Save(p2);

            var inst2 = store.GetOrCreateInstallation(game2Dir, "Game2");
            inst2.RestorationState = RestorationState.None;
            store.Save(inst2);

            var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(new HttpClient(), new CacheStore()), profileStore, store);

            // Act: run RestoreAll
            var summary = await manager.RestoreAllAsync();

            // Assert
            Assert.Equal(1, summary.TotalManaged);
            Assert.Equal(1, summary.Restored);
            Assert.Equal(1, summary.AlreadyRestored);
            Assert.Equal(0, summary.FailedOrAttentionRequired);

            // Game 1 d3d11.dll (which was Originally Missing) should be removed cleanly
            Assert.False(File.Exists(game1D3D11));
            Assert.False(p1.DxvkEnabled);
        }
    }
}
