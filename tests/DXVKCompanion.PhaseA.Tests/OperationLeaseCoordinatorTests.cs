using System;
using System.Diagnostics;
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

public sealed class OperationLeaseCoordinatorTests
{
    private static int RemainingHelperBudget(Stopwatch overall, int operationalBudgetMs, SyntheticSafetyFixture.Phase phase)
    {
        int remaining = operationalBudgetMs - (int)overall.ElapsedMilliseconds;
        if (remaining <= 0)
            throw new TimeoutException(SyntheticSafetyFixture.Diagnostic(phase, overall.ElapsedMilliseconds));
        return remaining;
    }

    private static async Task ReadHelperSignalAsync(Process helper, string expected,
        Stopwatch overall, int operationalBudgetMs, SyntheticSafetyFixture.Phase phase)
    {
        int remaining = RemainingHelperBudget(overall, operationalBudgetMs, phase);
        string? signal;
        try
        {
            signal = await helper.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromMilliseconds(remaining)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(SyntheticSafetyFixture.Diagnostic(phase, overall.ElapsedMilliseconds));
        }
        Assert.True(string.Equals(expected, signal, StringComparison.Ordinal),
            SyntheticSafetyFixture.Diagnostic(phase, overall.ElapsedMilliseconds));
    }

    private static ProcessStartInfo CreateTimedLockHelper(string lockPath, string armPath, DateTime deadlineUtc)
    {
        // Only this test's private paths and a deadline cross the child boundary.
        // UTC accounts for startup; the child uses a monotonic watch thereafter.
        string script = $$"""
            $handle = $null
            try {
                $watch = [Diagnostics.Stopwatch]::StartNew()
                $budget = ([DateTime]::new({{deadlineUtc.Ticks}}, [DateTimeKind]::Utc) - [DateTime]::UtcNow).TotalMilliseconds
                if ($budget -le 0) { exit 2 }
                $handle = [IO.FileStream]::new('{{lockPath.Replace("'", "''")}}', [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
                [Console]::Out.WriteLine('LOCKED')
                [Console]::Out.Flush()
                while (-not [IO.File]::Exists('{{armPath.Replace("'", "''")}}')) {
                    if ($watch.Elapsed.TotalMilliseconds -ge $budget) { exit 2 }
                    [Threading.Thread]::Sleep(10)
                }
                if (($budget - $watch.Elapsed.TotalMilliseconds) -lt 2000) { exit 3 }
                [Console]::Out.WriteLine('TIMER_STARTED')
                [Console]::Out.Flush()
                [Threading.Thread]::Sleep(2000)
                if ($watch.Elapsed.TotalMilliseconds -gt $budget) { exit 4 }
                exit 0
            } catch { exit 1 }
            finally { if ($null -ne $handle) { $handle.Dispose() } }
            """;
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(script);
        return info;
    }

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

    [Theory]
    [InlineData(0)]
    [InlineData(2200)]
    public async Task InterProcessLocking_OwnedCloudHelper_ExcludesParentDuringHold(int parentDelayMs)
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            return;

        var tempDir = new SyntheticTestDirectory();
        var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "HelperTarget");
        Directory.CreateDirectory(targetPath);

        string canonicalTarget = OperationResourceIdentity.ResolveCanonicalPath(targetPath);
        string lockName = OperationResourceIdentity.GetTargetLockFileName(canonicalTarget);
        string fullLockPath = Path.Combine(coordDir.RootPath, lockName);

        var overallSw = Stopwatch.StartNew();
        const int maxTotalLifetimeMs = 20000;
        const int cleanupReservationMs = 4000;
        const int operationalBudgetMs = maxTotalLifetimeMs - cleanupReservationMs;
        string armPath = Path.Combine(coordDir.RootPath, "self-exit.signal");
        var psi = CreateTimedLockHelper(fullLockPath, armPath,
            DateTime.UtcNow.AddMilliseconds(RemainingHelperBudget(overallSw, operationalBudgetMs, SyntheticSafetyFixture.Phase.HelperStartupSignal)));
        Process? helper = null;
        bool helperExited = false;
        try
        {
            helper = Process.Start(psi);
            Assert.NotNull(helper);

            await ReadHelperSignalAsync(helper, "LOCKED", overallSw, operationalBudgetMs,
                SyntheticSafetyFixture.Phase.HelperStartupSignal).ConfigureAwait(false);

            // Deliberately exceed the old autonomous two-second hold window.
            // The child must still hold until the parent explicitly arms self-exit.
            if (parentDelayMs > 0) await Task.Delay(parentDelayMs).ConfigureAwait(false);

            // Parent attempt to acquire with 200 ms budget must time out and return null
            var sw = Stopwatch.StartNew();
            await using var refusedLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            sw.Stop();

            Assert.True(refusedLease is null, SyntheticSafetyFixture.Diagnostic(
                SyntheticSafetyFixture.Phase.HelperExclusion, overallSw.ElapsedMilliseconds, coordinator.LastAcquisitionError));
            Assert.True(sw.ElapsedMilliseconds >= 150, $"Expected timeout around 200 ms, actual: {sw.ElapsedMilliseconds} ms");

            // Start the unchanged self-exit interval only after exclusion was observed.
            if (RemainingHelperBudget(overallSw, operationalBudgetMs, SyntheticSafetyFixture.Phase.HelperTimerArm) < 2000)
                throw new TimeoutException(SyntheticSafetyFixture.Diagnostic(SyntheticSafetyFixture.Phase.HelperTimerArm, overallSw.ElapsedMilliseconds));
            File.WriteAllText(armPath, "START");
            await ReadHelperSignalAsync(helper, "TIMER_STARTED", overallSw, operationalBudgetMs,
                SyntheticSafetyFixture.Phase.HelperTimerArm).ConfigureAwait(false);
            using var exitDeadline = new CancellationTokenSource(RemainingHelperBudget(overallSw,
                operationalBudgetMs, SyntheticSafetyFixture.Phase.HelperSelfExit));
            try
            {
                await helper.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (exitDeadline.IsCancellationRequested)
            {
                throw new TimeoutException(SyntheticSafetyFixture.Diagnostic(SyntheticSafetyFixture.Phase.HelperSelfExit, overallSw.ElapsedMilliseconds));
            }
            Assert.Equal(0, helper.ExitCode);

            // Once helper process exited and released handle, parent can immediately acquire
            await using var successLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(500));
            Assert.NotNull(successLease);
            Assert.True(successLease.IsAcquired);
        }
        finally
        {
            if (helper != null)
            {
                if (!helper.HasExited)
                {
                    try
                    {
                        helper.Kill(entireProcessTree: true);
                    }
                    catch { }
                }
                int remainingCleanupMs = maxTotalLifetimeMs - (int)overallSw.ElapsedMilliseconds;
                if (remainingCleanupMs > 0)
                {
                    helperExited = helper.WaitForExit(remainingCleanupMs);
                }
                else
                {
                    helperExited = helper.HasExited;
                }
            }
            else
            {
                helperExited = true;
            }

            if (helperExited)
            {
                helper?.Dispose();
                coordDir.Dispose();
                tempDir.Dispose();
            }

            Assert.True(helperExited, "Child helper process failed to terminate within bounded cleanup reservation.");
        }
    }

    [Fact]
    public async Task InterProcessLocking_UnarmedHelper_ExitsAtWatchdogWithoutParentKill()
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            return;

        var tempDir = new SyntheticTestDirectory();
        var coordDir = new SyntheticTestDirectory();
        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        string target = Path.Combine(tempDir.RootPath, "UnarmedHelperTarget");
        Directory.CreateDirectory(target);
        string lockPath = Path.Combine(coordDir.RootPath,
            OperationResourceIdentity.GetTargetLockFileName(OperationResourceIdentity.ResolveCanonicalPath(target)));
        string armPath = Path.Combine(coordDir.RootPath, "self-exit.signal");
        const int maxTotalLifetimeMs = 12000;
        const int cleanupReservationMs = 4000;
        var overall = Stopwatch.StartNew();
        Process? helper = null;
        bool helperExited = false;
        try
        {
            // No arm marker is ever written. The child must independently exit 2,
            // including when its own deadline expires during process startup.
            helper = Process.Start(CreateTimedLockHelper(lockPath, armPath, DateTime.UtcNow.AddSeconds(2)));
            Assert.NotNull(helper);
            using var deadline = new CancellationTokenSource(RemainingHelperBudget(overall,
                maxTotalLifetimeMs - cleanupReservationMs, SyntheticSafetyFixture.Phase.HelperSelfExit));
            try
            {
                await helper.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new TimeoutException(SyntheticSafetyFixture.Diagnostic(
                    SyntheticSafetyFixture.Phase.HelperSelfExit, overall.ElapsedMilliseconds));
            }
            Assert.Equal(2, helper.ExitCode);
            Assert.False(File.Exists(armPath));
            await using var lease = await coordinator.TryAcquireOperationScopeAsync(target, timeout: TimeSpan.FromMilliseconds(500));
            Assert.True(lease is not null, SyntheticSafetyFixture.Diagnostic(
                SyntheticSafetyFixture.Phase.PostHelperLease, overall.ElapsedMilliseconds, coordinator.LastAcquisitionError));
            Assert.True(lease!.IsAcquired);
        }
        finally
        {
            if (helper != null)
            {
                if (!helper.HasExited)
                {
                    try { helper.Kill(entireProcessTree: true); } catch { }
                }
                int remaining = maxTotalLifetimeMs - (int)overall.ElapsedMilliseconds;
                helperExited = remaining > 0 ? helper.WaitForExit(remaining) : helper.HasExited;
            }
            else helperExited = true;
            if (helperExited)
            {
                helper?.Dispose();
                coordDir.Dispose();
                tempDir.Dispose();
            }
            Assert.True(helperExited, SyntheticSafetyFixture.Diagnostic(
                SyntheticSafetyFixture.Phase.HelperCleanup, overall.ElapsedMilliseconds));
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
        await using var lease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(500), ct: cts.Token);
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

    [Fact]
    public void ProfileStore_CorruptedJson_MarksBrokenLoad_RefusesSaveAndMutation()
    {
        using var testDir = new SyntheticTestDirectory();
        string profilesFile = Path.Combine(testDir.RootPath, "games.json");

        // Write malformed JSON
        File.WriteAllText(profilesFile, "{ \"corrupted\": ");

        var store = new ProfileStore(profilesFile);

        // Preflight check must return false
        Assert.False(store.TryCheckCanModifyProfileStore(null));

        // In-memory profiles are empty
        Assert.Empty(store.GetAll());

        // Save must throw IOException and refuse to overwrite corrupted file
        var profile = new GameProfile(@"C:\Games\Test\game.exe");
        var ex = Assert.Throws<IOException>(() => store.Save(profile));
        Assert.Contains("corrupted or unreadable", ex.Message);

        // File on disk remains untouched
        Assert.Equal("{ \"corrupted\": ", File.ReadAllText(profilesFile));
    }

    [Fact]
    public void ProfileStore_ExternalFileModification_MismatchedBaseline_RefusesSave()
    {
        using var testDir = new SyntheticTestDirectory();
        string profilesFile = Path.Combine(testDir.RootPath, "games.json");

        var store = new ProfileStore(profilesFile);
        var profile = store.GetOrCreate(@"C:\Games\Test\game.exe");
        store.Save(profile);

        // Simulate external process modifying the profiles file
        File.WriteAllText(profilesFile, "{\"external\": true}");

        // Preflight check must detect baseline mismatch
        Assert.False(store.TryCheckCanModifyProfileStore(null));

        // Mutate and attempt to save: must refuse with IOException
        profile.DxvkVersion = "2.5";
        var ex = Assert.Throws<IOException>(() => store.Save(profile));
        Assert.Contains("modified externally", ex.Message);
    }

    [Fact]
    public async Task ProfileStore_BrokenLoad_DxvkManagerPreflightRefusal()
    {
        using var testDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var gameDir = Path.Combine(testDir.RootPath, "Game");
        Directory.CreateDirectory(gameDir);
        var exePath = Path.Combine(gameDir, "Game.exe");
        File.WriteAllText(exePath, "game-bin");

        var storePath = Path.Combine(testDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(testDir.RootPath, "backups");
        var profilesFile = Path.Combine(testDir.RootPath, "games.json");
        var sourceDir = Path.Combine(testDir.RootPath, "source");
        Directory.CreateDirectory(sourceDir);

        // Corrupt profiles file
        File.WriteAllText(profilesFile, "{ \"broken\": ");

        var profileStore = new ProfileStore(profilesFile) { LeaseCoordinator = coordinator };
        var libraryStore = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var engine = new MultiFileTransactionEngine(backupsPath);

        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, libraryStore, sourceDir)
        {
            LeaseCoordinator = coordinator,
            ProfileStore = profileStore
        };
        var rollback = new DxvkRollback(engine, libraryStore)
        {
            LeaseCoordinator = coordinator
        };
        var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(http, new CacheStore()), profileStore, libraryStore, coordinator);

        var profile = new GameProfile(exePath) { Api = GraphicsApi.DX11, Architecture = "x64" };

        // Act
        bool result = await manager.EnableDxvkAsync(profile, "2.5");

        // Assert: manager refused at preflight
        Assert.False(result);
        Assert.NotNull(manager.LastRefusalReason);
        Assert.Contains("corrupted or unreadable", manager.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Game directory remains untouched
        Assert.False(File.Exists(Path.Combine(gameDir, "d3d11.dll")));
    }

    [Fact]
    public async Task InterProcessLocking_ChildHelperPrematureFailure_HandledGracefully()
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            return;

        var tempDir = new SyntheticTestDirectory();
        var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "HelperTargetFail");
        Directory.CreateDirectory(targetPath);

        // Child process immediately exits with error code 1 without writing LOCKED
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"exit 1\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var overallSw = Stopwatch.StartNew();
        const int maxTotalLifetimeMs = 10000;
        const int cleanupReservationMs = 3000;
        Process? helper = null;
        bool helperExited = false;
        try
        {
            helper = Process.Start(psi);
            Assert.NotNull(helper);

            int operationalBudget = maxTotalLifetimeMs - cleanupReservationMs - (int)overallSw.ElapsedMilliseconds;
            if (operationalBudget <= 0) throw new TimeoutException("Operational budget expired before child start.");
            var readTask = helper.StandardOutput.ReadLineAsync();
            var readCompleted = await Task.WhenAny(readTask, Task.Delay(operationalBudget));
            Assert.Same(readTask, readCompleted);
            string? signal = await readTask;
            Assert.Null(signal); // No LOCKED signal written

            int exitBudget = maxTotalLifetimeMs - cleanupReservationMs - (int)overallSw.ElapsedMilliseconds;
            if (exitBudget <= 0) throw new TimeoutException("Exit budget expired before child exit.");
            bool exited = helper.WaitForExit(exitBudget);
            Assert.True(exited, "Child helper process failed to exit within bounded operational budget.");
            Assert.NotEqual(0, helper.ExitCode);

            // Coordinator can acquire target lease normally without hang
            await using var lease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(500));
            Assert.NotNull(lease);
            Assert.True(lease.IsAcquired);
        }
        finally
        {
            if (helper != null)
            {
                if (!helper.HasExited)
                {
                    try { helper.Kill(entireProcessTree: true); } catch { }
                }
                int remainingCleanupMs = maxTotalLifetimeMs - (int)overallSw.ElapsedMilliseconds;
                if (remainingCleanupMs > 0)
                {
                    helperExited = helper.WaitForExit(remainingCleanupMs);
                }
                else
                {
                    helperExited = helper.HasExited;
                }
            }
            else
            {
                helperExited = true;
            }

            if (helperExited)
            {
                helper?.Dispose();
                coordDir.Dispose();
                tempDir.Dispose();
            }

            Assert.True(helperExited, "Child helper process failed to terminate within cleanup reservation.");
        }
    }

    [Fact]
    public async Task InterProcessLocking_ChildHelperTimeoutFailure_TerminatedAndCleanedUpWithinBudget()
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            return;

        var tempDir = new SyntheticTestDirectory();
        var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "HelperTargetTimeout");
        Directory.CreateDirectory(targetPath);

        string canonicalTarget = OperationResourceIdentity.ResolveCanonicalPath(targetPath);
        string lockName = OperationResourceIdentity.GetTargetLockFileName(canonicalTarget);
        string fullLockPath = Path.Combine(coordDir.RootPath, lockName);

        var overallSw = Stopwatch.StartNew();
        const int maxTotalLifetimeMs = 12000;
        const int cleanupReservationMs = 4000;
        const int operationalBudgetMs = maxTotalLifetimeMs - cleanupReservationMs;
        string armPath = Path.Combine(coordDir.RootPath, "self-exit.signal");
        var psi = CreateTimedLockHelper(fullLockPath, armPath,
            DateTime.UtcNow.AddMilliseconds(RemainingHelperBudget(overallSw, operationalBudgetMs, SyntheticSafetyFixture.Phase.HelperStartupSignal)));
        Process? helper = null;
        bool helperExited = false;
        try
        {
            helper = Process.Start(psi);
            Assert.NotNull(helper);

            await ReadHelperSignalAsync(helper, "LOCKED", overallSw, operationalBudgetMs,
                SyntheticSafetyFixture.Phase.HelperStartupSignal).ConfigureAwait(false);

            // Parent attempts acquisition with 200 ms timeout and must be excluded while child holds lock
            var sw = Stopwatch.StartNew();
            await using var refusedLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            sw.Stop();
            Assert.True(refusedLease is null, SyntheticSafetyFixture.Diagnostic(
                SyntheticSafetyFixture.Phase.HelperExclusion, overallSw.ElapsedMilliseconds, coordinator.LastAcquisitionError));
            Assert.InRange(sw.ElapsedMilliseconds, 150, 2000);

            // Parent completion arms the unchanged independent two-second self-exit.
            if (RemainingHelperBudget(overallSw, operationalBudgetMs, SyntheticSafetyFixture.Phase.HelperTimerArm) < 2000)
                throw new TimeoutException(SyntheticSafetyFixture.Diagnostic(SyntheticSafetyFixture.Phase.HelperTimerArm, overallSw.ElapsedMilliseconds));
            File.WriteAllText(armPath, "START");
            await ReadHelperSignalAsync(helper, "TIMER_STARTED", overallSw, operationalBudgetMs,
                SyntheticSafetyFixture.Phase.HelperTimerArm).ConfigureAwait(false);
            using var exitDeadline = new CancellationTokenSource(RemainingHelperBudget(overallSw,
                operationalBudgetMs, SyntheticSafetyFixture.Phase.HelperSelfExit));
            try
            {
                await helper.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (exitDeadline.IsCancellationRequested)
            {
                throw new TimeoutException(SyntheticSafetyFixture.Diagnostic(
                    SyntheticSafetyFixture.Phase.HelperSelfExit, overallSw.ElapsedMilliseconds));
            }
            Assert.Equal(0, helper.ExitCode);

            // Once child self-terminated and released lock, coordinator acquires target lease successfully
            await using var successLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(500));
            Assert.True(successLease is not null, SyntheticSafetyFixture.Diagnostic(SyntheticSafetyFixture.Phase.PostHelperLease, overallSw.ElapsedMilliseconds, coordinator.LastAcquisitionError));
            Assert.True(successLease!.IsAcquired);
        }
        finally
        {
            if (helper != null)
            {
                if (!helper.HasExited)
                {
                    try { helper.Kill(entireProcessTree: true); } catch { }
                }
                int remainingCleanupMs = maxTotalLifetimeMs - (int)overallSw.ElapsedMilliseconds;
                if (remainingCleanupMs > 0)
                {
                    helperExited = helper.WaitForExit(remainingCleanupMs);
                }
                else
                {
                    helperExited = helper.HasExited;
                }
            }
            else
            {
                helperExited = true;
            }

            if (helperExited)
            {
                helper?.Dispose();
                coordDir.Dispose();
                tempDir.Dispose();
            }

            Assert.True(helperExited, SyntheticSafetyFixture.Diagnostic(SyntheticSafetyFixture.Phase.HelperCleanup, overallSw.ElapsedMilliseconds));
        }
    }

    [Fact]
    public async Task TargetLock_UnreadableLockFile_FailsClosedSyncAndAsync()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "UnreadableTarget");
        Directory.CreateDirectory(targetPath);

        // Create an unreadable .target.lock file by locking it exclusively with FileShare.None
        string unreadableLock = Path.Combine(coordDir.RootPath, "target_0000000000000000000000000000000000000000000000000000000000000001.target.lock");
        using var exclusiveStream = new FileStream(unreadableLock, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        // Async acquisition must fail closed (return null) and respect upper budget bounds
        var asyncSw = Stopwatch.StartNew();
        await using var asyncLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(100));
        asyncSw.Stop();
        Assert.Null(asyncLease);
        Assert.InRange(asyncSw.ElapsedMilliseconds, 70, 2000);

        // Cancellation during wait must abort within bounded time
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);
        var cancelSw = Stopwatch.StartNew();
        await using var cancelLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromSeconds(2), ct: cts.Token);
        cancelSw.Stop();
        Assert.Null(cancelLease);
        Assert.True(cancelSw.ElapsedMilliseconds < 1500, $"Expected early exit on cancellation during wait, actual: {cancelSw.ElapsedMilliseconds} ms");

        // Sync acquisition must fail closed (return null) and respect upper budget bounds
        var syncSw = Stopwatch.StartNew();
        using var syncLease = coordinator.TryAcquireOperationScope(targetPath, timeout: TimeSpan.FromMilliseconds(100));
        syncSw.Stop();
        Assert.Null(syncLease);
        Assert.InRange(syncSw.ElapsedMilliseconds, 70, 2000);
    }

    [Fact]
    public async Task TargetLock_EmptyPublication_FailsClosedWhenNotWritten_SyncAndAsync()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "EmptyPubTarget");
        Directory.CreateDirectory(targetPath);

        // Create an empty (0 bytes) .target.lock file with FileShare.ReadWrite so it can be opened but has empty content
        string emptyLock = Path.Combine(coordDir.RootPath, "target_0000000000000000000000000000000000000000000000000000000000000002.target.lock");
        File.WriteAllBytes(emptyLock, Array.Empty<byte>());

        // Async: fails closed because content is unreadable/empty and file does not disappear
        var asyncSw = Stopwatch.StartNew();
        await using var asyncLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(100));
        asyncSw.Stop();
        Assert.Null(asyncLease);
        Assert.InRange(asyncSw.ElapsedMilliseconds, 70, 2000);

        // Cancellation during wait must abort boundedly
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);
        var cancelSw = Stopwatch.StartNew();
        await using var cancelLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromSeconds(2), ct: cts.Token);
        cancelSw.Stop();
        Assert.Null(cancelLease);
        Assert.True(cancelSw.ElapsedMilliseconds < 1500, $"Expected early exit on cancellation during wait, actual: {cancelSw.ElapsedMilliseconds} ms");

        // Sync: fails closed because content is unreadable/empty and file does not disappear
        var syncSw = Stopwatch.StartNew();
        using var syncLease = coordinator.TryAcquireOperationScope(targetPath, timeout: TimeSpan.FromMilliseconds(100));
        syncSw.Stop();
        Assert.Null(syncLease);
        Assert.InRange(syncSw.ElapsedMilliseconds, 70, 2000);
    }

    [Fact]
    public async Task TargetLock_InjectedEnumerationFailure_FailsClosedSyncAndAsync()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "InjectedEnumTarget");
        Directory.CreateDirectory(targetPath);

        // Injected enumeration failure simulates directory access denial
        coordinator.CustomEnumerateFiles = _ => throw new UnauthorizedAccessException("Simulated directory permission denial");

        await using var asyncLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Null(asyncLease);

        using var syncLease = coordinator.TryAcquireOperationScope(targetPath, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Null(syncLease);
    }

    [Fact]
    public async Task TargetLock_InjectedCandidateOpenFailure_FailsClosedBoundedSyncAndAsync()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var targetPath = Path.Combine(tempDir.RootPath, "InjectedOpenTarget");
        Directory.CreateDirectory(targetPath);

        // Create an existing candidate lock file that throws UnauthorizedAccessException on inspection
        // Microsoft documents that File.Exists returns false on permission denial.
        // Our coordinator must NOT shortcut on false existence and must fail closed.
        string candidateLock = Path.Combine(coordDir.RootPath, "target_0000000000000000000000000000000000000000000000000000000000000003.lock");
        File.WriteAllText(candidateLock, "dummy-path");

        coordinator.CustomOpenLockFileStream = path =>
        {
            if (path.Contains("0000000000000000000000000000000000000000000000000000000000000003"))
            {
                throw new UnauthorizedAccessException("Injected access denied on candidate lock inspection");
            }
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        };

        // Async: fails closed within requested budget
        var asyncSw = Stopwatch.StartNew();
        await using var asyncLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(100));
        asyncSw.Stop();
        Assert.Null(asyncLease);
        Assert.InRange(asyncSw.ElapsedMilliseconds, 70, 2000);

        // Async cancellation during wait
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);
        var cancelSw = Stopwatch.StartNew();
        await using var cancelLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromSeconds(2), ct: cts.Token);
        cancelSw.Stop();
        Assert.Null(cancelLease);
        Assert.True(cancelSw.ElapsedMilliseconds < 1500, $"Expected early exit on cancellation during wait, actual: {cancelSw.ElapsedMilliseconds} ms");

        // Sync: fails closed within requested budget
        var syncSw = Stopwatch.StartNew();
        using var syncLease = coordinator.TryAcquireOperationScope(targetPath, timeout: TimeSpan.FromMilliseconds(100));
        syncSw.Stop();
        Assert.Null(syncLease);
        Assert.InRange(syncSw.ElapsedMilliseconds, 70, 2000);
    }

    [Fact]
    public void OperationScope_ReverseStoreOrdering_AcquiresDeterministicallyWithoutDeadlock()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var target = Path.Combine(tempDir.RootPath, "GameRoot");
        var storeA = Path.Combine(tempDir.RootPath, "store_a.json");
        var storeB = Path.Combine(tempDir.RootPath, "store_b.json");
        Directory.CreateDirectory(target);
        File.WriteAllText(storeA, "{}");
        File.WriteAllText(storeB, "{}");

        // First caller requests stores in order [A, B]
        using var lease1 = coordinator.TryAcquireOperationScope(target, new[] { storeA, storeB }, TimeSpan.FromMilliseconds(500));
        Assert.NotNull(lease1);
        Assert.True(lease1.IsAcquired);
        Assert.True(lease1.IsValidForStore(storeA));
        Assert.True(lease1.IsValidForStore(storeB));
        lease1.Dispose();

        // Second caller requests stores in reverse order [B, A]
        using var lease2 = coordinator.TryAcquireOperationScope(target, new[] { storeB, storeA }, TimeSpan.FromMilliseconds(500));
        Assert.NotNull(lease2);
        Assert.True(lease2.IsAcquired);
        Assert.True(lease2.IsValidForStore(storeA));
        Assert.True(lease2.IsValidForStore(storeB));
    }
}

