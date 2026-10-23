# Packaged HTTP-offline reuse — candidate v1

**PREPARATION ONLY — NOT ACTIVE.** [Issue #278](https://github.com/Tiflit/DXVK-Companion/issues/278) prepares `packaged-http-offline-reuse-v1` under [#159](https://github.com/Tiflit/DXVK-Companion/issues/159). Human merge or general continuation does not authorize local setup or execution. Activation requires explicit named-profile approval and current development-laptop availability. Earlier packaged roots/profiles remain ended and untouched; the reusable headless profile does not cover this run.

## Goal and limits

Acquire one official release online, then prove the packaged manager can use it for two synthetic targets with expired cached metadata and its HTTP transport configured unavailable. One target uses cached Latest, the other exact `v3.1.1`. Verify restart/policy persistence, exact deployed bytes/ownership, restoration and retained sources. Existing packaged-v3 and qualified DX10 evidence remain separate; this pass uses DX11/x64 and does not repeat their original-DLL or three-file policy scope.

The child-only proxy technique follows [Microsoft's .NET 8 DefaultProxy documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.defaultproxy?view=net-8.0). The frozen app constructs an ordinary shared `HttpClient` without a custom proxy handler. This is a configured HTTP-unavailability test, **not** a firewall, air gap, zero-network/zero-HTTP-request guarantee or all-protocol isolation. Actual app metadata failure plus fallback is required; file equality alone cannot count archive requests. No game, fixture executable, downloaded DLL or device workload runs.

## Frozen identities

| Item | Exact pin |
|---|---|
| Source / successful main run / attempt | `e9d8683426dc8312c11febc63df02896359bb6d9` / `40012073675` / `1` |
| Application artifact / ZIP size | `12525949545`, `DXVK-Companion-win-x64` / `66,410,146` bytes |
| Application ZIP SHA-256 | `5c4773e0012915477ac0ef6b808ebb820f2bde4934631200865c5a2d3806ed37` |
| Provenance artifact / ZIP size | `12526528615`, `build-provenance` / `324` bytes |
| Provenance ZIP SHA-256 | `0af7a88bef4718d8d6f8ab3fafb60e5d3296a928247117ccbfcf4ab64fa241c0` |
| Official tag / release ID | `v3.1.1` / `381956359` |
| Windows asset / ID / size | `dxvk-3.1.1.tar.gz` / `566164743` / `18,041,512` bytes |
| Published archive SHA-256 | `40565b4a724aadc4433fa4e010b4b23916d9b1f1baeee64e17186db94f54e608` |

Official download: `https://github.com/doitsujin/dxvk/releases/download/v3.1.1/dxvk-3.1.1.tar.gz`. Before setup recheck successful run/attempt/source, artifact expiry/IDs/sizes/digests, official `/releases/latest` and `/releases/tags/v3.1.1`; Latest must still be v3.1.1 with these exact asset pins. Reacquire main and require no `src/` changes since the frozen source. Stop for revised approval on mismatch; do not substitute, rebuild, dispatch CI or silently repin. Verify downloaded ZIP/archive bytes before extraction. Provenance must identify `refs/heads/main`, that source/run/attempt. Record extracted executable size/hash and recheck before every start.

## Resources, allowed operations and caps

- Host: existing development laptop, ordinary non-admin interactive session. Human closes real games/launchers and other Companion instances before readiness. No gaming desktop or personal/game/settings/save paths.
- New root: `<FIXTURE_ROOT>` under the approved development directory; abort if it exists. Suggested private basename `DXVK-packaged-http-offline-v1-20261023`. Layout `package/`, `provenance/`, `reference/`, `app/`, `runtime/`, `fixtures/OfflineLatest/`, `fixtures/OfflineExact/`, `evidence/`. No old-root access/reuse or deletion.
- Three cumulative application start attempts, including failed/unobserved attempts; **45 minutes** from first setup mutation, **10 minutes/start**, **1 GiB** root footprint. Measure at boundaries and before large evidence copies; stop new work before limits. Do not reset caps or automatically resume later.
- Approved after activation: normal PowerShell file/directory creation, scoped copies, JSON read/write, hashes/raw-byte/relative-file-set comparisons, static PE inspection, ZIP listing/extraction, pinned archive validation/extraction, official GitHub metadata and artifact downloads using existing authentication, and normal app launch supervision. Retain all inputs, snapshots, private logs/stdout/stderr and owned start/exit records. Never print tokens or publish raw evidence.
- Download the two exact ZIPs with authenticated `gh api repos/Tiflit/DXVK-Companion/actions/artifacts/12525949545/zip` and `gh api repos/Tiflit/DXVK-Companion/actions/artifacts/12526528615/zip`. Use normal `ProcessStartInfo` with redirected stdout, copying `StandardOutput.BaseStream` into a new ZIP file under this root; preserve binary bytes and require normal exit0. Do not use automatic-extraction commands before verifying compressed-byte hashes. Existing CLI authentication is permitted; never print/extract/persist a token. Download the independent official archive once with `Invoke-WebRequest` at the exact URL; normal GitHub asset redirects allowed. No mirrors, installers, SDK/build/test/publish commands or model invocation.
- ZIP extraction resolves every member under its new destination and refuses traversal/links/collisions. The reference archive helper below may use an existing Python 3 interpreter discovered through the bundled-runtime locator; no install/upgrade. Only pinned data is extracted, never executed.
- Launch exactly `<FIXTURE_ROOT>/app/DXVK-Companion.exe --no-auto-discovery`, cwd `app`, with child-only `DOTNET_BUNDLE_EXTRACT_BASE_DIR=<FIXTURE_ROOT>/runtime`. Use an owned process handle/timestamps, private stdout/stderr and normal completion/exit code. No broad process inventory: inspection is limited to the owned app and its exact executable path after exit. Actual GUI visibility/actions are human-reported; an unobserved launch consumes a start and stops, without desktop/privilege bypass.
- The app may use its existing same-user `%LOCALAPPDATA%/DXVKCompanion/locks` coordination location and normal runtime/OS effects; these specific effects are covered, not a zero-host-writes claim. No agent cleanup/permissions changes there. Startup registration must remain off.

## Bounded reference extraction helper

After activation, locate an existing Python3.9+ interpreter without installation; `<PYTHON> -I --version` is the only prerequisite invocation. Save this code verbatim as `<FIXTURE_ROOT>/evidence/extract-reference.py`; record its hash, then run `<PYTHON> -I <SCRIPT> <PINNED_ARCHIVE> <FIXTURE_ROOT>/reference/payload`. The payload subdirectory must not exist; its parent does. Isolated mode ignores Python environment overrides, including optimization that could disable assertions. The helper uses only the standard library. No other Python/script invocation is authorized.

```python
import hashlib, io, json, pathlib, sys, tarfile
archive, destination = map(pathlib.Path, sys.argv[1:])
compressed = archive.read_bytes()
assert len(compressed) == 18041512
assert hashlib.sha256(compressed).hexdigest() == "40565b4a724aadc4433fa4e010b4b23916d9b1f1baeee64e17186db94f54e608"
assert not destination.exists()
names = {f"dxvk-3.1.1/{arch}/{dll}" for arch in ("x32", "x64")
         for dll in ("d3d8.dll", "d3d9.dll", "d3d10core.dll", "d3d11.dll", "dxgi.dll")}
selected, seen, total = {}, set(), 0
with tarfile.open(fileobj=io.BytesIO(compressed), mode="r:gz") as source:
    for index, member in enumerate(source):
        assert index < 128
        path = pathlib.PurePosixPath(member.name)
        assert not path.is_absolute() and ".." not in path.parts and "\\" not in member.name
        assert member.name not in seen
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
    relative = pathlib.PurePosixPath(name).relative_to("dxvk-3.1.1")
    target = destination.joinpath(*relative.parts)
    assert target.resolve().is_relative_to(destination.resolve())
    target.parent.mkdir(exist_ok=True)
    with target.open("xb") as output:
        output.write(data)
    manifest[str(relative)] = {"size": len(data), "sha256": hashlib.sha256(data).hexdigest()}
(destination / "reference-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
```

## Setup and online session — start 1

After exact identity checks/setup authorization, create the new root and verify package/provenance/reference. Copy the full package into `app`. Seed current source-compatible settings/profile/library JSON only in this root: global Manual, startup false, exactly two visible disabled synthetic installations, DX11/x64 evidence in both installation/executable records, no managed version/files/conflicts/pending/cancelled work, clean restoration state. Use meaningful display names **Offline Latest** and **Offline Exact**. Both policies are `UseGlobal`; set `ManagementPolicy.PinnedDxvkVersion` null for Latest and `v3.1.1` for Exact (do not use legacy automated `PinnedVersion` mode). Do not preseed a release payload or fake an acquisition manifest.

Each target starts with inert `Game.exe`, unique-comment `dxvk.conf` and `keep.bin`, no DLLs. Record six raw original files, directory/file sets, hashes and bytes. Confirm all stored paths remain under this root. All placeholder DLL/config data stays inert.

Launch start1 online with discovery off. Human confirms Manual/startup off and both disabled, uses Game Details → Browse/Download official versions to download **v3.1.1 once only**, leaves both disabled, and exits normally via tray. No enable/version change/update/adoption or extra download. Record natural exit and exact owned-process absence before inspection.

Independently compare all ten acquired DLLs to reference raw bytes/sizes/hashes; verify exact acquisition origin/asset/archive hash/manifest/marker and x64 headers for selected d3d11/dxgi without loading them. Retain a raw immutable complete release-source snapshot, cache/settings/profiles/library snapshot, and unchanged target baselines. Require the acquired cached Latest to be v3.1.1. If acquisition/cache evidence is unavailable, stop before any offline launch.

## Stopped cache expiry and HTTP-unavailable configuration

While stopped, preserve the original `app/Cache/cache.json` raw bytes, parse its valid `CachedRelease` record, and change **only** `CachedAt` and `LastAttemptAt` to actual UTC now minus48hours. Preserve Release, ETag and all other fields; record before/after JSON privately. No cache deletion, fabricated Release, sidecar deletion or source-library change. Verify the primary remains valid and due (`EffectiveLastAttempt` older than24hours). Session3 keeps the application's own post-failure timestamps unchanged.

Before each offline start, one ordinary .NET `TcpClient` connect probe to **127.0.0.1:9 only**, maximum2seconds, must yield explicit `ConnectionRefused`; dispose its owned socket. A successful connection, timeout, access/other error is inconclusive and blocks that start. No listener, server, broad scan or service/process lookup is permitted.

For starts2/3 only, set on the child's `ProcessStartInfo.Environment` (case-insensitive Windows keys): `HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY` = `http://127.0.0.1:9`; `NO_PROXY` = empty; remove `GATEWAY_INTERFACE` from that child only. Record these overrides only, never an environment dump. Set them before process creation/HttpClient initialization. Keep the runtime extraction override. Do not change parent/user/system environment, Windows proxy/WPAD, firewall, adapter, DNS, security, credentials or another process. No `setx`, network disablement or permission changes.

## Offline apply — start 2

Human confirms discovery off/Manual/startup off, Latest target still Latest, Exact target still v3.1.1, both Use Global and disabled. Enable once per target, selecting the still-disabled row for the second operation. No browse/download attempt, policy edit, reapply/update/adopt or fixture/game execution. Both should show actual v3.1.1. Exit normally.

Stopped inspection must verify four deployed d3d11/dxgi files equal the independent x64 reference bytes and four durable ownership hashes, with absent original baselines; original six files/config unchanged, no unexpected target paths, pending/conflict/refusal or nonfixture records; both policies retained; complete local sources/manifest/marker unchanged.

Require private app evidence that the now-due DXVK metadata request actually failed under this transport configuration, cached v3.1.1 remained available, and failed-attempt timestamp advanced without changing successful `CachedAt` to a fresh validation. A startup update-check failure alone is insufficient; identify the DXVK metadata failure. If configured blocking/fallback evidence is missing, mark HTTP-offline acceptance inconclusive. If ownership/target checks are safe, the already-approved final restore below may still complete; do not repeat acquisition/apply or claim transport success. Hash/ownership/path inconsistency stops new mutations instead.

## Offline persistence/restore — start 3

With the same child-only blocked HTTP setup, human confirms discovery off/Manual/startup off, both enabled at actual v3.1.1 and their persisted distinct Latest/exact choices, then disables once per row and exits normally. No other actions or fourth start.

Verify exact original six files and complete relative file/directory sets restored byte-for-byte, injected d3d11/dxgi absent, both Restored/disabled with active installation/per-file managed versions cleared and zero pending/conflict/refusal. Keep implemented historical baseline/hash/last-version fields truthful; not every field must become null. Require no nonfixture records and complete source bytes unchanged, all evidence retained. No original DLL backup bytes were expected in this absence-baseline test; do not claim overwritten-native recovery coverage.

## Stop, retention and report

Normal app completion/tray exit or human normal window close only. Unexpected real game/launcher/nonfixture activity, UAC/install/update prompt, capability loss, hang, identity/hash/path/ownership mismatch or resource cap stops dependent work. No force kill, arbitrary diagnostic, state repair, override, cleanup or extra start. If normal stopping cannot be verified, report in-flight work and request human help. Preserve applied synthetic state/evidence if safe restore cannot proceed; do not touch unrelated paths.

Report once: profile approval source/revision, pinned and actual package/executable/reference identities, cumulative starts/durations/exits, child override/closed-endpoint evidence, actual expired-metadata failure/fallback and timestamp observations, per-target human GUI reports versus inspected deployed/owned/restored bytes, source retention, measured checkpoint footprint and limitations. Do not publish raw logs/JSON/path inventories/screenshots/private contents. A green result does not certify real games/rendering/default discovery/full concurrency/other releases or hardware, nor resolve historical CI causes.

Suggested activation **after review**: “Approve packaged-http-offline-reuse-v1; laptop available now.” Before that, no local resource setup/download/probe/helper/app run is authorized by this document.
