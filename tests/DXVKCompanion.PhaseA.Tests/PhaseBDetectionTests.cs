using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using DXVKCompanion.Models;
using DXVKCompanion.Monitoring;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class PhaseBDetectionTests
{
    private sealed class FakeModuleScanner : ModuleScanner
    {
        private readonly HashSet<string> _modules;

        public FakeModuleScanner(params string[] modules)
        {
            _modules = new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase);
        }

        public override HashSet<string> GetLoadedGraphicsModules(Process process) => _modules;
    }

    private sealed class FakePeParser : PeParser
    {
        private readonly IEnumerable<string> _imports;
        private readonly string _arch;

        public FakePeParser(IEnumerable<string> imports, string arch = "x64")
        {
            _imports = imports;
            _arch = arch;
        }

        public override IEnumerable<string> GetImports(string path) => _imports;
        public override string GetArchitecture(string path) => _arch;
    }

    [Fact]
    public void ApiClassifier_RuntimeModules_ReturnsHighConfidenceAndObservedApis()
    {
        var scanner = new FakeModuleScanner("d3d11.dll", "dxgi.dll");
        var parser = new FakePeParser(Array.Empty<string>(), "x64");
        var classifier = new ApiClassifier(scanner, parser);

        using var proc = Process.GetCurrentProcess();
        var result = classifier.ClassifyDetailed(proc, proc.MainModule?.FileName);

        Assert.Equal(GraphicsApi.DX11, result.PrimaryApi);
        Assert.Equal(ApiDetectionConfidence.High, result.Confidence);
        Assert.Equal("RuntimeModules", result.EvidenceSource);
        Assert.Contains(result.Evidence, e => e.Contains("d3d11.dll"));
    }

    [Fact]
    public void ApiClassifier_StaticImportsFallback_ReturnsMediumConfidence()
    {
        var scanner = new FakeModuleScanner(); // No loaded runtime modules
        var parser = new FakePeParser(new[] { "d3d9.dll", "kernel32.dll" }, "x32");
        var classifier = new ApiClassifier(scanner, parser);

        using var tempDir = new SyntheticTestDirectory();
        var fakeExe = tempDir.CreateFile("Game.exe", "synthetic-binary");

        using var proc = Process.GetCurrentProcess();
        var result = classifier.ClassifyDetailed(proc, fakeExe);

        Assert.Equal(GraphicsApi.DX9, result.PrimaryApi);
        Assert.Equal(ApiDetectionConfidence.Medium, result.Confidence);
        Assert.Equal("StaticPEImports", result.EvidenceSource);
        Assert.Equal("x32", result.Architecture);
        Assert.Contains(result.Evidence, e => e.Contains("d3d9.dll"));
    }

    [Fact]
    public void GameDetector_AssessAntiCheatRisk_DirectorySignature_ReturnsSuspectedWithEvidence()
    {
        using var gameDir = new SyntheticTestDirectory();
        gameDir.CreateFile("Game.exe", "binary");
        gameDir.CreateFile("EasyAntiCheat_Setup.exe", "eac-setup");

        var detector = new GameDetector();
        using var proc = Process.GetCurrentProcess();

        var assessment = detector.AssessAntiCheatRisk(proc, gameDir.RootPath);

        Assert.Equal(AntiCheatRisk.SuspectedOrKnown, assessment.Risk);
        Assert.NotEmpty(assessment.Evidence);
        Assert.Contains(assessment.Evidence, e => e.Contains("EasyAntiCheat_Setup.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GameLibraryStore_MultipleExecutables_TrackedUnderSameInstallation()
    {
        using var storageDir = new SyntheticTestDirectory();
        using var gameDir = new SyntheticTestDirectory();

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));

        var snapshot1 = new DetectionSnapshot
        {
            ProcessId = 1001,
            ProcessName = "Game_DX11",
            InstallationRoot = gameDir.RootPath,
            ExecutableRelativePath = "bin/Game_DX11.exe",
            Classification = new ApiClassificationResult
            {
                PrimaryApi = GraphicsApi.DX11,
                Confidence = ApiDetectionConfidence.High,
                Architecture = "x64",
                Evidence = new[] { "Loaded runtime module: d3d11.dll" }
            },
            TimestampUtc = DateTime.UtcNow
        };

        var snapshot2 = new DetectionSnapshot
        {
            ProcessId = 1002,
            ProcessName = "Game_DX9",
            InstallationRoot = gameDir.RootPath,
            ExecutableRelativePath = "bin/Game_DX9.exe",
            Classification = new ApiClassificationResult
            {
                PrimaryApi = GraphicsApi.DX9,
                Confidence = ApiDetectionConfidence.High,
                Architecture = "x32",
                Evidence = new[] { "Loaded runtime module: d3d9.dll" }
            },
            TimestampUtc = DateTime.UtcNow
        };

        store.RecordDetectionSnapshot(snapshot1);
        store.RecordDetectionSnapshot(snapshot2);

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        Assert.Equal(2, installation!.Executables.Count);

        var exec1 = installation.FindExecutable("bin/Game_DX11.exe");
        var exec2 = installation.FindExecutable("bin/Game_DX9.exe");

        Assert.NotNull(exec1);
        Assert.NotNull(exec2);
        Assert.Equal(GraphicsApi.DX11, exec1!.LastKnownApi);
        Assert.Equal("x64", exec1.LastKnownArchitecture);
        Assert.Equal(GraphicsApi.DX9, exec2!.LastKnownApi);
        Assert.Equal("x32", exec2.LastKnownArchitecture);
    }

    [Fact]
    public void GameLibraryStore_ApiTransition_DetectsAndFlagsChange()
    {
        using var storageDir = new SyntheticTestDirectory();
        using var gameDir = new SyntheticTestDirectory();

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));

        var snapshot1 = new DetectionSnapshot
        {
            ProcessId = 2001,
            ProcessName = "Game",
            InstallationRoot = gameDir.RootPath,
            ExecutableRelativePath = "Game.exe",
            Classification = new ApiClassificationResult
            {
                PrimaryApi = GraphicsApi.DX9,
                Confidence = ApiDetectionConfidence.High,
                Architecture = "x64"
            }
        };

        store.RecordDetectionSnapshot(snapshot1);
        Assert.False(snapshot1.HasApiChanged);

        var snapshot2 = new DetectionSnapshot
        {
            ProcessId = 2002,
            ProcessName = "Game",
            InstallationRoot = gameDir.RootPath,
            ExecutableRelativePath = "Game.exe",
            Classification = new ApiClassificationResult
            {
                PrimaryApi = GraphicsApi.DX11,
                Confidence = ApiDetectionConfidence.High,
                Architecture = "x64"
            }
        };

        store.RecordDetectionSnapshot(snapshot2);
        Assert.True(snapshot2.HasApiChanged);
        Assert.Equal(GraphicsApi.DX9, snapshot2.PreviousApi);

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        var exec = installation!.FindExecutable("Game.exe");
        Assert.Equal(GraphicsApi.DX11, exec!.LastKnownApi);
    }
}
