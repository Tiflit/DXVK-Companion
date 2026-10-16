using DXVKCompanion.Models;

namespace DXVKCompanion.UI
{
    /// <summary>
    /// Pure predicate helper for filtering games that require user attention.
    /// Used by the Manage Games view to determine inclusion under the "Attention Required" filter.
    /// </summary>
    public static class GameListAttentionFilter
    {
        /// <summary>
        /// Determines whether a game installation matches the Attention Required filter.
        /// Unrecorded profiles (null installation) never match Attention Required.
        /// Recorded installations match if RestorationState is AttentionRequired or if ConflictFlags is non-zero.
        /// </summary>
        /// <param name="installation">The tracked game installation, or null if unrecorded.</param>
        /// <returns>True if attention is required; otherwise false.</returns>
        public static bool MatchesAttentionRequired(GameInstallation? installation)
        {
            if (installation == null)
            {
                return false;
            }

            return installation.RestorationState == RestorationState.AttentionRequired
                || installation.ConflictFlags != InstallationConflictFlags.None;
        }

        /// <summary>
        /// Determines whether a game entry matches the Attention Required filter,
        /// evaluating regardless of the profile's enabled or version state.
        /// </summary>
        /// <param name="installation">The tracked game installation, or null if unrecorded.</param>
        /// <param name="profile">The associated game profile, if available.</param>
        /// <returns>True if attention is required; otherwise false.</returns>
        public static bool MatchesAttentionRequired(GameInstallation? installation, GameProfile? profile)
        {
            return MatchesAttentionRequired(installation);
        }
    }
}
