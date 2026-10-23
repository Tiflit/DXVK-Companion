using System.Net.Http;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class ReleaseInterprocessCoordinationTests
{
    private sealed class RendezvousCoordinator(OperationLeaseCoordinator inner) : IOperationLeaseCoordinator
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string CoordinationDirectory => inner.CoordinationDirectory;
        public string? LastAcquisitionError => inner.LastAcquisitionError;
        public async ValueTask<IOperationLease?> TryAcquireStoreLeaseAsync(string path, TimeSpan timeout = default, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Continue.Task.WaitAsync(ct);
            return await inner.TryAcquireStoreLeaseAsync(path, timeout, ct);
        }
        public IOperationLease? TryAcquireStoreLease(string path, TimeSpan timeout = default) => throw new NotSupportedException();
        public ValueTask<IOperationLease?> TryAcquireOperationScopeAsync(string root, string? storePath = null, TimeSpan timeout = default, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IOperationLease?> TryAcquireOperationScopeAsync(string root, IEnumerable<string>? paths, TimeSpan timeout = default, CancellationToken ct = default) => throw new NotSupportedException();
        public IOperationLease? TryAcquireOperationScope(string root, string? path = null, TimeSpan timeout = default) => throw new NotSupportedException();
        public IOperationLease? TryAcquireOperationScope(string root, IEnumerable<string>? paths, TimeSpan timeout = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaseRecheck_ObservesPeerCommitOrPublication_WithoutDuplicateHttp(bool acquisition)
    {
        using var fixture = new OfficialAcquisitionFixture();
        using var peer = new OfficialAcquisitionFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string coordination = fixture.Directory.GetPath("coordination");
        string destination = Path.Combine(fixture.Source, "v2.5");
        var store = new CacheStore(fixture.Directory.GetPath("latest.json"));
        var now = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
        if (acquisition)
        {
            peer.Acquirer.ProcessLeaseCoordinator = new OperationLeaseCoordinator(peer.Directory.GetPath("coordination"));
            await peer.Acquirer.AcquireAsync("v2.5", deadline.Token);
        }
        else store.SaveCachedRelease(new ReleaseInfo { Version = "v2.4" }, "\"old\"", now.AddDays(-2), now.AddDays(-2));
        var gate = new RendezvousCoordinator(new OperationLeaseCoordinator(coordination));
        fixture.Acquirer.ProcessLeaseCoordinator = gate;
        fixture.Override = acquisition ? null : (_, _) => Task.FromResult(OfficialAcquisitionFixture.Json(
            OfficialAcquisitionFixture.Metadata("v2.5", fixture.Archives["v2.5"])));
        var client = new DxvkGithubClient(fixture.Http, store, () => now) { ProcessLeaseCoordinator = gate };
        Task<DxvkReleaseAcquirer.AcquisitionResult>? acquiring = null;
        Task<ReleaseInfo?>? checking = null;
        using (var held = new OperationLeaseCoordinator(coordination).TryAcquireStoreLease(acquisition ? destination : store.CacheFile))
        {
            Assert.NotNull(held);
            if (acquisition) acquiring = fixture.Acquirer.AcquireAsync("v2.5", deadline.Token);
            else checking = client.FetchLatestReleaseAsync(deadline.Token);
            await gate.Entered.Task.WaitAsync(deadline.Token);
            // Simulate a cooperating peer's commit while it owns the real OS lease.
            if (acquisition)
            {
                string source = Path.Combine(peer.Source, "v2.5");
                foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
            }
            else store.SaveCachedRelease(new ReleaseInfo { Version = "v2.5" }, "\"peer\"", now, now);
        }
        gate.Continue.TrySetResult();
        if (acquisition)
        {
            Assert.True((await acquiring!.WaitAsync(deadline.Token)).Reused);
            Assert.True(DxvkReleaseAcquirer.ValidateManifest(destination, "v2.5", out var verified));
            Assert.True(verified);
        }
        else
        {
            Assert.Equal("v2.5", (await checking!.WaitAsync(deadline.Token))?.Version);
            Assert.Equal("\"peer\"", store.LoadCachedRelease()?.ETag);
            Assert.Equal(now, store.LoadCachedRelease()?.LastAttemptAt);
        }
        Assert.Empty(fixture.Requests);
    }

    private static DxvkGithubClient Latest(OfficialAcquisitionFixture fixture, CacheStore store,
        OperationLeaseCoordinator coordinator, Func<DateTime>? clock = null)
    {
        fixture.Override = (_, _) => Task.FromResult(OfficialAcquisitionFixture.Json(
            OfficialAcquisitionFixture.Metadata("v2.5", fixture.Archives["v2.5"])));
        return new(fixture.Http, store, clock) { ProcessLeaseCoordinator = coordinator };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Latest_BusyLease_PreservesFallbackWithoutAttempt_ThenRetries(bool prior)
    {
        using var fixture = new OfficialAcquisitionFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string path = fixture.Directory.GetPath("latest.json");
        string coordination = fixture.Directory.GetPath("coordination");
        var now = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
        var store = new CacheStore(path);
        if (prior) store.SaveCachedRelease(new ReleaseInfo { Version = "v2.4" }, "\"old\"", now.AddDays(-2), now.AddDays(-2));
        byte[]? before = prior ? File.ReadAllBytes(path) : null;
        var client = Latest(fixture, store, new(coordination), () => now);
        using (var held = new OperationLeaseCoordinator(coordination).TryAcquireStoreLease(path))
        {
            Assert.NotNull(held);
            Assert.Equal(prior ? "v2.4" : null, (await client.FetchLatestReleaseAsync(deadline.Token))?.Version);
            Assert.Empty(fixture.Requests);
            if (prior) Assert.Equal(before, File.ReadAllBytes(path));
            else Assert.False(File.Exists(path));
            Assert.False(File.Exists(store.AttemptFile));
        }
        Assert.Equal("v2.5", (await client.FetchLatestReleaseAsync(deadline.Token))?.Version);
        Assert.Single(fixture.Requests);
        Assert.Equal(now, store.LoadCachedRelease()?.LastAttemptAt);
    }

    [Fact]
    public async Task Acquisition_BusyLease_NoHttpOrOverwrite_ThenRetries()
    {
        using var fixture = new OfficialAcquisitionFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string coordination = fixture.Directory.GetPath("coordination");
        string destination = Path.Combine(fixture.Source, "v2.5");
        string sentinel = fixture.Directory.CreateFile("sources/v2.4/retained.txt", "older");
        fixture.Acquirer.ProcessLeaseCoordinator = new OperationLeaseCoordinator(coordination);
        using (var held = new OperationLeaseCoordinator(coordination).TryAcquireStoreLease(destination))
        {
            Assert.NotNull(held);
            var failure = await Assert.ThrowsAsync<IOException>(() => fixture.Acquirer.AcquireAsync("v2.5", deadline.Token));
            Assert.Contains("retry", failure.Message);
            Assert.DoesNotContain(fixture.Directory.RootPath, failure.Message);
            Assert.Empty(fixture.Requests);
            Assert.False(Directory.Exists(destination));
            Assert.Equal("older", File.ReadAllText(sentinel));
        }
        Assert.False((await fixture.Acquirer.AcquireAsync("v2.5", deadline.Token)).Reused);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(destination, "v2.5", out _));
        Assert.Equal("older", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task CompleteLocalRelease_StillReusableWhileCoordinationIsUnavailable()
    {
        using var fixture = new OfficialAcquisitionFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        fixture.Acquirer.ProcessLeaseCoordinator = new OperationLeaseCoordinator(fixture.Directory.GetPath("coordination"));
        await fixture.Acquirer.AcquireAsync("v2.5", deadline.Token);
        string blocked = fixture.Directory.CreateFile("blocked-coordination", "retain");
        var other = new DxvkReleaseAcquirer(fixture.Github, fixture.Source)
        {
            ProcessLeaseCoordinator = new OperationLeaseCoordinator(blocked)
        };
        fixture.Offline = true;
        Assert.True((await other.AcquireAsync("v2.5", deadline.Token)).Reused);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal("retain", File.ReadAllText(blocked));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableCoordination_NoHttpOrInventedAttempt_ThenRepairCanRetry(bool acquisition)
    {
        using var fixture = new OfficialAcquisitionFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string blocked = fixture.Directory.CreateFile("blocked-coordination", "retain");
        var unavailable = new OperationLeaseCoordinator(blocked);
        var usable = new OperationLeaseCoordinator(fixture.Directory.GetPath("coordination"));
        var store = new CacheStore(fixture.Directory.GetPath("latest.json"));
        if (acquisition)
        {
            fixture.Acquirer.ProcessLeaseCoordinator = unavailable;
            await Assert.ThrowsAsync<IOException>(() => fixture.Acquirer.AcquireAsync("v2.5", deadline.Token));
            Assert.Empty(fixture.Requests);
            fixture.Acquirer.ProcessLeaseCoordinator = usable;
            Assert.False((await fixture.Acquirer.AcquireAsync("v2.5", deadline.Token)).Reused);
            Assert.Equal(2, fixture.Requests.Count);
        }
        else
        {
            var client = Latest(fixture, store, unavailable);
            Assert.Null(await client.FetchLatestReleaseAsync(deadline.Token));
            Assert.Empty(fixture.Requests);
            Assert.False(File.Exists(store.CacheFile));
            Assert.False(File.Exists(store.AttemptFile));
            client.ProcessLeaseCoordinator = usable;
            Assert.Equal("v2.5", (await client.FetchLatestReleaseAsync(deadline.Token))?.Version);
            Assert.Single(fixture.Requests);
        }
        Assert.Equal("retain", File.ReadAllText(blocked));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWaitingForProcessLease_NoHttpOrAttempt_AndCanRetry(bool acquisition)
    {
        using var fixture = new OfficialAcquisitionFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        string coordination = fixture.Directory.GetPath("coordination");
        var store = new CacheStore(fixture.Directory.GetPath("latest.json"));
        string resource = acquisition ? Path.Combine(fixture.Source, "v2.5") : store.CacheFile;
        int observedContentions = 0;
        bool cancelledBeforeContention = false;
        var subjectCoordinator = new OperationLeaseCoordinator(coordination)
        {
            StoreContentionObserved = () =>
            {
                observedContentions++;
                cancelledBeforeContention = cancel.IsCancellationRequested;
                cancel.Cancel();
            }
        };
        var client = acquisition ? null : Latest(fixture, store, subjectCoordinator);
        fixture.Acquirer.ProcessLeaseCoordinator = subjectCoordinator;
        using (var held = new OperationLeaseCoordinator(coordination).TryAcquireStoreLease(resource))
        {
            Assert.NotNull(held);
            if (acquisition) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Acquirer.AcquireAsync("v2.5", cancel.Token));
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client!.FetchLatestReleaseAsync(cancel.Token));
            Assert.Equal(1, observedContentions);
            Assert.False(cancelledBeforeContention, "Cancellation must be triggered by observed real store contention, not the watchdog.");
            Assert.Empty(fixture.Requests);
            Assert.False(File.Exists(store.CacheFile));
            Assert.False(File.Exists(store.AttemptFile));
        }
        subjectCoordinator.StoreContentionObserved = null;
        if (acquisition) Assert.False((await fixture.Acquirer.AcquireAsync("v2.5", deadline.Token)).Reused);
        else Assert.Equal("v2.5", (await client!.FetchLatestReleaseAsync(deadline.Token))?.Version);
        Assert.Equal(acquisition ? 2 : 1, fixture.Requests.Count);
    }
}
