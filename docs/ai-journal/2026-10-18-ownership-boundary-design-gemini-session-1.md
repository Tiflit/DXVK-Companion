# Session Journal: Issue #169 — Ownership-Save Failure Reproductions & Commit/Recovery Boundary Design (Session 1)

- **Date:** 2026-10-18
- **Agent:** Gemini / Antigravity
- **Task:** [Issue #169](https://github.com/Tiflit/DXVK-Companion/issues/169) — *[Safety] Reproduce ownership-save failures and define the commit/recovery boundary*
- **Base `main` SHA:** `fb598add34d3e4eb936c714dc7be6e5214e6318a`
- **Trusted Policy SHA (`AGENTS.md`, `docs/AI-WORKFLOW-KERNEL.md`, `docs/AI-ENFORCEMENT-REGISTER.md`, `docs/AI-DEVELOPMENT-WORKFLOW.md`):** `f3e9974371cb08d1dd99192bd314a135010594ed`
- **Branch:** `test/issue-169-ownership-commit-recovery`

---

## Completed Work

1. **Fresh-Session Verification & Source Inspection:**
   - Verified remote `main` at `fb598add34d3e4eb936c714dc7be6e5214e6318a` (merge commit of [PR #168](https://github.com/Tiflit/DXVK-Companion/pull/168) / [Issue #166](https://github.com/Tiflit/DXVK-Companion/issues/166)) and policy identity `f3e9974371cb08d1dd99192bd314a135010594ed`.
   - Inspected `src/DXVKCompanion/DXVK/DxvkInstaller.cs`, `src/DXVKCompanion/DXVK/DxvkRollback.cs`, `src/DXVKCompanion/DXVK/DxvkManager.cs`, `src/DXVKCompanion/Storage/GameLibraryStore.cs`, `src/DXVKCompanion/Storage/ProfileStore.cs`, `src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs`, and `src/DXVKCompanion/Storage/ManagedFileInspector.cs` without modifying any production code.
2. **Executable Reproducer Suite (`tests/DXVKCompanion.PhaseA.Tests/OwnershipPersistenceFailureTests.cs`):**
   - Authored five deterministic tests using existing internal seams (`GameLibraryStore.CustomCommitReplace` and `MultiFileTransactionTestHooks`) and isolated `SyntheticTestDirectory` fixtures with no network requests:
     1. `FirstInstall_WhenPostFileOwnershipSaveFailsBeforeReplace_CompensatesTargetFilesAndKeepsInMemoryAndDiskStoreConsistent` (expected-red regression oracle on `main`).
     2. `UpdateAfterInstallA_WhenPostFileOwnershipSaveFailsBeforeReplace_RecoversVersionAFilesAndPreservesRestoreCapability` (expected-red regression oracle on `main`).
     3. `UnresolvedEngineRecovery_OnManagedInstallationWithWritableStore_PersistsAttentionRequiredAndLastRefusalReason` (expected-red regression oracle on `main`).
     4. `Restore_WhenPostFileOwnershipSaveFailsBeforeReplace_AvoidsUnrecoverableSplitBrainBetweenTargetFilesAndStore` (expected-red regression oracle on `main`).
     5. `Characterization_PostReplaceException_WhenCommitReplaceMovesFileThenThrows_MetadataIsAlreadyDurableOnDisk` (labeled characterization test proving `game-library.json` is already durable on disk when `File.Move` completes before an exception is thrown).
3. **Commit & Recovery Boundary Design (`docs/design/OWNERSHIP-COMMIT-RECOVERY.md`):**
   - Documented the complete 7-column state table across `Install`, `Update`, `Reapply`, and `Restore`, the narrow deferred-undo-commit + pre-mutation snapshot design (requiring zero schema/format changes), comparison against an additive intent record, exact rules for all six failure/recovery cases, startup/restart behavior with `ManagedFileInspector`, the allowed-path proposal for the follow-up implementation issue, and the mapping from each regression test to its acceptance criterion.

---

## Changed and Uncommitted Files

- `tests/DXVKCompanion.PhaseA.Tests/OwnershipPersistenceFailureTests.cs` (added)
- `docs/design/OWNERSHIP-COMMIT-RECOVERY.md` (added)
- `docs/ai-journal/2026-10-18-ownership-boundary-design-gemini-session-1.md` (added)

---

## Verification and Evidence

- **Local Execution:** `NOT RUN (pending human authorization)` — per `AGENTS.md` and [Issue #169](https://github.com/Tiflit/DXVK-Companion/issues/169), Cloud CI is the only authorized verification route.
- **Cloud CI (Expected-Red Draft PR):** Pending initial push and draft PR creation; will be updated with exact CI run IDs, tested merge checkout SHA, and TRX test totals once the run completes.

---

## Open Findings and Decisions

- **No human decision blocker required for `#169`:** Option A (retaining `#166`'s verified per-operation undo scope through `GameLibraryStore.Save`, reverting in-memory `GameInstallation` from a pre-mutation snapshot on pre-replace failure, verifying on-disk bytes to distinguish post-replace exceptions, and persisting `RestorationState.AttentionRequired` + `LastRefusalReason` on unresolved recovery) resolves all five cases without any schema or public-interface change.
- **Follow-up implementation scope:** Ready to be packaged into the follow-up production implementation issue once Codex reviews the expected-red tests and `docs/design/OWNERSHIP-COMMIT-RECOVERY.md`.

---

## Product Trial Interruptions

- **Interruptions encountered during `#169`:** `0` (none). All work proceeded within the delegated `### Allowed paths` and approved cloud-CI verification profile.

---

## Next Owner and Action

- **Next Owner:** Codex (reviewer) / Human coordinator.
- **Next Action:** Review the expected-red draft PR for [Issue #169](https://github.com/Tiflit/DXVK-Companion/issues/169) (`OwnershipPersistenceFailureTests.cs` and `docs/design/OWNERSHIP-COMMIT-RECOVERY.md`), while keeping `#165` queued.
