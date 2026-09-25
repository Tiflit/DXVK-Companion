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
        Assert.All(installation.ManagedFiles, f => Assert.Equal(FileOriginalState.DidNotExist, f.OriginalState));

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
    public async Task DxvkRollback_CleansUpDxvkConfFile()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var confPath = gameDir.CreateFile("dxvk.conf", "dxvk.hud = fps\ndxvk.frameRate = 120");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath);

        Assert.True(File.Exists(confPath));
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);
        Assert.False(File.Exists(confPath));
    }
}
