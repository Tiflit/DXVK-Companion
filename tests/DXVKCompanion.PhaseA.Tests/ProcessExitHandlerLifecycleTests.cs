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
        public bool FailAfterSubscription { get; set; }
        public bool SimulateSynchronousExit { get; set; }
        public bool ForceEmptyPath { get; set; }

        public int SubscriptionsCount { get; private set; }
        public int UnsubscriptionsCount { get; private set; }
        public int DisposalsCount { get; private set; }
        public EventHandler? LastRegisteredHandler { get; private set; }

        internal override void SubscribeProcessExit(Process process, EventHandler handler)
        {
            if (FailBeforeSubscription)
            {
                throw new InvalidOperationException("Simulated failure before subscription (e.g. Access Denied on EnableRaisingEvents)");
            }

            SubscriptionsCount++;
            LastRegisteredHandler = handler;
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
        var handler = new TestableProcessExitHandler { FailBeforeSubscription = true };
        using var dummyProc = Process.GetCurrentProcess();

        int exitedCount = 0;
        int instanceExitedCount = 0;
        handler.ProcessExited += _ => exitedCount++;
        handler.ProcessInstanceExited += (_, _, _) => instanceExitedCount++;

        var token = Guid.NewGuid();
        var resultToken = handler.AttachCore(dummyProc, token);

        Assert.Equal(Guid.Empty, resultToken);
        Assert.Equal(0, handler.SubscriptionsCount);
        Assert.Equal(0, handler.UnsubscriptionsCount);
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));
        Assert.Equal(0, exitedCount);
        Assert.Equal(0, instanceExitedCount);
    }

    [Fact]
    public void SetupFailure_AfterPartialSubscription_UnwindsSubscriptionAndReleasesRegistration()
    {
        var handler = new TestableProcessExitHandler { FailAfterSubscription = true };
        using var dummyProc = Process.GetCurrentProcess();

        int exitedCount = 0;
        int instanceExitedCount = 0;
        handler.ProcessExited += _ => exitedCount++;
        handler.ProcessInstanceExited += (_, _, _) => instanceExitedCount++;

        var token = Guid.NewGuid();
        var resultToken = handler.AttachCore(dummyProc, token);

        Assert.Equal(Guid.Empty, resultToken);
        Assert.Equal(1, handler.SubscriptionsCount);
        Assert.Equal(1, handler.UnsubscriptionsCount);
        Assert.Null(handler.LastRegisteredHandler);
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));
        Assert.Equal(0, exitedCount);
        Assert.Equal(0, instanceExitedCount);
    }

    [Fact]
    public void MissingPathExit_ReleasesRegistrationAndCleansUpWithoutRaisingMisleadingEvents()
    {
        var handler = new TestableProcessExitHandler();
        using var dummyProc = Process.GetCurrentProcess();

        int exitedCount = 0;
        int instanceExitedCount = 0;
        handler.ProcessExited += _ => exitedCount++;
        handler.ProcessInstanceExited += (_, _, _) => instanceExitedCount++;

        var token = Guid.NewGuid();
        // Register token with null exePath
        handler.RegisterToken(token, dummyProc.Id, null);
        Assert.Equal(1, handler.ActiveRegistrationCount);
        Assert.True(handler.HasRegistration(token));

        // Setup watcher manually through AttachCore with empty/null path
        var resultToken = handler.AttachCore(dummyProc, token);
        Assert.Equal(token, resultToken);
        Assert.Equal(1, handler.SubscriptionsCount);

        // Overwrite registration with null path to simulate unreportable/unavailable path
        handler.RegisterToken(token, dummyProc.Id, null);

        // Trigger exit callback
        handler.TriggerExitCallback();

        // Registration bookkeeping is released
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));

        // No misleading path-bearing events are raised
        Assert.Equal(0, exitedCount);
        Assert.Equal(0, instanceExitedCount);

        // Owned cleanup is performed
        Assert.Equal(1, handler.DisposalsCount);
    }

    [Fact]
    public void SuccessfulExit_DeliversExactlyOnce_AndReleasesRegistration()
    {
        var handler = new TestableProcessExitHandler();
        using var dummyProc = Process.GetCurrentProcess();

        var deliveredPaths = new List<string>();
        var deliveredTokens = new List<Guid>();
        handler.ProcessExited += path => deliveredPaths.Add(path);
        handler.ProcessInstanceExited += (tok, _, _) => deliveredTokens.Add(tok);

        var token = Guid.NewGuid();
        string expectedPath = @"C:\Games\ValidGame\Game.exe";
        handler.RegisterToken(token, dummyProc.Id, expectedPath);

        // Attach with this token
        var resultToken = handler.AttachCore(dummyProc, token);
        Assert.Equal(token, resultToken);
        Assert.Equal(1, handler.ActiveRegistrationCount);

        // Trigger exit
        handler.TriggerExitCallback();

        // Delivered exactly once and released
        Assert.Single(deliveredPaths);
        Assert.Equal(expectedPath, deliveredPaths[0]);
        Assert.Single(deliveredTokens);
        Assert.Equal(token, deliveredTokens[0]);
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));
        Assert.Equal(1, handler.DisposalsCount);

        // Duplicate callback does not deliver again
        handler.TriggerExitCallback();
        Assert.Single(deliveredPaths);
        Assert.Single(deliveredTokens);
        Assert.Equal(0, handler.ActiveRegistrationCount);
    }

    [Fact]
    public void Cancellation_ReleasesRegistration_AndSuppressesFutureExitDelivery()
    {
        var handler = new TestableProcessExitHandler();
        using var dummyProc = Process.GetCurrentProcess();

        int exitedCount = 0;
        handler.ProcessExited += _ => exitedCount++;

        var token = Guid.NewGuid();
        string expectedPath = @"C:\Games\Game\Game.exe";
        handler.RegisterToken(token, dummyProc.Id, expectedPath);
        handler.AttachCore(dummyProc, token);

        Assert.Equal(1, handler.ActiveRegistrationCount);
        Assert.True(handler.HasRegistration(token));

        // Cancel registration
        handler.CancelRegistration(token);
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));

        // Delayed exit delivery occurs after cancellation
        handler.TriggerExitCallback();
        Assert.Equal(0, exitedCount);
        Assert.Equal(0, handler.ActiveRegistrationCount);
    }

    [Fact]
    public void SubscriberException_DoesNotSkipResourceCleanupOrProcessDisposal()
    {
        var handler = new TestableProcessExitHandler();
        using var dummyProc = Process.GetCurrentProcess();

        var token = Guid.NewGuid();
        string expectedPath = @"C:\Games\CrashySubscriber\Game.exe";
        handler.RegisterToken(token, dummyProc.Id, expectedPath);
        handler.AttachCore(dummyProc, token);

        handler.ProcessExited += _ => throw new InvalidOperationException("Subscriber blew up!");

        // Trigger exit callback — subscriber exception is rethrown, not silently swallowed
        var ex = Assert.Throws<InvalidOperationException>(() => handler.TriggerExitCallback());
        Assert.Equal("Subscriber blew up!", ex.Message);

        // Resource release and disposal must have occurred in finally
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.False(handler.HasRegistration(token));
        Assert.Equal(1, handler.DisposalsCount);
    }

    [Fact]
    public void SynchronousExit_DeliversAndCleansUpDuringSetup()
    {
        var handler = new TestableProcessExitHandler { SimulateSynchronousExit = true };
        using var dummyProc = Process.GetCurrentProcess();

        var deliveredTokens = new List<Guid>();
        handler.ProcessInstanceExited += (tok, _, _) => deliveredTokens.Add(tok);

        var token = Guid.NewGuid();
        string expectedPath = @"C:\Games\SyncExitGame\Game.exe";
        handler.RegisterToken(token, dummyProc.Id, expectedPath);

        var resultToken = handler.AttachCore(dummyProc, token);

        // Setup succeeded and delivered exit during AttachCore
        Assert.Equal(token, resultToken);
        Assert.Single(deliveredTokens);
        Assert.Equal(token, deliveredTokens[0]);
        Assert.Equal(0, handler.ActiveRegistrationCount);
        Assert.Equal(1, handler.DisposalsCount);
    }
}
