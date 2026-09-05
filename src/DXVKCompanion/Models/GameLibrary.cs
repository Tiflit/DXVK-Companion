using System.Collections.Generic;

namespace DXVKCompanion.Models
{
    public sealed class GameLibrary
    {
        public const int CurrentSchemaVersion = 1;
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<GameInstallation> Installations { get; set; } = new();
    }
}
