using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests;

public class DxvkConfigManagerTests
{
    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;

    [Theory]
    [InlineData("dxvk.hudExtra = hud-sentinel")]
    [InlineData("dxvk.maxFrameRateExtra = frame-sentinel")]
    [InlineData("dxvk.hud.extra = dotted-sentinel")]
    [InlineData("dxvk.maxFrameRate.extra = dotted-sentinel")]
    [InlineData("dxvk.hud")]
    [InlineData("dxvk.maxFrameRate")]
    [InlineData("  dxvk.hud  ")]
    [InlineData("# dxvk.hud = comment-sentinel")]
    [InlineData("; dxvk.maxFrameRate = comment-sentinel")]
    public void GenerateConfigContent_PreservesUnmanagedLinesAndOrder(string preservedLine)
    {
        var config = new DxvkConfiguration
        {
            HudEnabled = true, FrameLimitEnabled = true, FrameLimit = 144
        };
        var existing = Lines("# before", preservedLine, "dxvk.hud = compiler", "",
            "other.setting = keep=both", "dxvk.maxFrameRate = 30", "# after");

        var result = DxvkConfigManager.GenerateConfigContent(config, existing);

        Assert.Equal(Lines("# before", preservedLine, "dxvk.hud = fps,devinfo", "",
            "other.setting = keep=both", "dxvk.maxFrameRate = 144", "# after"), result);
    }

    [Theory]
    [InlineData("dxvk.hud=compiler", "dxvk.maxFrameRate=30")]
    [InlineData(" \tDXVK.HUD \t= compiler", "\tdXvK.mAxFrAmErAtE \t= 30")]
    public void GenerateConfigContent_UpdatesExactAssignmentsDespiteCaseAndWhitespace(
        string hud, string frameLimit)
    {
        var config = new DxvkConfiguration
        {
            HudEnabled = true, FrameLimitEnabled = true, FrameLimit = 144
        };

        Assert.Equal(Lines("dxvk.hud = fps,devinfo", "dxvk.maxFrameRate = 144"),
            DxvkConfigManager.GenerateConfigContent(config, Lines(hud, frameLimit)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenerateConfigContent_RemovesOnlyExactDisabledSetting(bool hudEnabled)
    {
        var config = new DxvkConfiguration
        {
            HudEnabled = hudEnabled, FrameLimitEnabled = !hudEnabled, FrameLimit = 144
        };
        var existing = Lines("dxvk.hudExtra = keep-hud", "dxvk.hud = compiler",
            "dxvk.maxFrameRateExtra = keep-frame", "dxvk.maxFrameRate = 30");
        var expected = hudEnabled
            ? Lines("dxvk.hudExtra = keep-hud", "dxvk.hud = fps,devinfo",
                "dxvk.maxFrameRateExtra = keep-frame")
            : Lines("dxvk.hudExtra = keep-hud", "dxvk.maxFrameRateExtra = keep-frame",
                "dxvk.maxFrameRate = 144");

        Assert.Equal(expected, DxvkConfigManager.GenerateConfigContent(config, existing));
    }

    [Fact]
    public void GenerateConfigContent_AppendsEnabledKeysAfterUnrelatedLines()
    {
        var config = new DxvkConfiguration
        {
            HudEnabled = true, FrameLimitEnabled = true, FrameLimit = 144
        };

        Assert.Equal(Lines("dxvk.hudExtra = keep", "dxvk.maxFrameRateExtra = keep",
                "dxvk.hud = fps,devinfo", "dxvk.maxFrameRate = 144"),
            DxvkConfigManager.GenerateConfigContent(config,
                Lines("dxvk.hudExtra = keep", "dxvk.maxFrameRateExtra = keep")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dxvk.hudExtra = keep\ndxvk.hud = compiler")]
    public void GenerateConfigContent_NoManagedFeature_ReturnsNull(string? existing)
    {
        Assert.Null(DxvkConfigManager.GenerateConfigContent(new DxvkConfiguration(), existing));
    }

    [Fact]
    public void GenerateConfigContent_PreservesExistingDuplicateExactKeyBehavior()
    {
        var config = new DxvkConfiguration { HudEnabled = true };

        Assert.Equal(Lines("dxvk.hud = fps,devinfo", "dxvk.hud = fps,devinfo"),
            DxvkConfigManager.GenerateConfigContent(config, Lines("dxvk.hud=compiler", "dxvk.hud=off")));
    }
}
