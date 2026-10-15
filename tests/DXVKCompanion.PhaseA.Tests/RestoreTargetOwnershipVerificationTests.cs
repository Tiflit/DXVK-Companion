using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class RestoreTargetOwnershipVerificationTests
{
    [Fact]
    public async Task Restore_InjectedDllOriginallyAbsent_ExternallyReplaced_RefusesAndSurvivesRestore()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-content");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-content");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11 = "original-native-d3d11-content";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalD3D11);
        var dxgiPath = Path.Combine(gameDir.RootPath, "dxgi.dll");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        // 1. Install DXVK 2.5
        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);
        Assert.Equal("dxvk-2.5-d3d11-content", File.ReadAllText(d3d11Path));
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal("dxvk-2.5-dxgi-content", File.ReadAllText(dxgiPath));

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        var dxgiRecord = installation!.FindManagedFile("dxgi.dll");
        Assert.NotNull(dxgiRecord);
        Assert.Equal(FileOriginalState.Missing, dxgiRecord!.OriginalState);
        Assert.NotNull(dxgiRecord.ExpectedManagedSha256);

        // 2. Externally replace dxgi.dll (originally absent file) with injected/unrelated content
        var injectedContent = "externally-injected-dxgi-dll-payload";
        File.WriteAllText(dxgiPath, injectedContent);

        // 3. Attempt restore: must refuse because dxgi.dll no longer matches ExpectedManagedSha256
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);

        // Invariant: Injected dxgi.dll was NOT deleted
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal(injectedContent, File.ReadAllText(dxgiPath));

        // Invariant: d3d11.dll was NOT modified or restored
        Assert.Equal("dxvk-2.5-d3d11-content", File.ReadAllText(d3d11Path));

        // Invariant: Attention required and reason recorded, version retained
        var currentInst = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(currentInst);
        Assert.Equal(RestorationState.AttentionRequired, currentInst!.RestorationState);
        Assert.NotNull(currentInst.LastRefusalReason);
        Assert.Contains("Managed target hash mismatch for restore: dxgi.dll", currentInst.LastRefusalReason);
        Assert.Equal("2.5", currentInst.ManagedDxvkVersion);
    }

    [Fact]
    public async Task Restore_DllOriginallyPresent_ExternallyReplaced_RefusesAndNotOverwrittenEvenWithValidOriginalBackup()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-content");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-content");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11 = "original-native-d3d11-content";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalD3D11);
        var dxgiPath = Path.Combine(gameDir.RootPath, "dxgi.dll");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        // 1. Install DXVK 2.5
        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        var d3d11Record = installation!.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record);
        Assert.Equal(FileOriginalState.Existing, d3d11Record!.OriginalState);

        var backupPath = Path.Combine(storageDir.RootPath, "backups", d3d11Record.BackupRelativePath!);
        Assert.True(File.Exists(backupPath));
        Assert.Equal(originalD3D11, File.ReadAllText(backupPath));

        // 2. Externally replace d3d11.dll with modified/unrelated content
        var modifiedContent = "externally-modified-d3d11-dll";
        File.WriteAllText(d3d11Path, modifiedContent);

        // 3. Attempt restore: must refuse because d3d11.dll does not match ExpectedManagedSha256,
        // even though the original backup is completely valid and present!
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);

        // Invariant: d3d11.dll was NOT overwritten with original backup
        Assert.Equal(modifiedContent, File.ReadAllText(d3d11Path));

        // Invariant: Original backup is preserved intact
        Assert.Equal(originalD3D11, File.ReadAllText(backupPath));

        // Invariant: Attention required and reason recorded, version retained
        var currentInst = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(currentInst);
        Assert.Equal(RestorationState.AttentionRequired, currentInst!.RestorationState);
        Assert.NotNull(currentInst.LastRefusalReason);
        Assert.Contains("Managed target hash mismatch for restore: d3d11.dll", currentInst.LastRefusalReason);
        Assert.Equal("2.5", currentInst.ManagedDxvkVersion);
    }

    [Fact]
    public async Task Restore_MultiFile_LaterTargetMismatch_PreventsEarlierValidFilesFromMutation()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var dxgiPath = gameDir.CreateFile("dxgi.dll", "deployed-dxvk-dxgi");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "tampered-d3d11");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            DxvkEnabled = true,
            DxvkVersion = "2.5"
        };

        var backupDir = Path.Combine(storageDir.RootPath, "backups", "test-trans");
        Directory.CreateDirectory(backupDir);
        var d3d11Backup = Path.Combine(backupDir, "d3d11.dll");
        File.WriteAllText(d3d11Backup, "original-d3d11-content");

        var installation = store.GetOrCreateInstallation(gameDir.RootPath, "MultiFileTestGame");
        installation.RestorationState = RestorationState.Managed;
        installation.ManagedDxvkVersion = "2.5";
        installation.ManagedDxvkArchitecture = "x64";
        var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
        exeRecord.LastKnownApi = GraphicsApi.DX11;
        exeRecord.LastKnownArchitecture = "x64";

        // Ordered restore inputs:
        // 1. dxgi.dll: originally absent, currently matches ExpectedManagedSha256 (would be deleted if restore ran)
        // 2. d3d11.dll: originally existing, valid backup present, but target on disk does NOT match ExpectedManagedSha256
        installation.ManagedFiles.Clear();
        installation.ManagedFiles.Add(new ManagedFileRecord
        {
            RelativePath = "dxgi.dll",
            OriginalState = FileOriginalState.Missing,
            BackupRelativePath = null,
            OriginalSha256 = null,
            ExpectedManagedSha256 = FileIdentity.Capture(dxgiPath).Sha256,
            ManagedDxvkVersion = "2.5"
        });
        installation.ManagedFiles.Add(new ManagedFileRecord
        {
            RelativePath = "d3d11.dll",
            OriginalState = FileOriginalState.Existing,
            BackupRelativePath = Path.GetRelativePath(Path.Combine(storageDir.RootPath, "backups"), d3d11Backup),
            OriginalSha256 = FileIdentity.Capture(d3d11Backup).Sha256,
            ExpectedManagedSha256 = new string('a', 64), // Mismatched expected managed hash
            ManagedDxvkVersion = "2.5"
        });
        store.Save(installation);

        var dxgiBeforeBytes = File.ReadAllBytes(dxgiPath);
        var d3d11BeforeBytes = File.ReadAllBytes(d3d11Path);
        var backupBeforeBytes = File.ReadAllBytes(d3d11Backup);

        // Attempt restore
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);

        // Invariant: dxgi.dll was NOT deleted even though it was originally absent and valid
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal(dxgiBeforeBytes, File.ReadAllBytes(dxgiPath));

        // Invariant: d3d11.dll was NOT overwritten
        Assert.True(File.Exists(d3d11Path));
        Assert.Equal(d3d11BeforeBytes, File.ReadAllBytes(d3d11Path));

        // Invariant: Backup is unchanged
        Assert.Equal(backupBeforeBytes, File.ReadAllBytes(d3d11Backup));

        // Invariant: Attention required and reason recorded, version retained
        var currentInst = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(currentInst);
        Assert.Equal(RestorationState.AttentionRequired, currentInst!.RestorationState);
        Assert.Contains("Managed target hash mismatch for restore: d3d11.dll", currentInst.LastRefusalReason);
        Assert.Equal("2.5", currentInst.ManagedDxvkVersion);
    }

    [Theory]
    [InlineData(null, "missing or malformed")]
    [InlineData("", "missing or malformed")]
    [InlineData("1234abcd", "missing or malformed")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", "missing or malformed")]
    public async Task Restore_MalformedOrMissingManagedHash_RefusesSafely(string? invalidHash, string expectedReasonFragment)
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "deployed-d3d11");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        var installation = store.GetOrCreateInstallation(gameDir.RootPath, "InvalidHashGame");
        installation.RestorationState = RestorationState.Managed;
        installation.ManagedDxvkVersion = "2.5";
        installation.ManagedFiles.Add(new ManagedFileRecord
        {
            RelativePath = "d3d11.dll",
            OriginalState = FileOriginalState.Missing,
            ExpectedManagedSha256 = invalidHash,
            ManagedDxvkVersion = "2.5"
        });
        store.Save(installation);

        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);

        // Target file preserved
        Assert.True(File.Exists(d3d11Path));
        Assert.Equal("deployed-d3d11", File.ReadAllText(d3d11Path));

        var currentInst = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(currentInst);
        Assert.Equal(RestorationState.AttentionRequired, currentInst!.RestorationState);
        Assert.Contains(expectedReasonFragment, currentInst.LastRefusalReason);
        Assert.Equal("2.5", currentInst.ManagedDxvkVersion);
    }

    [Fact]
    public async Task Restore_MissingActivelyManagedTarget_RefusesSafely()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        // d3d11.dll is NOT created on disk

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        var installation = store.GetOrCreateInstallation(gameDir.RootPath, "MissingTargetGame");
        installation.RestorationState = RestorationState.Managed;
        installation.ManagedDxvkVersion = "2.5";
        installation.ManagedFiles.Add(new ManagedFileRecord
        {
            RelativePath = "d3d11.dll",
            OriginalState = FileOriginalState.Missing,
            ExpectedManagedSha256 = new string('0', 64),
            ManagedDxvkVersion = "2.5"
        });
        store.Save(installation);

        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);

        var currentInst = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(currentInst);
        Assert.Equal(RestorationState.AttentionRequired, currentInst!.RestorationState);
        Assert.Contains("Managed target file is missing for restore: d3d11.dll", currentInst.LastRefusalReason);
        Assert.Equal("2.5", currentInst.ManagedDxvkVersion);
    }

    [Fact]
    public async Task Restore_UnknownOriginalState_RefusesSafely_NotSilentlyReportedAsRestored()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "deployed-d3d11");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        var installation = store.GetOrCreateInstallation(gameDir.RootPath, "UnknownStateGame");
        installation.RestorationState = RestorationState.Managed;
        installation.ManagedDxvkVersion = "2.5";
        installation.ManagedFiles.Add(new ManagedFileRecord
        {
            RelativePath = "d3d11.dll",
            OriginalState = FileOriginalState.Unknown,
            ExpectedManagedSha256 = FileIdentity.Capture(d3d11Path).Sha256,
            ManagedDxvkVersion = "2.5"
        });
        store.Save(installation);

        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);

        // Invariant: Game file is untouched
        Assert.True(File.Exists(d3d11Path));
        Assert.Equal("deployed-d3d11", File.ReadAllText(d3d11Path));

        // Invariant: Not marked restored; marked AttentionRequired
        var currentInst = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(currentInst);
        Assert.Equal(RestorationState.AttentionRequired, currentInst!.RestorationState);
        Assert.Contains("Original state is unknown for managed file: d3d11.dll", currentInst.LastRefusalReason);
        Assert.Equal("2.5", currentInst.ManagedDxvkVersion);
    }

    [Fact]
    public async Task Restore_OrdinaryRestoration_AndVerifiedRepeatRestoration_Succeeds()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-content");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-content");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11 = "original-native-d3d11-content";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalD3D11);
        var dxgiPath = Path.Combine(gameDir.RootPath, "dxgi.dll");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        // 1. Install
        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);

        // 2. Ordinary restore: succeeds
        var firstRollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(firstRollbackOk);
        Assert.Equal(originalD3D11, File.ReadAllText(d3d11Path));
        Assert.False(File.Exists(dxgiPath));

        var instAfter = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(instAfter);
        Assert.Equal(RestorationState.Restored, instAfter!.RestorationState);
        Assert.Null(instAfter.ManagedDxvkVersion);

        // 3. Repeat restore on correctly restored installation: succeeds as verified no-op
        var secondRollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(secondRollbackOk);
        Assert.Equal(originalD3D11, File.ReadAllText(d3d11Path));
        Assert.False(File.Exists(dxgiPath));
        Assert.Equal(RestorationState.Restored, instAfter.RestorationState);

        // 4. Tamper disk by introducing an unexpected file at dxgi.dll
        File.WriteAllText(dxgiPath, "unexpected-file-after-restore");

        // Repeat restore now must refuse because unexpected file is present
        var tamperedRollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(tamperedRollbackOk);
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal("unexpected-file-after-restore", File.ReadAllText(dxgiPath));

        var instTampered = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(instTampered);
        Assert.Equal(RestorationState.AttentionRequired, instTampered!.RestorationState);
        Assert.Contains("Already restored installation has unexpected file present: dxgi.dll", instTampered.LastRefusalReason);
    }

    [Fact]
    public async Task Restore_Persistence_RefusalAndVersionSurviveStoreReload()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "original-d3d11");
        var dxgiPath = Path.Combine(gameDir.RootPath, "dxgi.dll");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");

        var store = new GameLibraryStore(storePath, backupsPath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);

        // Replace d3d11.dll with external content to trigger refusal
        File.WriteAllText(d3d11Path, "tampered-content");

        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);

        // Reload from a fresh store instance
        var reloadedStore = new GameLibraryStore(storePath, backupsPath);
        var reloadedInst = reloadedStore.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(reloadedInst);
        Assert.Equal(RestorationState.AttentionRequired, reloadedInst!.RestorationState);
        Assert.Equal("2.5", reloadedInst.ManagedDxvkVersion);
        Assert.NotNull(reloadedInst.LastRefusalReason);
        Assert.Contains("Managed target hash mismatch for restore: d3d11.dll", reloadedInst.LastRefusalReason);
    }

    [Fact]
    public async Task Restore_ManagerBoundary_FailedDisable_DoesNotClearProfileDxvkEnabled()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "original-d3d11");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);
        var profileStore = new ProfileStore(Path.Combine(storageDir.RootPath, "profiles.json"));

        var profile = profileStore.GetOrCreate(exePath);
        profile.Api = GraphicsApi.DX11;
        profile.Architecture = "x64";
        profileStore.Save(profile);

        var manager = new DxvkManager(installer, rollback, new DxvkGithubClient(http, new CacheStore()), profileStore, store);
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var installOk = await manager.EnableDxvkAsync(profile, release);
        Assert.True(installOk);
        Assert.True(profile.DxvkEnabled);
        Assert.Equal("2.5", profile.DxvkVersion);

        // Tamper d3d11.dll
        File.WriteAllText(d3d11Path, "tampered-content");

        // Attempt disable via manager
        var disableOk = await manager.DisableDxvkAsync(profile);
        Assert.False(disableOk);

        // Boundary check: profile remains enabled and version retained
        var reloadedProfile = profileStore.GetOrCreate(exePath);
        Assert.True(reloadedProfile.DxvkEnabled);
        Assert.Equal("2.5", reloadedProfile.DxvkVersion);
    }

    [Fact]
    public void Restore_MultiFileTransactionEngine_ExpectedManagedSha256_EnforcesTargetOwnershipDirectly()
    {
        using var game = new SyntheticTestDirectory();
        using var storage = new SyntheticTestDirectory();

        var target = game.CreateFile("d3d11.dll", "tampered-target-content");
        var expectedHash = new string('0', 64);

        var engine = new MultiFileTransactionEngine(storage.RootPath);
        var result = engine.Execute(new MultiFileTransactionRequest
        {
            InstallationRoot = game.RootPath,
            Operation = TransactionOperation.Restore,
            Files = new[]
            {
                new MultiFileTransactionFile
                {
                    RelativePath = "d3d11.dll",
                    OriginalState = OriginalFileState.DidNotExist,
                    ExpectedManagedSha256 = expectedHash
                }
            }
        });

        Assert.Equal(TransactionState.FailedSafely, result.State);
        Assert.Equal(TransactionOutcome.SafeFailure, result.Outcome);
        Assert.Contains("Managed target hash mismatch for restore: d3d11.dll", result.Message);

        // File on disk was NOT deleted
        Assert.True(File.Exists(target));
        Assert.Equal("tampered-target-content", File.ReadAllText(target));
    }
}
