using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using DXVKCompanion.UI;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class GameDetailsReleaseDownloadPresentationTests
{
    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    public void DownloadControl_RespectsBusyPendingAndSelection(bool busy, bool pending, bool selected, bool expected) =>
        Assert.Equal(expected, GameDetailsReleaseDownloadPresentation.CanDownload(busy, pending, selected));

    [Fact]
    public async Task DownloadStatus_ReportsIntegrityAndUnsupportedArchitecture_SelectionAndModeStayUnchanged()
    {
        using var fixture = new OfficialAcquisitionFixture();
        var policy = ManagementPolicy.Disabled();
        policy.PinnedDxvkVersion = "v2.4";
        string[] before = { "v2.4" };
        var labels = GameDetailsVersionSelection.GetAvailableLabels(before, policy.PinnedDxvkVersion);
        string? selected = GameDetailsVersionSelection.ResolveSelectedVersion(1, labels, policy.PinnedDxvkVersion);
        Assert.Equal("Available to download", GameDetailsReleaseDownloadPresentation.LocalStatus(fixture.Source, "v2.5", GraphicsApi.DX11, "x64"));
        var result = await fixture.Acquirer.AcquireAsync("v2.5");
        Assert.Contains("SHA-256 verified", GameDetailsReleaseDownloadPresentation.CompletedStatus(result));
        Assert.Contains("no upstream digest", GameDetailsReleaseDownloadPresentation.CompletedStatus(new("v2.5", true, false)));
        Assert.Contains("no acquisition manifest or upstream verification", GameDetailsReleaseDownloadPresentation.CompletedStatus(new("v2.5", true, false, false)));
        Assert.Equal("Downloaded — compatible", GameDetailsReleaseDownloadPresentation.LocalStatus(fixture.Source, "v2.5", GraphicsApi.DX11, "x64"));
        Assert.Contains("unavailable", GameDetailsReleaseDownloadPresentation.LocalStatus(fixture.Source, "v2.5", GraphicsApi.DX11, "x32"));
        Assert.Contains("unavailable", GameDetailsReleaseDownloadPresentation.LocalStatus(fixture.Source, "v2.5", GraphicsApi.DX12, "x64"));
        labels = GameDetailsVersionSelection.GetAvailableLabels(new[] { "v2.5", "v2.4" }, selected);
        Assert.Equal("v2.4", GameDetailsVersionSelection.ResolveSelectedVersion(
            GameDetailsVersionSelection.GetInitialSelectedIndex(labels, selected), labels, selected));
        Assert.Equal(ManagementMode.Disabled, policy.Mode);
        Assert.Equal("v2.4", policy.PinnedDxvkVersion);
    }
}
