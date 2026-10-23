using System.Buffers.Binary;
using System.Formats.Tar;
using System.Text;
using System.Text.Json.Nodes;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class D3D10ManagedPayloadTests
{
    private static readonly string[] Three = { "d3d10core.dll", "d3d11.dll", "dxgi.dll" };

    // Header-only synthetic DLLs, never loaded/executed. Distinct bytes per file/tag.
    private static byte[] Pe(string label, bool x64 = true)
    {
        var bytes = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x5a4d);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3c), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(128), 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(132), x64 ? (ushort)0x8664 : (ushort)0x14c);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(148), 240);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(150), 0x2022);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(152), 0x20b);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(184), 4096);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(188), 512);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(208), 4096);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(212), 512);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(220), 3);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(260), 16);
        Encoding.ASCII.GetBytes(label).CopyTo(bytes, 400);
        return bytes;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly OfficialAcquisitionFixture Official = new("v3.1.1");
        internal readonly SyntheticTestDirectory Game = new();
        internal readonly SyntheticTestDirectory Storage = new();
        internal GameLibraryStore Store;
        internal GameProfile Profile;
        internal string Backups => Storage.GetPath("backups");
        internal string Library => Storage.GetPath("library.json");
        internal string VersionDir => Path.Combine(Official.Source, "v3.1.1");
        internal string Source(string dll) => Path.Combine(VersionDir, "x64", dll);
        internal string Target(string dll) => Game.GetPath(dll);

        internal Fixture()
        {
            Store = new GameLibraryStore(Library, Backups);
            Profile = new GameProfile(Game.CreateFile("Game.exe", "synthetic-never-executed"))
                { Api = GraphicsApi.DX10, Architecture = "x64" };
            Game.CreateFile("keep.bin", "unrelated");
            Game.CreateFile("d3d11.dll", "native-wrapper");
            Official.Archives["v3.1.1"] = OfficialAcquisitionFixture.Archive("v3.1.1",
                Three.Append("d3d9.dll").Select(d => ($"dxvk-3.1.1/x64/{d}", TarEntryType.RegularFile, Pe("v3.1.1:" + d))).ToArray());
        }

        internal async Task Acquire() => await Official.Acquirer.AcquireAsync("v3.1.1");
        internal DxvkInstaller Installer(MultiFileTransactionTestHooks? hooks = null) =>
            SyntheticSafetyFixture.CreateInstaller(Official.Http, new MultiFileTransactionEngine(Backups, hooks), Store, Official.Source);
        internal void Restart() => Store = new GameLibraryStore(Library, Backups);
        internal async Task<bool> Restore() => await SyntheticSafetyFixture.CreateRollback(new MultiFileTransactionEngine(Backups), Store).RestoreOriginalDllsAsync(Profile);
        internal GameInstallation Installation => Store.FindByInstallationPath(Game.RootPath)!;
        internal async Task SeedLegacy()
        {
            string legacy = Path.Combine(Official.Source, "2.5", "x64");
            Directory.CreateDirectory(legacy);
            foreach (string dll in Three.Skip(1)) File.WriteAllText(Path.Combine(legacy, dll), "legacy:" + dll);
            Assert.True(await Installer().ApplyToGameAsync(Profile, new ReleaseInfo { Version = "2.5" }));
        }
        internal async Task<bool> Apply() => await Installer().ApplyToGameAsync(Profile, new ReleaseInfo { Version = "v3.1.1" });
        public void Dispose() { Official.Dispose(); Game.Dispose(); Storage.Dispose(); }
    }

    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.Exists(root)
        ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(root, p), File.ReadAllBytes)
        : new();

    private static void Unchanged(Dictionary<string, byte[]> before, string root)
    {
        var after = Snapshot(root);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
    }

    [Theory]
    [InlineData(GraphicsApi.DX10, "v3.1.1", "x64", true)]
    [InlineData(GraphicsApi.DX10, "v3.1.1", "amd64", true)]
    [InlineData(GraphicsApi.DX10, "v3.1.1", "x32", false)]
    [InlineData(GraphicsApi.DX10, "v3.1.2", "x64", false)]
    [InlineData(GraphicsApi.DX10, "3.1.1", "x64", false)]
    [InlineData(GraphicsApi.DX10, "legacy", "x64", false)]
    [InlineData(GraphicsApi.DX9, "v3.1.1", "x64", false)]
    [InlineData(GraphicsApi.DX11, "v3.1.1", "x64", false)]
    public void Selection_IsBoundToExactQualifiedContext(GraphicsApi api, string tag, string arch, bool core)
    {
        Assert.Equal(core, DxvkDeploymentPayload.Resolve(api, tag, arch).Contains("d3d10core.dll"));
        Assert.Equal(core ? 3 : DxvkCompatibility.GetRequiredDlls(api).Length,
            DxvkDeploymentPayload.Resolve(api, tag, arch).Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task QualifiedApply_Reapply_Restart_RestorePreservesActualBaseline(bool coreExisted, bool nested)
    {
        using var f = new Fixture();
        await f.Acquire();
        if (nested)
        {
            f.Profile = new GameProfile(f.Game.CreateFile("bin/Game.exe", "synthetic-never-executed"))
                { Api = GraphicsApi.DX10, Architecture = "x64" };
            f.Game.CreateFile("bin/d3d11.dll", "native-wrapper");
            var root = f.Store.GetOrCreateInstallation(f.Game.RootPath, "synthetic-root");
            var exe = root.GetOrAddExecutable("bin/Game.exe");
            exe.LastKnownApi = GraphicsApi.DX10;
            exe.LastKnownArchitecture = "x64";
            f.Store.Save(root);
        }
        string prefix = nested ? "bin/" : "";
        if (coreExisted) f.Game.CreateFile(prefix + "d3d10core.dll", "native-core-baseline");
        f.Game.CreateFile(prefix + "dxvk.conf", "# unrelated config\ndxgi.syncInterval = 1\n");
        var original = Snapshot(f.Game.RootPath);
        Assert.True(await f.Apply());
        foreach (string dll in Three)
        {
            Assert.Equal(File.ReadAllBytes(f.Source(dll)), File.ReadAllBytes(f.Target(prefix + dll)));
            var record = f.Installation.FindManagedFile(prefix + dll)!;
            Assert.Equal("v3.1.1", record.ManagedDxvkVersion);
        }
        var core = f.Installation.FindManagedFile(prefix + "d3d10core.dll")!;
        Assert.Equal(coreExisted ? FileOriginalState.Existing : FileOriginalState.Missing, core.OriginalState);
        f.Restart();
        Assert.True(await f.Installer().ReapplyAsync(f.Profile));
        f.Restart();
        Assert.True(await f.Restore());
        Unchanged(original, f.Game.RootPath);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("wrong-arch")]
    [InlineData("bad-pe")]
    [InlineData("wrong-tag")]
    [InlineData("no-record")]
    public async Task InvalidSelectedSource_ListingApplyAndReapplyRefuseWithoutMutation(string kind)
    {
        using var f = new Fixture();
        if (kind is "wrong-arch" or "bad-pe")
            f.Official.Archives["v3.1.1"] = OfficialAcquisitionFixture.Archive("v3.1.1",
                Three.Select(d => ($"dxvk-3.1.1/x64/{d}", TarEntryType.RegularFile,
                    d != "d3d10core.dll" ? Pe(d) : kind == "bad-pe" ? Encoding.ASCII.GetBytes("not-pe") : Pe(d, false))).ToArray());
        await f.Acquire();
        await f.SeedLegacy();
        // Persist a legacy two-file record for this exact version, as produced by older builds.
        f.Installation.ManagedDxvkVersion = "v3.1.1";
        f.Store.Save(f.Installation);
        if (kind == "missing") File.Delete(f.Source("d3d10core.dll"));
        if (kind == "corrupt") File.WriteAllText(f.Source("d3d10core.dll"), "changed-source");
        string manifestPath = Path.Combine(f.VersionDir, DxvkReleaseAcquirer.ManifestName);
        if (kind == "wrong-tag")
        {
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest["Origin"]!["Version"] = "v3.1.2";
            File.WriteAllText(manifestPath, manifest.ToJsonString());
        }
        if (kind == "no-record")
        {
            File.Delete(manifestPath);
            File.Delete(Path.Combine(f.VersionDir, DxvkReleaseAcquirer.MarkerName));
        }
        var beforeGame = Snapshot(f.Game.RootPath);
        var beforeBackups = Snapshot(f.Backups);
        byte[] beforeLibrary = File.ReadAllBytes(f.Library);
        int requests = f.Official.Requests.Count;
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(f.Official.Source, "v3.1.1", GraphicsApi.DX10, "x64"));
        Assert.DoesNotContain("v3.1.1", DxvkLocalReleaseLibrary.GetAvailableVersions(f.Official.Source, GraphicsApi.DX10, "x64"));
        Assert.False(await f.Apply());
        Assert.False(await f.Installer().ReapplyAsync(f.Profile));
        Assert.Equal(requests, f.Official.Requests.Count);
        Unchanged(beforeGame, f.Game.RootPath);
        Unchanged(beforeBackups, f.Backups);
        Assert.Equal(beforeLibrary, File.ReadAllBytes(f.Library));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyRestore_LeavesUnrecordedCoreUntouched(bool selectedVersion)
    {
        using var f = new Fixture();
        await f.SeedLegacy();
        f.Game.CreateFile("d3d10core.dll", "unowned-core");
        if (selectedVersion) { f.Installation.ManagedDxvkVersion = "v3.1.1"; f.Store.Save(f.Installation); }
        f.Restart();
        Assert.Null(f.Installation.FindManagedFile("d3d10core.dll"));
        Assert.True(await f.Restore());
        Assert.Equal("unowned-core", File.ReadAllText(f.Target("d3d10core.dll")));
        Assert.Equal("native-wrapper", File.ReadAllText(f.Target("d3d11.dll")));
        Assert.False(File.Exists(f.Target("dxgi.dll")));
    }

    [Fact]
    public async Task QualifiedSource_ChangedAfterSelectionCannotBecomeTransactionBaseline()
    {
        using var f = new Fixture();
        await f.Acquire();
        await f.SeedLegacy();
        var before = Snapshot(f.Game.RootPath);
        var backups = Snapshot(f.Backups);
        byte[] library = File.ReadAllBytes(f.Library);
        var installer = f.Installer();
        int inspections = 0;
        installer.ActivityInspector = new ProcessActivityInspector(processEnumerator: () =>
        {
            if (++inspections == 2) File.WriteAllText(f.Source("d3d10core.dll"), "changed-after-selection");
            return Array.Empty<System.Diagnostics.Process>();
        });
        Assert.False(await installer.ApplyToGameAsync(f.Profile, new ReleaseInfo { Version = "v3.1.1" }));
        Assert.Equal(2, inspections);
        Unchanged(before, f.Game.RootPath);
        Unchanged(backups, f.Backups);
        Assert.Equal(library, File.ReadAllBytes(f.Library));
    }

    [Fact]
    public async Task QualifiedSelection_UnknownArchitectureRefusesWithoutGuessing()
    {
        using var f = new Fixture();
        await f.Acquire();
        await f.SeedLegacy();
        f.Installation.ManagedDxvkVersion = "v3.1.1";
        f.Installation.ManagedDxvkArchitecture = null;
        f.Store.Save(f.Installation);
        f.Profile.Architecture = "Unknown";
        var before = Snapshot(f.Game.RootPath);
        byte[] library = File.ReadAllBytes(f.Library);
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(f.Official.Source, "v3.1.1", GraphicsApi.DX10, "Unknown"));
        Assert.False(await f.Apply());
        Assert.False(await f.Installer().ReapplyAsync(f.Profile));
        Unchanged(before, f.Game.RootPath);
        Assert.Equal(library, File.ReadAllBytes(f.Library));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExplicitLegacyExpansion_PreservesWrapperBaselineAndCapturesCore(bool coreExisted, bool reapply)
    {
        using var f = new Fixture();
        await f.Acquire();
        await f.SeedLegacy();
        if (coreExisted) f.Game.CreateFile("d3d10core.dll", "transition-core-baseline");
        var wrapper = f.Installation.FindManagedFile("d3d11.dll")!;
        string? originalHash = wrapper.OriginalSha256;
        string? originalBackup = wrapper.BackupRelativePath;
        if (reapply) { f.Installation.ManagedDxvkVersion = "v3.1.1"; f.Store.Save(f.Installation); }
        f.Restart();
        Assert.True(reapply ? await f.Installer().ReapplyAsync(f.Profile) : await f.Apply());
        Assert.Equal(originalHash, f.Installation.FindManagedFile("d3d11.dll")!.OriginalSha256);
        Assert.Equal(originalBackup, f.Installation.FindManagedFile("d3d11.dll")!.BackupRelativePath);
        Assert.Equal(coreExisted ? FileOriginalState.Existing : FileOriginalState.Missing,
            f.Installation.FindManagedFile("d3d10core.dll")!.OriginalState);
        f.Restart();
        Assert.True(await f.Restore());
        Assert.Equal("native-wrapper", File.ReadAllText(f.Target("d3d11.dll")));
        if (coreExisted) Assert.Equal("transition-core-baseline", File.ReadAllText(f.Target("d3d10core.dll")));
        else Assert.False(File.Exists(f.Target("d3d10core.dll")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedExpansion_RestoresImmediateLegacyStateAndDurableOwnership(bool coreExisted, bool persistenceFailure)
    {
        using var f = new Fixture();
        await f.Acquire();
        await f.SeedLegacy();
        if (coreExisted) f.Game.CreateFile("d3d10core.dll", "pre-operation-core");
        var before = Snapshot(f.Game.RootPath);
        var beforeBackups = Snapshot(f.Backups);
        byte[] library = File.ReadAllBytes(f.Library);
        bool injected = false;
        var installer = f.Installer(new MultiFileTransactionTestHooks
        {
            AfterApply = (_, index) =>
            {
                if (index != 2) return;
                injected = true;
                if (persistenceFailure) f.Store.CustomCommitReplace = (_, _) => throw new IOException("Synthetic ownership refusal");
                else throw new IOException("Synthetic third-file failure");
            }
        });
        Assert.False(await installer.ApplyToGameAsync(f.Profile, new ReleaseInfo { Version = "v3.1.1" }));
        Assert.True(injected);
        f.Store.CustomCommitReplace = null;
        Unchanged(before, f.Game.RootPath);
        Unchanged(beforeBackups, f.Backups);
        Assert.Equal(library, File.ReadAllBytes(f.Library));
        f.Restart();
        Assert.Null(f.Installation.FindManagedFile("d3d10core.dll"));
        Assert.Equal("2.5", f.Installation.ManagedDxvkVersion);
        Assert.True(await f.Restore());
        Assert.Equal("native-wrapper", File.ReadAllText(f.Target("d3d11.dll")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositiveExternalLayout_AdoptsOnlyObservedFiles_AndReapplyCanExpand(bool core)
    {
        using var f = new Fixture();
        await f.Acquire();
        foreach (string dll in core ? Three : Three.Skip(1)) File.Copy(f.Source(dll), f.Target(dll), true);
        var assessment = new ExistingDxvkDetector(f.Official.Source, GraphicsApi.DX10).AssessDirectory(f.Game.RootPath, "x64");
        Assert.True(assessment.CanBeAdopted);
        Assert.Equal(core ? 3 : 2, assessment.DetectedDlls.Count);
        Assert.True(f.Installer().AdoptExisting(f.Profile, assessment));
        Assert.Equal(core, f.Installation.FindManagedFile("d3d10core.dll") != null);
        f.Restart();
        Assert.True(await f.Installer().ReapplyAsync(f.Profile));
        Assert.NotNull(f.Installation.FindManagedFile("d3d10core.dll"));
        Assert.True(await f.Restore());
        foreach (string dll in Three) Assert.False(File.Exists(f.Target(dll))); // Existing adoption baseline semantics.
    }

    [Theory]
    [InlineData("core-only")]
    [InlineData("native")]
    [InlineData("modified")]
    [InlineData("conflicting")]
    public async Task CoreAssessment_UnknownOrIncoherentCannotBeAdopted(string kind)
    {
        using var f = new Fixture();
        await f.Acquire();
        foreach (string dll in kind == "core-only" ? Three.Take(1) : Three) File.Copy(f.Source(dll), f.Target(dll), true);
        if (kind is "native" or "modified") File.WriteAllText(f.Target("d3d10core.dll"), kind + "-core");
        if (kind == "conflicting")
        {
            string other = Path.Combine(f.Official.Source, "2.5", "x64");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "d3d10core.dll"), "other-release-core");
            File.WriteAllText(f.Target("d3d10core.dll"), "other-release-core");
        }
        var assessment = new ExistingDxvkDetector(f.Official.Source, GraphicsApi.DX10).AssessDirectory(f.Game.RootPath, "x64");
        Assert.Contains("d3d10core.dll", assessment.DetectedDlls);
        Assert.False(assessment.CanBeAdopted);
        var before = Snapshot(f.Game.RootPath);
        Assert.False(f.Installer().AdoptExisting(f.Profile, assessment));
        Assert.Empty(f.Store.GetAll());
        Unchanged(before, f.Game.RootPath);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("removed")]
    [InlineData("added")]
    public async Task Adoption_RevalidatesObservedIdentityAndSet(string change)
    {
        using var f = new Fixture();
        await f.Acquire();
        foreach (string dll in change == "added" ? Three.Skip(1) : Three) File.Copy(f.Source(dll), f.Target(dll), true);
        var assessment = new ExistingDxvkDetector(f.Official.Source, GraphicsApi.DX10).AssessDirectory(f.Game.RootPath, "x64");
        Assert.True(assessment.CanBeAdopted);
        if (change == "changed") File.WriteAllText(f.Target("d3d10core.dll"), "external-change");
        if (change == "removed") File.Delete(f.Target("d3d10core.dll"));
        if (change == "added") File.Copy(f.Source("d3d10core.dll"), f.Target("d3d10core.dll"));
        var before = Snapshot(f.Game.RootPath);
        Assert.False(f.Installer().AdoptExisting(f.Profile, assessment));
        Assert.Empty(f.Store.GetAll());
        Unchanged(before, f.Game.RootPath);
    }

    [Theory]
    [InlineData(GraphicsApi.DX9)]
    [InlineData(GraphicsApi.DX11)]
    public async Task OtherApis_DoNotRequireOrAdoptUnrelatedCore(GraphicsApi api)
    {
        using var f = new Fixture();
        await f.Acquire();
        f.Profile.Api = api;
        File.Delete(f.Target("d3d11.dll"));
        f.Game.CreateFile("d3d10core.dll", "unrelated-core");
        foreach (string dll in DxvkCompatibility.GetRequiredDlls(api)) File.Copy(f.Source(dll), f.Target(dll));
        var assessment = new ExistingDxvkDetector(f.Official.Source, api).AssessDirectory(f.Game.RootPath, "x64");
        Assert.True(assessment.CanBeAdopted);
        Assert.DoesNotContain("d3d10core.dll", assessment.DetectedDlls);
        Assert.True(f.Installer().AdoptExisting(f.Profile, assessment));
        Assert.True(await f.Restore());
        Assert.Equal("unrelated-core", File.ReadAllText(f.Target("d3d10core.dll")));
        Assert.True(await f.Apply());
        Assert.Null(f.Installation.FindManagedFile("d3d10core.dll"));
        Assert.Equal("unrelated-core", File.ReadAllText(f.Target("d3d10core.dll")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedCore_RefusesWrapperOnlySharedDirectoryTransitions(bool nestedSibling)
    {
        using var f = new Fixture();
        await f.Acquire();
        Assert.True(await f.Apply());
        var profile = nestedSibling ? new GameProfile(f.Game.CreateFile("child/Other.exe", "synthetic-never-executed")) : f.Profile;
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        if (nestedSibling)
        {
            var exe = f.Installation.GetOrAddExecutable("child/Other.exe");
            exe.LastKnownApi = GraphicsApi.DX11;
            exe.LastKnownArchitecture = "x64";
            f.Store.Save(f.Installation);
        }
        var before = Snapshot(f.Game.RootPath);
        var beforeBackups = Snapshot(f.Backups);
        byte[] library = File.ReadAllBytes(f.Library);
        var installer = f.Installer();
        Assert.False(await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "v3.1.1" }));
        Assert.Contains("owned D3D10 core", installer.LastRefusalReason);
        Assert.False(await installer.ReapplyAsync(profile));
        Unchanged(before, f.Game.RootPath);
        Unchanged(beforeBackups, f.Backups);
        Assert.Equal(library, File.ReadAllBytes(f.Library));
    }

    [Fact]
    public async Task LegacyReapply_InvalidRecordedArchitectureDoesNotFallBackToX64()
    {
        using var f = new Fixture();
        await f.SeedLegacy();
        f.Installation.ManagedDxvkArchitecture = "unrecognized-architecture";
        f.Store.Save(f.Installation);
        // A replacement x64 source exists, so guessing x64 would visibly overwrite
        // the managed wrapper instead of preserving the original refusal.
        File.WriteAllText(Path.Combine(f.Official.Source, "2.5", "x64", "d3d11.dll"), "replacement-source");
        var before = Snapshot(f.Game.RootPath);
        var backups = Snapshot(f.Backups);
        byte[] library = File.ReadAllBytes(f.Library);
        f.Restart();
        Assert.False(await f.Installer().ReapplyAsync(f.Profile));
        Unchanged(before, f.Game.RootPath);
        Unchanged(backups, f.Backups);
        Assert.Equal(library, File.ReadAllBytes(f.Library));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoreRestore_ChangedTargetOrBackupRefusesConservatively(bool backup)
    {
        using var f = new Fixture();
        await f.Acquire();
        f.Game.CreateFile("d3d10core.dll", "native-core");
        var installer = f.Installer();
        bool applied = await installer.ApplyToGameAsync(f.Profile, new ReleaseInfo { Version = "v3.1.1" });
        Assert.True(applied, $"DX10 core restore setup; backup={backup}; " +
            $"category={SyntheticSafetyFixture.RefusalCategory(installer.LastRefusalReason)}; " +
            $"{(installer.LeaseCoordinator as OperationLeaseCoordinator)?.LastAcquisitionDiagnostic ?? "NoLeaseDiagnosticRecorded"}");
        var record = f.Installation.FindManagedFile("d3d10core.dll")!;
        string changed = backup ? Path.Combine(f.Backups, record.BackupRelativePath!) : f.Target("d3d10core.dll");
        File.WriteAllText(changed, "external-change");
        var before = Snapshot(f.Game.RootPath);
        f.Restart();
        Assert.False(await f.Restore());
        Unchanged(before, f.Game.RootPath);
        Assert.Equal("external-change", File.ReadAllText(changed));
    }
}
