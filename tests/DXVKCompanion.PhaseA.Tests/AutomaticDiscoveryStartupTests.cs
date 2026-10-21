using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Monitoring;
using DXVKCompanion.Safety;
using DXVKCompanion.Startup;
using DXVKCompanion.Storage;
using DXVKCompanion.UI;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class AutomaticDiscoveryStartupTests
{
    private static StartupOptions Options(bool enabled)
    {
        Assert.True(StartupOptions.TryParse(enabled ? Array.Empty<string>() : new[] { "--no-auto-discovery" }, out var options));
        return Assert.IsType<StartupOptions>(options);
    }

    private static ApiClassifier Classifier() => new(new ModuleScanner(), new PeParser());

    [Fact]
    public async Task DefaultStartupComposition_StartsRealTimerWithInjectedProcessUniverse()
    {
        var enumerated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = Program.CreateMonitor(Options(true), new GameDetector(), new ProcessExitHandler(), Classifier(), null,
            processEnumerator: () => { enumerated.TrySetResult(true); return Array.Empty<Process>(); });
        Assert.True(monitor.AutomaticDiscoveryEnabled);
        Assert.True(monitor.HasPollingTimer);
        await enumerated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledStartupComposition_NeverEnumeratesAdmitsOrWritesStores(bool seedRecord)
    {
        using var root = new SyntheticTestDirectory();
        var store = new GameLibraryStore(Path.Combine(root.RootPath, "library.json"), Path.Combine(root.RootPath, "backups"));
        var coordinator = SyntheticSafetyFixture.ConfigureStore(store);
        var profiles = new ProfileStore(Path.Combine(root.RootPath, "profiles.json")) { LeaseCoordinator = coordinator };
        if (seedRecord)
        {
            var target = Path.Combine(root.RootPath, "Fixture");
            Directory.CreateDirectory(target);
            store.GetOrCreateInstallation(target, "Fixture");
            profiles.GetOrCreate(Path.Combine(target, "Game.exe"));
        }
        var before = Snapshot(root.RootPath);
        int enumerations = 0, notifications = 0;
        using var monitor = Program.CreateMonitor(Options(false), new GameDetector(), new ProcessExitHandler(), Classifier(), store,
            processEnumerator: () => { enumerations++; throw new InvalidOperationException("Passive enumeration must not run."); });
        monitor.ProfileStore = profiles;
        monitor.OnSnapshotDetected += _ => notifications++;
        monitor.OnGameDetected += _ => notifications++;
        monitor.OnGameExited += _ => notifications++;
        Assert.False(monitor.AutomaticDiscoveryEnabled);
        Assert.False(monitor.HasPollingTimer);
        // Also guard accidental/manual poll delivery: disabling only the timer is insufficient.
        for (int i = 0; i < 3; i++) monitor.PollProcessesOnce();
        Assert.Equal(0, enumerations);
        Assert.Equal(0, notifications);
        AssertSnapshot(before, root.RootPath);
        Assert.Equal(seedRecord ? 1 : 0, store.GetAll().Count);
        Assert.Equal(seedRecord ? 1 : 0, profiles.GetAll().Count());
    }

    [Fact]
    public void TrayStatus_IsVisibleAndNonActionableOnlyForPausedDiscovery()
    {
        Assert.Null(TrayMenu.CreateDiscoveryStatus(true));
        using var item = TrayMenu.CreateDiscoveryStatus(false);
        Assert.NotNull(item);
        Assert.Equal("Automatic discovery off", item.Text);
        Assert.False(item.Enabled);
        Assert.Contains("Activity safety checks remain active", item.ToolTipText);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisabledDiscovery_DoesNotBypassRealActivityChecksForEnableOrRestore(bool restore, bool unknown)
    {
        using var root = new SyntheticTestDirectory();
        var target = Path.Combine(root.RootPath, "Fixture");
        Directory.CreateDirectory(target);
        var exe = Path.Combine(target, "Game.exe");
        File.WriteAllText(exe, "non-PE fixture; never execute");
        File.WriteAllText(Path.Combine(target, "keep.bin"), "retained sentinel");
        File.WriteAllText(Path.Combine(target, "dxvk.conf"), "# retained config");
        var store = new GameLibraryStore(Path.Combine(root.RootPath, "library.json"), Path.Combine(root.RootPath, "backups"));
        var coordinator = SyntheticSafetyFixture.ConfigureStore(store);
        var profile = new GameProfile(exe) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var installation = store.GetOrCreateInstallation(target, "Fixture");
        installation.GetOrAddExecutable("Game.exe").LastKnownApi = GraphicsApi.DX11;
        store.Save(installation);
        var before = Snapshot(target);
        byte[] libraryBefore = File.ReadAllBytes(store.LibraryFilePath);
        using var candidate = Process.GetCurrentProcess();
        int activityEnumerations = 0;
        var activity = new ProcessActivityInspector(
            processEnumerator: () => { activityEnumerations++; return new[] { candidate }; },
            processNameResolver: _ => "Game",
            mainModulePathResolver: _ => unknown ? throw new Win32Exception(5, "Synthetic candidate access denied.") : exe,
            startTimeResolver: _ => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            hasExitedResolver: _ => false);
        using var monitor = Program.CreateMonitor(Options(false), new GameDetector(), new ProcessExitHandler(), Classifier(), store,
            processEnumerator: () => throw new InvalidOperationException("Discovery must remain off."));
        using var http = new HttpClient(new NoHttp());
        var engine = new MultiFileTransactionEngine(Path.Combine(root.RootPath, "backups"));
        bool result;
        string? reason;
        if (restore)
        {
            var rollback = new DxvkRollback(engine, store, null, coordinator, activity);
            result = await rollback.RestoreOriginalDllsAsync(profile);
            reason = rollback.LastRefusalReason;
        }
        else
        {
            var installer = new DxvkInstaller(http, engine, store, Path.Combine(root.RootPath, "source"), null, coordinator, activity);
            result = await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "v2.6" });
            reason = installer.LastRefusalReason;
        }
        Assert.False(result);
        Assert.True(activityEnumerations > 0);
        Assert.Contains(unknown ? "Cannot verify game activity" : "currently running", reason);
        AssertSnapshot(before, target);
        Assert.Equal(libraryBefore, File.ReadAllBytes(store.LibraryFilePath));
        Assert.Null(store.FindByInstallationPath(target)!.PendingAction);
        Assert.False(monitor.HasPollingTimer);
    }

    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Refusal must precede HTTP.");
    }

    private static Dictionary<string, byte[]> Snapshot(string root)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertSnapshot(Dictionary<string, byte[]> expected, string root)
    {
        var actual = Snapshot(root);
        Assert.Equal(expected.Keys.OrderBy(x => x), actual.Keys.OrderBy(x => x));
        foreach (var entry in expected) Assert.Equal(entry.Value, actual[entry.Key]);
    }
}
