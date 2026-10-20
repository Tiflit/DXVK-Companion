using System.Net.Http;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class SyntheticSafetyFixtureTests
{
    [Fact]
    public async Task PrivateCoordination_ConflictingFixtureDoesNotBlockIndependentFixture()
    {
        using var fixtureA = new SyntheticTestDirectory();
        using var fixtureB = new SyntheticTestDirectory();
        var storeA = new GameLibraryStore(fixtureA.GetPath("library.json"), fixtureA.GetPath("backups"));
        var storeB = new GameLibraryStore(fixtureB.GetPath("library.json"), fixtureB.GetPath("backups"));
        var coordinatorA = SyntheticSafetyFixture.ConfigureStore(storeA);
        var coordinatorB = SyntheticSafetyFixture.ConfigureStore(storeB);
        var target = fixtureA.GetPath("Game");
        Directory.CreateDirectory(target);

        await using var held = await coordinatorA.TryAcquireOperationScopeAsync(target);
        Assert.NotNull(held);
        await using var sameFixture = await coordinatorA.TryAcquireOperationScopeAsync(target,
            timeout: TimeSpan.FromMilliseconds(100));
        Assert.Null(sameFixture); // Still uses genuine exclusion, never a no-op lease.
        await using var independent = await coordinatorB.TryAcquireOperationScopeAsync(target);
        Assert.NotNull(independent);
        Assert.NotEqual(coordinatorA.CoordinationDirectory, coordinatorB.CoordinationDirectory);
    }

    [Fact]
    public async Task UnreadableUnrelatedRecord_RefusesSharedNamespaceButNotIndependentFixture()
    {
        using var fixtureA = new SyntheticTestDirectory();
        using var fixtureB = new SyntheticTestDirectory();
        var storeA = new GameLibraryStore(fixtureA.GetPath("library.json"), fixtureA.GetPath("backups"));
        var storeB = new GameLibraryStore(fixtureB.GetPath("library.json"), fixtureB.GetPath("backups"));
        var coordinatorA = SyntheticSafetyFixture.ConfigureStore(storeA);
        var coordinatorB = SyntheticSafetyFixture.ConfigureStore(storeB);
        var target = fixtureA.GetPath("Game");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(coordinatorA.CoordinationDirectory);
        var unrelated = fixtureA.GetPath("OtherGame");
        var record = Path.Combine(coordinatorA.CoordinationDirectory,
            OperationResourceIdentity.GetTargetLockFileName(unrelated));
        using var unpublished = new FileStream(record, FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        var payload = System.Text.Encoding.UTF8.GetBytes(unrelated);
        unpublished.Write(payload);
        unpublished.Flush();

        // Even an unrelated record must fail closed when its target cannot be read.
        await using var refused = await coordinatorA.TryAcquireOperationScopeAsync(target,
            timeout: TimeSpan.FromMilliseconds(100));
        Assert.Null(refused);
        // The identical target succeeds with only the coordination namespace changed.
        await using var independent = await coordinatorB.TryAcquireOperationScopeAsync(target);
        Assert.NotNull(independent);
    }

    [Fact]
    public async Task ExplicitUnknownActivity_StillRefusesSyntheticDeployment()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        using var source = new SyntheticTestDirectory();
        source.CreateFile("2.5/x64/d3d11.dll", "synthetic-d3d11");
        source.CreateFile("2.5/x64/dxgi.dll", "synthetic-dxgi");
        var exe = game.CreateFile("Game.exe", "synthetic-exe");
        var store = new GameLibraryStore(storage.GetPath("library.json"), storage.GetPath("backups"));
        using var http = new HttpClient();
        var installer = SyntheticSafetyFixture.CreateInstaller(http,
            new MultiFileTransactionEngine(storage.GetPath("backups")), store, source.RootPath);
        installer.ActivityInspector = new ProcessActivityInspector(
            processEnumerator: () => throw new InvalidOperationException("Synthetic enumeration failure"));
        var profile = new GameProfile(exe) { Api = GraphicsApi.DX11, Architecture = "x64" };

        var result = await installer.ApplyToGameAsync(profile,
            new ReleaseInfo { Version = "2.5", DownloadUrl = "" });

        Assert.False(result);
        Assert.Equal("ActivityUncertain", SyntheticSafetyFixture.RefusalCategory(installer.LastRefusalReason));
        Assert.False(File.Exists(game.GetPath("d3d11.dll")));
        Assert.False(File.Exists(game.GetPath("dxgi.dll")));
    }
}
