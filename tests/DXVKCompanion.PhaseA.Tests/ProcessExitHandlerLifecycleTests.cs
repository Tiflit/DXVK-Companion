using System;
using System.Collections.Generic;
using System.Diagnostics;
using DXVKCompanion.Monitoring;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class ProcessExitHandlerLifecycleTests
{
    private sealed class TestableProcessExitHandler : ProcessExitHandler
    {
        public bool FailBeforeSubscription { get; set; }
        public bool FailInsideSubscriptionAfterHook { get; set; }
        public bool FailAfterSubscription { get; set; }
        public bool SimulateSynchronousExit { get; set; }
        public bool ForceEmptyPath { get; set; }
        public string? SyntheticExePath { get; set; }

        public int SubscriptionsCount { get; private set; }
        public int UnsubscriptionsCount { get; private set; }
        public int DisposalsCount { get; private set; }
        public EventHandler? LastRegisteredHandler { get; private set; }

        internal override string? GetProcessExecutablePath(Process process)
        {
            if (ForceEmptyPath) return null;
            return SyntheticExePath ?? base.GetProcessExecutablePath(process);
        }

        internal override void SubscribeProcessExit(Process process, EventHandler handler)
        {
            if (FailBeforeSubscription)
            {
                throw new InvalidOperationException("Simulated failure before subscription (e.g. Access Denied on EnableRaisingEvents)");
            }

            SubscriptionsCount++;
            LastRegisteredHandler = handler;

            if (FailInsideSubscriptionAfterHook)
            {
                throw new InvalidOperationException("Simulated failure inside SubscribeProcessExit after hooking handler");
            }
        }

        internal override void UnsubscribeProcessExit(Process process, EventHandler handler)
        {
            UnsubscriptionsCount++;
            if (LastRegisteredHandler == handler)
            {
                LastRegisteredHandler = null;
            }
        }

        internal override bool CheckProcessHasExited(Process process)
        {
            if (FailAfterSubscription)
            {
                throw new InvalidOperationException("Simulated failure after subscription (e.g. process terminated before exit check)");
            }

            return SimulateSynchronousExit;
        }

        internal override void DisposeProcess(Process process)
        {
            DisposalsCount++;
        }

        public void TriggerExitCallback()
        {
            LastRegisteredHandler?.Invoke(this, EventArgs.Empty);
        }
    }

    [Fact]
    public void SetupFailure_BeforeSubscription_ReleasesRegistrationAndFiresNoEvents()
    {
        string expectedPath = @"C:\Games\FailBeforeSub\Game.exe";
        var handler = new TestableProcessExitHandler
        {
            SyntheticExePath = expectedPath,
            FailBeforeSubscription = true
        };
        using var dummyProc = Process.GetCurrentProcess();

        int exitedCount = 0;
        int instanceExitedCount = 0;
        handler.ProcessExited += _ => exitedCount++;
        handler.ProcessInstanceExited += (_, _, _) => instanceExitedCount++;

        var token = Guid.NewGuid();
        var resultToken = handler.AttachCore(dummyProc, token);

        Assert.Equal(Guid.Empty, resultToken);
        Assert.Equal(0, handler.SubscriptionsCount);
        // Consolidated cancellation path safely and defensively attempts unsubscription of the handler closure
        Assert.Equal(1, handler.UnsubscriptionsCount);
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));
        Assert.Equal(0, exitedCount);
        Assert.Equal(0, instanceExitedCount);
        Assert.Equal(0, handler.DisposalsCount);
    }

    [Fact]
    public void SetupFailure_InsideSubscriptionAfterHook_UnwindsSubscriptionAndReleasesRegistration()
    {
        string expectedPath = @"C:\Games\FailInsideSub\Game.exe";
        var handler = new TestableProcessExitHandler
        {
            SyntheticExePath = expectedPath,
            FailInsideSubscriptionAfterHook = true
        };
        using var dummyProc = Process.GetCurrentProcess();

        int exitedCount = 0;
        int instanceExitedCount = 0;
        handler.ProcessExited += _ => exitedCount++;
        handler.ProcessInstanceExited += (_, _, _) => instanceExitedCount++;

        var token = Guid.NewGuid();
        var resultToken = handler.AttachCore(dummyProc, token);

        Assert.Equal(Guid.Empty, resultToken);
        Assert.Equal(1, handler.SubscriptionsCount);
        // Consolidated cancellation path performs exactly one unsubscription
        Assert.Equal(1, handler.UnsubscriptionsCount);
        Assert.Null(handler.LastRegisteredHandler);
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));
        Assert.Equal(0, exitedCount);
        Assert.Equal(0, instanceExitedCount);
        Assert.Equal(0, handler.DisposalsCount);
    }

    [Fact]
    public void SetupFailure_AfterPartialSubscription_UnwindsSubscriptionAndReleasesRegistration()
    {
        string expectedPath = @"C:\Games\FailAfterSub\Game.exe";
        var handler = new TestableProcessExitHandler
        {
            SyntheticExePath = expectedPath,
            FailAfterSubscription = true
        };
        using var dummyProc = Process.GetCurrentProcess();

        int exitedCount = 0;
        int instanceExitedCount = 0;
        handler.ProcessExited += _ => exitedCount++;
        handler.ProcessInstanceExited += (_, _, _) => instanceExitedCount++;

        var token = Guid.NewGuid();
        var resultToken = handler.AttachCore(dummyProc, token);

        Assert.Equal(Guid.Empty, resultToken);
        Assert.Equal(1, handler.SubscriptionsCount);
        // Consolidated cancellation path performs exactly one unsubscription
        Assert.Equal(1, handler.UnsubscriptionsCount);
        Assert.Null(handler.LastRegisteredHandler);
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));
        Assert.Equal(0, exitedCount);
        Assert.Equal(0, instanceExitedCount);
        Assert.Equal(0, handler.DisposalsCount);
    }

    [Fact]
    public void MissingPathExit_ReleasesRegistrationAndCleansUpWithoutRaisingMisleadingEvents()
    {
        var handler = new TestableProcessExitHandler { ForceEmptyPath = true };
        using var dummyProc = Process.GetCurrentProcess();

        int exitedCount = 0;
        int instanceExitedCount = 0;
        handler.ProcessExited += _ => exitedCount++;
        handler.ProcessInstanceExited += (_, _, _) => instanceExitedCount++;

        var token = Guid.NewGuid();
        var resultToken = handler.AttachCore(dummyProc, token);

        Assert.Equal(token, resultToken);
        Assert.Equal(1, handler.ActiveRegistrationCount);
        Assert.True(handler.HasRegistration(token));
        Assert.Equal(1, handler.SubscriptionsCount);

        var callback = handler.LastRegisteredHandler;
        Assert.NotNull(callback);

        // Trigger exit callback
        callback.Invoke(this, EventArgs.Empty);

        // Registration bookkeeping is released
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));

        // Subscription is detached
        Assert.Equal(1, handler.UnsubscriptionsCount);

        // No misleading path-bearing events are raised
        Assert.Equal(0, exitedCount);
        Assert.Equal(0, instanceExitedCount);

        // Owned cleanup is performed exactly once
        Assert.Equal(1, handler.DisposalsCount);
    }

    [Fact]
    public void SuccessfulExit_DeliversExactlyOnce_AndReleasesRegistration()
    {
        string expectedPath = @"C:\Games\ValidGame\Game.exe";
        var handler = new TestableProcessExitHandler { SyntheticExePath = expectedPath };
        using var dummyProc = Process.GetCurrentProcess();

        var deliveredPaths = new List<string>();
        var deliveredTokens = new List<Guid>();
        handler.ProcessExited += path => deliveredPaths.Add(path);
        handler.ProcessInstanceExited += (tok, _, _) => deliveredTokens.Add(tok);

        var token = Guid.NewGuid();
        var resultToken = handler.AttachCore(dummyProc, token);

        Assert.Equal(token, resultToken);
        Assert.Equal(1, handler.ActiveRegistrationCount);

        var callback = handler.LastRegisteredHandler;
        Assert.NotNull(callback);

        // Trigger exit via captured delegate
        callback.Invoke(this, EventArgs.Empty);

        // Delivered exactly once and released
        Assert.Single(deliveredPaths);
        Assert.Equal(expectedPath, deliveredPaths[0]);
        Assert.Single(deliveredTokens);
        Assert.Equal(token, deliveredTokens[0]);
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));
        Assert.Equal(1, handler.UnsubscriptionsCount);
        Assert.Equal(1, handler.DisposalsCount);

        // Duplicate callback on the captured delegate does not deliver again and does not re-execute unsubscription or disposal
        callback.Invoke(this, EventArgs.Empty);
        Assert.Single(deliveredPaths);
        Assert.Single(deliveredTokens);
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.Equal(1, handler.UnsubscriptionsCount);
        Assert.Equal(1, handler.DisposalsCount);
    }

    [Fact]
    public void Cancellation_ReleasesRegistration_AndSuppressesFutureExitDelivery()
    {
        string expectedPath = @"C:\Games\Game\Game.exe";
        var handler = new TestableProcessExitHandler { SyntheticExePath = expectedPath };
        using var dummyProc = Process.GetCurrentProcess();

        int exitedCount = 0;
        handler.ProcessExited += _ => exitedCount++;

        var token = Guid.NewGuid();
        handler.AttachCore(dummyProc, token);

        Assert.Equal(1, handler.ActiveRegistrationCount);
        Assert.True(handler.HasRegistration(token));

        var callback = handler.LastRegisteredHandler;
        Assert.NotNull(callback);

        // Cancel registration
        handler.CancelRegistration(token);
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));
        Assert.Equal(1, handler.UnsubscriptionsCount);
        Assert.Equal(0, handler.DisposalsCount); // Cancellation does not dispose process

        // Delayed exit delivery on the captured callback occurs after cancellation:
        // must not deliver events, must not reacquire cleanup, and must not re-execute unsubscription or disposal
        callback.Invoke(this, EventArgs.Empty);
        Assert.Equal(0, exitedCount);
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.Equal(1, handler.UnsubscriptionsCount);
        Assert.Equal(0, handler.DisposalsCount);
    }

    [Fact]
    public void AsynchronousSubscriberException_DoesNotSkipResourceCleanupOrProcessDisposal()
    {
        string expectedPath = @"C:\Games\CrashyAsync\Game.exe";
        var handler = new TestableProcessExitHandler { SyntheticExePath = expectedPath };
        using var dummyProc = Process.GetCurrentProcess();

        var token = Guid.NewGuid();
        handler.AttachCore(dummyProc, token);

        handler.ProcessExited += _ => throw new InvalidOperationException("Async subscriber crash");

        // Trigger exit callback — subscriber exception is rethrown, not silently swallowed
        var ex = Assert.Throws<InvalidOperationException>(() => handler.TriggerExitCallback());
        Assert.Equal("Async subscriber crash", ex.Message);

        // Resource release, unsubscription, and disposal must have occurred in finally
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));
        Assert.Equal(1, handler.UnsubscriptionsCount);
        Assert.Equal(1, handler.DisposalsCount);
    }

    [Fact]
    public void SynchronousSubscriberException_PropagatesWithoutBeingSwallowed_AndCompletesDisposal()
    {
        string expectedPath = @"C:\Games\CrashySync\Game.exe";
        var handler = new TestableProcessExitHandler
        {
            SyntheticExePath = expectedPath,
            SimulateSynchronousExit = true
        };
        using var dummyProc = Process.GetCurrentProcess();

        handler.ProcessExited += _ => throw new InvalidOperationException("Sync subscriber crash");

        var token = Guid.NewGuid();

        // R3: Synchronous subscriber exception must propagate out of AttachCore, NOT get converted to Guid.Empty
        var ex = Assert.Throws<InvalidOperationException>(() => handler.AttachCore(dummyProc, token));
        Assert.Equal("Sync subscriber crash", ex.Message);

        // Owned cleanup ran in finally inside CompleteExit
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));
        Assert.Equal(1, handler.UnsubscriptionsCount);
        Assert.Equal(1, handler.DisposalsCount);
    }

    [Fact]
    public void SynchronousExit_DeliversAndCleansUpDuringSetup()
    {
        string expectedPath = @"C:\Games\SyncExitGame\Game.exe";
        var handler = new TestableProcessExitHandler
        {
            SyntheticExePath = expectedPath,
            SimulateSynchronousExit = true
        };
        using var dummyProc = Process.GetCurrentProcess();

        var deliveredTokens = new List<Guid>();
        var deliveredPaths = new List<string>();
        handler.ProcessInstanceExited += (tok, _, path) =>
        {
            deliveredTokens.Add(tok);
            deliveredPaths.Add(path);
        };

        var token = Guid.NewGuid();
        var resultToken = handler.AttachCore(dummyProc, token);

        // Setup succeeded and delivered exit synchronously during AttachCore
        Assert.Equal(token, resultToken);
        Assert.Single(deliveredTokens);
        Assert.Equal(token, deliveredTokens[0]);
        Assert.Single(deliveredPaths);
        Assert.Equal(expectedPath, deliveredPaths[0]);
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.Equal(1, handler.UnsubscriptionsCount);
        Assert.Equal(1, handler.DisposalsCount);
    }
}
