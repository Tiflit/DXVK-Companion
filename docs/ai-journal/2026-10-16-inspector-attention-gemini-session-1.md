# AI Engineering Journal: Preserve Unresolved Managed-File Attention Across Repeated Inspections

**Date**: 2026-10-16  
**Session**: Gemini Session 1 & Codex Focused Repair  
**Primary Issue**: [#133](https://github.com/Tiflit/DXVK-Companion/issues/133)  
**Pull Request**: [#134](https://github.com/Tiflit/DXVK-Companion/pull/134)  
**Base Revision**: `3ea4f39f8c7e4edb7b312a7c728c3a570e09a1fc` (`origin/main`)  

---

### 1. Objective & Defect Context

Fix `ManagedFileInspector` so an actively managed installation remains in `RestorationState.AttentionRequired` while a managed DLL remains missing or differs from its recorded managed hash:
- `ManagedFileInspector.InspectInstallation` previously set `anyExternalChange` only when a record transitioned from a non-`ExternallyChanged` state.
- On a subsequent scan of an unresolved condition (where a file was still missing or mismatched), `record.CurrentState` was already `ExternallyChanged`, leaving `anyExternalChange` as `false`.
- The previous recovery branch assumed `!anyExternalChange` implied all managed files were consistent, prematurely promoting `AttentionRequired` back to `Managed` (or `Restored`).
- Unresolved conditions were thus silently dropped from the Attention Required view on repeated scans.

---

### 2. Implementation

1. **`src/DXVKCompanion/Storage/ManagedFileInspector.cs`**:
   - Introduced `allFilesVerifiedConsistent = installation.ManagedFiles.Count > 0` in `InspectInstallation`.
   - Required current-pass verification for each managed file against usable recorded managed-hash evidence:
     - Missing target file on disk sets `allFilesVerifiedConsistent = false`.
     - Missing or unusable `record.ExpectedManagedSha256` (`!IsValidSha256(...)`) sets `allFilesVerifiedConsistent = false`.
     - Content hash mismatch against `record.ExpectedManagedSha256` sets `allFilesVerifiedConsistent = false`.
     - In all non-consistent cases, if `record.CurrentState != ManagedFileState.ExternallyChanged`, it transitions to `ExternallyChanged` and marks `anyExternalChange = true`.
   - Updated the healthy recovery branch to strictly require:
     `allFilesVerifiedConsistent && installation.ConflictFlags == InstallationConflictFlags.None && installation.RestorationState == RestorationState.AttentionRequired`.
   - When an unresolved condition persists across repeated scans, `allFilesVerifiedConsistent` remains `false`, preventing premature recovery and keeping `installation.RestorationState = RestorationState.AttentionRequired`.
   - Preserved Section 20 pending-action supersession: `anyExternalChange` remains `false` on repeat inspections of unresolved conditions, preventing repeated invalidation of newly queued actions or duplicate notification events.

2. **`tests/DXVKCompanion.PhaseA.Tests/ManagedFileInspectorAttentionTests.cs`**:
   - Dedicated headless test suite covering all 7 acceptance criteria:
     1. `MissingDll_RepeatedScan_RetainsExternallyChangedAndAttentionRequired_WithoutRepeatedNotification`: missing DLL retains `ExternallyChanged` and `AttentionRequired` across multiple scans.
     2. `ReplacedDll_RepeatedScan_RetainsExternallyChangedAndAttentionRequired_WithoutRepeatedNotification`: replaced DLL retains `ExternallyChanged` and `AttentionRequired` across multiple scans.
     3. `UnresolvedAttention_SurvivesStoreReload_BeforeRepeatedScan`: unresolved attention survives full `GameLibraryStore` reload from disk before repeated inspection.
     4. `MultiFileSet_RepairingOneFile_CannotClearAttention_WhileAnotherRemainsInconsistent`: repairing `d3d11.dll` while `dxgi.dll` remains missing retains `AttentionRequired`.
     5. `MissingOrUnusableManagedHashBaseline_CannotAuthorizeRecovery_AndFlagsAttention`: null, empty, whitespace, short, or invalid hex baselines cannot authorize recovery and flag attention.
     6. `AllFilesRepaired_TransitionsBackToManaged_UnlessConflictFlagsPresent`: repairing all files recovers to `Managed` when conflict flags are `None`; non-zero conflict flags prevent recovery.
     7. `Section20_NewlyQueuedPendingAction_NotDiscardedOnRepeatedScanOfUnresolvedCondition`: newly queued pending actions are preserved and not invalidated on repeated scans of unresolved conditions.

3. **`tests/DXVKCompanion.PhaseA.Tests/PhaseDExternalChangesAndPendingActionTests.cs`**:
   - Extended existing detection tests with repeated inspection assertions confirming persistent `ExternallyChanged` and `AttentionRequired` states.

---

### 3. Codex Review Findings & Focused Repair

Codex coordinator review identified that installations previously affected by the old bug could have `RestorationState.Managed` persisted alongside records already marked `ExternallyChanged`.
On inspection:
- `record.CurrentState` was already `ExternallyChanged`, so `anyExternalChange` remained `false`.
- Previously, `AttentionRequired` was assigned only when `anyExternalChange` was true (`if (anyExternalChange)`), so an affected installation stayed `RestorationState.Managed` even though files remained missing or mismatched.
- **Repair**: In `ManagedFileInspector.cs`, decoupled installation attention assignment from `anyExternalChange`. If `!allFilesVerifiedConsistent && installation.ManagedFiles.Count > 0`, `installation.RestorationState` is unconditionally set to `RestorationState.AttentionRequired`.
- Preserved `anyExternalChange` exclusively as the signal for Section 20 stale pending action invalidation and the method's Boolean return value.
- Added seeded persistence/reload regressions in `ManagedFileInspectorAttentionTests.cs`:
  - `PersistedLegacyState_ManagedWithExternallyChanged_MissingDll_ReconcilesAttentionRequired_PreservesPendingActionAndMetadata`
  - `PersistedLegacyState_ManagedWithExternallyChanged_ReplacedDll_ReconcilesAttentionRequired_PreservesPendingActionAndMetadata`
  Verifies that seeded `Managed + ExternallyChanged` installations reconcile to `AttentionRequired`, return `false`, preserve newly queued pending actions, retain management/version/file/backup data, and persist to disk.

---

### 4. Verification & Governance

- Local host execution: `NOT RUN (pending human authorization)`.
- Verified via GitHub Actions Cloud CI on Windows runners (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`, `Run Python workflow tests`).
