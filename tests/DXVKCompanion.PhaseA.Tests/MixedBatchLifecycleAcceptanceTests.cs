using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
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

public sealed class MixedBatchLifecycleAcceptanceTests
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

    private static string ComputeSha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

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

    private sealed record OwnershipSnapshot(
        GraphicsApi ProfileApi,
        string? ProfileArchitecture,
        string? ProfileDxvkVersion,
        bool ProfileDxvkEnabled,
        bool ProfileHudEnabled,
        int ProfileFrameLimit,
        RestorationState RestorationState,
        string? ManagedDxvkVersion,
        string? ManagedDxvkArchitecture,
        ManagementMode PolicyMode,
        string? PolicyPinnedVersion,
        bool ConfigHudEnabled,
        int ConfigFrameLimit,
        bool ConfigFrameLimitEnabled,
        Dictionary<string, ManagedFileRecordSnapshot> ManagedFiles,
        Dictionary<string, ExecutableRecordSnapshot> Executables);

    private sealed record ManagedFileRecordSnapshot(
        string RelativePath,
        ManagedFileState CurrentState,
        FileOriginalState OriginalState,
        string? OriginalSha256,
        string? ExpectedManagedSha256,
        string? BackupRelativePath,
        string? ManagedDxvkVersion);

    private sealed record ExecutableRecordSnapshot(
        string RelativePath,
        GraphicsApi LastKnownApi,
        string? LastKnownArchitecture);

    private static OwnershipSnapshot CaptureOwnership(GameProfile profile, GameInstallation installation)
    {
        var managedFiles = installation.ManagedFiles.ToDictionary(
            f => f.RelativePath,
            f => new ManagedFileRecordSnapshot(
                f.RelativePath,
                f.CurrentState,
                f.OriginalState,
                f.OriginalSha256,
                f.ExpectedManagedSha256,
                f.BackupRelativePath,
                f.ManagedDxvkVersion),
            StringComparer.OrdinalIgnoreCase);

        var executables = installation.Executables.ToDictionary(
            e => e.RelativePath,
            e => new ExecutableRecordSnapshot(
                e.RelativePath,
                e.LastKnownApi,
                e.LastKnownArchitecture),
            StringComparer.OrdinalIgnoreCase);

        return new OwnershipSnapshot(
            profile.Api,
            profile.Architecture,
            profile.DxvkVersion,
            profile.DxvkEnabled,
            profile.HudEnabled,
            profile.FrameLimit,
            installation.RestorationState,
            installation.ManagedDxvkVersion,
            installation.ManagedDxvkArchitecture,
            installation.ManagementPolicy.Mode,
            installation.ManagementPolicy.PinnedDxvkVersion,
            installation.Configuration.HudEnabled,
            installation.Configuration.FrameLimit,
            installation.Configuration.FrameLimitEnabled,
            managedFiles,
            executables);
    }

    private static void AssertOwnershipUnchanged(OwnershipSnapshot expected, GameProfile profile, GameInstallation installation)
    {
        Assert.Equal(expected.ProfileApi, profile.Api);
        Assert.Equal(expected.ProfileArchitecture, profile.Architecture);
        Assert.Equal(expected.ProfileDxvkVersion, profile.DxvkVersion);
        Assert.Equal(expected.ProfileDxvkEnabled, profile.DxvkEnabled);
        Assert.Equal(expected.ProfileHudEnabled, profile.HudEnabled);
        Assert.Equal(expected.ProfileFrameLimit, profile.FrameLimit);

        Assert.Equal(expected.RestorationState, installation.RestorationState);
        Assert.Equal(expected.ManagedDxvkVersion, installation.ManagedDxvkVersion);
        Assert.Equal(expected.ManagedDxvkArchitecture, installation.ManagedDxvkArchitecture);
        Assert.Equal(expected.PolicyMode, installation.ManagementPolicy.Mode);
        Assert.Equal(expected.PolicyPinnedVersion, installation.ManagementPolicy.PinnedDxvkVersion);
        Assert.Equal(expected.ConfigHudEnabled, installation.Configuration.HudEnabled);
        Assert.Equal(expected.ConfigFrameLimit, installation.Configuration.FrameLimit);
        Assert.Equal(expected.ConfigFrameLimitEnabled, installation.Configuration.FrameLimitEnabled);

        Assert.Equal(expected.Executables.Count, installation.Executables.Count);
        foreach (var (relPath, expExe) in expected.Executables)
        {
            var actualExe = installation.FindExecutable(relPath);
            Assert.NotNull(actualExe);
            Assert.Equal(expExe.LastKnownApi, actualExe!.LastKnownApi);
            Assert.Equal(expExe.LastKnownArchitecture, actualExe.LastKnownArchitecture);
        }

        Assert.Equal(expected.ManagedFiles.Count, installation.ManagedFiles.Count);
        foreach (var (relPath, expFile) in expected.ManagedFiles)
        {
            var actualFile = installation.FindManagedFile(relPath);
            Assert.NotNull(actualFile);
            Assert.Equal(expFile.CurrentState, actualFile!.CurrentState);
            Assert.Equal(expFile.OriginalState, actualFile.OriginalState);
            Assert.Equal(expFile.OriginalSha256, actualFile.OriginalSha256);
            Assert.Equal(expFile.ExpectedManagedSha256, actualFile.ExpectedManagedSha256);
            Assert.Equal(expFile.BackupRelativePath, actualFile.BackupRelativePath);
            Assert.Equal(expFile.ManagedDxvkVersion, actualFile.ManagedDxvkVersion);
        }
    }

    private static void AssertFaultedOwnershipRetained(
        OwnershipSnapshot faultedBaseline,
        GameProfile profile,
        GameInstallation installation)
    {
        Assert.Equal(faultedBaseline.ProfileApi, profile.Api);
        Assert.Equal(faultedBaseline.ProfileArchitecture, profile.Architecture);
        Assert.Equal(faultedBaseline.ProfileDxvkVersion, profile.DxvkVersion);
        Assert.Equal(faultedBaseline.ProfileDxvkEnabled, profile.DxvkEnabled);
        Assert.Equal(faultedBaseline.ProfileHudEnabled, profile.HudEnabled);
        Assert.Equal(faultedBaseline.ProfileFrameLimit, profile.FrameLimit);

        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.Null(installation.PendingAction);
        Assert.NotNull(installation.LastRefusalReason);
        Assert.Contains("backup hash mismatch", installation.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(faultedBaseline.ManagedDxvkVersion, installation.ManagedDxvkVersion);
        Assert.Equal(faultedBaseline.ManagedDxvkArchitecture, installation.ManagedDxvkArchitecture);
        Assert.Equal(faultedBaseline.PolicyMode, installation.ManagementPolicy.Mode);
        Assert.Equal(faultedBaseline.PolicyPinnedVersion, installation.ManagementPolicy.PinnedDxvkVersion);
        Assert.Equal(faultedBaseline.ConfigHudEnabled, installation.Configuration.HudEnabled);
        Assert.Equal(faultedBaseline.ConfigFrameLimit, installation.Configuration.FrameLimit);
        Assert.Equal(faultedBaseline.ConfigFrameLimitEnabled, installation.Configuration.FrameLimitEnabled);

        Assert.Equal(faultedBaseline.Executables.Count, installation.Executables.Count);
        foreach (var (relPath, expExe) in faultedBaseline.Executables)
        {
            var actualExe = installation.FindExecutable(relPath);
            Assert.NotNull(actualExe);
            Assert.Equal(expExe.LastKnownApi, actualExe!.LastKnownApi);
            Assert.Equal(expExe.LastKnownArchitecture, actualExe.LastKnownArchitecture);
        }

        Assert.Equal(faultedBaseline.ManagedFiles.Count, installation.ManagedFiles.Count);
        foreach (var (relPath, expFile) in faultedBaseline.ManagedFiles)
        {
            var actualFile = installation.FindManagedFile(relPath);
            Assert.NotNull(actualFile);
            Assert.Equal(expFile.CurrentState, actualFile!.CurrentState);
            Assert.Equal(expFile.OriginalState, actualFile.OriginalState);
            Assert.Equal(expFile.OriginalSha256, actualFile.OriginalSha256);
            Assert.Equal(expFile.ExpectedManagedSha256, actualFile.ExpectedManagedSha256);
            Assert.Equal(expFile.BackupRelativePath, actualFile.BackupRelativePath);
            Assert.Equal(expFile.ManagedDxvkVersion, actualFile.ManagedDxvkVersion);
        }
    }

    private static void AssertRestoredHistory(
        GameInstallation installation,
        GameProfile profile,
        string? expectedD3d11OriginalSha,
        string? expectedConfOriginalSha,
        string backupsDir)
    {
        Assert.False(profile.DxvkEnabled);
        Assert.Equal(RestorationState.Restored, installation.RestorationState);
        Assert.True(string.IsNullOrEmpty(installation.ManagedDxvkVersion));
        Assert.Null(installation.PendingAction);

        var expectedPaths = new[] { "d3d11.dll", "dxgi.dll", "dxvk.conf" };
        Assert.Equal(
            expectedPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
            installation.ManagedFiles.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

        foreach (var record in installation.ManagedFiles)
        {
            Assert.True(string.IsNullOrEmpty(record.ManagedDxvkVersion));
            Assert.Equal(ManagedFileState.Consistent, record.CurrentState);
        }

        var d3d11Rec = installation.FindManagedFile("d3d11.dll")!;
        Assert.Equal(FileOriginalState.Existing, d3d11Rec.OriginalState);
        Assert.Equal(expectedD3d11OriginalSha, d3d11Rec.OriginalSha256);
        Assert.NotNull(d3d11Rec.BackupRelativePath);
        Assert.True(File.Exists(Path.Combine(backupsDir, d3d11Rec.BackupRelativePath!)));

        var dxgiRec = installation.FindManagedFile("dxgi.dll")!;
        Assert.Equal(FileOriginalState.Missing, dxgiRec.OriginalState);
        Assert.Null(dxgiRec.OriginalSha256);
        Assert.Null(dxgiRec.BackupRelativePath);

        var confRec = installation.FindManagedFile("dxvk.conf")!;
        if (expectedConfOriginalSha != null)
        {
            Assert.Equal(FileOriginalState.Existing, confRec.OriginalState);
            Assert.Equal(expectedConfOriginalSha, confRec.OriginalSha256);
            Assert.NotNull(confRec.BackupRelativePath);
            Assert.True(File.Exists(Path.Combine(backupsDir, confRec.BackupRelativePath!)));
        }
        else
        {
            Assert.Equal(FileOriginalState.Missing, confRec.OriginalState);
            Assert.Null(confRec.OriginalSha256);
            Assert.Null(confRec.BackupRelativePath);
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
        DxvkManager Manager,
        SettingsStore SettingsStore);

    private static ServiceStack ConstructServiceStack(string appDataDir, HttpClient httpClient)
    {
        string profilesDir = Path.Combine(appDataDir, "Profiles");
        string libraryFile = Path.Combine(profilesDir, "game-library.json");
        string profilesFile = Path.Combine(profilesDir, "games.json");
        string backupsDir = Path.Combine(profilesDir, "Backups");
        string cacheFile = Path.Combine(appDataDir, "Cache", "release-cache.json");
        string sourcesDir = Path.Combine(appDataDir, "Sources");
        string settingsFile = Path.Combine(appDataDir, "settings.json");

        var cacheStore = new CacheStore(cacheFile);
        var github = new DxvkGithubClient(httpClient, cacheStore);
        var profileStore = new ProfileStore(profilesFile);
        var libraryStore = new GameLibraryStore(libraryFile, backupsDir, profilesFile);
        var engine = new MultiFileTransactionEngine(backupsDir);
        var installer = SyntheticSafetyFixture.CreateInstaller(httpClient, engine, libraryStore, sourcesDir);
        var rollback = SyntheticSafetyFixture.CreateRollback(engine, libraryStore);
        var manager = SyntheticSafetyFixture.CreateManager(installer, rollback, github, profileStore, libraryStore);
        var settingsStore = SettingsStore.Load(settingsFile);

        profileStore.LeaseCoordinator = libraryStore.LeaseCoordinator;
        github.ProcessLeaseCoordinator = libraryStore.LeaseCoordinator;
        installer.GetReleaseAcquirer(github).ProcessLeaseCoordinator = libraryStore.LeaseCoordinator;

        return new ServiceStack(appDataDir, cacheStore, github, profileStore, libraryStore, engine, installer, rollback, manager, settingsStore);
    }

    [Theory]
    [InlineData("BeforeHealthyTargets")]
    [InlineData("BetweenHealthyTargets")]
    public async Task MixedBatchLifecycle_UpdateAllAndRestoreAll_MixedResultsPreserveErrorIsolationAndDurability(
        string targetOrder)
    {
        using var testDir = new SyntheticTestDirectory();
        string gamesRoot = Path.Combine(testDir.RootPath, $"MixedBatch_{targetOrder}");
        Directory.CreateDirectory(gamesRoot);

        // Inert PE executable bytes
        byte[] exeBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 };

        // 1. Target A (Healthy DX11 / x64, preexisting config, Existing d3d11, Missing dxgi)
        string gameDirA = Path.Combine(gamesRoot, "GameA");
        Directory.CreateDirectory(gameDirA);
        string exePathA = Path.Combine(gameDirA, "GameA.exe");
        File.WriteAllBytes(exePathA, exeBytes);
        string emptyDirA = Path.Combine(gameDirA, "Data", "EmptySubDirA");
        Directory.CreateDirectory(emptyDirA);
        byte[] sentinelBytesA = new byte[] { 0xAA, 0x01, 0x02, 0x42 };
        File.WriteAllBytes(Path.Combine(gameDirA, "sentinel.bin"), sentinelBytesA);
        byte[] nativeD3d11A = Encoding.UTF8.GetBytes("native-d3d11-game-A:x64");
        File.WriteAllBytes(Path.Combine(gameDirA, "d3d11.dll"), nativeD3d11A);
        byte[] originalConfA = Encoding.UTF8.GetBytes(
            "# Preexisting user dxvk.conf for Game A\r\n" +
            "dxvk.hudExtra = full\n" +
            "dxvk.maxFrameRateTarget = 120\r\n" +
            "user.settingA = 1\n");
        File.WriteAllBytes(Path.Combine(gameDirA, "dxvk.conf"), originalConfA);
        var initialSnapshotA = SnapshotResources(gameDirA);

        // 2. Target B (Managed that will fail, preexisting config, Existing d3d11, Missing dxgi)
        string gameDirB = Path.Combine(gamesRoot, "GameB");
        Directory.CreateDirectory(gameDirB);
        string exePathB = Path.Combine(gameDirB, "GameB.exe");
        File.WriteAllBytes(exePathB, exeBytes);
        string emptyDirB = Path.Combine(gameDirB, "Data", "EmptySubDirB");
        Directory.CreateDirectory(emptyDirB);
        byte[] sentinelBytesB = new byte[] { 0xBB, 0x01, 0x02, 0x42 };
        File.WriteAllBytes(Path.Combine(gameDirB, "sentinel.bin"), sentinelBytesB);
        byte[] nativeD3d11B = Encoding.UTF8.GetBytes("native-d3d11-game-B:x64");
        File.WriteAllBytes(Path.Combine(gameDirB, "d3d11.dll"), nativeD3d11B);
        byte[] originalConfB = Encoding.UTF8.GetBytes(
            "# Preexisting user dxvk.conf for Game B\r\n" +
            "dxvk.hudExtra = compact\n" +
            "dxvk.maxFrameRateTarget = 144\r\n" +
            "user.settingB = 2\n");
        File.WriteAllBytes(Path.Combine(gameDirB, "dxvk.conf"), originalConfB);
        var initialSnapshotB = SnapshotResources(gameDirB);

        // 3. Target C (Healthy DX11 / x64, absent initial config, Existing d3d11, Missing dxgi)
        string gameDirC = Path.Combine(gamesRoot, "GameC");
        Directory.CreateDirectory(gameDirC);
        string exePathC = Path.Combine(gameDirC, "GameC.exe");
        File.WriteAllBytes(exePathC, exeBytes);
        string emptyDirC = Path.Combine(gameDirC, "Data", "EmptySubDirC");
        Directory.CreateDirectory(emptyDirC);
        byte[] sentinelBytesC = new byte[] { 0xCC, 0x01, 0x02, 0x42 };
        File.WriteAllBytes(Path.Combine(gameDirC, "sentinel.bin"), sentinelBytesC);
        byte[] nativeD3d11C = Encoding.UTF8.GetBytes("native-d3d11-game-C:x64");
        File.WriteAllBytes(Path.Combine(gameDirC, "d3d11.dll"), nativeD3d11C);
        // dxvk.conf is intentionally ABSENT initially for Game C (Missing baseline for config)
        var initialSnapshotC = SnapshotResources(gameDirC);
        Assert.False(initialSnapshotC.Files.ContainsKey("dxvk.conf"));

        // 4. Target D (Already-restored candidate with truthful durable history)
        string gameDirD = Path.Combine(gamesRoot, "GameD");
        Directory.CreateDirectory(gameDirD);
        string exePathD = Path.Combine(gameDirD, "GameD.exe");
        File.WriteAllBytes(exePathD, exeBytes);
        string emptyDirD = Path.Combine(gameDirD, "Data", "EmptySubDirD");
        Directory.CreateDirectory(emptyDirD);
        byte[] sentinelBytesD = new byte[] { 0xDD, 0x01, 0x02, 0x42 };
        File.WriteAllBytes(Path.Combine(gameDirD, "sentinel.bin"), sentinelBytesD);
        byte[] nativeD3d11D = Encoding.UTF8.GetBytes("native-d3d11-game-D:x64");
        File.WriteAllBytes(Path.Combine(gameDirD, "d3d11.dll"), nativeD3d11D);
        byte[] originalConfD = Encoding.UTF8.GetBytes(
            "# Preexisting user dxvk.conf for Game D\r\n" +
            "user.settingD = 4\n");
        File.WriteAllBytes(Path.Combine(gameDirD, "dxvk.conf"), originalConfD);
        var initialSnapshotD = SnapshotResources(gameDirD);

        string appDataDir = Path.Combine(testDir.RootPath, "AppData");
        string sourcesDir = Path.Combine(appDataDir, "Sources");
        string backupsDir = Path.Combine(appDataDir, "Profiles", "Backups");

        var transport = new LifecycleHttpTransport("v2.5", "v2.6");
        using var httpClient = new HttpClient(transport);

        // 1. Initial stack & official acquisition
        var stack1 = ConstructServiceStack(appDataDir, httpClient);

        var acq25 = await stack1.Manager.AcquireOfficialReleaseAsync("v2.5");
        Assert.NotNull(acq25);
        Assert.Equal("v2.5", acq25.Version);
        Assert.True(acq25.UpstreamDigestVerified);

        var acq26 = await stack1.Manager.AcquireOfficialReleaseAsync("v2.6");
        Assert.NotNull(acq26);
        Assert.Equal("v2.6", acq26.Version);
        Assert.True(acq26.UpstreamDigestVerified);

        // Verify manifests, markers, and payloads
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", "companion-release.json")));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(sourcesDir, "v2.5"), "v2.5", out bool v25Verified));
        Assert.True(v25Verified);
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x64:d3d11"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.5", "x64", "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x64:dxgi"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.5", "x64", "dxgi.dll")));

        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", "companion-release.json")));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(sourcesDir, "v2.6"), "v2.6", out bool v26Verified));
        Assert.True(v26Verified);
        Assert.Equal(Encoding.UTF8.GetBytes("v2.6:x64:d3d11"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.6", "x64", "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.6:x64:dxgi"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.6", "x64", "dxgi.dll")));

        var acquiredSourcesSnapshot = SnapshotResources(sourcesDir);
        Assert.NotEmpty(acquiredSourcesSnapshot.Files);

        // Reject all further HTTP calls throughout the entire lifecycle
        transport.RejectAllRequests = true;
        int requestCountAfterAcquisition = transport.RequestCount;

        // Persist isolated Manual settings
        stack1.SettingsStore.GlobalPolicy = GlobalManagementPolicy.Manual;
        Assert.True(stack1.SettingsStore.Save(out _));

        // 2. Setup profiles and installations in the exact order requested by targetOrder theory
        // "BeforeHealthyTargets": B, A, C, D
        // "BetweenHealthyTargets": A, B, C, D
        var targetOrderKeys = targetOrder == "BeforeHealthyTargets"
            ? new[] { "B", "A", "C", "D" }
            : new[] { "A", "B", "C", "D" };

        void SetupTarget(string key)
        {
            if (key == "A")
            {
                var pA = stack1.ProfileStore.GetOrCreate(exePathA);
                pA.Api = GraphicsApi.DX11;
                pA.Architecture = "x64";
                pA.HudEnabled = true;
                pA.FrameLimit = 60;
                stack1.ProfileStore.Save(pA);

                var instA = stack1.LibraryStore.GetOrCreateInstallation(gameDirA, "GameA");
                var exeRecA = instA.GetOrAddExecutable("GameA.exe", "GameA.exe");
                exeRecA.LastKnownApi = GraphicsApi.DX11;
                exeRecA.LastKnownArchitecture = "x64";
                instA.ManagementPolicy = ManagementPolicy.UseGlobal();
                instA.ManagementPolicy.PinnedDxvkVersion = "v2.5";
                stack1.LibraryStore.Save(instA);
            }
            else if (key == "B")
            {
                var pB = stack1.ProfileStore.GetOrCreate(exePathB);
                pB.Api = GraphicsApi.DX11;
                pB.Architecture = "x64";
                pB.HudEnabled = true;
                pB.FrameLimit = 60;
                stack1.ProfileStore.Save(pB);

                var instB = stack1.LibraryStore.GetOrCreateInstallation(gameDirB, "GameB");
                var exeRecB = instB.GetOrAddExecutable("GameB.exe", "GameB.exe");
                exeRecB.LastKnownApi = GraphicsApi.DX11;
                exeRecB.LastKnownArchitecture = "x64";
                instB.ManagementPolicy = ManagementPolicy.UseGlobal();
                instB.ManagementPolicy.PinnedDxvkVersion = "v2.5";
                stack1.LibraryStore.Save(instB);
            }
            else if (key == "C")
            {
                var pC = stack1.ProfileStore.GetOrCreate(exePathC);
                pC.Api = GraphicsApi.DX11;
                pC.Architecture = "x64";
                pC.HudEnabled = true;
                pC.FrameLimit = 60;
                stack1.ProfileStore.Save(pC);

                var instC = stack1.LibraryStore.GetOrCreateInstallation(gameDirC, "GameC");
                var exeRecC = instC.GetOrAddExecutable("GameC.exe", "GameC.exe");
                exeRecC.LastKnownApi = GraphicsApi.DX11;
                exeRecC.LastKnownArchitecture = "x64";
                instC.ManagementPolicy = ManagementPolicy.UseGlobal();
                instC.ManagementPolicy.PinnedDxvkVersion = "v2.5";
                stack1.LibraryStore.Save(instC);
            }
            else if (key == "D")
            {
                var pD = stack1.ProfileStore.GetOrCreate(exePathD);
                pD.Api = GraphicsApi.DX11;
                pD.Architecture = "x64";
                pD.HudEnabled = true;
                pD.FrameLimit = 60;
                stack1.ProfileStore.Save(pD);

                var instD = stack1.LibraryStore.GetOrCreateInstallation(gameDirD, "GameD");
                var exeRecD = instD.GetOrAddExecutable("GameD.exe", "GameD.exe");
                exeRecD.LastKnownApi = GraphicsApi.DX11;
                exeRecD.LastKnownArchitecture = "x64";
                instD.ManagementPolicy = ManagementPolicy.UseGlobal();
                instD.ManagementPolicy.PinnedDxvkVersion = "v2.5";
                stack1.LibraryStore.Save(instD);
            }
        }

        foreach (var key in targetOrderKeys)
        {
            SetupTarget(key);
        }

        // Prove actual profile traversal order from profile insertion behavior
        var expectedExeOrder = targetOrder == "BeforeHealthyTargets"
            ? new[] { "GameB.exe", "GameA.exe", "GameC.exe", "GameD.exe" }
            : new[] { "GameA.exe", "GameB.exe", "GameC.exe", "GameD.exe" };
        Assert.Equal(expectedExeOrder, stack1.ProfileStore.GetAll().Select(p => p.ExeName));

        // Create D through a real install/disable lifecycle so AlreadyRestored has truthful durable history
        var profileD1 = stack1.ProfileStore.GetOrCreate(exePathD);
        var enableDResult = await stack1.Manager.RequestEnableByPathAsync(profileD1);
        Assert.Equal(DxvkActionResult.Applied, enableDResult);
        Assert.Equal("v2.5", profileD1.DxvkVersion);

        // Deployed ownership, hashes, and exact configuration checks for Game D before Disable
        var instDAfterEnable = stack1.LibraryStore.FindByInstallationPath(gameDirD)!;
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x64:d3d11"), File.ReadAllBytes(Path.Combine(gameDirD, "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x64:dxgi"), File.ReadAllBytes(Path.Combine(gameDirD, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(gameDirD, "dxvk.conf")));
        var d3d11RecD1 = instDAfterEnable.FindManagedFile("d3d11.dll")!;
        var dxgiRecD1 = instDAfterEnable.FindManagedFile("dxgi.dll")!;
        var confRecD1 = instDAfterEnable.FindManagedFile("dxvk.conf")!;
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirD, "d3d11.dll"))), d3d11RecD1.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirD, "dxgi.dll"))), dxgiRecD1.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirD, "dxvk.conf"))), confRecD1.ExpectedManagedSha256);
        Assert.Equal(FileOriginalState.Existing, d3d11RecD1.OriginalState);
        Assert.Equal(ComputeSha256(nativeD3d11D), d3d11RecD1.OriginalSha256);
        Assert.Equal(nativeD3d11D, File.ReadAllBytes(Path.Combine(backupsDir, d3d11RecD1.BackupRelativePath!)));
        Assert.Equal(FileOriginalState.Missing, dxgiRecD1.OriginalState);
        Assert.Null(dxgiRecD1.OriginalSha256);
        Assert.Null(dxgiRecD1.BackupRelativePath);
        Assert.Equal(FileOriginalState.Existing, confRecD1.OriginalState);
        Assert.Equal(ComputeSha256(originalConfD), confRecD1.OriginalSha256);
        Assert.Equal(originalConfD, File.ReadAllBytes(Path.Combine(backupsDir, confRecD1.BackupRelativePath!)));

        var configDictD1 = ParseConfigAssignments(File.ReadAllText(Path.Combine(gameDirD, "dxvk.conf")));
        Assert.Equal("fps,devinfo", configDictD1["dxvk.hud"]);
        Assert.Equal("60", configDictD1["dxvk.maxFrameRate"]);
        Assert.Equal("4", configDictD1["user.settingD"]);

        var disableDResult = await stack1.Manager.RequestDisableByPathAsync(profileD1);
        Assert.Equal(DxvkActionResult.Applied, disableDResult);
        AssertResourcesUnchanged(initialSnapshotD, gameDirD);

        var instDAfterDisable = stack1.LibraryStore.FindByInstallationPath(gameDirD)!;
        AssertRestoredHistory(instDAfterDisable, profileD1, ComputeSha256(nativeD3d11D), ComputeSha256(originalConfD), backupsDir);

        // Deploy A, B, C through normal single-call manager Enable
        var profileA1 = stack1.ProfileStore.GetOrCreate(exePathA);
        var enableAResult = await stack1.Manager.RequestEnableByPathAsync(profileA1);
        Assert.Equal(DxvkActionResult.Applied, enableAResult);
        Assert.Equal("v2.5", profileA1.DxvkVersion);

        var profileB1 = stack1.ProfileStore.GetOrCreate(exePathB);
        var enableBResult = await stack1.Manager.RequestEnableByPathAsync(profileB1);
        Assert.Equal(DxvkActionResult.Applied, enableBResult);
        Assert.Equal("v2.5", profileB1.DxvkVersion);

        var profileC1 = stack1.ProfileStore.GetOrCreate(exePathC);
        var enableCResult = await stack1.Manager.RequestEnableByPathAsync(profileC1);
        Assert.Equal(DxvkActionResult.Applied, enableCResult);
        Assert.Equal("v2.5", profileC1.DxvkVersion);

        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Verify unconditional ExpectedManagedSha256 and deployed bytes for A, B, C
        var instA1 = stack1.LibraryStore.FindByInstallationPath(gameDirA)!;
        var instB1 = stack1.LibraryStore.FindByInstallationPath(gameDirB)!;
        var instC1 = stack1.LibraryStore.FindByInstallationPath(gameDirC)!;

        string idA = instA1.Id;
        string idB = instB1.Id;
        string idC = instC1.Id;
        string idD = instDAfterDisable.Id;

        // Target A verification
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x64:d3d11"), File.ReadAllBytes(Path.Combine(gameDirA, "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x64:dxgi"), File.ReadAllBytes(Path.Combine(gameDirA, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(gameDirA, "dxvk.conf")));
        var d3d11RecA1 = instA1.FindManagedFile("d3d11.dll")!;
        var dxgiRecA1 = instA1.FindManagedFile("dxgi.dll")!;
        var confRecA1 = instA1.FindManagedFile("dxvk.conf")!;
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirA, "d3d11.dll"))), d3d11RecA1.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirA, "dxgi.dll"))), dxgiRecA1.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirA, "dxvk.conf"))), confRecA1.ExpectedManagedSha256);
        Assert.Equal(FileOriginalState.Existing, d3d11RecA1.OriginalState);
        Assert.Equal(ComputeSha256(nativeD3d11A), d3d11RecA1.OriginalSha256);
        Assert.Equal(nativeD3d11A, File.ReadAllBytes(Path.Combine(backupsDir, d3d11RecA1.BackupRelativePath!)));
        Assert.Equal(FileOriginalState.Missing, dxgiRecA1.OriginalState);
        Assert.Null(dxgiRecA1.OriginalSha256);
        Assert.Null(dxgiRecA1.BackupRelativePath);
        Assert.Equal(FileOriginalState.Existing, confRecA1.OriginalState);
        Assert.Equal(ComputeSha256(originalConfA), confRecA1.OriginalSha256);
        Assert.Equal(originalConfA, File.ReadAllBytes(Path.Combine(backupsDir, confRecA1.BackupRelativePath!)));

        var configDictA1 = ParseConfigAssignments(File.ReadAllText(Path.Combine(gameDirA, "dxvk.conf")));
        Assert.Equal("fps,devinfo", configDictA1["dxvk.hud"]);
        Assert.Equal("60", configDictA1["dxvk.maxFrameRate"]);
        Assert.Equal("full", configDictA1["dxvk.hudExtra"]);
        Assert.Equal("120", configDictA1["dxvk.maxFrameRateTarget"]);
        Assert.Equal("1", configDictA1["user.settingA"]);

        // Target B verification
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x64:d3d11"), File.ReadAllBytes(Path.Combine(gameDirB, "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x64:dxgi"), File.ReadAllBytes(Path.Combine(gameDirB, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(gameDirB, "dxvk.conf")));
        var d3d11RecB1 = instB1.FindManagedFile("d3d11.dll")!;
        var dxgiRecB1 = instB1.FindManagedFile("dxgi.dll")!;
        var confRecB1 = instB1.FindManagedFile("dxvk.conf")!;
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirB, "d3d11.dll"))), d3d11RecB1.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirB, "dxgi.dll"))), dxgiRecB1.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirB, "dxvk.conf"))), confRecB1.ExpectedManagedSha256);
        Assert.Equal(FileOriginalState.Existing, d3d11RecB1.OriginalState);
        Assert.Equal(ComputeSha256(nativeD3d11B), d3d11RecB1.OriginalSha256);
        Assert.Equal(nativeD3d11B, File.ReadAllBytes(Path.Combine(backupsDir, d3d11RecB1.BackupRelativePath!)));
        Assert.Equal(FileOriginalState.Missing, dxgiRecB1.OriginalState);
        Assert.Null(dxgiRecB1.OriginalSha256);
        Assert.Null(dxgiRecB1.BackupRelativePath);
        Assert.Equal(FileOriginalState.Existing, confRecB1.OriginalState);
        Assert.Equal(ComputeSha256(originalConfB), confRecB1.OriginalSha256);
        Assert.Equal(originalConfB, File.ReadAllBytes(Path.Combine(backupsDir, confRecB1.BackupRelativePath!)));

        var configDictB1 = ParseConfigAssignments(File.ReadAllText(Path.Combine(gameDirB, "dxvk.conf")));
        Assert.Equal("fps,devinfo", configDictB1["dxvk.hud"]);
        Assert.Equal("60", configDictB1["dxvk.maxFrameRate"]);
        Assert.Equal("compact", configDictB1["dxvk.hudExtra"]);
        Assert.Equal("144", configDictB1["dxvk.maxFrameRateTarget"]);
        Assert.Equal("2", configDictB1["user.settingB"]);

        // Target C verification (synthetic Missing baseline for dxvk.conf)
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x64:d3d11"), File.ReadAllBytes(Path.Combine(gameDirC, "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.5:x64:dxgi"), File.ReadAllBytes(Path.Combine(gameDirC, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(gameDirC, "dxvk.conf")));
        var d3d11RecC1 = instC1.FindManagedFile("d3d11.dll")!;
        var dxgiRecC1 = instC1.FindManagedFile("dxgi.dll")!;
        var confRecC1 = instC1.FindManagedFile("dxvk.conf")!;
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirC, "d3d11.dll"))), d3d11RecC1.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirC, "dxgi.dll"))), dxgiRecC1.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirC, "dxvk.conf"))), confRecC1.ExpectedManagedSha256);
        Assert.Equal(FileOriginalState.Existing, d3d11RecC1.OriginalState);
        Assert.Equal(ComputeSha256(nativeD3d11C), d3d11RecC1.OriginalSha256);
        Assert.Equal(nativeD3d11C, File.ReadAllBytes(Path.Combine(backupsDir, d3d11RecC1.BackupRelativePath!)));
        Assert.Equal(FileOriginalState.Missing, dxgiRecC1.OriginalState);
        Assert.Null(dxgiRecC1.OriginalSha256);
        Assert.Null(dxgiRecC1.BackupRelativePath);
        Assert.Equal(FileOriginalState.Missing, confRecC1.OriginalState);
        Assert.Null(confRecC1.OriginalSha256);
        Assert.Null(confRecC1.BackupRelativePath);

        var configDictC1 = ParseConfigAssignments(File.ReadAllText(Path.Combine(gameDirC, "dxvk.conf")));
        Assert.Equal("fps,devinfo", configDictC1["dxvk.hud"]);
        Assert.Equal("60", configDictC1["dxvk.maxFrameRate"]);
        Assert.Equal(2, configDictC1.Count);
        Assert.False(configDictC1.ContainsKey("dxvk.hudExtra"));
        Assert.False(configDictC1.ContainsKey("dxvk.maxFrameRateTarget"));
        Assert.False(configDictC1.ContainsKey("user.settingA"));
        Assert.False(configDictC1.ContainsKey("user.settingB"));
        Assert.False(configDictC1.ContainsKey("user.settingC"));
        Assert.False(configDictC1.ContainsKey("user.settingD"));

        // 3. Reload services & stores; verify persisted policy before changing only selectors
        var stack2 = ConstructServiceStack(appDataDir, httpClient);
        Assert.Equal(GlobalManagementPolicy.Manual, stack2.SettingsStore.GlobalPolicy);
        Assert.Equal(expectedExeOrder, stack2.ProfileStore.GetAll().Select(p => p.ExeName));

        var profileA2 = stack2.ProfileStore.GetOrCreate(exePathA);
        var profileB2 = stack2.ProfileStore.GetOrCreate(exePathB);
        var profileC2 = stack2.ProfileStore.GetOrCreate(exePathC);
        var profileD2 = stack2.ProfileStore.GetOrCreate(exePathD);

        var instA2 = stack2.LibraryStore.FindByInstallationPath(gameDirA)!;
        var instB2 = stack2.LibraryStore.FindByInstallationPath(gameDirB)!;
        var instC2 = stack2.LibraryStore.FindByInstallationPath(gameDirC)!;
        var instD2 = stack2.LibraryStore.FindByInstallationPath(gameDirD)!;

        // Verify reloaded selector/API/architecture intent before changing selectors
        Assert.Equal(GraphicsApi.DX11, profileA2.Api);
        Assert.Equal("x64", profileA2.Architecture);
        Assert.Equal("v2.5", profileA2.DxvkVersion);
        Assert.True(profileA2.DxvkEnabled);
        Assert.Equal(ManagementMode.UseGlobal, instA2.ManagementPolicy.Mode);
        Assert.Equal("v2.5", instA2.ManagementPolicy.PinnedDxvkVersion);
        Assert.Equal("x64", instA2.ManagedDxvkArchitecture);
        Assert.Equal(GraphicsApi.DX11, instA2.FindExecutable("GameA.exe")!.LastKnownApi);
        Assert.Equal("x64", instA2.FindExecutable("GameA.exe")!.LastKnownArchitecture);
        Assert.Equal("v2.5", instA2.ManagedDxvkVersion);

        Assert.Equal(GraphicsApi.DX11, profileB2.Api);
        Assert.Equal("x64", profileB2.Architecture);
        Assert.Equal("v2.5", profileB2.DxvkVersion);
        Assert.True(profileB2.DxvkEnabled);
        Assert.Equal(ManagementMode.UseGlobal, instB2.ManagementPolicy.Mode);
        Assert.Equal("v2.5", instB2.ManagementPolicy.PinnedDxvkVersion);
        Assert.Equal("x64", instB2.ManagedDxvkArchitecture);
        Assert.Equal(GraphicsApi.DX11, instB2.FindExecutable("GameB.exe")!.LastKnownApi);
        Assert.Equal("x64", instB2.FindExecutable("GameB.exe")!.LastKnownArchitecture);
        Assert.Equal("v2.5", instB2.ManagedDxvkVersion);

        Assert.Equal(GraphicsApi.DX11, profileC2.Api);
        Assert.Equal("x64", profileC2.Architecture);
        Assert.Equal("v2.5", profileC2.DxvkVersion);
        Assert.True(profileC2.DxvkEnabled);
        Assert.Equal(ManagementMode.UseGlobal, instC2.ManagementPolicy.Mode);
        Assert.Equal("v2.5", instC2.ManagementPolicy.PinnedDxvkVersion);
        Assert.Equal("x64", instC2.ManagedDxvkArchitecture);
        Assert.Equal(GraphicsApi.DX11, instC2.FindExecutable("GameC.exe")!.LastKnownApi);
        Assert.Equal("x64", instC2.FindExecutable("GameC.exe")!.LastKnownArchitecture);
        Assert.Equal("v2.5", instC2.ManagedDxvkVersion);

        Assert.Equal(GraphicsApi.DX11, profileD2.Api);
        Assert.Equal("x64", profileD2.Architecture);
        Assert.False(profileD2.DxvkEnabled);
        Assert.Equal(ManagementMode.UseGlobal, instD2.ManagementPolicy.Mode);
        Assert.Equal("v2.5", instD2.ManagementPolicy.PinnedDxvkVersion);
        Assert.Equal(GraphicsApi.DX11, instD2.FindExecutable("GameD.exe")!.LastKnownApi);
        Assert.Equal("x64", instD2.FindExecutable("GameD.exe")!.LastKnownArchitecture);
        Assert.Equal(RestorationState.Restored, instD2.RestorationState);
        Assert.True(string.IsNullOrEmpty(instD2.ManagedDxvkVersion));

        // Change ONLY persisted selectors: A/C select v2.6, B selects deliberately unavailable exact version
        // (Do NOT preassign profile.DxvkVersion)
        instA2.ManagementPolicy.PinnedDxvkVersion = "v2.6";
        stack2.LibraryStore.Save(instA2);

        instB2.ManagementPolicy.PinnedDxvkVersion = "v2.7-unavailable";
        stack2.LibraryStore.Save(instB2);

        instC2.ManagementPolicy.PinnedDxvkVersion = "v2.6";
        stack2.LibraryStore.Save(instC2);

        // Snapshot complete raw B game, backup, and source resources before UpdateAllEnabled
        var preUpdateGameSnapshotB = SnapshotResources(gameDirB);
        var preUpdateBackupsSnapshot = SnapshotResources(backupsDir);
        var preUpdateOwnershipB = CaptureOwnership(profileB2, instB2);

        // Reconstruct stack from disk and call UpdateAllEnabledAsync
        var stack3 = ConstructServiceStack(appDataDir, httpClient);
        Assert.Equal(GlobalManagementPolicy.Manual, stack3.SettingsStore.GlobalPolicy);
        Assert.Equal(expectedExeOrder, stack3.ProfileStore.GetAll().Select(p => p.ExeName));

        var updateResults = await stack3.Manager.UpdateAllEnabledAsync();

        // Require traversal order to match profile enumeration/insertion order
        var expectedUpdateKeys = targetOrder == "BeforeHealthyTargets"
            ? new[] { exePathB, exePathA, exePathC }
            : new[] { exePathA, exePathB, exePathC };
        Assert.Equal(expectedUpdateKeys, updateResults.Keys);

        // Require A/C Applied, B Failed with meaningful retained batch refusal despite later successes
        Assert.Equal(DxvkActionResult.Applied, updateResults[exePathA]);
        Assert.Equal(DxvkActionResult.Failed, updateResults[exePathB]);
        Assert.Equal(DxvkActionResult.Applied, updateResults[exePathC]);
        Assert.False(updateResults.ContainsKey(exePathD)); // D unchanged/unselected, omitted by API

        Assert.NotNull(stack3.Manager.LastRefusalReason);
        Assert.Contains("v2.7-unavailable", stack3.Manager.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Zero additional HTTP calls
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Compare complete raw B game/backup sets and meaningful ownership before/after
        AssertResourcesUnchanged(preUpdateGameSnapshotB, gameDirB);
        AssertResourcesUnchanged(preUpdateBackupsSnapshot, backupsDir);
        var profileB3 = stack3.ProfileStore.GetOrCreate(exePathB);
        var instB3 = stack3.LibraryStore.FindByInstallationPath(gameDirB)!;
        AssertOwnershipUnchanged(preUpdateOwnershipB, profileB3, instB3);
        Assert.Equal("v2.5", profileB3.DxvkVersion);
        Assert.Equal("v2.5", instB3.ManagedDxvkVersion);
        Assert.Equal("v2.7-unavailable", instB3.ManagementPolicy.PinnedDxvkVersion);

        // Verify A/C newer selected bytes, hashes, and actual versions with original baselines retained
        var profileA3 = stack3.ProfileStore.GetOrCreate(exePathA);
        var instA3 = stack3.LibraryStore.FindByInstallationPath(gameDirA)!;
        Assert.Equal("v2.6", profileA3.DxvkVersion);
        Assert.Equal("v2.6", instA3.ManagedDxvkVersion);
        Assert.Equal(Encoding.UTF8.GetBytes("v2.6:x64:d3d11"), File.ReadAllBytes(Path.Combine(gameDirA, "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.6:x64:dxgi"), File.ReadAllBytes(Path.Combine(gameDirA, "dxgi.dll")));
        var d3d11RecA3 = instA3.FindManagedFile("d3d11.dll")!;
        var dxgiRecA3 = instA3.FindManagedFile("dxgi.dll")!;
        var confRecA3 = instA3.FindManagedFile("dxvk.conf")!;
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirA, "d3d11.dll"))), d3d11RecA3.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirA, "dxgi.dll"))), dxgiRecA3.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirA, "dxvk.conf"))), confRecA3.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(nativeD3d11A), d3d11RecA3.OriginalSha256);
        Assert.Equal(nativeD3d11A, File.ReadAllBytes(Path.Combine(backupsDir, d3d11RecA3.BackupRelativePath!)));
        Assert.Equal(ComputeSha256(originalConfA), confRecA3.OriginalSha256);

        var configDictA3 = ParseConfigAssignments(File.ReadAllText(Path.Combine(gameDirA, "dxvk.conf")));
        Assert.Equal("fps,devinfo", configDictA3["dxvk.hud"]);
        Assert.Equal("60", configDictA3["dxvk.maxFrameRate"]);
        Assert.Equal("full", configDictA3["dxvk.hudExtra"]);
        Assert.Equal("120", configDictA3["dxvk.maxFrameRateTarget"]);
        Assert.Equal("1", configDictA3["user.settingA"]);

        var profileC3 = stack3.ProfileStore.GetOrCreate(exePathC);
        var instC3 = stack3.LibraryStore.FindByInstallationPath(gameDirC)!;
        Assert.Equal("v2.6", profileC3.DxvkVersion);
        Assert.Equal("v2.6", instC3.ManagedDxvkVersion);
        Assert.Equal(Encoding.UTF8.GetBytes("v2.6:x64:d3d11"), File.ReadAllBytes(Path.Combine(gameDirC, "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes("v2.6:x64:dxgi"), File.ReadAllBytes(Path.Combine(gameDirC, "dxgi.dll")));
        var d3d11RecC3 = instC3.FindManagedFile("d3d11.dll")!;
        var dxgiRecC3 = instC3.FindManagedFile("dxgi.dll")!;
        var confRecC3 = instC3.FindManagedFile("dxvk.conf")!;
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirC, "d3d11.dll"))), d3d11RecC3.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirC, "dxgi.dll"))), dxgiRecC3.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(File.ReadAllBytes(Path.Combine(gameDirC, "dxvk.conf"))), confRecC3.ExpectedManagedSha256);
        Assert.Equal(ComputeSha256(nativeD3d11C), d3d11RecC3.OriginalSha256);
        Assert.Equal(nativeD3d11C, File.ReadAllBytes(Path.Combine(backupsDir, d3d11RecC3.BackupRelativePath!)));
        Assert.Null(confRecC3.OriginalSha256);

        var configDictC3 = ParseConfigAssignments(File.ReadAllText(Path.Combine(gameDirC, "dxvk.conf")));
        Assert.Equal("fps,devinfo", configDictC3["dxvk.hud"]);
        Assert.Equal("60", configDictC3["dxvk.maxFrameRate"]);
        Assert.Equal(2, configDictC3.Count);
        Assert.False(configDictC3.ContainsKey("dxvk.hudExtra"));
        Assert.False(configDictC3.ContainsKey("dxvk.maxFrameRateTarget"));
        Assert.False(configDictC3.ContainsKey("user.settingA"));
        Assert.False(configDictC3.ContainsKey("user.settingB"));
        Assert.False(configDictC3.ContainsKey("user.settingC"));
        Assert.False(configDictC3.ContainsKey("user.settingD"));

        // Target D remains untouched
        AssertResourcesUnchanged(initialSnapshotD, gameDirD);

        // 4. Corrupt B's recorded original native backup while leaving its managed game DLL bytes intact
        string bBackupFile = Path.Combine(backupsDir, instB3.FindManagedFile("d3d11.dll")!.BackupRelativePath!);
        byte[] corruptedBackupBytes = new byte[] { 0xFE, 0xED, 0xFA, 0xCE, 0x99 };
        File.WriteAllBytes(bBackupFile, corruptedBackupBytes);

        // Snapshot faulted backup/game/source state and meaningful ownership
        var faultedGameSnapshotB = SnapshotResources(gameDirB);
        var faultedBackupsSnapshot = SnapshotResources(backupsDir);
        var faultedSourcesSnapshot = SnapshotResources(sourcesDir);
        var faultedOwnershipB = CaptureOwnership(profileB3, instB3);

        // Reload fresh stack from disk and call RestoreAllAsync
        var stack4 = ConstructServiceStack(appDataDir, httpClient);
        Assert.Equal(GlobalManagementPolicy.Manual, stack4.SettingsStore.GlobalPolicy);
        Assert.Equal(expectedExeOrder, stack4.ProfileStore.GetAll().Select(p => p.ExeName));

        var restoreSummary = await stack4.Manager.RestoreAllAsync();

        // Require current summary semantics: TotalManaged3, Restored2 (A/C), FailedOrAttentionRequired1 (B), AlreadyRestored1 (D), QueuedRunning0
        Assert.Equal(3, restoreSummary.TotalManaged);
        Assert.Equal(2, restoreSummary.Restored);
        Assert.Equal(1, restoreSummary.FailedOrAttentionRequired);
        Assert.Equal(1, restoreSummary.AlreadyRestored);
        Assert.Equal(0, restoreSummary.QueuedRunning);

        // Check messages identify B and its backup failure and do NOT falsely attribute refusal to A/C/D
        Assert.Single(restoreSummary.Messages);
        string refusalMsg = restoreSummary.Messages[0];
        Assert.StartsWith("GameB.exe:", refusalMsg);
        Assert.Contains("backup hash mismatch", refusalMsg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GameA.exe", refusalMsg);
        Assert.DoesNotContain("GameC.exe", refusalMsg);
        Assert.DoesNotContain("GameD.exe", refusalMsg);

        // A/C must match their complete original raw file/directory sets
        AssertResourcesUnchanged(initialSnapshotA, gameDirA);
        AssertResourcesUnchanged(initialSnapshotC, gameDirC);
        AssertResourcesUnchanged(initialSnapshotD, gameDirD);

        // B must fail safely with game/source/faulted-backup resources unchanged, active management retained
        AssertResourcesUnchanged(faultedGameSnapshotB, gameDirB);
        AssertResourcesUnchanged(faultedSourcesSnapshot, sourcesDir);
        AssertResourcesUnchanged(faultedBackupsSnapshot, backupsDir);

        var profileB4 = stack4.ProfileStore.GetOrCreate(exePathB);
        var instB4 = stack4.LibraryStore.FindByInstallationPath(gameDirB)!;
        AssertFaultedOwnershipRetained(faultedOwnershipB, profileB4, instB4);

        // Zero additional HTTP calls
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // 5. Reload again and verify exact nonempty historical ownership sets for restored A/C/D, cleared active versions
        var stack5 = ConstructServiceStack(appDataDir, httpClient);
        Assert.Equal(GlobalManagementPolicy.Manual, stack5.SettingsStore.GlobalPolicy);
        Assert.Equal(expectedExeOrder, stack5.ProfileStore.GetAll().Select(p => p.ExeName));

        var profileA5 = stack5.ProfileStore.GetOrCreate(exePathA);
        var profileB5 = stack5.ProfileStore.GetOrCreate(exePathB);
        var profileC5 = stack5.ProfileStore.GetOrCreate(exePathC);
        var profileD5 = stack5.ProfileStore.GetOrCreate(exePathD);

        var instA5 = stack5.LibraryStore.FindByInstallationPath(gameDirA)!;
        var instB5 = stack5.LibraryStore.FindByInstallationPath(gameDirB)!;
        var instC5 = stack5.LibraryStore.FindByInstallationPath(gameDirC)!;
        var instD5 = stack5.LibraryStore.FindByInstallationPath(gameDirD)!;

        // Restored A/C/D history verification
        AssertRestoredHistory(instA5, profileA5, ComputeSha256(nativeD3d11A), ComputeSha256(originalConfA), backupsDir);
        AssertRestoredHistory(instC5, profileC5, ComputeSha256(nativeD3d11C), null, backupsDir);
        AssertRestoredHistory(instD5, profileD5, ComputeSha256(nativeD3d11D), ComputeSha256(originalConfD), backupsDir);

        // B remains in AttentionRequired with active management and exact faulted ownership retained
        AssertFaultedOwnershipRetained(faultedOwnershipB, profileB5, instB5);

        // Repeat RestoreAll once: TotalManaged1, Restored0, FailedOrAttentionRequired1, AlreadyRestored3, QueuedRunning0
        var repeatSummary = await stack5.Manager.RestoreAllAsync();

        Assert.Equal(1, repeatSummary.TotalManaged);
        Assert.Equal(0, repeatSummary.Restored);
        Assert.Equal(1, repeatSummary.FailedOrAttentionRequired);
        Assert.Equal(3, repeatSummary.AlreadyRestored);
        Assert.Equal(0, repeatSummary.QueuedRunning);

        Assert.Single(repeatSummary.Messages);
        Assert.StartsWith("GameB.exe:", repeatSummary.Messages[0]);
        Assert.Contains("backup hash mismatch", repeatSummary.Messages[0], StringComparison.OrdinalIgnoreCase);

        // B still unresolved without silent repair, restored resources unchanged
        AssertResourcesUnchanged(initialSnapshotA, gameDirA);
        AssertResourcesUnchanged(initialSnapshotC, gameDirC);
        AssertResourcesUnchanged(initialSnapshotD, gameDirD);
        AssertResourcesUnchanged(faultedGameSnapshotB, gameDirB);
        AssertResourcesUnchanged(faultedSourcesSnapshot, sourcesDir);
        AssertResourcesUnchanged(faultedBackupsSnapshot, backupsDir);

        // Reload fresh stack to verify persisted state after repeat RestoreAll
        var stack6 = ConstructServiceStack(appDataDir, httpClient);
        var profileA6 = stack6.ProfileStore.GetOrCreate(exePathA);
        var profileB6 = stack6.ProfileStore.GetOrCreate(exePathB);
        var profileC6 = stack6.ProfileStore.GetOrCreate(exePathC);
        var profileD6 = stack6.ProfileStore.GetOrCreate(exePathD);

        var instA6 = stack6.LibraryStore.FindByInstallationPath(gameDirA)!;
        var instB6 = stack6.LibraryStore.FindByInstallationPath(gameDirB)!;
        var instC6 = stack6.LibraryStore.FindByInstallationPath(gameDirC)!;
        var instD6 = stack6.LibraryStore.FindByInstallationPath(gameDirD)!;

        AssertRestoredHistory(instA6, profileA6, ComputeSha256(nativeD3d11A), ComputeSha256(originalConfA), backupsDir);
        AssertRestoredHistory(instC6, profileC6, ComputeSha256(nativeD3d11C), null, backupsDir);
        AssertRestoredHistory(instD6, profileD6, ComputeSha256(nativeD3d11D), ComputeSha256(originalConfD), backupsDir);
        AssertFaultedOwnershipRetained(faultedOwnershipB, profileB6, instB6);

        // Exact complete expected backup files retained
        var finalBackupsSnapshot = SnapshotResources(backupsDir);
        Assert.Equal(7, finalBackupsSnapshot.Files.Count);
        Assert.Equal(nativeD3d11A, finalBackupsSnapshot.Files[Path.Combine(idA, "d3d11.dll")]);
        Assert.Equal(originalConfA, finalBackupsSnapshot.Files[Path.Combine(idA, "dxvk.conf")]);
        Assert.Equal(corruptedBackupBytes, finalBackupsSnapshot.Files[Path.Combine(idB, "d3d11.dll")]);
        Assert.Equal(originalConfB, finalBackupsSnapshot.Files[Path.Combine(idB, "dxvk.conf")]);
        Assert.Equal(nativeD3d11C, finalBackupsSnapshot.Files[Path.Combine(idC, "d3d11.dll")]);
        Assert.Equal(nativeD3d11D, finalBackupsSnapshot.Files[Path.Combine(idD, "d3d11.dll")]);
        Assert.Equal(originalConfD, finalBackupsSnapshot.Files[Path.Combine(idD, "dxvk.conf")]);
        Assert.Contains(idA, finalBackupsSnapshot.Directories);
        Assert.Contains(idB, finalBackupsSnapshot.Directories);
        Assert.Contains(idC, finalBackupsSnapshot.Directories);
        Assert.Contains(idD, finalBackupsSnapshot.Directories);

        // Sources remain untouched
        AssertResourcesUnchanged(acquiredSourcesSnapshot, sourcesDir);

        // Zero additional HTTP calls throughout the entire suite
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);
    }
}
