using System;

namespace DXVKCompanion.Models
{
    public class CachedRelease
    {
        public ReleaseInfo Release { get; set; } = new ReleaseInfo();

        // Standardized name
        public DateTime CachedAt { get; set; }

        // GitHub ETag for 304 Not Modified support
        public string? ETag { get; set; }

        // 24-hour TTL
        public bool IsExpired()
        {
            return (DateTime.UtcNow - CachedAt).TotalHours > 24;
        }
    }
}
