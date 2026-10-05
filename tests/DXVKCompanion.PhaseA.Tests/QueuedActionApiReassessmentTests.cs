using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Monitoring;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class QueuedActionApiReassessmentTests
    {
        private sealed class FakeModuleScanner : ModuleScanner
        {
            private readonly HashSet<string> _modules;

            public FakeModuleScanner(params string[] modules)
            {
                _modules = new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase);
            }

            public override HashSet<string> GetLoadedGraphicsModules(Process process) => _modules;
        }

        private sealed class FakePeParser : PeParser
        {
            private readonly IEnumerable<string> _imports;
            private readonly string _arch;

            public FakePeParser(IEnumerable<string> imports, string arch = "x64")
            {
                _imports = imports;
                _arch = arch;
            }

            public override IEnumerable<string> GetImports(string path) => _imports;
            public override string GetArchitecture(string path) => _arch;
        }

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
        public async Task QueuedAction_TargetReclassifiedToDX12_RefusesDeployment_LeavesFilesUntouched()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

            // Profile initially DX11
            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "ReclassifiedGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.DX11;
            exeRecord.LastKnownArchitecture = "x64";
            installation.PendingAction = PendingAction.Install("2.5", "Queued while game running");
            store.Save(installation);

            // Reclassify available evidence to DX12 via DetectionSnapshot
            var snapshot = new DetectionSnapshot
            {
                ProcessName = "Game",
                ExecutablePath = exePath,
                InstallationRoot = gameDir.RootPath,
                ExecutableRelativePath = "Game.exe",
                Classification = new ApiClassificationResult
                {
                    PrimaryApi = GraphicsApi.DX12,
                    ObservedApis = new List<GraphicsApi> { GraphicsApi.DX12 },
                    Confidence = ApiDetectionConfidence.High,
                    Architecture = "x64",
                    Evidence = new List<string> { "Loaded runtime module: d3d12.dll" },
                    EvidenceSource = "RuntimeModules"
                },
                TimestampUtc = DateTime.UtcNow
            };
            store.RecordDetectionSnapshot(snapshot);

            // Act: Execute pending action upon game exit
            bool result = await manager.ApplyPendingAsync(exePath);

            // Assert: Refused, pending action preserved, refusal reason recorded, no files written
            Assert.False(result);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));

            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst);
            Assert.NotNull(refreshedInst.PendingAction);
            Assert.True(refreshedInst.PendingAction.IsPending);
            Assert.NotNull(refreshedInst.LastRefusalReason);
            Assert.Contains("DX12", refreshedInst.LastRefusalReason);
            Assert.Equal(RestorationState.None, refreshedInst.RestorationState);
        }

        [Fact]
        public async Task QueuedAction_SupportedPositiveControl_ExecutesAndDeploysFiles()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "SupportedGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.DX11;
            exeRecord.LastKnownArchitecture = "x64";
            installation.PendingAction = PendingAction.Install("2.5", "Queued while game running");
            store.Save(installation);

            // Act: Execute pending action upon game exit
            bool result = await manager.ApplyPendingAsync(exePath);

            // Assert: Applied, pending action cleared, DXVK files deployed, state Managed
            Assert.True(result);
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));

            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst);
            Assert.Null(refreshedInst.PendingAction);
            Assert.Null(refreshedInst.LastRefusalReason);
            Assert.Equal("2.5", refreshedInst.ManagedDxvkVersion);
            Assert.Equal(RestorationState.Managed, refreshedInst.RestorationState);
        }

        [Fact]
        public async Task QueuedAction_SiblingReclassifiedToVulkan_RefusesDeployment_InstallationWide()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exe11Path = gameDir.CreateFile("Game_DX11.exe", "binary");
            var siblingPath = gameDir.CreateFile("Sibling.exe", "binary");

            var profile = profileStore.GetOrCreate(exe11Path);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "MultiExeGame");
            var e11 = installation.GetOrAddExecutable("Game_DX11.exe", "Game_DX11.exe");
            e11.LastKnownApi = GraphicsApi.DX11;
            e11.LastKnownArchitecture = "x64";

            var eSibling = installation.GetOrAddExecutable("Sibling.exe", "Sibling.exe");
            eSibling.LastKnownApi = GraphicsApi.DX11;
            eSibling.LastKnownArchitecture = "x64";

            installation.PendingAction = PendingAction.Install("2.5", "Queued for DX11 target");
            store.Save(installation);

            // Reclassify sibling to Vulkan
            var siblingSnapshot = new DetectionSnapshot
            {
                ProcessName = "Sibling",
                ExecutablePath = siblingPath,
                InstallationRoot = gameDir.RootPath,
                ExecutableRelativePath = "Sibling.exe",
                Classification = new ApiClassificationResult
                {
                    PrimaryApi = GraphicsApi.Vulkan,
                    ObservedApis = new List<GraphicsApi> { GraphicsApi.Vulkan },
                    Confidence = ApiDetectionConfidence.High,
                    Architecture = "x64",
                    Evidence = new List<string> { "Loaded runtime module: vulkan-1.dll" }
                },
                TimestampUtc = DateTime.UtcNow
            };
            store.RecordDetectionSnapshot(siblingSnapshot);

            // Act: Execute pending action on the DX11 target
            bool result = await manager.ApplyPendingAsync(exe11Path);

            // Assert: Refused installation-wide because sibling is Vulkan
            Assert.False(result);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));

            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst);
            Assert.NotNull(refreshedInst.PendingAction);
            Assert.True(refreshedInst.PendingAction.IsPending);
            Assert.NotNull(refreshedInst.LastRefusalReason);
            Assert.Contains("Sibling", refreshedInst.LastRefusalReason);
            Assert.Contains("Vulkan", refreshedInst.LastRefusalReason);
        }

        [Fact]
        public async Task QueuedAction_PersistedReload_ConsultsUpdatedEvidence_OnStartup_ProcessAllPendingActions()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            string libraryPath = Path.Combine(storageDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(storageDir.RootPath, "backups");
            string gamesPath = Path.Combine(storageDir.RootPath, "games.json");

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

            // Session 1: Setup installation with pending action, target is reclassified to DX12 before shutdown
            {
                var store1 = new GameLibraryStore(libraryPath, backupsPath);
                var inst = store1.GetOrCreateInstallation(gameDir.RootPath, "PersistedGame");
                var exe = inst.GetOrAddExecutable("Game.exe", "Game.exe");
                exe.LastKnownApi = GraphicsApi.DX12;
                exe.LastKnownArchitecture = "x64";
                inst.PendingAction = PendingAction.Install("2.5", "Persisted install");
                store1.Save(inst);
            }

            // Session 2: Startup in fresh session
            {
                var store2 = new GameLibraryStore(libraryPath, backupsPath);
                var engine2 = new MultiFileTransactionEngine(backupsPath);
                var http2 = new HttpClient();
                var installer2 = new DxvkInstaller(http2, engine2, store2, sourceDir.RootPath);
                var rollback2 = new DxvkRollback(engine2, store2);
                var profileStore2 = new ProfileStore(gamesPath);
                var cacheStore2 = new CacheStore(Path.Combine(storageDir.RootPath, "cache.json"));
                cacheStore2.SaveCachedRelease(new ReleaseInfo { Version = "2.5", DownloadUrl = "" }, "etag");
                var github2 = new DxvkGithubClient(http2, cacheStore2);
                var manager2 = new DxvkManager(installer2, rollback2, github2, profileStore2, store2);

                int processed = await manager2.ProcessAllPendingActionsAsync();

                Assert.Equal(0, processed);
                Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));

                var instAfter = store2.FindByInstallationPath(gameDir.RootPath);
                Assert.NotNull(instAfter?.PendingAction);
                Assert.True(instAfter!.PendingAction!.IsPending);
                Assert.NotNull(instAfter.LastRefusalReason);
                Assert.Contains("DX12", instAfter.LastRefusalReason);
            }
        }

        [Fact]
        public async Task QueuedAction_PersistedReload_SupportedPositiveControl_ExecutesOnStartup_ProcessAllPendingActions()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            string libraryPath = Path.Combine(storageDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(storageDir.RootPath, "backups");
            string gamesPath = Path.Combine(storageDir.RootPath, "games.json");

            var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
            Directory.CreateDirectory(dxvkArchDir);
            File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-d3d11");
            File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-dxgi");

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

            // Session 1: Setup installation with pending action and supported DX11 classification
            {
                var store1 = new GameLibraryStore(libraryPath, backupsPath);
                var inst = store1.GetOrCreateInstallation(gameDir.RootPath, "PersistedSupportedGame");
                var exe = inst.GetOrAddExecutable("Game.exe", "Game.exe");
                exe.LastKnownApi = GraphicsApi.DX11;
                exe.LastKnownArchitecture = "x64";
                inst.PendingAction = PendingAction.Install("2.5", "Persisted install");
                store1.Save(inst);
            }

            // Session 2: Startup in fresh session (empty profileStore simulating startup load)
            {
                var store2 = new GameLibraryStore(libraryPath, backupsPath);
                var engine2 = new MultiFileTransactionEngine(backupsPath);
                var http2 = new HttpClient();
                var installer2 = new DxvkInstaller(http2, engine2, store2, sourceDir.RootPath);
                var rollback2 = new DxvkRollback(engine2, store2);
                var profileStore2 = new ProfileStore(gamesPath);
                var cacheStore2 = new CacheStore(Path.Combine(storageDir.RootPath, "cache.json"));
                cacheStore2.SaveCachedRelease(new ReleaseInfo { Version = "2.5", DownloadUrl = "" }, "etag");
                var github2 = new DxvkGithubClient(http2, cacheStore2);
                var manager2 = new DxvkManager(installer2, rollback2, github2, profileStore2, store2);

                int processed = await manager2.ProcessAllPendingActionsAsync();

                Assert.Equal(1, processed);
                Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
                Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));

                var instAfter = store2.FindByInstallationPath(gameDir.RootPath);
                Assert.NotNull(instAfter);
                Assert.Null(instAfter.PendingAction);
                Assert.Null(instAfter.LastRefusalReason);
                Assert.Equal("2.5", instAfter.ManagedDxvkVersion);
            }
        }

        [Fact]
        public async Task QueuedAction_Restore_RemainsAvailable_EvenAfterReclassifiedToDX12()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            string nativeD3D11 = "native-original-d3d11";
            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
            var d3d11Path = gameDir.CreateFile("d3d11.dll", nativeD3D11);

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "RestoreGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.DX11;
            exeRecord.LastKnownArchitecture = "x64";
            store.Save(installation);

            // Deploy DX11 initially
            bool initialOk = await manager.EnableDxvkAsync(profile, "2.5");
            Assert.True(initialOk);
            Assert.Equal("dxvk-d3d11", File.ReadAllText(d3d11Path));

            // Queue a Restore operation
            installation.PendingAction = PendingAction.Restore("Queued restore while game running");
            store.Save(installation);

            // Reclassify target to DX12 via snapshot
            var snapshot = new DetectionSnapshot
            {
                ProcessName = "Game",
                ExecutablePath = exePath,
                InstallationRoot = gameDir.RootPath,
                ExecutableRelativePath = "Game.exe",
                Classification = new ApiClassificationResult
                {
                    PrimaryApi = GraphicsApi.DX12,
                    ObservedApis = new List<GraphicsApi> { GraphicsApi.DX12 },
                    Confidence = ApiDetectionConfidence.High,
                    Architecture = "x64"
                },
                TimestampUtc = DateTime.UtcNow
            };
            store.RecordDetectionSnapshot(snapshot);

            // Act: Apply pending Restore
            bool restoreOk = await manager.ApplyPendingAsync(exePath);

            // Assert: Restore MUST succeed regardless of DX12 classification
            Assert.True(restoreOk);
            Assert.Equal(nativeD3D11, File.ReadAllText(d3d11Path));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));

            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst);
            Assert.Null(refreshedInst.PendingAction);
            Assert.Equal(RestorationState.Restored, refreshedInst.RestorationState);
        }

        [Fact]
        public async Task QueuedAction_SnapshotPassedToApplyPendingAsync_RefusesUnsupportedImmediately()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "SnapshotGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.DX11;
            exeRecord.LastKnownArchitecture = "x64";
            installation.PendingAction = PendingAction.Install("2.5", "Queued while running");
            store.Save(installation);

            // Provide snapshot directly to ApplyPendingAsync
            var snapshot = new DetectionSnapshot
            {
                ProcessName = "Game",
                ExecutablePath = exePath,
                InstallationRoot = gameDir.RootPath,
                ExecutableRelativePath = "Game.exe",
                Classification = new ApiClassificationResult
                {
                    PrimaryApi = GraphicsApi.DX12,
                    ObservedApis = new List<GraphicsApi> { GraphicsApi.DX12 },
                    Confidence = ApiDetectionConfidence.High,
                    Architecture = "x64",
                    Evidence = new List<string> { "Loaded runtime module: d3d12.dll" }
                },
                TimestampUtc = DateTime.UtcNow
            };

            // Act
            bool result = await manager.ApplyPendingAsync(exePath, snapshot);

            // Assert: Refused, files untouched, pending action preserved
            Assert.False(result);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));

            var refreshedInst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(refreshedInst);
            Assert.NotNull(refreshedInst.PendingAction);
            Assert.True(refreshedInst.PendingAction.IsPending);
            Assert.Contains("DX12", refreshedInst.LastRefusalReason);
        }

        [Fact]
        public void MixedModule_Precedence_UnsupportedEvidenceWins_RegardlessOfOrder()
        {
            using var proc = Process.GetCurrentProcess();
            var parser = new FakePeParser(Array.Empty<string>());

            // 1. Runtime modules: DX11 + Vulkan in both orders
            var scanner11Vk = new FakeModuleScanner("d3d11.dll", "vulkan-1.dll");
            var classifier11Vk = new ApiClassifier(scanner11Vk, parser);
            var res11Vk = classifier11Vk.ClassifyDetailed(proc);
            Assert.Equal(GraphicsApi.Vulkan, res11Vk.PrimaryApi);

            var scannerVk11 = new FakeModuleScanner("vulkan-1.dll", "d3d11.dll");
            var classifierVk11 = new ApiClassifier(scannerVk11, parser);
            var resVk11 = classifierVk11.ClassifyDetailed(proc);
            Assert.Equal(GraphicsApi.Vulkan, resVk11.PrimaryApi);

            // 2. Runtime modules: DX11 + DX12 in both orders
            var scanner11_12 = new FakeModuleScanner("d3d11.dll", "d3d12.dll");
            var classifier11_12 = new ApiClassifier(scanner11_12, parser);
            var res11_12 = classifier11_12.ClassifyDetailed(proc);
            Assert.Equal(GraphicsApi.DX12, res11_12.PrimaryApi);

            var scanner12_11 = new FakeModuleScanner("d3d12.dll", "d3d11.dll");
            var classifier12_11 = new ApiClassifier(scanner12_11, parser);
            var res12_11 = classifier12_11.ClassifyDetailed(proc);
            Assert.Equal(GraphicsApi.DX12, res12_11.PrimaryApi);

            // 3. Runtime modules: DX12 + Vulkan + DX11 all present
            var scannerAll = new FakeModuleScanner("d3d11.dll", "vulkan-1.dll", "d3d12.dll");
            var classifierAll = new ApiClassifier(scannerAll, parser);
            var resAll = classifierAll.ClassifyDetailed(proc);
            Assert.Equal(GraphicsApi.DX12, resAll.PrimaryApi);

            // 4. Static PE imports in both orders
            using var tempDir = new SyntheticTestDirectory();
            var fakeExe = tempDir.CreateFile("Game.exe", "binary");
            var emptyScanner = new FakeModuleScanner();

            var parser11Vk = new FakePeParser(new[] { "d3d11.dll", "vulkan-1.dll" });
            var cPe11Vk = new ApiClassifier(emptyScanner, parser11Vk);
            Assert.Equal(GraphicsApi.Vulkan, cPe11Vk.ClassifyDetailed(proc, fakeExe).PrimaryApi);

            var parserVk11 = new FakePeParser(new[] { "vulkan-1.dll", "d3d11.dll" });
            var cPeVk11 = new ApiClassifier(emptyScanner, parserVk11);
            Assert.Equal(GraphicsApi.Vulkan, cPeVk11.ClassifyDetailed(proc, fakeExe).PrimaryApi);

            var parser11_12 = new FakePeParser(new[] { "d3d11.dll", "d3d12.dll" });
            var cPe11_12 = new ApiClassifier(emptyScanner, parser11_12);
            Assert.Equal(GraphicsApi.DX12, cPe11_12.ClassifyDetailed(proc, fakeExe).PrimaryApi);

            var parser12_11 = new FakePeParser(new[] { "d3d12.dll", "d3d11.dll" });
            var cPe12_11 = new ApiClassifier(emptyScanner, parser12_11);
            Assert.Equal(GraphicsApi.DX12, cPe12_11.ClassifyDetailed(proc, fakeExe).PrimaryApi);

            // 5. Supported-only controls (DX11 vs DX9 vs DX10)
            var scanner11_9 = new FakeModuleScanner("d3d9.dll", "d3d11.dll");
            var classifier11_9 = new ApiClassifier(scanner11_9, parser);
            Assert.Equal(GraphicsApi.DX11, classifier11_9.ClassifyDetailed(proc).PrimaryApi);

            var scanner9_10 = new FakeModuleScanner("d3d9.dll", "d3d10.dll");
            var classifier9_10 = new ApiClassifier(scanner9_10, parser);
            Assert.Equal(GraphicsApi.DX10, classifier9_10.ClassifyDetailed(proc).PrimaryApi);
        }

        [Fact]
        public async Task QueuedAction_TargetReclassifiedToUnknown_RefusesDeployment_DoesNotPromoteUnknownToSupported()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

            // Profile initially DX11
            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "UnknownReclassifiedGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.DX11;
            exeRecord.LastKnownArchitecture = "x64";
            installation.PendingAction = PendingAction.Install("2.5", "Queued while running");
            store.Save(installation);

            // Reclassify evidence to Unknown via DetectionSnapshot
            var snapshot = new DetectionSnapshot
            {
                ProcessName = "Game",
                ExecutablePath = exePath,
                InstallationRoot = gameDir.RootPath,
                ExecutableRelativePath = "Game.exe",
                Classification = new ApiClassificationResult
                {
                    PrimaryApi = GraphicsApi.Unknown,
                    ObservedApis = Array.Empty<GraphicsApi>(),
                    Confidence = ApiDetectionConfidence.Unknown,
                    Architecture = "x64"
                },
                TimestampUtc = DateTime.UtcNow
            };
            store.RecordDetectionSnapshot(snapshot);

            // Verify store actually recorded Unknown
            var instBeforeApply = store.FindByInstallationPath(gameDir.RootPath);
            Assert.Equal(GraphicsApi.Unknown, instBeforeApply!.FindExecutable("Game.exe")!.LastKnownApi);

            // Act: Attempt to apply pending action upon exit
            bool result = await manager.ApplyPendingAsync(exePath);

            // Assert: Refused, evidence must NOT be promoted to DX11, files untouched
            Assert.False(result);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));

            var instAfter = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfter);
            Assert.Equal(GraphicsApi.Unknown, instAfter.FindExecutable("Game.exe")!.LastKnownApi);
            Assert.NotNull(instAfter.PendingAction);
            Assert.True(instAfter.PendingAction.IsPending);
            Assert.NotNull(instAfter.LastRefusalReason);
            Assert.Contains("Unknown", instAfter.LastRefusalReason);
        }

        [Fact]
        public async Task QueuedAction_SiblingReclassifiedToUnknown_RefusesDeployment_InstallationWide()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var targetPath = gameDir.CreateFile("Target_DX11.exe", "binary");
            var siblingPath = gameDir.CreateFile("Sibling.exe", "binary");

            var targetProfile = profileStore.GetOrCreate(targetPath);
            targetProfile.Api = GraphicsApi.DX11;
            targetProfile.Architecture = "x64";
            profileStore.Save(targetProfile);

            var siblingProfile = profileStore.GetOrCreate(siblingPath);
            siblingProfile.Api = GraphicsApi.DX11;
            siblingProfile.Architecture = "x64";
            profileStore.Save(siblingProfile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "SiblingUnknownGame");
            var targetRecord = installation.GetOrAddExecutable("Target_DX11.exe", "Target_DX11.exe");
            targetRecord.LastKnownApi = GraphicsApi.DX11;

            var siblingRecord = installation.GetOrAddExecutable("Sibling.exe", "Sibling.exe");
            siblingRecord.LastKnownApi = GraphicsApi.DX11;

            installation.PendingAction = PendingAction.Install("2.5", "Queued for target");
            store.Save(installation);

            // Reclassify sibling to Unknown via snapshot
            var siblingSnapshot = new DetectionSnapshot
            {
                ProcessName = "Sibling",
                ExecutablePath = siblingPath,
                InstallationRoot = gameDir.RootPath,
                ExecutableRelativePath = "Sibling.exe",
                Classification = new ApiClassificationResult
                {
                    PrimaryApi = GraphicsApi.Unknown,
                    ObservedApis = Array.Empty<GraphicsApi>(),
                    Confidence = ApiDetectionConfidence.Unknown
                },
                TimestampUtc = DateTime.UtcNow
            };
            store.RecordDetectionSnapshot(siblingSnapshot);

            // Act: Apply pending action on the supported target
            bool result = await manager.ApplyPendingAsync(targetPath);

            // Assert: Refused installation-wide; sibling must NOT be promoted to DX11
            Assert.False(result);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));

            var instAfter = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfter);
            Assert.Equal(GraphicsApi.Unknown, instAfter.FindExecutable("Sibling.exe")!.LastKnownApi);
            Assert.NotNull(instAfter.PendingAction);
            Assert.True(instAfter.PendingAction.IsPending);
            Assert.NotNull(instAfter.LastRefusalReason);
            Assert.Contains("Sibling.exe", instAfter.LastRefusalReason);
            Assert.Contains("Unknown", instAfter.LastRefusalReason);
        }

        [Fact]
        public async Task QueuedAction_ConflictingKnownValues_DX12Profile_VersusDX11Record_RetainsRefusal()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

            // Profile has unsupported evidence (DX12)
            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX12;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            // Library has supported record (DX11) without established freshness
            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "ConflictGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.DX11;
            exeRecord.LastKnownArchitecture = "x64";
            installation.PendingAction = PendingAction.Install("2.5", "Queued action");
            store.Save(installation);

            // Act: Apply pending action
            bool result = await manager.ApplyPendingAsync(exePath);

            // Assert: Conservative refusal invariant: supported record must NOT erase unsupported profile evidence
            Assert.False(result);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));

            var instAfter = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfter);
            Assert.NotNull(instAfter.PendingAction);
            Assert.True(instAfter.PendingAction.IsPending);
            Assert.NotNull(instAfter.LastRefusalReason);
            Assert.Contains("DX12", instAfter.LastRefusalReason);
        }

        [Fact]
        public async Task QueuedAction_ConflictingKnownValues_VulkanProfile_VersusDX11Record_RetainsRefusal()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

            // Profile has unsupported evidence (Vulkan)
            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.Vulkan;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            // Library has supported record (DX11)
            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "VulkanConflictGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.DX11;
            exeRecord.LastKnownArchitecture = "x64";
            installation.PendingAction = PendingAction.Install("2.5", "Queued action");
            store.Save(installation);

            // Act: Apply pending action
            bool result = await manager.ApplyPendingAsync(exePath);

            // Assert: Refused, zero files deployed, refusal reason records Vulkan
            Assert.False(result);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));

            var instAfter = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfter);
            Assert.NotNull(instAfter.PendingAction);
            Assert.True(instAfter.PendingAction.IsPending);
            Assert.NotNull(instAfter.LastRefusalReason);
            Assert.Contains("Vulkan", instAfter.LastRefusalReason);
        }

        [Fact]
        public async Task QueuedAction_PathIdentity_SameNameDifferentDirectory_DoesNotLeakEvidenceOrPromoteUnknown()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // Nested executable inside subfolder
            var binDir = Path.Combine(gameDir.RootPath, "Bin");
            Directory.CreateDirectory(binDir);
            var nestedExe = Path.Combine(binDir, "Game.exe");
            File.WriteAllText(nestedExe, "binary");

            // Unrelated profile with same basename "Game.exe" in different directory has DX11
            var otherProfile = profileStore.GetOrCreate(@"C:\OtherGame\Game.exe");
            otherProfile.Api = GraphicsApi.DX11;
            profileStore.Save(otherProfile);

            // The actual nested executable has Unknown in GameLibraryStore
            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "NestedGame");
            var exeRecord = installation.GetOrAddExecutable("Bin/Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.Unknown;
            installation.PendingAction = PendingAction.Install("2.5", "Queued nested");
            store.Save(installation);

            // Act: Apply pending action for the nested executable
            bool result = await manager.ApplyPendingAsync(nestedExe);

            // Assert: Must NOT match unrelated C:\OtherGame\Game.exe by basename to promote Unknown to DX11
            Assert.False(result);
            Assert.False(File.Exists(Path.Combine(binDir, "d3d11.dll")));

            var instAfter = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfter);
            Assert.Equal(GraphicsApi.Unknown, instAfter.FindExecutable("Bin/Game.exe")!.LastKnownApi);
            Assert.NotNull(instAfter.LastRefusalReason);
            Assert.Contains("Unknown", instAfter.LastRefusalReason);
        }
    }
}
