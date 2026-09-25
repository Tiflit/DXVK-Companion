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

public sealed class PhaseCManualManagementTests
{
    [Fact]
    public void ExistingDxvkDetector_NoDlls_ReturnsNone()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();
        gameDir.CreateFile("Game.exe", "synthetic-binary");

        var detector = new ExistingDxvkDetector(sourceDir.RootPath);
        var assessment = detector.AssessDirectory(gameDir.RootPath, "x64");

        Assert.Equal(ExistingDxvkStatus.None, assessment.Status);
        Assert.Null(assessment.MatchedVersion);
        Assert.False(assessment.CanBeAdopted);
        Assert.Empty(assessment.DetectedDlls);
    }

    [Fact]
    public void ExistingDxvkDetector_OfficialRelease_RecognizesAndCanBeAdopted()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        // Populate official release in local dxvk source dir
        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "official-dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "official-dxvk-2.5-dxgi");

        // Game folder has matching DLLs
        gameDir.CreateFile("Game.exe", "synthetic-binary");
        gameDir.CreateFile("d3d11.dll", "official-dxvk-2.5-d3d11");
        gameDir.CreateFile("dxgi.dll", "official-dxvk-2.5-dxgi");

        var detector = new ExistingDxvkDetector(sourceDir.RootPath);
        var assessment = detector.AssessDirectory(gameDir.RootPath, "x64");

        Assert.Equal(ExistingDxvkStatus.OfficialRelease, assessment.Status);
        Assert.Equal("2.5", assessment.MatchedVersion);
        Assert.True(assessment.CanBeAdopted);
        Assert.Contains("d3d11.dll", assessment.DetectedDlls);
        Assert.Contains("dxgi.dll", assessment.DetectedDlls);
    }

    [Fact]
    public void ExistingDxvkDetector_CatalogLookup_RecognizesOfficialReleaseWithoutSourceDir()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var emptySourceDir = new SyntheticTestDirectory();

        var d3d11Content = "catalog-dxvk-2.4-d3d11";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", d3d11Content);
        var d3d11Sha = FileIdentity.Capture(d3d11Path).Sha256;

        DxvkReleaseCatalog.RegisterKnownHash(d3d11Sha, "2.4", "x64", "d3d11.dll");

        var detector = new ExistingDxvkDetector(emptySourceDir.RootPath);
        var assessment = detector.AssessDirectory(gameDir.RootPath, "x64");

        Assert.Equal(ExistingDxvkStatus.OfficialRelease, assessment.Status);
        Assert.Equal("2.4", assessment.MatchedVersion);
        Assert.True(assessment.CanBeAdopted);
    }

    [Fact]
    public async Task DxvkInstaller_AdoptExisting_AdoptsOfficialReleaseCleanly_AndRollbackRemovesIt()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "official-dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "official-dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        gameDir.CreateFile("d3d11.dll", "official-dxvk-2.5-d3d11");
        gameDir.CreateFile("dxgi.dll", "official-dxvk-2.5-dxgi");

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

        var detector = new ExistingDxvkDetector(sourceDir.RootPath);
        var assessment = detector.AssessDirectory(gameDir.RootPath, "x64");
        Assert.True(assessment.CanBeAdopted);

        // 1. Adopt existing
        var adoptOk = installer.AdoptExisting(profile, assessment);
        Assert.True(adoptOk);
        Assert.True(profile.DxvkEnabled);
        Assert.Equal("2.5", profile.DxvkVersion);

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        Assert.Equal("2.5", installation!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, installation.RestorationState);
        Assert.Equal(2, installation.ManagedFiles.Count);
        Assert.All(installation.ManagedFiles, f => Assert.Equal(FileOriginalState.Missing, f.OriginalState));

        // 2. Rollback (Disable) removes adopted files
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);
        Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
    }

    [Fact]
    public void ExistingDxvkDetector_NativeNonDxvk_ReturnsNativeOrNonDxvk_CannotBeAdopted()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        gameDir.CreateFile("Game.exe", "synthetic-binary");
        gameDir.CreateFile("d3d11.dll", "standard-native-system-d3d11");

        var detector = new ExistingDxvkDetector(sourceDir.RootPath);
        var assessment = detector.AssessDirectory(gameDir.RootPath, "x64");

        Assert.Equal(ExistingDxvkStatus.NativeOrNonDxvk, assessment.Status);
        Assert.Null(assessment.MatchedVersion);
        Assert.False(assessment.CanBeAdopted);
    }

    [Fact]
    public async Task DxvkInstaller_DxvkConf_Invariant_NoConfigCreatedWhenDisabled()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);

        // Section 36 Invariant: HudEnabled is false and FrameLimit is 0 -> dxvk.conf must NOT be created
        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            HudEnabled = false,
            FrameLimit = 0
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var ok = await installer.ApplyToGameAsync(profile, release);
        Assert.True(ok);

        Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "d3d11.dll")));
        Assert.True(File.Exists(Path.Combine(gameDir.RootPath, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(gameDir.RootPath, "dxvk.conf")));
    }

    [Fact]
    public async Task DxvkInstaller_DxvkConf_CreatedWhenHudOrLimitEnabled_AndCleanedOnRollback()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.5-dxgi");

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
            Architecture = "x64",
            HudEnabled = true,
            FrameLimit = 144
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var ok = await installer.ApplyToGameAsync(profile, release);
        Assert.True(ok);

        var confPath = Path.Combine(gameDir.RootPath, "dxvk.conf");
        Assert.True(File.Exists(confPath));
        var content = File.ReadAllText(confPath);
        Assert.Contains("dxvk.hud = fps,devinfo", content);
        Assert.Contains("dxvk.maxFrameRate = 144", content);

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        var confRecord = installation!.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord);
        Assert.Equal(FileOriginalState.Missing, confRecord!.OriginalState);

        // Rollback cleans up dxvk.conf because it was originally absent
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);
        Assert.False(File.Exists(confPath));
    }

    [Fact]
    public async Task DxvkInstaller_DxvkConf_PreExistingUserConfig_PreservedAndRestoredOnRollback()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalConfContent = "dxvk.numCompilerThreads = 8" + Environment.NewLine;
        gameDir.CreateFile("dxvk.conf", originalConfContent);

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
            Architecture = "x64",
            HudEnabled = true,
            FrameLimit = 0
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        // 1. Install merges with user config
        var ok = await installer.ApplyToGameAsync(profile, release);
        Assert.True(ok);

        var confPath = Path.Combine(gameDir.RootPath, "dxvk.conf");
        Assert.True(File.Exists(confPath));
        var content = File.ReadAllText(confPath);
        Assert.Contains("dxvk.hud = fps,devinfo", content);
        Assert.Contains("dxvk.numCompilerThreads = 8", content);

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        var confRecord = installation!.FindManagedFile("dxvk.conf");
        Assert.NotNull(confRecord);
        Assert.Equal(FileOriginalState.Existing, confRecord!.OriginalState);

        // 2. Rollback restores the user's original config
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);
        Assert.True(File.Exists(confPath));
        Assert.Equal(originalConfContent, File.ReadAllText(confPath));
    }

    [Fact]
    public async Task DxvkInstaller_ReapplyAsync_RestoresMissingDllAfterExternalChange()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        var ok = await installer.ApplyToGameAsync(profile, release);
        Assert.True(ok);

        var d3d11Path = Path.Combine(gameDir.RootPath, "d3d11.dll");
        Assert.True(File.Exists(d3d11Path));

        // Simulate external change: Steam game update deleted d3d11.dll
        File.Delete(d3d11Path);
        Assert.False(File.Exists(d3d11Path));

        // ReapplyAsync safely restores missing file from cached release
        var reapplyOk = await installer.ReapplyAsync(profile);
        Assert.True(reapplyOk);
        Assert.True(File.Exists(d3d11Path));
        Assert.Equal("dxvk-2.5-d3d11", File.ReadAllText(d3d11Path));
    }

    [Fact]
    public async Task DxvkInstaller_And_DxvkRollback_FailClosed_WhenConflicted()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        // Create an installation with ConflictFlags != None
        var installation = store.GetOrCreateInstallation(gameDir.RootPath, "Game");
        installation.ConflictFlags = InstallationConflictFlags.DxvkVersion;
        installation.ManagedDxvkVersion = "2.5";
        store.Save(installation);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        // 1. Apply must fail closed
        var applyOk = await installer.ApplyToGameAsync(profile, release);
        Assert.False(applyOk);

        // 2. Reapply must fail closed
        var reapplyOk = await installer.ReapplyAsync(profile);
        Assert.False(reapplyOk);

        // 3. Rollback must fail closed
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.False(rollbackOk);

        // 4. AdoptExisting must fail closed
        var fakeAssessment = new ExistingDxvkAssessment
        {
            Status = ExistingDxvkStatus.OfficialRelease,
            MatchedVersion = "2.5",
            DetectedDlls = new[] { "d3d11.dll" }
        };
        var adoptOk = installer.AdoptExisting(profile, fakeAssessment);
        Assert.False(adoptOk);
    }
}
