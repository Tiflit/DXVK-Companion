namespace DXVKCompanion.Models
{
    public class ReleaseInfo
    {
        public string Version { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public long ReleaseId { get; set; }
        public long AssetId { get; set; }
        public string AssetName { get; set; } = "";
        public long AssetSize { get; set; }
        public string? AssetDigest { get; set; }
    }
}
