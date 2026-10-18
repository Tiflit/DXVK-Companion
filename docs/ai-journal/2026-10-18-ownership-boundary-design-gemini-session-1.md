# Session Journal: Issue #169 — Ownership-Save Failure Reproductions & Commit/Recovery Boundary Design (Session 1)

- **Date:** 2026-10-18
- **Agent:** Gemini / Antigravity
- **Task:** [Issue #169](https://github.com/Tiflit/DXVK-Companion/issues/169) — *[Safety] Reproduce ownership-save failures and define the commit/recovery boundary*
- **Base `main` SHA:** `fb598add34d3e4eb936c714dc7be6e5214e6318a`
- **Trusted Policy SHA (`AGENTS.md`, `docs/AI-WORKFLOW-KERNEL.md`, `docs/AI-ENFORCEMENT-REGISTER.md`, `docs/AI-DEVELOPMENT-WORKFLOW.md`):** `f3e9974371cb08d1dd99192bd314a135010594ed`
- **Branch:** `test/issue-169-ownership-commit-recovery`
- **Draft PR:** [PR #170](https://github.com/Tiflit/DXVK-Companion/pull/170)

---

## Completed Work

1. **Fresh-Session Verification & Source Inspection:**
   - Verified remote `main` at `fb598add34d3e4eb936c714dc7be6e5214e6318a` (merge commit of [PR #168](https://github.com/Tiflit/DXVK-Companion/pull/168) / [Issue #166](https://github.com/Tiflit/DXVK-Companion/issues/166)) and policy identity `f3e9974371cb08d1dd99192bd314a135010594ed`.
   - Inspected `src/DXVKCompanion/DXVK/DxvkInstaller.cs`, `src/DXVKCompanion/DXVK/DxvkRollback.cs`, `src/DXVKCompanion/DXVK/DxvkManager.cs`, `src/DXVKCompanion/DXVK/ExistingDxvkDetector.cs`, `src/DXVKCompanion/Storage/GameLibraryStore.cs`, `src/DXVKCompanion/Storage/ProfileStore.cs`, `src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs`, `src/DXVKCompanion/Storage/ManagedFileInspector.cs`, and `src/DXVKCompanion/UI/ManageGamesWindow.cs` without modifying any production code.
2. **Initial Expected-Red Draft & Coordinator Grouped Review (`20ee3a02ade71342c930d640daa2eecbd67a6de5`):**
   - Initial Cloud CI run [`39147944586`](https://github.com/Tiflit/DXVK-Companion/actions/runs/39147944586) on `20ee3a02ade71342c930d640daa2eecbd67a6de5` compiled with zero warnings/errors and reported `877 passed, 4 failed, 0 skipped (total 881)`.
   - Coordinator review ([PR #170 comment `6252756643`](https://github.com/Tiflit/DXVK-Companion/pull/170#issuecomment-6252756643)) requested targeted design and test revisions across three blocking gaps and four consistency items.
3. **Grouped Review Repair (`OwnershipPersistenceFailureTests.cs` & `docs/design/OWNERSHIP-COMMIT-RECOVERY.md`):**
   - **Blocking Gap 1 (`Committed` / `ProvenNotCommitted` / `Unknown` persistence classification):**
     - Defined `internal enum OwnershipPersistenceOutcome { Committed, ProvenNotCommitted, Unknown }` in `docs/design/OWNERSHIP-COMMIT-RECOVERY.md` using both authoritative pre-write evidence (`PreWriteExisted`, `PreWriteBytes`) and `ExpectedPostWriteBytes`.
     - Restricted target and backup compensation strictly to `ProvenNotCommitted` (failure before replacement attempt or positive readback verification of `PreWriteBytes`).
     - Specified `Unknown` handling for unreadable/locked, missing, malformed, or conflicting/newer `_libraryFile` readback: retain `.transactions/<txId>/` (`plan.json`, `undo/`, `prior-backups/`), avoid blind target compensation or a second `GameLibraryStore.Save`, and set `RestorationState.AttentionRequired` + `LastRefusalReason` in memory.
     - Added isolated expected-red fixture `PostReplaceException_WhenReadbackIsTemporarilyUnavailable_ClassifiesOutcomeAsUnknown_RetainsEvidenceAndAvoidsBlindCompensation` in `OwnershipPersistenceFailureTests.cs` using existing seams (`File.Move` followed by a temporary `FileShare.None` lock on `destPath` before throwing `IOException`).
   - **Blocking Gap 2 (Entry-point snapshot & non-destructive `Reapply(updateBaseline: true)` backup staging/compensation):**
     - Specified entry-point `GameInstallation` snapshot capture at Phase 1 before any `Executables`, `Configuration`, `ManagedFiles`, or `GetOrCreateInstallation` mutations.
     - Specified staged, verified, non-destructive baseline replacement in `MultiFileTransactionEngine` (`ReplaceExistingBackup = true`, preserving verified prior canonical backups in `.transactions/<txId>/prior-backups/<relPath>` before staging and publishing the new baseline, and restoring `prior-backups/` on early failure or `ProvenNotCommitted` save failure).
     - Completed explicit native-backup and `.transactions` cleanup assertions in `FirstInstall_WhenPostFileOwnershipSaveFailsBeforeReplace_CompensatesTargetFilesAndNativeBackups_AndKeepsStoreConsistent`.
     - Added expected-red fixture `ReapplyUpdateBaseline_WhenPostFileOwnershipSaveFailsBeforeReplace_RestoresPriorBackupAndPreReapplyTargets_ThenRetryAndRestoreSucceed` covering `Install A` $\to$ external native patch $\to$ `Reapply(updateBaseline: true)` $\to$ post-file metadata failure $\to$ verification of prior backup/targets/in-memory state $\to$ successful retry `Reapply` and `Restore` to the approved new baseline.
   - **Blocking Gap 3 (Bounded in-process repair vs. explicit restart/abrupt-termination & `ProfileStore` limitations):**
     - Rewrote Sections 4.5, 5, and 6 of `docs/design/OWNERSHIP-COMMIT-RECOVERY.md` as a bounded in-process repair without overclaiming cross-store/OS crash atomicity or startup journal reconciliation.
     - Documented exact current behaviors of `ExistingDxvkDetector` (returns `OfficialRelease` without inspecting `backups/` or `.transactions/`), `ManagedFileInspector` (iterates only recorded `ManagedFiles`, so a hard-killed first install with `ManagedFiles.Count == 0` is not detected; can reset `AttentionRequired` when target hashes match `ExpectedManagedSha256`), `DxvkManager.ReassessEffectiveApi` (extended in proposed scope to reconcile `DxvkEnabled`/`DxvkVersion` on `DxvkManager` entry points), and `ManageGamesWindow.RefreshList` (hydrates only missing profiles unless `ManageGamesWindow.cs` is added to scope).
   - **Consistency Points:**
     - Changed `MultiFileTransactionEngine.ExecuteWithCommitScope` to `internal`, preserving `public SafetyTransactionResult Execute(MultiFileTransactionRequest request)` as the sole public signature.
     - Explicitly authorized the follow-up implementation contract to transition `Assert.False(installOk)` $\to$ `Assert.True(installOk)` in `Characterization_PostReplaceException_WhenCommitReplaceMovesFileThenThrows_MetadataIsAlreadyDurableOnDisk` once readable post-replace verification returns `OwnershipPersistenceOutcome.Committed`.
     - Added the 5-phase normal execution sequence and the distinction between single-file namespace replacement, coordinated in-process compensation, and OS power-loss durability in Section 2.1.

---

## Changed and Uncommitted Files

- `tests/DXVKCompanion.PhaseA.Tests/OwnershipPersistenceFailureTests.cs` (added/updated: 7 tests total — 6 expected-red regression oracles + 1 readable post-replace characterization test)
- `docs/design/OWNERSHIP-COMMIT-RECOVERY.md` (added/updated)
- `docs/ai-journal/2026-10-18-ownership-boundary-design-gemini-session-1.md` (added/updated)

---

## Verification and Evidence

- **Local Execution:** `NOT RUN (pending human authorization)` — per `AGENTS.md` and [Issue #169](https://github.com/Tiflit/DXVK-Companion/issues/169), Cloud CI is the only authorized verification route.
- **Prior Cloud CI Baseline (`20ee3a02ade71342c930d640daa2eecbd67a6de5`):**
  - `Build and Test` [Run `39147944586`](https://github.com/Tiflit/DXVK-Companion/actions/runs/39147944586) on merge checkout `2214bb94de319794e331a83631629b7bdc618345`: `877 passed, 4 failed, 0 skipped (total 881)`.
- **Repaired Draft Cloud CI:** Final revision-bound CI run ID, merge checkout SHA, and TRX totals (`883` expected total: `877 passed, 6 failed, 0 skipped`) will be recorded in the PR #170 completion update once the run finishes.

---

## Open Findings and Decisions

- **Bounded in-process repair requires no schema or public-interface change:** The proposed follow-up implementation scope (`TransactionContracts.cs`, `MultiFileTransactionEngine.cs`, `GameLibraryStore.cs`, `ProfileStore.cs`, `DxvkInstaller.cs`, `DxvkRollback.cs`, `DxvkManager.cs`) resolves all six in-process expected-red regressions within existing schema version `1` and existing public signatures.
- **Explicit human decision boundary for out-of-process / UI startup behavior:**
  - If the human coordinator wants startup crash-journal reconciliation for hard-killed first installs (where `game-library.json` has `ManagedFiles.Count == 0`), `ManagedFileInspector` retention of `AttentionRequired` when `.transactions/<txId>/` exists, or `ManageGamesWindow.RefreshList` reconciliation of existing stale `games.json` rows prior to `DxvkManager` invocation, those require an explicit scope/policy decision beyond the bounded in-process commit/compensation repair (documented in `docs/design/OWNERSHIP-COMMIT-RECOVERY.md` §6).

---

## Product Trial Interruptions

- **Interruptions encountered during `#169`:** `0` (none). All work and review repairs proceeded within the delegated `### Allowed paths` and approved cloud-CI verification profile.

---

## Next Owner and Action

- **Next Owner:** Codex / Coordinator.
- **Next Action:** Focused delta review of the repaired expected-red tests (`OwnershipPersistenceFailureTests.cs`) and design specification (`docs/design/OWNERSHIP-COMMIT-RECOVERY.md`) on [Draft PR #170](https://github.com/Tiflit/DXVK-Companion/pull/170), while keeping `#165` queued and `#94` paused.
