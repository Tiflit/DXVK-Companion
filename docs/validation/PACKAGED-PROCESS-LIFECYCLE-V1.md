# Packaged Process-Lifecycle Validation Helper Specification (Stage A) — #299

## Status and Authority

The owner approved Issue [#299](https://github.com/Tiflit/DXVK-Companion/issues/299)'s bounded four-path contract and read-only hosted helper-build/smoke workflow ([recorded owner approval](https://github.com/Tiflit/DXVK-Companion/issues/299#issuecomment-6400475314)).
Execution is strictly confined to hosted Windows GitHub Actions CI. No local builds, local tests, helper execution, application execution, downloads to the owner's laptop, or desktop bridging are authorized.
This is **Stage A only** (helper build, import inspection, and cloud smoke verification). It does **not** adopt the later process-lifecycle runtime profile (Stage B host trial). Human alone merges.

## Objective and Motivation

Companion's `ProcessMonitor` attaches exit watchers to known installations with pending actions before window qualification, while `--no-auto-discovery` disables active process polling and window attachment.
To enable later validation of process guarding, queue suspension, and pending-action resumption without pretending an inert process is a freshly qualified graphics application, a small, reviewed, native x64 CPU-only process helper (`QueueFixture.exe`) is constructed.

The helper:
1. Stays alive without loading graphics libraries or making arbitrary system calls.
2. Signals readiness (`READY`) on standard output.
3. Supervised via an explicit control-file request (`control/stop-N.request`), exiting normally (`0`) with `STOPPED`.
4. Enforces a finite 25-minute watchdog yielding `WATCHDOG` and a distinct nonzero exit code.

## Helper Architecture and Behavior

### 1. Invocations and Arguments
- **Source**: `tools/validation/QueueFixture.cpp`, compiled with `/std:c++17 /EHsc /W4 /WX /O1 /MT`.
- **Exact supported invocation**: `QueueFixture.exe --slot N` where `N` is a single ASCII digit in `1..4`.
- **Argument enforcement**: Any missing, invalid, or extra arguments return exit code `2` immediately without entering the wait loop and without writing to standard output.

### 2. Disposable Layout Enforcement
- **Fixed directory layout**: `<FIXTURE_ROOT>/target/QueueFixture.exe`.
- **Derived paths**: `<FIXTURE_ROOT>` is derived strictly from the executable location via `GetModuleFileNameW`. Traversal sequences (`..`) and paths exceeding `MAX_PATH` are rejected before readiness.
- **Supervisor control**: The supervisor creates `<FIXTURE_ROOT>/control/`. The helper never creates directories or files. If `<FIXTURE_ROOT>/control/` is absent, the helper exits with code `4`.
- **Control marker**: `<FIXTURE_ROOT>/control/stop-N.request`.
- **Pre-existing marker refusal**: If the marker already exists before startup, the helper exits immediately with code `5` (refusing startup) without outputting `READY`.

### 3. Execution Lifecycle and Exit Codes
| Stage | Action / Condition | Stdout | Exit Code | Description |
|---|---|---|---|---|
| Arguments | Missing / invalid / extra args | (none) | `2` | Argument syntax rejection |
| Layout | Name not `QueueFixture.exe` or parent not `target` | (none) | `3` | Invalid layout rejection |
| Layout | Missing `<FIXTURE_ROOT>/control` directory | (none) | `4` | Supervisor layout incomplete |
| Startup | Pre-existing `stop-N.request` marker | (none) | `5` | Startup refused |
| Readiness | Validation succeeded | `READY\n` | - | Helper active and polling |
| Polling | Stop marker is a directory | (none) | `6` | Malformed marker rejection |
| Polling | Unexpected file access, missing control directory, or path error | (none) | `7` | I/O or path error (e.g. control directory disappears after READY) |
| Polling | 25-minute watchdog reached | `WATCHDOG\n` | `8` | Finite execution cap reached |
| Termination | Regular-file marker `stop-N.request` present | `STOPPED\n` | `0` | Normal clean shutdown |

### 4. Sandboxing and Resource Invariants
- **No static graphics dependencies**: The helper imports zero graphics libraries (`d3d9.dll`, `d3d10.dll`, `d3d10_1.dll`, `d3d10core.dll`, `d3d11.dll`, `d3d12.dll`, `dxgi.dll`, `vulkan-1.dll`).
- **No helper-authored file writes**: The helper never writes, creates, modifies, or deletes any files or directories on disk.
- **No helper-authored process spawning or dynamic library loading**: The helper source code authors zero calls to `CreateProcess*`, `ShellExecute*`, `WinExec`, `LoadLibrary*`, or any dynamic loading APIs. (Attributable correction: statically linked CRT startup/locale routines in `libcmt.lib` import `LoadLibraryExW`, `FreeLibrary`, and `GetProcAddress` from `KERNEL32.dll` for internal fallback bindings; direct helper-authored dynamic loaders such as `LoadLibraryA`/`LoadLibraryW` and process creation remain strictly forbidden and absent).
- **No networking or registry calls**: Zero sockets, HTTP, WinInet, or registry access.
- **Pure Win32 / static CRT dependencies**: Links statically with `/MT`; runtime imports are confined strictly to `KERNEL32.dll`. Exactly 73 functions are imported: 7 authored directly by the helper, and 66 originating from the statically linked MSVC runtime (`libcmt.lib`).

### 5. Toolchain Runtime Linkage & Loader Origin
- **Compiler and CRT**: Built with MSVC `/std:c++17 /EHsc /W4 /WX /O1 /MT`. Statically links the C runtime library `libcmt.lib`.
- **Helper-Authored APIs (7 functions)**: `GetStdHandle`, `GetFileAttributesW`, `WriteFile`, `GetLastError`, `Sleep`, `GetTickCount64`, `GetModuleFileNameW`.
- **Toolchain CRT Infrastructure (66 functions)**: Imported from `KERNEL32.dll` by MSVC's static C/C++ runtime library (`libcmt.lib`) for structured exception handling (`/EHsc`), CRT startup (`wmainCRTStartup`), heap memory allocation, code page conversion, and locale fallbacks (`LoadLibraryExW`, `FreeLibrary`, `GetProcAddress`).
- **Strictly Forbidden and Absent**: The binary imports zero graphics libraries (`d3d*.dll`, `dxgi.dll`, `vulkan-1.dll`), zero networking libraries (`ws2_32.dll`, `wininet.dll`), zero process creation APIs (`CreateProcess*`, `ShellExecute*`, `WinExec`), and zero direct dynamic loaders (`LoadLibraryA`, `LoadLibraryW`).

## Hosted CI Workflow (`.github/workflows/process-lifecycle-helper-cpu.yml`)

The hosted verification workflow runs on `windows-latest` under `permissions: contents: read` with a 10-minute timeout:
1. **Source & Toolchain Identification**: Verifies source SHA256, checkout commit, and locates preinstalled MSVC via `vswhere.exe`.
2. **Compilation**: Compiles `tools/validation/QueueFixture.cpp` to `%RUNNER_TEMP%\helper-build\QueueFixture.exe` with `/std:c++17 /EHsc /W4 /WX /O1 /MT`.
3. **PE & Import Analysis**:
   - `dumpbin /headers`: Asserts `machine (x64)`.
   - `dumpbin /dependents`: Fail-closed parser asserts that `KERNEL32.dll` is the sole runtime dependency; rejects empty/malformed output, unrecognized DLLs, and all graphics libraries.
   - `dumpbin /imports`: Strips hint/ordinal prefixes and rejects malformed/ordinal rows; verifies that all 73 imported functions belong strictly to the accounted set of helper-authored Win32 calls and reviewed static CRT (`libcmt.lib`) infrastructure; strictly asserts zero process-spawning (`CreateProcess*`), zero helper-authored dynamic loaders (`LoadLibraryA`/`LoadLibraryW`), zero networking (`ws2_32`, WinInet), and zero graphics APIs.
   - **Parser Negative Controls**: Verifies fail-closed parser rejection against data fixtures containing unexpected DLLs, hint-prefixed forbidden APIs (`LoadLibraryW`, `CreateProcessW`), malformed dependents rows, ordinal-only import rows, and malformed symbol rows before trusting output.
4. **Cloud-Owned Smoke Tests**:
   - `InvalidArgs`: Validates rejection (exit `2`, empty stdout) for missing args, invalid flag, missing slot, slot `0`, slot `5`, slot `99`, and extra args.
   - `InvalidLayout`: Validates rejection for wrong parent directory (exit `3`), renamed executable (exit `3`), and missing control directory (exit `4`).
   - `ValidLifecycle`: Validates `READY`, confirms process stays alive over observation interval, creates regular-file marker, and verifies `STOPPED` with exit `0`.
   - `RefuseRelaunch`: Validates refusal (exit `5`) when stop marker is already present.
   - `SlotIndependence`: Validates slot `2` runs and stops independently while slot `1` stop marker remains present.
   - `DirectoryMarker`: Validates directory marker error (exit `6`) when marker is created as a directory while helper is running.
   - `DisappearingControlDir`: Validates exit code `7` promptly when the control directory is renamed/removed after `READY`, preventing indefinite polling until watchdog.
   - **Harness Negative Controls**: Validates that test failures after `READY` trigger normal marker-driven child stopping in `finally` (exit `0`, `ResidualAlive == false`), and validates bounded stream drain timeout detection on a live helper without leaving orphan processes.
5. **Provenance Manifest & Artifact Packaging**:
   - Safely reads PR head and base commit SHAs from GitHub event JSON (avoiding shell interpolation).
   - Records MSVC compiler version, toolset identity, source SHA256, checkout commit/tree, run ID, and file inventory.
   - Measures total artifact size and asserts compliance against the approved 100MiB cap.
   - Packages `QueueFixture.exe`, `QueueFixture.cpp`, `headers.txt`, `dependents.txt`, `imports.txt`, `smoke-results.json`, and `provenance.json` into artifact `process-lifecycle-helper-x64` via `actions/upload-artifact@v6`.
   - On smoke failure, uploads diagnostic bundle `helper-smoke-failure-diagnostics` to retain failure evidence without presenting failing binaries as accepted.

## Boundary for Future Packaged Runtime Profile (Stage B)

The following requirements and disclosures govern any subsequent host trial profile:
1. **Prerequisites for Runtime Adoption**:
   - A subsequent profile proposal must pin reviewed helper and application binary digests, source commit, run ID, exact setup/supervision scripts, root directories, resource limits, ordinary stopping protocol, and artifact retention rules before seeking owner authorization.
2. **Privacy and Discovery Disclosure**:
   - Discovery-enabled packaged Companion can enumerate and inspect host processes and may record unrelated qualified running applications. Setting `Manual` mode is a management policy, not a fixture-only privacy filter.
3. **Known-Record Queue Semantics**:
   - `ProcessMonitor` attaches exit watchers to known installations with pending actions before qualification. The planned known-record queue test exercises this exact path and must not claim fresh graphics qualification.
   - A queued Enable action on an already enabled target may currently be represented in state models as pending `Install`; verification must evaluate requested version and final behavior rather than inventing an artificial enum requirement.
4. **Execution Authority**:
   - No host execution or desktop bridging is authorized by Stage A.

## Stage B Candidate Execution Profile (Preparation Only — #301)

> [!IMPORTANT]
> **CANDIDATE ONLY — PENDING SEPARATE HUMAN ADOPTION AND CURRENT READINESS**
> This candidate profile is prepared under Issue [#301](https://github.com/Tiflit/DXVK-Companion/issues/301). It is **NOT adopted** by this task.
> Execution on the owner's workstation or laptop remains **NOT RUN (pending human authorization)**.
> Only a subsequent separately adopted FINAL execution profile plus current laptop readiness can activate the actual trial. Human alone merges.

### 1. Cryptographic and Artifact Bindings

All binaries, archives, and tools must match the following frozen identities exactly before execution:

| Asset / Component | Source Revision / Identity | Artifact / Release ID | Size (Bytes) | SHA-256 Digest |
|---|---|---|---:|---|
| Trial Script (`ProcessLifecycleTrial.ps1`) | Current PR (`tools/validation/ProcessLifecycleTrial.ps1`) | N/A (in-repo) | 203,678 | `e36875925828362d5fe4ed13996ae8264f13f8e25baaa66787d51ff9ebfe1a5f` |
| Helper Archive (`process-lifecycle-helper-x64`) | Checkout `a62356d0acb918aec957463a0b670747763ae544`, Tree `4ebbd76b0f30ca7768d737361ea8b540da5a8a2e` | Run `40220299641`, Artifact `12609386490` | 81,717 | `f75ec55738e6ca3c1a0cd0a342aefeaa0fb3addd03538f03f2ac7a43295750de` |
| Helper Executable (`QueueFixture.exe`) | Source SHA256 `19229f59a4ab2504634cf9cd1bfff131a89a68e539808ab68c5459451e6ce0cc` | Extracted from `12609386490` | 138,240 | `f720cecae0f13eeef6d9b5e2d259b51df97b37bc3347b241071e25e8c1548d70` |
| Companion Application Archive | Commit `46e6f22363cddcfa88d805b588b6ac30173bc9dc` | Run `40025768457`, Artifact `12532606945` | 66,410,238 | `32660fd3e3ed87385e47940d490db2e6ab93daad7e993fe4225dd157232b3b14` |
| Companion Executable (`DXVK-Companion.exe`) | Built from `46e6f22363cddcfa88d805b588b6ac30173bc9dc` | Extracted from `12532606945` | 71,772,524 | `25012c67ade281e0d4585282bf485e4783a1f7c486018f8d1a038c5f2dc18ca4` |
| Companion Provenance Manifest | Run `40025768457` provenance JSON | Artifact `12531339870` | 323 | `c9b46817bc60e89b3848ecf2705cb929b718cf950689a15071c65933d2a0537d` |
| Official DXVK v3.1.1 Archive | Upstream `doitsujin/dxvk` tag `v3.1.1` | Release `381956359`, Asset `566164743` | 18,041,512 | `40565b4a724aadc4433fa4e010b4b23916d9b1f1baeee64e17186db94f54e608` |
| Official DXVK v3.1 Archive | Upstream `doitsujin/dxvk` tag `v3.1` | Release `357524779`, Asset `534051057` | 18,056,721 | `30f9cc326874be344285582275446968cfa4c069db31ce56df312d6644179154` |

### 2. Host, Resources, and Isolation Constraints
- **Host Alias**: `DEVELOPMENT_LAPTOP` (normal, standard non-admin Windows desktop session).
- **Dedicated Root**: Fresh directory `<FIXTURE_ROOT>` beneath the local development storage (e.g. `D:\dev\validation\process-lifecycle-v1-20261025`). Must not exist prior to setup. All writes strictly confined within `<FIXTURE_ROOT>`.
- **Directory Layout**:
  - `<FIXTURE_ROOT>/target/`: Contains `QueueFixture.exe`, inert baseline `d3d11.dll`, noncanonical `dxvk.conf`, `sentinel.txt`, and later dispatched DXVK DLLs.
  - `<FIXTURE_ROOT>/control/`: Supervisor control directory containing stop markers (`stop-N.request`), normal helper exit records (`helper-slot-N.exit.json`), active supervisor session handshake (`supervisor-session.json`), and explicit owner readiness record (`readiness.json`).
  - `<FIXTURE_ROOT>/baseline/`: Immutable copies of original baseline files and `baseline_manifest.json`.
  - `<FIXTURE_ROOT>/companion/`: Extracted DXVK Companion application, settings, and profile store.
  - `<FIXTURE_ROOT>/companion/Profiles/`: Contains `game-library.json` and `Backups/`.
  - `<FIXTURE_ROOT>/runtime/`: Child single-file extraction target (`DOTNET_BUNDLE_EXTRACT_BASE_DIR`).
  - `<FIXTURE_ROOT>/snapshots/`: Immutable checkpoint snapshots with cryptographic manifests.
  - `<FIXTURE_ROOT>/logs/`: Supervised process execution and verification logs.
  - `<FIXTURE_ROOT>/state.json`: Batch lifecycle state and execution accounting.
- **Resource Limits and Caps**:
  - Maximum Total Trial Duration: `120 minutes` (from first setup action, including operator pauses).
  - Maximum Helper Starts: `4 starts`.
  - Maximum Companion Starts: `8 starts`.
  - Maximum Root Storage Footprint: `2 GiB`.
  - Helper Execution Watchdog: `25 minutes` finite cap per helper instance.
- **Process Supervision**:
  - Permits at most one Companion and one QueueFixture process running simultaneously.
  - `UseShellExecute = false`, non-admin session, fixed working directories, redirected standard I/O drains.
  - Zero force-kill: process exits must occur normally via stop-markers or user GUI exit.

### 3. Sequential Manual Operator Instructions

All commands must be executed sequentially from PowerShell in the repository root:

```powershell
$FixtureRoot = "D:\dev\validation\process-lifecycle-v1-20261025"
$Script = "tools\validation\ProcessLifecycleTrial.ps1"
$AdoptionToken = "ADOPTED-PROCESS-LIFECYCLE-V1"
```

#### Step 1: Verification of Prerequisites and Readiness
1. Confirm the human owner has adopted this profile and verified that no games or other Companion instances are running.
2. Confirm downloaded helper archive `process-lifecycle-helper-x64.zip`, companion archive `DXVK-Companion-win-x64.zip`, and provenance match frozen pins.
3. Confirm `<FIXTURE_ROOT>` does not exist (fresh absent destination).

#### Step 2: Bounded Fixture Setup
Execute setup to initialize directory structure, extract binaries safely (create-new writes), extract official reference DLLs, and seed baseline:
```powershell
& powershell -NoProfile -ExecutionPolicy Bypass -File $Script -Action Setup `
    -FixtureRoot $FixtureRoot `
    -HelperZipPath "path\to\process-lifecycle-helper-x64.zip" `
    -CompanionZipPath "path\to\DXVK-Companion-win-x64.zip" `
    -CompanionProvenancePath "path\to\provenance.json" `
    -Dxvk31TarGzPath "path\to\dxvk-3.1.tar.gz" `
    -Dxvk311TarGzPath "path\to\dxvk-3.1.1.tar.gz"
```
Verify exit code is 0 and output confirms successful initialization (`state.json` initializes with `CurrentReadiness = "NotReady"`, `SetupScriptSha256`, `SetupProfileSha256`, `AdoptedScriptSha256 = $null`, `AdoptedProfileSha256 = $null`, and monotonic origin timestamps).

#### Step 3: Start Retained Supervisor Session
Start the interactive retained supervisor session. On startup, the supervisor binds its active session (`ActiveSupervisorPid` and `ActiveSupervisorSessionId` in `state.json`), writes `<FixtureRoot>\control\supervisor-session.json`, and prints both the `Supervisor PID` and `Supervisor Session ID` while remaining in `NotReady` state until the owner writes the session-bound `control\readiness.json`:
```powershell
& powershell -NoProfile -ExecutionPolicy Bypass -File $Script -Action Supervise `
    -FixtureRoot $FixtureRoot `
    -AdoptionAttestation $AdoptionToken
```

#### Step 3b: Write Explicit Owner Readiness, Profile, Revision & Session-Binding Record
Before issuing `start-companion` or `start-helper`, in a separate PowerShell terminal in the repository root, record the owner's explicit readiness confirmation bound to the adopted profile document (`docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md` and its exact SHA-256), the adopted script SHA-256, the fixture root, the batch start time, and the active supervisor session (`SupervisorPid` and `SupervisorSessionId` from `control\supervisor-session.json`):
```powershell
$State = Get-Content -LiteralPath "$FixtureRoot\state.json" -Raw | ConvertFrom-Json
$Session = Get-Content -LiteralPath "$FixtureRoot\control\supervisor-session.json" -Raw | ConvertFrom-Json
$ProfileSha256 = (Get-FileHash -LiteralPath "docs\validation\PACKAGED-PROCESS-LIFECYCLE-V1.md" -Algorithm SHA256).Hash.ToLowerInvariant()
$Readiness = [PSCustomObject]@{
    ProfileId               = "process-lifecycle-v1"
    AdoptedProfilePath      = "docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md"
    AdoptedProfileSha256    = $ProfileSha256
    AdoptionToken           = $AdoptionToken
    AdoptedScriptSha256     = "e36875925828362d5fe4ed13996ae8264f13f8e25baaa66787d51ff9ebfe1a5f"
    FixtureRoot             = $FixtureRoot
    BatchStartTimeUtc       = $State.BatchStartTimeUtc
    SupervisorPid           = [int]$Session.SupervisorPid
    SupervisorSessionId     = "$($Session.SupervisorSessionId)"
    OwnerAvailable          = $true
    GamesAndCompanionClosed = $true
    ConfirmedAtUtc          = [DateTime]::UtcNow.ToString("o")
}
$Readiness | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$FixtureRoot\control\readiness.json" -Encoding UTF8
```
Within 200ms of writing `control\readiness.json`, the interactive supervisor's observation cycle validates the profile path/SHA-256, script SHA-256, root, batch timestamp, active session PID/ID, and freshness window, transitioning the prompt to `Readiness=Ready`. Inside the supervisor session, the operator then issues the sequential trial commands:

#### Step 4: Checkpoint 0 — Initial Baseline Snapshot
Capture immutable baseline snapshot:
```text
Trial > snapshot Checkpoint0-Baseline
```

#### Step 5: Companion Acquisition Run (v3.1 and v3.1.1)
Launch Companion with discovery disabled:
```text
Trial > start-companion --no-auto-discovery
```
1. In GUI, download official DXVK v3.1 and v3.1.1 into the local release library.
2. Verify target `QueueFixture Trial Target` remains disabled with no pending actions.
3. Close Companion normally from GUI.
4. In supervisor terminal:
```text
Trial > stop-companion
```

#### Step 6: Start Helper Slot 1
Launch helper instance 1:
```text
Trial > start-helper 1
```
Verify helper outputs `READY` and supervisor retains active process handle.

#### Step 7: Queue Enable v3.1 in Companion
Launch Companion:
```text
Trial > start-companion
```
1. In GUI, navigate to `QueueFixture Trial Target` and queue **Enable** with DXVK v3.1.
2. Observe queue status: action is deferred due to active `QueueFixture.exe` process.

#### Step 8: Checkpoint 1 — Verify Durable Deferral
Capture snapshot and verify target files remain strictly unmutated:
```text
Trial > snapshot Checkpoint1-Pending-Slot1
Trial > verify Checkpoint1-Pending-Slot1 Pending v3.1
```

#### Step 9: Companion Restart Check
1. Close Companion normally via GUI (helper slot 1 remains running).
2. Confirm stop in supervisor:
```text
Trial > stop-companion
```
3. Restart Companion while helper slot 1 is still running:
```text
Trial > start-companion
```
4. Observe that pending intent is durably preserved across restart.

#### Step 10: Checkpoint 2 — Pending Across Restart Snapshot
```text
Trial > snapshot Checkpoint2-Pending-Restart
Trial > verify Checkpoint2-Pending-Restart Pending v3.1
```

#### Step 11: Stop Helper Slot 1 and Observe Exit Dispatch
Normally stop helper slot 1:
```text
Trial > stop-helper 1
```
1. Confirm helper exits normally with exit code 0 (`STOPPED`).
2. Observe Companion ProcessMonitor exit watcher triggering automatic dispatch.
3. Observe GUI reporting successful installation of DXVK v3.1.
4. Close Companion normally via GUI, then:
```text
Trial > stop-companion
```

#### Step 12: Checkpoint 3 — Verify Applied v3.1
Capture snapshot and verify deployed files match official reference and native backup exists:
```text
Trial > snapshot Checkpoint3-Applied-Slot1
Trial > verify Checkpoint3-Applied-Slot1 Applied v3.1
```

#### Step 13: Helper Slot 2 and Version Change to v3.1.1
1. Start helper slot 2:
```text
Trial > start-helper 2
```
2. Start Companion:
```text
Trial > start-companion
```
3. In GUI, select DXVK version `v3.1.1` and queue version change. Observe deferral.
4. Capture and verify snapshot `Checkpoint4-Pending-Slot2`:
```text
Trial > snapshot Checkpoint4-Pending-Slot2
Trial > verify Checkpoint4-Pending-Slot2 Pending v3.1.1
```
5. Normally stop helper slot 2:
```text
Trial > stop-helper 2
```
6. Observe exit-dispatched version change to v3.1.1 in Companion. Close Companion GUI.
7. Stop companion in supervisor:
```text
Trial > stop-companion
```
8. Capture and verify `Checkpoint5-Applied-Slot2`:
```text
Trial > snapshot Checkpoint5-Applied-Slot2
Trial > verify Checkpoint5-Applied-Slot2 Applied v3.1.1
```

#### Step 14: Helper Slot 3 and Reapply Action
1. Start helper slot 3:
```text
Trial > start-helper 3
```
2. Start Companion:
```text
Trial > start-companion
```
3. In GUI, queue **Reapply**. Observe deferral while helper slot 3 is running.
4. Normally stop helper slot 3:
```text
Trial > stop-helper 3
```
5. Observe exit-dispatched reapplication. Close Companion GUI.
6. Stop companion in supervisor:
```text
Trial > stop-companion
```
7. Capture and verify `Checkpoint6-Applied-Slot3`:
```text
Trial > snapshot Checkpoint6-Applied-Slot3
Trial > verify Checkpoint6-Applied-Slot3 Applied v3.1.1
```

#### Step 15: Helper Slot 4 and Disable / Restore Action
1. Start helper slot 4:
```text
Trial > start-helper 4
```
2. Start Companion:
```text
Trial > start-companion
```
3. In GUI, select **Disable / Restore**. Observe deferral while helper slot 4 is running.
4. Normally stop helper slot 4:
```text
Trial > stop-helper 4
```
5. Observe exit-dispatched restoration. Close Companion GUI.
6. Stop companion in supervisor:
```text
Trial > stop-companion
```
7. Capture and verify `Checkpoint7-Restored-Slot4`:
```text
Trial > snapshot Checkpoint7-Restored-Slot4
Trial > verify Checkpoint7-Restored-Slot4 Restored
```
Verify target files bit-for-bit match the initial baseline (`d3d11.dll`, `dxvk.conf`, `sentinel.txt`), `dxgi.dll` is strictly absent, and backup history is preserved.

#### Step 16: Final Restart and Replay Verification
1. Start Companion with no new intent:
```text
Trial > start-companion
```
2. Confirm target remains clean and restored with no unintended replay of prior actions.
3. Close Companion GUI and stop in supervisor:
```text
Trial > stop-companion
```

#### Step 17: End Batch and Exit Supervisor
End the batch and exit supervisor session:
```text
Trial > end-batch
Trial > exit
```

### 4. Stop and Hold Rules
1. **Helper Watchdog or Abnormal Exit**:
   - If helper exits with code `8` (`WATCHDOG`) or code `2..7`, the trial must pause immediately.
   - The operator must close Companion normally before taking any recovery action.
2. **Nonfixture Detection or Unexpected Game Discovery**:
   - If Companion detects an unrelated running application or game on the host, close Companion immediately.
   - Do not continue or whitelist the detected application.
3. **Budget Exhaustion**:
   - If total duration reaches `120 minutes`, helper starts reach `4`, or companion starts reach `8`, the script refuses further starts.
   - Retain all evidence, snapshots, and logs in `<FIXTURE_ROOT>` for audit. No automatic deletion or cleanup.

