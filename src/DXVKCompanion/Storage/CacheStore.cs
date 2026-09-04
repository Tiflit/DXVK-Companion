using System.IO;
using System.Text.Json;
using DXVKCompanion.Models;

namespace DXVKCompanion.Storage
{
    public class CacheStore
    {
        public CachedRelease? LoadCachedRelease()
        {
            Paths.EnsureDirectories();

            if (!File.Exists(Paths.CacheFile))
                return null;

            try
            {
                var json = File.ReadAllText(Paths.CacheFile);
                return JsonSerializer.Deserialize<CachedRelease>(json);
            }
            catch
            {
                return null;
            }
        }

        public void SaveCachedRelease(ReleaseInfo release, string? etag)
        {
            Paths.EnsureDirectories();

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

            File.WriteAllText(Paths.CacheFile, json);
        }
    }
}
