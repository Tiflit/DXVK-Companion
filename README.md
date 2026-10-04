# DXVK-Companion

A lightweight, fully portable, and self-cleaning Windows utility that detects game launches, identifies DirectX graphics APIs, and safely manages [DXVK](https://github.com/doitsujin/dxvk) deployment, updates, rollbacks, and configuration.

Optimized for modern GPUs—especially Intel Arc / Battlemage architectures (Arc B580 and Xe2)—while fully supporting Nvidia GeForce and AMD Radeon hardware running legacy Direct3D 9, 10, and 11 games.

---

## ⚡ Core Principles

* **Strict Portability**: Completely self-contained in its application folder. Never writes to `%APPDATA%`, the Windows Registry, or system directories (with the exception of optional Windows startup integration).
* **Self-Cleaning Game Directories**: Game directories remain pristine. Original DLLs are backed up exclusively inside Companion's isolated storage (`Profiles/Backups/{id}`), never leaving `.bak` artifacts in game folders. On restore, injected DXVK DLLs and generated `dxvk.conf` files are cleanly removed.
* **Atomic Multi-File Transactions**: Multi-file deployments (such as `d3d11.dll` + `dxgi.dll` for DirectX 11) are executed as a single logical transaction with SHA-256 pre-flight identity verification and automatic rollback if any file operation fails.
* **Zero External Dependencies**: Built on .NET 8 using native Windows APIs and runtime capabilities (including in-memory release tarball decompression via `GZipStream` and `System.Formats.Tar`).
* **Non-Aggressive Execution**: Never modifies running game processes. Deployment actions are staged and executed safely after the game cleanly terminates.
* **Anti-Cheat Safety**: Detects anti-cheat modules (Easy Anti-Cheat, BattlEye, Vanguard, etc.) with fail-closed heuristics (`UnableToDetermine` / `SuspectedOrKnown`) to protect online multiplayer titles from risky modifications.

---

## 🚀 Quick Start (Testing & Usage)

### Running Standalone
1. Download `DXVK-Companion-win-x64` from the latest [GitHub Actions Artifacts](https://github.com/Tiflit/DXVK-Companion/actions) or [GitHub Releases](https://github.com/Tiflit/DXVK-Companion/releases).
2. Extract the archive into any folder of your choice (e.g., `C:\Tools\DXVK-Companion`).
3. Run `DXVK-Companion.exe`.
4. Companion docks directly into the Windows system tray notification area (near the clock).

---

## 🎮 Operating Modes

DXVK Companion supports two operating modes configured globally in **Settings** or overridden per game:

### 1. Manual Mode (Default)
* Observes and reports game rendering APIs, architectures, and health states.
* Does not modify game files or deploy DXVK without explicit user confirmation.
* Direct control available via **Manage Games** and **Game Details** dialogs.

### 2. Automated Mode (Experimental)
* Automatically selects and deploys the latest official DXVK release for newly launched compatible Direct3D games.
* Queues safe deployment while the game is running and applies the transaction automatically upon game exit.
* Detects external game patches or file updates and automatically re-evaluates the baseline before reapplying DXVK.
* Respects fail-closed anti-cheat protection: automatic actions are strictly blocked if anti-cheat or anti-tamper components are detected.

---

## 🖥️ User Interface Overview

### System Tray Menu (Section 39)
Right-clicking the tray icon presents a clean, static, and predictable menu:
* **Status Header**: Displays current application state.
* **Active Game**: Shows the active detected process, API, and architecture (or *No Supported Game Running* when idle).
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
* **Frame Limiter**: Set a custom framerate limit (writes `dxvk.frameRate` cleanly).
* **Performance HUD**: Toggle DXVK's built-in telemetry overlay (`dxvk.hud = fps,devinfo`).
* **Configuration Safety**: Merges settings cleanly into existing `dxvk.conf` files without overwriting user-defined options.

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
             (DX9, DX10, DX11, ModernAPI DX12/Vulkan)
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
  * `MultiFileTransactionEngine`: Executes atomic multi-file operations (`Install`, `Update`, `Reapply`, `Restore`), manages isolated backups, and provides automatic rollback upon failure.
  * `SingleFileTransactionEngine`: Atomic single-file state machine with crash recovery.
  * `FileIdentity`: Deterministic SHA-256 and byte-size identity tracking for file provenance and tampering detection.
  * `TransactionContracts`: Formal state machines and outcome records.

* **Domain & Storage (`DXVKCompanion.Models`, `DXVKCompanion.Storage`)**:
  * `GameInstallation`: Tracks installation roots, multiple executables, managed file records, and conflict flags.
  * `ManagedFileRecord`: Tracks original state (`Existing` vs. `DidNotExist`), baseline hashes, and backup pointers.
  * `ManagedFileInspector`: Real-time inspection of managed files, detecting external modifications, deletions, and invalidating stale pending actions.
  * `GameLibraryStore`: Atomic JSON persistence for game libraries with corruption recovery, name-based enum serialization (`GraphicsApi`), legacy `ModernAPI` compatibility, and automatic snapshot recovery (`.recovery.*.json`) if downgraded builds encounter unrecognized API values (`DX12` / `Vulkan`).
  * `ProfileStore`: Flat profile persistence using pinned integer ordinals (`0..6`), where unrecognized numeric values deserialize safely without exception.
  * `CacheStore` & `SettingsStore`: Portable configuration, release caching, and global policy persistence.

* **Detection & Monitoring (`DXVKCompanion.Monitoring`)**:
  * `ProcessMonitor`: Polling process monitor with window wait retries and rich `DetectionSnapshot` generation.
  * `GameDetector`: Filters out system processes and store launchers, and assesses anti-cheat risks (`AntiCheatAssessment`) across process modules and game directories.
  * `ModuleScanner`: Inspects loaded graphics runtime modules in running processes.
  * `PeParser`: Static PE header and Import Address Table (IAT) inspection fallback with architecture detection.
  * `ApiClassifier`: Classifies Direct3D 9, 10, 11, and Modern API (DX12 / Vulkan) games with confidence levels and evidence tracking (`ApiClassificationResult`).

* **DXVK Management (`DXVKCompanion.DXVK`)**:
  * `DxvkGithubClient`: Fetches official release metadata from the GitHub API with local caching.
  * `DxvkReleaseCatalog`: Deterministic SHA-256 hash lookup for official DXVK releases.
  * `ExistingDxvkDetector`: Inspects game directories, PE version metadata, and SHA-256 hashes to reliably recognize official releases, unknown builds, or native files.
  * `DxvkInstaller`: In-memory extraction of release archives, atomic game deployment, configuration staging, existing DXVK adoption, and reapplication.
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

```text
DXVK-Companion/
├── DXVK-Companion.exe      # Standalone single-file executable
├── Profiles/
│   ├── game-library.json   # Hierarchical game installations and managed file records
│   ├── games.json          # Legacy profile configuration (migrated automatically)
│   └── Backups/            # Pristine original game file baselines (isolated from game folders)
├── Cache/                  # Cached DXVK release metadata
├── Logs/                   # Application log files
└── DXVK/                   # Extracted official DXVK release binaries (x32 and x64)
```

---

## 🧪 Testing & Validation

All file safety operations are validated using sandboxed synthetic environments (`SyntheticTestDirectory`) that guarantee tests never touch real game installations or developer workspaces:

```bash
# Build application in Release
dotnet build src/DXVKCompanion/DXVKCompanion.csproj --configuration Release

# Build test suite in Release
dotnet build tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj --configuration Release

# Run automated tests
dotnet test tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj --configuration Release --no-build --no-restore
```

Continuous integration runs automatically on Windows runners via GitHub Actions (`.github/workflows/build-and-test.yml`).
Automated standalone self-contained packaging is built on tag push via `.github/workflows/release.yml`.

---

## 🗺️ Project Specifications & Roadmap

For complete specifications and architectural contracts, refer to the canonical specification and safety supplement:
* [Canonical Project Specification](docs/spec/DXVK-COMPANION-SPEC.md)
* [Phase A.5 Safety & Identity Design (Normative Supplement)](docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md)

> [!NOTE]
> **Specification Authority & Precedence**: [`docs/spec/DXVK-COMPANION-SPEC.md`](docs/spec/DXVK-COMPANION-SPEC.md) is the canonical project specification, approved under [Issue #12](https://github.com/Tiflit/DXVK-Companion/issues/12). [`docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md`](docs/spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md) serves as its normative safety and identity supplement. Superseded historical specifications are archived under `docs/spec/archive/`.

### Development Progress & Verification Status
* [x] **Phase A**: Data Foundation (Hierarchical `GameInstallation`, `ExecutableProfile`, `ManagedFileRecord`)
* [x] **Phase A.1**: Legacy Profile Migration (`games.json` -> `game-library.json`)
* [x] **Phase A.5**: Multi-File Atomic Transaction Engine (`MultiFileTransactionEngine`, `FileIdentity`)
* [x] **Phase B**: Detection Layer Refactoring (Multi-executable folder tracking, delayed runtime scans, enhanced anti-cheat heuristics, and API transitions)
* [x] **Phase C**: DXVK Release Repository (Official release catalog, deterministic hash identification, existing DXVK adoption, Reapply, and Section 36 `dxvk.conf` management)
* [x] **Phase D**: External-Change & Pending-Action Handling (`ManagedFileInspector`, Section 20 supersession, baseline replacement, and persistent restart handling)
* [x] **Phase E & F**: UI Modernization & Delayed Notifications (Status-oriented management UI, adoption/reapply buttons, health badges, startup inspection, and delayed balloon notifications)
* [x] **Phase G**: Automated Maintenance Mode (`GlobalPolicy` engine, per-game policy overrides, automated deployment on exit, automated reapply)
* [x] **Phase H**: UI Refinement & Restore All (Minimal static tray menu, view filtering, global Restore All with error isolation)
* [x] **Release CI**: Standalone self-contained `win-x64` GitHub release pipeline

> [!NOTE]
> Completed checkboxes reflect implementation and unit test coverage in Phase A. Active product safety investigations (such as potential baseline overwrite during Reapply under [Issue #13](https://github.com/Tiflit/DXVK-Companion/issues/13)) and multi-executable policy choices ([Issue #14](https://github.com/Tiflit/DXVK-Companion/issues/14)) are tracked as open issues.

---

## 🤖 AI Development & Workflow

This project uses a GitHub-native multi-agent development workflow:
* [Agent Operating Rules](AGENTS.md): Core rules for AI contributors
* [Current Development State & Handoff](docs/AI-CURRENT-STATE.md): Active dashboard, branch heads, and work queue
* [AI Development Workflow](docs/AI-DEVELOPMENT-WORKFLOW.md): Operating policies, review standards, and contract grammar
* [Agent Activity Journal](docs/AI-ACTIVITY-JOURNAL.md): Session logs and activity records
* [Pilot & Review Log](docs/AI-PILOT-LOG.md): Historical record of review findings and arbitration

