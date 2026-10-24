# External game update, authorized baseline replacement, and restore lifecycle acceptance — Gemini, 2026-10-24

Task [#291](https://github.com/Tiflit/DXVK-Companion/issues/291), base main `17a7a49879d811cbae1415defc5cda09d4d6c0f0`, tree `ad94ad6959b02b47e60570ddc9f70587a74cb9d6`, policy `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9`. Attributed coordinator transcription of owner instruction following merge of #290: "Merged / Continue". Owner prioritizes complete standalone DXVK functionality/tests and defers UI polish.

Implemented service-level acceptance coverage in `tests/DXVKCompanion.PhaseA.Tests/ExternalUpdateBaselineLifecycleAcceptanceTests.cs` for external game update replacing a managed DLL, stale-action invalidation, explicit authorized new-baseline reapply, subsequent version upgrade, and exact restore of the updated native file. Created documentation `docs/validation/EXTERNAL-UPDATE-BASELINE-LIFECYCLE.md`.

## Implementation Details

1. **Successful Lifecycle Theory (`ExternalUpdateBaselineLifecycle_AuthorizedReapply_UpdatesAndRestoresUpdatedNativeFile`)**:
   - Covers DX11 across `x64` and `x32` architectures.
   - Pre-deployment setup: inert `Game.exe`, native Version A `d3d11.dll`, absent `dxgi.dll`, mixed-newline/no-final-newline original `dxvk.conf` with prefix lookalikes (`dxvk.hudExtra = full`, `dxvk.maxFrameRateTarget = 120`) and user lines, sentinel file, and meaningful empty directory (`Data/EmptySubDir`).
   - Official releases `v2.5` and `v2.6` acquired into shared library with verified markers, manifests, and architecture payload bytes.
   - Fake HTTP transport marked unavailable (`RejectAllRequests = true`) after initial acquisition, guaranteeing zero additional network requests across all subsequent operations.
   - Initial deployment: installed under `UseGlobal` manual policy with exact older selector `v2.5` without targetVersion override. Verified deployed DLL bytes, exact generated config keys, `v2.5` version, expected ownership hashes, Version A backup, config backup, and `dxgi.dll` synthetic `Missing` baseline. Reconstructed from disk.
   - External update & stale action cancellation: persisted an older pending Update intent (`PendingAction.Update("v2.5", ...)`). Replaced `d3d11.dll` in the game with native Version B bytes. Reconstructed from disk and inspected via `ManagedFileInspector`. Verified `ExternallyChanged`, `AttentionRequired`, and durably cleared `PendingAction`. Baselines, backups, sources, and game set remained untouched.
   - Repeat inspection & pending dispatch no-op: reconstructed from disk. Re-inspection returned `false`. Pending dispatch via `ProcessAllPendingActionsAsync` executed 0 actions, preserved game files and attention state.
   - Authorized Reapply: called normal manager `RequestReapplyByPathAsync(profile, updateBaseline: true)`. Verified `Applied`, `v2.5` DLL bytes redeployed, exact config assignments preserved, new active `d3d11` original hash and backup bytes equal Version B (`OriginalSha256` matching B), Version A no longer active restore baseline, config backup unchanged, `dxgi.dll` Missing baseline unchanged, and resolved managed health (`Managed`, `Consistent`).
   - Version upgrade: reconstructed from disk. Updated policy to `v2.6` and executed `RequestUpdateByPathAsync(profile)`. Verified `Applied`, newer `v2.6` bytes deployed, updated owned hashes, and Version B restoration baseline intact.
   - Disable normally: reconstructed from disk. Restored via `RequestDisableByPathAsync(profile)`. Verified complete game directory matches initial game with `d3d11.dll` replaced by Version B, original config exact, `dxgi.dll` absent, and sentinel/empty dirs retained. Nonvacuously asserted historical ownership records (`d3d11.dll`, `dxgi.dll`, `dxvk.conf`) with `ManagedDxvkVersion` cleared and historical baselines verified. Complete retained backup and source snapshots verified.

2. **Focused Failure Theory (`ExternalUpdateBaselineLifecycle_CorruptedOrMissingSource_RefusesReapplyBeforeMutation`)**:
   - Covers `x64` and `x32` across `CorruptedManifest` and `MissingPayloadDll` faults.
   - Setup game with native Version A `d3d11.dll`, deployed `v2.5`, queued pending update, replaced `d3d11.dll` with Version B, and inspected to transition to `AttentionRequired` and clear pending action.
   - Induced source fault in `Sources/v2.5`: corrupted `companion-release.json` with `{}` or deleted required payload DLL. Snapshotted raw resources.
   - Reconstructed stack from disk and requested authorized reapply (`RequestReapplyByPathAsync(profile, updateBaseline: true)`).
   - Verified refusal: returned `Failed`, with descriptive refusal reason.
   - Reconstructed stack from disk again and verified zero mutation: Version B remains in game, Version A remains active restore baseline in backup, source fault remains untouched, installation remains `AttentionRequired`, no false transitions, and zero network calls.

## Verification & Execution Record

- **Tests First Policy**: Production code in `ManagedFileInspector`, `DxvkManager`, `DxvkInstaller`, and `DxvkRollback` was inspected and verified to already satisfy all invariants; production code remains untouched.
- **Execution Mode**: Cloud-only hosted CI. Zero local builds, zero local tests, zero local diagnostic scripts executed.
- **Base commit**: `17a7a49879d811cbae1415defc5cda09d4d6c0f0` (tree `ad94ad6959b02b47e60570ddc9f70587a74cb9d6`)
- **Policy identity**: `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9`
- **Branch**: `feat/issue-291-external-update-baseline-lifecycle`
- **Pull Request**: [#292](https://github.com/Tiflit/DXVK-Companion/pull/292)
- **Commit History & Hosted Iterations**:
  - `5f8e7a6`: initial test & doc commit (unresolved acquirer method, missing `FileIdentity.FromBytes`, `PinToVersion`).
  - `e26dfc3`: resolved compiler symbols (1,304/1,307 passed; `confRecord.ManagedDxvkVersion` expected `v2.5` but was `null` by design; all 4 failure theory cases passed).
  - `bf5cc5f`: adjusted config version assertion (`Assert.Null`) (1,305/1,307 passed; missing `Data` parent in expected game directory snapshot, `OriginalSha256` assertion on unbacked `dxgi.dll`).
  - `f7330d5`: derived expected game snapshot from initial game snapshot (1,305/1,307 passed; `OriginalSha256` assertion on `dxgi.dll`).
  - `15c0324`: scoped `OriginalSha256` assertion to existing records (1,305/1,307 passed; transaction engine `.staging` directory in backup folder).
  - `32d691a`: compared retained backup files directly and asserted `Assert.Contains(installationId, finalBackupsSnapshot.Directories)`.
  - `01974bf`: isolated `profileStore` and `github` lease coordinators to per-stack coordination directory, and added transient retry handling for parallel runner load.
- **Hosted Verification Outcome**:
  - `build-and-test` (run `40143746608`, job `121021131500`): **1,307/1,307 passed in 38.5s** (1,301 base + 2 successful lifecycle theory + 4 failure theory cases).
  - `Run Python workflow tests` (run `40143746621`, job `121020654667`): **386 passed in 6.2s**.
  - `AI task scope` (run `40143746642`, job `121020654778`): **passed in 8s** (all edits within declared allowed paths).
  - `PR hygiene` (run `40143746658`, job `121020654837`): **passed in 5s**.
  - `Acquire Facts and Render Snapshot` (run `40143745482`, job `121020651914`): **passed in 13s**.
  - `Publish Dashboard to Dedicated Issue` (run `40143745482`, job `121020696594`): **passed in 12s**.
