using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Storage;
using DXVKCompanion.Safety;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class SharedDirectoryCompatibilityTests
    {
        private static (DxvkManager manager, DxvkInstaller installer, GameLibraryStore store, ProfileStore profileStore, string sourceDir)
            CreateTestEnvironment(SyntheticTestDirectory storageDir, SyntheticTestDirectory sourceDir)
        {
            var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
            Directory.CreateDirectory(dxvkArchDir);
            File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-d3d11");
            File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-dxgi");
            File.WriteAllText(Path.Combine(dxvkArchDir, "d3d9.dll"), "dxvk-d3d9");

            var store = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            var http = new HttpClient();
            var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
            var rollback = new DxvkRollback(engine, store);
            var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "games.json"));
            var cacheStore = new CacheStore(Path.Combine(storageDir.RootPath, "cache.json"));
            cacheStore.SaveCachedRelease(new ReleaseInfo { Version = "2.5", DownloadUrl = "" }, "etag");
            var github = new DxvkGithubClient(http, cacheStore);
            var manager = new DxvkManager(installer, rollback, github, profileStore, store);

            return (manager, installer, store, profileStore, sourceDir.RootPath);
        }

        [Fact]
        public async Task SharedDirectory_DX11AndDX12Siblings_RefusesDeployment_InstallationWide_WithUserVisibleReason()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, installer, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            var exe12Path = gameDir.CreateFile("Game_DX12.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "MixedGame");
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;
            e11.LastKnownArchitecture = "x64";

            var e12 = installation.GetOrAddExecutable("Game_DX12.exe", "Game_DX12.exe");
            e12.LastKnownApi = GraphicsApi.DX12;
            e12.LastKnownArchitecture = "x64";
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profile11.Architecture = "x64";
            profileStore.Save(profile11);

            // Act: Attempt to enable DXVK for the DX11 executable
            var result = await manager.RequestEnableByPathAsync(profile11);

            // Assert: Refused installation-wide
            Assert.Equal(DxvkActionResult.Failed, result);

            // Assert: Visible refusal reason identifying the blocking executable and API
            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst);
            Assert.NotNull(refreshedInst.LastRefusalReason);
            Assert.Contains("Game_DX12.exe", refreshedInst.LastRefusalReason);
            Assert.Contains("DX12", refreshedInst.LastRefusalReason);
            Assert.NotNull(manager.LastRefusalReason);
            Assert.Contains("Game_DX12.exe", manager.LastRefusalReason);

            // Assert: No DLLs written to game directory
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));

            // Also test lower-level installer entry point refuses deployment
            var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
            bool installerResult = await installer.ApplyToGameAsync(profile11, release);
            Assert.False(installerResult);
            Assert.NotNull(installer.LastRefusalReason);
            Assert.Contains("Game_DX12.exe", installer.LastRefusalReason);
        }

        [Fact]
        public async Task SharedDirectory_DX11AndVulkanSiblings_RefusesDeployment_InstallationWide()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            gameDir.CreateFile("Game_Vulkan.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "VulkanMixedGame");
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;

            var eVulkan = installation.GetOrAddExecutable("Game_Vulkan.exe", "Game_Vulkan.exe");
            eVulkan.LastKnownApi = GraphicsApi.Vulkan;
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profile11.Architecture = "x64";
            profileStore.Save(profile11);

            var result = await manager.RequestEnableByPathAsync(profile11);
            Assert.Equal(DxvkActionResult.Failed, result);

            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst?.LastRefusalReason);
            Assert.Contains("Game_Vulkan.exe", refreshedInst.LastRefusalReason);
            Assert.Contains("Vulkan", refreshedInst.LastRefusalReason);
        }

        [Fact]
        public async Task SharedDirectory_DX11AndRecordedUnknownSibling_RefusesDeployment_InstallationWide()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            gameDir.CreateFile("Launcher.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "UnknownMixedGame");
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;

            var eUnknown = installation.GetOrAddExecutable("Launcher.exe", "Launcher.exe");
            eUnknown.LastKnownApi = GraphicsApi.Unknown;
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profileStore.Save(profile11);

            var result = await manager.RequestEnableByPathAsync(profile11);
            Assert.Equal(DxvkActionResult.Failed, result);

            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst?.LastRefusalReason);
            Assert.Contains("Launcher.exe", refreshedInst.LastRefusalReason);
            Assert.Contains("Unknown", refreshedInst.LastRefusalReason);
        }

        [Fact]
        public async Task SharedDirectory_DX11AndModernApiSibling_RefusesDeployment_InstallationWide()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            gameDir.CreateFile("Game_Modern.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "ModernApiMixedGame");
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;

            var eModern = installation.GetOrAddExecutable("Game_Modern.exe", "Game_Modern.exe");
            eModern.LastKnownApi = GraphicsApi.ModernAPI;
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profileStore.Save(profile11);

            var result = await manager.RequestEnableByPathAsync(profile11);
            Assert.Equal(DxvkActionResult.Failed, result);

            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst?.LastRefusalReason);
            Assert.Contains("Game_Modern.exe", refreshedInst.LastRefusalReason);
            Assert.Contains("ModernAPI", refreshedInst.LastRefusalReason);
        }

        [Fact]
        public async Task SharedDirectory_DX11AndUndefinedEnumSibling_RefusesDeployment_InstallationWide()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            gameDir.CreateFile("Game_Custom.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "UndefinedMixedGame");
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;

            var eCustom = installation.GetOrAddExecutable("Game_Custom.exe", "Game_Custom.exe");
            eCustom.LastKnownApi = (GraphicsApi)99;
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profileStore.Save(profile11);

            var result = await manager.RequestEnableByPathAsync(profile11);
            Assert.Equal(DxvkActionResult.Failed, result);

            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst?.LastRefusalReason);
            Assert.Contains("Game_Custom.exe", refreshedInst.LastRefusalReason);
        }

        [Fact]
        public async Task SharedDirectory_DX11Only_PositiveControl_AllowsDeployment()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "CleanDX11Game");
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;
            e11.LastKnownArchitecture = "x64";
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profile11.Architecture = "x64";
            profile11.DxvkVersion = "2.5";
            profileStore.Save(profile11);

            var result = await manager.RequestEnableByPathAsync(profile11);
            Assert.Equal(DxvkActionResult.Applied, result);

            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
        }

        [Fact]
        public async Task SharedDirectory_DX9AndDX11Siblings_PositiveControl_AllowsDeployment()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            gameDir.CreateFile("Game_DX9.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "SupportedMixedGame");
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;
            e11.LastKnownArchitecture = "x64";

            var e9 = installation.GetOrAddExecutable("Game_DX9.exe", "Game_DX9.exe");
            e9.LastKnownApi = GraphicsApi.DX9;
            e9.LastKnownArchitecture = "x64";
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profile11.Architecture = "x64";
            profile11.DxvkVersion = "2.5";
            profileStore.Save(profile11);

            var result = await manager.RequestEnableByPathAsync(profile11);
            Assert.Equal(DxvkActionResult.Applied, result);

            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
        }

        [Fact]
        public async Task SharedDirectory_UnsupportedExeInAnotherInstallation_DoesNotBlockSelectedInstallation()
        {
            using var gameDirA = new SyntheticTestDirectory();
            using var gameDirB = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // Installation A has DX12
            gameDirA.CreateFile("GameA_DX12.exe", "binary");
            var instA = store.GetOrCreateInstallation(gameDirA.RootPath, "GameA");
            var eA = instA.GetOrAddExecutable("GameA_DX12.exe", "GameA_DX12.exe");
            eA.LastKnownApi = GraphicsApi.DX12;
            store.Save(instA);

            // Installation B has DX11
            var exeBPath = gameDirB.CreateFile("GameB_DX11.exe", "binary");
            var instB = store.GetOrCreateInstallation(gameDirB.RootPath, "GameB");
            var eB = instB.GetOrAddExecutable("GameB_DX11.exe", "GameB_DX11.exe");
            eB.LastKnownApi = GraphicsApi.DX11;
            eB.LastKnownArchitecture = "x64";
            store.Save(instB);

            var profileB = profileStore.GetOrCreate(exeBPath);
            profileB.Api = GraphicsApi.DX11;
            profileB.Architecture = "x64";
            profileB.DxvkVersion = "2.5";
            profileStore.Save(profileB);

            // Act: deploy to Installation B
            var result = await manager.RequestEnableByPathAsync(profileB);

            // Assert: Installation B succeeds; not blocked by Installation A
            Assert.Equal(DxvkActionResult.Applied, result);
            Assert.True(File.Exists(Path.Combine(gameDirB.RootPath, "d3d11.dll")));
        }

        [Fact]
        public async Task SharedDirectory_Refusal_OccursBeforeFileWrites_Backups_OrPendingActionCreation()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            gameDir.CreateFile("Game_DX12.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "MixedGame");
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;
            var e12 = installation.GetOrAddExecutable("Game_DX12.exe", "Game_DX12.exe");
            e12.LastKnownApi = GraphicsApi.DX12;
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profileStore.Save(profile11);

            // Attempt deployment
            var result = await manager.RequestEnableByPathAsync(profile11);
            Assert.Equal(DxvkActionResult.Failed, result);

            // Verify no pending action created
            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst);
            Assert.Null(refreshedInst.PendingAction);

            // Verify no backup directory contents
            var backupsDir = Path.Combine(storageDir.RootPath, "backups");
            if (Directory.Exists(backupsDir))
            {
                Assert.Empty(Directory.GetFiles(backupsDir, "*.*", SearchOption.AllDirectories));
            }
        }

        [Fact]
        public async Task SharedDirectory_AdoptExisting_RefusedWhenSiblingIncompatible()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, installer, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            gameDir.CreateFile("Game_DX12.exe", "binary");

            // Place DXVK files
            gameDir.CreateFile("d3d11.dll", "dxvk-d3d11");
            gameDir.CreateFile("dxgi.dll", "dxvk-dxgi");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "AdoptMixedGame");
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;
            var e12 = installation.GetOrAddExecutable("Game_DX12.exe", "Game_DX12.exe");
            e12.LastKnownApi = GraphicsApi.DX12;
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profile11.Architecture = "x64";
            profileStore.Save(profile11);

            // Act
            bool adoptResult = await manager.AdoptExistingAsync(profile11);

            // Assert: Refused
            Assert.False(adoptResult);
            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst);
            Assert.Equal(RestorationState.None, refreshedInst.RestorationState);
            Assert.Empty(refreshedInst.ManagedFiles);
            Assert.NotNull(refreshedInst.LastRefusalReason);
            Assert.Contains("Game_DX12.exe", refreshedInst.LastRefusalReason);
        }

        [Fact]
        public async Task SharedDirectory_Reapply_RefusedWhenSiblingIncompatible()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            gameDir.CreateFile("Game_DX12.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "ReapplyMixedGame");
            installation.RestorationState = RestorationState.Managed;
            installation.ManagedDxvkVersion = "2.5";
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;
            var e12 = installation.GetOrAddExecutable("Game_DX12.exe", "Game_DX12.exe");
            e12.LastKnownApi = GraphicsApi.DX12;
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profile11.Architecture = "x64";
            profileStore.Save(profile11);

            // Act
            var result = await manager.RequestReapplyByPathAsync(profile11, updateBaseline: true);

            // Assert: Refused
            Assert.Equal(DxvkActionResult.Failed, result);
            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst?.LastRefusalReason);
            Assert.Contains("Game_DX12.exe", refreshedInst.LastRefusalReason);
        }

        [Fact]
        public async Task SharedDirectory_Restore_SucceedsEvenWhenSiblingIncompatible()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            gameDir.CreateFile("Game_DX12.exe", "binary");

            // Setup an installation that was previously deployed/managed, but has an original baseline
            string originalD3D11Content = "original-d3d11-native";
            string backupPath = Path.Combine(storageDir.RootPath, "backups", "Game_DX11", "d3d11.dll.bak");
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.WriteAllText(backupPath, originalD3D11Content);

            // Current file on disk is the DXVK dll
            string deployedD3D11 = Path.Combine(gameDir.RootPath, "d3d11.dll");
            File.WriteAllText(deployedD3D11, "dxvk-d3d11-dll");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "RestoreMixedGame");
            installation.RestorationState = RestorationState.Managed;
            installation.ManagedDxvkVersion = "2.5";
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;
            var e12 = installation.GetOrAddExecutable("Game_DX12.exe", "Game_DX12.exe");
            e12.LastKnownApi = GraphicsApi.DX12; // Incompatible sibling!

            var managedRecord = installation.GetOrAddManagedFile("d3d11.dll");
            managedRecord.OriginalState = FileOriginalState.Existing;
            managedRecord.BackupRelativePath = Path.Combine("Game_DX11", "d3d11.dll.bak");
            managedRecord.CurrentState = ManagedFileState.Consistent;
            managedRecord.ManagedDxvkVersion = "2.5";
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profile11.Architecture = "x64";
            profile11.DxvkEnabled = true;
            profileStore.Save(profile11);

            // Act: Request Disable/Restore on the game despite the DX12 sibling
            var result = await manager.RequestDisableByPathAsync(profile11);

            // Assert: Restore operation succeeds!
            Assert.Equal(DxvkActionResult.Applied, result);

            // Assert: Original file was restored on disk
            Assert.True(File.Exists(deployedD3D11));
            Assert.Equal(originalD3D11Content, File.ReadAllText(deployedD3D11));
        }
    }
}
