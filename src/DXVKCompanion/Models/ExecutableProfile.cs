using System;
using System.Collections.Generic;

namespace DXVKCompanion.Models
{
    public sealed class ExecutableProfile
    {
        public string RelativePath { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public GraphicsApi LastKnownApi { get; set; } = GraphicsApi.Unknown;
        public ApiDetectionConfidence ApiConfidence { get; set; } = ApiDetectionConfidence.Unknown;
        public string LastKnownArchitecture { get; set; } = "Unknown";
        public DateTime? LastSeenUtc { get; set; }
        public List<string> DetectionEvidence { get; set; } = new();
    }
}
