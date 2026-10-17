using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public sealed class ManagedFileInspectorBatchIsolationTests
    {
        private sealed class FaultyFirstInspectionInspector : ManagedFileInspector
        {
            private readonly string _failingInstallationPath;
            public int InspectCalls { get; private set; }

            public FaultyFirstInspectionInspector(GameLibraryStore store, string failingInstallationPath)
                : base(store)
            {
                _failingInstallationPath = failingInstallationPath;
            }

            public override bool InspectInstallation(GameInstallation installation)
            {
                InspectCalls++;
                if (string.Equals(installation.InstallationPath, _failingInstallationPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Simulated exception during Game 1 file inspection");
                }
                return base.InspectInstallation(installation);
            }
        }

        private static (DxvkManager manager, DxvkInstaller installer, GameLibraryStore store, ProfileStore profileStore, string sourceDir)
            CreateTestEnvironment(SyntheticTestDirectory storageDir, SyntheticTestDirectory sourceDir)
        {
            var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
            Directory.CreateDirectory(dxvkArchDir);
            File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-d3d11");
            File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-dxgi");

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
        public async Task FirstInspectionThrows_ContinuationInspectsLaterTargetAndDetectsExternalChange()
        {
            // Acceptance: An exception from one InspectInstallation call does not abort inspection of later
            // installations or escape from InspectAll solely because that entry failed.
            // Mark failed installation AttentionRequired with explanatory failure reason, retaining active version,
            // ownership/hash/backup records and pending intent. Game/backup bytes remain unchanged.
            // Later real inspection detects changed target. Reload store verifies persisted state.
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();
            using var gameDir1 = new SyntheticTestDirectory();
            using var gameDir2 = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // Game 1: Managed DXVK 2.5 installation with backup and pending intent
            var exePath1 = gameDir1.CreateFile("Game1.exe", "synthetic-binary-1");
            gameDir1.CreateFile("d3d11.dll", "original-native-d3d11");
            var profile1 = profileStore.GetOrCreate(exePath1);
            profile1.Api = GraphicsApi.DX11;
            profile1.Architecture = "x64";
            profileStore.Save(profile1);

            bool deployOk1 = await manager.EnableDxvkAsync(profile1, "2.5");
            Assert.True(deployOk1);

            var inst1 = store.FindByInstallationPath(gameDir1.RootPath);
            Assert.NotNull(inst1);
            inst1.PendingAction = PendingAction.Install("2.5", "Queued install intent");
            store.Save(inst1);

            var game1DllPath = Path.Combine(gameDir1.RootPath, "d3d11.dll");
            byte[] game1DllBefore = File.ReadAllBytes(game1DllPath);
            var game1Record = inst1.FindManagedFile("d3d11.dll");
            Assert.NotNull(game1Record);
            var backupFilePath = Path.Combine(storageDir.RootPath, "backups", game1Record.OriginalBackupRelativePath!);
            byte[] game1BackupBefore = File.ReadAllBytes(backupFilePath);

            // Game 2: Managed DXVK 2.5 installation with external change
            var exePath2 = gameDir2.CreateFile("Game2.exe", "synthetic-binary-2");
            gameDir2.CreateFile("d3d11.dll", "original-native-d3d11");
            var profile2 = profileStore.GetOrCreate(exePath2);
            profile2.Api = GraphicsApi.DX11;
            profile2.Architecture = "x64";
            profileStore.Save(profile2);

            bool deployOk2 = await manager.EnableDxvkAsync(profile2, "2.5");
            Assert.True(deployOk2);

            // External tampering on Game 2 target
            File.WriteAllText(Path.Combine(gameDir2.RootPath, "d3d11.dll"), "tampered-external-change");

            var inspector = new FaultyFirstInspectionInspector(store, gameDir1.RootPath);

            // ACT
            int changesDetected = inspector.InspectAll();

            // ASSERT:
            // 1. Both installations were inspected (continuation proved)
            Assert.Equal(2, inspector.InspectCalls);

            // 2. Count is exactly 1 (only Game 2 was a detected external change; Game 1 failure is not counted)
            Assert.Equal(1, changesDetected);

            // 3. Game 1: Marked AttentionRequired with explanatory reason
            var inst1After = store.FindByInstallationPath(gameDir1.RootPath);
            Assert.NotNull(inst1After);
            Assert.Equal(RestorationState.AttentionRequired, inst1After.RestorationState);
            Assert.NotNull(inst1After.LastRefusalReason);
            Assert.Contains("Inspection failed", inst1After.LastRefusalReason);
            Assert.Contains("Simulated exception during Game 1 file inspection", inst1After.LastRefusalReason);

            // 4. Game 1: Retains active version, ownership/hash records, and pending intent
            Assert.Equal("2.5", inst1After.ManagedDxvkVersion);
            Assert.NotEmpty(inst1After.ManagedFiles);
            Assert.NotNull(inst1After.PendingAction);
            Assert.Equal(PendingActionType.Install, inst1After.PendingAction.Type);

            // 5. Game 1: Unchanged game bytes and backup bytes
            Assert.Equal(game1DllBefore, File.ReadAllBytes(game1DllPath));
            Assert.Equal(game1BackupBefore, File.ReadAllBytes(backupFilePath));

            // 6. Game 2: Correct later detection of external change
            var inst2After = store.FindByInstallationPath(gameDir2.RootPath);
            Assert.NotNull(inst2After);
            Assert.Equal(RestorationState.AttentionRequired, inst2After.RestorationState);
            var d3d11Rec2 = inst2After.FindManagedFile("d3d11.dll");
            Assert.NotNull(d3d11Rec2);
            Assert.Equal(ManagedFileState.ExternallyChanged, d3d11Rec2.CurrentState);

            // 7. Fresh store reload verifies persistence of failure record and external change
            var reloadedStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));

            var reloadedInst1 = reloadedStore.FindByInstallationPath(gameDir1.RootPath);
            Assert.NotNull(reloadedInst1);
            Assert.Equal(RestorationState.AttentionRequired, reloadedInst1.RestorationState);
            Assert.Contains("Inspection failed", reloadedInst1.LastRefusalReason);
            Assert.Equal("2.5", reloadedInst1.ManagedDxvkVersion);
            Assert.NotNull(reloadedInst1.PendingAction);

            var reloadedInst2 = reloadedStore.FindByInstallationPath(gameDir2.RootPath);
            Assert.NotNull(reloadedInst2);
            Assert.Equal(RestorationState.AttentionRequired, reloadedInst2.RestorationState);
            Assert.Equal(ManagedFileState.ExternallyChanged, reloadedInst2.FindManagedFile("d3d11.dll")!.CurrentState);
        }

        [Fact]
        public async Task InspectionFailure_PersistenceFailure_PermitsContinuedBatchProcessing()
        {
            // Acceptance: Verify continued batch behavior when best-effort failure persistence also throws.
            // Use existing storage test seams (GameLibraryStore.CustomTempWriter), not a new production framework.
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();
            using var gameDir1 = new SyntheticTestDirectory();
            using var gameDir2 = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // Game 1 setup
            var exePath1 = gameDir1.CreateFile("Game1.exe", "synthetic-binary-1");
            gameDir1.CreateFile("d3d11.dll", "original-native-d3d11");
            var profile1 = profileStore.GetOrCreate(exePath1);
            profile1.Api = GraphicsApi.DX11;
            profile1.Architecture = "x64";
            profileStore.Save(profile1);
            bool deployOk1 = await manager.EnableDxvkAsync(profile1, "2.5");
            Assert.True(deployOk1);

            // Game 2 setup with external change
            var exePath2 = gameDir2.CreateFile("Game2.exe", "synthetic-binary-2");
            gameDir2.CreateFile("d3d11.dll", "original-native-d3d11");
            var profile2 = profileStore.GetOrCreate(exePath2);
            profile2.Api = GraphicsApi.DX11;
            profile2.Architecture = "x64";
            profileStore.Save(profile2);
            bool deployOk2 = await manager.EnableDxvkAsync(profile2, "2.5");
            Assert.True(deployOk2);
            File.WriteAllText(Path.Combine(gameDir2.RootPath, "d3d11.dll"), "tampered-external-change");

            // Rig storage seam to throw when persisting Game 1's failure record
            int saveFailuresInjected = 0;
            store.CustomTempWriter = (tempPath, content) =>
            {
                if (content.Contains(gameDir1.RootPath) && saveFailuresInjected == 0)
                {
                    saveFailuresInjected++;
                    throw new IOException("Simulated I/O disk failure during Game 1 failure-state persistence");
                }
                File.WriteAllText(tempPath, content);
            };

            var inspector = new FaultyFirstInspectionInspector(store, gameDir1.RootPath);

            // ACT
            int changesDetected = inspector.InspectAll();

            // ASSERT:
            // 1. Persistence error on Game 1 occurred and was tolerated
            Assert.Equal(1, saveFailuresInjected);

            // 2. Batch continuation proceeded to Game 2
            Assert.Equal(2, inspector.InspectCalls);

            // 3. Game 2 change was detected and persisted
            Assert.Equal(1, changesDetected);

            // Restore storage seam to verify Game 2 persisted state across reload
            store.CustomTempWriter = null;
            var reloadedStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var reloadedInst2 = reloadedStore.FindByInstallationPath(gameDir2.RootPath);
            Assert.NotNull(reloadedInst2);
            Assert.Equal(RestorationState.AttentionRequired, reloadedInst2.RestorationState);
            Assert.Equal(ManagedFileState.ExternallyChanged, reloadedInst2.FindManagedFile("d3d11.dll")!.CurrentState);
        }

        [Fact]
        public async Task StartupOrder_InspectionFailure_DoesNotPreventSubsequentPendingActionRestoreFromCompleting()
        {
            // Acceptance: Focused synthetic integration check of existing startup order
            // (InspectAll then ProcessAllPendingActionsAsync), showing an unrelated valid queued restore
            // can complete after earlier inspection failure. Existing queued restore ownership/backup
            // validation remains authoritative; do not bypass validation for failed inspections.
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();
            using var gameDir1 = new SyntheticTestDirectory();
            using var gameDir2 = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // Game 1: Managed DXVK 2.5 installation with tampered target file and queued Restore
            var exePath1 = gameDir1.CreateFile("Game1.exe", "synthetic-binary-1");
            gameDir1.CreateFile("d3d11.dll", "original-native-d3d11");
            var profile1 = profileStore.GetOrCreate(exePath1);
            profile1.Api = GraphicsApi.DX11;
            profile1.Architecture = "x64";
            profileStore.Save(profile1);
            bool deployOk1 = await manager.EnableDxvkAsync(profile1, "2.5");
            Assert.True(deployOk1);

            // Tamper Game 1 target DLL so ownership validation would refuse restore
            File.WriteAllText(Path.Combine(gameDir1.RootPath, "d3d11.dll"), "tampered-unauthorized-dll");

            var inst1 = store.FindByInstallationPath(gameDir1.RootPath);
            Assert.NotNull(inst1);
            inst1.PendingAction = PendingAction.Restore("User requested restore 1");
            store.Save(inst1);

            // Game 2: Managed DXVK 2.5 installation with valid baseline and queued Restore
            var exePath2 = gameDir2.CreateFile("Game2.exe", "synthetic-binary-2");
            gameDir2.CreateFile("d3d11.dll", "original-native-d3d11");
            var profile2 = profileStore.GetOrCreate(exePath2);
            profile2.Api = GraphicsApi.DX11;
            profile2.Architecture = "x64";
            profileStore.Save(profile2);
            bool deployOk2 = await manager.EnableDxvkAsync(profile2, "2.5");
            Assert.True(deployOk2);

            var inst2 = store.FindByInstallationPath(gameDir2.RootPath);
            Assert.NotNull(inst2);
            inst2.PendingAction = PendingAction.Restore("User requested restore 2");
            store.Save(inst2);

            var inspector = new FaultyFirstInspectionInspector(store, gameDir1.RootPath);

            // ACT: Execute startup sequence matching TrayApp.InitializeStartupStateAsync
            int detectedChanges = inspector.InspectAll();
            int processedCount = await manager.ProcessAllPendingActionsAsync();

            // ASSERT:
            // 1. InspectAll: Game 1 threw -> caught and marked AttentionRequired; Game 2 inspected
            Assert.Equal(2, inspector.InspectCalls);
            Assert.Equal(0, detectedChanges); // Neither was detected as a valid external change during inspection

            // 2. ProcessAllPendingActionsAsync: exactly 1 action completed (Game 2 restore)
            Assert.Equal(1, processedCount);

            // 3. Reload store to verify authoritative state
            var reloadedStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));

            // Game 1: Ownership validation was NOT bypassed; restore was refused due to target hash mismatch
            var inst1After = reloadedStore.FindByInstallationPath(gameDir1.RootPath);
            Assert.NotNull(inst1After);
            Assert.Equal(RestorationState.AttentionRequired, inst1After.RestorationState);
            Assert.NotNull(inst1After.LastRefusalReason);
            Assert.Contains("Managed target hash mismatch for restore", inst1After.LastRefusalReason);
            // File on disk remains tampered, not restored
            Assert.Equal("tampered-unauthorized-dll", File.ReadAllText(Path.Combine(gameDir1.RootPath, "d3d11.dll")));

            // Game 2: Restore completed successfully, original native DLL restored from backup
            var inst2After = reloadedStore.FindByInstallationPath(gameDir2.RootPath);
            Assert.NotNull(inst2After);
            Assert.Equal(RestorationState.Restored, inst2After.RestorationState);
            Assert.Null(inst2After.PendingAction);
            Assert.Equal("original-native-d3d11", File.ReadAllText(Path.Combine(gameDir2.RootPath, "d3d11.dll")));
        }
    }
}
