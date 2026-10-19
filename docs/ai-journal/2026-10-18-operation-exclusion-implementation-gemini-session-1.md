# Activity Journal — Issue #178: Operation Exclusion and Installation Activity Boundary Implementation

**Date:** 2026-10-18  
**Agent:** Gemini / Antigravity  
**Role:** Implementer  
**Task:** Issue #178: [Safety] Implement operation exclusion and installation activity boundary  
**Approval Reference:** [PR #177 Comment 6256583829](https://github.com/Tiflit/DXVK-Companion/pull/177#issuecomment-6256583829)  
**Target Base:** `origin/main` (`de79a4615b3018e58b036897441f4f388238d6ea`)  
**Design History:** Carrying forward accepted commit `2d195b307e857ea06b1b7b864d704c76c373e78e`  

---

## 1. Summary of Changes

### A. Production Safety & Boundary Components
1. **`ProcessActivityEvidence.cs`:**
   - Introduced tri-state activity enum: `ConfirmedNotRunning = 0`, `Running = 1`, `Unknown = 2`.
2. **`OperationResourceIdentity.cs`:**
   - Canonical path normalization (`GetFullPath`, case-insensitivity on Windows, ancestor junction resolution via `Directory.ResolveLinkTarget`, trailing slash stripping, UNC path refusal).
   - Derives deterministic SHA-256 lock filenames (`target_<hash>.lock`, `store_<hash>.lock`).
   - Default coordination directory `%LOCALAPPDATA%\DXVKCompanion\locks`.
   - Path containment checking to detect child/ancestor overlap.
3. **`IOperationLease.cs` & `IOperationLeaseCoordinator.cs`:**
   - First-class operation lease token interface exposing `IsAcquired`, `IsDisposed`, `StoreLease`, `IsValidForTarget(path)`, `IsValidForStore(path)`.
   - Asynchronous and synchronous operation scope acquisition with a maximum 500 ms timeout budget.
4. **`OperationLeaseCoordinator.cs`:**
   - Cross-process and cross-thread exclusion primitive using `FileStream` (`FileMode.OpenOrCreate`, `FileAccess.ReadWrite`, `FileShare.None`, `FileOptions.DeleteOnClose`).
   - Strict lock ordering: **Tier 1 (Target Lease)** acquired first, then **Tier 2 (Store Lease)**; strict reverse disposal (**Tier 2**, then **Tier 1**).
   - Retry loop distinguishing contention (`ERROR_SHARING_VIOLATION`, `delete pending`, `UnauthorizedAccessException`) from structural path errors (`PathTooLongException`).
   - Composite lease implementation cleanly managing dual scopes.
5. **`IProcessActivityInspector.cs` & `ProcessActivityInspector.cs`:**
   - Candidate relevance filtering: Inspects only processes whose process name matches declared candidate executables (primary game executable + registered siblings).
   - Prevents unrelated system processes (e.g. `svchost.exe`) with denied permissions from spuriously triggering `Unknown`.
   - Start-time tracking and image normalization. Returns `Running` if matching process runs inside installation root, `ConfirmedNotRunning` if running outside, and `Unknown` if access to a matching candidate process is denied.

### B. Core Service Wiring
1. **`GameLibraryStore.cs` & `ProfileStore.cs`:**
   - Added `LeaseCoordinator` property and store lease validation on writes (`Save`, `SaveAll`, `RecordDetectionSnapshot`, `TryCheckCanModifyLibrary`, `TrySaveWithOutcome`).
   - When a caller holds an active store lease token, the store validates and reuses it without re-locking; when called without a token, the store safely acquires an internal short-lived store lease.
2. **`DxvkInstaller.cs`:**
   - Added `LeaseCoordinator` and `ActivityInspector` properties and internal constructor.
   - Updated `ApplyToGameAsync`, `ReapplyAsync`, and `AdoptExisting` to validate/acquire `IOperationLease`, inspect activity under the held lease, pass store leases into `GameLibraryStore`, and guarantee release in `finally`.
   - On `Running` or `Unknown` activity, returns retryable failure with informative diagnostic without latching `AttentionRequired`.
3. **`DxvkRollback.cs`:**
   - Added `LeaseCoordinator` and `ActivityInspector` properties and internal constructor.
   - Updated `RestoreOriginalDllsAsync` and `RefuseRollback` to coordinate under lease and verify activity before mutation.
4. **`DxvkManager.cs`:**
   - Added `LeaseCoordinator` and `ActivityInspector` properties and cascading constructor.
   - Updated `EnableDxvkAsync`, `DisableDxvkAsync`, `ReapplyAsync`, and `AdoptExistingAsync` with internal overloads taking `IOperationLease`.
   - Delegated `IsPathCurrentlyRunning` to `ActivityInspector`.
   - **Intent Preservation Invariant:** In `ApplyPendingAsync`, transient intents in `_pending` and persistent intents in `installation.PendingAction` are no longer prematurely removed prior to operation execution. They are preserved across retryable Busy/Running/Unknown refusals, and only cleared upon confirmed success or terminal compatibility cancellation (`CancelIncompatiblePendingAction`).
   - Updated `ProcessAllPendingActionsAsync` to acquire per-installation leases and defer on busy/running/unknown.

### C. Test Fixtures & Regressions
1. **`OperationExclusionBoundaryTests.cs`:**
   - Updated all four existing integration fixtures to inject isolated synthetic coordination directories (`coordDir`), ensuring tests never interfere with host or user `%LOCALAPPDATA%` state.
   - Replaced unbounded `await opTask` draining with safe bounded awaits inside `finally`.
2. **`OperationLeaseCoordinatorTests.cs`:**
   - Unit tests covering async/sync acquisition, contention budget timeout (200 ms / 500 ms), disposal lock release, two-tier lock ordering, different-target shared-store contention, case/slash canonicalization, and token validation.
   - Bounded Windows cloud helper process test using owned PowerShell child process (bounded max 10s wait, self-timeout 1.5s, explicit cleanup).
3. **`ProcessActivityBoundaryTests.cs`:**
   - Tests covering candidate relevance filtering (unrelated inaccessible process ignored -> `ConfirmedNotRunning`), candidate process access denial (`Unknown`), running inside installation root (`Running`), running outside installation root (`ConfirmedNotRunning`), registered sibling executable (`Running`), `EnableDxvkAsync` refusal without `AttentionRequired`, and `ApplyPendingAsync` intent preservation on `Unknown`.

---

## 2. Invariants & Policy Compliance

- **No Host Execution:** Local builds, tests, applications, and helper processes remain strictly NOT RUN on the human's Windows host.
- **Allowed Paths:** Changes are restricted strictly to declared allowed paths in Issue #178.
- **Public API & Formats:** All public constructor and method signatures are preserved; new parameters use internal overloads or optional defaults. Persisted schema formats are unaltered.
- **Intent Preservation:** Intent is preserved on transient contention or activity block; no automatic orphan recovery or speculative compensation.

---

## 3. Review & CI Repair (Session 1 Follow-up)

1. PR #179 opened targeting `main`.
2. Initial CI reported:
   - `build-and-test`: CS1513 in `GameLibraryStore.cs` (`RecordDetectionSnapshot` missing closing brace for `lock (_sync)`).
   - `PR hygiene`: Heading formatting mismatch (`### Summary of Implementation` instead of required `## Summary`, and missing required sections).
3. Repairs executed:
   - Fixed unclosed `lock (_sync)` block in `src/DXVKCompanion/Storage/GameLibraryStore.cs`.
   - Updated PR #179 body using `scripts/ai-workflow/update_pr_body.py` with standard contract headers (`## Summary`, `## Scope`, `## Verification`, `## Documentation`).
4. Committed repair (`f13d63a`) and pushed to branch `feat/issue-178-operation-exclusion-boundary`.
5. Second CI run reported `Build Phase A tests` error: missing `processNameResolver` parameter on `ProcessActivityInspector`.
6. Aligned `ProcessActivityInspector` constructor to include `Func<Process, string>? processNameResolver = null`, route process name queries through it when present, and fixed `DxvkManager` constructor nullability warnings (`f828641`).
7. Third CI run reported 3 test failures out of 927 tests:
   - `InterProcessLocking_OwnedCloudHelper_ExcludesParentDuringHold`: Cold-start timing on GitHub runner caused parent check before child helper started locking. Resolved by synchronizing helper lock via stdout (`LOCKED`) signal before parent timeout assertion.
   - `UnresolvedEngineRecovery_OnManagedInstallationWithWritableStore_PersistsAttentionRequiredAndLastRefusalReason`: `DxvkInstaller.TrySaveHealthAfterFailedCompensation` did not receive `leaseToken.StoreLease`, causing non-reentrant lease re-acquisition to time out. Resolved by passing held store lease in `ApplyToGameAsync` and `ReapplyAsync`.
   - `DxvkManager_ApplyPendingAsync_And_ProcessAllPendingActionsAsync_BlocksExecutionForUnsupportedApis`: `DxvkManager.ClearCancelledState` inside `EnableDxvkAsync` did not receive `leaseToken.StoreLease`, causing non-reentrant lock timeout on `GameLibraryStore.Save`. Resolved by adding `ClearCancelledState(installation, storeLease)` overload and passing `leaseToken.StoreLease`.
8. Committed repairs and pushed to branch `feat/issue-178-operation-exclusion-boundary`.
9. Monitoring cloud CI.
