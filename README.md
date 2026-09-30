# DXVK-Companion

A lightweight, fully portable, and self-cleaning Windows utility that detects game launches, identifies DirectX graphics APIs, and safely manages [DXVK](https://github.com/doitsujin/dxvk) deployment, updates, rollbacks, and configuration.

Optimized for modern GPUs—especially Intel Arc / Battlemage architectures (Arc B580 and Xe2)—while fully supporting Nvidia GeForce and AMD Radeon hardware running legacy Direct3D 9, 10, and 11 games.

---

## ⚡ Core Principles

* **Strict Portability**: Completely self-contained in its application directory. Never writes to `%APPDATA%`, the Windows Registry, or system directories (with the exception of optional Windows startup integration).
* **Self-Cleaning Game Directories**: Game directories remain pristine. Original DLLs are backed up exclusively inside Companion's isolated storage (`Profiles/Backups/`), never leaving `.bak` artifacts in game folders. On restore, injected DXVK DLLs and generated `dxvk.conf` files are cleanly deleted.
* **Atomic Multi-File Transactions**: Multi-file deployments (such as `d3d11.dll` + `dxgi.dll` for DirectX 11) are treated as a single logical transaction with SHA-256 pre-flight identity verification and automatic rollback if any file fails.
* **Zero External Dependencies**: Built entirely on .NET 8 using native Windows APIs and built-in runtime features (including in-memory tarball extraction via `GZipStream` and `System.Formats.Tar`).
* **Non-Aggressive Execution**: Never modifies running game processes. Deployment actions are staged and executed safely after the game cleanly terminates.
* **Anti-Cheat Safety**: Detects anti-cheat modules (Easy Anti-Cheat, BattlEye, Vanguard, etc.) with fail-closed heuristics (`UnableToDetermine` / `SuspectedOrKnown`) to guard online multiplayer titles from risky modifications.

---

## 🎮 Operating Modes

DXVK Companion supports two operating modes configured globally in **Settings** or overridden per game:

### 1. Manual Mode (Default)
* Observes and reports game rendering APIs, architectures, and health states.
* Does not automatically modify game files or deploy DXVK without explicit user confirmation.
* Full control via **Manage Games** and **Game Details** dialogs.

### 2. Automated Mode (Experimental)
* Automatically selects and deploys the latest official DXVK release for newly launched compatible Direct3D games.
* Queues safe deployment while the game is running and applies the transaction automatically upon game exit.
* Detects external game patches or file updates and automatically re-evaluates the baseline before reapplying DXVK.
* Respects fail-closed anti-cheat protection: automatic actions are blocked if anti-cheat or anti-tamper components are detected.

---

## 🏗️ Architecture Overview

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
  * `GameLibraryStore`: Atomic JSON persistence for game libraries with corruption recovery and seamless legacy `games.json` migration.
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
  * `ManageGamesWindow`: Status-oriented management UI with real-time health badges, view filtering (`Active Games`, `Managed`, `Attention Required`, `Hidden`), adoption, reapplication, and **Restore All**.
  * `GameDetailsWindow`: Game health banner, frame limiting, HUD overlay toggles, per-game policy selection, and hidden status toggling.
  * `SettingsWindow`: Global management policy toggle (Manual vs. Automated Experimental) and startup control.

---

## 📂 Portable Directory Structure

```text
DXVK-Companion/
├── Profiles/
│   ├── game-library.json   # Hierarchical game installations and managed file records
│   ├── games.json          # Legacy profile configuration (migrated automatically)
│   └── Backups/            # Pristine original game file baselines (isolated from game folders)
├── Cache/                  # Cached DXVK release metadata
├── Logs/                   # Application log files
└── DXVK/                   # Extracted DXVK release binaries (x32 and x64)
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

Continuous integration runs automatically on Windows via GitHub Actions (`.github/workflows/build-and-test.yml`).
Automated standalone self-contained packaging is built on tag push via `.github/workflows/release.yml`.

---

## 🗺️ Project Specifications & Roadmap

For complete specifications and architectural contracts, refer to the authoritative specification document:
* [Master Project Specification (Revised 2)](DXVK-COMPANION-SPEC-REVISED2.md)
* [Phase A.5 Safety & Identity Design](DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md)

### Development Progress
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
