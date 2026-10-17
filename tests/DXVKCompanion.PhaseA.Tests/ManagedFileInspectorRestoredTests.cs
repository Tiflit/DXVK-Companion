using System;
using System.IO;
using System.Linq;
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

public sealed class ManagedFileInspectorRestoredTests
{
    private static string ComputeSha256Hex(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact]
    public async Task Install_Restore_Inspect_MaintainsRestoredAndConsistent_WithoutNewExternalChange()
    {
        // Acceptance 1:
        // Exercise the production lifecycle in synthetic directories:
        // install DXVK over one original DLL plus one originally absent DLL,
        // restore successfully, then inspect.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11-payload");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi-payload");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11 = "original-native-d3d11-content";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalD3D11);
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

        // 1. Install DXVK 2.5
        var installOk = await installer.ApplyToGameAsync(profile, release);
        Assert.True(installOk);
        Assert.Equal("dxvk-2.5-d3d11-payload", File.ReadAllText(d3d11Path));
        Assert.True(File.Exists(dxgiPath));
        Assert.Equal("dxvk-2.5-dxgi-payload", File.ReadAllText(dxgiPath));

        // 2. Restore original DLLs
        var rollbackOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(rollbackOk);

        // Verification after restore: original content restored, created DLL deleted
        Assert.Equal(originalD3D11, File.ReadAllText(d3d11Path));
        Assert.False(File.Exists(dxgiPath));

        var installation = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(installation);
        Assert.Equal(RestorationState.Restored, installation!.RestorationState);
        Assert.Null(installation.ManagedDxvkVersion);

        // 3. Inspect installation
        var inspector = new ManagedFileInspector(store);
        bool changed = inspector.InspectInstallation(installation);

        // Invariant: No external change reported
        Assert.False(changed);
        Assert.Equal(RestorationState.Restored, installation.RestorationState);

        var d3d11Record = installation.FindManagedFile("d3d11.dll");
        Assert.NotNull(d3d11Record);
        Assert.Equal(ManagedFileState.Consistent, d3d11Record!.CurrentState);

        var dxgiRecord = installation.FindManagedFile("dxgi.dll");
        Assert.NotNull(dxgiRecord);
        Assert.Equal(ManagedFileState.Consistent, dxgiRecord!.CurrentState);

        // Original content remains unchanged and Companion-created DLL remains absent
        Assert.Equal(originalD3D11, File.ReadAllText(d3d11Path));
        Assert.False(File.Exists(dxgiPath));

        // 4. InspectAll coverage
        int allChanged = inspector.InspectAll();
        Assert.Equal(0, allChanged);

        // 5. Fresh-store reload coverage
        var reloadedStore = new GameLibraryStore(storePath, backupsPath);
        var reloadedInst = reloadedStore.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(reloadedInst);
        Assert.Equal(RestorationState.Restored, reloadedInst!.RestorationState);
        Assert.Null(reloadedInst.ManagedDxvkVersion);

        var reloadedInspector = new ManagedFileInspector(reloadedStore);
        bool reloadedChanged = reloadedInspector.InspectInstallation(reloadedInst);
        Assert.False(reloadedChanged);
        Assert.Equal(RestorationState.Restored, reloadedInst.RestorationState);
        Assert.Equal(ManagedFileState.Consistent, reloadedInst.FindManagedFile("d3d11.dll")!.CurrentState);
        Assert.Equal(ManagedFileState.Consistent, reloadedInst.FindManagedFile("dxgi.dll")!.CurrentState);
    }

    [Fact]
    public async Task RestoredLifecycle_OriginallyMissing_UnexpectedFilePresent_FlagsAttention_AndPersistsAcrossScans()
    {
        // Acceptance 2 & 3:
        // Unexpected files at originally absent paths produce attention; repeated scans retain unresolved attention.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11 = "original-d3d11-content";
        gameDir.CreateFile("d3d11.dll", originalD3D11);
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

        Assert.True(await installer.ApplyToGameAsync(profile, release));
        Assert.True(await rollback.RestoreOriginalDllsAsync(profile));

        var installation = store.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.Restored, installation.RestorationState);

        // Unexpected file dropped at originally absent path (dxgi.dll)
        File.WriteAllText(dxgiPath, "unexpected-injected-dxgi-bytes");

        // Queue a pending action to test Section 20 supersession
        installation.PendingAction = PendingAction.Reapply("2.5", "Initial user action");
        store.Save(installation);

        var inspector = new ManagedFileInspector(store);

        // Scan 1: Initial detection flags new transition and AttentionRequired
        bool scan1Changed = inspector.InspectInstallation(installation);
        Assert.True(scan1Changed);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.Equal(ManagedFileState.ExternallyChanged, installation.FindManagedFile("dxgi.dll")!.CurrentState);
        // Stale pending action invalidated on new transition
        Assert.Null(installation.PendingAction);

        // Queue a NEW pending action after the change occurred
        installation.PendingAction = PendingAction.Reapply("2.5", "Newly queued action after mismatch");
        store.Save(installation);

        // Scan 2: Unresolved condition retains AttentionRequired but returns false (no new transition)
        bool scan2Changed = inspector.InspectInstallation(installation);
        Assert.False(scan2Changed);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.Equal(ManagedFileState.ExternallyChanged, installation.FindManagedFile("dxgi.dll")!.CurrentState);
        // Newly queued action is preserved
        Assert.NotNull(installation.PendingAction);

        // Scan 3: Repeated scan continues to preserve attention and pending action
        bool scan3Changed = inspector.InspectInstallation(installation);
        Assert.False(scan3Changed);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.NotNull(installation.PendingAction);
    }

    [Fact]
    public async Task RestoredLifecycle_OriginallyExisting_MissingFile_FlagsAttention_AndPersistsAcrossScans()
    {
        // Acceptance 2 & 3:
        // Missing original files produce attention; repeated scans retain unresolved attention.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11 = "original-d3d11-content";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalD3D11);

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        Assert.True(await installer.ApplyToGameAsync(profile, release));
        Assert.True(await rollback.RestoreOriginalDllsAsync(profile));

        var installation = store.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.Restored, installation.RestorationState);

        // Delete restored original file (d3d11.dll)
        File.Delete(d3d11Path);

        var inspector = new ManagedFileInspector(store);

        // Scan 1: Flags new change and AttentionRequired
        bool scan1Changed = inspector.InspectInstallation(installation);
        Assert.True(scan1Changed);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.Equal(ManagedFileState.ExternallyChanged, installation.FindManagedFile("d3d11.dll")!.CurrentState);

        // Scan 2: Retains AttentionRequired, returns false
        bool scan2Changed = inspector.InspectInstallation(installation);
        Assert.False(scan2Changed);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.Equal(ManagedFileState.ExternallyChanged, installation.FindManagedFile("d3d11.dll")!.CurrentState);
    }

    [Fact]
    public async Task RestoredLifecycle_OriginallyExisting_ModifiedFile_FlagsAttention_AndPersistsAcrossScans()
    {
        // Acceptance 2 & 3:
        // Modified original files produce attention; repeated scans retain unresolved attention.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11 = "original-d3d11-content";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalD3D11);

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        Assert.True(await installer.ApplyToGameAsync(profile, release));
        Assert.True(await rollback.RestoreOriginalDllsAsync(profile));

        var installation = store.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.Restored, installation.RestorationState);

        // Modify restored original file (d3d11.dll) with different content
        File.WriteAllText(d3d11Path, "modified-d3d11-content");

        var inspector = new ManagedFileInspector(store);

        // Scan 1: Flags new change and AttentionRequired
        bool scan1Changed = inspector.InspectInstallation(installation);
        Assert.True(scan1Changed);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.Equal(ManagedFileState.ExternallyChanged, installation.FindManagedFile("d3d11.dll")!.CurrentState);

        // Scan 2: Retains AttentionRequired, returns false
        bool scan2Changed = inspector.InspectInstallation(installation);
        Assert.False(scan2Changed);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.Equal(ManagedFileState.ExternallyChanged, installation.FindManagedFile("d3d11.dll")!.CurrentState);
    }

    [Fact]
    public async Task RestoredLifecycle_PostRestoreMismatch_SurvivesReload_RepairRecoversToRestored()
    {
        // Acceptance 4:
        // Attention left by a post-restore mismatch must remain on original-baseline verification after reload;
        // repairing the synthetic files back to the recorded originals permits verified recovery to Restored
        // when no conflict flags remain. Do not require obsolete DXVK bytes to clear that attention.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11 = "original-d3d11-content";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalD3D11);
        var dxgiPath = Path.Combine(gameDir.RootPath, "dxgi.dll");

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var store = new GameLibraryStore(storePath, backupsPath);
        var engine = new MultiFileTransactionEngine(backupsPath);
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        Assert.True(await installer.ApplyToGameAsync(profile, release));
        Assert.True(await rollback.RestoreOriginalDllsAsync(profile));

        var installation = store.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.Restored, installation.RestorationState);

        // Introduce post-restore mismatch: unexpected dxgi.dll present
        File.WriteAllText(dxgiPath, "unexpected-dxgi");

        var inspector = new ManagedFileInspector(store);
        Assert.True(inspector.InspectInstallation(installation));
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);

        // Reload from a fresh store
        var reloadedStore = new GameLibraryStore(storePath, backupsPath);
        var reloadedInst = reloadedStore.FindByInstallationPath(gameDir.RootPath)!;
        Assert.NotNull(reloadedInst);
        Assert.Equal(RestorationState.AttentionRequired, reloadedInst.RestorationState);
        Assert.Null(reloadedInst.ManagedDxvkVersion);

        // Inspection before repair: verifies against original baselines, remains AttentionRequired
        var reloadedInspector = new ManagedFileInspector(reloadedStore);
        Assert.False(reloadedInspector.InspectInstallation(reloadedInst));
        Assert.Equal(RestorationState.AttentionRequired, reloadedInst.RestorationState);

        // Repair synthetic files back to recorded originals: remove unexpected dxgi.dll
        File.Delete(dxgiPath);

        // Inspect after repair: verifies recovery to Restored without requiring DXVK bytes
        bool repairScanChanged = reloadedInspector.InspectInstallation(reloadedInst);
        Assert.False(repairScanChanged);
        Assert.Equal(RestorationState.Restored, reloadedInst.RestorationState);
        Assert.Equal(ManagedFileState.Consistent, reloadedInst.FindManagedFile("d3d11.dll")!.CurrentState);
        Assert.Equal(ManagedFileState.Consistent, reloadedInst.FindManagedFile("dxgi.dll")!.CurrentState);

        // Confirm persistence of recovered Restored state
        var finalStore = new GameLibraryStore(storePath, backupsPath);
        var finalInst = finalStore.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.Restored, finalInst.RestorationState);
    }

    [Fact]
    public async Task RestoredLifecycle_ConflictFlagsPresent_PreventsRecoveryToRestored()
    {
        // Acceptance 5: Nonzero conflict flags continue preventing healthy recovery.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11 = "original-d3d11-content";
        gameDir.CreateFile("d3d11.dll", originalD3D11);
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

        Assert.True(await installer.ApplyToGameAsync(profile, release));
        Assert.True(await rollback.RestoreOriginalDllsAsync(profile));

        var installation = store.FindByInstallationPath(gameDir.RootPath)!;

        // Introduce mismatch to enter AttentionRequired
        File.WriteAllText(dxgiPath, "unexpected-dxgi");
        var inspector = new ManagedFileInspector(store);
        Assert.True(inspector.InspectInstallation(installation));
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);

        // Set conflict flags and repair the file
        installation.ConflictFlags = InstallationConflictFlags.DxvkVersion;
        store.Save(installation);
        File.Delete(dxgiPath);

        // Inspect: files are consistent, but conflict flags block recovery
        bool scanChanged = inspector.InspectInstallation(installation);
        Assert.False(scanChanged);
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
    }

    [Fact]
    public void RestoredLifecycle_UnusableOriginalEvidence_OrUnknownOriginalState_FlagsAttention()
    {
        // Acceptance 2: Unknown/unsupported original state or unusable original evidence
        // must not be treated as verified healthy.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();

        var storePath = Path.Combine(storageDir.RootPath, "game-library.json");
        var backupsPath = Path.Combine(storageDir.RootPath, "backups");
        var store = new GameLibraryStore(storePath, backupsPath);

        var d3d11Path = gameDir.CreateFile("d3d11.dll", "some-content");

        // Case A: Null original hash
        var instNull = new GameInstallation
        {
            InstallationPath = gameDir.RootPath,
            DisplayName = "Test Game Null Hash",
            RestorationState = RestorationState.Restored,
            ManagedDxvkVersion = null
        };
        instNull.ManagedFiles.Add(new ManagedFileRecord
        {
            RelativePath = "d3d11.dll",
            OriginalState = FileOriginalState.Existing,
            OriginalSha256 = null,
            CurrentState = ManagedFileState.Consistent
        });
        store.Save(instNull);

        var inspector = new ManagedFileInspector(store);
        Assert.True(inspector.InspectInstallation(instNull));
        Assert.Equal(RestorationState.AttentionRequired, instNull.RestorationState);
        Assert.Equal(ManagedFileState.ExternallyChanged, instNull.FindManagedFile("d3d11.dll")!.CurrentState);

        // Case B: Non-hex or invalid-length original hash
        var instBadHex = new GameInstallation
        {
            InstallationPath = gameDir.RootPath,
            DisplayName = "Test Game Bad Hex",
            RestorationState = RestorationState.Restored,
            ManagedDxvkVersion = null
        };
        instBadHex.ManagedFiles.Add(new ManagedFileRecord
        {
            RelativePath = "d3d11.dll",
            OriginalState = FileOriginalState.Existing,
            OriginalSha256 = new string('z', 64),
            CurrentState = ManagedFileState.Consistent
        });
        store.Save(instBadHex);

        Assert.True(inspector.InspectInstallation(instBadHex));
        Assert.Equal(RestorationState.AttentionRequired, instBadHex.RestorationState);
        Assert.Equal(ManagedFileState.ExternallyChanged, instBadHex.FindManagedFile("d3d11.dll")!.CurrentState);

        // Case C: Unknown original state
        var instUnknown = new GameInstallation
        {
            InstallationPath = gameDir.RootPath,
            DisplayName = "Test Game Unknown State",
            RestorationState = RestorationState.Restored,
            ManagedDxvkVersion = null
        };
        instUnknown.ManagedFiles.Add(new ManagedFileRecord
        {
            RelativePath = "d3d11.dll",
            OriginalState = FileOriginalState.Unknown,
            OriginalSha256 = ComputeSha256Hex("some-content"),
            CurrentState = ManagedFileState.Consistent
        });
        store.Save(instUnknown);

        Assert.True(inspector.InspectInstallation(instUnknown));
        Assert.Equal(RestorationState.AttentionRequired, instUnknown.RestorationState);
        Assert.Equal(ManagedFileState.ExternallyChanged, instUnknown.FindManagedFile("d3d11.dll")!.CurrentState);
    }

    [Fact]
    public async Task ActiveManagement_ProtectionsPreserved_DoesNotUseRestoredBaseline()
    {
        // Acceptance 5: Preserve active managed-file hash checks.
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        var dxvkArchDir = Path.Combine(sourceDir.RootPath, "2.5", "x64");
        Directory.CreateDirectory(dxvkArchDir);
        File.WriteAllText(Path.Combine(dxvkArchDir, "d3d11.dll"), "dxvk-2.5-d3d11");
        File.WriteAllText(Path.Combine(dxvkArchDir, "dxgi.dll"), "dxvk-2.5-dxgi");

        var exePath = gameDir.CreateFile("Game.exe", "synthetic-binary");
        var originalD3D11 = "original-d3d11-native";
        var d3d11Path = gameDir.CreateFile("d3d11.dll", originalD3D11);

        var store = new GameLibraryStore(
            Path.Combine(storageDir.RootPath, "game-library.json"),
            Path.Combine(storageDir.RootPath, "backups"));
        var engine = new MultiFileTransactionEngine(Path.Combine(storageDir.RootPath, "backups"));
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);

        var profile = new GameProfile(exePath) { Api = GraphicsApi.DX11, Architecture = "x64" };
        var release = new ReleaseInfo { Version = "2.5", DownloadUrl = "" };

        // Install DXVK
        Assert.True(await installer.ApplyToGameAsync(profile, release));

        var installation = store.FindByInstallationPath(gameDir.RootPath)!;
        Assert.Equal(RestorationState.Managed, installation.RestorationState);
        Assert.Equal("2.5", installation.ManagedDxvkVersion);

        var inspector = new ManagedFileInspector(store);

        // Active management scan: DXVK files match ExpectedManagedSha256 -> stays Managed & Consistent
        Assert.False(inspector.InspectInstallation(installation));
        Assert.Equal(RestorationState.Managed, installation.RestorationState);
        Assert.Equal(ManagedFileState.Consistent, installation.FindManagedFile("d3d11.dll")!.CurrentState);

        // Replace d3d11.dll with the original native file content (which does not match ExpectedManagedSha256)
        File.WriteAllText(d3d11Path, originalD3D11);

        // Must NOT treat this as healthy (it is actively managed, so ExpectedManagedSha256 is the baseline)
        Assert.True(inspector.InspectInstallation(installation));
        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.Equal(ManagedFileState.ExternallyChanged, installation.FindManagedFile("d3d11.dll")!.CurrentState);
    }
}
