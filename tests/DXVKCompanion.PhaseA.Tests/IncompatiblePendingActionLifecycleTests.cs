using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public class IncompatiblePendingActionLifecycleTests
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

        private static string SanitizeDiagnostic(string? text, params (string? search, string replacement)[] replacements)
        {
            if (string.IsNullOrEmpty(text)) return "<null>";
            string sanitized = text;
            if (replacements != null)
            {
                foreach (var (search, replacement) in replacements)
                {
                    if (!string.IsNullOrEmpty(search))
                    {
                        sanitized = sanitized.Replace(search, replacement, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                sanitized = sanitized.Replace(userProfile, "<userProfile>", StringComparison.OrdinalIgnoreCase);
            }
            var tempPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrEmpty(tempPath))
            {
                sanitized = sanitized.Replace(tempPath, "<temp>", StringComparison.OrdinalIgnoreCase);
            }
            return sanitized;
        }

        private static string BuildApplyPendingDiagnostic(
            string stage,
            bool result,
            DxvkManager manager,
            GameLibraryStore store,
            string gameRoot,
            string storageRoot,
            int notificationCount)
        {
            var inst = store.FindByInstallationPath(gameRoot);
            var leaseCoord = manager.LeaseCoordinator as OperationLeaseCoordinator;
            var replacements = new (string?, string)[]
            {
                (gameRoot, "<gameRoot>"),
                (storageRoot, "<storageRoot>")
            };

            return $"[{stage}] result={result}; " +
                $"manager.LastRefusal='{SanitizeDiagnostic(manager.LastRefusalReason, replacements)}'; " +
                $"LeaseCoord.Error='{SanitizeDiagnostic(manager.LeaseCoordinator?.LastAcquisitionError, replacements)}'; " +
                $"LeaseCoord.Diag='{SanitizeDiagnostic(leaseCoord?.LastAcquisitionDiagnostic, replacements)}'; " +
                $"inst.Pending={(inst?.PendingAction != null ? inst.PendingAction.Type.ToString() : "<null>")}; " +
                $"inst.Cancelled={(inst?.LastCancelledAction != null ? inst.LastCancelledAction.Type.ToString() : "<null>")}; " +
                $"inst.CancelReason='{SanitizeDiagnostic(inst?.LastCancellationReason, replacements)}'; " +
                $"inst.RefusalReason='{SanitizeDiagnostic(inst?.LastRefusalReason, replacements)}'; " +
                $"inst.RestorationState={(inst != null ? inst.RestorationState.ToString() : "<null>")}; " +
                $"notifications={notificationCount}";
        }

        [Fact]
        public async Task ExitTrigger_IncompatiblePendingAction_TerminallyCancelled_ReasonRetained_NotRetriedOnSubsequentExit()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX12;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "TestGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.DX12;
            installation.PendingAction = PendingAction.Install("2.5", "Queued while running");
            store.Save(installation);

            // Act 1: Game exits and triggers ApplyPendingAsync
            bool result1 = await manager.ApplyPendingAsync(exePath);
            string diag1 = BuildApplyPendingDiagnostic("Act1", result1, manager, store, gameDir.RootPath, storageDir.RootPath, 0);

            // Assert 1: Deployment refused, files untouched, pending action terminally cancelled
            Assert.False(result1, $"Act 1 ApplyPendingAsync returned true unexpectedly. Diagnostics: {diag1}");
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));

            var instAfter1 = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfter1);
            Assert.True(instAfter1.PendingAction == null, $"PendingAction was not cancelled after Act 1. Diagnostics: {diag1}");
            Assert.Null(instAfter1.PendingAction); // Must be terminally cancelled (null)
            Assert.NotNull(instAfter1.LastRefusalReason);
            Assert.Contains("DX12", instAfter1.LastRefusalReason);

            // Act 2: Subsequent game exit triggers ApplyPendingAsync again
            bool result2 = await manager.ApplyPendingAsync(exePath);
            string diag2 = BuildApplyPendingDiagnostic("Act2", result2, manager, store, gameDir.RootPath, storageDir.RootPath, 0);

            // Assert 2: No-op, remains cancelled, reason retained, no files written
            Assert.False(result2, $"Act 2 ApplyPendingAsync returned true unexpectedly. Diagnostics: {diag2}");
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            var instAfter2 = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfter2);
            Assert.True(instAfter2.PendingAction == null, $"PendingAction was revived after Act 2. Diagnostics: {diag2}");
            Assert.Null(instAfter2.PendingAction);
            Assert.NotNull(instAfter2.LastRefusalReason);
            Assert.Contains("DX12", instAfter2.LastRefusalReason);
        }

        [Fact]
        public async Task StartupTrigger_IncompatiblePendingAction_TerminallyCancelled_ReasonRetained_NotRetriedOnSubsequentStartup()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.Vulkan;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "VulkanStartupGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.Vulkan;
            installation.PendingAction = PendingAction.Install("2.5", "Persisted install");
            store.Save(installation);

            // Act 1: Startup processes pending actions
            int processed1 = await manager.ProcessAllPendingActionsAsync();
            var instAfter1 = store.FindByInstallationPath(gameDir.RootPath);
            string diag1 = $"[Act1] processed={processed1}; " +
                $"manager.LastRefusal='{SanitizeDiagnostic(manager.LastRefusalReason, (gameDir.RootPath, "<gameRoot>"), (storageDir.RootPath, "<storageRoot>"))}'; " +
                $"inst.Pending={(instAfter1?.PendingAction != null ? instAfter1.PendingAction.Type.ToString() : "<null>")}; " +
                $"inst.RefusalReason='{SanitizeDiagnostic(instAfter1?.LastRefusalReason, (gameDir.RootPath, "<gameRoot>"), (storageDir.RootPath, "<storageRoot>"))}'";

            // Assert 1: 0 processed, action terminally cancelled
            Assert.True(processed1 == 0, $"Act 1 ProcessAllPendingActionsAsync processed {processed1} unexpectedly. Diagnostics: {diag1}");
            Assert.Equal(0, processed1);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));

            Assert.NotNull(instAfter1);
            Assert.True(instAfter1.PendingAction == null, $"PendingAction was not cancelled after Act 1 startup processing. Diagnostics: {diag1}");
            Assert.Null(instAfter1.PendingAction); // Terminally cancelled
            Assert.NotNull(instAfter1.LastRefusalReason);
            Assert.Contains("Vulkan", instAfter1.LastRefusalReason);

            // Act 2: Subsequent startup
            int processed2 = await manager.ProcessAllPendingActionsAsync();
            var instAfter2 = store.FindByInstallationPath(gameDir.RootPath);
            string diag2 = $"[Act2] processed={processed2}; " +
                $"manager.LastRefusal='{SanitizeDiagnostic(manager.LastRefusalReason, (gameDir.RootPath, "<gameRoot>"), (storageDir.RootPath, "<storageRoot>"))}'; " +
                $"inst.Pending={(instAfter2?.PendingAction != null ? instAfter2.PendingAction.Type.ToString() : "<null>")}; " +
                $"inst.RefusalReason='{SanitizeDiagnostic(instAfter2?.LastRefusalReason, (gameDir.RootPath, "<gameRoot>"), (storageDir.RootPath, "<storageRoot>"))}'";

            // Assert 2: Still 0 processed, remains cancelled
            Assert.True(processed2 == 0, $"Act 2 ProcessAllPendingActionsAsync processed {processed2} unexpectedly. Diagnostics: {diag2}");
            Assert.Equal(0, processed2);
            var instAfter2Check = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfter2Check);
            Assert.True(instAfter2Check.PendingAction == null, $"PendingAction was revived after Act 2 startup processing. Diagnostics: {diag2}");
            Assert.Null(instAfter2Check.PendingAction);
            Assert.NotNull(instAfter2Check.LastRefusalReason);
            Assert.Contains("Vulkan", instAfter2Check.LastRefusalReason);
        }

        [Fact]
        public async Task PersistedReload_CancelledState_PersistedAcrossStoreReload_PreservesBackupsAndManagedData()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
            var nativeD3D11 = gameDir.CreateFile("d3d11.dll", "original-native-d3d11");

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            // Step 1: Deploy DXVK under DX11 to establish real managed state and backups
            bool deployed = await manager.EnableDxvkAsync(profile, "2.5");
            Assert.True(deployed, $"Initial DXVK deployment under DX11 failed before reaching cancellation test: LastRefusal='{SanitizeDiagnostic(manager.LastRefusalReason, (gameDir.RootPath, "<gameRoot>"), (storageDir.RootPath, "<storageRoot>"))}'");

            var inst = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(inst);
            Assert.Equal(RestorationState.Managed, inst.RestorationState);
            Assert.Equal("2.5", inst.ManagedDxvkVersion);

            // Verify backup exists
            string backupPath = Path.Combine(storageDir.RootPath, "backups", inst.Id, "d3d11.dll");
            Assert.True(File.Exists(backupPath));
            string backupBytes = File.ReadAllText(backupPath);
            Assert.Equal("original-native-d3d11", backupBytes);

            // Step 2: Queue a pending update/reapply, then reclassify to DX12
            profile.Api = GraphicsApi.DX12;
            profileStore.Save(profile);
            inst.FindExecutable("Game.exe")!.LastKnownApi = GraphicsApi.DX12;
            inst.PendingAction = PendingAction.Update("2.5", "Scheduled update");
            store.Save(inst);

            // Step 3: Trigger exit cancellation
            bool applyResult = await manager.ApplyPendingAsync(exePath);
            string applyDiag = BuildApplyPendingDiagnostic("Step3", applyResult, manager, store, gameDir.RootPath, storageDir.RootPath, 0);
            Assert.False(applyResult, $"Step 3 ApplyPendingAsync returned true unexpectedly. Diagnostics: {applyDiag}");

            // Step 4: Reload store from disk (simulate application restart)
            var reloadedStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var reloadedInst = reloadedStore.FindByInstallationPath(gameDir.RootPath);

            Assert.NotNull(reloadedInst);
            Assert.True(reloadedInst.PendingAction == null, $"PendingAction was not cancelled after reload. Diagnostics: {applyDiag}");
            Assert.Null(reloadedInst.PendingAction); // Terminally cancelled
            Assert.NotNull(reloadedInst.LastCancelledAction);
            Assert.Equal(PendingActionType.Update, reloadedInst.LastCancelledAction.Type);
            Assert.Equal("2.5", reloadedInst.LastCancelledAction.TargetDxvkVersion);
            Assert.NotNull(reloadedInst.LastCancellationOutcome);
            Assert.Contains("Cancelled pending Update", reloadedInst.LastCancellationOutcome);
            Assert.Contains("DX12", reloadedInst.LastCancellationOutcome);
            Assert.NotNull(reloadedInst.LastCancellationReason);
            Assert.Contains("DX12", reloadedInst.LastCancellationReason);
            Assert.Equal(reloadedInst.LastCancellationOutcome, reloadedInst.LastRefusalReason);

            // Critical: Unrelated managed state, versions, and backups MUST be preserved intact
            Assert.Equal(RestorationState.Managed, reloadedInst.RestorationState);
            Assert.Equal("2.5", reloadedInst.ManagedDxvkVersion);
            Assert.True(File.Exists(backupPath));
            Assert.Equal("original-native-d3d11", File.ReadAllText(backupPath));
        }

        [Fact]
        public async Task NotificationSignal_FiresOnceOnCancellationTransition_DoesNotRepeatOnSubsequentExitOrStartup()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX12;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "NotifyGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.DX12;
            installation.PendingAction = PendingAction.Install("2.5", "Queued install");
            store.Save(installation);

            var receivedNotifications = new List<(GameInstallation inst, PendingAction action, string reason)>();
            manager.OnPendingActionCancelled += (inst, action, reason) =>
            {
                receivedNotifications.Add((inst, action, reason));
            };

            // Act 1: First trigger (ApplyPendingAsync) cancels the action
            bool result1 = await manager.ApplyPendingAsync(exePath);
            string diag1 = BuildApplyPendingDiagnostic("Act1", result1, manager, store, gameDir.RootPath, storageDir.RootPath, receivedNotifications.Count);
            Assert.False(result1, $"Act 1 ApplyPendingAsync returned true unexpectedly. Diagnostics: {diag1}");

            // Assert 1: Exactly one notification raised with the actual cancelled action details
            Assert.True(receivedNotifications.Count == 1,
                $"Initial cancellation transition failed to raise exactly 1 notification (count={receivedNotifications.Count}). Earliest failed stage diagnostics: {diag1}");
            Assert.Single(receivedNotifications);
            Assert.Equal("NotifyGame", receivedNotifications[0].inst.DisplayName);
            Assert.Equal(PendingActionType.Install, receivedNotifications[0].action.Type);
            Assert.Contains("DX12", receivedNotifications[0].reason);

            // Act 2: Subsequent game exit
            bool result2 = await manager.ApplyPendingAsync(exePath);
            string diag2 = BuildApplyPendingDiagnostic("Act2", result2, manager, store, gameDir.RootPath, storageDir.RootPath, receivedNotifications.Count);
            Assert.False(result2, $"Act 2 ApplyPendingAsync returned true unexpectedly. Diagnostics: {diag2}");

            // Act 3: Subsequent startup batch processing
            int processed = await manager.ProcessAllPendingActionsAsync();
            string diag3 = $"[Act3] processed={processed}; " +
                $"manager.LastRefusal='{SanitizeDiagnostic(manager.LastRefusalReason, (gameDir.RootPath, "<gameRoot>"), (storageDir.RootPath, "<storageRoot>"))}'; " +
                $"notifications={receivedNotifications.Count}";
            Assert.True(processed == 0, $"Act 3 ProcessAllPendingActionsAsync processed {processed} actions unexpectedly. Diagnostics: {diag3}");
            Assert.Equal(0, processed);

            // Assert 2 & 3: No duplicate notification raised
            Assert.True(receivedNotifications.Count == 1,
                $"Expected notification count to remain 1 after repeated exit/startup triggers, but count={receivedNotifications.Count}. Diagnostics: {diag3}");
            Assert.Single(receivedNotifications);
        }

        [Fact]
        public async Task NonRevival_ReclassificationToSupportedApi_DoesNotReviveCancelledAction()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX12;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "RevivalTestGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.DX12;
            installation.PendingAction = PendingAction.Install("2.5", "Queued install");
            store.Save(installation);

            // Cancel the action due to DX12
            bool cancelResult = await manager.ApplyPendingAsync(exePath);
            string cancelDiag = BuildApplyPendingDiagnostic("InitialCancel", cancelResult, manager, store, gameDir.RootPath, storageDir.RootPath, 0);
            var instAfterCancel = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfterCancel);
            Assert.True(instAfterCancel.PendingAction == null, $"PendingAction was not cancelled during setup. Diagnostics: {cancelDiag}");
            Assert.Null(instAfterCancel.PendingAction);
            Assert.NotNull(instAfterCancel.LastCancelledAction);

            // Crucial R2 check: cancellation MUST suppress automatic deployment
            Assert.False(manager.CanAutomaticallyDeploy(instAfterCancel));
            Assert.False(manager.CanAutomaticallyDeploy(exePath));

            // Later: Game is reclassified back to supported DX11
            profile.Api = GraphicsApi.DX11;
            profileStore.Save(profile);
            exeRecord.LastKnownApi = GraphicsApi.DX11;
            store.Save(instAfterCancel);

            // Crucial R2 check: Reclassification to DX11 MUST NOT lift automatic deployment suppression
            Assert.False(manager.CanAutomaticallyDeploy(instAfterCancel));
            Assert.False(manager.CanAutomaticallyDeploy(exePath));

            // Reload store to simulate process restart: suppression MUST remain durable
            var reloadedStore = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var reloadedInst = reloadedStore.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(reloadedInst);
            Assert.False(manager.CanAutomaticallyDeploy(reloadedInst));

            // Act: Automatic execution attempts (exit and startup) must NOT revive cancelled work
            bool applyResult = await manager.ApplyPendingAsync(exePath);
            int batchResult = await manager.ProcessAllPendingActionsAsync();

            // Assert: Nothing deployed automatically
            Assert.False(applyResult);
            Assert.Equal(0, batchResult);
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
            Assert.False(profile.DxvkEnabled);
        }

        [Fact]
        public async Task FreshRequest_AfterReclassificationToSupportedApi_SucceedsAsPositiveControl()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX12;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "FreshRequestGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.DX12;
            installation.PendingAction = PendingAction.Install("2.5", "Queued install");
            store.Save(installation);

            // Cancel action
            bool cancelResult = await manager.ApplyPendingAsync(exePath);
            string cancelDiag = BuildApplyPendingDiagnostic("InitialCancel", cancelResult, manager, store, gameDir.RootPath, storageDir.RootPath, 0);
            var instCancelled = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instCancelled);
            Assert.True(instCancelled.PendingAction == null, $"PendingAction was not cancelled during setup. Diagnostics: {cancelDiag}");
            Assert.Null(instCancelled.PendingAction);
            Assert.NotNull(instCancelled.LastCancelledAction);
            Assert.False(manager.CanAutomaticallyDeploy(instCancelled));

            // Reclassify to DX11
            profile.Api = GraphicsApi.DX11;
            profileStore.Save(profile);
            exeRecord.LastKnownApi = GraphicsApi.DX11;
            store.Save(instCancelled);

            // Act: Deliberate fresh user request to enable DXVK via RequestEnableByPathAsync
            var actionResult = await manager.RequestEnableByPathAsync(profile);
            Assert.Equal(DxvkActionResult.Applied, actionResult);

            // Assert: Fresh request succeeds, files deployed, cancelled state cleared, auto-deploy re-enabled
            Assert.True(profile.DxvkEnabled);
            Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));

            var instAfterFresh = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfterFresh);
            Assert.Null(instAfterFresh.LastRefusalReason);
            Assert.Null(instAfterFresh.LastCancelledAction);
            Assert.Null(instAfterFresh.LastCancellationOutcome);
            Assert.True(manager.CanAutomaticallyDeploy(instAfterFresh));
            Assert.Equal(RestorationState.Managed, instAfterFresh.RestorationState);
        }

        [Fact]
        public async Task PublicRequestOrchestration_PopulatedTransientQueue_CancelledWithoutErasingUnrelatedOrRestoreIntent()
        {
            using var gameDir1 = new SyntheticTestDirectory();
            using var gameDir2 = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            // Game 1: Starts as DX11, public request orchestration queues while running
            var exe1 = gameDir1.CreateFile("Game1.exe", "synthetic-binary-1");
            var profile1 = profileStore.GetOrCreate(exe1);
            profile1.Api = GraphicsApi.DX11;
            profile1.Architecture = "x64";
            profileStore.Save(profile1);

            using var currentProcess = Process.GetCurrentProcess();
            var queueResult1 = await manager.RequestEnableAsync(profile1, currentProcess);
            Assert.Equal(DxvkActionResult.Queued, queueResult1);
            Assert.True(manager.HasTransientPendingAction(exe1));

            var inst1 = store.FindByInstallationPath(gameDir1.RootPath);
            Assert.NotNull(inst1);
            Assert.NotNull(inst1.PendingAction);

            // Reclassify Game 1 to DX12 (becomes incompatible while queued/running)
            profile1.Api = GraphicsApi.DX12;
            profileStore.Save(profile1);
            inst1.GetOrAddExecutable("Game1.exe", "Game1.exe").LastKnownApi = GraphicsApi.DX12;
            store.Save(inst1);

            // Game 2: Unrelated game with Restore queued via public RequestDisableAsync while running
            var exe2 = gameDir2.CreateFile("Game2.exe", "synthetic-binary-2");
            var profile2 = profileStore.GetOrCreate(exe2);
            profile2.Api = GraphicsApi.DX11;
            profile2.Architecture = "x64";
            profileStore.Save(profile2);

            var queueResult2 = await manager.RequestDisableAsync(profile2, currentProcess);
            Assert.Equal(DxvkActionResult.Queued, queueResult2);
            Assert.True(manager.HasTransientPendingAction(exe2));

            var inst2 = store.FindByInstallationPath(gameDir2.RootPath);
            Assert.NotNull(inst2);
            Assert.NotNull(inst2.PendingAction);
            Assert.Equal(PendingActionType.Restore, inst2.PendingAction.Type);

            // Act 1: Trigger exit for Game 1 -> should cancel Game 1's action
            bool result1 = await manager.ApplyPendingAsync(exe1);
            string diag1 = BuildApplyPendingDiagnostic("Act1", result1, manager, store, gameDir1.RootPath, storageDir.RootPath, 0);
            Assert.False(result1, $"Act 1 ApplyPendingAsync returned true unexpectedly. Diagnostics: {diag1}");

            var reloadedInst1 = store.FindByInstallationPath(gameDir1.RootPath);
            Assert.NotNull(reloadedInst1);
            Assert.True(reloadedInst1.PendingAction == null, $"PendingAction for Game 1 was not cancelled after Act 1. Diagnostics: {diag1}");
            Assert.Null(reloadedInst1.PendingAction); // Cancelled
            Assert.NotNull(reloadedInst1.LastCancelledAction);
            Assert.False(manager.HasTransientPendingAction(exe1)); // Removed from transient queue

            // Act 2: Verify Game 2's queued Restore intent in transient and persisted queues is completely untouched
            Assert.True(manager.HasTransientPendingAction(exe2));
            var reloadedInst2 = store.FindByInstallationPath(gameDir2.RootPath);
            Assert.NotNull(reloadedInst2);
            Assert.NotNull(reloadedInst2.PendingAction);
            Assert.Equal(PendingActionType.Restore, reloadedInst2.PendingAction.Type);
        }

        [Fact]
        public async Task TechnicalFailure_DownloadOrSourceUnavailable_DoesNotCancelPendingAction()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            // Set up environment where DXVK release source directory does NOT exist for version "99.0"
            var store = new GameLibraryStore(
                Path.Combine(storageDir.RootPath, "game-library.json"),
                Path.Combine(storageDir.RootPath, "backups"));
            var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
            var http = new HttpClient();
            var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
            var rollback = new DxvkRollback(engine, store);
            var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "games.json"));
            var cacheStore = new CacheStore(Path.Combine(storageDir.RootPath, "cache.json"));
            // No cached release or download URL
            var github = new DxvkGithubClient(http, cacheStore);
            var manager = new DxvkManager(installer, rollback, github, profileStore, store);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "TechFailGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.DX11;
            // Version "99.0" does not exist in sourceDir
            installation.PendingAction = PendingAction.Install("99.0", "Queued install");
            store.Save(installation);

            // Act: ApplyPendingAsync runs
            bool result = await manager.ApplyPendingAsync(exePath);

            // Assert: Technical failure does NOT trigger compatibility cancellation!
            Assert.False(result);
            var instAfter = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfter);
            Assert.NotNull(instAfter.PendingAction); // Retained for retry on technical failure
            Assert.Equal(PendingActionType.Install, instAfter.PendingAction.Type);
            Assert.Null(instAfter.LastCancelledAction);
        }

        [Fact]
        public async Task TechnicalFailure_TransactionEngineError_DoesNotCancelPendingAction()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
            var targetDll = gameDir.CreateFile("d3d11.dll", "original-file");

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            var installation = store.GetOrCreateInstallation(gameDir.RootPath, "LockedFileGame");
            var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
            exeRecord.LastKnownApi = GraphicsApi.DX11;
            installation.PendingAction = PendingAction.Install("2.5", "Queued install");
            store.Save(installation);

            // Lock destination file so that transaction engine copy encounters IOException
            using (var lockedStream = new FileStream(targetDll, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Act: ApplyPendingAsync runs while destination file is locked
                bool result = await manager.ApplyPendingAsync(exePath);
                Assert.False(result);
            }

            // Assert: Transaction failure must NOT terminally cancel the pending action!
            var instAfter = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfter);
            Assert.NotNull(instAfter.PendingAction);
            Assert.Equal(PendingActionType.Install, instAfter.PendingAction.Type);
            Assert.Null(instAfter.LastCancelledAction);
        }

        [Fact]
        public async Task RestoreInvariant_QueuedRestore_NeverCancelled_ExecutesRegardlessOfApi()
        {
            using var gameDir = new SyntheticTestDirectory();
            using var storageDir = new SyntheticTestDirectory();
            using var sourceDir = new SyntheticTestDirectory();

            var (manager, _, store, profileStore, _) = CreateTestEnvironment(storageDir, sourceDir);

            var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
            var nativeD3D11 = gameDir.CreateFile("d3d11.dll", "original-native-d3d11");

            var profile = profileStore.GetOrCreate(exePath);
            profile.Api = GraphicsApi.DX11;
            profile.Architecture = "x64";
            profileStore.Save(profile);

            // Step 1: Deploy DXVK under DX11
            bool deployed = await manager.EnableDxvkAsync(profile, "2.5");
            Assert.True(deployed, $"Initial DXVK deployment under DX11 failed before reaching restore invariant test: LastRefusal='{SanitizeDiagnostic(manager.LastRefusalReason, (gameDir.RootPath, "<gameRoot>"), (storageDir.RootPath, "<storageRoot>"))}'");

            // Step 2: Reclassify to DX12
            profile.Api = GraphicsApi.DX12;
            profileStore.Save(profile);

            var installation = store.FindByInstallationPath(gameDir.RootPath)!;
            installation.FindExecutable("Game.exe")!.LastKnownApi = GraphicsApi.DX12;
            // Queue Restore
            installation.PendingAction = PendingAction.Restore("Queued restore");
            store.Save(installation);

            // Act: ApplyPendingAsync runs for Restore on DX12 game
            bool restoreResult = await manager.ApplyPendingAsync(exePath);
            string restoreDiag = BuildApplyPendingDiagnostic("Restore", restoreResult, manager, store, gameDir.RootPath, storageDir.RootPath, 0);

            // Assert: Restore invariant: MUST NOT be cancelled; MUST execute and restore baseline
            Assert.True(restoreResult, $"Restore failed to execute: {restoreDiag}");
            Assert.False(profile.DxvkEnabled);
            Assert.Equal("original-native-d3d11", File.ReadAllText(nativeD3D11));
            Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));

            var instAfter = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(instAfter);
            Assert.Null(instAfter.PendingAction); // Cleared because restore completed successfully
            Assert.Equal(RestorationState.Restored, instAfter.RestorationState);
        }
    }
}
