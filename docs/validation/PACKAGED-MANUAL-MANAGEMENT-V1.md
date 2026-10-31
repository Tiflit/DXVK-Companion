# Packaged manual management — candidate v1

**PREPARATION ONLY — NOT ACTIVE.** [#282](https://github.com/Tiflit/DXVK-Companion/issues/282), under the [functional acceptance map](DXVK-FUNCTIONAL-ACCEPTANCE.md). Proposed activation: **“Approve packaged-manual-management-v1; laptop available now.”** Require closure/readiness for real games, launchers and other Companion instances before setup. Merge/Continue, prior readiness and ended profiles do not activate this run.

## Purpose and frozen inputs

Exercise actual packaged manual install→upgrade→configuration-disable/reapply→restore, with restarts between operations. One inert synthetic DX11/x64 target has a preexisting synthetic native DLL and configuration. Two real official releases are independently verified. No game, placeholder executable, downloaded DLL, Vulkan/GPU workload or HUD/frame-rate performance test runs. Networking stays available; previous HTTP-offline acceptance is separate. Five starts isolate stopped evidence checkpoints; this is functional acceptance, not UI polish.

| Input | Exact pin |
|---|---|
| Source / successful main run / attempt | `46e6f22363cddcfa88d805b588b6ac30173bc9dc` / `40025768457` / `1` |
| Application artifact / ZIP bytes / SHA256 | `12532606945` / `66,410,238` / `32660fd3e3ed87385e47940d490db2e6ab93daad7e993fe4225dd157232b3b14` |
| Provenance artifact / ZIP bytes / SHA256 | `12531339870` / `323` / `c9b46817bc60e89b3848ecf2705cb929b718cf950689a15071c65933d2a0537d` |
| Official v3.1 release / asset / archive bytes / SHA256 | `357524779` / `534051057` / `18,056,721` / `30f9cc326874be344285582275446968cfa4c069db31ce56df312d6644179154` |
| Official v3.1.1 release / asset / archive bytes / SHA256 | `381956359` / `566164743` / `18,041,512` / `40565b4a724aadc4433fa4e010b4b23916d9b1f1baeee64e17186db94f54e608` |

Reference URLs: `https://github.com/doitsujin/dxvk/releases/download/v3.1/dxvk-3.1.tar.gz` and `https://github.com/doitsujin/dxvk/releases/download/v3.1.1/dxvk-3.1.1.tar.gz`. Normal GitHub asset redirects allowed; no mirror/native/Linux/nightly/custom substitute. Before setup, recheck exact successful run/attempt/source, unexpired artifact IDs/sizes/digests, upstream Latest stillv3.1.1 and both tag/asset identities/URLs/sizes/digests. Reacquire main and require no `src/` changes since the frozen source. Mismatch ends preparation for a revised decision, without automatic repinning/rebuilding/dispatch. Verify compressed bytes before extraction, provenance `refs/heads/main`/source/run/attempt, then record extracted executable size/SHA256 and require unchanged before **every** start.

## Host, resources, operations and stopping

- Existing development laptop, ordinary non-admin interactive session. New absent root `<FIXTURE_ROOT>` under the approved development directory, suggested private basename `DXVK-packaged-manual-management-v1-20261023`. Abort if present. Layout `package/`, `provenance/`, `reference/`, `app/`, `runtime/`, `fixtures/ManualLifecycle/`, `evidence/`. No retained-root reuse/access/copy/cleanup; no personal/game/settings/save paths.
- **Five cumulative app start attempts** including failed/unobserved attempts, **60 minutes from first setup mutation**, **10 minutes/start**, **1 GiB root footprint**. Record immutable UTC-aware DateTimeOffset start/deadline and monotonic Stopwatch durations. Measure footprint at boundaries/before snapshots; stop new work before caps. Never reset budgets or resume after expiry.
- After exact activation/readiness, ordinary PowerShell/.NET root-confined file/directory creation/copies, JSON reads/writes, relative directory/file sets, hashes/raw-byte equality, static PE inspection and bounded ZIP operations are allowed. Preserve inputs/raw snapshots/private logs/start/exit records. No arbitrary harness/scanner, build/test/publish/install/update, privilege/permission/security/network/registry/environment change, service, agent/model invocation or payload execution.
- Download the exact application/provenance ZIPs using existing authenticated `gh api repos/Tiflit/DXVK-Companion/actions/artifacts/12532606945/zip` and `.../12531339870/zip`. Normal ProcessStartInfo redirected stdout BaseStream copied into new files, private stderr, natural exit0; no token extraction/printing/persistence. Verify size/hash before ZIP listing/extraction. Reject traversal/absolute/colon/link/collision members; resolve every destination inside its new root, at most64 entries/256MiB per ZIP. Download each independent reference archive once with Invoke-WebRequest at the exact URL and verify size/hash before the helper below. No automatic artifact extraction before hash verification.
- Only app launch command: `<FIXTURE_ROOT>/app/DXVK-Companion.exe --no-auto-discovery`, cwd`app`, normal ProcessStartInfo UseShellExecute=false, child-only `DOTNET_BUNDLE_EXTRACT_BASE_DIR=<FIXTURE_ROOT>/runtime`, owned process handle/private stdout/stderr/UTC+monotonic timing/natural exitcode. Keep launcher supervision alive until normal exit; short tool yields do not terminate it. No proxy overrides or endpoint probes in this profile. No parent/user/machine environment change.
- Human reports actual visible tray/discovery-off/Manual/startup-off state each time. Process existence is not GUI acceptance; unobserved launch consumes a start and stops. No desktop/privilege bypass. App may use existing same-user `%LOCALAPPDATA%/DXVKCompanion/locks` and normal runtime/OS effects; do not inspect/clean/change permissions there. No blanket zero-host-writes claim. No broad process inventory: owned app handle/exact executable exit evidence only.
- Unexpected real game/launcher/nonfixture record/action, UAC/install/update/security prompt, refusal/queue, hash/path/ownership inconsistency, lost observation, hang or cap stops dependent work. Normal tray/window exit only; no force termination/manual state repair/compensation/extra click/retry/sixth start. If safe stopping/restoration cannot be completed as planned, preserve the state/evidence and request human help; do not improvise a recovery launch. Human merging remains separate.

## Approved reference helper — only after activation

Locate an existing Python3.9+ through the bundled-runtime locator; no install. Only prerequisite invocation `<PYTHON> -I --version`. Save this code verbatim as `<FIXTURE_ROOT>/evidence/extract-reference.py`, record SHA256 and execute exactly twice: `<PYTHON> -I <SCRIPT> <FIXTURE_ROOT>/reference/dxvk-3.1.tar.gz <FIXTURE_ROOT>/reference/v3.1` and the corresponding `dxvk-3.1.1.tar.gz` / `reference/v3.1.1`. Destination must not exist. No other Python/script execution. Each helper validates the compressed bytes it actually parses and extracts exactly ten known regular DLLs without executing them.

```python
import hashlib, io, json, pathlib, sys, tarfile
archive, destination = map(pathlib.Path, sys.argv[1:])
pins = {
    "dxvk-3.1.tar.gz": ("3.1", 18056721, "30f9cc326874be344285582275446968cfa4c069db31ce56df312d6644179154"),
    "dxvk-3.1.1.tar.gz": ("3.1.1", 18041512, "40565b4a724aadc4433fa4e010b4b23916d9b1f1baeee64e17186db94f54e608"),
}
version, size, digest = pins[archive.name]
compressed = archive.read_bytes()
assert len(compressed) == size and hashlib.sha256(compressed).hexdigest() == digest
assert not destination.exists()
names = {f"dxvk-{version}/{arch}/{dll}" for arch in ("x32", "x64")
         for dll in ("d3d8.dll", "d3d9.dll", "d3d10core.dll", "d3d11.dll", "dxgi.dll")}
selected, seen, total = {}, set(), 0
with tarfile.open(fileobj=io.BytesIO(compressed), mode="r:gz") as source:
    for index, member in enumerate(source):
        assert index < 128
        path = pathlib.PurePosixPath(member.name)
        assert not path.is_absolute() and ".." not in path.parts
        assert "\\" not in member.name and ":" not in member.name and member.name not in seen
        seen.add(member.name)
        assert member.isdir() or member.isfile()
        total += member.size
        assert 0 <= member.size <= 64 * 1024 * 1024 and total <= 256 * 1024 * 1024
        if member.name in names:
            assert member.isfile()
            with source.extractfile(member) as stream:
                data = stream.read(member.size + 1)
            assert len(data) == member.size
            selected[member.name] = data
assert set(selected) == names
destination.mkdir()
manifest = {}
for name, data in selected.items():
    relative = pathlib.PurePosixPath(name).relative_to(f"dxvk-{version}")
    target = destination.joinpath(*relative.parts)
    assert target.resolve().is_relative_to(destination.resolve())
    target.parent.mkdir(exist_ok=True)
    with target.open("xb") as output:
        output.write(data)
    manifest[str(relative)] = {"size": len(data), "sha256": hashlib.sha256(data).hexdigest()}
(destination / "reference-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
```

## Setup and invariant evidence

Seed only current source-compatible JSON inside this new root: settings GlobalPolicy Manual/LaunchOnStartup false; exactly one visible disabled DX11/x64 profile/installation `Manual Lifecycle`, Game.exe evidenceHigh/DX11/x64, policyUseGlobal with exact pinv3.1, no managed version/files/pending/cancelled/refusal/conflicts and RestorationStateNone. Configuration HUDfalse/frame0/disabled in both stores. Seed fresh CachedRelease metadata for the pinnedv3.1.1 official origin with actual UTC CachedAt/LastAttemptAt, ETagnull; require it still within24hours at every start. This controls Latest scheduling rather than tests it; version choices remain exact. Do not preseed acquired payloads/markers/manifests.

Fixture starts with exactly **four files**: inert Game.exe; d3d11.dll containing unique inert synthetic-native bytes (never a valid loaded library); keep.bin sentinel; and original dxvk.conf with UTF8/no BOM, mixed newlines and no final newline, including unique comments, original `dxvk.hud = compiler`, original `dxvk.maxFrameRate = 30`, distinct-prefix `dxvk.hudExtra = keep` and unrelated `dxgi.syncInterval = 1`. dxgi.dll is absent. Record full original bytes/sizes/hashes and relative directory/file sets before start1. All stored paths resolve inside this root. Do not execute fixtures or manually edit target bytes after seeding.

At each stopped checkpoint require owned natural exit0/under10minutes, fixture-only exact identities, no pending/cancelled/refusal/conflicts/nonfixture records, Manual/startup-off/UseGlobal and expected pin/config/state. Capture separate immutable raw Cache/Profiles/Backups/settings/log/target snapshots and inventories. Before copies require projected root footprint below1GiB. Retain original backups and all acquired sources; compare complete directory/file sets and raw bytes, not just hash strings. A historical version/hash/architecture may remain after restore; distinguish active managed versions from history.

## Ordered five-session sequence

| Session | Human actions (once each, no substitutions) | Stopped independent checkpoint |
|---|---|---|
| 1 — acquire only | Confirm discoveryoff/Manual/startupoff/only disabled fixture. Game Details→Browse/Download official versions: download v3.1 once and v3.1.1 once, at most two catalog pages. No Enable/Update/Reapply/Adopt/settings edit. Exit normally. | All20 acquired DLLs equal respective independent reference bytes/sizes/hashes; both exact official origin/asset/archive hash/manifests/markers, selected x64 d3d11/dxgi static PE headers. Both complete12-file source trees recorded and raw-snapshotted. Four original target files/sets unchanged; still disabled. Otherwise no session2. |
| 2 — install older + settings | Confirm exactv3.1/UseGlobal/disabled. Game Details: enable HUD, frame limit60, Save Settings once. Enable DXVK once in Manage Games. Confirm actualv3.1, exit normally. | d3d11/dxgi matchv3.1/x64 raw reference and durable ownership. Exactlyfive target files; Game.exe/keep.bin unchanged. Config has one exact `dxvk.hud = fps,devinfo` and `dxvk.maxFrameRate = 60`, preserving comments/prefix/unrelated lines. Three active records: native d3d11 Existing, dxgi Missing, config Existing. Exactlytwo original raw backups native/config equal baseline; save immutable identities. Source trees unchanged. |
| 3 — upgrade newer | Confirm restart retained actualv3.1, HUDon/frame60/exactv3.1. Game Details: change only DXVK Version to v3.1.1, retain UseGlobal/HUD/frame60, Save Settings once; Update Selected once. Confirm actualv3.1.1, exit normally. | Both DLLs matchv3.1.1/x64 references/ownership and differ from older source. Original native/config baselines, backup paths and raw backup bytes remain unchanged; never replace baseline withv3.1 wrappers or generated config. Active versionv3.1.1, exact pinv3.1.1. Original sentinel/exe and full sources unchanged; five target files, same generated config. |
| 4 — disable features + reapply | Confirm restart retained actual/exactv3.1.1 and HUDon/frame60. Game Details: HUDoff/frame0, keep exactv3.1.1/UseGlobal, Save Settings once; Reapply DXVK once. Exit normally. | DLLs remainv3.1.1/reference/owned. Config now removes exact HUD/limiter assignments while preserving comments, `dxvk.hudExtra` and unrelated keys; no active managed setting remains. HUDfalse/frame0/disabled persisted. Config expected managed hash matches new bytes; original native/config baseline backups and source bytes unchanged. No manual file tampering or external-baseline replacement is part of this pass. |
| 5 — restart and restore | Confirm restart retained actual/exactv3.1.1, HUDoff/frame0/UseGlobal. Disable DXVK once. Exit normally; no further start. | Original **four files and complete directory/file sets exactly restored byte-for-byte**, including mixed-newline/no-final-newline config and original native DLL; dxgi absent. Both original backups retained byte-identically. Profiledisabled, installationRestored/active installation+per-file versions cleared, zero pending/conflict/refusal/nonfixture. Sources/manifests/markers unchanged. Historical hashes/absence/profile last-version/architecture retained truthfully. |

Saving preferences does not itself apply game files; the explicit Enable/Update/Reapply action is required. Restoring original config intentionally brings back its original HUD/limit lines even though Companion's saved controls are now off. Do not treat that exact-original restoration as an active Companion setting or an error. No adoption, Restore All, update-all, Forget/Hide, configuration-export, real game, proxy, startup toggle or extra operations in this profile.

## Result and retention

Publish one sanitized report bound to exact candidate approval/revision/package/executable/reference/helper identities: start/exit timings/codes/count, human GUI reports versus independent bytes/ownership/config/backup/state/source inspections, measured footprint/elapsed caps, actual failures/corrections and remaining limits. All private raw records stay under the root; publish only `<FIXTURE_ROOT>` aliases and aggregate outcomes, no paths/logs/JSON/screenshots/personal contents. Mark profile ENDED on completion or any stop; no force/cleanup/new attempt is authorized by its old approval. A pass adds manual management file-lifecycle evidence, not default discovery, adoption/queued/external-change/batch/Automated/runtime graphics, hardware support, crash recovery or stable-release certification.
