using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class ProfileStorePersistenceTests
    {
        [Fact]
        public void ProfileStore_FirstSave_RoundTripsAllFieldsAndMultipleCaseInsensitiveEntries()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");

            var store = new ProfileStore(profilesFile);

            var profile1 = store.GetOrCreate(@"C:\Games\GameA\game.exe");
            profile1.Api = GraphicsApi.DX11;
            profile1.Architecture = "x64";
            profile1.DxvkEnabled = true;
            profile1.DxvkVersion = "2.3.1";
            profile1.HudEnabled = true;
            profile1.FrameLimit = 144;
            store.Save(profile1);

            var profile2 = store.GetOrCreate(@"C:\Games\GameB\launcher.exe");
            profile2.Api = GraphicsApi.DX9;
            profile2.Architecture = "x86";
            profile2.DxvkEnabled = false;
            profile2.DxvkVersion = null;
            profile2.HudEnabled = false;
            profile2.FrameLimit = 0;
            store.Save(profile2);

            // Windows paths are case-insensitive: query with alternate casing returns existing instance
            var lookupCaseInsensitive = store.GetOrCreate(@"c:\games\gamea\GAME.EXE");
            Assert.Same(profile1, lookupCaseInsensitive);
            Assert.Equal(2, store.GetAll().Count());

            // Target file must exist and be valid indented JSON
            Assert.True(File.Exists(profilesFile));
            string rawJson = File.ReadAllText(profilesFile);
            Assert.Contains("\n", rawJson);

            // Reload via fresh store instance to verify full round-trip
            var reloadedStore = new ProfileStore(profilesFile);
            var reloadedProfiles = reloadedStore.GetAll().ToList();
            Assert.Equal(2, reloadedProfiles.Count);

            var loaded1 = reloadedStore.GetOrCreate(@"C:\Games\GameA\game.exe");
            Assert.Equal(@"C:\Games\GameA\game.exe", loaded1.ExePath);
            Assert.Equal("game.exe", loaded1.ExeName);
            Assert.Equal(GraphicsApi.DX11, loaded1.Api);
            Assert.Equal("x64", loaded1.Architecture);
            Assert.True(loaded1.DxvkEnabled);
            Assert.Equal("2.3.1", loaded1.DxvkVersion);
            Assert.True(loaded1.HudEnabled);
            Assert.Equal(144, loaded1.FrameLimit);

            var loaded2 = reloadedStore.GetOrCreate(@"C:\Games\GameB\launcher.exe");
            Assert.Equal(@"C:\Games\GameB\launcher.exe", loaded2.ExePath);
            Assert.Equal("launcher.exe", loaded2.ExeName);
            Assert.Equal(GraphicsApi.DX9, loaded2.Api);
            Assert.Equal("x86", loaded2.Architecture);
            Assert.False(loaded2.DxvkEnabled);
            Assert.Null(loaded2.DxvkVersion);
            Assert.False(loaded2.HudEnabled);
            Assert.Equal(0, loaded2.FrameLimit);
        }

        [Fact]
        public void ProfileStore_ReplacementSave_RoundTripsUpdatedFieldsSuccessfully()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");

            var store = new ProfileStore(profilesFile);
            var profile = store.GetOrCreate(@"C:\Games\Initial\game.exe");
            profile.Api = GraphicsApi.DX9;
            profile.DxvkEnabled = false;
            store.Save(profile);

            // Mutate and replacement save
            profile.Api = GraphicsApi.DX11;
            profile.DxvkEnabled = true;
            profile.DxvkVersion = "2.4";
            profile.HudEnabled = true;
            profile.FrameLimit = 60;
            store.Save(profile);

            // Reload to verify replacement was committed to live target
            var reloaded = new ProfileStore(profilesFile);
            var loaded = reloaded.GetOrCreate(@"C:\Games\Initial\game.exe");
            Assert.Equal(GraphicsApi.DX11, loaded.Api);
            Assert.True(loaded.DxvkEnabled);
            Assert.Equal("2.4", loaded.DxvkVersion);
            Assert.True(loaded.HudEnabled);
            Assert.Equal(60, loaded.FrameLimit);
        }

        [Fact]
        public void ProfileStore_StagedWrite_WritesToOperationUniqueTempFileInSameDirectory_BeforeCommitReachesTarget()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");

            var store = new ProfileStore(profilesFile);
            var initialProfile = store.GetOrCreate(@"C:\Games\Test\game.exe");
            initialProfile.Api = GraphicsApi.DX9;
            store.Save(initialProfile);

            string originalTargetContent = File.ReadAllText(profilesFile);
            string? capturedTempPath = null;
            string? capturedCommitSource = null;
            string? capturedCommitDest = null;
            bool targetWasUnchangedDuringTempWrite = false;
            bool tempFileExistedDuringCommit = false;

            store.CustomTempWriter = (tempPath, json) =>
            {
                capturedTempPath = tempPath;
                // Live target must still contain original content during staging write
                targetWasUnchangedDuringTempWrite = (File.ReadAllText(profilesFile) == originalTargetContent);
                File.WriteAllText(tempPath, json);
            };

            store.CustomCommitReplace = (tempPath, targetPath) =>
            {
                capturedCommitSource = tempPath;
                capturedCommitDest = targetPath;
                // Temp file must exist and contain the new serialized content prior to commit
                tempFileExistedDuringCommit = File.Exists(tempPath);
                File.Move(tempPath, targetPath, overwrite: true);
            };

            initialProfile.Api = GraphicsApi.DX11;
            store.Save(initialProfile);

            Assert.NotNull(capturedTempPath);
            Assert.NotEqual(profilesFile, capturedTempPath);
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(profilesFile)), Path.GetDirectoryName(Path.GetFullPath(capturedTempPath)));
            Assert.Contains(".tmp.", capturedTempPath);
            Assert.True(targetWasUnchangedDuringTempWrite, "Staging write must not modify the live target.");
            Assert.True(tempFileExistedDuringCommit, "Temporary file must exist when commit is invoked.");
            Assert.Equal(capturedTempPath, capturedCommitSource);
            Assert.Equal(profilesFile, capturedCommitDest);
            Assert.False(File.Exists(capturedTempPath), "Operation-owned temporary file must be consumed or cleaned after commit.");

            // Verified live target now reflects updated content
            var reloaded = new ProfileStore(profilesFile);
            Assert.Equal(GraphicsApi.DX11, reloaded.GetOrCreate(@"C:\Games\Test\game.exe").Api);
        }

        [Fact]
        public void ProfileStore_PartialWriteFailure_DuringStaging_LeavesExistingTargetByteForByteUnchanged_AndReloadable()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");

            var store = new ProfileStore(profilesFile);
            var initial = store.GetOrCreate(@"C:\Games\Existing\game.exe");
            initial.Api = GraphicsApi.DX11;
            initial.DxvkEnabled = true;
            initial.DxvkVersion = "2.3";
            store.Save(initial);

            byte[] originalBytes = File.ReadAllBytes(profilesFile);
            string? observedTempPath = null;

            // Simulate partial write failure during staging: write partial bytes, then throw
            store.CustomTempWriter = (tempPath, json) =>
            {
                observedTempPath = tempPath;
                // Write a partial, truncated fragment (which would corrupt a live file if written directly)
                File.WriteAllText(tempPath, json.Substring(0, Math.Min(30, json.Length)));
                throw new IOException("Simulated disk full or write abort during staging.");
            };

            var newProfile = new GameProfile(@"C:\Games\New\another.exe") { Api = GraphicsApi.Vulkan };
            var ex = Assert.Throws<IOException>(() => store.Save(newProfile));
            Assert.Contains("Simulated disk full", ex.Message);

            // Target file must remain 100% byte-for-byte identical to before the failed save
            byte[] afterFailureBytes = File.ReadAllBytes(profilesFile);
            Assert.Equal(originalBytes, afterFailureBytes);

            // Target file must remain cleanly reloadable without any corruption
            var reloaded = new ProfileStore(profilesFile);
            var reloadedProfiles = reloaded.GetAll().ToList();
            Assert.Single(reloadedProfiles);
            Assert.Equal(@"C:\Games\Existing\game.exe", reloadedProfiles[0].ExePath);
            Assert.Equal(GraphicsApi.DX11, reloadedProfiles[0].Api);

            // Operation-owned temporary file must have been deleted by cleanup
            Assert.NotNull(observedTempPath);
            Assert.False(File.Exists(observedTempPath), "Temporary file from failed staging must be cleaned up.");
        }

        [Fact]
        public void ProfileStore_FirstSave_PartialWriteFailure_LeavesNoLiveTarget()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");
            Assert.False(File.Exists(profilesFile));

            var store = new ProfileStore(profilesFile);
            string? observedTemp = null;

            store.CustomTempWriter = (tempPath, json) =>
            {
                observedTemp = tempPath;
                File.WriteAllText(tempPath, "[\n  {\"truncated\":");
                throw new IOException("Simulated write failure on first save.");
            };

            var profile = new GameProfile(@"C:\Games\First\game.exe") { Api = GraphicsApi.DX9 };
            var ex = Assert.Throws<IOException>(() => store.Save(profile));
            Assert.Contains("Simulated write failure", ex.Message);

            // A first-save staging failure must leave no live target file
            Assert.False(File.Exists(profilesFile), "No live target file should exist after first-save staging failure.");

            // Temp file must be cleaned up
            Assert.NotNull(observedTemp);
            Assert.False(File.Exists(observedTemp), "Temporary file must be cleaned up after first-save staging failure.");
        }

        [Fact]
        public void ProfileStore_FailureBeforeReplacementCompletes_PropagatesException_LeavesTargetIntact_AndCleansTempFile()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");

            var store = new ProfileStore(profilesFile);
            var profile = store.GetOrCreate(@"C:\Games\Stable\game.exe");
            profile.Api = GraphicsApi.DX11;
            store.Save(profile);

            byte[] originalBytes = File.ReadAllBytes(profilesFile);
            string? capturedTempPath = null;

            store.CustomCommitReplace = (tempPath, targetPath) =>
            {
                capturedTempPath = tempPath;
                // Temp file exists with complete serialized data, but commit fails before replacement
                throw new IOException("Simulated filesystem error during commit replacement.");
            };

            profile.Api = GraphicsApi.Vulkan;
            var ex = Assert.Throws<IOException>(() => store.Save(profile));
            Assert.Contains("Simulated filesystem error during commit replacement", ex.Message);

            // Previous live target remains byte-for-byte intact
            byte[] currentBytes = File.ReadAllBytes(profilesFile);
            Assert.Equal(originalBytes, currentBytes);

            // Operation-owned temporary file must be cleaned up
            Assert.NotNull(capturedTempPath);
            Assert.False(File.Exists(capturedTempPath), "Staging temp file must be deleted when commit replacement fails.");
        }

        [Fact]
        public void ProfileStore_CleanupFailure_DoesNotMaskPrimaryWriteFailure()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");

            var store = new ProfileStore(profilesFile);
            var profile = new GameProfile(@"C:\Games\Test\game.exe");

            store.CustomTempWriter = (tempPath, json) =>
            {
                throw new IOException("Primary staging write failure.");
            };

            store.CustomTempCleaner = (tempPath) =>
            {
                throw new UnauthorizedAccessException("Secondary cleanup failure.");
            };

            var ex = Assert.Throws<IOException>(() => store.Save(profile));
            Assert.Equal("Primary staging write failure.", ex.Message);
        }

        [Fact]
        public void ProfileStore_CleanupFailure_DoesNotMaskPrimaryCommitFailure()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");

            var store = new ProfileStore(profilesFile);
            var profile = new GameProfile(@"C:\Games\Test\game.exe");

            store.CustomCommitReplace = (tempPath, targetPath) =>
            {
                throw new IOException("Primary commit replacement failure.");
            };

            store.CustomTempCleaner = (tempPath) =>
            {
                throw new UnauthorizedAccessException("Secondary cleanup failure.");
            };

            var ex = Assert.Throws<IOException>(() => store.Save(profile));
            Assert.Equal("Primary commit replacement failure.", ex.Message);
        }

        [Fact]
        public void ProfileStore_StagedSave_CleansOnlyOwnTempFile_PreservingUnrelatedTempAndRecoveryFiles()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");

            // Seed unrelated files in the same directory
            string unrelatedTemp = Path.Combine(testDir.RootPath, "games.json.tmp.unrelated");
            string unrelatedRecovery = Path.Combine(testDir.RootPath, "games.json.recovery.20261014.json");
            string unrelatedTmpFile = Path.Combine(testDir.RootPath, "other.tmp");

            File.WriteAllText(unrelatedTemp, "unrelated temp content");
            File.WriteAllText(unrelatedRecovery, "unrelated recovery content");
            File.WriteAllText(unrelatedTmpFile, "other tmp content");

            var store = new ProfileStore(profilesFile);

            // 1. Successful save must not touch unrelated files
            var profile = store.GetOrCreate(@"C:\Games\Test\game.exe");
            store.Save(profile);

            Assert.True(File.Exists(unrelatedTemp));
            Assert.Equal("unrelated temp content", File.ReadAllText(unrelatedTemp));
            Assert.True(File.Exists(unrelatedRecovery));
            Assert.Equal("unrelated recovery content", File.ReadAllText(unrelatedRecovery));
            Assert.True(File.Exists(unrelatedTmpFile));
            Assert.Equal("other tmp content", File.ReadAllText(unrelatedTmpFile));

            // 2. Failed save with cleanup must also preserve unrelated files
            store.CustomTempWriter = (tempPath, json) =>
            {
                File.WriteAllText(tempPath, "temporary content");
                throw new IOException("Simulated write abort.");
            };

            Assert.Throws<IOException>(() => store.Save(profile));

            Assert.True(File.Exists(unrelatedTemp));
            Assert.Equal("unrelated temp content", File.ReadAllText(unrelatedTemp));
            Assert.True(File.Exists(unrelatedRecovery));
            Assert.Equal("unrelated recovery content", File.ReadAllText(unrelatedRecovery));
            Assert.True(File.Exists(unrelatedTmpFile));
            Assert.Equal("other tmp content", File.ReadAllText(unrelatedTmpFile));
        }

        [Fact]
        public void ProfileStore_MultipleOperations_GenerateDistinctTempPaths()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");

            var store = new ProfileStore(profilesFile);
            var seenTempPaths = new List<string>();

            store.CustomTempWriter = (tempPath, json) =>
            {
                seenTempPaths.Add(tempPath);
                File.WriteAllText(tempPath, json);
            };

            var p1 = new GameProfile(@"C:\Games\A\game.exe");
            store.Save(p1);

            var p2 = new GameProfile(@"C:\Games\B\game.exe");
            store.Save(p2);

            var p3 = new GameProfile(@"C:\Games\C\game.exe");
            store.Save(p3);

            Assert.Equal(3, seenTempPaths.Count);
            Assert.Equal(3, seenTempPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void ProfileStore_InPlaceMutationAndSignatures_Preserved()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");

            var store = new ProfileStore(profilesFile);

            // GetOrCreate creates in memory
            var profile = store.GetOrCreate(@"C:\Games\Test\game.exe");
            Assert.NotNull(profile);
            Assert.Equal(@"C:\Games\Test\game.exe", profile.ExePath);
            Assert.Equal("game.exe", profile.ExeName);

            // GetAll returns in-memory values
            var all = store.GetAll();
            Assert.Single(all);

            // Simulate save failure: in-memory profile was registered, but disk save threw
            store.CustomTempWriter = (_, _) => throw new IOException("Disk error");
            var profile2 = new GameProfile(@"C:\Games\Test2\game.exe");

            Assert.Throws<IOException>(() => store.Save(profile2));

            // In-memory mutation is preserved (existing behavior)
            Assert.Equal(2, store.GetAll().Count());
        }
    }
}
