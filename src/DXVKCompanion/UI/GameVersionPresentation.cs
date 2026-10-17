using DXVKCompanion.Models;

namespace DXVKCompanion.UI
{
    /// <summary>
    /// Pure presentation helper for distinguishing recorded installation management
    /// versions from historical profile versions in the UI.
    /// </summary>
    public static class GameVersionPresentation
    {
        public const string Unavailable = "Unavailable";
        public const string NoneRecorded = "None recorded";

        /// <summary>
        /// Gets the display text for the recorded managed DXVK version of an installation.
        /// Returns "Unavailable" if no installation record is available (or was not preexisting).
        /// Returns "None recorded" if an installation record exists but has no non-blank managed version.
        /// Otherwise returns the raw recorded ManagedDxvkVersion.
        /// </summary>
        /// <param name="installation">The installation record, if available.</param>
        /// <param name="isPreexisting">True if the installation record preexisted rather than being created on-the-fly for an unrecorded game.</param>
        /// <returns>The recorded managed version display text.</returns>
        public static string GetRecordedManagedVersion(GameInstallation? installation, bool isPreexisting = true)
        {
            if (installation == null || !isPreexisting)
            {
                return Unavailable;
            }

            if (string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion))
            {
                return NoneRecorded;
            }

            return installation.ManagedDxvkVersion;
        }

        /// <summary>
        /// Gets the historical profile version if non-blank; otherwise returns null.
        /// </summary>
        /// <param name="profile">The game profile, if available.</param>
        /// <returns>The historical profile version if present; otherwise null.</returns>
        public static string? GetHistoricalProfileVersion(GameProfile? profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.DxvkVersion))
            {
                return null;
            }

            return profile.DxvkVersion;
        }

        /// <summary>
        /// Formats the label text for the historical profile version in Game Details.
        /// </summary>
        /// <param name="profile">The game profile.</param>
        /// <returns>The formatted label text if the profile version is non-blank; otherwise null.</returns>
        public static string? FormatHistoricalProfileVersionLabel(GameProfile? profile)
        {
            var version = GetHistoricalProfileVersion(profile);
            return version != null ? $"Profile version (last recorded): {version}" : null;
        }
    }
}
