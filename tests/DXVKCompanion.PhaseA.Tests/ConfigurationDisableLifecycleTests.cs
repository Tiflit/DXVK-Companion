using System.Net.Http;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests;

public sealed class ConfigurationDisableLifecycleTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly SyntheticTestDirectory game = new();
        private readonly SyntheticTestDirectory storage = new();
        private readonly SyntheticTestDirectory source = new();
        private readonly HttpClient http = new();
        internal readonly GameLibraryStore Store;
        internal readonly MultiFileTransactionEngine Engine;
        internal readonly DxvkInstaller Installer;
        internal readonly GameProfile Profile;
        internal string Config => Path.Combine(game.RootPath, "dxvk.conf");
        internal string Library => Path.Combine(storage.RootPath, "game-library.json");
        internal string Backups => Path.Combine(storage.RootPath, "backups");
        internal GameInstallation Installation => Store.FindByInstallationPath(game.RootPath)!;

        internal Fixture()
        {
            var arch = Path.Combine(source.RootPath, "2.4", "x64");
            Directory.CreateDirectory(arch);
            File.WriteAllText(Path.Combine(arch, "d3d11.dll"), "synthetic-d3d11");
            File.WriteAllText(Path.Combine(arch, "dxgi.dll"), "synthetic-dxgi");
            Profile = new GameProfile(game.CreateFile("Game.exe", "synthetic-executable"))
            { Api = GraphicsApi.DX11, Architecture = "x64", HudEnabled = false, FrameLimit = 0 };
            Store = new GameLibraryStore(Library, Backups);
            Engine = new MultiFileTransactionEngine(Backups);
            Installer = SyntheticSafetyFixture.CreateInstaller(http, Engine, Store, source.RootPath);
        }

        internal Task<bool> Apply() => Installer.ApplyToGameAsync(Profile,
            new ReleaseInfo { Version = "2.4", DownloadUrl = "" });
        internal Task<bool> Change(bool reapply) => reapply ? Installer.ReapplyAsync(Profile) : Apply();
        internal Task<bool> Restore() => SyntheticSafetyFixture.CreateRollback(Engine, Store)
            .RestoreOriginalDllsAsync(Profile);

        public void Dispose() { http.Dispose(); source.Dispose(); storage.Dispose(); game.Dispose(); }
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 144)]
    [InlineData(true, false, 144)]
    [InlineData(false, true, 144)]
    [InlineData(true, true, 144)]
    public async Task LastFeatureDisabled_PreservesUserLinesAndExactOriginalRestore(
        bool reapply, bool hud, int limit)
    {
        using var f = new Fixture();
        // Noncanonical newlines/no final newline prove restore uses raw backup bytes.
        var original = System.Text.Encoding.UTF8.GetBytes(
            "# original\ndxvk.hud = compiler\ndxvk.maxFrameRate = 30\n" +
            "dxvk.hudExtra = keep\nother.setting = keep=both");
        File.WriteAllBytes(f.Config, original);
        f.Profile.HudEnabled = hud;
        f.Profile.FrameLimit = limit;
        Assert.True(await f.Apply());
        var record = f.Installation.FindManagedFile("dxvk.conf")!;
        var originalHash = record.OriginalSha256;
        var backup = Path.Combine(f.Backups, record.BackupRelativePath!);
        Assert.Equal(original, File.ReadAllBytes(backup));

        f.Profile.HudEnabled = false;
        f.Profile.FrameLimit = 0;
        Assert.True(await f.Change(reapply));
        Assert.Equal(string.Join(Environment.NewLine, "# original", "dxvk.hudExtra = keep",
            "other.setting = keep=both", ""), File.ReadAllText(f.Config));
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal(originalHash, record.OriginalSha256);
        Assert.Equal(FileIdentity.Capture(f.Config).Sha256, record.ExpectedManagedSha256);
        var reloaded = new GameLibraryStore(f.Library, f.Backups)
            .FindByInstallationPath(f.Installation.InstallationPath)!;
        Assert.False(reloaded.Configuration.HudEnabled);
        Assert.False(reloaded.Configuration.FrameLimitEnabled);

        Assert.True(await f.Restore());
        Assert.Equal(original, File.ReadAllBytes(f.Config));
        // Historical records survive Restore but must not cause takeover with both options off.
        Assert.True(await f.Apply());
        Assert.Equal(original, File.ReadAllBytes(f.Config));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginallyAbsentConfig_DisableKeepsInertManagedFileUntilRestore(bool reapply)
    {
        using var f = new Fixture();
        f.Profile.HudEnabled = true;
        Assert.True(await f.Apply());
        f.Profile.HudEnabled = false;
        Assert.True(await f.Change(reapply));
        Assert.Equal(Environment.NewLine, File.ReadAllText(f.Config));
        Assert.Equal(FileOriginalState.Missing, f.Installation.FindManagedFile("dxvk.conf")!.OriginalState);
        Assert.True(await f.Restore());
        Assert.False(File.Exists(f.Config));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothDisabled_DoNotTakeOverUnownedOrCreateAbsentConfig(bool existing)
    {
        using var f = new Fixture();
        var original = System.Text.Encoding.UTF8.GetBytes("dxvk.hud = compiler\ndxvk.maxFrameRate = 30");
        if (existing) File.WriteAllBytes(f.Config, original);
        Assert.True(await f.Apply());
        Assert.True(await f.Installer.ReapplyAsync(f.Profile));
        Assert.Null(f.Installation.FindManagedFile("dxvk.conf"));
        if (existing) Assert.Equal(original, File.ReadAllBytes(f.Config));
        else Assert.False(File.Exists(f.Config));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disable_MetadataCommitFailure_CompensatesConfigAndPreservesBaseline(bool reapply)
    {
        using var f = new Fixture();
        File.WriteAllText(f.Config, "# original\ndxvk.hud = compiler");
        f.Profile.HudEnabled = true;
        f.Profile.FrameLimit = 144;
        Assert.True(await f.Apply());
        var before = File.ReadAllBytes(f.Config);
        var library = File.ReadAllBytes(f.Library);
        var backupPath = Path.Combine(f.Backups, f.Installation.FindManagedFile("dxvk.conf")!.BackupRelativePath!);
        var backup = File.ReadAllBytes(backupPath);
        bool reachedCommit = false;
        f.Store.CustomCommitReplace = (_, _) =>
        { reachedCommit = true; throw new IOException("Synthetic pre-replacement failure"); };
        f.Profile.HudEnabled = false;
        f.Profile.FrameLimit = 0;

        Assert.False(await f.Change(reapply));
        Assert.True(reachedCommit);
        Assert.Equal(before, File.ReadAllBytes(f.Config));
        Assert.Equal(library, File.ReadAllBytes(f.Library));
        Assert.Equal(backup, File.ReadAllBytes(backupPath));
        Assert.True(f.Installation.Configuration.HudEnabled);
        Assert.True(f.Installation.Configuration.FrameLimitEnabled);
        Assert.False(f.Store.HasUncertainPersistence);
        f.Store.CustomCommitReplace = null;
        Assert.True(await f.Change(reapply));
        Assert.Equal("# original" + Environment.NewLine, File.ReadAllText(f.Config));
        Assert.True(await f.Restore());
        Assert.Equal(backup, File.ReadAllBytes(f.Config));
    }
}
