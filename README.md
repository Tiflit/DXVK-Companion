# DXVK-Companion
A lightweight Windows tray application that detects game launches, identifies DXVK‑compatible APIs, and automatically manages DXVK deployment, updates, rollbacks, and per‑game configuration. Includes real‑time process monitoring, GitHub release caching, and dxvk.conf/HUD controls.


DXVK‑Companion
A fully portable Windows tray utility that detects running games, identifies DXVK‑compatible APIs, and manages DXVK deployment automatically.


I am currently building this tool to automate DXVK management - aimed at Intel Battlemage GPUs (but should work for any GPU)


Overview
DXVK‑Companion is a lightweight, self‑contained Windows tray application designed to make DXVK management effortless.
It automatically detects when a game launches, determines whether it uses a DXVK‑compatible DirectX API (DX9 or DX11), and lets you enable, disable, or update DXVK with a single click.

DXVK‑Companion is:

Fully portable — no files written to %APPDATA%, registry, or system folders

Self‑contained — all configuration, cache, logs, and DXVK data live inside the app folder

Safe — DLL backups and rollbacks prevent accidental game corruption

Automated — detects games in real time and applies DXVK on next launch

Modern — built on .NET 8 with in‑memory tar extraction and clean architecture

DXVK‑Companion is ideal for users who want DXVK automation without relying on external managers or modifying their system.

Key Features
🎮 Real‑Time Game Detection
DXVK‑Companion monitors running processes and identifies games using:

PE header inspection

Loaded module scanning

DXVK‑compatible API classification (DX9 / DX11 / ModernAPI)

It ignores launchers (Steam, Epic, Origin, etc.) and only reacts to actual games.

🔧 One‑Click DXVK Management
From the tray menu, you can:

Enable DXVK for the active game

Disable DXVK and restore original DLLs

View per‑game settings

Toggle HUD and frame limits

Check for DXVK updates

DXVK is applied safely and only takes effect on the next launch.

📦 In‑Memory DXVK Extraction
DXVK releases are downloaded directly from GitHub and extracted in memory using:

GZipStream

TarReader (System.Formats.Tar)

No temporary files, no external dependencies, no leftover archives.

🗂 Fully Portable Storage
All app data lives inside the DXVK‑Companion folder:

Code
DXVK-Companion/
│
├── Profiles/      # Per-game settings
├── Cache/         # Cached DXVK release metadata
├── Logs/          # Application logs
└── DXVK/          # Optional local DXVK cache
This makes the app ideal for:

USB drives

Modded game setups

Multiple Windows installations

Offline environments

♻️ Safe Rollbacks
Before injecting DXVK DLLs, the app automatically backs up originals:

Code
d3d11.dll → d3d11.dll.bak
dxgi.dll  → dxgi.dll.bak
Disabling DXVK restores the backups exactly.

📈 Per‑Game Configuration
Each game gets its own profile:

API (DX9 / DX11 / ModernAPI)

Architecture (x32 / x64)

DXVK enabled/disabled

Last installed DXVK version

HUD toggle

Frame limit

Profiles are stored in JSON and survive game reinstalls or folder moves.

Design Philosophy
1. Portability First
DXVK‑Companion must never write outside its own folder.
This ensures:

No system pollution

No registry changes

No leftover files after deletion

No dependency on user profile paths

No risk of breaking modded game setups

All data is local, predictable, and easy to back up.

2. Clean Architecture
The project is divided into clear layers:

Monitoring — process detection, module scanning, API classification

DXVK — download, extraction, installation, rollback

Storage — profiles, cache, paths

Models — data structures

Utils — logging, PE parsing, file operations

UI — tray icon, windows, notifications

Each layer is isolated and testable.

3. Safety Over Aggression
DXVK is never injected into a running game.
Changes are staged and applied after the game exits, preventing:

File lock conflicts

Crashes

Corrupted DLLs

Unexpected behavior

4. Zero External Dependencies
The app uses:

Built‑in .NET 8 libraries

Native Windows APIs

No third‑party DLLs

No external installers

This keeps the project small, secure, and easy to maintain.

Architecture Summary




Monitoring Layer
ProcessMonitor — polls processes, detects new games

GameDetector — filters launchers

ModuleScanner — checks loaded modules

ApiClassifier — determines DXVK compatibility

ProcessExitHandler — triggers post‑session sync

DXVK Layer
DxvkGithubClient — fetches releases

DxvkReleaseCache — 24h TTL cache

DxvkInstaller — safe DLL deployment

DxvkRollback — restores backups

DxvkConfigManager — writes dxvk.conf

Storage Layer
ProfileStore — per‑game JSON profiles

CacheStore — DXVK release cache

Paths — portable directory management

Models
GameProfile

DxvkState

ReleaseInfo

CachedRelease

Utils
Logger

PeParser

FileUtils

EnvironmentUtils

UI
TrayApp

TrayMenu

SettingsWindow

GameDetailsWindow

UpdateNotification

What Has Been Implemented So Far
✔ Full project structure
Every layer has been scaffolded with clean, modular C# code.

✔ Portable filesystem
All data is stored inside the app folder.

✔ Monitoring layer
Game detection, module scanning, API classification.

✔ DXVK layer
Download, extraction, installation, rollback, config generation.

✔ Storage layer
Profiles and cache stored locally in JSON.

✔ UI layer
Tray icon, menu, settings windows, notifications.

✔ Integration
All layers wired together in Program.cs.

✔ Architecture documentation
This README summarizes the entire design.

Planned Features
Auto‑update DXVK on launch

Per‑game DXVK version pinning

Custom DXVK forks (async, gplasync, etc.)

Optional DXVK download mirror

Game launch history

Optional DXVK auto‑enable for new games

License
DXVK‑Companion is licensed under the MIT License, allowing:

Free use

Free modification

Free redistribution

Commercial use







1. Project Goal
DXVK‑Companion is a fully portable Windows tray application designed to automatically detect games, determine their DirectX API, and manage DXVK deployment safely and predictably.
The primary motivation is to improve performance on Intel Arc GPUs (specifically the Arc B580), where DX9 and DX11 performance is inconsistent and Vulkan performance is significantly better.

The tool aims to:

Detect games in real time

Identify DXVK‑compatible APIs (DX9, DX10, DX11)

Allow users to enable or disable DXVK with one click

Apply DXVK safely on the next game launch

Store all data inside the application folder

Avoid any external configuration or system modification

Remain compatible with modded games, portable installs, and multiple launchers

The design emphasizes safety, portability, and zero user configuration.

2. DXVK Compatibility Constraints
DXVK supports:

DirectX 9 → Vulkan

DirectX 10 → Vulkan

DirectX 11 → Vulkan

DXVK does not support:

DirectX 12

Vulkan (native)

OpenGL

DirectDraw / DX7 / DX8 (unless wrapped by dgVoodoo2 → DX11 → DXVK)

For Intel Arc GPUs:

DX9 performance is weak → DXVK helps significantly

DX11 performance is inconsistent → DXVK often stabilizes it

DX12 performance is excellent → no need for DXVK

Vulkan performance is excellent → no need for DXVK

Therefore, the tool should apply DXVK to any game that does not ship with Vulkan or DX12, which aligns perfectly with the project’s goals.

3. Key Design Choice: Apply DXVK on Next Launch
We discussed whether DXVK could be applied before the game launches, so users wouldn’t need to restart the game after enabling it.

After evaluating all options, we concluded:

DXVK must be applied on the next launch.
This is the correct design choice because:

Technical limitations
Once a game starts, Windows has already loaded d3d11.dll or d3d9.dll into memory.

Replacing DLLs in the folder has no effect until the next launch.

DXVK cannot “hot‑swap” into a running process.

Non‑intrusive design
Pre‑launch injection requires:

Process suspension

DLL injection

Launcher hooking

Wrapper executables

Admin rights

Modifying game EXEs

All of these violate the project’s goals of:

Zero configuration

Zero system modification

Full portability

Anti‑cheat safety

Mod manager compatibility

User experience
Applying DXVK on next launch is:

Predictable

Safe

Easy to understand

Compatible with all launchers

Compatible with modded games

Compatible with portable installs

This design is ideal for Intel Arc users who frequently relaunch games while testing performance.

4. Portability Model
A major architectural decision was to make DXVK‑Companion fully portable.

This means:

No files written to %APPDATA%, %LOCALAPPDATA%, registry, or ProgramData

No system‑wide environment variables

No installer

No external dependencies

All data is stored inside the application folder:

Code
DXVK-Companion/
│
├── Profiles/      # Per-game JSON profiles
├── Cache/         # Cached DXVK release metadata
├── Logs/          # Application logs
└── DXVK/          # Optional local DXVK cache
This ensures:

Users can delete the folder without leftovers

Users can move the folder anywhere

Users can use mod managers safely

Game reinstalls do not affect DXVK‑Companion

DXVK‑Companion does not break if game files move or change

This portability model is one of the strongest aspects of the design.

5. Architecture Overview
The project follows a clean, modular architecture with clear separation of concerns.

Monitoring Layer
Detects running processes

Filters out launchers

Scans loaded modules

Classifies DirectX API

Handles process exit events

DXVK Layer
Downloads DXVK releases from GitHub

Extracts .tar.gz archives in memory

Installs DXVK DLLs safely

Creates backups (.bak)

Restores original DLLs

Writes per‑game dxvk.conf

Storage Layer
Stores per‑game profiles

Stores DXVK release cache (24h TTL)

Manages portable paths

Models
GameProfile

DxvkState

ReleaseInfo

CachedRelease

Utils
Logger

PE parser (imports + architecture detection)

File utilities (backup/restore/copy)

Environment variable builder

UI Layer
Tray icon

Tray menu

Settings window

Per‑game configuration window

Update notifications

Integration
All layers are wired together in Program.cs.

6. What Has Been Implemented So Far
✔ Full project structure
All folders and files are created and organized.

✔ Monitoring layer
Game detection, module scanning, API classification.

✔ DXVK layer
Download, in‑memory extraction, installation, rollback, config generation.

✔ Storage layer
Portable JSON profiles and cache.

✔ Models layer
All data structures implemented.

✔ Utils layer
Logging, PE parsing, file operations.

✔ UI layer
Tray icon, menu, settings windows, notifications.

✔ Full integration
Program.cs wires all layers together.

✔ Portability
All data stored inside the application folder.

✔ DXVK compatibility logic
DX9/DX11 detection and classification.

✔ Design documentation
A complete README has been generated.

7. What Has Not Been Implemented Yet
❌ DXVK update checker integration
The logic exists but is not yet connected to the UI.

❌ Post‑session sync logic
The event handler exists, but staged operations are not yet implemented.

❌ Architecture detection integration
PeParser.GetArchitecture() exists, but profiles do not yet store architecture automatically.

❌ Environment variable injection
EnvironmentUtils exists, but environment variables are not yet applied to game launches.

❌ Advanced per‑game settings
Frame limit and HUD toggles exist, but more DXVK options could be added.

❌ Error handling and logging polish
Logging exists but is minimal.

❌ UI polish
Settings window and game details window are placeholders.

❌ DXVK fork support
Async, gplasync, or custom forks are not yet supported.

❌ Anti‑cheat safe mode
No detection or warnings yet.

❌ Build pipeline
No release packaging or versioning yet.

8. Next Steps
1. Implement post‑session sync
When a user toggles DXVK while a game is running:

Stage the operation

Apply it immediately when the game exits

Notify the user

2. Integrate architecture detection
Automatically detect x32/x64 and store it in the profile.

3. Connect DXVK update checker
Notify users when a new DXVK version is available.

4. Expand per‑game configuration
Add more DXVK options:

Async

HUD options

Barrier behavior

Shader caching

Frame pacing

5. Improve UI
Add real settings

Add game list

Add DXVK version display

Add update button

6. Add error handling
Graceful handling of:

Missing files

Permission issues

Corrupted DLLs

Failed downloads

7. Prepare for release
Add versioning

Add build instructions

Add icon

Add screenshots

Add installer (optional)
