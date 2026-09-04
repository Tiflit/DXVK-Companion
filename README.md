DXVK‑Companion
A lightweight, fully portable Windows tray application that detects game launches, identifies DXVK‑compatible DirectX APIs, and automatically manages DXVK deployment, updates, rollbacks, and per‑game configuration.
Designed primarily for Intel Battlemage GPUs (Arc B580 and similar), but works on any GPU.

✨ Overview
DXVK‑Companion automates DXVK management for Windows games.
It detects when a game launches, determines whether it uses a DXVK‑compatible API (DX9, DX10, DX11), and lets you enable or disable DXVK with a single click.

DXVK‑Companion is:

Fully portable — no files written to %APPDATA%, registry, or system folders

Self‑contained — all configuration, cache, logs, and DXVK data live inside the app folder

Safe — DLL backups and rollbacks prevent accidental game corruption

Automated — detects games in real time and applies DXVK on next launch

Modern — built on .NET 8 with in‑memory tar extraction and clean architecture

Ideal for Intel Arc users who want Vulkan performance for DX9/DX11 titles without manual setup.

🎮 Key Features
Real‑Time Game Detection
DXVK‑Companion monitors running processes and identifies games using:

PE header inspection

Loaded module scanning

DXVK‑compatible API classification (DX9 / DX10 / DX11 / ModernAPI)

Launchers (Steam, Epic, Origin, Ubisoft, etc.) are ignored.

One‑Click DXVK Management
From the tray menu:

Enable DXVK

Disable DXVK and restore original DLLs

View per‑game settings

Toggle HUD and frame limits

Check for DXVK updates

DXVK is applied safely and takes effect on next launch.

In‑Memory DXVK Extraction
DXVK releases are downloaded directly from GitHub and extracted in memory using:

GZipStream

TarReader (System.Formats.Tar)

No temporary files, no leftover archives.

Fully Portable Storage
All app data lives inside the DXVK‑Companion folder:

Code
DXVK-Companion/
│
├── Profiles/      # Per-game JSON profiles
├── Cache/         # Cached DXVK release metadata
├── Logs/          # Application logs
└── DXVK/          # Optional local DXVK cache
Perfect for:

USB drives

Modded game setups

Multiple Windows installations

Offline environments

Safe Rollbacks
Before injecting DXVK DLLs, the app automatically backs up originals:

Code
d3d11.dll → d3d11.dll.bak
dxgi.dll  → dxgi.dll.bak
Disabling DXVK restores the backups exactly.

Per‑Game Configuration
Each game gets its own profile:

API (DX9 / DX10 / DX11 / ModernAPI)

Architecture (x32 / x64)

DXVK enabled/disabled

Last installed DXVK version

HUD toggle

Frame limit

Profiles survive game reinstalls or folder moves.

⚠️ Anti‑Cheat Disclaimer
DXVK‑Companion replaces DirectX DLLs inside game folders.
This is NOT SAFE for online multiplayer titles with anti‑cheat systems such as:

Easy Anti‑Cheat (EAC)

BattleEye

Vanguard

FACEIT

Ricochet

Use DXVK‑Companion only with single‑player or offline games.  
You are responsible for ensuring DXVK is not used with protected titles.

🧠 Design Philosophy
1. Portability First
DXVK‑Companion never writes outside its own folder.
No registry, no %APPDATA%, no installers.

2. Clean Architecture
The project is divided into clear layers:

Monitoring

DXVK management

Storage

Models

Utilities

UI

Each layer is isolated and testable.

3. Safety Over Aggression
DXVK is never injected into a running game.
Changes are staged and applied after the game exits.

4. Zero External Dependencies
Only built‑in .NET 8 libraries and native Windows APIs are used.

📐 Architecture Diagram
Lifecycle Overview (Mermaid)
mermaid
sequenceDiagram
    participant PM as ProcessMonitor
    participant GD as GameDetector
    participant AC as ApiClassifier
    participant UI as TrayApp
    participant DX as DxvkManager

    PM->>GD: New process detected
    GD->>AC: Inspect executable (PE + modules)
    AC->>UI: API classification result
    UI->>DX: User enables DXVK
    DX->>DX: Stage DXVK deployment
    PM->>DX: ProcessExit event
    DX->>DX: Apply DXVK safely (backup + inject)
🧱 Architecture Summary
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

🚀 Build Instructions (Single‑File Portable EXE)
To build a self‑contained, single‑file, portable executable:

bash
dotnet publish src/DXVKCompanion/DXVKCompanion.csproj \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true
This produces:

Code
bin/Release/net8.0/win-x64/publish/DXVK-Companion.exe
No .NET runtime required.

📌 DXVK Compatibility Constraints
DXVK supports:

DirectX 9

DirectX 10

DirectX 11

DXVK does not support:

DirectX 12

Vulkan

OpenGL

DirectDraw (unless wrapped by dgVoodoo2 → DX11 → DXVK)

For Intel Arc GPUs:

DX9 → huge improvement

DX11 → often improved

DX12 → excellent natively

Vulkan → excellent natively

DXVK‑Companion applies DXVK only to DX9/DX10/DX11 titles.

📦 What Has Been Implemented
Full project structure

Portable filesystem

Monitoring layer

DXVK download + in‑memory extraction

Safe DLL deployment + rollback

Per‑game profiles

GitHub release caching

Tray UI

Integration in Program.cs

Architecture documentation

🛠 What Has Not Been Implemented Yet
DXVK update checker integration

Post‑session sync logic

Architecture detection integration

Environment variable injection

Advanced DXVK settings

UI polish

DXVK fork support (async, gplasync, etc.)

Anti‑cheat safe mode

Release packaging

📅 Planned Features
Auto‑update DXVK on launch

Per‑game DXVK version pinning

Custom DXVK forks

Optional DXVK download mirror

Game launch history

Auto‑enable DXVK for new games

📄 License
DXVK‑Companion is licensed under the MIT License, allowing:

Free use

Free modification

Free redistribution

Commercial use
