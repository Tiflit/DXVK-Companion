# Adopted official-DXVK persisted lifecycle acceptance — Gemini, 2026-10-24

Task [#289](https://github.com/Tiflit/DXVK-Companion/issues/289), base main `c4c8e409aeb1624fcdde221da952d0425f87d8a9`, policy `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9`. Attributed coordinator transcription of owner instruction following merge of #288: "Merged / Continue". Owner prioritizes complete standalone DXVK functionality/tests and defers UI polish.

Implemented service-level acceptance coverage in `tests/DXVKCompanion.PhaseA.Tests/AdoptedDxvkLifecycleAcceptanceTests.cs` for existing-official-DXVK recognition, explicit adoption, update, and disable lifecycle management. Created documentation `docs/validation/ADOPTED-DXVK-LIFECYCLE.md`.

## Implementation Details

1. **8-Case Successful Lifecycle Theory (`AdoptedDxvkLifecycle_PersistedManual_AdoptsUpdatesDisablesCleanly`)**:
   - Covers DX9 and DX11 across `x32` and `x64` architectures, both with and without preexisting mixed-newline configuration.
   - Pre-adoption game setup: inert `Game.exe`, sentinel file, meaningful empty subdirectory (`Data/EmptySubDir`), and externally placed matching older wrappers (`d3d9.dll` for DX9; `d3d11.dll` and `dxgi.dll` for DX11). Unrelated wrapper DLLs are excluded to adhere to recognizer semantics.
   - Preexisting configuration contains mixed newlines (`\r\n` and `\n`), no final newline, user settings, and prefix lookalikes (`dxvk.hudExtra = full`, `dxvk.maxFrameRateTarget = 120`).
   - Official releases `v2.5` and `v2.6` acquired into the shared source library with verified manifests, markers, and request accounting.
   - Fake transport marked unavailable (`RejectAllRequests = true`) after initial acquisition, guaranteeing zero additional network requests across all subsequent operations.
   - Initial adoption: executed via real manager assessment (`AssessExistingDxvk`) and `AdoptExistingAsync`. Verifies:
     - All game files remain 100% byte-identical immediately after adoption.
     - Profile is enabled (`DxvkEnabled == true`).
     - Adopted DLLs are recorded with synthetic `Missing` original states and null backup relative paths; no native backups are created.
     - Untouched configuration is unowned during adoption (`ManagedFiles` contains only adopted DLLs).
     - No spurious backup files are created.
   - Update without overrides: reconstructed from disk. Updated to `v2.6` with HUD and frame limiting enabled. Normal manager update entry point (`RequestUpdateByPathAsync`) deploys newer version bytes, retains `Missing` DLL baselines, takes ownership of configuration, backs up preexisting configuration truthfully based on original bytes, and preserves sources.
   - Reapply & re-adoption refusal: reconstructed from disk. Reapplies with disabled features (`HudEnabled = false`, `FrameLimit = 0`). Verifies unconditional configuration presence and ownership hash, absence of managed keys, and preservation of lookalikes. Demonstrates that a redundant `AdoptExistingAsync` call after an owned update is safely refused with `"Use reapply instead of adoption"`, without mutating game files, baselines, or backups.
   - Disable normally: reconstructed from disk. Restores via `RequestDisableByPathAsync`. Verifies adopted wrapper DLLs are deleted (initial game set minus adopted wrappers), original config is restored byte-identical or removed, sentinels/empty subdirectories remain intact, active managed versions are cleared, and historical records/pin/architecture are preserved.

2. **5-Case Negative Refusal Theory (`AdoptedDxvkLifecycle_RefusesInvalidWrappersOrSourcesBeforeAdoption`)**:
   - Evaluates DX9 and DX11 across architectures for:
     - Mixed-version DX11 pair (`d3d11.dll` from `v2.5` and `dxgi.dll` from `v2.6`).
     - Corrupted source payload in `Sources/v2.5` (manifest SHA-256 verification fails).
   - Verifies `AssessExistingDxvk` recognizes the conflict/invalidity and reports `CanBeAdopted == false`.
   - Verifies `AdoptExistingAsync` refuses adoption with a descriptive status message.
   - Asserts game files, available sources, and backups remain completely untouched, profile `DxvkEnabled` remains `false`, no files are marked `Managed`, and zero additional HTTP calls occur.

## Verification & Execution Record

- **Tests First Policy**: Production code was inspected and verified to already correctly satisfy all adoption, update, and disable invariants; production code remains untouched.
- **Execution Mode**: Cloud-only hosted CI. No local builds, tests, or diagnostic scripts.
