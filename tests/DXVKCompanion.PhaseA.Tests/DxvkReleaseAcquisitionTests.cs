using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseATests;

internal sealed class OfficialAcquisitionFixture : IDisposable
{
    internal readonly SyntheticTestDirectory Directory = new();
    internal readonly ConcurrentQueue<string> Requests = new();
    internal readonly Dictionary<string, byte[]> Archives = new();
    internal readonly HttpClient Http;
    internal readonly DxvkGithubClient Github;
    internal readonly DxvkReleaseAcquirer Acquirer;
    internal bool Offline;
    internal bool IncludeDigest = true;
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Override;
    internal string Source => Path.Combine(Directory.RootPath, "sources");

    internal OfficialAcquisitionFixture(params string[] tags)
    {
        foreach (string tag in tags.Length == 0 ? new[] { "v2.5" } : tags) Archives[tag] = Archive(tag);
        Http = new HttpClient(new Handler(async (request, token) =>
        {
            Requests.Enqueue(request.RequestUri!.AbsoluteUri);
            if (Offline) throw new HttpRequestException("Synthetic offline fixture.");
            if (Override != null) return await Override(request, token);
            string tag = request.RequestUri.AbsolutePath.Split('/')[^1];
            if (Archives.TryGetValue(tag, out var bytes)) return Json(Metadata(tag, bytes, IncludeDigest));
            foreach (var entry in Archives)
                if (request.RequestUri.AbsoluteUri == AssetUrl(entry.Key)) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(entry.Value) };
            return new(HttpStatusCode.NotFound);
        }));
        Github = new DxvkGithubClient(Http, new CacheStore(Path.Combine(Directory.RootPath, "cache.json")));
        Acquirer = new DxvkReleaseAcquirer(Github, Source);
    }

    internal static string AssetUrl(string tag) => $"https://github.com/doitsujin/dxvk/releases/download/{tag}/dxvk-{tag[1..]}.tar.gz";

    internal static JsonObject Metadata(string tag, byte[] bytes, bool digest = true) => new()
    {
        ["id"] = 100, ["tag_name"] = tag, ["draft"] = false, ["prerelease"] = false,
        ["assets"] = new JsonArray(new JsonObject
        {
            ["id"] = 200, ["name"] = $"dxvk-{tag[1..]}.tar.gz", ["size"] = bytes.Length,
            ["browser_download_url"] = AssetUrl(tag),
            ["digest"] = digest ? "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() : null
        })
    };

    internal static HttpResponseMessage Json(JsonNode node) => new(HttpStatusCode.OK)
        { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    internal static byte[] Archive(string tag, params (string Name, TarEntryType Type, byte[] Data)[] files)
    {
        if (files.Length == 0) files = new[]
        {
            ($"dxvk-{tag[1..]}/x64/d3d11.dll", TarEntryType.RegularFile, Encoding.UTF8.GetBytes(tag + ":d3d11")),
            ($"dxvk-{tag[1..]}/x64/dxgi.dll", TarEntryType.RegularFile, Encoding.UTF8.GetBytes(tag + ":dxgi"))
        };
        using var result = new MemoryStream();
        using (var gzip = new GZipStream(result, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            foreach (var file in files)
            {
                var entry = new PaxTarEntry(file.Type, file.Name);
                if (file.Type == TarEntryType.RegularFile) entry.DataStream = new MemoryStream(file.Data);
                if (file.Type == TarEntryType.SymbolicLink || file.Type == TarEntryType.HardLink) entry.LinkName = "../../sentinel";
                tar.WriteEntry(entry);
            }
        }
        return result.ToArray();
    }

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    public void Dispose() { Http.Dispose(); Directory.Dispose(); }
}

public sealed class DxvkReleaseAcquisitionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctAcquirers_SameDestination_ShareRequestsAndRetainCompletePayload(bool alias)
    {
        using var fixture = new OfficialAcquisitionFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        fixture.Override = async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return OfficialAcquisitionFixture.Json(OfficialAcquisitionFixture.Metadata("v2.5", fixture.Archives["v2.5"]));
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Archives["v2.5"]) };
        };
        string sentinel = fixture.Directory.CreateFile("sources/v2.4/retained.txt", "older-source");
        string root = alias ? Path.Combine(fixture.Source, ".") + Path.DirectorySeparatorChar : fixture.Source;
        if (alias && OperatingSystem.IsWindows()) root = root.ToUpperInvariant();
        var leader = fixture.Acquirer.AcquireAsync("v2.5", deadline.Token);
        var calls = new List<Task<DxvkReleaseAcquirer.AcquisitionResult>> { leader };
        try
        {
            await started.Task.WaitAsync(deadline.Token);
            int arrivals = 0;
            for (int i = 0; i < 6; i++)
            {
                var client = new DxvkGithubClient(fixture.Http, new CacheStore(fixture.Directory.GetPath($"cache-{i}.json")));
                var other = new DxvkReleaseAcquirer(client, root)
                {
                    BeforeGateWait = () => { if (Interlocked.Increment(ref arrivals) == 6) queued.TrySetResult(); }
                };
                calls.Add(other.AcquireAsync("v2.5", deadline.Token));
            }
            await queued.Task.WaitAsync(deadline.Token);
            Assert.Single(fixture.Requests); // Leader's exact-tag metadata request, still blocked.
        }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(calls).WaitAsync(deadline.Token);
        Assert.Single(results, result => !result.Reused);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal("older-source", File.ReadAllText(sentinel));
        Assert.Equal("v2.5:d3d11", File.ReadAllText(Path.Combine(fixture.Source, "v2.5", "x64", "d3d11.dll")));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(fixture.Source, "v2.5"), "v2.5", out var verified));
        Assert.True(verified);
        fixture.Offline = true;
        Assert.True((await new DxvkReleaseAcquirer(fixture.Github, root).AcquireAsync("v2.5", deadline.Token)).Reused);
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctAcquirers_CancelledWaiter_DoesNotCancelLeaderOrFollowingWaiter(bool cancelBeforeWait)
    {
        using var fixture = new OfficialAcquisitionFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        fixture.Override = async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return OfficialAcquisitionFixture.Json(OfficialAcquisitionFixture.Metadata("v2.5", fixture.Archives["v2.5"]));
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Archives["v2.5"]) };
        };
        var leader = fixture.Acquirer.AcquireAsync("v2.5", deadline.Token);
        Task<DxvkReleaseAcquirer.AcquisitionResult>? follower = null;
        try
        {
            await started.Task.WaitAsync(deadline.Token);
            using var cancel = new CancellationTokenSource();
            var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new DxvkReleaseAcquirer(fixture.Github, fixture.Source)
            {
                BeforeGateWait = () => { attempted.TrySetResult(); if (cancelBeforeWait) cancel.Cancel(); }
            };
            var cancelledCall = cancelled.AcquireAsync("v2.5", cancel.Token);
            await attempted.Task.WaitAsync(deadline.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledCall);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var live = new DxvkReleaseAcquirer(fixture.Github, fixture.Source) { BeforeGateWait = () => entered.TrySetResult() };
            follower = live.AcquireAsync("v2.5", deadline.Token);
            await entered.Task.WaitAsync(deadline.Token);
            Assert.Single(fixture.Requests);
        }
        finally { release.TrySetResult(); }
        Assert.False((await leader.WaitAsync(deadline.Token)).Reused);
        Assert.NotNull(follower);
        Assert.True((await follower.WaitAsync(deadline.Token)).Reused);
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task DistinctAcquirers_CancelledLeader_LiveWaiterRetriesAndPublishes()
    {
        using var fixture = new OfficialAcquisitionFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        int metadata = 0;
        fixture.Override = async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                if (Interlocked.Increment(ref metadata) == 1)
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                return OfficialAcquisitionFixture.Json(OfficialAcquisitionFixture.Metadata("v2.5", fixture.Archives["v2.5"]));
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Archives["v2.5"]) };
        };
        var leader = fixture.Acquirer.AcquireAsync("v2.5", cancel.Token);
        await started.Task.WaitAsync(deadline.Token);
        var other = new DxvkReleaseAcquirer(fixture.Github, fixture.Source) { BeforeGateWait = () => entered.TrySetResult() };
        var follower = other.AcquireAsync("v2.5", deadline.Token);
        try { await entered.Task.WaitAsync(deadline.Token); }
        finally { cancel.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader);
        Assert.False((await follower.WaitAsync(deadline.Token)).Reused);
        Assert.Equal(3, fixture.Requests.Count);
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(fixture.Source, "v2.5"), "v2.5", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctAcquirers_UnrelatedDestinations_DoNotBlockEachOther(bool differentLibraries)
    {
        using var first = new OfficialAcquisitionFixture("v2.5", "v2.6");
        using var second = new OfficialAcquisitionFixture("v2.5");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals = 0;
        async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token)
        {
            string tag = request.RequestUri!.Host == "api.github.com" ? request.RequestUri.AbsolutePath.Split('/')[^1]
                : request.RequestUri.AbsolutePath.Split('/')[^2];
            var bytes = first.Archives[tag];
            if (request.RequestUri.Host == "api.github.com")
            {
                if (Interlocked.Increment(ref arrivals) == 2) bothStarted.TrySetResult();
                await release.Task.WaitAsync(token);
                return OfficialAcquisitionFixture.Json(OfficialAcquisitionFixture.Metadata(tag, bytes));
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
        first.Override = Respond;
        second.Override = Respond;
        var other = differentLibraries ? second.Acquirer : new DxvkReleaseAcquirer(first.Github, first.Source);
        var a = first.Acquirer.AcquireAsync("v2.5", deadline.Token);
        var b = other.AcquireAsync(differentLibraries ? "v2.5" : "v2.6", deadline.Token);
        try { await bothStarted.Task.WaitAsync(deadline.Token); }
        finally { release.TrySetResult(); }
        Assert.False((await a.WaitAsync(deadline.Token)).Reused);
        Assert.False((await b.WaitAsync(deadline.Token)).Reused);
        Assert.Equal(4, first.Requests.Count + second.Requests.Count);
    }

    [Fact]
    public async Task DistinctAcquirers_PublicationFailure_ReleasesGateForRetryAndPreservesSources()
    {
        using var fixture = new OfficialAcquisitionFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string sentinel = fixture.Directory.CreateFile("sources/v2.4/retained.txt", "older-source");
        fixture.Acquirer.BeforePublication = _ => throw new InvalidDataException("Synthetic publication refusal.");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Acquirer.AcquireAsync("v2.5", deadline.Token));
        Assert.False(System.IO.Directory.Exists(Path.Combine(fixture.Source, "v2.5")));
        var other = new DxvkReleaseAcquirer(new DxvkGithubClient(fixture.Http,
            new CacheStore(fixture.Directory.GetPath("other-cache.json"))), fixture.Source);
        Assert.False((await other.AcquireAsync("v2.5", deadline.Token)).Reused);
        Assert.Equal(4, fixture.Requests.Count);
        Assert.Equal("older-source", File.ReadAllText(sentinel));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(fixture.Source, "v2.5"), "v2.5", out _));
    }

    [Fact]
    public async Task DistinctAcquirers_PreWaitException_DoesNotLeaveGateHeld()
    {
        using var fixture = new OfficialAcquisitionFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        fixture.Acquirer.BeforeGateWait = () => throw new InvalidOperationException("Synthetic pre-wait failure.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Acquirer.AcquireAsync("v2.5", deadline.Token));
        Assert.Empty(fixture.Requests);
        Assert.False((await new DxvkReleaseAcquirer(fixture.Github, fixture.Source).AcquireAsync("v2.5", deadline.Token)).Reused);
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Theory]
    [InlineData("x86", "x32")]
    [InlineData("amd64", "x64")]
    [InlineData("unknown", null)]
    public async Task Installer_UsesSameArchitectureNormalizationAsEligibility_UnknownDoesNotAcquire(string architecture, string? expectedDirectory)
    {
        using var fixture = new OfficialAcquisitionFixture();
        using var game = new SyntheticTestDirectory();
        if (expectedDirectory != null)
            fixture.Archives["v2.5"] = OfficialAcquisitionFixture.Archive("v2.5",
                ($"dxvk-2.5/{expectedDirectory}/d3d11.dll", TarEntryType.RegularFile, Encoding.UTF8.GetBytes("matching-architecture")),
                ($"dxvk-2.5/{expectedDirectory}/dxgi.dll", TarEntryType.RegularFile, Encoding.UTF8.GetBytes("matching-dxgi")));
        var store = new GameLibraryStore(fixture.Directory.GetPath("library.json"), fixture.Directory.GetPath("backups"));
        var installer = SyntheticSafetyFixture.CreateInstaller(fixture.Http,
            new MultiFileTransactionEngine(fixture.Directory.GetPath("backups")), store, fixture.Source);
        var profile = new GameProfile(game.CreateFile("Game.exe", "synthetic")) { Api = GraphicsApi.DX11, Architecture = architecture };
        bool result = await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "v2.5" });
        Assert.Equal(expectedDirectory != null, result);
        if (expectedDirectory == null)
        {
            Assert.Empty(fixture.Requests);
            Assert.False(File.Exists(game.GetPath("d3d11.dll")));
        }
        else
        {
            Assert.Equal("matching-architecture", File.ReadAllText(game.GetPath("d3d11.dll")));
            Assert.True(File.Exists(Path.Combine(fixture.Source, "v2.5", expectedDirectory, "d3d11.dll")));
        }
    }

    [Fact]
    public async Task Installer_SharedAcquirer_IgnoresAssertedUrl_PublishesVerifiedPayload_AndReconstructedGameReusesOffline()
    {
        using var fixture = new OfficialAcquisitionFixture();
        using var gameA = new SyntheticTestDirectory();
        using var gameB = new SyntheticTestDirectory();
        string libraryPath = fixture.Directory.GetPath("library.json");
        string backups = fixture.Directory.GetPath("backups");
        var store = new GameLibraryStore(libraryPath, backups);
        var installer = SyntheticSafetyFixture.CreateInstaller(fixture.Http,
            new MultiFileTransactionEngine(backups), store, fixture.Source);
        var profileA = new GameProfile(gameA.CreateFile("Game.exe", "synthetic")) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var untrusted = new ReleaseInfo { Version = "v2.5", DownloadUrl = "https://example.invalid/asserted.tar.gz" };
        var acquisition = installer.GetReleaseAcquirer(fixture.Github).AcquireAsync("v2.5");
        var installation = installer.ApplyToGameAsync(profileA, untrusted);
        await Task.WhenAll(acquisition, installation);
        Assert.True(await installation, SyntheticSafetyFixture.RefusalCategory(installer.LastRefusalReason));
        Assert.Equal(2, fixture.Requests.Count); // One exact-tag lookup and one archive, shared by UI/install paths.
        Assert.All(fixture.Requests, url => Assert.DoesNotContain("example.invalid", url));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(fixture.Source, "v2.5"), "v2.5", out var digestVerified));
        Assert.True(digestVerified);
        Assert.Equal("v2.5:d3d11", File.ReadAllText(gameA.GetPath("d3d11.dll")));

        fixture.Offline = true;
        var reloadedStore = new GameLibraryStore(libraryPath, backups);
        var reloadedInstaller = SyntheticSafetyFixture.CreateInstaller(fixture.Http,
            new MultiFileTransactionEngine(backups), reloadedStore, fixture.Source);
        var profileB = new GameProfile(gameB.CreateFile("Game.exe", "synthetic")) { Api = GraphicsApi.DX11, Architecture = "x64" };
        Assert.True(await reloadedInstaller.ApplyToGameAsync(profileB, untrusted), SyntheticSafetyFixture.RefusalCategory(reloadedInstaller.LastRefusalReason));
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal("v2.5:d3d11", File.ReadAllText(gameB.GetPath("d3d11.dll")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Installer_FailedAcquisition_PreservesGameAndOtherRelease_NoPublishedPartial_ThenRetries(bool digestMismatch)
    {
        using var fixture = new OfficialAcquisitionFixture();
        using var game = new SyntheticTestDirectory();
        string native = game.CreateFile("d3d11.dll", "native-original");
        string config = game.CreateFile("dxvk.conf", "# untouched user configuration");
        string older = Path.Combine(fixture.Source, "v2.4", "x64", "d3d11.dll");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(older)!);
        File.WriteAllText(older, "retained-older-version");
        var store = new GameLibraryStore(fixture.Directory.GetPath("library.json"), fixture.Directory.GetPath("backups"));
        var installer = SyntheticSafetyFixture.CreateInstaller(fixture.Http,
            new MultiFileTransactionEngine(fixture.Directory.GetPath("backups")), store, fixture.Source);
        var profile = new GameProfile(game.CreateFile("Game.exe", "synthetic")) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var release = new ReleaseInfo { Version = "v2.5", DownloadUrl = "https://example.invalid/asserted.tar.gz" };
        fixture.Override = (request, _) =>
        {
            var metadata = OfficialAcquisitionFixture.Metadata("v2.5", fixture.Archives["v2.5"]);
            if (digestMismatch) metadata["assets"]![0]!["digest"] = "sha256:" + new string('0', 64);
            return Task.FromResult(request.RequestUri!.Host == "api.github.com" ? OfficialAcquisitionFixture.Json(metadata)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(digestMismatch ? fixture.Archives["v2.5"] : fixture.Archives["v2.5"][..^8]) });
        };
        Assert.False(await installer.ApplyToGameAsync(profile, release));
        Assert.Equal("native-original", File.ReadAllText(native));
        Assert.Equal("# untouched user configuration", File.ReadAllText(config));
        Assert.False(File.Exists(game.GetPath("dxgi.dll")));
        Assert.Equal("retained-older-version", File.ReadAllText(older));
        Assert.False(System.IO.Directory.Exists(Path.Combine(fixture.Source, "v2.5")));
        fixture.Override = null;
        Assert.True(await installer.ApplyToGameAsync(profile, release), SyntheticSafetyFixture.RefusalCategory(installer.LastRefusalReason));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(fixture.Source, "v2.5"), "v2.5", out _));
        Assert.Equal("retained-older-version", File.ReadAllText(older));
    }

    [Fact]
    public async Task Installer_PartialExistingSource_RefusesWithoutNetworkOrOverwrite()
    {
        using var fixture = new OfficialAcquisitionFixture();
        using var game = new SyntheticTestDirectory();
        string partial = Path.Combine(fixture.Source, "v2.5", "x64", "d3d11.dll");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        File.WriteAllText(partial, "partial-sentinel");
        var store = new GameLibraryStore(fixture.Directory.GetPath("library.json"), fixture.Directory.GetPath("backups"));
        var installer = SyntheticSafetyFixture.CreateInstaller(fixture.Http,
            new MultiFileTransactionEngine(fixture.Directory.GetPath("backups")), store, fixture.Source);
        var profile = new GameProfile(game.CreateFile("Game.exe", "synthetic")) { Api = GraphicsApi.DX11, Architecture = "x64" };
        Assert.False(await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "v2.5" }));
        Assert.Empty(fixture.Requests);
        Assert.Equal("partial-sentinel", File.ReadAllText(partial));
        Assert.False(File.Exists(game.GetPath("d3d11.dll")));
    }

    [Fact]
    public async Task Installer_RechecksUnknownActivityAfterAcquisition_WithoutGameMutation()
    {
        using var fixture = new OfficialAcquisitionFixture();
        using var game = new SyntheticTestDirectory();
        var store = new GameLibraryStore(fixture.Directory.GetPath("library.json"), fixture.Directory.GetPath("backups"));
        var installer = SyntheticSafetyFixture.CreateInstaller(fixture.Http,
            new MultiFileTransactionEngine(fixture.Directory.GetPath("backups")), store, fixture.Source);
        fixture.Override = (request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Task.FromResult(OfficialAcquisitionFixture.Json(OfficialAcquisitionFixture.Metadata("v2.5", fixture.Archives["v2.5"])));
            installer.ActivityInspector = new ProcessActivityInspector(processEnumerator: () => throw new InvalidOperationException("Synthetic unknown activity"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Archives["v2.5"]) });
        };
        var profile = new GameProfile(game.CreateFile("Game.exe", "synthetic")) { Api = GraphicsApi.DX11, Architecture = "x64" };
        Assert.False(await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "v2.5", DownloadUrl = "https://example.invalid/asserted.tar.gz" }));
        Assert.Equal("ActivityUncertain", SyntheticSafetyFixture.RefusalCategory(installer.LastRefusalReason));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(fixture.Source, "v2.5"), "v2.5", out _));
        Assert.False(File.Exists(game.GetPath("d3d11.dll")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Acquire_ExactBytesManifest_ConcurrentAndReconstructedReuse_NoFurtherHttp(bool digest)
    {
        using var fixture = new OfficialAcquisitionFixture();
        fixture.IncludeDigest = digest;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Acquirer.AcquireAsync("v2.5")));
        Assert.All(results, r => Assert.Equal(digest, r.UpstreamDigestVerified));
        Assert.Single(results, r => !r.Reused);
        Assert.Equal(2, fixture.Requests.Count);
        string version = Path.Combine(fixture.Source, "v2.5");
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:d3d11"), File.ReadAllBytes(Path.Combine(version, "x64", "d3d11.dll")));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(version, "v2.5", out var verified));
        Assert.Equal(digest, verified);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(version, DxvkReleaseAcquirer.ManifestName)))!;
        Assert.Equal("v2.5", (string?)manifest["Origin"]!["Version"]);
        Assert.Equal(new[] { "x64/d3d11.dll", "x64/dxgi.dll" }, manifest["Files"]!.AsArray().Select(f => (string)f!["Path"]!));
        fixture.Offline = true;
        Assert.True((await new DxvkReleaseAcquirer(fixture.Github, fixture.Source).AcquireAsync("v2.5")).Reused);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal(new[] { "v2.5" }, DxvkLocalReleaseLibrary.GetAvailableVersions(fixture.Source, GraphicsApi.DX11, "x64"));
        Assert.Empty(DxvkLocalReleaseLibrary.GetAvailableVersions(fixture.Source, GraphicsApi.DX11, "x32"));
    }

    [Theory]
    [InlineData("dll")]
    [InlineData("manifest")]
    [InlineData("missing-manifest")]
    [InlineData("extra-dll")]
    public async Task TamperedAcquiredRelease_RefusedWithoutLegacyDowngradeOrNetwork(string corruption)
    {
        using var fixture = new OfficialAcquisitionFixture();
        await fixture.Acquirer.AcquireAsync("v2.5");
        string version = Path.Combine(fixture.Source, "v2.5");
        switch (corruption)
        {
            case "dll": File.WriteAllText(Path.Combine(version, "x64", "d3d11.dll"), "tampered"); break;
            case "manifest": File.WriteAllText(Path.Combine(version, DxvkReleaseAcquirer.ManifestName), "{}"); break;
            case "missing-manifest": File.Delete(Path.Combine(version, DxvkReleaseAcquirer.ManifestName)); break;
            case "extra-dll": File.WriteAllText(Path.Combine(version, "x64", "d3d9.dll"), "extra"); break;
        }
        fixture.Offline = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Acquirer.AcquireAsync("v2.5"));
        Assert.Empty(DxvkLocalReleaseLibrary.GetAvailableVersions(fixture.Source, GraphicsApi.DX11, "x64"));
        Assert.Equal(2, fixture.Requests.Count);
    }

    public static IEnumerable<object[]> InvalidArchives()
    {
        string root = "dxvk-2.5/x64/";
        byte[] content = { 1, 2, 3 };
        yield return new object[] { "missing", OfficialAcquisitionFixture.Archive("v2.5", (root + "d3d11.dll", TarEntryType.RegularFile, content)) };
        yield return new object[] { "empty", OfficialAcquisitionFixture.Archive("v2.5", (root + "d3d11.dll", TarEntryType.RegularFile, Array.Empty<byte>()), (root + "dxgi.dll", TarEntryType.RegularFile, content)) };
        foreach (string name in new[] { "../sentinel", "dxvk-2.5/../sentinel", "dxvk-2.5/x64/../../sentinel", "dxvk-2.5/x64/D3D11.dll", "other/x64/d3d11.dll", "dxvk-2.5/x64/d3d11.dll:stream", "dxvk-2.5\\x64\\d3d11.dll" })
            yield return new object[] { "unsafe", OfficialAcquisitionFixture.Archive("v2.5", (name, TarEntryType.RegularFile, content)) };
        foreach (var type in new[] { TarEntryType.SymbolicLink, TarEntryType.HardLink, TarEntryType.Fifo })
            yield return new object[] { "special", OfficialAcquisitionFixture.Archive("v2.5", (root + "d3d11.dll", type, content)) };
        yield return new object[] { "duplicate", OfficialAcquisitionFixture.Archive("v2.5", (root + "d3d11.dll", TarEntryType.RegularFile, content), (root + "d3d11.dll", TarEntryType.RegularFile, content)) };
        var valid = OfficialAcquisitionFixture.Archive("v2.5");
        yield return new object[] { "truncated", valid[..^8] };
        yield return new object[] { "corrupt", new byte[] { 1, 2, 3, 4 } };
    }

    [Theory]
    [MemberData(nameof(InvalidArchives))]
    public async Task InvalidArchive_NoPublication_SentinelAndOtherVersionPreserved_RetrySucceeds(string category, byte[] archive)
    {
        Assert.NotEmpty(category);
        using var fixture = new OfficialAcquisitionFixture("v2.4", "v2.5");
        await fixture.Acquirer.AcquireAsync("v2.4");
        string previous = Path.Combine(fixture.Source, "v2.4", "x64", "d3d11.dll");
        byte[] previousBytes = File.ReadAllBytes(previous);
        string sentinel = Path.Combine(fixture.Directory.RootPath, "sentinel");
        File.WriteAllText(sentinel, "owned sentinel");
        fixture.Archives["v2.5"] = archive; // Digest and size match even for deliberately invalid archives.
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Acquirer.AcquireAsync("v2.5"));
        Assert.False(System.IO.Directory.Exists(Path.Combine(fixture.Source, "v2.5")));
        Assert.Equal("owned sentinel", File.ReadAllText(sentinel));
        Assert.Equal(previousBytes, File.ReadAllBytes(previous));
        fixture.Archives["v2.5"] = OfficialAcquisitionFixture.Archive("v2.5");
        Assert.False((await fixture.Acquirer.AcquireAsync("v2.5")).Reused);
        Assert.True(DxvkLocalReleaseLibrary.IsVersionAvailable(fixture.Source, "v2.5", GraphicsApi.DX11, "x64"));
    }

    [Fact]
    public async Task DigestMismatch_RefusesWithoutPublication_AndCancellationCanRetry()
    {
        using var fixture = new OfficialAcquisitionFixture();
        fixture.Override = (request, _) =>
        {
            var json = OfficialAcquisitionFixture.Metadata("v2.5", fixture.Archives["v2.5"]);
            json["assets"]![0]!["digest"] = "sha256:" + new string('0', 64);
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("v2.5") ? OfficialAcquisitionFixture.Json(json)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Archives["v2.5"]) });
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Acquirer.AcquireAsync("v2.5"));
        Assert.False(System.IO.Directory.Exists(Path.Combine(fixture.Source, "v2.5")));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Override = async (_, token) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = fixture.Acquirer.AcquireAsync("v2.5", cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        fixture.Override = null;
        Assert.False((await fixture.Acquirer.AcquireAsync("v2.5")).Reused);
    }

    [Fact]
    public async Task LegacyCompleteReuseAndIncompleteDestination_DoNotFetchOrOverwrite()
    {
        using var fixture = new OfficialAcquisitionFixture();
        string arch = Path.Combine(fixture.Source, "v2.5", "x64");
        System.IO.Directory.CreateDirectory(arch);
        File.WriteAllText(Path.Combine(arch, "d3d11.dll"), "legacy");
        File.WriteAllText(Path.Combine(arch, "dxgi.dll"), "legacy");
        fixture.Offline = true;
        var result = await fixture.Acquirer.AcquireAsync("v2.5");
        Assert.True(result.Reused);
        Assert.False(result.UpstreamDigestVerified);
        Assert.False(result.HasAcquisitionManifest);
        File.Delete(Path.Combine(arch, "dxgi.dll"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Acquirer.AcquireAsync("v2.5"));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(arch, "d3d11.dll")));
        Assert.Empty(fixture.Requests);
        string interrupted = Path.Combine(fixture.Source, ".acquire-interrupted", "x64");
        System.IO.Directory.CreateDirectory(interrupted);
        File.WriteAllText(Path.Combine(interrupted, "d3d11.dll"), "partial");
        File.WriteAllText(Path.Combine(interrupted, "dxgi.dll"), "partial");
        Assert.DoesNotContain(".acquire-interrupted", DxvkLocalReleaseLibrary.GetAvailableVersions(fixture.Source, GraphicsApi.DX11, "x64"));
    }

    [Theory]
    [InlineData("x32", false)]
    [InlineData("x64", false)]
    [InlineData("x32", true)]
    public async Task SuppliedArchitectureAndApiSets_ControlEligibility(string arch, bool dx9Only)
    {
        using var fixture = new OfficialAcquisitionFixture();
        string prefix = "dxvk-2.5/" + arch + "/";
        byte[] bytes = { 1, 2, 3 };
        fixture.Archives["v2.5"] = dx9Only
            ? OfficialAcquisitionFixture.Archive("v2.5", (prefix + "d3d9.dll", TarEntryType.RegularFile, bytes))
            : OfficialAcquisitionFixture.Archive("v2.5", (prefix + "d3d11.dll", TarEntryType.RegularFile, bytes), (prefix + "dxgi.dll", TarEntryType.RegularFile, bytes));
        await fixture.Acquirer.AcquireAsync("v2.5");
        Assert.True(DxvkLocalReleaseLibrary.IsVersionAvailable(fixture.Source, "v2.5", dx9Only ? GraphicsApi.DX9 : GraphicsApi.DX11, arch));
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(fixture.Source, "v2.5", dx9Only ? GraphicsApi.DX11 : GraphicsApi.DX9, arch));
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(fixture.Source, "v2.5", GraphicsApi.DX11, arch == "x32" ? "x64" : "x32"));
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotCancelSharedLeader_AndOnlyOneArchiveIsFetched()
    {
        using var fixture = new OfficialAcquisitionFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Override = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("v2.5"))
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
                return OfficialAcquisitionFixture.Json(OfficialAcquisitionFixture.Metadata("v2.5", fixture.Archives["v2.5"]));
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Archives["v2.5"]) };
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var leader = fixture.Acquirer.AcquireAsync("v2.5", deadline.Token);
        await started.Task.WaitAsync(deadline.Token);
        using var cancel = new CancellationTokenSource();
        var waiter = fixture.Acquirer.AcquireAsync("v2.5", cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.SetResult();
        Assert.False((await leader).Reused);
        Assert.True((await fixture.Acquirer.AcquireAsync("v2.5")).Reused);
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task AcquiredVersions_RestartOfflineTwoGamesSwitchAndRestore_KeepBaselinesAndSources()
    {
        using var fixture = new OfficialAcquisitionFixture("v2.3", "v2.4");
        var first = CreateManager(fixture);
        await first.Manager.AcquireOfficialReleaseAsync("v2.3");
        await first.Manager.AcquireOfficialReleaseAsync("v2.4");
        Assert.Empty(first.Library.GetAll());
        var sourceSnapshot = System.IO.Directory.GetFiles(fixture.Source, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(fixture.Source, p), File.ReadAllBytes);
        fixture.Offline = true;
        int requests = fixture.Requests.Count;
        var restarted = CreateManager(fixture);
        var games = new List<GameProfile>();
        foreach (string name in new[] { "GameA", "GameB" })
        {
            string game = Path.Combine(fixture.Directory.RootPath, name);
            System.IO.Directory.CreateDirectory(game);
            File.WriteAllBytes(Path.Combine(game, "Game.exe"), new byte[] { 0x4d, 0x5a, 0x90, 0 });
            File.WriteAllText(Path.Combine(game, "d3d11.dll"), "original-" + name);
            var profile = new GameProfile(Path.Combine(game, "Game.exe")) { Api = GraphicsApi.DX11, Architecture = "x64" };
            restarted.Profiles.Save(profile);
            var installation = restarted.Library.GetOrCreateInstallation(game, name);
            installation.ManagementPolicy = ManagementPolicy.Automatic();
            installation.ManagementPolicy.PinnedDxvkVersion = "v2.3";
            restarted.Library.Save(installation);
            Assert.Equal(DxvkActionResult.Applied, await restarted.Manager.RequestEnableByPathAsync(profile));
            Assert.Equal("v2.3", profile.DxvkVersion);
            Assert.Equal("v2.3:d3d11", File.ReadAllText(Path.Combine(game, "d3d11.dll")));
            games.Add(profile);
        }
        var target = restarted.Library.FindInstallationForExecutable(games[0].ExePath)!;
        target.ManagementPolicy.PinnedDxvkVersion = "v2.4";
        restarted.Library.Save(target);
        Assert.Equal(DxvkActionResult.Applied, await restarted.Manager.RequestUpdateByPathAsync(games[0]));
        Assert.Equal("v2.4", games[0].DxvkVersion);
        foreach (var game in games)
        {
            Assert.Equal(DxvkActionResult.Applied, await restarted.Manager.RequestDisableByPathAsync(game));
            string directory = Path.GetDirectoryName(game.ExePath)!;
            Assert.Equal("original-" + Path.GetFileName(directory), File.ReadAllText(Path.Combine(directory, "d3d11.dll")));
            Assert.False(File.Exists(Path.Combine(directory, "dxgi.dll")));
        }
        Assert.Equal(requests, fixture.Requests.Count);
        Assert.Equal(sourceSnapshot.Keys.Order(), System.IO.Directory.GetFiles(fixture.Source, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(fixture.Source, p)).Order());
        foreach (var file in sourceSnapshot) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(fixture.Source, file.Key)));
        File.WriteAllText(Path.Combine(fixture.Source, "v2.4", "x64", "d3d11.dll"), "corrupt");
        Assert.Equal(DxvkActionResult.Failed, await restarted.Manager.RequestEnableByPathAsync(games[0]));
        Assert.Contains("Browse / Download", restarted.Manager.LastRefusalReason);
        Assert.Equal("original-GameA", File.ReadAllText(Path.Combine(Path.GetDirectoryName(games[0].ExePath)!, "d3d11.dll")));
        Assert.Equal(requests, fixture.Requests.Count);
    }

    [Fact]
    public async Task AcquiredRelease_QueuedTargetSurvivesNewerAcquisitionAndRestart_EntirelyOffline()
    {
        using var fixture = new OfficialAcquisitionFixture("v2.3", "v2.4");
        var first = CreateManager(fixture);
        await first.Manager.AcquireOfficialReleaseAsync("v2.3");
        string directory = Path.Combine(fixture.Directory.RootPath, "QueuedGame");
        System.IO.Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "Game.exe");
        File.WriteAllBytes(executable, new byte[] { 0x4d, 0x5a, 0x90, 0 });
        var profile = new GameProfile(executable) { Api = GraphicsApi.DX11, Architecture = "x64" };
        first.Profiles.Save(profile);
        var installation = first.Library.GetOrCreateInstallation(directory, "QueuedGame");
        installation.ManagementPolicy.PinnedDxvkVersion = "v2.3";
        first.Library.Save(installation);
        using var running = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal(DxvkActionResult.Queued, await first.Manager.RequestEnableAsync(profile, running));
        Assert.Equal("v2.3", installation.PendingAction!.TargetDxvkVersion);
        await first.Manager.AcquireOfficialReleaseAsync("v2.4");
        Assert.Equal("v2.3", installation.PendingAction.TargetDxvkVersion);
        Assert.False(File.Exists(Path.Combine(directory, "d3d11.dll")));
        fixture.Offline = true;
        int requests = fixture.Requests.Count;
        var second = CreateManager(fixture);
        Assert.Equal(1, await second.Manager.ProcessAllPendingActionsAsync());
        Assert.Equal("v2.3", second.Profiles.GetOrCreate(executable).DxvkVersion);
        Assert.Equal("v2.3:d3d11", File.ReadAllText(Path.Combine(directory, "d3d11.dll")));
        Assert.Equal(requests, fixture.Requests.Count);
    }

    [Fact]
    public async Task PublicationCollision_PreservesUnexpectedFileAndRetriesAfterFixtureRemovesIt()
    {
        using var fixture = new OfficialAcquisitionFixture();
        fixture.Override = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("v2.5"))
                return Task.FromResult(OfficialAcquisitionFixture.Json(OfficialAcquisitionFixture.Metadata("v2.5", fixture.Archives["v2.5"])));
            File.WriteAllText(Path.Combine(fixture.Source, "v2.5"), "collision sentinel");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Archives["v2.5"]) });
        };
        await Assert.ThrowsAsync<IOException>(() => fixture.Acquirer.AcquireAsync("v2.5"));
        Assert.Equal("collision sentinel", File.ReadAllText(Path.Combine(fixture.Source, "v2.5")));
        Assert.Empty(DxvkLocalReleaseLibrary.GetAvailableVersions(fixture.Source, GraphicsApi.DX11, "x64"));
        File.Delete(Path.Combine(fixture.Source, "v2.5")); // Fixture-owned collision file only.
        fixture.Override = null;
        Assert.False((await fixture.Acquirer.AcquireAsync("v2.5")).Reused);
    }

    private static (DxvkManager Manager, GameLibraryStore Library, ProfileStore Profiles) CreateManager(OfficialAcquisitionFixture fixture)
    {
        string root = fixture.Directory.RootPath;
        string backups = Path.Combine(root, "backups");
        var profiles = new ProfileStore(Path.Combine(root, "games.json"));
        var library = new GameLibraryStore(Path.Combine(root, "library.json"), backups, profiles.ProfilesFile);
        var engine = new MultiFileTransactionEngine(backups);
        var installer = SyntheticSafetyFixture.CreateInstaller(fixture.Http, engine, library, fixture.Source);
        var rollback = SyntheticSafetyFixture.CreateRollback(engine, library);
        return (SyntheticSafetyFixture.CreateManager(installer, rollback, fixture.Github, profiles, library), library, profiles);
    }

    [Fact]
    public async Task UnpublishedStage_IsNeitherSelectableNorRecognizable_InterruptedPublicationCanRetry()
    {
        using var fixture = new OfficialAcquisitionFixture();
        string game = Path.Combine(fixture.Directory.RootPath, "RecognitionGame");
        System.IO.Directory.CreateDirectory(game);
        fixture.Acquirer.BeforePublication = stage =>
        {
            foreach (string dll in new[] { "d3d11.dll", "dxgi.dll" })
                File.Copy(Path.Combine(stage, "x64", dll), Path.Combine(game, dll));
            Assert.Empty(DxvkLocalReleaseLibrary.GetAvailableVersions(fixture.Source, GraphicsApi.DX11, "x64"));
            Assert.False(new ExistingDxvkDetector(fixture.Source).AssessDirectory(game, "x64").CanBeAdopted);
            throw new IOException("Synthetic publication interruption.");
        };
        await Assert.ThrowsAsync<IOException>(() => fixture.Acquirer.AcquireAsync("v2.5"));
        Assert.False(System.IO.Directory.Exists(Path.Combine(fixture.Source, "v2.5")));
        fixture.Acquirer.BeforePublication = null;
        await fixture.Acquirer.AcquireAsync("v2.5");
        Assert.True(new ExistingDxvkDetector(fixture.Source).AssessDirectory(game, "x64").CanBeAdopted);
    }

    [Fact]
    public async Task InvalidAcquiredManifest_BlocksManagerRecognitionAndBothAdoptionEntryPoints()
    {
        using var fixture = new OfficialAcquisitionFixture();
        await fixture.Acquirer.AcquireAsync("v2.5");
        string game = Path.Combine(fixture.Directory.RootPath, "AdoptionGame");
        System.IO.Directory.CreateDirectory(game);
        string executable = Path.Combine(game, "Game.exe");
        File.WriteAllBytes(executable, new byte[] { 0x4d, 0x5a, 0x90, 0 });
        foreach (string dll in new[] { "d3d11.dll", "dxgi.dll" })
            File.Copy(Path.Combine(fixture.Source, "v2.5", "x64", dll), Path.Combine(game, dll));
        var profile = new GameProfile(executable) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var services = CreateManager(fixture);
        services.Profiles.Save(profile);
        Assert.True(services.Manager.AssessExistingDxvk(profile).CanBeAdopted);
        File.WriteAllText(Path.Combine(fixture.Source, "v2.5", DxvkReleaseAcquirer.ManifestName), "{}");
        var rawAssessment = new ExistingDxvkDetector(fixture.Source).AssessDirectory(game, "x64");
        Assert.True(rawAssessment.CanBeAdopted); // Demonstrates the old hash-only recognizer's limitation.
        Assert.False(services.Manager.AssessExistingDxvk(profile).CanBeAdopted);
        Assert.False(await services.Manager.AdoptExistingAsync(profile));
        var installer = SyntheticSafetyFixture.CreateInstaller(fixture.Http,
            new MultiFileTransactionEngine(Path.Combine(fixture.Directory.RootPath, "backups")), services.Library, fixture.Source);
        Assert.False(installer.AdoptExisting(profile, rawAssessment));
        Assert.Empty(services.Library.GetAll());
        Assert.False(profile.DxvkEnabled);
        Assert.Equal("v2.5:d3d11", File.ReadAllText(Path.Combine(game, "d3d11.dll")));
        Assert.Equal(2, fixture.Requests.Count);
    }
}
