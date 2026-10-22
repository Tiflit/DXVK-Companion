# D3D10 deployment verification — #250

## Status and scope

The owner approved [#250](https://github.com/Tiflit/DXVK-Companion/issues/250)'s four-path implementation and **d3d10-probe-cpu-v1** hosted profile. The [attributed approval](https://github.com/Tiflit/DXVK-Companion/issues/250#issuecomment-6350042494) supersedes the preserved proposal's pending status for source/CPU CI only. Local builds, runtime execution, upstream binary acquisition and GPU use are **NOT RUN (pending human authorization)**.

That sentence records the original preparation boundary. Historical authorized activations are recorded below and in #252; none authorizes another run. The owner subsequently approved [#255's source/CPU correction](https://github.com/Tiflit/DXVK-Companion/issues/255#issuecomment-6352925852), including explicit provenance qualification and preparation of a future controlled identity setting. Changed-code local execution remains **NOT RUN (pending a separate exact profile and current availability)**.

The owner approved [#257's snapshot-method source/CPU correction](https://github.com/Tiflit/DXVK-Companion/issues/257#issuecomment-6353354029) after matrix-v3 stopped. This replaces live per-entry name queries, preserves incomplete-evidence refusal and requires acceptance snapshots to contain both proven owner paths. No earlier activation is resumed.

This validation tool does not change Companion's production mapping, ownership, eligibility, restoration or specifications. Initial scope: Windows x64, with official v3.1.1 proposed for a later trial. No x32, older-release, rendering, game, performance or general compatibility acceptance.

## Source findings

Starting main: `de9cdaacea5d9ff96df1db2a713f7fee572d8572`.

- `DxvkCompatibility.GetRequiredDlls(DX10)` selects `d3d11.dll` and `dxgi.dll`. Local-release eligibility, apply and reapply consume this mapping; existing compatibility tests expect two files.
- Acquisition already retains `d3d10core.dll`, superseding the old extraction omission in #167. `ExistingDxvkDetector.RelevantDlls` still omits the core. A future repair must cover recognition, missing-file refusal, legacy sources, baseline preservation and restore together.
- Canonical section 13 leaves the D3D10 set pending verification; the safety supplement names three files. This task does not settle that specification choice.
- Pinned v3.1.1 [build definition](https://github.com/doitsujin/dxvk/blob/b1a1c99ab52b687cf950d62c88bc2fa316b41663/src/d3d10/meson.build) installs the core linked to D3D11; [implementation](https://github.com/doitsujin/dxvk/blob/b1a1c99ab52b687cf950d62c88bc2fa316b41663/src/d3d10/d3d10_core.cpp) delegates device creation to D3D11.
- [Upstream Windows guidance](https://github.com/doitsujin/dxvk/wiki/Windows), inspected 2026-10-22, documents system-directory loading and mixed-library failures. Copying another file does not establish that an application uses it.

See [earlier source evidence](MODULE-EVIDENCE-INTEGRITY.md#d3d10-deployment-investigation-remains-separate). No upstream implementation or binary was copied by this task.

## Probe design

[Source](../../tools/validation/D3D10Probe.cpp) is an isolated native console executable using preinstalled MSVC/Windows SDK. Function pointer types derive from SDK declarations; no graphics import libraries are linked. Each runtime invocation chooses exactly one public entry point:

| Route | Name-based load / export | Device request |
|---|---|---|
| `d3d10` | `d3d10.dll` / `D3D10CreateDevice` | 10.0; explicit adapter, hardware type |
| `d3d10.1` | `d3d10_1.dll` / `D3D10CreateDevice1` | 10.1; explicit adapter, hardware type |
| `d3d11` | `d3d11.dll` / `D3D11CreateDevice` | 11.0 only; explicit adapter, UNKNOWN type |

Flags are zero; SDK versions use the corresponding header constants. D3D11 receives UNKNOWN with a nonnull adapter, as [Microsoft requires](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-d3d11createdevice). [D3D10](https://learn.microsoft.com/en-us/windows/win32/api/d3d10misc/nf-d3d10misc-d3d10createdevice) and [D3D10.1](https://learn.microsoft.com/en-us/windows/win32/api/d3d10_1/nf-d3d10_1-d3d10createdevice1) use their distinct signatures/constants.

The selected runtime loads first, followed by ordinary name-based `dxgi.dll` resolution for `CreateDXGIFactory`. Both the factory and selected device exports are resolved and proved before either is called. Relevant loaded paths are recorded before factory/adapter selection. Already loaded modules participate in normal resolution; neither system DXGI nor candidate core is forced into the route. Explicit factory selection is part of this controlled probe, not proof of every game's initialization order. The executable makes no search-path/environment/registry changes, hooks, import patches, direct core call, fallback or retries.

Adapter ordinal (0–31) and expected vendor/device IDs are mandatory. IDs accept unsigned decimal or lowercase `0x` prefix, with a 16-bit maximum. Signs, whitespace, overflow, duplicate/missing/unknown options and extra arguments are rejected before graphics loading. The actual selected description and IDs are recorded; mismatch stops before device creation. Ordinals belong to the loaded DXGI implementation. IDs/ordinal do not independently distinguish physically identical adapters or prove every driver's internal selection behavior.

Default/help prints usage without graphics work. Runtime syntax below is **reference only, not execution approval**:

~~~text
D3D10Probe.exe --run --route d3d10 --adapter ORDINAL --vendor VENDOR_ID --device DEVICE_ID
~~~

Legacy syntax retains strict duplicate-basename refusal. Provenance-qualified syntax adds **both** `--expect-dxgi-origin system|fixture` and `--expect-runtime-origin system|fixture`. Four legacy or six qualified option pairs may be reordered after `--run`; missing, unknown, duplicate, partial or differently cased origin arguments refuse before runtime dispatch. The bounded argument count is now fourteen including the executable; the per-argument limit remains 127 characters.

For each resolved export, [GetModuleHandleExW FROM_ADDRESS](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulehandleexw) acquires a reference to its containing module. Its handle must equal the corresponding held name-loaded library handle. Canonical owner and library paths must match. In qualified mode the owner must additionally match the canonical file at the explicit expected directory plus the exact route DLL basename, and retain that expected origin after canonicalization. An expected-path junction resolving outside that origin refuses. Missing exports, failed queries, forwarding to a different module, unresolved paths and unexpected files/origins refuse before factory/adapter/device calls. Legacy mode also proves exact export ownership, without imposing caller-specified origins.

Each `export_proof` event records fixed role/export, private canonical owner path/origin, expected origin, individual proof flags, numeric owner-query error and final `verified`. No function address is emitted. The acquired references are balanced by RAII and outlive all COM objects, alongside the original library references; no PIN or UNCHANGED_REFCOUNT is used. This establishes the two public entry-point owners, not every downstream implementation call, driver choice, authenticity or memory integrity.

One fresh process per route. No window, swapchain, frame loop, game or child process. Context/device, adapter and factory are released before library references. No timer, forced termination or automatic handling of native crashes/hangs.

## Evidence and interpretation

Output is flushed UTF-8 JSON Lines with fixed events and hexadecimal HRESULT/ID strings. The probe writes no files itself. Runtime output includes **private absolute loaded paths**: retain locally and publish only sanitized summaries.

Only its own process is inspected, with a 1,024-module cap and no retry. Six exact basenames are reported: `d3d10.dll`, `d3d10_1.dll`, `d3d10core.dll`, `d3d11.dll`, `dxgi.dll`, `vulkan-1.dll`. One [CreateToolhelp32Snapshot](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/nf-tlhelp32-createtoolhelp32snapshot) call per phase uses exactly `TH32CS_SNAPMODULE` and `GetCurrentProcessId()`. No process/thread/heap list, remote PID, alternate capture or loader lock is used. The read-only snapshot handle is closed once by RAII on normal, refusal and C++ exception paths.

Module32FirstW/Module32NextW read copied MODULEENTRY32W names/paths with dwSize initialized for each call. The collector processes at most 1,024 entries; a single next-entry lookahead distinguishes end-of-list after the 1,024th entry from excess data, which is never processed/resolved/disclosed. Only ERROR_NO_MORE_FILES after a valid first entry proves normal termination. An empty initial list, any other capture/traversal error or excess list marks evidence incomplete. Microsoft's ERROR_BAD_LENGTH retry advice is deliberately not adopted: one attempt, no delay or retry.

Names require bounded nonempty termination and no directory separators. Only the six exact case-insensitive names reach path handling; unrelated copied names/paths/PIDs/addresses/load counts are never output or used for file reads. Recognized copied paths require bounded nonempty termination, absolute spelling and matching basename. Boundary-length name/path fields are conservatively refused because truncation cannot be excluded; the SDK's MAX_PATH field is not a long-path guarantee. Paths resolve through read-only file handles and are classified under canonical executable directory (`fixture`), system directory (`system`), or `other`. Location does not establish authenticity. A fixture under a system/game directory invalidates the later trial setup.

Snapshots occur before graphics loading, after runtime/DXGI loading, after successful adapter identity selection immediately before device creation, and while a returned device is alive. Preloaded graphics or incomplete evidence stop the route. Duplicate flags remain visible: legacy mode refuses duplicates; qualified mode can admit them only with complete snapshots and both exact owner/path/origin proofs. Loaded/selected/device acceptance also requires both proven export-owner canonical paths in that captured module list. Missing either refuses, even when all other proof flags pass. A capture/traversal/path error marks the snapshot incomplete and refuses; no proof can override it. Final admission uses the same rule. Failure snapshots remain diagnostic and do not grant success. Captures can still fail during loading changes; they are bounded captured-list evidence, not continuous or atomic execution history. Other module basenames/processes are not reported.

Current fixed `module_error` stages are snapshot_capture, snapshot_first, snapshot_next, entry_pid, entry_name, entry_path, entry_limit, canonical_path and path_encoding. Each records phase, bounded index (0–1023), numeric Win32 error and truncation flag. Field/cap refusal uses ERROR_INVALID_DATA/ERROR_INSUFFICIENT_BUFFER; actual API errors are retained, including zero if no error was supplied. CanonicalFile preserves failed final-path query errors across CloseHandle and classifies insufficient capacity explicitly. No unrelated basename/path, address or raw error string is disclosed. There is no retry, fallback API, larger scan or conversion to success.

Historical `basename_query` diagnostics belong to earlier live-PSAPI revisions and remain valid records of those attempts. The new collector does not call GetModuleBaseName/GetModuleFileName for enumerated entries. The separate exact export-owner proofs still use ModulePath with retained module references; captured metadata does not replace that proof or attest executed memory.

| Exit | Meaning |
|---|---|
| 0 | Help, successful CPU self-checks, or successful runtime HRESULT + nonnull device + complete final evidence; inspect event type |
| 1 | CPU self-check failure |
| 2 | Invalid arguments; no runtime dispatch |
| 3 | Runtime refusal/failure or incomplete evidence |
| 4 | Caught C++ exception; inconclusive |

The `device` record precedes COM cleanup. Runtime acceptance requires normal exit 0, expected route/adapter, complete output and post-exit evidence. Crash/hang/nonzero exit is not success despite earlier records. `feature_level` is requested for 10/10.1 and returned for 11; it proves no device creation on a failed call.

Runtime exit 0 does **not** certify DXVK: Windows may create a native device. Record actual DLL origins and compare post-exit sizes/SHA-256 with the staged manifest. Disk hashes identify inspected file bytes, not executed-memory attestation. Module presence does not prove every call route or useful rendering. Failures retain their stage/HRESULT/paths rather than changing loading rules.

## Approved CPU-only hosted verification

[Workflow](../../.github/workflows/d3d10-probe-cpu.yml): one Windows worker, ten-minute cap, pull-request path filter for #250's four paths, contents-read permission. Logs identify actual checkout/source hash. Preinstalled `vswhere`/`VsDevCmd` locate MSVC; missing tools cause refusal rather than installation.

~~~text
cl /nologo /std:c++17 /EHsc /W4 /WX /MT tools\validation\D3D10Probe.cpp /Fe:OUTPUT\D3D10Probe.exe /Fo:OUTPUT\D3D10Probe.obj
dumpbin /dependents OUTPUT\D3D10Probe.exe
OUTPUT\D3D10Probe.exe --self-test
~~~

These are hosted commands, not a local profile. CI checks direct imports before execution. Its only execution is `--self-test`: CLI/gates/routes/ranges, injected dispatch (no real runtime callback), HRESULT/null/evidence outcome logic, JSON escaping/fixed output, hex formatting, path boundaries/normalization and UTF-8 conversion. All original 114 checks remain, including the initial 54. Added synthetic collector checks exercise capture/first/next errors, empty-list refusal, exact limits, malformed names/paths/PIDs, filtering/canonical-path handoff, duplicate retention, missing either owner path and snapshot-handle release on normal/refusal/exception paths. Synthetic handles/PIDs/entries and canonicalizers are injected into the production collector; they never reach Windows snapshot APIs or host files. `GetModuleHandleW` queries absence of the six names before/after without loading/initializing graphics. No real address-owner query, module/adapter enumeration or DXGI/Vulkan/device call occurs in self-test. Presence/import checks support source review; they do not intercept every possible future call.

No upstream package download, runtime route, GPU initialization, secrets, cache or artifact upload exists in this workflow. Normal existing CI remains separate and may upload its normal application artifacts. A future local trial needs an approved local build or separately authorized binary-delivery change: this workflow provides no downloadable probe package.

Compilation and CPU checks do not execute/validate graphics ABI, driver routes or real-device COM cleanup. Actual counts/results belong in the revision-bound PR completion record.

## Future runtime matrix — not activated

After source/CPU review and human merge, prepare an exact host profile and obtain current availability acknowledgment. Proposed initial matrix on one explicitly selected development adapter:

| x64 fixture | Local DXVK files | Fresh-process routes |
|---|---|---|
| Native | none | 10, 10.1, 11 control |
| Current | `d3d11.dll`, `dxgi.dll` | 10, 10.1, 11 control |
| Candidate | `d3d10core.dll`, `d3d11.dll`, `dxgi.dll` | 10, 10.1, 11 control |

Three new separate directories with identical probe bytes, outside system/game folders. At most nine starts, one attempt per case. Later profile fixes exact commands, source/executable hashes, root, compiler/output effects, OS/driver/adapter identity, duration/memory/space budgets, stopping and retention. Include transitive loading, GPU/driver caches/logs and active overlays as measured limits; writes cannot be presumed confined to the root.

Before any approved download, re-read official metadata and require exact pins:
- [v3.1.1](https://github.com/doitsujin/dxvk/releases/tag/v3.1.1), source `b1a1c99ab52b687cf950d62c88bc2fa316b41663`.
- Release `381956359`, Windows archive asset `566164743`, `dxvk-3.1.1.tar.gz`, 18,041,512 bytes.
- SHA-256 `40565b4a724aadc4433fa4e010b4b23916d9b1f1baeee64e17186db94f54e608`.
- [Official archive](https://github.com/doitsujin/dxvk/releases/download/v3.1.1/dxvk-3.1.1.tar.gz).

Validate regular archive members, reject links/duplicates/traversal, verify x64 PE machine types and retain staged hashes. No binary is acquired here. Preserve archive, manifests, stdout/exit outcomes and prior evidence; no automatic cleanup. v2.4 is deferred: acquired official asset metadata has no published digest; a self-computed hash is not an independent upstream pin.

Stop new work on unexpected origins/modules, identity mismatch, missing evidence, abnormal exit, hang or budget limit. Ordinary failed HRESULTs with complete evidence are matrix results, not retry permission. No forced termination, system DLL replacement, registry overrides, installations or games. The fixture/process-local configuration preparation below is the only proposed environment exception, and needs exact future approval. A separately justified core/load-order diagnostic cannot replace the normal-route result.

Measured results precede a coherent version-scoped mapping/eligibility/recognition/restore contract and explicit specification decision. Preserve DX9/DX11 behavior, source/architecture checks, native existing/absent baselines, external-change refusals and recovery evidence. A source dependency table or green CPU check alone does not finalize deployment.

## First authorized local activation — 2026-10-22

The owner approved **d3d10-local-v1** and current availability in [#252](https://github.com/Tiflit/DXVK-Companion/issues/252#issuecomment-6351043687). One local x64 build succeeded (only KERNEL32.dll direct dependency); its SHA-256 is `443196d5f7e4eccea23469e2947cdeff11af95d89a5bfb945740d3382456628b`. All 54 local CPU checks passed. Official archive pins matched; all 13 archive members passed bounded validation; three required payloads were x64 PE and three fixtures were staged with matching probe bytes.

The FIRST native discovery call (D3D11 route, ordinal 0, expected IDs 0/0) reported Intel Iris Xe 8086:A7A0, refused at `adapter_identity_mismatch` and exited normally with code 3, without a device event. Initial and loaded snapshots were complete; the failure snapshot was incomplete. No `module_error` record appeared. The accepted source therefore narrows the silent branch to a failed/truncated basename query; exact index/error/cause was not recorded. Do not infer a driver defect, transient race or harmless unrelated module from this evidence.

The profile stopped correctly: one build, one CPU invocation, ONE discovery invocation, ZERO of nine device-case starts. No repeat, forced termination, cleanup or game/personal-path access. The retained root measured 59,457,529 bytes. [Executed outcome](https://github.com/Tiflit/DXVK-Companion/issues/252#issuecomment-6351135026) preserves the incomplete result. [#253](https://github.com/Tiflit/DXVK-Companion/issues/253) adds only the diagnostic fields above, retaining every stop/completeness condition and all 54 CPU checks. It does not diagnose or repair the historical runtime cause. Source/CPU review precedes a separate changed-probe host continuation; the earlier activation remains stopped.

## Subsequent stops and controlled identity preparation — #255

A separately approved diagnostic activation completed one discovery call with complete snapshots and no query error; the original missing-query cause did not recur and remains unknown. Matrix-v2 later stopped after **three discovery calls and zero device cases**, with normal process exits. Its first current-fixture discovery had complete evidence but both fixture and system `dxgi.dll`. This differs from incomplete evidence; neither stop certifies compatibility or establishes why the modules coexisted. See the [sanitized executed outcome](https://github.com/Tiflit/DXVK-Companion/issues/252#issuecomment-6352846706). All activations remain completed/stopped and their evidence retained.

Pinned v3.1.1 [options](https://github.com/doitsujin/dxvk/blob/b1a1c99ab52b687cf950d62c88bc2fa316b41663/src/dxgi/dxgi_options.cpp) and [adapter implementation](https://github.com/doitsujin/dxvk/blob/b1a1c99ab52b687cf950d62c88bc2fa316b41663/src/dxgi/dxgi_adapter.cpp) show that DXVK may hide/remap reported PCI IDs. A description match alone is insufficient. This task does not select a general LUID alternative: upstream fallback LUID construction needs separate qualification.

Preparation for a future **configured diagnostic comparison**, not execution approval:

- Current/candidate fixtures would each receive a recorded private `dxvk.conf` containing exactly ASCII `dxgi.hideNvidiaGpu = False` plus one LF, no BOM. Record bytes/hash and readback; no forced vendor/device values or other options.
- Pinned [configuration loading](https://github.com/doitsujin/dxvk/blob/b1a1c99ab52b687cf950d62c88bc2fa316b41663/src/util/config/config.cpp) reads `DXVK_CONFIG_FILE` (otherwise working-directory `dxvk.conf`), then `DXVK_CONFIG` overrides. A later launcher must supply the exact absolute fixture config through its **child-only** `DXVK_CONFIG_FILE` environment and remove inherited `DXVK_CONFIG` in that child. Parent/user/machine environment and global/game configuration stay untouched. Do not rely solely on a working directory or silently accept inherited overrides. Native controls receive no DXVK setting.
- Later review fixes exact config bytes, child-environment commands, all other relevant inherited DXVK overrides, fixture manifests, qualified origins per route and finite host limits. For the proposed layout DXGI should be system for native, fixture for current/candidate; runtime should be system for 10/10.1 and fixture for current/candidate 11, subject to observed exact proof. Unexpected ownership refuses rather than broadening expectations.
- Verify effective configuration in retained private logs and keep the explicit ordinal/vendor/device identity guard. Native metadata anchors selection; a remapped/mismatched identity still refuses. No fallback adapter or description-only acceptance. This setting does not prove physical uniqueness among identical adapters.

The changed probe needs source/CPU review and human merge before a new host profile/current availability. No config is written and no executable is built or run locally by #255. Configured results cannot be presented as acceptance of default game settings, useful rendering or every downstream route.

## Matrix-v3 stop and captured-list correction — #257

The owner separately approved matrix-v3. One local x64 /W4 /WX build/import inspection succeeded and its single CPU invocation passed 114 checks. The two native D3D11 discovery calls both passed exact system-export ownership and exited normally at the expected identity guard, without a device call. The first had complete evidence; the second had four failure-phase basename_query errors, indices 24–27, each ERROR_INVALID_HANDLE (6), truncated=false. That snapshot remained incomplete and stopped the profile. **Zero configured DXVK calls and zero device cases** ran. See the [sanitized executed outcome](https://github.com/Tiflit/DXVK-Companion/issues/252#issuecomment-6353314873). All outputs remain retained; no retry, forced termination, cleanup or game/personal-file access.

The numeric result establishes a failed API branch, not the identities of those entries or a root cause. [Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getmodulebasenamew) documents limitations of live module queries. #257 uses copied captured entries to remove the enumerate-then-live-name-query gap; capture/traversal/canonicalization can still fail and must refuse. No source/CPU result can claim this historical cause repaired, certify actual Toolhelp behavior, or resume a stopped activation. Human source review/merge and a separately approved finite host profile/current availability precede any changed-code local test.

## Completed matrix-v4 — 2026-10-22

The owner approved **d3d10-matrix-v4** and current readiness in [#252](https://github.com/Tiflit/DXVK-Companion/issues/252#issuecomment-6353668075). Human-merged main was `20ff411ca560be448712cc3a249351cbc9ba7438`; the accepted probe blob was `42e684df7af9e673f195ea169d8204a153bd368a`, source SHA-256 `c20036de994aa703a4e8857029b47adc627be9e60d9fdd1edd03f40b15627c4b`. One local x64 /W4 /WX /MT build succeeded, KERNEL32.dll only direct dependency, executable SHA-256 `e8bb3a220086964af8d3166ed001fcf8229b4cdfdccbfbd4243ccfdc294be363`. Its single CPU invocation passed **151/151** checks.

Retained official v3.1.1 release/archive pins above matched; staged payloads were x64 PE and byte-equal before/after each case:

| Payload | Bytes | SHA-256 |
|---|---:|---|
| d3d10core.dll | 122894 | beffdd081dce91c2bccdc87845b88cbbb7848d39d4642143462d2556d69bd5d9 |
| d3d11.dll | 7483406 | 0a203b6255c893430f0a8461c618962df04e62aa41538d5a875b21868317cf9f |
| dxgi.dll | 5414926 | e3c1178eb7f0dd59f91bcfcc257549e5b9cfdb5e8508719293de7f8704f53160 |

Current/candidate fixtures used exactly 27 ASCII bytes: `dxgi.hideNvidiaGpu = False` plus one LF, no BOM, SHA-256 `46d51bb5d683712ea5458d1a61eb348f3eb754da4920c3785b36685c29f6429c`. Child-only config selection and unique log directories prevented inherited DXVK/DXGI overrides and log reuse; parent/user/machine settings were unchanged. Native controls received no added DXVK setting. Per-call private logs verified the selected config/effective option.

All **six discovery calls** passed evidence/provenance/expected identity-refusal checks and found one target ordinal independently per fixture. All **nine device calls** returned S_OK, nonnull devices and normal exit0:

| Fixture | D3D11 (11.0) | D3D10 (10.0) | D3D10.1 (10.1) |
|---|---|---|---|
| Native | S_OK | S_OK | S_OK |
| Current: d3d11.dll + dxgi.dll | S_OK | S_OK | S_OK |
| Candidate: current + d3d10core.dll | S_OK | S_OK | S_OK |

There were **54 complete module snapshots, zero module_error records and 30 verified export-owner proofs**. Both exact proven owner paths appeared in every required loaded/selected/device acceptance snapshot. Configured calls retained visible fixture/system DXGI duplicates, qualified by complete evidence and exact proofs rather than ignored. No retries or unplanned calls occurred.

Device-lifetime module evidence distinguished the layouts:

| Route | Native | Current | Candidate |
|---|---|---|---|
| D3D11 | System D3D11 | Fixture D3D11 | Fixture D3D11 |
| D3D10 | System core + system D3D11 | **System core + fixture D3D11** | **Fixture core + fixture D3D11** |
| D3D10.1 | System D3D11; no core reported | Fixture D3D11; no core reported | Fixture D3D11; no core reported |

Current/candidate D3D10 and D3D10.1 DXVK D3D11 logs recorded internal initialization at feature levels10_0/10_1. Public D3D10/10.1 exports still belonged to their expected system libraries. A reported module and log are evidence, not proof of every downstream function owner. The probe's D3D10/10.1 feature field records the request; D3D11 returns its feature level.

The activation ran 23:34:35–23:36:46 UTC and completed within its fixed 30-minute budget: one build, one CPU invocation, six discovery and nine device starts. Every returned process exited; no manual handling was needed. All private case outputs/logs/configs/proofs/manifests/hashes and earlier roots were retained. No game/personal-file access, download/extraction/install, forced termination, cleanup, source/specification change or merge. The [executed completion record](https://github.com/Tiflit/DXVK-Companion/issues/252#issuecomment-6353765990) closes the finite execution task; all local profiles remain ended.

This establishes configured x64/v3.1.1 loading/device-creation results on one Windows host. Both layouts passed; adding the core changed the observed D3D10 core origin without changing success. It does not establish universal core necessity, default-game/rendering/performance compatibility, x32/other-release support or apply/adopt/restore safety. Earlier invalid-handle causes remain unknown; these successful captures do not retroactively diagnose them.

The [non-normative deployment contract proposal](D3D10-DEPLOYMENT-CONTRACT-PROPOSAL.md) separates that measured result from a proposed three-file manager policy and defines coherent eligibility/recognition/ownership/restore regressions. Owner adoption and a separate assigned implementation contract precede any production or normative change. Merging evidence/proposal documentation is not adoption.
