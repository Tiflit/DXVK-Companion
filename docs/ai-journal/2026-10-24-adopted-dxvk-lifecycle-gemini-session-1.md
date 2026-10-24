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
- **Attempt 1 (Commit `f5b9a28882004ee0db5d00eabf8945cce61f19cb`)**:
  - Run 40130543307 (`build-and-test`): Passed, 1,300/1,300 tests passed (0 failed, 0 skipped).
  - Workflow 40130543264 (`Run Python workflow tests`): Passed, 386 passed.
  - Workflow 40130543272 (`PR hygiene`): Passed.
  - Workflow 40130543278 (`AI task scope`): Passed.
  - PR #290 submitted for Codex review.

## Codex Grouped Review & Repair Cycle

Codex review on PR #290 requested three specific assertion tightenings:
1. **Acquired-source eligibility boundary after genuine reload**:
   - Explicitly verify valid manifests, markers, and expected acquired payloads for releases `v2.5` and `v2.6` across `x32` and `x64` before fault induction.
   - Assert positive control (`CanBeAdopted == true`, `OfficialRelease`) before mutating source metadata.
   - Refusal matrix expanded to 6 cases: 2 mixed-version DX11 pairs + 4 invalid manifest cases (`(DX9, x32)`, `(DX9, x64)`, `(DX11, x32)`, `(DX11, x64)`).
   - Invalidate manifest (`companion-release.json` corrupted with `{}`) while keeping DLL payload bytes intact. Prove raw hash recognizer (`ExistingDxvkDetector`) still succeeds on raw hashes, while real manager assessment on a fresh disk reload (`stack2 = ConstructServiceStack(...)`) refuses adoption with `RecognizedUnknownVersion` and evidence `"Local acquired source verification failed; no verified official identity is available."`.
   - Perform a third reload (`stack3 = ConstructServiceStack(...)`) to inspect durable refusal state and confirm zero side effects on game, source, or backup files.
2. **Feature transition and re-adoption no-write guarantee**:
   - Assert exact enabled configuration properties on `GameInstallation.Configuration` (`HudEnabled == true`, `FrameLimit == 60`, `FrameLimitEnabled == true`).
   - Parse `dxvk.conf` on disk and assert exact keys and values (`dxvk.hud == "fps,devinfo"`, `dxvk.maxFrameRate == "60"`), verifying lookalikes (`dxvk.hudExtra`, `dxvk.maxFrameRateTarget`) and user lines are retained.
   - After Reapply, assert newer version wrapper bytes, owned DLL record hashes/baselines, and original config backup retention.
   - Around the refused second Adopt, snapshot complete raw game, source, and backup directories as well as durable installation and profile records before and after across a reload, asserting zero byte or metadata mutation.
3. **Complete and nonvacuous final resource and history assertions**:
   - Construct `expectedFinalSnapshot` as `initialGameSnapshot` minus adopted wrapper DLLs, and assert exact disk match via `AssertResourcesUnchanged(expectedFinalSnapshot, gameDir)`.
   - Assert exact retained managed files set (`new[] { "d3d9.dll", "dxvk.conf" }` for DX9; `new[] { "d3d11.dll", "dxgi.dll", "dxvk.conf" }` for DX11).
   - Nonvacuously verify each record: `ActiveVersion` cleared (null or empty), `OriginalState` verified, `RelativeBackupPath` verified, and `OriginalHash` non-empty.
   - Compare complete retained backup snapshot against disk (`AssertResourcesUnchanged(expectedFinalBackupsSnapshot, backupsDir)`).
