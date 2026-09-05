using System;

namespace DXVKCompanion.Models
{
    public sealed class PendingAction
    {
        public PendingActionType Type { get; set; } = PendingActionType.None;
        public string? TargetDxvkVersion { get; set; }
        public string? Reason { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public bool IsPending => Type != PendingActionType.None;

        public static PendingAction Install(string version, string? reason = null) => new()
        {
            Type = PendingActionType.Install, TargetDxvkVersion = version, Reason = reason
        };
        public static PendingAction Update(string version, string? reason = null) => new()
        {
            Type = PendingActionType.Update, TargetDxvkVersion = version, Reason = reason
        };
        public static PendingAction Reapply(string version, string? reason = null) => new()
        {
            Type = PendingActionType.Reapply, TargetDxvkVersion = version, Reason = reason
        };
        public static PendingAction Restore(string? reason = null) => new()
        {
            Type = PendingActionType.Restore, Reason = reason
        };
    }
}
