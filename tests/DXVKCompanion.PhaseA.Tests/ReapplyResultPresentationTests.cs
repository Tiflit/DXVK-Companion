using System.Windows.Forms;
using DXVKCompanion.DXVK;
using DXVKCompanion.UI;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests;

public sealed class ReapplyResultPresentationTests
{
    [Fact]
    public void GetPresentation_WhenApplied_ReturnsSuccessExplanationInformationIconAndClosesWindow()
    {
        var presentation = ReapplyResultPresentation.GetPresentation(DxvkActionResult.Applied, null);

        Assert.Equal("Reapply", presentation.Title);
        Assert.Equal("DXVK reapplied successfully and baseline updated.", presentation.Message);
        Assert.Equal(MessageBoxIcon.Information, presentation.Icon);
        Assert.True(presentation.ShouldCloseWindow);
    }

    [Fact]
    public void GetPresentation_WhenApplied_WithStaleRefusalReason_DoesNotLeakRefusalReason()
    {
        const string staleReason = "Stale failure reason from previous operation";
        var presentation = ReapplyResultPresentation.GetPresentation(DxvkActionResult.Applied, staleReason);

        Assert.Equal("Reapply", presentation.Title);
        Assert.Equal("DXVK reapplied successfully and baseline updated.", presentation.Message);
        Assert.DoesNotContain(staleReason, presentation.Message);
        Assert.DoesNotContain(staleReason, presentation.Title);
        Assert.Equal(MessageBoxIcon.Information, presentation.Icon);
        Assert.True(presentation.ShouldCloseWindow);
    }

    [Fact]
    public void GetPresentation_WhenQueued_ReturnsInformationalAcknowledgementAndClosesWindow()
    {
        var presentation = ReapplyResultPresentation.GetPresentation(DxvkActionResult.Queued, null);

        Assert.Equal("Reapply Queued", presentation.Title);
        Assert.Equal("DXVK reapply is queued and will be applied when the game exits.", presentation.Message);
        Assert.Equal(MessageBoxIcon.Information, presentation.Icon);
        Assert.True(presentation.ShouldCloseWindow);

        // Verify it does not claim completion or baseline changes
        Assert.DoesNotContain("reapplied", presentation.Message);
        Assert.DoesNotContain("baseline updated", presentation.Message);

        // Verify it does not describe queueing as refusal or failure
        Assert.DoesNotContain("refused", presentation.Message);
        Assert.DoesNotContain("failed", presentation.Message);
        Assert.DoesNotContain("refused", presentation.Title);
        Assert.DoesNotContain("failed", presentation.Title);
    }

    [Fact]
    public void GetPresentation_WhenQueued_WithStaleRefusalReason_DoesNotLeakRefusalReason()
    {
        const string staleReason = "Stale anti-cheat rejection message";
        var presentation = ReapplyResultPresentation.GetPresentation(DxvkActionResult.Queued, staleReason);

        Assert.Equal("Reapply Queued", presentation.Title);
        Assert.Equal("DXVK reapply is queued and will be applied when the game exits.", presentation.Message);
        Assert.DoesNotContain(staleReason, presentation.Message);
        Assert.DoesNotContain(staleReason, presentation.Title);
        Assert.Equal(MessageBoxIcon.Information, presentation.Icon);
        Assert.True(presentation.ShouldCloseWindow);
    }

    [Fact]
    public void GetPresentation_WhenFailed_WithNonBlankReason_ReturnsWarningWithActualReasonAndKeepsWindowOpen()
    {
        const string reason = "Target installation directory is read-only or in conflict.";
        var presentation = ReapplyResultPresentation.GetPresentation(DxvkActionResult.Failed, reason);

        Assert.Equal("Reapply Refused", presentation.Title);
        Assert.Equal($"DXVK reapply was refused or failed:\n\n{reason}", presentation.Message);
        Assert.Contains(reason, presentation.Message);
        Assert.Equal(MessageBoxIcon.Warning, presentation.Icon);
        Assert.False(presentation.ShouldCloseWindow);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetPresentation_WhenFailed_WithMissingOrBlankReason_UsesGenericFallback(string? blankReason)
    {
        var presentation = ReapplyResultPresentation.GetPresentation(DxvkActionResult.Failed, blankReason);

        Assert.Equal("Reapply Refused", presentation.Title);
        Assert.Equal("DXVK reapply was refused or failed:\n\nReapply operation failed.", presentation.Message);
        Assert.Equal(MessageBoxIcon.Warning, presentation.Icon);
        Assert.False(presentation.ShouldCloseWindow);
    }

    [Theory]
    [InlineData((DxvkActionResult)99, null)]
    [InlineData((DxvkActionResult)(-1), "Unexpected internal code")]
    public void GetPresentation_WhenUnknownResult_DoesNotClaimSuccessOrQueueingAndReturnsWarning(DxvkActionResult unknownResult, string? reason)
    {
        var presentation = ReapplyResultPresentation.GetPresentation(unknownResult, reason);

        Assert.Equal("Reapply Refused", presentation.Title);
        Assert.Equal(MessageBoxIcon.Warning, presentation.Icon);
        Assert.False(presentation.ShouldCloseWindow);

        // Must not claim success or queueing
        Assert.DoesNotContain("reapplied successfully", presentation.Message);
        Assert.DoesNotContain("baseline updated", presentation.Message);
        Assert.DoesNotContain("queued", presentation.Message);

        string expectedReason = !string.IsNullOrWhiteSpace(reason) ? reason : "Reapply operation failed.";
        Assert.Equal($"DXVK reapply was refused or failed:\n\n{expectedReason}", presentation.Message);
    }
}
