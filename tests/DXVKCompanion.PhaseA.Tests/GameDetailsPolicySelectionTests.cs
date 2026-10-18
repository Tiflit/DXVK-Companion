using System.Collections.Generic;
using DXVKCompanion.Models;
using DXVKCompanion.UI;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests;

public sealed class GameDetailsPolicySelectionTests
{
    [Fact]
    public void GetAvailableLabels_WhenExistingPolicyIsPinned_ReturnsFourLabelsWithKeepPinned()
    {
        var pinnedPolicy = ManagementPolicy.PinVersion("2.3");

        var labels = GameDetailsPolicySelection.GetAvailableLabels(pinnedPolicy);

        Assert.Equal(4, labels.Count);
        Assert.Equal(GameDetailsPolicySelection.UseGlobalLabel, labels[0]);
        Assert.Equal(GameDetailsPolicySelection.AutomaticLabel, labels[1]);
        Assert.Equal(GameDetailsPolicySelection.DisabledLabel, labels[2]);
        Assert.Equal(GameDetailsPolicySelection.KeepPinnedLabel, labels[3]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ManagementMode.UseGlobal)]
    [InlineData(ManagementMode.Automatic)]
    [InlineData(ManagementMode.Disabled)]
    public void GetAvailableLabels_WhenExistingPolicyIsNotPinnedOrNull_ReturnsThreeStandardLabels(ManagementMode? mode)
    {
        ManagementPolicy? policy = mode switch
        {
            ManagementMode.UseGlobal => ManagementPolicy.UseGlobal(),
            ManagementMode.Automatic => ManagementPolicy.Automatic(),
            ManagementMode.Disabled => ManagementPolicy.Disabled(),
            _ => null
        };

        var labels = GameDetailsPolicySelection.GetAvailableLabels(policy);

        Assert.Equal(3, labels.Count);
        Assert.Equal(GameDetailsPolicySelection.UseGlobalLabel, labels[0]);
        Assert.Equal(GameDetailsPolicySelection.AutomaticLabel, labels[1]);
        Assert.Equal(GameDetailsPolicySelection.DisabledLabel, labels[2]);
        Assert.DoesNotContain(GameDetailsPolicySelection.KeepPinnedLabel, labels);
    }

    [Fact]
    public void GetInitialSelectedIndex_MapsAllFourNamedModesAndNull()
    {
        Assert.Equal(0, GameDetailsPolicySelection.GetInitialSelectedIndex(null));
        Assert.Equal(0, GameDetailsPolicySelection.GetInitialSelectedIndex(ManagementPolicy.UseGlobal()));
        Assert.Equal(1, GameDetailsPolicySelection.GetInitialSelectedIndex(ManagementPolicy.Automatic()));
        Assert.Equal(2, GameDetailsPolicySelection.GetInitialSelectedIndex(ManagementPolicy.Disabled()));
        Assert.Equal(3, GameDetailsPolicySelection.GetInitialSelectedIndex(ManagementPolicy.PinVersion("2.3")));

        var unmappedMode = new ManagementPolicy { Mode = (ManagementMode)99 };
        Assert.Equal(0, GameDetailsPolicySelection.GetInitialSelectedIndex(unmappedMode));
    }

    [Fact]
    public void ResolvePolicy_WhenExistingPolicyIsPinned_AndUnchangedSelection_PreservesExactPolicyAndValidVersion()
    {
        var policy = ManagementPolicy.PinVersion("2.3");

        var resultByIndex = GameDetailsPolicySelection.ResolvePolicy(3, policy);
        Assert.Same(policy, resultByIndex);
        Assert.Equal(ManagementMode.PinnedVersion, resultByIndex.Mode);
        Assert.Equal("2.3", resultByIndex.PinnedDxvkVersion);

        var resultByLabel = GameDetailsPolicySelection.ResolvePolicy(GameDetailsPolicySelection.KeepPinnedLabel, policy);
        Assert.Same(policy, resultByLabel);
        Assert.Equal(ManagementMode.PinnedVersion, resultByLabel.Mode);
        Assert.Equal("2.3", resultByLabel.PinnedDxvkVersion);
    }

    [Fact]
    public void ResolvePolicy_WhenExistingPolicyIsPinned_AndUnchangedSelection_PreservesNullOrMalformedVersion()
    {
        var nullVersionPolicy = new ManagementPolicy
        {
            Mode = ManagementMode.PinnedVersion,
            PinnedDxvkVersion = null
        };

        var resultNullByIndex = GameDetailsPolicySelection.ResolvePolicy(3, nullVersionPolicy);
        Assert.Same(nullVersionPolicy, resultNullByIndex);
        Assert.Null(resultNullByIndex.PinnedDxvkVersion);

        var resultNullByLabel = GameDetailsPolicySelection.ResolvePolicy(GameDetailsPolicySelection.KeepPinnedLabel, nullVersionPolicy);
        Assert.Same(nullVersionPolicy, resultNullByLabel);
        Assert.Null(resultNullByLabel.PinnedDxvkVersion);

        var malformedPolicy = new ManagementPolicy
        {
            Mode = ManagementMode.PinnedVersion,
            PinnedDxvkVersion = "invalid/version#$! 9.9.9-beta"
        };

        var resultMalformedByIndex = GameDetailsPolicySelection.ResolvePolicy(3, malformedPolicy);
        Assert.Same(malformedPolicy, resultMalformedByIndex);
        Assert.Equal("invalid/version#$! 9.9.9-beta", resultMalformedByIndex.PinnedDxvkVersion);

        var resultMalformedByLabel = GameDetailsPolicySelection.ResolvePolicy(GameDetailsPolicySelection.KeepPinnedLabel, malformedPolicy);
        Assert.Same(malformedPolicy, resultMalformedByLabel);
        Assert.Equal("invalid/version#$! 9.9.9-beta", resultMalformedByLabel.PinnedDxvkVersion);
    }

    [Fact]
    public void ResolvePolicy_WhenExistingPolicyIsPinned_AndExplicitlySwitchedToAlternatives_UsesExistingFactories()
    {
        var pinned = ManagementPolicy.PinVersion("2.3");

        // Switch to UseGlobal
        var toGlobalByIndex = GameDetailsPolicySelection.ResolvePolicy(0, pinned);
        Assert.NotSame(pinned, toGlobalByIndex);
        Assert.Equal(ManagementMode.UseGlobal, toGlobalByIndex.Mode);
        Assert.Null(toGlobalByIndex.PinnedDxvkVersion);

        var toGlobalByLabel = GameDetailsPolicySelection.ResolvePolicy(GameDetailsPolicySelection.UseGlobalLabel, pinned);
        Assert.NotSame(pinned, toGlobalByLabel);
        Assert.Equal(ManagementMode.UseGlobal, toGlobalByLabel.Mode);
        Assert.Null(toGlobalByLabel.PinnedDxvkVersion);

        // Switch to Automatic
        var toAutoByIndex = GameDetailsPolicySelection.ResolvePolicy(1, pinned);
        Assert.NotSame(pinned, toAutoByIndex);
        Assert.Equal(ManagementMode.Automatic, toAutoByIndex.Mode);
        Assert.Null(toAutoByIndex.PinnedDxvkVersion);

        var toAutoByLabel = GameDetailsPolicySelection.ResolvePolicy(GameDetailsPolicySelection.AutomaticLabel, pinned);
        Assert.NotSame(pinned, toAutoByLabel);
        Assert.Equal(ManagementMode.Automatic, toAutoByLabel.Mode);
        Assert.Null(toAutoByLabel.PinnedDxvkVersion);

        // Switch to Disabled
        var toDisabledByIndex = GameDetailsPolicySelection.ResolvePolicy(2, pinned);
        Assert.NotSame(pinned, toDisabledByIndex);
        Assert.Equal(ManagementMode.Disabled, toDisabledByIndex.Mode);
        Assert.Null(toDisabledByIndex.PinnedDxvkVersion);

        var toDisabledByLabel = GameDetailsPolicySelection.ResolvePolicy(GameDetailsPolicySelection.DisabledLabel, pinned);
        Assert.NotSame(pinned, toDisabledByLabel);
        Assert.Equal(ManagementMode.Disabled, toDisabledByLabel.Mode);
        Assert.Null(toDisabledByLabel.PinnedDxvkVersion);
    }

    [Fact]
    public void ResolvePolicy_StandardNonPinnedMappings_ReturnsExpectedPolicies()
    {
        var useGlobal = ManagementPolicy.UseGlobal();
        var automatic = ManagementPolicy.Automatic();
        var disabled = ManagementPolicy.Disabled();

        // From UseGlobal
        var autoFromGlobal = GameDetailsPolicySelection.ResolvePolicy(1, useGlobal);
        Assert.Equal(ManagementMode.Automatic, autoFromGlobal.Mode);
        var disabledFromGlobal = GameDetailsPolicySelection.ResolvePolicy(2, useGlobal);
        Assert.Equal(ManagementMode.Disabled, disabledFromGlobal.Mode);

        // From Automatic
        var globalFromAuto = GameDetailsPolicySelection.ResolvePolicy(0, automatic);
        Assert.Equal(ManagementMode.UseGlobal, globalFromAuto.Mode);
        var disabledFromAuto = GameDetailsPolicySelection.ResolvePolicy(2, automatic);
        Assert.Equal(ManagementMode.Disabled, disabledFromAuto.Mode);

        // From Disabled
        var globalFromDisabled = GameDetailsPolicySelection.ResolvePolicy(0, disabled);
        Assert.Equal(ManagementMode.UseGlobal, globalFromDisabled.Mode);
        var autoFromDisabled = GameDetailsPolicySelection.ResolvePolicy(1, disabled);
        Assert.Equal(ManagementMode.Automatic, autoFromDisabled.Mode);

        // From null
        Assert.Equal(ManagementMode.UseGlobal, GameDetailsPolicySelection.ResolvePolicy(0, null)?.Mode);
        Assert.Equal(ManagementMode.Automatic, GameDetailsPolicySelection.ResolvePolicy(1, null)?.Mode);
        Assert.Equal(ManagementMode.Disabled, GameDetailsPolicySelection.ResolvePolicy(2, null)?.Mode);
    }

    [Fact]
    public void ResolvePolicy_WhenSelectionIsInvalid_PreservesExistingPolicyWithoutOverwriting()
    {
        var pinned = ManagementPolicy.PinVersion("2.3");
        var auto = ManagementPolicy.Automatic();
        var disabled = ManagementPolicy.Disabled();
        var global = ManagementPolicy.UseGlobal();

        // Out of range indices
        Assert.Same(pinned, GameDetailsPolicySelection.ResolvePolicy(-1, pinned));
        Assert.Same(pinned, GameDetailsPolicySelection.ResolvePolicy(4, pinned));
        Assert.Same(pinned, GameDetailsPolicySelection.ResolvePolicy(99, pinned));

        Assert.Same(auto, GameDetailsPolicySelection.ResolvePolicy(-1, auto));
        Assert.Same(auto, GameDetailsPolicySelection.ResolvePolicy(99, auto));

        Assert.Same(disabled, GameDetailsPolicySelection.ResolvePolicy(-1, disabled));
        Assert.Same(global, GameDetailsPolicySelection.ResolvePolicy(-1, global));

        // Index 3 is invalid when policy is not pinned
        Assert.Same(auto, GameDetailsPolicySelection.ResolvePolicy(3, auto));
        Assert.Same(disabled, GameDetailsPolicySelection.ResolvePolicy(3, disabled));
        Assert.Same(global, GameDetailsPolicySelection.ResolvePolicy(3, global));

        // Label matching invalid cases
        Assert.Same(auto, GameDetailsPolicySelection.ResolvePolicy(GameDetailsPolicySelection.KeepPinnedLabel, auto));
        Assert.Same(pinned, GameDetailsPolicySelection.ResolvePolicy("NonExistentLabel", pinned));
        Assert.Same(pinned, GameDetailsPolicySelection.ResolvePolicy(string.Empty, pinned));
        Assert.Same(pinned, GameDetailsPolicySelection.ResolvePolicy(null, pinned));

        // Null existing policy with invalid selection returns null
        Assert.Null(GameDetailsPolicySelection.ResolvePolicy(-1, null));
        Assert.Null(GameDetailsPolicySelection.ResolvePolicy(3, null));
        Assert.Null(GameDetailsPolicySelection.ResolvePolicy("Invalid", null));
    }

    [Fact]
    public void Operations_DoNotMutateInputPolicy()
    {
        var policy = ManagementPolicy.PinVersion("2.3.1");

        _ = GameDetailsPolicySelection.GetAvailableLabels(policy);
        _ = GameDetailsPolicySelection.GetInitialSelectedIndex(policy);

        _ = GameDetailsPolicySelection.ResolvePolicy(0, policy);
        _ = GameDetailsPolicySelection.ResolvePolicy(1, policy);
        _ = GameDetailsPolicySelection.ResolvePolicy(2, policy);
        _ = GameDetailsPolicySelection.ResolvePolicy(3, policy);
        _ = GameDetailsPolicySelection.ResolvePolicy(-1, policy);
        _ = GameDetailsPolicySelection.ResolvePolicy(99, policy);

        _ = GameDetailsPolicySelection.ResolvePolicy(GameDetailsPolicySelection.UseGlobalLabel, policy);
        _ = GameDetailsPolicySelection.ResolvePolicy(GameDetailsPolicySelection.KeepPinnedLabel, policy);
        _ = GameDetailsPolicySelection.ResolvePolicy("UnknownLabel", policy);

        Assert.Equal(ManagementMode.PinnedVersion, policy.Mode);
        Assert.Equal("2.3.1", policy.PinnedDxvkVersion);
    }

    [Fact]
    public void LegacyPinnedMode_PreservesAutomaticEligibility_AndKeepsPin()
    {
        var legacyPolicy = ManagementPolicy.PinVersion("2.3");
        Assert.Equal(ManagementMode.PinnedVersion, legacyPolicy.Mode);
        Assert.Equal("2.3", legacyPolicy.PinnedDxvkVersion);
        Assert.True(legacyPolicy.IsAutomated(GlobalManagementPolicy.Manual));
        Assert.True(legacyPolicy.IsAutomated(GlobalManagementPolicy.Automated));

        var resolved = GameDetailsPolicySelection.ResolvePolicy(3, legacyPolicy);
        Assert.Same(legacyPolicy, resolved);
        Assert.True(resolved.IsAutomated(GlobalManagementPolicy.Manual));
    }
}
