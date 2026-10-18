# Ownership Commit & Recovery Boundary Design (`#169` / `#171`)

- **Task Contracts:** [Issue #169](https://github.com/Tiflit/DXVK-Companion/issues/169) (Reproducers & Design) and [Issue #171](https://github.com/Tiflit/DXVK-Companion/issues/171) (Bounded In-Process Production Implementation)
- **Baseline `main` Commit:** `fb598add34d3e4eb936c714dc7be6e5214e6318a` (includes [PR #168](https://github.com/Tiflit/DXVK-Companion/pull/168) / [Issue #166](https://github.com/Tiflit/DXVK-Companion/issues/166))
- **Expected-Red Baseline Evidence:** [Draft PR #170](https://github.com/Tiflit/DXVK-Companion/pull/170) (`860140e5c3fbc7fc03047db532a6495734a568ce`, Run `39149282765`: `total=883, passed=877, failed=6, skipped=0`)
- **Executable Regression Suite:** `tests/DXVKCompanion.PhaseA.Tests/OwnershipPersistenceFailureTests.cs`
- **Canonical Spec Authority:** `docs/spec/DXVK-COMPANION-SPEC.md` (§§12, 13, 18, 22, 23, 27) and `docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md`

---

## 1. Problem Statement & Observed Failure Boundaries on `main`

After [PR #168](https://github.com/Tiflit/DXVK-Companion/pull/168) (`#166`), `MultiFileTransactionEngine.Execute` (`src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs`) guarantees that **within** `Execute(request)`:
- pre-operation target snapshots are captured in an operation-isolated namespace (`<TransactionStoreRoot>/.transactions/<txId>/undo/<relPath>`),
- newly created native baseline backups are staged in `<TransactionStoreRoot>/.transactions/<txId>/native-backups/<relPath>` and published only after verification,
- failed `Install`, `Update`, `Reapply`, and `Restore` file mutations inside `Execute` recover exact pre-operation target bytes/existence, and
- unresolved file recovery returns `TransactionState.AttentionRequired` / `TransactionOutcome.UnresolvedRecovery` while retaining `.transactions/<txId>/` evidence.

However, four concrete safety gaps remain across the commit boundary between `DxvkInstaller` / `DxvkRollback`, `MultiFileTransactionEngine`, and `GameLibraryStore`:

1. **Premature disposal of per-operation undo evidence (`MultiFileTransactionEngine.cs:195-210`):**
   As soon as `Execute(request)` finishes applying target files (`state = TransactionState.Committed; outcome = TransactionOutcome.Success;`), its `finally` block deletes `<TransactionStoreRoot>/.transactions/<txId>/` **before** the caller (`DxvkInstaller` or `DxvkRollback`) persists `game-library.json` via `_gameLibraryStore.Save(installation)`.
2. **Pre-`Execute` mutations & destructive `Reapply(updateBaseline: true)` backup overwrite (`DxvkInstaller.cs:176-196`, `521-579`; `DxvkRollback.cs:171-183`):**
   - `DxvkInstaller.ApplyToGameAsync` calls `_gameLibraryStore.GetOrCreateInstallation` (which writes `game-library.json` on first install) and mutates `installation.Executables` and `installation.Configuration` (`lines 176-195`) **before** `Execute` runs.
   - More critically, `DxvkInstaller.ReapplyAsync(updateBaseline: true)` (`lines 521-559`) executes `File.Copy(targetPath, fullBackupPath, overwrite: true)` and mutates `existingRecord.OriginalState`, `existingRecord.OriginalSha256`, and `existingRecord.BackupRelativePath` (plus `installation.Configuration` at `line 578`) **before** `MultiFileTransactionEngine.Execute` is even called. Because `MultiFileTransactionEngine` undo only captures target files in `gameDir`, a subsequent preparation failure, mid-transaction failure, or post-file `GameLibraryStore.Save` failure leaves the prior canonical restoration backup in `backups/<instId>/` permanently overwritten.
3. **Three-way persistence ambiguity (`Committed` vs. `ProvenNotCommitted` vs. `Unknown`) (`GameLibraryStore.cs:349-410`):**
   When `_gameLibraryStore.Save(installation)` throws an exception, the on-disk state of `game-library.json` is not binary:
   - The failure may have occurred **before replacement** (`ProvenNotCommitted`), where the prior `game-library.json` bytes are positively verified intact on disk and target files/backups must be compensated.
   - The exception may have occurred **after replacement** while `game-library.json` is readable (`Committed`), where the new ownership metadata is already observable on disk and compensating target files would create the inverse split-brain.
   - Or the exception may have occurred after replacement was attempted while `game-library.json` readback is **temporarily unavailable (locked), missing, malformed, or conflicting/newer** (`Unknown`), where neither old nor new ownership can be proven—making both blind target compensation and a blind second `GameLibraryStore.Save` unsafe.
4. **Silent drop of `TransactionOutcome.UnresolvedRecovery` (`DxvkInstaller.cs:366-370`, `705-709`):**
   When `MultiFileTransactionEngine.Execute` returns `TransactionState.AttentionRequired` / `TransactionOutcome.UnresolvedRecovery` on a managed installation, `DxvkInstaller.ApplyToGameAsync` and `DxvkInstaller.ReapplyAsync` log a message and return `false` without setting `LastRefusalReason` or persisting `RestorationState.AttentionRequired` and `LastRefusalReason` to `GameLibraryStore`.

---

## 2. Normal-Phase Sequence, Atomicity Boundaries, and Failure State Table

### 2.1 Normal-Phase Sequence & Atomicity/Durability Distinctions

The bounded in-process repair coordinates five ordered phases across a single deployment/restoration operation:

1. **Phase 1 — Prepare & Entry-Point Snapshot:**
   - Capture the authoritative entry-point snapshot of `GameInstallation` (and whether the installation already existed in `GameLibraryStore._library`) **before** mutating `Executables`, `Configuration`, `ManagedFiles`, or any backup file.
   - Perform all compatibility, conflict, source-file, and `dxvk.conf` staging checks without mutating canonical backups (`backups/<instId>/`) or `game-library.json`.
   - On any Phase 1 refusal or preparation error, restore in-memory `GameInstallation` from the entry-point snapshot (persisting only an explicit compatibility/conflict `LastRefusalReason` when required by existing refusal contracts).
2. **Phase 2 — Verified Undo & Baseline Staging (`MultiFileTransactionEngine`):**
   - Validate all `MultiFileTransactionFile` entries in memory before creating operation artifacts.
   - Write `<TransactionStoreRoot>/.transactions/<txId>/plan.json`.
   - Capture and verify per-operation target undo copies in `.transactions/<txId>/undo/<relPath>`.
   - For **new** native backups (`Install` / `Reapply` on previously unbacked existing file), stage to `.transactions/<txId>/native-backups/<relPath>`, verify identity, and publish via `File.Move(..., overwrite: false)`.
   - For **`Reapply(updateBaseline: true)` replacing an existing canonical backup**, first verify the existing canonical backup at `backups/<instId>/<relPath>` against the pre-reapply `OriginalSha256`, preserve a verified copy in `.transactions/<txId>/prior-backups/<relPath>`, stage and verify the new baseline in `.transactions/<txId>/native-backups/<relPath>`, and only then replace `backups/<instId>/<relPath>`.
3. **Phase 3 — Target Apply & Post-Apply Verification (`MultiFileTransactionEngine`):**
   - Apply each target mutation (`Copy` or `Delete`) in `gameDir` and verify post-operation target identity.
   - Keep `.transactions/<txId>/` (`plan.json`, `undo/`, `prior-backups/`, `native-backups/`) open and intact on disk while invoking the commit-scope callback.
4. **Phase 4 — Metadata Stage, Replace & Three-Way Outcome Classification (`GameLibraryStore`):**
   - Capture authoritative **pre-write evidence** of `game-library.json` (`PreWriteExisted`, `PreWriteBytes`, `PreWriteIdentity`) and serialize **expected post-write bytes** (`ExpectedPostWriteBytes`, `ExpectedPostWriteIdentity`).
   - Stage `ExpectedPostWriteBytes` to `game-library.json.tmp.<guid>`, attempt same-directory replacement (`CustomCommitReplace` or `File.Move(tempPath, _libraryFile, overwrite: true)`), and classify the outcome via readback verification into:
     - `OwnershipPersistenceOutcome.Committed`
     - `OwnershipPersistenceOutcome.ProvenNotCommitted`
     - `OwnershipPersistenceOutcome.Unknown`
5. **Phase 5 — Finalize, Compensate, or Retain Evidence:**
   - **`Committed`:** Delete `.transactions/<txId>/`, return `TransactionState.Committed` / `TransactionOutcome.Success`, and update `ProfileStore` (best-effort in-process sync).
   - **`ProvenNotCommitted`:** Restore in-memory `GameInstallation` from the Phase 1 entry-point snapshot (removing any uncommitted first-install entry from `_library`), compensate all target files from `.transactions/<txId>/undo/`, restore any replaced canonical backups from `.transactions/<txId>/prior-backups/`, delete any newly created canonical backups from this attempt, verify full pre-operation target and backup state, and delete `.transactions/<txId>/` (returning `FailedSafely` / `SafeFailure`, or transitioning to `UnresolvedRecovery` if compensation fails).
   - **`Unknown` or `UnresolvedRecovery`:** Do **not** compensate targets or backups and do **not** delete `.transactions/<txId>/`. Set in-memory `RestorationState.AttentionRequired` and `LastRefusalReason` (persisting to `GameLibraryStore` **only** when `UnresolvedRecovery` occurred with a healthy store, never when the store outcome itself is `Unknown`).

> **Architectural Distinction — Single-File Replacement vs. In-Process Compensation vs. OS Power-Loss Durability:**
> - **Single-file namespace replacement:** `File.Move(tempPath, destPath, overwrite: true)` swaps a single directory entry within the same filesystem volume.
> - **Coordinated in-process compensation:** Target DLLs (`gameDir`), canonical backups (`backups/<instId>/`), and ownership metadata (`game-library.json`) span multiple files. Atomicity across them is achieved **in-process** by holding verified undo/prior-backup snapshots in `.transactions/<txId>/` until `game-library.json` replacement is classified, and compensating when `ProvenNotCommitted`.
> - **OS / physical media durability:** Matching `ExpectedPostWriteBytes` on readback proves the **observable filesystem state** in the OS cache/namespace, not physical disk flush (`FlushFileBuffers`) or cross-file crash atomicity across sudden power loss or `TerminateProcess` (see §6 for explicit restart/abrupt-termination limitations).

### 2.2 State Table Across `Install`, `Update`, `Reapply`, and `Restore`

| Operation & Failure Stage | Target Files (`gameDir`) | Native Backups (`backups/<instId>/`) | Per-Operation Undo (`backups/.transactions/<txId>/`) | `GameLibraryStore` on Disk (`game-library.json`) | `GameLibraryStore` in Memory (`_library`) | `ProfileStore` (`games.json`) | UI / Manager Health (`RestorationState`, `LastRefusalReason`) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`Install` — `ProvenNotCommitted` `GameLibraryStore.Save` failure** | **Current:** DXVK deployed (orphaned)<br>**Required:** Compensated back to pre-install native baseline (existing native restored, originally absent deleted) | **Current:** Published native backups left unreferenced<br>**Required:** Newly created native backups for this attempt rolled back; pre-existing backups untouched | **Current:** Deleted before `Save`<br>**Required:** Retained through `Save`; used to compensate targets/backups, then deleted once verified | **Current:** Initial `GetOrCreateInstallation` written before `Execute`<br>**Required:** Pre-operation state preserved (unchanged bytes if file existed; no stray `Managed` record) | **Current:** Diverges (`Managed`, `vA`, `ManagedFiles` & `Configuration` mutated)<br>**Required:** Restored to exact Phase 1 entry-point snapshot matching disk (`RestorationState.None`, `ManagedFiles = []`) | **Current & Required:** Unchanged (`DxvkEnabled = false`) | **Current:** In-memory `Managed`, disk `RestorationState.None`, `LastRefusalReason = null`<br>**Required:** `RestorationState.None` (when compensated cleanly) + non-empty caller `LastRefusalReason`; `AttentionRequired` if compensation fails |
| **`Update` (A $\to$ B) — `ProvenNotCommitted` `GameLibraryStore.Save` failure** | **Current:** Version B deployed (mismatches disk metadata A; breaks future `Restore`)<br>**Required:** Compensated back to Version A bytes/set | **Current & Required:** True native baseline from Install A preserved byte-for-byte | **Current:** Deleted before `Save`<br>**Required:** Retained through `Save`; used to restore Version A targets, then deleted once verified | **Current & Required:** Version A (`RestorationState.Managed`, `ManagedDxvkVersion = "vA"`, Version A `ExpectedManagedSha256`) | **Current:** Diverges (`ManagedDxvkVersion = "vB"`, Version B hashes)<br>**Required:** Restored to exact Phase 1 Version A snapshot matching disk | **Current & Required:** Remains at Version A (`DxvkEnabled = true`, `DxvkVersion = "vA"`) | **Current:** `Managed` (`vB` in memory, `vA` on disk), `LastRefusalReason = null`<br>**Required:** `Managed` (`vA` in memory and on disk; `Restore` works immediately) + non-empty caller `LastRefusalReason`; `AttentionRequired` if compensation fails |
| **`Reapply(updateBaseline: true)` — `ProvenNotCommitted` `GameLibraryStore.Save` failure** | **Current:** Reapplied DXVK/`dxvk.conf` left on disk<br>**Required:** Compensated back to exact pre-reapply target bytes/existence (e.g., externally patched native v2) | **Current:** Prior canonical backup (`native-v1`) overwritten before `Execute`<br>**Required:** Prior canonical backup (`native-v1`) restored from `.transactions/<txId>/prior-backups/` and verified | **Current:** Deleted before `Save`; never captured prior backup<br>**Required:** Captures both `undo/` (pre-reapply target) and `prior-backups/` (prior canonical backup); cleaned up after verified compensation | **Current & Required:** Pre-reapply durable record (`OriginalSha256 = sha256(native-v1)`) preserved byte-for-byte | **Current:** Diverges (`OriginalSha256 = sha256(native-v2)`, `Configuration` mutated)<br>**Required:** Restored to exact Phase 1 snapshot (`OriginalSha256 = sha256(native-v1)`, prior `Configuration`) | **Current & Required:** Unchanged (`ProfileStore.Save` not called when `ReapplyAsync` returns `false`) | **Current:** In-memory `Managed` with mutated baseline/config, `LastRefusalReason = null`<br>**Required:** Consistent pre-reapply state + non-empty caller `LastRefusalReason`; retry `Reapply` + `Restore` succeeds to `native-v2` |
| **`Restore` — `ProvenNotCommitted` `GameLibraryStore.Save` failure** | **Current:** Restored to native on disk while disk store still says `Managed` (retry `Restore` refuses)<br>**Required:** Compensated back to pre-restore managed DXVK files (`vA`) | **Current & Required:** Native baseline backups preserved in `backups/<instId>/` | **Current:** Deleted before `Save`<br>**Required:** Retained through `Save`; used to restore managed `vA` targets, then deleted once verified | **Current & Required:** Pre-restore `Managed` (`vA`) record preserved byte-for-byte | **Current:** Diverges (`ManagedDxvkVersion = null`, `AttentionRequired`)<br>**Required:** Restored to Phase 1 `Managed` (`vA`) snapshot matching disk when compensation succeeds | **Current & Required:** Remains `DxvkEnabled = true` | **Current:** Split-brain (`AttentionRequired`/`null` in memory; `Managed`/`vA` on disk; retry fails)<br>**Required:** Consistent `Managed` (`vA`) across disk, memory, and target files so retry `Restore` succeeds; `AttentionRequired` if compensation fails |
| **Any Operation — Post-replace exception with readable `ExpectedPostWriteBytes` (`Committed`)** | **Current & Required:** Post-operation target files remain on disk (matching committed `game-library.json`) | **Current & Required:** Committed baseline backups remain intact | **Current:** Already deleted<br>**Required:** Finalized & cleaned up (`prior-backups/` and `undo/` removed) because `ExpectedPostWriteBytes` is verified on disk | **Current & Required:** Post-operation `game-library.json` is verified on disk | **Current & Required:** Matches post-operation `game-library.json` on disk | **Current:** Skipped because installer/rollback returns `false`<br>**Required:** Operation returns `true`; `DxvkManager` updates `ProfileStore` (best-effort) | **Current:** Returns `false` despite committed disk state<br>**Required:** Treated as `Committed` (`returns true`, `LastRefusalReason = null`, no target compensation) |
| **Any Operation — Post-replace exception with unavailable/ambiguous readback (`Unknown`)** | **Current:** Target files left on disk without undo evidence<br>**Required:** Target files left untouched (no blind compensation!) | **Current:** Published backups left without prior-backup undo<br>**Required:** Backups left untouched (no blind rollback!) | **Current:** Deleted before `Save`<br>**Required:** **Retained** at `backups/.transactions/<txId>/` (`plan.json`, `undo/`, `prior-backups/`) | **Current & Required:** Not overwritten by a second blind `Save` (preserves whatever is on disk until lock/issue clears) | **Current:** Remains `Managed` / `Restored` with `LastRefusalReason = null`<br>**Required:** `RestorationState = AttentionRequired` and descriptive `LastRefusalReason` set in memory | **Current & Required:** Unchanged | **Current:** `LastRefusalReason = null`, `Managed` in memory<br>**Required:** Returns `false`; `AttentionRequired` + `LastRefusalReason` in memory and on installer/rollback/manager |
| **Any Operation — Unresolved engine recovery (`UnresolvedRecovery`)** | **Current & Required:** Partially mutated / locked target files remain untouched after failed recovery attempt | **Current & Required:** Pre-existing native backups preserved; `.transactions/<txId>/prior-backups/` retained | **Current & Required:** **Retained** at `backups/.transactions/<txId>/` (`plan.json`, `undo/`, `prior-backups/`) | **Current (`Install`/`Update`/`Reapply`):** Unchanged (`Managed` `vA` or `None`)<br>**Required:** `RestorationState = AttentionRequired` and `LastRefusalReason` persisted when store is writable | **Current (`Install`/`Update`/`Reapply`):** Remains `Managed` / `None`, `LastRefusalReason = null`<br>**Required:** `RestorationState = AttentionRequired` and `LastRefusalReason` set in memory (even if store write also fails) | **Current & Required:** Unchanged | **Current:** `DxvkInstaller` silently leaves `Managed` and `LastRefusalReason = null`<br>**Required:** All operations set `RestorationState.AttentionRequired` and non-empty `LastRefusalReason` |

---

## 3. Evaluation of Design Approaches

### 3.1 Option A (Recommended): Narrow In-Process Deferred-Undo-Commit, Entry-Point Snapshot, Staged Baseline Replacement & Three-Way Persistence Classification

Option A resolves all in-process ownership commit and recovery defects without changing `GameLibrary.CurrentSchemaVersion` (`1`) or any public API signature:

1. **Entry-Point Snapshot Before Any Mutation:**
   - Capture a deep snapshot of `GameInstallation` (and whether the installation already existed in `GameLibraryStore`) at the start of `ApplyToGameAsync`, `ReapplyAsync`, `AdoptExisting`, and `RestoreOriginalDllsAsync` before modifying `Executables`, `Configuration`, `ManagedFiles`, or `GameLibraryStore`.
2. **Staged, Non-Destructive Baseline Replacement in `Reapply(updateBaseline: true)`:**
   - Move baseline-backup replacement out of `DxvkInstaller.ReapplyAsync`'s pre-`Execute` loop and into `MultiFileTransactionEngine`'s Phase 2 preparation so that any existing canonical backup at `backups/<instId>/<relPath>` is first verified against the prior `OriginalSha256` and preserved in `.transactions/<txId>/prior-backups/<relPath>` before the new baseline is staged, verified, and published.
3. **Internal Commit-Scope Callback in `MultiFileTransactionEngine`:**
   - Keep `public SafetyTransactionResult Execute(MultiFileTransactionRequest request)` unchanged and add an `internal` entry point (`ExecuteWithCommitScope`) that retains `.transactions/<txId>/` (`undo/`, `prior-backups/`, `native-backups/`, `plan.json`) while invoking the caller's metadata commit callback.
4. **Three-Way Persistence Outcome (`Committed` / `ProvenNotCommitted` / `Unknown`) in `GameLibraryStore`:**
   - Capture authoritative `PreWriteBytes` (or `PreWriteExisted == false`) before staging, plus `ExpectedPostWriteBytes`.
   - Authorize target and backup compensation **only** on `ProvenNotCommitted` (unambiguous pre-replace failure or positive readback verification that `PreWriteBytes` survived on disk).
   - Recognize `Committed` when positive readback verifies `ExpectedPostWriteBytes` on disk.
   - Classify any unreadable, missing, malformed, or conflicting/newer post-attempt state as `Unknown`: retain `.transactions/<txId>/`, avoid blind compensation or a second `GameLibraryStore.Save`, and report `RestorationState.AttentionRequired` + `LastRefusalReason` in memory.

### 3.2 Option B (Comparison): Additive Durable Intent Record in `game-library.json`

| Criterion | Option A: Bounded In-Process Deferred-Commit + Three-Way Classification (Recommended) | Option B: Additive Intent Record in `game-library.json` |
| :--- | :--- | :--- |
| **Schema / Format Impact** | **None.** `GameLibrary.CurrentSchemaVersion` (`1`) and `game-library.json` format are unchanged. | Adds intent fields to `GameInstallation` / `GameLibrary`, requiring schema compatibility and migration rules. |
| **In-Process Failure Coverage** | **Complete** across `Install`, `Update`, `Reapply(updateBaseline: true)`, `Restore`, `UnresolvedRecovery`, and `Unknown` readback. | Requires the exact same `prior-backups/` staging and three-way readback classification anyway, while doubling `game-library.json` write passes. |
| **Hard Process Termination (Crash / Power Loss) on First Install** | Bounded limitation (see §6): if hard-killed after target copy before first `game-library.json` write, `ManagedFiles` on disk is empty on restart. | Can record pre-install intent in `game-library.json` before target copy, **if** a startup reconciliation pass is also added. |
| **Human Decision Boundary** | Delegated within existing schema and architecture. | Requires a human product/schema decision if crash-journal startup recovery is desired beyond bounded in-process repair. |

**Recommendation:** Adopt **Option A** for the follow-up production repair issue. Document the abrupt-termination / startup boundaries explicitly (§6) so the human coordinator can decide whether a separate startup crash-reconciliation issue is ever needed.

---

## 4. Exact Owner of the Commit Boundary & Proposed Production Changes

The in-process commit boundary is owned jointly by **`MultiFileTransactionEngine.ExecuteWithCommitScope`** (which holds the file-system undo and prior-backup scope open until metadata commit classification finishes) and **`GameLibraryStore.TrySaveWithOutcome`** (which classifies `game-library.json` replacement into `Committed`, `ProvenNotCommitted`, or `Unknown`).

### 4.1 `src/DXVKCompanion/Storage/GameLibraryStore.cs`
- Define internal outcome types (no public API change):
  ```csharp
  internal enum OwnershipPersistenceOutcome
  {
      Committed = 0,
      ProvenNotCommitted = 1,
      Unknown = 2
  }

  internal readonly record struct OwnershipSaveResult(
      OwnershipPersistenceOutcome Outcome,
      Exception? Exception,
      string? DiagnosticMessage);
  ```
- Add `internal OwnershipSaveResult TrySaveWithOutcome(GameInstallation installation)`:
  1. Under `_lock`, before staging:
     - Capture `bool preWriteExisted = File.Exists(_libraryFile);`
     - Capture `byte[]? preWriteBytes = preWriteExisted ? File.ReadAllBytes(_libraryFile) : null;` (if reading `_libraryFile` before staging throws `IOException`, return `ProvenNotCommitted` immediately because neither temp staging nor replacement was attempted).
     - Serialize `json` and capture `byte[] expectedPostWriteBytes = Encoding.UTF8.GetBytes(json);`.
  2. Stage `tempPath`:
     - Track `bool replaceAttempted = false;`.
     - If `CustomTempWriter` or `File.WriteAllText(tempPath, json)` throws before `replaceAttempted` is set: clean up `tempPath`, verify `_libraryFile` still matches `preWriteExisted`/`preWriteBytes` (returning `ProvenNotCommitted` if verified, or `Unknown` if `_libraryFile` was externally mutated/locked concurrently).
  3. Attempt replacement (`replaceAttempted = true`):
     - Invoke `CustomCommitReplace` or `File.Move(tempPath, _libraryFile, overwrite: true)`.
     - Whether replacement returns normally or throws an exception `ex`, perform **positive readback verification** on `_libraryFile`:
       - Try reading `byte[] actualDiskBytes = File.ReadAllBytes(_libraryFile)`:
         - **Matches `expectedPostWriteBytes`:** Clean up `tempPath` if still present and return `OwnershipPersistenceOutcome.Committed` (even if `CustomCommitReplace` threw after moving!).
         - **Matches `preWriteBytes` (when `preWriteExisted == true`):** Clean up `tempPath` and return `OwnershipPersistenceOutcome.ProvenNotCommitted`.
         - **Readable but matches neither `expectedPostWriteBytes` nor `preWriteBytes` (malformed, truncated, or conflicting/newer external write):** Do **not** overwrite `_libraryFile`; clean up `tempPath` if still present and return `OwnershipPersistenceOutcome.Unknown`.
       - If `!File.Exists(_libraryFile)`:
         - If `!preWriteExisted` AND `File.Exists(tempPath)` with bytes equal to `expectedPostWriteBytes`: clean up `tempPath` and return `OwnershipPersistenceOutcome.ProvenNotCommitted`.
         - Otherwise (`preWriteExisted == true` and `_libraryFile` vanished, or both `_libraryFile` and `tempPath` vanished): return `OwnershipPersistenceOutcome.Unknown`.
       - If reading `_libraryFile` throws `IOException` or `UnauthorizedAccessException` (readback temporarily unavailable / locked):
         - Leave `tempPath` cleanup best-effort only if `tempPath` still exists separately from `_libraryFile`, and return `OwnershipPersistenceOutcome.Unknown`.
- Add internal snapshot helpers on `GameLibraryStore`:
  - `internal GameInstallation CreateSnapshot(GameInstallation source)` (deep copy of `GameInstallation`, `DxvkConfiguration`, `ManagementPolicy`, `ExecutableProfile` list, `ManagedFileRecord` list, `PendingAction`, and `LastCancelledAction`).
  - `internal void RestoreSnapshot(GameInstallation target, GameInstallation snapshot, bool existedBeforeOperation)` (copies all properties back into `target` in place, or removes `target` from `_library.Installations` if `!existedBeforeOperation` and `_libraryFile` was not committed).

### 4.2 `src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs` & `src/DXVKCompanion/Safety/TransactionContracts.cs`
- **Preserve the public API:** `public SafetyTransactionResult Execute(MultiFileTransactionRequest request)` remains the only public execution method on `MultiFileTransactionEngine`, delegating to `ExecuteWithCommitScope(request, commitOwnershipCallback: null)`.
- Add an `internal` property on `MultiFileTransactionFile` (in `TransactionContracts.cs`):
  - `internal bool ReplaceExistingBackup { get; init; }`
  - `internal SafetyFileIdentity? ExpectedPriorBackupIdentity { get; init; }`
- Add `internal SafetyTransactionResult ExecuteWithCommitScope(MultiFileTransactionRequest request, Func<OwnershipSaveResult>? commitOwnershipCallback)`:
  - **Phase 2 (`ReplaceExistingBackup == true` support for `Reapply(updateBaseline: true)`):**
    - When `file.OriginalState == OriginalFileState.Existing`, `file.ReplaceExistingBackup == true`, and `File.Exists(backupPath)`:
      1. Verify existing `backupPath` matches `file.ExpectedPriorBackupIdentity` (aborting safely before any mutation if mismatched).
      2. Copy `backupPath` to `.transactions/<txId>/prior-backups/<relPath>` and verify its identity matches `file.ExpectedPriorBackupIdentity`.
      3. Stage `targetPath` (the new native baseline) to `.transactions/<txId>/native-backups/<relPath>` and verify its identity matches `file.ExpectedTargetIdentity`.
      4. Replace `backupPath` via `File.Move(stagedBackupPath, backupPath, overwrite: true)` and record `(backupPath, priorBackupPath, ExpectedPriorBackupIdentity)` in `replacedPriorBackups`.
  - **Phase 3 & 4 (Target Apply + Commit Callback):**
    - After all target files are applied and verified, if `commitOwnershipCallback != null`, invoke `var saveResult = commitOwnershipCallback()`.
    - **`saveResult.Outcome == OwnershipPersistenceOutcome.Committed`:**
      - Proceed to Phase 5 finalization (`state = TransactionState.Committed; outcome = TransactionOutcome.Success;`), where `finally` deletes `.transactions/<txId>/`.
    - **`saveResult.Outcome == OwnershipPersistenceOutcome.ProvenNotCommitted`:**
      - Enter the engine's compensation path (`RecoverAttempted(prepared, prepared.Count - 1)` + `RollbackNewlyPublishedBackups(createdNativeBackups)` + `RestoreReplacedPriorBackups(replacedPriorBackups)` + `VerifyPreOperationState(prepared)`).
      - If both target recovery and backup recovery verify cleanly: delete `.transactions/<txId>/` in `finally` and return `TransactionState.FailedSafely` / `TransactionOutcome.SafeFailure` with `saveResult.DiagnosticMessage`.
      - If either target recovery or backup recovery fails verification: retain `.transactions/<txId>/` in `finally` and return `TransactionState.AttentionRequired` / `TransactionOutcome.UnresolvedRecovery`.
    - **`saveResult.Outcome == OwnershipPersistenceOutcome.Unknown`:**
      - Do **not** invoke `RecoverAttempted`, `RollbackNewlyPublishedBackups`, or `RestoreReplacedPriorBackups`.
      - Retain `.transactions/<txId>/` in `finally` and return `TransactionState.AttentionRequired` / `TransactionOutcome.UnresolvedRecovery` with a message identifying the `Unknown` persistence outcome and `transactionId`.

### 4.3 `src/DXVKCompanion/DXVK/DxvkInstaller.cs`
- **`ApplyToGameAsync` (`lines 92-383`):**
  - Look up `var existingInstallation = _gameLibraryStore.FindInstallationForExecutable(profile.ExePath) ?? _gameLibraryStore.FindByInstallationPath(gameDir);`.
  - Capture `bool existedBefore = existingInstallation != null;` and `var snapshot = existingInstallation != null ? _gameLibraryStore.CreateSnapshot(existingInstallation) : null;` **before** mutating `Executables`, `Configuration`, or calling `GetOrCreateInstallation`.
  - If `existingInstallation == null`, construct the `GameInstallation` in memory (or register in `_library` without writing `_libraryFile` until the commit callback) so preflight/source-file/transaction failures never leave an unbacked installation on disk or mutated state in memory.
  - Remove pre-commit mutations from the pre-`Execute` body or revert them via `RestoreSnapshot` on any preflight refusal, `SafeAbort`, or `ProvenNotCommitted` failure.
  - Invoke `_transactionEngine.ExecuteWithCommitScope(request, () => { ... })`:
    - Inside the callback, apply the `Managed` / `ManagedDxvkVersion` / `ManagedFiles` / `Configuration` / `Executables` updates to `installation` and call `var saveResult = _gameLibraryStore.TrySaveWithOutcome(installation);`.
    - If `saveResult.Outcome == OwnershipPersistenceOutcome.ProvenNotCommitted`, call `_gameLibraryStore.RestoreSnapshot(installation, snapshot, existedBefore)` before returning `saveResult` to the engine.
    - Track `lastSaveOutcome = saveResult.Outcome`.
  - On non-success `SafetyTransactionResult`:
    - Populate `LastRefusalReason = result.Message ?? "Deployment transaction failed."`.
    - If `result.State == TransactionState.AttentionRequired || result.Outcome == TransactionOutcome.UnresolvedRecovery`:
      - Set `installation.RestorationState = RestorationState.AttentionRequired;` and `installation.LastRefusalReason = LastRefusalReason;` in memory.
      - **Only if `lastSaveOutcome != OwnershipPersistenceOutcome.Unknown`** (i.e., the failure was an engine file-recovery failure with a healthy store, or a `ProvenNotCommitted` save failure whose target compensation failed), attempt a best-effort `_gameLibraryStore.TrySaveWithOutcome(installation)` when `lastSaveOutcome == null` (store did not fail). When `lastSaveOutcome == OwnershipPersistenceOutcome.Unknown`, **never** issue another `Save` that could clobber uncertain on-disk library evidence.
- **`ReapplyAsync` (`lines 385-723`):**
  - Capture `var snapshot = _gameLibraryStore.CreateSnapshot(installation)` at the top of `ReapplyAsync` before any `Configuration` or `ManagedFiles` mutation.
  - In the `updateBaseline && File.Exists(targetPath)` branch (`lines 521-559`):
    - **Do not** call `File.Copy(targetPath, fullBackupPath, overwrite: true)` and **do not** mutate `existingRecord.OriginalState`, `existingRecord.OriginalSha256`, or `existingRecord.BackupRelativePath` before `ExecuteWithCommitScope`.
    - Instead, set `ReplaceExistingBackup = true` and `ExpectedPriorBackupIdentity = existingBackupIdentity` on the `MultiFileTransactionFile` item, and apply the updated `OriginalSha256` / `OriginalState` / `BackupRelativePath` to `ManagedFileRecord` only inside the `ExecuteWithCommitScope` commit callback after Phase 2 & Phase 3 have succeeded!
  - Handle `Committed`, `ProvenNotCommitted` (restoring `snapshot` and compensating both target files and `prior-backups/`), `Unknown`, and `UnresolvedRecovery` identically to `ApplyToGameAsync`.

### 4.4 `src/DXVKCompanion/DXVK/DxvkRollback.cs`
- **`RestoreOriginalDllsAsync` (`lines 35-224`):**
  - Capture `var snapshot = _gameLibraryStore.CreateSnapshot(installation)` before `ExecuteWithCommitScope`.
  - Inside the `ExecuteWithCommitScope` callback, mutate `installation` (`ManagedDxvkVersion = null`, `RestorationState = RestorationState.Restored`, `ManagedFiles[*].ManagedDxvkVersion = null`) and call `_gameLibraryStore.TrySaveWithOutcome(installation)`.
  - If `Outcome == OwnershipPersistenceOutcome.ProvenNotCommitted`: restore `installation` from `snapshot` (`RestorationState.Managed`, `ManagedDxvkVersion = "vA"`) and let `MultiFileTransactionEngine` compensate the target files back to the managed DXVK DLLs from `.transactions/<txId>/undo/`.
  - If compensation succeeds (`SafeFailure`), `installation` remains `RestorationState.Managed` in memory and on disk with the managed DXVK DLLs intact on disk (so a subsequent retry of `RestoreOriginalDllsAsync` succeeds!), while `LastRefusalReason` is populated and `RestoreOriginalDllsAsync` returns `false`.
  - If `Outcome == OwnershipPersistenceOutcome.Unknown` or target compensation fails (`UnresolvedRecovery`), set `installation.RestorationState = RestorationState.AttentionRequired` and `installation.LastRefusalReason = LastRefusalReason` in memory (and persist to `GameLibraryStore` only when the store itself did not return `Unknown`/`ProvenNotCommitted`).

### 4.5 `src/DXVKCompanion/DXVK/DxvkManager.cs` & `src/DXVKCompanion/Storage/ProfileStore.cs`
- **Propagate refusal/failure reasons:** Ensure `EnableDxvkAsync`, `DisableDxvkAsync`, `ReapplyAsync`, and `AdoptExistingAsync` always copy `_installer.LastRefusalReason` / `_rollback.LastRefusalReason` into `DxvkManager.LastRefusalReason` when an operation returns `false`.
- **Post-commit `ProfileStore.Save` handling (`DxvkManager.cs:775-780`, `807-811`, `848-852`, `881-886`):**
  - Once `ApplyToGameAsync` / `RestoreOriginalDllsAsync` / `ReapplyAsync` / `AdoptExisting` returns `true` (meaning target files, backups, and `GameLibraryStore` have all committed), update `profile.DxvkEnabled` and `profile.DxvkVersion` in memory first, then call `_profiles.Save(profile)` inside a `try/catch (Exception ex)`.
  - If `_profiles.Save(profile)` throws:
    - Do **not** roll back `GameLibraryStore` or target files (`GameLibraryStore` is the authoritative ownership store per `docs/spec/DXVK-COMPANION-SPEC.md` §13).
    - Keep `profile.DxvkEnabled` and `profile.DxvkVersion` updated in memory for the current process session.
    - Log a clear warning via `Logger.Log($"DxvkManager: ownership committed in GameLibraryStore, but failed to persist profile cache for {profile.ExeName}: {ex.Message}")`.
- **Concrete stale-profile reconciliation in `DxvkManager.ReassessEffectiveApi` (`DxvkManager.cs:96-204`):**
  - Currently, `ReassessEffectiveApi` only synchronizes `Architecture` and `Api` between `GameProfile` and `ExecutableProfile`.
  - Extend `ReassessEffectiveApi(GameInstallation? installation, GameProfile profile)` so that when `installation != null`:
    - If `installation.RestorationState == RestorationState.Managed`: ensure `profile.DxvkEnabled == true` and, if `!string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion)`, `profile.DxvkVersion == installation.ManagedDxvkVersion` (setting `profileChanged = true` if either differed).
    - Else if `installation.RestorationState == RestorationState.Restored`: ensure `profile.DxvkEnabled == false` (setting `profileChanged = true` if `profile.DxvkEnabled` was `true`).
  - When `profileChanged == true`, wrap `_profiles.Save(profile)` in a `try/catch` so a transient `games.json` write error never blocks compatibility checks or operation execution.

---

## 5. Exact Rules for Each Failure & Ambiguity Case

1. **`ProvenNotCommitted` (`GameLibraryStore.TrySaveWithOutcome`):**
   - Requires positive evidence that `_libraryFile` on disk was not replaced: either failure occurred before `replaceAttempted` (`CustomTempWriter` / staging throw) with `_libraryFile` verified equal to `PreWriteBytes` (or still absent when `!PreWriteExisted`), OR after a failed replace attempt, readback proves `_libraryFile` equals `PreWriteBytes` (when `PreWriteExisted`) or `!File.Exists(_libraryFile)` with `File.Exists(tempPath)` matching `ExpectedPostWriteBytes` (when `!PreWriteExisted`).
   - Action: Restore in-memory `GameInstallation` from the Phase 1 entry-point snapshot; compensate all target files from `.transactions/<txId>/undo/`; restore any replaced canonical backups from `.transactions/<txId>/prior-backups/`; delete any newly created canonical backups; verify pre-operation target and backup identities; clean up `.transactions/<txId>/`; set caller `LastRefusalReason`; return `false`.
2. **`Committed` (including post-replace exception with readable `ExpectedPostWriteBytes`):**
   - Requires positive readback verification that `_libraryFile` on disk equals `ExpectedPostWriteBytes`.
   - Action: Treat ownership persistence as committed (`return true` from `ApplyToGameAsync` / `ReapplyAsync` / `RestoreOriginalDllsAsync`), finalize and delete `.transactions/<txId>/`, and do **not** compensate target files or backups.
3. **`Unknown` (unreadable/locked, missing, malformed, or conflicting/newer `_libraryFile` after `replaceAttempted`):**
   - Action:
     - **Never** compensate target files or revert published backups (since the new ownership state may already be on disk behind a transient lock).
     - **Retain** `.transactions/<txId>/` (`plan.json`, `undo/`, `prior-backups/`, `native-backups/`).
     - **Never** issue another `_gameLibraryStore.Save(installation)` in the failure handler (which could overwrite concurrent/newer data or clobber evidence).
     - Set in-memory `installation.RestorationState = RestorationState.AttentionRequired` and `installation.LastRefusalReason` (recording the transaction ID and readback error), set `LastRefusalReason` on `DxvkInstaller`/`DxvkRollback`/`DxvkManager`, and return `false`.
   - **Handling of missing, unreadable, malformed, conflicting, and newer evidence on later inspection/retry:**
     - **Temporarily unreadable (locked):** In-memory state stays `AttentionRequired` during the current call. Once the external lock clears, a fresh `GameLibraryStore` load or inspection reads the actual disk bytes.
     - **Malformed / truncated JSON:** `GameLibraryStore.Load()` isolates the unreadable file to `game-library.json.recovery.<timestamp>.json` and preserves it without deletion (`GameLibraryStore.cs:294-334`).
     - **Newer schema version:** `StartupCompatibility.Evaluate()` / `GameLibrarySchemaCompatibility.AssessFile()` refuses startup/modification to protect newer library data (`StartupCompatibility.cs:43-57`).
     - **Conflicting external modification:** Because `Unknown` sets `AttentionRequired` in memory and retains `.transactions/<txId>/`, subsequent operations on the same in-process instance see `AttentionRequired` / `LastRefusalReason`; on reload, `ManagedFileInspector` compares the on-disk `ManagedFiles` against `gameDir` targets and marks any hash divergence as `ManagedFileState.ExternallyChanged` / `RestorationState.AttentionRequired`.
4. **Compensation failure during `ProvenNotCommitted` rollback:**
   - If restoring a target file from `undo/` or restoring a prior canonical backup from `prior-backups/` fails during `ProvenNotCommitted` compensation, `MultiFileTransactionEngine` retains `.transactions/<txId>/` and returns `TransactionState.AttentionRequired` / `TransactionOutcome.UnresolvedRecovery`.
   - Because `game-library.json` on disk was proven not committed (and may still be unwritable), `DxvkInstaller` / `DxvkRollback` sets `installation.RestorationState = RestorationState.AttentionRequired` and `installation.LastRefusalReason` in memory and returns `false` without throwing.
5. **Unresolved engine recovery (`TransactionOutcome.UnresolvedRecovery`) when `GameLibraryStore` is writable vs. failing:**
   - When `MultiFileTransactionEngine` fails mid-operation target recovery before metadata commit is even attempted, `DxvkInstaller` / `DxvkRollback` sets `installation.RestorationState = RestorationState.AttentionRequired` and `installation.LastRefusalReason = result.Message` in memory, sets `LastRefusalReason` on the installer/rollback/manager, and calls `_gameLibraryStore.TrySaveWithOutcome(installation)`.
   - When the store is writable, `AttentionRequired` and `LastRefusalReason` are durably persisted to `game-library.json`. When the store is also failing, the in-memory installation and manager still expose `AttentionRequired` and `LastRefusalReason`.
6. **`ProfileStore.Save` failure after library commit:**
   - Handled as a non-fatal cache write warning in `DxvkManager` (§4.5): `GameLibraryStore` and target files remain committed, in-memory `GameProfile` remains updated, and `DxvkManager.ReassessEffectiveApi` reconciles `DxvkEnabled`/`DxvkVersion` from `GameInstallation` on subsequent manager operations.

---

## 6. Bounded In-Process Scope vs. Explicit Restart & Abrupt-Termination Limitations

This design is an **in-process commit and compensation repair**. It does **not** claim OS-level cross-file power-loss atomicity, and it does **not** treat `.transactions/<txId>/plan.json` as a full ownership write-ahead journal. The exact restart and abrupt-termination behaviors of the current codebase are:

1. **Abrupt process termination (`Kill` / power loss) during Update, Reapply, or Restore on an already-managed installation:**
   - Because `game-library.json` on disk already contains `ManagedFiles` from the prior committed state, `ManagedFileInspector.InspectAll()` (`src/DXVKCompanion/Storage/ManagedFileInspector.cs:19-247`) on next startup compares on-disk target files against the recorded `ExpectedManagedSha256` (or `OriginalSha256`) and transitions the installation to `RestorationState.AttentionRequired` (`ManagedFileState.ExternallyChanged`) whenever target files were partially or fully mutated before `game-library.json` was replaced.
   - However, if an abrupt termination occurred during `Reapply(updateBaseline: true)` *after* `backups/<instId>/<relPath>` was replaced with the new baseline but *before* `game-library.json` was replaced, `game-library.json` still holds the old `OriginalSha256` while `backups/<instId>/<relPath>` holds the new baseline (with the old baseline preserved in `.transactions/<txId>/prior-backups/<relPath>`). On a later `Reapply`, `DxvkInstaller.cs:490-494` detects that `backups/<instId>/<relPath>` does not match `existingRecord.OriginalSha256` and refuses safely.
2. **Abrupt process termination during First Install (after target copy, before `game-library.json` replacement):**
   - **Limitation in `ManagedFileInspector` (`ManagedFileInspector.cs:29, 33, 170`):** `ManagedFileInspector.InspectInstallation` only inspects `installation.ManagedFiles`. If the process terminated before the first-install `ManagedFiles` list was written to `game-library.json`, `ManagedFiles` is empty (`Count == 0`), so `ManagedFileInspector` does **not** detect the orphaned DXVK DLLs in `gameDir` or reconstruct baseline state from `.transactions/<txId>/plan.json`.
   - **Limitation in `ExistingDxvkDetector` (`ExistingDxvkDetector.cs:21-110`) and `AdoptExisting` (`DxvkInstaller.cs:725-823`):** `ExistingDxvkDetector.AssessDirectory` only inspects DLLs in `gameDir` against PE metadata and `_dxvkSourceDir`; it does **not** inspect `backups/<instId>/` or `.transactions/<txId>/`, and returns `ExistingDxvkStatus.OfficialRelease` (`CanBeAdopted = true`). If a user subsequently clicks "Adopt Existing" after a hard-killed first install that had overwritten an existing native DLL, `DxvkInstaller.AdoptExisting` (`line 788`) records `OriginalState = FileOriginalState.Missing` and `BackupRelativePath = null`, ignoring any unreferenced backup left in `backups/<instId>/` or `.transactions/<txId>/`.
3. **Same-session `ManagedFileInspector` guard vs. cross-restart inspection (`ManagedFileInspector.cs`):**
   - Within the same process session, once `GameLibraryStore.HasUncertainPersistence` is latched by an `OwnershipPersistenceOutcome.Unknown` result, `ManagedFileInspector.InspectInstallation` and `InspectAll` return early without mutating in-memory `RestorationState.AttentionRequired` / `LastRefusalReason` or issuing any `GameLibraryStore.Save`.
   - Across a fresh process restart (where `HasUncertainPersistence` starts `false`), `ManagedFileInspector.InspectInstallation` evaluates the durable `game-library.json` against target files on disk.
4. **Stale `ProfileStore` (`games.json`) rows in `ManageGamesWindow.RefreshList` (`ManageGamesWindow.cs:144-161`):**
   - Extending `DxvkManager.ReassessEffectiveApi` (§4.5) and `ProfileStore.HasPendingWriteRetry` repairs stale `profile.DxvkEnabled` and `profile.DxvkVersion` across all `DxvkManager` entry points (`UpdateAvailable`, `EnableDxvkAsync`, `ReapplyAsync`, `AdoptExistingAsync`, `ProcessAllPendingActionsAsync`).
   - However, `ManageGamesWindow.RefreshList()` (`ManageGamesWindow.cs:144-161`) currently hydrates only profiles whose `ExePath` is absent from `_profiles.GetAll()`, and does not call `ReassessEffectiveApi` on existing profiles before building the list items (`lines 163-239`). Unless `ManageGamesWindow.cs` is included in a future UI scope, an existing stale `games.json` entry would display stale `DXVK: Enabled/Disabled` in `ManageGamesWindow` until a `DxvkManager` method runs on that profile.
5. **Explicit Decision Boundary for Follow-Up Scope:**
   - The bounded in-process repair (§4 & §7) does **not** silently introduce startup crash-journal reconciliation or new `ExistingDxvkDetector` policy. If the human coordinator wants startup reconciliation of `.transactions/<txId>/` after hard process kills or `ManageGamesWindow.RefreshList` profile synchronization on existing rows, that can be explicitly authorized as a separate scope addition.

---

## 7. Allowed Paths for the Issue #171 Production Repair

- `src/DXVKCompanion/Safety/TransactionContracts.cs`
- `src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs`
- `src/DXVKCompanion/Storage/GameLibraryStore.cs`
- `src/DXVKCompanion/Storage/ManagedFileInspector.cs`
- `src/DXVKCompanion/Storage/ProfileStore.cs`
- `src/DXVKCompanion/DXVK/DxvkInstaller.cs`
- `src/DXVKCompanion/DXVK/DxvkRollback.cs`
- `src/DXVKCompanion/DXVK/DxvkManager.cs`
- `tests/DXVKCompanion.PhaseA.Tests/OwnershipPersistenceFailureTests.cs`
- `docs/design/OWNERSHIP-COMMIT-RECOVERY.md`
- `docs/ai-journal/**`

No changes to `src/DXVKCompanion/Models/**`, `DXVKCompanion.csproj`, CI workflows, public API signatures, or schema version are required.

---

## 8. Mapping from Regression Tests to Acceptance Criteria (`#169` & `#171`)

| Test Method in `OwnershipPersistenceFailureTests.cs` | Issue #169 / #171 Acceptance Criterion | Role on Unchanged `main` (`#169`) | Role in Production Fix (`#171`) |
| :--- | :--- | :--- | :--- |
| `FirstInstall_WhenPostFileOwnershipSaveFailsBeforeReplace_CompensatesTargetFilesAndNativeBackups_AndKeepsStoreConsistent` | First install post-file `ProvenNotCommitted` save failure, including native backup & `.transactions` cleanup and `Configuration` snapshot | **Expected-red regression oracle** | **Green:** target files compensated to native baseline, newly published native backup and `.transactions` cleaned up, and in-memory store matches disk (`RestorationState.None`). |
| `UpdateAfterInstallA_WhenPostFileOwnershipSaveFailsBeforeReplace_RecoversVersionAFilesAndPreservesRestoreCapability` | Update B post-file `ProvenNotCommitted` save failure after real Install A | **Expected-red regression oracle** | **Green:** Version A DLLs recovered on disk, matching Version A metadata, and subsequent `RestoreOriginalDllsAsync` restores true native baseline. |
| `ReapplyUpdateBaseline_WhenPostFileOwnershipSaveFailsBeforeReplace_RestoresPriorBackupAndPreReapplyTargets_ThenRetryAndRestoreSucceed` | `Install A` $\to$ external native update $\to$ `Reapply(updateBaseline: true)` $\to$ post-file `ProvenNotCommitted` save failure $\to$ retry & restore | **Expected-red regression oracle** | **Green:** prior canonical backup (`native-v1`), pre-Reapply targets (`native-v2`), and in-memory records are restored on failed `Reapply`; subsequent retry `Reapply` + `Restore` succeeds and restores `native-v2`. |
| `UnresolvedEngineRecovery_OnManagedInstallationWithWritableStore_PersistsAttentionRequiredAndLastRefusalReason` | Unresolved engine recovery on a managed installation with a writable store | **Expected-red regression oracle** | **Green:** `LastRefusalReason` populated on installer/manager and `RestorationState.AttentionRequired` persisted in memory and on disk while preserving Version A ownership metadata. |
| `Restore_WhenPostFileOwnershipSaveFailsBeforeReplace_AvoidsUnrecoverableSplitBrainBetweenTargetFilesAndStore` | Restore post-file `ProvenNotCommitted` save failure before replacement | **Expected-red regression oracle** | **Green:** target files compensated back to managed `"2.4"` state matching disk store, and retry `Restore` on reconstructed services succeeds. |
| `PostReplaceException_WhenReadbackIsTemporarilyUnavailable_ClassifiesOutcomeAsUnknown_RetainsEvidenceAndAvoidsBlindCompensation` | Post-replace exception with temporarily unavailable/locked readback $\Rightarrow$ `OwnershipPersistenceOutcome.Unknown` | **Expected-red regression oracle** | **Green:** classifies outcome as `Unknown`, latches `HasUncertainPersistence`, avoids blind target compensation or second `Save`, retains `.transactions/<txId>` (`plan.json` + `undo/d3d11.dll`), and sets `AttentionRequired` + `LastRefusalReason` in memory. |
| `Characterization_PostReplaceException_WhenCommitReplaceMovesFileThenThrows_MetadataIsAlreadyDurableOnDisk` | Readable post-replace exception characterization vs. post-fix `Committed` behavior | **Labeled characterization test (`Green` on `main` with `Assert.False(installOk)`)** | **Green (`Assert.True(installOk)`):** positive readback verification of `ExpectedPostWriteBytes` classifies outcome as `Committed`, cleans up `.transactions/<txId>`, and returns `true`. |
| `ReapplyUpdateBaseline_WhenEarlyPreTargetFailureOccursAfterBackupPublish_RestoresPriorBackupAndLeavesTargetsUnmodified` | `#171` Early/pre-target failure during `Reapply(updateBaseline: true)` after backup publish (`AfterPublishNativeBackup` / file-1 staging) | Added in `#171` | **Green:** restores prior canonical backup from `prior-backups/`, leaves targets unmodified, and cleans up `.transactions/<txId>`. |
| `UnresolvedRecovery_WhenNoPriorLibraryFileExistedOrBaselineChanged_PreservesInMemoryHealthWithoutWritingIncompleteLibraryFile` | `#171` Compensation failure after `ProvenNotCommitted` when no prior `game-library.json` existed | Added in `#171` | **Green:** keeps `AttentionRequired` and `LastRefusalReason` in memory while restoring uncommitted ownership fields and refusing to create an incomplete `game-library.json`. |
| `PostReplaceException_ReadbackClassification_DistinguishesMalformedConflictingNewerSchemaAndPositivelyMissingFiles` | `#171` Positive absence (`ProvenNotCommitted`) vs. malformed, conflicting, and newer-schema readback (`Unknown`) | Added in `#171` | **Green:** verifies positive absence when `PreWriteExisted == false` compensates safely (`ProvenNotCommitted`), while malformed JSON, conflicting writes, and newer `SchemaVersion` latch `Unknown`. |
| `UnknownPersistenceOutcome_SessionGuard_BlocksSubsequentStoreSavesInspectorOverwritesAndMutationsInSameSession` | `#171` Session-scoped `HasUncertainPersistence` guard across `GameLibraryStore`, `ManagedFileInspector`, and `DxvkManager` | Added in `#171` | **Green:** blocks direct `Save`/`SaveAll`/`GetOrCreateInstallation`/`RecordDetectionSnapshot`, prevents `ManagedFileInspector` from overwriting disk or clearing `AttentionRequired`, and refuses subsequent mutating operations. |
| `SecondaryProfileStoreSaveFailure_AfterCommittedEnableOrDisable_DoesNotFailOperation_AndReconcilesOnSubsequentManagerOperation` | `#171` Non-fatal secondary `ProfileStore.Save` failure after committed `EnableDxvkAsync` / `DisableDxvkAsync` and subsequent reconciliation | Added in `#171` | **Green:** `EnableDxvkAsync` and `DisableDxvkAsync` succeed despite `ProfileStore.Save` failure, and subsequent `DxvkManager` operation reconciles `profiles.json` on disk. |
