using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        [Fact]
        public async Task SharedDirectory_SameNamedRecordedExecutable_WithUnsupportedRecordedApi_RefusesDeployment_InstallationWide()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, installer, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "SameNamedGame");
            var recordedExe = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            recordedExe.LastKnownApi = GraphicsApi.DX12;
            recordedExe.LastKnownArchitecture = "x64";
            store.Save(installation);

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            // Act 1: Manager request
            var result = await manager.RequestEnableByPathAsync(profile);

            // Assert: Refused installation-wide
            Assert.Equal(DxvkActionResult.Failed, result);
            Assert.NotNull(manager.LastRefusalReason);
            Assert.Contains("Game.exe", manager.LastRefusalReason);
            Assert.Contains("DX12", manager.LastRefusalReason);

            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst?.LastRefusalReason);
            Assert.Contains("Game.exe", refreshedInst.LastRefusalReason);

            // Assert: No DLLs written
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));

            // Act 2: Direct installer ApplyToGameAsync
            var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
            bool installerResult = await installer.ApplyToGameAsync(profile, release);
            Assert.False(installerResult);
            Assert.NotNull(installer.LastRefusalReason);
            Assert.Contains("Game.exe", installer.LastRefusalReason);
        }

        [Fact]
        public async Task SharedDirectory_CollidingBasenameSibling_RefusesDeployment_InstallationWide()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var rootExePath = gameDir.CreateFile("Game.exe", "binary");
            var subExePath = gameDir.CreateFile(Path.Combine("Sub", "Game.exe"), "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "CollidingBasenameGame");
            var eSub = installation.GetOrAddExecutable(Path.Combine("Sub", "Game.exe"), "Game.exe");
            eSub.LastKnownApi = GraphicsApi.DX12;
            var eRoot = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            eRoot.LastKnownApi = GraphicsApi.DX11;
            store.Save(installation);

            var profileRoot = profileStore.GetOrCreate(rootExePath);
            profileRoot.Api = GraphicsApi.DX11;
            profileStore.Save(profileRoot);

            var result = await manager.RequestEnableByPathAsync(profileRoot);
            Assert.Equal(DxvkActionResult.Failed, result);

            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst?.LastRefusalReason);
            Assert.Contains("Game.exe", refreshedInst.LastRefusalReason);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
        }

        [Fact]
        public async Task SharedDirectory_NestedExecutable_FindsContainingInstallation_AndRefusesDeployment_WhenSiblingIncompatible()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, installer, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // Root has DX12 executable
            var rootExePath = gameDir.CreateFile("Game_DX12.exe", "binary");
            // Nested folder Bin has DX11 executable
            var nestedExePath = gameDir.CreateFile(Path.Combine("Bin", "Game_DX11.exe"), "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "NestedRootGame");
            var e12 = installation.GetOrAddExecutable("Game_DX12.exe", "Game_DX12.exe");
            e12.LastKnownApi = GraphicsApi.DX12;
            store.Save(installation);

            var profileNested = profileStore.GetOrCreate(nestedExePath);
            profileNested.Api = GraphicsApi.DX11;
            profileNested.Architecture = "x64";
            profileStore.Save(profileNested);

            // Act 1: via Manager
            var result = await manager.RequestEnableByPathAsync(profileNested);

            // Assert: Refused
            Assert.Equal(DxvkActionResult.Failed, result);
            Assert.NotNull(manager.LastRefusalReason);
            Assert.Contains("Game_DX12.exe", manager.LastRefusalReason);

            // Assert: Zero files written anywhere
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "Bin", "d3d11.dll")));

            // Assert: Did NOT split into a separate installation for "Bin"
            var binInst = store.FindByInstallationPath(Path.Combine(gameDir.RootPath, "Bin"));
            Assert.Null(binInst);

            // Act 2: Direct installer ApplyToGameAsync
            var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
            bool installerResult = await installer.ApplyToGameAsync(profileNested, release);
            Assert.False(installerResult);
            Assert.NotNull(installer.LastRefusalReason);
            Assert.Contains("Game_DX12.exe", installer.LastRefusalReason);
            Assert.Null(store.FindByInstallationPath(Path.Combine(gameDir.RootPath, "Bin")));
        }

        [Fact]
        public async Task SharedDirectory_NestedExecutable_InSeparateInstallation_DoesNotBlock_PositiveControl()
        {
            using var gameDirA = new SyntheticTestDirectory();
            using var gameDirB = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // Installation A has DX12 executable
            gameDirA.CreateFile("GameA_DX12.exe", "binary");
            var instA = store.GetOrCreateInstallation(gameDirA.RootPath, "GameA");
            var eA = instA.GetOrAddExecutable("GameA_DX12.exe", "GameA_DX12.exe");
            eA.LastKnownApi = GraphicsApi.DX12;
            store.Save(instA);

            // Installation B has nested executable Bin/GameB_DX11.exe and root DX11 executable
            var nestedExeB = gameDirB.CreateFile(Path.Combine("Bin", "GameB_DX11.exe"), "binary");
            gameDirB.CreateFile("Launcher_DX11.exe", "binary");
            var instB = store.GetOrCreateInstallation(gameDirB.RootPath, "GameB");
            var eB1 = instB.GetOrAddExecutable("Launcher_DX11.exe", "Launcher_DX11.exe");
            eB1.LastKnownApi = GraphicsApi.DX11;
            var eB2 = instB.GetOrAddExecutable(Path.Combine("Bin", "GameB_DX11.exe"), "GameB_DX11.exe");
            eB2.LastKnownApi = GraphicsApi.DX11;
            store.Save(instB);

            var profileB = profileStore.GetOrCreate(nestedExeB);
            profileB.Api = GraphicsApi.DX11;
            profileB.Architecture = "x64";
            profileB.DxvkVersion = "2.5";
            profileStore.Save(profileB);

            // Act
            var result = await manager.RequestEnableByPathAsync(profileB);

            // Assert: Deployed successfully to gameDirB
            Assert.Equal(DxvkActionResult.Applied, result);
            Assert.True(File.Exists(Path.Combine(gameDirB.RootPath, "Bin", "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(gameDirA.RootPath, "d3d11.dll")));
        }

        [Fact]
        public async Task SharedDirectory_RunningProcess_WithIncompatibleSibling_RefusesImmediately_WithoutQueueingPendingAction()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            gameDir.CreateFile("Game_DX12.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "RunningMixedGame");
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;
            var e12 = installation.GetOrAddExecutable("Game_DX12.exe", "Game_DX12.exe");
            e12.LastKnownApi = GraphicsApi.DX12;
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profile11.Architecture = "x64";
            profileStore.Save(profile11);

            // Act: Request enable with a running process (using current process as live process handle)
            var currentProcess = Process.GetCurrentProcess();
            var enableResult = await manager.RequestEnableAsync(profile11, currentProcess);

            // Assert: Refused immediately (Failed, NOT Queued)
            Assert.Equal(DxvkActionResult.Failed, enableResult);
            Assert.NotNull(manager.LastRefusalReason);
            Assert.Contains("Game_DX12.exe", manager.LastRefusalReason);

            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst);
            Assert.Null(refreshedInst.PendingAction); // Crucial: must NOT queue!
            Assert.NotNull(refreshedInst.LastRefusalReason);

            // Also test RequestReapplyAsync with running process
            var reapplyResult = await manager.RequestReapplyAsync(profile11, currentProcess, updateBaseline: true);
            Assert.Equal(DxvkActionResult.Failed, reapplyResult);
            refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.Null(refreshedInst?.PendingAction);
        }

        [Fact]
        public async Task SharedDirectory_PersistedPendingAction_RefusedOnProcessAllPendingActions_AndApplyPendingAsync()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            gameDir.CreateFile("Game_DX12.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "PersistedPendingGame");
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;
            var e12 = installation.GetOrAddExecutable("Game_DX12.exe", "Game_DX12.exe");
            e12.LastKnownApi = GraphicsApi.DX12;
            installation.PendingAction = PendingAction.Install("2.5", "previously persisted");
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profileStore.Save(profile11);

            // Act 1: ApplyPendingAsync
            bool applied = await manager.ApplyPendingAsync(exe11Path);

            // Assert: Refused and did not write files
            Assert.False(applied);
            Assert.NotNull(manager.LastRefusalReason);
            Assert.Contains("Game_DX12.exe", manager.LastRefusalReason);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));

            // Act 2: ProcessAllPendingActionsAsync
            int count = await manager.ProcessAllPendingActionsAsync();
            Assert.Equal(0, count);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
        }

        [Fact]
        public async Task SharedDirectory_DirectInstallerEntryPoints_RefuseDeployment_WhenSiblingIncompatible()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (_, installer, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            gameDir.CreateFile("Game_DX12.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "DirectInstallerGame");
            installation.ManagedDxvkVersion = "2.5";
            installation.RestorationState = RestorationState.Managed;
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;
            var e12 = installation.GetOrAddExecutable("Game_DX12.exe", "Game_DX12.exe");
            e12.LastKnownApi = GraphicsApi.DX12;
            store.Save(installation);

            var profile11 = profileStore.GetOrCreate(exe11Path);
            profile11.Api = GraphicsApi.DX11;
            profile11.Architecture = "x64";
            profileStore.Save(profile11);

            var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

            // 1. Direct ApplyToGameAsync
            bool applyOk = await installer.ApplyToGameAsync(profile11, release);
            Assert.False(applyOk);
            Assert.NotNull(installer.LastRefusalReason);
            Assert.Contains("Game_DX12.exe", installer.LastRefusalReason);

            // 2. Direct ReapplyAsync
            bool reapplyOk = await installer.ReapplyAsync(profile11, updateBaseline: true);
            Assert.False(reapplyOk);
            Assert.NotNull(installer.LastRefusalReason);
            Assert.Contains("Game_DX12.exe", installer.LastRefusalReason);

            // 3. Direct AdoptExisting
            var assessment = new ExistingDxvkAssessment
            {
                Status = ExistingDxvkStatus.OfficialRelease,
                MatchedVersion = "2.5",
                DetectedDlls = new List<string> { "d3d11.dll" }
            };
            gameDir.CreateFile("d3d11.dll", "fake-dxvk");
            bool adoptOk = installer.AdoptExisting(profile11, assessment);
            Assert.False(adoptOk);
            Assert.NotNull(installer.LastRefusalReason);
            Assert.Contains("Game_DX12.exe", installer.LastRefusalReason);
        }

        [Fact]
        public async Task SharedDirectory_Installation_RecordsRefusalReason_AndClearsOnSuccess()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "binary");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "RefusalLifecycleGame");
            var e = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            e.LastKnownApi = GraphicsApi.DX12; // Incompatible initially
            store.Save(installation);

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profile.DxvkVersion = "2.5";
            profileStore.Save(profile);

            // Initial attempt: fails due to DX12 recorded executable
            var failResult = await manager.RequestEnableByPathAsync(profile);
            Assert.Equal(DxvkActionResult.Failed, failResult);

            var instAfterFail = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfterFail?.LastRefusalReason);
            Assert.NotNull(manager.LastRefusalReason);

            // Update recorded executable to DX11 (now compatible)
            e.LastKnownApi = GraphicsApi.DX11;
            store.Save(installation);

            // Second attempt: succeeds and clears refusal reason
            var successResult = await manager.RequestEnableByPathAsync(profile);
            Assert.Equal(DxvkActionResult.Applied, successResult);

            var instAfterSuccess = store.FindByInstallationPath(gameDir.RootPath);
            Assert.Null(instAfterSuccess?.LastRefusalReason);
            Assert.Null(manager.LastRefusalReason);
        }

        [Fact]
        public async Task SharedDirectory_NestedExecutable_DeploymentAndRestore_PreservesRootFiles_RestoresNestedBaseline()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // 1. Root unrelated sentinels
            string rootD3D11Content = "root-unrelated-native-d3d11";
            string rootConfContent = "root-unrelated-dxvk-conf";
            var rootD3D11 = gameDir.CreateFile("d3d11.dll", rootD3D11Content);
            var rootConf = gameDir.CreateFile("dxvk.conf", rootConfContent);

            // 2. Nested target directory (Bin)
            string nestedNativeD3D11Content = "nested-native-baseline-d3d11";
            var nestedExePath = gameDir.CreateFile(Path.Combine("Bin", "Game_DX11.exe"), "binary");
            var nestedD3D11 = gameDir.CreateFile(Path.Combine("Bin", "d3d11.dll"), nestedNativeD3D11Content);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "NestedGame");
            var exe = installation.GetOrAddExecutable(Path.Combine("Bin", "Game_DX11.exe"), "Game_DX11.exe");
            exe.LastKnownApi = GraphicsApi.DX11;
            exe.LastKnownArchitecture = "x64";
            store.Save(installation);

            var profile = profileStore.GetOrCreate(nestedExePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profile.DxvkVersion = "2.5";
            profile.HudEnabled = true;
            profileStore.Save(profile);

            // Act 1: Deploy DXVK to nested executable
            var deployResult = await manager.RequestEnableByPathAsync(profile);
            Assert.Equal(DxvkActionResult.Applied, deployResult);

            // Assert: Nested files updated to DXVK
            Assert.True(File.Exists(nestedD3D11));
            Assert.Equal("dxvk-d3d11", File.ReadAllText(nestedD3D11));
            string nestedDxgi = Path.Combine(gameDir.RootPath, "Bin", "dxgi.dll");
            Assert.True(File.Exists(nestedDxgi));
            Assert.Equal("dxvk-dxgi", File.ReadAllText(nestedDxgi));
            string nestedConf = Path.Combine(gameDir.RootPath, "Bin", "dxvk.conf");
            Assert.True(File.Exists(nestedConf));

            // Assert: Root unrelated sentinels completely untouched
            Assert.Equal(rootD3D11Content, File.ReadAllText(rootD3D11));
            Assert.Equal(rootConfContent, File.ReadAllText(rootConf));

            // Assert: Root-relative managed file keys on installation
            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst);
            Assert.Contains(refreshedInst.ManagedFiles, f => f.RelativePath == Path.Combine("Bin", "d3d11.dll"));
            Assert.Contains(refreshedInst.ManagedFiles, f => f.RelativePath == Path.Combine("Bin", "dxgi.dll"));
            Assert.Contains(refreshedInst.ManagedFiles, f => f.RelativePath == Path.Combine("Bin", "dxvk.conf"));

            // Act 2: Disable / Restore DXVK from nested profile
            var restoreResult = await manager.RequestDisableByPathAsync(profile);
            Assert.Equal(DxvkActionResult.Applied, restoreResult);

            // Assert: Nested baseline restored byte-identical
            Assert.True(File.Exists(nestedD3D11));
            Assert.Equal(nestedNativeD3D11Content, File.ReadAllText(nestedD3D11));

            // Assert: Originally absent nested files removed
            Assert.False(File.Exists(nestedDxgi));
            Assert.False(File.Exists(nestedConf));

            // Assert: Root unrelated sentinels STILL completely untouched
            Assert.True(File.Exists(rootD3D11));
            Assert.Equal(rootD3D11Content, File.ReadAllText(rootD3D11));
            Assert.True(File.Exists(rootConf));
            Assert.Equal(rootConfContent, File.ReadAllText(rootConf));
        }

        [Fact]
        public async Task SharedDirectory_NestedExecutable_ReapplyAndRestoreAll_PreservesRootFiles_RestoresNestedBaseline()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // 1. Root unrelated sentinels
            string rootD3D11Content = "root-unrelated-native-d3d11";
            string rootConfContent = "root-unrelated-dxvk-conf";
            var rootD3D11 = gameDir.CreateFile("d3d11.dll", rootD3D11Content);
            var rootConf = gameDir.CreateFile("dxvk.conf", rootConfContent);

            // 2. Nested target directory (Bin)
            string nestedNativeD3D11Content = "nested-native-baseline-d3d11";
            var nestedExePath = gameDir.CreateFile(Path.Combine("Bin", "Game_DX11.exe"), "binary");
            var nestedD3D11 = gameDir.CreateFile(Path.Combine("Bin", "d3d11.dll"), nestedNativeD3D11Content);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "NestedReapplyGame");
            var exe = installation.GetOrAddExecutable(Path.Combine("Bin", "Game_DX11.exe"), "Game_DX11.exe");
            exe.LastKnownApi = GraphicsApi.DX11;
            exe.LastKnownArchitecture = "x64";
            store.Save(installation);

            var profile = profileStore.GetOrCreate(nestedExePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profile.DxvkVersion = "2.5";
            profileStore.Save(profile);

            // Act 1: Initial deployment
            var deployResult = await manager.RequestEnableByPathAsync(profile);
            Assert.Equal(DxvkActionResult.Applied, deployResult);

            // Act 2: Reapply DXVK to nested executable
            var reapplyResult = await manager.RequestReapplyByPathAsync(profile, updateBaseline: true);
            Assert.Equal(DxvkActionResult.Applied, reapplyResult);

            // Assert: Root sentinels untouched
            Assert.Equal(rootD3D11Content, File.ReadAllText(rootD3D11));
            Assert.Equal(rootConfContent, File.ReadAllText(rootConf));

            // Act 3: RestoreAll
            var summary = await manager.RestoreAllAsync();
            Assert.Equal(1, summary.TotalManaged);
            Assert.Equal(1, summary.Restored);

            // Assert: Nested baseline restored byte-identical
            Assert.True(File.Exists(nestedD3D11));
            Assert.Equal(nestedNativeD3D11Content, File.ReadAllText(nestedD3D11));
            string nestedDxgi = Path.Combine(gameDir.RootPath, "Bin", "dxgi.dll");
            Assert.False(File.Exists(nestedDxgi));

            // Assert: Root sentinels preserved
            Assert.Equal(rootD3D11Content, File.ReadAllText(rootD3D11));
            Assert.Equal(rootConfContent, File.ReadAllText(rootConf));
        }

        [Fact]
        public async Task SharedDirectory_NestedExecutable_AdoptExisting_AndRestore_PreservesRootFiles()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // 1. Root unrelated sentinels
            string rootD3D11Content = "root-unrelated-native-d3d11";
            var rootD3D11 = gameDir.CreateFile("d3d11.dll", rootD3D11Content);

            // 2. Nested target directory (Bin)
            var nestedExePath = gameDir.CreateFile(Path.Combine("Bin", "Game_DX11.exe"), "binary");
            var nestedD3D11 = gameDir.CreateFile(Path.Combine("Bin", "d3d11.dll"), "dxvk-d3d11");
            var nestedDxgi = gameDir.CreateFile(Path.Combine("Bin", "dxgi.dll"), "dxvk-dxgi");

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "NestedAdoptGame");
            var exe = installation.GetOrAddExecutable(Path.Combine("Bin", "Game_DX11.exe"), "Game_DX11.exe");
            exe.LastKnownApi = GraphicsApi.DX11;
            exe.LastKnownArchitecture = "x64";
            store.Save(installation);

            var profile = profileStore.GetOrCreate(nestedExePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            // Act 1: Adopt existing in nested folder
            bool adoptOk = await manager.AdoptExistingAsync(profile);
            Assert.True(adoptOk);

            // Assert: ManagedFiles keys are root-relative
            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst);
            Assert.Contains(refreshedInst.ManagedFiles, f => f.RelativePath == Path.Combine("Bin", "d3d11.dll"));
            Assert.Contains(refreshedInst.ManagedFiles, f => f.RelativePath == Path.Combine("Bin", "dxgi.dll"));

            // Assert: Root sentinel untouched
            Assert.Equal(rootD3D11Content, File.ReadAllText(rootD3D11));

            // Act 2: Restore
            var restoreResult = await manager.RequestDisableByPathAsync(profile);
            Assert.Equal(DxvkActionResult.Applied, restoreResult);

            // Assert: Nested DLLs removed (since they were originally adopted as Missing original state)
            Assert.False(File.Exists(nestedD3D11));
            Assert.False(File.Exists(nestedDxgi));

            // Assert: Root sentinel still untouched
            Assert.True(File.Exists(rootD3D11));
            Assert.Equal(rootD3D11Content, File.ReadAllText(rootD3D11));
        }
    }
}
