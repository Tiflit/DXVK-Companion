using System;

namespace DXVKCompanion.Models
{
    public sealed class ManagedFileRecord
    {
        public string RelativePath { get; set; } = string.Empty;
        public FileOriginalState OriginalState { get; set; } = FileOriginalState.Unknown;
        public string? BackupRelativePath { get; set; }
        public string? OriginalSha256 { get; set; }
        public string? ExpectedManagedSha256 { get; set; }
        public string? ManagedDxvkVersion { get; set; }
        public string? OriginalDxvkVersion { get; set; }
        public ManagedFileState CurrentState { get; set; } = ManagedFileState.Unknown;
        public DateTime? LastVerifiedUtc { get; set; }
    }
}
