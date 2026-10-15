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

public sealed class RestoreBackupIdentityVerificationTests
{
    [Fact]
    public async Task Restore_BackupModifiedAfterInstallation_RefusesBeforeGameFileMutation()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-content");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-content");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalContent = "original-native-d3d11-content";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalContent);
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

        // 1. Install successfully
        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);
        Assert.Equal("dxvk-2.5-d3d11-content", File.ReadAllText(d3d11Path));
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal("dxvk-2.5-dxgi-content", File.ReadAllText(dxgiPath));

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        var record = installation!.FindManagedFile("d3d11.dll");
        Assert.NotNull(record);
        Assert.NotNull(record!.OriginalSha256);

        // 2. Corrupt/modify the backup file on disk after installation
        var backupPath = Path.Combine(storageDir.RootPath, "backups", record.BackupRelativePath!);
        Assert.True(File.Exists(backupPath));
        File.WriteAllText(backupPath, "corrupted-or-modified-backup-content");

        // 3. Attempt restore: must refuse before game-file mutation
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);

        // Invariant: Game files were NOT modified/restored with corrupted content
        Assert.Equal("dxvk-2.5-d3d11-content", File.ReadAllText(d3d11Path));
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal("dxvk-2.5-dxgi-content", File.ReadAllText(dxgiPath));
        // Invariant: Corrupted backup was preserved
        Assert.Equal("corrupted-or-modified-backup-content", File.ReadAllText(backupPath));

        // Invariant: Attention required and reason recorded, version retained
        var currentInst = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(currentInst);
        Assert.Equal(RestorationState.AttentionRequired, currentInst!.RestorationState);
        Assert.NotNull(currentInst.LastRefusalReason);
        Assert.Contains("Original backup hash mismatch", currentInst.LastRefusalReason);
        Assert.Equal("2.5", currentInst.ManagedDxvkVersion);
    }

    [Fact]
    public async Task Restore_MissingBackup_RefusesSafely()
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

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);
        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal("dxvk-2.5-dxgi", File.ReadAllText(dxgiPath));

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        var record = installation!.FindManagedFile("d3d11.dll");
        var backupPath = Path.Combine(storageDir.RootPath, "backups", record!.BackupRelativePath!);
        File.Delete(backupPath);
        Assert.False(File.Exists(backupPath));

        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);
        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal("dxvk-2.5-dxgi", File.ReadAllText(dxgiPath));

        var currentInst = store.FindByInstallationPath(gameDir.RootPath);
        Assert.Equal(RestorationState.AttentionRequired, currentInst!.RestorationState);
        Assert.NotNull(currentInst.LastRefusalReason);
        Assert.Contains("unavailable", currentInst.LastRefusalReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("2.5", currentInst.ManagedDxvkVersion);
    }

    [Fact]
    public async Task Restore_MissingOrMalformedRecordedHash_RefusesSafely()
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

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);
        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal("dxvk-2.5-dxgi", File.ReadAllText(dxgiPath));

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        var record = installation!.FindManagedFile("d3d11.dll");
        Assert.NotNull(record);

        // Test with missing hash (null, empty-string, whitespace) and malformed hash (incorrect length, 64-char non-hex)
        var malformedTestCases = new (string? Hash, string Description)[]
        {
            (null, "null baseline hash"),
            (string.Empty, "empty-string baseline hash"),
            ("   ", "whitespace-only baseline hash"),
            ("not-64-hex", "short non-64 length baseline hash"),
            (new string('a', 63), "63-character baseline hash"),
            (new string('a', 65), "65-character baseline hash"),
            (new string('z', 64), "64-character non-hex baseline hash"),
            ("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", "64-character containing non-hex character")
        };

        foreach (var (testHash, description) in malformedTestCases)
        {
            record!.OriginalSha256 = testHash;
            store.Save(installation);

            var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
            Assert.False(rollbackOk, $"Expected restore to refuse for {description}");

            // Invariant: Game files remain untouched
            Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));
            Assert.True(File.Exists(dxgiPath));
            Assert.Equal("dxvk-2.5-dxgi", File.ReadAllText(dxgiPath));

            // Invariant: Attention required and failure recorded
            var current = store.FindByInstallationPath(gameDir.RootPath);
            Assert.NotNull(current);
            Assert.Equal(RestorationState.AttentionRequired, current!.RestorationState);
            Assert.NotNull(current.LastRefusalReason);
            Assert.Contains("missing or malformed", current.LastRefusalReason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("2.5", current.ManagedDxvkVersion);
        }
    }

    [Fact]
    public async Task Restore_MultiFile_InvalidLaterBackup_LeavesEarlierFilesAndOriginallyAbsentFileUntouched()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var libraryJsonPath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsDir = Path.Combine(storageDir.RootPath, "backups");

        var store = new GameLibraryStore(libraryJsonPath, backupsDir);
        var engine = new MultiFileTransactionEngine(backupsDir);
        var rollback = new DxvkRollback(engine, store);

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var profile = new GameProfile(exePath) { Api = GraphicsApi.DX11, Architecture = "x64" };

        // 1. File 1: An originally absent file that was deployed and would be deleted during restore
        var dxgiDeployedBytes = "deployed-dxgi-content"u8.ToArray();
        var dxgiPath = gameDir.GetPath("dxgi.dll");
        File.WriteAllBytes(dxgiPath, dxgiDeployedBytes);

        // 2. File 2: A file with a valid original backup
        var d3d11OriginalBytes = "original-native-d3d11-content"u8.ToArray();
        var d3d11DeployedBytes = "deployed-dxvk-d3d11-content"u8.ToArray();
        var d3d11Path = gameDir.GetPath("d3d11.dll");
        File.WriteAllBytes(d3d11Path, d3d11DeployedBytes);
        var d3d11BackupRel = Path.Combine("Game", "d3d11.dll.bak");
        var d3d11BackupFull = Path.Combine(backupsDir, d3d11BackupRel);
        Directory.CreateDirectory(Path.GetDirectoryName(d3d11BackupFull)!);
        File.WriteAllBytes(d3d11BackupFull, d3d11OriginalBytes);
        var d3d11ValidSha = FileIdentity.Capture(d3d11BackupFull).Sha256;

        // 3. File 3: A later file with an invalid backup (corrupted on disk)
        var confExpectedOriginalBytes = "expected-original-conf-content"u8.ToArray();
        var confDeployedBytes = "deployed-dxvk-conf-content"u8.ToArray();
        var confCorruptedBackupBytes = "corrupted-conf-backup-content"u8.ToArray();
        var confPath = gameDir.GetPath("dxvk.conf");
        File.WriteAllBytes(confPath, confDeployedBytes);
        var confBackupRel = Path.Combine("Game", "dxvk.conf.bak");
        var confBackupFull = Path.Combine(backupsDir, confBackupRel);
        File.WriteAllBytes(confBackupFull, confCorruptedBackupBytes);

        var tempRefFile = gameDir.GetPath("temp_conf_ref");
        File.WriteAllBytes(tempRefFile, confExpectedOriginalBytes);
        var confExpectedSha = FileIdentity.Capture(tempRefFile).Sha256;
        File.Delete(tempRefFile);

        // Set up the GameInstallation in store
        var installation = store.GetOrCreateInstallation(gameDir.RootPath, "Game");
        installation.RestorationState = RestorationState.Managed;
        installation.ManagedDxvkVersion = "2.5";
        installation.ManagedDxvkArchitecture = "x64";
        var exeRecord = installation.GetOrAddExecutable("Game.exe", "Game.exe");
        exeRecord.LastKnownApi = GraphicsApi.DX11;
        exeRecord.LastKnownArchitecture = "x64";

        // Restore inputs are explicitly ordered:
        // 1. An originally absent file that would be deleted (dxgi.dll)
        // 2. A file with a valid original backup (d3d11.dll)
        // 3. A file with an invalid backup (dxvk.conf)
        installation.ManagedFiles.Clear();
        installation.ManagedFiles.Add(new ManagedFileRecord
        {
            RelativePath = "dxgi.dll",
            OriginalState = FileOriginalState.Missing,
            BackupRelativePath = null,
            OriginalSha256 = null,
            ManagedDxvkVersion = "2.5"
        });
        installation.ManagedFiles.Add(new ManagedFileRecord
        {
            RelativePath = "d3d11.dll",
            OriginalState = FileOriginalState.Existing,
            BackupRelativePath = d3d11BackupRel,
            OriginalSha256 = d3d11ValidSha,
            ManagedDxvkVersion = "2.5"
        });
        installation.ManagedFiles.Add(new ManagedFileRecord
        {
            RelativePath = "dxvk.conf",
            OriginalState = FileOriginalState.Existing,
            BackupRelativePath = confBackupRel,
            OriginalSha256 = confExpectedSha,
            ManagedDxvkVersion = "2.5"
        });
        store.Save(installation);

        // Capture exact state before restore attempt
        var dxgiBeforeBytes = File.ReadAllBytes(dxgiPath);
        var d3d11BeforeBytes = File.ReadAllBytes(d3d11Path);
        var confBeforeBytes = File.ReadAllBytes(confPath);
        var d3d11BackupBeforeBytes = File.ReadAllBytes(d3d11BackupFull);
        var confBackupBeforeBytes = File.ReadAllBytes(confBackupFull);

        // Attempt restore
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);

        // Invariant: ALL game-file bytes remain unchanged after refusal
        // dxgi.dll (originally absent, which would otherwise be deleted during restore) remains untouched
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal(dxgiBeforeBytes, File.ReadAllBytes(dxgiPath));
        // d3d11.dll (valid backup) remains untouched
        Assert.True(File.Exists(d3d11Path));
        Assert.Equal(d3d11BeforeBytes, File.ReadAllBytes(d3d11Path));
        // dxvk.conf (invalid backup) remains untouched
        Assert.True(File.Exists(confPath));
        Assert.Equal(confBeforeBytes, File.ReadAllBytes(confPath));

        // Invariant: ALL existing backups remain unchanged after refusal
        Assert.Equal(d3d11BackupBeforeBytes, File.ReadAllBytes(d3d11BackupFull));
        Assert.Equal(confBackupBeforeBytes, File.ReadAllBytes(confBackupFull));

        // Invariant: state is AttentionRequired and management info retained
        var instAfter = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(instAfter);
        Assert.Equal(RestorationState.AttentionRequired, instAfter!.RestorationState);
        Assert.NotNull(instAfter.LastRefusalReason);
        Assert.Contains("Original backup hash mismatch", instAfter.LastRefusalReason);
        Assert.Equal("2.5", instAfter.ManagedDxvkVersion);
    }

    [Fact]
    public async Task Restore_ValidBackups_ExactOriginalContentsRestoredSuccessfully()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalNativeContent = "original-native-d3d11-exact-content";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalNativeContent);
        var expectedOriginalIdentity = FileIdentity.Capture(d3d11Path);
        string dxgiPath = Path.Combine(gameDir.RootPath, "dxgi.dll");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);

        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);

        // Exact original contents are restored
        Assert.True(File.Exists(d3d11Path));
        Assert.Equal(originalNativeContent, File.ReadAllText(d3d11Path));
        Assert.Equal(expectedOriginalIdentity, FileIdentity.Capture(d3d11Path));

        // Originally absent file is deleted
        Assert.False(File.Exists(dxgiPath));

        var instAfter = store.FindByInstallationPath(gameDir.RootPath);
        Assert.Equal(RestorationState.Restored, instAfter!.RestorationState);
        Assert.Null(instAfter.ManagedDxvkVersion);
        Assert.Null(instAfter.LastRefusalReason);
    }

    [Fact]
    public async Task Restore_FailureStateAndManagementInfo_SurviveReloadingLibrary()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var libraryJsonPath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsDir = Path.Combine(storageDir.RootPath, "backups");

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        gameDir.CreateFile("d3d11.dll", "original-d3d11");

        var store = new GameLibraryStore(libraryJsonPath, backupsDir);
        var engine = new MultiFileTransactionEngine(backupsDir);
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        var record = installation!.FindManagedFile("d3d11.dll");
        var backupPath = Path.Combine(backupsDir, record!.BackupRelativePath!);
        File.WriteAllText(backupPath, "corrupted-content");

        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);

        // Now construct a brand new store instance from disk
        var reloadedStore = new GameLibraryStore(libraryJsonPath, backupsDir);
        var reloadedInst = reloadedStore.FindByInstallationPath(gameDir.RootPath);

        Assert.NotNull(reloadedInst);
        Assert.Equal(RestorationState.AttentionRequired, reloadedInst!.RestorationState);
        Assert.NotNull(reloadedInst.LastRefusalReason);
        Assert.Contains("Original backup hash mismatch", reloadedInst.LastRefusalReason);
        Assert.Equal("2.5", reloadedInst.ManagedDxvkVersion);
        Assert.Equal("x64", reloadedInst.ManagedDxvkArchitecture);

        // Explicitly verify expected managed-file records and retained metadata
        Assert.Equal(2, reloadedInst.ManagedFiles.Count);

        var reloadedD3D11 = reloadedInst.FindManagedFile("d3d11.dll");
        Assert.NotNull(reloadedD3D11);
        Assert.Equal(FileOriginalState.Existing, reloadedD3D11!.OriginalState);
        Assert.NotNull(reloadedD3D11.BackupRelativePath);
        Assert.NotNull(reloadedD3D11.OriginalSha256);
        Assert.Equal("2.5", reloadedD3D11.ManagedDxvkVersion);

        var reloadedDxgi = reloadedInst.FindManagedFile("dxgi.dll");
        Assert.NotNull(reloadedDxgi);
        Assert.Equal(FileOriginalState.Missing, reloadedDxgi!.OriginalState);
        Assert.Null(reloadedDxgi.BackupRelativePath);
        Assert.Equal("2.5", reloadedDxgi.ManagedDxvkVersion);
    }
}
