# DX9 x32/x64 persisted manual lifecycle acceptance — Gemini, 2026-10-23

Task [#287](https://github.com/Tiflit/DXVK-Companion/issues/287), base main `6900d0e98e9a55fc39a2b10431fb05568de0df26`, policy `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9`. Attributed coordinator transcription of owner instruction following merge of #285: "Merged / Continue". Owner prioritizes complete standalone DXVK functionality/tests and defers UI polish.

Implemented service-level acceptance coverage in `tests/DXVKCompanion.PhaseA.Tests/Dx9ManagedLifecycleAcceptanceTests.cs` for Direct3D 9 (DX9) x32 and x64 persisted manual lifecycle management. Created documentation `docs/validation/DX9-MANAGED-LIFECYCLE.md`.

## Implementation Details

1. **4-Case Successful Lifecycle Theory (`Dx9ManagedLifecycle_PersistedManual_PreservesBaselinesAndRestoresByteIdentical`)**:
   - Evaluates four combinations: `(x32, preexisting: true)`, `(x32, preexisting: false)`, `(x64, preexisting: true)`, and `(x64, preexisting: false)`.
   - Stationary synthetic game contains an inert executable (`Game.exe`), a sentinel file (`sentinel.bin`), a meaningful empty subdirectory (`Data/EmptySubDir`), and optional unrelated native `d3d11.dll` and `dxgi.dll`.
   - Distinguishable official releases (`v2.5` and `v2.6`) constructed with distinct x32 and x64 `d3d9.dll` bytes and explicit nonselected DX11/DXGI archive entries, verified against official manifests and markers.
   - Fake HTTP transport records initial acquisition requests and is marked unavailable (`RejectAllRequests = true`) for all subsequent lifecycle operations, requiring zero additional HTTP requests.
   - Durable profile and executable records store `Api = GraphicsApi.DX9` and matching architecture.
   - `UseGlobal` management policy persists exact older version intent ("v2.5") without legacy automatic `PinnedVersion` mode. Normal manager entry points (`RequestEnableByPathAsync`, `RequestUpdateByPathAsync`) execute without `targetVersion` overrides.
   - Verifies that only `d3d9.dll` and `dxvk.conf` are managed; unrelated native `d3d11.dll` and `dxgi.dll` remain completely unowned, untouched, and byte-identical throughout install, update, reapply, and restore.
   - Fresh service stack (`ConstructServiceStack`) reconstructed from disk between every stage without reusing surviving in-memory instances.
   - Config file presence and ownership assertions maintained unconditionally across feature disable/reapply; parsed key assignments verify exact removal of `dxvk.hud` and `dxvk.maxFrameRate` while prefix lookalikes (`dxvk.hudExtra = full`, `dxvk.maxFrameRateTarget = 120`) and user lines are preserved.
   - Restoration returns game to exact original raw resource set (files, bytes, empty subdirectories), restores or deletes `d3d9.dll` and `dxvk.conf` according to original baseline state, clears active managed versions, and truthfully retains historical records, pin, and architecture. Acquired sources and original backups are verified byte-identical at every boundary.

2. **4-Case Negative Theory (`Dx9ManagedLifecycle_CorruptedOrMissingPayload_RefusesBeforeGameWrites`)**:
   - Evaluates both `x32` and `x64` against missing payload files and corrupted payload bytes.
   - Initial install refusal: verifies that manager refuses deployment before game writes when the acquired release payload is missing or corrupted, returns `DxvkActionResult.Failed`, records truthful refusal reason (`"not available locally"`), and preserves game files, available sources, and backups.
   - Update refusal: establishes a valid baseline deployment with backups, then injects a missing or corrupted payload into the update release. Verifies manager refuses update before game writes, preserves the existing running deployment, preserves original baseline backups and available sources, and maintains `Managed` state on the existing version without false switch or false `Restored` status.

## Verification & Execution Record

- **Tests First Policy**: Production code was inspected and already correctly handles DX9 single-file payload resolution, unrelated file isolation, and manual persisted version intent; production files remain untouched.
- **Execution Mode**: Cloud-only hosted CI. No local builds, tests, or diagnostic scripts.
