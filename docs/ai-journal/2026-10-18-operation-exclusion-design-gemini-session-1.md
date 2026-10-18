# Session Journal: Issue #176 — Reproduce Overlapping Operations and Design Installation Activity/Exclusion Boundary (Session 1)

- **Date:** 2026-10-18
- **Agent:** Gemini / Antigravity
- **Task:** [Issue #176](https://github.com/Tiflit/DXVK-Companion/issues/176) — *[Safety] Reproduce overlapping operations and design installation activity/exclusion boundary*
- **Base `main` SHA:** `de79a4615b3018e58b036897441f4f388238d6ea`
- **Trusted Policy SHA (`AGENTS.md`, `docs/AI-WORKFLOW-KERNEL.md`, `docs/AI-ENFORCEMENT-REGISTER.md`, `docs/AI-DEVELOPMENT-WORKFLOW.md`):** `f3e9974371cb08d1dd99192bd314a135010594ed`
- **Branch:** `test/issue-176-operation-exclusion-boundary`

---

## Completed Work

1. **Policy & Baseline Verification:**
   - Acquired and derived policy identity `f3e9974371cb08d1dd99192bd314a135010594ed` matching policy checkpoint on verified `main` (`de79a4615b3018e58b036897441f4f388238d6ea`).
   - Inspected machine dashboard Issue #27 and verified clean working tree.
   - Created branch `test/issue-176-operation-exclusion-boundary` from `origin/main`.

2. **Deterministic Expected-Red Concurrency Fixtures (`OperationExclusionBoundaryTests.cs`):**
   - Added comprehensive concurrency regression test suite `tests/DXVKCompanion.PhaseA.Tests/OperationExclusionBoundaryTests.cs` using only existing `MultiFileTransactionTestHooks` and in-memory `TaskCompletionSource` barriers with bounded timeouts (10s) and guaranteed `finally` cleanup:
     - `OverlappingOperations_SameInstallation_RollbackUndoingCommittedBytes_MustBeExcluded`: Demonstrates that when Operation 1 pauses after mutating `d3d11.dll`, Operation 2 is NOT excluded on unchanged production and commits version 2.7 files; when Operation 1 fails and triggers `RecoverAttempted`, Operation 1's rollback silently overwrites Operation 2's committed version 2.7 bytes with pre-operation version 2.5 bytes.
     - `OverlappingOperations_SameInstallation_ConcurrentApply_MustRefuseSecondOperationToPreventMixedPayload`: Demonstrates that concurrent mutations to the same installation root are not refused on unchanged production, creating the risk of mixed payload files and split-brain metadata.
     - `OverlappingOperations_SameInstallation_ManagerLevel_ConcurrentDisableAndEnable_MustBeExcluded`: Demonstrates that concurrent public manager operations across independent service graphs lack mutual exclusion and do not provide an operation-in-progress refusal reason.
     - `OverlappingOperations_IndependentInstallations_ProceedConcurrentlyWithoutMutualInterference`: Passing positive control proving that independent game installations proceed concurrently without mutual blocking or deadlock.

3. **Comprehensive Implementation Design (`OPERATION-EXCLUSION-AND-ACTIVITY-BOUNDARY.md`):**
   - Completed technical design in `docs/design/OPERATION-EXCLUSION-AND-ACTIVITY-BOUNDARY.md`:
     - Comprehensive entry-point inventory table across all 12 direct, requested, batch, startup, exit, and adoption entry points.
     - Operation lease resource identity based on normalized installation root (`GameInstallation.NormalizeInstallationPath`) and lifetime bounded to pre-mutation through verified commit or recovery.
     - Evaluated same-root cross-process lease (`Global\DXVK_Lease_<Hash>`) versus global single-application-instance mutex, recommending the per-installation cross-process lease.
     - Three-state process activity evidence model (`Running`, `ConfirmedNotRunning`, `Unknown`), sibling executable inspection, and honest TOCTOU external process-start boundary analysis.
     - Integration with Issue #171 commit/compensation and store lock hierarchy.
     - Minimal production file additions and internal test seams (`IOperationLeaseCoordinator`, `IProcessActivityInspector`).
     - Architectural decision brief for human merge authority regarding contention behavior (bounded refusal vs. blocking wait).

---

## Changed Files

- `tests/DXVKCompanion.PhaseA.Tests/OperationExclusionBoundaryTests.cs`
- `docs/design/OPERATION-EXCLUSION-AND-ACTIVITY-BOUNDARY.md`
- `docs/ai-journal/2026-10-18-operation-exclusion-design-gemini-session-1.md`
