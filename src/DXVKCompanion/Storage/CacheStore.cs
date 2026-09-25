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
                CachedAt = System.DateTime.UtcNow,
                ETag = etag
            };

            var json = JsonSerializer.Serialize(cached, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_cacheFile, json);
        }
    }
}
