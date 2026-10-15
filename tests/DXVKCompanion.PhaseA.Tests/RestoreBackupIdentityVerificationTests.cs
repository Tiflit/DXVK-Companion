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

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalContent = "original-native-d3d11-content";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalContent);

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

        // Invariant: Game file was NOT modified/restored with corrupted content
        Assert.Equal("dxvk-2.5-d3d11-content", File.ReadAllText(d3d11Path));
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

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "original-d3d11");

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

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        var record = installation!.FindManagedFile("d3d11.dll");
        var backupPath = Path.Combine(storageDir.RootPath, "backups", record!.BackupRelativePath!);
        File.Delete(backupPath);
        Assert.False(File.Exists(backupPath));

        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);
        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));

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

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "original-d3d11");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        // Test with missing hash (null/empty)
        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        var record = installation!.FindManagedFile("d3d11.dll");
        record!.OriginalSha256 = null; // simulate missing hash baseline
        store.Save(installation);

        var rollbackOkNull = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOkNull);
        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));

        var instAfterNull = store.FindByInstallationPath(gameDir.RootPath);
        Assert.Equal(RestorationState.AttentionRequired, instAfterNull!.RestorationState);
        Assert.Contains("missing or malformed", instAfterNull.LastRefusalReason, StringComparison.OrdinalIgnoreCase);

        // Test with malformed hash (invalid hex/length)
        record.OriginalSha256 = "invalid-not-64-hex";
        store.Save(installation);

        var rollbackOkMalformed = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOkMalformed);
        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));

        var instAfterMalformed = store.FindByInstallationPath(gameDir.RootPath);
        Assert.Equal(RestorationState.AttentionRequired, instAfterMalformed!.RestorationState);
        Assert.Contains("missing or malformed", instAfterMalformed.LastRefusalReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("2.5", instAfterMalformed.ManagedDxvkVersion);
    }

    [Fact]
    public async Task Restore_MultiFile_InvalidLaterBackup_LeavesEarlierFilesAndOriginallyAbsentFileUntouched()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        // Game originally has native d3d11.dll, but dxgi.dll was absent
        var d3d11Path = gameDir.CreateFile("d3d11.dll", "original-native-d3d11");
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

        // Verify deployed state: dxgi.dll was deployed (originally absent), d3d11.dll deployed
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal("dxvk-2.5-dxgi", File.ReadAllText(dxgiPath));
        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        var d3d11Record = installation!.FindManagedFile("d3d11.dll");
        var dxgiRecord = installation.FindManagedFile("dxgi.dll");
        Assert.Equal(FileOriginalState.Existing, d3d11Record!.OriginalState);
        Assert.Equal(FileOriginalState.Missing, dxgiRecord!.OriginalState);

        // Corrupt the backup of d3d11.dll
        var d3d11Backup = Path.Combine(storageDir.RootPath, "backups", d3d11Record.BackupRelativePath!);
        File.WriteAllText(d3d11Backup, "corrupted-d3d11-backup");

        // Attempt restore
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);

        // Invariant: dxgi.dll (originally absent, which would otherwise be deleted during restore) remains untouched!
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal("dxvk-2.5-dxgi", File.ReadAllText(dxgiPath));

        // Invariant: d3d11.dll remains untouched
        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));

        // Invariant: state is AttentionRequired and management info retained
        var instAfter = store.FindByInstallationPath(gameDir.RootPath);
        Assert.Equal(RestorationState.AttentionRequired, instAfter!.RestorationState);
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
        Assert.Single(reloadedInst.ManagedFiles);
    }
}
