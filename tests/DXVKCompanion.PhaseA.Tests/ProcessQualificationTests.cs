using System;
using System.Collections.Generic;
using System.Diagnostics;
using DXVKCompanion.Models;
using DXVKCompanion.Monitoring;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class ProcessQualificationTests
{
    private sealed class OverriddenFakeModuleScanner : ModuleScanner
    {
        private readonly HashSet<string> _modules;

        public OverriddenFakeModuleScanner(params string[] modules)
        {
            _modules = new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase);
        }

        public override HashSet<string> GetLoadedGraphicsModules(Process process) => _modules;
    }

    private sealed class DelegatingModuleScanner : ModuleScanner
    {
        public override HashSet<string> GetLoadedGraphicsModules(Process process)
        {
            var baseMods = base.GetLoadedGraphicsModules(process);
            baseMods.Add("d3d11.dll");
            return baseMods;
        }
    }

    private sealed class DirectResultModuleScanner : ModuleScanner
    {
        private readonly ModuleScanResult _result;

        public DirectResultModuleScanner(ModuleScanResult result)
        {
            _result = result;
        }

        internal override ModuleScanResult ScanLoadedGraphicsModules(Process process) => _result;
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
    public void ModuleScanner_WhenOverridden_DelegatesWithoutRecursion()
    {
        var scanner = new DelegatingModuleScanner
        {
            RawModuleEnumerator = _ => new[] { "kernel32.dll", "user32.dll" }
        };
        using var proc = Process.GetCurrentProcess();

        var result = scanner.ScanLoadedGraphicsModules(proc);

        Assert.True(result.IsComplete);
        Assert.False(result.HasFailed);
        Assert.Contains("d3d11.dll", result.Modules);
    }

    [Fact]
    public void ModuleScanner_WhenOverriddenDirectly_ReturnsCompleteOverriddenModules()
    {
        var scanner = new OverriddenFakeModuleScanner("d3d11.dll", "dxgi.dll");
        using var proc = Process.GetCurrentProcess();

        var result = scanner.ScanLoadedGraphicsModules(proc);

        Assert.True(result.IsComplete);
        Assert.False(result.HasFailed);
        Assert.Contains("d3d11.dll", result.Modules);
        Assert.Contains("dxgi.dll", result.Modules);
    }

    [Fact]
    public void ApiClassifier_RuntimeComplete_NoGraphicsModules_AndNoStaticImports_ReturnsUnknownWithEvidenceSourceNone()
    {
        var scanResult = ModuleScanResult.Complete(new HashSet<string>());
        var scanner = new DirectResultModuleScanner(scanResult);
        var parser = new FakePeParser();
        var classifier = new ApiClassifier(scanner, parser);

        using var proc = Process.GetCurrentProcess();
        var result = classifier.ClassifyDetailed(proc);

        Assert.Equal(GraphicsApi.Unknown, result.PrimaryApi);
        Assert.Equal(ApiDetectionConfidence.Unknown, result.Confidence);
        Assert.Equal("None", result.EvidenceSource);
    }

    [Fact]
    public void ApiClassifier_RuntimeIncomplete_ScanFailed_AndNoStaticImports_ReturnsUnknownWithEvidenceSourceRuntimeIncomplete()
    {
        var scanResult = ModuleScanResult.Incomplete(new HashSet<string>(), "Win32Exception");
        var scanner = new DirectResultModuleScanner(scanResult);
        var parser = new FakePeParser();
        var classifier = new ApiClassifier(scanner, parser);

        using var proc = Process.GetCurrentProcess();
        var result = classifier.ClassifyDetailed(proc);

        Assert.Equal(GraphicsApi.Unknown, result.PrimaryApi);
        Assert.Equal(ApiDetectionConfidence.Unknown, result.Confidence);
        Assert.Equal("RuntimeIncomplete", result.EvidenceSource);
        Assert.Contains(result.Evidence, e => e.Contains("Win32Exception"));
    }

    [Fact]
    public void ApiClassifier_RuntimeIncomplete_ScanFailed_WithStaticImports_ReturnsStaticPEImportsWithMediumConfidence_AndCarriesIncompleteNotice()
    {
        var scanResult = ModuleScanResult.Incomplete(new HashSet<string>(), "AccessDenied");
        var scanner = new DirectResultModuleScanner(scanResult);
        var parser = new FakePeParser(new[] { "d3d9.dll" });
        var classifier = new ApiClassifier(scanner, parser);

        using var tempDir = new SyntheticTestDirectory();
        var fakeExe = tempDir.CreateFile("Game.exe", "stub");

        using var proc = Process.GetCurrentProcess();
        var result = classifier.ClassifyDetailed(proc, fakeExe);

        Assert.Equal(GraphicsApi.DX9, result.PrimaryApi);
        Assert.Equal(ApiDetectionConfidence.Medium, result.Confidence);
        Assert.Equal("StaticPEImports", result.EvidenceSource);
        Assert.Contains(result.Evidence, e => e.Contains("AccessDenied"));
        Assert.Contains(result.Evidence, e => e.Contains("d3d9.dll"));
    }

    [Fact]
    public void ApiClassifier_PartialRecognizedModules_WithFailedScan_ReturnsRuntimeIncompleteAndMediumConfidence()
    {
        var partialModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "d3d11.dll" };
        var scanResult = ModuleScanResult.Incomplete(partialModules, "EnumerationAborted");
        var scanner = new DirectResultModuleScanner(scanResult);
        var parser = new FakePeParser();
        var classifier = new ApiClassifier(scanner, parser);

        using var proc = Process.GetCurrentProcess();
        var result = classifier.ClassifyDetailed(proc);

        Assert.Equal(GraphicsApi.DX11, result.PrimaryApi);
        Assert.Equal(ApiDetectionConfidence.Medium, result.Confidence);
        Assert.Equal("RuntimeIncomplete", result.EvidenceSource);
        Assert.Contains(result.Evidence, e => e.Contains("EnumerationAborted"));
        Assert.Contains(result.Evidence, e => e.Contains("d3d11.dll"));
    }

    [Theory]
    [InlineData(GraphicsApi.DX9, true)]
    [InlineData(GraphicsApi.DX10, true)]
    [InlineData(GraphicsApi.DX11, true)]
    [InlineData(GraphicsApi.DX12, true)]
    [InlineData(GraphicsApi.Vulkan, true)]
    [InlineData(GraphicsApi.Unknown, false)]
    public void ProcessQualification_IsRecognizedGraphicsApi_ReturnsExpectedResults(GraphicsApi api, bool expected)
    {
        Assert.Equal(expected, ProcessQualification.IsRecognizedGraphicsApi(api));
    }

    [Fact]
    public void CandidateProcessState_MonotonicSchedule_Has12DistinctOffsets()
    {
        int[] expectedOffsets = { 0, 2, 4, 6, 8, 10, 20, 30, 44, 60, 90, 120 };
        Assert.Equal(expectedOffsets, ProcessQualification.ScheduledOffsetsSeconds);
        Assert.Equal(12, ProcessQualification.MaxAssessments);
    }

    [Fact]
    public void CandidateProcessState_IsAssessmentDue_RespectsMonotonicOffsets()
    {
        var startTime = DateTime.UtcNow;
        var startElapsed = TimeSpan.FromSeconds(100);
        var candidate = new CandidateProcessState(1234, startTime, startElapsed);

        // At t=0s offset: due
        Assert.True(candidate.IsAssessmentDue(startElapsed));

        // Advance after t=0s assessment
        candidate.AdvanceAfterAssessment(startElapsed);
        Assert.Equal(1, candidate.NextAssessmentIndex);
        Assert.Equal(1, candidate.AssessmentsConductedCount);

        // At t=1s offset: not due (next is at 2s)
        Assert.False(candidate.IsAssessmentDue(startElapsed + TimeSpan.FromSeconds(1)));

        // At t=2s offset: due
        Assert.True(candidate.IsAssessmentDue(startElapsed + TimeSpan.FromSeconds(2)));

        // Advance after t=2s assessment
        candidate.AdvanceAfterAssessment(startElapsed + TimeSpan.FromSeconds(2));
        Assert.Equal(2, candidate.NextAssessmentIndex);
    }

    [Fact]
    public void CandidateProcessState_AdvanceAfterAssessment_PreventsCatchUpBursts()
    {
        var startTime = DateTime.UtcNow;
        var startElapsed = TimeSpan.FromSeconds(50);
        var candidate = new CandidateProcessState(1234, startTime, startElapsed);

        // Conduct 1st assessment at t=0s
        Assert.True(candidate.IsAssessmentDue(startElapsed));

        // Clock suddenly jumps by 25 seconds before advance (e.g. system suspended or timer latency)
        var jumpElapsed = startElapsed + TimeSpan.FromSeconds(25);
        candidate.AdvanceAfterAssessment(jumpElapsed);

        // Conducted count should only be 1
        Assert.Equal(1, candidate.AssessmentsConductedCount);

        // Offsets passed: 0 (conducted), 2, 4, 6, 8, 10, 20.
        // Next due offset should be 30s (index 7).
        Assert.Equal(7, candidate.NextAssessmentIndex);

        // At 25s elapsed, offset 30s is NOT due.
        Assert.False(candidate.IsAssessmentDue(jumpElapsed));

        // At 30s elapsed, offset 30s is due.
        Assert.True(candidate.IsAssessmentDue(startElapsed + TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void CandidateProcessState_ExpiresAfter12Assessments()
    {
        var startTime = DateTime.UtcNow;
        var startElapsed = TimeSpan.Zero;
        var candidate = new CandidateProcessState(1234, startTime, startElapsed);

        for (int i = 0; i < ProcessQualification.ScheduledOffsetsSeconds.Length; i++)
        {
            var offsetTime = TimeSpan.FromSeconds(ProcessQualification.ScheduledOffsetsSeconds[i]);
            Assert.True(candidate.IsAssessmentDue(offsetTime));
            candidate.AdvanceAfterAssessment(offsetTime);
        }

        Assert.True(candidate.IsExpired);
        Assert.Equal(12, candidate.AssessmentsConductedCount);
        Assert.False(candidate.IsAssessmentDue(TimeSpan.FromSeconds(150)));
    }
}
