using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class ManagedFileInspectorAttentionTests
{
    private static string ComputeSha256Hex(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact]
    public async Task MissingDll_RepeatedScan_RetainsExternallyChangedAndAttentionRequired_WithoutRepeatedNotification()
    {
        // Acceptance Criterion 1 (missing DLL):
        // Detect a missing DLL, then inspect again without repairing: retain ExternallyChanged and AttentionRequired.
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

        // External change: d3d11.dll is deleted
        File.Delete(d3d11Path);

        var inspector = new ManagedFileInspector(store);
        var installation = store.FindByInstallationPath(gameDir.RootPath)!;
        Assert.NotNull(installation);

        // Scan 1: Initial detection triggers new external change and sets AttentionRequired
        bool scan1Changed = inspector.InspectInstallation(installation);
        Assert.True(scan1Changed);

        var d3d11Record = installation.FindManagedFile("d3d11.dll")!;
        Assert.NotNull(d3d11Record);
        Assert.Equal(ManagedFileState.ExternallyChanged, d3d11Record.CurrentState);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);

        // Scan 2 (without repairing): Must retain ExternallyChanged and AttentionRequired, but return false (no new transition)
        bool scan2Changed = inspector.InspectInstallation(installation);
        Assert.False(scan2Changed);
        Assert.Equal(ManagedFileState.ExternallyChanged, d3d11Record.CurrentState);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);

        // Scan 3: Still persists across subsequent scans
        bool scan3Changed = inspector.InspectInstallation(installation);
        Assert.False(scan3Changed);
        Assert.Equal(ManagedFileState.ExternallyChanged, d3d11Record.CurrentState);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
    }

    [Fact]
    public async Task ReplacedDll_RepeatedScan_RetainsExternallyChangedAndAttentionRequired_WithoutRepeatedNotification()
    {
        // Acceptance Criterion 1 (replaced DLL):
        // Detect an externally replaced DLL, then inspect again without repairing: retain ExternallyChanged and AttentionRequired.
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

        // External change: overwrite d3d11.dll with modified bytes
        File.WriteAllText(d3d11Path, "external-game-patch-content");

        var inspector = new ManagedFileInspector(store);
        var installation = store.FindByInstallationPath(gameDir.RootPath)!;
        Assert.NotNull(installation);

        // Scan 1: Initial detection
        bool scan1Changed = inspector.InspectInstallation(installation);
        Assert.True(scan1Changed);

        var d3d11Record = installation.FindManagedFile("d3d11.dll")!;
        Assert.NotNull(d3d11Record);
        Assert.Equal(ManagedFileState.ExternallyChanged, d3d11Record.CurrentState);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);

        // Scan 2 (without repairing): Unresolved condition retained
        bool scan2Changed = inspector.InspectInstallation(installation);
        Assert.False(scan2Changed);
        Assert.Equal(ManagedFileState.ExternallyChanged, d3d11Record.CurrentState);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
    }

    [Fact]
    public async Task UnresolvedAttention_SurvivesStoreReload_BeforeRepeatedScan()
    {
        // Acceptance Criterion 2:
        // Persist and reload through a fresh GameLibraryStore/inspector before the repeated scan; unresolved attention survives.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var releaseDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(releaseDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");

        string libraryPath = Path.Combine(storageDir.RootPath, "game-library.json");
        string backupsPath = Path.Combine(storageDir.RootPath, "backups");

        var store = new GameLibraryStore(libraryPath, backupsPath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        await installer.ApplyToGameAsync(profile, release);

        // Overwrite d3d11.dll
        var d3d11Path = Path.Combine(gameDir.RootPath, "d3d11.dll");
        File.WriteAllText(d3d11Path, "modified-bytes");

        var inspector = new ManagedFileInspector(store);
        var installation = store.FindByInstallationPath(gameDir.RootPath)!;
        inspector.InspectInstallation(installation);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);

        // Reload store and inspector from disk
        var freshStore = new GameLibraryStore(libraryPath, backupsPath);
        var freshInspector = new ManagedFileInspector(freshStore);
        var loadedInstallation = freshStore.FindByInstallationPath(gameDir.RootPath)!;
        Assert.NotNull(loadedInstallation);

        // Pre-scan assertion after reload
        Assert.Equal(RestorationState.AttentionRequired, loadedInstallation.RestorationState);
        var loadedRecord = loadedInstallation.FindManagedFile("d3d11.dll")!;
        Assert.Equal(ManagedFileState.ExternallyChanged, loadedRecord.CurrentState);

        // Repeated scan on fresh store instance
        bool changed = freshInspector.InspectInstallation(loadedInstallation);
        Assert.False(changed);
        Assert.Equal(RestorationState.AttentionRequired, loadedInstallation.RestorationState);
        Assert.Equal(ManagedFileState.ExternallyChanged, loadedRecord.CurrentState);

        // Verify disk persistence after repeated scan
        var reloadedStore = new GameLibraryStore(libraryPath, backupsPath);
        var persistedInstallation = reloadedStore.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.AttentionRequired, persistedInstallation.RestorationState);
    }

    [Fact]
    public async Task MultiFileSet_RepairingOneFile_CannotClearAttention_WhileAnotherRemainsInconsistent()
    {
        // Acceptance Criterion 3:
        // In a multi-file set, repairing one file cannot clear attention while another remains inconsistent.
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

        await installer.ApplyToGameAsync(profile, release);

        var d3d11Path = Path.Combine(gameDir.RootPath, "d3d11.dll");
        var dxgiPath = Path.Combine(gameDir.RootPath, "dxgi.dll");

        // Both files become inconsistent: d3d11 modified, dxgi deleted
        File.WriteAllText(d3d11Path, "modified-d3d11");
        File.Delete(dxgiPath);

        var inspector = new ManagedFileInspector(store);
        var installation = store.FindByInstallationPath(gameDir.RootPath)!;

        bool scan1 = inspector.InspectInstallation(installation);
        Assert.True(scan1);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.Equal(ManagedFileState.ExternallyChanged, installation.FindManagedFile("d3d11.dll")!.CurrentState);
        Assert.Equal(ManagedFileState.ExternallyChanged, installation.FindManagedFile("dxgi.dll")!.CurrentState);

        // Repair ONLY d3d11.dll by restoring the expected managed content; leave dxgi.dll missing
        File.WriteAllText(d3d11Path, "dxvk-2.5-d3d11");

        bool scan2 = inspector.InspectInstallation(installation);
        Assert.False(scan2);

        // d3d11 is now Consistent, but dxgi remains ExternallyChanged, so AttentionRequired MUST NOT be cleared
        Assert.Equal(ManagedFileState.Consistent, installation.FindManagedFile("d3d11.dll")!.CurrentState);
        Assert.Equal(ManagedFileState.ExternallyChanged, installation.FindManagedFile("dxgi.dll")!.CurrentState);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short-hash")]
    [InlineData("not-a-valid-hex-64-character-hash-value-0123456789abcdef0123456789zz")]
    public void MissingOrUnusableBaseline_CannotAuthorizeRecovery_AndFlagsAttention(string? invalidHash)
    {
        // Acceptance Criterion 4:
        // Only current-pass verification against usable recorded managed-hash evidence can permit recovery.
        // A missing or unusable baseline cannot substitute for verification.
        using var testDir = new SyntheticTestDirectory();
        var store = new GameLibraryStore(
            Path.Combine(testDir.RootPath, "game-library.json"),
            Path.Combine(testDir.RootPath, "backups"));

        string gameDir = Path.Combine(testDir.RootPath, "UnusableBaselineGame");
        Directory.CreateDirectory(gameDir);
        string d3d11Path = Path.Combine(gameDir, "d3d11.dll");
        File.WriteAllText(d3d11Path, "some-dll-bytes");

        var installation = store.GetOrCreateInstallation(gameDir, "UnusableBaselineGame");
        installation.RestorationState = RestorationState.AttentionRequired;
        installation.ManagedDxvkVersion = "2.5";
        installation.ConflictFlags = InstallationConflictFlags.None;
        installation.ManagedFiles.Add(new ManagedFileRecord
        {
            RelativePath = "d3d11.dll",
            OriginalState = FileOriginalState.Missing,
            CurrentState = ManagedFileState.Consistent, // Stale persisted consistent state
            ExpectedManagedSha256 = invalidHash,
            ManagedDxvkVersion = "2.5"
        });
        store.Save(installation);

        var inspector = new ManagedFileInspector(store);
        inspector.InspectInstallation(installation);

        // Must NOT promote to Managed; must remain AttentionRequired and mark record ExternallyChanged
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.Equal(ManagedFileState.ExternallyChanged, installation.ManagedFiles[0].CurrentState);
    }

    [Fact]
    public async Task AllFilesRepaired_TransitionsBackToManaged_UnlessConflictFlagsPresent()
    {
        // Acceptance Criterion 5:
        // Once all active managed files match their recorded hashes, preserve healthy recovery behavior.
        // Nonzero conflict flags still prevent recovery.
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

        await installer.ApplyToGameAsync(profile, release);

        var d3d11Path = Path.Combine(gameDir.RootPath, "d3d11.dll");
        File.WriteAllText(d3d11Path, "modified-dll");

        var inspector = new ManagedFileInspector(store);
        var installation = store.FindByInstallationPath(gameDir.RootPath)!;
        inspector.InspectInstallation(installation);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);

        // Case A: When conflict flags are present, repairing files still does NOT clear AttentionRequired
        installation.ConflictFlags = InstallationConflictFlags.DxvkVersion;
        File.WriteAllText(d3d11Path, "dxvk-2.5-d3d11"); // repaired

        inspector.InspectInstallation(installation);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.Equal(ManagedFileState.Consistent, installation.FindManagedFile("d3d11.dll")!.CurrentState);

        // Case B: Once conflict flags are cleared, healthy recovery to Managed succeeds
        installation.ConflictFlags = InstallationConflictFlags.None;
        inspector.InspectInstallation(installation);
        Assert.Equal(RestorationState.Managed, installation.RestorationState);
        Assert.Equal("2.5", installation.ManagedDxvkVersion);
    }

    [Fact]
    public async Task Section20_NewlyQueuedPendingAction_NotDiscardedOnRepeatedScanOfUnresolvedCondition()
    {
        // Acceptance Criterion 6:
        // Do not turn a persistent unchanged condition into a new automatic notification or repeated invalidation.
        // Do not repeatedly discard a newly queued action solely because an old condition persists.
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

        await installer.ApplyToGameAsync(profile, release);

        // External change deletes d3d11.dll
        var d3d11Path = Path.Combine(gameDir.RootPath, "d3d11.dll");
        File.Delete(d3d11Path);

        var inspector = new ManagedFileInspector(store);
        var installation = store.FindByInstallationPath(gameDir.RootPath)!;

        // Scan 1: initial detection marks AttentionRequired
        bool scan1 = inspector.InspectInstallation(installation);
        Assert.True(scan1);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);

        // Now user/system queues a new recovery pending action while in AttentionRequired
        installation.PendingAction = PendingAction.Reapply("2.5", "Recovery action queued by user");
        store.Save(installation);

        // Scan 2: Unresolved condition still present, but NO new transition occurred
        bool scan2 = inspector.InspectInstallation(installation);
        Assert.False(scan2);

        // PendingAction must NOT be discarded or invalidated on repeated scan of existing condition
        Assert.NotNull(installation.PendingAction);
        Assert.Equal(PendingActionType.Reapply, installation.PendingAction!.Type);
        Assert.True(installation.PendingAction.IsPending);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
    }
}
