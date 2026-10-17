using DXVKCompanion.Models;
using DXVKCompanion.UI;
using Xunit;

namespace DXVKCompanion.PhaseATests
{
    public sealed class GameVersionPresentationTests
    {
        [Fact]
        public void NullInstallation_ReturnsUnavailable_RegardlessOfProfileVersion()
        {
            // Acceptance: When no installation record is available, display "Unavailable" for the installation version.
            // Do not infer native mode or current management from the profile alone.
            var profileWithVersion = new GameProfile(@"C:\Games\GameA\game.exe")
            {
                DxvkEnabled = true,
                DxvkVersion = "2.5"
            };
            var disabledProfile = new GameProfile(@"C:\Games\GameB\game.exe")
            {
                DxvkEnabled = false,
                DxvkVersion = null
            };

            Assert.Equal("Unavailable", GameVersionPresentation.GetRecordedManagedVersion(null));
            Assert.Equal("Unavailable", GameVersionPresentation.GetRecordedManagedVersion(null, isPreexisting: true));
            Assert.Equal("Unavailable", GameVersionPresentation.GetRecordedManagedVersion(null, isPreexisting: false));

            // Profile version is accessible separately, but never substituted as the installation version
            Assert.Equal("2.5", GameVersionPresentation.GetHistoricalProfileVersion(profileWithVersion));
            Assert.Equal("Profile version (last recorded): 2.5", GameVersionPresentation.FormatHistoricalProfileVersionLabel(profileWithVersion));
            Assert.Null(GameVersionPresentation.GetHistoricalProfileVersion(disabledProfile));
            Assert.Null(GameVersionPresentation.FormatHistoricalProfileVersionLabel(disabledProfile));
        }

        [Fact]
        public void UnrecordedGame_NotPreexisting_ReturnsUnavailable()
        {
            // Acceptance: Game Details currently creates a local installation record when opening an unrecorded profile.
            // Use isPreexistingInstallation fact to distinguish unavailable preexisting evidence from an empty stored version.
            var newlyCreatedInstallation = new GameInstallation
            {
                InstallationPath = @"C:\Games\UnrecordedGame",
                DisplayName = "UnrecordedGame.exe",
                ManagedDxvkVersion = null
            };

            Assert.Equal("Unavailable", GameVersionPresentation.GetRecordedManagedVersion(newlyCreatedInstallation, isPreexisting: false));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\r\n")]
        public void PreexistingInstallation_WithNullOrBlankManagedVersion_ReturnsNoneRecorded(string? blankVersion)
        {
            // Acceptance: When an installation record exists but its managed version is null/empty/whitespace,
            // display "None recorded". Never substitute profile.DxvkVersion as if it were the installation's current managed version.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\TrackedGame",
                ManagedDxvkVersion = blankVersion
            };
            var profile = new GameProfile(@"C:\Games\TrackedGame\game.exe")
            {
                DxvkVersion = "2.4" // Historical profile version
            };

            var result = GameVersionPresentation.GetRecordedManagedVersion(installation, isPreexisting: true);

            Assert.Equal("None recorded", result);
            Assert.NotEqual(profile.DxvkVersion, result);
        }

        [Fact]
        public void RestoredInstallation_WithHistoricalProfileVersion_ShowsNoneRecordedAndSeparateProfileVersion()
        {
            // Acceptance: Restored installation with historical profile version:
            // A successful restore clears installation.ManagedDxvkVersion but retains profile.DxvkVersion.
            // Distinguish recorded installation management from profile history.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\RestoredGame",
                RestorationState = RestorationState.Restored,
                ManagedDxvkVersion = null // Cleared on restore
            };
            var profile = new GameProfile(@"C:\Games\RestoredGame\game.exe")
            {
                DxvkEnabled = false,
                DxvkVersion = "2.3.1" // Retained historical version
            };

            var recordedVersion = GameVersionPresentation.GetRecordedManagedVersion(installation);
            var historicalVersion = GameVersionPresentation.GetHistoricalProfileVersion(profile);
            var historicalLabel = GameVersionPresentation.FormatHistoricalProfileVersionLabel(profile);

            Assert.Equal("None recorded", recordedVersion);
            Assert.Equal("2.3.1", historicalVersion);
            Assert.Equal("Profile version (last recorded): 2.3.1", historicalLabel);
        }

        [Fact]
        public void ActiveAttentionInstallation_WithStaleDisabledOrBlankProfile_ShowsActiveManagedVersion()
        {
            // Acceptance: Active AttentionRequired installations with stale disabled/blank profiles:
            // The main list version column displays the nonblank installation.ManagedDxvkVersion.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\AttentionGame",
                RestorationState = RestorationState.AttentionRequired,
                ManagedDxvkVersion = "2.5"
            };
            var staleBlankProfile = new GameProfile(@"C:\Games\AttentionGame\game.exe")
            {
                DxvkEnabled = false,
                DxvkVersion = null
            };

            var recordedVersion = GameVersionPresentation.GetRecordedManagedVersion(installation);
            var historicalVersion = GameVersionPresentation.GetHistoricalProfileVersion(staleBlankProfile);

            Assert.Equal("2.5", recordedVersion);
            Assert.Null(historicalVersion);
            Assert.Null(GameVersionPresentation.FormatHistoricalProfileVersionLabel(staleBlankProfile));
        }

        [Fact]
        public void ConflictingProfileAndInstallationVersions_DistinguishesBothClearly()
        {
            // Acceptance: Conflicting profile/installation versions are distinguished clearly.
            // profile.DxvkVersion must not masquerade as the current managed version.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\Game",
                RestorationState = RestorationState.Managed,
                ManagedDxvkVersion = "2.5"
            };
            var profile = new GameProfile(@"C:\Games\Game\game.exe")
            {
                DxvkEnabled = true,
                DxvkVersion = "2.4" // Older historical version in profile
            };

            var recordedVersion = GameVersionPresentation.GetRecordedManagedVersion(installation);
            var historicalVersion = GameVersionPresentation.GetHistoricalProfileVersion(profile);
            var historicalLabel = GameVersionPresentation.FormatHistoricalProfileVersionLabel(profile);

            Assert.Equal("2.5", recordedVersion);
            Assert.Equal("2.4", historicalVersion);
            Assert.Equal("Profile version (last recorded): 2.4", historicalLabel);
        }

        [Theory]
        [InlineData("2.5")]
        [InlineData("v2.5.1")]
        [InlineData("custom-build-20261017")]
        [InlineData("2.0-async")]
        public void PreservesRawNonBlankVersionContent(string rawVersion)
        {
            // Acceptance: Preserve raw nonblank version content.
            // Avoid "Native" or successful verification claims based solely on missing version fields.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\Game",
                ManagedDxvkVersion = rawVersion
            };

            var result = GameVersionPresentation.GetRecordedManagedVersion(installation);

            Assert.Equal(rawVersion, result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\r\n")]
        public void HistoricalProfileVersion_WhenBlank_ReturnsNull(string? blankVersion)
        {
            var profile = new GameProfile(@"C:\Games\Game\game.exe")
            {
                DxvkVersion = blankVersion
            };

            Assert.Null(GameVersionPresentation.GetHistoricalProfileVersion(profile));
            Assert.Null(GameVersionPresentation.FormatHistoricalProfileVersionLabel(profile));
            Assert.Null(GameVersionPresentation.GetHistoricalProfileVersion(null));
            Assert.Null(GameVersionPresentation.FormatHistoricalProfileVersionLabel(null));
        }

        [Fact]
        public void PresentationIsReadOnly_LeavesInstallationAndProfileUnmodified()
        {
            // Acceptance: Read-only behavior; do not clear or rewrite profile versions to fix display.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\Game",
                RestorationState = RestorationState.Restored,
                ManagedDxvkVersion = null
            };
            var profile = new GameProfile(@"C:\Games\Game\game.exe")
            {
                DxvkEnabled = false,
                DxvkVersion = "2.5"
            };

            _ = GameVersionPresentation.GetRecordedManagedVersion(installation);
            _ = GameVersionPresentation.GetHistoricalProfileVersion(profile);
            _ = GameVersionPresentation.FormatHistoricalProfileVersionLabel(profile);

            Assert.Equal(RestorationState.Restored, installation.RestorationState);
            Assert.Null(installation.ManagedDxvkVersion);
            Assert.False(profile.DxvkEnabled);
            Assert.Equal("2.5", profile.DxvkVersion);
        }
    }
}
