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
- **No process spawning or dynamic library loading**: The helper does not import or call `CreateProcess`, `ShellExecute`, `WinExec`, `LoadLibrary`, or `LoadLibraryEx`.
- **No networking or registry calls**: Zero sockets, HTTP, WinInet, or registry access.
- **Pure Win32 / static CRT dependencies**: Links statically with `/MT`; runtime imports are confined strictly to `KERNEL32.dll`.

## Hosted CI Workflow (`.github/workflows/process-lifecycle-helper-cpu.yml`)

The hosted verification workflow runs on `windows-latest` under `permissions: contents: read` with a 10-minute timeout:
1. **Source & Toolchain Identification**: Verifies source SHA256, checkout commit, and locates preinstalled MSVC via `vswhere.exe`.
2. **Compilation**: Compiles `tools/validation/QueueFixture.cpp` to `%RUNNER_TEMP%\helper-build\QueueFixture.exe` with `/std:c++17 /EHsc /W4 /WX /O1 /MT`.
3. **PE & Import Analysis**:
   - `dumpbin /headers`: Asserts `machine (x64)`.
   - `dumpbin /dependents`: Fail-closed parser asserts that `KERNEL32.dll` is the sole runtime dependency; rejects empty/malformed output, unrecognized DLLs, and all graphics libraries.
   - `dumpbin /imports`: Normalizes hint/ordinal-prefixed symbol rows; asserts zero process-spawning (`CreateProcess*`, `ShellExecute*`, `WinExec`), dynamic library loading (`LoadLibrary*`), networking (`ws2_32`, sockets, WinInet), or graphics APIs.
   - **Parser Negative Controls**: Verifies parser rejection against data fixtures containing hint-prefixed forbidden APIs and unexpected DLLs before trusting output.
4. **Cloud-Owned Smoke Tests**:
   - `InvalidArgs`: Validates rejection (exit `2`, empty stdout) for missing args, invalid flag, missing slot, slot `0`, slot `5`, slot `99`, and extra args.
   - `InvalidLayout`: Validates rejection for wrong parent directory (exit `3`), renamed executable (exit `3`), and missing control directory (exit `4`).
   - `ValidLifecycle`: Validates `READY`, confirms process stays alive over observation interval, creates regular-file marker, and verifies `STOPPED` with exit `0`.
   - `RefuseRelaunch`: Validates refusal (exit `5`) when stop marker is already present.
   - `SlotIndependence`: Validates slot `2` runs and stops independently while slot `1` stop marker remains present.
   - `DirectoryMarker`: Validates directory marker error (exit `6`) when marker is created as a directory while helper is running.
   - `DisappearingControlDir`: Validates exit code `7` promptly when the control directory is renamed/removed after `READY`, preventing indefinite polling until watchdog.
   - **Harness Negative Controls**: Validates that test failures after `READY` trigger normal marker-driven child stopping in `finally`, and validates bounded output/exit timeouts without hanging.
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
