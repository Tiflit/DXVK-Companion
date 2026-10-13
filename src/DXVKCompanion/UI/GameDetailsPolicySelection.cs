using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using DXVKCompanion.Models;

namespace DXVKCompanion.UI;

public static class GameDetailsPolicySelection
{
    public const string UseGlobalLabel = "Use Global Policy";
    public const string AutomaticLabel = "Automatic Management";
    public const string DisabledLabel = "Disabled";
    public const string KeepPinnedLabel = "Keep existing pinned version";

    public static IReadOnlyList<string> GetAvailableLabels(ManagementPolicy? existingPolicy)
    {
        if (existingPolicy?.Mode == ManagementMode.PinnedVersion)
        {
            return new[]
            {
                UseGlobalLabel,
                AutomaticLabel,
                DisabledLabel,
                KeepPinnedLabel
            };
        }

        return new[]
        {
            UseGlobalLabel,
            AutomaticLabel,
            DisabledLabel
        };
    }

    public static int GetInitialSelectedIndex(ManagementPolicy? existingPolicy)
    {
        if (existingPolicy == null)
        {
            return 0;
        }

        return existingPolicy.Mode switch
        {
            ManagementMode.Automatic => 1,
            ManagementMode.Disabled => 2,
            ManagementMode.PinnedVersion => 3,
            _ => 0
        };
    }

    [return: NotNullIfNotNull(nameof(existingPolicy))]
    public static ManagementPolicy? ResolvePolicy(int selectedIndex, ManagementPolicy? existingPolicy)
    {
        return selectedIndex switch
        {
            0 => ManagementPolicy.UseGlobal(),
            1 => ManagementPolicy.Automatic(),
            2 => ManagementPolicy.Disabled(),
            3 when existingPolicy?.Mode == ManagementMode.PinnedVersion => existingPolicy,
            _ => existingPolicy
        };
    }

    [return: NotNullIfNotNull(nameof(existingPolicy))]
    public static ManagementPolicy? ResolvePolicy(string? selectedLabel, ManagementPolicy? existingPolicy)
    {
        return selectedLabel switch
        {
            UseGlobalLabel => ManagementPolicy.UseGlobal(),
            AutomaticLabel => ManagementPolicy.Automatic(),
            DisabledLabel => ManagementPolicy.Disabled(),
            KeepPinnedLabel when existingPolicy?.Mode == ManagementMode.PinnedVersion => existingPolicy,
            _ => existingPolicy
        };
    }
}
