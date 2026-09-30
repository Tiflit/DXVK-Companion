namespace DXVKCompanion.Models
{
    public class ReleaseInfo
    {
        public string Version { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string? ArchiveSha256 { get; set; }
    }
}
