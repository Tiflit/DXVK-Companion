using System.IO;

namespace DXVKCompanion.Storage
{
    public static class GameLibraryPaths
    {
        public static string GameLibraryFile => Path.Combine(Paths.ProfilesDir, "game-library.json");
        public static string BackupsDir => Path.Combine(Paths.ProfilesDir, "Backups");
    }
}
