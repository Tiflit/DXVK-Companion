using System;
using System.IO;
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
    public sealed class PendingActionBatchIsolationTests
    {
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
        public async Task IncompatibleDeployment_ThrowingCancellationSubscriber_DoesNotAbortUnrelatedQueuedRestore()
        {
            // Acceptance: An incompatible pending deployment first, a throwing cancellation subscriber,
            // and a valid unrelated queued restore later. Assert no forbidden deployment, durable cancellation,
            // later original-baseline restoration, accurate processed count and no cancellation replay.
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();
            using var gameDir1 = new SyntheticTestDirectory();
            using var gameDir2 = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // Game 1: Incompatible DX12 game with pending Install
            var exePath1 = gameDir1.CreateFile("Game1.exe", "synthetic-binary-1");
            var profile1 = profileStore.GetOrCreate(exePath1);
            profile1.Api = GraphicsApi.DX12;
            profile1.Architecture = "x64";
            profileStore.Save(profile1);

            var installation1 = store.GetOrCreateInstallation(gameDir1.RootPath, "Game1");
            var exeRec1 = installation1.GetOrAddExecutable("Game1.exe", "Game1.exe");
            exeRec1.LastKnownApi = GraphicsApi.DX12;
            installation1.PendingAction = PendingAction.Install("2.5", "Queued while running");
            store.Save(installation1);

            // Game 2: Supported DX11 game, initially deployed and managed with backup baseline, now has queued Restore
            var exePath2 = gameDir2.CreateFile("Game2.exe", "synthetic-binary-2");
            gameDir2.CreateFile("d3d11.dll", "original-native-d3d11");
            var profile2 = profileStore.GetOrCreate(exePath2);
            profile2.Api = GraphicsApi.DX11;
            profile2.Architecture = "x64";
            profileStore.Save(profile2);

            // Initial deployment to establish managed baseline and backup
            bool deployOk = await manager.EnableDxvkAsync(profile2, "2.5");
            Assert.True(deployOk);
            Assert.Equal("dxvk-d3d11", File.ReadAllText(Path.Combine(gameDir2.RootPath, "d3d11.dll")));

            var installation2 = store.FindByInstallationPath(gameDir2.RootPath);
            Assert.NotNull(installation2);
            Assert.Equal(RestorationState.Managed, installation2.RestorationState);

            // Queue Restore on Game 2
            installation2.PendingAction = PendingAction.Restore("User requested restore");
            store.Save(installation2);

            // Wire up throwing cancellation subscriber
            int cancellationEventCalls = 0;
            manager.OnPendingActionCancelled += (_, _, _) =>
            {
                cancellationEventCalls++;
                throw new InvalidOperationException("Simulated subscriber exception during cancellation notification");
            };

            // ACT: Process all pending actions
            int processedCount = await manager.ProcessAllPendingActionsAsync();

            // ASSERT:
            // 1. Processed count is exactly 1 (Game 2 succeeded; Game 1 was cancelled and its subscriber threw)
            Assert.Equal(1, processedCount);
            Assert.Equal(1, cancellationEventCalls);

            // 2. Game 1: No forbidden deployment occurred
            Assert.False(File.Exists(Path.Combine(gameDir1.RootPath, "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir1.RootPath, "dxgi.dll")));

            // 3. Game 1: Cancellation is durable and terminal across reload
            var reloadedStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var inst1After = reloadedStore.FindByInstallationPath(gameDir1.RootPath);
            Assert.NotNull(inst1After);
            Assert.Null(inst1After.PendingAction); // Cleared upon cancellation
            Assert.NotNull(inst1After.LastCancelledAction);
            Assert.Equal(PendingActionType.Install, inst1After.LastCancelledAction.Type);
            Assert.NotNull(inst1After.LastCancellationReason);
            Assert.Contains("DX12", inst1After.LastCancellationReason);

            // 4. Game 2: Unrelated queued restore completed successfully, original baseline restored
            var inst2After = reloadedStore.FindByInstallationPath(gameDir2.RootPath);
            Assert.NotNull(inst2After);
            Assert.Null(inst2After.PendingAction); // Completed
            Assert.Equal(RestorationState.Restored, inst2After.RestorationState);
            Assert.Equal("original-native-d3d11", File.ReadAllText(Path.Combine(gameDir2.RootPath, "d3d11.dll")));

            // 5. Subsequent batch does not replay cancellation or retry cancelled deployment
            int secondBatchCount = await manager.ProcessAllPendingActionsAsync();
            Assert.Equal(0, secondBatchCount);
            Assert.Equal(1, cancellationEventCalls); // No second event invocation
        }

        [Fact]
        public async Task TechnicalError_BeforeActionCompletion_RetainsPendingIntentAndAllowsLaterActionsToProceed()
        {
            // Acceptance: Technical failures before operation completion retain pending intent for retry.
            // Do not turn them into terminal compatibility cancellations, clear unrelated queues,
            // or claim file rollback merely because an exception was caught.
            // Later work proceeds while the failed entry's pending intent remains; include reload assertions.
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();
            using var gameDir1 = new SyntheticTestDirectory();
            using var gameDir2 = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // Game 1: Supported DX11 game with pending Install
            var exePath1 = gameDir1.CreateFile("Game1.exe", "synthetic-binary-1");
            var profile1 = profileStore.GetOrCreate(exePath1);
            profile1.Api = GraphicsApi.DX11;
            profile1.Architecture = "x64";
            profileStore.Save(profile1);

            var installation1 = store.GetOrCreateInstallation(gameDir1.RootPath, "Game1");
            var exeRec1 = installation1.GetOrAddExecutable("Game1.exe", "Game1.exe");
            exeRec1.LastKnownApi = GraphicsApi.DX11;
            installation1.PendingAction = PendingAction.Install("2.5", "Queued install 1");
            store.Save(installation1);

            // Game 2: Supported DX11 game with pending Install
            var exePath2 = gameDir2.CreateFile("Game2.exe", "synthetic-binary-2");
            var profile2 = profileStore.GetOrCreate(exePath2);
            profile2.Api = GraphicsApi.DX11;
            profile2.Architecture = "x64";
            profileStore.Save(profile2);

            var installation2 = store.GetOrCreateInstallation(gameDir2.RootPath, "Game2");
            var exeRec2 = installation2.GetOrAddExecutable("Game2.exe", "Game2.exe");
            exeRec2.LastKnownApi = GraphicsApi.DX11;
            installation2.PendingAction = PendingAction.Install("2.5", "Queued install 2");
            store.Save(installation2);

            // Use existing test seam: ProfileStore.CustomTempWriter fails only for Game 1's profile save
            int errorInjectedCount = 0;
            profileStore.CustomTempWriter = (tempPath, content) =>
            {
                if (content.Contains("Game1.exe") && errorInjectedCount == 0)
                {
                    errorInjectedCount++;
                    throw new IOException("Simulated transient I/O write error during Game 1 profile commit");
                }
                File.WriteAllText(tempPath, content);
            };

            // ACT: Process all pending actions
            int processedCount = await manager.ProcessAllPendingActionsAsync();

            // ASSERT:
            // 1. Processed count is exactly 1 (Game 2 succeeded; Game 1 failed technically before completion)
            Assert.Equal(1, processedCount);
            Assert.Equal(1, errorInjectedCount);

            // 2. Game 1: Pending action is retained (NOT cleared, NOT turned into a terminal compatibility cancellation)
            var reloadedStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var inst1After = reloadedStore.FindByInstallationPath(gameDir1.RootPath);
            Assert.NotNull(inst1After);
            Assert.NotNull(inst1After.PendingAction);
            Assert.Equal(PendingActionType.Install, inst1After.PendingAction.Type);
            Assert.Equal("2.5", inst1After.PendingAction.TargetDxvkVersion);
            Assert.Null(inst1After.LastCancelledAction); // Not cancelled

            // 3. Game 2: Succeeded and pending action cleared
            var inst2After = reloadedStore.FindByInstallationPath(gameDir2.RootPath);
            Assert.NotNull(inst2After);
            Assert.Null(inst2After.PendingAction);
            Assert.True(File.Exists(Path.Combine(gameDir2.RootPath, "d3d11.dll")));

            // 4. Retry: Once the transient error condition resolves, Game 1's pending action completes successfully
            profileStore.CustomTempWriter = null;
            int retryProcessedCount = await manager.ProcessAllPendingActionsAsync();
            Assert.Equal(1, retryProcessedCount);

            var freshStoreAfterRetry = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var inst1AfterRetry = freshStoreAfterRetry.FindByInstallationPath(gameDir1.RootPath);
            Assert.NotNull(inst1AfterRetry);
            Assert.Null(inst1AfterRetry.PendingAction);
            Assert.True(File.Exists(Path.Combine(gameDir1.RootPath, "d3d11.dll")));
        }

        [Fact]
        public async Task UnresolvedExecutable_MissingDirectory_SkipsAndRetainsPendingIntent_WithoutAbortingRemainingInstallations()
        {
            // Acceptance: An installation with an unresolvable executable / missing directory is skipped
            // as unresolved work, retaining its pending intent without aborting processing of later queued installations.
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();
            using var missingDirRoot = new SyntheticTestDirectory();
            using var gameDir2 = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // Game 1: Installation with a missing child directory under an owned synthetic root
            var missingGamePath = Path.Combine(missingDirRoot.RootPath, "MissingGameDir");
            Assert.False(Directory.Exists(missingGamePath));

            var installation1 = store.GetOrCreateInstallation(missingGamePath, "MissingGame");
            installation1.PendingAction = PendingAction.Install("2.5", "Queued on missing directory");
            store.Save(installation1);

            // Game 2: Valid supported DX11 installation with queued Install
            var exePath2 = gameDir2.CreateFile("Game2.exe", "synthetic-binary-2");
            var profile2 = profileStore.GetOrCreate(exePath2);
            profile2.Api = GraphicsApi.DX11;
            profile2.Architecture = "x64";
            profileStore.Save(profile2);

            var installation2 = store.GetOrCreateInstallation(gameDir2.RootPath, "Game2");
            var exeRec2 = installation2.GetOrAddExecutable("Game2.exe", "Game2.exe");
            exeRec2.LastKnownApi = GraphicsApi.DX11;
            installation2.PendingAction = PendingAction.Install("2.5", "Queued install 2");
            store.Save(installation2);

            // ACT
            int processed = await manager.ProcessAllPendingActionsAsync();

            // ASSERT:
            // 1. Processed count is exactly 1 (Game 2 succeeded; Game 1 was skipped as unresolved)
            Assert.Equal(1, processed);
            Assert.True(File.Exists(Path.Combine(gameDir2.RootPath, "d3d11.dll")));

            // 2. Reload store to verify state persistence
            var reloadedStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));

            // Game 1: Skipped unresolved work retains pending intent across reload (not cleared, not cancelled)
            var inst1After = reloadedStore.FindByInstallationPath(missingGamePath);
            Assert.NotNull(inst1After);
            Assert.NotNull(inst1After.PendingAction);
            Assert.Equal(PendingActionType.Install, inst1After.PendingAction.Type);
            Assert.Equal("2.5", inst1After.PendingAction.TargetDxvkVersion);
            Assert.Null(inst1After.LastCancelledAction);

            // Game 2: Succeeded and pending action cleared
            var inst2After = reloadedStore.FindByInstallationPath(gameDir2.RootPath);
            Assert.NotNull(inst2After);
            Assert.Null(inst2After.PendingAction);
        }
    }
}
