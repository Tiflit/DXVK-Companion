using System.Net.Http;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class ReleaseInterprocessCoordinationTests
{
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
        var client = acquisition ? null : Latest(fixture, store, new(coordination));
        fixture.Acquirer.ProcessLeaseCoordinator = new OperationLeaseCoordinator(coordination);
        using (var held = new OperationLeaseCoordinator(coordination).TryAcquireStoreLease(resource))
        {
            Assert.NotNull(held);
            cancel.CancelAfter(TimeSpan.FromMilliseconds(50));
            if (acquisition) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Acquirer.AcquireAsync("v2.5", cancel.Token));
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client!.FetchLatestReleaseAsync(cancel.Token));
            Assert.Empty(fixture.Requests);
            Assert.False(File.Exists(store.CacheFile));
            Assert.False(File.Exists(store.AttemptFile));
        }
        if (acquisition) Assert.False((await fixture.Acquirer.AcquireAsync("v2.5", deadline.Token)).Reused);
        else Assert.Equal("v2.5", (await client!.FetchLatestReleaseAsync(deadline.Token))?.Version);
        Assert.Equal(acquisition ? 2 : 1, fixture.Requests.Count);
    }
}
