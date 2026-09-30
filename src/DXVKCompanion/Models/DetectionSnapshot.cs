using System;

namespace DXVKCompanion.Models
{
    public sealed class DetectionSnapshot
    {
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public string ExecutablePath { get; init; } = string.Empty;
        public string InstallationRoot { get; init; } = string.Empty;
        public string ExecutableRelativePath { get; init; } = string.Empty;
        public ApiClassificationResult Classification { get; init; } = new();
        public AntiCheatAssessment AntiCheat { get; init; } = new();
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
        public bool HasApiChanged { get; set; }
        public GraphicsApi? PreviousApi { get; set; }
    }
}
