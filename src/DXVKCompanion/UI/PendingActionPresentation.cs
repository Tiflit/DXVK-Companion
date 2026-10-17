using DXVKCompanion.Models;

namespace DXVKCompanion.UI
{
    /// <summary>
    /// Pure presentation helper for formatting recorded pending actions in the UI.
    /// </summary>
    public static class PendingActionPresentation
    {
        public const string Unavailable = "Unavailable";
        public const string None = "None";
        public const string Install = "Install";
        public const string Update = "Update";
        public const string Reapply = "Reapply";
        public const string Restore = "Restore";
        public const string Unknown = "Unknown";

        public const string LabelPrefix = "Recorded Pending Action: ";

        /// <summary>
        /// Gets the recorded pending action display text for an installation.
        /// Returns "Unavailable" if the installation is null or was not preexisting.
        /// Returns "None" if the installation has no pending action or if the pending action type is None.
        /// Returns "Install", "Update", "Reapply", "Restore" for recognized pending action types.
        /// Returns "Unknown" for unrecognized enum values.
        /// Historical LastCancelledAction is intentionally excluded from current pending work.
        /// </summary>
        /// <param name="installation">The installation record, if available.</param>
        /// <param name="isPreexisting">True if the installation record preexisted rather than being created on-the-fly for an unrecorded game.</param>
        /// <returns>The recorded pending action display string.</returns>
        public static string GetRecordedPendingAction(GameInstallation? installation, bool isPreexisting = true)
        {
            if (installation == null || !isPreexisting)
            {
                return Unavailable;
            }

            return FormatPendingAction(installation.PendingAction);
        }

        /// <summary>
        /// Formats a PendingAction instance into its display string.
        /// Returns "None" if the action is null.
        /// </summary>
        /// <param name="action">The pending action instance.</param>
        /// <returns>The display string.</returns>
        public static string FormatPendingAction(PendingAction? action)
        {
            if (action == null)
            {
                return None;
            }

            return FormatPendingActionType(action.Type);
        }

        /// <summary>
        /// Explicitly maps a PendingActionType enum value to its standard presentation string.
        /// Unknown/undefined enum values map to "Unknown" rather than "None" or a recognized action.
        /// </summary>
        /// <param name="type">The pending action type enum value.</param>
        /// <returns>The presentation string.</returns>
        public static string FormatPendingActionType(PendingActionType type)
        {
            return type switch
            {
                PendingActionType.None => None,
                PendingActionType.Install => Install,
                PendingActionType.Update => Update,
                PendingActionType.Reapply => Reapply,
                PendingActionType.Restore => Restore,
                _ => Unknown
            };
        }

        /// <summary>
        /// Formats the full display label text for the recorded pending action in Game Details.
        /// </summary>
        /// <param name="installation">The installation record, if available.</param>
        /// <param name="isPreexisting">True if the installation record preexisted.</param>
        /// <returns>The formatted label text.</returns>
        public static string FormatRecordedPendingActionLabel(GameInstallation? installation, bool isPreexisting = true)
        {
            var actionText = GetRecordedPendingAction(installation, isPreexisting);
            return $"{LabelPrefix}{actionText}";
        }
    }
}
