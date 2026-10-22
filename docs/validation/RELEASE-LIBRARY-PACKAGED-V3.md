# Packaged release-library validation v3 — ended with qualified results

**Current status: ENDED.** Acquisition, final applied bytes and exact restoration
were verified, with the sequence qualification and separately approved restore
supplement recorded below. All three cumulative starts are consumed. The profile
instructions and preparation pins are retained as history, not permission to
resume this root or launch again. See [execution disposition](#execution-disposition--october-21-2026).

This is a preparation artifact for [#231](https://github.com/Tiflit/DXVK-Companion/issues/231), following [#159](https://github.com/Tiflit/DXVK-Companion/issues/159). Merging this document or saying “continue” does **not** authorize host execution. Activation requires explicit approval of **release-library-packaged-v3** and current development-laptop availability. The earlier [#156 session ended with qualified results and a stop-rule violation](https://github.com/Tiflit/DXVK-Companion/issues/156#issuecomment-6241077412); it supplies no current readiness or permission to resume its root.

This v3 proposal supersedes [PR #227 / v2 preparation](https://github.com/Tiflit/DXVK-Companion/pull/227) after [the ended v2 and startup diagnostics](https://github.com/Tiflit/DXVK-Companion/issues/156#issuecomment-6327859034). Those roots/start counts and the earlier #156 deviation remain historical and cannot be resumed. [Merged #229](https://github.com/Tiflit/DXVK-Companion/pull/229) adds the explicitly approved `--no-auto-discovery` option; this new package/root/launch-command profile still needs its own execution approval and current readiness.

## Purpose and current coverage

Validate the shipped Windows application's actual older-release acquisition, exact/Latest selection, x32/x64 file deployment, persistence, source retention and restore using synthetic targets. Codex performs approved bounded setup, three exact host-context app starts and file evidence inspection. The human operates and reports the GUI and exits normally. No fixture executable or DXVK DLL is executed/loaded, and the Arc B580 system is not used.

| Requirement | Current cloud/source evidence | What this pass adds or leaves open |
|---|---|---|
| Explicit discovery pause and real activity safety | [#229](https://github.com/Tiflit/DXVK-Companion/pull/229), 20 new startup/options/activity cases | Require visible discovery-off status on every host start. Retain real Running/Unknown refusal; default discovery/classification and on-exit dispatch are not tested by this pass. |
| Official older Windows selection/acquisition | [#195](https://github.com/Tiflit/DXVK-Companion/pull/195), [#204](https://github.com/Tiflit/DXVK-Companion/pull/204), [#206](https://github.com/Tiflit/DXVK-Companion/pull/206) | Real packaged browser/download, independent official-byte comparison; legacy/manual payloads remain uncertified. |
| Latest policy distinct from actual version; explicit older choice | [#211](https://github.com/Tiflit/DXVK-Companion/pull/211), version-selection/lifecycle suites | One synthetic x32 target on exact v3.1, one x64 target on cached Latest v3.1.1, persistence after restart. No update-between-releases pass. |
| Daily checks/restart/failure throttling | [#217](https://github.com/Tiflit/DXVK-Companion/pull/217), [#223](https://github.com/Tiflit/DXVK-Companion/pull/223) | Seeded fresh metadata controls this short run. Live 24-hour scheduling/transport behavior remains cloud-covered. |
| Shared requests and safe publication | [#221](https://github.com/Tiflit/DXVK-Companion/pull/221), [#225](https://github.com/Tiflit/DXVK-Companion/pull/225) | Real package acquisition in one GUI. Two full GUI instances are not launched. Cooperative same-user/path/filesystem limits remain. |
| Local reuse and retention across enable/restart/restore | Acquisition, first-run and offline-release suites; [#225 evidence](https://github.com/Tiflit/DXVK-Companion/pull/225#issuecomment-6326100422) | Keep both real releases byte-identical through apply/restart/restore. Network stays available; do not claim independently proven offline transport or zero HTTP. |
| Partial/corrupt source refusal and no game mutation on failure | Integrity, transaction/ownership, cancellation and Unknown-activity suites | No fault injection/tampering in this run. Unwritable persistence cannot preserve an uncommitted attempt across reconstruction. |
| Device compatibility/performance | Not established by these fixtures | No real game, rendering, driver/GPU/dual-GPU/Arc compatibility or frame-rate acceptance. A separate device profile is needed. |

Main `c5109080fae918fadab3269dc0bdafef197b7cb1` passed application run **39687970329**, attempt 1 (**1,145 tests**), and workflow run **39687970432**. Policy identity: `1fae6e06f94111f67fa1da6df87a43257a37609c`. This closes neither #159 nor #156 on preparation evidence.

## Frozen package and upstream pins

Use the following successful **main** package, not a documentation-PR build. Before setup re-acquire main and require no changes under `src/` since the frozen source; otherwise stop for a revised profile. Documentation-only main movement does not substitute another binary.

| Item | Pin |
|---|---|
| Source / run / attempt | `c5109080fae918fadab3269dc0bdafef197b7cb1` / `39687970329` / `1` |
| Application artifact | `12387158564`, `DXVK-Companion-win-x64`, ZIP size **66,406,801 bytes** |
| Application ZIP SHA-256 | `bb3d7d971f82e632114d34f805e2e6aacf071191e3b9e7371651930326f2f9c6` |
| Provenance artifact | `12387635229`, `build-provenance` |
| Provenance ZIP SHA-256 | `913e5ce0957b008d343fb907e9fab4cc442f3e6a3a9b5061e80c1450642fd546` |
| TRX artifact (review evidence) | `12387690112`, SHA-256 `5cfa9cc69540a64f73fd55d5008bacefaafca90540b4d8cf400cb0061a4286b8` |

Require unexpired artifact IDs/digests, successful run/attempt, and provenance `ref=refs/heads/main`, `run_id=39687970329`, `run_attempt=1`, `head_sha=sha=c5109080fae918fadab3269dc0bdafef197b7cb1`. Verify downloaded ZIP bytes before extraction and record the extracted application executable's size/SHA-256 privately. No rebuild, alternate package, installed SDK or workflow dispatch is permitted. Stop on any mismatch/unavailability.

The following official `doitsujin/dxvk` API metadata was verified during preparation. Re-read `/releases/latest` and both `/releases/tags/<tag>` immediately before setup; require Latest still v3.1.1 and every pinned identity/URL/size/digest unchanged. No automatic repinning.

| Tag | Release ID | Windows asset ID / name | Bytes | Published SHA-256 |
|---|---|---|---:|---|
| v3.1.1 | 381956359 | 566164743 / dxvk-3.1.1.tar.gz | 18,041,512 | `40565b4a724aadc4433fa4e010b4b23916d9b1f1baeee64e17186db94f54e608` |
| v3.1 | 357524779 | 534051057 / dxvk-3.1.tar.gz | 18,056,721 | `30f9cc326874be344285582275446968cfa4c069db31ce56df312d6644179154` |

Exact public download URLs: [v3.1.1](https://github.com/doitsujin/dxvk/releases/download/v3.1.1/dxvk-3.1.1.tar.gz) and [v3.1](https://github.com/doitsujin/dxvk/releases/download/v3.1/dxvk-3.1.tar.gz). Normal GitHub release-asset/CDN redirects are permitted; no mirror/native/Linux/alternate asset, authentication injection or script download. Hash pins establish the selected published bytes, not an upstream signature.

## Host, resources and effects to approve

- **Host:** current development laptop (public alias `DEVELOPMENT_LAPTOP`), normal non-admin interactive Windows session. Human closes real games/launchers and other Companion instances before readiness. No gaming-desktop actions, installed game launches, driver/adapter/security changes or privileges.
- **New root:** a new dedicated directory beneath the approved development root, proposed local name `DXVK-packaged-v3-20261021`. Publish only `<FIXTURE_ROOT>`. It was absent during preparation; recheck and abort if present. Never reuse/delete an old validation root.
- **Layout:** `package/`, `provenance/`, `reference/`, `app/`, `runtime/`, `fixtures/Fixture32/`, `fixtures/Fixture64/`, `evidence/`. All agent-managed downloads/seeds/copies/extraction/inspection stay here. Retain all data.
- **Limits:** maximum **three** application start attempts, including failed/unobserved starts; **45 minutes total** from first setup mutation, **10 minutes per start**, **1 GiB root footprint**. Measure at each boundary and before additional copies; stop before exceeding a cap. Do not reset a budget after failure. This is not an automatic monitoring/continuation authorization.
- **Other runtime effects:** app defaults use existing `%LOCALAPPDATA%/DXVKCompanion/locks` files for same-user coordination. Approval includes this specific program-owned location and normal OS runtime effects; no agent cleanup/permission changes there. Set child-only `DOTNET_BUNDLE_EXTRACT_BASE_DIR=<FIXTURE_ROOT>/runtime` for each launch. Microsoft documents this [single-file extraction override](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview#native-libraries); it does not isolate all Windows/OS caches or prove zero writes outside the root.
- **Discovery and privacy:** every start passes exactly `--no-auto-discovery`. Require the visible **Automatic discovery off** tray status before any action. This prevents passive discovery polling/admission and monitor-driven exit dispatch; it does not change stored policies or disable startup inspection/pending processing, release/update checks or real activity/Unknown/lease safety protections. Relevant activity checks may enumerate host processes and inspect candidate paths; private logs can contain host metadata. Manual plus only the seeded UseGlobal policies controls deployment consent. No isolated-desktop/zero-host-access claim. The agent performs no broad inventory, personal-document search or external game-file baseline. Never publish raw logs, inventories, private JSON, host/account/path IDs or screenshots. Stop immediately if any nonfixture record/notification still occurs; do not whitelist it or continue.
- **Stop on discovery:** the human stays present and checks before each action. On **any nonfixture Game Detected notification, newly recorded real application/game, unexpected external action, security/UAC prompt, hang, lost observation or budget limit**, stop new steps and exit normally. Do not repeat the previous profile's continuation after detection. If a missed earlier detection is found later, record the deviation and stop; do not retrospectively accept compliance. No forced termination or stop-rule bypass; unresolved processes go to the human.

Relevant normal read-only activity inspection/private logging and the program-owned lock location are explicit parts of the requested approval. Passive discovery stays disabled, without disabling real activity guards. If those effects are unacceptable, this host profile stays inactive; a separately designed isolated environment or approved product change is needed.

## Allowed setup and inspection commands

After explicit approval/current readiness, ordinary PowerShell may use `Test-Path`, `New-Item`, `Join-Path`, `Resolve-Path`, `Get-Item`, root-scoped `Get-ChildItem`, `Copy-Item`, `Get-Content`, `Set-Content`, `ConvertTo/From-Json`, `Get-FileHash`, and corresponding bounded `System.IO` reads/writes. Use `gh api` for the pinned artifact/release metadata and binary ZIP downloads, `Invoke-WebRequest` for the two exact unauthenticated reference archives, `Expand-Archive` for verified artifact ZIPs, and installed Windows `tar.exe` for bounded reference listing/extraction. Binary downloads must preserve bytes (a `cmd /c` output redirection may be used solely for the pinned `gh api .../artifacts/<id>/zip` downloads). Do not compose destructive cross-shell commands.

Beyond the stated PowerShell/cmd/gh/tar setup operations and three Codex-operated host-context pinned app starts and human-operated GUI sessions, no additional executable is run. No test suite, new harness, scanner, compilation, install, downloaded-script activation, relay/listener, desktop bridge, firewall/proxy/registry/environment policy change, credential extraction, process suspension/termination, diagnostics export or cleanup. Missing tools block the dependent step; do not install or substitute them. Network remains available, with the app's existing public GitHub Companion-update/latest/catalog/tag/asset requests allowed. Do not click update balloons, download another app version or open unrelated URLs.

Use owned process handles and, if needed, narrowly named `Get-Process -Name DXVK-Companion` plus exact executable-path matching only for launch/exit evidence; do not enumerate unrelated processes. PIDs remain private. The agent cannot infer UI success from process existence; the human reports each UI checkpoint. No unsupported native automation is proposed.

### Independent reference verification

1. Download each pinned upstream archive once into `reference/`, verify exact size and published SHA-256, then retain it. This is separate from the application's own two requested downloads; do not prepopulate `app/DXVK`.
2. List at most 256 archive entries before extraction. Select only regular DLL members matching `dxvk-<numeric tag>/x32/<known DLL>` or `.../x64/<known DLL>`, where known names are d3d8.dll, d3d9.dll, d3d10.dll, d3d10_1.dll, d3d10core.dll, d3d11.dll, dxgi.dll. Reject duplicate names, links/special members, traversal/absolute/colon/backslash paths, unexpected selected layouts, per-DLL size above 128 MiB or total extraction above 512 MiB per release. Require d3d11.dll and dxgi.dll for both fixture architectures.
3. Extract only those exact prevalidated regular members into a fresh per-tag reference directory. Resolve each final destination beneath the root, verify no reparse-point parents, and record relative path/size/SHA-256. If installed tar cannot reliably list types/sizes or safely select members, stop. No unbounded extraction or new parser/tool installation.

### Schema-correct initial state

Before any launch, seed only `app/settings.json`, `app/Cache/cache.json`, `app/Profiles/games.json` and `app/Profiles/game-library.json`, with all absolute target paths resolved privately beneath `<FIXTURE_ROOT>/fixtures`.

- Settings: `GlobalPolicy="Manual"`, `LaunchOnStartup=false`.
- Cache: `Release` containing v3.1.1's exact Version, DownloadUrl, ReleaseId, AssetId, AssetName, AssetSize and `AssetDigest="sha256:<published hash>"`; `CachedAt` and `LastAttemptAt` actual current UTC; `ETag=null`. Require it to remain within the 24-hour window before every start. This controls, rather than tests, live Latest scheduling.
- `games.json` is a **list**, not a dictionary. Two profiles: ExePath to the synthetic placeholder, ExeName matching it, `Api=3` (numeric DX11 for this serializer), Architecture x32/x64, `DxvkEnabled=false`, `DxvkVersion=null`, HudEnabled false, FrameLimit 0.
- Library root: `SchemaVersion=1`, `Installations` list of two records. Each has a new private Id, exact InstallationPath, generic DisplayName, IsHidden false, `ManagementPolicy={Mode:"UseGlobal",PinnedDxvkVersion:null}`, null managed version/architecture, RestorationState/ConflictFlags `"None"`, empty ManagedFiles, null PendingAction/LastCancelledAction/refusal/cancellation data, and current CreatedUtc. Configuration: HudEnabled false, FrameLimit 120, FrameLimitEnabled false. Each Executables list has its relative Game32.exe/Game64.exe path/name, LastKnownApi `"DX11"`, ApiConfidence `"Unknown"`, LastKnownArchitecture x32/x64, empty DetectionEvidence and null LastSeenUtc. No imported host profile or real game path.
- Each target initially contains only its non-PE ASCII placeholder exe (never launched), `keep.bin` sentinel and `dxvk.conf` with a unique generic comment. No original DLLs. Retain **raw baseline copies** and exact relative file-set/size/hash manifests before launch, rather than only summary hashes.

Validate seed JSON syntax, shapes, enum representations, null pending work and all resolved paths against the frozen source before launching. Do not repair a refused or partially committed state during execution.

## Ordered run

Record time/footprint/start count at every boundary. Codex uses the approved host-context execution route (outside the default restricted tool context), first verifying a normal non-admin token. Do not use RunAs, UAC elevation, changed permissions or an alternative context after failure. Use `ProcessStartInfo` for exact `app/DXVK-Companion.exe`, WorkingDirectory `app`, `UseShellExecute=false`, `ArgumentList.Add("--no-auto-discovery")`, redirected stdout/stderr and the single child runtime extraction override. No machine/user environment changes. Count the start before invocation; record the owned handle/private PID and timestamps. Keep the launcher active while the human operates the GUI, for at most ten minutes, using bounded `WaitForExit` intervals and tool yields so the agent can communicate. Retain stdout/stderr privately on natural exit; do not publish raw output or treat an incomplete capture as success.

The human checks the tray promptly and confirms **Automatic discovery off**, Manual and startup-off before any action on **every** start. A process handle/startup log alone is not GUI acceptance. If not visible or the status is absent/ambiguous, stop this run without another context/retry. The default restricted-context launch attempts previously gave inconclusive visibility; do not repeat them. At each boundary inspect only owned records/footprint and stop on nonfixture discovery, pending work, failure or limits. The human exits normally through the tray; verify owned-handle completion and a matching host-context process absence before evidence copies/another approved start. No force termination. If normal exit/observation cannot be established, request human assistance and end new actions; retain the unresolved outcome rather than restarting. No additional helper/compiled harness/background monitor is authorized by this launcher supervision.

1. **Start 1 — acquisition only.** Confirm Automatic discovery off, Manual/startup-off and only synthetic records eligible for actions; stop on nonfixture detection. From Game Details, browse official releases (maximum two catalog pages) and request exactly one download each of v3.1.1 and v3.1, no other version/refresh/retry. Do not Enable, Adopt, Update, Reapply or change game configuration. Record human-observed completed statuses and exit normally.
2. **Stopped verification A.** Compare every published known DLL byte/hash with its corresponding independent archive member. Require complete eligible x32/x64 payloads, marker `1`, manifest file identities and origin IDs/URL/size/digest matching the pins, and manifest archive hash matching the reference archive. Record both complete source inventories/bytes, unchanged fixture baselines, settings and null pending work. If anything fails, stop before deployment. Source hashes now become the retention baseline.
3. **Start 2 — apply two explicit choices.** Confirm Automatic discovery off, Manual and startup-off first. For Fixture32 set Management Policy **Use Global Policy**, DXVK Version **v3.1**, Save Settings, then Enable DXVK once. For Fixture64 keep Use Global Policy and **Latest**, Save Settings, then Enable once. Keep global Manual; do not choose Automatic/PinnedVersion management mode, modify HUD/frame-limit settings or click unrelated actions. Confirm actual recorded versions v3.1/x32 and v3.1.1/x64, success/no pending work; then exit normally.
4. **Stopped verification B.** Require deployed d3d11.dll/dxgi.dll bytes to equal each target's selected reference architecture/version; no d3d9.dll for DX11. Check placeholder/keep.bin and preexisting configuration retention, managed ownership/hash/backup records, target policies (older pin vs null Latest) distinct from actual managed versions, and null pending work. Retain raw applied target copies **and separate applied Profiles/library/Backups snapshots** plus inventories. Both source trees must equal verification A. Stop on any refusal, queue, mismatch or unexpected file; no manual compensation.
5. **Start 3 — persistence and restore.** Confirm Automatic discovery off, both actual versions/architectures and policy choices persisted. Select each synthetic target individually and click **Disable DXVK** once. No Restore All, Update Selected/All, Adopt or Reapply. Confirm success, managed version cleared and no pending work; exit normally.
6. **Stopped verification C.** Both fixture file sets and raw bytes must exactly equal their baseline copies, including dxvk.conf/placeholder/keep.bin; deployed DLLs absent, recorded restoration state appropriate and null managed/pending state. Preserve restoration evidence/backups and both source trees unchanged. Settings remain Manual/startup-off. Retain final raw snapshots, exact exit evidence, timings/start counts, source/backup inventories and measured root footprint. Do not remove packages, logs, runtime files, backups, fixture data or old worktrees.

If any UI action is unavailable, blocked or ambiguous, report the observed blocker and stop. No additional launch/retry/tamper, running-state edits, native/DXVK execution or expanded action is authorized by a passing earlier stage.

## Reporting and activation

Keep complete raw evidence private under the root. Publish one original sanitized report using aliases and public pins: actual stage outcomes, human-observed UI versus independently inspected files, exact package/executable/upstream hashes, raw-baseline/applied/restored equality, policy/actual-version facts, backup/source retention, settings/pending/process/footprint, limits and any stop/deviation. Never paste raw logs, private JSON, paths/account/host IDs, process lists, unrelated screenshots or personal data. Preserve blocked/failed outcomes; no blanket release-ready/real-game/offline/network-isolation claim.

**Historical preparation status at `590e32f`: NOT RUN (then pending explicit profile approval and current host readiness).** No new application/upstream binary was downloaded, fixture seeded, local test/helper/scanner launched or GUI/device run during that preparation. Subsequent execution is recorded below. Earlier roots/results remain preserved separately. Documentation merge grants no additional execution.

Historical suggested activation wording (not reusable after this ended run): **“Approve release-library-packaged-v3, including the non-admin host-context launcher with discovery paused, relevant safety inspection/private logs and the existing AppData lock location. The development laptop is available now; Codex may use the stated non-admin host-context launcher, and I can operate the three GUI sessions.”** [AGENTS.md Local Execution Profiles](../../AGENTS.md#local-execution-profiles) requires authorization for exact host/resources/commands/limits and the user's advance-availability condition.

## Execution disposition — October 21, 2026

Canonical evidence: [stopped-run report and supplement proposal](https://github.com/Tiflit/DXVK-Companion/issues/156#issuecomment-6329760828)
and [final restore report](https://github.com/Tiflit/DXVK-Companion/issues/156#issuecomment-6329846388).
Coordinator transcriptions attribute the owner's approval for later and subsequent
readiness, followed by **“Approve restore supplement; laptop available now”**.
Shared credentials do not independently authenticate a human GitHub identity.

The pinned merged-main package/provenance and both official reference archives
matched their sizes/digests. Extracted executable: **71,769,010 bytes**, SHA-256
`158fa728e298e6e8a04a6102faa1d09f27d7499dfda3d2e250e90fd5057eeb04`.
Codex performed non-admin host-context launches with the exact switch/runtime
override and file inspection; the human confirmed UI settings, operated the GUI
and reported normal exits. Process completion/absence was independently checked.

| Boundary | Evidence and qualification |
|---|---|
| Acquisition | Both releases acquired through the packaged UI; all 20 DLLs matched independent reference bytes, sizes/hashes, origin metadata, manifests and markers. Both targets were still baseline-identical afterward. |
| Applied state | Final x32 exact v3.1 and x64 Latest resolved to v3.1.1. All four deployed d3d11/dxgi DLLs and ownership hashes matched the selected version/architecture. Original three files per target remained unchanged. |
| Sequence deviation | The owner clarified that x32 was first enabled at v3.1.1, then changed to v3.1. Private logs record that extra deployment. Codex stopped before restart/restore; the prescribed single-enable sequence is not retroactively certified. |
| Approved supplement | The owner separately approved only the final persistence/Disable step, without resetting the original cumulative limits. Human-reported persisted choices and successful Disable were followed by independent file/state verification. |
| Exact restoration | All six original target files, raw bytes and relative file sets matched retained baselines; deployed DLLs absent. Both records Restored, active managed/per-file versions cleared, no pending work/refusal. |
| Retained history | Historical architecture/ownership/hash records and legacy profile last-version strings remain as implemented; both legacy profiles are disabled. Do not describe every historical managed-related field as null. |
| Retention and limits | All 24 release-source files remained byte-identical through the run. Three starts, exit code 0 each; recorded durations 180/104/66 seconds. Final verification at 16.76 minutes. Highest measured footprint checkpoint 510,704,287 bytes; final verification checkpoint 483,749,688 bytes, below 1 GiB. |

Raw baseline/applied/restored targets, separate applied/final Profiles/library/
Backups state, source snapshots and private launch/exit/log evidence are retained.
Original DLLs were initially absent, so original-DLL backup files were zero
before/after; retaining that empty state does not test overwritten-original DLL
recovery. Existing dxvk.conf stayed unchanged; changed-config recovery was not
exercised. A read-only inspection initially concatenated expected file names in
a PowerShell expression; the corrected comparison matched the actual five files
per applied fixture. No product/fixture mutation was used to repair that check.

**Disposition:** useful packaged acquisition/selection/architecture-specific byte,
reported restart-persistence, exact restoration and source-retention evidence,
with the explicit sequence deviation and approved supplement. No blanket release,
default-discovery, real-game/driver/GPU, changed-config/original-proxy recovery,
daily-scheduler, offline-transport or full two-GUI concurrency acceptance. No
observed nonfixture stored records in v3. All resources remain private/retained;
no cleanup or fourth start is authorized. Earlier session deviations remain
historical. #156/#159 remain open for disposition/remaining acceptance; #94 paused.
