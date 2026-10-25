# Persisted Update All and Restore All mixed-result lifecycle acceptance — Gemini, 2026-10-24

Task [#293](https://github.com/Tiflit/DXVK-Companion/issues/293), base main `b1904d4a062483ac1c4a0c40bbe6a72ebe1eba62`, tree `80ebe37a524350592976f57d3281cc6276a90317`, policy `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9`. Attributed coordinator transcription of owner instruction following human merge of #292: "Merged / Continue". Owner prioritizes complete standalone DXVK functionality/tests and defers UI polish.

Implemented service-level acceptance coverage in `tests/DXVKCompanion.PhaseA.Tests/MixedBatchLifecycleAcceptanceTests.cs` for an integrated acquired-source -> persisted `UpdateAllEnabled` -> persisted `RestoreAll` mixed-result lifecycle across service reloads. Created documentation `docs/validation/MIXED-BATCH-LIFECYCLE.md`.

## Implementation Details

1. **Integrated Lifecycle Theory (`MixedBatchLifecycle_UpdateAllAndRestoreAll_MixedResultsPreserveErrorIsolationAndDurability`)**:
   - Covers DX11 / `x64` across a bounded two-case insertion-order theory:
     - `BeforeHealthyTargets`: profiles inserted and enumerated in order `[GameB, GameA, GameC, GameD]`.
     - `BetweenHealthyTargets`: profiles inserted and enumerated in order `[GameA, GameB, GameC, GameD]`.
   - Distinct inert executable names: `GameA.exe`, `GameB.exe`, `GameC.exe`, `GameD.exe`.
   - Pre-deployment setup:
     - Game A: native `d3d11.dll` (Existing), absent `dxgi.dll` (Missing), preexisting user `dxvk.conf` with lookalike keys and user lines, sentinel file, empty subdirectory.
     - Game B: native `d3d11.dll` (Existing), absent `dxgi.dll` (Missing), preexisting user `dxvk.conf`, sentinel file, empty subdirectory.
     - Game C: native `d3d11.dll` (Existing), absent `dxgi.dll` (Missing), absent `dxvk.conf` (synthetic Missing baseline for config), sentinel file, empty subdirectory.
     - Game D: native `d3d11.dll` (Existing), absent `dxgi.dll` (Missing), preexisting user `dxvk.conf`, sentinel file, empty subdirectory.
   - Official releases `v2.5` and `v2.6` acquired into shared library with verified markers, manifests, and payload bytes.
   - Fake HTTP transport marked unavailable (`RejectAllRequests = true`) after initial acquisition, guaranteeing zero additional network requests across all subsequent operations.
   - Initial deployment under isolated `SettingsStore` Manual global policy (`GlobalManagementPolicy.Manual`) loaded from `settings.json`:
     - Game D deployed through real enable (`RequestEnableByPathAsync`) and disable (`RequestDisableByPathAsync`), creating truthful durable history (`RestorationState.Restored`, cleared managed version, retained historical ownership).
     - Games A, B, C deployed via single-call `RequestEnableByPathAsync` under `UseGlobal` manual policy with exact older selector `v2.5` without targetVersion override.
     - Unconditional `ExpectedManagedSha256` comparisons against deployed game bytes for DLLs and configs across all targets.
     - Verifies original native/config baselines and backup files across all targets.
   - Selector modification & `UpdateAllEnabledAsync`:
     - Reconstructed stack from disk. Verified persisted policy and actual version before modifying selectors.
     - Changed only persisted selectors: Game A and Game C select `v2.6`; Game B selects deliberately unavailable `v2.7-unavailable`. Game D remains unselected. Did not preassign profile actual versions.
     - Snapshotted complete raw Game B resources and captured meaningful ownership before update.
     - Reconstructed stack from disk and called `UpdateAllEnabledAsync()`.
     - Verified traversal order matches profile enumeration order (`[GameB, GameA, GameC]` or `[GameA, GameB, GameC]`).
     - Verified Game A: `Applied`, updated to `v2.6`, release DLLs deployed, `ExpectedManagedSha256` matching deployed bytes, original baselines intact.
     - Verified Game B: `Failed`, actual version remains `v2.5`, selector remains `v2.7-unavailable`, complete raw game directory and backups completely unchanged, ownership matches pre-update state.
     - Verified Game C: `Applied`, updated to `v2.6`, release DLLs deployed, `ExpectedManagedSha256` matching deployed bytes, original baselines intact.
     - Verified Game D: intentionally omitted from results dictionary.
     - Verified retained batch refusal reason: `manager.LastRefusalReason` retains Game B's unavailable version diagnostic despite subsequent healthy successes.
     - Zero additional HTTP calls.
   - Backup fault injection & `RestoreAllAsync`:
     - Corrupted Game B's recorded original native backup (`Backups/<idB>/d3d11.dll`) while leaving its managed game DLL bytes in `GameB` intact.
     - Snapshotted faulted backup, game, and source state and captured meaningful ownership.
     - Reconstructed stack from disk and called `RestoreAllAsync()`.
     - Verified summary semantics: `TotalManaged`: 3 (Games A, B, C), `Restored`: 2 (Games A, C), `FailedOrAttentionRequired`: 1 (Game B), `AlreadyRestored`: 1 (Game D), `QueuedRunning`: 0.
     - Verified diagnostic messages: single message identifying `GameB.exe:` with backup hash mismatch, with no false attribution to Games A, C, or D.
     - Games A and C match their complete original raw file/directory snapshots.
     - Game B resources (game, sources, faulted backup) remain completely untouched. Active management retained (`profileB.DxvkEnabled == true`, `instB.ManagedDxvkVersion == "v2.5"`), `instB.RestorationState == AttentionRequired`, no false Restored state, no pending action.
     - Zero additional HTTP calls.
   - Reload, historical ownership, and repeat `RestoreAllAsync`:
     - Reconstructed stack from disk.
     - Verified exact nonempty historical ownership sets for restored Games A, C, D with cleared active versions, truthful retained baselines, and preserved backup files.
     - Verified Game B remains in `AttentionRequired` with active management retained and documented refusal diagnostic.
     - Repeated `RestoreAllAsync()`: `TotalManaged`: 1 (Game B), `Restored`: 0, `FailedOrAttentionRequired`: 1 (Game B), `AlreadyRestored`: 3 (Games A, C, D), `QueuedRunning`: 0.
     - Message identifies `GameB.exe:` backup hash mismatch.
     - Verified Game B remains safely unresolved without silent repair and restored resources remain unchanged.
     - Verified complete retained backup file set (7 backup files across the 4 installations).
     - Verified sources remain intact.
     - Zero additional HTTP calls throughout the entire suite.

## Verification & Execution Record

- **Tests First Policy**: Existing production code in `DxvkManager`, `DxvkInstaller`, and `DxvkRollback` was inspected and verified to already satisfy all invariants; production code remains untouched.
- **Execution Mode**: Cloud-only hosted CI. Zero local builds, zero local tests, zero local diagnostic scripts executed.
- **Base commit**: `b1904d4a062483ac1c4a0c40bbe6a72ebe1eba62` (tree `80ebe37a524350592976f57d3281cc6276a90317`)
- **Policy identity**: `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9`
- **Branch**: `feat/issue-293-mixed-batch-lifecycle`

## Review & Follow-up Repairs (Codex Review on PR #294)

- **Initial Commit**: `19febbebb22cf9a8135185c336980e4effeb4ced` (tree `e5ea45cb236368969b0cc989193d90c20f7bbb33`)
- **Initial Hosted CI Results**:
  - Test Suite Run `40149803749`:
    - Attempt 1: 1,308 passed, 1 failed (`DxvkModernApiCompatibilityTests.DxvkInstaller_AdoptExisting_DirectCall_RefusesUnsupportedApis(api: Vulkan)` on line 769).
    - Attempt 2 (rerun failed jobs): 1,309 passed in 1m34s.
  - Workflow Tests Run `40149803786`: 386 passed in 5.426s.
- **Codex Grouped Review Feedback & Resolution**:
  1. **Preservation snapshots at actual failure boundaries**:
     - Added `AssertResourcesUnchanged(preUpdateBackupsSnapshot, backupsDir)` immediately after `UpdateAllEnabledAsync()` and before B's backup corruption, verifying that failed batch update mutates zero backups.
     - Implemented `AssertFaultedOwnershipRetained` helper comparing reloaded B against `faultedOwnershipB`, allowing only the documented attention/refusal transition (`RestorationState.AttentionRequired`, `LastRefusalReason` containing backup hash mismatch, `PendingAction == null`, and exact record sets, hashes, backup paths, actual versions, selectors, and config intent retained).
     - Applied `AssertFaultedOwnershipRetained` after first `RestoreAllAsync` reload and after repeat `RestoreAllAsync` fresh reload.
  2. **Exact and complete restored history checks for A/C/D**:
     - Implemented `AssertRestoredHistory` helper verifying exact paths `["d3d11.dll", "dxgi.dll", "dxvk.conf"]`, cleared `ManagedDxvkVersion`, `CurrentState == Consistent`, and truthful `OriginalState`/`OriginalSha256`/`BackupRelativePath` across existing vs absent baselines.
     - Applied `AssertRestoredHistory` to Game D after its real install/disable lifecycle in Stage 2, and to Games A, C, D in Stage 5 after reload and after repeat `RestoreAllAsync` fresh reload.
     - Added assertions verifying reloaded selector/API/architecture intent on A, B, C, D before changing selectors in Stage 3 (`profile.Api`, `profile.Architecture`, `profile.DxvkVersion`, `profile.DxvkEnabled`, `inst.ManagementPolicy.Mode`, `inst.ManagementPolicy.PinnedDxvkVersion`, `inst.ManagedDxvkArchitecture`, executable API and architecture).
  3. **Exact configuration assertions via `ParseConfigAssignments`**:
     - For Game D in Stage 2: performed deployed DLL bytes, hash, and exact configuration checks before disable (`dxvk.hud == "fps,devinfo"`, `dxvk.maxFrameRate == "60"`, `user.settingD == "4"`, `ExpectedManagedSha256` matching deployed bytes).
     - For Games A, B, C in Stage 2: checked exact config assignments (A: `dxvk.hud`, `dxvk.maxFrameRate`, lookalikes `dxvk.hudExtra`, `dxvk.maxFrameRateTarget`, user key `user.settingA`; B: `dxvk.hud`, `dxvk.maxFrameRate`, lookalikes `dxvk.hudExtra`, `dxvk.maxFrameRateTarget`, user key `user.settingB`; C: only generated keys, no lookalike or user keys).
     - After A/C update in Stage 3: verified exact config assignments for updated Game A and Game C.
- **Repair Commits & CI**:
  - Commit `41b8902e73b7e8f5ebcefa46a03c0f7e4add2cc0` (tree `77b64cc05254d23d37bb23e74fdd964effbd7c6b`):
    - Scope Check Run `40151354536`: Passed (9s).
    - PR Hygiene Run `40151354529`: Passed (10s).
    - Workflow Tests Run `40151354534`: Passed (19s).
    - Build and Test Run `40151354563`: Failed with CS0117 (`ManagementMode.Global` instead of `ManagementMode.UseGlobal` in lines 795, 806, 817, 827).
  - Repaired enum identifier to `ManagementMode.UseGlobal`.
