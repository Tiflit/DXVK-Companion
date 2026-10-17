# AI Engineering Journal: Inspect Restored Installations Against Original Baselines

**Date**: 2026-10-16  
**Session**: Gemini Session 1  
**Primary Issue**: [#135](https://github.com/Tiflit/DXVK-Companion/issues/135)  
**Base Revision**: `f8f0966c481e99882533f5adb7ec47580ae837a6` (`origin/main`)  

---

### 1. Objective & Defect Context

Fix `ManagedFileInspector` so that after a successful DXVK restore/disable, inspection verifies the recorded original file state instead of requiring the deployed DXVK files to remain present:
- `DxvkRollback` restores original files, deletes files whose original state was `Missing`, clears `ManagedDxvkVersion` markers, and persists `RestorationState.Restored`.
- `ManagedFileInspector` previously compared all tracked files solely against `ExpectedManagedSha256` and treated absence of any tracked file as an external change.
- Consequently, running `InspectInstallation` or `InspectAll` on a successfully restored installation erroneously flagged external changes and forced `RestorationState.AttentionRequired`.
- The fix enables `ManagedFileInspector` to differentiate restored lifecycle from active management, verifying completed restores against original baselines while maintaining active management protections.

---

### 2. Implementation

1. **`src/DXVKCompanion/Storage/ManagedFileInspector.cs`**:
   - Identified restored lifecycle state:
     `isRestoredLifecycle = installation.RestorationState == RestorationState.Restored || (installation.RestorationState != RestorationState.Managed && string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion));`
   - In restored lifecycle:
     - `FileOriginalState.Missing`: requires absence on disk. An unexpected file at an originally absent path marks `allFilesVerifiedConsistent = false` and transitions to `ExternallyChanged`.
     - `FileOriginalState.Existing`: requires presence, valid `OriginalSha256` baseline, and current content matching `OriginalSha256`. Absence, malformed original baseline, or hash mismatch marks `allFilesVerifiedConsistent = false` and transitions to `ExternallyChanged`.
     - `FileOriginalState.Unknown`: treated as unsupported/unusable, marks `allFilesVerifiedConsistent = false` and transitions to `ExternallyChanged`.
   - In active management lifecycle:
     - Preserved exact verification against `ExpectedManagedSha256`, target presence checks, and managed baseline validity checks from #133/#134.
   - Retained transition semantics:
     - `anyExternalChange` is set exclusively on new transitions to `ExternallyChanged`.
     - Section 20 pending action invalidation triggers only on new transitions (`anyExternalChange == true`), preserving newly queued actions across repeat scans of unresolved conditions.
   - Post-restore mismatch recovery:
     - Once mismatching files in a restored installation are repaired back to recorded originals, inspection recognizes all files as consistent and recovers `RestorationState` back to `Restored` (when `installation.ConflictFlags == InstallationConflictFlags.None`).

2. **`tests/DXVKCompanion.PhaseA.Tests/ManagedFileInspectorRestoredTests.cs`**:
   - Added synthetic headless test suite covering all acceptance criteria:
     1. `Install_Restore_Inspect_MaintainsRestoredAndConsistent_WithoutNewExternalChange`: full production install -> restore -> inspect lifecycle, InspectAll coverage, and fresh-store reload verification.
     2. `RestoredLifecycle_OriginallyMissing_UnexpectedFilePresent_FlagsAttention_AndPersistsAcrossScans`: unexpected file at originally absent path triggers attention, preserves attention and newly queued pending action across repeated scans.
     3. `RestoredLifecycle_OriginallyExisting_MissingFile_FlagsAttention_AndPersistsAcrossScans`: deleted original file triggers attention and persists across scans.
     4. `RestoredLifecycle_OriginallyExisting_ModifiedFile_FlagsAttention_AndPersistsAcrossScans`: modified original file triggers attention and persists across scans.
     5. `RestoredLifecycle_PostRestoreMismatch_SurvivesReload_RepairRecoversToRestored`: mismatch survives store reload, inspecting before repair retains AttentionRequired against original baselines, and repairing files recovers to Restored.
     6. `RestoredLifecycle_ConflictFlagsPresent_PreventsRecoveryToRestored`: non-zero conflict flags prevent recovery to Restored even when files are repaired.
     7. `RestoredLifecycle_UnusableOriginalEvidence_OrUnknownOriginalState_FlagsAttention`: null, malformed hex, or Unknown original states flag attention.
     8. `ActiveManagement_ProtectionsPreserved_DoesNotUseRestoredBaseline`: actively managed installation requires ExpectedManagedSha256 and does not accept original native files as consistent.

3. **`tests/DXVKCompanion.PhaseA.Tests/RestoreTargetOwnershipVerificationTests.cs`**:
   - Extended `Restore_RepeatRestoration_SafelyNoOpsAndDetectsPostRestoreTampering` with step 7 verifying that `ManagedFileInspector` maintains `RestorationState.Restored` and `Consistent` records after resolved rollback.

---

### 3. Verification & Governance

- Local host execution: `NOT RUN (pending human authorization)` under AGENTS.md.
- Verified via GitHub Actions Cloud CI on Windows runners (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`, `Run Python workflow tests`).
