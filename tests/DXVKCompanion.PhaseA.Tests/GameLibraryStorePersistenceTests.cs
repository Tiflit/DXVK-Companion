using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Safety;
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
            installation.ManagementPolicy = ManagementPolicy.PinVersion("2.3.1");
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
            managedFile.OriginalState = FileOriginalState.Existing;
            managedFile.BackupRelativePath = "backups/d3d11.dll.bak";
            managedFile.OriginalSha256 = "ORIGINAL_HASH_123456";
            managedFile.ExpectedManagedSha256 = "MANAGED_HASH_789012";
            managedFile.ManagedDxvkVersion = "2.3.1";
            managedFile.CurrentState = ManagedFileState.Consistent;
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
            Assert.Equal(ManagementMode.PinnedVersion, loaded.ManagementPolicy.Mode);
            Assert.Equal("2.3.1", loaded.ManagementPolicy.PinnedDxvkVersion);

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
            Assert.Equal(FileOriginalState.Existing, loadedFile.OriginalState);
            Assert.Equal("backups/d3d11.dll.bak", loadedFile.BackupRelativePath);
            Assert.Equal("ORIGINAL_HASH_123456", loadedFile.OriginalSha256);
            Assert.Equal("MANAGED_HASH_789012", loadedFile.ExpectedManagedSha256);
            Assert.Equal("2.3.1", loadedFile.ManagedDxvkVersion);
            Assert.Equal(ManagedFileState.Consistent, loadedFile.CurrentState);
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

        [Fact]
        public void GameLibraryStore_InitialNewerSchemaLoad_MarksRefused_PreservesBytes_DoesNotCreateDirectoriesOrFiles()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string absentBackupsDir = Path.Combine(testDir.RootPath, "absent-backups");

            string newerJson = @"{
  ""SchemaVersion"": 999,
  ""Installations"": [
    {
      ""InstallationPath"": ""C:\\Games\\FutureGame"",
      ""DisplayName"": ""Future Game""
    }
  ]
}";
            File.WriteAllText(libraryFile, newerJson);
            byte[] expectedBytes = File.ReadAllBytes(libraryFile);

            // Sentinel sibling files
            string sentinelTmpFile = libraryFile + ".tmp";
            byte[] sentinelTmpBytes = Encoding.UTF8.GetBytes("SENTINEL_TMP");
            File.WriteAllBytes(sentinelTmpFile, sentinelTmpBytes);

            string unrelatedTmpFile = libraryFile + ".tmp.unrelated_external";
            byte[] unrelatedTmpBytes = Encoding.UTF8.GetBytes("UNRELATED_TMP");
            File.WriteAllBytes(unrelatedTmpFile, unrelatedTmpBytes);

            string sentinelRecoveryFile = libraryFile + ".recovery.sentinel.json";
            byte[] sentinelRecoveryBytes = Encoding.UTF8.GetBytes("SENTINEL_RECOVERY");
            File.WriteAllBytes(sentinelRecoveryFile, sentinelRecoveryBytes);

            int tempWriterCalls = 0;
            int commitReplaceCalls = 0;
            int tempCleanerCalls = 0;

            var store = new GameLibraryStore(libraryFile, absentBackupsDir)
            {
                CustomTempWriter = (_, _) => tempWriterCalls++,
                CustomCommitReplace = (_, _) => commitReplaceCalls++,
                CustomTempCleaner = _ => tempCleanerCalls++
            };

            // Refusal state
            Assert.True(store.IsRefused);
            Assert.Equal("999", store.RefusedSchemaVersionText);
            Assert.Empty(store.GetAll());
            Assert.Null(store.FindByInstallationPath(@"C:\Games\FutureGame"));

            // Absent backups directory must NOT be created on refusal
            Assert.False(Directory.Exists(absentBackupsDir), "Absent backups directory must not be created on refusal");

            // Library file remains byte-for-byte intact
            Assert.Equal(expectedBytes, File.ReadAllBytes(libraryFile));

            // Sentinel siblings remain byte-for-byte intact
            Assert.Equal(sentinelTmpBytes, File.ReadAllBytes(sentinelTmpFile));
            Assert.Equal(unrelatedTmpBytes, File.ReadAllBytes(unrelatedTmpFile));
            Assert.Equal(sentinelRecoveryBytes, File.ReadAllBytes(sentinelRecoveryFile));

            // No recovery files created
            var recoveryFiles = Directory.GetFiles(testDir.RootPath, "game-library.json.recovery.*.json");
            Assert.Single(recoveryFiles); // only our sentinel

            // Zero I/O seam invocations
            Assert.Equal(0, tempWriterCalls);
            Assert.Equal(0, commitReplaceCalls);
            Assert.Equal(0, tempCleanerCalls);
        }

        [Fact]
        public void GameLibraryStore_InitiallyRefused_AllFourMutationEntryPoints_ThrowIncompatibleLibrarySchemaException_AndMakeNoChanges()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            string newerJson = @"{ ""SchemaVersion"": 2, ""Installations"": [] }";
            File.WriteAllText(libraryFile, newerJson);
            byte[] expectedBytes = File.ReadAllBytes(libraryFile);

            int writerCalls = 0;
            var store = new GameLibraryStore(libraryFile, backupsDir)
            {
                CustomTempWriter = (_, _) => writerCalls++
            };

            Assert.True(store.IsRefused);

            // Entry point 1: GetOrCreateInstallation
            var ex1 = Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.GetOrCreateInstallation(@"C:\Games\BlockedGame", "Blocked Game"));
            Assert.Equal("2", ex1.FoundSchemaVersionText);
            Assert.Equal(GameLibrary.CurrentSchemaVersion, ex1.SupportedSchemaVersion);
            Assert.Empty(store.GetAll());

            // Entry point 2: Save
            var testInstallation = new GameInstallation
            {
                InstallationPath = @"C:\Games\BlockedGame",
                DisplayName = "Blocked Game"
            };
            var ex2 = Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.Save(testInstallation));
            Assert.Equal("2", ex2.FoundSchemaVersionText);
            Assert.Empty(store.GetAll());

            // Entry point 3: SaveAll
            var ex3 = Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.SaveAll());
            Assert.Equal("2", ex3.FoundSchemaVersionText);

            // Entry point 4: RecordDetectionSnapshot
            var snapshot = new DetectionSnapshot
            {
                InstallationRoot = @"C:\Games\BlockedGame",
                ExecutableRelativePath = "bin/game.exe",
                ProcessName = "BlockedGame",
                Classification = new ApiClassificationResult
                {
                    PrimaryApi = GraphicsApi.DX11,
                    Confidence = ApiDetectionConfidence.High,
                    Architecture = "x64"
                },
                TimestampUtc = DateTime.UtcNow
            };
            var ex4 = Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.RecordDetectionSnapshot(snapshot));
            Assert.Equal("2", ex4.FoundSchemaVersionText);
            Assert.False(snapshot.HasApiChanged);
            Assert.Empty(store.GetAll());

            // Zero write seam calls and target byte-for-byte intact
            Assert.Equal(0, writerCalls);
            Assert.Equal(expectedBytes, File.ReadAllBytes(libraryFile));
        }

        [Fact]
        public void GameLibraryStore_NewerDataAppearingAfterConstruction_TransitionsToRefused_GuardsAllMutations()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            // Initial supported store
            var store = new GameLibraryStore(libraryFile, backupsDir);
            Assert.False(store.IsRefused);

            // External process writes newer schema file to disk
            string newerContent = @"{ ""SchemaVersion"": 7, ""Installations"": [] }";
            File.WriteAllText(libraryFile, newerContent);
            byte[] expectedBytes = File.ReadAllBytes(libraryFile);

            // Subsequent mutation attempts must detect on-disk newer schema, refuse, and throw
            var ex = Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.GetOrCreateInstallation(@"C:\Games\ExternalRaceGame", "External Race"));

            Assert.True(store.IsRefused);
            Assert.Equal("7", store.RefusedSchemaVersionText);
            Assert.Equal("7", ex.FoundSchemaVersionText);

            // Additional mutations continue to be guarded
            Assert.Throws<IncompatibleLibrarySchemaException>(() => store.SaveAll());
            Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.Save(new GameInstallation { InstallationPath = @"C:\Games\ExternalRaceGame" }));

            // Target file remains byte-for-byte unchanged
            Assert.Equal(expectedBytes, File.ReadAllBytes(libraryFile));
        }

        [Fact]
        public void GameLibraryStore_NewerSchemaWithUnknownFutureEnums_PreservedByteForByte_DoesNotCreateRecoveryCopy()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            string futureJsonWithUnknownEnums = @"{
  ""SchemaVersion"": 5,
  ""Installations"": [
    {
      ""InstallationPath"": ""C:\\Games\\SpaceGame"",
      ""DisplayName"": ""Space Game"",
      ""ManagementPolicy"": {
        ""Mode"": ""FuturisticAutonomousMode999""
      },
      ""Executables"": [
        {
          ""RelativeExecutablePath"": ""bin/space.exe"",
          ""LastKnownApi"": ""DirectX25Quantum""
        }
      ]
    }
  ]
}";
            File.WriteAllText(libraryFile, futureJsonWithUnknownEnums);
            byte[] expectedBytes = File.ReadAllBytes(libraryFile);

            var store = new GameLibraryStore(libraryFile, backupsDir);

            Assert.True(store.IsRefused);
            Assert.Equal("5", store.RefusedSchemaVersionText);
            Assert.Empty(store.GetAll());

            // Target file is byte-for-byte identical
            Assert.Equal(expectedBytes, File.ReadAllBytes(libraryFile));

            // No recovery files created in directory
            var recoveryFiles = Directory.GetFiles(testDir.RootPath, "game-library.json.recovery.*.json");
            Assert.Empty(recoveryFiles);
        }

        [Fact]
        public void GameLibraryStore_ExistingBackupDirectoryWithSentinel_RefusedLoadAndMutations_LeavesBackupDirectoryAndContentsUntouched()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            // Create existing backup directory and seed sentinel files
            Directory.CreateDirectory(backupsDir);
            string sentinelBackupFile = Path.Combine(backupsDir, "backup-sentinel.json.bak");
            byte[] sentinelBackupBytes = Encoding.UTF8.GetBytes("PREEXISTING_BACKUP_SENTINEL_DATA_12345");
            File.WriteAllBytes(sentinelBackupFile, sentinelBackupBytes);

            string unrelatedBackupFile = Path.Combine(backupsDir, "unrelated_archive.zip");
            byte[] unrelatedBackupBytes = Encoding.UTF8.GetBytes("UNRELATED_ZIP_CONTENT_67890");
            File.WriteAllBytes(unrelatedBackupFile, unrelatedBackupBytes);

            // Record initial backup entries
            var initialBackupEntries = Directory.GetFileSystemEntries(backupsDir).OrderBy(x => x).ToArray();

            // Seed newer schema file
            string newerJson = @"{ ""SchemaVersion"": 4, ""Installations"": [] }";
            File.WriteAllText(libraryFile, newerJson);
            byte[] expectedLibraryBytes = File.ReadAllBytes(libraryFile);

            int writerCalls = 0;
            int commitCalls = 0;
            int cleanerCalls = 0;
            var store = new GameLibraryStore(libraryFile, backupsDir)
            {
                CustomTempWriter = (_, _) => writerCalls++,
                CustomCommitReplace = (_, _) => commitCalls++,
                CustomTempCleaner = _ => cleanerCalls++
            };

            Assert.True(store.IsRefused);
            Assert.Empty(store.GetAll());

            // Attempt all 4 mutating entry points
            Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.GetOrCreateInstallation(@"C:\Games\BlockedGame", "Blocked Game"));

            Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.Save(new GameInstallation { InstallationPath = @"C:\Games\BlockedGame" }));

            Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.SaveAll());

            var snapshot = new DetectionSnapshot
            {
                InstallationRoot = @"C:\Games\BlockedGame",
                ExecutableRelativePath = "bin/game.exe",
                ProcessName = "BlockedGame",
                Classification = new ApiClassificationResult
                {
                    PrimaryApi = GraphicsApi.DX11,
                    Confidence = ApiDetectionConfidence.High,
                    Architecture = "x64"
                },
                TimestampUtc = DateTime.UtcNow
            };
            Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.RecordDetectionSnapshot(snapshot));

            // Verify backup directory entries and contents are completely unchanged
            Assert.True(Directory.Exists(backupsDir));
            var finalBackupEntries = Directory.GetFileSystemEntries(backupsDir).OrderBy(x => x).ToArray();
            Assert.Equal(initialBackupEntries, finalBackupEntries);
            Assert.Equal(sentinelBackupBytes, File.ReadAllBytes(sentinelBackupFile));
            Assert.Equal(unrelatedBackupBytes, File.ReadAllBytes(unrelatedBackupFile));

            // Verify library file is byte-for-byte unchanged
            Assert.Equal(expectedLibraryBytes, File.ReadAllBytes(libraryFile));

            // Verify zero writer/commit/cleaner calls and zero in-memory mutations
            Assert.Equal(0, writerCalls);
            Assert.Equal(0, commitCalls);
            Assert.Equal(0, cleanerCalls);
            Assert.Empty(store.GetAll());
        }

        [Fact]
        public void GameLibraryStore_NewerDataAppearingAfterConstruction_GetOrCreateInstallation_FirstDetector_RefusesAndThrowsWithoutMutation()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            int writerCalls = 0;
            int commitCalls = 0;
            int cleanerCalls = 0;
            var store = new GameLibraryStore(libraryFile, backupsDir)
            {
                CustomTempWriter = (_, _) => writerCalls++,
                CustomCommitReplace = (_, _) => commitCalls++,
                CustomTempCleaner = _ => cleanerCalls++
            };
            Assert.False(store.IsRefused);

            // External process writes newer schema file to disk
            string newerContent = @"{ ""SchemaVersion"": 9, ""Installations"": [] }";
            File.WriteAllText(libraryFile, newerContent);
            byte[] expectedBytes = File.ReadAllBytes(libraryFile);

            // GetOrCreateInstallation is the first mutating call on this instance
            var ex = Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.GetOrCreateInstallation(@"C:\Games\ExternalFirstGame", "External First Game"));

            Assert.Equal("9", ex.FoundSchemaVersionText);
            Assert.True(store.IsRefused);
            Assert.Equal("9", store.RefusedSchemaVersionText);
            Assert.Empty(store.GetAll());
            Assert.Equal(0, writerCalls);
            Assert.Equal(0, commitCalls);
            Assert.Equal(0, cleanerCalls);
            Assert.Equal(expectedBytes, File.ReadAllBytes(libraryFile));
        }

        [Fact]
        public void GameLibraryStore_NewerDataAppearingAfterConstruction_Save_FirstDetector_RefusesAndThrowsWithoutMutation()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            int writerCalls = 0;
            int commitCalls = 0;
            int cleanerCalls = 0;
            var store = new GameLibraryStore(libraryFile, backupsDir)
            {
                CustomTempWriter = (_, _) => writerCalls++,
                CustomCommitReplace = (_, _) => commitCalls++,
                CustomTempCleaner = _ => cleanerCalls++
            };
            Assert.False(store.IsRefused);

            // External process writes newer schema file to disk
            string newerContent = @"{ ""SchemaVersion"": 9, ""Installations"": [] }";
            File.WriteAllText(libraryFile, newerContent);
            byte[] expectedBytes = File.ReadAllBytes(libraryFile);

            // Save is the first mutating call on this instance
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\ExternalFirstGame",
                DisplayName = "External First Game"
            };
            var ex = Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.Save(installation));

            Assert.Equal("9", ex.FoundSchemaVersionText);
            Assert.True(store.IsRefused);
            Assert.Equal("9", store.RefusedSchemaVersionText);
            Assert.Empty(store.GetAll());
            Assert.Equal(0, writerCalls);
            Assert.Equal(0, commitCalls);
            Assert.Equal(0, cleanerCalls);
            Assert.Equal(expectedBytes, File.ReadAllBytes(libraryFile));
        }

        [Fact]
        public void GameLibraryStore_NewerDataAppearingAfterConstruction_SaveAll_FirstDetector_RefusesAndThrowsWithoutMutation()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            int writerCalls = 0;
            int commitCalls = 0;
            int cleanerCalls = 0;
            var store = new GameLibraryStore(libraryFile, backupsDir)
            {
                CustomTempWriter = (_, _) => writerCalls++,
                CustomCommitReplace = (_, _) => commitCalls++,
                CustomTempCleaner = _ => cleanerCalls++
            };
            Assert.False(store.IsRefused);

            // External process writes newer schema file to disk
            string newerContent = @"{ ""SchemaVersion"": 9, ""Installations"": [] }";
            File.WriteAllText(libraryFile, newerContent);
            byte[] expectedBytes = File.ReadAllBytes(libraryFile);

            // SaveAll is the first mutating call on this instance
            var ex = Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.SaveAll());

            Assert.Equal("9", ex.FoundSchemaVersionText);
            Assert.True(store.IsRefused);
            Assert.Equal("9", store.RefusedSchemaVersionText);
            Assert.Empty(store.GetAll());
            Assert.Equal(0, writerCalls);
            Assert.Equal(0, commitCalls);
            Assert.Equal(0, cleanerCalls);
            Assert.Equal(expectedBytes, File.ReadAllBytes(libraryFile));
        }

        [Fact]
        public void GameLibraryStore_NewerDataAppearingAfterConstruction_RecordDetectionSnapshot_FirstDetector_RefusesAndThrowsWithoutMutation()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            int writerCalls = 0;
            int commitCalls = 0;
            int cleanerCalls = 0;
            var store = new GameLibraryStore(libraryFile, backupsDir)
            {
                CustomTempWriter = (_, _) => writerCalls++,
                CustomCommitReplace = (_, _) => commitCalls++,
                CustomTempCleaner = _ => cleanerCalls++
            };
            Assert.False(store.IsRefused);

            // External process writes newer schema file to disk
            string newerContent = @"{ ""SchemaVersion"": 9, ""Installations"": [] }";
            File.WriteAllText(libraryFile, newerContent);
            byte[] expectedBytes = File.ReadAllBytes(libraryFile);

            // RecordDetectionSnapshot is the first mutating call on this instance
            var snapshot = new DetectionSnapshot
            {
                InstallationRoot = @"C:\Games\ExternalFirstGame",
                ExecutableRelativePath = "bin/game.exe",
                ProcessName = "ExternalFirstGame",
                Classification = new ApiClassificationResult
                {
                    PrimaryApi = GraphicsApi.DX11,
                    Confidence = ApiDetectionConfidence.High,
                    Architecture = "x64"
                },
                TimestampUtc = DateTime.UtcNow
            };
            var ex = Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.RecordDetectionSnapshot(snapshot));

            Assert.Equal("9", ex.FoundSchemaVersionText);
            Assert.False(snapshot.HasApiChanged);
            Assert.True(store.IsRefused);
            Assert.Equal("9", store.RefusedSchemaVersionText);
            Assert.Empty(store.GetAll());
            Assert.Equal(0, writerCalls);
            Assert.Equal(0, commitCalls);
            Assert.Equal(0, cleanerCalls);
            Assert.Equal(expectedBytes, File.ReadAllBytes(libraryFile));
        }

        [Fact]
        public void GameLibraryStore_Utf16BomNewerSchemaWithUnknownEnums_RefusedLoadAndSave_PreservedByteForByte()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            string futureJsonWithUnknownEnums = @"{
  ""SchemaVersion"": 10,
  ""Installations"": [
    {
      ""InstallationPath"": ""C:\\Games\\Utf16StoreGame"",
      ""DisplayName"": ""UTF16 Store Game"",
      ""ManagementPolicy"": {
        ""Mode"": ""FuturisticAutonomousMode777""
      }
    }
  ]
}";
            File.WriteAllText(libraryFile, futureJsonWithUnknownEnums, Encoding.Unicode);
            byte[] expectedBytes = File.ReadAllBytes(libraryFile);

            int writerCalls = 0;
            int commitCalls = 0;
            int cleanerCalls = 0;
            var store = new GameLibraryStore(libraryFile, backupsDir)
            {
                CustomTempWriter = (_, _) => writerCalls++,
                CustomCommitReplace = (_, _) => commitCalls++,
                CustomTempCleaner = _ => cleanerCalls++
            };

            Assert.True(store.IsRefused);
            Assert.Equal("10", store.RefusedSchemaVersionText);
            Assert.Empty(store.GetAll());

            Assert.Throws<IncompatibleLibrarySchemaException>(() => store.SaveAll());
            Assert.Throws<IncompatibleLibrarySchemaException>(() =>
                store.GetOrCreateInstallation(@"C:\Games\BlockedUtf16", "Blocked UTF16"));

            // File on disk remains byte-for-byte identical
            Assert.Equal(expectedBytes, File.ReadAllBytes(libraryFile));

            // No recovery files created
            var recoveryFiles = Directory.GetFiles(testDir.RootPath, "game-library.json.recovery.*.json");
            Assert.Empty(recoveryFiles);

            // Zero write calls
            Assert.Equal(0, writerCalls);
            Assert.Equal(0, commitCalls);
            Assert.Equal(0, cleanerCalls);
        }

        [Fact]
        public void GameLibraryStore_TrailingCommaInJson_IsTreatedAsMalformedAndTriggersRecovery_NotRefused()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            // Trailing comma in JSON: rejected by loader's JsonOptions
            string jsonWithTrailingComma = @"{ ""SchemaVersion"": 2, ""Installations"": [], }";
            File.WriteAllText(libraryFile, jsonWithTrailingComma);

            var store = new GameLibraryStore(libraryFile, backupsDir);

            // Must NOT be refused (preflight treats as Malformed, passing to loader recovery)
            Assert.False(store.IsRefused);
            Assert.Null(store.RefusedSchemaVersionText);

            // Recovery file must be preserved
            var recoveryFiles = Directory.GetFiles(testDir.RootPath, "game-library.json.recovery.*.json");
            Assert.Single(recoveryFiles);

            // Clean-slate in-memory initialization
            Assert.Empty(store.GetAll());
        }

        [Fact]
        public void GameLibraryStore_IndependentStoreLease_BlocksGetOrCreateAndTrySaveHealth()
        {
            using var testDir = new SyntheticTestDirectory();
            using var coordDir = new SyntheticTestDirectory();
            string libraryFile = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsDir = Path.Combine(testDir.RootPath, "backups");

            var coordinator = new OperationLeaseCoordinator(coordDir.RootPath);
            var store = new GameLibraryStore(libraryFile, backupsDir) { LeaseCoordinator = coordinator };

            // 1. Initial setup: create and save an initial installation
            var initial = store.GetOrCreateInstallation(@"C:\Games\GameA", "Game A");
            byte[] baselineBytes = File.ReadAllBytes(libraryFile);

            // 2. An independent coordinator acquires the store lease
            var externalCoordinator = new OperationLeaseCoordinator(coordDir.RootPath);
            using (var externalLease = externalCoordinator.TryAcquireStoreLease(libraryFile, timeout: TimeSpan.FromMilliseconds(500)))
            {
                Assert.NotNull(externalLease);
                Assert.True(externalLease.IsAcquired);

                // With store lease held externally, GetOrCreateInstallation must fail to acquire lease and throw IOException
                var ex = Assert.Throws<IOException>(() =>
                    store.GetOrCreateInstallation(@"C:\Games\GameB", "Game B"));
                Assert.Contains("Cannot acquire store lease", ex.Message);

                // TrySaveHealthAfterFailedCompensation must return false without modifying store
                bool healthSaved = store.TrySaveHealthAfterFailedCompensation(
                    initial,
                    RestorationState.AttentionRequired,
                    "Failed compensation",
                    "Simulated error");
                Assert.False(healthSaved);

                // Disk baseline must remain byte-for-byte identical
                Assert.Equal(baselineBytes, File.ReadAllBytes(libraryFile));
            }

            // 3. After external store lease is released, retry of GetOrCreateInstallation succeeds immediately
            var retry = store.GetOrCreateInstallation(@"C:\Games\GameB", "Game B");
            Assert.NotNull(retry);
            Assert.Equal("Game B", retry.DisplayName);

            // Disk file now updated with Game B
            var reloaded = new GameLibraryStore(libraryFile, backupsDir);
            Assert.NotNull(reloaded.FindByInstallationPath(@"C:\Games\GameB"));
        }
    }
}
