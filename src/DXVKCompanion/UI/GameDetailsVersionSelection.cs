using System;
using System.Collections.Generic;
using DXVKCompanion.Models;

namespace DXVKCompanion.UI
{
    public static class GameDetailsVersionSelection
    {
        public const string LatestLabel = "Latest";

        public static string FormatUnavailableLabel(string version) => $"{version} (unavailable)";

        public static IReadOnlyList<string> GetAvailableLabels(
            IReadOnlyList<string>? availableVersions,
            string? currentPinnedVersion)
        {
            var labels = new List<string> { LatestLabel };

            bool currentFound = false;
            if (availableVersions != null)
            {
                foreach (var v in availableVersions)
                {
                    labels.Add(v);
                    if (!string.IsNullOrWhiteSpace(currentPinnedVersion) &&
                        string.Equals(v, currentPinnedVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        currentFound = true;
                    }
                }
            }

            // If a persisted pinned version is not among the available local versions,
            // include it with an unavailable indicator so it remains visibly selected
            // without silent reversion to Latest.
            if (!string.IsNullOrWhiteSpace(currentPinnedVersion) && !currentFound)
            {
                labels.Add(FormatUnavailableLabel(currentPinnedVersion.Trim()));
            }

            return labels;
        }

        public static int GetInitialSelectedIndex(
            IReadOnlyList<string>? labels,
            string? currentPinnedVersion)
        {
            if (string.IsNullOrWhiteSpace(currentPinnedVersion) || labels == null || labels.Count == 0)
                return 0;

            string target = currentPinnedVersion.Trim();
            string unavailableTarget = FormatUnavailableLabel(target);

            for (int i = 1; i < labels.Count; i++)
            {
                if (string.Equals(labels[i], target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(labels[i], unavailableTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return 0;
        }

        public static string? ResolveSelectedVersion(
            int selectedIndex,
            IReadOnlyList<string>? labels,
            string? currentPinnedVersion)
        {
            if (labels == null || selectedIndex <= 0 || selectedIndex >= labels.Count)
                return null; // Latest

            string selectedLabel = labels[selectedIndex];
            if (string.Equals(selectedLabel, LatestLabel, StringComparison.OrdinalIgnoreCase))
                return null;

            if (!string.IsNullOrWhiteSpace(currentPinnedVersion) &&
                string.Equals(selectedLabel, FormatUnavailableLabel(currentPinnedVersion.Trim()), StringComparison.OrdinalIgnoreCase))
            {
                return currentPinnedVersion.Trim();
            }

            return selectedLabel;
        }

        public static void ApplyVersionSelection(ManagementPolicy policy, string? selectedVersion)
        {
            if (policy == null) return;

            if (string.IsNullOrWhiteSpace(selectedVersion))
            {
                policy.PinnedDxvkVersion = null;
                // Legacy normalization: if clearing a legacy PinnedVersion mode to Latest,
                // preserve its already-automatic eligibility by setting Mode to Automatic.
                if (policy.Mode == ManagementMode.PinnedVersion)
                {
                    policy.Mode = ManagementMode.Automatic;
                }
            }
            else
            {
                policy.PinnedDxvkVersion = selectedVersion.Trim();
                // Preserves existing Mode (whether UseGlobal, Automatic, Disabled, or PinnedVersion)
            }
        }
    }
}
