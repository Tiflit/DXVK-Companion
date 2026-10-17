namespace DXVKCompanion.UI;

using System.Windows.Forms;
using DXVKCompanion.DXVK;

/// <summary>
/// Encapsulates the presentation details for a Reapply action outcome.
/// </summary>
public readonly record struct ReapplyPresentation(
    string Message,
    string Title,
    MessageBoxIcon Icon,
    bool ShouldCloseWindow);

/// <summary>
/// Pure presentation mapper for DXVK reapply action outcomes in Game Details.
/// </summary>
public static class ReapplyResultPresentation
{
    public const string AppliedTitle = "Reapply";
    public const string AppliedMessage = "DXVK reapplied successfully and baseline updated.";

    public const string QueuedTitle = "Reapply Queued";
    public const string QueuedMessage = "DXVK reapply is queued and will be applied when the game exits.";

    public const string FailedTitle = "Reapply Refused";
    public const string DefaultFailedReason = "Reapply operation failed.";
    public const string FailedMessagePrefix = "DXVK reapply was refused or failed:\n\n";

    /// <summary>
    /// Maps a <see cref="DxvkActionResult"/> and optional refusal reason to its UI presentation.
    /// </summary>
    /// <param name="result">The outcome of the reapply request.</param>
    /// <param name="refusalReason">The refusal reason recorded by the manager, if any.</param>
    /// <returns>A <see cref="ReapplyPresentation"/> with message, title, icon, and close behavior.</returns>
    public static ReapplyPresentation GetPresentation(DxvkActionResult result, string? refusalReason)
    {
        switch (result)
        {
            case DxvkActionResult.Applied:
                return new ReapplyPresentation(
                    AppliedMessage,
                    AppliedTitle,
                    MessageBoxIcon.Information,
                    ShouldCloseWindow: true);

            case DxvkActionResult.Queued:
                return new ReapplyPresentation(
                    QueuedMessage,
                    QueuedTitle,
                    MessageBoxIcon.Information,
                    ShouldCloseWindow: true);

            case DxvkActionResult.Failed:
            default:
                string reason = !string.IsNullOrWhiteSpace(refusalReason)
                    ? refusalReason
                    : DefaultFailedReason;
                return new ReapplyPresentation(
                    $"{FailedMessagePrefix}{reason}",
                    FailedTitle,
                    MessageBoxIcon.Warning,
                    ShouldCloseWindow: false);
        }
    }
}
