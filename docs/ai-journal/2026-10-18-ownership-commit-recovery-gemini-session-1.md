# Session Journal: Issue #171 — Bounded In-Process Ownership Commit & Recovery Repair (Session 1)

- **Date:** 2026-10-18
- **Agent:** Gemini / Antigravity
- **Task:** [Issue #171](https://github.com/Tiflit/DXVK-Companion/issues/171) — *[Safety] Implement bounded in-process ownership commit and recovery repair*
- **Base `main` SHA:** `fb598add34d3e4eb936c714dc7be6e5214e6318a`
- **Tests/Design Baseline ([Draft PR #170](https://github.com/Tiflit/DXVK-Companion/pull/170)):** `860140e5c3fbc7fc03047db532a6495734a568ce` (Run `39149282765`: `total=883, passed=877, failed=6, skipped=0`)
- **Trusted Policy SHA (`AGENTS.md`, `docs/AI-WORKFLOW-KERNEL.md`, `docs/AI-ENFORCEMENT-REGISTER.md`, `docs/AI-DEVELOPMENT-WORKFLOW.md`):** `f3e9974371cb08d1dd99192bd314a135010594ed`
- **Branch:** `fix/issue-171-ownership-commit-recovery`

---

## Completed Work

1. **Fresh-Session Verification & Branch Setup:**
   - Verified remote `main` at `fb598add34d3e4eb936c714dc7be6e5214e6318a`, reviewed expected-red baseline `860140e5c3fbc7fc03047db532a6495734a568ce` on `test/issue-169-ownership-commit-recovery` ([Draft PR #170](https://github.com/Tiflit/DXVK-Companion/pull/170)), and confirmed policy identity `f3e9974371cb08d1dd99192bd314a135010594ed`.
   - Created production branch `fix/issue-171-ownership-commit-recovery` from `860140e5c3fbc7fc03047db532a6495734a568ce` while leaving `test/issue-169-ownership-commit-recovery` and [Draft PR #170](https://github.com/Tiflit/DXVK-Companion/pull/170) untouched.
2. **Three-Way Ownership Persistence Classification & Session-Scoped Uncertainty Guard (`GameLibraryStore.cs`, `ManagedFileInspector.cs`):**
   - Implemented `OwnershipPersistenceOutcome` (`Committed`, `ProvenNotCommitted`, `Unknown`) and `TrySaveWithOutcome(GameInstallation)` in `src/DXVKCompanion/Storage/GameLibraryStore.cs`.
   - Captured pre-write baseline (`PreWriteExisted`, `PreWriteBytes`) and serialized `ExpectedPostWriteBytes`. Distinguished positive destination absence (`!File.Exists` when directory is accessible and `PreWriteExisted == false`) from unreadable/locked files (`IOException` / `UnauthorizedAccessException`), malformed JSON, conflicting external writes, and newer `SchemaVersion`.
   - Latched session-scoped `HasUncertainPersistence` / `UncertainPersistenceReason` on `OwnershipPersistenceOutcome.Unknown` so subsequent `Save`, `SaveAll`, `GetOrCreateInstallation`, and `RecordDetectionSnapshot` calls refuse in the same session, and guarded `ManagedFileInspector.InspectInstallation` / `InspectAll` so background inspection cannot overwrite uncertain on-disk evidence or clear in-memory `AttentionRequired`.
   - Added deep entry-point snapshot helpers (`CreateSnapshot`, `RestoreSnapshot`), `CaptureCurrentLibraryBaseline`, and `TrySaveHealthAfterFailedCompensation` (which persists `RestorationState.AttentionRequired` and `LastRefusalReason` only when a pre-existing valid `game-library.json` still matches the expected pre-write byte baseline).
3. **Commit-Scope Retention & Transactional Baseline Replacement (`MultiFileTransactionEngine.cs`):**
   - Added internal `ExecuteWithCommitScope` on `MultiFileTransactionEngine` so `.transactions/<txId>/` (`plan.json`, `undo/`, `prior-backups/`, `native-backups/`) stays intact while the caller's ownership persistence callback executes and is classified.
   - Added transactional `ReplaceExistingBackup` and `ExpectedPriorBackupIdentity` handling so `Reapply(updateBaseline: true)` preserves verified prior canonical backups in `.transactions/<txId>/prior-backups/<relPath>` before staging and publishing new baselines, and restores them via `CompensateBackups` on any early/pre-target failure or `ProvenNotCommitted` ownership-save failure.
4. **Installer, Rollback, and Manager Commit/Recovery Wiring (`DxvkInstaller.cs`, `DxvkRollback.cs`, `DxvkManager.cs`, `ProfileStore.cs`):**
   - Wired `ApplyToGameAsync`, `ReapplyAsync`, `AdoptExisting`, and `RestoreOriginalDllsAsync` to check `TryCheckCanModifyLibrary`, capture entry-point `GameInstallation` snapshots before any mutation, execute file and ownership persistence inside `ExecuteWithCommitScope`, compensate target files and backups on `ProvenNotCommitted`, retain `.transactions/<txId>` evidence on `Unknown` or `UnresolvedRecovery`, and propagate `LastRefusalReason`.
   - Updated `DxvkManager` and `ProfileStore` so post-commit `ProfileStore.Save` failures are logged as non-fatal warnings without failing or reverting committed operations, and subsequent `DxvkManager` operations reconcile `DxvkEnabled` and `DxvkVersion` from authoritative `GameLibraryStore` state.
5. **Regression Suite Expansion (`OwnershipPersistenceFailureTests.cs`, `OWNERSHIP-COMMIT-RECOVERY.md`):**
   - Transitioned `Characterization_PostReplaceException_WhenCommitReplaceMovesFileThenThrows_MetadataIsAlreadyDurableOnDisk` to `Assert.True(installOk)` as authorized in the `#171` contract.
   - Strengthened `PostReplaceException_WhenReadbackIsTemporarilyUnavailable_ClassifiesOutcomeAsUnknown_RetainsEvidenceAndAvoidsBlindCompensation` and added the 5 required regression test groups (12 test methods total in `OwnershipPersistenceFailureTests.cs`).

6. **PR #172 Grouped Implementation Review Repair Cycle (Findings R1, R2, R3 & Prior Backup Compensation):**
   - **R1 (Authoritative library baseline & external change refusal):**
     - Tracked authoritative library baseline (`_lastCommittedExisted`, `_lastCommittedLibraryBytes`, `_loadedFromBrokenFileRecovery`) from `Load()` and every committed write.
     - Enforced `VerifyDiskMatchesAuthoritativeBaselineLocked` before any library modification (`TryCheckCanModifyLibrary`, `EnsureNotRefusedLocked`, `ExecuteClassifiedCommitLocked`) so external disk modifications (valid schema-1 conflicting JSON, malformed JSON, unexpected appearance/deletion) latch `HasUncertainPersistence` and refuse without overwriting or staging. Preserved `IncompatibleLibrarySchemaException` on newer-schema appearance.
     - Fixed `PostReplaceException_ReadbackClassification_DistinguishesMalformedConflictingNewerSchemaAndPositivelyMissingFiles` (eliminated `File.Delete` hack and changed Sub-case 3 to valid schema-1 conflicting JSON) and eliminated `File.Delete` hack in `UnresolvedRecovery_WhenNoPriorLibraryFileExistedOrBaselineChanged_PreservesInMemoryHealthWithoutWritingIncompleteLibraryFile`.
     - Added comprehensive regression tests `PreCommitCallback_WhenExternalLibraryChangeOccurs_PreservesDiskBytes_RetainsRecoveryEvidence_AndBlocksSessionWrites` and `PreOperationExternalLibraryChange_RefusesBeforeModifyingTargetOrBackups_AndProtectsSession`.
   - **R2 (Universal uncertain-save protection across all persistence/exception paths):**
     - Changed `OwnershipPersistenceOutcome` default value to `Unknown = 0` (`Unknown = 0, Committed = 1, ProvenNotCommitted = 2`).
     - Unified `GetOrCreateInstallation`, `RecordDetectionSnapshot`, `Save`, `SaveAll`, and `TrySaveWithOutcome` through `ExecuteClassifiedCommitLocked`.
     - Wrapped commit callbacks in `DxvkInstaller.cs` and `DxvkRollback.cs` in `try/catch` recording unexpected exceptions on `_gameLibraryStore` via `RecordUnexpectedCommitCallbackException` and latching `HasUncertainPersistence`.
     - Added regression tests `PublicSave_WhenPostReplaceLockedReadbackOccurs_LatchesUncertainPersistence_AndBlocksSubsequentSaveAllOverwrite`, `InitialCheckpoint_WhenGetOrCreateInstallationAmbiguityOccurs_LatchesUncertainPersistence_AndBlocksSubsequentOperations`, and `UnexpectedCommitCallbackException_LatchesUncertainPersistence_AndRetainsRecoveryEvidence`.
   - **R3 (Entry snapshot restoration on preparation refusals/exceptions):**
     - Deferred speculative mutations to `installation.Configuration` and `installation.Executables` in `ApplyToGameAsync` and `ReapplyAsync` until inside the commit scope callback.
     - Ensured entry-point snapshot `preOperationSnapshot` is restored across all preparation refusals and outer `catch (Exception ex)` blocks in `ApplyToGameAsync`, `ReapplyAsync`, and `AdoptExisting`.
     - Added regression test `PreparationException_WhenDxvkConfLocked_RestoresEntrySnapshotsInApplyAndReapply_AndPreventsPublishingSpeculativeSettings` verifying `dxvk.conf` lock during preparation restores in-memory configuration, does not publish rejected settings via `SaveAll()`, and subsequent retry succeeds.
   - **Prior Backup Compensation Failure Verification:**
     - Added regression test `ReapplyUpdateBaseline_WhenPriorBackupCompensationFails_RetainsPriorBackupEvidenceAndTransitionsToAttentionRequired` verifying retained `.transactions/<txId>/prior-backups/` evidence and transition to `AttentionRequired`.

---

## Changed Files

- `src/DXVKCompanion/Storage/GameLibraryStore.cs`
- `src/DXVKCompanion/Storage/ManagedFileInspector.cs`
- `src/DXVKCompanion/Storage/ProfileStore.cs`
- `src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs`
- `src/DXVKCompanion/DXVK/DxvkInstaller.cs`
- `src/DXVKCompanion/DXVK/DxvkRollback.cs`
- `src/DXVKCompanion/DXVK/DxvkManager.cs`
- `tests/DXVKCompanion.PhaseA.Tests/OwnershipPersistenceFailureTests.cs`
- `docs/design/OWNERSHIP-COMMIT-RECOVERY.md`
- `docs/ai-journal/2026-10-18-ownership-commit-recovery-gemini-session-1.md`

---

## Verification and Evidence

- **Local Execution:** `NOT RUN (pending human authorization)` — per `AGENTS.md` and [Issue #171](https://github.com/Tiflit/DXVK-Companion/issues/171), Cloud CI is the only authorized verification route.
- **Prior Expected-Red Baseline ([Draft PR #170](https://github.com/Tiflit/DXVK-Companion/pull/170)):**
  - `Build and Test` [Run `39149282765`](https://github.com/Tiflit/DXVK-Companion/actions/runs/39149282765) on head `860140e5c3fbc7fc03047db532a6495734a568ce` (merge checkout `14c98931edba4e3ffb170002409128531bdc9238`): `877 passed, 6 failed, 0 skipped (total 883)`.
- **Initial Production Head ([PR #172](https://github.com/Tiflit/DXVK-Companion/pull/172)):**
  - `Build and Test` [Run `39159343965`](https://github.com/Tiflit/DXVK-Companion/actions/runs/39159343965) on head `2bea4113365611ea5d0bac7b28f8d37d2c2fa093`: `888 passed, 0 failed, 0 skipped`.
- **Repaired Production Head ([PR #172](https://github.com/Tiflit/DXVK-Companion/pull/172)):**
  - Cloud CI results and test totals will be updated in the PR body upon completion of the CI workflows.

---

## Open Findings and Decisions

- **No open blocking findings or unsettled product decisions within `#171` scope.** Cross-restart journal replay after abrupt process termination (`Kill`/power loss) and `ManageGamesWindow.RefreshList` reconciliation remain explicitly out of scope as documented in `docs/design/OWNERSHIP-COMMIT-RECOVERY.md` §6.

---

## Product Trial Interruptions

- **Interruptions encountered during `#171` review repairs:** `0` (none). All implementation, test expansion, and documentation updates were completed within the delegated `### Allowed paths` and approved cloud-CI verification route.

---

## Next Owner and Action

- **Next Owner:** Codex / Coordinator.
- **Next Action:** Grouped review of the repaired production PR #172 for [Issue #171](https://github.com/Tiflit/DXVK-Companion/issues/171), while keeping `#165` queued and `#94` paused.

