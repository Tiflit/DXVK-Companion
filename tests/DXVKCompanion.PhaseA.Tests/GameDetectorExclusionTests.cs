using System;
using System.Diagnostics;
using DXVKCompanion.Monitoring;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class GameDetectorExclusionTests
{
    [Theory]
    [InlineData("TextInputHost")]
    [InlineData("TextInputHost.exe")]
    [InlineData("textinputhost")]
    [InlineData("textinputhost.exe")]
    [InlineData("TEXTINPUTHOST")]
    [InlineData("TEXTINPUTHOST.EXE")]
    [InlineData("tExTiNpUtHoSt")]
    [InlineData("tExTiNpUtHoSt.exe")]
    [InlineData("Nahimic3")]
    [InlineData("Nahimic3.exe")]
    [InlineData("nahimic3")]
    [InlineData("nahimic3.exe")]
    [InlineData("NAHIMIC3")]
    [InlineData("NAHIMIC3.EXE")]
    [InlineData("NaHiMiC3")]
    [InlineData("NaHiMiC3.exe")]
    public void ExcludedProcessNames_ExactAndCaseInsensitive_AndWithExtension_AreExcluded(string name)
    {
        bool isGame = GameDetector.IsGameProcessName(name);
        Assert.False(isGame, $"Expected exact process name '{name}' to be excluded from new discovery.");
    }

    [Theory]
    [InlineData("TextInput")]
    [InlineData("TextInput.exe")]
    [InlineData("TextInputHostApp")]
    [InlineData("TextInputHostApp.exe")]
    [InlineData("MyTextInputHost")]
    [InlineData("MyTextInputHost.exe")]
    [InlineData("Host")]
    [InlineData("Host.exe")]
    [InlineData("TextInputHost2")]
    [InlineData("TextInputHost2.exe")]
    [InlineData("Nahimic")]
    [InlineData("Nahimic.exe")]
    [InlineData("Nahimic3Game")]
    [InlineData("Nahimic3Game.exe")]
    [InlineData("Nahimic2")]
    [InlineData("Nahimic2.exe")]
    [InlineData("NahimicVR")]
    [InlineData("NahimicVR.exe")]
    [InlineData("NahimicService")]
    [InlineData("NahimicService.exe")]
    public void SimilarProcessNames_NotExactMatches_RemainEligibleAsPositiveControls(string name)
    {
        bool isGame = GameDetector.IsGameProcessName(name);
        Assert.True(isGame, $"Expected similar-but-not-equal name '{name}' to remain eligible as positive control.");
    }

    [Theory]
    [InlineData("Game")]
    [InlineData("Game.exe")]
    [InlineData("Witcher3.exe")]
    [InlineData("Cyberpunk2077.exe")]
    [InlineData("SkyrimSE.exe")]
    [InlineData("QueueFixture.exe")]
    [InlineData("SyntheticGame.exe")]
    public void OrdinaryGameAndFixtureNames_RemainEligible(string name)
    {
        bool isGame = GameDetector.IsGameProcessName(name);
        Assert.True(isGame, $"Expected ordinary game/fixture name '{name}' to be eligible.");
    }

    [Theory]
    [InlineData("system")]
    [InlineData("svchost")]
    [InlineData("svchost.exe")]
    [InlineData("explorer.exe")]
    [InlineData("steam")]
    [InlineData("steam.exe")]
    [InlineData("epicgameslauncher.exe")]
    [InlineData("riotclientservices.exe")]
    [InlineData("applicationframehost.exe")]
    public void ExistingSystemAndLauncherExclusions_RemainExcluded(string name)
    {
        bool isGame = GameDetector.IsGameProcessName(name);
        Assert.False(isGame, $"Expected existing system/launcher '{name}' to remain excluded.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrWhitespace_ReturnsFalse(string? name)
    {
        bool isGame = GameDetector.IsGameProcessName(name);
        Assert.False(isGame);
    }

    [Fact]
    public void IsGameProcess_ProcessInstance_EvaluatesThroughShippedPredicate()
    {
        using var currentProc = Process.GetCurrentProcess();
        var detector = new GameDetector();

        // Positive control using a game name
        detector.ProcessNameResolver = _ => "Witcher3.exe";
        Assert.True(detector.IsGameProcess(currentProc));

        // Exact exclusion for TextInputHost
        detector.ProcessNameResolver = _ => "TextInputHost";
        Assert.False(detector.IsGameProcess(currentProc));

        detector.ProcessNameResolver = _ => "TextInputHost.exe";
        Assert.False(detector.IsGameProcess(currentProc));

        // Exact exclusion for Nahimic3
        detector.ProcessNameResolver = _ => "Nahimic3";
        Assert.False(detector.IsGameProcess(currentProc));

        detector.ProcessNameResolver = _ => "Nahimic3.exe";
        Assert.False(detector.IsGameProcess(currentProc));

        // Similar names remain eligible through Process instance
        detector.ProcessNameResolver = _ => "TextInputHostGame.exe";
        Assert.True(detector.IsGameProcess(currentProc));

        detector.ProcessNameResolver = _ => "Nahimic3Game.exe";
        Assert.True(detector.IsGameProcess(currentProc));
    }
}
