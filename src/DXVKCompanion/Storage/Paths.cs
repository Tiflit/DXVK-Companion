using System;
using System.IO;

namespace DXVKCompanion.Storage
{
    public static class Paths
    {
        public static readonly string Root =
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        public static string ProfilesDir => Path.Combine(Root, "Profiles");
        public static string CacheDir => Path.Combine(Root, "Cache");
        public static string LogsDir => Path.Combine(Root, "Logs");
        public static string DxvkDir => Path.Combine(Root, "DXVK");

        public static string ProfilesFile => Path.Combine(ProfilesDir, "games.json");
        public static string CacheFile => Path.Combine(CacheDir, "cache.json");
        public static string LogFile => Path.Combine(LogsDir, "companion.log");

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(ProfilesDir);
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(DxvkDir);
        }
    }
}
