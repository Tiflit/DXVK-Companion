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
   When Operation 1 (e.g. Update to 2.6) begins applying target files, it creates an operation-scoped undo copy in `.transactions/<Op1_Id>/undo/`. If Operation 1 pauses or interleaves, and Operation 2 (e.g. Update to 2.7) executes concurrently on the same game directory, Operation 2 applies its files and commits to `game-library.json`. If Operation 1 subsequently encounters an error (e.g. simulated disk fault or network failure), Operation 1 triggers `RecoverAttempted`. Its undo copy contains pre-operation bytes (version 2.5), which it blindly copies back over `d3d11.dll`. Operation 1's rollback **silently destroys Operation 2's committed version 2.7 files**, leaving the filesystem reverted to 2.5 while `game-library.json` claims version 2.7.
2. **Concurrent Mutation & Mixed Payload Infiltration:**
   When two deploy operations run concurrently on the same installation without an operational lease, file mutations interleave at the operating system level. Because `MultiFileTransactionEngine` prepares and copies files sequentially (`d3d11.dll`, then `dxgi.dll`, then `dxvk.conf`), an unexcluded interleaving risks deploying mixed version files into the game directory.
3. **Shared Metadata Baseline Conflicts & Stale Store Rejections:**
   `GameLibraryStore` utilizes whole-file optimistic serialization (`VerifyDiskMatchesAuthoritativeBaselineLocked`) backed by `lock (_sync)`. When two separate operations (even on independent installations) concurrently attempt to commit to the same shared `game-library.json`, the second commit observes a modified baseline and legitimately refuses the write. If multiple service graphs or processes hold stale in-memory store instances, operations fail with baseline mismatch errors rather than coordinating orderly metadata updates.
4. **False Inactivity & Activity Check Bypasses:**
   `DxvkManager.IsPathCurrentlyRunning` inspects `Process.GetProcesses()` matching a single `exePath`. If any process throws `Win32Exception` (e.g. access denied on `MainModule` due to anti-cheat or elevated privileges), it catches and swallows the exception, erroneously reporting that the game is inactive (`false`). Direct entry points (`EnableDxvkAsync`, `DisableDxvkAsync`, `ReapplyAsync`, `ApplyToGameAsync`, `RestoreAsync`) perform **no activity check whatsoever**.
5. **TOCTOU Gap in Queued Execution (`ApplyPendingAsync`):**
   `RequestEnableByPathAsync` checks activity before downloading releases. During the seconds required to fetch network archives and stage files, the game can launch. `ApplyPendingAsync` removes the transient intent from `_pending` before execution; if mutation fails or is deferred, the intent is lost unless preserved.

---

## 2. Comprehensive Entry-Point Inventory

The table below catalogs every public, requested, batch, startup, exit, and direct mutation entry point in DXVK Companion against the actual source implementation, detailing current activity checks, locking behaviors, and proposed lease boundaries.

| Entry Point | Trigger / Caller | Target Scope | Current Activity Check | Current Exclusion / Locking | Staged vs Direct Mutation | Proposed Lease Boundary & Revalidation |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`RequestEnableByPathAsync`** | UI (Manage Games) / CLI | Single Game Profile | `IsPathCurrentlyRunning(profile.ExePath)` at entry | None | Direct if inactive; Queued in `_pending` & Store if active | Prepares release payload unleased; acquires installation lease; revalidates activity under lease before calling `EnableDxvkAsync` |
| **`RequestEnableAsync`** | UI (Tray running-game menu) | Single Game Profile | `IsStillRunning(process)` on supplied handle | None | Direct if inactive; Queued in `_pending` & Store if active | Same as `RequestEnableByPathAsync` |
| **`EnableDxvkAsync`** | Direct public / programmatic | Single Game Profile | **None** | None | Direct mutation via `DxvkInstaller.ApplyToGameAsync` | Acquires installation lease; validates activity is `ConfirmedNotRunning`; passes lease token to `DxvkInstaller` |
| **`RequestDisableByPathAsync`** | UI (Manage Games) / CLI | Single Game Profile | `IsPathCurrentlyRunning(profile.ExePath)` at entry | None | Direct if inactive; Queued in `_pending` & Store if active | Acquires installation lease; revalidates activity under lease before calling `DisableDxvkAsync` |
| **`RequestDisableAsync`** | UI (Tray running-game menu) | Single Game Profile | `IsStillRunning(process)` on supplied handle | None | Direct if inactive; Queued in `_pending` & Store if active | Same as `RequestDisableByPathAsync` |
| **`DisableDxvkAsync`** | Direct public / programmatic | Single Game Profile | **None** | None | Direct mutation via `DxvkRollback.RestoreAsync` | Acquires installation lease; validates activity is `ConfirmedNotRunning`; passes lease token to `DxvkRollback` |
| **`RequestUpdateByPathAsync`** | UI (Update button) / CLI | Single Game Profile | `IsPathCurrentlyRunning(profile.ExePath)` at entry | None | Delegates to `RequestEnableByPathAsync` | Same as `RequestEnableByPathAsync` |
| **`RequestReapplyByPathAsync`** | UI (Reapply button) | Single Game Profile | `IsPathCurrentlyRunning(profile.ExePath)` at entry | None | Direct if inactive; Queued in `_pending` & Store if active | Acquires installation lease; revalidates activity under lease before calling `ReapplyAsync` |
| **`ReapplyAsync`** | Direct public / programmatic | Single Game Profile | **None** | None | Direct mutation via `DxvkInstaller.ReapplyAsync` | Acquires installation lease; passes lease token to `DxvkInstaller.ReapplyAsync` |
| **`ApplyPendingAsync`** | Tray process exit handler | Single Executable Path | **None** (assumes exit event proves total inactivity) | None | Direct mutation via `EnableDxvkAsync` / `ReapplyAsync` / `DisableDxvkAsync` | Retains transient intent in `_pending` until successful completion; acquires lease; revalidates **all** installation executables |
| **`ProcessAllPendingActionsAsync`** | Tray startup / resume scan | All Library Installations | Per-profile `IsPathCurrentlyRunning` loop | None | Directly dispatches to `EnableDxvkAsync` / `ReapplyAsync` / `DisableDxvkAsync` | Sequentially acquires per-installation lease; defers busy/running installations without dropping pending intents from store |
| **`UpdateAllEnabledAsync`** | Daily scheduler / release check | All Enabled Profiles | Per-profile `RequestUpdateByPathAsync` | None | Iterative execution | Iteratively acquires per-installation lease; isolates batch failures; continues to next game on lease refusal |
| **`RestoreAllAsync`** | Global emergency restore | All Library Installations | `IsPathCurrentlyRunning(profile.ExePath)` before queueing | None | Directly calls `DisableDxvkAsync` if inactive; queues if active | Iteratively acquires per-installation lease; failure on one does not block or taint others |
| **`AdoptExisting` / `AdoptExistingAsync`** | Manual adoption wizard | Target Directory & Profile | **None** | None | Direct ownership creation via `DxvkInstaller.AdoptExisting` | Acquires installation lease to prevent concurrent installer execution during baseline capture |
| **`DxvkInstaller.ApplyToGameAsync`** | Direct installer entry point | Single Profile & Release | **None** | None | Direct multi-file transaction | Accepts optional `IOperationLease`; acquires lease if null; split payload extraction from mutation; acquires storage lock at commit |
| **`DxvkInstaller.AdoptExisting`** | Direct installer entry point | Single Profile & Assessment | **None** | None | Synchronous baseline capture & commit | Accepts optional `IOperationLease`; acquires lease if null; acquires storage lock to commit initial ownership record |
| **`DxvkRollback.RestoreAsync`** | Direct rollback entry point | Single Profile | **None** | None | Direct multi-file restoration | Accepts optional `IOperationLease`; acquires lease if null; acquires storage lock at commit |
| **`DxvkRollback.RestoreFromLibraryAsync`** | Library-driven restore | Single Installation Record | **None** | None | Direct multi-file restoration | Accepts optional `IOperationLease`; acquires lease if null; acquires storage lock at commit |

---

## 3. Two-Tier Resource & Lock Hierarchy

To prevent both same-installation mutation races and whole-library metadata collisions without creating global bottlenecks, we define a strict **Two-Tier Resource & Lock Hierarchy**:

```
┌────────────────────────────────────────────────────────────────────────┐
│                        TIER 1: INSTALLATION LEASE                      │
│            (Fine-Grained, per Normalized Installation Root)            │
│  - Held throughout the entire mutation lifecycle:                      │
│    Pre-validation -> Process Activity Check -> Target Mutation ->       │
│    Metadata Commit / Recovery Verification                             │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
                        Requires Metadata Commit
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│                     TIER 2: STORAGE DIRECTORY LOCK                     │
│               (Coarse-Grained, per Storage Root Directory)             │
│  - Held briefly only during whole-file read-modify-write phases:       │
│    Authoritative Baseline Read -> In-Memory Mutation -> Atomic Save     │
└────────────────────────────────────────────────────────────────────────┘
```

### 3.1 Lock Ordering & Deadlock Prevention
- **Strict Order:** An operation acquiring both tiers must ALWAYS acquire the **Tier 1 Installation Lease FIRST**, and the **Tier 2 Storage Directory Lock SECOND**.
- **No Inversion:** Code holding the Tier 2 Storage Lock must NEVER attempt to acquire a Tier 1 Installation Lease, await external I/O, or perform long-running transactions.
- **Short Duration:** Tier 2 Storage Lock is held exclusively during atomic in-memory update and disk commit (`ExecuteClassifiedCommitLocked`).
- **Independent Roots:** Distinct storage directories (e.g. in test suites or separate profiles) operate with independent Tier 2 locks and never contend.

### 3.2 Stale Store Handling & Evidence Preservation
When an operation holding a Tier 1 Installation Lease acquires the Tier 2 Storage Lock to commit:
- `GameLibraryStore` rechecks disk freshness via `VerifyDiskMatchesAuthoritativeBaselineLocked`.
- If another installation was committed while this operation was staging, `GameLibraryStore` incorporates the disk changes for other installations without overwriting them.
- **Preservation Invariant:** A store refresh must **never erase retained uncertainty or recovery evidence** (`AttentionRequired`, `ActiveRecovery`, or recorded refusal reasons). If disk state indicates external unowned tampering, the operation conservatively refuses rather than silently clearing safety markers.

---

## 4. Async-Safe Operation Lease Primitive & Enforceable Ownership

### 4.1 The Thread-Affinity Flaw of `System.Threading.Mutex`
In .NET on Windows, `System.Threading.Mutex` enforces **strict OS thread affinity**. The exact OS thread that calls `WaitOne()` must be the thread that calls `ReleaseMutex()`; otherwise, `ReleaseMutex()` throws `ApplicationException: Object synchronization method was called from an unsynchronized block of code`.

In modern asynchronous C# code (`async` / `await`), execution continuations after an `await` (such as `await _engine.ExecuteTransactionAsync(...)`) resume on arbitrary thread pool threads. Therefore, a standard `System.Threading.Mutex` **cannot safely protect asynchronous operations across await boundaries**.

### 4.2 Viable Async-Safe Lease Primitive: Lock FileStream (`FileShare.None`)
To provide robust, cross-process and in-process mutual exclusion without thread affinity, the Tier 1 Installation Lease is implemented via an exclusive OS file handle:

1. **Primitive:** An exclusive, non-shared `FileStream` opened on a lease marker file:
   ```csharp
   new FileStream(
       leaseFilePath,
       FileMode.OpenOrCreate,
       FileAccess.ReadWrite,
       FileShare.None,
       4096,
       FileOptions.DeleteOnClose);
   ```
2. **Zero Thread Affinity:** Unlike a `Mutex`, a `FileStream` can be safely closed or disposed from **any thread pool thread** in a `finally` block or asynchronous continuation.
3. **Kernel Enforced Across All Processes:** Windows kernel filesystem semantics enforce `FileShare.None` across all processes, preventing CLI, Tray, and third-party tools from acquiring overlapping leases on the same installation.
4. **Crash-Proof Cleanup:** If a process crashes or is forcefully terminated, the Windows kernel automatically closes all open file handles, releasing the lock immediately and removing the file (`FileOptions.DeleteOnClose`). No abandoned mutex states or orphaned locks can occur.
5. **No Special Privileges Required:** Lease files reside in the user's local storage directory (`%LOCALAPPDATA%\DXVKCompanion\leases\<Hash>.lease`). This requires zero administrative privileges and avoids the security DACL complexities of `Global\` kernel objects.
6. **In-Process Coordination:** Combined with an in-process `ConcurrentDictionary<string, SemaphoreSlim>` to eliminate unnecessary disk contention for concurrent tasks within the same process.

### 4.3 Single Enforceable Owner: `IOperationLeaseCoordinator` & Lease Tokens
To ensure that direct calls to `DxvkInstaller` or `DxvkRollback` cannot bypass exclusion, while `DxvkManager -> DxvkInstaller -> MultiFileTransactionEngine` does not attempt duplicate nested acquisitions:

- Introduce `IOperationLeaseCoordinator`:
  ```csharp
  public interface IOperationLeaseCoordinator
  {
      ValueTask<IOperationLease?> TryAcquireAsync(string installationRoot, TimeSpan timeout, CancellationToken ct = default);
  }

  public interface IOperationLease : IAsyncDisposable, IDisposable
  {
      string InstallationRoot { get; }
      string LeaseKey { get; }
      bool IsAcquired { get; }
  }
  ```
- **Internal Lease Token Passing:**
  - `DxvkManager` acquires the `IOperationLease` token before initiating pre-validation.
  - `DxvkInstaller.ApplyToGameAsync` and `DxvkRollback.RestoreAsync` accept an optional `IOperationLease? leaseToken = null`.
  - If a token is provided: The installer/rollback verifies that `leaseToken.IsAcquired` is true and `leaseToken.InstallationRoot` matches the target directory. It proceeds without re-acquiring.
  - If no token is provided (direct external call): The installer/rollback acquires the lease directly from `IOperationLeaseCoordinator` and disposes it upon completion.
  - This architecture guarantees 100% enforcement with zero bypass vectors and zero reentrancy deadlocks.

---

## 5. Path Identity, Alias Handling & Sibling Resolution

### 5.1 Factual Correction on `GameInstallation.NormalizeInstallationPath`
Inspection of `src/DXVKCompanion/Models/GameInstallation.cs` reveals:
```csharp
public static string NormalizeInstallationPath(string path)
{
    if (string.IsNullOrWhiteSpace(path))
        throw new ArgumentException("Installation path is required.", nameof(path));
    var fullPath = Path.GetFullPath(path);
    var root = Path.GetPathRoot(fullPath);
    if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        return root;
    return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
```
`NormalizeInstallationPath` performs lexical resolution via `Path.GetFullPath` and trims trailing separators; **it does not resolve NTFS junctions, directory symbolic links, or normalize letter casing**.

### 5.2 Canonical Lease Key & Case-Normalized Hashing
To prevent split-brain lease locking on Windows (where `C:\Games\Doom` and `c:\games\doom` refer to the same folder):
1. **Case Normalization:** The installation root path is canonicalized using invariant uppercase:
   $$\text{CanonicalRoot} = \text{GameInstallation.NormalizeInstallationPath}(path).\text{ToUpperInvariant}()$$
2. **Stable Hash Key:** The lease key is computed via SHA256 over the UTF-8 bytes of `CanonicalRoot`:
   $$\text{LeaseKey} = \text{SHA256}(\text{CanonicalRoot})$$
   Lease file path: `Path.Combine(storageDir, "leases", $"{LeaseKey}.lease")`.

### 5.3 Physical Alias Handling & Conservative Refusal
- If a target directory is an NTFS reparse point (junction or directory symlink), `Directory.ResolveLinkTarget(path, returnFinalTarget: true)` is queried.
- If the target path resolves to a physical volume path, the resolved physical path is used for lease key computation.
- **Conservative Refusal Invariant:** If directory link resolution fails, or if the path uses unsupported aliasing (e.g. `subst` virtual drives or network shares with indeterminate physical identities), DXVK Companion **conservatively refuses mutation** with a clear refusal diagnostic: `"Installation path utilizes an unsupported directory alias or junction that cannot be verified."`

### 5.4 Sibling & Subdirectory Executable Resolution
Games often place executables in subdirectories (e.g. `<Root>\bin\x64\Game.exe` alongside `<Root>\Launcher.exe`).
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
1. **`ConfirmedNotRunning`:** Process enumeration completed successfully. Zero running processes match the candidate executable image names, OR matching processes positively resolved to completely different directories (e.g. a different game with the same executable name).
2. **`Running`:** One or more running processes with matching candidate names were positively confirmed to execute from the target installation root directory.
3. **`Unknown`:**
   - Process enumeration itself failed (`Process.GetProcesses()` threw an OS exception), OR
   - A running process matching a **candidate game executable name** returned `Win32Exception: Access is denied` when querying its image path (e.g. running under anti-cheat protection or elevated administrator context). Unrelated processes with access denied do NOT cause `Unknown`.

### 6.3 Tracked PID Lifetime & Identity
To prevent false matches from PID reuse on Windows:
- The inspector captures `process.Id` alongside `process.StartTime`.
- When tracking a running process across time (e.g. waiting for exit), both PID and `StartTime` are compared. A PID match with a different `StartTime` indicates that the original process exited and the PID was reassigned by the OS.

### 6.4 Realistic Limits & Honest TOCTOU Boundary
- **Cooperative Software Exclusion:** DXVK Companion guarantees that all cooperating operations (UI, CLI, background tasks, scheduler) are mutually excluded by the Tier 1 Installation Lease.
- **External User/Launcher Start Race (TOCTOU):** No user-mode software can physically prevent an external user or external game launcher from executing `Game.exe` at the exact millisecond files are being copied to disk, unless an invasive kernel minifilter driver is installed.
- **Anti-Cheat & Non-Invasive Stance:** DXVK Companion strictly refuses to terminate, suspend, or inject code into game processes.
- **Honest Invariant:** DXVK Companion guarantees it will never initiate or permit a mutation when an installation is actively observed running or in an `Unknown` state. Revalidating activity under the Tier 1 Lease immediately prior to file replacement minimizes the external race window to the absolute minimum duration of the atomic filesystem copy.

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
  - The installation is skipped in the current cycle and will be retried automatically on the next cycle, tray resume, or application restart.
- **Terminal Incompatibility vs. Deferred Refusal:**
  - Only permanent compatibility violations (e.g. game reclassified to unsupported DX12/Vulkan API) trigger `CancelIncompatiblePendingAction`, recording durable cancellation evidence.
  - Transient busy/running conditions never cancel pending actions.

---

## 8. Deterministic Fake-Inspector Test Scenarios (Internal Test Seams)

To verify the process evidence model deterministically in Cloud CI without spawning real processes or violating no-child-process rules:

Introduce internal test seam `IProcessActivityInspector`:
```csharp
public interface IProcessActivityInspector
{
    ProcessActivityEvidence InspectActivity(GameInstallation installation);
}
```

The subsequent implementation task will verify the following 7 test scenarios via mock/fake implementations:
1. **Multiple Game Instances:** Two processes running from the same game folder -> returns `Running`.
2. **Known Sibling Executable Running:** Primary `Game.exe` inactive, but registered sibling `bin/x64/Game.exe` running -> returns `Running`.
3. **Relaunch After Exit:** Game process exits, but relaunch occurs before lease acquisition -> caught by in-lease revalidation.
4. **Relevant Candidate Access Denial:** Process named `Game.exe` running elevated returns `Access is denied` -> returns `Unknown`.
5. **Unrelated Inaccessible Process:** Elevated system process (`svchost.exe`) returns `Access is denied` -> safely ignored; returns `ConfirmedNotRunning`.
6. **Enumeration Failure:** `Process.GetProcesses()` throws `Win32Exception` -> returns `Unknown`.
7. **PID Reuse Detection:** Process with same PID but different `StartTime` -> detected as exited / not the original process.

---

## 9. Concrete Implementation Plan & Exact Production File List

The following exact files will be added or modified in Phase B:

```
src/DXVKCompanion/
├── Safety/
│   ├── IOperationLeaseCoordinator.cs     <-- [NEW] Internal lease coordinator interface
│   ├── OperationLeaseCoordinator.cs      <-- [NEW] FileStream-based cross-process & in-process lease
│   ├── IOperationLease.cs                <-- [NEW] Disposable lease token interface
│   ├── IProcessActivityInspector.cs      <-- [NEW] Internal process activity inspection seam
│   ├── ProcessActivityInspector.cs       <-- [NEW] Candidate-filtered Windows process inspection
│   └── ProcessActivityEvidence.cs        <-- [NEW] 3-state evidence enum (Running, ConfirmedNotRunning, Unknown)
├── Storage/
│   └── GameLibraryStore.cs               <-- [MOD] Tier 2 Storage Lock ordering & stale refresh validation
└── DXVK/
    ├── DxvkManager.cs                    <-- [MOD] Acquires Tier 1 lease; passes lease tokens; preserves pending intents
    ├── DxvkInstaller.cs                  <-- [MOD] Accepts optional IOperationLease; enforces lease before transaction
    └── DxvkRollback.cs                   <-- [MOD] Accepts optional IOperationLease; enforces lease before restoration
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
  Allow separate DXVK Companion processes (e.g. CLI tool and Tray application) to run simultaneously, using per-installation leases to coordinate file access.
  *Rationale:* Maximizes developer and scripting flexibility; non-conflicting games can be managed simultaneously.
- **Option B (Strict Single-Instance Application Mutex):**
  Enforce a single global application mutex (`Global\DXVKCompanion_AppMutex`) at process startup. Second processes immediately exit or send commands via named pipes.

### Decision 3: Unresolved Process Evidence (`Unknown`) Policy
- **Option A (Recommended — Strict Conservative Refusal):**
  If process activity cannot be verified due to access denial on a candidate game executable, refuse mutation unconditionally.
  *Rationale:* Prevents corrupting protected or anti-cheat enabled games.
- **Option B (Refusal with Explicit User Override):**
  Refuse by default, but allow an explicit CLI flag (`--force-ignore-activity`) or UI confirmation dialog.
