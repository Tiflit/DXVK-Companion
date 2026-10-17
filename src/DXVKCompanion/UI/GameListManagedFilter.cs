using DXVKCompanion.Models;

namespace DXVKCompanion.UI
{
    /// <summary>
    /// Pure predicate helper for filtering games under the "Managed / Active Only" filter.
    /// Used by the Manage Games view to determine inclusion in the managed/active list.
    /// </summary>
    public static class GameListManagedFilter
    {
        /// <summary>
        /// Determines whether a game entry matches the "Managed / Active Only" filter.
        /// A visible entry matches when:
        ///   1. Its profile is enabled (DxvkEnabled == true); OR
        ///   2. Its installation state is Managed; OR
        ///   3. Its installation is AttentionRequired with a non-blank ManagedDxvkVersion.
        /// Hidden installations never match this filter.
        /// Native and Restored entries with disabled profiles do not match.
        /// A null installation preserves existing enabled-profile behavior.
        /// A disabled or unavailable profile with no active installation does not match.
        /// </summary>
        /// <param name="installation">The tracked game installation, or null if unrecorded.</param>
        /// <param name="profile">The associated game profile, or null if unavailable.</param>
        /// <returns>True if the entry matches the filter; otherwise false.</returns>
        public static bool MatchesManagedOrActive(GameInstallation? installation, GameProfile? profile)
        {
            if (installation != null && installation.IsHidden)
            {
                return false;
            }

            if (profile != null && profile.DxvkEnabled)
            {
                return true;
            }

            if (installation != null)
            {
                if (installation.RestorationState == RestorationState.Managed)
                {
                    return true;
                }

                if (installation.RestorationState == RestorationState.AttentionRequired &&
                    !string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether a game installation matches the "Managed / Active Only" filter
        /// without an associated profile.
        /// </summary>
        /// <param name="installation">The tracked game installation, or null if unrecorded.</param>
        /// <returns>True if the installation is actively managed and visible; otherwise false.</returns>
        public static bool MatchesManagedOrActive(GameInstallation? installation)
        {
            return MatchesManagedOrActive(installation, null);
        }
    }
}
