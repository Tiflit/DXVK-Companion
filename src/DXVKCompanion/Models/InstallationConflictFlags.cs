using System;

namespace DXVKCompanion.Models
{
    [Flags]
    public enum InstallationConflictFlags
    {
        None = 0,
        DxvkVersion = 1,
        Architecture = 2,
        FrameLimit = 4,
        UnknownOriginalFile = 8
    }
}
