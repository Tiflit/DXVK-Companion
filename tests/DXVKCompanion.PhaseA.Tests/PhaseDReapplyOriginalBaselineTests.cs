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

public sealed class PhaseDReapplyOriginalBaselineTests
{
    [Fact]
    public async Task DxvkInstaller_ReapplyAsync_DX9ToDX11Reclassification_BacksUpPreExistingNativeDllAndRestoresByteIdentical()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        // Prepare synthetic DXVK release payload files for version 2.5 x64
        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d9.dll"), "dxvk-2.5-d3d9-payload");
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-payload");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-payload");

        // Game folder starts with Game.exe and an original native d3d11.dll.
        // Positive control: dxgi.dll does NOT exist initially in the game folder.
        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var nativeD3D11Content = "native-original-d3d11-content-before-dxvk";
        var nativeD3D11Path = gameDir.CreateFile("d3d11.dll", nativeD3D11Content);
        var expectedNativeD3D11Identity = FileIdentity.Capture(nativeD3D11Path);

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        // 1. Initial deployment as DX9 (deploys only d3d9.dll)
        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX9,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);

        // Verify DX9 install: d3d9.dll deployed, pre-existing d3d11.dll untouched
        var targetD3D9 = Path.Combine(gameDir.RootPath, "d3d9.dll");
        Assert.True(File.Exists(targetD3D9));
        Assert.Equal("dxvk-2.5-d3d9-payload", File.ReadAllText(targetD3D9));
        Assert.Equal(nativeD3D11Content, File.ReadAllText(nativeD3D11Path));

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        Assert.NotNull(installation!.FindManagedFile("d3d9.dll"));
        Assert.Null(installation.FindManagedFile("d3d11.dll"));
        Assert.Null(installation.FindManagedFile("dxgi.dll"));

        // 2. Game is reclassified to DX11 and ReapplyAsync is called
        profile.Api = GraphicsApi.DX11;
        var reapplyOk = await installer.ReapplyAsync(profile, updateBaseline: false);
        Assert.True(reapplyOk);

        // Verify DXVK DX11 files are deployed to game directory
        var targetD3D11 = Path.Combine(gameDir.RootPath, "d3d11.dll");
        var targetDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");
        Assert.True(File.Exists(targetD3D11));
        Assert.True(File.Exists(targetDxgi));
        Assert.Equal("dxvk-2.5-d3d11-payload", File.ReadAllText(targetD3D11));
        Assert.Equal("dxvk-2.5-dxgi-payload", File.ReadAllText(targetDxgi));

        // 3. Inspect metadata and backup for newly required d3d11.dll
        var updatedInstallation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(updatedInstallation);

        var d3d11Record = updatedInstallation!.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record);
        Assert.Equal(FileOriginalState.Existing, d3d11Record!.OriginalState);
        Assert.False(string.IsNullOrEmpty(d3d11Record.BackupRelativePath));
        Assert.Equal(expectedNativeD3D11Identity.Sha256, d3d11Record.OriginalSha256);

        // Backup file must exist in Companion storage with original native content
        var backupPath = Path.Combine(storageDir.RootPath, "backups", d3d11Record.BackupRelativePath!);
        Assert.True(File.Exists(backupPath));
        Assert.Equal(nativeD3D11Content, File.ReadAllText(backupPath));

        // Positive control: dxgi.dll was missing originally, must be recorded as Missing with no backup
        var dxgiRecord = updatedInstallation.FindManagedFile("dxgi.dll");
        Assert.NotNull(dxgiRecord);
        Assert.Equal(FileOriginalState.Missing, dxgiRecord!.OriginalState);
        Assert.Null(dxgiRecord.BackupRelativePath);
        Assert.Null(dxgiRecord.OriginalSha256);

        // 4. Restore / Rollback must return byte-identical native d3d11.dll and delete dxgi.dll
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);

        Assert.True(File.Exists(targetD3D11));
        Assert.Equal(nativeD3D11Content, File.ReadAllText(targetD3D11));
        Assert.Equal(expectedNativeD3D11Identity, FileIdentity.Capture(targetD3D11));

        // Positive control: absent file is cleanly deleted
        Assert.False(File.Exists(targetDxgi));
    }

    [Fact]
    public async Task DxvkInstaller_ReapplyAsync_PlainReapply_BacksUpPreExistingNativeDllAndRestoresByteIdentical()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-payload");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-payload");

        // Game folder starts with Game.exe and an original native d3d11.dll.
        // Positive control: dxgi.dll does NOT exist initially.
        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var nativeD3D11Content = "native-original-d3d11-content-plain";
        var nativeD3D11Path = gameDir.CreateFile("d3d11.dll", nativeD3D11Content);
        var expectedNativeD3D11Identity = FileIdentity.Capture(nativeD3D11Path);

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        // Existing managed installation without d3d11.dll or dxgi.dll in ManagedFiles
        var installation = store.GetOrCreateInstallation(gameDir.RootPath, "PlainGame");
        installation.ManagedDxvkVersion = "2.5";
        installation.ManagedDxvkArchitecture = "x64";
        installation.RestorationState = RestorationState.Managed;
        store.Save(installation);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        // Execute ReapplyAsync
        var reapplyOk = await installer.ReapplyAsync(profile, updateBaseline: false);
        Assert.True(reapplyOk);

        var targetD3D11 = Path.Combine(gameDir.RootPath, "d3d11.dll");
        var targetDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");
        Assert.True(File.Exists(targetD3D11));
        Assert.True(File.Exists(targetDxgi));
        Assert.Equal("dxvk-2.5-d3d11-payload", File.ReadAllText(targetD3D11));
        Assert.Equal("dxvk-2.5-dxgi-payload", File.ReadAllText(targetDxgi));

        // Inspect metadata
        var updatedInstallation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(updatedInstallation);

        var d3d11Record = updatedInstallation!.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record);
        Assert.Equal(FileOriginalState.Existing, d3d11Record!.OriginalState);
        Assert.False(string.IsNullOrEmpty(d3d11Record.BackupRelativePath));
        Assert.Equal(expectedNativeD3D11Identity.Sha256, d3d11Record.OriginalSha256);

        var backupPath = Path.Combine(storageDir.RootPath, "backups", d3d11Record.BackupRelativePath!);
        Assert.True(File.Exists(backupPath));
        Assert.Equal(nativeD3D11Content, File.ReadAllText(backupPath));

        // Positive control: dxgi.dll was missing originally
        var dxgiRecord = updatedInstallation.FindManagedFile("dxgi.dll");
        Assert.NotNull(dxgiRecord);
        Assert.Equal(FileOriginalState.Missing, dxgiRecord!.OriginalState);
        Assert.Null(dxgiRecord.BackupRelativePath);

        // Restore
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);

        Assert.True(File.Exists(targetD3D11));
        Assert.Equal(nativeD3D11Content, File.ReadAllText(targetD3D11));
        Assert.Equal(expectedNativeD3D11Identity, FileIdentity.Capture(targetD3D11));
        Assert.False(File.Exists(targetDxgi));
    }

    [Fact]
    public async Task DxvkInstaller_ReapplyAsync_UpdateBaselineTrue_NewlyRequiredDll_BacksUpCurrentFileAsBaseline()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d9.dll"), "dxvk-2.5-d3d9-payload");
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-payload");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-payload");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var nativeD3D11Content = "native-original-d3d11-update-baseline";
        var nativeD3D11Path = gameDir.CreateFile("d3d11.dll", nativeD3D11Content);
        var expectedNativeD3D11Identity = FileIdentity.Capture(nativeD3D11Path);

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        // DX9 install
        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX9,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };
        await installer.ApplyToGameAsync(profile, release);

        // Reclassify and Reapply with updateBaseline = true
        profile.Api = GraphicsApi.DX11;
        var reapplyOk = await installer.ReapplyAsync(profile, updateBaseline: true);
        Assert.True(reapplyOk);

        var updatedInstallation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(updatedInstallation);

        var d3d11Record = updatedInstallation!.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record);
        Assert.Equal(FileOriginalState.Existing, d3d11Record!.OriginalState);
        Assert.False(string.IsNullOrEmpty(d3d11Record.BackupRelativePath));
        Assert.Equal(expectedNativeD3D11Identity.Sha256, d3d11Record.OriginalSha256);

        var backupPath = Path.Combine(storageDir.RootPath, "backups", d3d11Record.BackupRelativePath!);
        Assert.True(File.Exists(backupPath));
        Assert.Equal(nativeD3D11Content, File.ReadAllText(backupPath));

        // Rollback
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);

        var targetD3D11 = Path.Combine(gameDir.RootPath, "d3d11.dll");
        Assert.True(File.Exists(targetD3D11));
        Assert.Equal(nativeD3D11Content, File.ReadAllText(targetD3D11));
        Assert.Equal(expectedNativeD3D11Identity, FileIdentity.Capture(targetD3D11));
    }

    [Fact]
    public async Task DxvkInstaller_ReapplyAsync_PreExistingDxvkConf_BacksUpAndRestoresConfig()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-payload");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-payload");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var nativeConfContent = "# User custom pre-existing dxvk configuration\ndxvk.numCompilerThreads = 4\n";
        var nativeConfPath = gameDir.CreateFile("dxvk.conf", nativeConfContent);
        var expectedNativeConfIdentity = FileIdentity.Capture(nativeConfPath);

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var installation = store.GetOrCreateInstallation(gameDir.RootPath, "ConfGame");
        installation.ManagedDxvkVersion = "2.5";
        installation.ManagedDxvkArchitecture = "x64";
        installation.RestorationState = RestorationState.Managed;
        store.Save(installation);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            HudEnabled = true
        };

        var reapplyOk = await installer.ReapplyAsync(profile, updateBaseline: false);
        Assert.True(reapplyOk);

        var updatedInstallation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(updatedInstallation);

        var confRecord = updatedInstallation!.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord);
        Assert.Equal(FileOriginalState.Existing, confRecord!.OriginalState);
        Assert.False(string.IsNullOrEmpty(confRecord.BackupRelativePath));
        Assert.Equal(expectedNativeConfIdentity.Sha256, confRecord.OriginalSha256);

        var backupPath = Path.Combine(storageDir.RootPath, "backups", confRecord.BackupRelativePath!);
        Assert.True(File.Exists(backupPath));
        Assert.Equal(nativeConfContent, File.ReadAllText(backupPath));

        // Restore must return the user's original dxvk.conf
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);

        Assert.True(File.Exists(nativeConfPath));
        Assert.Equal(nativeConfContent, File.ReadAllText(nativeConfPath));
        Assert.Equal(expectedNativeConfIdentity, FileIdentity.Capture(nativeConfPath));
    }

    [Fact]
    public async Task DxvkInstaller_ReapplyAsync_NewlyRequiredDll_WithBackupCollision_PreservesBothAndRestoresByteIdentical()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-payload");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-payload");

        // Native d3d11.dll in game directory with content A
        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var nativeD3D11Content = "native-original-d3d11-A";
        var nativeD3D11Path = gameDir.CreateFile("d3d11.dll", nativeD3D11Content);
        var expectedNativeD3D11Identity = FileIdentity.Capture(nativeD3D11Path);

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var installation = store.GetOrCreateInstallation(gameDir.RootPath, "CollisionGame");
        installation.ManagedDxvkVersion = "2.5";
        installation.ManagedDxvkArchitecture = "x64";
        installation.RestorationState = RestorationState.Managed;
        store.Save(installation);

        // Pre-seed an unrelated existing backup file at the default path <installation.Id>/d3d11.dll with content B
        var seededBackupRelativePath = Path.Combine(installation.Id, "d3d11.dll");
        var seededBackupFullPath = Path.Combine(storageDir.RootPath, "backups", seededBackupRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(seededBackupFullPath)!);
        var unrelatedBackupContent = "unrelated-preexisting-backup-B";
        File.WriteAllText(seededBackupFullPath, unrelatedBackupContent);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        // Execute ReapplyAsync
        var reapplyOk = await installer.ReapplyAsync(profile, updateBaseline: false);
        Assert.True(reapplyOk);

        // DXVK is deployed to target
        var targetD3D11 = Path.Combine(gameDir.RootPath, "d3d11.dll");
        Assert.Equal("dxvk-2.5-d3d11-payload", File.ReadAllText(targetD3D11));

        // The pre-existing unrelated backup B must NOT have been overwritten
        Assert.True(File.Exists(seededBackupFullPath));
        Assert.Equal(unrelatedBackupContent, File.ReadAllText(seededBackupFullPath));

        // Inspect metadata: record must point to the verified backup of A, not B
        var updatedInstallation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(updatedInstallation);
        var d3d11Record = updatedInstallation!.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record);
        Assert.Equal(FileOriginalState.Existing, d3d11Record!.OriginalState);
        Assert.Equal(expectedNativeD3D11Identity.Sha256, d3d11Record.OriginalSha256);
        Assert.False(string.IsNullOrEmpty(d3d11Record.BackupRelativePath));

        var actualBackupPath = Path.Combine(storageDir.RootPath, "backups", d3d11Record.BackupRelativePath!);
        Assert.True(File.Exists(actualBackupPath));
        Assert.Equal(nativeD3D11Content, File.ReadAllText(actualBackupPath));

        // Restore must return native content A, not unrelated bytes B!
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);
        Assert.Equal(nativeD3D11Content, File.ReadAllText(targetD3D11));
        Assert.Equal(expectedNativeD3D11Identity, FileIdentity.Capture(targetD3D11));

        // Pre-existing backup B must remain untouched after restore
        Assert.Equal(unrelatedBackupContent, File.ReadAllText(seededBackupFullPath));
    }

    [Fact]
    public async Task DxvkInstaller_ReapplyAsync_PreExistingDxvkConf_WithBackupCollision_PreservesBothAndRestoresByteIdentical()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-payload");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-payload");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var nativeConfContent = "# Native custom config A\ndxvk.hud = compiler\n";
        var nativeConfPath = gameDir.CreateFile("dxvk.conf", nativeConfContent);
        var expectedNativeConfIdentity = FileIdentity.Capture(nativeConfPath);

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var installation = store.GetOrCreateInstallation(gameDir.RootPath, "ConfCollisionGame");
        installation.ManagedDxvkVersion = "2.5";
        installation.ManagedDxvkArchitecture = "x64";
        installation.RestorationState = RestorationState.Managed;
        store.Save(installation);

        // Pre-seed an unrelated backup file at <installation.Id>/dxvk.conf with content B
        var seededConfBackupPath = Path.Combine(storageDir.RootPath, "backups", installation.Id, "dxvk.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(seededConfBackupPath)!);
        var unrelatedConfContent = "# Unrelated backup B\ndxvk.hud = fps\n";
        File.WriteAllText(seededConfBackupPath, unrelatedConfContent);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            HudEnabled = true
        };

        var reapplyOk = await installer.ReapplyAsync(profile, updateBaseline: false);
        Assert.True(reapplyOk);

        // Unrelated backup B is not overwritten
        Assert.Equal(unrelatedConfContent, File.ReadAllText(seededConfBackupPath));

        var updatedInstallation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(updatedInstallation);
        var confRecord = updatedInstallation!.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord);
        Assert.Equal(FileOriginalState.Existing, confRecord!.OriginalState);
        Assert.Equal(expectedNativeConfIdentity.Sha256, confRecord.OriginalSha256);

        var actualConfBackupPath = Path.Combine(storageDir.RootPath, "backups", confRecord.BackupRelativePath!);
        Assert.True(File.Exists(actualConfBackupPath));
        Assert.Equal(nativeConfContent, File.ReadAllText(actualConfBackupPath));

        // Restore must return native custom config A
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);
        Assert.Equal(nativeConfContent, File.ReadAllText(nativeConfPath));
        Assert.Equal(expectedNativeConfIdentity, FileIdentity.Capture(nativeConfPath));
        Assert.Equal(unrelatedConfContent, File.ReadAllText(seededConfBackupPath));
    }

    [Fact]
    public async Task DxvkInstaller_ReapplyAsync_UnknownRecordState_WithBackupCollision_PreservesVerifiedBaselineAndRestores()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-payload");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-payload");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var nativeContent = "native-unknown-state-A";
        var nativePath = gameDir.CreateFile("d3d11.dll", nativeContent);
        var expectedIdentity = FileIdentity.Capture(nativePath);

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var installation = store.GetOrCreateInstallation(gameDir.RootPath, "UnknownRecordGame");
        installation.ManagedDxvkVersion = "2.5";
        installation.ManagedDxvkArchitecture = "x64";
        installation.RestorationState = RestorationState.Managed;
        var existingRecord = installation.GetOrAddManagedFile("d3d11.dll");
        existingRecord.OriginalState = FileOriginalState.Unknown;
        existingRecord.BackupRelativePath = Path.Combine(installation.Id, "d3d11.dll");
        store.Save(installation);

        // Pre-seed unrelated file B at the recorded path
        var seededPath = Path.Combine(storageDir.RootPath, "backups", existingRecord.BackupRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(seededPath)!);
        var unrelatedContent = "unrelated-unknown-backup-B";
        File.WriteAllText(seededPath, unrelatedContent);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        var reapplyOk = await installer.ReapplyAsync(profile, updateBaseline: false);
        Assert.True(reapplyOk);

        // Backup B is not overwritten
        Assert.Equal(unrelatedContent, File.ReadAllText(seededPath));

        var updatedInstallation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(updatedInstallation);
        var d3d11Record = updatedInstallation!.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record);
        Assert.Equal(FileOriginalState.Existing, d3d11Record!.OriginalState);
        Assert.Equal(expectedIdentity.Sha256, d3d11Record.OriginalSha256);

        // Restore returns A
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);
        Assert.Equal(nativeContent, File.ReadAllText(nativePath));
        Assert.Equal(unrelatedContent, File.ReadAllText(seededPath));
    }

    [Fact]
    public async Task DxvkInstaller_ReapplyAsync_RepeatedNormalReapply_PreservesEstablishedBackupAndOriginalSha256()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-payload");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-payload");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-game-binary");
        var nativeContent = "native-original-content-A";
        var nativePath = gameDir.CreateFile("d3d11.dll", nativeContent);
        var expectedIdentity = FileIdentity.Capture(nativePath);

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

        // 1. Initial Apply
        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        var record1 = installation!.FindManagedFile("d3d11.dll");
        Assert.NotNull(record1);
        var originalBackupRelPath = record1!.BackupRelativePath;
        var originalSha256 = record1.OriginalSha256;

        // 2. First Reapply (normal Reapply)
        var reapply1Ok = await installer.ReapplyAsync(profile, updateBaseline: false);
        Assert.True(reapply1Ok);

        var afterReapply1 = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(afterReapply1);
        var recordAfter1 = afterReapply1!.FindManagedFile("d3d11.dll");
        Assert.NotNull(recordAfter1);
        Assert.Equal(originalBackupRelPath, recordAfter1!.BackupRelativePath);
        Assert.Equal(originalSha256, recordAfter1.OriginalSha256);

        // 3. Second Reapply (repeated normal Reapply)
        var reapply2Ok = await installer.ReapplyAsync(profile, updateBaseline: false);
        Assert.True(reapply2Ok);

        var afterReapply2 = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(afterReapply2);
        var recordAfter2 = afterReapply2!.FindManagedFile("d3d11.dll");
        Assert.NotNull(recordAfter2);
        Assert.Equal(originalBackupRelPath, recordAfter2!.BackupRelativePath);
        Assert.Equal(originalSha256, recordAfter2.OriginalSha256);

        // 4. Restore returns native content A
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);
        Assert.Equal(nativeContent, File.ReadAllText(nativePath));
        Assert.Equal(expectedIdentity, FileIdentity.Capture(nativePath));
    }
}
