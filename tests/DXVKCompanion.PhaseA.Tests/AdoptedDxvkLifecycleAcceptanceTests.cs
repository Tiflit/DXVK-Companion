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

        // Missing baselines for adopted DLLs remain intact across update
        if (api == GraphicsApi.DX9)
        {
            var d3d9Rec2 = inst2Updated.FindManagedFile("d3d9.dll");
            Assert.NotNull(d3d9Rec2);
            Assert.Equal(FileOriginalState.Missing, d3d9Rec2!.OriginalState);
            Assert.Null(d3d9Rec2.BackupRelativePath);
            Assert.Equal("v2.6", d3d9Rec2.ManagedDxvkVersion);
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
            Assert.Equal(FileOriginalState.Missing, dxgiRec2!.OriginalState);
            Assert.Null(dxgiRec2.BackupRelativePath);
            Assert.Equal("v2.6", dxgiRec2.ManagedDxvkVersion);
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

        AssertResourcesUnchanged(acquiredSourcesSnapshot, sourcesDir);

        // Demonstrate that an attempt to AdoptExisting again after owned update is refused
        bool secondAdoptResult = await stack3.Manager.AdoptExistingAsync(profile3);
        Assert.False(secondAdoptResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);
        Assert.NotNull(stack3.Manager.LastRefusalReason);
        Assert.Contains("Use reapply instead of adoption", stack3.Manager.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Refused adoption attempt did not reset baseline/ownership or mutate game files
        var instAfterSecondAdopt = stack3.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(instAfterSecondAdopt);
        Assert.Equal(RestorationState.Managed, instAfterSecondAdopt!.RestorationState);
        Assert.Equal("v2.6", instAfterSecondAdopt.ManagedDxvkVersion);

        // 5. Reconstruct from disk and Disable normally
        var stack4 = ConstructServiceStack(appDataDir, httpClient);

        var profile4 = stack4.ProfileStore.GetOrCreate(exePath);
        var disableResult = await stack4.Manager.RequestDisableByPathAsync(profile4);
        Assert.Equal(DxvkActionResult.Applied, disableResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Expected final game set is initial set MINUS adopted wrapper DLLs
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

        foreach (var mf in inst4Restored.ManagedFiles)
        {
            Assert.Null(mf.ManagedDxvkVersion);
        }

        AssertResourcesUnchanged(acquiredSourcesSnapshot, sourcesDir);

        if (preexistingConfig)
        {
            string confBackup = Path.Combine(backupsDir, confRecord2.BackupRelativePath!);
            Assert.True(File.Exists(confBackup));
            Assert.Equal(originalConf, File.ReadAllBytes(confBackup));
        }
    }

    [Theory]
    [InlineData(GraphicsApi.DX11, "x64", "MixedVersions")]
    [InlineData(GraphicsApi.DX11, "x32", "MixedVersions")]
    [InlineData(GraphicsApi.DX11, "x64", "CorruptSource")]
    [InlineData(GraphicsApi.DX9, "x64", "CorruptSource")]
    [InlineData(GraphicsApi.DX9, "x32", "CorruptSource")]
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

        // Setup wrapper files in game directory according to refusalType
        if (refusalType == "MixedVersions")
        {
            // DX11 pair with mismatched versions: d3d11 is v2.5, dxgi is v2.6
            File.WriteAllBytes(Path.Combine(gameDir, "d3d11.dll"), Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d11"));
            File.WriteAllBytes(Path.Combine(gameDir, "dxgi.dll"), Encoding.UTF8.GetBytes($"v2.6:{architecture}:dxgi"));
        }
        else if (refusalType == "CorruptSource")
        {
            // Matching wrappers in game directory for v2.5
            if (api == GraphicsApi.DX9)
            {
                File.WriteAllBytes(Path.Combine(gameDir, "d3d9.dll"), Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d9"));
            }
            else
            {
                File.WriteAllBytes(Path.Combine(gameDir, "d3d11.dll"), Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d11"));
                File.WriteAllBytes(Path.Combine(gameDir, "dxgi.dll"), Encoding.UTF8.GetBytes($"v2.5:{architecture}:dxgi"));
            }
        }

        var originalGameSnapshot = SnapshotResources(gameDir);

        string appDataDir = Path.Combine(testDir.RootPath, $"AppData_Refusal_{api}_{architecture}_{refusalType}");
        string profilesDir = Path.Combine(appDataDir, "Profiles");
        string backupsDir = Path.Combine(profilesDir, "Backups");
        string sourcesDir = Path.Combine(appDataDir, "Sources");

        var transport = new LifecycleHttpTransport("v2.5", "v2.6");
        using var httpClient = new HttpClient(transport);

        var stack = ConstructServiceStack(appDataDir, httpClient);

        // Acquire official releases
        var acq25 = await stack.Manager.AcquireOfficialReleaseAsync("v2.5");
        Assert.Equal("v2.5", acq25.Version);

        var acq26 = await stack.Manager.AcquireOfficialReleaseAsync("v2.6");
        Assert.Equal("v2.6", acq26.Version);

        // If CorruptSource, invalidate the acquired source manifest verification
        if (refusalType == "CorruptSource")
        {
            string targetDll = api == GraphicsApi.DX9 ? "d3d9.dll" : "d3d11.dll";
            string sourcePayloadPath = Path.Combine(sourcesDir, "v2.5", architecture, targetDll);
            File.WriteAllBytes(sourcePayloadPath, Encoding.UTF8.GetBytes("corrupted-source-payload-bytes"));
        }

        var sourcesSnapshot = SnapshotResources(sourcesDir);
        var backupsSnapshot = SnapshotResources(backupsDir);

        transport.RejectAllRequests = true;
        int requestCountBefore = transport.RequestCount;

        var profile = stack.ProfileStore.GetOrCreate(exePath);
        profile.Api = api;
        profile.Architecture = architecture;
        profile.DxvkVersion = "v2.5";
        stack.ProfileStore.Save(profile);

        // Assessment should recognize invalid/unsupported state
        var assessment = stack.Manager.AssessExistingDxvk(profile);
        Assert.False(assessment.CanBeAdopted);

        // Attempt adoption through real manager after fresh stack setup
        bool adoptResult = await stack.Manager.AdoptExistingAsync(profile);
        Assert.False(adoptResult);
        Assert.Equal(requestCountBefore, transport.RequestCount);

        Assert.NotNull(stack.Manager.LastRefusalReason);
        Assert.Contains("cannot be adopted", stack.Manager.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Game files, available sources, and backups remain unchanged
        AssertResourcesUnchanged(originalGameSnapshot, gameDir);
        AssertResourcesUnchanged(sourcesSnapshot, sourcesDir);
        AssertResourcesUnchanged(backupsSnapshot, backupsDir);

        // Profile remains unmanaged
        var profileAfter = stack.ProfileStore.GetOrCreate(exePath);
        Assert.False(profileAfter.DxvkEnabled);

        var instAfter = stack.LibraryStore.FindByInstallationPath(gameDir);
        Assert.True(instAfter == null || instAfter.RestorationState != RestorationState.Managed);
        Assert.True(instAfter == null || instAfter.ManagedFiles.Count == 0);
    }
}
