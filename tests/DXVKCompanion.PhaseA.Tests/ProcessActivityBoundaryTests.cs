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

        // Set up pending action in store and transient queue
        var inst = store.GetOrCreateInstallation(gameDir, "Game");
        inst.PendingAction = Models.PendingAction.Install("2.5", "Queued test action");
        store.Save(inst);

        // Act: ApplyPendingAsync while activity is Unknown
        bool result = await manager.ApplyPendingAsync(exePath);

        // Assert: Refused, but intent is PRESERVED in both store and manager
        Assert.False(result);
        Assert.NotNull(manager.LastRefusalReason);
        Assert.Contains("access denied", manager.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        var reloadedInst = store.FindByInstallationPath(gameDir);
        Assert.NotNull(reloadedInst);
        Assert.NotNull(reloadedInst.PendingAction);
        Assert.Equal("2.5", reloadedInst.PendingAction.TargetDxvkVersion);
        Assert.True(reloadedInst.PendingAction.IsPending);
        Assert.NotEqual(RestorationState.AttentionRequired, reloadedInst.RestorationState);
    }
}
