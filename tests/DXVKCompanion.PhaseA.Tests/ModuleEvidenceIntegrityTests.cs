using System.Diagnostics;
using DXVKCompanion.Models;
using DXVKCompanion.Monitoring;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class ModuleEvidenceIntegrityTests
{
    private sealed class ControlledParser : PeParser
    {
        internal IEnumerable<string> Imports = Array.Empty<string>();
        internal int ImportCalls;
        public override string GetArchitecture(string path) => "Unknown";
        public override IEnumerable<string> GetImports(string path) { ImportCalls++; return Imports; }
    }

    [Theory]
    [InlineData("d3d8.dll")]
    [InlineData("d3d9.dll")]
    [InlineData("d3d10.dll")]
    [InlineData("d3d10core.dll")]
    [InlineData("d3d11.dll")]
    [InlineData("d3d12.dll")]
    [InlineData("dxgi.dll")]
    [InlineData("vulkan-1.dll")]
    [InlineData("opengl32.dll")]
    [InlineData("ddraw.dll")]
    [InlineData("dgvoodoo.dll")]
    public void ExactModuleNames_AreCaseInsensitiveDeduplicatedAndEnumeratedOnce(string module)
    {
        int enumerations = 0;
        var scanner = new ModuleScanner { RawModuleEnumerator = _ =>
        { enumerations++; return new[] { module, module.ToUpperInvariant(), module }; } };
        using var process = Process.GetCurrentProcess();
        var result = scanner.ScanLoadedGraphicsModules(process);
        Assert.Equal(1, enumerations);
        Assert.True(result.IsComplete);
        Assert.False(result.HasFailed);
        Assert.Equal(module, Assert.Single(result.Modules));
    }

    [Theory]
    [InlineData("helper-d3d11.dll")]
    [InlineData("d3d11.dll.disabled")]
    [InlineData("backup-vulkan-1.dll")]
    [InlineData("vulkan-1.dll.bak")]
    [InlineData("notd3d12.dll")]
    [InlineData("d3d12.dll.proxy")]
    [InlineData("prefix-d3d10core.dll")]
    [InlineData("d3d10core.dll.bak")]
    [InlineData(@"C:\GenericModules\d3d11.dll")]
    [InlineData("generic/d3d11.dll")]
    public void NonBasenameOrLookalike_DoesNotInventTrackedEvidence(string module)
    {
        var scanner = new ModuleScanner { RawModuleEnumerator = _ => new[] { module } };
        using var process = Process.GetCurrentProcess();
        var result = scanner.ScanLoadedGraphicsModules(process);
        Assert.True(result.IsComplete);
        Assert.Empty(result.Modules);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoreRuntime_PassesRealScannerAndReportsOnlyActualD3D10Evidence(bool includeD3D10)
    {
        using var root = new SyntheticTestDirectory();
        var path = root.CreateFile("Game.exe", "synthetic parser fixture");
        var parser = new ControlledParser { Imports = new[] { "d3d12.dll" } };
        var scanner = new ModuleScanner { RawModuleEnumerator = _ => includeD3D10
            ? new[] { "D3D10CORE.DLL", "d3d10.dll" } : new[] { "D3D10CORE.DLL" } };
        using var process = Process.GetCurrentProcess();
        var result = new ApiClassifier(scanner, parser).ClassifyDetailed(process, path);
        Assert.Equal(GraphicsApi.DX10, result.PrimaryApi);
        Assert.Equal(GraphicsApi.DX10, Assert.Single(result.ObservedApis));
        Assert.Equal(ApiDetectionConfidence.High, result.Confidence);
        Assert.Equal("RuntimeModules", result.EvidenceSource);
        Assert.Equal("Unknown", result.Architecture);
        Assert.Contains("Loaded runtime module: d3d10core.dll", result.Evidence);
        Assert.Equal(includeD3D10, result.Evidence.Contains("Loaded runtime module: d3d10.dll"));
        Assert.Equal(0, parser.ImportCalls);
    }

    [Theory]
    [InlineData("d3d12.dll", GraphicsApi.DX12)]
    [InlineData("vulkan-1.dll", GraphicsApi.Vulkan)]
    [InlineData("d3d11.dll", GraphicsApi.DX11)]
    public void CoreRuntime_DoesNotChangeExistingPrecedence(string higherModule, GraphicsApi expected)
    {
        using var root = new SyntheticTestDirectory();
        var scanner = new ModuleScanner { RawModuleEnumerator = _ =>
            new[] { "d3d10core.dll", higherModule } };
        using var process = Process.GetCurrentProcess();
        var result = new ApiClassifier(scanner, new ControlledParser())
            .ClassifyDetailed(process, root.CreateFile("Game.exe", "fixture"));
        Assert.Equal(expected, result.PrimaryApi);
        Assert.Equal(new[] { expected, GraphicsApi.DX10 }, result.ObservedApis);
        Assert.Contains("Loaded runtime module: d3d10core.dll", result.Evidence);
    }

    private static IEnumerable<string> InterruptedScan(bool yieldCore)
    {
        yield return "notd3d12.dll";
        if (yieldCore) yield return "d3d10core.dll";
        throw new IOException("Synthetic enumeration failure");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialScan_RetainsOnlyActualEvidenceAndExistingFallbackConfidence(bool yieldCore)
    {
        using var root = new SyntheticTestDirectory();
        var parser = new ControlledParser { Imports = new[] { "d3d9.dll" } };
        int enumerations = 0;
        var scanner = new ModuleScanner { RawModuleEnumerator = _ =>
        { enumerations++; return InterruptedScan(yieldCore); } };
        using var process = Process.GetCurrentProcess();
        var result = new ApiClassifier(scanner, parser)
            .ClassifyDetailed(process, root.CreateFile("Game.exe", "fixture"));
        Assert.Equal(1, enumerations);
        Assert.Equal(yieldCore ? GraphicsApi.DX10 : GraphicsApi.DX9, result.PrimaryApi);
        Assert.Equal(ApiDetectionConfidence.Medium, result.Confidence);
        Assert.Equal(yieldCore ? "RuntimeIncomplete" : "StaticPEImports", result.EvidenceSource);
        Assert.Contains("Runtime module scan incomplete: IOException", result.Evidence);
        Assert.DoesNotContain("Loaded runtime module: d3d12.dll", result.Evidence);
        Assert.Equal(yieldCore ? 0 : 1, parser.ImportCalls);
    }

    [Theory]
    [InlineData("notd3d12.dll")]
    [InlineData("vulkan-1.dll.bak")]
    [InlineData("d3d10core.dll.bak")]
    public void Lookalikes_DoNotSuppressStaticFallback(string module)
    {
        using var root = new SyntheticTestDirectory();
        var parser = new ControlledParser { Imports = new[] { "d3d9.dll" } };
        var scanner = new ModuleScanner { RawModuleEnumerator = _ => new[] { module } };
        using var process = Process.GetCurrentProcess();
        var result = new ApiClassifier(scanner, parser)
            .ClassifyDetailed(process, root.CreateFile("Game.exe", "fixture"));
        Assert.Equal(GraphicsApi.DX9, result.PrimaryApi);
        Assert.Equal("StaticPEImports", result.EvidenceSource);
        Assert.Equal(1, parser.ImportCalls);
    }

    [Theory]
    [InlineData("d3d10.dll", true)]
    [InlineData("D3D10CORE.DLL", true)]
    [InlineData("prefix-d3d10core.dll", false)]
    public void UsesDx10_UsesOneExactModuleScan(string module, bool expected)
    {
        int enumerations = 0;
        var scanner = new ModuleScanner { RawModuleEnumerator = _ =>
        { enumerations++; return new[] { module }; } };
        using var process = Process.GetCurrentProcess();
        Assert.Equal(expected, scanner.UsesDx10(process));
        Assert.Equal(1, enumerations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoreStaticImports_ReportActualNamesWithoutDuplicatingDx10(bool includeD3D10)
    {
        using var root = new SyntheticTestDirectory();
        var parser = new ControlledParser { Imports = includeD3D10
            ? new[] { "D3D10CORE.DLL", "D3D10.DLL" } : new[] { "D3D10CORE.DLL" } };
        var scanner = new ModuleScanner { RawModuleEnumerator = _ => Array.Empty<string>() };
        using var process = Process.GetCurrentProcess();
        var result = new ApiClassifier(scanner, parser)
            .ClassifyDetailed(process, root.CreateFile("Game.exe", "fixture"));
        Assert.Equal(GraphicsApi.DX10, Assert.Single(result.ObservedApis));
        Assert.Equal("StaticPEImports", result.EvidenceSource);
        Assert.Contains("Static PE import: d3d10core.dll", result.Evidence);
        Assert.Equal(includeD3D10, result.Evidence.Contains("Static PE import: d3d10.dll"));
    }
}
