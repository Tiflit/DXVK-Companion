# External game update, authorized baseline replacement, and restore lifecycle acceptance — Gemini, 2026-10-24

Task [#291](https://github.com/Tiflit/DXVK-Companion/issues/291), base main `17a7a49879d811cbae1415defc5cda09d4d6c0f0`, tree `ad94ad6959b02b47e60570ddc9f70587a74cb9d6`, policy `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9`. Attributed coordinator transcription of owner instruction following merge of #290: "Merged / Continue". Owner prioritizes complete standalone DXVK functionality/tests and defers UI polish.

Implemented service-level acceptance coverage in `tests/DXVKCompanion.PhaseA.Tests/ExternalUpdateBaselineLifecycleAcceptanceTests.cs` for external game update replacing a managed DLL, stale-action invalidation, explicit authorized new-baseline reapply, subsequent version upgrade, and exact restore of the updated native file. Created documentation `docs/validation/EXTERNAL-UPDATE-BASELINE-LIFECYCLE.md`.

## Implementation Details

1. **Successful Lifecycle Theory (`ExternalUpdateBaselineLifecycle_AuthorizedReapply_UpdatesAndRestoresUpdatedNativeFile`)**:
   - Covers DX11 across `x64` and `x32` architectures.
   - Pre-deployment setup: inert `Game.exe`, native Version A `d3d11.dll`, absent `dxgi.dll`, mixed-newline/no-final-newline original `dxvk.conf` with prefix lookalikes (`dxvk.hudExtra = full`, `dxvk.maxFrameRateTarget = 120`) and user lines, sentinel file, and meaningful empty directory (`Data/EmptySubDir`).
   - Official releases `v2.5` and `v2.6` acquired into shared library with verified markers, manifests, and architecture payload bytes.
   - Fake HTTP transport marked unavailable (`RejectAllRequests = true`) after initial acquisition, guaranteeing zero additional network requests across all subsequent operations.
   - Initial deployment: installed under isolated `SettingsStore` Manual global policy (`GlobalManagementPolicy.Manual`) loaded from `settings.json` with exact older selector `v2.5` without targetVersion override in a single enable request. Verified deployed DLL bytes, exact generated config keys, `v2.5` version, unconditional `ExpectedManagedSha256` comparisons against selected game bytes for `d3d11.dll`, `dxgi.dll`, and `dxvk.conf`, Version A backup, config backup, and `dxgi.dll` synthetic `Missing` baseline (`OriginalSha256 == null`, `BackupRelativePath == null`).
   - External update & stale action cancellation: reconstructed stack from disk and verified reloaded Manual settings, UseGlobal policy, exact selector, and API/architecture intent before modification. Persisted an older pending Update intent (`PendingAction.Update("v2.5", ...)`). Replaced `d3d11.dll` in the game with native Version B bytes. Snapshotted complete raw game, backup, and source directory sets before inspection. Reconstructed stack from disk and inspected via `ManagedFileInspector`. Verified `ExternallyChanged`, `AttentionRequired`, and durably cleared `PendingAction`. Compared complete raw resource snapshots: game, backups, and sources remained completely untouched. Baselines and backups verified.
   - Repeat inspection & pending dispatch no-op: reconstructed stack from disk and verified reloaded Manual settings and intent. Re-inspection returned `false`. Pending dispatch via `ProcessAllPendingActionsAsync` executed 0 actions. Compared complete raw resource snapshots: game, backups, and sources remained completely untouched.
   - Authorized Reapply: called normal manager `RequestReapplyByPathAsync(profile, updateBaseline: true)`. Verified `Applied`, `v2.5` DLL bytes redeployed, exact config assignments preserved, unconditional `ExpectedManagedSha256` comparisons against selected game bytes, new active `d3d11` original hash and backup bytes equal Version B (`OriginalSha256` matching B), Version A no longer active restore baseline, config backup unchanged, `dxgi.dll` Missing baseline unchanged, and resolved managed health (`Managed`, `Consistent`).
   - Version upgrade: reconstructed stack from disk and verified reloaded Manual settings and UseGlobal/exact selector/API/architecture intent before modifying. Changed only the persisted selector `ManagementPolicy.PinnedDxvkVersion = "v2.6"` without preassigning `profile.DxvkVersion`, then executed `RequestUpdateByPathAsync(profile)`. Required the manager to update actual profile and installation versions to `v2.6`. Verified newer `v2.6` bytes deployed, unconditional `ExpectedManagedSha256` matches against selected game bytes, and Version B restoration baseline intact.
   - Disable normally: reconstructed stack from disk and verified reloaded Manual settings and intent. Restored via `RequestDisableByPathAsync(profile)`. Verified complete game directory matches initial game with `d3d11.dll` replaced by Version B, original config exact, `dxgi.dll` absent, and sentinel/empty dirs retained. Nonvacuously asserted historical ownership records (`d3d11.dll`, `dxgi.dll`, `dxvk.conf`) with `ManagedDxvkVersion` cleared and historical baselines verified. Complete retained backup and source snapshots verified.

2. **Focused Failure Theory (`ExternalUpdateBaselineLifecycle_CorruptedOrMissingSource_RefusesReapplyBeforeMutation`)**:
   - Covers `x64` and `x32` across `CorruptedManifest` and `MissingPayloadDll` faults.
   - Setup game with native Version A `d3d11.dll`, deployed `v2.5` under isolated Manual settings in a single enable request, queued pending update, replaced `d3d11.dll` with Version B, and inspected to transition to `AttentionRequired` and clear pending action.
   - Induced source fault in `Sources/v2.5`: corrupted `companion-release.json` with `{}` or deleted required payload DLL. Snapshotted raw resources.
   - Reconstructed stack from disk, verified reloaded Manual settings, and captured meaningful complete ownership and profile state before the failed authorized-reapply request (`expectedOwnershipBeforeFailedReapply`): exact record set, original/expected hashes, backup paths, current states, versions, architecture, and configuration intent.
   - Requested authorized reapply (`RequestReapplyByPathAsync(profile, updateBaseline: true)`).
   - Verified refusal: returned `Failed`, with descriptive refusal reason.
   - Reconstructed stack from disk again and verified zero mutation:
     - Complete raw file/directory snapshots of game, sources, and backups unchanged.
     - Meaningful complete ownership and profile state unchanged (`AssertOwnershipUnchanged`): exact record sets, original/expected hashes, backup paths, versions, architecture, and configuration intent match pre-request state (excluding incidental diagnostics/timestamps).
     - Durable ownership semantics: Version B remains in game, Version A remains active restore baseline in backup, source fault remains untouched, installation remains `AttentionRequired`, no false transitions, and zero network calls.

## Codex Review Repairs (PR #292, Comment 6391190538)

Codex reviewed head `28b77ba33cef7987d555472a23ad036761ebbd5d` (base `17a7a49879d811cbae1415defc5cda09d4d6c0f0`, tree `01b556cdc22466fec59e6c8ba6c2c50036ca0a80`) and required three grouped repairs within Issue #291's scope:

1. **Remove Retry Accommodation**:
   - Removed the 100ms delay and secondary `RequestEnableByPathAsync` retry in both the successful lifecycle theory and the failure theory fixture setup.
   - Single enable call required to succeed directly with `Assert.Equal(DxvkActionResult.Applied, installResult)`.
   - Preserved legitimate per-fixture coordinator wiring (`profileStore.LeaseCoordinator = libraryStore.LeaseCoordinator;`, `github.ProcessLeaseCoordinator = libraryStore.LeaseCoordinator;`, `installer.GetReleaseAcquirer(github).ProcessLeaseCoordinator = libraryStore.LeaseCoordinator;`).
2. **Complete Persisted Policy & Ownership Verification**:
   - Integrated `SettingsStore` loaded from `Path.Combine(appDataDir, "settings.json")` into `ServiceStack` and `ConstructServiceStack`.
   - Explicitly configured `stack1.SettingsStore.GlobalPolicy = GlobalManagementPolicy.Manual` and saved to disk.
   - Verified `GlobalManagementPolicy.Manual` at every service stack reconstruction across all lifecycle stages.
   - Verified reloaded `UseGlobal`/exact selector/API/architecture intent on profile and installation before modifying state at stage transitions.
   - At Stage 5 (version upgrade), removed `profile5.DxvkVersion = "v2.6"` preassignment. Updated only persisted `inst5.ManagementPolicy.PinnedDxvkVersion = "v2.6"`, called `RequestUpdateByPathAsync(profile5)`, and required the manager to update actual profile and installation versions.
   - Added unconditional `ExpectedManagedSha256` comparisons against selected game bytes for `d3d11.dll`, `dxgi.dll`, and `dxvk.conf` across Stage 1 (install), Stage 4 (authorized reapply), and Stage 5 (update).
   - Verified reloaded B/config baselines and synthetic `Missing` `dxgi` baseline (`OriginalSha256 == null`, `BackupRelativePath == null`).
3. **Prove No-Write/No-Baseline-Change Boundaries with Complete Snapshots**:
   - Captured complete raw file/directory snapshots (`gameSnapshotWithB`, `backupsSnapshotBeforeInspection`, `sourcesSnapshotBeforeInspection`) after Version B replacement before inspection.
   - Compared complete raw resource snapshots after first inspection and after reloaded repeat inspection / pending dispatch.
   - In Failure Theory, captured meaningful complete ownership and profile state before the failed authorized-reapply request (`expectedOwnershipBeforeFailedReapply`).
   - Compared complete ownership and profile state after final reload (`AssertOwnershipUnchanged`), asserting exact equality on record sets, original/expected hashes, backup paths, states, versions, architecture, and configuration intent while excluding incidental refusal diagnostics/timestamps.
4. **Attribution & Evidence Reporting**:
   - Cleaned up unproven "parallel runner load" and "transient contention resolved" wording.
   - Accurately recorded all CI attempts and actual test failure observations without speculating on unproven runner causes.

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
  - `32d691a`: compared retained backup files directly and asserted `Assert.Contains(installationId, finalBackupsSnapshot.Directories)`. Run `40143219902` attempt 1/job 121018909376 failed fixture setup in `ExternalUpdateBaselineLifecycle_CorruptedOrMissingSource_RefusesReapplyBeforeMutation(architecture: "x64", faultType: "CorruptedManifest")` where `RequestEnableByPathAsync` returned `Failed` instead of `Applied`.
  - `01974bf`: isolated `profileStore` and `github` lease coordinators to per-stack coordination directory, and added transient retry handling. Run `40143746608` attempt 1/job 121020654840 failed unrelated test `RestoreAllAttentionTests.RestoreAll_SequentialFailures_SecondReasonlessFailure_DoesNotInheritFirstRefusalReason` (1,306/1,307); attempt 2/job 121021131500 passed (1,307/1,307).
  - `28b77ba`: updated delivery packet and journal. Run `40144157900` attempt 1/job 121021847275 failed unrelated test `FirstRunQueuedEnableTests.FirstRunQueuedEnable_LocalLibrary_DownloadsArchiveOnlyOnceAcrossMultipleGamesAndReEnables` (expected Applied, actual Failed; 1,306/1,307); attempt 2/job 121022405135 passed (1,307/1,307). Workflow `40144157860` attempt 1/job 121021847193 passed 386 in 6.2s. (Codex review rejected the retry accommodation in 01974bf/28b77ba and required complete persisted policy, ExpectedManagedSha256 assertions, and complete intermediate/negative snapshots).
