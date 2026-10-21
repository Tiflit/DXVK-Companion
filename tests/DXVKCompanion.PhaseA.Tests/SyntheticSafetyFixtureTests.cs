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
    public async Task ActualInspector_RefusesUnreadableSharedNamespace_ButIndependentFixtureDetectsReplacement()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();
        using var otherStorage = new SyntheticTestDirectory();
        using var source = new SyntheticTestDirectory();
        source.CreateFile("2.5/x64/d3d11.dll", "synthetic-d3d11");
        source.CreateFile("2.5/x64/dxgi.dll", "synthetic-dxgi");
        var exe = game.CreateFile("Game.exe", "synthetic-exe");
        var store = new GameLibraryStore(storage.GetPath("library.json"), storage.GetPath("backups"));
        var otherStore = new GameLibraryStore(otherStorage.GetPath("library.json"), otherStorage.GetPath("backups"));
        using var http = new HttpClient();
        var installer = SyntheticSafetyFixture.CreateInstaller(http,
            new MultiFileTransactionEngine(storage.GetPath("backups")), store, source.RootPath);
        var profile = new GameProfile(exe) { Api = GraphicsApi.DX11, Architecture = "x64" };
        Assert.True(await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.5", DownloadUrl = "" }),
            SyntheticSafetyFixture.RefusalCategory(installer.LastRefusalReason));
        var installation = store.FindByInstallationPath(game.RootPath);
        Assert.NotNull(installation);
        var record = installation.FindManagedFile("d3d11.dll");
        Assert.NotNull(record);
        var beforeState = record.CurrentState;
        var beforeLibrary = File.ReadAllBytes(store.LibraryFilePath);
        File.WriteAllText(game.GetPath("d3d11.dll"), "synthetic-game-patch");

        var otherCoordinator = SyntheticSafetyFixture.ConfigureStore(otherStore);
        Directory.CreateDirectory(otherCoordinator.CoordinationDirectory);
        var unrelated = otherStorage.GetPath("OtherGame");
        var lockPath = Path.Combine(otherCoordinator.CoordinationDirectory,
            OperationResourceIdentity.GetTargetLockFileName(unrelated));
        using var unpublished = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        unpublished.Write(System.Text.Encoding.UTF8.GetBytes(unrelated));
        unpublished.Flush();

        // Hold bytes and a real unreadable lock constant; change only namespace.
        store.LeaseCoordinator = otherCoordinator;
        Assert.False(new ManagedFileInspector(store).InspectInstallation(installation));
        Assert.Equal(beforeState, record.CurrentState);
        Assert.Equal(beforeLibrary, File.ReadAllBytes(store.LibraryFilePath));
        var isolatedInspector = SyntheticSafetyFixture.CreateInspector(store);
        Assert.NotEqual(otherCoordinator.CoordinationDirectory,
            ((OperationLeaseCoordinator)store.LeaseCoordinator).CoordinationDirectory);
        Assert.True(isolatedInspector.InspectInstallation(installation));
        Assert.Equal(ManagedFileState.ExternallyChanged, record.CurrentState);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.Equal("synthetic-game-patch", File.ReadAllText(game.GetPath("d3d11.dll")));
    }

    [Fact]
    public void BoundedDiagnostics_EmitOnlyFixedPhaseCategoryAndElapsedTime()
    {
        var diagnostic = SyntheticSafetyFixture.Diagnostic(SyntheticSafetyFixture.Phase.StoreLeaseSetup,
            517, @"Access denied at C:\Users\Synthetic Owner\PrivateSentinel\secret.txt");
        Assert.Equal("Synthetic phase=StoreLeaseSetup; elapsedMs=517; category=AccessDenied", diagnostic);
        Assert.DoesNotContain("PrivateSentinel", diagnostic);
        Assert.DoesNotContain("Synthetic Owner", diagnostic);
        Assert.DoesNotContain("secret.txt", diagnostic);
        Assert.Equal("Synthetic phase=HelperStartupSignal; elapsedMs=0; category=OtherRefusal",
            SyntheticSafetyFixture.Diagnostic(SyntheticSafetyFixture.Phase.HelperStartupSignal, -1,
                "PrivateSentinel Bearer SyntheticSentinelCredential"));
    }

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
