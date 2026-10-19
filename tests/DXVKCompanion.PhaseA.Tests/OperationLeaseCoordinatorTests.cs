using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.Safety;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class OperationLeaseCoordinatorTests
{
    [Fact]
    public async Task TryAcquireOperationScopeAsync_SingleTarget_AcquiresSuccessfully()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "GameFolder");
        Directory.CreateDirectory(targetPath);

        await using var lease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(500));

        Assert.NotNull(lease);
        Assert.True(lease.IsAcquired);
        Assert.False(lease.IsDisposed);
        Assert.True(lease.IsValidForTarget(targetPath));
    }

    [Fact]
    public void TryAcquireOperationScope_Sync_AcquiresSuccessfully()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "GameFolder");
        Directory.CreateDirectory(targetPath);

        using var lease = coordinator.TryAcquireOperationScope(targetPath, timeout: TimeSpan.FromMilliseconds(500));

        Assert.NotNull(lease);
        Assert.True(lease.IsAcquired);
        Assert.False(lease.IsDisposed);
        Assert.True(lease.IsValidForTarget(targetPath));
    }

    [Fact]
    public async Task TryAcquireOperationScopeAsync_Contention_ReturnsNullAfterBudget()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "GameFolder");
        Directory.CreateDirectory(targetPath);

        await using var lease1 = await coordinator.TryAcquireOperationScopeAsync(targetPath);
        Assert.NotNull(lease1);

        var sw = Stopwatch.StartNew();
        await using var lease2 = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(200));
        sw.Stop();

        Assert.Null(lease2);
        Assert.True(sw.ElapsedMilliseconds >= 150, $"Expected timeout around 200 ms, actual: {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task LeaseDisposal_ReleasesLockForSubsequentAcquisition()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "GameFolder");
        Directory.CreateDirectory(targetPath);

        var lease1 = await coordinator.TryAcquireOperationScopeAsync(targetPath);
        Assert.NotNull(lease1);
        Assert.True(lease1.IsAcquired);

        await lease1.DisposeAsync();
        Assert.True(lease1.IsDisposed);
        Assert.False(lease1.IsAcquired);

        // Immediate subsequent acquisition must succeed
        await using var lease2 = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(100));
        Assert.NotNull(lease2);
        Assert.True(lease2.IsAcquired);
    }

    [Fact]
    public async Task TwoTierCoordination_TargetAndStore_LockOrderingAndDisposal()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "GameFolder");
        var storePath = Path.Combine(tempDir.RootPath, "game-library.json");
        Directory.CreateDirectory(targetPath);
        File.WriteAllText(storePath, "{}");

        await using var lease = await coordinator.TryAcquireOperationScopeAsync(targetPath, storePath, timeout: TimeSpan.FromMilliseconds(500));
        Assert.NotNull(lease);
        Assert.True(lease.IsAcquired);
        Assert.True(lease.IsValidForTarget(targetPath));
        Assert.True(lease.IsValidForStore(storePath));
        Assert.NotNull(lease.StoreLease);
        Assert.True(lease.StoreLease.IsValidForStore(storePath));

        // Competing store-only lease must be refused while store lease is held
        await using var storeConflict = await coordinator.TryAcquireStoreLeaseAsync(storePath, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Null(storeConflict);

        // Competing target lease must also be refused
        await using var targetConflict = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Null(targetConflict);
    }

    [Fact]
    public async Task DifferentTarget_SharedStore_CompetesForStoreOnly()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var target1 = Path.Combine(tempDir.RootPath, "Game1");
        var target2 = Path.Combine(tempDir.RootPath, "Game2");
        var sharedStore = Path.Combine(tempDir.RootPath, "game-library.json");
        Directory.CreateDirectory(target1);
        Directory.CreateDirectory(target2);
        File.WriteAllText(sharedStore, "{}");

        await using var lease1 = await coordinator.TryAcquireOperationScopeAsync(target1, sharedStore, timeout: TimeSpan.FromMilliseconds(500));
        Assert.NotNull(lease1);

        // Target 2 can acquire target-only lease
        await using (var leaseTarget2Only = await coordinator.TryAcquireOperationScopeAsync(target2, storePath: null, timeout: TimeSpan.FromMilliseconds(100)))
        {
            Assert.NotNull(leaseTarget2Only);
        }

        // Target 2 is now free, but cannot acquire scope with the shared store while lease1 holds it
        await using var leaseTarget2Store = await coordinator.TryAcquireOperationScopeAsync(target2, sharedStore, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Null(leaseTarget2Store);
    }

    [Fact]
    public async Task CanonicalPath_CaseAndTrailingSlashNormalization_MapsToSameLock()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetLower = Path.Combine(tempDir.RootPath, "gamefolder");
        var targetUpperWithSlash = Path.Combine(tempDir.RootPath, "GAMEFOLDER") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(targetLower);

        await using var lease1 = await coordinator.TryAcquireOperationScopeAsync(targetLower);
        Assert.NotNull(lease1);

        // Upper-case with trailing slash must resolve to identical lock identity and be refused
        await using var lease2 = await coordinator.TryAcquireOperationScopeAsync(targetUpperWithSlash, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Null(lease2);
    }

    [Fact]
    public void TokenValidation_DisposedOrMismatchedToken_FailsValidation()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var target1 = Path.Combine(tempDir.RootPath, "Game1");
        var target2 = Path.Combine(tempDir.RootPath, "Game2");
        Directory.CreateDirectory(target1);
        Directory.CreateDirectory(target2);

        var lease = coordinator.TryAcquireOperationScope(target1);
        Assert.NotNull(lease);
        Assert.True(lease.IsValidForTarget(target1));
        Assert.False(lease.IsValidForTarget(target2));

        lease.Dispose();
        Assert.False(lease.IsValidForTarget(target1));
        Assert.True(lease.IsDisposed);
    }

    [Fact]
    public void TokenValidation_RoleAndKind_Enforced()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var target = Path.Combine(tempDir.RootPath, "Game");
        var store = Path.Combine(tempDir.RootPath, "store.json");
        Directory.CreateDirectory(target);
        File.WriteAllText(store, "{}");

        using (var targetOnlyLease = coordinator.TryAcquireOperationScope(target))
        {
            Assert.NotNull(targetOnlyLease);
            Assert.Equal(OperationLeaseKind.Target, targetOnlyLease.Kind);
            Assert.True(targetOnlyLease.IsValidForTarget(target));
            Assert.False(targetOnlyLease.IsValidForStore(store));
        }

        using (var storeOnlyLease = coordinator.TryAcquireStoreLease(store))
        {
            Assert.NotNull(storeOnlyLease);
            Assert.Equal(OperationLeaseKind.Store, storeOnlyLease.Kind);
            Assert.False(storeOnlyLease.IsValidForTarget(target));
            Assert.True(storeOnlyLease.IsValidForStore(store));
        }

        using (var compositeLease = coordinator.TryAcquireOperationScope(target, store))
        {
            Assert.NotNull(compositeLease);
            Assert.Equal(OperationLeaseKind.Composite, compositeLease.Kind);
            Assert.True(compositeLease.IsValidForTarget(target));
            Assert.True(compositeLease.IsValidForStore(store));
            Assert.NotNull(compositeLease.StoreLease);
            Assert.Equal(OperationLeaseKind.Store, compositeLease.StoreLease.Kind);
        }
    }

    [Fact]
    public async Task PartialAcquisitionFailure_DisposesPriorAcquisitions()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var target = Path.Combine(tempDir.RootPath, "Game");
        var store1 = Path.Combine(tempDir.RootPath, "store1.json");
        var store2 = Path.Combine(tempDir.RootPath, "store2.json");
        Directory.CreateDirectory(target);
        File.WriteAllText(store1, "{}");
        File.WriteAllText(store2, "{}");

        // Pre-lock store2 with an independent store lease
        var blockerCoordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        using var store2Lock = blockerCoordinator.TryAcquireStoreLease(store2);
        Assert.NotNull(store2Lock);

        // Attempt multi-store acquisition covering target, store1, and store2
        var lease = await coordinator.TryAcquireOperationScopeAsync(
            target,
            new[] { store1, store2 },
            timeout: TimeSpan.FromMilliseconds(100));

        // Must return null
        Assert.Null(lease);

        // Target and store1 must NOT remain locked; another coordinator can acquire them immediately
        using var targetRetry = coordinator.TryAcquireOperationScope(target, store1, timeout: TimeSpan.FromMilliseconds(100));
        Assert.NotNull(targetRetry);
        Assert.True(targetRetry.IsAcquired);
    }

    [Fact]
    public async Task AncestorDescendantTargetCollision_DetectedAndRefused()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var root = Path.Combine(tempDir.RootPath, "GameRoot");
        var subDir = Path.Combine(root, "bin", "x64");
        Directory.CreateDirectory(subDir);

        // Lock root
        await using var rootLease = await coordinator.TryAcquireOperationScopeAsync(root);
        Assert.NotNull(rootLease);

        // Attempting to lock subdirectory must be detected as descendant collision and refused
        await using var subLease = await coordinator.TryAcquireOperationScopeAsync(subDir, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Null(subLease);

        // Release root
        await rootLease.DisposeAsync();

        // Lock subdirectory
        await using var subLease2 = await coordinator.TryAcquireOperationScopeAsync(subDir);
        Assert.NotNull(subLease2);

        // Attempting to lock root must be detected as ancestor collision and refused
        await using var rootLease2 = await coordinator.TryAcquireOperationScopeAsync(root, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Null(rootLease2);
    }

    [Fact]
    public void DeterministicMultiStoreLockOrdering_PreventsDeadlock()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var target = Path.Combine(tempDir.RootPath, "Game");
        var storeA = Path.Combine(tempDir.RootPath, "a_store.json");
        var storeB = Path.Combine(tempDir.RootPath, "b_store.json");
        Directory.CreateDirectory(target);
        File.WriteAllText(storeA, "{}");
        File.WriteAllText(storeB, "{}");

        // Pass stores in reverse order [B, A] with duplicates
        using var lease = coordinator.TryAcquireOperationScope(target, new[] { storeB, storeA, storeB });
        Assert.NotNull(lease);
        Assert.True(lease.IsValidForStore(storeA));
        Assert.True(lease.IsValidForStore(storeB));
    }

    [Fact]
    public async Task InterProcessLocking_OwnedCloudHelper_ExcludesParentDuringHold()
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            return;

        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "HelperTarget");
        Directory.CreateDirectory(targetPath);

        string canonicalTarget = OperationResourceIdentity.ResolveCanonicalPath(targetPath);
        string lockName = OperationResourceIdentity.GetTargetLockFileName(canonicalTarget);
        string fullLockPath = Path.Combine(coordDir.RootPath, lockName);

        // Prepare child helper process: holds lock exclusively for 2000 ms using DeleteOnClose
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"$f = new-object System.IO.FileStream('{fullLockPath.Replace("'", "''")}', [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None, 4096, [System.IO.FileOptions]::DeleteOnClose); [Console]::Out.WriteLine('LOCKED'); [Console]::Out.Flush(); Start-Sleep -Milliseconds 2000; $f.Dispose(); exit 0\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process? helper = null;
        using var hardTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            helper = Process.Start(psi);
            Assert.NotNull(helper);

            // Wait for helper to confirm lock acquisition via stdout
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(hardTimeoutCts.Token);
            readCts.CancelAfter(TimeSpan.FromSeconds(10));
            string? signal = await helper.StandardOutput.ReadLineAsync(readCts.Token);
            Assert.Equal("LOCKED", signal);

            // Parent attempt to acquire with 200 ms budget must time out and return null
            var sw = Stopwatch.StartNew();
            await using var refusedLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(200));
            sw.Stop();

            Assert.Null(refusedLease);
            Assert.True(sw.ElapsedMilliseconds >= 150, $"Expected timeout around 200 ms, actual: {sw.ElapsedMilliseconds} ms");

            // Wait for helper to exit naturally (bounded max 10s wait)
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(hardTimeoutCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await helper.WaitForExitAsync(cts.Token);

            // Once helper process exited and released handle, parent can immediately acquire
            await using var successLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(500));
            Assert.NotNull(successLease);
            Assert.True(successLease.IsAcquired);
        }
        finally
        {
            // Exact owned child process termination and cleanup with confirmation
            if (helper != null)
            {
                try
                {
                    if (!helper.HasExited)
                    {
                        helper.Kill(entireProcessTree: true);
                        Assert.True(helper.WaitForExit(5000));
                    }
                    else
                    {
                        Assert.True(helper.HasExited);
                    }
                }
                catch { }
                helper.Dispose();
            }
        }
    }

    [Fact]
    public async Task TargetLockPublicationContention_EmptyFile_DetectedAsConflictAndRefused()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "ContestedGame");
        Directory.CreateDirectory(targetPath);

        // Pre-create an empty 0-byte target lock file to simulate in-flight publication contention
        string dummyLockPath = Path.Combine(coordDir.RootPath, "target_0000000000000000000000000000000000000000000000000000000000000000.lock");
        File.WriteAllBytes(dummyLockPath, Array.Empty<byte>());

        // Attempting to acquire any target lease must fail-closed on publication contention
        await using var lease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Null(lease);
    }

    [Fact]
    public async Task TryAcquireOperationScopeAsync_CancelledToken_TerminatesPromptly()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "Game");
        Directory.CreateDirectory(targetPath);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        await using var lease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(500), cancellationToken: cts.Token);
        sw.Stop();

        Assert.Null(lease);
        Assert.True(sw.ElapsedMilliseconds < 150, $"Expected prompt cancellation, took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void OperationLease_WrongStoreValidation_RejectedAcrossComponents()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var target = Path.Combine(tempDir.RootPath, "Game");
        var storeA = Path.Combine(tempDir.RootPath, "store_a.json");
        var storeB = Path.Combine(tempDir.RootPath, "store_b.json");
        Directory.CreateDirectory(target);
        File.WriteAllText(storeA, "{}");
        File.WriteAllText(storeB, "{}");

        // Acquire composite lease covering target and storeA only
        using var lease = coordinator.TryAcquireOperationScope(target, storeA);
        Assert.NotNull(lease);
        Assert.True(lease.IsValidForTarget(target));
        Assert.True(lease.IsValidForStore(storeA));

        // Validation against wrong storeB must return false on both composite token and StoreLease
        Assert.False(lease.IsValidForStore(storeB));
        Assert.NotNull(lease.StoreLease);
        Assert.False(lease.StoreLease.IsValidForStore(storeB));
    }

    [Fact]
    public void ResolveAuthoritativeInstallationRoot_WithValidInstallationPath_UsesInstallationPath()
    {
        using var temp = new SyntheticTestDirectory();
        var gameRoot = Path.Combine(temp.RootPath, "Games", "MyGame");
        var exePath = Path.Combine(gameRoot, "bin", "x64", "game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

        string authoritative = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(gameRoot, exePath);

        Assert.Equal(OperationResourceIdentity.ResolveCanonicalPath(gameRoot), authoritative);
    }

    [Fact]
    public void ResolveAuthoritativeInstallationRoot_WithNullInstallationPath_FallsBackToExeDirectory()
    {
        using var temp = new SyntheticTestDirectory();
        var gameDir = Path.Combine(temp.RootPath, "Games", "MyGame");
        var exePath = Path.Combine(gameDir, "game.exe");
        Directory.CreateDirectory(gameDir);

        string authoritative = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(null, exePath);

        Assert.Equal(OperationResourceIdentity.ResolveCanonicalPath(gameDir), authoritative);
    }

    [Fact]
    public void ResolveAuthoritativeInstallationRoot_SuppliedInvalidUncRoot_ThrowsNotSupportedException()
    {
        using var temp = new SyntheticTestDirectory();
        var localExe = Path.Combine(temp.RootPath, "game.exe");
        File.WriteAllText(localExe, "dummy");

        // Supplying an unsupported UNC installation root must throw NotSupportedException
        Assert.Throws<NotSupportedException>(() =>
            OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(@"\\server\share\game", localExe));

        // When installation path is null or whitespace, falls back to exe directory without throwing
        string fallback = OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(null, localExe);
        Assert.Equal(OperationResourceIdentity.ResolveCanonicalPath(temp.RootPath), fallback);
    }

    [Fact]
    public void ResolveAuthoritativeInstallationRoot_WithEmptyOrNullPaths_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(null, string.Empty));
        Assert.Equal(string.Empty, OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(string.Empty, "   "));
    }
}

