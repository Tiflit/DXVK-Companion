using System;
using System.Collections.Generic;

namespace DXVKCompanion.Models
{
    public sealed class PendingAction
    {
        public PendingActionType Type { get; set; } = PendingActionType.None;
        public string? TargetDxvkVersion { get; set; }
        public string? DownloadUrl { get; set; }
        public string? ArchiveSha256 { get; set; }
        public string? Architecture { get; set; }
        public List<string> RequiredDlls { get; set; } = new();
        public string? Reason { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public bool IsPending => Type != PendingActionType.None;

        public static PendingAction Install(
            string version,
            string? reason = null,
            string? downloadUrl = null,
            string? archiveSha256 = null,
            string? architecture = null,
            IEnumerable<string>? requiredDlls = null) => new()
        {
            Type = PendingActionType.Install,
            TargetDxvkVersion = version,
            Reason = reason,
            DownloadUrl = downloadUrl,
            ArchiveSha256 = archiveSha256,
            Architecture = architecture,
            RequiredDlls = requiredDlls != null ? new List<string>(requiredDlls) : new List<string>()
        };

        public static PendingAction Update(
            string version,
            string? reason = null,
            string? downloadUrl = null,
            string? archiveSha256 = null,
            string? architecture = null,
            IEnumerable<string>? requiredDlls = null) => new()
        {
            Type = PendingActionType.Update,
            TargetDxvkVersion = version,
            Reason = reason,
            DownloadUrl = downloadUrl,
            ArchiveSha256 = archiveSha256,
            Architecture = architecture,
            RequiredDlls = requiredDlls != null ? new List<string>(requiredDlls) : new List<string>()
        };

        public static PendingAction Reapply(
            string version,
            string? reason = null,
            string? downloadUrl = null,
            string? architecture = null,
            IEnumerable<string>? requiredDlls = null) => new()
        {
            Type = PendingActionType.Reapply,
            TargetDxvkVersion = version,
            Reason = reason,
            DownloadUrl = downloadUrl,
            Architecture = architecture,
            RequiredDlls = requiredDlls != null ? new List<string>(requiredDlls) : new List<string>()
        };

        public static PendingAction Restore(string? reason = null) => new()
        {
            Type = PendingActionType.Restore,
            Reason = reason
        };
    }
}
