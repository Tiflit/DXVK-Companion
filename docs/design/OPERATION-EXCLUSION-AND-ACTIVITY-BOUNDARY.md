# Operation Exclusion & Activity Boundary Design (`#176`)

- **Task Contract:** [Issue #176](https://github.com/Tiflit/DXVK-Companion/issues/176) (Reproduce Overlapping Operations & Design Installation Activity/Exclusion Boundary)
- **Baseline `main` Commit:** `de79a4615b3018e58b036897441f4f388238d6ea` (includes [PR #174](https://github.com/Tiflit/DXVK-Companion/pull/174) / [Issue #173](https://github.com/Tiflit/DXVK-Companion/issues/173) / [Issue #175](https://github.com/Tiflit/DXVK-Companion/issues/175))
- **Executable Concurrency Regression Fixture:** `tests/DXVKCompanion.PhaseA.Tests/OperationExclusionBoundaryTests.cs`
- **Canonical Spec Authority:** `docs/spec/DXVK-COMPANION-SPEC.md` (§§12, 13, 18, 22, 23, 27, 36) and `docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md`

---

## 1. Executive Summary & Problem Statement

DXVK Companion performs multi-file operations (`Install`, `Update`, `Reapply`, `Restore`, and `AdoptExisting`) that span target game directories, canonical backup directories (`backups/<instId>/`), and the whole-library metadata store (`game-library.json`). While Issue #171 established in-process compensation and Issue #173 established strict unowned-restore refusal, **zero operational mutual exclusion exists across concurrent operations targeting the same game installation root**.

Furthermore, process activity checking is currently fragmented, incomplete, and decoupled from the actual point of mutation.

### 1.1 Observed Concurrency Failure Modes on `main`

1. **Rollback Undoing Another Operation's Committed Bytes (Silent Data Reversion):**
   When Operation 1 (e.g. Update to 2.6) begins applying target files, it creates an operation-scoped undo copy in `.transactions/<Op1_Id>/undo/`. If Operation 1 pauses or interleaves, and Operation 2 (e.g. Update to 2.7) executes on the same game directory, Operation 2 applies its files and commits to `game-library.json`. If Operation 1 subsequently encounters an error (e.g. disk write fault, permission issue, or store persistence failure), Operation 1 triggers `RecoverAttempted`. Its undo copy contains the pre-operation bytes (version 2.5), which it blindly copies over `d3d11.dll`. Operation 1's rollback **silently destroys Operation 2's committed version 2.7 files**, leaving the disk in a corrupt, split-brain state where metadata indicates 2.7 but the filesystem has 2.5.
2. **Concurrent Mutation & Mixed Payload Infiltration:**
   When two deploy operations (e.g. Update to 2.6 and Update to 2.7) run concurrently on the same installation, file mutations interleave at the operating system level. Because `MultiFileTransactionEngine` prepares and copies files sequentially (`d3d11.dll`, then `dxgi.dll`, then `dxvk.conf`), an interleaving can leave `d3d11.dll` from Version 2.7 and `dxgi.dll` from Version 2.6.
3. **Split-Brain Store Metadata Overwrites:**
   Each `GameLibraryStore` instance holds an internal in-memory dictionary protected only by an instance-scoped `lock (_sync)`. Two separate application instances, or two independently constructed service graphs within the same process, hold distinct `_sync` instances. When both save, the second staged replacement overwrites the first, discarding recorded managed file records or pending actions.
4. **False Inactivity & Activity Check Bypasses:**
   `DxvkManager.IsPathCurrentlyRunning` inspects only `Process.GetProcesses()` matching a single `exePath`. If any process cannot be inspected (e.g. `Win32Exception` on `MainModule` due to elevated games or anti-cheat), it catches and ignores the exception, treating the process as inactive (`false`). Direct entry points (`EnableDxvkAsync`, `DisableDxvkAsync`, `ReapplyAsync`, `ApplyToGameAsync`, `RestoreOriginalDllsAsync`) perform **no activity check whatsoever**.
5. **TOCTOU Gap in Queued Execution (`ApplyPendingAsync`):**
   `RequestEnableByPathAsync` checks activity before downloading releases. During the seconds required to fetch network archives and stage files, the game can launch. `ApplyPendingAsync` removes the transient intent from `_pending` and executes mutations on exit of one process instance without verifying whether other instances or sibling executables remain running.

---

## 2. Comprehensive Entry-Point Inventory

The table below catalogs every public, requested, batch, startup, exit, and direct mutation entry point in DXVK Companion, its current activity check, its locking behavior, and the proposed single exclusion owner.

| Entry Point | Trigger / Caller | Target Scope | Current Activity Check | Current Exclusion / Locking | Staged vs Direct Mutation | Proposed Lease Boundary & Revalidation |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`RequestEnableByPathAsync`** | UI (Manage Games) / CLI | Single Game Profile | `IsPathCurrentlyRunning(profile.ExePath)` at start | None | Direct if inactive; Queued in `_pending` & Store if active | Acquire installation lease **before** mutation; revalidate activity under lease |
| **`RequestEnableAsync`** | UI (Tray running-game menu) | Single Game Profile | `IsStillRunning(process)` on supplied handle | None | Direct if inactive; Queued if active | Acquire installation lease **before** mutation; revalidate activity under lease |
| **`EnableDxvkAsync`** | Direct public / programmatic | Single Game Profile | **None** | None | Direct mutation via `DxvkInstaller.ApplyToGameAsync` | Acquire installation lease; reject/queue if lease unavailable or game active |
| **`RequestDisableByPathAsync`** | UI (Manage Games) / CLI | Single Game Profile | `IsPathCurrentlyRunning(profile.ExePath)` at start | None | Direct if inactive; Queued in `_pending` & Store if active | Acquire installation lease **before** mutation; revalidate activity under lease |
| **`RequestDisableAsync`** | UI (Tray running-game menu) | Single Game Profile | `IsStillRunning(process)` on supplied handle | None | Direct if inactive; Queued if active | Acquire installation lease **before** mutation; revalidate activity under lease |
| **`DisableDxvkAsync`** | Direct public / programmatic | Single Game Profile | **None** | None | Direct mutation via `DxvkRollback.RestoreOriginalDllsAsync` | Acquire installation lease; reject if lease held by another operation |
| **`RequestUpdateByPathAsync`** | UI (Update button) / CLI | Single Game Profile | `IsPathCurrentlyRunning(profile.ExePath)` at start | None | Delegates to `RequestEnableByPathAsync` | Same as `RequestEnableByPathAsync` |
| **`RequestReapplyByPathAsync`** | UI (Reapply button) | Single Game Profile | `IsPathCurrentlyRunning(profile.ExePath)` at start | None | Direct if inactive; Queued in `_pending` & Store if active | Acquire installation lease **before** mutation; revalidate activity under lease |
| **`ReapplyAsync`** | Direct public / programmatic | Single Game Profile | **None** | None | Direct mutation via `DxvkInstaller.ReapplyAsync` | Acquire installation lease; reject if lease unavailable |
| **`ApplyPendingAsync`** | Tray process exit handler | Single Executable Path | **None** (assumes exit event proves total inactivity) | None | Direct mutation via `EnableDxvkAsync` / `ReapplyAsync` / `DisableDxvkAsync` | Revalidate **all** installation executables; acquire lease before mutation |
| **`ProcessAllPendingActionsAsync`** | Tray startup / resume scan | All Library Installations | Per-profile `IsPathCurrentlyRunning` loop | None | Sequentially calls `ApplyPendingAsync` | Sequential per-installation lease acquisition; skips busy/active installations |
| **`UpdateAllEnabledAsync`** | Scheduler / Daily release check | All Enabled Profiles | Per-profile `RequestUpdateByPathAsync` | None | Iterative execution | Sequential per-installation lease acquisition; isolates batch failures |
| **`RestoreAllAsync`** | Emergency / Reset All | All Library Installations | None | None | Iterative execution via `DisableDxvkAsync` | Per-installation lease; failure on one does not block others |
| **`AdoptExisting` / `AdoptExistingAsync`**| Manual adoption wizard | Target Directory & Profile | **None** | None | Direct ownership creation & profile enable | Acquire installation lease to prevent concurrent installer execution |

---

## 3. Operation Lease Resource Identity, Lifetime & Architecture

To prevent simultaneous mutation, split-brain metadata, and rollback collisions, operations must acquire an **exclusive operation lease** over the target installation.

```
       [Caller: UI / CLI / Scheduler / Process-Exit]
                           │
                           ▼
            [1. Resolve Target Installation Root]
                           │
                           ▼
          [2. Asynchronous Preparation & Download]
          (Network acquisition, staging in cache)
                           │
                           ▼
          [3. Acquire Operation Lease (Exclusive)]
     (Named system mutex: Global\DXVKCompanion_<Sha256>)
               ├── Acquired ───► [4. Revalidate Process Activity]
               │                         │
               │                   [Active / Unknown]
               │                         ├── Yes ──► Queue Pending Intent & Release Lease
               │                         │
               │                         └── No ───► [5. Execute Multi-File Transaction]
               │                                           │
               │                                     (Target Mutation, Native Backups,
               │                                      Undo Staging, Commit Callback)
               │                                           │
               │                                           ▼
               │                                     [6. Release Operation Lease]
               │                                           │
               └── Busy (Lease Held by Other Op)           ▼
                         │                          [7. Return Result]
                         ▼
             [Refuse with OperationBusy]
```

### 3.1 Lease Resource Identity
The lease must be identified by the **normalized installation root path**:
$$\text{LeaseKey} = \text{GameInstallation.NormalizeInstallationPath}(\text{installationDirectory})$$
- All sibling executables residing within the same game root (e.g. `Game.exe`, `Game_x64.exe`, `bin/Launcher.exe`) map to the exact same installation root and therefore compete for the exact same lease.
- Normalization strips trailing directory separators, resolves symbolic links/junctions to canonical physical paths, and handles case-insensitivity on Windows (`OrdinalIgnoreCase`).

### 3.2 Lease Lifetime & Phase Boundary
Holding a lock across seconds of network downloads degrades user responsiveness and risks timeouts. Therefore, the lease lifetime is strictly bounded:
1. **Source Acquisition (Unleased):** GitHub release metadata query, archive download, asset verification, and local unpack staging occur outside the lease.
2. **Lease Acquisition:** Acquired immediately prior to the pre-mutation boundary.
3. **In-Lease Activity Revalidation:** Under the lease, the system performs a fresh, definitive process activity check across all known executables in the installation. If any game process is running or activity is `Unknown`, the operation transitions to `Queued` (for requests) or refuses (for direct calls), releasing the lease immediately without file mutation.
4. **Protected Mutation & Commit:** The lease is held continuously through:
   - Target undo snapshot capture (`.transactions/<txId>/undo/`),
   - Baseline backup verification and staging (`.transactions/<txId>/native-backups/`),
   - Target file replacement,
   - In-process ownership commit callback (`GameLibraryStore.TrySaveWithOutcome`), and
   - Compensation/recovery in the event of `ProvenNotCommitted`.
5. **Lease Release:** The lease is released in a mandatory `finally` block after transaction finalization or rollback.

### 3.3 Cross-Process Synchronization vs. Single-Instance Application
Two design alternatives exist for operational exclusion:

| Criterion | Option A: Per-Installation Cross-Process Lease (Recommended) | Option B: Single-Instance Application Mutex |
| :--- | :--- | :--- |
| **Mechanism** | Named OS Mutex per installation root: `Global\DXVK_Lease_<Hash>` + in-process `SemaphoreSlim` | Single `Global\DXVKCompanion_AppMutex` at process startup |
| **Concurrency** | Independent games can be updated in parallel (CLI + Tray + Background) | Entire application is single-instance; second launcher exits or forwards command |
| **CLI / Tool Interop** | Headless CLI commands can safely operate alongside running Tray App | CLI cannot run while Tray App is active; requires IPC pipe |
| **Granularity** | Scoped strictly to the target game directory | Global application-wide lock |
| **Failure Blast Radius** | If an operation crashes, only that installation's mutex is abandoned | If the app crashes, global lock state requires cleanup |
| **Complexity** | Requires path hashing and per-root mutex management | Trivial process-level check |

**Recommendation:** Adopt **Option A (Per-Installation Cross-Process Lease)**. It preserves non-conflicting parallel operations on independent games, supports CLI and Tray co-existence, and eliminates the root cause of the observed collisions without restricting multi-window application workflows.

---

## 4. Process Activity Evidence Model

Current production equates `IsPathCurrentlyRunning == false` with permission to mutate files. In reality, process inspection can fail due to Windows security boundaries. We define an explicit three-state evidence model:

```
                  [Process Inspection Request]
                               │
                               ▼
                [1. Enumerate System Processes]
                               │
               ┌───────────────┴───────────────┐
               ▼                               ▼
       [Exception / Access Denied]    [Processes Available]
               │                               │
               ▼                               ▼
      [State: Unknown]           [2. Check All Installation Executables]
      (Conservative Refusal)                   │
                               ┌───────────────┴───────────────┐
                               ▼                               ▼
                      [Matching PID Found]           [No Matches Found]
                               │                               │
                               ▼                               ▼
                        [State: Running]        [State: ConfirmedNotRunning]
                       (Queue / Refuse)             (Safe to Mutate)
```

### 4.1 Evidence States
1. **`Running`:** One or more running processes are positively identified whose main module path matches any registered executable or discovered executable within the installation root.
2. **`ConfirmedNotRunning`:** System process enumeration completed successfully without security or enumeration barriers, and zero running processes match any installation executable.
3. **`Unknown`:** System process enumeration failed, or an individual process returned `Win32Exception` (Access Denied / elevated process / Protected Process Light) such that its executable path could not be verified.

### 4.2 Sibling Executables & Multi-Instance Handling
Games frequently employ multi-process architectures (e.g. `Launcher.exe` spawning `Game.exe`, or 32-bit crash-reporters alongside 64-bit binaries).
- The activity inspector must query `GameInstallation.Executables` and inspect **all** registered executables for the installation, not merely the single executable associated with the invoking profile.
- If any executable within the installation directory is `Running` or `Unknown`, the entire installation is treated as active.

### 4.3 Windows Kernel Limits & The External Start Race (TOCTOU)
It is essential to state the limits of user-mode software honestly:
- **No user-mode mutex or check can prevent a user or external launcher from double-clicking `Game.exe` during the 50 milliseconds that DXVK Companion is writing `d3d11.dll`.**
- Windows does not provide transactional directory locking across arbitrary third-party process creation without specialized filesystem filter drivers (minifilters).
- Attempting to suspend or terminate external processes violates non-invasive application safety and anti-cheat policies.
- **Contract Boundary:** The activity boundary guarantees that DXVK Companion will never **knowingly initiate** or **permit** mutations while a game is observable or uncertain. By revalidating activity under the lease immediately before file copy, the window of vulnerability is reduced to the absolute physical minimum.

---

## 5. Integration with Ownership Commit & Recovery (Issue #171)

The operation lease must integrate seamlessly with the five-phase commit and recovery pipeline from Issue #171:

1. **Lease Extends Through Commit Verification:**
   The lease is **not** released upon file copy completion. It remains held while `GameLibraryStore.TrySaveWithOutcome` replaces `game-library.json` and verifies readback.
2. **Lease Retained During Compensation:**
   If `TrySaveWithOutcome` returns `ProvenNotCommitted`, `MultiFileTransactionEngine` invokes `RecoverAttempted`. Because the lease is still held, no concurrent operation can start or observe the intermediate state while files are being restored to the pre-operation baseline.
3. **Deadlock Prevention with Store Locks:**
   `GameLibraryStore` utilizes an internal `lock (_sync)` for in-memory collections and staged saves. The lock hierarchy is strictly ordered:
   $$\text{Operation Lease (Outermost)} \longrightarrow \text{Store } \_sync \text{ (Innermost / Leaf)}$$
   The store lock is never held while awaiting an operation lease or performing I/O outside `ExecuteClassifiedCommitLocked`.
4. **Preservation of Queued Intents & Terminal Cancellation:**
   If an operation encounters `Running` or `Unknown` activity, or if the lease is busy, existing persisted intents (`PendingAction`) and terminal compatibility cancellations are preserved byte-for-byte. A busy lease must return `DxvkActionResult.Failed` with `LastRefusalReason = "Operation in progress on this installation."` without overwriting prior refusal diagnostics.

---

## 6. Minimal Production File Impact & Internal Test Seams

To implement this design in the subsequent implementation task without architectural creep, the following minimal changes are planned:

```
src/DXVKCompanion/
├── Safety/
│   ├── IOperationLeaseCoordinator.cs     <-- Internal interface: TryAcquire(path, out lease)
│   ├── OperationLeaseCoordinator.cs      <-- Named system mutex + in-process SemaphoreSlim
│   ├── IProcessActivityInspector.cs      <-- Internal interface: InspectActivity(installation)
│   └── ProcessActivityInspector.cs       <-- 3-state Windows process enumeration
└── DXVK/
    ├── DxvkManager.cs                    <-- Injects lease & activity inspector; wraps mutations
    ├── DxvkInstaller.cs                  <-- Validates lease assertion prior to transaction
    └── DxvkRollback.cs                   <-- Validates lease assertion prior to transaction
```

### 6.1 Testability Seams (No Child Processes Required)
- **`IProcessActivityInspector`:** Allows unit tests in Cloud CI to inject simulated `Running`, `ConfirmedNotRunning`, and `Unknown` process states without spawning real OS processes or triggering security alerts.
- **`IOperationLeaseCoordinator`:** Allows unit tests to verify that `DxvkManager` correctly refuses or queues operations when a simulated lease conflict occurs, while the real implementation uses OS named mutexes.

---

## 7. Architectural Decision Brief for Human Authority

The following architectural decision is submitted for human merge authority consideration:

### Decision Question: Cross-Process Lease Scope & Contention Behavior
**Context:** When a second operation (e.g. a second CLI invocation or rapid UI click) targets an installation root that is currently undergoing mutation:
- **Option 1 (Recommended — Bounded Refusal / Busy Signal):** The second operation attempts to acquire the lease with a short, bounded timeout (e.g. 500 ms). If the lease is held, it immediately returns `false` / `DxvkActionResult.Failed` with a clear diagnostic: `"Another operation is currently modifying this installation. Please retry shortly."`
- **Option 2 (Blocking Wait):** The second operation blocks synchronously or asynchronously until the first operation completes, then executes.
- **Trade-off Analysis:** Option 2 risks executing stale intents (e.g. user clicked "Disable" then "Enable", and both run sequentially instead of canceling), and risks UI freezes if a lock is abandoned. Option 1 provides immediate, deterministic feedback, honors safety invariants, and avoids hidden queuing.
