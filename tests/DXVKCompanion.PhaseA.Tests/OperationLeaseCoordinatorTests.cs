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
        await using var leaseTarget2Only = await coordinator.TryAcquireOperationScopeAsync(target2, storePath: null, timeout: TimeSpan.FromMilliseconds(100));
        Assert.NotNull(leaseTarget2Only);

        // But Target 2 cannot acquire scope with the shared store while lease1 holds it
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
    public async Task InterProcessLocking_OwnedCloudHelper_ExcludesParentDuringHold()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "HelperTarget");
        Directory.CreateDirectory(targetPath);

        string canonicalTarget = OperationResourceIdentity.ResolveCanonicalPath(targetPath);
        string lockName = OperationResourceIdentity.GetTargetLockFileName(canonicalTarget);
        string fullLockPath = Path.Combine(coordDir.RootPath, lockName);

        // Prepare child helper process: holds lock exclusively for 1500 ms, then exits
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"$f = [System.IO.File]::Open('{fullLockPath.Replace("'", "''")}', [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None); Start-Sleep -Milliseconds 1500; $f.Dispose()\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process? helper = null;
        try
        {
            helper = Process.Start(psi);
            Assert.NotNull(helper);

            // Give helper brief window to acquire file lock
            await Task.Delay(400);

            // Parent attempt to acquire with 200 ms budget must time out and return null
            var sw = Stopwatch.StartNew();
            await using var refusedLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(200));
            sw.Stop();

            Assert.Null(refusedLease);
            Assert.True(sw.ElapsedMilliseconds >= 150, $"Expected timeout around 200 ms, actual: {sw.ElapsedMilliseconds} ms");

            // Wait for helper to exit naturally (bounded max 10s wait)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await helper.WaitForExitAsync(cts.Token);

            // Once helper process exited and released handle, parent can immediately acquire
            await using var successLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(500));
            Assert.NotNull(successLease);
            Assert.True(successLease.IsAcquired);
        }
        finally
        {
            // Exact owned child process termination and cleanup
            if (helper != null)
            {
                try
                {
                    if (!helper.HasExited)
                    {
                        helper.Kill();
                    }
                }
                catch { }
                helper.Dispose();
            }
        }
    }
}
