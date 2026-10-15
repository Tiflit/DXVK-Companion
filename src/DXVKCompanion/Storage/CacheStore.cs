using System;
using System.IO;
using System.Text.Json;
using DXVKCompanion.Models;

namespace DXVKCompanion.Storage
{
    public class CacheStore
    {
        private readonly string _cacheFile;

        internal Action<string, string>? CustomTempWriter { get; set; }
        internal Action<string, string>? CustomCommitReplace { get; set; }
        internal Action<string>? CustomTempCleaner { get; set; }

        public CacheStore(string? cacheFile = null)
        {
            _cacheFile = cacheFile ?? Paths.CacheFile;
        }

        public CachedRelease? LoadCachedRelease()
        {
            var dir = Path.GetDirectoryName(_cacheFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(_cacheFile))
                return null;

            try
            {
                var json = File.ReadAllText(_cacheFile);
                return JsonSerializer.Deserialize<CachedRelease>(json);
            }
            catch
            {
                return null;
            }
        }

        public void SaveCachedRelease(ReleaseInfo release, string? etag)
        {
            var dir = Path.GetDirectoryName(_cacheFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var cached = new CachedRelease
            {
                Release = release,
                CachedAt = DateTime.UtcNow,
                ETag = etag
            };

            var json = JsonSerializer.Serialize(cached, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            string tempPath = _cacheFile + ".tmp." + Guid.NewGuid().ToString("N");
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
                    CustomCommitReplace(tempPath, _cacheFile);
                }
                else
                {
                    if (File.Exists(_cacheFile))
                    {
                        File.Move(tempPath, _cacheFile, overwrite: true);
                    }
                    else
                    {
                        File.Move(tempPath, _cacheFile);
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
