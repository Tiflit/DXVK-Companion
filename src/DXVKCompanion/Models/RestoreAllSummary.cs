using System.Collections.Generic;

namespace DXVKCompanion.Models
{
    public class RestoreAllSummary
    {
        public int TotalManaged { get; set; }
        public int Restored { get; set; }
        public int AlreadyRestored { get; set; }
        public int QueuedRunning { get; set; }
        public int FailedOrAttentionRequired { get; set; }
        public List<string> Messages { get; } = new();
    }
}
