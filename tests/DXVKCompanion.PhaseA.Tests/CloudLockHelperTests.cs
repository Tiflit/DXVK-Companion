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

[CollectionDefinition("Cloud lock helpers", DisableParallelization = true)]
public sealed class CloudLockHelperCollection { }

[Collection("Cloud lock helpers")]
public sealed class CloudLockHelperTests
{
    private static string SchedulingDiagnostic(Process helper)
    {
        ThreadPool.GetAvailableThreads(out int availableWorkers, out _);
        ThreadPool.GetMinThreads(out int minimumWorkers, out _);
        string childState;
        try { childState = helper.HasExited ? $"Exited{helper.ExitCode}" : "Running"; }
        catch { childState = "Unavailable"; }
        return $"workers={ThreadPool.ThreadCount}; minWorkers={minimumWorkers}; availableWorkers={availableWorkers}; pendingWork={ThreadPool.PendingWorkItemCount}; child={childState}";
    }

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
        var readTask = helper.StandardOutput.ReadLineAsync();
        string? signal;
        try
        {
            signal = await readTask.WaitAsync(TimeSpan.FromMilliseconds(remaining)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{SyntheticSafetyFixture.Diagnostic(phase, overall.ElapsedMilliseconds)}; readState={readTask.Status}; {SchedulingDiagnostic(helper)}");
        }
        Assert.True(string.Equals(expected, signal, StringComparison.Ordinal),
            SyntheticSafetyFixture.Diagnostic(phase, overall.ElapsedMilliseconds));
    }

    private static async Task WaitForHelperExitAsync(Process helper, Stopwatch overall, int operationalBudgetMs)
    {
        using var deadline = new CancellationTokenSource(RemainingHelperBudget(overall,
            operationalBudgetMs, SyntheticSafetyFixture.Phase.HelperSelfExit));
        try
        {
            await helper.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(SyntheticSafetyFixture.Diagnostic(
                SyntheticSafetyFixture.Phase.HelperSelfExit, overall.ElapsedMilliseconds));
        }
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
            long processStartedMs = overallSw.ElapsedMilliseconds;

            await ReadHelperSignalAsync(helper, "LOCKED", overallSw, operationalBudgetMs,
                SyntheticSafetyFixture.Phase.HelperStartupSignal);
            long signalMs = overallSw.ElapsedMilliseconds;

            // Deliberately exceed the old autonomous two-second hold window.
            // The child must still hold until the parent explicitly arms self-exit.
            if (parentDelayMs > 0) await Task.Delay(parentDelayMs);
            long delayCompletedMs = overallSw.ElapsedMilliseconds;

            // Parent attempt to acquire with 200 ms budget must time out and return null
            var sw = Stopwatch.StartNew();
            await using var refusedLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(200));
            sw.Stop();

            Assert.True(refusedLease is null, $"{SyntheticSafetyFixture.Diagnostic(
                SyntheticSafetyFixture.Phase.HelperExclusion, overallSw.ElapsedMilliseconds, coordinator.LastAcquisitionError)}; processStartedMs={processStartedMs}; signalMs={signalMs}; delayCompletedMs={delayCompletedMs}; probeMs={sw.ElapsedMilliseconds}; {SchedulingDiagnostic(helper)}");
            Assert.True(sw.ElapsedMilliseconds >= 150, $"Expected timeout around 200 ms, actual: {sw.ElapsedMilliseconds} ms");

            // Start the unchanged self-exit interval only after exclusion was observed.
            if (RemainingHelperBudget(overallSw, operationalBudgetMs, SyntheticSafetyFixture.Phase.HelperTimerArm) < 2000)
                throw new TimeoutException(SyntheticSafetyFixture.Diagnostic(SyntheticSafetyFixture.Phase.HelperTimerArm, overallSw.ElapsedMilliseconds));
            File.WriteAllText(armPath, "START");
            await ReadHelperSignalAsync(helper, "TIMER_STARTED", overallSw, operationalBudgetMs,
                SyntheticSafetyFixture.Phase.HelperTimerArm);
            await WaitForHelperExitAsync(helper, overallSw, operationalBudgetMs);
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
            await WaitForHelperExitAsync(helper, overall, maxTotalLifetimeMs - cleanupReservationMs);
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
                SyntheticSafetyFixture.Phase.HelperStartupSignal);

            // Parent attempts acquisition with 200 ms timeout and must be excluded while child holds lock
            var sw = Stopwatch.StartNew();
            await using var refusedLease = await coordinator.TryAcquireOperationScopeAsync(targetPath, timeout: TimeSpan.FromMilliseconds(200));
            sw.Stop();
            Assert.True(refusedLease is null, SyntheticSafetyFixture.Diagnostic(
                SyntheticSafetyFixture.Phase.HelperExclusion, overallSw.ElapsedMilliseconds, coordinator.LastAcquisitionError));
            Assert.InRange(sw.ElapsedMilliseconds, 150, 2000);

            // Parent completion arms the unchanged independent two-second self-exit.
            if (RemainingHelperBudget(overallSw, operationalBudgetMs, SyntheticSafetyFixture.Phase.HelperTimerArm) < 2000)
                throw new TimeoutException(SyntheticSafetyFixture.Diagnostic(SyntheticSafetyFixture.Phase.HelperTimerArm, overallSw.ElapsedMilliseconds));
            File.WriteAllText(armPath, "START");
            await ReadHelperSignalAsync(helper, "TIMER_STARTED", overallSw, operationalBudgetMs,
                SyntheticSafetyFixture.Phase.HelperTimerArm);
            await WaitForHelperExitAsync(helper, overallSw, operationalBudgetMs);
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

}
