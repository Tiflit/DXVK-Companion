# AI Engineering Journal: Failed-Operation Recovery and Native Baseline Preservation

**Date**: 2026-10-18  
**Session**: Gemini Session 1  
**Primary Issue**: [#166](https://github.com/Tiflit/DXVK-Companion/issues/166)  
**Base Revision**: `1fab0607d2c3327e7641c259082af96114a9bcd3` (`origin/main`)  
**Policy Identity**: `f3e9974371cb08d1dd99192bd314a135010594ed`  

---

### 1. Objective & Defect Context

Fix [`MultiFileTransactionEngine`](src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs) so failed `Install`, `Update`, `Reapply`, and `Restore` operations recover the exact pre-operation target state without corrupting, overwriting, or deleting the long-lived native baseline used by `Restore`:

1. **Conflation of native baseline and pre-operation state**: `MultiFileTransactionEngine` previously passed the long-lived native `file.OriginalState` and `backupPath` into `PreparedFile` alongside `currentIdentity` (the pre-operation target identity). During `RecoverAll`:
   - For files with `OriginalFileState.Existing`, recovery copied the native backup (`item.BackupPath`) over the pre-operation target (e.g., DXVK version A) and then compared the native file's identity against `item.OriginalIdentity` (DXVK version A), throwing `IOException` and halting recovery in `AttentionRequired` / `UnresolvedRecovery` with a corrupted mixed target state.
   - For files with `OriginalFileState.DidNotExist` (common for `dxgi.dll` and `dxvk.conf`), recovery deleted the working pre-operation DXVK version-A files on a failed `Update`, `Reapply`, or `Restore`.
2. **Untouched target mutation during recovery**: `RecoverAll` iterated across all `prepared` entries (`prepared.Count - 1` down to `0`) rather than only the indices where target mutation was attempted (`0 .. lastAttemptedIndex`).
3. **First-file partial-copy classification gap**: `anyWrite = true` was assigned after `Apply(item, request.Operation)` returned. An exception thrown inside `Apply` on `index == 0` (before `_hooks.AfterApply`) took the `!anyWrite` branch and returned `Aborted` / `SafeAbort` without entering recovery.
4. **Validation/preparation interleaving and unisolated plan artifacts**: Native backups were written inside the validation loop before later files in the request were validated, leaving stray backups on validation refusal. Operation plan metadata (`<transactionId>.json`) was written directly to `_transactionStoreRoot` rather than an isolated per-operation namespace.

---

### 2. Red-Baseline Evidence (Unmodified Production Code)

Before modifying `src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs`, commit `0aaf03874d51f8c0a62ec594655d348ec19b8875` pushed the new regression tests against unmodified `MultiFileTransactionEngine.cs` on Cloud CI ([Run `39098892503`](https://github.com/Tiflit/DXVK-Companion/actions/runs/39098892503)).

- **Red-Baseline TRX Summary**: `total=873, executed=873, passed=854, failed=19, skipped=0`.
- **Failing Test Names and Exact Assertions**:
  1. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedUpdate_AllOriginallyExisting_RecoversVersionA_PreservesNativeBackups_AndRestoresNative(failIndex: 0)` — `Assert.Equal() Failure: Values differ | Expected: FailedSafely | Actual: AttentionRequired`
  2. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedUpdate_AllOriginallyExisting_RecoversVersionA_PreservesNativeBackups_AndRestoresNative(failIndex: 1)` — `Assert.Equal() Failure: Values differ | Expected: FailedSafely | Actual: AttentionRequired`
  3. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedUpdate_AllOriginallyExisting_RecoversVersionA_PreservesNativeBackups_AndRestoresNative(failIndex: 2)` — `Assert.Equal() Failure: Values differ | Expected: FailedSafely | Actual: AttentionRequired`
  4. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedUpdate_AllOriginallyAbsent_RecoversVersionA_DoesNotDeleteWorkingManagedFiles_AndRestoresCleanAbsence(failIndex: 0)` — `Assert.Equal() Failure: Collections differ | Expected: ["d3d11.dll", "data/sentinel.pak", "dxgi.dll", "dxvk.conf", "Game.exe"] | Actual: ["data/sentinel.pak", "Game.exe"]`
  5. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedUpdate_AllOriginallyAbsent_RecoversVersionA_DoesNotDeleteWorkingManagedFiles_AndRestoresCleanAbsence(failIndex: 1)` — `Assert.Equal() Failure: Collections differ | Expected: ["d3d11.dll", "data/sentinel.pak", "dxgi.dll", "dxvk.conf", "Game.exe"] | Actual: ["data/sentinel.pak", "Game.exe"]`
  6. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedUpdate_AllOriginallyAbsent_RecoversVersionA_DoesNotDeleteWorkingManagedFiles_AndRestoresCleanAbsence(failIndex: 2)` — `Assert.Equal() Failure: Collections differ | Expected: ["d3d11.dll", "data/sentinel.pak", "dxgi.dll", "dxvk.conf", "Game.exe"] | Actual: ["data/sentinel.pak", "Game.exe"]`
  7. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedUpdate_MixedDllAndConfigStates_IncludingNewlyAddedConfigOnUpdate_RecoversExactVersionA_AndRestoresNative(failIndex: 0)` — `Assert.Equal() Failure: Values differ | Expected: FailedSafely | Actual: AttentionRequired`
  8. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedUpdate_MixedDllAndConfigStates_IncludingNewlyAddedConfigOnUpdate_RecoversExactVersionA_AndRestoresNative(failIndex: 1)` — `Assert.Equal() Failure: Values differ | Expected: FailedSafely | Actual: AttentionRequired`
  9. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedUpdate_MixedDllAndConfigStates_IncludingNewlyAddedConfigOnUpdate_RecoversExactVersionA_AndRestoresNative(failIndex: 2)` — `Assert.Equal() Failure: Values differ | Expected: FailedSafely | Actual: AttentionRequired`
  10. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedReapply_WithValidSuppliedBaseline_RecoversExactPreReapplyState_PreservesNativeBackups_AndRestoresNative(failIndex: 0)` — `Assert.Equal() Failure: Values differ | Expected: FailedSafely | Actual: AttentionRequired`
  11. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedReapply_WithValidSuppliedBaseline_RecoversExactPreReapplyState_PreservesNativeBackups_AndRestoresNative(failIndex: 1)` — `Assert.Equal() Failure: Values differ | Expected: FailedSafely | Actual: AttentionRequired`
  12. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedReapply_WithValidSuppliedBaseline_RecoversExactPreReapplyState_PreservesNativeBackups_AndRestoresNative(failIndex: 2)` — `Assert.Equal() Failure: Values differ | Expected: FailedSafely | Actual: AttentionRequired`
  13. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedRestore_AfterEarlyTargetMutation_RecoversManagedVersionAIncludingOriginallyAbsentFiles_ThenSubsequentRestoreSucceeds(failIndex: 0)` — `Assert.Equal() Failure: Values differ | Expected: FailedSafely | Actual: AttentionRequired`
  14. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FailedRestore_AfterEarlyTargetMutation_RecoversManagedVersionAIncludingOriginallyAbsentFiles_ThenSubsequentRestoreSucceeds(failIndex: 1)` — `Assert.Equal() Failure: Values differ | Expected: FailedSafely | Actual: AttentionRequired`
  15. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.RecoveryFailure_WhenUndoReadOrWriteFails_ReturnsUnresolvedRecovery_AndRetainsOperationEvidenceAcrossLaterTransactions` — `Expected unresolved operation directory to be retained.`
  16. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.FirstFileCopyException_BeforeAfterApplyHook_IsClassifiedAsAttempted_AndEntersRecoveryInsteadOfSafeAbort` — `First-file Apply exception must trigger recovery rather than returning SafeAbort.`
  17. `DXVKCompanion.PhaseATests.FailedOperationRecoveryTests.ValidationRefusalBeforeTargetMutation_PreservesTargetAndStorageSnapshotsWithoutStrayBackups` — `Assert.Equal() Failure: Collections differ | Expected: ["sentinel.txt"] | Actual: ["inst-abort/d3d11.dll", "sentinel.txt"]`
  18. `DXVKCompanion.PhaseATests.MultiFileTransactionEngineTests.RepeatedUpdateFailure_PreservesSuppliedNativeBackupAndPreUpdateTargets` — `Assert.Equal() Failure: Values differ | Expected: FailedSafely | Actual: AttentionRequired`
  19. `DXVKCompanion.PhaseATests.SafeDeploymentIntegrationTests.DxvkInstaller_And_DxvkRollback_FailedUpdateReapplyAndRestore_RecoverPreOperationStateAndPreserveNativeBaseline` — `Assert.True() Failure | Expected: True | Actual: False`

---

### 3. Implementation Details

1. **`src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs`**:
   - **Two-phase validation and preparation**:
     - Phase 1 validates all `request.Files` (`ValidateRequest`, root containment for target/backup/undo paths, `ValidateTargetExpectation`, source file existence and identity, and restore managed/original SHA-256 baseline checks) before creating any directory or file in `_transactionStoreRoot`.
     - Phase 2 creates an isolated per-operation directory `.transactions/<transactionId>` under `_transactionStoreRoot` containing `plan.json` and `undo/<relativePath>`.
   - **Separation of per-operation undo from long-lived native baseline (AC 1, AC 4)**:
     - Every target file with `PreOperationState == OriginalFileState.Existing` in `Install`, `Update`, `Reapply`, or `Restore` is copied to `.transactions/<transactionId>/undo/<relativePath>` and verified against `PreOperationIdentity` before any target mutation begins.
     - Native backups at `BackupRelativePath` are created only when `file.OriginalState == OriginalFileState.Existing` and `!File.Exists(backupPath)` (`overwrite: false`), tracked in `createdNativeBackups`. Existing native backups at `BackupRelativePath` are never overwritten during per-operation undo capture or subsequent `Install`/`Update`/`Reapply` operations.
   - **First-file partial-copy classification and targeted recovery (AC 2, AC 5)**:
     - **Code-level reasoning for AC 5**: `anyMutationAttempted = true` and `lastAttemptedIndex = index` are assigned immediately before `Apply(item, request.Operation)` is invoked. Any exception thrown during `File.Copy` or `File.Delete` on `index == 0` (before `_hooks.AfterApply` is reached) is classified as an attempted target mutation and enters the recovery block rather than returning `SafeAbort`.
     - `RecoverAttempted(prepared, lastAttemptedIndex)` iterates in reverse from `Math.Min(lastAttemptedIndex, prepared.Count - 1)` down to `0` (invoking `_hooks.DuringRecovery`), restoring `PreOperationState.Existing` files from their verified `UndoPath` copy and deleting `PreOperationState.DidNotExist` files created during the failed operation without rewriting or deleting untouched target files (`index > lastAttemptedIndex`).
     - `VerifyPreOperationState(prepared)` then verifies that every prepared target (`0 .. prepared.Count - 1`, including untouched targets) matches its pre-operation state and `PreOperationIdentity` before returning `FailedSafely` / `SafeFailure`.
   - **Artifact isolation, cleanup, and unresolved-recovery evidence retention (AC 3, AC 4, AC 6)**:
     - On `Committed`: `CleanupOperationArtifacts` removes `.transactions/<transactionId>` (and `.transactions` if empty) while preserving `createdNativeBackups`.
     - On `Aborted` or verified `FailedSafely`: `CleanupOperationArtifacts` and `CleanupCreatedNativeBackups` remove `.transactions/<transactionId>` and any native backups newly created during the failed/aborted operation (pruning empty parent directories up to `_transactionStoreRoot`), leaving pre-existing native backups, sibling `.recovery.json` records, and earlier `.transactions/<otherTxId>` directories untouched.
     - On `AttentionRequired` / `UnresolvedRecovery`: `.transactions/<transactionId>` (`plan.json` and `undo/<relativePath>`) is retained for inspection/recovery.

2. **Regression Test Suites**:
   - `tests/DXVKCompanion.PhaseA.Tests/FailedOperationRecoveryTests.cs`: Engine-level full directory snapshot tests covering all-existing, all-absent, and mixed failed updates (`failIndex = 0, 1, 2`), failed reapply with supplied baseline (`failIndex = 0, 1, 2`), failed restore after early target deletion/overwrite (`failIndex = 0, 1`), unresolved recovery evidence retention across subsequent transactions, first-file `Apply` copy exception recovery before `AfterApply`, and validation refusal before target mutation without stray backups.
   - `tests/DXVKCompanion.PhaseA.Tests/MultiFileTransactionEngineTests.cs`: Added `RepeatedUpdateFailure_PreservesSuppliedNativeBackupAndPreUpdateTargets`.
   - `tests/DXVKCompanion.PhaseA.Tests/SafeDeploymentIntegrationTests.cs`: Added `DxvkInstaller_And_DxvkRollback_FailedUpdateReapplyAndRestore_RecoverPreOperationStateAndPreserveNativeBaseline` exercising real `GameLibraryStore`, `DxvkInstaller`, and `DxvkRollback` across install, failed update, failed reapply, failed restore, and final native restore.

---

### 4. Verification & Observability

- **Local host execution**: `NOT RUN (pending human authorization)` under `AGENTS.md`.
- **Cloud CI verification**:
  - Red baseline: GitHub Actions [Run `39098892503`](https://github.com/Tiflit/DXVK-Companion/actions/runs/39098892503) (`0aaf03874d51f8c0a62ec594655d348ec19b8875`) — `total=873, passed=854, failed=19`.
  - Scope CI on first PR run: [Run `39098892497`](https://github.com/Tiflit/DXVK-Companion/actions/runs/39098892497) — passed (`success`) on first PR run.
- **Workflow Observability**:
  - **Single uninterrupted engineering pass**: Yes (`0` human stops).
  - **Interruptions / triggers / classification**: None (`0` interruptions; all implementation and verification steps were within delegated authority and allowed paths).
  - **Red-baseline defect detection**: Yes (`19` failing test cases caught the exact defects on unmodified `MultiFileTransactionEngine.cs` prior to production edits).
  - **Out-of-scope observations**: `0` (Issue `#165` remains queued and untouched as instructed).
