using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class ProcessActivityBoundaryTests
{
    private sealed class FakeProcessInfo
    {
        public int Id { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string? MainModulePath { get; set; }
        public bool ThrowAccessDeniedOnPath { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
    }

    private static ProcessActivityInspector CreateFakeInspector(
        IEnumerable<FakeProcessInfo> fakeProcesses,
        Action<int, DateTime>? onRecordStart = null)
    {
        var procList = new List<FakeProcessInfo>(fakeProcesses);

        return new ProcessActivityInspector(
            processEnumerator: () =>
            {
                // Return dummy handles or empty array; we route via name/path/id resolvers
                return Array.Empty<System.Diagnostics.Process>();
            },
            processNameResolver: null,
            mainModulePathResolver: null,
            startTimeResolver: null)
        {
            // Custom delegate hooks for synthetic test simulation
        };
    }

    [Fact]
    public void CandidateRelevanceFiltering_UnrelatedInaccessibleProcess_ReturnsConfirmedNotRunning()
    {
        using var tempDir = new SyntheticTestDirectory();
        var installRoot = Path.Combine(tempDir.RootPath, "MyGame");
        Directory.CreateDirectory(installRoot);

        // Simulation: System process with PID 4, process name "svchost", access denied on main module
        var inspector = new ProcessActivityInspector(
            processEnumerator: () =>
            {
                // Returns current process as stand-in process
                return new[] { System.Diagnostics.Process.GetCurrentProcess() };
            },
            processNameResolver: p => "svchost",
            mainModulePathResolver: p => throw new Win32Exception(5, "Access is denied."),
            startTimeResolver: p => DateTime.UtcNow);

        var evidence = inspector.GetActivityEvidence(installRoot, new[] { "Game.exe", "Launcher.exe" });

        // Unrelated process (svchost != Game.exe) must NOT cause Unknown; must be ConfirmedNotRunning
        Assert.Equal(ProcessActivityEvidence.ConfirmedNotRunning, evidence);
    }

    [Fact]
    public void CandidateProcess_AccessDenied_ReturnsUnknown()
    {
        using var tempDir = new SyntheticTestDirectory();
        var installRoot = Path.Combine(tempDir.RootPath, "MyGame");
        Directory.CreateDirectory(installRoot);

        // Simulation: Process name matches candidate "Game", but query for main module path throws access denied
        var inspector = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => "Game",
            mainModulePathResolver: p => throw new Win32Exception(5, "Access is denied."),
            startTimeResolver: p => DateTime.UtcNow);

        var evidence = inspector.GetActivityEvidence(installRoot, new[] { "Game.exe" });

        Assert.Equal(ProcessActivityEvidence.Unknown, evidence);
    }

    [Fact]
    public void CandidateProcess_RunningInInstallationRoot_ReturnsRunning()
    {
        using var tempDir = new SyntheticTestDirectory();
        var installRoot = Path.Combine(tempDir.RootPath, "MyGame");
        Directory.CreateDirectory(installRoot);
        var exePath = Path.Combine(installRoot, "Game.exe");

        var inspector = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => "Game",
            mainModulePathResolver: p => exePath,
            startTimeResolver: p => DateTime.UtcNow);

        var evidence = inspector.GetActivityEvidence(installRoot, new[] { "Game.exe" });

        Assert.Equal(ProcessActivityEvidence.Running, evidence);
    }

    [Fact]
    public void CandidateProcess_RunningInDifferentRoot_ReturnsConfirmedNotRunning()
    {
        using var tempDir = new SyntheticTestDirectory();
        var installRoot = Path.Combine(tempDir.RootPath, "MyGame");
        var otherRoot = Path.Combine(tempDir.RootPath, "OtherGame");
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(otherRoot);
        var otherExePath = Path.Combine(otherRoot, "Game.exe");

        var inspector = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => "Game",
            mainModulePathResolver: p => otherExePath,
            startTimeResolver: p => DateTime.UtcNow);

        var evidence = inspector.GetActivityEvidence(installRoot, new[] { "Game.exe" });

        Assert.Equal(ProcessActivityEvidence.ConfirmedNotRunning, evidence);
    }

    [Fact]
    public void RegisteredSiblingExecutable_RunningInRoot_ReturnsRunning()
    {
        using var tempDir = new SyntheticTestDirectory();
        var installRoot = Path.Combine(tempDir.RootPath, "MyGame");
        Directory.CreateDirectory(installRoot);
        var launcherPath = Path.Combine(installRoot, "bin", "Launcher.exe");

        var inspector = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => "Launcher",
            mainModulePathResolver: p => launcherPath,
            startTimeResolver: p => DateTime.UtcNow);

        // Candidate list contains primary Game.exe and registered sibling Launcher.exe
        var evidence = inspector.GetActivityEvidence(installRoot, new[] { "Game.exe", @"bin\Launcher.exe" });

        Assert.Equal(ProcessActivityEvidence.Running, evidence);
    }

    [Fact]
    public async Task DxvkManager_EnableDxvkAsync_RunningGame_RefusedWithoutAttentionRequired()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var gameDir = Path.Combine(tempDir.RootPath, "Game");
        Directory.CreateDirectory(gameDir);
        var exePath = Path.Combine(gameDir, "Game.exe");
        File.WriteAllText(exePath, "dummy-game");

        var storePath = Path.Combine(tempDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(tempDir.RootPath, "backups");
        var profilesPath = Path.Combine(tempDir.RootPath, "profiles.json");
        var sourceDir = Path.Combine(tempDir.RootPath, "source");
        Directory.CreateDirectory(sourceDir);

        var inspector = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => "Game",
            mainModulePathResolver: p => exePath,
            startTimeResolver: p => DateTime.UtcNow);

        using var http = new HttpClient();
        var store = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var profileStore = new ProfileStore(profilesPath) { LeaseCoordinator = coordinator };
        var engine = new MultiFileTransactionEngine(backupsPath);
        var installer = new DxvkInstaller(http, engine, store, sourceDir)
        {
            LeaseCoordinator = coordinator,
            ActivityInspector = inspector
        };
        var rollback = new DxvkRollback(engine, store)
        {
            LeaseCoordinator = coordinator,
            ActivityInspector = inspector
        };
        var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(http, new CacheStore()), profileStore, store, coordinator, inspector);

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profileStore.Save(profile);

        // Act: attempt to enable DXVK while game is running
        bool result = await manager.EnableDxvkAsync(profile, "2.5");

        // Assert: Refused, diagnostic set, AttentionRequired is NOT latched
        Assert.False(result);
        Assert.NotNull(manager.LastRefusalReason);
        Assert.Contains("running", manager.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        var inst = store.FindByInstallationPath(gameDir);
        if (inst != null)
        {
            Assert.NotEqual(RestorationState.AttentionRequired, inst.RestorationState);
        }
    }

    [Fact]
    public async Task DxvkManager_ApplyPendingAsync_RunningOrUnknown_PreservesPendingIntent()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var gameDir = Path.Combine(tempDir.RootPath, "Game");
        Directory.CreateDirectory(gameDir);
        var exePath = Path.Combine(gameDir, "Game.exe");
        File.WriteAllText(exePath, "dummy-game");

        var storePath = Path.Combine(tempDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(tempDir.RootPath, "backups");
        var profilesPath = Path.Combine(tempDir.RootPath, "profiles.json");
        var sourceDir = Path.Combine(tempDir.RootPath, "source");
        Directory.CreateDirectory(sourceDir);

        // Simulation: candidate process has access denied -> Unknown
        var inspector = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => "Game",
            mainModulePathResolver: p => throw new Win32Exception(5, "Access is denied."),
            startTimeResolver: p => DateTime.UtcNow);

        using var http = new HttpClient();
        var store = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var profileStore = new ProfileStore(profilesPath) { LeaseCoordinator = coordinator };
        var engine = new MultiFileTransactionEngine(backupsPath);
        var installer = new DxvkInstaller(http, engine, store, sourceDir)
        {
            LeaseCoordinator = coordinator,
            ActivityInspector = inspector
        };
        var rollback = new DxvkRollback(engine, store)
        {
            LeaseCoordinator = coordinator,
            ActivityInspector = inspector
        };
        var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(http, new CacheStore()), profileStore, store, coordinator, inspector);

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profileStore.Save(profile);

        // First queue with game running
        var runningInspector = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => "Game",
            mainModulePathResolver: p => exePath,
            startTimeResolver: p => DateTime.UtcNow);
        manager.ActivityInspector = runningInspector;
        installer.ActivityInspector = runningInspector;

        var queueResult = await manager.RequestEnableByPathAsync(profile, "2.5");
        Assert.Equal(DxvkActionResult.Queued, queueResult);
        Assert.True(manager.HasTransientPendingAction(exePath));

        // Now set inspector to throw access denied -> Unknown
        manager.ActivityInspector = inspector;
        installer.ActivityInspector = inspector;

        // Act: ApplyPendingAsync while activity is Unknown
        bool result = await manager.ApplyPendingAsync(exePath);

        // Assert: Refused, but intent is PRESERVED in both store and manager transient dictionary
        Assert.False(result);
        Assert.True(manager.HasTransientPendingAction(exePath));
        Assert.NotNull(manager.LastRefusalReason);
        Assert.Contains("access denied", manager.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        var reloadedStore = new GameLibraryStore(storePath, backupsPath);
        var reloadedInst = reloadedStore.FindByInstallationPath(gameDir);
        Assert.NotNull(reloadedInst);
        Assert.NotNull(reloadedInst.PendingAction);
        Assert.Equal("2.5", reloadedInst.PendingAction.TargetDxvkVersion);
        Assert.True(reloadedInst.PendingAction.IsPending);
        Assert.NotEqual(RestorationState.AttentionRequired, reloadedInst.RestorationState);
    }

    [Fact]
    public void ProcessNameQuery_Win32Exception_ReturnsUnknown()
    {
        using var tempDir = new SyntheticTestDirectory();
        var installRoot = Path.Combine(tempDir.RootPath, "MyGame");
        Directory.CreateDirectory(installRoot);

        var inspector = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => throw new Win32Exception(5, "Access is denied."),
            mainModulePathResolver: p => null,
            startTimeResolver: p => DateTime.UtcNow);

        var evidence = inspector.GetActivityEvidence(installRoot, new[] { "Game.exe" });
        Assert.Equal(ProcessActivityEvidence.Unknown, evidence);
    }

    [Fact]
    public void MainModuleQuery_InvalidOperationException_UnconfirmedExit_ReturnsUnknown()
    {
        using var tempDir = new SyntheticTestDirectory();
        var installRoot = Path.Combine(tempDir.RootPath, "MyGame");
        Directory.CreateDirectory(installRoot);

        // When InvalidOperationException occurs while process is NOT confirmed exited, returns Unknown
        var inspector = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => "Game",
            mainModulePathResolver: p => throw new InvalidOperationException("Process query failed."),
            startTimeResolver: p => DateTime.UtcNow);

        var evidence = inspector.GetActivityEvidence(installRoot, new[] { "Game.exe" });
        Assert.Equal(ProcessActivityEvidence.Unknown, evidence);
    }

    [Fact]
    public void ProcessLifetimeRevalidation_PidReplacement_ReturnsUnknown()
    {
        using var tempDir = new SyntheticTestDirectory();
        var installRoot = Path.Combine(tempDir.RootPath, "MyGame");
        Directory.CreateDirectory(installRoot);
        var exePath = Path.Combine(installRoot, "Game.exe");

        int startTimeCallCount = 0;
        var inspector = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => "Game",
            mainModulePathResolver: p => exePath,
            startTimeResolver: p =>
            {
                startTimeCallCount++;
                // First call before module inspection returns t1; second call after module inspection returns t2
                return startTimeCallCount == 1 ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc);
            });

        var evidence = inspector.GetActivityEvidence(installRoot, new[] { "Game.exe" });
        Assert.Equal(ProcessActivityEvidence.Unknown, evidence);
    }

    [Fact]
    public void ProcessEnumeration_LazyException_ReturnsUnknown()
    {
        using var tempDir = new SyntheticTestDirectory();
        var installRoot = Path.Combine(tempDir.RootPath, "MyGame");
        Directory.CreateDirectory(installRoot);

        var inspector = new ProcessActivityInspector(
            processEnumerator: () => throw new Win32Exception(5, "Process enumeration denied."),
            processNameResolver: null,
            mainModulePathResolver: null,
            startTimeResolver: null);

        var evidence = inspector.GetActivityEvidence(installRoot, new[] { "Game.exe" });
        Assert.Equal(ProcessActivityEvidence.Unknown, evidence);
    }

    [Fact]
    public async Task DownloadStageRace_ActivityDetected_AbortsBeforeMutation_FilesUntouched()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var gameDir = Path.Combine(tempDir.RootPath, "Game");
        Directory.CreateDirectory(gameDir);
        var exePath = Path.Combine(gameDir, "Game.exe");
        File.WriteAllText(exePath, "game-binary");
        var d3d11Path = Path.Combine(gameDir, "d3d11.dll");
        File.WriteAllText(d3d11Path, "pristine-native-d3d11");

        var storePath = Path.Combine(tempDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(tempDir.RootPath, "backups");
        var sourceDir = Path.Combine(tempDir.RootPath, "source");
        var archDir = Path.Combine(sourceDir, "2.6", "x64");
        Directory.CreateDirectory(archDir);
        File.WriteAllText(Path.Combine(archDir, "d3d11.dll"), "dxvk-2.6-d3d11");
        File.WriteAllText(Path.Combine(archDir, "dxgi.dll"), "dxvk-2.6-dxgi");

        int enumCallCount = 0;
        var raceInspector = new ProcessActivityInspector(
            processEnumerator: () =>
            {
                enumCallCount++;
                // Call 1 is pre-download check (clean); Call 2 is post-download revalidation check (running)
                return enumCallCount > 1 ? new[] { System.Diagnostics.Process.GetCurrentProcess() } : Array.Empty<System.Diagnostics.Process>();
            },
            processNameResolver: p => "Game",
            mainModulePathResolver: p => exePath,
            startTimeResolver: p => DateTime.UtcNow);

        using var http = new HttpClient();
        var store = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var engine = new MultiFileTransactionEngine(backupsPath);
        var installer = new DxvkInstaller(http, engine, store, sourceDir)
        {
            LeaseCoordinator = coordinator,
            ActivityInspector = raceInspector
        };

        var profile = new GameProfile(exePath) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var release = new ReleaseInfo { Version = "2.6", DownloadUrl = "" };

        bool result = await installer.ApplyToGameAsync(profile, release);

        // Must refuse
        Assert.False(result);
        Assert.NotNull(installer.LastRefusalReason);
        Assert.Contains("running", installer.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Disk files must be completely untouched!
        Assert.Equal("pristine-native-d3d11", File.ReadAllText(d3d11Path));
        Assert.False(File.Exists(Path.Combine(gameDir, "dxgi.dll")));

        // Backups directory must be completely empty (no backup taken)
        if (Directory.Exists(backupsPath))
        {
            Assert.Empty(Directory.EnumerateFiles(backupsPath, "*", SearchOption.AllDirectories));
        }

        // Store must not have any managed record
        var inst = store.FindByInstallationPath(gameDir);
        Assert.True(inst == null || inst.RestorationState != RestorationState.Managed);
    }
}
