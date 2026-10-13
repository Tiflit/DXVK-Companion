using System;
using DXVKCompanion.Models;
using DXVKCompanion.UI;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests;

public sealed class GameDetailsConfigurationSelectionTests
{
    [Fact]
    public void GetInitialDisplay_PreexistingInstallation_TakesPrecedenceOverDivergentProfile()
    {
        var installation = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example",
            Configuration = new DxvkConfiguration
            {
                HudEnabled = true,
                FrameLimit = 60,
                FrameLimitEnabled = true
            }
        };

        var profile = new GameProfile(@"C:\Games\Example\game.exe")
        {
            HudEnabled = false,
            FrameLimit = 0
        };

        var display = GameDetailsConfigurationSelection.GetInitialDisplay(
            installation,
            isPreexistingInstallation: true,
            profile);

        Assert.True(display.HudEnabled);
        Assert.Equal(60, display.FrameLimit);
    }

    [Fact]
    public void GetInitialDisplay_PreexistingInstallationWithDisabledLimiter_EmitsZeroDisplayValue()
    {
        var installation = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example",
            Configuration = new DxvkConfiguration
            {
                HudEnabled = false,
                FrameLimit = 120,
                FrameLimitEnabled = false
            }
        };

        var profile = new GameProfile(@"C:\Games\Example\game.exe")
        {
            HudEnabled = true,
            FrameLimit = 144
        };

        var display = GameDetailsConfigurationSelection.GetInitialDisplay(
            installation,
            isPreexistingInstallation: true,
            profile);

        Assert.False(display.HudEnabled);
        Assert.Equal(0, display.FrameLimit);
    }

    [Fact]
    public void GetInitialDisplay_NewlyCreatedInstallation_FallsBackToProfileValues()
    {
        var installation = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example",
            Configuration = new DxvkConfiguration
            {
                HudEnabled = false,
                FrameLimit = 120,
                FrameLimitEnabled = false
            }
        };

        var profile = new GameProfile(@"C:\Games\Example\game.exe")
        {
            HudEnabled = true,
            FrameLimit = 75
        };

        var display = GameDetailsConfigurationSelection.GetInitialDisplay(
            installation,
            isPreexistingInstallation: false,
            profile);

        Assert.True(display.HudEnabled);
        Assert.Equal(75, display.FrameLimit);
    }

    [Fact]
    public void GetInitialDisplay_AbsentInstallation_FallsBackToProfileValues()
    {
        var profile = new GameProfile(@"C:\Games\Example\game.exe")
        {
            HudEnabled = true,
            FrameLimit = 144
        };

        var display = GameDetailsConfigurationSelection.GetInitialDisplay(
            installation: null,
            isPreexistingInstallation: false,
            profile);

        Assert.True(display.HudEnabled);
        Assert.Equal(144, display.FrameLimit);
    }

    [Theory]
    [InlineData(1500, 1000)]
    [InlineData(-25, 0)]
    [InlineData(0, 0)]
    [InlineData(1000, 1000)]
    public void GetInitialDisplay_ClampsNumericDisplay_WithinControlBounds(int rawFrameLimit, int expectedDisplayLimit)
    {
        var installation = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example",
            Configuration = new DxvkConfiguration
            {
                HudEnabled = false,
                FrameLimit = rawFrameLimit,
                FrameLimitEnabled = true
            }
        };

        var profile = new GameProfile(@"C:\Games\Example\game.exe")
        {
            HudEnabled = false,
            FrameLimit = rawFrameLimit
        };

        var preexistingDisplay = GameDetailsConfigurationSelection.GetInitialDisplay(
            installation,
            isPreexistingInstallation: true,
            profile);

        var fallbackDisplay = GameDetailsConfigurationSelection.GetInitialDisplay(
            installation: null,
            isPreexistingInstallation: false,
            profile);

        Assert.Equal(expectedDisplayLimit, preexistingDisplay.FrameLimit);
        Assert.Equal(expectedDisplayLimit, fallbackDisplay.FrameLimit);
    }

    [Fact]
    public void GetInitialDisplay_ThrowsArgumentNullException_WhenProfileIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            GameDetailsConfigurationSelection.GetInitialDisplay(null, false, null!));
    }

    [Fact]
    public void ApplyConfiguration_PreexistingUnchangedSave_PreservesBothInstallationFieldsIndividually()
    {
        var installation = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example",
            Configuration = new DxvkConfiguration
            {
                HudEnabled = true,
                FrameLimit = 120,
                FrameLimitEnabled = false
            }
        };

        var profile = new GameProfile(@"C:\Games\Example\game.exe")
        {
            HudEnabled = false,
            FrameLimit = 0
        };

        var baseline = new InitialConfigurationDisplay(HudEnabled: true, FrameLimit: 0);

        GameDetailsConfigurationSelection.ApplyConfiguration(
            finalHud: true,
            finalFrameLimit: 0,
            baseline,
            installation,
            isPreexistingInstallation: true,
            profile);

        Assert.True(installation.Configuration.HudEnabled);
        Assert.Equal(120, installation.Configuration.FrameLimit);
        Assert.False(installation.Configuration.FrameLimitEnabled);
        Assert.False(profile.HudEnabled);
        Assert.Equal(0, profile.FrameLimit);
    }

    [Fact]
    public void ApplyConfiguration_PreexistingUnchangedSave_PreservesOutOfRangePersistedValueWithoutNormalizing()
    {
        var installation = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example",
            Configuration = new DxvkConfiguration
            {
                HudEnabled = false,
                FrameLimit = 2500,
                FrameLimitEnabled = true
            }
        };

        var profile = new GameProfile(@"C:\Games\Example\game.exe");
        var baseline = new InitialConfigurationDisplay(HudEnabled: false, FrameLimit: 1000);

        GameDetailsConfigurationSelection.ApplyConfiguration(
            finalHud: false,
            finalFrameLimit: 1000,
            baseline,
            installation,
            isPreexistingInstallation: true,
            profile);

        Assert.Equal(2500, installation.Configuration.FrameLimit);
        Assert.True(installation.Configuration.FrameLimitEnabled);
    }

    [Fact]
    public void ApplyConfiguration_Preexisting_HudOnlyEdit_UpdatesHudAndPreservesLimiter()
    {
        var installation = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example",
            Configuration = new DxvkConfiguration
            {
                HudEnabled = false,
                FrameLimit = 120,
                FrameLimitEnabled = false
            }
        };

        var profile = new GameProfile(@"C:\Games\Example\game.exe")
        {
            HudEnabled = false,
            FrameLimit = 0
        };

        var baseline = new InitialConfigurationDisplay(HudEnabled: false, FrameLimit: 0);

        GameDetailsConfigurationSelection.ApplyConfiguration(
            finalHud: true,
            finalFrameLimit: 0,
            baseline,
            installation,
            isPreexistingInstallation: true,
            profile);

        Assert.True(installation.Configuration.HudEnabled);
        Assert.True(profile.HudEnabled);
        Assert.Equal(120, installation.Configuration.FrameLimit);
        Assert.False(installation.Configuration.FrameLimitEnabled);
        Assert.Equal(0, profile.FrameLimit);
    }

    [Fact]
    public void ApplyConfiguration_Preexisting_LimiterOnlyEdit_UpdatesLimiterAndPreservesHud()
    {
        var installation = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example",
            Configuration = new DxvkConfiguration
            {
                HudEnabled = true,
                FrameLimit = 120,
                FrameLimitEnabled = false
            }
        };

        var profile = new GameProfile(@"C:\Games\Example\game.exe")
        {
            HudEnabled = false,
            FrameLimit = 0
        };

        var baseline = new InitialConfigurationDisplay(HudEnabled: true, FrameLimit: 0);

        GameDetailsConfigurationSelection.ApplyConfiguration(
            finalHud: true,
            finalFrameLimit: 60,
            baseline,
            installation,
            isPreexistingInstallation: true,
            profile);

        Assert.True(installation.Configuration.HudEnabled);
        Assert.False(profile.HudEnabled);
        Assert.Equal(60, installation.Configuration.FrameLimit);
        Assert.True(installation.Configuration.FrameLimitEnabled);
        Assert.Equal(60, profile.FrameLimit);
    }

    [Fact]
    public void ApplyConfiguration_Preexisting_ExplicitEnableAndDisable()
    {
        var installation = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example",
            Configuration = new DxvkConfiguration
            {
                HudEnabled = false,
                FrameLimit = 120,
                FrameLimitEnabled = false
            }
        };

        var profile = new GameProfile(@"C:\Games\Example\game.exe")
        {
            HudEnabled = false,
            FrameLimit = 0
        };

        // Explicit enable: 0 -> 60
        var baselineDisabled = new InitialConfigurationDisplay(HudEnabled: false, FrameLimit: 0);
        GameDetailsConfigurationSelection.ApplyConfiguration(
            finalHud: false,
            finalFrameLimit: 60,
            baselineDisabled,
            installation,
            isPreexistingInstallation: true,
            profile);

        Assert.Equal(60, installation.Configuration.FrameLimit);
        Assert.True(installation.Configuration.FrameLimitEnabled);
        Assert.Equal(60, profile.FrameLimit);

        // Explicit disable: 60 -> 0
        var baselineEnabled = new InitialConfigurationDisplay(HudEnabled: false, FrameLimit: 60);
        GameDetailsConfigurationSelection.ApplyConfiguration(
            finalHud: false,
            finalFrameLimit: 0,
            baselineEnabled,
            installation,
            isPreexistingInstallation: true,
            profile);

        Assert.Equal(0, installation.Configuration.FrameLimit);
        Assert.False(installation.Configuration.FrameLimitEnabled);
        Assert.Equal(0, profile.FrameLimit);
    }

    [Fact]
    public void ApplyConfiguration_NewlyCreatedInstallation_InitializesInstallationFromProfileOrEdits()
    {
        // Case 1: Unchanged save with profile limit enabled
        var installation1 = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example",
            Configuration = new DxvkConfiguration()
        };
        var profile1 = new GameProfile(@"C:\Games\Example\game.exe")
        {
            HudEnabled = true,
            FrameLimit = 90
        };
        var baseline1 = new InitialConfigurationDisplay(HudEnabled: true, FrameLimit: 90);

        GameDetailsConfigurationSelection.ApplyConfiguration(
            finalHud: true,
            finalFrameLimit: 90,
            baseline1,
            installation1,
            isPreexistingInstallation: false,
            profile1);

        Assert.True(installation1.Configuration.HudEnabled);
        Assert.Equal(90, installation1.Configuration.FrameLimit);
        Assert.True(installation1.Configuration.FrameLimitEnabled);

        // Case 2: Unchanged save with profile limit disabled (0)
        var installation2 = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example",
            Configuration = new DxvkConfiguration() // FrameLimit = 120, FrameLimitEnabled = false
        };
        var profile2 = new GameProfile(@"C:\Games\Example\game.exe")
        {
            HudEnabled = false,
            FrameLimit = 0
        };
        var baseline2 = new InitialConfigurationDisplay(HudEnabled: false, FrameLimit: 0);

        GameDetailsConfigurationSelection.ApplyConfiguration(
            finalHud: false,
            finalFrameLimit: 0,
            baseline2,
            installation2,
            isPreexistingInstallation: false,
            profile2);

        Assert.False(installation2.Configuration.HudEnabled);
        Assert.Equal(120, installation2.Configuration.FrameLimit);
        Assert.False(installation2.Configuration.FrameLimitEnabled);

        // Case 3: Explicit edit on newly created installation
        var installation3 = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example",
            Configuration = new DxvkConfiguration()
        };
        var profile3 = new GameProfile(@"C:\Games\Example\game.exe")
        {
            HudEnabled = false,
            FrameLimit = 0
        };
        var baseline3 = new InitialConfigurationDisplay(HudEnabled: false, FrameLimit: 0);

        GameDetailsConfigurationSelection.ApplyConfiguration(
            finalHud: true,
            finalFrameLimit: 144,
            baseline3,
            installation3,
            isPreexistingInstallation: false,
            profile3);

        Assert.True(installation3.Configuration.HudEnabled);
        Assert.True(profile3.HudEnabled);
        Assert.Equal(144, installation3.Configuration.FrameLimit);
        Assert.True(installation3.Configuration.FrameLimitEnabled);
        Assert.Equal(144, profile3.FrameLimit);
    }

    [Fact]
    public void ApplyConfiguration_AbsentInstallation_UpdatesProfileOnlyOnEdits()
    {
        var profile = new GameProfile(@"C:\Games\Example\game.exe")
        {
            HudEnabled = false,
            FrameLimit = 0
        };
        var baseline = new InitialConfigurationDisplay(HudEnabled: false, FrameLimit: 0);

        // Unchanged
        GameDetailsConfigurationSelection.ApplyConfiguration(
            finalHud: false,
            finalFrameLimit: 0,
            baseline,
            installation: null,
            isPreexistingInstallation: false,
            profile);

        Assert.False(profile.HudEnabled);
        Assert.Equal(0, profile.FrameLimit);

        // HUD edit
        GameDetailsConfigurationSelection.ApplyConfiguration(
            finalHud: true,
            finalFrameLimit: 0,
            baseline,
            installation: null,
            isPreexistingInstallation: false,
            profile);

        Assert.True(profile.HudEnabled);

        // Limiter edit
        GameDetailsConfigurationSelection.ApplyConfiguration(
            finalHud: true,
            finalFrameLimit: 120,
            baseline,
            installation: null,
            isPreexistingInstallation: false,
            profile);

        Assert.Equal(120, profile.FrameLimit);
    }

    [Fact]
    public void ApplyConfiguration_ThrowsArgumentNullException_WhenProfileIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            GameDetailsConfigurationSelection.ApplyConfiguration(
                false, 0, default, null, false, null!));
    }
}
