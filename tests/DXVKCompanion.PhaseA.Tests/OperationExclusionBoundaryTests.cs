using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class OperationExclusionBoundaryTests
{
    private static void CreateDxvkReleasePayload(string sourceRoot, string version, string d3d11Content, string dxgiContent)
    {
        var archDir = Path.Combine(sourceRoot, version, "x64");
        Directory.CreateDirectory(archDir);
        File.WriteAllText(Path.Combine(archDir, "d3d11.dll"), d3d11Content);
        File.WriteAllText(Path.Combine(archDir, "dxgi.dll"), dxgiContent);
    }

    [Fact]
    public async Task OverlappingOperations_SameInstallation_RollbackUndoingCommittedBytes_MustBeExcluded()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);

        CreateDxvkReleasePayload(sourceDir.RootPath, "2.5", "dxvk-2.5-d3d11", "dxvk-2.5-dxgi");
        CreateDxvkReleasePayload(sourceDir.RootPath, "2.6", "dxvk-2.6-d3d11", "dxvk-2.6-dxgi");
        CreateDxvkReleasePayload(sourceDir.RootPath, "2.7", "dxvk-2.7-d3d11", "dxvk-2.7-dxgi");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "native-original-d3d11");

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        // 1. Baseline: legitimately deploy DXVK 2.5 and capture native backup
        using var http = new HttpClient();
        var initialEngine = new MultiFileTransactionEngine(backupsPath);
        var initialStore = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var initialInstaller = new DxvkInstaller(http, initialEngine, initialStore, sourceDir.RootPath) { LeaseCoordinator = coordinator };
        var release25 = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
        Assert.True(await initialInstaller.ApplyToGameAsync(profile, release25));

        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));
        Assert.Equal("dxvk-2.5-dxgi", File.ReadAllText(Path.Combine(gameDir.RootPath, "dxgi.dll")));

        // Verify native backup was created and holds pristine original bytes
        var initialInst = initialStore.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(initialInst);
        var nativeBackupRecord = initialInst.FindManagedFile("d3d11.dll");
        Assert.NotNull(nativeBackupRecord);
        Assert.NotNull(nativeBackupRecord.BackupRelativePath);
        var nativeBackupFile = Path.Combine(backupsPath, nativeBackupRecord.BackupRelativePath);
        Assert.True(File.Exists(nativeBackupFile), "Native backup file must exist in backups directory.");
        Assert.Equal("native-original-d3d11", File.ReadAllText(nativeBackupFile));

        // 2. Setup concurrency coordination barriers with finite timeouts
        var testTimeout = TimeSpan.FromSeconds(10);
        var op1AppliedD3d11 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var op2Finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Service Graph 1: pauses after mutating d3d11.dll, then fails to trigger recovery
        var hooks1 = new MultiFileTransactionTestHooks
        {
            AfterApply = (targetPath, index) =>
            {
                if (targetPath.EndsWith("d3d11.dll", StringComparison.OrdinalIgnoreCase))
                {
                    op1AppliedD3d11.TrySetResult(true);
                    if (!op2Finished.Task.Wait(testTimeout))
                    {
                        throw new TimeoutException("Timed out waiting for overlapping Operation 2.");
                    }
                    throw new IOException("Simulated fault triggering recovery in Operation 1.");
                }
            }
        };

        var engine1 = new MultiFileTransactionEngine(backupsPath, hooks1);
        var store1 = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var installer1 = new DxvkInstaller(http, engine1, store1, sourceDir.RootPath) { LeaseCoordinator = coordinator };

        // Service Graph 2: independent instance sharing the same target and storage root
        var engine2 = new MultiFileTransactionEngine(backupsPath);
        var store2 = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var installer2 = new DxvkInstaller(http, engine2, store2, sourceDir.RootPath) { LeaseCoordinator = coordinator };

        var release26 = new ReleaseInfo { Version = "2.6", DownloadUrl = "" };
        var release27 = new ReleaseInfo { Version = "2.7", DownloadUrl = "" };

        Task<bool>? op1Task = null;
        Task<bool>? op2Task = null;
        bool op2Result = false;
        bool op1Result = true;

        try
        {
            // 3. Launch Operation 1 (Update to 2.6) in background
            op1Task = Task.Run(async () => await installer1.ApplyToGameAsync(profile, release26));

            // 4. Wait until Operation 1 has applied d3d11.dll (v2.6 bytes now on disk)
            var reached = await Task.WhenAny(op1AppliedD3d11.Task, Task.Delay(testTimeout));
            Assert.True(reached == op1AppliedD3d11.Task, "Operation 1 failed to reach AfterApply hook within timeout.");

            // 5. While Operation 1 holds in-flight mutations, launch Operation 2 (Update to 2.7) with bounded wait
            op2Task = Task.Run(async () => await installer2.ApplyToGameAsync(profile, release27));
            var op2Completed = await Task.WhenAny(op2Task, Task.Delay(testTimeout));
            Assert.True(op2Completed == op2Task, "Operation 2 timed out during execution.");
            op2Result = await op2Task;
        }
        finally
        {
            // Guaranteed barrier release and bounded task draining inside finally to prevent orphaned work on disposed directories
            op2Finished.TrySetResult(true);
            if (op2Task != null)
            {
                var completed2 = await Task.WhenAny(op2Task, Task.Delay(testTimeout));
                if (completed2 == op2Task)
                {
                    op2Result = await op2Task;
                }
            }
            if (op1Task != null)
            {
                var completed1 = await Task.WhenAny(op1Task, Task.Delay(testTimeout));
                if (completed1 == op1Task)
                {
                    op1Result = await op1Task;
                }
            }
        }

        // Verify Operation 1 failed and handled the deliberate injected fault
        Assert.False(op1Result);
        Assert.NotNull(installer1.LastRefusalReason);
        Assert.Contains("Simulated fault", installer1.LastRefusalReason);

        // Intended Safe Invariant:
        // 1. Operation 2 must be refused at the lease boundary while Operation 1 is in-flight.
        Assert.False(op2Result, "Overlapping operation on the same installation root must be refused while another operation holds the exclusion boundary.");
        Assert.NotNull(installer2.LastRefusalReason);
        Assert.Contains("in progress", installer2.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // 2. Because Operation 2 is refused, Operation 1's safe rollback restores the installation to its pre-operation 2.5 baseline.
        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));
        Assert.Equal("dxvk-2.5-dxgi", File.ReadAllText(Path.Combine(gameDir.RootPath, "dxgi.dll")));

        // 3. Reloaded store state confirms the installation remains at 2.5 with its native backup intact and uncorrupted.
        var reloadedStore = new GameLibraryStore(storePath, backupsPath);
        var reloadedInst = reloadedStore.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(reloadedInst);
        Assert.Equal("2.5", reloadedInst.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, reloadedInst.RestorationState);
        Assert.True(File.Exists(nativeBackupFile), "Native backup file must remain preserved after rollback.");
        Assert.Equal("native-original-d3d11", File.ReadAllText(nativeBackupFile));
    }

    [Fact]
    public async Task OverlappingOperations_SameInstallation_ConcurrentApply_MustRefuseSecondOperationToPreventMixedPayload()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);

        CreateDxvkReleasePayload(sourceDir.RootPath, "2.5", "dxvk-2.5-d3d11", "dxvk-2.5-dxgi");
        CreateDxvkReleasePayload(sourceDir.RootPath, "2.6", "dxvk-2.6-d3d11", "dxvk-2.6-dxgi");
        CreateDxvkReleasePayload(sourceDir.RootPath, "2.7", "dxvk-2.7-d3d11", "dxvk-2.7-dxgi");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "native-original-d3d11");

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        // 1. Establish a real committed baseline (2.5) before constructing concurrent service graphs
        using var http = new HttpClient();
        var initialEngine = new MultiFileTransactionEngine(backupsPath);
        var initialStore = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var initialInstaller = new DxvkInstaller(http, initialEngine, initialStore, sourceDir.RootPath) { LeaseCoordinator = coordinator };
        var release25 = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
        Assert.True(await initialInstaller.ApplyToGameAsync(profile, release25));

        var testTimeout = TimeSpan.FromSeconds(10);
        var op1AppliedD3d11 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var op2Finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var hooks1 = new MultiFileTransactionTestHooks
        {
            AfterApply = (targetPath, index) =>
            {
                if (targetPath.EndsWith("d3d11.dll", StringComparison.OrdinalIgnoreCase))
                {
                    op1AppliedD3d11.TrySetResult(true);
                    if (!op2Finished.Task.Wait(testTimeout))
                    {
                        throw new TimeoutException("Timed out waiting for overlapping Operation 2.");
                    }
                }
            }
        };

        var engine1 = new MultiFileTransactionEngine(backupsPath, hooks1);
        var store1 = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var installer1 = new DxvkInstaller(http, engine1, store1, sourceDir.RootPath) { LeaseCoordinator = coordinator };

        var engine2 = new MultiFileTransactionEngine(backupsPath);
        var store2 = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var installer2 = new DxvkInstaller(http, engine2, store2, sourceDir.RootPath) { LeaseCoordinator = coordinator };

        var release26 = new ReleaseInfo { Version = "2.6", DownloadUrl = "" };
        var release27 = new ReleaseInfo { Version = "2.7", DownloadUrl = "" };

        Task<bool>? op1Task = null;
        Task<bool>? op2Task = null;
        bool op2Result = false;
        bool op1Result = false;

        try
        {
            // Launch Op 1 (Update to 2.6)
            op1Task = Task.Run(async () => await installer1.ApplyToGameAsync(profile, release26));

            var reached = await Task.WhenAny(op1AppliedD3d11.Task, Task.Delay(testTimeout));
            Assert.True(reached == op1AppliedD3d11.Task, "Operation 1 failed to reach AfterApply hook.");

            // While Op 1 has partially mutated the installation, Op 2 attempts to update to 2.7
            op2Task = Task.Run(async () => await installer2.ApplyToGameAsync(profile, release27));
            var op2Completed = await Task.WhenAny(op2Task, Task.Delay(testTimeout));
            Assert.True(op2Completed == op2Task, "Operation 2 timed out during execution.");
            op2Result = await op2Task;
        }
        finally
        {
            // Guaranteed barrier release and bounded task draining inside finally
            op2Finished.TrySetResult(true);
            if (op2Task != null)
            {
                var completed2 = await Task.WhenAny(op2Task, Task.Delay(testTimeout));
                if (completed2 == op2Task)
                {
                    op2Result = await op2Task;
                }
            }
            if (op1Task != null)
            {
                var completed1 = await Task.WhenAny(op1Task, Task.Delay(testTimeout));
                if (completed1 == op1Task)
                {
                    op1Result = await op1Task;
                }
            }
        }

        // Intended Safe Invariant:
        // Concurrent mutations to the same installation root must be mutually excluded at the operation boundary.
        // Operation 2 must be cleanly refused with an "in progress" reason before attempting mutation.
        Assert.False(op2Result, "Concurrent install/update on the same installation root must be refused while another operation is in flight.");
        Assert.NotNull(installer2.LastRefusalReason);
        Assert.Contains("in progress", installer2.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Operation 1 completes its update to 2.6 safely without interference from refused Operation 2.
        Assert.True(op1Result);
        Assert.Equal("dxvk-2.6-d3d11", File.ReadAllText(d3d11Path));
        Assert.Equal("dxvk-2.6-dxgi", File.ReadAllText(Path.Combine(gameDir.RootPath, "dxgi.dll")));
    }

    [Fact]
    public async Task OverlappingOperations_SameInstallation_ManagerLevel_ConcurrentDisableAndEnable_MustBeExcluded()
    {
        // Characterization Note: On unchanged production, Manager 2 enters rollback execution and halts when
        // mid-transaction target hash mismatch is detected, flagging AttentionRequired. Under the proposed contract,
        // Manager 2 must be cleanly refused at the entry lease boundary with an "in progress" diagnostic before touching disk.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);

        CreateDxvkReleasePayload(sourceDir.RootPath, "2.5", "dxvk-2.5-d3d11", "dxvk-2.5-dxgi");
        CreateDxvkReleasePayload(sourceDir.RootPath, "2.6", "dxvk-2.6-d3d11", "dxvk-2.6-dxgi");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var profilesPath = Path.Combine(storageDir.RootPath, "profiles.json");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "native-original-d3d11");

        using var http = new HttpClient();
        var initialEngine = new MultiFileTransactionEngine(backupsPath);
        var initialStore = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var initialProfileStore = new ProfileStore(profilesPath) { LeaseCoordinator = coordinator };
        var initialInstaller = new DxvkInstaller(http, initialEngine, initialStore, sourceDir.RootPath) { LeaseCoordinator = coordinator };
        var initialRollback = new DxvkRollback(initialEngine, initialStore) { LeaseCoordinator = coordinator };
        var initialManager = new DxvkManager(initialInstaller, initialRollback, new DxvkGithubClient(http, new CacheStore()), initialProfileStore, initialStore, coordinator);

        var profile = initialProfileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        initialProfileStore.Save(profile);

        // Initial install: 2.5
        var release25 = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
        Assert.True(await initialInstaller.ApplyToGameAsync(profile, release25));
        profile.DxvkEnabled = true;
        profile.DxvkVersion = "2.5";
        initialProfileStore.Save(profile);

        var testTimeout = TimeSpan.FromSeconds(10);
        var manager1AppliedD3d11 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager2Finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var hooks1 = new MultiFileTransactionTestHooks
        {
            AfterApply = (targetPath, index) =>
            {
                if (targetPath.EndsWith("d3d11.dll", StringComparison.OrdinalIgnoreCase))
                {
                    manager1AppliedD3d11.TrySetResult(true);
                    if (!manager2Finished.Task.Wait(testTimeout))
                    {
                        throw new TimeoutException("Timed out waiting for Manager 2.");
                    }
                }
            }
        };

        var engine1 = new MultiFileTransactionEngine(backupsPath, hooks1);
        var store1 = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var profileStore1 = new ProfileStore(profilesPath) { LeaseCoordinator = coordinator };
        var installer1 = new DxvkInstaller(http, engine1, store1, sourceDir.RootPath) { LeaseCoordinator = coordinator };
        var rollback1 = new DxvkRollback(engine1, store1) { LeaseCoordinator = coordinator };
        var manager1 = new DxvkManager(installer1, rollback1, new DxvkGithubClient(http, new CacheStore()), profileStore1, store1, coordinator);

        var engine2 = new MultiFileTransactionEngine(backupsPath);
        var store2 = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var profileStore2 = new ProfileStore(profilesPath) { LeaseCoordinator = coordinator };
        var installer2 = new DxvkInstaller(http, engine2, store2, sourceDir.RootPath) { LeaseCoordinator = coordinator };
        var rollback2 = new DxvkRollback(engine2, store2) { LeaseCoordinator = coordinator };
        var manager2 = new DxvkManager(installer2, rollback2, new DxvkGithubClient(http, new CacheStore()), profileStore2, store2, coordinator);

        Task<bool>? m1Task = null;
        Task<bool>? m2Task = null;
        bool m2Result = false;
        bool m1Result = false;

        try
        {
            // Launch Manager 1 Enable/Update to 2.6
            m1Task = Task.Run(async () => await manager1.EnableDxvkAsync(profile, "2.6"));

            var reached = await Task.WhenAny(manager1AppliedD3d11.Task, Task.Delay(testTimeout));
            Assert.True(reached == manager1AppliedD3d11.Task, "Manager 1 failed to reach AfterApply hook.");

            // While Manager 1 is in-flight, Manager 2 attempts DisableDxvkAsync on the same profile
            m2Task = Task.Run(async () => await manager2.DisableDxvkAsync(profile));
            var m2Completed = await Task.WhenAny(m2Task, Task.Delay(testTimeout));
            Assert.True(m2Completed == m2Task, "Manager 2 timed out during execution.");
            m2Result = await m2Task;
        }
        finally
        {
            // Guaranteed barrier release and bounded task draining inside finally
            manager2Finished.TrySetResult(true);
            if (m2Task != null)
            {
                var completed2 = await Task.WhenAny(m2Task, Task.Delay(testTimeout));
                if (completed2 == m2Task)
                {
                    m2Result = await m2Task;
                }
            }
            if (m1Task != null)
            {
                var completed1 = await Task.WhenAny(m1Task, Task.Delay(testTimeout));
                if (completed1 == m1Task)
                {
                    m1Result = await m1Task;
                }
            }
        }

        // Intended Safe Invariant:
        // Manager 2 must refuse the operation before entering rollback execution because Manager 1 holds an active operation lease.
        // It must cleanly refuse with an "in progress" diagnostic rather than proceeding to inspect disk and failing mid-flight with a hash mismatch.
        Assert.False(m2Result, "Concurrent manager operation on the same game profile must be refused while another operation is in flight.");
        Assert.NotNull(manager2.LastRefusalReason);
        Assert.Contains("in progress", manager2.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Manager 1 completes successfully
        Assert.True(m1Result);
    }

    [Fact]
    public async Task OverlappingOperations_IndependentInstallations_ProceedConcurrentlyWithoutMutualInterference()
    {
        using var gameDir1 = new SyntheticTestDirectory();
        using var gameDir2 = new SyntheticTestDirectory();
        using var storageDir1 = new SyntheticTestDirectory();
        using var storageDir2 = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);

        CreateDxvkReleasePayload(sourceDir.RootPath, "2.5", "dxvk-2.5-d3d11", "dxvk-2.5-dxgi");
        CreateDxvkReleasePayload(sourceDir.RootPath, "2.6", "dxvk-2.6-d3d11", "dxvk-2.6-dxgi");

        var storePath1 = Path.Combine(storageDir1.RootPath, "game-library.json");
        var backupsPath1 = Path.Combine(storageDir1.RootPath, "backups");
        var storePath2 = Path.Combine(storageDir2.RootPath, "game-library.json");
        var backupsPath2 = Path.Combine(storageDir2.RootPath, "backups");

        var exe1 = gameDir1.CreateFile("Game1.exe", "binary-1");
        var exe2 = gameDir2.CreateFile("Game2.exe", "binary-2");

        var profile1 = new GameProfile(exe1) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var profile2 = new GameProfile(exe2) { Api = GraphicsApi.DX11, Architecture = "x64" };

        var testTimeout = TimeSpan.FromSeconds(10);
        var op1AppliedD3d11 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var op2AppliedD3d11 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var testFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Hook for Installation 1: pauses after applying d3d11.dll until Installation 2 also reaches d3d11.dll
        var hooks1 = new MultiFileTransactionTestHooks
        {
            AfterApply = (targetPath, index) =>
            {
                if (targetPath.EndsWith("d3d11.dll", StringComparison.OrdinalIgnoreCase))
                {
                    op1AppliedD3d11.TrySetResult(true);
                    if (!Task.WaitAny(new[] { op2AppliedD3d11.Task, testFinished.Task }, testTimeout).Equals(0))
                    {
                        throw new TimeoutException("Installation 1 timed out waiting for concurrent Installation 2 to reach AfterApply.");
                    }
                }
            }
        };

        // Hook for Installation 2: pauses after applying d3d11.dll until Installation 1 also reaches d3d11.dll
        var hooks2 = new MultiFileTransactionTestHooks
        {
            AfterApply = (targetPath, index) =>
            {
                if (targetPath.EndsWith("d3d11.dll", StringComparison.OrdinalIgnoreCase))
                {
                    op2AppliedD3d11.TrySetResult(true);
                    if (!Task.WaitAny(new[] { op1AppliedD3d11.Task, testFinished.Task }, testTimeout).Equals(0))
                    {
                        throw new TimeoutException("Installation 2 timed out waiting for concurrent Installation 1 to reach AfterApply.");
                    }
                }
            }
        };

        using var http = new HttpClient();
        var store1 = new GameLibraryStore(storePath1, backupsPath1) { LeaseCoordinator = coordinator };
        var engine1 = new MultiFileTransactionEngine(backupsPath1, hooks1);
        var installer1 = new DxvkInstaller(http, engine1, store1, sourceDir.RootPath) { LeaseCoordinator = coordinator };

        var store2 = new GameLibraryStore(storePath2, backupsPath2) { LeaseCoordinator = coordinator };
        var engine2 = new MultiFileTransactionEngine(backupsPath2, hooks2);
        var installer2 = new DxvkInstaller(http, engine2, store2, sourceDir.RootPath) { LeaseCoordinator = coordinator };

        var release25 = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
        var release26 = new ReleaseInfo { Version = "2.6", DownloadUrl = "" };

        Task<bool>? task1 = null;
        Task<bool>? task2 = null;
        try
        {
            // Act: Run both independent installs concurrently
            task1 = Task.Run(async () => await installer1.ApplyToGameAsync(profile1, release25));
            task2 = Task.Run(async () => await installer2.ApplyToGameAsync(profile2, release26));

            var bothReached = await Task.WhenAll(
                Task.WhenAny(op1AppliedD3d11.Task, Task.Delay(testTimeout)),
                Task.WhenAny(op2AppliedD3d11.Task, Task.Delay(testTimeout))
            );

            Assert.True(bothReached[0] == op1AppliedD3d11.Task, "Installation 1 failed to reach AfterApply handshake.");
            Assert.True(bothReached[1] == op2AppliedD3d11.Task, "Installation 2 failed to reach AfterApply handshake.");

            var allTasks = Task.WhenAll(task1, task2);
            var completed = await Task.WhenAny(allTasks, Task.Delay(testTimeout));
            Assert.Same(allTasks, completed);
        }
        finally
        {
            testFinished.TrySetResult(true);
            if (task1 != null)
            {
                await Task.WhenAny(task1, Task.Delay(TimeSpan.FromSeconds(5)));
            }
            if (task2 != null)
            {
                await Task.WhenAny(task2, Task.Delay(TimeSpan.FromSeconds(5)));
            }
        }

        // Assert: Both succeed independently without mutual interference
        Assert.NotNull(task1);
        Assert.NotNull(task2);
        Assert.True(await task1);
        Assert.True(await task2);

        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(Path.Combine(gameDir1.RootPath, "d3d11.dll")));
        Assert.Equal("dxvk-2.6-d3d11", File.ReadAllText(Path.Combine(gameDir2.RootPath, "d3d11.dll")));
    }

    [Fact]
    public async Task RootVsSubdirectoryCollision_AcrossSeparateStores_DetectedAndRefused()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir1 = new SyntheticTestDirectory();
        using var storageDir2 = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);

        CreateDxvkReleasePayload(sourceDir.RootPath, "2.5", "dxvk-2.5-d3d11", "dxvk-2.5-dxgi");

        var storePath1 = Path.Combine(storageDir1.RootPath, "game-library.json");
        var backupsPath1 = Path.Combine(storageDir1.RootPath, "backups");
        var storePath2 = Path.Combine(storageDir2.RootPath, "game-library.json");
        var backupsPath2 = Path.Combine(storageDir2.RootPath, "backups");

        // Root installation: gameDir.RootPath
        var rootExe = gameDir.CreateFile("RootGame.exe", "root-binary");
        // Subdirectory installation: gameDir.RootPath\bin\x64
        var subDirPath = Path.Combine(gameDir.RootPath, "bin", "x64");
        Directory.CreateDirectory(subDirPath);
        var subExe = Path.Combine(subDirPath, "SubGame.exe");
        File.WriteAllText(subExe, "sub-binary");

        var rootProfile = new GameProfile(rootExe) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var subProfile = new GameProfile(subExe) { Api = GraphicsApi.DX11, Architecture = "x64" };

        var testTimeout = TimeSpan.FromSeconds(10);
        var rootAppliedD3d11 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var rootHooks = new MultiFileTransactionTestHooks
        {
            AfterApply = (targetPath, index) =>
            {
                if (targetPath.EndsWith("d3d11.dll", StringComparison.OrdinalIgnoreCase))
                {
                    rootAppliedD3d11.TrySetResult(true);
                    if (!subFinished.Task.Wait(testTimeout))
                    {
                        throw new TimeoutException("Timed out waiting for sub-operation.");
                    }
                }
            }
        };

        using var http = new HttpClient();
        var store1 = new GameLibraryStore(storePath1, backupsPath1) { LeaseCoordinator = coordinator };
        var engine1 = new MultiFileTransactionEngine(backupsPath1, rootHooks);
        var installer1 = new DxvkInstaller(http, engine1, store1, sourceDir.RootPath) { LeaseCoordinator = coordinator };

        var store2 = new GameLibraryStore(storePath2, backupsPath2) { LeaseCoordinator = coordinator };
        var engine2 = new MultiFileTransactionEngine(backupsPath2);
        var installer2 = new DxvkInstaller(http, engine2, store2, sourceDir.RootPath) { LeaseCoordinator = coordinator };

        var release25 = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        Task<bool>? rootTask = null;
        Task<bool>? subTask = null;
        bool subResult = false;
        bool rootResult = false;

        try
        {
            rootTask = Task.Run(async () => await installer1.ApplyToGameAsync(rootProfile, release25));

            var reached = await Task.WhenAny(rootAppliedD3d11.Task, Task.Delay(testTimeout));
            Assert.Same(rootAppliedD3d11.Task, reached);

            // While Root holds in-flight operation, Sub attempts install on bin\x64 with its own store
            subTask = Task.Run(async () => await installer2.ApplyToGameAsync(subProfile, release25));
            var subCompleted = await Task.WhenAny(subTask, Task.Delay(testTimeout));
            Assert.Same(subTask, subCompleted);
            subResult = await subTask;
        }
        finally
        {
            subFinished.TrySetResult(true);
            if (subTask != null)
            {
                var drain = await Task.WhenAny(subTask, Task.Delay(TimeSpan.FromSeconds(5)));
                if (drain == subTask) subResult = await subTask;
            }
            if (rootTask != null)
            {
                var drain = await Task.WhenAny(rootTask, Task.Delay(TimeSpan.FromSeconds(5)));
                if (drain == rootTask) rootResult = await rootTask;
            }
        }

        // Sub install must be refused because of target ancestor/descendant collision, despite using separate store files
        Assert.False(subResult);
        Assert.NotNull(installer2.LastRefusalReason);
        Assert.Contains("in progress", installer2.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Root succeeds
        Assert.True(rootResult);
    }
}
