using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Monitoring;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class ArchitectureClassificationTests
{
    // Imports/modules are controlled evidence. Architecture uses the real parser.
    // These minimal header fixtures are data, not runnable executables.
    private sealed class FixtureParser : PeParser
    {
        public override IEnumerable<string> GetImports(string path) => new[] { "d3d11.dll" };
    }

    private static ApiClassifier Classifier(bool runtime)
        => new(new ModuleScanner
        {
            RawModuleEnumerator = _ => runtime ? new[] { "d3d11.dll" } : Array.Empty<string>()
        }, new FixtureParser());

    private static void WriteMachine(string path, ushort machine)
    {
        byte[] data = new byte[256];
        data[0] = (byte)'M'; data[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x3c), 0x80);
        data[0x80] = (byte)'P'; data[0x81] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x84), machine);
        File.WriteAllBytes(path, data);
    }

    [Theory]
    [InlineData(0x014c, "x32", true)]
    [InlineData(0x014c, "x32", false)]
    [InlineData(0x8664, "x64", true)]
    [InlineData(0x8664, "x64", false)]
    public void DeclaredPeMachine_IsPreservedIndependentlyOfGraphicsEvidence(int machine, string expected, bool runtime)
    {
        using var root = new SyntheticTestDirectory();
        string path = Path.Combine(root.RootPath, "Game.exe");
        WriteMachine(path, (ushort)machine);
        using var candidate = Process.GetCurrentProcess();
        var result = Classifier(runtime).ClassifyDetailed(candidate, path);
        Assert.Equal(expected, result.Architecture);
        Assert.Equal(GraphicsApi.DX11, result.PrimaryApi);
        Assert.Equal(runtime ? "RuntimeModules" : "StaticPEImports", result.EvidenceSource);
    }

    [Theory]
    [InlineData("missing", true)]
    [InlineData("missing", false)]
    [InlineData("non-pe", true)]
    [InlineData("non-pe", false)]
    [InlineData("truncated", true)]
    [InlineData("truncated", false)]
    [InlineData("unsupported-machine", true)]
    [InlineData("unsupported-machine", false)]
    [InlineData("locked", true)]
    [InlineData("locked", false)]
    public void UnavailablePeArchitecture_NeverBecomesCoordinatorOrOsArchitecture(string state, bool runtime)
    {
        using var root = new SyntheticTestDirectory();
        string path = Path.Combine(root.RootPath, "Game.exe");
        switch (state)
        {
            case "missing": break;
            case "non-pe": File.WriteAllText(path, "non-PE fixture"); break;
            case "truncated": File.WriteAllBytes(path, new byte[] { (byte)'M', (byte)'Z' }); break;
            case "unsupported-machine": WriteMachine(path, 0xaa64); break;
            case "locked": WriteMachine(path, 0x014c); break;
            default: throw new InvalidOperationException("Unknown fixture case.");
        }
        using var held = state == "locked" ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        using var candidate = Process.GetCurrentProcess();
        var result = Classifier(runtime).ClassifyDetailed(candidate, path);
        Assert.Equal("Unknown", result.Architecture);
        // A missing file cannot supply static imports; runtime API evidence is independent.
        Assert.Equal(state == "missing" && !runtime ? GraphicsApi.Unknown : GraphicsApi.DX11, result.PrimaryApi);
        if (result.PrimaryApi == GraphicsApi.DX11)
        {
            Assert.Equal(runtime ? "RuntimeModules" : "StaticPEImports", result.EvidenceSource);
            Assert.Contains("d3d11.dll", string.Join(" ", result.Evidence));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownClassification_RefusesDeploymentDespiteAvailableX32AndX64Payloads(bool runtime)
    {
        using var root = new SyntheticTestDirectory();
        string target = Path.Combine(root.RootPath, "Fixture");
        Directory.CreateDirectory(target);
        string exe = Path.Combine(target, "Game.exe");
        File.WriteAllText(exe, "non-PE fixture; never execute");
        File.WriteAllText(Path.Combine(target, "dxvk.conf"), "# preserve independent config");
        File.WriteAllText(Path.Combine(target, "keep.bin"), "retained sentinel");
        using var candidate = Process.GetCurrentProcess();
        var classified = Classifier(runtime).ClassifyDetailed(candidate, exe);
        var profile = new GameProfile(exe) { Api = classified.PrimaryApi, Architecture = classified.Architecture };
        string source = Path.Combine(root.RootPath, "source");
        foreach (string architecture in new[] { "x32", "x64" })
        {
            string directory = Path.Combine(source, "v2.6", architecture);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "d3d11.dll"), "synthetic payload " + architecture);
            File.WriteAllText(Path.Combine(directory, "dxgi.dll"), "synthetic DXGI " + architecture);
            Assert.True(DxvkLocalReleaseLibrary.IsVersionAvailable(source, "v2.6", GraphicsApi.DX11, architecture));
        }
        var store = new GameLibraryStore(Path.Combine(root.RootPath, "library.json"), Path.Combine(root.RootPath, "backups"));
        var coordinator = SyntheticSafetyFixture.ConfigureStore(store);
        var installation = store.GetOrCreateInstallation(target, "Fixture");
        installation.GetOrAddExecutable("Game.exe").LastKnownApi = classified.PrimaryApi;
        store.Save(installation);
        byte[] libraryBefore = File.ReadAllBytes(store.LibraryFilePath);
        var targetBefore = Directory.GetFiles(target).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        var sourceBefore = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(source, path), File.ReadAllBytes);
        var handler = new NoHttp();
        using var http = new HttpClient(handler);
        var installer = new DxvkInstaller(http, new MultiFileTransactionEngine(Path.Combine(root.RootPath, "backups")),
            store, source, null, coordinator, new ProcessActivityInspector(processEnumerator: () => Array.Empty<Process>()));

        bool result = await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "v2.6" });

        Assert.Equal(GraphicsApi.DX11, classified.PrimaryApi);
        Assert.Equal("Unknown", classified.Architecture);
        Assert.False(result);
        Assert.Equal("Cannot select DXVK source for an unknown architecture.", installer.LastRefusalReason);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(targetBefore.Keys.OrderBy(x => x), Directory.GetFiles(target).Select(path => Path.GetFileName(path)!).OrderBy(x => x));
        foreach (var file in targetBefore) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(target, file.Key!)));
        Assert.Equal(sourceBefore.Keys.OrderBy(x => x), Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(source, path)).OrderBy(x => x));
        foreach (var file in sourceBefore) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(source, file.Key)));
        Assert.Equal(libraryBefore, File.ReadAllBytes(store.LibraryFilePath));
        Assert.Null(store.FindByInstallationPath(target)!.PendingAction);
    }

    private sealed class NoHttp : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Unknown architecture must refuse before HTTP.");
        }
    }
}
