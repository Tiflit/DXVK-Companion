# Operation Exclusion & Activity Boundary Design (`#176`)

- **Task Contract:** [Issue #176](https://github.com/Tiflit/DXVK-Companion/issues/176) (Reproduce Overlapping Operations & Design Installation Activity/Exclusion Boundary)
- **Baseline `main` Commit:** `de79a4615b3018e58b036897441f4f388238d6ea` (includes [PR #174](https://github.com/Tiflit/DXVK-Companion/pull/174) / [Issue #173](https://github.com/Tiflit/DXVK-Companion/issues/173) / [Issue #175](https://github.com/Tiflit/DXVK-Companion/issues/175))
- **Executable Concurrency Regression Fixture:** `tests/DXVKCompanion.PhaseA.Tests/OperationExclusionBoundaryTests.cs`
- **Canonical Spec Authority:** `docs/spec/DXVK-COMPANION-SPEC.md` (§§12, 13, 18, 22, 23, 27, 36) and `docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md`

---

## 1. Executive Summary & Problem Statement

DXVK Companion performs multi-file filesystem operations (`Install`, `Update`, `Reapply`, `Restore`, and `AdoptExisting`) that span target game directories, canonical backup directories (`backups/<instId>/`), and the shared whole-library metadata store (`game-library.json`). While Issue #171 established in-process compensation and Issue #173 established strict unowned-restore refusal, **zero operational mutual exclusion exists across concurrent operations targeting the same game installation root**.

Furthermore, process activity checking is currently fragmented, incomplete, and decoupled from the actual point of mutation.

### 1.1 Observed Concurrency Failure Modes on `main`

1. **Rollback Undoing Another Operation's Committed Bytes (Silent Data Reversion):**
   When Operation 1 (e.g. Update to 2.6) begins applying target files, it creates an operation-scoped undo copy in `.transactions/<Op1_Id>/undo/`. If Operation 1 pauses or interleaves, and Operation 2 (e.g. Update to 2.7) executes concurrently on the same game directory, Operation 2 applies its files and commits to `game-library.json`. If Operation 1 subsequently encounters an error (e.g. simulated disk fault), Operation 1 triggers `RecoverAttempted`. Its undo copy contains pre-operation bytes (version 2.5), which it copies back over `d3d11.dll`. Operation 1's rollback **silently destroys Operation 2's committed version 2.7 files**, leaving the filesystem reverted to 2.5 while `game-library.json` claims version 2.7.
2. **Concurrent Mutation & Mixed Payload Infiltration:**
   When two deploy operations run concurrently on the same installation without an operational lease, file mutations interleave at the operating system level. Because `MultiFileTransactionEngine` prepares and copies files sequentially (`d3d11.dll`, then `dxgi.dll`, then `dxvk.conf`), an unexcluded interleaving risks deploying mixed version files into the game directory.
3. **Shared Metadata Baseline Conflicts & Stale Store Rejections:**
   `GameLibraryStore` utilizes whole-file optimistic serialization (`VerifyDiskMatchesAuthoritativeBaselineLocked`) backed by `lock (_sync)`. When two separate operations concurrently attempt to commit to the same shared `game-library.json`, the second commit observes a modified baseline and legitimately refuses the write. If multiple service graphs or processes hold stale in-memory store instances, operations fail with baseline mismatch errors rather than coordinating orderly metadata updates.
4. **False Inactivity & Activity Check Bypasses:**
   `DxvkManager.IsPathCurrentlyRunning` inspects `Process.GetProcesses()` matching a single `exePath`. If any process throws `Win32Exception` (e.g. access denied on `MainModule` due to anti-cheat or elevated privileges), it catches and swallows the exception, erroneously reporting that the game is inactive (`false`). Direct entry points (`EnableDxvkAsync`, `DisableDxvkAsync`, `ReapplyAsync`, `ApplyToGameAsync`, `RestoreOriginalDllsAsync`) perform **no activity check whatsoever**.
5. **TOCTOU Gap in Queued Execution (`ApplyPendingAsync`):**
   `RequestEnableByPathAsync` checks activity before downloading releases. During the seconds required to fetch network archives and stage files, the game can launch. `ApplyPendingAsync` removes the transient intent from `_pending` before execution; if mutation fails or is deferred, the intent is lost unless preserved.

---

## 2. Comprehensive Entry-Point Inventory

The table below catalogs every public, requested, batch, startup, exit, and direct mutation entry point in DXVK Companion against the actual source implementation, detailing current activity checks, locking behaviors, and proposed lease boundaries.

| Entry Point | Trigger / Caller | Target Scope | Current Activity Check | Current Exclusion / Locking | Staged vs Direct Mutation | Proposed Lease Boundary & Revalidation |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`RequestEnableByPathAsync`** | UI (Manage Games) / CLI | Single Game Profile | `IsPathCurrentlyRunning(profile.ExePath)` at entry | None | Direct if inactive; Queued in `_pending` & Store if active | Prepares release payload unleased; acquires installation lease; revalidates activity under lease before calling `EnableDxvkAsync` |
| **`RequestEnableAsync`** | UI (Tray running-game menu) | Single Game Profile | `IsStillRunning(process)` on supplied handle | None | Direct if inactive; Queued in `_pending` & Store if active | Same as `RequestEnableByPathAsync` |
| **`EnableDxvkAsync`** | Direct public / programmatic | Single Game Profile | **None** | None | Direct mutation via `DxvkInstaller.ApplyToGameAsync` | Acquires installation lease; validates activity is `ConfirmedNotRunning`; passes lease token to internal `ApplyToGameAsync` helper |
| **`RequestDisableByPathAsync`** | UI (Manage Games) / CLI | Single Game Profile | `IsPathCurrentlyRunning(profile.ExePath)` at entry | None | Direct if inactive; Queued in `_pending` & Store if active | Acquires installation lease; revalidates activity under lease before calling `DisableDxvkAsync` |
| **`RequestDisableAsync`** | UI (Tray running-game menu) | Single Game Profile | `IsStillRunning(process)` on supplied handle | None | Direct if inactive; Queued in `_pending` & Store if active | Same as `RequestDisableByPathAsync` |
| **`DisableDxvkAsync`** | Direct public / programmatic | Single Game Profile | **None** | None | Direct mutation via `DxvkRollback.RestoreOriginalDllsAsync` | Acquires installation lease; validates activity is `ConfirmedNotRunning`; passes lease token to internal `RestoreOriginalDllsAsync` helper |
| **`RequestUpdateByPathAsync`** | UI (Update button) / CLI | Single Game Profile | `IsPathCurrentlyRunning(profile.ExePath)` at entry | None | Delegates to `RequestEnableByPathAsync` | Same as `RequestEnableByPathAsync` |
| **`RequestReapplyByPathAsync`** | UI (Reapply button) | Single Game Profile | `IsPathCurrentlyRunning(profile.ExePath)` at entry | None | Direct if inactive; Queued in `_pending` & Store if active | Acquires installation lease; revalidates activity under lease before calling `ReapplyAsync` |
| **`ReapplyAsync`** | Direct public / programmatic | Single Game Profile | **None** | None | Direct mutation via `DxvkInstaller.ReapplyAsync` | Acquires installation lease; passes lease token to internal `ReapplyAsync` helper |
| **`ApplyPendingAsync`** | Tray process exit handler | Single Executable Path | **None** (assumes exit event proves total inactivity) | None | Direct mutation via `EnableDxvkAsync` / `ReapplyAsync` / `DisableDxvkAsync` | Retains transient intent in `_pending` until successful completion; acquires lease; revalidates **all** installation executables |
| **`ProcessAllPendingActionsAsync`** | Tray startup / resume scan | All Library Installations | Per-profile `IsPathCurrentlyRunning` loop | None | Directly dispatches to `EnableDxvkAsync` / `ReapplyAsync` / `DisableDxvkAsync` | Sequentially acquires per-installation lease; defers busy/running installations without dropping pending intents from store |
| **`UpdateAllEnabledAsync`** | Daily scheduler / release check | All Enabled Profiles | Per-profile `RequestUpdateByPathAsync` | None | Iterative execution | Iteratively acquires per-installation lease; isolates batch failures; continues to next game on lease refusal |
| **`RestoreAllAsync`** | Global emergency restore | All Library Installations | `IsPathCurrentlyRunning(profile.ExePath)` before queueing | None | Directly calls `DisableDxvkAsync` if inactive; queues if active | Iteratively acquires per-installation lease; failure on one does not block or taint others |
| **`AdoptExisting` / `AdoptExistingAsync`** | Manual adoption wizard | Target Directory & Profile | **None** | None | Direct ownership creation via `DxvkInstaller.AdoptExisting` | Acquires installation lease to prevent concurrent installer execution during baseline capture |
| **`DxvkInstaller.ApplyToGameAsync`** | Public installer method | Single Profile & Release | **None** | None | Direct multi-file transaction | Preserves public signature; delegates to internal overload accepting `IOperationLease`; acquires lease if null; split payload extraction from mutation |
| **`DxvkInstaller.AdoptExisting`** | Public installer method | Single Profile & Assessment | **None** | None | Synchronous baseline capture & commit | Preserves public signature; delegates to internal overload accepting `IOperationLease`; acquires lease if null |
| **`DxvkRollback.RestoreOriginalDllsAsync`** | Public rollback method | Single Profile | **None** | None | Direct multi-file restoration | Preserves public signature; delegates to internal overload accepting `IOperationLease`; acquires lease if null |

---

## 3. Two-Tier Resource & Lock Hierarchy (Simpler Safe Scope)

To prevent same-installation mutation races and whole-library metadata collisions without introducing an unreviewed three-way delta merge algorithm, we adopt a concrete, safe **Two-Tier Resource & Lock Hierarchy**:

```
┌────────────────────────────────────────────────────────────────────────┐
│                        TIER 1: INSTALLATION LEASE                      │
│             (Fine-Grained, per Canonical Target Game Path)             │
│  - Discoverable in shared coordination namespace (independent of       │
│    storage root).                                                      │
│  - Held across: Pre-validation -> Activity Check -> Target Mutation -> │
│    Metadata Commit -> Recovery Verification.                           │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
                        Requires Metadata Commit
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│                     TIER 2: SHARED STORE ROOT LEASE                    │
│               (Coarse-Grained, per Storage Root Directory)             │
│  - Scoped to the metadata storage root (game-library.json).            │
│  - Serializes full mutation/commit scope for a shared store root.      │
│  - Verifies authoritative baseline BEFORE target mutation.             │
└────────────────────────────────────────────────────────────────────────┘
```

### 3.1 Lock Ordering & Deadlock Prevention
- **Strict Order:** An operation acquiring both tiers must ALWAYS acquire the **Tier 1 Installation Lease FIRST**, and the **Tier 2 Shared Store Root Lease SECOND**.
- **No Inversion:** Code holding the Tier 2 Store Lock must NEVER attempt to acquire a Tier 1 Installation Lease, await external network I/O, or perform long-running transactions.
- **Independent Roots:** Distinct storage roots (e.g. separate synthetic roots in tests or independent profiles) operate with independent Tier 2 locks and never serialize against each other.

### 3.2 Pre-Mutation Authoritative State Acquisition & Invariant Guard
Rather than attempting a complex late merge of stale in-memory state:
1. **Pre-Mutation Freshness Check:** When acquiring the Tier 2 Store Lease, the operation loads or verifies that the current in-memory store state matches the authoritative on-disk `game-library.json`.
2. **Conservative Refusal on Stale Baseline:** If disk state was modified externally by another process or unowned changes are present, the operation **conservatively refuses BEFORE touching target files**.
3. **Preservation of #171 Safety Evidence:** A store refresh or commit must **never erase retained uncertainty or recovery evidence** (`RestorationState.AttentionRequired`, uncommitted transaction evidence, or recorded refusal reasons). If disk state indicates external unowned tampering, the operation refuses immediately.

### 3.3 Full Metadata Writer File List
The Tier 2 Store Lease serializes all writers touching shared metadata:
- `src/DXVKCompanion/Storage/GameLibraryStore.cs` (`game-library.json` updates and snapshot saves)
- `src/DXVKCompanion/Storage/ProfileStore.cs` (`profiles.json` updates)
- `src/DXVKCompanion/DXVK/DxvkManager.cs` (`_pending` dictionary and `pending-actions.json` intent tracking)
- Detection & inspection writes (recording snapshots)

---

## 4. Target-Scoped Coordination Namespace & Async-Safe Lease Primitive

### 4.1 Target Identity Independent of Storage Directory
Placing lease files inside `<storageDir>/leases/` introduces a critical flaw: two cooperating processes or testing harnesses configured with different storage directories targeting the **same game** would open different lock files and fail to exclude each other.

To guarantee true target exclusion:
1. **Canonical Coordination Root:** Cooperating DXVK Companion processes within the current Windows user session share a canonical coordination directory:
   $$\text{CoordinationRoot} = \text{Path.Combine}(\text{Environment.GetFolderPath}(\text{SpecialFolder.LocalApplicationData}), \text{"DXVKCompanion", "locks"})$$
   *(Note: Test suites and isolated harnesses may inject an environment override `DXVK_COORDINATION_DIR` to sandbox coordination roots cleanly).*
2. **Target Lock File Identity:** Computed strictly from the canonicalized target installation root:
   $$\text{LockFileName} = \$\text{"target\_" + ComputeSha256(CanonicalTargetPath) + ".lock"}$$
   $$\text{LockFilePath} = \text{Path.Combine}(\text{CoordinationRoot}, \text{LockFileName})$$
   Every cooperating process targeting the same physical game directory opens the exact same lock file.

### 4.2 Async-Safe Primitive: OS Lock FileStream (`FileShare.None`)
To eliminate the thread-affinity crash flaw of `System.Threading.Mutex` across async `await` boundaries:
1. **Primitive:** An exclusive, non-shared `FileStream`:
   ```csharp
   new FileStream(
       lockFilePath,
       FileMode.OpenOrCreate,
       FileAccess.ReadWrite,
       FileShare.None,
       4096,
       FileOptions.DeleteOnClose);
   ```
2. **Zero Thread Affinity:** Unlike `Mutex`, `FileStream` can be safely closed or disposed from **any thread pool thread** in a `finally` block or asynchronous continuation.
3. **Kernel Enforced Across All Processes:** Windows kernel filesystem semantics enforce `FileShare.None` across all processes. If another process holds the lease, opening throws `IOException` (`ERROR_SHARING_VIOLATION`), signaling busy.
4. **Lifecycle & `DeleteOnClose` Contention:**
   - Under `FileOptions.DeleteOnClose`, closing the stream deletes the file entry.
   - If Process B attempts to open the file while Process A is closing/deleting it, Windows NT may return `ERROR_ACCESS_DENIED` or `ERROR_DELETE_PENDING`. The lease acquisition logic handles this with a brief, bounded retry loop (up to 50 ms in 10 ms increments).

### 4.3 Crash vs. Recovery Distinction (Honest Invariant)
- **Lease Release vs. Target State:** When a process crashes, the OS kernel automatically closes open handles, releasing the lease lock. However, **this does NOT restore or heal the filesystem**. If the crash occurred mid-mutation, target files may be partially written, or an uncommitted `.transactions/<txId>/` folder may be left behind.
- **Pre-Operation Safety Inspection:** When an operation acquires the Tier 1 Lease, it inspects whether an incomplete transaction folder (`.transactions/`) exists from a crashed predecessor. If orphaned transaction state or target hash mismatch is detected, the operation does NOT proceed blindly—it initiates compensation / marks `AttentionRequired` / prompts for restoration.

### 4.4 Explicit Unsupported Boundaries & Refusals
DXVK Companion does not claim universal "zero bypass" across unsupported environments:
- **Cross-User / Cross-Session Boundaries:** Separate Windows user accounts have isolated `%LOCALAPPDATA%` roots and cannot coordinate via user-local lock files without shared administrative directories.
- **Network Shares (UNC Paths):** Paths like `\\server\share\game` where SMB mandatory locking semantics or volume identities cannot be guaranteed are **conservatively refused**.
- **Read-Only / Non-Locking Volumes:** Filesystems that reject exclusive locking or file creation in the coordination root are **conservatively refused**.

---

## 5. Path Identity, Ancestor Resolution & Sibling Executables

### 5.1 Factual Clarification on `GameInstallation.NormalizeInstallationPath`
Inspection of `src/DXVKCompanion/Models/GameInstallation.cs` confirms that `NormalizeInstallationPath` only applies `Path.GetFullPath` and trims trailing separators; **it does not resolve NTFS junctions, directory symbolic links, or normalize letter casing**.

### 5.2 Canonical Target Path Resolution & Ancestor Traversal
A game folder `C:\Games\Doom\` may reside inside an ancestor directory `C:\Games` that is an NTFS junction to `D:\SteamLibrary\Games\`. Inspecting only the leaf directory fails to detect aliasing.

**Resolution Algorithm:**
1. Call `Path.GetFullPath(path).TrimEnd('\\', '/')`.
2. Traverse ancestor directories from the leaf up to the volume root. For each directory component, if `Directory.ResolveLinkTarget(component, returnFinalTarget: true)` returns a target, resolve it to its physical target.
3. Apply invariant uppercase canonicalization (`ToUpperInvariant()`) to eliminate Windows case variations (`C:\Games` vs `c:\games`).
4. **Conservative Refusal:** If directory link resolution fails, or if the path sits on an unsupported alias (e.g. `subst` virtual drive), DXVK Companion **conservatively refuses mutation** with: `"Installation path utilizes an unsupported directory alias or junction that cannot be verified."`

### 5.3 Sibling & Subdirectory Executable Resolution
- All operations resolve the primary installation root using `_gameLibraryStore.FindInstallationForExecutable(exePath)` or directory traversal.
- The Tier 1 Lease is **always acquired on the canonical installation root**, never on an individual executable file or subdirectory.
- All sibling executables belonging to the same installation share the same installation root and are covered by the same lease.

---

## 6. Process Activity Evidence Model & Realistic Windows Limits

### 6.1 Three-State Evidence Model with Candidate Filtering
To prevent unrelated elevated system processes (`services.exe`, `csrss.exe`, background services) from causing false `Unknown` states and blocking normal Windows usage, process inspection uses **Candidate Relevance Filtering**:

```
                  [Process Inspection Request for Installation]
                                       │
                                       ▼
                     [1. Identify Candidate Executables]
                  (Primary Exe + Registered Siblings in Profile)
                                       │
                                       ▼
                     [2. Enumerate Running OS Processes]
                                       │
                    ┌──────────────────┴──────────────────┐
                    ▼                                     ▼
        [Enumeration Throws Exception]          [Process List Obtained]
                    │                                     │
                    ▼                                     ▼
             [State: Unknown]                [Filter by Candidate Image Name]
                                                          │
                             ┌────────────────────────────┴────────────────────────────┐
                             ▼                                                         ▼
                 [No Matching Process Names]                              [Matching Process Name Found]
                             │                                                         │
                             ▼                                                         ▼
               [State: ConfirmedNotRunning]                              [Query Process Full Image Path]
                     (Safe to Mutate)                                                  │
                                                          ┌────────────────────────────┴────────────────────────────┐
                                                          ▼                                                         ▼
                                               [Path Matches Install Root]                             [Access Denied / Query Fault]
                                                          │                                                         │
                                                          ▼                                                         ▼
                                                   [State: Running]                                          [State: Unknown]
                                                  (Refuse / Defer)                                      (Candidate Access Denied)
```

### 6.2 Evidence State Definitions
1. **`ConfirmedNotRunning`:** Process enumeration completed successfully. Zero running processes match candidate executable image names, OR matching processes positively resolved to completely different directories (e.g. a different game with the same executable name).
2. **`Running`:** One or more running processes with matching candidate names were positively confirmed to execute from the target installation root directory.
3. **`Unknown`:**
   - Process enumeration itself failed (`Process.GetProcesses()` threw an OS exception), OR
   - A running process matching a **candidate game executable name** returned `Win32Exception: Access is denied` when querying its image path (e.g. running under anti-cheat protection or elevated administrator context). Unrelated processes with access denied do NOT cause `Unknown`.

### 6.3 Acknowledged Candidate Limits, Image Normalization & PID Reuse
- **Candidate Set Limitation:** Candidate filtering protects only known/registered executables in `GameInstallation.Executables`. An unregistered, differently named child process spawned by the game is an acknowledged limitation. (Candidate discovery occurs during installation scanning by registering all `.exe` files in the directory).
- **Image Name Normalization:** Process image names are compared without `.exe` extension using `StringComparison.OrdinalIgnoreCase`.
- **PID Reuse Detection:** The inspector captures `process.Id` alongside `process.StartTime`. If a process exits and its PID is reassigned by Windows, start-time mismatch proves it is a different process.
- **Relaunch After Download:** If a user launches the game while DXVK Companion is downloading assets, the in-lease pre-mutation revalidation check detects the newly launched process and defers mutation.

### 6.4 Realistic Limits & Honest TOCTOU Boundary
- **Cooperative Software Exclusion:** DXVK Companion guarantees that all cooperating operations (UI, CLI, background tasks, scheduler) are mutually excluded by the Tier 1 Installation Lease.
- **External User/Launcher Start Race (TOCTOU):** No user-mode software can physically prevent an external user or external game launcher from executing `Game.exe` at the exact millisecond files are being copied to disk, unless an invasive kernel minifilter driver is installed.
- **Anti-Cheat & Non-Invasive Stance:** DXVK Companion strictly refuses to terminate, suspend, or inject code into game processes.
- **Honest Invariant:** DXVK Companion guarantees it will never initiate or permit a mutation when an installation is actively observed running or in an `Unknown` state. Bounded pre-mutation activity-check under the Tier 1 Lease minimizes the external race window to the duration of the filesystem copy.

---

## 7. Preservation of Queued Intents & Refusal Semantics

When an operation cannot proceed due to `Busy` (lease held), `Running` (game active), or `Unknown` (candidate access denied), the return and persistence behaviors are strictly differentiated:

### 7.1 Fresh Explicit User Requests (`EnableDxvkAsync`, `DisableDxvkAsync`)
- Immediate return: `false` or `DxvkActionResult.Failed`.
- Sets `LastRefusalReason` with clear diagnostic:
  - If Busy: `"Operation in progress for installation: <path>. Please retry shortly."`
  - If Running: `"Game is currently running: <path>. Close the game before modifying files."`
  - If Unknown: `"Cannot verify game activity: candidate process access denied (protected or elevated)."`
- **State Preservation:** Profile and installation records remain completely unchanged. No spurious `AttentionRequired` status is set.

### 7.2 Queued Pending Actions (`ProcessAllPendingActionsAsync`, `ApplyPendingAsync`)
- When batch or background processing encounters `Busy`, `Running`, or `Unknown`:
  - **Preserve Pending Intent:** The persisted `PendingAction` in `game-library.json` is **preserved intact**. It is NOT deleted, cleared, or marked failed.
  - **Preserve Transient Intent:** In `ApplyPendingAsync`, the transient action in `_pending` is NOT dropped if the operation is deferred.
  - The installation is skipped in the current pass and will be retried upon explicit triggers: application startup scan (`ProcessAllPendingActionsAsync`), process-exit notification (`ApplyPendingAsync`), or explicit user action.
- **Terminal Incompatibility vs. Deferred Refusal:**
  - Only permanent compatibility violations (e.g. game reclassified to unsupported DX12/Vulkan API) trigger `CancelIncompatiblePendingAction`, recording durable cancellation evidence.
  - Transient busy/running conditions never cancel pending actions.

---

## 8. Single Enforceable Lease Owner & Token Architecture

To ensure that direct calls to `DxvkInstaller` or `DxvkRollback` cannot bypass exclusion, while `DxvkManager -> DxvkInstaller -> MultiFileTransactionEngine` does not attempt duplicate nested acquisitions:

### 8.1 Internal Seams & Token Passing
- **Seams:**
  ```csharp
  internal interface IOperationLeaseCoordinator
  {
      ValueTask<IOperationLease?> TryAcquireAsync(string canonicalInstallationRoot, TimeSpan timeout, CancellationToken ct = default);
  }

  internal interface IOperationLease : IAsyncDisposable, IDisposable
  {
      string InstallationRoot { get; }
      string LeaseKey { get; }
      bool IsAcquired { get; }
  }
  ```
- **Single Enforceable Flow:**
  - `DxvkManager` acquires the `IOperationLease` token before initiating pre-validation.
  - Existing public methods preserve their signatures:
    - `public Task<bool> ApplyToGameAsync(GameProfile profile, ReleaseInfo release)`
    - `public Task<bool> RestoreOriginalDllsAsync(GameProfile profile)`
    - `public bool AdoptExisting(GameProfile profile, ExistingDxvkAssessment assessment)`
  - Internal overloads accept `IOperationLease lease`:
    - `internal Task<bool> ApplyToGameAsync(GameProfile profile, ReleaseInfo release, IOperationLease lease)`
    - `internal Task<bool> RestoreOriginalDllsAsync(GameProfile profile, IOperationLease lease)`
    - `internal bool AdoptExisting(GameProfile profile, ExistingDxvkAssessment assessment, IOperationLease lease)`
  - If a public method is called directly (no token passed), it acquires the lease from `IOperationLeaseCoordinator` and disposes it in a `finally` block.
  - Nested calls pass the existing token, which validates target path equality and bypasses duplicate re-acquisition.

---

## 9. Concrete Implementation Plan & Exact Production File List

The following exact files will be added or modified in Phase B:

```
src/DXVKCompanion/
├── Safety/
│   ├── IOperationLeaseCoordinator.cs     <-- [NEW] Internal lease coordinator interface
│   ├── OperationLeaseCoordinator.cs      <-- [NEW] FileStream-based target lock coordinator
│   ├── IOperationLease.cs                <-- [NEW] Disposable lease token interface
│   ├── IProcessActivityInspector.cs      <-- [NEW] Internal process activity inspection seam
│   ├── ProcessActivityInspector.cs       <-- [NEW] Candidate-filtered Windows process inspection
│   └── ProcessActivityEvidence.cs        <-- [NEW] 3-state evidence enum (Running, ConfirmedNotRunning, Unknown)
├── Storage/
│   ├── GameLibraryStore.cs               <-- [MOD] Tier 2 Storage Lock & pre-mutation baseline validation
│   └── ProfileStore.cs                   <-- [MOD] Synchronized with Tier 2 Storage Lock
└── DXVK/
    ├── DxvkManager.cs                    <-- [MOD] Acquires Tier 1 lease; passes tokens; preserves pending intents
    ├── DxvkInstaller.cs                  <-- [MOD] Internal overload with IOperationLease; splits source extraction
    └── DxvkRollback.cs                   <-- [MOD] Internal overload with IOperationLease; enforces lease before restore
```

---

## 10. Architectural Decision Brief for Human Authority

The following architectural decision questions are submitted for human merge authority consideration:

### Decision 1: Contention Policy on Busy Installation Lease
- **Option A (Recommended — Immediate Refusal with Retryable Diagnostic):**
  When an operation attempts to acquire an installation lease that is held by another operation, it attempts acquisition with a short, bounded timeout (e.g. 500 ms). If unavailable, it immediately returns `false` / `DxvkActionResult.Failed` with: `"Another operation is currently modifying this installation. Please retry shortly."`
  *Rationale:* Deterministic, zero UI hang, avoids queueing conflicting sequential user clicks.
- **Option B (Asynchronous Wait Queue):**
  Operations wait in an async queue until the in-flight operation completes.
  *Risk:* High risk of user intent confusion (e.g. user toggles Disable then Enable rapidly, causing both to execute in sequence).

### Decision 2: Cross-Process Application Instance Policy
- **Option A (Recommended — Per-Installation Lease with Parallel Companion Processes):**
  Allow separate DXVK Companion processes (e.g. CLI tool and Tray application) to run simultaneously, using per-installation leases in the shared coordination namespace to coordinate file access.
  *Rationale:* Maximizes developer and scripting flexibility; non-conflicting games can be managed simultaneously.
- **Option B (Strict Single-Instance Application Mutex):**
  Enforce a single global application mutex (`Global\DXVKCompanion_AppMutex`) at process startup. Second processes immediately exit or send commands via named pipes.

### Decision 3: Unresolved Process Evidence (`Unknown`) Policy
- **Option A (Recommended — Strict Conservative Refusal):**
  If process activity cannot be verified due to access denial on a candidate game executable, refuse mutation unconditionally.
  *Rationale:* Prevents corrupting protected or anti-cheat enabled games.
- **Option B (Refusal with Explicit User Override):**
  Refuse by default, but allow an explicit CLI flag (`--force-ignore-activity`) or UI confirmation dialog.
