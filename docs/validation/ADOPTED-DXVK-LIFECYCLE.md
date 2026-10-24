# Adopted official-DXVK persisted lifecycle acceptance — #289

Service-level acceptance coverage for existing-official-DXVK recognition, explicit adoption, update, and disable lifecycle management. This suite verifies that DXVK Companion correctly detects externally placed official DXVK wrappers, adopts them under `UseGlobal` manual policy with synthetic `Missing` baselines, updates and reapplies configuration truthfully based on pre-update state without overwriting baselines, and restores the game directory to its pre-adoption clean state by deleting adopted wrappers upon Disable.

## Scope and model

When DXVK is placed into a game directory externally before Companion is engaged, native DirectX runtime DLLs may have been overwritten or lost prior to Companion's awareness. Under Companion's established safety invariants:
1. **Synthetic Missing baseline**: Adopted wrappers are recorded with `FileOriginalState.Missing` and no native backup. Companion cannot manufacture or restore native binaries that were never backed up. Upon Disable/Restore, adopted wrappers are deleted, returning the game directory to the initial state minus the adopted wrappers.
2. **Metadata-only adoption**: `AdoptExistingAsync` records file ownership in `GameInstallation` without rewriting game binaries, without modifying game files, without taking ownership of untouched configurations, and without creating spurious backup records.
3. **Subsequent configuration management**: When an explicit Update or Reapply introduces configuration management (e.g., enabling HUD or frame limiting), `dxvk.conf` is backed up based on its actual original pre-update bytes (or recorded as `Missing` if originally absent). On Disable, the original configuration is restored byte-identical or removed if originally absent.
4. **Re-adoption refusal**: Once an installation is managed and updated, a redundant `AdoptExistingAsync` attempt is safely refused with a clear diagnostic message, preventing baseline loss or state corruption.
5. **Fresh service reconstruction & offline isolation**: All services are reconstructed from disk at each stage. Fake HTTP transport is marked unavailable after initial acquisition, guaranteeing zero additional network requests across assessment, adoption, update, reapply, and disable.

## Acceptance scenarios

### 1. Successful 8-case lifecycle theory

Tests all eight combinations of API, architecture, and configuration presence:
- DX9 x32 with preexisting mixed-newline config
- DX9 x32 with clean-slate/absent config
- DX9 x64 with preexisting mixed-newline config
- DX9 x64 with clean-slate/absent config
- DX11 x32 with preexisting mixed-newline config
- DX11 x32 with clean-slate/absent config
- DX11 x64 with preexisting mixed-newline config
- DX11 x64 with clean-slate/absent config

Lifecycle stages exercised:
1. **Pre-adoption setup & acquisition**: Acquires official releases `v2.5` and `v2.6` into the shared library with complete x32/x64 DX9 and DX11 payloads, verifying manifests, markers, and request counts. Externally places only the matching older version wrappers (`d3d9.dll` for DX9; `d3d11.dll` and `dxgi.dll` for DX11) into an inert game directory containing `Game.exe`, `sentinel.bin`, and an empty `Data/EmptySubDir`.
2. **Assessment & adoption**: Assesses directory via `AssessExistingDxvk`, confirming `OfficialRelease` status, `CanBeAdopted == true`, and version `v2.5`. Executes `AdoptExistingAsync`. Verifies all game files remain 100% byte-identical, only matching wrappers are owned with `OriginalState == Missing`, untouched config is unowned, and no backup files exist.
3. **Reconstruct & update without overrides**: Reconstructs services from disk. Updates policy to `v2.6` and enables HUD/frame limiting. Applies update via `RequestUpdateByPathAsync`. Verifies newer version wrapper bytes, intact `Missing` DLL baselines, truthful config ownership and backup capture, and unchanged sources.
4. **Reconstruct, disable features & reapply**: Reconstructs services from disk. Disables HUD and frame limiting. Applies reapply via `RequestReapplyByPathAsync`. Verifies unconditional config presence and ownership hash, absence of managed keys (`dxvk.hud`, `dxvk.maxFrameRate`), retention of prefix lookalikes (`dxvk.hudExtra`, `dxvk.maxFrameRateTarget`) and user lines, and unchanged wrapper bytes. Demonstrates that a redundant `AdoptExistingAsync` attempt refuses without mutating game files, baselines, or backups.
5. **Reconstruct & disable normally**: Reconstructs services from disk. Restores via `RequestDisableByPathAsync`. Verifies that adopted wrapper DLLs are deleted (no false native DLL restoration claim), original config is restored byte-identical or removed, sentinels/empty subdirectories are retained, active versions are cleared, and historical records/pin/architecture are preserved.

### 2. Negative refusal theory: invalid wrappers or sources

Tests both DX9 and DX11 across architectures for two core refusal boundaries:
1. **Mixed-version DX11 pair**: Game directory contains `d3d11.dll` matching `v2.5` and `dxgi.dll` matching `v2.6`. `AssessExistingDxvk` recognizes the version conflict and sets `CanBeAdopted == false`. `AdoptExistingAsync` refuses adoption with a descriptive status message.
2. **Corrupted or invalid source metadata**: Game directory contains matching `v2.5` wrappers, but the local acquired source in `Sources/v2.5` has corrupted payload bytes (causing manifest hash verification to fail). `AssessExistingDxvk` refuses eligibility and sets `CanBeAdopted == false`. `AdoptExistingAsync` refuses adoption.

In all refusal cases:
- Game files, available sources, and backups remain completely untouched.
- Profile `DxvkEnabled` remains `false`.
- No files are newly owned or marked `Managed`.
- Zero additional HTTP requests occur.
- No invalid source files are repaired or deleted.

## Execution and boundaries

- Tests execute exclusively via hosted Windows GitHub Actions CI. No local builds, tests, or diagnostic scripts.
- Tests-first policy: existing production logic in `ExistingDxvkDetector`, `DxvkManager`, `DxvkInstaller`, `DxvkRollback`, and `DxvkLocalReleaseLibrary` already satisfies all requirements; production code remains unchanged.
- Synthetic releases and digests validate local archive extraction and manifest verification; they do not represent independent upstream authenticity claims.
