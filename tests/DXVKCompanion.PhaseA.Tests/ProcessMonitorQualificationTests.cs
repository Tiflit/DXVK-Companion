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

        public void SimulateInstanceExit(int pid, string exePath)
        {
            RaiseProcessInstanceExited(pid, exePath);
        }
    }

    private sealed class FakeHttpMessageHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly System.Net.HttpStatusCode _statusCode;
        private readonly string _responseContent;

        public FakeHttpMessageHandler(string responseContent = "{\"tag_name\":\"v2.3\",\"assets\":[]}", System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
        {
            _responseContent = responseContent;
            _statusCode = statusCode;
        }

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            var response = new System.Net.Http.HttpResponseMessage(_statusCode)
            {
                Content = new System.Net.Http.StringContent(_responseContent, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
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
    public void ExitWatcher_DisappearanceBeforeQueuedExitDelivery_DeliversExitCleanly()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string> { "d3d11.dll" }));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        string exePath = @"C:\Games\Game11\Game.exe";
        bool enumerateProc = true;

        var monitor = new ProcessMonitor(
            new GameDetector(),
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => enumerateProc ? new[] { currentProc } : Array.Empty<Process>(),
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => exePath;

        // Poll 1: Qualifies and attaches to exit watcher
        monitor.PollProcessesOnce();
        Assert.Single(exitHandler.AttachedProcesses);
        Assert.NotNull(monitor.GetCandidateState(currentProc.Id));

        // Poll 2: Process disappears from enumeration before Exited event arrives
        enumerateProc = false;
        monitor.PollProcessesOnce();

        // Candidates are cleaned up on disappearance
        Assert.Null(monitor.GetCandidateState(currentProc.Id));

        // But exit watcher still delivers exit cleanly when the queued exit arrives
        string? exitedExe = null;
        monitor.OnGameExited += path => exitedExe = path;

        exitHandler.SimulateInstanceExit(currentProc.Id, exePath);
        Assert.Equal(exePath, exitedExe);
    }

    [Fact]
    public void ExitWatcher_TwoSameExecutableInstances_ExitingInOppositeOrder_RemovesCorrectRegistrations()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string> { "d3d11.dll" }));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var proc1 = Process.GetCurrentProcess();
        using var proc2 = Process.GetProcesses().FirstOrDefault(p => p.Id > 0 && p.Id != proc1.Id);
        if (proc2 == null) return; // Safeguard if environment has single process

        string exePath = @"C:\Games\MultiInstance\Game.exe";

        var monitor = new ProcessMonitor(
            new GameDetector(),
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { proc1, proc2 },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = p => p.Id == proc1.Id
            ? new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc)
            : new DateTime(2026, 10, 15, 12, 1, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => exePath;

        // Poll: both qualify and attach
        monitor.PollProcessesOnce();
        Assert.Equal(2, exitHandler.AttachedProcesses.Count);

        var exits = new List<string>();
        monitor.OnGameExited += path => exits.Add(path);

        // Instance 2 exits first (opposite order)
        exitHandler.SimulateInstanceExit(proc2.Id, exePath);
        Assert.Single(exits);

        // Verify proc1 is still qualified in monitor, while proc2 was removed
        Assert.True(monitor.TryGetQualifiedSnapshot(proc1.Id, out _));
        Assert.False(monitor.TryGetQualifiedSnapshot(proc2.Id, out _));

        // Instance 1 exits
        exitHandler.SimulateInstanceExit(proc1.Id, exePath);
        Assert.Equal(2, exits.Count);
        Assert.False(monitor.TryGetQualifiedSnapshot(proc1.Id, out _));
    }

    [Fact]
    public void ExitWatcher_RepeatedAssessments_AttachesOncePerInstance()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string> { "d3d11.dll" }));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        string exePath = @"C:\Games\Game11\Game.exe";

        var monitor = new ProcessMonitor(
            new GameDetector(),
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => exePath;

        // Poll 1: Qualifies and attaches
        monitor.PollProcessesOnce();
        Assert.Single(exitHandler.AttachedProcesses);

        // Poll 2: Process still running and qualified
        monitor.PollProcessesOnce();
        Assert.Single(exitHandler.AttachedProcesses);

        // Poll 3: Still only one attachment
        monitor.PollProcessesOnce();
        Assert.Single(exitHandler.AttachedProcesses);
    }

    [Fact]
    public void ExitWatcher_ExitAfterDisposal_ProducesZeroMonitorEventsOrManagementCallbacks()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string> { "d3d11.dll" }));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        string exePath = @"C:\Games\Game11\Game.exe";

        var monitor = new ProcessMonitor(
            new GameDetector(),
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => exePath;

        monitor.PollProcessesOnce();
        Assert.Single(exitHandler.AttachedProcesses);

        int exitEvents = 0;
        monitor.OnGameExited += _ => exitEvents++;

        // Dispose the monitor
        monitor.Dispose();

        // Simulate exit delivery after disposal
        exitHandler.SimulateInstanceExit(currentProc.Id, exePath);

        // Zero events emitted
        Assert.Equal(0, exitEvents);
    }

    [Fact]
    public void Reentrancy_ScannerDrivenReentry_SuppressesNestedPolling()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));
        var exitHandler = new FakeProcessExitHandler();

        ProcessMonitor? monitor = null;
        int scannerInvocations = 0;

        var scanner = new DynamicModuleScanner(_ =>
        {
            scannerInvocations++;
            // Reentrancy attempt: synchronously call PollProcessesOnce from within scanner
            monitor?.PollProcessesOnce();
            return ModuleScanResult.Complete(new HashSet<string> { "d3d11.dll" });
        });
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        string exePath = @"C:\Games\Game11\Game.exe";

        monitor = new ProcessMonitor(
            new GameDetector(),
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => exePath;

        monitor.PollProcessesOnce();

        // Exactly one scanner invocation; nested reentrant poll was suppressed by _isPolling guard
        Assert.Equal(1, scannerInvocations);
    }

    [Fact]
    public void Disposal_DuringAssessment_AbortsQualificationAndZeroStatePersisted()
    {
        using var tempDir = new SyntheticTestDirectory();
        string libraryPath = Path.Combine(tempDir.RootPath, "game-library.json");
        var libraryStore = new GameLibraryStore(
            libraryPath,
            Path.Combine(tempDir.RootPath, "backups"));
        var exitHandler = new FakeProcessExitHandler();

        ProcessMonitor? monitor = null;
        var scanner = new DynamicModuleScanner(_ =>
        {
            // Dispose monitor in-flight during assessment
            monitor?.Dispose();
            return ModuleScanResult.Complete(new HashSet<string> { "d3d11.dll" });
        });
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        string exePath = @"C:\Games\Game11\Game.exe";

        monitor = new ProcessMonitor(
            new GameDetector(),
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => exePath;

        int qualifiedEvents = 0;
        int snapshotEvents = 0;
        monitor.OnQualifiedGameDetected += (_, _) => qualifiedEvents++;
        monitor.OnSnapshotDetected += _ => snapshotEvents++;

        monitor.PollProcessesOnce();

        // Qualification aborted: zero events and zero library records persisted
        Assert.Equal(0, qualifiedEvents);
        Assert.Equal(0, snapshotEvents);
        Assert.Empty(libraryStore.GetAll());
        Assert.Empty(exitHandler.AttachedProcesses);
    }

    [Fact]
    public void Disposal_DuringFirstEvent_SuppressesSubsequentEvents()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string> { "d3d11.dll" }));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        string exePath = @"C:\Games\Game11\Game.exe";

        ProcessMonitor? monitor = null;
        monitor = new ProcessMonitor(
            new GameDetector(),
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => exePath;

        int snapshotEvents = 0;
        int qualifiedEvents = 0;
        int gameDetectedEvents = 0;

        monitor.OnSnapshotDetected += _ =>
        {
            snapshotEvents++;
            // Dispose in first event callback
            monitor.Dispose();
        };
        monitor.OnQualifiedGameDetected += (_, _) => qualifiedEvents++;
        monitor.OnGameDetected += _ => gameDetectedEvents++;

        monitor.PollProcessesOnce();

        Assert.Equal(1, snapshotEvents);
        // Subsequent events must be suppressed by _disposed boundary check
        Assert.Equal(0, qualifiedEvents);
        Assert.Equal(0, gameDetectedEvents);
    }

    [Fact]
    public async Task Polling_ConcurrentCalls_ExcludeOverlappingPasses()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));
        var exitHandler = new FakeProcessExitHandler();

        var enteredFirst = new System.Threading.ManualResetEventSlim(false);
        var releaseFirst = new System.Threading.ManualResetEventSlim(false);
        int totalScans = 0;

        var scanner = new DynamicModuleScanner(_ =>
        {
            System.Threading.Interlocked.Increment(ref totalScans);
            enteredFirst.Set();
            releaseFirst.Wait(5000);
            return ModuleScanResult.Complete(new HashSet<string> { "d3d11.dll" });
        });
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        string exePath = @"C:\Games\Game11\Game.exe";

        var monitor = new ProcessMonitor(
            new GameDetector(),
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => exePath;

        var task1 = Task.Run(() => monitor.PollProcessesOnce());
        enteredFirst.Wait(5000);

        // Attempt concurrent poll while task1 holds lock in assessment
        monitor.PollProcessesOnce();

        releaseFirst.Set();
        await task1;

        // Exactly one scan occurred; second concurrent poll was excluded by Monitor.TryEnter
        Assert.Equal(1, totalScans);
    }

    [Fact]
    public void CandidateLifecycle_UnknownToKnownIdentity_AdoptsKnownStartTimeWithoutResettingBudget()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>()));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        string exePath = @"C:\Games\Game\Game.exe";
        DateTime currentStartTime = DateTime.MinValue;
        TimeSpan currentClock = TimeSpan.Zero;

        var monitor = new ProcessMonitor(
            new GameDetector(),
            exitHandler,
            classifier,
            libraryStore,
            clock: () => currentClock,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => currentStartTime;
        monitor.ExePathResolver = _ => exePath;

        // Poll 1 at t=0s: start time is unknown (MinValue)
        monitor.PollProcessesOnce();
        var candidate1 = monitor.GetCandidateState(currentProc.Id);
        Assert.NotNull(candidate1);
        Assert.Equal(DateTime.MinValue, candidate1!.ProcessStartTime);
        Assert.Equal(1, candidate1.AssessmentsConductedCount);

        // At t=2s: start time becomes known
        currentClock = TimeSpan.FromSeconds(2);
        var knownStart = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        currentStartTime = knownStart;

        // Poll 2 at t=2s
        monitor.PollProcessesOnce();
        var candidate2 = monitor.GetCandidateState(currentProc.Id);
        Assert.NotNull(candidate2);
        Assert.Equal(knownStart, candidate2!.ProcessStartTime);
        // Budget was NOT reset: conducted count advanced to 2
        Assert.Equal(2, candidate2.AssessmentsConductedCount);
    }

    [Fact]
    public void CandidateLifecycle_QualifiedCandidate_PIDReused_ResetsCandidate()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));
        var exitHandler = new FakeProcessExitHandler();
        var loadedModules = new HashSet<string> { "d3d11.dll" };
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>(loadedModules)));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        string exePath = @"C:\Games\Game11\Game.exe";
        var firstStart = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        DateTime currentStart = firstStart;

        var monitor = new ProcessMonitor(
            new GameDetector(),
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => currentStart;
        monitor.ExePathResolver = _ => exePath;

        // Poll 1: Qualifies
        monitor.PollProcessesOnce();
        var candidate1 = monitor.GetCandidateState(currentProc.Id);
        Assert.NotNull(candidate1);
        Assert.True(candidate1!.IsQualified);

        // Next: PID is reused by a new process instance with differing start time and no graphics modules
        currentStart = new DateTime(2026, 10, 15, 12, 5, 0, DateTimeKind.Utc);
        loadedModules.Clear();

        // Poll 2: State must reset, not inherit prior qualification
        monitor.PollProcessesOnce();
        var candidate2 = monitor.GetCandidateState(currentProc.Id);
        Assert.NotNull(candidate2);
        Assert.False(candidate2!.IsQualified);
        Assert.Equal(currentStart, candidate2.ProcessStartTime);
        Assert.Equal(1, candidate2.AssessmentsConductedCount);
    }

    [Fact]
    public void CandidateLifecycle_ExpiredCandidate_PIDReused_ResetsCandidate()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));
        var exitHandler = new FakeProcessExitHandler();
        var loadedModules = new HashSet<string>();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>(loadedModules)));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        string exePath = @"C:\Games\UnknownApp\App.exe";
        var firstStart = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        DateTime currentStart = firstStart;
        TimeSpan currentClock = TimeSpan.Zero;

        var monitor = new ProcessMonitor(
            new GameDetector(),
            exitHandler,
            classifier,
            libraryStore,
            clock: () => currentClock,
            processEnumerator: () => new[] { currentProc },
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => true;
        monitor.StartTimeResolver = _ => currentStart;
        monitor.ExePathResolver = _ => exePath;

        // Conduct all 12 assessments until expired
        for (int i = 0; i < ProcessQualification.ScheduledOffsetsSeconds.Length; i++)
        {
            currentClock = TimeSpan.FromSeconds(ProcessQualification.ScheduledOffsetsSeconds[i]);
            monitor.PollProcessesOnce();
        }

        var candidate1 = monitor.GetCandidateState(currentProc.Id);
        Assert.NotNull(candidate1);
        Assert.True(candidate1!.IsExpired);

        // Next: PID is reused by a new process instance with differing start time
        currentStart = new DateTime(2026, 10, 15, 12, 10, 0, DateTimeKind.Utc);

        // Poll again: State must reset, not inherit prior expiration
        monitor.PollProcessesOnce();
        var candidate2 = monitor.GetCandidateState(currentProc.Id);
        Assert.NotNull(candidate2);
        Assert.False(candidate2!.IsExpired);
        Assert.Equal(currentStart, candidate2.ProcessStartTime);
        Assert.Equal(1, candidate2.AssessmentsConductedCount);
    }

    [Fact]
    public void CandidateLifecycle_ExitedWindowRetry_CleanedUp()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryStore = new GameLibraryStore(
            Path.Combine(tempDir.RootPath, "game-library.json"),
            Path.Combine(tempDir.RootPath, "backups"));
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>()));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        using var currentProc = Process.GetCurrentProcess();
        string exePath = @"C:\Games\Game\Game.exe";
        bool enumerateProc = true;

        var monitor = new ProcessMonitor(
            new GameDetector(),
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => enumerateProc ? new[] { currentProc } : Array.Empty<Process>(),
            startTimer: false);
        monitor.GameProcessFilter = _ => true;
        monitor.WindowFilter = _ => false; // No window yet -> enters _windowRetries
        monitor.StartTimeResolver = _ => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        monitor.ExePathResolver = _ => exePath;

        // Poll 1: Tracked in window retries
        monitor.PollProcessesOnce();

        // Process exits/disappears before gaining a window
        enumerateProc = false;
        monitor.PollProcessesOnce();

        // Process reappears with a window: window retries was cleaned up so it starts fresh at tick 0
        enumerateProc = true;
        monitor.WindowFilter = _ => true;
        monitor.PollProcessesOnce();

        var candidate = monitor.GetCandidateState(currentProc.Id);
        Assert.NotNull(candidate);
        Assert.Equal(1, candidate!.AssessmentsConductedCount);
    }

    [Fact]
    public async Task HeadlessGameAdmission_Orchestration_DX11_Vs_Unknown_Vs_DX12_Vs_Vulkan()
    {
        using var tempDir = new SyntheticTestDirectory();
        var gamesPath = Path.Combine(tempDir.RootPath, "games.json");
        var libraryPath = Path.Combine(tempDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(tempDir.RootPath, "backups");
        var profiles = new ProfileStore(gamesPath);
        var libraryStore = new GameLibraryStore(libraryPath, backupsPath);
        var settings = new SettingsStore { GlobalPolicy = GlobalManagementPolicy.Automated };
        var inspector = new ManagedFileInspector(libraryStore);
        var fileUtils = new FileUtils();
        var engine = new MultiFileTransactionEngine(backupsPath);

        // Use FakeHttpMessageHandler to guarantee zero real network access
        var fakeHttpHandler = new FakeHttpMessageHandler("{\"tag_name\":\"v2.3\",\"assets\":[]}");
        var fakeHttpClient = new System.Net.Http.HttpClient(fakeHttpHandler);

        var installer = new DxvkInstaller(fakeHttpClient, engine, libraryStore, null, fileUtils);
        var rollback = new DxvkRollback(engine, libraryStore, fileUtils);
        var github = new DxvkGithubClient(fakeHttpClient, new CacheStore(Path.Combine(tempDir.RootPath, "cache.json")));
        var dxvk = new DxvkManager(installer, rollback, github, profiles, libraryStore);

        using var dummyProcess = Process.GetCurrentProcess();

        int autoEnableCalls = 0;
        int autoReapplyCalls = 0;
        Func<GameProfile, Process, Task> autoEnableDispatcher = (p, proc) =>
        {
            autoEnableCalls++;
            return Task.CompletedTask;
        };
        Func<GameProfile, Process, Task> autoReapplyDispatcher = (p, proc) =>
        {
            autoReapplyCalls++;
            return Task.CompletedTask;
        };

        // Case 1: Unknown snapshot -> returns false, 0 profiles created, 0 dispatch calls
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
            unknownSnapshot, dummyProcess, profiles, libraryStore, dxvk, inspector, settings,
            autoEnableDispatcher: autoEnableDispatcher,
            autoReapplyDispatcher: autoReapplyDispatcher);
        Assert.False(admittedUnknown);
        Assert.Empty(profiles.GetAll());
        Assert.Equal(0, autoEnableCalls);

        // Case 2: DX11 snapshot -> returns true, profile and library record created, exactly 1 autoEnable dispatch
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
            dx11Snapshot, dummyProcess, profiles, libraryStore, dxvk, inspector, settings,
            autoEnableDispatcher: autoEnableDispatcher,
            autoReapplyDispatcher: autoReapplyDispatcher);
        Assert.True(admittedDx11);
        Assert.Single(profiles.GetAll());
        Assert.Equal(GraphicsApi.DX11, profiles.GetAll().First().Api);
        Assert.Equal(1, autoEnableCalls);

        // Assert file storage bytes exist for games.json and game-library.json
        Assert.True(File.Exists(gamesPath));
        Assert.True(new FileInfo(gamesPath).Length > 0);
        string gamesContent = File.ReadAllText(gamesPath);
        Assert.Contains("Game11", gamesContent);

        Assert.True(File.Exists(libraryPath));
        Assert.True(new FileInfo(libraryPath).Length > 0);
        string libraryContent = File.ReadAllText(libraryPath);
        Assert.Contains("Game11", libraryContent);

        // Case 3: DX12 snapshot -> returns true (recorded), but deployment refused, 0 additional dispatch
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
            dx12Snapshot, dummyProcess, profiles, libraryStore, dxvk, inspector, settings,
            autoEnableDispatcher: autoEnableDispatcher,
            autoReapplyDispatcher: autoReapplyDispatcher);
        Assert.True(admittedDx12);
        Assert.Equal(1, autoEnableCalls); // Still 1 from DX11, not incremented
        var inst12 = libraryStore.FindByInstallationPath(@"C:\Games\Game12");
        Assert.NotNull(inst12);
        bool deploySupported12 = DxvkCompatibility.IsInstallationSupported(inst12, GraphicsApi.DX12, "Game.exe", out var refusalReason12);
        Assert.False(deploySupported12);
        Assert.NotNull(refusalReason12);

        // Case 4: Vulkan snapshot -> returns true (recorded), but deployment refused, 0 additional dispatch
        var vulkanSnapshot = new DetectionSnapshot
        {
            ProcessId = 400,
            ProcessName = "GameVk",
            ExecutablePath = @"C:\Games\GameVk\Game.exe",
            InstallationRoot = @"C:\Games\GameVk",
            ExecutableRelativePath = "Game.exe",
            Classification = new ApiClassificationResult
            {
                PrimaryApi = GraphicsApi.Vulkan,
                Confidence = ApiDetectionConfidence.High,
                Architecture = "x64"
            },
            AntiCheat = AntiCheatAssessment.None()
        };

        bool admittedVulkan = await TrayApp.ProcessGameAdmissionAsync(
            vulkanSnapshot, dummyProcess, profiles, libraryStore, dxvk, inspector, settings,
            autoEnableDispatcher: autoEnableDispatcher,
            autoReapplyDispatcher: autoReapplyDispatcher);
        Assert.True(admittedVulkan);
        Assert.Equal(1, autoEnableCalls); // Still 1 from DX11, not incremented
        var instVk = libraryStore.FindByInstallationPath(@"C:\Games\GameVk");
        Assert.NotNull(instVk);
        bool deploySupportedVk = DxvkCompatibility.IsInstallationSupported(instVk, GraphicsApi.Vulkan, "Game.exe", out var refusalReasonVk);
        Assert.False(deploySupportedVk);
        Assert.NotNull(refusalReasonVk);
    }
}
