# DXVK-Companion

A Windows utility for detecting game launches, identifying graphics APIs, and managing official [DXVK](https://github.com/doitsujin/dxvk) deployment, versions, restoration, and per-game configuration.

**Status: pre-release development.** A standalone Windows x64 application and automated checks exist; a stable public release and broad game/GPU compatibility are not established. Intel Arc, NVIDIA GeForce and AMD Radeon are development targets, not certified support. Companion targets Direct3D 9, 10 and 11 management; the D3D10 deployment DLL set still needs separate Windows loading verification ([current findings](docs/validation/MODULE-EVIDENCE-INTEGRITY.md#d3d10-deployment-investigation-remains-separate)).

> [!WARNING]
> DXVK changes the graphics libraries loaded by a game. Avoid games with anti-cheat or anti-tamper protection, including offline or single-player modes that still use it. Detection heuristics cannot guarantee safety or prevent bans. Unknown compatibility is not permission to deploy.

---

## ⚡ Core Principles

* **Portable App Data**: Profiles, release payloads, cache and logs normally live beside the executable. Managed operations deliberately write to selected game folders. Same-user coordination uses `%LOCALAPPDATA%/DXVKCompanion/locks`; optional Windows startup registration and .NET native-library extraction also have effects outside the app folder. Portability is not host isolation or a zero-external-writes guarantee.
* **Owned Files & Restoration**: Original baseline DLL/configuration backups live in Companion storage (`Profiles/Backups/{id}`), rather than `.bak` files in game directories. Restore verifies ownership and recorded baselines before restoring original bytes or removing files that Companion created. External changes or uncertain recovery require attention; arbitrary files are not silently deleted.
* **Transaction & In-Process Rollback**: Multi-file operations (such as `d3d11.dll` + `dxgi.dll` for DirectX 11) execute under an in-process transaction state machine with SHA-256 pre-flight identity verification; if an operation encounters an in-process exception following writes, it attempts rollback to restore backed-up baselines and reports unresolved recovery (`AttentionRequired`) if rollback fails. (Note: attempted rollback handles caught in-process exceptions, but does not provide OS-level atomic multi-file visibility to external processes or guarantee restart recovery after abrupt process termination or power loss; pre-write validation aborts exit cleanly without needing rollback).
* **Self-Contained Packaging**: The testing package includes the .NET runtime; a separate .NET installation is not required to run it. Source builds require the .NET 8 SDK. Games separately require suitable official DXVK payloads and Vulkan-capable graphics drivers.
* **Guarded File Operations**: Deployment and restoration check relevant game activity before mutation; running games can have work queued for a later safe opportunity. Unknown activity can prevent an operation. Companion does not replace Windows system DLLs; installed DXVK libraries execute inside the game when it runs.
* **Anti-Cheat Heuristics**: Detects known anti-cheat modules and signatures (Easy Anti-Cheat, BattlEye, Vanguard, etc.) with fail-closed heuristics (`UnableToDetermine` / `SuspectedOrKnown`) that block automated deployment. (Heuristics reduce risk but do not guarantee detection of all anti-cheat software or guarantee ban safety in online games).

---

## 🚀 Quick Start (Testing & Usage)

### Requirements

* The provided Companion package targets **Windows x64**. It can select x32 or x64 DXVK payloads for supported game executables; package architecture alone does not prove game or GPU support.
* The selected DXVK release has its own Vulkan feature, GPU and driver requirements. Consult the official [driver requirements](https://github.com/doitsujin/dxvk/wiki/Driver-support) and the selected [release notes](https://github.com/doitsujin/dxvk/releases); older retained releases may have different requirements. The upstream driver table includes Linux-specific guidance and is not a certified Windows compatibility list.
* Upstream [Windows guidance](https://github.com/doitsujin/dxvk/wiki/Windows) says Windows use is not officially supported by DXVK and documents game-specific DLL loading issues. Companion needs its own Windows validation. Never replace DLLs in Windows system directories.
* Choose a writable application folder and retain its profiles/backups. Obtain missing DXVK releases from the official source; eligible downloaded releases can be reused locally. See [offline selection and its limits](docs/LATEST-RELEASE-METADATA.md#explicit-offline-recovery).

### Running Standalone
1. Open a successful **Build and Test** run for `main` in [GitHub Actions](https://github.com/Tiflit/DXVK-Companion/actions/workflows/build-and-test.yml?query=branch%3Amain), verify its source revision and download the `DXVK-Companion-win-x64` artifact. These are development builds; retained artifacts can expire or become unavailable.
2. Extract the complete artifact into a writable folder of your choice.
3. Run `DXVK-Companion.exe`.
4. Companion docks directly into the Windows system tray notification area (near the clock).

As of October 22, 2026, this repository has **no published GitHub Release**. The [Releases page](https://github.com/Tiflit/DXVK-Companion/releases) is the intended future stable-download route. The existing release workflow prepares a ZIP and SHA-256 sidecar; its existence does not establish a published, signed or attested release. A checksum identifies bytes rather than certifying compatibility.

### Optional discovery pause

Start `DXVK-Companion.exe --no-auto-discovery` to pause passive discovery for that invocation. Confirm **Automatic discovery off** in the tray menu. Monitor-driven game-exit dispatch is also paused, so newly queued work does not automatically execute on exit during that invocation. Startup inspection/pending processing, activity safety checks and network/update checks can still occur; saved management policies are unchanged. See [startup control and limits](docs/validation/AUTOMATIC-DISCOVERY-STARTUP.md).

### Optional Windows startup

The Settings startup option writes the value `DXVK Companion` under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`, containing the quoted executable path. Turning the option off removes that value; deleting or moving the executable alone does not unregister it. Disable startup before moving/removing the application, and restore managed games before discarding restoration backups.

---

## 🎮 Operating Modes

DXVK Companion supports two operating modes configured globally in **Settings** or overridden per game:

### 1. Manual Mode (Default)
* Observes and reports game rendering APIs, architectures, and health states.
* Does not modify game files or deploy DXVK without explicit user confirmation.
* Direct control available via **Manage Games** and **Game Details** dialogs.

### 2. Automated Mode (Experimental)
* Automatically selects and deploys the latest official DXVK release for newly launched compatible Direct3D 9, 10, and 11 games (DX12 and Vulkan titles are detected for informational reporting only; DXVK deployment is refused).
* Queues safe deployment while the game is running and applies the transaction automatically upon game exit.
* Detects changes to managed files and reassesses eligibility; reapplication that overwrites external changes requires user authorization and baseline handling through the safety path.
* Respects fail-closed anti-cheat protection: automatic actions are strictly blocked if anti-cheat or anti-tamper components are detected.

---

## 🖥️ User Interface Overview

### System Tray Menu (Section 39)
Right-clicking the tray icon presents a clean, static, and predictable menu:
* **Header**: Displays the application name, with **Automatic discovery off** when launched with the discovery-pause switch.
* **Manage Games...**: Opens the centralized game management dashboard.
* **Game Details...**: Opens detailed configuration for the currently active game.
* **Settings...**: Configures global management policy and Windows startup behavior.
* **Exit**: Closes Companion cleanly.

### Manage Games Dashboard
* **View Filters**: Quickly filter between `Active Games`, `Managed / Active Only`, `Attention Required`, `Hidden Games`, and `All Tracked Games`.
* **Health Badges**: Real-time status reporting (`Clean / Native`, `Managed`, `Attention Required`, `Conflict`, `Restored`).
* **Batch Operations**: Support for updating all enabled games or safely executing **Restore All** with per-game error isolation.
* **Adoption & Reapplication**: One-click adoption of pre-existing official DXVK releases and reapplication after external game patches.

### Game Details Window
* **Health & API Inspection**: Inspects detected Direct3D version, bitness (x86/x64), and managed files.
* **Per-Game Policy Override**: Configure individual titles to `Use Global Policy`, `Automatic Management`, or `Disabled`.
* **Frame Limiter**: Set a custom framerate limit (writes `dxvk.maxFrameRate`).
* **Performance HUD**: Toggle DXVK's built-in telemetry overlay (`dxvk.hud = fps,devinfo`).
* **Configuration Safety**: Updates the managed HUD/frame-limit keys while preserving unrelated configuration lines and recording the original restoration baseline.

---

## 🏗️ Architecture & Component Design

```text
                    PROCESS MONITOR
                           │
                           ▼
                    DETECTION LAYER
       (ProcessFilter, ModuleScanner, PE Import Fallback)
                           │
                           ▼
                    API CLASSIFICATION
         (DX9, DX10, DX11, DX12, Vulkan [Informational])
                           │
                           ▼
                    POLICY ENGINE
                (Manual vs. Automated)
                           │
                           ▼
                 SAFE TRANSACTION ENGINE
         (MultiFileTransactionEngine & FileIdentity)
             ┌─────────────┼─────────────┐
             ▼             ▼             ▼
          Install       Update        Restore
             └─────────────┼─────────────┘
                           │
                           ▼
                 VERIFY & PERSIST STATE
             (GameLibraryStore & Backups)
                           │
                           ▼
                  SYSTEM TRAY & UI
      (Manage Games, Game Details, Static Tray)
```

### Component Breakdown

* **Safety & Transactions (`DXVKCompanion.Safety`)**:
  * `MultiFileTransactionEngine`: Executes multi-file operations (`Install`, `Update`, `Reapply`, `Restore`), manages isolated backups, and attempts in-process rollback upon caught exceptions following writes, reporting unresolved recovery (`AttentionRequired`) if rollback cannot be completed.
  * `SingleFileTransactionEngine`: Single-file state machine with backup tracking and simulated crash/recovery state handling.
  * `FileIdentity`: Deterministic SHA-256 and byte-size identity tracking for file provenance and tampering detection.
  * `TransactionContracts`: Formal state machines and outcome records.

* **Domain & Storage (`DXVKCompanion.Models`, `DXVKCompanion.Storage`)**:
  * `GameInstallation`: Tracks installation roots, multiple executables, managed file records, and conflict flags. Under approved shared-directory policy, if any recorded executable is classified as incompatible (DirectX 12, Vulkan, or Unknown/unsupported), DXVK deployment is refused across the entire installation directory, while Restore and RestoreAll remain available (undiscovered sibling executables remain a known limitation).
  * `ManagedFileRecord`: Tracks original state (`Existing` vs. `DidNotExist`), baseline hashes, and backup pointers.
  * `ManagedFileInspector`: Real-time inspection of managed files, detecting external modifications, deletions, and invalidating stale pending actions.
  * `GameLibraryStore`: JSON persistence for hierarchical game installations (`game-library.json`) with corruption recovery, name-based enum serialization (`GraphicsApi`), legacy `ModernAPI` compatibility, and clean-slate V1 initialization (does not import pre-release `games.json` profiles). Saves use operation-owned temporary staging (`game-library.json.tmp.<guid>`) before atomic replacement to prevent file truncation on partial writes without disturbing sibling recovery files (Issue #104, PR #105). On startup and prior to typed deserialization, root schema preflight inspects the schema version; libraries declaring an explicit newer schema (`SchemaVersion > CurrentSchemaVersion`) trigger clean startup refusal and storage mutation guards (`IncompatibleLibrarySchemaException`), preserving newer library files, backups, and recovery siblings byte-for-byte intact (Issue #106/107, PR #108). (Note: single-file staging with atomic replace guards against in-process partial-write truncation, but does not provide OS-level cross-process locking, power-loss durability, cross-store atomicity, or automatic data recovery; recovery snapshots `.recovery.*.json` remain preserved diagnostic data rather than auto-restored state).
  * `ProfileStore`: Active flat profile persistence (`games.json`) for UI components using pinned integer ordinals (`0..6`), operating independently from `GameLibraryStore` (unsupported legacy import into `GameLibraryStore` is omitted under Clean-Slate V1 policy, while `games.json` remains actively maintained by `ProfileStore`). Saves stage to a temporary file before atomic replacement (`File.Move` with overwrite) to prevent file truncation on partial writes (Issue #101/103, PR #102). Numeric values deserialize via underlying cast without exception (though unhandled ordinals fall back to unmanaged application behavior).
  * `CacheStore` & `SettingsStore`: Portable configuration, release caching, and global policy persistence.

* **Detection & Monitoring (`DXVKCompanion.Monitoring`)**:
  * `ProcessMonitor`: Polling process monitor with window wait retries and rich `DetectionSnapshot` generation.
  * `GameDetector`: Filters out system processes and store launchers, and assesses anti-cheat risks (`AntiCheatAssessment`) across process modules and game directories.
  * `ModuleScanner`: Inspects loaded graphics runtime modules in running processes.
  * `PeParser`: Static PE header and Import Address Table (IAT) inspection fallback with architecture detection.
  * `ApiClassifier`: Classifies Direct3D 9, 10, 11, DirectX 12, and Vulkan games with confidence levels and evidence tracking (`ApiClassificationResult`). Supported DXVK deployment targets are DX9, DX10, and DX11; DX12 and Vulkan classifications are informational, and DXVK deployment is refused for them (legacy `ModernAPI` is retained only as an obsolete enum value for deserialization backward compatibility).

* **DXVK Management (`DXVKCompanion.DXVK`)**:
  * `DxvkGithubClient`: Fetches official release metadata from the GitHub API with local caching.
  * `DxvkReleaseCatalog`: Deterministic SHA-256 hash lookup for official DXVK releases.
  * `ExistingDxvkDetector`: Inspects game directories, PE version metadata, and SHA-256 hashes to reliably recognize official releases, unknown builds, or native files.
  * `DxvkInstaller`: In-memory extraction of release archives, staged deployment with in-process rollback, configuration staging, existing DXVK adoption, and reapplication.
  * `DxvkRollback`: Clean restoration of original game baselines and self-cleaning deletion of injected DXVK files.
  * `DxvkConfigManager`: Manages `dxvk.conf` settings (Section 36 invariant: atomic merge, zero unneeded config creation).
  * `RestoreAllAsync`: Global baseline restoration with per-game error isolation (Section 23).

* **User Interface (`DXVKCompanion.UI`)**:
  * `TrayApp` & `TrayMenu`: Minimal static system tray menu adhering to Section 39.
  * `ManageGamesWindow`: Status-oriented management UI with real-time health badges, view filtering, adoption, reapplication, and **Restore All**.
  * `GameDetailsWindow`: Game health banner, frame limiting, HUD overlay toggles, per-game policy selection, and hidden status toggling.
  * `SettingsWindow`: Global management policy toggle (Manual vs. Automated Experimental) and startup control.

---

## 📂 Portable Directory Structure

Normal Companion data layout (not an inventory of every OS, runtime or DXVK write):

```text
DXVK-Companion/
├── DXVK-Companion.exe      # Standalone single-file executable
├── Profiles/
│   ├── game-library.json   # Hierarchical game installations and managed file records
│   ├── games.json          # Flat profile configuration used by ProfileStore (not imported into GameLibraryStore under Clean-Slate V1)
│   └── Backups/            # Pristine original game file baselines (isolated from game folders)
├── Cache/                  # Cached DXVK release metadata
├── Logs/                   # Application log files
└── DXVK/                   # Extracted official DXVK release binaries (x32 and x64)
```

Settings are also stored beside the executable. Coordination locks and runtime extraction are outside this layout as described above. DXVK itself can create game logs and shader caches; its [v3.1.1 documentation](https://github.com/doitsujin/dxvk/blob/b1a1c99ab52b687cf950d62c88bc2fa316b41663/README.md#logs) describes those separate runtime effects. Preserve profiles and original backups while any game remains managed.

---

## 🧪 Testing & Validation

Automated file operations and state transitions use disposable synthetic fixtures. Passing these checks supports only the exercised logic and failure paths; it does not certify live GUI behavior, game/driver compatibility or all external-process concurrency. The [packaged lifecycle record](docs/validation/RELEASE-LIBRARY-PACKAGED-V3.md#execution-disposition--october-21-2026) separately documents acquisition, selected payload bytes and exact synthetic restoration, qualified by a sequence deviation and an approved supplement. It is not general game acceptance. Intermittent service/lease CI failures retain unresolved causes in [the backlog](https://github.com/Tiflit/DXVK-Companion/issues/167); later green runs do not diagnose them.

For source builds, use Windows with the **.NET 8 SDK** and access to restore the declared project/test packages. The self-contained download and source-build prerequisites are different. Contributor host execution follows [repository authorization rules](AGENTS.md#local-execution-profiles); the examples below are references, not a standing execution approval.

```bash
# Build application in Release
dotnet build src/DXVKCompanion/DXVKCompanion.csproj --configuration Release

# Build test suite in Release
dotnet build tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj --configuration Release

# Run automated tests
dotnet test tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj --configuration Release --no-build --no-restore
```

Continuous integration runs automatically on Windows runners via GitHub Actions (`.github/workflows/build-and-test.yml`).
The release workflow (`.github/workflows/release.yml`) packages a self-contained ZIP plus SHA-256 sidecar and publishes a GitHub Release on a matching tag push. No release is created by this documentation task.

---

## 🗺️ Project Specifications & Roadmap

For complete specifications and architectural contracts, refer to the canonical specification and safety supplement:
* [Canonical Project Specification](docs/spec/DXVK-COMPANION-SPEC.md)
* [Phase A.5 Safety & Identity Design (Normative Supplement)](docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md)
* [Pending component research consolidation (PR #181)](https://github.com/Tiflit/DXVK-Companion/pull/181): pinned source/licence findings proposed separately, not adopted dependencies or a document already merged into main.

> [!NOTE]
> **Specification Authority & Precedence**: [`docs/spec/DXVK-COMPANION-SPEC.md`](docs/spec/DXVK-COMPANION-SPEC.md) is the canonical project specification, approved under [Issue #12](https://github.com/Tiflit/DXVK-Companion/issues/12). [`docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md`](docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md) serves as its normative safety and identity supplement. Superseded historical specifications are archived under `docs/spec/archive/`.

### Development Progress & Verification Status
* [x] **Phase A**: Data Foundation (Hierarchical `GameInstallation`, `ExecutableProfile`, `ManagedFileRecord`)
* [x] **Phase A.5**: Multi-File Transaction Engine (`MultiFileTransactionEngine`, `FileIdentity`)
* [x] **Phase B**: Detection Layer Refactoring (Multi-executable folder tracking, delayed runtime scans, enhanced anti-cheat heuristics, and API transitions)
* [x] **Phase C**: DXVK Release Repository (Official release catalog, deterministic hash identification, existing DXVK adoption, Reapply, and Section 36 `dxvk.conf` management)
* [x] **Phase D**: External-Change & Pending-Action Handling (`ManagedFileInspector`, Section 20 supersession, baseline replacement, and persistent restart handling)
* [x] **Phase E & F**: UI Modernization & Delayed Notifications (Status-oriented management UI, adoption/reapply buttons, health badges, startup inspection, and delayed balloon notifications)
* [x] **Phase G**: Automated Maintenance Mode (`GlobalPolicy` engine, per-game policy overrides, automated deployment on exit, automated reapply)
* [x] **Phase H**: UI Refinement & Restore All (Minimal static tray menu, view filtering, global Restore All with error isolation)
* [x] **Release CI**: Standalone self-contained `win-x64` GitHub release pipeline

> [!NOTE]
> Completed checkboxes describe historical implementation milestones and exercised synthetic coverage, not closure of every specification requirement or release readiness. In particular, restart/power-loss transaction recovery and D3D10 payload mapping remain unresolved. The following product safety repairs and policy choices have been integrated on `main`:
> * **Reapply Baseline Preservation**: Baseline capture and backup preservation defects repaired ([Issue #13](https://github.com/Tiflit/DXVK-Companion/issues/13), [PR #23](https://github.com/Tiflit/DXVK-Companion/pull/23)).
> * **Shared-Directory Compatibility Policy**: Approved installation-wide DXVK refusal across shared directories when any recorded executable is incompatible (DX12, Vulkan, or Unknown/unsupported), while preserving Restore and RestoreAll operations ([Issue #14](https://github.com/Tiflit/DXVK-Companion/issues/14), [PR #35](https://github.com/Tiflit/DXVK-Companion/pull/35); undiscovered sibling executables remain a known limitation).
> * **Pre-Execution API Reassessment**: Reassesses queued actions against the latest available recorded API evidence prior to execution, preserving conservative unsupported and conflicting classifications; this reconciles stored profile and installation records but does not perform fresh runtime process scanning or ensure detection of late-loaded APIs ([Issue #15](https://github.com/Tiflit/DXVK-Companion/issues/15), [PR #36](https://github.com/Tiflit/DXVK-Companion/pull/36)).
> * **Incompatible Pending Action Lifecycle**: Terminal cancellation lifecycle for compatibility-refused pending actions, preventing automatic revival without explicit user intent ([Issue #16](https://github.com/Tiflit/DXVK-Companion/issues/16), [PR #37](https://github.com/Tiflit/DXVK-Companion/pull/37)).
> * **Clean-Slate V1 & Persistence Alignment**: `GraphicsApi` name serialization and clean-slate V1 initialization without legacy profile import ([Issue #18](https://github.com/Tiflit/DXVK-Companion/issues/18), [PR #30](https://github.com/Tiflit/DXVK-Companion/pull/30); [Issue #32](https://github.com/Tiflit/DXVK-Companion/issues/32), [PR #34](https://github.com/Tiflit/DXVK-Companion/pull/34)).
> * **ProfileStore Staged-Save Persistence**: Replaced direct-stream truncation vulnerability with same-directory temporary staging followed by single-file replacement ([Issue #101](https://github.com/Tiflit/DXVK-Companion/issues/101)/[Issue #103](https://github.com/Tiflit/DXVK-Companion/issues/103), [PR #102](https://github.com/Tiflit/DXVK-Companion/pull/102)).
> * **GameLibraryStore Operation-Owned Staging**: Replaced fixed `.tmp` naming with unique GUID-qualified staging paths, isolating operations and preserving sibling recovery files on partial write ([Issue #104](https://github.com/Tiflit/DXVK-Companion/issues/104), [PR #105](https://github.com/Tiflit/DXVK-Companion/pull/105)).
> * **Newer-Schema Refusal & Startup Preflight**: Preflight inspection refuses startup on explicit newer library schemas and guards all storage mutations, preserving newer data files and backups byte-for-byte intact without downgrade overwrite ([Issue #106](https://github.com/Tiflit/DXVK-Companion/issues/106)/[Issue #107](https://github.com/Tiflit/DXVK-Companion/issues/107), [PR #108](https://github.com/Tiflit/DXVK-Companion/pull/108)).

---

## 🤖 AI Development & Workflow

This project uses a GitHub-native multi-agent development workflow with GitHub as the durable source of truth:
* **Model Role Allocation**:
  * **Gemini**: Primary implementation layer (repository code, test suites, local verification, focused revisions).
  * **ChatGPT**: Verification, architecture, reproduction analysis, and arbitration layer.
  * **Claude**: Reserved for occasional independent audits and high-risk material decisions (quota-conserving).
  * **Human**: Retains final merge authority, policy governance, and architectural decisions.
* **Workflow Navigation**:
  * [Agent Operating Rules](AGENTS.md): Core rules, 4-step startup route, and repository invariants for AI contributors.
  * [Live AI Dashboard (Issue #27)](https://github.com/Tiflit/DXVK-Companion/issues/27): Automated machine-owned orientation dashboard maintaining an automatically refreshed snapshot of default branch SHA, open PRs, and active CI runs. Underlying GitHub records (commits, PRs, Issues) remain authoritative.
  * [Curated Orientation & State](docs/AI-CURRENT-STATE.md): Curated guidance, stable decisions, role allocation, and governance protocols.
  * [AI Development Workflow](docs/AI-DEVELOPMENT-WORKFLOW.md): Operating policies, review standards, and contract grammar.
  * [Agent Activity Journal](docs/AI-ACTIVITY-JOURNAL.md) and [`docs/ai-journal/`](docs/ai-journal/): Session logs and activity records.
  * [Pilot & Review Log](docs/AI-PILOT-LOG.md): Selective historical reference for pilot outcomes and review arbitration.

Role allocation follows current human/task assignments; these descriptions do not activate execution or change human merge authority.

---

## Companion Suite

DXVK-Companion is being completed as a standalone DXVK management tool first. [Vulkan-Companion](https://github.com/Tiflit/Vulkan-Companion) is the planned simple central interface for independent modules; hub integration follows a working DXVK module. Modules should remain independently usable where practical, with prerequisites documented.

[Neural-Companion](https://github.com/Tiflit/Neural-Companion) remains a research project for Vulkan-based access to rendering inputs and future enhancement-provider integration. Optional management integration is a later possibility. Native Vulkan enhancement does not require DXVK translation; translated Direct3D routes can require separately installed DXVK. No Neural provider, universal game support or hub integration is delivered by this application.

## License & Credits

Project-authored DXVK-Companion material is available under the [MIT License](LICENSE). [DXVK](https://github.com/doitsujin/dxvk) is developed independently by its upstream contributors and retains its own [zlib/libpng licence](https://github.com/doitsujin/dxvk/blob/b1a1c99ab52b687cf950d62c88bc2fa316b41663/LICENSE). Other third-party components retain their respective terms; Companion's MIT licence does not relicense upstream runtimes.

The official project source and future release location is [Tiflit/DXVK-Companion](https://github.com/Tiflit/DXVK-Companion). This project is independent and is not affiliated with or endorsed by the DXVK authors, Microsoft, Intel, NVIDIA or AMD. Courtesy credit and a link to the official repository are appreciated when sharing the project; they add no conditions to its MIT licence.
