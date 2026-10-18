using System;

namespace DXVKCompanion.Models
{
    public class CachedRelease
    {
        public ReleaseInfo Release { get; set; } = new ReleaseInfo();

        // Standardized name - successful metadata validation timestamp
        public DateTime CachedAt { get; set; }

        // Time of the latest check attempt (successful or failed)
        public DateTime? LastAttemptAt { get; set; }

        // GitHub ETag for 304 Not Modified support
        public string? ETag { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime EffectiveLastAttempt
        {
            get
            {
                if (LastAttemptAt.HasValue && LastAttemptAt.Value != default)
                {
                    if (CachedAt != default && CachedAt > LastAttemptAt.Value)
                        return CachedAt;
                    return LastAttemptAt.Value;
                }
                return CachedAt;
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasValidRelease =>
            Release != null &&
            !string.IsNullOrWhiteSpace(Release.Version) &&
            !string.Equals(Release.Version, "unknown", StringComparison.OrdinalIgnoreCase);

        // 24-hour TTL based on CachedAt
        public bool IsExpired() => IsExpired(DateTime.UtcNow);

        public bool IsExpired(DateTime utcNow)
        {
            return (utcNow - CachedAt).TotalHours > 24;
        }

        // 24-hour check due calculation based on EffectiveLastAttempt
        public bool IsDue() => IsDue(DateTime.UtcNow);

        public bool IsDue(DateTime utcNow)
        {
            var effective = EffectiveLastAttempt;
            if (effective == default)
                return true;
            return (utcNow - effective) >= TimeSpan.FromHours(24);
        }
    }
}
