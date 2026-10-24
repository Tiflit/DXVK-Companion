# DX9 x32/x64 persisted manual lifecycle acceptance — #287

Service-level acceptance coverage for Direct3D 9 (DX9) x32 and x64 persisted manual lifecycle management. This suite verifies that DXVK Companion correctly manages the single-file DX9 payload (`d3d9.dll`), preserves unrelated native DirectX libraries, maintains durable executable records and `UseGlobal` exact-version selection across fresh service reloads, enforces configuration ownership and prefix-lookalike preservation, restores exact original file/directory sets, and strictly refuses corrupt or missing acquired payloads before modifying game files.

## Scope and model

Production Direct3D 9 games use either 32-bit (x86/win32) or 64-bit (x64/amd64) architectures. Unlike DX10/DX11 which deploy pair or triple payloads (`d3d11.dll`, `dxgi.dll`, and optionally `d3d10core.dll`), DX9 deploys exclusively `d3d9.dll`. Unrelated native libraries (such as system or game native `d3d11.dll` and `dxgi.dll`) frequently coexist in game directories; the manager must never adopt, modify, back up, or remove them during DX9 lifecycle operations.

The acceptance suite operates under an isolated `SyntheticTestDirectory` with zero live process execution:
- Synthetic game directory contains an inert executable (`Game.exe`), a sentinel file (`sentinel.bin`), a meaningful empty subdirectory (`Data/EmptySubDir`), and optional unrelated native `d3d11.dll` and `dxgi.dll`.
- Distinguishable official releases (`v2.5` and `v2.6`) are constructed with distinct x32 and x64 `d3d9.dll` bytes along with nonselected DX11/DXGI files, verified archive digests, and manifest/marker checks.
- Zero network requests are permitted after initial acquisition (`RejectAllRequests = true` on the HTTP transport); all subsequent operations (install, update, reapply, restore) must resolve strictly from local sources.
- Normal `DxvkManager`, `GameLibraryStore`, `ProfileStore`, `MultiFileTransactionEngine`, `DxvkInstaller`, and `DxvkRollback` entry points are used with real private leases configured against an isolated `OperationLeaseCoordinator`.
- Process activity inspection operates against an explicitly inactive synthetic process universe.
- Every lifecycle checkpoint reconstructs the full service stack from disk; no surviving in-memory instances are reused.

## Acceptance scenarios

### 1. Successful 4-case lifecycle theory

Tests all four combinations of architecture and preexisting baseline:
- `x32` with preexisting native `d3d9.dll` and mixed-newline `dxvk.conf`
- `x32` with clean-slate/absent baseline
- `x64` with preexisting native `d3d9.dll` and mixed-newline `dxvk.conf`
- `x64` with clean-slate/absent baseline

Lifecycle stages exercised:
1. **Acquisition & durable setup**: Acquires `v2.5` and `v2.6` into the shared source library, verifying manifests, markers, and initial request counts. Durable profile and installation executable records record `Api = GraphicsApi.DX9` and matching architecture. Policy is set to `UseGlobal` with `PinnedDxvkVersion = "v2.5"`.
2. **Initial install via manager without targetVersion override**: `RequestEnableByPathAsync(profile)` resolves version from persisted policy. Asserts only `d3d9.dll` and `dxvk.conf` are managed; unrelated `d3d11.dll` and `dxgi.dll`, sentinel, and empty subdirectory are untouched. Config contains exact generated `dxvk.hud` and `dxvk.maxFrameRate` settings.
3. **Reconstruct & update without overrides**: Reconstructs all services from disk. Updates persisted policy intent to `v2.6`. `RequestUpdateByPathAsync(profile)` applies newer version without method overrides. Verifies new `d3d9.dll` bytes, retained older sources, original baseline backups, and actual-versus-selected versions.
4. **Reconstruct, disable features & reapply**: Reconstructs services from disk. Sets `HudEnabled = false` and `FrameLimit = 0`. `RequestReapplyByPathAsync(profile)` reapplies configuration. Verifies `dxvk.conf` presence and ownership hash unconditionally; parsed assignments confirm exact removal of `dxvk.hud` and `dxvk.maxFrameRate` while prefix lookalikes (`dxvk.hudExtra = full`, `dxvk.maxFrameRateTarget = 120`) and user comments/keys are preserved.
5. **Reconstruct & restore**: Reconstructs services from disk. `RequestDisableByPathAsync(profile)` restores game to exact original resource set (files, bytes, empty directories). Preexisting files match original raw bytes (including mixed newlines and lack of trailing newline); absent files are deleted. Unrelated DLLs, sentinel, and empty directories remain intact. Active managed versions are cleared, and historical records/pin/architecture are retained truthfully.

### 2. Negative theory: corrupt or missing payload refusal

Tests both `x32` and `x64` against two failure modes (missing file and corrupted file bytes):
1. **Initial install refusal**: Injects fault into acquired `v2.5` (`d3d9.dll` deleted or overwritten with corrupt bytes). Manager `RequestEnableByPathAsync` refuses before game writes, returns `DxvkActionResult.Failed`, records truthful refusal reason (`"not available locally"`), and leaves game files, empty directories, and available sources completely untouched.
2. **Update refusal with preserved deployment & backups**: Repairs `v2.5` to valid state and completes initial install, creating valid baseline backups. Injects fault into `v2.6` (`d3d9.dll` deleted or corrupted). Manager `RequestUpdateByPathAsync` refuses update before game writes, returning `Failed`. Verifies that existing `v2.5` game deployment, original baseline backups, and available sources remain completely unchanged, and installation remains `Managed` on `v2.5` without false switch or false `Restored` status.

## Execution and boundaries

- Tests execute exclusively via hosted Windows GitHub Actions CI. No local builds, tests, or diagnostic scripts.
- Tests-first policy: production code was inspected and found to already correctly handle the DX9 lifecycle; no production code changes were required.
- Synthetic releases and digests validate local archive extraction and manifest verification; they do not represent independent upstream authenticity claims.
