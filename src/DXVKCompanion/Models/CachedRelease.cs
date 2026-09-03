using System;

namespace DXVKCompanion.Models
{
    public class CachedRelease
    {
        public ReleaseInfo Release { get; set; }
        public DateTime Timestamp { get; set; }

        public CachedRelease() { }

        public CachedRelease(ReleaseInfo release, DateTime timestamp)
        {
            Release = release;
            Timestamp = timestamp;
        }
    }
}
