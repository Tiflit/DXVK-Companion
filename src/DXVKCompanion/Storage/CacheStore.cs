using System;
using System.IO;
using System.Text.Json;
using DXVKCompanion.Models;
using DXVKCompanion.Utils;

namespace DXVKCompanion.Storage
{
    public class CacheStore
    {
        private readonly string _cacheFile;
        private readonly string _attemptFile;
        private readonly string _corruptFile;

        public string CacheFile => _cacheFile;
        public string AttemptFile => _attemptFile;
        public string CorruptBackupFile => _corruptFile;

        internal Action<string, string>? CustomTempWriter { get; set; }
        internal Action<string, string>? CustomCommitReplace { get; set; }
        internal Action<string>? CustomTempCleaner { get; set; }

        public CacheStore(string? cacheFile = null)
        {
            _cacheFile = cacheFile ?? Paths.CacheFile;
            _attemptFile = _cacheFile + ".attempt.json";
            _corruptFile = _cacheFile + ".corrupt";
        }

        public bool HasUnreadableCacheFile()
        {
            if (!File.Exists(_cacheFile))
                return false;

            try
            {
                var json = File.ReadAllText(_cacheFile);
                var parsed = JsonSerializer.Deserialize<CachedRelease>(json);
                return parsed == null;
            }
            catch
            {
                return true;
            }
        }

        public CachedRelease? LoadCachedRelease()
        {
            if (File.Exists(_cacheFile))
            {
                try
                {
                    var json = File.ReadAllText(_cacheFile);
                    var cached = JsonSerializer.Deserialize<CachedRelease>(json);
                    if (cached != null)
                        return cached;
                }
                catch
                {
                    // Cache file exists on disk but is corrupt / unparseable
                }
            }

            // If the primary cache file was absent or unreadable, check if an attempt record was stored
            if (File.Exists(_attemptFile))
            {
                try
                {
                    var attemptJson = File.ReadAllText(_attemptFile);
                    var attemptRecord = JsonSerializer.Deserialize<CachedRelease>(attemptJson);
                    if (attemptRecord != null)
                        return attemptRecord;
                }
                catch
                {
                    // Attempt sidecar could not be read
                }
            }

            return null;
        }

        public void SaveCachedRelease(ReleaseInfo release, string? etag, DateTime? cachedAt = null, DateTime? lastAttemptAt = null)
        {
            var dir = Path.GetDirectoryName(_cacheFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (HasUnreadableCacheFile())
            {
                // Preserve recoverable bytes from unreadable cache before overwriting
                PreserveCorruptFile();
            }

            var now = DateTime.UtcNow;
            var cached = new CachedRelease
            {
                Release = release,
                CachedAt = cachedAt ?? now,
                LastAttemptAt = lastAttemptAt ?? cachedAt ?? now,
                ETag = etag
            };

            var json = JsonSerializer.Serialize(cached, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            CommitStagedJson(json, _cacheFile);

            // Clean up attempt sidecar now that a valid release cache is committed
            if (File.Exists(_attemptFile))
            {
                try { File.Delete(_attemptFile); } catch { }
            }
        }

        public void RecordFailedAttempt(DateTime? attemptAt = null)
        {
            var dir = Path.GetDirectoryName(_cacheFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var now = attemptAt ?? DateTime.UtcNow;

            if (HasUnreadableCacheFile())
            {
                // The cache file exists on disk but could not be parsed.
                // Attempt to preserve the unreadable file's recoverable bytes in .corrupt,
                // and store the attempt timestamp in the attempt sidecar so that
                // the corrupted file on disk is not overwritten, while throttle state
                // is reliably persisted across repeated calls and client reconstruction.
                try
                {
                    PreserveCorruptFile();
                }
                catch (Exception ex)
                {
                    Logger.Log($"CacheStore: failed to preserve corrupt cache file during failed attempt recording: {ex.Message}");
                }

                var attemptRecord = new CachedRelease
                {
                    Release = new ReleaseInfo(),
                    CachedAt = default,
                    LastAttemptAt = now,
                    ETag = null
                };

                var attemptJson = JsonSerializer.Serialize(attemptRecord, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                CommitStagedJson(attemptJson, _attemptFile);
                return;
            }

            var existing = LoadCachedRelease();
            var toSave = existing ?? new CachedRelease
            {
                Release = new ReleaseInfo(),
                CachedAt = default,
                ETag = null
            };

            toSave.LastAttemptAt = now;

            var json = JsonSerializer.Serialize(toSave, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            CommitStagedJson(json, _cacheFile);
        }

        private void PreserveCorruptFile()
        {
            if (!File.Exists(_cacheFile))
                return;

            byte[] primaryBytes = File.ReadAllBytes(_cacheFile);

            if (Directory.Exists(_corruptFile))
            {
                throw new IOException($"Cannot preserve corrupt cache file: target path '{_corruptFile}' is an existing directory.");
            }

            if (!File.Exists(_corruptFile))
            {
                File.Copy(_cacheFile, _corruptFile, overwrite: false);
                if (!File.Exists(_corruptFile) || !File.ReadAllBytes(_corruptFile).AsSpan().SequenceEqual(primaryBytes))
                {
                    throw new IOException($"Failed to verify corrupt backup file preservation at '{_corruptFile}'.");
                }
                return;
            }

            // _corruptFile exists as a file. Check if it already contains the identical bytes.
            byte[] existingBytes = File.ReadAllBytes(_corruptFile);
            if (existingBytes.AsSpan().SequenceEqual(primaryBytes))
            {
                // Already preserved identically in _corruptFile.
                return;
            }

            // _corruptFile contains different earlier recovery bytes. Keep it immutable!
            // Find a unique additional recovery filename that does not exist.
            int suffix = 1;
            string targetPath = $"{_corruptFile}.{suffix}";
            while (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                if (File.Exists(targetPath) && File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(primaryBytes))
                {
                    // Already preserved identically in targetPath.
                    return;
                }
                suffix++;
                targetPath = $"{_corruptFile}.{suffix}";
            }

            File.Copy(_cacheFile, targetPath, overwrite: false);
            if (!File.Exists(targetPath) || !File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(primaryBytes))
            {
                throw new IOException($"Failed to verify corrupt backup file preservation at '{targetPath}'.");
            }
        }

        private void CommitStagedJson(string json, string? targetFile = null)
        {
            string destination = targetFile ?? _cacheFile;
            string tempPath = destination + ".tmp." + Guid.NewGuid().ToString("N");
            bool committed = false;
            try
            {
                if (CustomTempWriter != null)
                {
                    CustomTempWriter(tempPath, json);
                }
                else
                {
                    File.WriteAllText(tempPath, json);
                }

                if (CustomCommitReplace != null)
                {
                    CustomCommitReplace(tempPath, destination);
                }
                else
                {
                    if (File.Exists(destination))
                    {
                        File.Move(tempPath, destination, overwrite: true);
                    }
                    else
                    {
                        File.Move(tempPath, destination);
                    }
                }

                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    try
                    {
                        if (CustomTempCleaner != null)
                        {
                            CustomTempCleaner(tempPath);
                        }
                        else if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch
                    {
                        // Clean only the temporary file owned by the current operation.
                        // Cleanup failure must never mask the primary failure or reverse a commit.
                    }
                }
            }
        }
    }
}
