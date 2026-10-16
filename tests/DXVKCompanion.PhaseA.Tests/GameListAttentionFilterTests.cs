using DXVKCompanion.Models;
using DXVKCompanion.UI;
using Xunit;

namespace DXVKCompanion.PhaseATests
{
    public sealed class GameListAttentionFilterTests
    {
        [Fact]
        public void NullInstallation_DoesNotMatchAttentionRequired_RegardlessOfProfileEnabledState()
        {
            // Acceptance Criterion 1: A null installation does not match Attention Required,
            // regardless of the profile's enabled state.
            var disabledProfile = new GameProfile(@"C:\Games\GameA\game.exe") { DxvkEnabled = false };
            var enabledProfile = new GameProfile(@"C:\Games\GameB\game.exe") { DxvkEnabled = true };

            Assert.False(GameListAttentionFilter.MatchesAttentionRequired(null));
            Assert.False(GameListAttentionFilter.MatchesAttentionRequired(null, null));
            Assert.False(GameListAttentionFilter.MatchesAttentionRequired(null, disabledProfile));
            Assert.False(GameListAttentionFilter.MatchesAttentionRequired(null, enabledProfile));
        }

        [Theory]
        [InlineData(RestorationState.None)]
        [InlineData(RestorationState.Managed)]
        [InlineData(RestorationState.Restored)]
        public void RecordedInstallation_WithoutAttentionOrConflicts_DoesNotMatch(RestorationState state)
        {
            // Acceptance Criterion 2: Recorded installations with restoration state None,
            // Managed or Restored and no conflict flags do not match.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\TrackedGame",
                RestorationState = state,
                ConflictFlags = InstallationConflictFlags.None
            };

            var profile = new GameProfile(@"C:\Games\TrackedGame\game.exe") { DxvkEnabled = state == RestorationState.Managed };

            Assert.False(GameListAttentionFilter.MatchesAttentionRequired(installation));
            Assert.False(GameListAttentionFilter.MatchesAttentionRequired(installation, profile));
        }

        [Theory]
        [InlineData(InstallationConflictFlags.None)]
        [InlineData(InstallationConflictFlags.DxvkVersion)]
        [InlineData(InstallationConflictFlags.Architecture)]
        [InlineData(InstallationConflictFlags.FrameLimit)]
        [InlineData(InstallationConflictFlags.UnknownOriginalFile)]
        [InlineData(InstallationConflictFlags.DxvkVersion | InstallationConflictFlags.Architecture)]
        public void AttentionRequiredState_Matches_WithOrWithoutConflictFlags(InstallationConflictFlags conflictFlags)
        {
            // Acceptance Criterion 3 (part 1): AttentionRequired state matches with or without conflict flags.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\AttentionGame",
                RestorationState = RestorationState.AttentionRequired,
                ConflictFlags = conflictFlags
            };

            Assert.True(GameListAttentionFilter.MatchesAttentionRequired(installation));
            Assert.True(GameListAttentionFilter.MatchesAttentionRequired(installation, new GameProfile(@"C:\Games\AttentionGame\game.exe")));
        }

        [Theory]
        [InlineData(RestorationState.None, InstallationConflictFlags.DxvkVersion)]
        [InlineData(RestorationState.None, InstallationConflictFlags.Architecture)]
        [InlineData(RestorationState.None, InstallationConflictFlags.FrameLimit)]
        [InlineData(RestorationState.None, InstallationConflictFlags.UnknownOriginalFile)]
        [InlineData(RestorationState.Managed, InstallationConflictFlags.DxvkVersion)]
        [InlineData(RestorationState.Managed, InstallationConflictFlags.Architecture | InstallationConflictFlags.FrameLimit)]
        [InlineData(RestorationState.Restored, InstallationConflictFlags.DxvkVersion)]
        [InlineData(RestorationState.Restored, InstallationConflictFlags.UnknownOriginalFile)]
        public void NonzeroConflictFlags_Match_EvenWithoutAttentionRequiredState(
            RestorationState state,
            InstallationConflictFlags conflictFlags)
        {
            // Acceptance Criterion 3 (part 2): Nonzero conflict flags match even without AttentionRequired state.
            var installation = new GameInstallation
            {
                InstallationPath = @"C:\Games\ConflictGame",
                RestorationState = state,
                ConflictFlags = conflictFlags
            };

            Assert.True(GameListAttentionFilter.MatchesAttentionRequired(installation));
            Assert.True(GameListAttentionFilter.MatchesAttentionRequired(installation, new GameProfile(@"C:\Games\ConflictGame\game.exe")));
        }
    }
}
