# DXVK Companion — Phase A, Revision 2

This revision incorporates the Phase A review findings before application integration.

## Changes from the first Phase A package

- Added `FileOriginalState.Unknown`.
- Added `RestorationState`.
- Legacy DXVK-enabled profiles now create `ManagedFileRecord` entries.
- Legacy managed architecture is reconciled conservatively.
- Legacy frame-limit conflicts are handled conservatively instead of taking the last profile.
- A corrupt or unreadable `game-library.json` is preserved as a recovery copy rather than silently falling back to stale `games.json`.
- A future/unsupported schema is not downgraded through legacy migration.
- `GameLibraryStore` uses a synchronization lock for in-memory collection access and serialized writes.
- Restoration state is refreshed centrally from persisted managed-file state.

## Important migration limitation

The old profile format does not contain a complete record of the pre-Companion DLL state. During migration:

- an existing legacy `.bak` file is recognized as evidence that an original file may be recoverable;
- the new record does not claim a missing/original state unless that fact is actually known;
- if no legacy backup exists, `OriginalState` is `Unknown` and the installation is marked as requiring attention.

The old `.bak` files are not copied or deleted by Phase A. A later file-engine migration must inspect and safely import them before legacy cleanup.

## Important D3D10 note

The legacy migration currently uses the existing application's historical D3D10 file behavior (`d3d11.dll` + `dxgi.dll`) when reconstructing legacy managed-file records. This is a migration representation only. The final D3D10 deployment matrix must be verified against the official DXVK release contents before the deployment engine is rewritten.

## Existing application is not wired to this store yet

The new store remains parallel. No current detection, UI, installer, rollback, or manager code has been switched over in this phase.

## Next phase

Review this revision, then wire `GameLibraryStore` into profile/application orchestration while keeping DXVK file operations unchanged.


## Revision 3 corrections

This revision fixes the review findings before any application wiring:

- Uses the actual project enum names `GraphicsApi.DX9`, `DX10`, and `DX11`.
- Uses only defined `RestorationState` members.
- `FileOriginalState.Unknown` is the zero/default value.
- `ManagedFileRecord.OriginalState` defaults to `Unknown`.
- Legacy migration continues to create `ManagedFileRecord` entries.
- Non-file migration conflicts marked `AttentionRequired` are no longer overwritten by aggregate file-state recomputation.

The next test harness should explicitly cover the conflicting-legacy-profile case where all `.bak` files exist and the final restoration state must remain `AttentionRequired`.


## r4 aggregate-state correction

`RestorationState` is now derived from recorded facts. Migration reconciliation methods no longer assign `RestorationState` directly. They set `InstallationConflictFlags`, and the single `UpdateRestorationStateLocked` method is the only place that aggregates the final restoration state.

This prevents file-record creation from erasing a version/architecture/frame-limit conflict.

The persisted conflict flags are intentionally separate from `RestorationState`: the latter is the UI-facing aggregate, while the flags retain why an installation requires attention.
