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

public sealed class ExternalUpdateBaselineLifecycleAcceptanceTests
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
    [InlineData("x64")]
    [InlineData("x32")]
    public async Task ExternalUpdateBaselineLifecycle_AuthorizedReapply_UpdatesAndRestoresUpdatedNativeFile(
        string architecture)
    {
        using var testDir = new SyntheticTestDirectory();
        string gameDir = Path.Combine(testDir.RootPath, $"ExternalUpdateGame_{architecture}");
        Directory.CreateDirectory(gameDir);

        string emptySubDir = Path.Combine(gameDir, "Data", "EmptySubDir");
        Directory.CreateDirectory(emptySubDir);

        string exePath = Path.Combine(gameDir, "Game.exe");
        byte[] exeBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 };
        File.WriteAllBytes(exePath, exeBytes);

        string sentinelPath = Path.Combine(gameDir, "sentinel.bin");
        byte[] sentinelBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };
        File.WriteAllBytes(sentinelPath, sentinelBytes);

        // Original native d3d11.dll Version A
        string d3d11Path = Path.Combine(gameDir, "d3d11.dll");
        byte[] nativeD3d11A = Encoding.UTF8.GetBytes($"native-d3d11-version-A:{architecture}");
        File.WriteAllBytes(d3d11Path, nativeD3d11A);

        // dxgi.dll is initially ABSENT in original game
        string dxgiPath = Path.Combine(gameDir, "dxgi.dll");
        Assert.False(File.Exists(dxgiPath));

        // Mixed-newline and no-final-newline configuration with prefix lookalikes and user lines
        string confPath = Path.Combine(gameDir, "dxvk.conf");
        byte[] originalConf = Encoding.UTF8.GetBytes(
            "# Preexisting user dxvk.conf for Game\r\n" +
            "dxvk.hudExtra = full\n" +
            "dxvk.maxFrameRateTarget = 120\r\n" +
            "user.customSetting = 1\n" +
            "user.unrelatedSuffix = keep");
        File.WriteAllBytes(confPath, originalConf);

        var initialGameSnapshot = SnapshotResources(gameDir);
        Assert.True(initialGameSnapshot.Files.ContainsKey("Game.exe"));
        Assert.True(initialGameSnapshot.Files.ContainsKey("d3d11.dll"));
        Assert.True(initialGameSnapshot.Files.ContainsKey("dxvk.conf"));
        Assert.True(initialGameSnapshot.Files.ContainsKey("sentinel.bin"));
        Assert.False(initialGameSnapshot.Files.ContainsKey("dxgi.dll"));
        Assert.Contains(Path.Combine("Data", "EmptySubDir"), initialGameSnapshot.Directories);

        string appDataDir = Path.Combine(testDir.RootPath, "AppData");
        string sourcesDir = Path.Combine(appDataDir, "Sources");
        string backupsDir = Path.Combine(appDataDir, "Profiles", "Backups");

        var transport = new LifecycleHttpTransport("v2.5", "v2.6");
        using var httpClient = new HttpClient(transport);

        // 1. Initial Service Stack & Acquisition of official releases
        var stack1 = ConstructServiceStack(appDataDir, httpClient);

        var acq25 = await stack1.Manager.AcquireOfficialReleaseAsync("v2.5");
        Assert.NotNull(acq25);
        Assert.Equal("v2.5", acq25.Version);
        Assert.True(acq25.UpstreamDigestVerified);

        var acq26 = await stack1.Manager.AcquireOfficialReleaseAsync("v2.6");
        Assert.NotNull(acq26);
        Assert.Equal("v2.6", acq26.Version);
        Assert.True(acq26.UpstreamDigestVerified);

        // Verify valid manifests, markers, and exact architecture payloads before operations
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", "companion-release.json")));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(sourcesDir, "v2.5"), "v2.5", out bool v25ManifestVerified));
        Assert.True(v25ManifestVerified);
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d11"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.5", architecture, "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.5:{architecture}:dxgi"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.5", architecture, "dxgi.dll")));

        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.6", "companion-release.json")));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(sourcesDir, "v2.6"), "v2.6", out bool v26ManifestVerified));
        Assert.True(v26ManifestVerified);
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:d3d11"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.6", architecture, "d3d11.dll")));
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:dxgi"), File.ReadAllBytes(Path.Combine(sourcesDir, "v2.6", architecture, "dxgi.dll")));

        var acquiredSourcesSnapshot = SnapshotResources(sourcesDir);
        Assert.NotEmpty(acquiredSourcesSnapshot.Files);

        // Reject all further HTTP transport requests
        transport.RejectAllRequests = true;
        int requestCountAfterAcquisition = transport.RequestCount;

        // 2. Setup profile and installation records with UseGlobal exact older selector (v2.5)
        var profile1 = stack1.ProfileStore.GetOrCreate(exePath);
        profile1.Api = GraphicsApi.DX11;
        profile1.Architecture = architecture;
        profile1.HudEnabled = true;
        profile1.FrameLimit = 60;
        profile1.DxvkVersion = "v2.5";
        stack1.ProfileStore.Save(profile1);

        var inst1 = stack1.LibraryStore.GetOrCreateInstallation(gameDir, "SyntheticGame");
        var exeRecord1 = inst1.GetOrAddExecutable("Game.exe", "Game.exe");
        exeRecord1.LastKnownApi = GraphicsApi.DX11;
        exeRecord1.LastKnownArchitecture = architecture;
        inst1.ManagementPolicy = ManagementPolicy.UseGlobal();
        inst1.ManagementPolicy.PinnedDxvkVersion = "v2.5";
        stack1.LibraryStore.Save(inst1);
        string installationId = inst1.Id;

        // Install through manager without targetVersion override
        var installResult = await stack1.Manager.RequestEnableByPathAsync(profile1);
        Assert.Equal(DxvkActionResult.Applied, installResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Verify selected DLL bytes, exact generated config keys, actual version, expected ownership hashes, backups
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d11"), File.ReadAllBytes(d3d11Path));
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.5:{architecture}:dxgi"), File.ReadAllBytes(dxgiPath));
        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
        Assert.True(Directory.Exists(emptySubDir));

        Assert.True(File.Exists(confPath));
        string confContent1 = File.ReadAllText(confPath);
        var assignments1 = ParseConfigAssignments(confContent1);
        Assert.True(assignments1.ContainsKey("dxvk.hud"));
        Assert.Equal("fps,devinfo", assignments1["dxvk.hud"]);
        Assert.True(assignments1.ContainsKey("dxvk.maxFrameRate"));
        Assert.Equal("60", assignments1["dxvk.maxFrameRate"]);
        Assert.Equal("full", assignments1["dxvk.hudExtra"]);
        Assert.Equal("120", assignments1["dxvk.maxFrameRateTarget"]);
        Assert.Equal("1", assignments1["user.customSetting"]);
        Assert.Equal("keep", assignments1["user.unrelatedSuffix"]);

        var inst1AfterInstall = stack1.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst1AfterInstall);
        Assert.Equal("v2.5", inst1AfterInstall!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, inst1AfterInstall.RestorationState);
        Assert.Equal(ManagementMode.UseGlobal, inst1AfterInstall.ManagementPolicy.Mode);
        Assert.Equal("v2.5", inst1AfterInstall.ManagementPolicy.PinnedDxvkVersion);
        Assert.True(inst1AfterInstall.Configuration.HudEnabled);
        Assert.Equal(60, inst1AfterInstall.Configuration.FrameLimit);
        Assert.True(inst1AfterInstall.Configuration.FrameLimitEnabled);

        // d3d11 was present as Version A: backed up with FileOriginalState.Existing
        var d3d11Record1 = inst1AfterInstall.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record1);
        Assert.Equal("v2.5", d3d11Record1!.ManagedDxvkVersion);
        Assert.Equal(FileOriginalState.Existing, d3d11Record1.OriginalState);
        Assert.NotNull(d3d11Record1.BackupRelativePath);
        string d3d11Backup1Path = Path.Combine(backupsDir, d3d11Record1.BackupRelativePath);
        Assert.True(File.Exists(d3d11Backup1Path));
        Assert.Equal(nativeD3d11A, File.ReadAllBytes(d3d11Backup1Path));
        Assert.Equal(ComputeSha256(nativeD3d11A), d3d11Record1.OriginalSha256);
        Assert.Equal(FileIdentity.Capture(d3d11Backup1Path).Sha256, d3d11Record1.OriginalSha256);

        // dxgi was absent in original game: synthetic Missing baseline, null backup path
        var dxgiRecord1 = inst1AfterInstall.FindManagedFile("dxgi.dll");
        Assert.NotNull(dxgiRecord1);
        Assert.Equal("v2.5", dxgiRecord1!.ManagedDxvkVersion);
        Assert.Equal(FileOriginalState.Missing, dxgiRecord1.OriginalState);
        Assert.Null(dxgiRecord1.BackupRelativePath);

        // dxvk.conf was present: backed up with FileOriginalState.Existing
        var confRecord1 = inst1AfterInstall.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord1);
        Assert.Null(confRecord1!.ManagedDxvkVersion);
        Assert.Equal(FileOriginalState.Existing, confRecord1.OriginalState);
        Assert.NotNull(confRecord1.BackupRelativePath);
        string confBackup1Path = Path.Combine(backupsDir, confRecord1.BackupRelativePath);
        Assert.True(File.Exists(confBackup1Path));
        Assert.Equal(originalConf, File.ReadAllBytes(confBackup1Path));
        Assert.Equal(ComputeSha256(originalConf), confRecord1.OriginalSha256);

        // 3. Reconstruct stack from disk & persist an older pending Update intent
        var stack2 = ConstructServiceStack(appDataDir, httpClient);
        var inst2 = stack2.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst2);
        Assert.Equal(RestorationState.Managed, inst2!.RestorationState);
        Assert.Equal("v2.5", inst2.ManagedDxvkVersion);

        inst2.PendingAction = PendingAction.Update("v2.5", "Synthetic queued update while game running");
        stack2.LibraryStore.Save(inst2);

        // Replace ONLY managed d3d11 in the game with distinguishable native B fixture bytes
        byte[] nativeD3d11B = Encoding.UTF8.GetBytes($"native-d3d11-version-B:{architecture}");
        File.WriteAllBytes(d3d11Path, nativeD3d11B);

        // Reconstruct stack from disk & inspect through real ManagedFileInspector
        var stack3 = ConstructServiceStack(appDataDir, httpClient);
        var inst3 = stack3.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst3);
        Assert.NotNull(inst3!.PendingAction);
        Assert.True(inst3.PendingAction.IsPending);

        var inspector3 = new ManagedFileInspector(stack3.LibraryStore);
        bool changed3 = inspector3.InspectInstallation(inst3);
        Assert.True(changed3);

        // Require ExternallyChanged, AttentionRequired, and stale PendingAction cleared durably
        Assert.Equal(RestorationState.AttentionRequired, inst3.RestorationState);
        Assert.Null(inst3.PendingAction);

        var d3d11Record3 = inst3.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record3);
        Assert.Equal(ManagedFileState.ExternallyChanged, d3d11Record3!.CurrentState);
        Assert.Equal(FileOriginalState.Existing, d3d11Record3.OriginalState);

        // Original A / config baselines/backups, source bytes, and game set remain untouched
        Assert.Equal(nativeD3d11A, File.ReadAllBytes(Path.Combine(backupsDir, d3d11Record3.BackupRelativePath!)));
        Assert.Equal(ComputeSha256(nativeD3d11A), d3d11Record3.OriginalSha256);
        Assert.Equal(originalConf, File.ReadAllBytes(Path.Combine(backupsDir, inst3.FindManagedFile("dxvk.conf")!.BackupRelativePath!)));
        Assert.Equal(FileOriginalState.Missing, inst3.FindManagedFile("dxgi.dll")!.OriginalState);
        Assert.Null(inst3.FindManagedFile("dxgi.dll")!.BackupRelativePath);
        AssertResourcesUnchanged(acquiredSourcesSnapshot, sourcesDir);

        Assert.Equal(nativeD3d11B, File.ReadAllBytes(d3d11Path));
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.5:{architecture}:dxgi"), File.ReadAllBytes(dxgiPath));
        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelPath));
        Assert.True(Directory.Exists(emptySubDir));

        // Reload, repeat inspection, and run pending dispatch
        var stack4 = ConstructServiceStack(appDataDir, httpClient);
        var inst4 = stack4.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst4);
        Assert.Equal(RestorationState.AttentionRequired, inst4!.RestorationState);
        Assert.Null(inst4.PendingAction);

        var inspector4 = new ManagedFileInspector(stack4.LibraryStore);
        bool changed4 = inspector4.InspectInstallation(inst4);
        Assert.False(changed4);
        Assert.Equal(RestorationState.AttentionRequired, inst4.RestorationState);
        Assert.Null(inst4.PendingAction);

        int processed = await stack4.Manager.ProcessAllPendingActionsAsync();
        Assert.Equal(0, processed);

        var inst4AfterDispatch = stack4.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst4AfterDispatch);
        Assert.Equal(RestorationState.AttentionRequired, inst4AfterDispatch!.RestorationState);
        Assert.Null(inst4AfterDispatch.PendingAction);

        // Game files and baselines not overwritten by stale dispatch
        Assert.Equal(nativeD3d11B, File.ReadAllBytes(d3d11Path));
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.5:{architecture}:dxgi"), File.ReadAllBytes(dxgiPath));
        Assert.Equal(nativeD3d11A, File.ReadAllBytes(Path.Combine(backupsDir, inst4AfterDispatch.FindManagedFile("d3d11.dll")!.BackupRelativePath!)));
        Assert.Equal(ComputeSha256(nativeD3d11A), inst4AfterDispatch.FindManagedFile("d3d11.dll")!.OriginalSha256);

        // 4. Authorized Reapply with updateBaseline = true
        var profile4 = stack4.ProfileStore.GetOrCreate(exePath);
        var reapplyResult = await stack4.Manager.RequestReapplyByPathAsync(profile4, updateBaseline: true);
        Assert.Equal(DxvkActionResult.Applied, reapplyResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Verify older-release selected DLL bytes reapplied
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.5:{architecture}:d3d11"), File.ReadAllBytes(d3d11Path));
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.5:{architecture}:dxgi"), File.ReadAllBytes(dxgiPath));

        // Exact config assignments preserved
        Assert.True(File.Exists(confPath));
        var assignments4 = ParseConfigAssignments(File.ReadAllText(confPath));
        Assert.Equal("fps,devinfo", assignments4["dxvk.hud"]);
        Assert.Equal("60", assignments4["dxvk.maxFrameRate"]);
        Assert.Equal("full", assignments4["dxvk.hudExtra"]);
        Assert.Equal("120", assignments4["dxvk.maxFrameRateTarget"]);

        // New active d3d11 original hash and backup bytes equal Version B!
        var inst4Reapplied = stack4.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst4Reapplied);
        Assert.Equal(RestorationState.Managed, inst4Reapplied!.RestorationState);
        Assert.Null(inst4Reapplied.PendingAction);

        var d3d11RecordReapplied = inst4Reapplied.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11RecordReapplied);
        Assert.Equal(FileOriginalState.Existing, d3d11RecordReapplied!.OriginalState);
        Assert.Equal(ManagedFileState.Consistent, d3d11RecordReapplied.CurrentState);
        Assert.NotNull(d3d11RecordReapplied.BackupRelativePath);

        string d3d11UpdatedBackupPath = Path.Combine(backupsDir, d3d11RecordReapplied.BackupRelativePath);
        Assert.True(File.Exists(d3d11UpdatedBackupPath));
        Assert.Equal(nativeD3d11B, File.ReadAllBytes(d3d11UpdatedBackupPath));
        Assert.Equal(ComputeSha256(nativeD3d11B), d3d11RecordReapplied.OriginalSha256);
        Assert.Equal(FileIdentity.Capture(d3d11UpdatedBackupPath).Sha256, d3d11RecordReapplied.OriginalSha256);

        // Original A is NO LONGER the active restore baseline
        Assert.NotEqual(ComputeSha256(nativeD3d11A), d3d11RecordReapplied.OriginalSha256);

        // Original config baseline unchanged
        var confRecordReapplied = inst4Reapplied.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecordReapplied);
        Assert.Equal(FileOriginalState.Existing, confRecordReapplied!.OriginalState);
        Assert.Equal(originalConf, File.ReadAllBytes(Path.Combine(backupsDir, confRecordReapplied.BackupRelativePath!)));
        Assert.Equal(ComputeSha256(originalConf), confRecordReapplied.OriginalSha256);

        // dxgi Missing baseline unchanged
        var dxgiRecordReapplied = inst4Reapplied.FindManagedFile("dxgi.dll");
        Assert.NotNull(dxgiRecordReapplied);
        Assert.Equal(FileOriginalState.Missing, dxgiRecordReapplied!.OriginalState);
        Assert.Null(dxgiRecordReapplied.BackupRelativePath);

        // 5. Reload, persist newer exact selector (v2.6) and Update through normal manager
        var stack5 = ConstructServiceStack(appDataDir, httpClient);
        var profile5 = stack5.ProfileStore.GetOrCreate(exePath);
        var inst5 = stack5.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst5);

        profile5.DxvkVersion = "v2.6";
        stack5.ProfileStore.Save(profile5);
        inst5!.ManagementPolicy.PinnedDxvkVersion = "v2.6";
        stack5.LibraryStore.Save(inst5);

        var updateResult = await stack5.Manager.RequestUpdateByPathAsync(profile5);
        Assert.Equal(DxvkActionResult.Applied, updateResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Verify newer architecture bytes and owned hashes
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:d3d11"), File.ReadAllBytes(d3d11Path));
        Assert.Equal(Encoding.UTF8.GetBytes($"v2.6:{architecture}:dxgi"), File.ReadAllBytes(dxgiPath));

        var inst5Updated = stack5.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst5Updated);
        Assert.Equal("v2.6", inst5Updated!.ManagedDxvkVersion);
        Assert.Equal("v2.6", profile5.DxvkVersion);

        // Version B and config restoration baselines unchanged across update
        var d3d11RecordUpdated = inst5Updated.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11RecordUpdated);
        Assert.Equal(FileOriginalState.Existing, d3d11RecordUpdated!.OriginalState);
        Assert.Equal(nativeD3d11B, File.ReadAllBytes(Path.Combine(backupsDir, d3d11RecordUpdated.BackupRelativePath!)));
        Assert.Equal(ComputeSha256(nativeD3d11B), d3d11RecordUpdated.OriginalSha256);

        var confRecordUpdated = inst5Updated.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecordUpdated);
        Assert.Equal(FileOriginalState.Existing, confRecordUpdated!.OriginalState);
        Assert.Equal(originalConf, File.ReadAllBytes(Path.Combine(backupsDir, confRecordUpdated.BackupRelativePath!)));
        Assert.Equal(ComputeSha256(originalConf), confRecordUpdated.OriginalSha256);

        var dxgiRecordUpdated = inst5Updated.FindManagedFile("dxgi.dll");
        Assert.NotNull(dxgiRecordUpdated);
        Assert.Equal(FileOriginalState.Missing, dxgiRecordUpdated!.OriginalState);
        Assert.Null(dxgiRecordUpdated.BackupRelativePath);

        // 6. Reload and Disable normally
        var stack6 = ConstructServiceStack(appDataDir, httpClient);
        var profile6 = stack6.ProfileStore.GetOrCreate(exePath);

        var disableResult = await stack6.Manager.RequestDisableByPathAsync(profile6);
        Assert.Equal(DxvkActionResult.Applied, disableResult);
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);

        // Compare complete expected game file/directory/raw-byte set:
        // Initial game with d3d11 replaced by B, original config exact, dxgi absent, sentinel/empty dirs retained
        var expectedFinalGameFiles = new Dictionary<string, byte[]>(initialGameSnapshot.Files, StringComparer.OrdinalIgnoreCase)
        {
            ["d3d11.dll"] = nativeD3d11B
        };
        var expectedFinalGameSnapshot = new ResourceSnapshot(expectedFinalGameFiles, initialGameSnapshot.Directories);
        AssertResourcesUnchanged(expectedFinalGameSnapshot, gameDir);

        // Historical ownership set assertions and cleared active versions
        var inst6 = stack6.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst6);
        Assert.Equal(RestorationState.Restored, inst6!.RestorationState);
        Assert.True(string.IsNullOrEmpty(inst6.ManagedDxvkVersion));
        Assert.False(profile6.DxvkEnabled);
        Assert.Null(inst6.PendingAction);

        var expectedRelativePaths = new[] { "d3d11.dll", "dxgi.dll", "dxvk.conf" };
        Assert.Equal(
            expectedRelativePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
            inst6.ManagedFiles.Select(m => m.RelativePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

        foreach (var relativePath in expectedRelativePaths)
        {
            var record = inst6.FindManagedFile(relativePath);
            Assert.NotNull(record);
            Assert.True(string.IsNullOrEmpty(record!.ManagedDxvkVersion));
            Assert.NotEmpty(record.OriginalSha256!);

            if (relativePath == "d3d11.dll")
            {
                Assert.Equal(FileOriginalState.Existing, record.OriginalState);
                Assert.NotNull(record.BackupRelativePath);
                Assert.Equal(ComputeSha256(nativeD3d11B), record.OriginalSha256);
                Assert.Equal(nativeD3d11B, File.ReadAllBytes(Path.Combine(backupsDir, record.BackupRelativePath)));
            }
            else if (relativePath == "dxgi.dll")
            {
                Assert.Equal(FileOriginalState.Missing, record.OriginalState);
                Assert.Null(record.BackupRelativePath);
            }
            else if (relativePath == "dxvk.conf")
            {
                Assert.Equal(FileOriginalState.Existing, record.OriginalState);
                Assert.NotNull(record.BackupRelativePath);
                Assert.Equal(ComputeSha256(originalConf), record.OriginalSha256);
                Assert.Equal(originalConf, File.ReadAllBytes(Path.Combine(backupsDir, record.BackupRelativePath)));
            }
        }

        // Complete retained backup set comparison
        var expectedFinalBackupsFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(installationId, "d3d11.dll")] = nativeD3d11B,
            [Path.Combine(installationId, "dxvk.conf")] = originalConf
        };
        var expectedFinalBackupsDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            installationId
        };
        var expectedFinalBackupsSnapshot = new ResourceSnapshot(expectedFinalBackupsFiles, expectedFinalBackupsDirs);
        AssertResourcesUnchanged(expectedFinalBackupsSnapshot, backupsDir);

        // Sources remain intact and untouched
        AssertResourcesUnchanged(acquiredSourcesSnapshot, sourcesDir);

        // Zero additional HTTP calls throughout the entire lifecycle
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);
    }

    [Theory]
    [InlineData("x64", "CorruptedManifest")]
    [InlineData("x32", "CorruptedManifest")]
    [InlineData("x64", "MissingPayloadDll")]
    [InlineData("x32", "MissingPayloadDll")]
    public async Task ExternalUpdateBaselineLifecycle_CorruptedOrMissingSource_RefusesReapplyBeforeMutation(
        string architecture,
        string faultType)
    {
        using var testDir = new SyntheticTestDirectory();
        string gameDir = Path.Combine(testDir.RootPath, $"ExternalUpdateFaultGame_{architecture}_{faultType}");
        Directory.CreateDirectory(gameDir);

        string emptySubDir = Path.Combine(gameDir, "Data", "EmptySubDir");
        Directory.CreateDirectory(emptySubDir);

        string exePath = Path.Combine(gameDir, "Game.exe");
        byte[] exeBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 };
        File.WriteAllBytes(exePath, exeBytes);

        string sentinelPath = Path.Combine(gameDir, "sentinel.bin");
        byte[] sentinelBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };
        File.WriteAllBytes(sentinelPath, sentinelBytes);

        // Original native d3d11 Version A
        string d3d11Path = Path.Combine(gameDir, "d3d11.dll");
        byte[] nativeD3d11A = Encoding.UTF8.GetBytes($"native-d3d11-version-A:{architecture}");
        File.WriteAllBytes(d3d11Path, nativeD3d11A);

        string confPath = Path.Combine(gameDir, "dxvk.conf");
        byte[] originalConf = Encoding.UTF8.GetBytes(
            "# Preexisting user dxvk.conf for Fault Game\r\n" +
            "dxvk.hudExtra = full\n" +
            "dxvk.maxFrameRateTarget = 120\r\n" +
            "user.customSetting = 1\n" +
            "user.unrelatedSuffix = keep");
        File.WriteAllBytes(confPath, originalConf);

        string appDataDir = Path.Combine(testDir.RootPath, "AppData");
        string sourcesDir = Path.Combine(appDataDir, "Sources");
        string backupsDir = Path.Combine(appDataDir, "Profiles", "Backups");

        var transport = new LifecycleHttpTransport("v2.5", "v2.6");
        using var httpClient = new HttpClient(transport);

        // 1. Acquire official releases
        var stack1 = ConstructServiceStack(appDataDir, httpClient);

        var acq25 = await stack1.Manager.AcquireOfficialReleaseAsync("v2.5");
        Assert.NotNull(acq25);
        Assert.Equal("v2.5", acq25.Version);

        var acq26 = await stack1.Manager.AcquireOfficialReleaseAsync("v2.6");
        Assert.NotNull(acq26);
        Assert.Equal("v2.6", acq26.Version);

        // Explicitly verify valid manifests, markers, and expected payloads
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", ".companion-acquired")));
        Assert.True(File.Exists(Path.Combine(sourcesDir, "v2.5", "companion-release.json")));
        Assert.True(DxvkReleaseAcquirer.ValidateManifest(Path.Combine(sourcesDir, "v2.5"), "v2.5", out bool v25ManifestVerified));
        Assert.True(v25ManifestVerified);

        transport.RejectAllRequests = true;
        int requestCountAfterAcquisition = transport.RequestCount;

        // 2. Initial deployment of v2.5
        var profile1 = stack1.ProfileStore.GetOrCreate(exePath);
        profile1.Api = GraphicsApi.DX11;
        profile1.Architecture = architecture;
        profile1.HudEnabled = true;
        profile1.FrameLimit = 60;
        profile1.DxvkVersion = "v2.5";
        stack1.ProfileStore.Save(profile1);

        var inst1 = stack1.LibraryStore.GetOrCreateInstallation(gameDir, "SyntheticGame");
        var exeRecord1 = inst1.GetOrAddExecutable("Game.exe", "Game.exe");
        exeRecord1.LastKnownApi = GraphicsApi.DX11;
        exeRecord1.LastKnownArchitecture = architecture;
        inst1.ManagementPolicy = ManagementPolicy.UseGlobal();
        inst1.ManagementPolicy.PinnedDxvkVersion = "v2.5";
        stack1.LibraryStore.Save(inst1);

        var installResult = await stack1.Manager.RequestEnableByPathAsync(profile1);
        Assert.Equal(DxvkActionResult.Applied, installResult);

        // 3. Queue pending update action and replace d3d11 with Version B
        var stack2 = ConstructServiceStack(appDataDir, httpClient);
        var inst2 = stack2.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst2);
        inst2!.PendingAction = PendingAction.Update("v2.5", "Synthetic queued update while game running");
        stack2.LibraryStore.Save(inst2);

        byte[] nativeD3d11B = Encoding.UTF8.GetBytes($"native-d3d11-version-B:{architecture}");
        File.WriteAllBytes(d3d11Path, nativeD3d11B);

        // Inspect through real ManagedFileInspector: cancels pending action, marks AttentionRequired
        var stack3 = ConstructServiceStack(appDataDir, httpClient);
        var inst3 = stack3.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(inst3);
        var inspector3 = new ManagedFileInspector(stack3.LibraryStore);
        bool changed3 = inspector3.InspectInstallation(inst3!);
        Assert.True(changed3);
        Assert.Equal(RestorationState.AttentionRequired, inst3.RestorationState);
        Assert.Null(inst3.PendingAction);

        // 4. Induce fault in acquired source for v2.5
        if (faultType == "CorruptedManifest")
        {
            File.WriteAllText(Path.Combine(sourcesDir, "v2.5", DxvkReleaseAcquirer.ManifestName), "{}");
        }
        else if (faultType == "MissingPayloadDll")
        {
            File.Delete(Path.Combine(sourcesDir, "v2.5", architecture, "d3d11.dll"));
        }

        // Snapshot fault state before authorized reapply request
        var faultGameSnapshot = SnapshotResources(gameDir);
        var faultSourcesSnapshot = SnapshotResources(sourcesDir);
        var faultBackupsSnapshot = SnapshotResources(backupsDir);

        // 5. Reload fresh service stack from disk and request authorized reapply
        var faultStack = ConstructServiceStack(appDataDir, httpClient);
        var faultProfile = faultStack.ProfileStore.GetOrCreate(exePath);

        var reapplyResult = await faultStack.Manager.RequestReapplyByPathAsync(faultProfile, updateBaseline: true);
        Assert.Equal(DxvkActionResult.Failed, reapplyResult);
        Assert.NotNull(faultStack.Manager.LastRefusalReason);
        Assert.Contains("source", faultStack.Manager.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // 6. Reload fresh service stack from disk again to inspect durable state and confirm zero mutation
        var reloadStack = ConstructServiceStack(appDataDir, httpClient);

        // Complete raw resource comparisons
        AssertResourcesUnchanged(faultGameSnapshot, gameDir);
        AssertResourcesUnchanged(faultSourcesSnapshot, sourcesDir);
        AssertResourcesUnchanged(faultBackupsSnapshot, backupsDir);

        // Durable ownership semantics: Version B remains in game, Version A remains active restore baseline
        Assert.Equal(nativeD3d11B, File.ReadAllBytes(d3d11Path));

        var reloadInst = reloadStack.LibraryStore.FindByInstallationPath(gameDir);
        Assert.NotNull(reloadInst);
        Assert.Equal(RestorationState.AttentionRequired, reloadInst!.RestorationState);
        Assert.Null(reloadInst.PendingAction);

        var reloadD3d11Record = reloadInst.FindManagedFile("d3d11.dll");
        Assert.NotNull(reloadD3d11Record);
        Assert.Equal(FileOriginalState.Existing, reloadD3d11Record!.OriginalState);
        Assert.NotNull(reloadD3d11Record.BackupRelativePath);
        Assert.Equal(nativeD3d11A, File.ReadAllBytes(Path.Combine(backupsDir, reloadD3d11Record.BackupRelativePath)));
        Assert.Equal(ComputeSha256(nativeD3d11A), reloadD3d11Record.OriginalSha256);

        // Zero additional HTTP calls
        Assert.Equal(requestCountAfterAcquisition, transport.RequestCount);
    }
}
