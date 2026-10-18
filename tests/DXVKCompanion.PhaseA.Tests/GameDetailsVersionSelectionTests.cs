using System;
using System.Collections.Generic;
using DXVKCompanion.Models;
using DXVKCompanion.UI;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests;

public sealed class GameDetailsVersionSelectionTests
{
    [Fact]
    public void GetAvailableLabels_WhenNoPinnedVersion_ReturnsLatestPlusAvailableVersions()
    {
        var available = new List<string> { "v2.5", "v2.4", "v2.3" };
        var labels = GameDetailsVersionSelection.GetAvailableLabels(available, currentPinnedVersion: null);

        Assert.Equal(4, labels.Count);
        Assert.Equal("Latest", labels[0]);
        Assert.Equal("v2.5", labels[1]);
        Assert.Equal("v2.4", labels[2]);
        Assert.Equal("v2.3", labels[3]);
    }

    [Fact]
    public void GetAvailableLabels_WhenPinnedVersionIsAvailable_ReturnsWithoutUnavailableSuffix()
    {
        var available = new List<string> { "v2.5", "v2.4", "v2.3" };
        var labels = GameDetailsVersionSelection.GetAvailableLabels(available, currentPinnedVersion: "v2.4");

        Assert.Equal(4, labels.Count);
        Assert.Equal("Latest", labels[0]);
        Assert.Equal("v2.5", labels[1]);
        Assert.Equal("v2.4", labels[2]);
        Assert.Equal("v2.3", labels[3]);
        Assert.DoesNotContain("v2.4 (unavailable)", labels);
    }

    [Fact]
    public void GetAvailableLabels_WhenPinnedVersionIsUnavailable_AppendsUnavailableSuffix()
    {
        var available = new List<string> { "v2.5", "v2.4" };
        var labels = GameDetailsVersionSelection.GetAvailableLabels(available, currentPinnedVersion: "v2.2");

        Assert.Equal(4, labels.Count);
        Assert.Equal("Latest", labels[0]);
        Assert.Equal("v2.5", labels[1]);
        Assert.Equal("v2.4", labels[2]);
        Assert.Equal("v2.2 (unavailable)", labels[3]);
    }

    [Fact]
    public void GetInitialSelectedIndex_MapsCorrectly()
    {
        var available = new List<string> { "v2.5", "v2.4" };
        var labels = GameDetailsVersionSelection.GetAvailableLabels(available, currentPinnedVersion: "v2.2");
        // labels: ["Latest", "v2.5", "v2.4", "v2.2 (unavailable)"]

        Assert.Equal(0, GameDetailsVersionSelection.GetInitialSelectedIndex(labels, null));
        Assert.Equal(0, GameDetailsVersionSelection.GetInitialSelectedIndex(labels, ""));
        Assert.Equal(1, GameDetailsVersionSelection.GetInitialSelectedIndex(labels, "v2.5"));
        Assert.Equal(2, GameDetailsVersionSelection.GetInitialSelectedIndex(labels, "v2.4"));
        Assert.Equal(3, GameDetailsVersionSelection.GetInitialSelectedIndex(labels, "v2.2"));
        Assert.Equal(0, GameDetailsVersionSelection.GetInitialSelectedIndex(labels, "nonexistent"));
    }

    [Fact]
    public void ResolveSelectedVersion_MapsLabelsToVersions()
    {
        var available = new List<string> { "v2.5", "v2.4" };
        var labels = GameDetailsVersionSelection.GetAvailableLabels(available, currentPinnedVersion: "v2.2");
        // labels: ["Latest", "v2.5", "v2.4", "v2.2 (unavailable)"]

        // 0 -> Latest -> null
        Assert.Null(GameDetailsVersionSelection.ResolveSelectedVersion(0, labels, "v2.2"));

        // 1 -> "v2.5"
        Assert.Equal("v2.5", GameDetailsVersionSelection.ResolveSelectedVersion(1, labels, "v2.2"));

        // 2 -> "v2.4"
        Assert.Equal("v2.4", GameDetailsVersionSelection.ResolveSelectedVersion(2, labels, "v2.2"));

        // 3 -> "v2.2 (unavailable)" -> resolves back to original pinned version "v2.2"
        Assert.Equal("v2.2", GameDetailsVersionSelection.ResolveSelectedVersion(3, labels, "v2.2"));

        // Invalid index
        Assert.Null(GameDetailsVersionSelection.ResolveSelectedVersion(-1, labels, "v2.2"));
        Assert.Null(GameDetailsVersionSelection.ResolveSelectedVersion(99, labels, "v2.2"));
    }

    [Fact]
    public void ApplyVersionSelection_PreservesModes_WhenSettingExactVersion()
    {
        // 1. Automatic mode
        var autoPolicy = ManagementPolicy.Automatic();
        GameDetailsVersionSelection.ApplyVersionSelection(autoPolicy, "v2.4");
        Assert.Equal(ManagementMode.Automatic, autoPolicy.Mode);
        Assert.Equal("v2.4", autoPolicy.PinnedDxvkVersion);

        // 2. UseGlobal mode
        var globalPolicy = ManagementPolicy.UseGlobal();
        GameDetailsVersionSelection.ApplyVersionSelection(globalPolicy, "v2.3");
        Assert.Equal(ManagementMode.UseGlobal, globalPolicy.Mode);
        Assert.Equal("v2.3", globalPolicy.PinnedDxvkVersion);

        // 3. Disabled mode
        var disabledPolicy = ManagementPolicy.Disabled();
        GameDetailsVersionSelection.ApplyVersionSelection(disabledPolicy, "v2.5");
        Assert.Equal(ManagementMode.Disabled, disabledPolicy.Mode);
        Assert.Equal("v2.5", disabledPolicy.PinnedDxvkVersion);

        // 4. Legacy PinnedVersion mode
        var legacyPolicy = ManagementPolicy.PinVersion("v2.2");
        GameDetailsVersionSelection.ApplyVersionSelection(legacyPolicy, "v2.4");
        Assert.Equal(ManagementMode.PinnedVersion, legacyPolicy.Mode);
        Assert.Equal("v2.4", legacyPolicy.PinnedDxvkVersion);
    }

    [Fact]
    public void ApplyVersionSelection_WhenClearingToLatest_ClearsPinAndNormalizesLegacyMode()
    {
        // 1. Automatic mode clearing to Latest preserves Automatic
        var autoPolicy = ManagementPolicy.Automatic();
        autoPolicy.PinnedDxvkVersion = "v2.4";
        GameDetailsVersionSelection.ApplyVersionSelection(autoPolicy, null);
        Assert.Equal(ManagementMode.Automatic, autoPolicy.Mode);
        Assert.Null(autoPolicy.PinnedDxvkVersion);

        // 2. Disabled mode clearing to Latest preserves Disabled (never turns automatic)
        var disabledPolicy = ManagementPolicy.Disabled();
        disabledPolicy.PinnedDxvkVersion = "v2.4";
        GameDetailsVersionSelection.ApplyVersionSelection(disabledPolicy, "");
        Assert.Equal(ManagementMode.Disabled, disabledPolicy.Mode);
        Assert.Null(disabledPolicy.PinnedDxvkVersion);

        // 3. UseGlobal mode clearing to Latest preserves UseGlobal
        var globalPolicy = ManagementPolicy.UseGlobal();
        globalPolicy.PinnedDxvkVersion = "v2.4";
        GameDetailsVersionSelection.ApplyVersionSelection(globalPolicy, null);
        Assert.Equal(ManagementMode.UseGlobal, globalPolicy.Mode);
        Assert.Null(globalPolicy.PinnedDxvkVersion);

        // 4. Legacy PinnedVersion mode clearing to Latest normalizes to Automatic (preserves automated eligibility)
        var legacyPolicy = ManagementPolicy.PinVersion("v2.3");
        GameDetailsVersionSelection.ApplyVersionSelection(legacyPolicy, null);
        Assert.Equal(ManagementMode.Automatic, legacyPolicy.Mode);
        Assert.Null(legacyPolicy.PinnedDxvkVersion);
    }
}
