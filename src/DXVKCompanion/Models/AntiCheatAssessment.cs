using System;
using System.Collections.Generic;

namespace DXVKCompanion.Models
{
    public sealed class AntiCheatAssessment
    {
        public AntiCheatRisk Risk { get; init; } = AntiCheatRisk.None;
        public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

        public static AntiCheatAssessment None() => new()
        {
            Risk = AntiCheatRisk.None,
            Evidence = Array.Empty<string>()
        };

        public static AntiCheatAssessment SuspectedOrKnown(params string[] evidence) => new()
        {
            Risk = AntiCheatRisk.SuspectedOrKnown,
            Evidence = evidence
        };

        public static AntiCheatAssessment UnableToDetermine(string reason) => new()
        {
            Risk = AntiCheatRisk.UnableToDetermine,
            Evidence = new[] { reason }
        };
    }
}
