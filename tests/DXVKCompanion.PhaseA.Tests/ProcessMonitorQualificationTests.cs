using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Monitoring;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class ProcessMonitorQualificationTests
{
    private sealed class FakeGameDetector : GameDetector
    {
        public bool IsGame { get; set; } = true;
        public bool HasWin { get; set; } = true;
        public AntiCheatAssessment AntiCheat { get; set; } = new AntiCheatAssessment(AntiCheatRisk.None, Array.Empty<string>());

        public override bool IsGameProcess(Process process) => IsGame;
        public override bool HasWindow(Process process) => HasWin;
        public override AntiCheatAssessment AssessAntiCheatRisk(Process process, string installationRoot) => AntiCheat;
    }

    private sealed class FakeProcessExitHandler : ProcessExitHandler
    {
        public List<int> AttachedPids { get; } = new();

        public override void Attach(Process process)
        {
            AttachedPids.Add(process.Id);
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

        public override ModuleScanResult ScanLoadedGraphicsModules(Process process)
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
        string libraryPath = Path.Combine(tempDir.RootPath, "game-library.json");
        string backupsPath = Path.Combine(tempDir.RootPath, "backups");
        var libraryStore = new GameLibraryStore(libraryPath, backupsPath);
        var profileStore = new ProfileStore(tempDir.RootPath);

        var detector = new FakeGameDetector { IsGame = true, HasWin = true };
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>()));
        var parser = new FakePeParser();
        var classifier = new ApiClassifier(scanner, parser);

        TimeSpan currentElapsed = TimeSpan.Zero;
        using var currentProc = Process.GetCurrentProcess();

        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore,
            clock: () => currentElapsed,
            processEnumerator: () => new[] { Process.GetProcessById(currentProc.Id) },
            startTimer: false);
        monitor.ProfileStore = profileStore;

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
        Assert.Empty(libraryStore.GetInstallations());
        Assert.Empty(profileStore.All());

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

        var detector = new FakeGameDetector { IsGame = true, HasWin = true };
        var exitHandler = new FakeProcessExitHandler();

        var loadedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>(loadedModules)));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        TimeSpan currentElapsed = TimeSpan.Zero;
        using var currentProc = Process.GetCurrentProcess();

        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore,
            clock: () => currentElapsed,
            processEnumerator: () => new[] { Process.GetProcessById(currentProc.Id) },
            startTimer: false);

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
        Assert.Empty(libraryStore.GetInstallations());

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
        Assert.NotEmpty(libraryStore.GetInstallations());
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

        var detector = new FakeGameDetector { IsGame = true, HasWin = true };
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
            processEnumerator: () => new[] { Process.GetProcessById(currentProc.Id) },
            startTimer: false);

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

        var detector = new FakeGameDetector { IsGame = true, HasWin = true };
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
            processEnumerator: () => new[] { Process.GetProcessById(currentProc.Id) },
            startTimer: false);

        monitor.PollProcessesOnce();

        Assert.True(monitor.TryGetQualifiedSnapshot(currentProc.Id, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(GraphicsApi.DX12, snapshot!.Classification.PrimaryApi);

        // Verification of deployment refusal: DX12 is recognized for observation but rejected for DXVK deployment
        var installations = libraryStore.GetInstallations();
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

        var detector = new FakeGameDetector { IsGame = true, HasWin = true };
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Incomplete(new HashSet<string>(), "ScanDenied"));
        var parser = new FakePeParser(new[] { "d3d9.dll" }, "x32");
        var classifier = new ApiClassifier(scanner, parser);

        using var currentProc = Process.GetCurrentProcess();
        var classification = classifier.ClassifyDetailed(currentProc, fakeExe);

        Assert.Equal(GraphicsApi.DX9, classification.PrimaryApi);
        Assert.Equal(ApiDetectionConfidence.Medium, classification.Confidence);
        Assert.Equal("StaticPEImports", classification.EvidenceSource);
    }

    [Fact]
    public void BackoffSchedule_ExecutesExactly12Assessments_NoWorkBeforeDueTimes_AndExpiresCleanly()
    {
        var detector = new FakeGameDetector { IsGame = true, HasWin = true };
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
            processEnumerator: () => new[] { Process.GetProcessById(currentProc.Id) },
            startTimer: false);

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
        var detector = new FakeGameDetector { IsGame = true, HasWin = true };
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
            processEnumerator: () => new[] { Process.GetProcessById(currentProc.Id) },
            startTimer: false);

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
        var detector = new FakeGameDetector { IsGame = true, HasWin = true };
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
            processEnumerator: () => processRunning ? new[] { Process.GetProcessById(currentProc.Id) } : Array.Empty<Process>(),
            startTimer: false);

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
    public void PreservedExistingLibraryEntries_AndPendingActionExitTracking()
    {
        using var tempDir = new SyntheticTestDirectory();
        var libraryPath = Path.Combine(tempDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(tempDir.RootPath, "backups");
        var libraryStore = new GameLibraryStore(libraryPath, backupsPath);

        using var currentProc = Process.GetCurrentProcess();
        string currentExe = currentProc.MainModule?.FileName ?? "C:\\Games\\Test\\Game.exe";
        string installDir = Path.GetDirectoryName(currentExe) ?? "C:\\Games\\Test";
        string relExe = Path.GetRelativePath(installDir, currentExe);

        // Seed an existing library entry with DX9 and a PendingAction
        var initialSnapshot = new DetectionSnapshot
        {
            ProcessId = 9999,
            ProcessName = "TestGame",
            ExecutablePath = currentExe,
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
        libraryStore.Save();

        string jsonBefore = File.ReadAllText(libraryPath);

        // Now run ProcessMonitor where process produces NO graphics modules (Unknown / Incomplete)
        var detector = new FakeGameDetector { IsGame = true, HasWin = true };
        var exitHandler = new FakeProcessExitHandler();
        var scanner = new DynamicModuleScanner(_ => ModuleScanResult.Complete(new HashSet<string>()));
        var classifier = new ApiClassifier(scanner, new FakePeParser());

        var monitor = new ProcessMonitor(
            detector,
            exitHandler,
            classifier,
            libraryStore,
            clock: () => TimeSpan.Zero,
            processEnumerator: () => new[] { Process.GetProcessById(currentProc.Id) },
            startTimer: false);

        // Act
        monitor.PollProcessesOnce();

        // Assert: Exit handler IS attached so game exit will process the pending action
        Assert.Contains(currentProc.Id, exitHandler.AttachedPids);

        // Candidate did NOT qualify
        var candidate = monitor.GetCandidateState(currentProc.Id);
        Assert.NotNull(candidate);
        Assert.False(candidate!.IsQualified);

        // Library store was NOT mutated or overwritten with Unknown
        string jsonAfter = File.ReadAllText(libraryPath);
        Assert.Equal(jsonBefore, jsonAfter);

        var refreshedInst = libraryStore.FindByInstallationPath(installDir);
        Assert.NotNull(refreshedInst!.PendingAction);
        Assert.Equal("2.5", refreshedInst.PendingAction!.TargetDxvkVersion);
        var exec = refreshedInst.FindExecutable(relExe);
        Assert.Equal(GraphicsApi.DX9, exec!.LastKnownApi);
    }
}
