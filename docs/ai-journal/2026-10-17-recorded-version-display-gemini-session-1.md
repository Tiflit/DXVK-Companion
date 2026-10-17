# AI Engineering Journal: Distinguish Recorded Managed DXVK Version from Profile History

**Date**: 2026-10-17  
**Session**: Gemini Session 1  
**Primary Issue**: [#145](https://github.com/Tiflit/DXVK-Companion/issues/145)  
**Base Revision**: `7cc649b595518abb874ad9792396c80eaee4b4be` (`origin/main`)  

---

### 1. Objective & Defect Context

Correct version display in Manage Games and Game Details so recorded installation management is clearly distinguished from historical profile versions:
- Previously, `ManageGamesWindow.RefreshList` displayed `profile.DxvkVersion ?? "-"` in the main list version column.
- `GameDetailsWindow` previously displayed `DXVK Version: {profile.DxvkVersion ?? "None (Native)"}`.
- Following restoration, `installation.ManagedDxvkVersion` is cleared to `null`, but `profile.DxvkVersion` retains the historical release version, causing the UI to display stale historical versions or falsely infer native mode from an empty profile string alone.
- Stale or reconstructed profiles also failed to display active installation versions (e.g. for `AttentionRequired` installations).

---

### 2. Implementation

1. **`src/DXVKCompanion/UI/GameVersionPresentation.cs`**:
   - Introduced pure presentation helper `GameVersionPresentation` in `DXVKCompanion.UI`.
   - `GetRecordedManagedVersion(GameInstallation? installation, bool isPreexisting = true)`:
     - Returns `"Unavailable"` if `installation == null` or `!isPreexisting`.
     - Returns `"None recorded"` if `string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion)`.
     - Otherwise returns raw non-blank `installation.ManagedDxvkVersion`.
     - Never substitutes `profile.DxvkVersion` as the installation's managed version.
   - `GetHistoricalProfileVersion(GameProfile? profile)`:
     - Returns `profile.DxvkVersion` if non-blank; otherwise `null`.
   - `FormatHistoricalProfileVersionLabel(GameProfile? profile)`:
     - Returns `"Profile version (last recorded): {version}"` when non-blank.

2. **`src/DXVKCompanion/UI/ManageGamesWindow.cs`**:
   - Relabeled the version column header from `"Version"` to `"Recorded Version"`.
   - Updated `RefreshList` to populate the version subitem with `GameVersionPresentation.GetRecordedManagedVersion(installation)`.
   - Preserves profile reconstruction, filters, actions, and status rendering.

3. **`src/DXVKCompanion/UI/GameDetailsWindow.cs`**:
   - Displays `Recorded Managed Version: {recordedVersion}` using `GameVersionPresentation.GetRecordedManagedVersion(installation, isPreexistingInstallation)`.
   - When `profile.DxvkVersion` is non-blank, displays a separate label `Profile version (last recorded): {version}`.
   - Increments vertical layout offset (`top += 25`) to prevent overlapping subsequent controls.
   - Preserves in-memory installation creation for unrecorded profiles while respecting `isPreexistingInstallation`.

4. **`tests/DXVKCompanion.PhaseA.Tests/GameVersionPresentationTests.cs`**:
   - Pure headless tests covering:
     - Null installation returns `"Unavailable"`.
     - Unrecorded game (`!isPreexisting`) returns `"Unavailable"`.
     - Preexisting installation with null/empty/whitespace version returns `"None recorded"`.
     - Restored installation with historical profile version returns `"None recorded"` and separate profile version.
     - Active attention installation with stale disabled/blank profile displays active managed version.
     - Conflicting profile and installation versions distinguished without substitution.
     - Raw non-blank version content preserved verbatim.
     - Blank historical profile versions return `null`.
     - Read-only preservation of installation and profile objects.

---

### 3. Verification & Governance

- Local host execution: `NOT RUN (pending human authorization)` under AGENTS.md.
- Verified via GitHub Actions Cloud CI on Windows runners (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`, `Run Python workflow tests`).
