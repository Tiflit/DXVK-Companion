using System;
using System.Collections.Generic;

namespace DXVKCompanion.Models
{
    public sealed class ExistingDxvkAssessment
    {
        public ExistingDxvkStatus Status { get; init; } = ExistingDxvkStatus.None;
        public string? MatchedVersion { get; init; }
        public IReadOnlyList<string> DetectedDlls { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

        public bool CanBeAdopted => Status == ExistingDxvkStatus.OfficialRelease && !string.IsNullOrEmpty(MatchedVersion);
    }
}
