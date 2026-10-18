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

## Response to Codex Grouped Review (Repairs R1–R4)

Codex provided a detailed grouped review on PR #177 ([Comment 6256230302](https://github.com/Tiflit/DXVK-Companion/pull/177#issuecomment-6256230302)). All four review findings were addressed:

### R1: Test Oracle Consistency, Cleanup & Concurrency Handshake
- **Fixture 1 (`RollbackUndoingCommittedBytes`):** Eliminated the contradictory assertion demanding both `Assert.False(op2Result)` and version 2.7 bytes. Defined a clean, consistent oracle: under the safe exclusion design, Operation 2 is refused at the lease boundary, Operation 1 recovers to pre-operation 2.5, and disk/metadata remain consistent at 2.5. On unpatched production, Operation 2 is not refused (`op2Result == true`), deterministically failing as expected-red. Added verification that the deliberate injected fault (`IOException`) was processed and verified by Operation 1.
- **Fixture 2 (`ConcurrentApply`):** Established a real committed baseline (version 2.5) before constructing both concurrent service graphs/stores. This ensures Operation 2 does not fail on stale baseline creation, but genuinely attempts concurrent target mutation. On unpatched production, it fails on missing "in progress" lease refusal.
- **Fixture 3 (`ManagerLevel_ConcurrentDisableAndEnable`):** Honestly labeled the observed protection on unpatched code (mid-transaction tamper detection / hash mismatch) and structured the oracle around entry-boundary lease refusal.
- **Fixture 4 (`IndependentInstallations`):** Instrumented a deterministic two-way bounded handshake via `MultiFileTransactionTestHooks.AfterApply` verifying that both independent operations actively mutate files simultaneously without mutual blocking.
- **Cleanup & Safety:** All fixtures guarantee barrier release in `finally` blocks, bound all task waits with 10-second timeouts, avoid blanket exception swallows, and ensure zero background tasks leak.

### R2: Two-Tier Lock Hierarchy & Async-Safe Lease Primitive
- Defined a strict Two-Tier Hierarchy:
  - **Tier 1 Installation Lease (Fine-Grained):** Held across pre-validation, activity check, target mutation, and metadata commit/recovery.
  - **Tier 2 Storage Directory Lock (Coarse-Grained):** Held briefly only during atomic read-modify-write phases on `game-library.json`.
  - Acquisition order is strictly `Tier 1 -> Tier 2`, preventing deadlocks.
- Resolved the thread-affinity flaw of `System.Threading.Mutex` across `await` boundaries by specifying an exclusive OS `FileStream` lock (`FileShare.None`, `FileOptions.DeleteOnClose`) on `<storageDir>/leases/<hash>.lease`. This primitive is thread-agnostic, kernel-enforced across all processes, privilege-free, and automatically cleaned up on crash.
- Defined `IOperationLeaseCoordinator` as the single enforceable lease owner with internal `IOperationLease` token passing to prevent bypass or nested reentrancy deadlocks.

### R3: Path Identity, Realistic Process Model & Accurate Boundary
- Corrected factual claim regarding `GameInstallation.NormalizeInstallationPath` (it does not resolve junctions or change case); defined uppercase canonicalization and SHA256 hashing.
- Specified Candidate Relevance Filtering in the process evidence model: only processes matching registered game executables are evaluated; unrelated elevated system processes (`services.exe`, `svchost.exe`) are ignored and do not trigger false `Unknown` states.
- Defined 7 deterministic fake-inspector test scenarios for the internal test seam.
- Removed unmeasured timing claims ("50 ms", "physical minimum") and honestly bounded cooperative exclusion versus external process start races.

### R4: Source Call Flows, Preservation & File List
- Corrected inventory against actual source: `ProcessAllPendingActionsAsync` directly dispatches operations; `RestoreAllAsync` checks running state before queueing.
- Specified preservation of persisted `PendingAction` in the store and transient intents in `_pending` when operations are deferred due to busy/running/unknown states.
- Enumerated the exact production files to add and modify in Phase B.
- Separated delegated internal choices from human authority decisions (contention policy, single-instance policy, and `Unknown` evidence policy).

---

## Cloud CI Verification History

- **PR:** [#177](https://github.com/Tiflit/DXVK-Companion/pull/177) — `[Draft] [Do not merge] test(safety): reproduce overlapping operations and design installation activity/exclusion boundary (#176)`
- **Run 1:** `39172485694` (Job `117808277752`) — Initial draft test suite.
- **Run 2:** `39172910470` (Job `117809521101`) — Baseline 907 tests pass; 1 positive control passes; 3 expected-red tests fail.
- **Current Head:** Calibrated fixtures and refined technical design addressing Codex R1–R4 review findings.

---

## Changed Files

- `tests/DXVKCompanion.PhaseA.Tests/OperationExclusionBoundaryTests.cs`
- `docs/design/OPERATION-EXCLUSION-AND-ACTIVITY-BOUNDARY.md`
- `docs/ai-journal/2026-10-18-operation-exclusion-design-gemini-session-1.md`
