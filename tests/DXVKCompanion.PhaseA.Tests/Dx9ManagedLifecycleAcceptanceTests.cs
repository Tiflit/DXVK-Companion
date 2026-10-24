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

public sealed class Dx9ManagedLifecycleAcceptanceTests
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
    [InlineData("x32", true)]
    [InlineData("x32", false)]
    [InlineData("x64", true)]
    [InlineData("x64", false)]
    public async Task Dx9ManagedLifecycle_PersistedManual_PreservesBaselinesAndRestoresByteIdentical(
        string architecture,
        bool preexistingNativeBaseline)
    {
        using var testDir = new SyntheticTestDirectory();
        string gameDir = Path.Combine(testDir.RootPath, $"SyntheticGame_{architecture}_{preexistingNativeBaseline}");
        Directory.CreateDirectory(gameDir);

        string emptySubDir = Path.Combine(gameDir, "Data", "EmptySubDir");
        Directory.CreateDirectory(emptySubDir);

        string exePath = Path.Combine(gameDir, "Game.exe");
        byte[] exeBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 };
        File.WriteAllBytes(exePath, exeBytes);

        string sentinelPath = Path.Combine(gameDir, "sentinel.bin");
        byte[] sentinelBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };
        File.WriteAllBytes(sentinelPath, sentinelBytes);

        // Optional unrelated native d3d11 and dxgi files remain byte-identical and unowned throughout
        string unrelatedD3D11Path = Path.Combine(gameDir, "d3d11.dll");
        byte[] unrelatedD3D11Bytes = Encoding.UTF8.GetBytes($"unrelated-native-d3d11-content-do-not-touch-{architecture}");
        File.WriteAllBytes(unrelatedD3D11Path, unrelatedD3D11Bytes);
        var unrelatedD3D11Identity = FileIdentity.Capture(unrelatedD3D11Path);

        string unrelatedDxgiPath = Path.Combine(gameDir, "dxgi.dll");
        byte[] unrelatedDxgiBytes = Encoding.UTF8.GetBytes($"unrelated-native-dxgi-content-do-not-touch-{architecture}");
        File.WriteAllBytes(unrelatedDxgiPath, unrelatedDxgiBytes);
        var unrelatedDxgiIdentity = FileIdentity.Capture(unrelatedDxgiPath);

        string d3d9Path = Path.Combine(gameDir, "d3d9.dll");
        string confPath = Path.Combine(gameDir, "dxvk.conf");

        byte[]? originalD3D9 = null;
        byte[]? originalConf = null;
        SafetyFileIdentity? originalD3D9Identity = null;
        SafetyFileIdentity? originalConfIdentity = null;

        if (preexistingNativeBaseline)
        {
            originalD3D9 = Encoding.UTF8.GetBytes($"native-d3d9-original-{architecture}-baseline-bytes");
            File.WriteAllBytes(d3d9Path, originalD3D9);
            originalD3D9Identity = FileIdentity.Capture(d3d9Path);

            // Raw mixed-newline and no-final-newline configuration with prefix lookalikes
            originalConf = Encoding.UTF8.GetBytes(
                "# Preexisting user dxvk.conf for DX9 Game\r\n" +
                "# Distinctive comments and settings\n" +
                "user.customSetting = 1\n" +
                "dxvk.hud = compiler\n" +
                "dxvk.maxFrameRate = 30\n" +
                "dxvk.hudExtra = full\n" +
                "dxvk.maxFrameRateTarget = 120\n" +
                "user.unrelatedSuffix = keep");
            File.WriteAllBytes(confPath, originalConf);
            originalConfIdentity = FileIdentity.Capture(confPath);
        }

        if (!preexistingNativeBaseline)
        {
            Assert.False(File.Exists(d3d9Path));
            Assert.False(File.Exists(confPath));
        }

        var originalGameSnapshot = SnapshotResources(gameDir);

        string appDataDir = Path.Combine(testDir.RootPath, $"AppData_{architecture}_{preexistingNativeBaseline}");
        string profilesDir = Path.Combine(appDataDir, "Profiles");
        string backupsDir = Path.Combine(profilesDir, "Backups");
        string sourcesDir = Path.Combine(appDataDir, "Sources");

        var transport = new LifecycleHttpTransport("v2.5", "v2.6");
        using var httpClient = new HttpClient(transport);

        // 1. Acquire two distinguishable official releases into shared source library
        var stack1 = ConstructServiceStack(appDataDir, httpClient);

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
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(sourcesDir, "v2.5"), "v2.5", out bool v25ManifestVerified));
        Assert.True(v25ManifestVerified);
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d9"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.5", architecture, "d3d9.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d11"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.5", architecture, "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.5:{architecture}:dxgi"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.5", architecture, "dxgi.dll")));

        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", "companion-release.json")));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(sourcesDir, "v2.6"), "v2.6", out bool v26ManifestVerified));
        Assert.True(v26ManifestVerified);
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:d3d9"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.6", architecture, "d3d9.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:d3d11"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.6", architecture, "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:dxgi"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.6", architecture, "dxgi.dll")));

        int requestCountAfterAcquisition = transport.RequestCount;
        Assert.True(requestCountAfterAcquisition > 0);

        // Mark transport unavailable: require zero additional network calls across install, update, reapply, restore
        transport.RejectAllRequests = true;

        // 2. Install exact older release into DX9 synthetic game via manager without targetVersion override
        var profile1 = stack1.ProfileStore.GetOrCreate(exePath);
        profile1.Api = GraphicsApi.DX9;
        profile1.Architecture = architecture;
        profile1.HudEnabled = true;
        profile1.FrameLimit = 60;
        profile1.DxvkVersion = "v2.5";
        stack1.ProfileStore.Save(profile1);

        var inst1 = stack1.LibraryStore.GetOrCreateInstallation(gameDir, "SyntheticGame");
        var exeRecord1 = inst1.GetOrAddExecutable("Game.exe", "Game.exe");
        exeRecord1.LastKnownApi = GraphicsApi.DX9;
        exeRecord1.LastKnownArchitecture = architecture;
        inst1.ManagementPolicy = ManagementPolicy.UseGlobal();
        inst1.ManagementPolicy.PinnedDxvkVersion = "v2.5";
        stack1.LibraryStore.Save(inst1);
        string installationId = inst1.Id;

        var installResult = await stack1.Manager.RequestEnableByPathAsync(profile1);
        Assert.Equal(DxvkActionResult.Applied, installResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Verify only d3d9.dll and dxvk.conf are managed; unrelated native dlls untouched
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d9"), File.ReadAllBytes(d3d9Path));
        Assert.Equal(unrelatedD3D11Bytes, File.ReadAllBytes(unrelatedD3D11Path));
        Assert.Equal(unrelatedDxgiBytes, File.ReadAllBytes(unrelatedDxgiPath));
        Assert.Equal(unrelatedD3D11Identity, FileIdentity.Capture(unrelatedD3D11Path));
        Assert.Equal(unrelatedDxgiIdentity, FileIdentity.Capture(unrelatedDxgiPath));
        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
        Assert.True(Directory.Exists(emptySubDir));

        Assert.True(File.Exists(confPath));
        string confContent1 = File.ReadAllText(confPath);
        var assignments1 = ParseConfigAssignments(confContent1);
        Assert.True(assignments1.ContainsKey("dxvk.hud"));
        Assert.Equal("fps,devinfo", assignments1["dxvk.hud"]);
        Assert.True(assignments1.ContainsKey("dxvk.maxFrameRate"));
        Assert.Equal("60", assignments1["dxvk.maxFrameRate"]);

        var inst1AfterInstall = stack1.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst1AfterInstall);
        Assert.Equal("v2.5", inst1AfterInstall!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, inst1AfterInstall.RestorationState);
        Assert.Equal(ManagementMode.UseGlobal, inst1AfterInstall.ManagementPolicy.Mode);
        Assert.Equal("v2.5", inst1AfterInstall.ManagementPolicy.PinnedDxvkVersion);
        Assert.True(inst1AfterInstall.Configuration.HudEnabled);
        Assert.Equal(60, inst1AfterInstall.Configuration.FrameLimit);
        Assert.True(inst1AfterInstall.Configuration.FrameLimitEnabled);
        Assert.Equal(2, inst1AfterInstall.ManagedFiles.Count);

        var d3d9Record1 = inst1AfterInstall.FindManagedFile("d3d9.dll");
        Assert.NotNull(d3d9Record1);
        Assert.Equal("v2.5", d3d9Record1!.ManagedDxvkVersion);
        Assert.Equal(FileIdentity.Capture(d3d9Path).Sha256, d3d9Record1.ExpectedManagedSha256);

        var confRecord1 = inst1AfterInstall.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord1);
        Assert.Null(confRecord1!.ManagedDxvkVersion);
        Assert.Equal(FileIdentity.Capture(confPath).Sha256, confRecord1.ExpectedManagedSha256);

        Assert.Null(inst1AfterInstall.FindManagedFile("d3d11.dll"));
        Assert.Null(inst1AfterInstall.FindManagedFile("dxgi.dll"));

        if (preexistingNativeBaseline)
        {
            Assert.Equal("full", assignments1["dxvk.hudExtra"]);
            Assert.Equal("120", assignments1["dxvk.maxFrameRateTarget"]);
            Assert.Equal(FileOriginalState.Existing, d3d9Record1.OriginalState);
            Assert.Equal(originalD3D9Identity!.Sha256, d3d9Record1.OriginalSha256);
            Assert.Equal(Path.Combine(installationId, "d3d9.dll"), d3d9Record1.BackupRelativePath);
            string d3d9BackupPath = Path.Combine(backupsDir, d3d9Record1.BackupRelativePath!);
            Assert.True(File.Exists(d3d9BackupPath));
            Assert.Equal(originalD3D9, File.ReadAllBytes(d3d9BackupPath));

            Assert.Equal(FileOriginalState.Existing, confRecord1.OriginalState);
            Assert.Equal(originalConfIdentity!.Sha256, confRecord1.OriginalSha256);
            Assert.Equal(Path.Combine(installationId, "dxvk.conf"), confRecord1.BackupRelativePath);
            string confBackupPath = Path.Combine(backupsDir, confRecord1.BackupRelativePath!);
            Assert.True(File.Exists(confBackupPath));
            Assert.Equal(originalConf, File.ReadAllBytes(confBackupPath));
        }
        else
        {
            Assert.Equal(FileOriginalState.Missing, d3d9Record1.OriginalState);
            Assert.Null(d3d9Record1.OriginalSha256);
            Assert.Null(d3d9Record1.BackupRelativePath);

            Assert.Equal(FileOriginalState.Missing, confRecord1.OriginalState);
            Assert.Null(confRecord1.OriginalSha256);
            Assert.Null(confRecord1.BackupRelativePath);
        }

        var originalBackupsSnapshot = SnapshotResources(backupsDir);
        if (preexistingNativeBaseline)
        {
            Assert.NotEmpty(originalBackupsSnapshot.Files);
        }
        else
        {
            Assert.Empty(originalBackupsSnapshot.Files);
        }

        // 3. Reconstruct all services from disk, persist newer selector intent, and update without overrides
        var stack2 = ConstructServiceStack(appDataDir, httpClient);

        var inst2 = stack2.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst2);
        inst2!.ManagementPolicy = ManagementPolicy.UseGlobal();
        inst2.ManagementPolicy.PinnedDxvkVersion = "v2.6";
        stack2.LibraryStore.Save(inst2);

        var profile2 = stack2.ProfileStore.GetOrCreate(exePath);
        var updateResult = await stack2.Manager.RequestUpdateByPathAsync(profile2);
        Assert.Equal(DxvkActionResult.Applied, updateResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:d3d9"), File.ReadAllBytes(d3d9Path));
        Assert.Equal(unrelatedD3D11Bytes, File.ReadAllBytes(unrelatedD3D11Path));
        Assert.Equal(unrelatedDxgiBytes, File.ReadAllBytes(unrelatedDxgiPath));
        Assert.Equal(unrelatedD3D11Identity, FileIdentity.Capture(unrelatedD3D11Path));
        Assert.Equal(unrelatedDxgiIdentity, FileIdentity.Capture(unrelatedDxgiPath));
        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
        Assert.True(Directory.Exists(emptySubDir));

        var inst2Updated = stack2.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst2Updated);
        Assert.Equal("v2.6", inst2Updated!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, inst2Updated.RestorationState);
        Assert.Equal("v2.6", inst2Updated.ManagementPolicy.PinnedDxvkVersion);
        Assert.Equal("v2.6", profile2.DxvkVersion);

        var d3d9Record2 = inst2Updated.FindManagedFile("d3d9.dll");
        Assert.NotNull(d3d9Record2);
        Assert.Equal("v2.6", d3d9Record2!.ManagedDxvkVersion);
        Assert.Equal(FileIdentity.Capture(d3d9Path).Sha256, d3d9Record2.ExpectedManagedSha256);

        var confRecord2 = inst2Updated.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord2);
        Assert.Null(confRecord2!.ManagedDxvkVersion);

        AssertResourcesUnchanged(acquiredSourcesSnapshot, sourcesDir);
        AssertResourcesUnchanged(originalBackupsSnapshot, backupsDir);

        // 4. Reconstruct all services from disk, disable HUD/frame-limit, and reapply
        var stack3 = ConstructServiceStack(appDataDir, httpClient);

        var profile3 = stack3.ProfileStore.GetOrCreate(exePath);
        profile3.HudEnabled = false;
        profile3.FrameLimit = 0;
        stack3.ProfileStore.Save(profile3);

        var reapplyResult = await stack3.Manager.RequestReapplyByPathAsync(profile3);
        Assert.Equal(DxvkActionResult.Applied, reapplyResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Require owned config presence and ownership assertions unconditionally until Restore
        Assert.True(File.Exists(confPath));
        string confContent3 = File.ReadAllText(confPath);
        var assignments3 = ParseConfigAssignments(confContent3);
        Assert.False(assignments3.ContainsKey("dxvk.hud"));
        Assert.False(assignments3.ContainsKey("dxvk.maxFrameRate"));

        if (preexistingNativeBaseline)
        {
            Assert.True(assignments3.ContainsKey("dxvk.hudExtra"));
            Assert.Equal("full", assignments3["dxvk.hudExtra"]);
            Assert.True(assignments3.ContainsKey("dxvk.maxFrameRateTarget"));
            Assert.Equal("120", assignments3["dxvk.maxFrameRateTarget"]);
            Assert.True(assignments3.ContainsKey("user.customSetting"));
            Assert.Equal("1", assignments3["user.customSetting"]);
            Assert.True(assignments3.ContainsKey("user.unrelatedSuffix"));
            Assert.Equal("keep", assignments3["user.unrelatedSuffix"]);
            Assert.Contains("# Preexisting user dxvk.conf for DX9 Game", confContent3);
        }

        Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:d3d9"), File.ReadAllBytes(d3d9Path));
        Assert.Equal(unrelatedD3D11Bytes, File.ReadAllBytes(unrelatedD3D11Path));
        Assert.Equal(unrelatedDxgiBytes, File.ReadAllBytes(unrelatedDxgiPath));
        Assert.Equal(unrelatedD3D11Identity, FileIdentity.Capture(unrelatedD3D11Path));
        Assert.Equal(unrelatedDxgiIdentity, FileIdentity.Capture(unrelatedDxgiPath));
        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
        Assert.True(Directory.Exists(emptySubDir));

        var inst3Reapplied = stack3.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst3Reapplied);
        Assert.False(inst3Reapplied!.Configuration.HudEnabled);
        Assert.False(inst3Reapplied.Configuration.FrameLimitEnabled);
        Assert.Equal("v2.6", inst3Reapplied.ManagedDxvkVersion);

        var confRecord3 = inst3Reapplied.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord3);
        Assert.Equal(FileIdentity.Capture(confPath).Sha256, confRecord3!.ExpectedManagedSha256);

        AssertResourcesUnchanged(acquiredSourcesSnapshot, sourcesDir);
        AssertResourcesUnchanged(originalBackupsSnapshot, backupsDir);

        // 5. Reconstruct all services from disk and restore
        var stack4 = ConstructServiceStack(appDataDir, httpClient);

        var profile4 = stack4.ProfileStore.GetOrCreate(exePath);
        var restoreResult = await stack4.Manager.RequestDisableByPathAsync(profile4);
        Assert.Equal(DxvkActionResult.Applied, restoreResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Final exact original raw resources, bytes, sentinel, unrelated DLLs, and empty directories restored
        AssertResourcesUnchanged(originalGameSnapshot, gameDir);
        if (preexistingNativeBaseline)
        {
            Assert.True(File.Exists(d3d9Path));
            Assert.Equal(originalD3D9, File.ReadAllBytes(d3d9Path));
            Assert.Equal(originalD3D9Identity, FileIdentity.Capture(d3d9Path));
            Assert.True(File.Exists(confPath));
            Assert.Equal(originalConf, File.ReadAllBytes(confPath));
            Assert.Equal(originalConfIdentity, FileIdentity.Capture(confPath));
        }
        else
        {
            Assert.False(File.Exists(d3d9Path));
            Assert.False(File.Exists(confPath));
        }

        Assert.True(File.Exists(unrelatedD3D11Path));
        Assert.Equal(unrelatedD3D11Bytes, File.ReadAllBytes(unrelatedD3D11Path));
        Assert.Equal(unrelatedD3D11Identity, FileIdentity.Capture(unrelatedD3D11Path));
        Assert.True(File.Exists(unrelatedDxgiPath));
        Assert.Equal(unrelatedDxgiBytes, File.ReadAllBytes(unrelatedDxgiPath));
        Assert.Equal(unrelatedDxgiIdentity, FileIdentity.Capture(unrelatedDxgiPath));
        Assert.True(File.Exists(sentinelPath));
        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
        Assert.True(Directory.Exists(emptySubDir));

        var inst4Restored = stack4.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst4Restored);
        Assert.Null(inst4Restored!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Restored, inst4Restored.RestorationState);
        Assert.Equal(InstallationConflictFlags.None, inst4Restored.ConflictFlags);
        Assert.Null(inst4Restored.PendingAction);
        Assert.Null(inst4Restored.LastRefusalReason);
        Assert.False(profile4.DxvkEnabled);
        Assert.Equal("v2.6", profile4.DxvkVersion);
        Assert.Equal(ManagementMode.UseGlobal, inst4Restored.ManagementPolicy.Mode);
        Assert.Equal("v2.6", inst4Restored.ManagementPolicy.PinnedDxvkVersion);

        var recordedExe = inst4Restored.FindExecutable("Game.exe");
        Assert.NotNull(recordedExe);
        Assert.Equal(GraphicsApi.DX9, recordedExe!.LastKnownApi);
        Assert.Equal(architecture, recordedExe.LastKnownArchitecture);

        Assert.Equal(2, inst4Restored.ManagedFiles.Count);
        Assert.Equal(new[] { "d3d9.dll", "dxvk.conf" },
            inst4Restored.ManagedFiles.Select(record => record.RelativePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

        foreach (var mf in inst4Restored.ManagedFiles)
        {
            Assert.Null(mf.ManagedDxvkVersion);
        }

        AssertResourcesUnchanged(acquiredSourcesSnapshot, sourcesDir);
        AssertResourcesUnchanged(originalBackupsSnapshot, backupsDir);
    }

    [Theory]
    [InlineData("x32", true)]
    [InlineData("x32", false)]
    [InlineData("x64", true)]
    [InlineData("x64", false)]
    public async Task Dx9ManagedLifecycle_CorruptedOrMissingPayload_RefusesBeforeGameWrites(
        string architecture,
        bool missingPayload)
    {
        using var testDir = new SyntheticTestDirectory();
        string gameDir = Path.Combine(testDir.RootPath, $"SyntheticGame_{architecture}_{missingPayload}");
        Directory.CreateDirectory(gameDir);

        string emptySubDir = Path.Combine(gameDir, "Data", "EmptySubDir");
        Directory.CreateDirectory(emptySubDir);

        string exePath = Path.Combine(gameDir, "Game.exe");
        byte[] exeBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 };
        File.WriteAllBytes(exePath, exeBytes);

        string sentinelPath = Path.Combine(gameDir, "sentinel.bin");
        byte[] sentinelBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };
        File.WriteAllBytes(sentinelPath, sentinelBytes);

        string unrelatedD3D11Path = Path.Combine(gameDir, "d3d11.dll");
        byte[] unrelatedD3D11Bytes = Encoding.UTF8.GetBytes($"unrelated-native-d3d11-{architecture}");
        File.WriteAllBytes(unrelatedD3D11Path, unrelatedD3D11Bytes);

        string unrelatedDxgiPath = Path.Combine(gameDir, "dxgi.dll");
        byte[] unrelatedDxgiBytes = Encoding.UTF8.GetBytes($"unrelated-native-dxgi-{architecture}");
        File.WriteAllBytes(unrelatedDxgiPath, unrelatedDxgiBytes);

        string d3d9Path = Path.Combine(gameDir, "d3d9.dll");
        byte[] originalD3D9Bytes = Encoding.UTF8.GetBytes($"native-d3d9-original-{architecture}");
        File.WriteAllBytes(d3d9Path, originalD3D9Bytes);

        string confPath = Path.Combine(gameDir, "dxvk.conf");
        byte[] originalConfBytes = Encoding.UTF8.GetBytes("# user config\nuser.setting = 1\n");
        File.WriteAllBytes(confPath, originalConfBytes);

        var originalGameSnapshot = SnapshotResources(gameDir);

        string appDataDir = Path.Combine(testDir.RootPath, $"AppData_{architecture}_{missingPayload}");
        string profilesDir = Path.Combine(appDataDir, "Profiles");
        string backupsDir = Path.Combine(profilesDir, "Backups");
        string sourcesDir = Path.Combine(appDataDir, "Sources");

        var transport = new LifecycleHttpTransport("v2.5", "v2.6");
        using var httpClient = new HttpClient(transport);

        var stack = ConstructServiceStack(appDataDir, httpClient);

        // Acquire both releases
        var acq25 = await stack.Manager.AcquireOfficialReleaseAsync("v2.5");
        Assert.Equal("v2.5", acq25.Version);

        var acq26 = await stack.Manager.AcquireOfficialReleaseAsync("v2.6");
        Assert.Equal("v2.6", acq26.Version);

        // 1. Induce missing or corrupted payload fault on v2.5
        string payload25 = Path.Combine(sourcesDir, "v2.5", architecture, "d3d9.dll");
        if (missingPayload)
        {
            File.Delete(payload25);
            Assert.False(File.Exists(payload25));
        }
        else
        {
            File.WriteAllBytes(payload25, Encoding.UTF8.GetBytes("corrupted-v2.5-payload-bytes"));
        }

        var sourcesSnapshotFault25 = SnapshotResources(sourcesDir);
        var backupsSnapshotEmpty = SnapshotResources(backupsDir);

        transport.RejectAllRequests = true;
        int requestCountBeforeAttempt = transport.RequestCount;

        var profile = stack.ProfileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX9;
        profile.Architecture = architecture;
        profile.DxvkVersion = "v2.5";
        stack.ProfileStore.Save(profile);

        var inst = stack.LibraryStore.GetOrCreateInstallation(gameDir, "SyntheticGame");
        var exeRecord = inst.GetOrAddExecutable("Game.exe", "Game.exe");
        exeRecord.LastKnownApi = GraphicsApi.DX9;
        exeRecord.LastKnownArchitecture = architecture;
        inst.ManagementPolicy = ManagementPolicy.UseGlobal();
        inst.ManagementPolicy.PinnedDxvkVersion = "v2.5";
        stack.LibraryStore.Save(inst);

        // Attempt initial install: must refuse before game writes
        var installResult = await stack.Manager.RequestEnableByPathAsync(profile);
        Assert.Equal(DxvkActionResult.Failed, installResult);
        Assert.Equal(requestCountBeforeAttempt, transport.RequestCount);

        Assert.NotNull(stack.Manager.LastRefusalReason);
        Assert.Contains("not available locally", stack.Manager.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Game files, available sources, and backups remain unchanged
        AssertResourcesUnchanged(originalGameSnapshot, gameDir);
        AssertResourcesUnchanged(sourcesSnapshotFault25, sourcesDir);
        AssertResourcesUnchanged(backupsSnapshotEmpty, backupsDir);

        var instAfterFail = stack.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(instAfterFail);
        Assert.NotEqual(RestorationState.Managed, instAfterFail!.RestorationState);
        Assert.Null(instAfterFail.ManagedDxvkVersion);
        Assert.Null(instAfterFail.PendingAction);

        // 2. Repair v2.5 payload and perform valid installation to establish baseline backups
        File.WriteAllBytes(payload25, Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d9"));
        var validInstallResult = await stack.Manager.RequestEnableByPathAsync(profile);
        Assert.Equal(DxvkActionResult.Applied, validInstallResult);
        Assert.Equal(requestCountBeforeAttempt, transport.RequestCount);

        var gameSnapshotAfterValidInstall = SnapshotResources(gameDir);
        var backupsSnapshotAfterValidInstall = SnapshotResources(backupsDir);
        Assert.NotEmpty(backupsSnapshotAfterValidInstall.Files);

        // 3. Induce missing or corrupted payload fault on v2.6
        string payload26 = Path.Combine(sourcesDir, "v2.6", architecture, "d3d9.dll");
        if (missingPayload)
        {
            File.Delete(payload26);
            Assert.False(File.Exists(payload26));
        }
        else
        {
            File.WriteAllBytes(payload26, Encoding.UTF8.GetBytes("corrupted-v2.6-payload-bytes"));
        }

        var sourcesSnapshotFault26 = SnapshotResources(sourcesDir);

        // Persist update selector intent to v2.6 and attempt update
        inst = stack.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst);
        inst!.ManagementPolicy.PinnedDxvkVersion = "v2.6";
        stack.LibraryStore.Save(inst);

        var updateResult = await stack.Manager.RequestUpdateByPathAsync(profile);
        Assert.Equal(DxvkActionResult.Failed, updateResult);
        Assert.Equal(requestCountBeforeAttempt, transport.RequestCount);

        Assert.NotNull(stack.Manager.LastRefusalReason);
        Assert.Contains("not available locally", stack.Manager.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Existing deployment files, original baseline backups, and available sources must be preserved
        AssertResourcesUnchanged(gameSnapshotAfterValidInstall, gameDir);
        AssertResourcesUnchanged(backupsSnapshotAfterValidInstall, backupsDir);
        AssertResourcesUnchanged(sourcesSnapshotFault26, sourcesDir);

        // Installation remains Managed on v2.5 without false switch or false Applied/Restored
        var instAfterUpdateFail = stack.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(instAfterUpdateFail);
        Assert.Equal(RestorationState.Managed, instAfterUpdateFail!.RestorationState);
        Assert.Equal("v2.5", instAfterUpdateFail.ManagedDxvkVersion);
        Assert.Equal(InstallationConflictFlags.None, instAfterUpdateFail.ConflictFlags);
        Assert.Null(instAfterUpdateFail.PendingAction);
    }
}
