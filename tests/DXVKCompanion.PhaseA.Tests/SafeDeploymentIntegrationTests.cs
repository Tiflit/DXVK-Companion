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

public sealed class SafeDeploymentIntegrationTests
{
    [Fact]
    public async Task DxvkInstaller_And_DxvkRollback_CleanGameDirectory_InstallsAndSelfCleans()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        // Prepare synthetic DXVK release files
        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-d3d11-content");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-dxgi-content");

        // Game folder starts completely clean (no directx DLLs)
        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

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

        var targetD3D11 = Path.Combine(gameDir.RootPath, "d3d11.dll");
        var targetDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");

        Assert.True(File.Exists(targetD3D11));
        Assert.True(File.Exists(targetDxgi));
        Assert.Equal("dxvk-d3d11-content", File.ReadAllText(targetD3D11));
        Assert.Equal("dxvk-dxgi-content", File.ReadAllText(targetDxgi));

        // Invariant: Game directory must NOT have .bak files
        Assert.False(File.Exists(targetD3D11 + ".bak"));
        Assert.False(File.Exists(targetDxgi + ".bak"));

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        Assert.Equal("2.5", installation!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, installation.RestorationState);
        Assert.Equal(2, installation.ManagedFiles.Count);
        Assert.All(installation.ManagedFiles, f => Assert.Equal(FileOriginalState.Missing, f.OriginalState));
        Assert.All(installation.ManagedFiles, f => Assert.Equal(ManagedFileState.Consistent, f.CurrentState));

        // 2. Rollback / Restore
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);

        // Invariant: Clean self-cleaning — files that did not exist originally are deleted!
        Assert.False(File.Exists(targetD3D11));
        Assert.False(File.Exists(targetDxgi));

        var updatedInstallation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(updatedInstallation);
        Assert.Null(updatedInstallation!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Restored, updatedInstallation.RestorationState);
        Assert.All(updatedInstallation.ManagedFiles, f => Assert.Equal(ManagedFileState.Consistent, f.CurrentState));
    }

    [Fact]
    public async Task DxvkInstaller_And_DxvkRollback_ExistingGameDll_PreservesBaselineAndRestores()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-d3d11-content");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-dxgi-content");

        // Game folder has an original d3d11.dll, but no dxgi.dll
        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11Path = gameDir.CreateFile("d3d11.dll", "original-native-d3d11");
        var originalIdentity = FileIdentity.Capture(originalD3D11Path);

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

        var targetD3D11 = Path.Combine(gameDir.RootPath, "d3d11.dll");
        var targetDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");

        Assert.Equal("dxvk-d3d11-content", File.ReadAllText(targetD3D11));
        Assert.Equal("dxvk-dxgi-content", File.ReadAllText(targetDxgi));

        // Invariant: Game directory must NOT have .bak files
        Assert.False(File.Exists(targetD3D11 + ".bak"));
        Assert.False(File.Exists(targetDxgi + ".bak"));

        // 2. Rollback
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);

        // Invariant: Original file is restored to exact content, and absent file is deleted
        Assert.True(File.Exists(targetD3D11));
        Assert.Equal("original-native-d3d11", File.ReadAllText(targetD3D11));
        Assert.Equal(originalIdentity, FileIdentity.Capture(targetD3D11));
        Assert.False(File.Exists(targetDxgi));
    }

    [Fact]
    public async Task DxvkInstaller_PartialFailure_RollsBackAllFilesSafely()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-d3d11-content");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-dxgi-content");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11Path = gameDir.CreateFile("d3d11.dll", "original-native-d3d11");
        var originalIdentity = FileIdentity.Capture(originalD3D11Path);

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));

        // Inject failure on second file (dxgi.dll)
        var engine = new MultiFileTransactionEngine(
            Path.Combine(storageDir.RootPath, "backups"),
            new MultiFileTransactionTestHooks
            {
                AfterApply = (_, index) =>
                {
                    if (index == 1)
                        throw new IOException("Simulated I/O failure on second file");
                }
            });

        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.False(installOk);

        // Invariant: Game folder must remain in pre-operation state
        var targetD3D11 = Path.Combine(gameDir.RootPath, "d3d11.dll");
        var targetDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");

        Assert.Equal("original-native-d3d11", File.ReadAllText(targetD3D11));
        Assert.Equal(originalIdentity, FileIdentity.Capture(targetD3D11));
        Assert.False(File.Exists(targetDxgi));
    }

    [Fact]
    public async Task DxvkInstaller_And_DxvkRollback_DX9_Lifecycle()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x32");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d9.dll"), "dxvk-d3d9-content");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX9,
            Architecture = "x32"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);

        var targetD3D9 = Path.Combine(gameDir.RootPath, "d3d9.dll");
        Assert.True(File.Exists(targetD3D9));
        Assert.Equal("dxvk-d3d9-content", File.ReadAllText(targetD3D9));

        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);
        Assert.False(File.Exists(targetD3D9));
    }

    [Fact]
    public async Task DxvkRollback_UnownedDxvkConf_RefusesRollbackAndPreservesConfFile()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var confContent = "dxvk.hud = fps\ndxvk.frameRate = 120";
        var confPath = gameDir.CreateFile("dxvk.conf", confContent);

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath);

        Assert.True(File.Exists(confPath));
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);
        Assert.NotNull(rollback.LastRefusalReason);
        Assert.Contains("missing ownership", rollback.LastRefusalReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(confPath));
        Assert.Equal(confContent, File.ReadAllText(confPath));
    }

    [Fact]
    public async Task DxvkInstaller_And_DxvkRollback_FailedUpdateReapplyAndRestore_RecoverPreOperationStateAndPreserveNativeBaseline()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var backupsRoot = Path.Combine(storageDir.RootPath, "backups");
        var libraryPath = Path.Combine(storageDir.RootPath, "game-library.json");

        var v24ArchDir = Path.Combine(sourceDir.RootPath, "2.4", "x64");
        Directory.CreateDirectory(v24ArchDir);
        File.WriteAllText(Path.Combine(v24ArchDir, "d3d11.dll"), "dxvk-2.4-d3d11");
        File.WriteAllText(Path.Combine(v24ArchDir, "dxgi.dll"), "dxvk-2.4-dxgi");

        var v25ArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(v25ArchDir);
        File.WriteAllText(Path.Combine(v25ArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-updated");
        File.WriteAllText(Path.Combine(v25ArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-updated");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var targetD3D11 = gameDir.CreateFile("d3d11.dll", "original-native-d3d11");
        var targetDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");
        var targetConf = Path.Combine(gameDir.RootPath, "dxvk.conf");
        var nativeD3D11Identity = FileIdentity.Capture(targetD3D11);

        var store = new GameLibraryStore(libraryPath, backupsRoot);
        using var http = new HttpClient();

        // 1. Initial install of DXVK 2.4 succeeds
        var cleanEngine = new MultiFileTransactionEngine(backupsRoot);
        var cleanInstaller = new DxvkInstaller(http, cleanEngine, store, sourceDir.RootPath);
        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        var install24Ok = await cleanInstaller.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.4", DownloadUrl = "" });
        Assert.True(install24Ok);

        var v24D3D11Identity = FileIdentity.Capture(targetD3D11);
        var v24DxgiIdentity = FileIdentity.Capture(targetDxgi);
        var installationAfter24 = store.FindByInstallationPath(gameDir.RootPath)!;
        var d3d11Record = installationAfter24.FindManagedFile("d3d11.dll")!;
        var nativeBackupPath = Path.Combine(backupsRoot, d3d11Record.BackupRelativePath!);
        Assert.Equal(nativeD3D11Identity, FileIdentity.Capture(nativeBackupPath));

        // 2. Failed update to DXVK 2.5 (with HUD config enabled so dxvk.conf is newly added as 3rd file)
        var failUpdateEngine = new MultiFileTransactionEngine(backupsRoot, new MultiFileTransactionTestHooks
        {
            AfterApply = (_, index) =>
            {
                if (index == 2)
                    throw new IOException("Simulated failure on third file during 2.4 -> 2.5 update");
            }
        });
        var failUpdateInstaller = new DxvkInstaller(http, failUpdateEngine, store, sourceDir.RootPath);
        var updateProfile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            HudEnabled = true
        };

        var update25Ok = await failUpdateInstaller.ApplyToGameAsync(updateProfile, new ReleaseInfo { Version = "2.5", DownloadUrl = "" });
        Assert.False(update25Ok);

        // Pre-operation version 2.4 state AND long-lived native backup must both remain intact
        Assert.True(File.Exists(targetD3D11));
        Assert.True(File.Exists(targetDxgi));
        Assert.False(File.Exists(targetConf));
        Assert.Equal(v24D3D11Identity, FileIdentity.Capture(targetD3D11));
        Assert.Equal(v24DxgiIdentity, FileIdentity.Capture(targetDxgi));
        Assert.Equal(nativeD3D11Identity, FileIdentity.Capture(nativeBackupPath));

        // 3. Failed reapply of DXVK 2.4 (fails on second file dxgi.dll)
        var failReapplyEngine = new MultiFileTransactionEngine(backupsRoot, new MultiFileTransactionTestHooks
        {
            AfterApply = (_, index) =>
            {
                if (index == 1)
                    throw new IOException("Simulated failure on second file during reapply");
            }
        });
        var failReapplyInstaller = new DxvkInstaller(http, failReapplyEngine, store, sourceDir.RootPath);
        var reapplyOk = await failReapplyInstaller.ReapplyAsync(profile);
        Assert.False(reapplyOk);

        Assert.True(File.Exists(targetD3D11));
        Assert.True(File.Exists(targetDxgi));
        Assert.Equal(v24D3D11Identity, FileIdentity.Capture(targetD3D11));
        Assert.Equal(v24DxgiIdentity, FileIdentity.Capture(targetDxgi));
        Assert.Equal(nativeD3D11Identity, FileIdentity.Capture(nativeBackupPath));

        // 4. Failed restore (fails after mutating first file)
        var failRestoreEngine = new MultiFileTransactionEngine(backupsRoot, new MultiFileTransactionTestHooks
        {
            AfterApply = (_, index) =>
            {
                if (index == 0)
                    throw new IOException("Simulated failure after first file during restore");
            }
        });
        var failRollback = new DxvkRollback(failRestoreEngine, store);
        var failedRestoreOk = await failRollback.RestoreOriginalDllsAsync(profile);
        Assert.False(failedRestoreOk);

        Assert.True(File.Exists(targetD3D11));
        Assert.True(File.Exists(targetDxgi));
        Assert.Equal(v24D3D11Identity, FileIdentity.Capture(targetD3D11));
        Assert.Equal(v24DxgiIdentity, FileIdentity.Capture(targetDxgi));
        Assert.Equal(nativeD3D11Identity, FileIdentity.Capture(nativeBackupPath));

        // 5. Subsequent clean restore succeeds and returns to exact native baseline
        var cleanRollback = new DxvkRollback(cleanEngine, store);
        var cleanRestoreOk = await cleanRollback.RestoreOriginalDllsAsync(profile);
        Assert.True(cleanRestoreOk);

        Assert.True(File.Exists(targetD3D11));
        Assert.Equal(nativeD3D11Identity, FileIdentity.Capture(targetD3D11));
        Assert.False(File.Exists(targetDxgi));
        Assert.False(File.Exists(targetConf));
    }
}
