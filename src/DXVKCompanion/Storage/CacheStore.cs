using System.IO;
using System.Text.Json;
using DXVKCompanion.Models;

namespace DXVKCompanion.Storage
{
    public class CacheStore
    {
        private readonly string _cacheFile;

        public CacheStore(string? cacheFile = null)
        {
            _cacheFile = cacheFile ?? Paths.CacheFile;
        }

        private string GetCacheFilePath(string? version = null)
        {
            if (string.IsNullOrWhiteSpace(version) || string.Equals(version, "latest", System.StringComparison.OrdinalIgnoreCase))
                return _cacheFile;

            var dir = Path.GetDirectoryName(_cacheFile);
            string safeVersion = version.Replace('/', '_').Replace('\\', '_');
            return Path.Combine(dir ?? string.Empty, $"release-{safeVersion}.json");
        }

        public CachedRelease? LoadCachedRelease(string? version = null)
        {
            string path = GetCacheFilePath(version);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(path))
                return null;

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<CachedRelease>(json);
            }
            catch
            {
                return null;
            }
        }

        public void SaveCachedRelease(ReleaseInfo release, string? etag, string? version = null)
        {
            var cached = new CachedRelease
            {
                Release = release,
                CachedAt = System.DateTime.UtcNow,
                ETag = etag
            };

            var json = JsonSerializer.Serialize(cached, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            if (string.IsNullOrWhiteSpace(version) || string.Equals(version, "latest", System.StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(_cacheFile);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_cacheFile, json);
            }

            string targetVersion = !string.IsNullOrWhiteSpace(version) ? version : release.Version;
            if (!string.IsNullOrWhiteSpace(targetVersion) && !string.Equals(targetVersion, "latest", System.StringComparison.OrdinalIgnoreCase))
            {
                string versionPath = GetCacheFilePath(targetVersion);
                var dir = Path.GetDirectoryName(versionPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(versionPath, json);
            }
        }

        public void SaveLatest(CachedRelease cached)
        {
            var dir = Path.GetDirectoryName(_cacheFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(cached, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_cacheFile, json);
        }
    }
}
