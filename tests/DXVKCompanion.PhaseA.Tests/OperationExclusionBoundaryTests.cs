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

        // 1. Baseline: legitimately deploy DXVK 2.5
        using var http = new HttpClient();
        var initialEngine = new MultiFileTransactionEngine(backupsPath);
        var initialStore = new GameLibraryStore(storePath, backupsPath);
        var initialInstaller = new DxvkInstaller(http, initialEngine, initialStore, sourceDir.RootPath);
        var release25 = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
        Assert.True(await initialInstaller.ApplyToGameAsync(profile, release25));

        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));
        Assert.Equal("dxvk-2.5-dxgi", File.ReadAllText(Path.Combine(gameDir.RootPath, "dxgi.dll")));

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
        var store1 = new GameLibraryStore(storePath, backupsPath);
        var installer1 = new DxvkInstaller(http, engine1, store1, sourceDir.RootPath);

        // Service Graph 2: independent instance sharing the same target and storage root
        var engine2 = new MultiFileTransactionEngine(backupsPath);
        var store2 = new GameLibraryStore(storePath, backupsPath);
        var installer2 = new DxvkInstaller(http, engine2, store2, sourceDir.RootPath);

        var release26 = new ReleaseInfo { Version = "2.6", DownloadUrl = "" };
        var release27 = new ReleaseInfo { Version = "2.7", DownloadUrl = "" };

        // 3. Launch Operation 1 (Update to 2.6) in background
        var op1Task = Task.Run(async () =>
        {
            try
            {
                return await installer1.ApplyToGameAsync(profile, release26);
            }
            catch
            {
                return false;
            }
        });

        // 4. Wait until Operation 1 has applied d3d11.dll (v2.6 bytes now on disk)
        var reached = await Task.WhenAny(op1AppliedD3d11.Task, Task.Delay(testTimeout));
        Assert.True(reached == op1AppliedD3d11.Task, "Operation 1 failed to reach AfterApply hook within timeout.");

        // 5. While Operation 1 holds in-flight mutations, launch Operation 2 (Update to 2.7)
        bool op2Result;
        try
        {
            op2Result = await installer2.ApplyToGameAsync(profile, release27);
        }
        finally
        {
            op2Finished.TrySetResult(true);
        }

        var op1Result = await op1Task;
        Assert.False(op1Result); // Op 1 failed and rolled back

        // 6. Assert intended safety invariants:
        // Invariant A: Overlapping Operation 2 on the same installation root must be refused while Op 1 holds the exclusion boundary.
        Assert.False(op2Result, "Overlapping operation on the same installation root must be refused while another operation holds the exclusion boundary.");

        // Invariant B: Even if Operation 2 committed, an overlapping operation's rollback must NEVER
        // silently overwrite another operation's committed bytes on disk.
        var finalD3d11 = File.ReadAllText(d3d11Path);
        Assert.Equal("dxvk-2.7-d3d11", finalD3d11);
    }

    [Fact]
    public async Task OverlappingOperations_SameInstallation_ConcurrentApply_MustRefuseSecondOperationToPreventMixedPayload()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        CreateDxvkReleasePayload(sourceDir.RootPath, "2.5", "dxvk-2.5-d3d11", "dxvk-2.5-dxgi");
        CreateDxvkReleasePayload(sourceDir.RootPath, "2.6", "dxvk-2.6-d3d11", "dxvk-2.6-dxgi");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "native-original-d3d11");

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        using var http = new HttpClient();
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
        var store1 = new GameLibraryStore(storePath, backupsPath);
        var installer1 = new DxvkInstaller(http, engine1, store1, sourceDir.RootPath);

        var engine2 = new MultiFileTransactionEngine(backupsPath);
        var store2 = new GameLibraryStore(storePath, backupsPath);
        var installer2 = new DxvkInstaller(http, engine2, store2, sourceDir.RootPath);

        var release25 = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
        var release26 = new ReleaseInfo { Version = "2.6", DownloadUrl = "" };

        // Launch Op 1 (Install 2.5)
        var op1Task = Task.Run(async () => await installer1.ApplyToGameAsync(profile, release25));

        var reached = await Task.WhenAny(op1AppliedD3d11.Task, Task.Delay(testTimeout));
        Assert.True(reached == op1AppliedD3d11.Task, "Operation 1 failed to reach AfterApply hook.");

        // While Op 1 has partially mutated the installation, Op 2 attempts to install 2.6
        bool op2Result;
        try
        {
            op2Result = await installer2.ApplyToGameAsync(profile, release26);
        }
        finally
        {
            op2Finished.TrySetResult(true);
        }

        var op1Result = await op1Task;

        // Intended Safe Invariant:
        // Concurrent mutations to the same installation root must be mutually excluded to prevent mixed file payloads
        // and must refuse with a clean operation-in-progress reason rather than a mid-mutation validation error.
        Assert.False(op2Result, "Concurrent install/update on the same installation root must be refused while another operation is in flight.");
        Assert.NotNull(installer2.LastRefusalReason);
        Assert.Contains("in progress", installer2.LastRefusalReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverlappingOperations_SameInstallation_ManagerLevel_ConcurrentDisableAndEnable_MustBeExcluded()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        CreateDxvkReleasePayload(sourceDir.RootPath, "2.5", "dxvk-2.5-d3d11", "dxvk-2.5-dxgi");
        CreateDxvkReleasePayload(sourceDir.RootPath, "2.6", "dxvk-2.6-d3d11", "dxvk-2.6-dxgi");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var profilesPath = Path.Combine(storageDir.RootPath, "profiles.json");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "native-original-d3d11");

        using var http = new HttpClient();
        var initialEngine = new MultiFileTransactionEngine(backupsPath);
        var initialStore = new GameLibraryStore(storePath, backupsPath);
        var initialProfileStore = new ProfileStore(profilesPath);
        var initialInstaller = new DxvkInstaller(http, initialEngine, initialStore, sourceDir.RootPath);
        var initialRollback = new DxvkRollback(initialEngine, initialStore);
        var initialManager = new DxvkManager(initialInstaller, initialRollback, new DxvkGithubClient(http, new CacheStore()), initialProfileStore, initialStore);

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
        var store1 = new GameLibraryStore(storePath, backupsPath);
        var profileStore1 = new ProfileStore(profilesPath);
        var installer1 = new DxvkInstaller(http, engine1, store1, sourceDir.RootPath);
        var rollback1 = new DxvkRollback(engine1, store1);
        var manager1 = new DxvkManager(installer1, rollback1, new DxvkGithubClient(http, new CacheStore()), profileStore1, store1);

        var engine2 = new MultiFileTransactionEngine(backupsPath);
        var store2 = new GameLibraryStore(storePath, backupsPath);
        var profileStore2 = new ProfileStore(profilesPath);
        var installer2 = new DxvkInstaller(http, engine2, store2, sourceDir.RootPath);
        var rollback2 = new DxvkRollback(engine2, store2);
        var manager2 = new DxvkManager(installer2, rollback2, new DxvkGithubClient(http, new CacheStore()), profileStore2, store2);

        // Launch Manager 1 Enable/Update to 2.6
        var m1Task = Task.Run(async () => await manager1.EnableDxvkAsync(profile, "2.6"));

        var reached = await Task.WhenAny(manager1AppliedD3d11.Task, Task.Delay(testTimeout));
        Assert.True(reached == manager1AppliedD3d11.Task, "Manager 1 failed to reach AfterApply hook.");

        // While Manager 1 is in-flight, Manager 2 attempts DisableDxvkAsync on the same profile
        bool m2Result;
        try
        {
            m2Result = await manager2.DisableDxvkAsync(profile);
        }
        finally
        {
            manager2Finished.TrySetResult(true);
        }

        var m1Result = await m1Task;

        // Intended Safe Invariant:
        // Manager 2 must refuse operation because Manager 1 holds an active operation on this installation root.
        Assert.False(m2Result, "Concurrent manager operation on the same game profile must be refused while another operation is in flight.");
        Assert.NotNull(manager2.LastRefusalReason);
        Assert.Contains("in progress", manager2.LastRefusalReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverlappingOperations_IndependentInstallations_ProceedConcurrentlyWithoutMutualInterference()
    {
        using var gameDir1 = new SyntheticTestDirectory();
        using var gameDir2 = new SyntheticTestDirectory();
        using var storageDir1 = new SyntheticTestDirectory();
        using var storageDir2 = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

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

        using var http = new HttpClient();
        var store1 = new GameLibraryStore(storePath1, backupsPath1);
        var engine1 = new MultiFileTransactionEngine(backupsPath1);
        var installer1 = new DxvkInstaller(http, engine1, store1, sourceDir.RootPath);

        var store2 = new GameLibraryStore(storePath2, backupsPath2);
        var engine2 = new MultiFileTransactionEngine(backupsPath2);
        var installer2 = new DxvkInstaller(http, engine2, store2, sourceDir.RootPath);

        var release25 = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
        var release26 = new ReleaseInfo { Version = "2.6", DownloadUrl = "" };

        // Act: Run both independent installs concurrently
        var task1 = Task.Run(async () => await installer1.ApplyToGameAsync(profile1, release25));
        var task2 = Task.Run(async () => await installer2.ApplyToGameAsync(profile2, release26));

        await Task.WhenAll(task1, task2);

        // Assert: Both succeed independently without interference
        Assert.True(await task1);
        Assert.True(await task2);

        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(Path.Combine(gameDir1.RootPath, "d3d11.dll")));
        Assert.Equal("dxvk-2.6-d3d11", File.ReadAllText(Path.Combine(gameDir2.RootPath, "d3d11.dll")));
    }
}
