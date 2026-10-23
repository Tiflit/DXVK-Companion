using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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

public sealed class PortableManagedLifecycleAcceptanceTests
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
                _archives[tag] = OfficialAcquisitionFixture.Archive(tag);
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task PortableManagedLifecycle_RelocatedBundle_PreservesBaselinesAndRestoresByteIdentical(bool preexistingNativeBaseline)
    {
        using var testDir = new SyntheticTestDirectory();
        string gameDir = Path.Combine(testDir.RootPath, "SyntheticGame");
        Directory.CreateDirectory(gameDir);

        // Meaningful empty subdirectory preserved across lifecycle
        string emptySubDir = Path.Combine(gameDir, "Data", "EmptySubDir");
        Directory.CreateDirectory(emptySubDir);

        string exePath = Path.Combine(gameDir, "Game.exe");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 });
        string d3d11Path = Path.Combine(gameDir, "d3d11.dll");
        string confPath = Path.Combine(gameDir, "dxvk.conf");
        string dxgiPath = Path.Combine(gameDir, "dxgi.dll");

        byte[]? originalD3D11 = null;
        byte[]? originalConf = null;
        SafetyFileIdentity? originalD3D11Identity = null;
        SafetyFileIdentity? originalConfIdentity = null;

        if (preexistingNativeBaseline)
        {
            originalD3D11 = Encoding.UTF8.GetBytes("native-d3d11-original-v1.0-bytes");
            File.WriteAllBytes(d3d11Path, originalD3D11);
            originalD3D11Identity = FileIdentity.Capture(d3d11Path);

            originalConf = Encoding.UTF8.GetBytes(
                "# Custom preexisting dxvk.conf for Game\n" +
                "# Distinctive user comments\n" +
                "user.customSetting = 1\n" +
                "dxvk.hud = compiler\n" +
                "dxvk.maxFrameRate = 30\n" +
                "dxvk.hudExtra = full\n" +
                "dxvk.maxFrameRateTarget = 120\n" +
                "user.unrelatedSuffix = keep\n");
            File.WriteAllBytes(confPath, originalConf);
            originalConfIdentity = FileIdentity.Capture(confPath);
        }

        Assert.False(File.Exists(dxgiPath));
        if (!preexistingNativeBaseline)
        {
            Assert.False(File.Exists(d3d11Path));
            Assert.False(File.Exists(confPath));
        }

        var originalGameSnapshot = SnapshotResources(gameDir);

        string appDataDir = Path.Combine(testDir.RootPath, "AppDataBundle");
        string profilesDir = Path.Combine(appDataDir, "Profiles");
        string backupsDir = Path.Combine(profilesDir, "Backups");
        string sourcesDir = Path.Combine(appDataDir, "Sources");

        var transport = new LifecycleHttpTransport("v2.5", "v2.6");
        using var httpClient = new HttpClient(transport);

        // 1. Acquire two distinguishable official releases into shared source library
        var stack1 = ConstructServiceStack(appDataDir, httpClient);

        // Persist nondefault SettingsStore and CacheStore records before relocation
        string settingsFile1 = Path.Combine(appDataDir, "settings.json");
        var settings1 = SettingsStore.Load(settingsFile1);
        settings1.GlobalPolicy = GlobalManagementPolicy.Automated;
        settings1.Save();

        stack1.CacheStore.SaveCachedRelease(
            new ReleaseInfo { Version = "v2.5", TagName = "v2.5" },
            etag: "\"cached-etag-v2.5\"",
            cachedAt: DateTime.UtcNow,
            lastAttemptAt: DateTime.UtcNow);

        var acq25 = await stack1.Manager.AcquireOfficialReleaseAsync("v2.5");
        Assert.Equal("v2.5", acq25.Version);
        Assert.True(acq25.UpstreamDigestVerified);

        var acq26 = await stack1.Manager.AcquireOfficialReleaseAsync("v2.6");
        Assert.Equal("v2.6", acq26.Version);
        Assert.True(acq26.UpstreamDigestVerified);

        var acquiredSourcesSnapshot = SnapshotResources(sourcesDir);
        Assert.NotEmpty(acquiredSourcesSnapshot.Files);
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", "companion-release.json")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", "x64", "d3d11.dll")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", "x64", "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", "companion-release.json")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", "x64", "d3d11.dll")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", "x64", "dxgi.dll")));

        // 2. Install exact older release into DX11/x64 synthetic game with HUD and frame limit enabled
        var profile1 = stack1.ProfileStore.GetOrCreate(exePath);
        profile1.Api = GraphicsApi.DX11;
        profile1.Architecture = "x64";
        profile1.HudEnabled = true;
        profile1.FrameLimit = 60;
        profile1.DxvkVersion = "v2.5";
        stack1.ProfileStore.Save(profile1);

        var installResult = await stack1.Manager.RequestEnableByPathAsync(profile1, "v2.5");
        Assert.Equal(DxvkActionResult.Applied, installResult);

        Assert.Equal("v2.5:d3d11", File.ReadAllText(d3d11Path));
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal("v2.5:dxgi", File.ReadAllText(dxgiPath));
        Assert.True(File.Exists(confPath));

        var inst1 = stack1.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst1);
        Assert.Equal("v2.5", inst1!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, inst1.RestorationState);
        Assert.True(inst1.Configuration.HudEnabled);
        Assert.Equal(60, inst1.Configuration.FrameLimit);
        Assert.True(inst1.Configuration.FrameLimitEnabled);

        string installationId = inst1.Id;

        // Check enabled config settings and ownership hash
        var confRecord1 = inst1.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord1);
        var initialAssignments = ParseConfigAssignments(File.ReadAllText(confPath));
        Assert.True(initialAssignments.ContainsKey("dxvk.hud"));
        Assert.Equal("fps,devinfo", initialAssignments["dxvk.hud"]);
        Assert.True(initialAssignments.ContainsKey("dxvk.maxFrameRate"));
        Assert.Equal("60", initialAssignments["dxvk.maxFrameRate"]);
        Assert.Equal(FileIdentity.Capture(confPath).Sha256, confRecord1!.ExpectedManagedSha256);

        var d3d11Record1 = inst1.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record1);
        Assert.Equal("v2.5", d3d11Record1!.ManagedDxvkVersion);
        Assert.Equal(FileIdentity.Capture(d3d11Path).Sha256, d3d11Record1.ExpectedManagedSha256);

        var dxgiRecord1 = inst1.FindManagedFile("dxgi.dll");
        Assert.NotNull(dxgiRecord1);
        Assert.Equal("v2.5", dxgiRecord1!.ManagedDxvkVersion);
        Assert.Equal(FileIdentity.Capture(dxgiPath).Sha256, dxgiRecord1.ExpectedManagedSha256);
        Assert.Equal(FileOriginalState.Missing, dxgiRecord1.OriginalState);
        Assert.Null(dxgiRecord1.OriginalSha256);
        Assert.Null(dxgiRecord1.BackupRelativePath);

        if (preexistingNativeBaseline)
        {
            Assert.Equal("full", initialAssignments["dxvk.hudExtra"]);
            Assert.Equal("120", initialAssignments["dxvk.maxFrameRateTarget"]);
            Assert.Equal(FileOriginalState.Existing, confRecord1.OriginalState);
            Assert.Equal(originalConfIdentity!.Sha256, confRecord1.OriginalSha256);
            Assert.Equal(Path.Combine(installationId, "dxvk.conf"), confRecord1.BackupRelativePath);
            string confBackup1 = Path.Combine(backupsDir, confRecord1.BackupRelativePath!);
            Assert.True(File.Exists(confBackup1));
            Assert.Equal(originalConf, File.ReadAllBytes(confBackup1));

            Assert.Equal(FileOriginalState.Existing, d3d11Record1.OriginalState);
            Assert.Equal(originalD3D11Identity!.Sha256, d3d11Record1.OriginalSha256);
            Assert.Equal(Path.Combine(installationId, "d3d11.dll"), d3d11Record1.BackupRelativePath);
            string d3d11Backup1 = Path.Combine(backupsDir, d3d11Record1.BackupRelativePath!);
            Assert.True(File.Exists(d3d11Backup1));
            Assert.Equal(originalD3D11, File.ReadAllBytes(d3d11Backup1));
        }
        else
        {
            Assert.Equal(FileOriginalState.Missing, confRecord1.OriginalState);
            Assert.Null(confRecord1.OriginalSha256);
            Assert.Null(confRecord1.BackupRelativePath);

            Assert.Equal(FileOriginalState.Missing, d3d11Record1.OriginalState);
            Assert.Null(d3d11Record1.OriginalSha256);
            Assert.Null(d3d11Record1.BackupRelativePath);
        }

        // Persist explicit version selection intent for subsequent update
        inst1.ManagementPolicy = ManagementPolicy.PinVersion("v2.6");
        stack1.LibraryStore.Save(inst1);

        var originalBackupsSnapshot = SnapshotResources(backupsDir);
        if (preexistingNativeBaseline)
        {
            Assert.NotEmpty(originalBackupsSnapshot.Files);
        }
        else
        {
            Assert.Empty(originalBackupsSnapshot.Files);
        }

        // 3. Relocate disposable app-data bundle to distinct path under test directory
        string relocatedAppDataDir = Path.Combine(testDir.RootPath, "RelocatedAppDataBundle");
        Directory.Move(appDataDir, relocatedAppDataDir);
        Assert.False(Directory.Exists(appDataDir));
        Assert.True(Directory.Exists(gameDir));

        string newProfilesDir = Path.Combine(relocatedAppDataDir, "Profiles");
        string newBackupsDir = Path.Combine(newProfilesDir, "Backups");
        string newSourcesDir = Path.Combine(relocatedAppDataDir, "Sources");

        // Reconstruct all services against relocated bundle paths
        var stack2 = ConstructServiceStack(relocatedAppDataDir, httpClient);

        // Verify persisted nondefault SettingsStore and CacheStore records
        var settings2 = SettingsStore.Load(Path.Combine(relocatedAppDataDir, "settings.json"));
        Assert.Equal(GlobalManagementPolicy.Automated, settings2.GlobalPolicy);

        var cached2 = stack2.CacheStore.LoadCachedRelease();
        Assert.NotNull(cached2);
        Assert.Equal("v2.5", cached2!.Release.Version);
        Assert.Equal("\"cached-etag-v2.5\"", cached2.ETag);

        Assert.False(Directory.Exists(appDataDir));

        var profile2 = stack2.ProfileStore.GetOrCreate(exePath);
        Assert.True(profile2.DxvkEnabled);
        Assert.Equal("v2.5", profile2.DxvkVersion);
        Assert.True(profile2.HudEnabled);
        Assert.Equal(60, profile2.FrameLimit);

        var inst2 = stack2.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst2);
        Assert.Equal(installationId, inst2!.Id);
        Assert.Equal("v2.5", inst2.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, inst2.RestorationState);
        Assert.Equal(InstallationConflictFlags.None, inst2.ConflictFlags);
        Assert.Null(inst2.PendingAction);
        Assert.Null(inst2.LastRefusalReason);
        Assert.Equal(ManagementMode.PinnedVersion, inst2.ManagementPolicy.Mode);
        Assert.Equal("v2.6", inst2.ManagementPolicy.PinnedDxvkVersion);

        AssertResourcesUnchanged(acquiredSourcesSnapshot, newSourcesDir);
        AssertResourcesUnchanged(originalBackupsSnapshot, newBackupsDir);
        Assert.False(Directory.Exists(appDataDir));

        // 4. Set fake transport unavailable after initial acquisition (assert zero additional HTTP calls)
        transport.RejectAllRequests = true;
        int requestCountAfterAcquisition = transport.RequestCount;

        // Update through manager using persisted selector intent (no targetVersion method override)
        var updateResult = await stack2.Manager.RequestUpdateByPathAsync(profile2);
        Assert.Equal(DxvkActionResult.Applied, updateResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        Assert.Equal("v2.6:d3d11", File.ReadAllText(d3d11Path));
        Assert.Equal("v2.6:dxgi", File.ReadAllText(dxgiPath));
        Assert.Equal("v2.6", profile2.DxvkVersion);

        var inst2Updated = stack2.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst2Updated);
        Assert.Equal("v2.6", inst2Updated!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, inst2Updated.RestorationState);

        var d3d11Record2 = inst2Updated.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record2);
        Assert.Equal("v2.6", d3d11Record2!.ManagedDxvkVersion);
        Assert.Equal(FileIdentity.Capture(d3d11Path).Sha256, d3d11Record2.ExpectedManagedSha256);

        var dxgiRecord2 = inst2Updated.FindManagedFile("dxgi.dll");
        Assert.NotNull(dxgiRecord2);
        Assert.Equal("v2.6", dxgiRecord2!.ManagedDxvkVersion);
        Assert.Equal(FileIdentity.Capture(dxgiPath).Sha256, dxgiRecord2.ExpectedManagedSha256);

        var confRecord2 = inst2Updated.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord2);
        Assert.Equal("v2.6", confRecord2!.ManagedDxvkVersion);

        if (preexistingNativeBaseline)
        {
            Assert.Equal(FileOriginalState.Existing, d3d11Record2.OriginalState);
            Assert.Equal(originalD3D11Identity!.Sha256, d3d11Record2.OriginalSha256);
            Assert.Equal(originalD3D11, File.ReadAllBytes(Path.Combine(newBackupsDir, d3d11Record2.BackupRelativePath!)));

            Assert.Equal(FileOriginalState.Existing, confRecord2.OriginalState);
            Assert.Equal(originalConfIdentity!.Sha256, confRecord2.OriginalSha256);
            Assert.Equal(originalConf, File.ReadAllBytes(Path.Combine(newBackupsDir, confRecord2.BackupRelativePath!)));
        }
        else
        {
            Assert.Equal(FileOriginalState.Missing, d3d11Record2.OriginalState);
            Assert.Null(d3d11Record2.OriginalSha256);
            Assert.Null(d3d11Record2.BackupRelativePath);

            Assert.Equal(FileOriginalState.Missing, confRecord2.OriginalState);
            Assert.Null(confRecord2.OriginalSha256);
            Assert.Null(confRecord2.BackupRelativePath);
        }

        AssertResourcesUnchanged(acquiredSourcesSnapshot, newSourcesDir);
        AssertResourcesUnchanged(originalBackupsSnapshot, newBackupsDir);
        Assert.False(Directory.Exists(appDataDir));

        // Restart/reload all services, disable the HUD/frame feature, and reapply
        var stack3 = ConstructServiceStack(relocatedAppDataDir, httpClient);

        var profile3 = stack3.ProfileStore.GetOrCreate(exePath);
        profile3.HudEnabled = false;
        profile3.FrameLimit = 0;
        stack3.ProfileStore.Save(profile3);

        var reapplyResult = await stack3.Manager.RequestReapplyByPathAsync(profile3);
        Assert.Equal(DxvkActionResult.Applied, reapplyResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        if (File.Exists(confPath))
        {
            string confContentAfterReapply = File.ReadAllText(confPath);
            var assignments = ParseConfigAssignments(confContentAfterReapply);
            Assert.False(assignments.ContainsKey("dxvk.hud"));
            Assert.False(assignments.ContainsKey("dxvk.maxFrameRate"));

            if (preexistingNativeBaseline)
            {
                Assert.True(assignments.ContainsKey("dxvk.hudExtra"));
                Assert.Equal("full", assignments["dxvk.hudExtra"]);
                Assert.True(assignments.ContainsKey("dxvk.maxFrameRateTarget"));
                Assert.Equal("120", assignments["dxvk.maxFrameRateTarget"]);
                Assert.True(assignments.ContainsKey("user.customSetting"));
                Assert.Equal("1", assignments["user.customSetting"]);
                Assert.True(assignments.ContainsKey("user.unrelatedSuffix"));
                Assert.Equal("keep", assignments["user.unrelatedSuffix"]);
                Assert.Contains("# Custom preexisting dxvk.conf for Game", confContentAfterReapply);
            }
        }

        Assert.Equal("v2.6:d3d11", File.ReadAllText(d3d11Path));
        Assert.Equal("v2.6:dxgi", File.ReadAllText(dxgiPath));

        var inst3Reapplied = stack3.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst3Reapplied);
        Assert.False(inst3Reapplied!.Configuration.HudEnabled);
        Assert.False(inst3Reapplied.Configuration.FrameLimitEnabled);
        Assert.Equal("v2.6", inst3Reapplied.ManagedDxvkVersion);

        var confRecord3 = inst3Reapplied.FindManagedFile("dxvk.conf");
        if (confRecord3 != null && File.Exists(confPath))
        {
            Assert.Equal(FileIdentity.Capture(confPath).Sha256, confRecord3.ExpectedManagedSha256);
        }

        AssertResourcesUnchanged(acquiredSourcesSnapshot, newSourcesDir);
        AssertResourcesUnchanged(originalBackupsSnapshot, newBackupsDir);
        Assert.False(Directory.Exists(appDataDir));

        // Reload all services and restore
        var stack4 = ConstructServiceStack(relocatedAppDataDir, httpClient);

        var profile4 = stack4.ProfileStore.GetOrCreate(exePath);
        var restoreResult = await stack4.Manager.RequestDisableByPathAsync(profile4);
        Assert.Equal(DxvkActionResult.Applied, restoreResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Final exact original raw resources, bytes, and empty directories restored
        AssertResourcesUnchanged(originalGameSnapshot, gameDir);
        if (preexistingNativeBaseline)
        {
            Assert.True(File.Exists(d3d11Path));
            Assert.Equal(originalD3D11, File.ReadAllBytes(d3d11Path));
            Assert.Equal(originalD3D11Identity, FileIdentity.Capture(d3d11Path));
            Assert.True(File.Exists(confPath));
            Assert.Equal(originalConf, File.ReadAllBytes(confPath));
            Assert.Equal(originalConfIdentity, FileIdentity.Capture(confPath));
            Assert.False(File.Exists(dxgiPath));
        }
        else
        {
            Assert.False(File.Exists(d3d11Path));
            Assert.False(File.Exists(confPath));
            Assert.False(File.Exists(dxgiPath));
        }

        var inst4Restored = stack4.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst4Restored);
        Assert.Null(inst4Restored!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Restored, inst4Restored.RestorationState);
        Assert.Equal(InstallationConflictFlags.None, inst4Restored.ConflictFlags);
        Assert.Null(inst4Restored.PendingAction);
        Assert.Null(inst4Restored.LastRefusalReason);
        Assert.False(profile4.DxvkEnabled);
        Assert.Equal("v2.6", profile4.DxvkVersion);

        foreach (var mf in inst4Restored.ManagedFiles)
        {
            Assert.Null(mf.ManagedDxvkVersion);
        }

        AssertResourcesUnchanged(acquiredSourcesSnapshot, newSourcesDir);
        AssertResourcesUnchanged(originalBackupsSnapshot, newBackupsDir);
        Assert.False(Directory.Exists(appDataDir));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PortableManagedLifecycle_RelocatedCorruptOrMissingBackup_RefusesRestoreWithoutPartialTargetWrites(bool missingBackup)
    {
        using var testDir = new SyntheticTestDirectory();
        string gameDir = Path.Combine(testDir.RootPath, "SyntheticGame");
        Directory.CreateDirectory(gameDir);

        string emptySubDir = Path.Combine(gameDir, "Data", "EmptySubDir");
        Directory.CreateDirectory(emptySubDir);

        string exePath = Path.Combine(gameDir, "Game.exe");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 });
        string d3d11Path = Path.Combine(gameDir, "d3d11.dll");
        byte[] originalD3D11 = Encoding.UTF8.GetBytes("native-d3d11-v1.0-bytes");
        File.WriteAllBytes(d3d11Path, originalD3D11);
        string confPath = Path.Combine(gameDir, "dxvk.conf");
        byte[] originalConf = Encoding.UTF8.GetBytes("# user config\ncustom_key = 1\ndxvk.hud = compiler\n");
        File.WriteAllBytes(confPath, originalConf);

        string appDataDir = Path.Combine(testDir.RootPath, "AppDataBundle");
        string profilesDir = Path.Combine(appDataDir, "Profiles");
        string backupsDir = Path.Combine(profilesDir, "Backups");
        string sourcesDir = Path.Combine(appDataDir, "Sources");

        var transport = new LifecycleHttpTransport("v2.5");
        using var httpClient = new HttpClient(transport);

        var stack1 = ConstructServiceStack(appDataDir, httpClient);

        var acq = await stack1.Manager.AcquireOfficialReleaseAsync("v2.5");
        Assert.Equal("v2.5", acq.Version);

        var profile1 = stack1.ProfileStore.GetOrCreate(exePath);
        profile1.Api = GraphicsApi.DX11;
        profile1.Architecture = "x64";
        profile1.HudEnabled = true;
        profile1.FrameLimit = 60;
        stack1.ProfileStore.Save(profile1);

        var installResult = await stack1.Manager.RequestEnableByPathAsync(profile1, "v2.5");
        Assert.Equal(DxvkActionResult.Applied, installResult);

        // Relocate bundle
        string relocatedAppDataDir = Path.Combine(testDir.RootPath, "RelocatedAppDataBundle");
        Directory.Move(appDataDir, relocatedAppDataDir);
        Assert.False(Directory.Exists(appDataDir));

        string newProfilesDir = Path.Combine(relocatedAppDataDir, "Profiles");
        string newBackupsDir = Path.Combine(newProfilesDir, "Backups");
        string newSourcesDir = Path.Combine(relocatedAppDataDir, "Sources");

        var stack2 = ConstructServiceStack(relocatedAppDataDir, httpClient);

        var inst2 = stack2.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst2);
        var record = inst2!.FindManagedFile("d3d11.dll");
        Assert.NotNull(record);
        string backupFile = Path.Combine(newBackupsDir, record!.BackupRelativePath!);
        Assert.True(File.Exists(backupFile));

        if (missingBackup)
        {
            File.Delete(backupFile);
            Assert.False(File.Exists(backupFile));
        }
        else
        {
            File.WriteAllBytes(backupFile, Encoding.UTF8.GetBytes("corrupted-native-d3d11-backup-bytes"));
        }

        var deployedGameSnapshot = SnapshotResources(gameDir);
        var sourcesSnapshot = SnapshotResources(newSourcesDir);
        var backupsSnapshot = SnapshotResources(newBackupsDir);

        var profile2 = stack2.ProfileStore.GetOrCreate(exePath);
        var restoreResult = await stack2.Manager.RequestDisableByPathAsync(profile2);
        Assert.Equal(DxvkActionResult.Failed, restoreResult);

        // Assert target bytes were not partially mutated
        AssertResourcesUnchanged(deployedGameSnapshot, gameDir);
        // Assert source and backup bytes were retained
        AssertResourcesUnchanged(sourcesSnapshot, newSourcesDir);
        AssertResourcesUnchanged(backupsSnapshot, newBackupsDir);

        // Truthful health/refusal/pending state
        var reloadedInst = stack2.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(reloadedInst);
        Assert.Equal(RestorationState.AttentionRequired, reloadedInst!.RestorationState);
        Assert.NotNull(reloadedInst.LastRefusalReason);
        if (missingBackup)
        {
            Assert.Contains("unavailable", reloadedInst.LastRefusalReason, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains("mismatch", reloadedInst.LastRefusalReason, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("v2.5", reloadedInst.ManagedDxvkVersion);
        Assert.True(profile2.DxvkEnabled);
        Assert.Null(reloadedInst.PendingAction);
        Assert.False(Directory.Exists(appDataDir));
    }
}
