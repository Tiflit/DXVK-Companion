# DXVK-Companion

A lightweight, fully portable, and self-cleaning Windows utility that detects game launches, identifies DirectX graphics APIs, and safely manages [DXVK](https://github.com/doitsujin/dxvk) deployment, updates, rollbacks, and configuration.

Optimized for modern GPUs—especially Intel Arc / Battlemage architectures (Arc B580 and Xe2)—while fully supporting Nvidia GeForce and AMD Radeon hardware running legacy Direct3D 9, 10, and 11 games.

---

## ⚡ Core Principles

* **Strict Portability**: Completely self-contained in its application directory. Never writes to `%APPDATA%`, the Windows Registry, or system directories. Perfect for USB drives and portable libraries.
* **Self-Cleaning Game Directories**: Game directories remain pristine. Original DLLs are backed up exclusively inside Companion's isolated storage (`Profiles/Backups/`), never leaving `.bak` artifacts in game folders. On restore, injected DXVK DLLs and generated `dxvk.conf` files are cleanly deleted.
* **Atomic Multi-File Transactions**: Multi-file deployments (such as `d3d11.dll` + `dxgi.dll` for DirectX 11) are treated as a single logical transaction with SHA-256 pre-flight identity verification and automatic rollback if any file fails.
* **Zero External Dependencies**: Built entirely on .NET 8 using native Windows APIs and built-in runtime features (including in-memory tarball extraction via `GZipStream` and `System.Formats.Tar`).
* **Non-Aggressive Execution**: Never modifies running game processes. Deployment actions are staged and executed safely after the game cleanly terminates.
* **Anti-Cheat Safety**: Detects anti-cheat modules (Easy Anti-Cheat, BattlEye, Vanguard, etc.) and guards single-player titles from risky modifications.

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
                  SYSTEM TRAY INTERFACE
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
  * `GameLibraryStore`: Atomic JSON persistence for game libraries with corruption recovery.
  * `CacheStore` & `SettingsStore`: Portable configuration and release caching.

* **Detection & Monitoring (`DXVKCompanion.Monitoring`)**:
  * `ProcessMonitor`: Polling process monitor with window wait retries and rich `DetectionSnapshot` generation.
  * `GameDetector`: Filters out system processes and store launchers, and assesses anti-cheat risks (`AntiCheatAssessment`) across process modules and game directories.
  * `ModuleScanner`: Inspects loaded graphics runtime modules in running processes.
  * `PeParser`: Static PE header and Import Address Table (IAT) inspection fallback with architecture detection.
  * `ApiClassifier`: Classifies Direct3D 9, 10, 11, and Modern API (DX12 / Vulkan) games with confidence levels and evidence tracking (`ApiClassificationResult`).

* **DXVK Management (`DXVKCompanion.DXVK`)**:
  * `DxvkGithubClient`: Fetches official release metadata from the GitHub API with local caching.
  * `DxvkInstaller`: In-memory extraction of release archives into isolated cache and atomic game deployment.
  * `DxvkRollback`: Clean restoration of original game baselines and deletion of injected DXVK files.
  * `DxvkConfigManager`: Manages `dxvk.conf` settings (HUD overlay and frame limiters).

* **User Interface (`DXVKCompanion.UI`)**:
  * `TrayApp` & `TrayMenu`: Lightweight system tray control.
  * `GameDetailsWindow` & `ManageGamesWindow`: Per-game settings and library management.

---

## 📂 Portable Directory Structure

```text
DXVK-Companion/
├── Profiles/
│   ├── game-library.json   # Hierarchical game installations and managed file records
│   ├── games.json          # Legacy profile configuration
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

---

## 🗺️ Project Specifications & Roadmap

For complete specifications and architectural contracts, refer to the project documents:
* [Master Project Specification](DXVK-COMPANION-SPEC-A1-UPDATED.md)
* [Phase A.5 Safety & Identity Design](DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md)

### Development Progress
* [x] **Phase A**: Data Foundation (Hierarchical `GameInstallation`, `ExecutableProfile`, `ManagedFileRecord`)
* [x] **Phase A.1**: Pre-Release Legacy Migration Cleanup
* [x] **Phase A.5**: Multi-File Atomic Transaction Engine (`MultiFileTransactionEngine`, `FileIdentity`)
* [x] **Phase D (Integration)**: Safe File Engine Integration (`DxvkInstaller` and `DxvkRollback` wired to transaction engine, isolated backups, and clean self-cleaning)
* [x] **Phase B**: Detection Layer Refactoring (Multi-executable folder tracking, delayed runtime scans, enhanced anti-cheat heuristics, and API transitions)
* [ ] **Phase C**: DXVK Release Repository (Official release catalog, deterministic hash identification, and existing DXVK adoption)
* [ ] **Phase E & F**: UI Modernization & Delayed Notifications
* [ ] **Phase G**: Automated Maintenance Mode
* [ ] **Future Goal**: Opt-in global crowd-sourced game & GPU compatibility catalog
