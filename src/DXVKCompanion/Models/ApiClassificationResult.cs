using System;
using System.Collections.Generic;

namespace DXVKCompanion.Models
{
    public sealed class ApiClassificationResult
    {
        public GraphicsApi PrimaryApi { get; init; } = GraphicsApi.Unknown;
        public IReadOnlyList<GraphicsApi> ObservedApis { get; init; } = Array.Empty<GraphicsApi>();
        public ApiDetectionConfidence Confidence { get; init; } = ApiDetectionConfidence.Unknown;
        public string Architecture { get; init; } = "Unknown";
        public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
        public string EvidenceSource { get; init; } = "None";
    }
}
