# AI Engineering Journal: Show Actively Managed Installations in Managed / Active Only Filter

**Date**: 2026-10-17  
**Session**: Gemini Session 1  
**Primary Issue**: [#139](https://github.com/Tiflit/DXVK-Companion/issues/139)  
**Base Revision**: `4718e4ede24d06f2a2260f861b0dacd36b79db59` (`origin/main`)  

---

### 1. Objective & Defect Context

Fix Manage Games "Managed / Active Only" filter so actively managed installations are visible even when their profile flag `DxvkEnabled` is `false`:
- Previously, `ManageGamesWindow.RefreshList` filtered:
  ```csharp
  if (filter == "Managed / Active Only" && (!profile.DxvkEnabled || isHidden))
      continue;
  ```
- A stale or reconstructed disabled profile hid an installation that is still `RestorationState.Managed` or `RestorationState.AttentionRequired` with a non-blank `ManagedDxvkVersion`. Following Issue #137 which included actively managed attention-state games in Restore All, users must also be able to find those tracked installations in the "Managed / Active Only" view.

---

### 2. Implementation

1. **`src/DXVKCompanion/UI/GameListManagedFilter.cs`**:
   - Introduced pure predicate helper `GameListManagedFilter` in `DXVKCompanion.UI`.
   - `MatchesManagedOrActive(GameInstallation? installation, GameProfile? profile)`:
     - Returns `false` immediately if `installation != null && installation.IsHidden` (hidden installations never match).
     - Returns `true` if `profile != null && profile.DxvkEnabled` (preserves existing enabled-profile behavior for visible and unrecorded installations).
     - If `installation != null`:
       - Returns `true` if `installation.RestorationState == RestorationState.Managed`.
       - Returns `true` if `installation.RestorationState == RestorationState.AttentionRequired && !string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion)`.
     - Otherwise returns `false` (native/restored entries, attention required without active version, conflicts alone, historical `profile.DxvkVersion` alone, and null/disabled profiles without active installations do not match).
   - `MatchesManagedOrActive(GameInstallation? installation)` convenience overload evaluating without a profile.

2. **`src/DXVKCompanion/UI/ManageGamesWindow.cs`**:
   - Updated `RefreshList` to use `!GameListManagedFilter.MatchesManagedOrActive(installation, profile)` for the "Managed / Active Only" filter.
   - Preserves all other filters, hidden-game handling, status text rendering, item ordering, and selection behavior.
   - Read-only filtering: does not modify profile flags, installation states, ownership records, or pending actions.

3. **`tests/DXVKCompanion.PhaseA.Tests/GameListManagedFilterTests.cs`**:
   - Headless unit tests covering:
     - Null installation preserves enabled-profile behavior and rejects disabled/null profiles.
     - Enabled profile with visible installation matches across all restoration states.
     - Disabled profile with `RestorationState.Managed` matches filter.
     - Disabled profile with `RestorationState.AttentionRequired` and non-blank version matches filter.
     - Hidden installations never match filter regardless of profile or installation states.
     - Native and restored installations with disabled profiles do not match.
     - `AttentionRequired` alone with null/empty/whitespace version does not match.
     - Conflicts alone without active management do not match.
     - Historical `profile.DxvkVersion` alone does not match.
     - Read-only verification asserting no mutated state.

---

### 3. Verification & Governance

- Local host execution: `NOT RUN (pending human authorization)` under AGENTS.md.
- Verified via GitHub Actions Cloud CI on Windows runners (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`, `Run Python workflow tests`).
