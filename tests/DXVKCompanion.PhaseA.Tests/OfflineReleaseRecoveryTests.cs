using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;
using DXVKCompanion.UI;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class OfflineReleaseRecoveryTests
{
    private sealed record Context(DxvkManager Manager, GameLibraryStore Library, ProfileStore Profiles, CacheStore Cache, string Backups);

    private static Context Reconstruct(OfficialAcquisitionFixture fixture)
    {
        string state = Path.Combine(fixture.Directory.RootPath, "state");
        var profiles = new ProfileStore(Path.Combine(state, "games.json"));
        var library = new GameLibraryStore(Path.Combine(state, "library.json"), Path.Combine(state, "backups"), profiles.ProfilesFile);
        var cache = new CacheStore(Path.Combine(state, "latest-cache.json"));
        string backups = Path.Combine(state, "backups");
        var engine = new MultiFileTransactionEngine(backups);
        var installer = SyntheticSafetyFixture.CreateInstaller(fixture.Http, engine, library, fixture.Source);
        var rollback = SyntheticSafetyFixture.CreateRollback(engine, library);
        var github = new DxvkGithubClient(fixture.Http, cache);
        return new Context(SyntheticSafetyFixture.CreateManager(installer, rollback, github, profiles, library), library, profiles, cache, backups);
    }

    private static GameProfile AddGame(OfficialAcquisitionFixture fixture, Context context, string name)
    {
        string root = Path.Combine(fixture.Directory.RootPath, name);
        Directory.CreateDirectory(root);
        string executable = Path.Combine(root, "Game.exe");
        File.WriteAllBytes(executable, new byte[] { 0x4d, 0x5a, 0x90, 0 });
        File.WriteAllText(Path.Combine(root, "d3d11.dll"), "synthetic native baseline");
        File.WriteAllText(Path.Combine(root, "dxvk.conf"), "synthetic unrelated setting");
        var profile = new GameProfile(executable) { Api = GraphicsApi.DX11, Architecture = "x64" };
        context.Profiles.Save(profile);
        context.Library.GetOrCreateInstallation(root, name);
        return profile;
    }

    // Exercise the existing selector semantics and persistence, without opening UI.
    private static void SelectAndSave(Context context, GameProfile profile, string? exact)
    {
        var installation = context.Library.FindInstallationForExecutable(profile.ExePath)!;
        var versions = DxvkLocalReleaseLibrary.GetAvailableVersions(context.Manager.DxvkSourceDir, profile.Api, profile.Architecture);
        var labels = GameDetailsVersionSelection.GetAvailableLabels(versions, installation.ManagementPolicy.PinnedDxvkVersion);
        int index = exact == null ? 0 : labels.ToList().FindIndex(label => label == exact);
        Assert.True(index >= 0);
        string? selected = GameDetailsVersionSelection.ResolveSelectedVersion(index, labels, installation.ManagementPolicy.PinnedDxvkVersion);
        Assert.Equal(exact, selected);
        GameDetailsVersionSelection.ApplyVersionSelection(installation.ManagementPolicy, selected);
        context.Library.Save(installation);
    }

    private static Dictionary<string, byte[]> Snapshot(string root) => !Directory.Exists(root) ? new()
        : Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(root, file), File.ReadAllBytes);

    private static void AssertUnchanged(string root, Dictionary<string, byte[]> before)
    {
        var after = Snapshot(root);
        Assert.Equal(before.Keys.OrderBy(key => key), after.Keys.OrderBy(key => key));
        foreach (var file in before) Assert.Equal(file.Value, after[file.Key]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptMetadata_ExplainsLatestFailure_ThenExplicitSelectionDeploysOfflineAfterRestart(bool corrupt)
    {
        using var fixture = new OfficialAcquisitionFixture();
        await fixture.Acquirer.AcquireAsync("v2.5");
        var context = Reconstruct(fixture);
        var profile = AddGame(fixture, context, "RecoveryGame");
        if (corrupt) File.WriteAllText(context.Cache.CacheFile, "<<<synthetic corrupt metadata>>>");
        fixture.Offline = true;
        int initialRequests = fixture.Requests.Count;
        string game = Path.GetDirectoryName(profile.ExePath)!;
        var before = Snapshot(game);

        Assert.Equal(DxvkActionResult.Failed, await context.Manager.RequestEnableByPathAsync(profile));
        Assert.Contains("Latest DXVK metadata is unavailable", context.Manager.LastRefusalReason);
        Assert.Contains("Game Details", context.Manager.LastRefusalReason);
        AssertUnchanged(game, before);
        Assert.False(profile.DxvkEnabled);
        Assert.Null(context.Library.FindInstallationForExecutable(profile.ExePath)!.ManagementPolicy.PinnedDxvkVersion);
        Assert.Equal(initialRequests + 1, fixture.Requests.Count);
        if (corrupt) Assert.Equal("<<<synthetic corrupt metadata>>>", File.ReadAllText(context.Cache.CacheFile));
        var cacheBytes = File.ReadAllBytes(context.Cache.CacheFile);

        SelectAndSave(context, profile, "v2.5");
        var restarted = Reconstruct(fixture);
        var restartedProfile = restarted.Profiles.GetOrCreate(profile.ExePath);
        Assert.Equal(DxvkActionResult.Applied, await restarted.Manager.RequestEnableByPathAsync(restartedProfile));
        Assert.Null(restarted.Manager.LastRefusalReason);
        Assert.Equal(initialRequests + 1, fixture.Requests.Count);
        Assert.Equal(cacheBytes, File.ReadAllBytes(context.Cache.CacheFile));
        Assert.Equal("v2.5", restartedProfile.DxvkVersion);
        Assert.Equal("v2.5", restarted.Library.FindInstallationForExecutable(profile.ExePath)!.ManagedDxvkVersion);
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Source, "v2.5", "x64", "d3d11.dll")), File.ReadAllBytes(Path.Combine(game, "d3d11.dll")));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(fixture.Source, "v2.5"), "v2.5", out _));
    }

    [Fact]
    public async Task KnownLatestWithoutPayload_RetainsInstalledRelease_UntilExplicitOlderSelection()
    {
        using var fixture = new OfficialAcquisitionFixture("v2.4", "v2.5");
        await fixture.Acquirer.AcquireAsync("v2.4");
        await fixture.Acquirer.AcquireAsync("v2.5");
        var context = Reconstruct(fixture);
        var profile = AddGame(fixture, context, "InstalledGame");
        SelectAndSave(context, profile, "v2.5");
        Assert.Equal(DxvkActionResult.Applied, await context.Manager.RequestEnableByPathAsync(profile));
        SelectAndSave(context, profile, null);
        context.Cache.SaveCachedRelease(new ReleaseInfo { Version = "v2.6" }, null);
        fixture.Offline = true;
        int initialRequests = fixture.Requests.Count;
        string game = Path.GetDirectoryName(profile.ExePath)!;
        var before = Snapshot(game);
        var backups = Snapshot(context.Backups);

        Assert.Equal(DxvkActionResult.Failed, await context.Manager.RequestUpdateByPathAsync(profile));
        Assert.Contains("v2.6", context.Manager.LastRefusalReason);
        Assert.Contains("Game Details", context.Manager.LastRefusalReason);
        AssertUnchanged(game, before);
        AssertUnchanged(context.Backups, backups);
        Assert.Equal("v2.5", profile.DxvkVersion);
        var installation = context.Library.FindInstallationForExecutable(profile.ExePath)!;
        Assert.Equal("v2.5", installation.ManagedDxvkVersion);
        Assert.Null(installation.ManagementPolicy.PinnedDxvkVersion);
        Assert.False(Directory.Exists(Path.Combine(fixture.Source, "v2.6")));
        Assert.Equal(initialRequests + 1, fixture.Requests.Count);
        Assert.EndsWith("/releases/tags/v2.6", fixture.Requests.Last());

        SelectAndSave(context, profile, "v2.4");
        Assert.Equal(DxvkActionResult.Applied, await context.Manager.RequestUpdateByPathAsync(profile));
        Assert.Equal(initialRequests + 1, fixture.Requests.Count);
        Assert.Equal("v2.4", profile.DxvkVersion);
        Assert.Equal("v2.4", installation.ManagedDxvkVersion);
        Assert.Equal("v2.4", installation.ManagementPolicy.PinnedDxvkVersion);
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Source, "v2.4", "x64", "d3d11.dll")), File.ReadAllBytes(Path.Combine(game, "d3d11.dll")));
        Assert.Equal("v2.6", context.Cache.LoadCachedRelease()!.Release.Version);
        AssertUnchanged(context.Backups, backups);
        Assert.True(DxvkLocalReleaseLibrary.IsVersionAvailable(fixture.Source, "v2.5", profile.Api, profile.Architecture));
    }

    [Fact]
    public async Task ExpiredMetadata_OfflineCheckDoesNotInvalidateVerifiedLocalLatestPayload()
    {
        using var fixture = new OfficialAcquisitionFixture();
        await fixture.Acquirer.AcquireAsync("v2.5");
        var context = Reconstruct(fixture);
        var profile = AddGame(fixture, context, "ExpiredMetadataGame");
        var oldTime = DateTime.UtcNow.AddDays(-2);
        context.Cache.SaveCachedRelease(new ReleaseInfo { Version = "v2.5" }, null, oldTime, oldTime);
        fixture.Offline = true;
        int initialRequests = fixture.Requests.Count;

        Assert.Equal(DxvkActionResult.Applied, await context.Manager.RequestEnableByPathAsync(profile));
        Assert.Equal("v2.5", profile.DxvkVersion);
        Assert.Null(context.Library.FindInstallationForExecutable(profile.ExePath)!.ManagementPolicy.PinnedDxvkVersion);
        Assert.Equal(initialRequests + 1, fixture.Requests.Count);
        Assert.EndsWith("/releases/latest", fixture.Requests.Last());
        Assert.Equal(oldTime, context.Cache.LoadCachedRelease()!.CachedAt);
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(fixture.Source, "v2.5"), "v2.5", out _));
    }

    [Fact]
    public async Task NoLocalReleaseAndNoMetadata_RefusesClearlyWithoutGameMutation()
    {
        using var fixture = new OfficialAcquisitionFixture();
        var context = Reconstruct(fixture);
        var profile = AddGame(fixture, context, "NoLocalGame");
        fixture.Offline = true;
        string game = Path.GetDirectoryName(profile.ExePath)!;
        var before = Snapshot(game);

        Assert.Equal(DxvkActionResult.Failed, await context.Manager.RequestEnableByPathAsync(profile));
        Assert.Contains("Latest DXVK metadata is unavailable", context.Manager.LastRefusalReason);
        Assert.Contains("daily metadata check", context.Manager.LastRefusalReason);
        AssertUnchanged(game, before);
        Assert.False(profile.DxvkEnabled);
        Assert.False(Directory.Exists(fixture.Source));
        Assert.Single(fixture.Requests);
        Assert.Empty(await context.Manager.UpdateAllEnabledAsync());
        Assert.Null(context.Manager.LastRefusalReason);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task BatchMetadataFailure_IsReportedAndRetainsReason_AfterLaterPinnedSuccess()
    {
        using var fixture = new OfficialAcquisitionFixture("v2.4", "v2.5");
        await fixture.Acquirer.AcquireAsync("v2.4");
        await fixture.Acquirer.AcquireAsync("v2.5");
        var context = Reconstruct(fixture);
        var latestProfile = AddGame(fixture, context, "LatestGame");
        SelectAndSave(context, latestProfile, "v2.4");
        Assert.Equal(DxvkActionResult.Applied, await context.Manager.RequestEnableByPathAsync(latestProfile));
        SelectAndSave(context, latestProfile, null);
        var pinnedProfile = AddGame(fixture, context, "PinnedGame");
        SelectAndSave(context, pinnedProfile, "v2.4");
        Assert.Equal(DxvkActionResult.Applied, await context.Manager.RequestEnableByPathAsync(pinnedProfile));
        SelectAndSave(context, pinnedProfile, "v2.5");
        fixture.Offline = true;
        int initialRequests = fixture.Requests.Count;
        string latestGame = Path.GetDirectoryName(latestProfile.ExePath)!;
        var before = Snapshot(latestGame);

        var results = await context.Manager.UpdateAllEnabledAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal(DxvkActionResult.Failed, results[latestProfile.ExePath]);
        Assert.Equal(DxvkActionResult.Applied, results[pinnedProfile.ExePath]);
        Assert.Contains("Latest DXVK metadata is unavailable", context.Manager.LastRefusalReason);
        AssertUnchanged(latestGame, before);
        Assert.Equal("v2.4", latestProfile.DxvkVersion);
        Assert.Equal("v2.5", pinnedProfile.DxvkVersion);
        Assert.Equal(initialRequests + 1, fixture.Requests.Count);

        // Persisted failed-attempt throttling also applies to the next batch.
        var next = await context.Manager.UpdateAllEnabledAsync();
        Assert.Single(next);
        Assert.Equal(DxvkActionResult.Failed, next[latestProfile.ExePath]);
        Assert.Equal(initialRequests + 1, fixture.Requests.Count);

        SelectAndSave(context, latestProfile, "v2.4");
        Assert.Empty(await context.Manager.UpdateAllEnabledAsync());
        Assert.Null(context.Manager.LastRefusalReason);
        Assert.Equal(initialRequests + 1, fixture.Requests.Count);
    }
}
