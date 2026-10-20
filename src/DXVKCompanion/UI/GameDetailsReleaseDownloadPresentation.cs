using DXVKCompanion.DXVK;
using DXVKCompanion.Models;

namespace DXVKCompanion.UI;

/// <summary>Pure presentation decisions: acquisition never chooses or pins a version.</summary>
public static class GameDetailsReleaseDownloadPresentation
{
    public static bool CanDownload(bool busy, bool pendingWork, bool hasSelection) => !busy && !pendingWork && hasSelection;

    public static string LocalStatus(string root, string tag, GraphicsApi api, string? architecture)
    {
        if (DxvkLocalReleaseLibrary.IsVersionAvailable(root, tag, api, architecture)) return "Downloaded — compatible";
        if (DxvkLocalReleaseLibrary.GetVersionDirectory(root, tag) != null) return "Local files — unavailable for this game or invalid";
        return "Available to download";
    }

    public static string CompletedStatus(DxvkReleaseAcquirer.AcquisitionResult result) => !result.HasAcquisitionManifest
        ? "Ready — existing local payload reused; no acquisition manifest or upstream verification. Game selection unchanged."
        : result.UpstreamDigestVerified
        ? "Ready — official SHA-256 verified. Game selection unchanged."
        : "Ready — official origin and local hashes recorded; no upstream digest. Game selection unchanged.";
}
