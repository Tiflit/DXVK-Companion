using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
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
    private static readonly DateTime FixedStartTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static byte[] CreateValidTarGzArchive(string version)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        using (var tar = new TarWriter(gz))
        {
            var d3d11Entry = new PaxTarEntry(TarEntryType.RegularFile, $"dxvk-{version}/x64/d3d11.dll");
            byte[] d3d11Bytes = "synthetic-dxvk-d3d11"u8.ToArray();
            d3d11Entry.DataStream = new MemoryStream(d3d11Bytes);
            tar.WriteEntry(d3d11Entry);

            var dxgiEntry = new PaxTarEntry(TarEntryType.RegularFile, $"dxvk-{version}/x64/dxgi.dll");
            byte[] dxgiBytes = "synthetic-dxvk-dxgi"u8.ToArray();
            dxgiEntry.DataStream = new MemoryStream(dxgiBytes);
            tar.WriteEntry(dxgiEntry);
        }
        return ms.ToArray();
    }

    private sealed class BlockingHttpMessageHandler : HttpMessageHandler
    {
        private readonly byte[] _responseData;
        private readonly TaskCompletionSource<bool> _downloadStartedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _continueDownloadTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingHttpMessageHandler(byte[] responseData)
        {
            _responseData = responseData;
        }

        public async Task<bool> WaitForDownloadStartAsync(TimeSpan timeout = default)
        {
            if (timeout <= TimeSpan.Zero) timeout = TimeSpan.FromSeconds(5);
            var completed = await Task.WhenAny(_downloadStartedTcs.Task, Task.Delay(timeout));
            return completed == _downloadStartedTcs.Task;
        }

        public void ReleaseDownload() => _continueDownloadTcs.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _downloadStartedTcs.TrySetResult(true);
            await _continueDownloadTcs.Task.WaitAsync(cancellationToken);

            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_responseData)
            };
            return response;
        }
    }

    private static IEnumerable<System.Diagnostics.Process> ThrowingProcessEnumerable()
    {
        throw new Win32Exception(5, "Process enumeration denied during MoveNext().");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private sealed class FakeProcessInfo
    {
        public int Id { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string? MainModulePath { get; set; }
        public bool ThrowAccessDeniedOnPath { get; set; }
        public DateTime StartTime { get; set; } = FixedStartTime;
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
            startTimeResolver: p => FixedStartTime);

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
            startTimeResolver: p => FixedStartTime);

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
            startTimeResolver: p => FixedStartTime);

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
            startTimeResolver: p => FixedStartTime);

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
            startTimeResolver: p => FixedStartTime);

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
            startTimeResolver: p => FixedStartTime);

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
            startTimeResolver: p => FixedStartTime);

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
            startTimeResolver: p => FixedStartTime);
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
            startTimeResolver: p => FixedStartTime);

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
            startTimeResolver: p => FixedStartTime);

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
            processEnumerator: () => ThrowingProcessEnumerable(),
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
        Directory.CreateDirectory(sourceDir);

        byte[] tarGzData = CreateValidTarGzArchive("2.6");
        var handler = new BlockingHttpMessageHandler(tarGzData);
        using var http = new HttpClient(handler);

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
            startTimeResolver: p => FixedStartTime);

        var store = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var engine = new MultiFileTransactionEngine(backupsPath);
        var installer = new DxvkInstaller(http, engine, store, sourceDir)
        {
            LeaseCoordinator = coordinator,
            ActivityInspector = raceInspector
        };

        var profile = new GameProfile(exePath) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var release = new ReleaseInfo { Version = "2.6", DownloadUrl = "https://synthetic.test/dxvk-2.6.tar.gz" };

        var applyTask = installer.ApplyToGameAsync(profile, release);

        // Wait for download to begin
        bool started = await handler.WaitForDownloadStartAsync(TimeSpan.FromSeconds(5));
        Assert.True(started, "Timed out waiting for download to start.");

        // Release the download with the valid archive
        handler.ReleaseDownload();

        bool result = await applyTask;

        // Must refuse due to post-download activity detection
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

    [Fact]
    public async Task DownloadStageRace_ProfileChangedDuringDownload_AbortsBeforeMutation()
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
        var profilesPath = Path.Combine(tempDir.RootPath, "profiles.json");
        var sourceDir = Path.Combine(tempDir.RootPath, "source");
        Directory.CreateDirectory(sourceDir);

        byte[] tarGzData = CreateValidTarGzArchive("2.6");
        var handler = new BlockingHttpMessageHandler(tarGzData);
        using var http = new HttpClient(handler);

        var cleanInspector = new ProcessActivityInspector(
            processEnumerator: () => Array.Empty<System.Diagnostics.Process>(),
            processNameResolver: null,
            mainModulePathResolver: null,
            startTimeResolver: null);

        var store = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var profileStore = new ProfileStore(profilesPath) { LeaseCoordinator = coordinator };
        var engine = new MultiFileTransactionEngine(backupsPath);
        var installer = new DxvkInstaller(http, engine, store, sourceDir)
        {
            LeaseCoordinator = coordinator,
            ActivityInspector = cleanInspector,
            ProfileStore = profileStore
        };

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profileStore.Save(profile);

        var release = new ReleaseInfo { Version = "2.6", DownloadUrl = "https://synthetic.test/dxvk-2.6.tar.gz" };

        var applyTask = installer.ApplyToGameAsync(profile, release);

        // Wait for download to start
        bool started = await handler.WaitForDownloadStartAsync(TimeSpan.FromSeconds(5));
        Assert.True(started, "Timed out waiting for download to start.");

        // Concurrently mutate profile file on disk to simulate baseline invalidation during download
        File.WriteAllText(profilesPath, "{\"concurrent\": \"mutation\"}");

        // Release the download
        handler.ReleaseDownload();

        bool result = await applyTask;

        // Must refuse due to post-download profile baseline invalidation
        Assert.False(result);
        Assert.NotNull(installer.LastRefusalReason);
        Assert.Contains("Profile store baseline mismatch", installer.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Disk files untouched
        Assert.Equal("pristine-native-d3d11", File.ReadAllText(d3d11Path));
        Assert.False(File.Exists(Path.Combine(gameDir, "dxgi.dll")));

        // Backups directory empty
        if (Directory.Exists(backupsPath))
        {
            Assert.Empty(Directory.EnumerateFiles(backupsPath, "*", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public void CandidateProcess_WithEmbeddedDots_MatchesImageName()
    {
        using var tempDir = new SyntheticTestDirectory();
        var installRoot = Path.Combine(tempDir.RootPath, "MyGame");
        Directory.CreateDirectory(installRoot);
        var exePath = Path.Combine(installRoot, "Game.Client.exe");

        // Process name reported without .exe (e.g. "Game.Client") matches candidate "Game.Client.exe"
        var inspector1 = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => "Game.Client",
            mainModulePathResolver: p => exePath,
            startTimeResolver: p => FixedStartTime);

        var evidence1 = inspector1.GetActivityEvidence(installRoot, new[] { "Game.Client.exe" });
        Assert.Equal(ProcessActivityEvidence.Running, evidence1);

        // Process name reported with .exe (e.g. "Game.Client.exe") matches candidate "Game.Client.exe"
        var inspector2 = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => "Game.Client.exe",
            mainModulePathResolver: p => exePath,
            startTimeResolver: p => FixedStartTime);

        var evidence2 = inspector2.GetActivityEvidence(installRoot, new[] { "Game.Client.exe" });
        Assert.Equal(ProcessActivityEvidence.Running, evidence2);

        // Non-matching process "Game.Server" does not match candidate "Game.Client.exe"
        var inspector3 = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => "Game.Server",
            mainModulePathResolver: p => exePath,
            startTimeResolver: p => FixedStartTime);

        var evidence3 = inspector3.GetActivityEvidence(installRoot, new[] { "Game.Client.exe" });
        Assert.Equal(ProcessActivityEvidence.ConfirmedNotRunning, evidence3);
    }

    [Fact]
    public void DxvkInstaller_AdoptExisting_SubdirectoryExecutable_VerifiesAuthoritativeRoot_RunningSiblingBlocks()
    {
        using var tempDir = new SyntheticTestDirectory();
        using var coordDir = new SyntheticTestDirectory();

        var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
        var rootDir = Path.Combine(tempDir.RootPath, "MyGame");
        var subDir = Path.Combine(rootDir, "bin");
        Directory.CreateDirectory(subDir);

        var subExe = Path.Combine(subDir, "Game.exe");
        File.WriteAllText(subExe, "sub-game-exe");
        var d3d11Dll = Path.Combine(subDir, "d3d11.dll");
        File.WriteAllText(d3d11Dll, "dxvk-d3d11-content");

        var siblingExe = Path.Combine(rootDir, "Launcher.exe");
        File.WriteAllText(siblingExe, "launcher-exe");

        var storePath = Path.Combine(tempDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(tempDir.RootPath, "backups");
        var sourceDir = Path.Combine(tempDir.RootPath, "source");
        Directory.CreateDirectory(sourceDir);

        var store = new GameLibraryStore(storePath, backupsPath) { LeaseCoordinator = coordinator };
        var installation = store.GetOrCreateInstallation(rootDir, "My Game");
        installation.GetOrAddExecutable("bin/Game.exe", "Game Client");
        installation.GetOrAddExecutable("Launcher.exe", "Game Launcher");
        store.Save(installation);

        // Inspector simulates Launcher.exe running in authoritative root
        var inspector = new ProcessActivityInspector(
            processEnumerator: () => new[] { System.Diagnostics.Process.GetCurrentProcess() },
            processNameResolver: p => "Launcher",
            mainModulePathResolver: p => siblingExe,
            startTimeResolver: p => FixedStartTime);

        using var http = new HttpClient();
        var engine = new MultiFileTransactionEngine(backupsPath);
        var installer = new DxvkInstaller(http, engine, store, sourceDir)
        {
            LeaseCoordinator = coordinator,
            ActivityInspector = inspector
        };

        var profile = new GameProfile(subExe) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var assessment = new ExistingDxvkAssessment
        {
            Status = ExistingDxvkStatus.OfficialRelease,
            MatchedVersion = "2.5"
        };

        bool adopted = installer.AdoptExisting(profile, assessment);

        // Must refuse because running Launcher under authoritativeRoot was detected
        Assert.False(adopted);
        Assert.NotNull(installer.LastRefusalReason);
        Assert.Contains("running", installer.LastRefusalReason, StringComparison.OrdinalIgnoreCase);
    }
}
