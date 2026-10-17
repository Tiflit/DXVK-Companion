using System;
using DXVKCompanion.Models;
using DXVKCompanion.UI;
using Xunit;

namespace DXVKCompanion.PhaseATests
{
    public sealed class PendingActionPresentationTests
    {
        [Fact]
        public void NullInstallation_ReturnsUnavailable()
        {
            // Acceptance: A missing installation record displays Unavailable.
            Assert.Equal("Unavailable", PendingActionPresentation.GetRecordedPendingAction(null));
            Assert.Equal("Unavailable", PendingActionPresentation.GetRecordedPendingAction(null, isPreexisting: true));
            Assert.Equal("Unavailable", PendingActionPresentation.GetRecordedPendingAction(null, isPreexisting: false));

            Assert.Equal("Recorded Pending Action: Unavailable", PendingActionPresentation.FormatRecordedPendingActionLabel(null));
            Assert.Equal("Recorded Pending Action: Unavailable", PendingActionPresentation.FormatRecordedPendingActionLabel(null, isPreexisting: false));
        }

        [Fact]
        public void UnrecordedGame_NotPreexisting_ReturnsUnavailable()
        {
            // Acceptance: Preserve the distinction for an unrecorded profile whose installation object was created
            // while opening Game Details: use existing isPreexistingInstallation, as the version display does.
            var newlyCreatedInstallation = new GameInstallation
            {
                InstallationPath = @"C:\Games\UnrecordedGame",
                DisplayName = "UnrecordedGame.exe",
                PendingAction = null
            };

            Assert.Equal("Unavailable", PendingActionPresentation.GetRecordedPendingAction(newlyCreatedInstallation, isPreexisting: false));
            Assert.Equal("Recorded Pending Action: Unavailable", PendingActionPresentation.FormatRecordedPendingActionLabel(newlyCreatedInstallation, isPreexisting: false));

            // Even if an action was populated on a non-preexisting object, non-preexisting status reports Unavailable
            newlyCreatedInstallation.PendingAction = PendingAction.Install("2.5");
            Assert.Equal("Unavailable", PendingActionPresentation.GetRecordedPendingAction(newlyCreatedInstallation, isPreexisting: false));
            Assert.Equal("Recorded Pending Action: Unavailable", PendingActionPresentation.FormatRecordedPendingActionLabel(newlyCreatedInstallation, isPreexisting: false));

            // Positive control: pre-existing installation with action reports the action
            Assert.Equal("Install", PendingActionPresentation.GetRecordedPendingAction(newlyCreatedInstallation, isPreexisting: true));
            Assert.Equal("Recorded Pending Action: Install", PendingActionPresentation.FormatRecordedPendingActionLabel(newlyCreatedInstallation, isPreexisting: true));
        }

        [Fact]
        public void PreexistingInstallation_WithAbsentPendingAction_ReturnsNone()
        {
            // Acceptance: An existing installation with no PendingAction object displays None.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\TrackedGame",
                PendingAction = null
            };

            Assert.Equal("None", PendingActionPresentation.GetRecordedPendingAction(installation, isPreexisting: true));
            Assert.Equal("Recorded Pending Action: None", PendingActionPresentation.FormatRecordedPendingActionLabel(installation, isPreexisting: true));
            Assert.Equal("None", PendingActionPresentation.FormatPendingAction(null));
        }

        [Fact]
        public void PreexistingInstallation_WithPendingActionTypeOfNone_ReturnsNone()
        {
            // Acceptance: An existing installation with an action of type None displays None.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\TrackedGame",
                PendingAction = new PendingAction { Type = PendingActionType.None }
            };

            Assert.Equal("None", PendingActionPresentation.GetRecordedPendingAction(installation, isPreexisting: true));
            Assert.Equal("Recorded Pending Action: None", PendingActionPresentation.FormatRecordedPendingActionLabel(installation, isPreexisting: true));
            Assert.Equal("None", PendingActionPresentation.FormatPendingAction(installation.PendingAction));
        }

        [Theory]
        [InlineData(PendingActionType.Install, "Install")]
        [InlineData(PendingActionType.Update, "Update")]
        [InlineData(PendingActionType.Reapply, "Reapply")]
        [InlineData(PendingActionType.Restore, "Restore")]
        public void PreexistingInstallation_WithNamedActionTypes_ReturnsExplicitMapping(PendingActionType type, string expectedText)
        {
            // Acceptance: Display a clearly labelled recorded pending-action value: None, Install, Update, Reapply or Restore,
            // using explicit mappings of PendingActionType.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\TrackedGame",
                PendingAction = new PendingAction { Type = type }
            };

            Assert.Equal(expectedText, PendingActionPresentation.GetRecordedPendingAction(installation, isPreexisting: true));
            Assert.Equal($"Recorded Pending Action: {expectedText}", PendingActionPresentation.FormatRecordedPendingActionLabel(installation, isPreexisting: true));
            Assert.Equal(expectedText, PendingActionPresentation.FormatPendingActionType(type));
        }

        [Theory]
        [InlineData(PendingActionType.None, "None")]
        [InlineData(PendingActionType.Install, "Install")]
        [InlineData(PendingActionType.Update, "Update")]
        [InlineData(PendingActionType.Reapply, "Reapply")]
        [InlineData(PendingActionType.Restore, "Restore")]
        public void FormatPendingActionType_MapsKnownEnumsDirectly(PendingActionType type, string expectedText)
        {
            Assert.Equal(expectedText, PendingActionPresentation.FormatPendingActionType(type));
        }

        [Theory]
        [InlineData(99)]
        [InlineData(1000)]
        [InlineData(-1)]
        [InlineData(-999)]
        public void PositiveAndNegativeUnknownValues_ReturnUnknown(int rawValue)
        {
            // Acceptance: Unknown enum values display Unknown, rather than None or a successful/recognized queued action.
            var unknownType = (PendingActionType)rawValue;
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\TrackedGame",
                PendingAction = new PendingAction { Type = unknownType }
            };

            var actionText = PendingActionPresentation.GetRecordedPendingAction(installation, isPreexisting: true);
            var label = PendingActionPresentation.FormatRecordedPendingActionLabel(installation, isPreexisting: true);

            Assert.Equal("Unknown", actionText);
            Assert.Equal("Recorded Pending Action: Unknown", label);
            Assert.Equal("Unknown", PendingActionPresentation.FormatPendingActionType(unknownType));

            // Must never be confused with None or valid actions
            Assert.NotEqual("None", actionText);
            Assert.NotEqual("Install", actionText);
            Assert.NotEqual("Update", actionText);
            Assert.NotEqual("Reapply", actionText);
            Assert.NotEqual("Restore", actionText);
            Assert.NotEqual("Unavailable", actionText);
        }

        [Fact]
        public void HistoricalLastCancelledAction_MustNotAppearAsCurrentPendingWork()
        {
            // Acceptance: A historical LastCancelledAction must not appear as current pending work.
            // Current pending work can coexist with cancellation history; display the current PendingAction.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\TrackedGame",
                PendingAction = null,
                LastCancelledAction = PendingAction.Install("2.4", "cancelled reason"),
                LastCancellationReason = "Incompatible API",
                LastCancellationOutcome = "Cancelled"
            };

            // Historical cancellation present, but no current PendingAction -> must report None
            Assert.Equal("None", PendingActionPresentation.GetRecordedPendingAction(installation, isPreexisting: true));
            Assert.Equal("Recorded Pending Action: None", PendingActionPresentation.FormatRecordedPendingActionLabel(installation, isPreexisting: true));

            // Action with Type=None and cancellation history -> still None
            installation.PendingAction = new PendingAction { Type = PendingActionType.None };
            Assert.Equal("None", PendingActionPresentation.GetRecordedPendingAction(installation, isPreexisting: true));

            // When new pending action coexists with cancellation history, current pending work is displayed
            installation.PendingAction = PendingAction.Restore("User restored game");
            Assert.Equal("Restore", PendingActionPresentation.GetRecordedPendingAction(installation, isPreexisting: true));
            Assert.Equal("Recorded Pending Action: Restore", PendingActionPresentation.FormatRecordedPendingActionLabel(installation, isPreexisting: true));
        }

        [Fact]
        public void DoesNotLeakTargetVersionReasonOrTimestamp()
        {
            // Acceptance: The label describes recorded intent only. Do not claim a game is running, an action has completed,
            // or execution is guaranteed. Do not include arbitrary action reasons, target-version strings, timestamps or other record dumps.
            var action = new PendingAction
            {
                Type = PendingActionType.Install,
                TargetDxvkVersion = "v2.5.1-custom-build",
                Reason = "Overnight scheduled update from automated orchestrator",
                CreatedUtc = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc)
            };
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\TrackedGame",
                PendingAction = action
            };

            var actionText = PendingActionPresentation.GetRecordedPendingAction(installation, isPreexisting: true);
            var label = PendingActionPresentation.FormatRecordedPendingActionLabel(installation, isPreexisting: true);

            Assert.Equal("Install", actionText);
            Assert.Equal("Recorded Pending Action: Install", label);

            Assert.DoesNotContain("v2.5.1-custom-build", label);
            Assert.DoesNotContain("Overnight scheduled update", label);
            Assert.DoesNotContain("2026", label);
        }

        [Fact]
        public void PresentationIsReadOnly_LeavesInstallationAndActionsUnmodified()
        {
            // Acceptance: Mapping and rendering add no persistence, queue consumption/cancellation/retry,
            // inspection, file/process/network access or state mutation.
            var pending = PendingAction.Reapply("2.4", "User reapply");
            var cancelled = PendingAction.Install("2.3", "Cancelled previous install");
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\TrackedGame",
                DisplayName = "TrackedGame",
                ManagedDxvkVersion = "2.4",
                RestorationState = RestorationState.AttentionRequired,
                PendingAction = pending,
                LastCancelledAction = cancelled,
                LastCancellationReason = "Device busy",
                LastCancellationOutcome = "Refused",
                LastRefusalReason = "Refusal text"
            };

            var recordedPending = PendingActionPresentation.GetRecordedPendingAction(installation, isPreexisting: true);
            var label = PendingActionPresentation.FormatRecordedPendingActionLabel(installation, isPreexisting: true);
            var typeFormatted = PendingActionPresentation.FormatPendingActionType(pending.Type);
            var actionFormatted = PendingActionPresentation.FormatPendingAction(pending);

            Assert.Equal("Reapply", recordedPending);
            Assert.Equal("Recorded Pending Action: Reapply", label);
            Assert.Equal("Reapply", typeFormatted);
            Assert.Equal("Reapply", actionFormatted);

            // Verify zero state mutation
            Assert.Same(pending, installation.PendingAction);
            Assert.Equal(PendingActionType.Reapply, installation.PendingAction.Type);
            Assert.Equal("2.4", installation.PendingAction.TargetDxvkVersion);
            Assert.Equal("User reapply", installation.PendingAction.Reason);

            Assert.Same(cancelled, installation.LastCancelledAction);
            Assert.Equal(PendingActionType.Install, installation.LastCancelledAction.Type);
            Assert.Equal("2.3", installation.LastCancelledAction.TargetDxvkVersion);
            Assert.Equal("Cancelled previous install", installation.LastCancelledAction.Reason);

            Assert.Equal("C:\\Games\\TrackedGame", installation.InstallationPath);
            Assert.Equal("TrackedGame", installation.DisplayName);
            Assert.Equal("2.4", installation.ManagedDxvkVersion);
            Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
            Assert.Equal("Device busy", installation.LastCancellationReason);
            Assert.Equal("Refused", installation.LastCancellationOutcome);
            Assert.Equal("Refusal text", installation.LastRefusalReason);
        }
    }
}
