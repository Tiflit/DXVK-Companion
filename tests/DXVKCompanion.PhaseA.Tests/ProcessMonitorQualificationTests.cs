using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Monitoring;
using DXVKCompanion.Storage;
using DXVKCompanion.Safety;
using DXVKCompanion.UI;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class ProcessMonitorQualificationTests
{
    private sealed class FakeProcessExitHandler : ProcessExitHandler
    {
        public List<Process> AttachedProcesses { get; } = new();

        internal override void AttachCore(Process process)
        {
            AttachedProcesses.Add(process);
        }

        public void SimulateExit(string exePath)
        {
            RaiseProcessExited(exePath);
        }
    }

    private sealed class DynamicModuleScanner : ModuleScanner
    {
        public Func<Process, ModuleScanResult> ScannerFunc { get; set; }
        public int ScanCallCount { get; private set; }

        public DynamicModuleScanner(Func<Process, ModuleScanResult> func)
        {
            ScannerFunc = func;
        }

        internal override ModuleScanResult ScanLoadedGraphicsModules(Process process)
        {
            ScanCallCount++;
            return ScannerFunc(process);
        }
    }

    private sealed class FakePeParser : PeParser
    {
        private readonly IEnumerable<string> _imports;
        private readonly string _arch;

        public FakePeParser(IEnumerable<string>? imports = null, string arch = "x64")
        {
            _imports = imports ?? Array.Empty<string>();
            _arch = arch;
        }

        public override IEnumerable<string> GetImports(string path) => _imports;
        public override string GetArchitecture(string path) => _arch;
    }

    [Fact]
    public void UnknownCandidate_WithWindow_CausesZeroProfileCreation_ZeroLibraryMutations_ZeroActiveGameEvents()
    {
        using var tempDir = new SyntheticTestDirectory();
        string gamesPath = Path.Combine(tempDir.RootPath, "games.json");
        string libraryPath = Path.Combine(tempDir.RootPath, "game-library.json");
        string backupsPath = Path.Combine(tempDir.RootPath, "backups");
        var libraryStore = new GameLibraryStore(libraryPath, backupsPath);
        var profileStore = new ProfileStore(gamesPath);

        var detector = new GameDetector();
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>()));
        var parser = new FakePeParser();
        var classifier = new ApiClassifier(scanner, parser);

        TimeSpan currentElapsed = TimeSpan.Zero;
        using var currentProc = Process.GetCurrentProcess();
        var fakeStartTime = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore,
            clock: () => currentElapsed,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.ProfileStore = profileStore;
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => fakeStartTime;
        monitor.ExePathResolver = _ => @"C:\Games\UnknownApp\App.exe";

        int qualifiedEvents = 0;
        int gameDetectedEvents = 0;
        int snapshotEvents = 0;

        monitor.OnQualifiedGameDetected += (_, _) => qualifiedEvents++;
        monitor.OnGameDetected += _ => gameDetectedEvents++;
        monitor.OnSnapshotDetected += _ => snapshotEvents++;

        // Act: Poll once at t=0s
        monitor.PollProcessesOnce();

        // Assert: zero admissions, zero events, zero mutations
        Assert.Equal(0, qualifiedEvents);
        Assert.Equal(0, gameDetectedEvents);
        Assert.Equal(0, snapshotEvents);
        Assert.Empty(libraryStore.GetAll());
        Assert.Empty(profileStore.GetAll());

        var candidate = monitor.GetCandidateState(currentProc.Id);
        Assert.NotNull(candidate);
        Assert.False(candidate!.IsQualified);
        Assert.False(candidate.IsExpired);
        Assert.Equal(1, candidate.AssessmentsConductedCount);
    }

    [Fact]
    public void Candidate_QualifyingOnDelayedAssessment_TransitionsToActiveAndStoredState()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));

        var detector = new GameDetector();
        var exitHandler = new FakeProcessExitHandler();

        var loadedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>(loadedModules)));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        TimeSpan currentElapsed = TimeSpan.Zero;
        using var currentProc = Process.GetCurrentProcess();
        var fakeStartTime = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore,
            clock: () => currentElapsed,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => fakeStartTime;
        monitor.ExePathResolver = _ => @"C:\Games\Game11\Game.exe";

        int qualifiedEvents = 0;
        DetectionSnapshot? capturedSnapshot = null;
        monitor.OnQualifiedGameDetected += (_, s) =>
        {
            qualifiedEvents++;
            capturedSnapshot = s;
        };

        // Poll at t=0s: no graphics modules loaded yet
        monitor.PollProcessesOnce();
        Assert.Equal(0, qualifiedEvents);
        Assert.Empty(libraryStore.GetAll());

        // At t=2s: graphics module loads
        currentElapsed = TimeSpan.FromSeconds(2);
        loadedModules.Add("d3d11.dll");

        // Poll at t=2s: qualification occurs
        monitor.PollProcessesOnce();

        Assert.Equal(1, qualifiedEvents);
        Assert.NotNull(capturedSnapshot);
        Assert.Equal(GraphicsApi.DX11, capturedSnapshot!.Classification.PrimaryApi);
        Assert.True(monitor.TryGetQualifiedSnapshot(currentProc.Id, out var storedSnapshot));
        Assert.NotNull(storedSnapshot);
        Assert.Equal(GraphicsApi.DX11, storedSnapshot!.Classification.PrimaryApi);
        Assert.NotEmpty(libraryStore.GetAll());
    }

    [Theory]
    [InlineData("d3d9.dll", GraphicsApi.DX9)]
    [InlineData("d3d10.dll", GraphicsApi.DX10)]
    [InlineData("d3d11.dll", GraphicsApi.DX11)]
    [InlineData("d3d12.dll", GraphicsApi.DX12)]
    [InlineData("vulkan-1.dll", GraphicsApi.Vulkan)]
    public void PositiveControls_AllRecognizedApis_SuccessfullyQualify(string moduleName, GraphicsApi expectedApi)
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));

        var detector = new GameDetector();
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { moduleName }));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => @"C:\Games\PositiveControl\Game.exe";

        bool qualified = false;
        monitor.OnQualifiedGameDetected += (_, s) =>
        {
            qualified = true;
            Assert.Equal(expectedApi, s.Classification.PrimaryApi);
        };

        monitor.PollProcessesOnce();
        Assert.True(qualified);
        Assert.True(monitor.TryGetQualifiedSnapshot(currentProc.Id, out _));
    }

    [Fact]
    public void ModernApi_DX12_ObservationPermitted_WhileDeploymentRefused()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));

        var detector = new GameDetector();
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "d3d12.dll" }));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => @"C:\Games\DX12Game\Game.exe";

        monitor.PollProcessesOnce();

        Assert.True(monitor.TryGetQualifiedSnapshot(currentProc.Id, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(GraphicsApi.DX12, snapshot!.Classification.PrimaryApi);

        // Verification of deployment refusal: DX12 is recognized for observation but rejected for DXVK deployment
        var installations = libraryStore.GetAll().ToList();
        Assert.Single(installations);
        var inst = installations[0];
        bool supported = DxvkCompatibility.IsInstallationSupported(inst, GraphicsApi.DX12, "Game.exe", out var refusalReason);
        Assert.False(supported);
        Assert.NotNull(refusalReason);
        Assert.Contains("unsupported API 'DX12'", refusalReason);
    }

    [Fact]
    public void StaticPEImportsFallback_QualifiesWithMediumConfidence_AndPreservesProvenance()
    {
        using var tempDir = new SyntheticTestDirectory();
        var fakeExe = tempDir.CreateFile("Game.exe", "synthetic-binary");

        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));

        var detector = new GameDetector();
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Incomplete(new HashSet<string>(), "ScanDenied"));
        var parser = new FakePeParser(new[] { "d3d9.dll" }, "x32");
        var classifier = new ApiClassifier(scanner, parser);

        using var currentProc = Process.GetCurrentProcess();
        var classification = classifier.ClassifyDetailed(currentProc, fakeExe);

        Assert.Equal(GraphicsApi.DX9, classification.PrimaryApi);
        Assert.Equal(ApiDetectionConfidence.Medium, classification.Confidence);
        Assert.Equal("StaticPEImports", classification.EvidenceSource);
        Assert.Contains(classification.Evidence, e => e.Contains("ScanDenied"));
        Assert.Contains(classification.Evidence, e => e.Contains("d3d9.dll"));
    }

    [Fact]
    public void BackoffSchedule_ExecutesExactly12Assessments_NoWorkBeforeDueTimes_AndExpiresCleanly()
    {
        var detector = new GameDetector();
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>()));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        TimeSpan currentElapsed = TimeSpan.Zero;
        using var currentProc = Process.GetCurrentProcess();

        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore: null,
            clock: () => currentElapsed,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => @"C:\Games\Testing\App.exe";

        int[] offsets = ProcessQualification.ScheduledOffsetsSeconds;
        Assert.Equal(12, offsets.Length);

        // At t=0s offset: Assessment 1 executes
        monitor.PollProcessesOnce();
        Assert.Equal(1, scanner.ScanCallCount);

        // At t=1s offset: Assessment is NOT due, scanner must not be invoked
        currentElapsed = TimeSpan.FromSeconds(1);
        monitor.PollProcessesOnce();
        Assert.Equal(1, scanner.ScanCallCount);

        // Advance through remaining 11 offsets
        for (int i = 1; i < offsets.Length; i++)
        {
            // Intermediate offset before due time: no assessment
            currentElapsed = TimeSpan.FromSeconds(offsets[i] - 1);
            monitor.PollProcessesOnce();
            Assert.Equal(i, scanner.ScanCallCount);

            // Due offset: assessment executes
            currentElapsed = TimeSpan.FromSeconds(offsets[i]);
            monitor.PollProcessesOnce();
            Assert.Equal(i + 1, scanner.ScanCallCount);
        }

        // Total assessments conducted must be exactly 12
        Assert.Equal(12, scanner.ScanCallCount);

        var candidate = monitor.GetCandidateState(currentProc.Id);
        Assert.NotNull(candidate);
        Assert.True(candidate!.IsExpired);

        // Subsequent poll at t=150s: no more assessments conducted
        currentElapsed = TimeSpan.FromSeconds(150);
        monitor.PollProcessesOnce();
        Assert.Equal(12, scanner.ScanCallCount);
    }

    [Fact]
    public void CatchUpBurstAvoidance_SingleAssessmentOnDelayedTick()
    {
        var detector = new GameDetector();
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>()));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        TimeSpan currentElapsed = TimeSpan.Zero;
        using var currentProc = Process.GetCurrentProcess();

        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore: null,
            clock: () => currentElapsed,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => @"C:\Games\Burst\App.exe";

        // Assessment 1 at t=0s
        monitor.PollProcessesOnce();
        Assert.Equal(1, scanner.ScanCallCount);

        // Clock suddenly jumps by 45 seconds
        currentElapsed = TimeSpan.FromSeconds(45);
        monitor.PollProcessesOnce();

        // Exactly one assessment must execute during this tick (no catch-up burst)
        Assert.Equal(2, scanner.ScanCallCount);

        var candidate = monitor.GetCandidateState(currentProc.Id);
        Assert.NotNull(candidate);
        // Next due offset should have advanced past 44 to 60s (index 9)
        Assert.Equal(9, candidate!.NextAssessmentIndex);
    }

    [Fact]
    public void ProcessExitDuringBackoff_CleansUpCandidateAndWindowRetries()
    {
        var detector = new GameDetector();
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>()));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        bool processRunning = true;

        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore: null,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => processRunning ? new[] { currentProc } : Array.Empty<Process>(),
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => @"C:\Games\Exiting\App.exe";

        // Candidate registered
        monitor.PollProcessesOnce();
        Assert.NotNull(monitor.GetCandidateState(currentProc.Id));

        // Process exits
        processRunning = false;
        monitor.PollProcessesOnce();

        // State cleaned up cleanly without leaking
        Assert.Null(monitor.GetCandidateState(currentProc.Id));
    }

    [Fact]
    public void PreservedExistingLibraryEntries_AndPendingActionExitTracking_OnUnknownObservation()
    {
        using var tempDir = new SyntheticTestDirectory();
        var gamesPath = Path.Combine(tempDir.RootPath, "games.json");
        var libraryPath = Path.Combine(tempDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(tempDir.RootPath, "backups");
        var libraryStore = new GameLibraryStore(libraryPath, backupsPath);
        var profileStore = new ProfileStore(gamesPath);

        string targetExe = @"C:\Games\ExistingGame\Game.exe";
        string installDir = @"C:\Games\ExistingGame";
        string relExe = "Game.exe";

        // Seed an existing library entry with DX9 and a PendingAction
        var initialSnapshot = new DetectionSnapshot
        {
            ProcessId = 9999,
            ProcessName = "TestGame",
            ExecutablePath = targetExe,
            InstallationRoot = installDir,
            ExecutableRelativePath = relExe,
            Classification = new ApiClassificationResult
            {
                PrimaryApi = GraphicsApi.DX9,
                Confidence = ApiDetectionConfidence.High,
                Architecture = "x64",
                Evidence = new[] { "Loaded runtime module: d3d9.dll" }
            },
            TimestampUtc = DateTime.UtcNow
        };
        libraryStore.RecordDetectionSnapshot(initialSnapshot);

        var inst = libraryStore.FindByInstallationPath(installDir);
        Assert.NotNull(inst);
        inst!.PendingAction = PendingAction.Install("2.5", "Queued upgrade");
        libraryStore.Save(inst);

        var profile = profileStore.GetOrCreate(targetExe);
        profile.Api = GraphicsApi.DX9;
        profileStore.Save(profile);

        byte[] libraryBytesBefore = File.ReadAllBytes(libraryPath);
        byte[] gamesBytesBefore = File.ReadAllBytes(gamesPath);

        // Now run ProcessMonitor where process produces NO graphics modules (Unknown / Incomplete)
        var detector = new GameDetector();
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>()));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.ProfileStore = profileStore;
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => targetExe;

        // Act
        monitor.PollProcessesOnce();

        // Assert: Exit handler IS attached so game exit will process the pending action
        Assert.Contains(currentProc, exitHandler.AttachedProcesses);

        // Candidate did NOT qualify
        var candidate = monitor.GetCandidateState(currentProc.Id);
        Assert.NotNull(candidate);
        Assert.False(candidate!.IsQualified);

        // Library store and profiles bytes remain identical
        byte[] libraryBytesAfter = File.ReadAllBytes(libraryPath);
        byte[] gamesBytesAfter = File.ReadAllBytes(gamesPath);
        Assert.Equal(libraryBytesBefore, libraryBytesAfter);
        Assert.Equal(gamesBytesBefore, gamesBytesAfter);

        var refreshedInst = libraryStore.FindByInstallationPath(installDir);
        Assert.NotNull(refreshedInst!.PendingAction);
        Assert.Equal("2.5", refreshedInst.PendingAction!.TargetDxvkVersion);
        var exec = refreshedInst.FindExecutable(relExe);
        Assert.Equal(GraphicsApi.DX9, exec!.LastKnownApi);
    }

    [Fact]
    public void PreservedExistingLibraryEntries_WithPriorUnsupportedApi()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryPath = Path.Combine(tempDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(tempDir.RootPath, "backups");
        var libraryStore = new GameLibraryStore(libraryPath, backupsPath);

        string targetExe = @"C:\Games\DX12Game\Game.exe";
        string installDir = @"C:\Games\DX12Game";
        string relExe = "Game.exe";

        var initialSnapshot = new DetectionSnapshot
        {
            ProcessId = 8888,
            ProcessName = "DX12Game",
            ExecutablePath = targetExe,
            InstallationRoot = installDir,
            ExecutableRelativePath = relExe,
            Classification = new ApiClassificationResult
            {
                PrimaryApi = GraphicsApi.DX12,
                Confidence = ApiDetectionConfidence.High,
                Architecture = "x64",
                Evidence = new[] { "Loaded runtime module: d3d12.dll" }
            },
            TimestampUtc = DateTime.UtcNow
        };
        libraryStore.RecordDetectionSnapshot(initialSnapshot);
        byte[] bytesBefore = File.ReadAllBytes(libraryPath);

        var detector = new GameDetector();
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>()));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => targetExe;

        monitor.PollProcessesOnce();

        byte[] bytesAfter = File.ReadAllBytes(libraryPath);
        Assert.Equal(bytesBefore, bytesAfter);

        var inst = libraryStore.FindByInstallationPath(installDir);
        var exec = inst!.FindExecutable(relExe);
        Assert.Equal(GraphicsApi.DX12, exec!.LastKnownApi);
    }

    [Fact]
    public void WindowlessKnownPendingExecutable_AttachesExitHandler_AndFiresExitNotification()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryPath = Path.Combine(tempDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(tempDir.RootPath, "backups");
        var libraryStore = new GameLibraryStore(libraryPath, backupsPath);

        string targetExe = @"C:\Games\PendingGame\Game.exe";
        string installDir = @"C:\Games\PendingGame";

        var inst = libraryStore.GetOrCreateInstallation(installDir, "PendingGame");
        inst.PendingAction = PendingAction.Install("2.5", "Queued headless install");
        libraryStore.Save(inst);

        var detector = new GameDetector();
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>()));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        // WindowFilter returns false: process has no window!
        monitor.WindowFilter = _ => false;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => targetExe;

        string? exitedExe = null;
        monitor.OnGameExited += path => exitedExe = path;

        // Poll runs
        monitor.PollProcessesOnce();

        // Exit handler must be attached even without window
        Assert.Contains(currentProc, exitHandler.AttachedProcesses);

        // Simulate exit
        exitHandler.SimulateExit(targetExe);
        Assert.Equal(targetExe, exitedExe);
    }

    [Fact]
    public void CachedExcludedPidReuse_ResetsWhenStartTimeDiffers()
    {
        var detector = new GameDetector();
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string> { "d3d11.dll" }));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        var time1 = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var time2 = new DateTime(2026, 10, 15, 12, 5, 0, DateTimeKind.Utc);

        DateTime currentProcStartTime = time1;
        bool isGameCheck = false;

        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore: null,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => isGameCheck;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => currentProcStartTime;
        monitor.ExePathResolver = _ => @"C:\Apps\Test.exe";

        // Poll 1: not a game -> PID cached in nonGamePids
        monitor.PollProcessesOnce();
        Assert.Null(monitor.GetCandidateState(currentProc.Id));

        // Now PID is reused by a new process instance with time2 and is a game
        currentProcStartTime = time2;
        isGameCheck = true;
        monitor.ExePathResolver = _ => @"C:\Games\NewGame\Game.exe";

        bool qualified = false;
        monitor.OnQualifiedGameDetected += (_, _) => qualified = true;

        // Poll 2: start time differs, so cached nonGame state is reset, candidate qualifies
        monitor.PollProcessesOnce();
        Assert.True(qualified);
    }

    [Fact]
    public void DisposalBoundary_PreventsNewStateOrEventsAfterDispose()
    {
        var detector = new GameDetector();
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string> { "d3d11.dll" }));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore: null,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => @"C:\Games\Test\Game.exe";

        int eventCount = 0;
        monitor.OnQualifiedGameDetected += (_, _) => eventCount++;

        // Dispose monitor before poll
        monitor.Dispose();

        // Poll runs after dispose
        monitor.PollProcessesOnce();

        Assert.Equal(0, eventCount);
        Assert.Null(monitor.GetCandidateState(currentProc.Id));
    }

    [Fact]
    public async Task HeadlessGameAdmission_Orchestration_DX11_Vs_Unknown_Vs_DX12()
    {
        using var tempDir = new SyntheticTestDirectory();
        var gamesPath = Path.Combine(tempDir.RootPath, "games.json");
        var libraryPath = Path.Combine(tempDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(tempDir.RootPath, "backups");
        var profiles = new ProfileStore(gamesPath);
        var libraryStore = new GameLibraryStore(libraryPath, backupsPath);
        var settings = new SettingsStore();
        var inspector = new ManagedFileInspector(libraryStore);
        var fileUtils = new FileUtils();
        var engine = new MultiFileTransactionEngine(backupsPath);
        var installer = new DxvkInstaller(new System.Net.Http.HttpClient(), engine, libraryStore, null, fileUtils);
        var rollback = new DxvkRollback(engine, libraryStore, fileUtils);
        var github = new DxvkGithubClient(new System.Net.Http.HttpClient(), new CacheStore(Path.Combine(tempDir.RootPath, "cache.json")));
        var dxvk = new DxvkManager(installer, rollback, github, profiles, libraryStore);

        // Case 1: Unknown snapshot -> returns false, 0 profiles created
        var unknownSnapshot = new DetectionSnapshot
        {
            ProcessId = 100,
            ProcessName = "Unknown",
            ExecutablePath = @"C:\Apps\Unknown.exe",
            InstallationRoot = @"C:\Apps",
            ExecutableRelativePath = "Unknown.exe",
            Classification = new ApiClassificationResult { PrimaryApi = GraphicsApi.Unknown },
            AntiCheat = AntiCheatAssessment.None()
        };

        bool admittedUnknown = await TrayApp.ProcessGameAdmissionAsync(
            unknownSnapshot, null, profiles, libraryStore, dxvk, inspector, settings);
        Assert.False(admittedUnknown);
        Assert.Empty(profiles.GetAll());

        // Case 2: DX11 snapshot -> returns true, profile and library record created
        var dx11Snapshot = new DetectionSnapshot
        {
            ProcessId = 200,
            ProcessName = "Game11",
            ExecutablePath = @"C:\Games\Game11\Game.exe",
            InstallationRoot = @"C:\Games\Game11",
            ExecutableRelativePath = "Game.exe",
            Classification = new ApiClassificationResult
            {
                PrimaryApi = GraphicsApi.DX11,
                Confidence = ApiDetectionConfidence.High,
                Architecture = "x64"
            },
            AntiCheat = AntiCheatAssessment.None()
        };

        bool admittedDx11 = await TrayApp.ProcessGameAdmissionAsync(
            dx11Snapshot, null, profiles, libraryStore, dxvk, inspector, settings);
        Assert.True(admittedDx11);
        Assert.Single(profiles.GetAll());
        Assert.Equal(GraphicsApi.DX11, profiles.GetAll().First().Api);

        // Case 3: DX12 snapshot -> returns true (recorded), but deployment refused
        var dx12Snapshot = new DetectionSnapshot
        {
            ProcessId = 300,
            ProcessName = "Game12",
            ExecutablePath = @"C:\Games\Game12\Game.exe",
            InstallationRoot = @"C:\Games\Game12",
            ExecutableRelativePath = "Game.exe",
            Classification = new ApiClassificationResult
            {
                PrimaryApi = GraphicsApi.DX12,
                Confidence = ApiDetectionConfidence.High,
                Architecture = "x64"
            },
            AntiCheat = AntiCheatAssessment.None()
        };

        bool admittedDx12 = await TrayApp.ProcessGameAdmissionAsync(
            dx12Snapshot, null, profiles, libraryStore, dxvk, inspector, settings);
        Assert.True(admittedDx12);
        var inst = libraryStore.FindByInstallationPath(@"C:\Games\Game12");
        Assert.NotNull(inst);
        bool deploySupported = DxvkCompatibility.IsInstallationSupported(inst, GraphicsApi.DX12, "Game.exe", out var refusalReason);
        Assert.False(deploySupported);
        Assert.NotNull(refusalReason);
    }
}
