# AI Engineering Journal: Exclude Unrecorded Profiles from Attention Required Filter

**Date**: 2026-10-16  
**Session**: Gemini Session 1  
**Primary Issue**: [#131](https://github.com/Tiflit/DXVK-Companion/issues/131)  
**Base Revision**: `aa592e6da66a0a4da80f3c51994e0eaf4392d9ae` (`origin/main`)  

---

### 1. Objective & Defect Context

Fix the Manage Games **Attention Required** filter so profiles without a matching `GameInstallation` are excluded, while preserving the rule for recorded installations:
- In `ManageGamesWindow.RefreshList`, the filter checked:
  ```csharp
  if (filter == "Attention Required" &&
      (installation?.RestorationState != RestorationState.AttentionRequired &&
       installation?.ConflictFlags == InstallationConflictFlags.None))
      continue;
  ```
- When `installation` was null, `(RestorationState?)null != RestorationState.AttentionRequired` evaluated to `true`, while `(InstallationConflictFlags?)null == InstallationConflictFlags.None` evaluated to `false`.
- The conjunction `true && false` resulted in `false`, failing to trigger the `continue` statement and incorrectly including unrecorded profiles in the "Attention Required" view (subsequently rendered with status "Clean / Native" or "Enabled").

---

### 2. Implementation

1. **`src/DXVKCompanion/UI/GameListAttentionFilter.cs`**:
   - Introduced a small, pure predicate helper `GameListAttentionFilter` in `DXVKCompanion.UI`.
   - `MatchesAttentionRequired(GameInstallation? installation)`:
     - Returns `false` immediately if `installation == null`.
     - Returns `true` if `installation.RestorationState == RestorationState.AttentionRequired` or `installation.ConflictFlags != InstallationConflictFlags.None`.
   - `MatchesAttentionRequired(GameInstallation? installation, GameProfile? profile)`:
     - Evaluates the installation regardless of profile enabled/version state.

2. **`src/DXVKCompanion/UI/ManageGamesWindow.cs`**:
   - Updated `RefreshList` to call `GameListAttentionFilter.MatchesAttentionRequired(installation, profile)`.
   - Preserves all other filters ("Active Games", "Hidden Games", "Managed / Active Only", "All Tracked Games") and hidden-game behavior.

3. **`tests/DXVKCompanion.PhaseA.Tests/GameListAttentionFilterTests.cs`**:
   - Comprehensive headless unit tests covering:
     - Null installation does not match Attention Required, regardless of profile enabled state.
     - Recorded installations with `RestorationState.None`, `RestorationState.Managed`, or `RestorationState.Restored` and `ConflictFlags == InstallationConflictFlags.None` do not match.
     - `RestorationState.AttentionRequired` matches with or without conflict flags.
     - Nonzero conflict flags match even without `RestorationState.AttentionRequired`.

---

### 3. Verification & Governance

- Local host execution: `NOT RUN (pending human authorization)`.
- Verified via GitHub Actions Cloud CI on Windows runners (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`, `Run Python workflow tests`).
