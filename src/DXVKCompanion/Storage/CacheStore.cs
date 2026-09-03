using System.IO;
using System.Text.Json;
using DXVKCompanion.Models;

namespace DXVKCompanion.Storage
{
    public class CacheStore
    {
        public CachedRelease? Load()
        {
            Paths.EnsureDirectories();

            if (!File.Exists(Paths.CacheFile))
                return null;

            var json = File.ReadAllText(Paths.CacheFile);
            return JsonSerializer.Deserialize<CachedRelease>(json);
        }

        public void Save(CachedRelease release)
        {
            Paths.EnsureDirectories();

            var json = JsonSerializer.Serialize(release, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(Paths.CacheFile, json);
        }
    }
}
