using DXVKCompanion.Models;
using DXVKCompanion.UI;
using Xunit;

namespace DXVKCompanion.PhaseATests
{
    public sealed class GameListManagedFilterTests
    {
        [Fact]
        public void NullInstallation_PreservesEnabledProfileBehavior_AndRejectsDisabledOrNullProfile()
        {
            // Acceptance: A null installation preserves existing enabled-profile behavior;
            // a disabled/unavailable profile with no active installation does not match.
            var enabledProfile = new GameProfile(@"C:\Games\GameA\game.exe") { DxvkEnabled = true };
            var disabledProfile = new GameProfile(@"C:\Games\GameB\game.exe") { DxvkEnabled = false };

            Assert.True(GameListManagedFilter.MatchesManagedOrActive(null, enabledProfile));
            Assert.False(GameListManagedFilter.MatchesManagedOrActive(null, disabledProfile));
            Assert.False(GameListManagedFilter.MatchesManagedOrActive(null, null));
            Assert.False(GameListManagedFilter.MatchesManagedOrActive(null));
        }

        [Theory]
        [InlineData(RestorationState.None)]
        [InlineData(RestorationState.Managed)]
        [InlineData(RestorationState.Restored)]
        [InlineData(RestorationState.AttentionRequired)]
        public void EnabledProfile_WithVisibleInstallation_MatchesFilter(RestorationState state)
        {
            // Acceptance: A visible entry matches Managed / Active Only when its profile is enabled.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\Game",
                IsHidden = false,
                RestorationState = state
            };
            var enabledProfile = new GameProfile(@"C:\Games\Game\game.exe") { DxvkEnabled = true };

            Assert.True(GameListManagedFilter.MatchesManagedOrActive(installation, enabledProfile));
        }

        [Fact]
        public void DisabledProfile_WithManagedInstallationState_MatchesFilter()
        {
            // Acceptance: A visible entry matches Managed / Active Only when its installation state is Managed,
            // even if the profile is disabled or unavailable.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\ManagedGame",
                IsHidden = false,
                RestorationState = RestorationState.Managed,
                ManagedDxvkVersion = "2.5"
            };
            var disabledProfile = new GameProfile(@"C:\Games\ManagedGame\game.exe") { DxvkEnabled = false };

            Assert.True(GameListManagedFilter.MatchesManagedOrActive(installation, disabledProfile));
            Assert.True(GameListManagedFilter.MatchesManagedOrActive(installation, null));
            Assert.True(GameListManagedFilter.MatchesManagedOrActive(installation));
        }

        [Theory]
        [InlineData("2.5")]
        [InlineData("v2.5.1")]
        [InlineData("1.10.3")]
        public void DisabledProfile_WithActiveAttentionInstallation_MatchesFilter(string activeVersion)
        {
            // Acceptance: A visible entry matches Managed / Active Only when its installation is AttentionRequired
            // with a nonblank ManagedDxvkVersion, even if the profile is disabled.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\AttentionGame",
                IsHidden = false,
                RestorationState = RestorationState.AttentionRequired,
                ManagedDxvkVersion = activeVersion
            };
            var disabledProfile = new GameProfile(@"C:\Games\AttentionGame\game.exe") { DxvkEnabled = false };

            Assert.True(GameListManagedFilter.MatchesManagedOrActive(installation, disabledProfile));
            Assert.True(GameListManagedFilter.MatchesManagedOrActive(installation, null));
            Assert.True(GameListManagedFilter.MatchesManagedOrActive(installation));
        }

        [Theory]
        [InlineData(true, RestorationState.None, null)]
        [InlineData(false, RestorationState.Managed, "2.5")]
        [InlineData(true, RestorationState.Managed, "2.5")]
        [InlineData(false, RestorationState.AttentionRequired, "2.5")]
        [InlineData(true, RestorationState.AttentionRequired, "2.5")]
        public void HiddenInstallation_NeverMatchesFilter(
            bool profileEnabled,
            RestorationState state,
            string? managedVersion)
        {
            // Acceptance: Hidden installations never match this filter,
            // including enabled profiles and active attention installations.
            var hiddenInstallation = new GameInstallation
            {
                InstallationPath = @"C:\Games\HiddenGame",
                IsHidden = true,
                RestorationState = state,
                ManagedDxvkVersion = managedVersion
            };
            var profile = new GameProfile(@"C:\Games\HiddenGame\game.exe") { DxvkEnabled = profileEnabled };

            Assert.False(GameListManagedFilter.MatchesManagedOrActive(hiddenInstallation, profile));
            Assert.False(GameListManagedFilter.MatchesManagedOrActive(hiddenInstallation, null));
            Assert.False(GameListManagedFilter.MatchesManagedOrActive(hiddenInstallation));
        }

        [Theory]
        [InlineData(RestorationState.None)]
        [InlineData(RestorationState.Restored)]
        public void DisabledProfile_WithNativeOrRestoredInstallation_DoesNotMatchFilter(RestorationState state)
        {
            // Acceptance: Native and Restored entries with disabled profiles do not match.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\InactiveGame",
                IsHidden = false,
                RestorationState = state,
                ManagedDxvkVersion = null
            };
            var disabledProfile = new GameProfile(@"C:\Games\InactiveGame\game.exe") { DxvkEnabled = false };

            Assert.False(GameListManagedFilter.MatchesManagedOrActive(installation, disabledProfile));
            Assert.False(GameListManagedFilter.MatchesManagedOrActive(installation, null));
            Assert.False(GameListManagedFilter.MatchesManagedOrActive(installation));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\n")]
        public void AttentionRequiredAlone_WithNullEmptyOrWhitespaceVersion_DoesNotMatchFilter(string? blankVersion)
        {
            // Acceptance: AttentionRequired alone does not establish active management;
            // null/empty/whitespace installation versions are not active versions.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\AttentionGame",
                IsHidden = false,
                RestorationState = RestorationState.AttentionRequired,
                ManagedDxvkVersion = blankVersion
            };
            var disabledProfile = new GameProfile(@"C:\Games\AttentionGame\game.exe") { DxvkEnabled = false };

            Assert.False(GameListManagedFilter.MatchesManagedOrActive(installation, disabledProfile));
            Assert.False(GameListManagedFilter.MatchesManagedOrActive(installation, null));
            Assert.False(GameListManagedFilter.MatchesManagedOrActive(installation));
        }

        [Theory]
        [InlineData(RestorationState.None, InstallationConflictFlags.DxvkVersion)]
        [InlineData(RestorationState.None, InstallationConflictFlags.Architecture)]
        [InlineData(RestorationState.Restored, InstallationConflictFlags.UnknownOriginalFile)]
        [InlineData(RestorationState.AttentionRequired, InstallationConflictFlags.DxvkVersion)]
        public void ConflictsAlone_WithoutActiveManagement_DoesNotMatchFilter(
            RestorationState state,
            InstallationConflictFlags flags)
        {
            // Acceptance: Conflicts alone do not establish active management.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\ConflictGame",
                IsHidden = false,
                RestorationState = state,
                ConflictFlags = flags,
                ManagedDxvkVersion = null // No active version
            };
            var disabledProfile = new GameProfile(@"C:\Games\ConflictGame\game.exe") { DxvkEnabled = false };

            Assert.False(GameListManagedFilter.MatchesManagedOrActive(installation, disabledProfile));
            Assert.False(GameListManagedFilter.MatchesManagedOrActive(installation, null));
            Assert.False(GameListManagedFilter.MatchesManagedOrActive(installation));
        }

        [Theory]
        [InlineData(RestorationState.None)]
        [InlineData(RestorationState.Restored)]
        public void HistoricalProfileDxvkVersionAlone_DoesNotEstablishActiveManagement(RestorationState state)
        {
            // Acceptance: A historical profile.DxvkVersion alone does not establish active management.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\Game",
                IsHidden = false,
                RestorationState = state,
                ManagedDxvkVersion = null
            };
            var profile = new GameProfile(@"C:\Games\Game\game.exe")
            {
                DxvkEnabled = false,
                DxvkVersion = "2.5" // Historical version recorded on disabled profile
            };

            Assert.False(GameListManagedFilter.MatchesManagedOrActive(installation, profile));
            Assert.False(GameListManagedFilter.MatchesManagedOrActive(null, profile));
        }

        [Fact]
        public void FilteringIsReadOnly_PreservesStateUnmodified()
        {
            // Acceptance: Filtering is read-only: do not change persisted profile flags,
            // installation state/version, ownership records or pending actions to make rows appear.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\Game",
                IsHidden = false,
                RestorationState = RestorationState.AttentionRequired,
                ManagedDxvkVersion = "2.5",
                ConflictFlags = InstallationConflictFlags.None,
                PendingAction = new PendingAction { Type = PendingActionType.Restore, IsPending = true }
            };
            installation.ManagedFiles.Add(new ManagedFileRecord { RelativePath = "d3d11.dll" });

            var profile = new GameProfile(@"C:\Games\Game\game.exe")
            {
                DxvkEnabled = false,
                DxvkVersion = "2.5"
            };

            bool matches = GameListManagedFilter.MatchesManagedOrActive(installation, profile);

            Assert.True(matches);
            Assert.False(profile.DxvkEnabled);
            Assert.Equal("2.5", profile.DxvkVersion);
            Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
            Assert.Equal("2.5", installation.ManagedDxvkVersion);
            Assert.Single(installation.ManagedFiles);
            Assert.NotNull(installation.PendingAction);
            Assert.True(installation.PendingAction.IsPending);
        }
    }
}
