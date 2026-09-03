using System;
using System.IO;

namespace DXVKCompanion.Storage
{
    public static class Paths
    {
        public static string AppDataRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "DXVK-Companion");

        public static string ProfilesFile =>
            Path.Combine(AppDataRoot, "games.json");

        public static string CacheFile =>
            Path.Combine(AppDataRoot, "cache.json");

        public static void EnsureDirectories()
        {
            if (!Directory.Exists(AppDataRoot))
                Directory.CreateDirectory(AppDataRoot);
        }
    }
}
