using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class CacheStorePersistenceTests
    {
        [Fact]
        public void CacheStore_FirstSave_RoundTripsRepresentativeMetadataAndETag_IndentedJson()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            var store = new CacheStore(cacheFile);
            var release = new ReleaseInfo
            {
                Version = "2.5",
                DownloadUrl = "https://github.com/doitsujin/dxvk/releases/download/v2.5/dxvk-2.5.tar.gz"
            };
            string etag = "W/\"release-2.5-tag\"";

            store.SaveCachedRelease(release, etag);

            Assert.True(File.Exists(cacheFile));
            string rawJson = File.ReadAllText(cacheFile);
            Assert.Contains("\n", rawJson);
            Assert.Contains("\"Version\": \"2.5\"", rawJson);

            var reloadedStore = new CacheStore(cacheFile);
            var loaded = reloadedStore.LoadCachedRelease();

            Assert.NotNull(loaded);
            Assert.Equal("2.5", loaded.Release.Version);
            Assert.Equal("https://github.com/doitsujin/dxvk/releases/download/v2.5/dxvk-2.5.tar.gz", loaded.Release.DownloadUrl);
            Assert.Equal(etag, loaded.ETag);
            Assert.True((DateTime.UtcNow - loaded.CachedAt).TotalSeconds < 30);
            Assert.False(loaded.IsExpired());
        }

        [Fact]
        public void CacheStore_FirstSave_NullETag_RoundTripsSuccessfully()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            var store = new CacheStore(cacheFile);
            var release = new ReleaseInfo
            {
                Version = "2.4.1",
                DownloadUrl = "https://github.com/doitsujin/dxvk/releases/download/v2.4.1/dxvk-2.4.1.tar.gz"
            };

            store.SaveCachedRelease(release, etag: null);

            var reloadedStore = new CacheStore(cacheFile);
            var loaded = reloadedStore.LoadCachedRelease();

            Assert.NotNull(loaded);
            Assert.Equal("2.4.1", loaded.Release.Version);
            Assert.Null(loaded.ETag);
        }

        [Fact]
        public void CacheStore_ReplacementSave_RoundTripsUpdatedReleaseAndETag()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            var store = new CacheStore(cacheFile);
            store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.4", DownloadUrl = "https://example.com/2.4.tar.gz" },
                "etag-v2.4");

            // Mutate and save replacement
            store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.5", DownloadUrl = "https://example.com/2.5.tar.gz" },
                "etag-v2.5");

            var reloaded = new CacheStore(cacheFile);
            var loaded = reloaded.LoadCachedRelease();

            Assert.NotNull(loaded);
            Assert.Equal("2.5", loaded.Release.Version);
            Assert.Equal("https://example.com/2.5.tar.gz", loaded.Release.DownloadUrl);
            Assert.Equal("etag-v2.5", loaded.ETag);
        }

        [Fact]
        public void CacheStore_StagedWrite_WritesToOperationUniqueTempFileInSameDirectory_BeforeCommitReachesTarget()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            var store = new CacheStore(cacheFile);
            store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.4", DownloadUrl = "https://example.com/2.4.tar.gz" },
                "etag-initial");

            string originalTargetContent = File.ReadAllText(cacheFile);
            string? capturedTempPath = null;
            string? capturedCommitSource = null;
            string? capturedCommitDest = null;
            bool targetWasUnchangedDuringTempWrite = false;
            bool tempFileExistedDuringCommit = false;

            store.CustomTempWriter = (tempPath, json) =>
            {
                capturedTempPath = tempPath;
                targetWasUnchangedDuringTempWrite = (File.ReadAllText(cacheFile) == originalTargetContent);
                File.WriteAllText(tempPath, json);
            };

            store.CustomCommitReplace = (tempPath, targetPath) =>
            {
                capturedCommitSource = tempPath;
                capturedCommitDest = targetPath;
                tempFileExistedDuringCommit = File.Exists(tempPath);
                File.Move(tempPath, targetPath, overwrite: true);
            };

            store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.5", DownloadUrl = "https://example.com/2.5.tar.gz" },
                "etag-updated");

            Assert.NotNull(capturedTempPath);
            Assert.NotEqual(cacheFile, capturedTempPath);
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(cacheFile)), Path.GetDirectoryName(Path.GetFullPath(capturedTempPath)));
            Assert.Contains(".tmp.", capturedTempPath);
            Assert.True(targetWasUnchangedDuringTempWrite, "Staging write must not modify the live target.");
            Assert.True(tempFileExistedDuringCommit, "Temporary file must exist when commit is invoked.");
            Assert.Equal(capturedTempPath, capturedCommitSource);
            Assert.Equal(cacheFile, capturedCommitDest);
            Assert.False(File.Exists(capturedTempPath), "Operation-owned temporary file must be consumed or cleaned after commit.");

            var reloaded = new CacheStore(cacheFile);
            var loaded = reloaded.LoadCachedRelease();
            Assert.NotNull(loaded);
            Assert.Equal("2.5", loaded.Release.Version);
        }

        [Fact]
        public void CacheStore_PartialWriteFailure_DuringStaging_LeavesExistingTargetByteForByteUnchanged_AndReloadable()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            var store = new CacheStore(cacheFile);
            store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.4", DownloadUrl = "https://example.com/2.4.tar.gz" },
                "etag-v2.4");

            byte[] originalBytes = File.ReadAllBytes(cacheFile);
            string? observedTempPath = null;

            store.CustomTempWriter = (tempPath, json) =>
            {
                observedTempPath = tempPath;
                File.WriteAllText(tempPath, json.Substring(0, Math.Min(30, json.Length)));
                throw new IOException("Simulated disk full or abort during staging.");
            };

            var ex = Assert.Throws<IOException>(() => store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.5", DownloadUrl = "https://example.com/2.5.tar.gz" },
                "etag-v2.5"));
            Assert.Contains("Simulated disk full", ex.Message);

            byte[] afterFailureBytes = File.ReadAllBytes(cacheFile);
            Assert.Equal(originalBytes, afterFailureBytes);

            var reloaded = new CacheStore(cacheFile);
            var loaded = reloaded.LoadCachedRelease();
            Assert.NotNull(loaded);
            Assert.Equal("2.4", loaded.Release.Version);
            Assert.Equal("etag-v2.4", loaded.ETag);

            Assert.NotNull(observedTempPath);
            Assert.False(File.Exists(observedTempPath), "Temporary file from failed staging must be cleaned up.");
        }

        [Fact]
        public void CacheStore_FirstSave_PartialWriteFailure_LeavesNoLiveTarget()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            Assert.False(File.Exists(cacheFile));

            var store = new CacheStore(cacheFile);
            string? observedTemp = null;

            store.CustomTempWriter = (tempPath, json) =>
            {
                observedTemp = tempPath;
                File.WriteAllText(tempPath, "{\"Release\": {");
                throw new IOException("Simulated write abort on first save.");
            };

            var ex = Assert.Throws<IOException>(() => store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.5", DownloadUrl = "https://example.com/2.5.tar.gz" },
                "etag-v2.5"));
            Assert.Contains("Simulated write abort", ex.Message);

            Assert.False(File.Exists(cacheFile), "No live cache file should exist after first-save staging failure.");
            Assert.NotNull(observedTemp);
            Assert.False(File.Exists(observedTemp), "Temporary file must be cleaned up after first-save staging failure.");
        }

        [Fact]
        public void CacheStore_FailureBeforeReplacementCompletes_PropagatesException_LeavesTargetIntact_AndCleansTempFile()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            var store = new CacheStore(cacheFile);
            store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.4", DownloadUrl = "https://example.com/2.4.tar.gz" },
                "etag-v2.4");

            byte[] originalBytes = File.ReadAllBytes(cacheFile);
            string? capturedTempPath = null;

            store.CustomCommitReplace = (tempPath, targetPath) =>
            {
                capturedTempPath = tempPath;
                throw new IOException("Simulated filesystem error during commit replacement.");
            };

            var ex = Assert.Throws<IOException>(() => store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.5", DownloadUrl = "https://example.com/2.5.tar.gz" },
                "etag-v2.5"));
            Assert.Contains("Simulated filesystem error during commit replacement", ex.Message);

            byte[] currentBytes = File.ReadAllBytes(cacheFile);
            Assert.Equal(originalBytes, currentBytes);

            var reloaded = new CacheStore(cacheFile);
            var loaded = reloaded.LoadCachedRelease();
            Assert.NotNull(loaded);
            Assert.Equal("2.4", loaded.Release.Version);

            Assert.NotNull(capturedTempPath);
            Assert.False(File.Exists(capturedTempPath), "Staging temp file must be deleted when commit replacement fails.");
        }

        [Fact]
        public void CacheStore_CleanupFailure_DoesNotMaskPrimaryStagingFailure()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            var store = new CacheStore(cacheFile);

            store.CustomTempWriter = (tempPath, json) =>
            {
                throw new IOException("Primary staging write failure.");
            };

            store.CustomTempCleaner = (tempPath) =>
            {
                throw new UnauthorizedAccessException("Secondary cleanup failure.");
            };

            var ex = Assert.Throws<IOException>(() => store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.5", DownloadUrl = "" },
                "etag"));
            Assert.Equal("Primary staging write failure.", ex.Message);
        }

        [Fact]
        public void CacheStore_CleanupFailure_DoesNotMaskPrimaryCommitFailure()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            var store = new CacheStore(cacheFile);

            store.CustomCommitReplace = (tempPath, targetPath) =>
            {
                throw new IOException("Primary commit replacement failure.");
            };

            store.CustomTempCleaner = (tempPath) =>
            {
                throw new UnauthorizedAccessException("Secondary cleanup failure.");
            };

            var ex = Assert.Throws<IOException>(() => store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.5", DownloadUrl = "" },
                "etag"));
            Assert.Equal("Primary commit replacement failure.", ex.Message);
        }

        [Fact]
        public void CacheStore_StagedSave_CleansOnlyOwnTempFile_PreservingSiblingSentinels()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            // Seed unrelated sibling files in target directory
            string preExistingFixedTmp = Path.Combine(testDir.RootPath, "release-cache.json.tmp");
            string unrelatedTemp = Path.Combine(testDir.RootPath, "release-cache.json.tmp.unrelated");
            string unrelatedRecovery = Path.Combine(testDir.RootPath, "release-cache.json.recovery.20261015.json");
            string otherTmp = Path.Combine(testDir.RootPath, "other.tmp");

            File.WriteAllText(preExistingFixedTmp, "pre-existing fixed tmp content");
            File.WriteAllText(unrelatedTemp, "unrelated temp content");
            File.WriteAllText(unrelatedRecovery, "unrelated recovery content");
            File.WriteAllText(otherTmp, "other tmp content");

            var store = new CacheStore(cacheFile);

            // 1. Successful save preserves all sentinels
            store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.5", DownloadUrl = "" },
                "etag-1");

            Assert.True(File.Exists(preExistingFixedTmp));
            Assert.Equal("pre-existing fixed tmp content", File.ReadAllText(preExistingFixedTmp));
            Assert.True(File.Exists(unrelatedTemp));
            Assert.Equal("unrelated temp content", File.ReadAllText(unrelatedTemp));
            Assert.True(File.Exists(unrelatedRecovery));
            Assert.Equal("unrelated recovery content", File.ReadAllText(unrelatedRecovery));
            Assert.True(File.Exists(otherTmp));
            Assert.Equal("other tmp content", File.ReadAllText(otherTmp));

            // 2. Failed staging write with cleanup preserves all sentinels
            store.CustomTempWriter = (tempPath, json) =>
            {
                File.WriteAllText(tempPath, "aborting temp content");
                throw new IOException("Simulated write abort.");
            };

            Assert.Throws<IOException>(() => store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.6", DownloadUrl = "" },
                "etag-2"));

            Assert.True(File.Exists(preExistingFixedTmp));
            Assert.Equal("pre-existing fixed tmp content", File.ReadAllText(preExistingFixedTmp));
            Assert.True(File.Exists(unrelatedTemp));
            Assert.Equal("unrelated temp content", File.ReadAllText(unrelatedTemp));
            Assert.True(File.Exists(unrelatedRecovery));
            Assert.Equal("unrelated recovery content", File.ReadAllText(unrelatedRecovery));
            Assert.True(File.Exists(otherTmp));
            Assert.Equal("other tmp content", File.ReadAllText(otherTmp));

            // 3. Failed commit replacement with cleanup preserves all sentinels
            store.CustomTempWriter = null;
            store.CustomCommitReplace = (tempPath, targetPath) =>
            {
                throw new IOException("Simulated commit abort.");
            };

            Assert.Throws<IOException>(() => store.SaveCachedRelease(
                new ReleaseInfo { Version = "2.7", DownloadUrl = "" },
                "etag-3"));

            Assert.True(File.Exists(preExistingFixedTmp));
            Assert.Equal("pre-existing fixed tmp content", File.ReadAllText(preExistingFixedTmp));
            Assert.True(File.Exists(unrelatedTemp));
            Assert.Equal("unrelated temp content", File.ReadAllText(unrelatedTemp));
            Assert.True(File.Exists(unrelatedRecovery));
            Assert.Equal("unrelated recovery content", File.ReadAllText(unrelatedRecovery));
            Assert.True(File.Exists(otherTmp));
            Assert.Equal("other tmp content", File.ReadAllText(otherTmp));
        }

        [Fact]
        public void CacheStore_MultipleOperations_GenerateDistinctTempPaths()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            var store = new CacheStore(cacheFile);
            var seenTempPaths = new List<string>();

            store.CustomTempWriter = (tempPath, json) =>
            {
                seenTempPaths.Add(tempPath);
                File.WriteAllText(tempPath, json);
            };

            store.SaveCachedRelease(new ReleaseInfo { Version = "2.3", DownloadUrl = "" }, "etag-1");
            store.SaveCachedRelease(new ReleaseInfo { Version = "2.4", DownloadUrl = "" }, "etag-2");
            store.SaveCachedRelease(new ReleaseInfo { Version = "2.5", DownloadUrl = "" }, "etag-3");

            Assert.Equal(3, seenTempPaths.Count);
            Assert.Equal(3, seenTempPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void CacheStore_RecordFailedAttempt_WithoutExistingCache_SavesAttemptTimestamp_WithoutValidRelease()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var attemptTime = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            store.RecordFailedAttempt(attemptTime);

            Assert.True(File.Exists(cacheFile));
            var reloaded = store.LoadCachedRelease();
            Assert.NotNull(reloaded);
            Assert.Equal(attemptTime, reloaded.LastAttemptAt);
            Assert.False(reloaded.HasValidRelease);
            Assert.Null(reloaded.ETag);
        }

        [Fact]
        public void CacheStore_RecordFailedAttempt_WithExistingCache_UpdatesAttemptTimestamp_PreservingPriorReleaseAndETag()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");
            var store = new CacheStore(cacheFile);

            var originalCachedAt = new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc);
            store.SaveCachedRelease(
                new ReleaseInfo { Version = "v2.5", DownloadUrl = "https://example.com/v2.5.tar.gz" },
                "\"etag-2.5\"",
                cachedAt: originalCachedAt,
                lastAttemptAt: originalCachedAt);

            var failedAttemptTime = new DateTime(2026, 10, 17, 10, 30, 0, DateTimeKind.Utc);
            store.RecordFailedAttempt(failedAttemptTime);

            var reloaded = store.LoadCachedRelease();
            Assert.NotNull(reloaded);
            Assert.Equal("v2.5", reloaded.Release.Version);
            Assert.Equal("https://example.com/v2.5.tar.gz", reloaded.Release.DownloadUrl);
            Assert.Equal("\"etag-2.5\"", reloaded.ETag);
            Assert.Equal(originalCachedAt, reloaded.CachedAt);
            Assert.Equal(failedAttemptTime, reloaded.LastAttemptAt);
            Assert.True(reloaded.HasValidRelease);
        }

        [Fact]
        public void CacheStore_LoadCachedRelease_LegacyFormatWithoutLastAttemptAt_LoadsCorrectlyWithNullLastAttempt()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            string legacyJson = @"{
  ""Release"": {
    ""Version"": ""v2.4"",
    ""DownloadUrl"": ""https://example.com/v2.4.tar.gz""
  },
  ""CachedAt"": ""2026-10-17T08:00:00Z"",
  ""ETag"": ""\""etag-legacy\""""
}";
            File.WriteAllText(cacheFile, legacyJson);

            var store = new CacheStore(cacheFile);
            var loaded = store.LoadCachedRelease();

            Assert.NotNull(loaded);
            Assert.Equal("v2.4", loaded.Release.Version);
            Assert.Equal("\"etag-legacy\"", loaded.ETag);
            Assert.Null(loaded.LastAttemptAt);
            Assert.Equal(DateTime.Parse("2026-10-17T08:00:00Z").ToUniversalTime(), loaded.EffectiveLastAttempt);
            Assert.True(loaded.HasValidRelease);
        }

        [Fact]
        public void CacheStore_RecordFailedAttempt_WhenExistingFileMalformed_PreservesFileWithoutOverwriting()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            string corruptedContent = "<<<CORRUPTED JSON CONTENT>>>";
            File.WriteAllText(cacheFile, corruptedContent);

            var attemptTime = new DateTime(2026, 10, 17, 18, 0, 0, DateTimeKind.Utc);
            var store = new CacheStore(cacheFile);
            store.RecordFailedAttempt(attemptTime);

            // Staged write must not overwrite the unparseable file
            Assert.Equal(corruptedContent, File.ReadAllText(cacheFile));

            // Preserves recoverable bytes to .corrupt backup
            Assert.True(File.Exists(store.CorruptBackupFile));
            Assert.Equal(corruptedContent, File.ReadAllText(store.CorruptBackupFile));

            // Stores attempt in sidecar so that throttle is preserved across reloads
            Assert.True(File.Exists(store.AttemptFile));
            var loaded = store.LoadCachedRelease();
            Assert.NotNull(loaded);
            Assert.Equal(attemptTime, loaded.LastAttemptAt);
            Assert.False(loaded.HasValidRelease);

            // Reconstructed store also preserves throttle and corrupt file
            var reloaded = new CacheStore(cacheFile);
            var reloadedRecord = reloaded.LoadCachedRelease();
            Assert.NotNull(reloadedRecord);
            Assert.Equal(attemptTime, reloadedRecord.LastAttemptAt);
            Assert.False(reloadedRecord.HasValidRelease);
            Assert.Equal(corruptedContent, File.ReadAllText(cacheFile));
        }

        [Fact]
        public void CacheStore_SaveCachedRelease_WhenExistingFileMalformed_PreservesCorruptBackupAndWritesNewRelease()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            string corruptedContent = "<<<CORRUPTED JSON CONTENT>>>";
            File.WriteAllText(cacheFile, corruptedContent);

            var store = new CacheStore(cacheFile);
            var release = new ReleaseInfo { Version = "v2.5", DownloadUrl = "https://example.com/dxvk-2.5.tar.gz" };
            store.SaveCachedRelease(release, "\"etag-25\"");

            // Preserves recoverable corrupt bytes in .corrupt
            Assert.True(File.Exists(store.CorruptBackupFile));
            Assert.Equal(corruptedContent, File.ReadAllText(store.CorruptBackupFile));

            // Primary cache file now contains valid new release
            var loaded = store.LoadCachedRelease();
            Assert.NotNull(loaded);
            Assert.True(loaded.HasValidRelease);
            Assert.Equal("v2.5", loaded.Release.Version);

            // Attempt file is cleaned up if present
            Assert.False(File.Exists(store.AttemptFile));
        }

        [Fact]
        public void CacheStore_SaveCachedRelease_WhenDistinctCorruptBackupExists_PreservesPriorBackupAndCreatesUniqueBackup()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            var store = new CacheStore(cacheFile);

            string priorCorruptContent = "<<<PRIOR DISTINCT RECOVERY BYTES>>>";
            File.WriteAllText(store.CorruptBackupFile, priorCorruptContent);

            string newCorruptContent = "<<<NEW DISTINCT CORRUPT PRIMARY BYTES>>>";
            File.WriteAllText(cacheFile, newCorruptContent);

            var release = new ReleaseInfo { Version = "v2.5", DownloadUrl = "https://example.com/dxvk-2.5.tar.gz" };
            store.SaveCachedRelease(release, "\"etag-25\"");

            // Prior distinct backup remains completely immutable
            Assert.True(File.Exists(store.CorruptBackupFile));
            Assert.Equal(priorCorruptContent, File.ReadAllText(store.CorruptBackupFile));

            // New corrupt primary is preserved in a unique additional backup file
            string additionalBackup = store.CorruptBackupFile + ".1";
            Assert.True(File.Exists(additionalBackup));
            Assert.Equal(newCorruptContent, File.ReadAllText(additionalBackup));

            // Primary cache file now contains valid new release
            var loaded = store.LoadCachedRelease();
            Assert.NotNull(loaded);
            Assert.True(loaded.HasValidRelease);
            Assert.Equal("v2.5", loaded.Release.Version);
        }

        [Fact]
        public void CacheStore_SaveCachedRelease_WhenPreservationFails_LeavesPrimaryAndPriorBackupsUnchangedAndThrows()
        {
            using var testDir = new SyntheticTestDirectory();
            string cacheFile = Path.Combine(testDir.RootPath, "release-cache.json");

            var store = new CacheStore(cacheFile);

            string corruptPrimaryContent = "<<<ONLY CORRUPT EVIDENCE - DO NOT CLOBBER>>>";
            File.WriteAllText(cacheFile, corruptPrimaryContent);

            // Induce backup-copy failure by creating a directory at the intended backup path
            Directory.CreateDirectory(store.CorruptBackupFile);

            var release = new ReleaseInfo { Version = "v2.5", DownloadUrl = "https://example.com/dxvk-2.5.tar.gz" };

            // Save must throw and refuse to overwrite primary when preservation cannot complete
            Assert.Throws<IOException>(() => store.SaveCachedRelease(release, "\"etag-25\""));

            // Primary cache file is completely unchanged and contains original corrupt bytes
            Assert.Equal(corruptPrimaryContent, File.ReadAllText(cacheFile));

            // Attempt file is not created by SaveCachedRelease
            Assert.False(File.Exists(store.AttemptFile));
        }
    }
}
