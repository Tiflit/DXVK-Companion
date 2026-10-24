# External Game Update, Authorized Baseline Replacement, and Restore Lifecycle Acceptance — #291

Service-level acceptance coverage for an external game update replacing a managed DLL, stale-action invalidation, explicit authorized new-baseline reapply, subsequent version upgrade, and exact restore of the updated native file.

## Scope and model

When DXVK manages a game directory and the game platform (e.g. Steam) applies an external update, game binaries may be overwritten or updated without DXVK Companion's advance involvement. Under Companion's established safety invariants and specification sections 18.2 and 20:

1. **Observation alone does not authorize overwriting external changes**: When an external modification replaces a managed binary (such as `d3d11.dll` updated from native Version A to native Version B), Companion detects the hash discrepancy upon inspection, marks the managed file as `ExternallyChanged`, and transitions the installation to `RestorationState.AttentionRequired`. It does not automatically overwrite or revert the file.
2. **Section 20 Pending-Action Conflict Rule**: Any preexisting pending action referring to the installation (such as a queued `Update` scheduled while the game was running) is invalidated and cleared durably (`PendingAction = null`). Subsequent pending action dispatches do not execute stale operations against the changed game files.
3. **Explicit authorized baseline replacement (Section 18.2)**: Only an explicit user action—calling `RequestReapplyByPathAsync(profile, updateBaseline: true)`—authorizes Companion to adopt the newly observed game file (native Version B) as the new active restoration baseline. In the transactional multi-file engine:
   - The backup record for `d3d11.dll` is replaced with the bytes of Version B.
   - `ManagedFileRecord.OriginalSha256` is updated to the SHA-256 digest of Version B.
   - Version A is no longer the active restore baseline; Version B becomes the sole active restoration baseline.
   - Companion re-deploys the selected DXVK release binaries (`d3d11.dll` and `dxgi.dll`) and restores managed health (`RestorationState.Managed`, `ManagedFileState.Consistent`).
4. **Subsequent version upgrades preserve the new baseline**: When an explicit `Update` is subsequently performed (e.g., upgrading to `v2.6`), newer release binaries are deployed and owned hashes are updated, while the restoration baseline for `d3d11.dll` (Version B) and original `dxvk.conf` remain intact.
5. **Exact restoration of the updated native file**: Upon normal `Disable` (`RequestDisableByPathAsync`), Companion restores `d3d11.dll` to Version B (the updated native binary), restores `dxvk.conf` byte-identical, deletes unbacked wrapper DLLs (`dxgi.dll` with synthetic `Missing` baseline), and preserves all unrelated game binaries, sentinels, and empty subdirectories.
6. **Fresh service reconstruction & offline isolation**: All services and stores are reconstructed from disk at each lifecycle boundary. Fake HTTP transport is marked unavailable after initial release acquisition, guaranteeing zero additional network requests across all inspection, reapply, update, and disable stages.

## Acceptance scenarios

### 1. Full lifecycle theory (`ExternalUpdateBaselineLifecycle_AuthorizedReapply_UpdatesAndRestoresUpdatedNativeFile`)

Evaluated on DX11 across `x64` and `x32` architectures:
- **Pre-deployment setup & acquisition**: Acquires official releases `v2.5` and `v2.6` into the shared source library, validating markers, manifests, and architecture payload bytes. Initializes game directory with inert `Game.exe`, native Version A `d3d11.dll`, absent `dxgi.dll`, mixed-newline/no-final-newline original `dxvk.conf` with lookalike keys (`dxvk.hudExtra`, `dxvk.maxFrameRateTarget`) and user lines, sentinel file, and meaningful empty directory (`Data/EmptySubDir`).
- **Initial installation**: Deploys `v2.5` with explicit HUD and frame limiting choices under `UseGlobal` manual policy. Verifies deployed DLL bytes, exact generated config keys, `v2.5` version, Version A backup, config backup, and `dxgi.dll` synthetic `Missing` baseline. Reconstructs stack from disk.
- **Queued pending intent & external update to Version B**: Persists an older pending Update intent (`PendingAction.Update("v2.5", ...)`). Replaces `d3d11.dll` in the game with native Version B bytes. Reconstructs stack from disk and inspects through `ManagedFileInspector`:
  - Verifies `d3d11.dll` transitions to `ExternallyChanged`.
  - Verifies installation transitions to `AttentionRequired`.
  - Verifies stale `PendingAction` is cleared durably.
  - Verifies Version A and config backups, source files, and game directory remain untouched.
- **Repeat inspection & pending dispatch no-op**: Reconstructs stack from disk. Repeats inspection (returns `false`, attention retained). Runs `ProcessAllPendingActionsAsync()` (0 dispatched, no stale deployment or overwrite, attention retained).
- **Authorized Reapply (`updateBaseline: true`)**: Calls `RequestReapplyByPathAsync(profile, updateBaseline: true)`. Verifies:
  - Returns `Applied`.
  - Deploys `v2.5` DLL bytes and preserves exact config assignments.
  - `d3d11.dll` backup file and `OriginalSha256` now match Version B byte-for-byte and hash-for-hash.
  - Version A is no longer the active restore baseline.
  - Config backup and `dxgi.dll` `Missing` baseline remain unchanged.
  - Installation transitions to `RestorationState.Managed` with `ManagedFileState.Consistent`.
- **Version upgrade (`v2.6`)**: Reconstructs stack from disk. Updates policy to `v2.6` and executes `RequestUpdateByPathAsync(profile)`. Verifies newer release bytes, updated owned hashes, actual-vs-selected versions, and unchanged Version B restoration baseline.
- **Disable normally & complete restoration**: Reconstructs stack from disk. Executes `RequestDisableByPathAsync(profile)`. Verifies:
  - Game directory matches the exact expected final snapshot: initial game with `d3d11.dll` replaced by Version B, original config exact, `dxgi.dll` absent, sentinel and empty directories retained.
  - Exact historical ownership set (`d3d11.dll`, `dxgi.dll`, `dxvk.conf`) with `ManagedDxvkVersion` cleared and historical baselines intact.
  - Complete retained backup and source sets match expected snapshots.
  - Zero additional HTTP calls throughout.

### 2. Focused failure theory (`ExternalUpdateBaselineLifecycle_CorruptedOrMissingSource_RefusesReapplyBeforeMutation`)

Evaluated on `x64` and `x32` across two fault types (`CorruptedManifest` and `MissingPayloadDll`):
- Setup game with native Version A `d3d11.dll`, deploy `v2.5`, queue pending update, replace `d3d11.dll` with Version B, and inspect to clear pending action and mark `AttentionRequired`.
- Induce source fault in `Sources/v2.5`: corrupt `companion-release.json` with `{}` or delete required payload DLL. Snapshot raw game, source, and backup resources.
- Reconstruct stack from disk and request authorized reapply (`RequestReapplyByPathAsync(profile, updateBaseline: true)`).
- Verify refusal: returns `Failed`, `LastRefusalReason` provides descriptive source diagnostic.
- Reconstruct stack from disk again and verify zero side effects:
  - Raw game resources unchanged: Version B remains in game.
  - Raw source resources unchanged: fault is not repaired or deleted.
  - Raw backup resources unchanged: Version A remains the active restore baseline.
  - Installation remains in `AttentionRequired` with `PendingAction == null`.
  - Zero additional HTTP calls.

## Execution and boundaries

- Tests execute exclusively via hosted Windows GitHub Actions CI. No local builds, tests, or diagnostic scripts.
- Tests-first policy: existing production logic in `ManagedFileInspector`, `DxvkManager`, `DxvkInstaller`, and `DxvkRollback` already satisfies all invariants; production code remains unchanged.
