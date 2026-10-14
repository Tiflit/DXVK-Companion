using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class GameLibraryStorePersistenceTests
    {
        [Fact]
        public void GameLibraryStore_FirstSaveAndReplacementSave_LeavesPreExistingFixedTempFileIntact()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            // Seed pre-existing fixed .tmp file with sentinel bytes
            string sentinelTmpFile = libraryFile + ".tmp";
            byte[] sentinelBytes = Encoding.UTF8.GetBytes("PREEXISTING_FIXED_TEMP_SENTINEL_CONTENT_12345");
            File.WriteAllBytes(sentinelTmpFile, sentinelBytes);

            // Seed unrelated .tmp.* and .recovery.* files
            string unrelatedTmpFile = libraryFile + ".tmp.unrelated_external_operation";
            byte[] unrelatedTmpBytes = Encoding.UTF8.GetBytes("UNRELATED_TEMP_FILE_CONTENT");
            File.WriteAllBytes(unrelatedTmpFile, unrelatedTmpBytes);

            string recoveryFile = libraryFile + ".recovery.20261014-123456789.json";
            byte[] recoveryBytes = Encoding.UTF8.GetBytes("PRESERVED_RECOVERY_CONTENT");
            File.WriteAllBytes(recoveryFile, recoveryBytes);

            var store = new GameLibraryStore(libraryFile, backupsDir);

            // 1. First save: adds an installation and commits to live file
            var installation = store.GetOrCreateInstallation(@"C:\Games\TestGame", "Test Game");
            Assert.True(File.Exists(libraryFile), "Library file must be created on first save.");

            // Sentinel file and unrelated files must be byte-for-byte intact after first save
            Assert.True(File.Exists(sentinelTmpFile));
            Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelTmpFile));
            Assert.True(File.Exists(unrelatedTmpFile));
            Assert.Equal(unrelatedTmpBytes, File.ReadAllBytes(unrelatedTmpFile));
            Assert.True(File.Exists(recoveryFile));
            Assert.Equal(recoveryBytes, File.ReadAllBytes(recoveryFile));

            // 2. Replacement save: mutate installation and save
            installation.DisplayName = "Updated Test Game Name";
            store.Save(installation);

            // Sentinel file and unrelated files must remain byte-for-byte intact after replacement save
            Assert.True(File.Exists(sentinelTmpFile));
            Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelTmpFile));
            Assert.True(File.Exists(unrelatedTmpFile));
            Assert.Equal(unrelatedTmpBytes, File.ReadAllBytes(unrelatedTmpFile));
            Assert.True(File.Exists(recoveryFile));
            Assert.Equal(recoveryBytes, File.ReadAllBytes(recoveryFile));

            // Verify reloaded library reflects the saved changes
            var reloaded = new GameLibraryStore(libraryFile, backupsDir);
            var reloadedInstallation = reloaded.FindByInstallationPath(@"C:\Games\TestGame");
            Assert.NotNull(reloadedInstallation);
            Assert.Equal("Updated Test Game Name", reloadedInstallation.DisplayName);
        }

        [Fact]
        public void GameLibraryStore_FirstSaveAndReplacementSave_RoundTripsRepresentativeDataFieldsSuccessfully()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            var store = new GameLibraryStore(libraryFile, backupsDir);

            var installation = store.GetOrCreateInstallation(@"C:\Games\RPGGame", "RPG Game");
            installation.LastSeenUtc = new DateTime(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc);
            installation.ManagementPolicy = ManagementPolicy.Pinned("2.3.1");
            installation.Configuration = new DxvkConfiguration
            {
                HudEnabled = true,
                FrameLimit = 120,
                FrameLimitEnabled = true
            };

            var exe = installation.GetOrAddExecutable("bin/game.exe", "Main Game Executable");
            exe.LastKnownApi = GraphicsApi.DX11;
            exe.ApiConfidence = ApiDetectionConfidence.High;
            exe.LastKnownArchitecture = "x64";
            exe.DetectionEvidence = new List<string> { "d3d11.dll import detected", "PE64 machine flag" };
            exe.LastSeenUtc = new DateTime(2026, 10, 14, 18, 5, 0, DateTimeKind.Utc);

            var managedFile = installation.GetOrAddManagedFile("bin/d3d11.dll");
            managedFile.OriginalState = FileOriginalState.ExistedAtInstall;
            managedFile.BackupRelativePath = "backups/d3d11.dll.bak";
            managedFile.OriginalSha256 = "ORIGINAL_HASH_123456";
            managedFile.ExpectedManagedSha256 = "MANAGED_HASH_789012";
            managedFile.ManagedDxvkVersion = "2.3.1";
            managedFile.CurrentState = ManagedFileState.Managed;
            managedFile.LastVerifiedUtc = new DateTime(2026, 10, 14, 18, 10, 0, DateTimeKind.Utc);

            // First save
            store.Save(installation);

            // Reload via fresh store instance to verify full round-trip persistence
            var reloadedStore = new GameLibraryStore(libraryFile, backupsDir);
            var loaded = reloadedStore.FindByInstallationPath(@"C:\Games\RPGGame");

            Assert.NotNull(loaded);
            Assert.Equal("RPG Game", loaded.DisplayName);
            Assert.Equal(new DateTime(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc), loaded.LastSeenUtc);

            // Verify ManagementPolicy
            Assert.NotNull(loaded.ManagementPolicy);
            Assert.Equal(InstallationPolicyType.Pinned, loaded.ManagementPolicy.PolicyType);
            Assert.Equal("2.3.1", loaded.ManagementPolicy.PinnedVersion);

            // Verify DxvkConfiguration
            Assert.NotNull(loaded.Configuration);
            Assert.True(loaded.Configuration.HudEnabled);
            Assert.Equal(120, loaded.Configuration.FrameLimit);
            Assert.True(loaded.Configuration.FrameLimitEnabled);

            // Verify ExecutableProfile
            var loadedExe = loaded.FindExecutable("bin/game.exe");
            Assert.NotNull(loadedExe);
            Assert.Equal("Main Game Executable", loadedExe.DisplayName);
            Assert.Equal(GraphicsApi.DX11, loadedExe.LastKnownApi);
            Assert.Equal(ApiDetectionConfidence.High, loadedExe.ApiConfidence);
            Assert.Equal("x64", loadedExe.LastKnownArchitecture);
            Assert.Equal(2, loadedExe.DetectionEvidence.Count);
            Assert.Contains("d3d11.dll import detected", loadedExe.DetectionEvidence);
            Assert.Equal(new DateTime(2026, 10, 14, 18, 5, 0, DateTimeKind.Utc), loadedExe.LastSeenUtc);

            // Verify ManagedFileRecord
            var loadedFile = loaded.FindManagedFile("bin/d3d11.dll");
            Assert.NotNull(loadedFile);
            Assert.Equal(FileOriginalState.ExistedAtInstall, loadedFile.OriginalState);
            Assert.Equal("backups/d3d11.dll.bak", loadedFile.BackupRelativePath);
            Assert.Equal("ORIGINAL_HASH_123456", loadedFile.OriginalSha256);
            Assert.Equal("MANAGED_HASH_789012", loadedFile.ExpectedManagedSha256);
            Assert.Equal("2.3.1", loadedFile.ManagedDxvkVersion);
            Assert.Equal(ManagedFileState.Managed, loadedFile.CurrentState);
            Assert.Equal(new DateTime(2026, 10, 14, 18, 10, 0, DateTimeKind.Utc), loadedFile.LastVerifiedUtc);

            // Replacement save with mutated configuration
            loaded.Configuration.FrameLimit = 144;
            loaded.Configuration.HudEnabled = false;
            reloadedStore.Save(loaded);

            var reloadedStore2 = new GameLibraryStore(libraryFile, backupsDir);
            var loaded2 = reloadedStore2.FindByInstallationPath(@"C:\Games\RPGGame");
            Assert.NotNull(loaded2);
            Assert.Equal(144, loaded2.Configuration.FrameLimit);
            Assert.False(loaded2.Configuration.HudEnabled);
        }

        [Fact]
        public void GameLibraryStore_StagedWrite_WritesToOperationUniqueTempFileInSameDirectory_BeforeCommitReachesTarget()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            var store = new GameLibraryStore(libraryFile, backupsDir);
            var inst = store.GetOrCreateInstallation(@"C:\Games\Test", "Initial Name");

            string originalTargetContent = File.ReadAllText(libraryFile);
            string? capturedTempPath = null;
            string? capturedCommitSource = null;
            string? capturedCommitDest = null;
            bool targetWasUnchangedDuringTempWrite = false;
            bool tempFileExistedDuringCommit = false;

            store.CustomTempWriter = (tempPath, json) =>
            {
                capturedTempPath = tempPath;
                targetWasUnchangedDuringTempWrite = (File.ReadAllText(libraryFile) == originalTargetContent);
                File.WriteAllText(tempPath, json);
            };

            store.CustomCommitReplace = (tempPath, targetPath) =>
            {
                capturedCommitSource = tempPath;
                capturedCommitDest = targetPath;
                tempFileExistedDuringCommit = File.Exists(tempPath);
                File.Move(tempPath, targetPath, overwrite: true);
            };

            inst.DisplayName = "Updated Name";
            store.Save(inst);

            Assert.NotNull(capturedTempPath);
            Assert.NotEqual(libraryFile, capturedTempPath);
            Assert.NotEqual(libraryFile + ".tmp", capturedTempPath);
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(libraryFile)), Path.GetDirectoryName(Path.GetFullPath(capturedTempPath)));
            Assert.Contains(".tmp.", capturedTempPath);
            Assert.True(targetWasUnchangedDuringTempWrite, "Live target must remain unchanged during staging write.");
            Assert.True(tempFileExistedDuringCommit, "Temporary file must contain complete serialized content when commit is invoked.");
            Assert.Equal(capturedTempPath, capturedCommitSource);
            Assert.Equal(libraryFile, capturedCommitDest);
            Assert.False(File.Exists(capturedTempPath), "Operation-owned temporary file must be consumed or cleaned after commit.");

            var reloaded = new GameLibraryStore(libraryFile, backupsDir);
            Assert.Equal("Updated Name", reloaded.FindByInstallationPath(@"C:\Games\Test")?.DisplayName);
        }

        [Fact]
        public void GameLibraryStore_PartialWriteFailure_LeavesLiveTargetByteForByteUnchanged_AndPreservesSentinelFiles()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            var store = new GameLibraryStore(libraryFile, backupsDir);
            var initial = store.GetOrCreateInstallation(@"C:\Games\Stable", "Stable Game");

            byte[] originalTargetBytes = File.ReadAllBytes(libraryFile);

            // Seed sentinel file
            string sentinelFile = libraryFile + ".tmp";
            byte[] sentinelBytes = Encoding.UTF8.GetBytes("SENTINEL_TEMP_BYTES");
            File.WriteAllBytes(sentinelFile, sentinelBytes);

            string? observedTempPath = null;

            // Simulate partial write failure during staging: write partial bytes then throw
            store.CustomTempWriter = (tempPath, json) =>
            {
                observedTempPath = tempPath;
                File.WriteAllText(tempPath, json.Substring(0, Math.Min(40, json.Length)));
                throw new IOException("Simulated disk full or write abort during staging.");
            };

            var second = new GameInstallation { InstallationPath = @"C:\Games\Second", DisplayName = "Second Game" };
            var ex = Assert.Throws<IOException>(() => store.Save(second));
            Assert.Contains("Simulated disk full", ex.Message);

            // Target file must remain 100% byte-for-byte identical to before the failed save
            byte[] afterFailureBytes = File.ReadAllBytes(libraryFile);
            Assert.Equal(originalTargetBytes, afterFailureBytes);

            // Sentinel file must remain 100% byte-for-byte intact
            Assert.True(File.Exists(sentinelFile));
            Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelFile));

            // Operation-owned temporary file must have been deleted by cleanup
            Assert.NotNull(observedTempPath);
            Assert.False(File.Exists(observedTempPath), "Operation-owned temporary file must be deleted on staging failure.");

            // Target file remains cleanly reloadable
            var reloaded = new GameLibraryStore(libraryFile, backupsDir);
            Assert.Single(reloaded.GetAll());
            Assert.Equal("Stable Game", reloaded.FindByInstallationPath(@"C:\Games\Stable")?.DisplayName);
        }

        [Fact]
        public void GameLibraryStore_FirstSave_PartialWriteFailure_LeavesNoLiveTarget_AndPreservesSentinelFiles()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");
            Assert.False(File.Exists(libraryFile));

            // Seed sentinel file
            string sentinelFile = libraryFile + ".tmp";
            byte[] sentinelBytes = Encoding.UTF8.GetBytes("SENTINEL_FIRST_SAVE_TEMP");
            File.WriteAllBytes(sentinelFile, sentinelBytes);

            var store = new GameLibraryStore(libraryFile, backupsDir);
            string? observedTemp = null;

            store.CustomTempWriter = (tempPath, json) =>
            {
                observedTemp = tempPath;
                File.WriteAllText(tempPath, "{\"Installations\": [{\"truncated\":");
                throw new IOException("Simulated failure on first library save.");
            };

            var inst = new GameInstallation { InstallationPath = @"C:\Games\First", DisplayName = "First Game" };
            var ex = Assert.Throws<IOException>(() => store.Save(inst));
            Assert.Contains("Simulated failure on first library save", ex.Message);

            // First-save staging failure must leave no live target file
            Assert.False(File.Exists(libraryFile), "No live target file should exist after first-save staging failure.");

            // Sentinel file must remain intact
            Assert.True(File.Exists(sentinelFile));
            Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelFile));

            // Temp file must be cleaned up
            Assert.NotNull(observedTemp);
            Assert.False(File.Exists(observedTemp), "Operation-owned temp file must be deleted after first-save staging failure.");
        }

        [Fact]
        public void GameLibraryStore_FailureBeforeReplacementCompletes_PropagatesException_LeavesTargetIntact_AndCleansTempFile()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            var store = new GameLibraryStore(libraryFile, backupsDir);
            var inst = store.GetOrCreateInstallation(@"C:\Games\Existing", "Existing Game");

            byte[] originalBytes = File.ReadAllBytes(libraryFile);

            string sentinelFile = libraryFile + ".tmp";
            byte[] sentinelBytes = Encoding.UTF8.GetBytes("SENTINEL_BEFORE_COMMIT");
            File.WriteAllBytes(sentinelFile, sentinelBytes);

            string? capturedTempPath = null;

            store.CustomCommitReplace = (tempPath, targetPath) =>
            {
                capturedTempPath = tempPath;
                throw new IOException("Simulated filesystem error during commit replacement.");
            };

            inst.DisplayName = "Mutated Name";
            var ex = Assert.Throws<IOException>(() => store.Save(inst));
            Assert.Contains("Simulated filesystem error during commit replacement", ex.Message);

            // Previous live target remains byte-for-byte intact
            Assert.Equal(originalBytes, File.ReadAllBytes(libraryFile));

            // Sentinel file remains intact
            Assert.True(File.Exists(sentinelFile));
            Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinelFile));

            // Operation-owned temp file must be cleaned up
            Assert.NotNull(capturedTempPath);
            Assert.False(File.Exists(capturedTempPath), "Operation-owned temp file must be deleted when commit replacement fails.");
        }

        [Fact]
        public void GameLibraryStore_CleanupFailure_DoesNotMaskPrimaryWriteFailure()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            var store = new GameLibraryStore(libraryFile, backupsDir);
            var inst = new GameInstallation { InstallationPath = @"C:\Games\Test", DisplayName = "Test" };

            store.CustomTempWriter = (tempPath, json) =>
            {
                throw new IOException("Primary staging write error.");
            };

            store.CustomTempCleaner = (tempPath) =>
            {
                throw new UnauthorizedAccessException("Secondary cleanup error.");
            };

            var ex = Assert.Throws<IOException>(() => store.Save(inst));
            Assert.Equal("Primary staging write error.", ex.Message);
        }

        [Fact]
        public void GameLibraryStore_CleanupFailure_DoesNotMaskPrimaryCommitFailure()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            var store = new GameLibraryStore(libraryFile, backupsDir);
            var inst = new GameInstallation { InstallationPath = @"C:\Games\Test", DisplayName = "Test" };

            store.CustomCommitReplace = (tempPath, targetPath) =>
            {
                throw new IOException("Primary commit replace error.");
            };

            store.CustomTempCleaner = (tempPath) =>
            {
                throw new UnauthorizedAccessException("Secondary cleanup error.");
            };

            var ex = Assert.Throws<IOException>(() => store.Save(inst));
            Assert.Equal("Primary commit replace error.", ex.Message);
        }

        [Fact]
        public void GameLibraryStore_MultipleOperations_GenerateDistinctTempPaths()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            var store = new GameLibraryStore(libraryFile, backupsDir);
            var seenTempPaths = new List<string>();

            store.CustomTempWriter = (tempPath, json) =>
            {
                seenTempPaths.Add(tempPath);
                File.WriteAllText(tempPath, json);
            };

            var i1 = new GameInstallation { InstallationPath = @"C:\Games\Game1", DisplayName = "Game 1" };
            store.Save(i1);

            var i2 = new GameInstallation { InstallationPath = @"C:\Games\Game2", DisplayName = "Game 2" };
            store.Save(i2);

            var i3 = new GameInstallation { InstallationPath = @"C:\Games\Game3", DisplayName = "Game 3" };
            store.Save(i3);

            Assert.Equal(3, seenTempPaths.Count);
            Assert.Equal(3, seenTempPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void GameLibraryStore_PreservesLockingAndSignatures()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            var store = new GameLibraryStore(libraryFile, backupsDir);

            // GetOrCreateInstallation creates and persists
            var inst1 = store.GetOrCreateInstallation(@"C:\Games\GameA", "Game A");
            Assert.NotNull(inst1);
            Assert.Equal(@"C:\Games\GameA", inst1.InstallationPath);

            // FindByInstallationPath queries in memory
            var found = store.FindByInstallationPath(@"c:\games\gamea");
            Assert.Same(inst1, found);

            // FindInstallationForExecutable finds executable inside installation root
            var exe = inst1.GetOrAddExecutable(@"bin\game.exe", "Game Executable");
            store.Save(inst1);

            var foundByExe = store.FindInstallationForExecutable(@"C:\Games\GameA\bin\game.exe");
            Assert.Same(inst1, foundByExe);

            // GetAll returns collection
            var all = store.GetAll();
            Assert.Single(all);

            // SaveAll persists all installations
            store.SaveAll();
            Assert.True(File.Exists(libraryFile));
        }
    }
}
