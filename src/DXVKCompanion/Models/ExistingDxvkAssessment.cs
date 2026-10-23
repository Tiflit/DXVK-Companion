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
        // Transient assessment evidence; never a persisted ownership/default-file list.
        public IReadOnlyDictionary<string, string> ObservedSha256 { get; init; } = new Dictionary<string, string>();

        public bool CanBeAdopted => Status == ExistingDxvkStatus.OfficialRelease && !string.IsNullOrEmpty(MatchedVersion);
    }
}
