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
