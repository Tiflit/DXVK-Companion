using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Safety;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests;

/// <summary>
/// Issue #169: Reproduces the post-file ownership-metadata persistence gap across
/// First Install, Update, Reapply (with baseline replacement), Unresolved Recovery,
/// Restore, and post-replacement readback unavailability (Unknown outcome), plus
/// characterizes readable post-replacement completion in <see cref="GameLibraryStore"/>.
/// </summary>
public sealed class OwnershipPersistenceFailureTests
{
    private static void SeedLocalRelease(string sourceRoot, string version, string d3d11Content, string dxgiContent)
    {
        string archDir = Path.Combine(sourceRoot, version, "x64");
        Directory.CreateDirectory(archDir);
        File.WriteAllText(Path.Combine(archDir, "d3d11.dll"), d3d11Content);
        File.WriteAllText(Path.Combine(archDir, "dxgi.dll"), dxgiContent);
    }

    [Fact]
    public async Task FirstInstall_WhenPostFileOwnershipSaveFailsBeforeReplace_CompensatesTargetFilesAndNativeBackups_AndKeepsStoreConsistent()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        SeedLocalRelease(sourceDir.RootPath, "2.4", "dxvk-2.4-d3d11", "dxvk-2.4-dxgi");

        string exePath = gameDir.CreateFile("Game.exe", "synthetic-game-exe");
        string targetD3d11 = gameDir.CreateFile("d3d11.dll", "native-original-d3d11");
        string targetDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");
        string sentinelPath = gameDir.CreateFile("data/sentinel.pak", "untouched-game-asset");
        var nativeD3d11Identity = FileIdentity.Capture(targetD3d11);

        string libraryFile = Path.Combine(storageDir.RootPath, "game-library.json");
        string backupsDir = Path.Combine(storageDir.RootPath, "backups");
        var store = new GameLibraryStore(libraryFile, backupsDir);

        bool allFilesApplied = false;
        bool backupPublishedBeforeSave = false;
        bool postFileSaveAttempted = false;
        byte[]? prePostFileSaveLibraryBytes = null;

        var engine = new MultiFileTransactionEngine(
            backupsDir,
            new MultiFileTransactionTestHooks
            {
                AfterApply = (_, index) =>
                {
                    if (index == 1)
                    {
                        allFilesApplied = true;
                        prePostFileSaveLibraryBytes = File.ReadAllBytes(libraryFile);

                        var instDuringApply = store.FindByInstallationPath(gameDir.RootPath);
                        if (instDuringApply != null)
                        {
                            string candidateBackup = Path.Combine(backupsDir, instDuringApply.Id, "d3d11.dll");
                            backupPublishedBeforeSave = File.Exists(candidateBackup);
                        }

                        // Arm failure specifically for the post-file ownership Save call (after initial GetOrCreateInstallation).
                        store.CustomCommitReplace = (tempPath, destPath) =>
                        {
                            postFileSaveAttempted = true;
                            throw new IOException("Simulated pre-replacement failure during post-file ownership metadata save.");
                        };
                    }
                }
            });

        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            HudEnabled = true,
            FrameLimit = 120
        };

        bool installOk = await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.4", DownloadUrl = "" });
        store.CustomCommitReplace = null;

        // 1. Verify the failure occurred at the intended post-file metadata commit stage, not initial creation or preflight.
        Assert.False(installOk);
        Assert.True(allFilesApplied, "Expected MultiFileTransactionEngine to finish applying all target files before the injected metadata save failure.");
        Assert.True(backupPublishedBeforeSave, "Expected native backup for d3d11.dll to be published before the post-file Save attempt.");
        Assert.True(postFileSaveAttempted, "Expected post-file GameLibraryStore.Save commit replacement to be attempted.");
        Assert.NotNull(prePostFileSaveLibraryBytes);
        Assert.Equal(prePostFileSaveLibraryBytes!, File.ReadAllBytes(libraryFile));
        Assert.Equal("untouched-game-asset", File.ReadAllText(sentinelPath));

        // 2. Compare in-memory store state vs. newly reconstructed store from durable disk bytes.
        var inMemoryInst = store.FindByInstallationPath(gameDir.RootPath);
        var reconstructedStore = new GameLibraryStore(libraryFile, backupsDir);
        var reconstructedInst = reconstructedStore.FindByInstallationPath(gameDir.RootPath);

        Assert.NotNull(inMemoryInst);
        Assert.NotNull(reconstructedInst);
        Assert.Equal(RestorationState.None, reconstructedInst!.RestorationState);
        Assert.Null(reconstructedInst.ManagedDxvkVersion);
        Assert.Empty(reconstructedInst.ManagedFiles);

        string canonicalBackupD3d11 = Path.Combine(backupsDir, inMemoryInst!.Id, "d3d11.dll");
        string transactionsRoot = Path.Combine(backupsDir, ".transactions");

        // 3. Expected-red safe invariant assertions:
        // When post-file ownership persistence fails with ProvenNotCommitted, target files must be compensated back
        // to their pre-install native baseline (not left with orphaned DXVK DLLs), newly published native backups
        // and per-operation .transactions directories must be cleaned up, and in-memory store state (including
        // Configuration and Executables) must match the durable on-disk library record.
        Assert.Equal("native-original-d3d11", File.ReadAllText(targetD3d11));
        Assert.Equal(nativeD3d11Identity, FileIdentity.Capture(targetD3d11));
        Assert.False(File.Exists(targetDxgi), "Originally absent dxgi.dll must not remain deployed when ownership metadata fails to commit.");
        Assert.False(File.Exists(canonicalBackupD3d11), "Unreferenced native backup created during an uncommitted First Install must be rolled back.");
        Assert.False(
            Directory.Exists(transactionsRoot) && Directory.GetDirectories(transactionsRoot).Length > 0,
            "Per-operation .transactions evidence must be cleaned up after verified compensation.");
        Assert.Equal(reconstructedInst.RestorationState, inMemoryInst.RestorationState);
        Assert.Equal(reconstructedInst.ManagedDxvkVersion, inMemoryInst.ManagedDxvkVersion);
        Assert.Equal(reconstructedInst.ManagedFiles.Count, inMemoryInst.ManagedFiles.Count);
        Assert.Equal(reconstructedInst.Configuration.HudEnabled, inMemoryInst.Configuration.HudEnabled);
    }

    [Fact]
    public async Task UpdateAfterInstallA_WhenPostFileOwnershipSaveFailsBeforeReplace_RecoversVersionAFilesAndPreservesRestoreCapability()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        SeedLocalRelease(sourceDir.RootPath, "2.4", "dxvk-2.4-d3d11", "dxvk-2.4-dxgi");
        SeedLocalRelease(sourceDir.RootPath, "2.5", "dxvk-2.5-d3d11", "dxvk-2.5-dxgi");

        string exePath = gameDir.CreateFile("Game.exe", "synthetic-game-exe");
        string targetD3d11 = gameDir.CreateFile("d3d11.dll", "native-original-d3d11");
        string targetDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");
        var nativeD3d11Identity = FileIdentity.Capture(targetD3d11);

        string libraryFile = Path.Combine(storageDir.RootPath, "game-library.json");
        string backupsDir = Path.Combine(storageDir.RootPath, "backups");
        var store = new GameLibraryStore(libraryFile, backupsDir);

        bool armUpdateSaveFailure = false;
        bool updateFilesApplied = false;
        bool updatePostFileSaveAttempted = false;

        var engine = new MultiFileTransactionEngine(
            backupsDir,
            new MultiFileTransactionTestHooks
            {
                AfterApply = (_, index) =>
                {
                    if (armUpdateSaveFailure && index == 1)
                    {
                        updateFilesApplied = true;
                        store.CustomCommitReplace = (tempPath, destPath) =>
                        {
                            updatePostFileSaveAttempted = true;
                            throw new IOException("Simulated pre-replacement failure during Update B ownership metadata save.");
                        };
                    }
                }
            });

        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        // 1. Real successful Install of Version A (2.4)
        bool installAOk = await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.4", DownloadUrl = "" });
        Assert.True(installAOk);
        Assert.Equal("dxvk-2.4-d3d11", File.ReadAllText(targetD3d11));
        Assert.Equal("dxvk-2.4-dxgi", File.ReadAllText(targetDxgi));

        var postInstallALibraryBytes = File.ReadAllBytes(libraryFile);
        var instAfterA = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(instAfterA);
        string backupRelD3d11 = instAfterA!.FindManagedFile("d3d11.dll")!.BackupRelativePath!;
        string fullBackupD3d11 = Path.Combine(backupsDir, backupRelD3d11);
        Assert.Equal("native-original-d3d11", File.ReadAllText(fullBackupD3d11));

        // 2. Attempt Update to Version B (2.5) where file execution completes and metadata save fails before replace
        armUpdateSaveFailure = true;
        bool updateBOk = await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.5", DownloadUrl = "" });
        store.CustomCommitReplace = null;

        Assert.False(updateBOk);
        Assert.True(updateFilesApplied, "Expected Update B file execution to finish before metadata save failed.");
        Assert.True(updatePostFileSaveAttempted, "Expected post-file GameLibraryStore.Save to be attempted during Update B.");
        Assert.Equal(postInstallALibraryBytes, File.ReadAllBytes(libraryFile));

        // True native baseline backup must remain untouched across Install A and failed Update B
        Assert.Equal("native-original-d3d11", File.ReadAllText(fullBackupD3d11));
        Assert.Equal(nativeD3d11Identity, FileIdentity.Capture(fullBackupD3d11));

        // 3. Reconstruct services from durable disk state and verify file/metadata version alignment
        var reconstructedStore = new GameLibraryStore(libraryFile, backupsDir);
        var reconstructedInst = reconstructedStore.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(reconstructedInst);
        Assert.Equal("2.4", reconstructedInst!.ManagedDxvkVersion);
        Assert.Equal(RestorationState.Managed, reconstructedInst.RestorationState);

        var inMemoryInstAfterFailedB = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(inMemoryInstAfterFailedB);

        // Expected-red safe invariant assertions:
        // Target files on disk must be recovered to Version A (2.4) so they match the durable Version A metadata,
        // in-memory store state must remain aligned with durable Version A metadata, and a reconstructed
        // DxvkRollback must be able to restore the true native baseline without a managed target hash mismatch.
        Assert.Equal("dxvk-2.4-d3d11", File.ReadAllText(targetD3d11));
        Assert.Equal("dxvk-2.4-dxgi", File.ReadAllText(targetDxgi));
        Assert.Equal("2.4", inMemoryInstAfterFailedB!.ManagedDxvkVersion);

        var reconstructedEngine = new MultiFileTransactionEngine(backupsDir);
        var reconstructedRollback = new DxvkRollback(reconstructedEngine, reconstructedStore);
        bool restoreOk = await reconstructedRollback.RestoreOriginalDllsAsync(profile);

        Assert.True(restoreOk, $"Expected Restore on reconstructed services to succeed after failed Update B, but it refused with: {reconstructedRollback.LastRefusalReason}");
        Assert.Equal("native-original-d3d11", File.ReadAllText(targetD3d11));
        Assert.False(File.Exists(targetDxgi));
    }

    [Fact]
    public async Task ReapplyUpdateBaseline_WhenPostFileOwnershipSaveFailsBeforeReplace_RestoresPriorBackupAndPreReapplyTargets_ThenRetryAndRestoreSucceed()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        SeedLocalRelease(sourceDir.RootPath, "2.4", "dxvk-2.4-d3d11", "dxvk-2.4-dxgi");

        string exePath = gameDir.CreateFile("Game.exe", "synthetic-game-exe");
        string targetD3d11 = gameDir.CreateFile("d3d11.dll", "native-v1-original-d3d11");
        string targetDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");
        string targetConf = Path.Combine(gameDir.RootPath, "dxvk.conf");
        var nativeV1Identity = FileIdentity.Capture(targetD3d11);

        string libraryFile = Path.Combine(storageDir.RootPath, "game-library.json");
        string backupsDir = Path.Combine(storageDir.RootPath, "backups");
        var store = new GameLibraryStore(libraryFile, backupsDir);

        bool armReapplySaveFailure = false;
        bool reapplyFilesApplied = false;
        bool reapplyPostFileSaveAttempted = false;

        var engine = new MultiFileTransactionEngine(
            backupsDir,
            new MultiFileTransactionTestHooks
            {
                AfterApply = (_, index) =>
                {
                    // With HudEnabled = true and FrameLimit = 60, Reapply processes d3d11.dll (0), dxgi.dll (1), and dxvk.conf (2).
                    if (armReapplySaveFailure && index == 2)
                    {
                        reapplyFilesApplied = true;
                        store.CustomCommitReplace = (tempPath, destPath) =>
                        {
                            reapplyPostFileSaveAttempted = true;
                            throw new IOException("Simulated pre-replacement failure during Reapply(updateBaseline: true) ownership metadata save.");
                        };
                    }
                }
            });

        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);
        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64",
            HudEnabled = false,
            FrameLimit = 0
        };

        // 1. Real successful Install of Version A (2.4) over initial native-v1 baseline
        bool installAOk = await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.4", DownloadUrl = "" });
        Assert.True(installAOk);

        var instAfterInstall = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(instAfterInstall);
        string backupRelD3d11 = instAfterInstall!.FindManagedFile("d3d11.dll")!.BackupRelativePath!;
        string fullBackupD3d11 = Path.Combine(backupsDir, backupRelD3d11);
        Assert.Equal("native-v1-original-d3d11", File.ReadAllText(fullBackupD3d11));
        Assert.Equal(nativeV1Identity.Sha256, instAfterInstall.FindManagedFile("d3d11.dll")!.OriginalSha256);

        // 2. Simulate an external game patch replacing d3d11.dll on disk with native-v2
        File.WriteAllText(targetD3d11, "native-v2-patched-d3d11");
        var nativeV2Identity = FileIdentity.Capture(targetD3d11);
        var preReapplyLibraryBytes = File.ReadAllBytes(libraryFile);

        // 3. Run ReapplyAsync(updateBaseline: true) with config changes, failing post-file metadata Save before replacement
        profile.HudEnabled = true;
        profile.FrameLimit = 60;
        armReapplySaveFailure = true;

        bool reapplyOk = await installer.ReapplyAsync(profile, updateBaseline: true);
        armReapplySaveFailure = false;
        store.CustomCommitReplace = null;

        Assert.False(reapplyOk);
        Assert.True(reapplyFilesApplied, "Expected Reapply file execution to complete all 3 files before metadata Save failed.");
        Assert.True(reapplyPostFileSaveAttempted, "Expected post-file GameLibraryStore.Save to be attempted during Reapply.");
        Assert.Equal(preReapplyLibraryBytes, File.ReadAllBytes(libraryFile));

        var inMemoryInst = store.FindByInstallationPath(gameDir.RootPath);
        var reconstructedStore = new GameLibraryStore(libraryFile, backupsDir);
        var reconstructedInst = reconstructedStore.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(inMemoryInst);
        Assert.NotNull(reconstructedInst);

        // Expected-red safe invariant assertions:
        // When Reapply(updateBaseline: true) fails at post-file ownership Save with ProvenNotCommitted:
        // (a) the prior canonical backup ("native-v1-original-d3d11") must NOT remain overwritten by "native-v2-patched-d3d11";
        // (b) target files on disk must be recovered to their exact pre-Reapply state ("native-v2-patched-d3d11", "dxvk-2.4-dxgi", no dxvk.conf);
        // (c) in-memory OriginalSha256 and Configuration must be restored to match durable game-library.json on disk;
        // (d) a subsequent retry of Reapply(updateBaseline: true) followed by RestoreOriginalDllsAsync must succeed and restore "native-v2-patched-d3d11".
        Assert.Equal("native-v1-original-d3d11", File.ReadAllText(fullBackupD3d11));
        Assert.Equal(nativeV1Identity, FileIdentity.Capture(fullBackupD3d11));
        Assert.Equal("native-v2-patched-d3d11", File.ReadAllText(targetD3d11));
        Assert.Equal(nativeV2Identity, FileIdentity.Capture(targetD3d11));
        Assert.Equal("dxvk-2.4-dxgi", File.ReadAllText(targetDxgi));
        Assert.False(File.Exists(targetConf), "Newly created dxvk.conf from failed Reapply must be removed during compensation.");
        Assert.Equal(nativeV1Identity.Sha256, inMemoryInst!.FindManagedFile("d3d11.dll")!.OriginalSha256);
        Assert.Equal(reconstructedInst!.Configuration.HudEnabled, inMemoryInst.Configuration.HudEnabled);
        Assert.Equal(reconstructedInst.Configuration.FrameLimit, inMemoryInst.Configuration.FrameLimit);

        // Retry Reapply(updateBaseline: true) with working store, then Restore to verify the approved new native-v2 baseline
        bool retryReapplyOk = await installer.ReapplyAsync(profile, updateBaseline: true);
        Assert.True(retryReapplyOk);
        Assert.Equal("native-v2-patched-d3d11", File.ReadAllText(fullBackupD3d11));

        bool restoreOk = await rollback.RestoreOriginalDllsAsync(profile);
        Assert.True(restoreOk);
        Assert.Equal("native-v2-patched-d3d11", File.ReadAllText(targetD3d11));
        Assert.False(File.Exists(targetDxgi));
        Assert.False(File.Exists(targetConf));
    }

    [Fact]
    public async Task UnresolvedEngineRecovery_OnManagedInstallationWithWritableStore_PersistsAttentionRequiredAndLastRefusalReason()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        SeedLocalRelease(sourceDir.RootPath, "2.4", "dxvk-2.4-d3d11", "dxvk-2.4-dxgi");
        SeedLocalRelease(sourceDir.RootPath, "2.5", "dxvk-2.5-d3d11", "dxvk-2.5-dxgi");

        string exePath = gameDir.CreateFile("Game.exe", "synthetic-game-exe");
        string targetD3d11 = gameDir.CreateFile("d3d11.dll", "native-original-d3d11");
        string targetDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");

        string libraryFile = Path.Combine(storageDir.RootPath, "game-library.json");
        string backupsDir = Path.Combine(storageDir.RootPath, "backups");
        string profilesFile = Path.Combine(storageDir.RootPath, "games.json");
        string cacheFile = Path.Combine(storageDir.RootPath, "release-cache.json");

        var store = new GameLibraryStore(libraryFile, backupsDir, profilesFile);
        var profileStore = new ProfileStore(profilesFile);
        var cacheStore = new CacheStore(cacheFile);

        bool triggerUnresolvedRecovery = false;
        bool beforeRecoveryReached = false;
        FileStream? recoveryWriteBlocker = null;

        var engine = new MultiFileTransactionEngine(
            backupsDir,
            new MultiFileTransactionTestHooks
            {
                AfterApply = (_, index) =>
                {
                    if (triggerUnresolvedRecovery && index == 0)
                    {
                        throw new IOException("Simulated update failure after overwriting first managed file.");
                    }
                },
                BeforeRecovery = _ =>
                {
                    if (triggerUnresolvedRecovery)
                    {
                        beforeRecoveryReached = true;
                        recoveryWriteBlocker = new FileStream(targetD3d11, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                }
            });

        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);
        var github = new DxvkGithubClient(http, cacheStore);
        var manager = new DxvkManager(installer, rollback, github, profileStore, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };
        profileStore.Save(profile);

        // 1. Succeed Install of Version A (2.4)
        bool enableAOk = await manager.EnableDxvkAsync(profile, "2.4");
        Assert.True(enableAOk);
        Assert.Equal("dxvk-2.4-d3d11", File.ReadAllText(targetD3d11));
        Assert.Equal("dxvk-2.4-dxgi", File.ReadAllText(targetDxgi));

        // 2. Trigger unresolved engine recovery during Update to Version B (2.5) with a writable GameLibraryStore
        triggerUnresolvedRecovery = true;
        bool enableBOk;
        try
        {
            enableBOk = await manager.EnableDxvkAsync(profile, "2.5");
        }
        finally
        {
            recoveryWriteBlocker?.Dispose();
        }

        Assert.False(enableBOk);
        Assert.True(beforeRecoveryReached, "Expected MultiFileTransactionEngine to enter recovery and fail recovery due to locked target.");

        var inMemoryInst = store.FindByInstallationPath(gameDir.RootPath);
        var reconstructedStore = new GameLibraryStore(libraryFile, backupsDir, profilesFile);
        var reconstructedInst = reconstructedStore.FindByInstallationPath(gameDir.RootPath);

        Assert.NotNull(inMemoryInst);
        Assert.NotNull(reconstructedInst);

        // Expected-red safe invariant assertions:
        // Unresolved recovery on a managed installation with a writable store must surface LastRefusalReason
        // through DxvkInstaller/DxvkManager and persist RestorationState.AttentionRequired + LastRefusalReason
        // both in memory and after GameLibraryStore reconstruction.
        Assert.False(string.IsNullOrWhiteSpace(installer.LastRefusalReason), "Expected DxvkInstaller.LastRefusalReason to be populated on unresolved recovery.");
        Assert.False(string.IsNullOrWhiteSpace(manager.LastRefusalReason), "Expected DxvkManager.LastRefusalReason to be populated on unresolved recovery.");
        Assert.Equal(RestorationState.AttentionRequired, inMemoryInst!.RestorationState);
        Assert.False(string.IsNullOrWhiteSpace(inMemoryInst.LastRefusalReason));
        Assert.Equal(RestorationState.AttentionRequired, reconstructedInst!.RestorationState);
        Assert.False(string.IsNullOrWhiteSpace(reconstructedInst.LastRefusalReason));
    }

    [Fact]
    public async Task Restore_WhenPostFileOwnershipSaveFailsBeforeReplace_AvoidsUnrecoverableSplitBrainBetweenTargetFilesAndStore()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        SeedLocalRelease(sourceDir.RootPath, "2.4", "dxvk-2.4-d3d11", "dxvk-2.4-dxgi");

        string exePath = gameDir.CreateFile("Game.exe", "synthetic-game-exe");
        string targetD3d11 = gameDir.CreateFile("d3d11.dll", "native-original-d3d11");
        string targetDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");

        string libraryFile = Path.Combine(storageDir.RootPath, "game-library.json");
        string backupsDir = Path.Combine(storageDir.RootPath, "backups");
        var store = new GameLibraryStore(libraryFile, backupsDir);
        var engine = new MultiFileTransactionEngine(backupsDir);
        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var rollback = new DxvkRollback(engine, store);

        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        // 1. Succeed Install of Version A (2.4)
        bool installOk = await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.4", DownloadUrl = "" });
        Assert.True(installOk);
        var postInstallLibraryBytes = File.ReadAllBytes(libraryFile);

        // 2. Fail the post-file GameLibraryStore.Save during RestoreOriginalDllsAsync before replacement
        int restoreSaveAttempts = 0;
        store.CustomCommitReplace = (tempPath, destPath) =>
        {
            restoreSaveAttempts++;
            throw new IOException("Simulated pre-replacement failure during post-file Restore ownership save.");
        };

        bool restoreOk = await rollback.RestoreOriginalDllsAsync(profile);
        store.CustomCommitReplace = null;

        Assert.False(restoreOk);
        Assert.True(restoreSaveAttempts >= 1, "Expected post-file GameLibraryStore.Save to be attempted after Restore file execution.");
        Assert.Equal(postInstallLibraryBytes, File.ReadAllBytes(libraryFile));

        var inMemoryInst = store.FindByInstallationPath(gameDir.RootPath);
        var reconstructedStore = new GameLibraryStore(libraryFile, backupsDir);
        var reconstructedInst = reconstructedStore.FindByInstallationPath(gameDir.RootPath);

        Assert.NotNull(inMemoryInst);
        Assert.NotNull(reconstructedInst);

        // Expected-red safe invariant assertions:
        // When post-file Save fails before replacement during Restore, in-memory state must not diverge from
        // reconstructed disk state (e.g., clearing ManagedDxvkVersion in memory while disk still records 2.4),
        // target files must be compensated back to the managed 2.4 state matching durable metadata, and a
        // subsequent Restore on reconstructed services must succeed cleanly.
        Assert.Equal(reconstructedInst!.ManagedDxvkVersion, inMemoryInst!.ManagedDxvkVersion);
        Assert.Equal("dxvk-2.4-d3d11", File.ReadAllText(targetD3d11));
        Assert.True(File.Exists(targetDxgi), "Expected dxgi.dll to be compensated back to managed state when Restore ownership save failed before replacement.");

        var reconstructedRollback = new DxvkRollback(new MultiFileTransactionEngine(backupsDir), reconstructedStore);
        bool retryRestoreOk = await reconstructedRollback.RestoreOriginalDllsAsync(profile);
        Assert.True(retryRestoreOk, $"Expected retry of RestoreOriginalDllsAsync on reconstructed services to succeed, but it refused with: {reconstructedRollback.LastRefusalReason}");
        Assert.Equal("native-original-d3d11", File.ReadAllText(targetD3d11));
        Assert.False(File.Exists(targetDxgi));
    }

    [Fact]
    public async Task PostReplaceException_WhenReadbackIsTemporarilyUnavailable_ClassifiesOutcomeAsUnknown_RetainsEvidenceAndAvoidsBlindCompensation()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        SeedLocalRelease(sourceDir.RootPath, "2.4", "dxvk-2.4-d3d11", "dxvk-2.4-dxgi");

        string exePath = gameDir.CreateFile("Game.exe", "synthetic-game-exe");
        string targetD3d11 = gameDir.CreateFile("d3d11.dll", "native-original-d3d11");
        string targetDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");

        string libraryFile = Path.Combine(storageDir.RootPath, "game-library.json");
        string backupsDir = Path.Combine(storageDir.RootPath, "backups");
        var store = new GameLibraryStore(libraryFile, backupsDir);

        bool postFileReplaceMoved = false;
        FileStream? readbackBlocker = null;

        var engine = new MultiFileTransactionEngine(
            backupsDir,
            new MultiFileTransactionTestHooks
            {
                AfterApply = (_, index) =>
                {
                    if (index == 1)
                    {
                        store.CustomCommitReplace = (tempPath, destPath) =>
                        {
                            // Perform the actual replacement on disk, then lock destPath with FileShare.None
                            // before throwing so synchronous readback cannot inspect game-library.json (Unknown outcome).
                            File.Move(tempPath, destPath, overwrite: true);
                            postFileReplaceMoved = true;
                            readbackBlocker = new FileStream(destPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                            throw new IOException("Simulated post-replace exception while game-library.json readback is temporarily locked.");
                        };
                    }
                }
            });

        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        bool installOk;
        try
        {
            installOk = await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.4", DownloadUrl = "" });
        }
        finally
        {
            readbackBlocker?.Dispose();
            store.CustomCommitReplace = null;
        }

        Assert.True(postFileReplaceMoved, "Expected CustomCommitReplace to move tempPath to destPath before locking readback and throwing.");
        Assert.False(installOk);

        // Because the outcome at catch time was Unknown (neither ProvenNotCommitted nor readable Committed),
        // production code must NOT blindly compensate target files back to native!
        Assert.Equal("dxvk-2.4-d3d11", File.ReadAllText(targetD3d11));
        Assert.Equal("dxvk-2.4-dxgi", File.ReadAllText(targetDxgi));

        var inMemoryInst = store.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(inMemoryInst);
        string transactionsRoot = Path.Combine(backupsDir, ".transactions");

        // Expected-red safe invariant assertions for Unknown persistence outcome:
        // (a) Per-operation .transactions/<txId> evidence (plan.json + undo/) must be retained on Unknown outcome;
        // (b) DxvkInstaller.LastRefusalReason and in-memory installation health must report AttentionRequired;
        // (c) No blind second Save overwrote game-library.json after the lock was released (reconstructed store has the replaced 2.4 record).
        Assert.True(
            Directory.Exists(transactionsRoot) && Directory.GetDirectories(transactionsRoot).Length > 0,
            "Expected per-operation .transactions/<txId> undo evidence to be retained when ownership persistence outcome is Unknown.");
        Assert.False(string.IsNullOrWhiteSpace(installer.LastRefusalReason), "Expected DxvkInstaller.LastRefusalReason to be populated when persistence outcome is Unknown.");
        Assert.Equal(RestorationState.AttentionRequired, inMemoryInst!.RestorationState);
        Assert.False(string.IsNullOrWhiteSpace(inMemoryInst.LastRefusalReason));

        var reconstructedStore = new GameLibraryStore(libraryFile, backupsDir);
        var reconstructedInst = reconstructedStore.FindByInstallationPath(gameDir.RootPath);
        Assert.NotNull(reconstructedInst);
        Assert.Equal("2.4", reconstructedInst!.ManagedDxvkVersion);
    }

    [Fact]
    public async Task Characterization_PostReplaceException_WhenCommitReplaceMovesFileThenThrows_MetadataIsAlreadyDurableOnDisk()
    {
        using var gameDir = new SyntheticTestDirectory();
        using var storageDir = new SyntheticTestDirectory();
        using var sourceDir = new SyntheticTestDirectory();

        SeedLocalRelease(sourceDir.RootPath, "2.4", "dxvk-2.4-d3d11", "dxvk-2.4-dxgi");

        string exePath = gameDir.CreateFile("Game.exe", "synthetic-game-exe");
        string targetD3d11 = gameDir.CreateFile("d3d11.dll", "native-original-d3d11");
        string targetDxgi = Path.Combine(gameDir.RootPath, "dxgi.dll");

        string libraryFile = Path.Combine(storageDir.RootPath, "game-library.json");
        string backupsDir = Path.Combine(storageDir.RootPath, "backups");
        var store = new GameLibraryStore(libraryFile, backupsDir);

        bool postFileReplaceMoved = false;

        var engine = new MultiFileTransactionEngine(
            backupsDir,
            new MultiFileTransactionTestHooks
            {
                AfterApply = (_, index) =>
                {
                    if (index == 1)
                    {
                        store.CustomCommitReplace = (tempPath, destPath) =>
                        {
                            // Perform the actual atomic replacement on disk first, then throw afterward while destPath remains readable.
                            File.Move(tempPath, destPath, overwrite: true);
                            postFileReplaceMoved = true;
                            throw new IOException("Simulated exception thrown after File.Move replacement already succeeded.");
                        };
                    }
                }
            });

        using var http = new HttpClient();
        var installer = new DxvkInstaller(http, engine, store, sourceDir.RootPath);
        var profile = new GameProfile(exePath)
        {
            Api = GraphicsApi.DX11,
            Architecture = "x64"
        };

        // CHARACTERIZATION (current main behavior): ApplyToGameAsync catches the post-replace exception and returns false,
        // even though both target files and game-library.json were already replaced on disk and are readable.
        // NOTE FOR FOLLOW-UP IMPLEMENTATION ISSUE: Under the approved design, when readback positively verifies that
        // expectedPostWriteBytes are present on disk (PersistenceOutcome.Committed), ApplyToGameAsync will treat the
        // operation as committed and return true; the follow-up implementation contract is authorized to transition
        // Assert.False(installOk) -> Assert.True(installOk) (or add a post-fix regression assertion alongside this characterization).
        bool installOk = await installer.ApplyToGameAsync(profile, new ReleaseInfo { Version = "2.4", DownloadUrl = "" });
        store.CustomCommitReplace = null;

        Assert.True(postFileReplaceMoved, "Expected CustomCommitReplace to complete File.Move before throwing.");
        Assert.False(installOk);

        // CHARACTERIZATION: Target files on disk have the deployed Version 2.4 DXVK bytes.
        Assert.Equal("dxvk-2.4-d3d11", File.ReadAllText(targetD3d11));
        Assert.Equal("dxvk-2.4-dxgi", File.ReadAllText(targetDxgi));

        // CHARACTERIZATION: Reconstructing GameLibraryStore from disk proves the updated ownership record
        // IS already on disk (RestorationState.Managed, ManagedDxvkVersion == "2.4", ExpectedManagedSha256 populated).
        var reconstructedStore = new GameLibraryStore(libraryFile, backupsDir);
        var reconstructedInst = reconstructedStore.FindByInstallationPath(gameDir.RootPath);

        Assert.NotNull(reconstructedInst);
        Assert.Equal(RestorationState.Managed, reconstructedInst!.RestorationState);
        Assert.Equal("2.4", reconstructedInst.ManagedDxvkVersion);
        Assert.Equal(2, reconstructedInst.ManagedFiles.Count);
        Assert.All(reconstructedInst.ManagedFiles, mf => Assert.Equal("2.4", mf.ManagedDxvkVersion));
        Assert.Equal(
            FileIdentity.Capture(targetD3d11).Sha256,
            reconstructedInst.FindManagedFile("d3d11.dll")!.ExpectedManagedSha256);
        Assert.Equal(
            FileIdentity.Capture(targetDxgi).Sha256,
            reconstructedInst.FindManagedFile("dxgi.dll")!.ExpectedManagedSha256);
    }
}
