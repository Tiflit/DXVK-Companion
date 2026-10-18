# Ownership Commit & Recovery Boundary Design (`#169`)

- **Task Contract:** [Issue #169](https://github.com/Tiflit/DXVK-Companion/issues/169)
- **Baseline `main` Commit:** `fb598add34d3e4eb936c714dc7be6e5214e6318a` (includes [PR #168](https://github.com/Tiflit/DXVK-Companion/pull/168) / [Issue #166](https://github.com/Tiflit/DXVK-Companion/issues/166))
- **Executable Reproducer Suite:** `tests/DXVKCompanion.PhaseA.Tests/OwnershipPersistenceFailureTests.cs`
- **Canonical Spec Authority:** `docs/spec/DXVK-COMPANION-SPEC.md` (§§12, 13, 18, 22, 23, 27) and `docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md`

---

## 1. Problem Statement & Observed Failure Boundary on `main`

After [PR #168](https://github.com/Tiflit/DXVK-Companion/pull/168) (`#166`), `MultiFileTransactionEngine.Execute` (`src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs`) guarantees that **within** `Execute(request)`:
- pre-operation target snapshots are captured in an operation-isolated namespace (`<TransactionStoreRoot>/.transactions/<txId>/undo/<relPath>`),
- newly created native baseline backups are staged in `<TransactionStoreRoot>/.transactions/<txId>/native-backups/<relPath>` and published only after verification,
- failed `Install`, `Update`, `Reapply`, and `Restore` file mutations recover exact pre-operation target bytes/existence, and
- unresolved file recovery returns `TransactionState.AttentionRequired` / `TransactionOutcome.UnresolvedRecovery` while retaining `.transactions/<txId>/` evidence.

However, a **two-phase commit gap** remains between `MultiFileTransactionEngine.Execute` and durable ownership persistence in `GameLibraryStore`:

1. **Premature disposal of per-operation undo evidence (`MultiFileTransactionEngine.cs:195-210`):**
   As soon as `Execute(request)` finishes applying target files (`state = TransactionState.Committed; outcome = TransactionOutcome.Success;`), its `finally` block deletes `<TransactionStoreRoot>/.transactions/<txId>/` **before** the caller (`DxvkInstaller` or `DxvkRollback`) mutates `GameInstallation` and persists `game-library.json` via `_gameLibraryStore.Save(installation)`.
2. **In-memory mutation before durable commit & uncompensated post-file save failures (`DxvkInstaller.cs:331-360`, `670-699`; `DxvkRollback.cs:171-183`):**
   `DxvkInstaller.ApplyToGameAsync`, `DxvkInstaller.ReapplyAsync`, and `DxvkRollback.RestoreOriginalDllsAsync` mutate the live in-memory `GameInstallation` object inside `GameLibraryStore._library` and then call `_gameLibraryStore.Save(installation)`. If `WriteAllLocked()` throws before replacing `game-library.json`:
   - Target files on disk have already been replaced with the post-operation payload, and the per-operation undo directory has already been deleted.
   - `game-library.json` on disk still contains the pre-operation metadata (`RestorationState.None` on first install, Version A hashes on Update B, or `Managed` Version A hashes on Restore).
   - The in-memory `GameInstallation` in `GameLibraryStore._library` retains its mutated post-operation properties (`Managed` / Version B / `Restored`), diverging from `game-library.json` on disk.
3. **Silent drop of `TransactionOutcome.UnresolvedRecovery` (`DxvkInstaller.cs:366-370`, `705-709`):**
   When `MultiFileTransactionEngine.Execute` returns `TransactionState.AttentionRequired` / `TransactionOutcome.UnresolvedRecovery` on a managed installation, `DxvkInstaller.ApplyToGameAsync` and `DxvkInstaller.ReapplyAsync` log a message and return `false` without setting `LastRefusalReason` or persisting `RestorationState.AttentionRequired` and `LastRefusalReason` to `GameLibraryStore`.
4. **Pre-replacement vs. post-replacement ambiguity (`GameLibraryStore.cs:349-410`):**
   If an exception is thrown *after* `File.Move(tempPath, _libraryFile, overwrite: true)` succeeds (for example, in a post-replace hook or cleanup path), the updated ownership metadata is **already durable on disk**. Treating every exception from `Save` as a pre-replacement failure and compensating target files would roll target files back while `game-library.json` on disk remains at the new state—creating the inverse split-brain.

---

## 2. State Table Across `Install`, `Update`, `Reapply`, and `Restore`

The table below defines the current (`main` at `fb598add`) vs. required post-repair state across all seven state dimensions for each post-file failure mode.

| Operation & Failure Stage | Target Files (`gameDir`) | Native Backups (`backups/<instId>/`) | Per-Operation Undo (`backups/.transactions/<txId>/`) | `GameLibraryStore` on Disk (`game-library.json`) | `GameLibraryStore` in Memory (`_library`) | `ProfileStore` (`games.json`) | UI / Manager Health (`RestorationState`, `LastRefusalReason`) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`Install` — Pre-replace `GameLibraryStore.Save` failure** | **Current:** DXVK deployed (orphaned)<br>**Required:** Compensated back to pre-install native baseline (existing native restored, originally absent deleted) | **Current:** Published native backups left unreferenced<br>**Required:** Newly created native backups for this attempt rolled back; pre-existing backups untouched | **Current:** Deleted before `Save`<br>**Required:** Retained until `Save` commits; consumed to compensate targets, then deleted if compensation succeeds | **Current & Required:** Pre-install baseline (`RestorationState.None`, `ManagedDxvkVersion = null`, `ManagedFiles = []`) | **Current:** Diverges (`Managed`, `vA`, `ManagedFiles` populated)<br>**Required:** Rolled back to match disk (`RestorationState.None`, `ManagedDxvkVersion = null`, `ManagedFiles = []`) | **Current & Required:** Unchanged (`DxvkEnabled = false`) because `DxvkManager` only saves profile after installer returns `true` | **Current:** In-memory `Managed`, disk `RestorationState.None`, `LastRefusalReason = null`<br>**Required:** `RestorationState.None` (if compensated cleanly) with non-empty `installer.LastRefusalReason` / `manager.LastRefusalReason`; `AttentionRequired` if compensation fails |
| **`Update` (A $\to$ B) — Pre-replace `GameLibraryStore.Save` failure** | **Current:** Version B deployed (mismatches disk metadata A; breaks future `Restore`)<br>**Required:** Compensated back to Version A bytes/set | **Current & Required:** True native baseline from Install A preserved byte-for-byte | **Current:** Deleted before `Save`<br>**Required:** Retained until `Save` commits; consumed to restore Version A targets, then deleted if compensation succeeds | **Current & Required:** Version A (`RestorationState.Managed`, `ManagedDxvkVersion = "vA"`, Version A `ExpectedManagedSha256`) | **Current:** Diverges (`ManagedDxvkVersion = "vB"`, Version B hashes)<br>**Required:** Restored to exact pre-operation Version A snapshot matching disk | **Current & Required:** Remains at Version A (`DxvkEnabled = true`, `DxvkVersion = "vA"`) | **Current:** `Managed` (`vB` in memory, `vA` on disk), `LastRefusalReason = null`<br>**Required:** `Managed` (`vA` in memory and on disk; `Restore` works immediately) + non-empty caller `LastRefusalReason`; `AttentionRequired` if compensation fails |
| **`Reapply` — Pre-replace `GameLibraryStore.Save` failure** | **Current:** Reapplied DXVK/`dxvk.conf` left on disk<br>**Required:** Compensated back to exact pre-reapply target bytes/existence | **Current:** Any newly published baseline backup left on disk<br>**Required:** Newly created backups from this attempt rolled back; prior valid backups preserved | **Current:** Deleted before `Save`<br>**Required:** Retained until `Save` commits; consumed to compensate targets, then deleted if compensation succeeds | **Current & Required:** Pre-reapply durable record preserved byte-for-byte | **Current:** Diverges (`Managed`, updated `ExpectedManagedSha256`)<br>**Required:** Restored to exact pre-operation `GameInstallation` snapshot matching disk | **Current & Required:** Unchanged (`ProfileStore.Save` not called when `ReapplyAsync` returns `false`) | **Current:** In-memory `Managed`, `LastRefusalReason = null`<br>**Required:** Matches durable pre-reapply state + non-empty caller `LastRefusalReason`; `AttentionRequired` if compensation fails |
| **`Restore` — Pre-replace `GameLibraryStore.Save` failure** | **Current:** Restored to native on disk while disk store still says `Managed` (subsequent `Restore` refuses with hash mismatch)<br>**Required:** Compensated back to pre-restore managed DXVK files (`vA`) | **Current & Required:** Native baseline backups preserved in `backups/<instId>/` | **Current:** Deleted before `Save`<br>**Required:** Retained until `Save` commits; consumed to restore managed `vA` targets, then deleted if compensation succeeds | **Current & Required:** Remains at pre-restore `Managed` (`vA`) record (or `AttentionRequired` if compensation fails and store becomes writable) | **Current:** Diverges (`ManagedDxvkVersion = null`, `AttentionRequired`)<br>**Required:** Restored to pre-restore `Managed` (`vA`) snapshot matching disk if compensated cleanly | **Current & Required:** Remains `DxvkEnabled = true` (`DxvkManager.DisableDxvkAsync` does not save profile on `false`) | **Current:** Split-brain (`AttentionRequired` / `null` version in memory; `Managed` / `vA` on disk; retry fails)<br>**Required:** Consistent `Managed` (`vA`) across disk, memory, and target files so retry `Restore` succeeds; `AttentionRequired` if compensation fails |
| **`Install` / `Update` / `Reapply` / `Restore` — Post-replace exception** | **Current & Required:** Post-operation target files remain on disk (matching committed `game-library.json`) | **Current & Required:** Committed baseline backups remain intact | **Current:** Already deleted<br>**Required:** Committed & cleaned up because both target files and `game-library.json` replacement succeeded | **Current & Required:** Post-operation `game-library.json` is already durable on disk | **Current & Required:** Matches durable post-operation `game-library.json` | **Current:** Skipped if installer/rollback returns `false`<br>**Required:** Treated as committed ownership state; `DxvkManager` synchronizes `ProfileStore` (best-effort) | **Current:** Operation returns `false` despite durable commit<br>**Required:** Recognized as committed once `WriteAllLocked` replacement succeeds (or verified on disk); no target compensation is run |
| **`Install` / `Update` / `Reapply` / `Restore` — Unresolved engine recovery (`UnresolvedRecovery`)** | **Current & Required:** Partially mutated / locked target files remain untouched after failed recovery attempt | **Current & Required:** Pre-existing native backups preserved; unreferenced new backups from failed attempt not left at canonical paths unless needed | **Current & Required:** Retained at `backups/.transactions/<txId>/` (`plan.json` + `undo/`) | **Current (`Install`/`Update`/`Reapply`):** Unchanged (`Managed` `vA` or `RestorationState.None`)<br>**Required:** `RestorationState = AttentionRequired` and `LastRefusalReason` persisted when store is writable | **Current (`Install`/`Update`/`Reapply`):** Remains `Managed` / `RestorationState.None`, `LastRefusalReason = null`<br>**Required:** `RestorationState = AttentionRequired` and `LastRefusalReason` populated in memory (even if store write also fails) | **Current & Required:** Unchanged | **Current:** `DxvkRollback` sets `AttentionRequired`; `DxvkInstaller` silently leaves `Managed` and `LastRefusalReason = null`<br>**Required:** All operations set `RestorationState.AttentionRequired` and non-empty `LastRefusalReason` on `installation`, `DxvkInstaller`/`DxvkRollback`, and `DxvkManager` |

---

## 3. Evaluation of Design Approaches

### 3.1 Option A (Recommended): Narrow Deferred-Commit & In-Memory Snapshot Scope (Zero Schema Change)

Instead of discarding `.transactions/<txId>/` inside `MultiFileTransactionEngine.Execute` before `GameLibraryStore.Save` runs, extend the lifetime of the per-operation recovery scope across the `_gameLibraryStore.Save(installation)` call:

1. **Two-Phase Session in `MultiFileTransactionEngine`:**
   - Add an internal `BeginTransaction(MultiFileTransactionRequest request)` (or `ExecuteWithCommitScope(request, Action commitOwnershipMetadata)`) on `MultiFileTransactionEngine` that performs the exact same validation, native-backup staging/publication, and target-file application as `Execute(request)`, but **retains** the in-memory recovery lists (`appliedIndices`, `publishedBackups`, `undoPaths`, `opRoot`, `planPath`) while invoking the caller's metadata commit callback.
   - Keep `Execute(MultiFileTransactionRequest request)` as a thin wrapper (`ExecuteWithCommitScope(request, static () => { })`) so all existing direct engine callers and unit tests in `MultiFileTransactionEngineTests` and `FailedOperationRecoveryTests` continue to work unchanged.
2. **Pre-Mutation Snapshot of `GameInstallation`:**
   - Before mutating `installation` inside the commit callback (`DxvkInstaller.ApplyToGameAsync`, `DxvkInstaller.ReapplyAsync`, `DxvkRollback.RestoreOriginalDllsAsync`), capture a deep in-memory snapshot of `installation` (via existing `GameInstallation` / `ManagedFileRecord` / `ExecutableProfile` copy helper or JSON clone).
   - If the post-file `GameLibraryStore.Save(installation)` fails **before** `game-library.json` replacement:
     1. Restore the in-memory `installation` fields from the pre-mutation snapshot so `GameLibraryStore`'s in-memory state never diverges from `game-library.json` on disk.
     2. Invoke `MultiFileTransactionEngine`'s existing `RecoverAttempted` + `RollbackNewlyPublishedBackups` logic using the still-retained `.transactions/<txId>/undo/` files.
     3. If compensation succeeds (`SafeFailure`), delete `.transactions/<txId>/` and surface a clear `LastRefusalReason` without corrupting either target files or durable metadata.
     4. If compensation itself fails (`UnresolvedRecovery`), retain `.transactions/<txId>/`, set `installation.RestorationState = RestorationState.AttentionRequired` and `installation.LastRefusalReason` in memory, and best-effort persist that `AttentionRequired` record if the store is writable.
3. **Explicit Pre-Replace vs. Post-Replace Distinction in `GameLibraryStore`:**
   - In `GameLibraryStore.WriteAllLocked()`, track `bool replaceCompleted = false;` immediately after `CustomCommitReplace` or `File.Move(tempPath, _libraryFile, ...)` returns.
   - If an exception is thrown when `replaceCompleted == false` (or if `CustomCommitReplace` threw before moving `tempPath` to `_libraryFile`, verified by checking whether `File.Exists(tempPath)` is still true and `_libraryFile` bytes match the pre-write snapshot), classify the error as a **pre-replacement failure** and trigger target-file compensation + in-memory snapshot rollback.
   - If `replaceCompleted == true`, or if `CustomCommitReplace` threw *after* moving `tempPath` to `_libraryFile` (detected because `_libraryFile` on disk now deserializes to the new `GameInstallation` state / matches the serialized JSON bytes), classify the outcome as **committed on disk**: do **not** compensate target files or revert in-memory `installation`, clean up `.transactions/<txId>/`, and treat the metadata commit as succeeded.

### 3.2 Option B (Rejected / Unnecessary): Additive Intent Record or Schema Bump

An alternative would be writing a durable `PendingTransactionIntent` record into `game-library.json` before `MultiFileTransactionEngine.Execute` and clearing it in a second `GameLibraryStore.Save` after `Execute`.

| Criterion | Option A: Deferred Undo Cleanup + Pre-Replace Check (Recommended) | Option B: Additive Intent Record in `game-library.json` |
| :--- | :--- | :--- |
| **Schema / Format Impact** | **None.** `GameLibrary.CurrentSchemaVersion` (`1`) and `game-library.json` format are unchanged. | Requires new fields in `GameInstallation` or `GameLibrary`, schema compatibility rules, and migration testing. |
| **Write Amplification & Failure Surface** | **1 store write per operation** (unchanged from today). | **2 store writes per operation** (doubles `game-library.json` replacement window). |
| **Crash Window Between File Apply and Metadata Save** | `.transactions/<txId>/plan.json` and `undo/` already exist on disk during `Execute` (`#166`), and `ManagedFileInspector` already detects target/metadata hash divergence on next startup. | Duplicates `.transactions/<txId>/plan.json` inside `game-library.json`. |
| **Complexity** | Localized to `MultiFileTransactionEngine`, `GameLibraryStore`, `DxvkInstaller`, `DxvkRollback`, and `DxvkManager`. | Spreads across `Models/`, `GameLibrarySchemaCompatibility`, `ManagedFileInspector`, and UI presentation. |

**Conclusion:** Option A solves all five reproductions without any schema/file-format change or extra `game-library.json` write pass.

---

## 4. Exact Owner of the Commit Boundary & Proposed Production Changes

The **commit boundary** is owned jointly by **`MultiFileTransactionEngine.ExecuteWithCommitScope`** (which holds the file-system undo scope open until the metadata commit callback completes) and **`GameLibraryStore.WriteAllLocked`** (which determines whether `game-library.json` replacement occurred on disk).

### Proposed Production Files and Methods

1. **`src/DXVKCompanion/Storage/GameLibraryStore.cs`**
   - Add an internal helper or exception/status contract (`internal bool TrySaveWithCommitStatus(GameInstallation installation, out Exception? error, out bool committedOnDisk)`) or enrich `WriteAllLocked()`:
     - Capture the serialized `byte[] expectedJsonBytes = Encoding.UTF8.GetBytes(json)` before invoking `CustomTempWriter` / `CustomCommitReplace`.
     - If `CustomCommitReplace` or `File.Move` throws, inspect whether `_libraryFile` on disk now equals `expectedJsonBytes`.
     - If `_libraryFile` on disk equals `expectedJsonBytes`, `committedOnDisk = true` (the replacement succeeded before the exception was thrown).
     - Otherwise `committedOnDisk = false` (pre-replacement failure; `game-library.json` on disk was not replaced).
   - Provide an internal snapshot/restore helper for `GameInstallation` (`CloneInstallationSnapshot(GameInstallation source)` and `RestoreInstallationSnapshot(GameInstallation target, GameInstallation snapshot)`) so callers can revert in-memory mutations atomically when `committedOnDisk == false`.

2. **`src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs`**
   - Add `public SafetyTransactionResult Execute(MultiFileTransactionRequest request, Action? afterTargetApplyCommitHook)` (or `ExecuteWithCommitScope`):
     - Runs steps 1–4 of `Execute` (validation, plan write, native-backup staging/publication, undo capture, target apply).
     - When all target files are applied, invokes `afterTargetApplyCommitHook()` **inside** the `try` block before declaring `TransactionState.Committed` / `TransactionOutcome.Success`.
     - If `afterTargetApplyCommitHook()` throws an exception indicating a **pre-replacement metadata commit failure**, the existing `catch (Exception ex)` block at line 153 automatically runs! Because `appliedIndices` contains all `0 .. request.Files.Count - 1` and `publishedBackups` contains all newly published backups from this attempt, `RecoverAttempted` restores every target file from `.transactions/<txId>/undo/`, `RollbackNewlyPublishedBackups` removes any newly created unreferenced backups from this attempt, and the engine returns:
       - `TransactionState.FailedSafely` / `TransactionOutcome.SafeFailure` if target compensation succeeded (and `finally` deletes `.transactions/<txId>/`), or
       - `TransactionState.AttentionRequired` / `TransactionOutcome.UnresolvedRecovery` if target compensation failed (and `finally` retains `.transactions/<txId>/`).

3. **`src/DXVKCompanion/DXVK/DxvkInstaller.cs`**
   - **`ApplyToGameAsync` (`lines 176-378`):**
     - Avoid persisting a brand-new `RestorationState.None` installation to disk before preflight succeeds, or capture whether `installation` existed before `ApplyToGameAsync` and snapshot `installation` prior to mutation.
     - Pass a commit callback into `_transactionEngine.Execute(request, commitCallback)`.
     - Inside `commitCallback`:
       - Snapshot `installation`, apply the `Managed` / `ManagedDxvkVersion` / `ManagedFiles` updates, and call `_gameLibraryStore.TrySaveWithCommitStatus(installation, out var saveEx, out bool committedOnDisk)`.
       - If `!committedOnDisk`: restore `installation` from the pre-mutation snapshot and throw `saveEx` so `MultiFileTransactionEngine` compensates all target files and newly published backups!
       - If `committedOnDisk && saveEx != null`: log the post-replace warning and treat the transaction as `Committed` (do not throw into `MultiFileTransactionEngine` recovery, because `game-library.json` on disk already has the new state).
     - Handle non-success `SafetyTransactionResult`:
       - Set `LastRefusalReason = result.Message ?? $"Transaction failed ({result.State}/{result.Outcome})."`.
       - When `result.State == TransactionState.AttentionRequired || result.Outcome == TransactionOutcome.UnresolvedRecovery`:
         - Set `installation.RestorationState = RestorationState.AttentionRequired;`
         - Set `installation.LastRefusalReason = LastRefusalReason;`
         - Best-effort persist `_gameLibraryStore.Save(installation)` inside a `try/catch` so a writable store durably records `AttentionRequired` + `LastRefusalReason`, while a failing store still keeps `AttentionRequired` + `LastRefusalReason` in memory.
   - **`ReapplyAsync` (`lines 420-723`):**
     - Apply the identical snapshot + `Execute(request, commitCallback)` + `UnresolvedRecovery` (`RestorationState.AttentionRequired` and `LastRefusalReason`) handling as `ApplyToGameAsync`.

4. **`src/DXVKCompanion/DXVK/DxvkRollback.cs`**
   - **`RestoreOriginalDllsAsync` (`lines 35-224`):**
     - Snapshot `installation` before mutating `ManagedFiles`, `ManagedDxvkVersion`, and `RestorationState`.
     - Pass the metadata commit callback into `_transactionEngine.Execute(request, commitCallback)`.
     - Inside `commitCallback`:
       - Mutate `installation` to `RestorationState.Restored` / `ManagedDxvkVersion = null` and call `_gameLibraryStore.TrySaveWithCommitStatus(installation, out var saveEx, out bool committedOnDisk)`.
       - If `!committedOnDisk`: restore `installation` in memory from the pre-restore `Managed` snapshot and throw `saveEx` so `MultiFileTransactionEngine` compensates the target files back to the pre-restore managed DXVK DLLs (`undo/`).
       - If target compensation in `MultiFileTransactionEngine` succeeds (`SafeFailure`), `installation` in memory and on disk remains `RestorationState.Managed` with the managed DXVK DLLs intact on disk (so a retry of `RestoreOriginalDllsAsync` succeeds cleanly!), while `LastRefusalReason` reports the metadata save failure and `RestoreOriginalDllsAsync` returns `false`.
       - Only transition `installation.RestorationState` to `RestorationState.AttentionRequired` when `result.State == TransactionState.AttentionRequired` / `TransactionOutcome.UnresolvedRecovery` (or when preflight refuses rollback).

5. **`src/DXVKCompanion/DXVK/DxvkManager.cs` & `src/DXVKCompanion/Storage/ProfileStore.cs`**
   - **`EnableDxvkAsync` (`lines 765-780`), `DisableDxvkAsync` (`lines 800-811`), `ReapplyAsync` (`lines 880-894`):**
     - Propagate `_installer.LastRefusalReason` / `_rollback.LastRefusalReason` to `DxvkManager.LastRefusalReason` on all failure paths.
     - Wrap `_profiles.Save(profile)` (which runs *after* `MultiFileTransactionEngine` + `GameLibraryStore` have both durably committed) in a guarded helper: `GameLibraryStore` is the authoritative ownership record (`docs/spec/DXVK-COMPANION-SPEC.md` §13), so a post-commit `ProfileStore.Save` exception must not revert or invalidate the already-committed `GameLibraryStore` or target files; instead, log the profile-cache save warning and keep in-memory `profile` synchronized with the committed `GameInstallation`.

---

## 5. Exact Rules for Each Failure Case

1. **Pre-replacement `GameLibraryStore.Save` failure (`committedOnDisk == false`):**
   - Revert in-memory `GameInstallation` to its exact pre-operation snapshot.
   - Run `MultiFileTransactionEngine` recovery (`RecoverAttempted` + `RollbackNewlyPublishedBackups`) using the retained `.transactions/<txId>/undo/` directory.
   - If target compensation succeeds (`TransactionState.FailedSafely` / `TransactionOutcome.SafeFailure`), delete `.transactions/<txId>/`, keep `installation` in its pre-operation state (`RestorationState.None` for first install, `Managed` Version A for Update B or Restore), populate `LastRefusalReason` on `DxvkInstaller`/`DxvkRollback`/`DxvkManager`, and return `false`.
2. **Post-replacement exception / ambiguous completion (`committedOnDisk == true`):**
   - Verified by `WriteAllLocked` either reaching `replaceCompleted = true` or confirming that `_libraryFile` on disk matches the newly serialized JSON bytes (`expectedJsonBytes`).
   - Do **not** revert in-memory `GameInstallation` and do **not** compensate target files (since both target files and `game-library.json` on disk already hold the new state).
   - Delete `.transactions/<txId>/` and complete the commit cleanly.
3. **Compensation failure during pre-replacement `GameLibraryStore.Save` rollback:**
   - If `RecoverAttempted` fails to restore one or more target files after a pre-replacement `Save` failure, `MultiFileTransactionEngine` returns `TransactionState.AttentionRequired` / `TransactionOutcome.UnresolvedRecovery` and retains `.transactions/<txId>/`.
   - `DxvkInstaller` / `DxvkRollback` sets `installation.RestorationState = RestorationState.AttentionRequired` and `installation.LastRefusalReason = result.Message` in memory, best-effort attempts `_gameLibraryStore.Save(installation)` (which may also fail if the disk/store is still unwritable, caught and logged), sets `LastRefusalReason` on the service, and returns `false`.
4. **Unresolved engine recovery (`TransactionOutcome.UnresolvedRecovery`) when `GameLibraryStore` is writable vs. failing:**
   - Whether `UnresolvedRecovery` was triggered by a mid-apply file error + failed undo or by a pre-replace metadata save error + failed undo:
     - Always set `installation.RestorationState = RestorationState.AttentionRequired` and `installation.LastRefusalReason = result.Message` in memory, and set `LastRefusalReason` on `DxvkInstaller` / `DxvkRollback` / `DxvkManager`.
     - Attempt `_gameLibraryStore.Save(installation)` inside a `try/catch`. When the store is writable (the standard case for a locked game DLL during update recovery), `AttentionRequired` and `LastRefusalReason` are durably persisted to `game-library.json`. When the store is also unwritable, the exception is caught and logged while in-memory health state still reflects `AttentionRequired`.
5. **`ProfileStore.Save` failure after library commit:**
   - `GameLibraryStore` (`game-library.json`) and target files have already committed atomically.
   - `DxvkManager` catches and logs any `ProfileStore.Save` I/O exception after a committed `ApplyToGameAsync` / `RestoreOriginalDllsAsync` / `ReapplyAsync` without rolling back `GameLibraryStore` or target files; on next startup, `DxvkManager.ReassessEffectiveApi` and library hydration reconcile `ProfileStore` from `GameLibraryStore`.
6. **Process termination (crash / power loss) between target file replacement and `game-library.json` replacement:**
   - On next launch, `ManagedFileInspector.InspectAll()` (`src/DXVKCompanion/Storage/ManagedFileInspector.cs:19-247`) compares every `ManagedFileRecord` in `game-library.json` against on-disk target files:
     - For an interrupted **Update B** or **Restore** on an already-managed installation, `ManagedFileInspector` sees that on-disk target hashes do not match `ExpectedManagedSha256` in `game-library.json`, marks `record.CurrentState = ManagedFileState.ExternallyChanged`, and transitions `installation.RestorationState = RestorationState.AttentionRequired` while preserving the original native backup in `backups/<instId>/`.
     - For an interrupted **First Install** where the process died before `game-library.json` recorded `ManagedFiles`, existing `ExistingDxvkDetector` classifies the known official DXVK DLLs in the game folder (`AdoptableOfficial` if originally absent, or `AttentionRequired` if native backups exist in `backups/<instId>/`), while `.transactions/<txId>/` retains the pre-operation undo evidence for diagnostics.

---

## 6. Startup & Restart Behavior

No new startup reconciliation pass or schema migration is required:
- `ManagedFileInspector.InspectInstallation` (`src/DXVKCompanion/Storage/ManagedFileInspector.cs:19-194`) already verifies `ExpectedManagedSha256` (when `Managed`) and `OriginalSha256` / absence (when `Restored`) on startup and transitions any installation whose target files diverged from `game-library.json` (e.g., due to hard process termination or a double fault where both target recovery and store write failed) to `RestorationState.AttentionRequired`.
- Retaining `.transactions/<txId>/` only on `TransactionOutcome.UnresolvedRecovery` preserves the forensic undo snapshot without polluting `ManagedFileInspector` or `GameLibraryStore`.

---

## 7. Allowed-Path Proposal for the Follow-Up Implementation Issue

The follow-up production repair issue (which turns the 4 expected-red regression tests in `OwnershipPersistenceFailureTests.cs` green while keeping the characterization test green) should declare the following `### Allowed paths`:

- `src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs`
- `src/DXVKCompanion/Storage/GameLibraryStore.cs`
- `src/DXVKCompanion/DXVK/DxvkInstaller.cs`
- `src/DXVKCompanion/DXVK/DxvkRollback.cs`
- `src/DXVKCompanion/DXVK/DxvkManager.cs`
- `tests/DXVKCompanion.PhaseA.Tests/OwnershipPersistenceFailureTests.cs` (only if the characterization test needs an additional post-fix assertion alongside its characterization comments)
- `docs/ai-journal/**`

No changes to `src/DXVKCompanion/Models/**`, `DXVKCompanion.csproj`, CI workflows, or policy files are required.

---

## 8. Mapping from Regression Tests to Acceptance Criteria

| Test Method in `OwnershipPersistenceFailureTests.cs` | Issue #169 Acceptance Criterion | Role on Unchanged `main` (`#169`) | Role in Follow-Up Production Fix |
| :--- | :--- | :--- | :--- |
| `FirstInstall_WhenPostFileOwnershipSaveFailsBeforeReplace_CompensatesTargetFilesAndKeepsInMemoryAndDiskStoreConsistent` | Reproduction 1: First install post-file ownership save failure before replacement | **Expected-red regression oracle:** proves `AllFilesApplied` and `PostFileSaveAttempted`, then fails because DXVK DLLs remain deployed and in-memory `store` says `Managed` while disk says `RestorationState.None`. | Must pass (`Green`): target files compensated to native baseline; in-memory and reconstructed store both `RestorationState.None`. |
| `UpdateAfterInstallA_WhenPostFileOwnershipSaveFailsBeforeReplace_RecoversVersionAFilesAndPreservesRestoreCapability` | Reproduction 2: Update B post-file ownership save failure after real Install A | **Expected-red regression oracle:** proves native backup is preserved, then fails because Version B DLLs remain on disk while reconstructed store has Version A metadata (causing `RestoreOriginalDllsAsync` to refuse). | Must pass (`Green`): Version A DLLs recovered on disk, matching Version A metadata, and subsequent `RestoreOriginalDllsAsync` restores true native baseline. |
| `UnresolvedEngineRecovery_OnManagedInstallationWithWritableStore_PersistsAttentionRequiredAndLastRefusalReason` | Reproduction 3: Unresolved engine recovery on a managed installation with a writable store | **Expected-red regression oracle:** proves `BeforeRecovery` ran and failed recovery, then fails because `DxvkInstaller`/`DxvkManager` leave `LastRefusalReason == null` and `RestorationState == Managed`. | Must pass (`Green`): `LastRefusalReason` populated on installer/manager and `RestorationState.AttentionRequired` persisted in memory and on disk. |
| `Restore_WhenPostFileOwnershipSaveFailsBeforeReplace_AvoidsUnrecoverableSplitBrainBetweenTargetFilesAndStore` | Reproduction 4: Restore post-file ownership save failure before replacement | **Expected-red regression oracle:** fails because in-memory `ManagedDxvkVersion` is cleared to `null` while disk still says `"2.4"` and target files are left native (causing retry `Restore` on reconstructed services to refuse). | Must pass (`Green`): target files compensated back to managed `"2.4"` state matching disk store, and retry `Restore` on reconstructed services succeeds. |
| `Characterization_PostReplaceException_WhenCommitReplaceMovesFileThenThrows_MetadataIsAlreadyDurableOnDisk` | Reproduction 5: Ambiguity characterization when `CustomCommitReplace` moves file and then throws | **Labeled characterization test (`Green` on `main`):** proves that when `File.Move` completes before an exception is thrown, reconstructed `GameLibraryStore` on disk already has `RestorationState.Managed` and `"2.4"` hashes. | Must remain `Green`: documents why production repair must verify whether replacement completed on disk before compensating target files. |
