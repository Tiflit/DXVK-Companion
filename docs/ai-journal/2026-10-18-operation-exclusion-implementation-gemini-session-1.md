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
9. Completed all grouped repairs across areas R1-R8 in a single repair cycle.
10. Monitoring cloud CI.

---

## 4. Codex Grouped Review & Complete Repair Cycle (PR #179 Comment 6257405783)

### Review Findings Addressed (R1 - R8):
- **R1. Target canonicalization & collision semantics:**
  - `OperationResourceIdentity.ResolveCanonicalPath`: preserved rooted drive paths throughout (never trims `C:\` to `C:`, preventing `Path.Combine` from creating drive-relative paths like `C:Games`), added strict `Path.IsPathFullyQualified` enforcement, refused UNC/network shares consistently for both input and resolved junction targets.
  - Implemented ancestor/descendant target lock collision detection (`HasConflictingTargetLock` reads active `target_*.lock` files opened with `FileAccess.ReadWrite, FileShare.Read, FileOptions.DeleteOnClose`).
  - Resolved authoritative installation root (`existingInstallation.InstallationPath` or normalized directory) across `DxvkInstaller`, `DxvkRollback`, and `DxvkManager`.
- **R2. Authoritative store baseline & partial lease cleanup:**
  - `ProfileStore`: added authoritative disk baseline tracking (`_lastCommittedBytes`); in `Save`, acquires/validates lease token, verifies disk baseline against `_lastCommittedBytes` under `lock (_sync)` before mutating in-memory dictionary; refuses with `IOException` if file was externally created, modified, or deleted. Added `TryCheckCanModifyProfileStore`. Thread-safe `GetAll()` and `GetOrCreate` prevent premature mutation.
  - `GameLibraryStore`: added token-aware `GetOrCreateInstallation(installationPath, displayName, storeLease)`, moved lease acquisition outside and before `lock (_sync)`.
  - Multi-store scope acquisition in `OperationLeaseCoordinator`: deterministic sorted ordering (`StringComparer.OrdinalIgnoreCase`) and deduplication; wrapped partial acquisitions in `try ... finally` to guarantee disposal of all acquired store leases and target lease on any failure or cancellation in both async and sync paths.
  - Preserved distinct non-busy diagnostics for permanent ACL failures in `LastAcquisitionError`.
- **R3. Intent transition guarding:**
  - `ManagedFileInspector.InspectInstallation`: acquires or validates operation scope before inspecting files or mutating live objects (`ManagedFileRecord`, `PendingAction`, `RestorationState`). If busy, skips inspection without mutating or setting `AttentionRequired`.
  - `DxvkManager.ApplyPendingAsync`: acquires composite operation scope upfront and holds it across execution, clearing `PendingAction`, removing from `_pending`, and saving to store.
  - `DxvkManager.ProcessAllPendingActionsAsync`: on success, clears the corresponding transient `_pending` entry for all installation executables.
  - `DxvkManager.QueueOrApplyAsync` / `QueueOrApplyReapplyAsync`: do not latch `AttentionRequired` on busy store contention; acquire store lease safely; pass store lease to `GetOrCreateInstallation` and `Save`.
- **R4. Post-download activity & store revalidation race:**
  - `DxvkInstaller.ApplyToGameAsync`: after release download and before file transaction mutations or backup staging, re-evaluates process activity on authoritative root and re-verifies store/profile baselines under the held lease; aborts cleanly leaving target files and store untouched if game launched or store changed during download.
- **R5. Candidate process inspection boundaries & PID reuse:**
  - `ProcessActivityInspector`: wrapped process enumeration in `try ... catch` to return `Unknown` on enumeration exceptions; removed implicit directory candidate discovery when candidate set is empty (returns `Unknown`); normalizes candidate and actual image names using `Path.GetFileNameWithoutExtension`; treats `Win32Exception` on process name queries as candidate access denial (`Unknown`); verifies `HasExited` on `InvalidOperationException` before treating as exited; implemented PID and `StartTime` lifetime revalidation before and after module inspection to detect PID reuse/relaunch; catches `IsPathInside` errors and treats as candidate access denial (`Unknown`).
- **R6. Role-aware token validation:**
  - `IOperationLease`: added `OperationLeaseKind` enum (`Target`, `Store`, `Composite`) and `OperationLeaseKind Kind { get; }`.
  - `FileLockLease` and `CompositeOperationLease`: role-aware `IsValidForTarget` and `IsValidForStore` validation rejecting target leases passed as store leases and vice versa.
- **R7. Visibility audit (zero public surface additions):**
  - All newly introduced safety primitives and properties made `internal`: `IOperationLease`, `OperationLeaseKind`, `IOperationLeaseCoordinator`, `OperationLeaseCoordinator`, `IProcessActivityInspector`, `ProcessActivityInspector`, `ProcessActivityEvidence`, `OperationResourceIdentity`, and properties on existing types (`LeaseCoordinator`, `ActivityInspector`, `ProfilesFile`, `LibraryFilePath`).
- **R8. Test fixtures, design doc reconciliation, & regression harness:**
  - `OperationLeaseCoordinatorTests`: fixed `DifferentTarget_SharedStore_CompetesForStoreOnly` by disposing `leaseTarget2Only` before composite acquisition; hardened `InterProcessLocking_OwnedCloudHelper_ExcludesParentDuringHold` (requires `GITHUB_ACTIONS == "true"`, hard 20s helper lifetime timeout, DeleteOnClose in PowerShell script, exit confirmation after kill); added unit tests for `OperationLeaseKind` token validation rejection, partial acquisition failure cleanup, ancestor/descendant target conflict detection, and deterministic multi-store lock ordering.
  - `OperationExclusionBoundaryTests`: fixed `allCompleted` `Task.Delay` reference comparison bug using `Assert.Same(allTasks, completed)`; bounded all `finally` draining awaits; added `RootVsSubdirectoryCollision_AcrossSeparateStores_DetectedAndRefused`.
  - `ProcessActivityBoundaryTests`: added tests for process name query `Win32Exception` fault -> `Unknown`, `InvalidOperationException` with unconfirmed exit -> `Unknown`, PID replacement / relaunch -> `Unknown`, lazy enumeration exception -> `Unknown`, download-stage activity/baseline race (verifying untouched files, backups, and store), and updated `ApplyPendingAsync` test to verify transient dictionary preservation and reloaded store reconstruction.
  - Reconciled `docs/design/OPERATION-EXCLUSION-AND-ACTIVITY-BOUNDARY.md` removing active references to `pending-actions.json`, env overrides, and automatic orphan compensation.
  - Implemented `OperationResourceIdentity.ResolveAuthoritativeInstallationRoot(string? installationPath, string exePath)` resolving canonical installation roots and falling back to exe directory, with unit tests in `OperationLeaseCoordinatorTests`.

---

## 5. Final Verification & CI Signoff

### Root Cause Analysis & Focused Delta Repairs
1. **Target Lock Sharing (`FileShare.Delete` on Windows):**
   - Windows file locking requires `FileShare.Delete` when opening files marked with `FileOptions.DeleteOnClose`. Readers without `FileShare.Delete` threw `IOException: Sharing violation`. `HasConflictingTargetLock` caught `IOException` and returned `true`, causing every target lock to falsely block every other target lock.
   - Fixed by adding `FileShare.Delete` and retries, eliminating false collision returns (`30e892e`).
2. **`CompositeStoreLease` for Multi-Store Token Validation:**
   - In composite operations involving multiple stores (`dxvk-profiles.json` and `game-library.json`), `CompositeOperationLease.StoreLease` previously returned `_storeLeases[0]`. Because stores were sorted alphabetically, `_storeLeases[0]` was `dxvk-profiles.json`. When `DxvkInstaller` or `DxvkManager` validated `leaseToken.StoreLease.IsValidForStore(gameLibraryPath)`, validation failed with `ArgumentException`.
   - Fixed by implementing `CompositeStoreLease` which delegates `IsValidForStore` across all acquired stores, and caching the instance in `CompositeOperationLease` (`411eb3a`).
3. **Restoration of `gameDir = rawDir` for Relative DLL Paths:**
   - Restored `gameDir = rawDir` for relative path resolution across `ApplyToGameAsync`, `ReapplyAsync`, and `AdoptExistingAsync` while keeping `authoritativeRoot` for lease acquisition and process activity boundary inspection (`30e892e`).
4. **Pre-mutation Activity Revalidation:**
   - Added `preMutationActivity` check immediately prior to mutating target or store in `DxvkInstaller.ApplyToGameAsync` (`411eb3a`).
5. **Test Fixture Scoping:**
   - Scoped `targetOnlyLease` and `storeOnlyLease` into independent `using` blocks in `OperationLeaseCoordinatorTests.TokenValidation_RoleAndKind_Enforced` so locks are released before composite lease acquisition (`411eb3a`).

### Verified Cloud CI Outcomes (Commit `411eb3a`)
- **`Build and Test` ([Run 39189978697](https://github.com/Tiflit/DXVK-Companion/actions/runs/39189978697)):**
  - Result: **SUCCESS** in 2m 6s.
  - Phase A Test Suite: **940 tests total, 940 passed, 0 failed, 0 skipped**.
  - 100% pass rate; all 907 pre-existing baseline invariants and all 33 newly added Issue #178 tests passed.
  - Executable Artifact: `DXVK-Companion-win-x64.zip` published and uploaded successfully.
- **`AI Scope Check` ([Run 39189978600](https://github.com/Tiflit/DXVK-Companion/actions/runs/39189978600)):**
  - Result: **SUCCESS** (all modified files within declared allowed paths).
- **`AI PR Hygiene` ([Run 39189978619](https://github.com/Tiflit/DXVK-Companion/actions/runs/39189978619)):**
  - Result: **SUCCESS**.
- **`AI Workflow Tests` ([Run 39189978611](https://github.com/Tiflit/DXVK-Companion/actions/runs/39189978611)):**
  - Result: **SUCCESS**.
- **`AI Current State Dashboard` ([Run 39189977331](https://github.com/Tiflit/DXVK-Companion/actions/runs/39189977331)):**
  - Result: **SUCCESS**.
- **Host Execution Guarantee:** Strictly 0 host builds or test runs on the human's Windows host (all verification performed on GitHub Actions Cloud CI).
- **Public API Surface Guarantee:** Strictly 0 public API additions or signature breaks (all safety types and methods remain internal).

---

## 6. Focused Delta Repair Cycle — Codex Review (PR #179 Comment 6258447248)

**Date:** 2026-10-19  
**Trigger:** Focused delta review on PR #179 across 6 specific design and verification areas (D1 - D6).

### A. Addressed Areas & Implementation
1. **D1 / R2 (Store Lease Write Bypass & Inverted Wait):**
   - In `GameLibraryStore.cs`, `GetOrCreateInstallation` and `TrySaveHealthAfterFailedCompensation`: lease acquisition now fails closed before entering `lock (_sync)`. If lease acquisition times out or returns null, `GetOrCreateInstallation` throws `IOException` immediately and `TrySaveHealthAfterFailedCompensation` returns `false` immediately without mutating in-memory state or attempting to write.
2. **D2 / R2 (Profile Store Preflights & Broken-Load Isolation):**
   - In `ProfileStore.cs`: introduced `_isBrokenLoad` and `_brokenLoadReason`. In `Load()`, unreadable files or malformed JSON deserialization set `_isBrokenLoad = true` and leave `_lastCommittedBytes` empty. `TryCheckCanModifyProfileStore` returns `false` on broken load or disk baseline mismatch. `Save` throws `IOException` refusing to overwrite corrupted files.
   - In `DxvkInstaller.cs`: wired `ProfileStore` preflight validation into `ApplyToGameAsync`, `ReapplyAsync`, and `AdoptExisting`.
   - In `DxvkManager.cs`: wired `ProfileStore.TryCheckCanModifyProfileStore` into all mutation preflights (`EnableDxvkAsync`, `DisableDxvkAsync`, `AdoptExistingAsync`, `ReapplyAsync`, `QueueOrApplyAsync`).
3. **D3 / R3 (Manager Reassessment, Cancellation & Intent Under Lease Scope):**
   - In `DxvkManager.cs`: `ClearCancelledState`, `ReassessEffectiveApi`, and intent capture now execute strictly under established owner lease scope; eliminated unguarded naked `ClearCancelledState` calls before guards; propagated explicit store leases into `GameLibraryStore`; in `ApplyPendingAsync`, removed only completed transient intent via `_pending.TryRemove(new KeyValuePair<string, PendingAction>(exePath, currentTransient))`.
4. **D4 / R1, R5 (Activity Boundaries, Lifetime Matching, & Authoritative Roots):**
   - In `ProcessActivityInspector.cs`: image normalization strips only the `.exe` suffix, preserving embedded dots (e.g. `Game.Client.exe` -> `Game.Client`). Process lifetime verification enforces exact start-time equality (`currentStartTime == initialStartTime`).
   - In `DxvkInstaller.AdoptExisting`: passes `authoritativeRoot` (not `gameDir`) to `GetActivityEvidence`.
   - In `OperationResourceIdentity.ResolveAuthoritativeInstallationRoot`: supplied invalid/unsupported roots (UNC/network shares) throw `NotSupportedException` directly rather than silently falling back to `exePath`.
5. **D5 / R1, R6 (Hierarchy Scan Publication Race, Stream Ownership Transfer & Token Validation):**
   - In `OperationLeaseCoordinator.cs`: hierarchy scanning fail-closed protocol treats empty or unreadable target locks and directory enumeration failures as conflicts after retries; carries remaining acquisition budget and cancellation token to bound scan execution to 500 ms; wraps raw `FileStream` post-open setup in `try ... finally { stream?.Dispose(); }` with explicit ownership transfer to `FileLockLease`; `UnauthorizedAccessException` sets `LastAcquisitionError` and returns `null` immediately without busy retry.
   - In `DxvkInstaller.cs`, `DxvkRollback.cs`, and `ManagedFileInspector.cs`: validated composite store leases using `(!leaseToken.IsValidForStore(...) && (leaseToken.StoreLease == null || !leaseToken.StoreLease.IsValidForStore(...)))`.
6. **D6 / R4, R8 (Test Fixture Harness & Hardening):**
   - In `ProcessActivityBoundaryTests.cs`: replaced all synthetic test `startTimeResolver` `DateTime.UtcNow` hooks with fixed timestamps to adhere to exact equality; updated `ProcessEnumeration_LazyException_ReturnsUnknown` to test lazy enumeration `MoveNext()` throw; rewrote `DownloadStageRace_ActivityDetected_AbortsBeforeMutation_FilesUntouched` using `BlockingHttpMessageHandler` and dynamically generated valid `tar.gz` archive (`PaxTarEntry`); added sibling test `DownloadStageRace_ProfileChangedDuringDownload_AbortsBeforeMutation`; added embedded-dot candidate matching tests; added `AdoptExisting` authoritative root check with sibling running.
   - In `OperationLeaseCoordinatorTests.cs`: added publication contention test with empty target lock file; added prompt cancellation test with cancelled token; added component wrong-store validation test; added `ResolveAuthoritativeInstallationRoot` UNC throwing test; hardened `InterProcessLocking_OwnedCloudHelper_ExcludesParentDuringHold` with self-terminating PowerShell script (`exit 0`), 15s parent timeout, and confirmed child termination in `finally`.
   - In `OperationLeaseCoordinatorTests.cs` (consolidated from ProfileStore tests to preserve scope boundary): added tests for `ProfileStore` corrupted JSON / broken load refusal, external modification baseline mismatch, and manager preflight refusal.
   - In `GameLibraryStorePersistenceTests.cs`: added test for independent held store lease blocking `GetOrCreateInstallation` and `TrySaveHealthAfterFailedCompensation`, preserving disk baseline, and succeeding on retry.
### B. Delta Repair & Scope Alignment
- Verified strict adherence to Issue #178 allowed paths (`ProfileStorePersistenceTests.cs` reverted byte-for-byte; all ProfileStore delta tests consolidated into `OperationLeaseCoordinatorTests.cs`).
- Resolved compilation and signature alignment for `ProfileStore` (restored closing brace), `OperationLeaseCoordinatorTests` (passed `ct:` cancellation token parameter), `ProcessActivityBoundaryTests` (passed `ExistingDxvkAssessment` into `AdoptExisting`), and `GameLibraryStorePersistenceTests` (aligned parameter signature for `TrySaveHealthAfterFailedCompensation`).

### C. Resolution of Cloud CI Run 39290905187 Test Failures
- **Analysis:** Cloud CI run `39290905187` achieved clean hygiene, scope, workflow, and dashboard checks, built cleanly, and passed 928 of 951 tests. The 23 failures traced to three root causes:
  1. *NullReferenceException in DxvkManager constructor:* `RestoreAllAttentionTests` passed `installer: null!`. `_installer.ProfileStore = _profiles;` and `_installer.LeaseCoordinator = leaseCoordinator;` threw `NullReferenceException`. Fixed by adding `if (_installer != null)` guards.
  2. *DxvkInstaller Single-Store Acquisition:* `DxvkInstaller.ApplyToGameAsync`, `ReapplyAsync`, and `AdoptExisting` acquired operation scopes covering only `_gameLibraryStore.LibraryFilePath`, omitting `ProfileStore.ProfilesFile`. When `ProfileStore.TryCheckCanModifyProfileStore` checked `leaseToken.StoreLease`, the store lease was rejected as mismatched. Fixed by including `ProfileStore.ProfilesFile` in `storePaths` when `ProfileStore` is non-null, and introducing `GetProfileStoreLease(leaseToken)` helper which safely passes `null` if the token does not cover the profile store.
  3. *ProfileStore Composite Lease & Unscoped Preflight Graceful Fallback:* `ProfileStore.Save` and `TryCheckCanModifyProfileStore` checked only `storeLease.IsValidForStore(_profilesFile)`. When called with a composite store lease or when caller holds only a library store lease, they now accept `storeLease` if `storeLease.IsValidForStore(_profilesFile) || storeLease.StoreLease?.IsValidForStore(_profilesFile) == true`, and gracefully fall back to acquiring their own store lease for `_profilesFile` if the provided token does not cover the profile store. In `DxvkManager.QueueOrApplyAsync` and `QueueOrApplyReapplyAsync`, `_profiles.TryCheckCanModifyProfileStore(out var profileRefusalReason)` is called without passing the library store lease.
- **Scope & API Surface Check:** All modifications remain strictly confined to declared Issue #178 paths (`DxvkInstaller.cs`, `DxvkManager.cs`, `GameLibraryStore.cs`, `ProfileStore.cs`, and this journal). Zero public API changes. Strictly 0 host test executions.
