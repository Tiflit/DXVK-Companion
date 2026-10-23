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
    private static Dictionary<string, byte[]> SnapshotFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertFilesUnchanged(Dictionary<string, byte[]> expected, string root)
    {
        var actual = SnapshotFiles(root);
        Assert.Equal(
            expected.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            actual.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        foreach (var (path, bytes) in expected)
        {
            Assert.Equal(bytes, actual[path]);
        }
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

    [Fact]
    public async Task PortableManagedLifecycle_RelocatedBundle_PreservesBaselinesAndRestoresByteIdentical()
    {
        using var testDir = new SyntheticTestDirectory();
        string gameDir = Path.Combine(testDir.RootPath, "SyntheticGame");
        Directory.CreateDirectory(gameDir);

        string exePath = Path.Combine(gameDir, "Game.exe");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 });
        string d3d11Path = Path.Combine(gameDir, "d3d11.dll");
        byte[] originalD3D11 = Encoding.UTF8.GetBytes("native-d3d11-original-v1.0-bytes");
        File.WriteAllBytes(d3d11Path, originalD3D11);
        string confPath = Path.Combine(gameDir, "dxvk.conf");
        byte[] originalConf = Encoding.UTF8.GetBytes("# Custom preexisting dxvk.conf for Game\n# Distinctive user comments\nuser.customSetting = 1\ndxvk.hud = compiler\ndxvk.maxFrameRate = 30\nuser.unrelatedSuffix = keep\n");
        File.WriteAllBytes(confPath, originalConf);
        string dxgiPath = Path.Combine(gameDir, "dxgi.dll");
        Assert.False(File.Exists(dxgiPath));

        var originalGameSnapshot = SnapshotFiles(gameDir);
        var originalD3D11Identity = FileIdentity.Capture(d3d11Path);
        var originalConfIdentity = FileIdentity.Capture(confPath);

        string appDataDir = Path.Combine(testDir.RootPath, "AppDataBundle");
        string profilesDir = Path.Combine(appDataDir, "Profiles");
        string libraryFile = Path.Combine(profilesDir, "game-library.json");
        string profilesFile = Path.Combine(profilesDir, "games.json");
        string backupsDir = Path.Combine(profilesDir, "Backups");
        string cacheDir = Path.Combine(appDataDir, "Cache");
        string cacheFile = Path.Combine(cacheDir, "release-cache.json");
        string sourcesDir = Path.Combine(appDataDir, "Sources");

        var transport = new LifecycleHttpTransport("v2.5", "v2.6");
        using var httpClient = new HttpClient(transport);

        // 1. Acquire two distinguishable official releases into shared source library
        var cacheStore1 = new CacheStore(cacheFile);
        var github1 = new DxvkGithubClient(httpClient, cacheStore1);
        var profileStore1 = new ProfileStore(profilesFile);
        var libraryStore1 = new GameLibraryStore(libraryFile, backupsDir, profilesFile);
        var engine1 = new MultiFileTransactionEngine(backupsDir);
        var installer1 = SyntheticSafetyFixture.CreateInstaller(httpClient, engine1, libraryStore1, sourcesDir);
        var rollback1 = SyntheticSafetyFixture.CreateRollback(engine1, libraryStore1);
        var manager1 = SyntheticSafetyFixture.CreateManager(installer1, rollback1, github1, profileStore1, libraryStore1);
        installer1.GetReleaseAcquirer(github1).ProcessLeaseCoordinator = libraryStore1.LeaseCoordinator;

        var acq25 = await manager1.AcquireOfficialReleaseAsync("v2.5");
        Assert.Equal("v2.5", acq25.Version);
        Assert.True(acq25.UpstreamDigestVerified);

        var acq26 = await manager1.AcquireOfficialReleaseAsync("v2.6");
        Assert.Equal("v2.6", acq26.Version);
        Assert.True(acq26.UpstreamDigestVerified);

        var acquiredSourcesSnapshot = SnapshotFiles(sourcesDir);
        Assert.NotEmpty(acquiredSourcesSnapshot);
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", "companion-release.json")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", "x64", "d3d11.dll")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", "x64", "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", "companion-release.json")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", "x64", "d3d11.dll")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", "x64", "dxgi.dll")));

        // 2. Install exact older release into DX11/x64 synthetic game with HUD and frame limit enabled
        var profile1 = profileStore1.GetOrCreate(exePath);
        profile1.Api = GraphicsApi.DX11;
        profile1.Architecture = "x64";
        profile1.HudEnabled = true;
        profile1.FrameLimit = 60;
        profileStore1.Save(profile1);

        var installResult = await manager1.RequestEnableByPathAsync(profile1, "v2.5");
        Assert.Equal(DxvkActionResult.Applied, installResult);

        Assert.Equal("v2.5:d3d11", File.ReadAllText(d3d11Path));
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal("v2.5:dxgi", File.ReadAllText(dxgiPath));
        Assert.True(File.Exists(confPath));

        var inst1 = libraryStore1.FindByInstallationPath(gameDir);
        Assert.NotNull(inst1);
        Assert.Equal("v2.5", inst1!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, inst1.RestorationState);
        Assert.True(inst1.Configuration.HudEnabled);
        Assert.True(inst1.Configuration.FrameLimitEnabled);
        Assert.Equal(60, inst1.Configuration.FrameLimit);

        string installationId = inst1.Id;
        var d3d11Record1 = inst1.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record1);
        Assert.Equal(FileOriginalState.Existing, d3d11Record1!.OriginalState);
        Assert.Equal(originalD3D11Identity.Sha256, d3d11Record1.OriginalSha256);
        Assert.Equal(Path.Combine(installationId, "d3d11.dll"), d3d11Record1.BackupRelativePath);
        string d3d11Backup1 = Path.Combine(backupsDir, d3d11Record1.BackupRelativePath!);
        Assert.True(File.Exists(d3d11Backup1));
        Assert.Equal(originalD3D11, File.ReadAllBytes(d3d11Backup1));

        var confRecord1 = inst1.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord1);
        Assert.Equal(FileOriginalState.Existing, confRecord1!.OriginalState);
        Assert.Equal(originalConfIdentity.Sha256, confRecord1.OriginalSha256);
        Assert.Equal(Path.Combine(installationId, "dxvk.conf"), confRecord1.BackupRelativePath);
        string confBackup1 = Path.Combine(backupsDir, confRecord1.BackupRelativePath!);
        Assert.True(File.Exists(confBackup1));
        Assert.Equal(originalConf, File.ReadAllBytes(confBackup1));

        var dxgiRecord1 = inst1.FindManagedFile("dxgi.dll");
        Assert.NotNull(dxgiRecord1);
        Assert.Equal(FileOriginalState.Missing, dxgiRecord1!.OriginalState);
        Assert.Null(dxgiRecord1.OriginalSha256);
        Assert.Null(dxgiRecord1.BackupRelativePath);

        var originalBackupsSnapshot = SnapshotFiles(backupsDir);
        Assert.NotEmpty(originalBackupsSnapshot);

        // 3. Relocate disposable app-data bundle to distinct path under test directory
        string relocatedAppDataDir = Path.Combine(testDir.RootPath, "RelocatedAppDataBundle");
        Directory.Move(appDataDir, relocatedAppDataDir);
        Assert.False(Directory.Exists(appDataDir));
        Assert.True(Directory.Exists(gameDir));

        string newProfilesDir = Path.Combine(relocatedAppDataDir, "Profiles");
        string newLibraryFile = Path.Combine(newProfilesDir, "game-library.json");
        string newProfilesFile = Path.Combine(newProfilesDir, "games.json");
        string newBackupsDir = Path.Combine(newProfilesDir, "Backups");
        string newCacheDir = Path.Combine(relocatedAppDataDir, "Cache");
        string newCacheFile = Path.Combine(newCacheDir, "release-cache.json");
        string newSourcesDir = Path.Combine(relocatedAppDataDir, "Sources");

        var cacheStore2 = new CacheStore(newCacheFile);
        var github2 = new DxvkGithubClient(httpClient, cacheStore2);
        var profileStore2 = new ProfileStore(newProfilesFile);
        var libraryStore2 = new GameLibraryStore(newLibraryFile, newBackupsDir, newProfilesFile);
        var engine2 = new MultiFileTransactionEngine(newBackupsDir);
        var installer2 = SyntheticSafetyFixture.CreateInstaller(httpClient, engine2, libraryStore2, newSourcesDir);
        var rollback2 = SyntheticSafetyFixture.CreateRollback(engine2, libraryStore2);
        var manager2 = SyntheticSafetyFixture.CreateManager(installer2, rollback2, github2, profileStore2, libraryStore2);
        installer2.GetReleaseAcquirer(github2).ProcessLeaseCoordinator = libraryStore2.LeaseCoordinator;

        Assert.False(Directory.Exists(appDataDir));

        var profile2 = profileStore2.GetOrCreate(exePath);
        Assert.True(profile2.DxvkEnabled);
        Assert.Equal("v2.5", profile2.DxvkVersion);
        Assert.True(profile2.HudEnabled);
        Assert.Equal(60, profile2.FrameLimit);

        var inst2 = libraryStore2.FindByInstallationPath(gameDir);
        Assert.NotNull(inst2);
        Assert.Equal(installationId, inst2!.Id);
        Assert.Equal("v2.5", inst2.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, inst2.RestorationState);
        Assert.Equal(InstallationConflictFlags.None, inst2.ConflictFlags);
        Assert.Null(inst2.PendingAction);
        Assert.Null(inst2.LastRefusalReason);

        AssertFilesUnchanged(acquiredSourcesSnapshot, newSourcesDir);
        AssertFilesUnchanged(originalBackupsSnapshot, newBackupsDir);
        Assert.False(Directory.Exists(appDataDir));

        // 4. Set fake transport unavailable after initial acquisition (assert zero additional HTTP calls)
        transport.RejectAllRequests = true;
        int requestCountAfterAcquisition = transport.RequestCount;

        // Update through manager to already acquired newer exact release
        var updateResult = await manager2.RequestUpdateByPathAsync(profile2, "v2.6");
        Assert.Equal(DxvkActionResult.Applied, updateResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        Assert.Equal("v2.6:d3d11", File.ReadAllText(d3d11Path));
        Assert.Equal("v2.6:dxgi", File.ReadAllText(dxgiPath));
        Assert.Equal("v2.6", profile2.DxvkVersion);

        var inst2Updated = libraryStore2.FindByInstallationPath(gameDir);
        Assert.NotNull(inst2Updated);
        Assert.Equal("v2.6", inst2Updated!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, inst2Updated.RestorationState);

        var d3d11Record2 = inst2Updated.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record2);
        Assert.Equal(FileOriginalState.Existing, d3d11Record2!.OriginalState);
        Assert.Equal(originalD3D11Identity.Sha256, d3d11Record2.OriginalSha256);
        Assert.Equal(originalD3D11, File.ReadAllBytes(Path.Combine(newBackupsDir, d3d11Record2.BackupRelativePath!)));

        var confRecord2 = inst2Updated.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord2);
        Assert.Equal(FileOriginalState.Existing, confRecord2!.OriginalState);
        Assert.Equal(originalConfIdentity.Sha256, confRecord2.OriginalSha256);
        Assert.Equal(originalConf, File.ReadAllBytes(Path.Combine(newBackupsDir, confRecord2.BackupRelativePath!)));

        AssertFilesUnchanged(acquiredSourcesSnapshot, newSourcesDir);
        AssertFilesUnchanged(originalBackupsSnapshot, newBackupsDir);
        Assert.False(Directory.Exists(appDataDir));

        // Restart/reload, disable the last HUD/frame feature and reapply
        var profileStore3 = new ProfileStore(newProfilesFile);
        var libraryStore3 = new GameLibraryStore(newLibraryFile, newBackupsDir, newProfilesFile);
        var engine3 = new MultiFileTransactionEngine(newBackupsDir);
        var installer3 = SyntheticSafetyFixture.CreateInstaller(httpClient, engine3, libraryStore3, newSourcesDir);
        var rollback3 = SyntheticSafetyFixture.CreateRollback(engine3, libraryStore3);
        var manager3 = SyntheticSafetyFixture.CreateManager(installer3, rollback3, github2, profileStore3, libraryStore3);
        installer3.GetReleaseAcquirer(github2).ProcessLeaseCoordinator = libraryStore3.LeaseCoordinator;

        var profile3 = profileStore3.GetOrCreate(exePath);
        profile3.HudEnabled = false;
        profile3.FrameLimit = 0;
        profileStore3.Save(profile3);

        var reapplyResult = await manager3.RequestReapplyByPathAsync(profile3);
        Assert.Equal(DxvkActionResult.Applied, reapplyResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        string confContentAfterReapply = File.ReadAllText(confPath);
        Assert.DoesNotContain("dxvk.hud", confContentAfterReapply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dxvk.maxFrameRate", confContentAfterReapply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("# Preexisting user config", confContentAfterReapply);
        Assert.Contains("custom_user_setting = 42", confContentAfterReapply);
        Assert.Contains("unrelated_tail = true", confContentAfterReapply);

        Assert.Equal("v2.6:d3d11", File.ReadAllText(d3d11Path));
        Assert.Equal("v2.6:dxgi", File.ReadAllText(dxgiPath));

        var inst3Reapplied = libraryStore3.FindByInstallationPath(gameDir);
        Assert.NotNull(inst3Reapplied);
        Assert.False(inst3Reapplied!.Configuration.HudEnabled);
        Assert.False(inst3Reapplied.Configuration.FrameLimitEnabled);
        Assert.Equal("v2.6", inst3Reapplied.ManagedDxvkVersion);

        AssertFilesUnchanged(acquiredSourcesSnapshot, newSourcesDir);
        AssertFilesUnchanged(originalBackupsSnapshot, newBackupsDir);
        Assert.False(Directory.Exists(appDataDir));

        // Reload and restore
        var profileStore4 = new ProfileStore(newProfilesFile);
        var libraryStore4 = new GameLibraryStore(newLibraryFile, newBackupsDir, newProfilesFile);
        var engine4 = new MultiFileTransactionEngine(newBackupsDir);
        var installer4 = SyntheticSafetyFixture.CreateInstaller(httpClient, engine4, libraryStore4, newSourcesDir);
        var rollback4 = SyntheticSafetyFixture.CreateRollback(engine4, libraryStore4);
        var manager4 = SyntheticSafetyFixture.CreateManager(installer4, rollback4, github2, profileStore4, libraryStore4);
        installer4.GetReleaseAcquirer(github2).ProcessLeaseCoordinator = libraryStore4.LeaseCoordinator;

        var profile4 = profileStore4.GetOrCreate(exePath);
        var restoreResult = await manager4.RequestDisableByPathAsync(profile4);
        Assert.Equal(DxvkActionResult.Applied, restoreResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Final exact original raw bytes plus complete sets restored
        AssertFilesUnchanged(originalGameSnapshot, gameDir);
        Assert.True(File.Exists(d3d11Path));
        Assert.Equal(originalD3D11, File.ReadAllBytes(d3d11Path));
        Assert.Equal(originalD3D11Identity, FileIdentity.Capture(d3d11Path));
        Assert.True(File.Exists(confPath));
        Assert.Equal(originalConf, File.ReadAllBytes(confPath));
        Assert.Equal(originalConfIdentity, FileIdentity.Capture(confPath));
        Assert.False(File.Exists(dxgiPath));

        var inst4Restored = libraryStore4.FindByInstallationPath(gameDir);
        Assert.NotNull(inst4Restored);
        Assert.Null(inst4Restored!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Restored, inst4Restored.RestorationState);
        Assert.Equal(InstallationConflictFlags.None, inst4Restored.ConflictFlags);
        Assert.Null(inst4Restored.PendingAction);
        Assert.Null(inst4Restored.LastRefusalReason);
        Assert.False(profile4.DxvkEnabled);
        Assert.Equal("v2.6", profile4.DxvkVersion);

        AssertFilesUnchanged(acquiredSourcesSnapshot, newSourcesDir);
        AssertFilesUnchanged(originalBackupsSnapshot, newBackupsDir);
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

        string exePath = Path.Combine(gameDir, "Game.exe");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 });
        string d3d11Path = Path.Combine(gameDir, "d3d11.dll");
        byte[] originalD3D11 = Encoding.UTF8.GetBytes("native-d3d11-v1.0-bytes");
        File.WriteAllBytes(d3d11Path, originalD3D11);
        string confPath = Path.Combine(gameDir, "dxvk.conf");
        byte[] originalConf = Encoding.UTF8.GetBytes("# user config\ncustom_key = 1\ndxvk.hud = compiler\n");
        File.WriteAllBytes(confPath, originalConf);
        string dxgiPath = Path.Combine(gameDir, "dxgi.dll");

        string appDataDir = Path.Combine(testDir.RootPath, "AppDataBundle");
        string profilesDir = Path.Combine(appDataDir, "Profiles");
        string libraryFile = Path.Combine(profilesDir, "game-library.json");
        string profilesFile = Path.Combine(profilesDir, "games.json");
        string backupsDir = Path.Combine(profilesDir, "Backups");
        string cacheFile = Path.Combine(appDataDir, "Cache", "release-cache.json");
        string sourcesDir = Path.Combine(appDataDir, "Sources");

        var transport = new LifecycleHttpTransport("v2.5");
        using var httpClient = new HttpClient(transport);

        var cacheStore1 = new CacheStore(cacheFile);
        var github1 = new DxvkGithubClient(httpClient, cacheStore1);
        var profileStore1 = new ProfileStore(profilesFile);
        var libraryStore1 = new GameLibraryStore(libraryFile, backupsDir, profilesFile);
        var engine1 = new MultiFileTransactionEngine(backupsDir);
        var installer1 = SyntheticSafetyFixture.CreateInstaller(httpClient, engine1, libraryStore1, sourcesDir);
        var rollback1 = SyntheticSafetyFixture.CreateRollback(engine1, libraryStore1);
        var manager1 = SyntheticSafetyFixture.CreateManager(installer1, rollback1, github1, profileStore1, libraryStore1);
        installer1.GetReleaseAcquirer(github1).ProcessLeaseCoordinator = libraryStore1.LeaseCoordinator;

        var acq = await manager1.AcquireOfficialReleaseAsync("v2.5");
        Assert.Equal("v2.5", acq.Version);

        var profile1 = profileStore1.GetOrCreate(exePath);
        profile1.Api = GraphicsApi.DX11;
        profile1.Architecture = "x64";
        profile1.HudEnabled = true;
        profile1.FrameLimit = 60;
        profileStore1.Save(profile1);

        var installResult = await manager1.RequestEnableByPathAsync(profile1, "v2.5");
        Assert.Equal(DxvkActionResult.Applied, installResult);

        // Relocate bundle
        string relocatedAppDataDir = Path.Combine(testDir.RootPath, "RelocatedAppDataBundle");
        Directory.Move(appDataDir, relocatedAppDataDir);
        Assert.False(Directory.Exists(appDataDir));

        string newProfilesDir = Path.Combine(relocatedAppDataDir, "Profiles");
        string newLibraryFile = Path.Combine(newProfilesDir, "game-library.json");
        string newProfilesFile = Path.Combine(newProfilesDir, "games.json");
        string newBackupsDir = Path.Combine(newProfilesDir, "Backups");
        string newCacheFile = Path.Combine(relocatedAppDataDir, "Cache", "release-cache.json");
        string newSourcesDir = Path.Combine(relocatedAppDataDir, "Sources");

        var cacheStore2 = new CacheStore(newCacheFile);
        var github2 = new DxvkGithubClient(httpClient, cacheStore2);
        var profileStore2 = new ProfileStore(newProfilesFile);
        var libraryStore2 = new GameLibraryStore(newLibraryFile, newBackupsDir, newProfilesFile);
        var engine2 = new MultiFileTransactionEngine(newBackupsDir);
        var installer2 = SyntheticSafetyFixture.CreateInstaller(httpClient, engine2, libraryStore2, newSourcesDir);
        var rollback2 = SyntheticSafetyFixture.CreateRollback(engine2, libraryStore2);
        var manager2 = SyntheticSafetyFixture.CreateManager(installer2, rollback2, github2, profileStore2, libraryStore2);
        installer2.GetReleaseAcquirer(github2).ProcessLeaseCoordinator = libraryStore2.LeaseCoordinator;

        var inst2 = libraryStore2.FindByInstallationPath(gameDir);
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

        var deployedGameSnapshot = SnapshotFiles(gameDir);
        var sourcesSnapshot = SnapshotFiles(newSourcesDir);
        var backupsSnapshot = SnapshotFiles(newBackupsDir);

        var profile2 = profileStore2.GetOrCreate(exePath);
        var restoreResult = await manager2.RequestDisableByPathAsync(profile2);
        Assert.Equal(DxvkActionResult.Failed, restoreResult);

        // Assert target bytes were not partially mutated
        AssertFilesUnchanged(deployedGameSnapshot, gameDir);
        // Assert source and backup bytes were retained
        AssertFilesUnchanged(sourcesSnapshot, newSourcesDir);
        AssertFilesUnchanged(backupsSnapshot, newBackupsDir);

        // Truthful health/refusal/pending state
        var reloadedInst = libraryStore2.FindByInstallationPath(gameDir);
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
