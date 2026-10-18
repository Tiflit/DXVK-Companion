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
- R1: Replaced contradictory oracles; established genuine committed baseline; labeled observed protection honestly; instrumented two-way overlap handshake; guaranteed barrier release in `finally`; bounded task waits.
- R2: Two-tier lock hierarchy; async-safe `FileStream` primitive overcoming thread affinity of `Mutex`; `IOperationLeaseCoordinator` single enforceable owner with internal tokens.
- R3: Factual correction of `NormalizeInstallationPath`; candidate relevance filtering in process model; 7 deterministic fake-inspector test scenarios; removed unmeasured claims.
- R4: Corrected inventory against actual source; preservation of pending intents in store on deferred states; exact Phase B file list; clear separation of delegated choices from human authority decisions.

---

## Response to Codex Focused Delta Review ([Comment 6256379288](https://github.com/Tiflit/DXVK-Companion/pull/177#issuecomment-6256379288))

Codex accepted the initial delta and identified five remaining areas for refinement:

1. **Target-Scoped Coordination Namespace (Independent of Storage Root):**
   - Moved the Tier 1 Installation Lease out of `<storageDir>/leases/` into a shared, discoverable coordination namespace: `%LOCALAPPDATA%\DXVKCompanion\locks\target_<CanonicalHash>.lock` (with `DXVK_COORDINATION_DIR` environment override for test sandboxing).
   - Clarified the lifecycle of `FileStream` (`FileShare.None`, `FileOptions.DeleteOnClose`), handling transient `ERROR_DELETE_PENDING` contention.
   - Distinguished handle release on crash from transaction recovery: OS handle closure releases the lease, but does not restore corrupted files. Added pre-operation safety inspection (checking for orphaned `.transactions/` or hash mismatches from a crashed predecessor).
   - Explicitly defined unsupported boundaries: cross-session Windows users, UNC network paths, read-only volumes, and non-locking filesystems are conservatively refused.

2. **Simpler Safe Scope for Metadata (No Unreviewed 3-Way Merge):**
   - Replaced the late merge sketch with the recommended simpler safe scope: serialize the full mutation/commit scope for a shared store root.
   - Authoritative state is acquired and checked for freshness BEFORE target mutation. If disk changed or unowned mutations are present, stale instances conservatively refuse before touching target files.
   - Independent store roots are disjoint on Tier 2 and proceed concurrently without serialization.
   - Strict preservation of #171 uncertainty guard (`AttentionRequired`, uncommitted evidence).
   - Removed nonexistent fields like `ActiveRecovery`; defined full writer file list (`GameLibraryStore.cs`, `ProfileStore.cs`, `DxvkManager.cs`).

3. **Actual Call/API and Path-Boundary Specification:**
   - Corrected method names to match actual production source: `DxvkRollback.RestoreOriginalDllsAsync(GameProfile profile)`.
   - Preserved all existing public signatures unchanged; added internal overloads with `IOperationLease lease`.
   - Explicitly split installer source preparation (`DownloadAndExtractAsync` unleased) from in-lease pre-mutation revalidation.
   - Specified directory ancestor traversal up to volume root to detect junctions/symlinks in parent folders; unsupported aliases conservatively refused.

4. **Evidence Claims & Fixture Hygiene in `OperationExclusionBoundaryTests.cs`:**
   - Asserted native backup bytes and identity in Fixture 1 (`backups/<instId>/d3d11.dll` contains `native-original-d3d11`), verifying backup preservation after rollback.
   - Kept all task handles in outer scope and performed bounded joins after releasing barriers INSIDE `finally` blocks, preventing orphaned tasks accessing disposed synthetic directories if assertions fail.
   - Labeled Fixture 3 honestly as an entry-boundary contract oracle.

5. **Bounded Process & Physical Claims:**
   - Acknowledged candidate-filtering limitation (protects known/registered executables in `Executables`; unregistered differently named siblings are an acknowledged boundary).
   - Specified image name normalization and PID reuse detection with `StartTime`.
   - Highlighted in-lease pre-mutation revalidation catching relaunches during downloads.
   - Replaced unmeasured filesystem claims with bounded pre-mutation activity-check guarantees and explicitly retained external-start races.
   - Clarified explicit triggers for pending action retries (startup scan, process exit notification, user re-request).

---

## Cloud CI Verification History

- **PR:** [#177](https://github.com/Tiflit/DXVK-Companion/pull/177) — `[Draft] [Do not merge] test(safety): reproduce overlapping operations and design installation activity/exclusion boundary (#176)`
- **Run 1:** `39172485694` (Job `117808277752`) — Initial draft test suite.
- **Run 2:** `39172910470` (Job `117809521101`) — Baseline 907 tests pass; 1 positive control passes; 3 expected-red tests fail.
- **Run 3:** `39175104905` (Job `117816031276`) — R1–R4 repaired; 908 passed, 3 failed as expected-red; 2-way handshake verified; scope and hygiene pass.
- **Current Head:** Refined target-scoped coordination namespace, simpler safe metadata scope, ancestor junction traversal, and outer-scope inside-finally task draining.

---

## Changed Files

- `tests/DXVKCompanion.PhaseA.Tests/OperationExclusionBoundaryTests.cs`
- `docs/design/OPERATION-EXCLUSION-AND-ACTIVITY-BOUNDARY.md`
- `docs/ai-journal/2026-10-18-operation-exclusion-design-gemini-session-1.md`
