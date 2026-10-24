using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests;

public sealed class AdoptedDxvkLifecycleAcceptanceTests
{
    private sealed record ResourceSnapshot(
        Dictionary<string, byte[]> Files,
        HashSet<string> Directories);

    private static ResourceSnapshot SnapshotResources(string root)
    {
        if (!Directory.Exists(root))
        {
            return new ResourceSnapshot(
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);

        var dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ResourceSnapshot(files, dirs);
    }

    private static void AssertResourcesUnchanged(ResourceSnapshot expected, string root)
    {
        var actual = SnapshotResources(root);
        Assert.Equal(
            expected.Directories.OrderBy(d => d, StringComparer.OrdinalIgnoreCase),
            actual.Directories.OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            expected.Files.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            actual.Files.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        foreach (var (path, bytes) in expected.Files)
        {
            Assert.Equal(bytes, actual.Files[path]);
        }
    }

    private static Dictionary<string, string> ParseConfigAssignments(string content)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#") || trimmed.StartsWith(";"))
                continue;
            int idx = trimmed.IndexOf('=');
            if (idx >= 0)
            {
                string key = trimmed[..idx].Trim();
                string val = trimmed[(idx + 1)..].Trim();
                dict[key] = val;
            }
        }
        return dict;
    }

    private static (string Name, TarEntryType Type, byte[] Data)[] CreateReleaseArchiveEntries(string tag)
    {
        string prefix = $"dxvk-{tag[1..]}";
        return new[]
        {
            ($"{prefix}/x32/d3d9.dll", TarEntryType.RegularFile, Encoding.UTF8.GetBytes($"{tag}:x32:d3d9")),
            ($"{prefix}/x32/d3d11.dll", TarEntryType.RegularFile, Encoding.UTF8.GetBytes($"{tag}:x32:d3d11")),
            ($"{prefix}/x32/dxgi.dll", TarEntryType.RegularFile, Encoding.UTF8.GetBytes($"{tag}:x32:dxgi")),
            ($"{prefix}/x64/d3d9.dll", TarEntryType.RegularFile, Encoding.UTF8.GetBytes($"{tag}:x64:d3d9")),
            ($"{prefix}/x64/d3d11.dll", TarEntryType.RegularFile, Encoding.UTF8.GetBytes($"{tag}:x64:d3d11")),
            ($"{prefix}/x64/dxgi.dll", TarEntryType.RegularFile, Encoding.UTF8.GetBytes($"{tag}:x64:dxgi")),
        };
    }

    private sealed class LifecycleHttpTransport : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _archives = new(StringComparer.OrdinalIgnoreCase);
        private int _requestCount;
        private volatile bool _rejectAllRequests;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public bool RejectAllRequests
        {
            get => _rejectAllRequests;
            set => _rejectAllRequests = value;
        }

        public LifecycleHttpTransport(params string[] tags)
        {
            foreach (var tag in tags)
            {
                _archives[tag] = OfficialAcquisitionFixture.Archive(tag, CreateReleaseArchiveEntries(tag));
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (_rejectAllRequests)
            {
                throw new InvalidOperationException($"Unexpected HTTP call after transport marked unavailable: {request.Method} {request.RequestUri}");
            }

            string tag = request.RequestUri!.AbsolutePath.Split('/')[^1];
            if (_archives.TryGetValue(tag, out var bytes))
            {
                return Task.FromResult(OfficialAcquisitionFixture.Json(OfficialAcquisitionFixture.Metadata(tag, bytes, digest: true)));
            }

            foreach (var entry in _archives)
            {
                if (string.Equals(request.RequestUri.AbsoluteUri, OfficialAcquisitionFixture.AssetUrl(entry.Key), StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(entry.Value)
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed record ServiceStack(
        string AppDataDir,
        CacheStore CacheStore,
        DxvkGithubClient GithubClient,
        ProfileStore ProfileStore,
        GameLibraryStore LibraryStore,
        MultiFileTransactionEngine TransactionEngine,
        DxvkInstaller Installer,
        DxvkRollback Rollback,
        DxvkManager Manager);

    private static ServiceStack ConstructServiceStack(string appDataDir, HttpClient httpClient)
    {
        string profilesDir = Path.Combine(appDataDir, "Profiles");
        string libraryFile = Path.Combine(profilesDir, "game-library.json");
        string profilesFile = Path.Combine(profilesDir, "games.json");
        string backupsDir = Path.Combine(profilesDir, "Backups");
        string cacheFile = Path.Combine(appDataDir, "Cache", "release-cache.json");
        string sourcesDir = Path.Combine(appDataDir, "Sources");

        var cacheStore = new CacheStore(cacheFile);
        var github = new DxvkGithubClient(httpClient, cacheStore);
        var profileStore = new ProfileStore(profilesFile);
        var libraryStore = new GameLibraryStore(libraryFile, backupsDir, profilesFile);
        var engine = new MultiFileTransactionEngine(backupsDir);
        var installer = SyntheticSafetyFixture.CreateInstaller(httpClient, engine, libraryStore, sourcesDir);
        var rollback = SyntheticSafetyFixture.CreateRollback(engine, libraryStore);
        var manager = SyntheticSafetyFixture.CreateManager(installer, rollback, github, profileStore, libraryStore);
        installer.GetReleaseAcquirer(github).ProcessLeaseCoordinator = libraryStore.LeaseCoordinator;

        return new ServiceStack(appDataDir, cacheStore, github, profileStore, libraryStore, engine, installer, rollback, manager);
    }

    [Theory]
    [InlineData(GraphicsApi.DX9, "x32", true)]
    [InlineData(GraphicsApi.DX9, "x32", false)]
    [InlineData(GraphicsApi.DX9, "x64", true)]
    [InlineData(GraphicsApi.DX9, "x64", false)]
    [InlineData(GraphicsApi.DX11, "x32", true)]
    [InlineData(GraphicsApi.DX11, "x32", false)]
    [InlineData(GraphicsApi.DX11, "x64", true)]
    [InlineData(GraphicsApi.DX11, "x64", false)]
    public async Task AdoptedDxvkLifecycle_PersistedManual_AdoptsUpdatesDisablesCleanly(
        GraphicsApi api,
        string architecture,
        bool preexistingConfig)
    {
        using var testDir = new SyntheticTestDirectory();
        string gameDir = Path.Combine(testDir.RootPath, $"AdoptedGame_{api}_{architecture}_{preexistingConfig}");
        Directory.CreateDirectory(gameDir);

        string emptySubDir = Path.Combine(gameDir, "Data", "EmptySubDir");
        Directory.CreateDirectory(emptySubDir);

        string exePath = Path.Combine(gameDir, "Game.exe");
        byte[] exeBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 };
        File.WriteAllBytes(exePath, exeBytes);

        string sentinelPath = Path.Combine(gameDir, "sentinel.bin");
        byte[] sentinelBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };
        File.WriteAllBytes(sentinelPath, sentinelBytes);

        // Place ONLY the matching older-version wrappers externally into game dir before adoption
        string? d3d9Path = null;
        string? d3d11Path = null;
        string? dxgiPath = null;

        if (api == GraphicsApi.DX9)
        {
            d3d9Path = Path.Combine(gameDir, "d3d9.dll");
            File.WriteAllBytes(d3d9Path, Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d9"));
        }
        else if (api == GraphicsApi.DX11)
        {
            d3d11Path = Path.Combine(gameDir, "d3d11.dll");
            File.WriteAllBytes(d3d11Path, Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d11"));
            dxgiPath = Path.Combine(gameDir, "dxgi.dll");
            File.WriteAllBytes(dxgiPath, Encoding.UTF8.GetBytes($"v2.5:{architecture}:dxgi"));
        }

        string confPath = Path.Combine(gameDir, "dxvk.conf");
        byte[]? originalConf = null;
        SafetyFileIdentity? originalConfIdentity = null;

        if (preexistingConfig)
        {
            // Raw mixed-newline and no-final-newline configuration with prefix lookalikes
            originalConf = Encoding.UTF8.GetBytes(
                "# Preexisting user dxvk.conf for Adopted Game\r\n" +
                "# Distinctive comments and user keys\n" +
                "user.customSetting = 1\n" +
                "dxvk.hud = compiler\n" +
                "dxvk.maxFrameRate = 30\n" +
                "dxvk.hudExtra = full\n" +
                "dxvk.maxFrameRateTarget = 120\n" +
                "user.unrelatedSuffix = keep");
            File.WriteAllBytes(confPath, originalConf);
            originalConfIdentity = FileIdentity.Capture(confPath);
        }
        else
        {
            Assert.False(File.Exists(confPath));
        }

        var initialGameSnapshot = SnapshotResources(gameDir);

        string appDataDir = Path.Combine(testDir.RootPath, $"AppData_{api}_{architecture}_{preexistingConfig}");
        string profilesDir = Path.Combine(appDataDir, "Profiles");
        string backupsDir = Path.Combine(profilesDir, "Backups");
        string sourcesDir = Path.Combine(appDataDir, "Sources");

        var transport = new LifecycleHttpTransport("v2.5", "v2.6");
        using var httpClient = new HttpClient(transport);

        // 1. Acquire two distinguishable official releases into shared source library
        var stack1 = ConstructServiceStack(appDataDir, httpClient);

        // Persist Manual global settings before adoption
        string settingsFile = Path.Combine(appDataDir, "settings.json");
        var settings = SettingsStore.Load(settingsFile);
        settings.GlobalPolicy = GlobalManagementPolicy.Manual;
        settings.Save();

        var acq25 = await stack1.Manager.AcquireOfficialReleaseAsync("v2.5");
        Assert.Equal("v2.5", acq25.Version);
        Assert.True(acq25.UpstreamDigestVerified);

        var acq26 = await stack1.Manager.AcquireOfficialReleaseAsync("v2.6");
        Assert.Equal("v2.6", acq26.Version);
        Assert.True(acq26.UpstreamDigestVerified);

        // Explicitly verify valid manifests, markers, and expected acquired payloads
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", "companion-release.json")));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(sourcesDir, "v2.5"), "v2.5", out bool v25ManifestVerified));
        Assert.True(v25ManifestVerified);
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x32:d3d9"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.5", "x32", "d3d9.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x32:d3d11"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.5", "x32", "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x32:dxgi"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.5", "x32", "dxgi.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x64:d3d9"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.5", "x64", "d3d9.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x64:d3d11"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.5", "x64", "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x64:dxgi"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.5", "x64", "dxgi.dll")));

        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", "companion-release.json")));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(sourcesDir, "v2.6"), "v2.6", out bool v26ManifestVerified));
        Assert.True(v26ManifestVerified);
        Assert.Equal(Encoding.UTF8.GetBytes("v2.6:x32:d3d9"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.6", "x32", "d3d9.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.6:x32:d3d11"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.6", "x32", "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.6:x32:dxgi"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.6", "x32", "dxgi.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.6:x64:d3d9"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.6", "x64", "d3d9.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.6:x64:d3d11"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.6", "x64", "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.6:x64:dxgi"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.6", "x64", "dxgi.dll")));

        var acquiredSourcesSnapshot = SnapshotResources(sourcesDir);
        Assert.NotEmpty(acquiredSourcesSnapshot.Files);

        int requestCountAfterAcquisition = transport.RequestCount;
        Assert.True(requestCountAfterAcquisition > 0);

        // Reject all fake HTTP after acquisition: zero additional requests allowed
        transport.RejectAllRequests = true;

        // 2. Setup profile and installation records with UseGlobal exact selector intent
        var profile1 = stack1.ProfileStore.GetOrCreate(exePath);
        profile1.Api = api;
        profile1.Architecture = architecture;
        profile1.DxvkVersion = "v2.5";
        stack1.ProfileStore.Save(profile1);

        var inst1 = stack1.LibraryStore.GetOrCreateInstallation(gameDir, "AdoptedGame");
        var exeRecord1 = inst1.GetOrAddExecutable("Game.exe", "Game.exe");
        exeRecord1.LastKnownApi = api;
        exeRecord1.LastKnownArchitecture = architecture;
        inst1.ManagementPolicy = ManagementPolicy.UseGlobal();
        inst1.ManagementPolicy.PinnedDxvkVersion = "v2.5";
        stack1.LibraryStore.Save(inst1);
        string installationId = inst1.Id;

        // Assess and adopt through real manager
        var assessment = stack1.Manager.AssessExistingDxvk(profile1);
        Assert.Equal(ExistingDxvkStatus.OfficialRelease, assessment.Status);
        Assert.True(assessment.CanBeAdopted);
        Assert.Equal("v2.5", assessment.MatchedVersion);

        if (api == GraphicsApi.DX9)
        {
            Assert.Equal(new[] { "d3d9.dll" }, assessment.DetectedDlls);
        }
        else
        {
            Assert.Equal(new[] { "d3d11.dll", "dxgi.dll" }, assessment.DetectedDlls.OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
        }

        bool adoptResult = await stack1.Manager.AdoptExistingAsync(profile1);
        Assert.True(adoptResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Assert all game files byte-identical immediately after adoption
        AssertResourcesUnchanged(initialGameSnapshot, gameDir);
        Assert.True(profile1.DxvkEnabled);
        Assert.Equal("v2.5", profile1.DxvkVersion);

        var inst1AfterAdopt = stack1.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst1AfterAdopt);
        Assert.Equal("v2.5", inst1AfterAdopt!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, inst1AfterAdopt.RestorationState);
        Assert.Equal(ManagementMode.UseGlobal, inst1AfterAdopt.ManagementPolicy.Mode);
        Assert.Equal("v2.5", inst1AfterAdopt.ManagementPolicy.PinnedDxvkVersion);

        if (api == GraphicsApi.DX9)
        {
            Assert.Single(inst1AfterAdopt.ManagedFiles);
            var d3d9Rec = inst1AfterAdopt.FindManagedFile("d3d9.dll");
            Assert.NotNull(d3d9Rec);
            Assert.Equal(FileOriginalState.Missing, d3d9Rec!.OriginalState);
            Assert.Null(d3d9Rec.BackupRelativePath);
            Assert.Null(d3d9Rec.OriginalSha256);
            Assert.Equal("v2.5", d3d9Rec.ManagedDxvkVersion);
            Assert.Equal(FileIdentity.Capture(d3d9Path!).Sha256, d3d9Rec.ExpectedManagedSha256);
        }
        else
        {
            Assert.Equal(2, inst1AfterAdopt.ManagedFiles.Count);
            var d3d11Rec = inst1AfterAdopt.FindManagedFile("d3d11.dll");
            var dxgiRec = inst1AfterAdopt.FindManagedFile("dxgi.dll");
            Assert.NotNull(d3d11Rec);
            Assert.NotNull(dxgiRec);
            Assert.Equal(FileOriginalState.Missing, d3d11Rec!.OriginalState);
            Assert.Null(d3d11Rec.BackupRelativePath);
            Assert.Null(d3d11Rec.OriginalSha256);
            Assert.Equal("v2.5", d3d11Rec.ManagedDxvkVersion);
            Assert.Equal(FileIdentity.Capture(d3d11Path!).Sha256, d3d11Rec.ExpectedManagedSha256);

            Assert.Equal(FileOriginalState.Missing, dxgiRec!.OriginalState);
            Assert.Null(dxgiRec.BackupRelativePath);
            Assert.Null(dxgiRec.OriginalSha256);
            Assert.Equal("v2.5", dxgiRec.ManagedDxvkVersion);
            Assert.Equal(FileIdentity.Capture(dxgiPath!).Sha256, dxgiRec.ExpectedManagedSha256);
        }

        // Untouched config is NOT owned during adoption and no backups are created
        Assert.Null(inst1AfterAdopt.FindManagedFile("dxvk.conf"));
        Assert.Empty(SnapshotResources(backupsDir).Files);

        // 3. Reconstruct all services from disk, persist newer selector & HUD/frame-limit choices, and Update
        var stack2 = ConstructServiceStack(appDataDir, httpClient);

        var inst2 = stack2.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst2);
        inst2!.ManagementPolicy = ManagementPolicy.UseGlobal();
        inst2.ManagementPolicy.PinnedDxvkVersion = "v2.6";
        stack2.LibraryStore.Save(inst2);

        var profile2 = stack2.ProfileStore.GetOrCreate(exePath);
        profile2.HudEnabled = true;
        profile2.FrameLimit = 60;
        stack2.ProfileStore.Save(profile2);

        var updateResult = await stack2.Manager.RequestUpdateByPathAsync(profile2);
        Assert.Equal(DxvkActionResult.Applied, updateResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Verify newer version bytes in game dir
        if (api == GraphicsApi.DX9)
        {
            Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:d3d9"), File.ReadAllBytes(d3d9Path!));
        }
        else
        {
            Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:d3d11"), File.ReadAllBytes(d3d11Path!));
            Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:dxgi"), File.ReadAllBytes(dxgiPath!));
        }

        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
        Assert.True(Directory.Exists(emptySubDir));

        var inst2Updated = stack2.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst2Updated);
        Assert.Equal("v2.6", inst2Updated!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, inst2Updated.RestorationState);
        Assert.Equal("v2.6", inst2Updated.ManagementPolicy.PinnedDxvkVersion);
        Assert.Equal("v2.6", profile2.DxvkVersion);

        // Verify exact enabled config assignments and durable configuration values after Update
        Assert.True(inst2Updated.Configuration.HudEnabled);
        Assert.Equal(60, inst2Updated.Configuration.FrameLimit);
        Assert.True(inst2Updated.Configuration.FrameLimitEnabled);

        Assert.True(File.Exists(confPath));
        string confContentAfterUpdate = File.ReadAllText(confPath);
        var assignmentsAfterUpdate = ParseConfigAssignments(confContentAfterUpdate);
        Assert.True(assignmentsAfterUpdate.ContainsKey("dxvk.hud"));
        Assert.Equal("fps,devinfo", assignmentsAfterUpdate["dxvk.hud"]);
        Assert.True(assignmentsAfterUpdate.ContainsKey("dxvk.maxFrameRate"));
        Assert.Equal("60", assignmentsAfterUpdate["dxvk.maxFrameRate"]);

        if (preexistingConfig)
        {
            Assert.Equal("full", assignmentsAfterUpdate["dxvk.hudExtra"]);
            Assert.Equal("120", assignmentsAfterUpdate["dxvk.maxFrameRateTarget"]);
            Assert.Equal("1", assignmentsAfterUpdate["user.customSetting"]);
            Assert.Equal("keep", assignmentsAfterUpdate["user.unrelatedSuffix"]);
            Assert.Contains("# Preexisting user dxvk.conf for Adopted Game", confContentAfterUpdate);
        }

        // Missing baselines for adopted DLLs remain intact across update
        if (api == GraphicsApi.DX9)
        {
            var d3d9Rec2 = inst2Updated.FindManagedFile("d3d9.dll");
            Assert.NotNull(d3d9Rec2);
            Assert.Equal(FileOriginalState.Missing, d3d9Rec2!.OriginalState);
            Assert.Null(d3d9Rec2.BackupRelativePath);
            Assert.Equal("v2.6", d3d9Rec2.ManagedDxvkVersion);
            Assert.Equal(FileIdentity.Capture(d3d9Path!).Sha256, d3d9Rec2.ExpectedManagedSha256);
        }
        else
        {
            var d3d11Rec2 = inst2Updated.FindManagedFile("d3d11.dll");
            var dxgiRec2 = inst2Updated.FindManagedFile("dxgi.dll");
            Assert.NotNull(d3d11Rec2);
            Assert.NotNull(dxgiRec2);
            Assert.Equal(FileOriginalState.Missing, d3d11Rec2!.OriginalState);
            Assert.Null(d3d11Rec2.BackupRelativePath);
            Assert.Equal("v2.6", d3d11Rec2.ManagedDxvkVersion);
            Assert.Equal(FileIdentity.Capture(d3d11Path!).Sha256, d3d11Rec2.ExpectedManagedSha256);

            Assert.Equal(FileOriginalState.Missing, dxgiRec2!.OriginalState);
            Assert.Null(dxgiRec2.BackupRelativePath);
            Assert.Equal("v2.6", dxgiRec2.ManagedDxvkVersion);
            Assert.Equal(FileIdentity.Capture(dxgiPath!).Sha256, dxgiRec2.ExpectedManagedSha256);
        }

        // Config ownership and backup truthfully reflect pre-update state
        var confRecord2 = inst2Updated.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord2);
        Assert.Null(confRecord2!.ManagedDxvkVersion);
        Assert.Equal(FileIdentity.Capture(confPath).Sha256, confRecord2.ExpectedManagedSha256);

        if (preexistingConfig)
        {
            Assert.Equal(FileOriginalState.Existing, confRecord2.OriginalState);
            Assert.Equal(originalConfIdentity!.Sha256, confRecord2.OriginalSha256);
            Assert.Equal(Path.Combine(installationId, "dxvk.conf"), confRecord2.BackupRelativePath);
            string confBackupPath = Path.Combine(backupsDir, confRecord2.BackupRelativePath!);
            Assert.True(File.Exists(confBackupPath));
            Assert.Equal(originalConf, File.ReadAllBytes(confBackupPath));
        }
        else
        {
            Assert.Equal(FileOriginalState.Missing, confRecord2.OriginalState);
            Assert.Null(confRecord2.OriginalSha256);
            Assert.Null(confRecord2.BackupRelativePath);
            Assert.Empty(SnapshotResources(backupsDir).Files);
        }

        AssertResourcesUnchanged(acquiredSourcesSnapshot, sourcesDir);

        // 4. Reconstruct from disk, disable HUD/frame-limit, and explicitly Reapply
        var stack3 = ConstructServiceStack(appDataDir, httpClient);

        var profile3 = stack3.ProfileStore.GetOrCreate(exePath);
        profile3.HudEnabled = false;
        profile3.FrameLimit = 0;
        stack3.ProfileStore.Save(profile3);

        var reapplyResult = await stack3.Manager.RequestReapplyByPathAsync(profile3);
        Assert.Equal(DxvkActionResult.Applied, reapplyResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Config file presence and ownership unconditionally asserted
        Assert.True(File.Exists(confPath));
        string confContent3 = File.ReadAllText(confPath);
        var assignments3 = ParseConfigAssignments(confContent3);
        Assert.False(assignments3.ContainsKey("dxvk.hud"));
        Assert.False(assignments3.ContainsKey("dxvk.maxFrameRate"));

        if (preexistingConfig)
        {
            Assert.Equal("full", assignments3["dxvk.hudExtra"]);
            Assert.Equal("120", assignments3["dxvk.maxFrameRateTarget"]);
            Assert.Equal("1", assignments3["user.customSetting"]);
            Assert.Equal("keep", assignments3["user.unrelatedSuffix"]);
            Assert.Contains("# Preexisting user dxvk.conf for Adopted Game", confContent3);
        }

        var inst3Reapplied = stack3.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst3Reapplied);
        Assert.False(inst3Reapplied!.Configuration.HudEnabled);
        Assert.False(inst3Reapplied.Configuration.FrameLimitEnabled);
        Assert.Equal("v2.6", inst3Reapplied.ManagedDxvkVersion);

        var confRecord3 = inst3Reapplied.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord3);
        Assert.Equal(FileIdentity.Capture(confPath).Sha256, confRecord3!.ExpectedManagedSha256);

        // Wrapper bytes and owned DLL records still match expected newer release after reapply
        if (api == GraphicsApi.DX9)
        {
            Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:d3d9"), File.ReadAllBytes(d3d9Path!));
            var d3d9Rec3 = inst3Reapplied.FindManagedFile("d3d9.dll");
            Assert.NotNull(d3d9Rec3);
            Assert.Equal("v2.6", d3d9Rec3!.ManagedDxvkVersion);
            Assert.Equal(FileOriginalState.Missing, d3d9Rec3.OriginalState);
            Assert.Null(d3d9Rec3.BackupRelativePath);
            Assert.Equal(FileIdentity.Capture(d3d9Path!).Sha256, d3d9Rec3.ExpectedManagedSha256);
        }
        else
        {
            Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:d3d11"), File.ReadAllBytes(d3d11Path!));
            Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:dxgi"), File.ReadAllBytes(dxgiPath!));
            var d3d11Rec3 = inst3Reapplied.FindManagedFile("d3d11.dll");
            var dxgiRec3 = inst3Reapplied.FindManagedFile("dxgi.dll");
            Assert.NotNull(d3d11Rec3);
            Assert.NotNull(dxgiRec3);
            Assert.Equal("v2.6", d3d11Rec3!.ManagedDxvkVersion);
            Assert.Equal(FileOriginalState.Missing, d3d11Rec3.OriginalState);
            Assert.Null(d3d11Rec3.BackupRelativePath);
            Assert.Equal(FileIdentity.Capture(d3d11Path!).Sha256, d3d11Rec3.ExpectedManagedSha256);

            Assert.Equal("v2.6", dxgiRec3!.ManagedDxvkVersion);
            Assert.Equal(FileOriginalState.Missing, dxgiRec3.OriginalState);
            Assert.Null(dxgiRec3.BackupRelativePath);
            Assert.Equal(FileIdentity.Capture(dxgiPath!).Sha256, dxgiRec3.ExpectedManagedSha256);
        }

        // Original config backup retention verified after reapply
        if (preexistingConfig)
        {
            string confBackup = Path.Combine(backupsDir, confRecord3.BackupRelativePath!);
            Assert.True(File.Exists(confBackup));
            Assert.Equal(originalConf, File.ReadAllBytes(confBackup));
        }
        else
        {
            Assert.Empty(SnapshotResources(backupsDir).Files);
        }

        AssertResourcesUnchanged(acquiredSourcesSnapshot, sourcesDir);

        // Demonstrate that an attempt to AdoptExisting again after owned update is refused and causes no mutation
        var gameBeforeSecondAdopt = SnapshotResources(gameDir);
        var sourcesBeforeSecondAdopt = SnapshotResources(sourcesDir);
        var backupsBeforeSecondAdopt = SnapshotResources(backupsDir);

        string savedManagedVersion = inst3Reapplied.ManagedDxvkVersion!;
        RestorationState savedRestorationState = inst3Reapplied.RestorationState;
        InstallationConflictFlags savedConflictFlags = inst3Reapplied.ConflictFlags;
        var savedManagedFiles = inst3Reapplied.ManagedFiles
            .Select(mf => (mf.RelativePath, mf.OriginalState, mf.BackupRelativePath, mf.OriginalSha256, mf.ExpectedManagedSha256, mf.ManagedDxvkVersion, mf.CurrentState))
            .ToList();

        bool secondAdoptResult = await stack3.Manager.AdoptExistingAsync(profile3);
        Assert.False(secondAdoptResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);
        Assert.NotNull(stack3.Manager.LastRefusalReason);
        Assert.Contains("Use reapply instead of adoption", stack3.Manager.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Reconstruct from disk and verify complete resource and ownership integrity after refused second adopt
        var stack3AfterSecondAdopt = ConstructServiceStack(appDataDir, httpClient);
        AssertResourcesUnchanged(gameBeforeSecondAdopt, gameDir);
        AssertResourcesUnchanged(sourcesBeforeSecondAdopt, sourcesDir);
        AssertResourcesUnchanged(backupsBeforeSecondAdopt, backupsDir);

        var instAfterSecondAdopt = stack3AfterSecondAdopt.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(instAfterSecondAdopt);
        Assert.Equal(savedManagedVersion, instAfterSecondAdopt!.ManagedDxvkVersion);
        Assert.Equal(savedRestorationState, instAfterSecondAdopt.RestorationState);
        Assert.Equal(savedConflictFlags, instAfterSecondAdopt.ConflictFlags);
        Assert.Null(instAfterSecondAdopt.PendingAction);
        Assert.Equal(savedManagedFiles.Count, instAfterSecondAdopt.ManagedFiles.Count);

        foreach (var savedMf in savedManagedFiles)
        {
            var mf = instAfterSecondAdopt.FindManagedFile(savedMf.RelativePath);
            Assert.NotNull(mf);
            Assert.Equal(savedMf.OriginalState, mf!.OriginalState);
            Assert.Equal(savedMf.BackupRelativePath, mf.BackupRelativePath);
            Assert.Equal(savedMf.OriginalSha256, mf.OriginalSha256);
            Assert.Equal(savedMf.ExpectedManagedSha256, mf.ExpectedManagedSha256);
            Assert.Equal(savedMf.ManagedDxvkVersion, mf.ManagedDxvkVersion);
            Assert.Equal(savedMf.CurrentState, mf.CurrentState);
        }

        var profileAfterSecondAdopt = stack3AfterSecondAdopt.ProfileStore.GetOrCreate(exePath);
        Assert.True(profileAfterSecondAdopt.DxvkEnabled);
        Assert.Equal("v2.6", profileAfterSecondAdopt.DxvkVersion);
        Assert.False(profileAfterSecondAdopt.HudEnabled);
        Assert.Equal(0, profileAfterSecondAdopt.FrameLimit);

        // 5. Reconstruct from disk and Disable normally
        var stack4 = ConstructServiceStack(appDataDir, httpClient);

        var profile4 = stack4.ProfileStore.GetOrCreate(exePath);
        var disableResult = await stack4.Manager.RequestDisableByPathAsync(profile4);
        Assert.Equal(DxvkActionResult.Applied, disableResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Expected final game set is initialGameSnapshot MINUS adopted wrapper DLLs
        var expectedFiles = new Dictionary<string, byte[]>(initialGameSnapshot.Files, StringComparer.OrdinalIgnoreCase);
        if (api == GraphicsApi.DX9)
        {
            expectedFiles.Remove("d3d9.dll");
        }
        else
        {
            expectedFiles.Remove("d3d11.dll");
            expectedFiles.Remove("dxgi.dll");
        }
        var expectedFinalSnapshot = new ResourceSnapshot(expectedFiles, initialGameSnapshot.Directories);
        AssertResourcesUnchanged(expectedFinalSnapshot, gameDir);

        if (api == GraphicsApi.DX9)
        {
            Assert.False(File.Exists(d3d9Path!));
        }
        else
        {
            Assert.False(File.Exists(d3d11Path!));
            Assert.False(File.Exists(dxgiPath!));
        }

        Assert.True(File.Exists(exePath));
        Assert.Equal(exeBytes, File.ReadAllBytes(exePath));
        Assert.True(File.Exists(sentinelPath));
        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
        Assert.True(Directory.Exists(emptySubDir));

        if (preexistingConfig)
        {
            Assert.True(File.Exists(confPath));
            Assert.Equal(originalConf, File.ReadAllBytes(confPath));
            Assert.Equal(originalConfIdentity, FileIdentity.Capture(confPath));
        }
        else
        {
            Assert.False(File.Exists(confPath));
        }

        Assert.False(profile4.DxvkEnabled);
        Assert.Equal("v2.6", profile4.DxvkVersion);

        var inst4Restored = stack4.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst4Restored);
        Assert.Null(inst4Restored!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Restored, inst4Restored.RestorationState);
        Assert.Equal(InstallationConflictFlags.None, inst4Restored.ConflictFlags);
        Assert.Null(inst4Restored.PendingAction);
        Assert.Null(inst4Restored.LastRefusalReason);
        Assert.Equal(ManagementMode.UseGlobal, inst4Restored.ManagementPolicy.Mode);
        Assert.Equal("v2.6", inst4Restored.ManagementPolicy.PinnedDxvkVersion);

        var recExe = inst4Restored.FindExecutable("Game.exe");
        Assert.NotNull(recExe);
        Assert.Equal(api, recExe!.LastKnownApi);
        Assert.Equal(architecture, recExe.LastKnownArchitecture);

        // Require exact retained managed-file set (adopted DLLs plus config) and verify cleared active versions & retained history
        var expectedManagedFilePaths = api == GraphicsApi.DX9
            ? new[] { "d3d9.dll", "dxvk.conf" }
            : new[] { "d3d11.dll", "dxgi.dll", "dxvk.conf" };
        Assert.Equal(
            expectedManagedFilePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
            inst4Restored.ManagedFiles.Select(mf => mf.RelativePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

        foreach (var mf in inst4Restored.ManagedFiles)
        {
            Assert.Null(mf.ManagedDxvkVersion);
            if (string.Equals(mf.RelativePath, "dxvk.conf", StringComparison.OrdinalIgnoreCase))
            {
                if (preexistingConfig)
                {
                    Assert.Equal(FileOriginalState.Existing, mf.OriginalState);
                    Assert.Equal(originalConfIdentity!.Sha256, mf.OriginalSha256);
                    Assert.Equal(Path.Combine(installationId, "dxvk.conf"), mf.BackupRelativePath);
                }
                else
                {
                    Assert.Equal(FileOriginalState.Missing, mf.OriginalState);
                    Assert.Null(mf.OriginalSha256);
                    Assert.Null(mf.BackupRelativePath);
                }
            }
            else
            {
                // Adopted DLL records truthfully retain Missing baseline and null backup path
                Assert.Equal(FileOriginalState.Missing, mf.OriginalState);
                Assert.Null(mf.BackupRelativePath);
                Assert.Null(mf.OriginalSha256);
            }
        }

        // Compare complete retained backup set
        var finalBackupsSnapshot = SnapshotResources(backupsDir);
        if (preexistingConfig)
        {
            Assert.Single(finalBackupsSnapshot.Files);
            string expectedBackupRelPath = Path.Combine(installationId, "dxvk.conf");
            Assert.True(finalBackupsSnapshot.Files.ContainsKey(expectedBackupRelPath));
            Assert.Equal(originalConf, finalBackupsSnapshot.Files[expectedBackupRelPath]);
        }
        else
        {
            Assert.Empty(finalBackupsSnapshot.Files);
        }

        AssertResourcesUnchanged(acquiredSourcesSnapshot, sourcesDir);
    }

    [Theory]
    [InlineData(GraphicsApi.DX11, "x64", "MixedVersions")]
    [InlineData(GraphicsApi.DX11, "x32", "MixedVersions")]
    [InlineData(GraphicsApi.DX11, "x64", "InvalidManifest")]
    [InlineData(GraphicsApi.DX11, "x32", "InvalidManifest")]
    [InlineData(GraphicsApi.DX9,  "x64", "InvalidManifest")]
    [InlineData(GraphicsApi.DX9,  "x32", "InvalidManifest")]
    public async Task AdoptedDxvkLifecycle_RefusesInvalidWrappersOrSourcesBeforeAdoption(
        GraphicsApi api,
        string architecture,
        string refusalType)
    {
        using var testDir = new SyntheticTestDirectory();
        string gameDir = Path.Combine(testDir.RootPath, $"RefusalGame_{api}_{architecture}_{refusalType}");
        Directory.CreateDirectory(gameDir);

        string emptySubDir = Path.Combine(gameDir, "Data", "EmptySubDir");
        Directory.CreateDirectory(emptySubDir);

        string exePath = Path.Combine(gameDir, "Game.exe");
        byte[] exeBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 };
        File.WriteAllBytes(exePath, exeBytes);

        string sentinelPath = Path.Combine(gameDir, "sentinel.bin");
        byte[] sentinelBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };
        File.WriteAllBytes(sentinelPath, sentinelBytes);

        string confPath = Path.Combine(gameDir, "dxvk.conf");
        File.WriteAllBytes(confPath, Encoding.UTF8.GetBytes("# user config\nuser.setting = 1\n"));

        // Setup matching v2.5 wrappers in game directory initially (coherent hash-matching wrappers)
        if (api == GraphicsApi.DX9)
        {
            File.WriteAllBytes(Path.Combine(gameDir, "d3d9.dll"), Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d9"));
        }
        else
        {
            File.WriteAllBytes(Path.Combine(gameDir, "d3d11.dll"), Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d11"));
            File.WriteAllBytes(Path.Combine(gameDir, "dxgi.dll"), Encoding.UTF8.GetBytes($"v2.5:{architecture}:dxgi"));
        }

        string appDataDir = Path.Combine(testDir.RootPath, $"AppData_Refusal_{api}_{architecture}_{refusalType}");
        string profilesDir = Path.Combine(appDataDir, "Profiles");
        string backupsDir = Path.Combine(profilesDir, "Backups");
        string sourcesDir = Path.Combine(appDataDir, "Sources");

        var transport = new LifecycleHttpTransport("v2.5", "v2.6");
        using var httpClient = new HttpClient(transport);

        var stack1 = ConstructServiceStack(appDataDir, httpClient);

        // 1. Acquire official releases and explicitly verify manifests, markers, and expected payloads
        var acq25 = await stack1.Manager.AcquireOfficialReleaseAsync("v2.5");
        Assert.Equal("v2.5", acq25.Version);

        var acq26 = await stack1.Manager.AcquireOfficialReleaseAsync("v2.6");
        Assert.Equal("v2.6", acq26.Version);

        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", "companion-release.json")));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(sourcesDir, "v2.5"), "v2.5", out bool v25ManifestVerified));
        Assert.True(v25ManifestVerified);

        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", "companion-release.json")));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(sourcesDir, "v2.6"), "v2.6", out bool v26ManifestVerified));
        Assert.True(v26ManifestVerified);

        var profile = stack1.ProfileStore.GetOrCreate(exePath);
        profile.Api = api;
        profile.Architecture = architecture;
        profile.DxvkVersion = "v2.5";
        stack1.ProfileStore.Save(profile);

        // Positive control check: assert valid official matching wrappers are eligible before fault induction
        var controlAssessment = stack1.Manager.AssessExistingDxvk(profile);
        Assert.True(controlAssessment.CanBeAdopted);
        Assert.Equal(ExistingDxvkStatus.OfficialRelease, controlAssessment.Status);
        Assert.Equal("v2.5", controlAssessment.MatchedVersion);

        // 2. Induce the specific fault
        if (refusalType == "InvalidManifest")
        {
            // Invalidate manifest metadata while keeping payload DLLs intact
            File.WriteAllText(Path.Combine(sourcesDir, "v2.5", DxvkReleaseAcquirer.ManifestName), "{}");

            // Raw hash-only recognizer still sees matching official hashes
            var rawAssessment = new ExistingDxvkDetector(sourcesDir, profile.Api).AssessDirectory(gameDir, architecture);
            Assert.True(rawAssessment.CanBeAdopted);
            Assert.Equal(ExistingDxvkStatus.OfficialRelease, rawAssessment.Status);
        }
        else if (refusalType == "MixedVersions")
        {
            // DX11 pair with mismatched versions: d3d11 is v2.5, dxgi is v2.6
            File.WriteAllBytes(Path.Combine(gameDir, "dxgi.dll"), Encoding.UTF8.GetBytes($"v2.6:{architecture}:dxgi"));
        }

        var originalGameSnapshot = SnapshotResources(gameDir);
        var sourcesSnapshot = SnapshotResources(sourcesDir);
        var backupsSnapshot = SnapshotResources(backupsDir);

        transport.RejectAllRequests = true;
        int requestCountBefore = transport.RequestCount;

        // 3. Reconstruct fresh service stack from disk to exercise real reload boundary
        var stack2 = ConstructServiceStack(appDataDir, httpClient);

        // Assessment should refuse adoption with meaningful evidence
        var assessment = stack2.Manager.AssessExistingDxvk(profile);
        Assert.False(assessment.CanBeAdopted);
        Assert.Equal(ExistingDxvkStatus.RecognizedUnknownVersion, assessment.Status);

        if (refusalType == "InvalidManifest")
        {
            Assert.Contains(assessment.Evidence, e => e.Contains("Local acquired source verification failed", StringComparison.OrdinalIgnoreCase));
        }
        else if (refusalType == "MixedVersions")
        {
            Assert.Contains(assessment.Evidence, e => e.Contains("matches official release v2.5", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(assessment.Evidence, e => e.Contains("matches official release v2.6", StringComparison.OrdinalIgnoreCase));
        }

        // Attempt adoption through real manager
        bool adoptResult = await stack2.Manager.AdoptExistingAsync(profile);
        Assert.False(adoptResult);
        Assert.Equal(requestCountBefore, transport.RequestCount);

        Assert.NotNull(stack2.Manager.LastRefusalReason);
        Assert.Contains("cannot be adopted", stack2.Manager.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // 4. Reload again from disk to inspect durable refusal state and confirm zero mutation
        var stack3 = ConstructServiceStack(appDataDir, httpClient);

        AssertResourcesUnchanged(originalGameSnapshot, gameDir);
        AssertResourcesUnchanged(sourcesSnapshot, sourcesDir);
        AssertResourcesUnchanged(backupsSnapshot, backupsDir);

        // Profile remains unmanaged
        var profileAfter = stack3.ProfileStore.GetOrCreate(exePath);
        Assert.False(profileAfter.DxvkEnabled);

        var instAfter = stack3.LibraryStore.FindByInstallationPath(gameDir);
        Assert.True(instAfter == null || instAfter.RestorationState != RestorationState.Managed);
        Assert.True(instAfter == null || instAfter.ManagedFiles.Count == 0);
    }
}
