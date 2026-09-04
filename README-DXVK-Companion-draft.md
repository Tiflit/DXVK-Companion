# DXVK Companion

**A lightweight, portable Windows utility for automatically managing DXVK on compatible games.**

DXVK Companion is designed to make DXVK practical on systems where older DirectX games can perform poorly, particularly on modern GPUs such as Intel Arc.

The application runs quietly in the Windows system tray, watches for game launches, identifies the graphics API being used, keeps a profile for detected games, and can manage DXVK deployment, updates, configuration, and rollback without requiring third-party tools.

> **Development status: Pre-testing / Work in progress**
>
> The project is actively being developed and has **not yet entered formal compatibility testing**. Automatic DXVK enabling is intentionally marked **experimental** and is expected to evolve substantially as more games are tested.

---

## Why DXVK Companion exists

Modern GPUs can be very good at Vulkan and DirectX 12 while providing less satisfying performance with older graphics APIs. Intel Arc is one example where this can be particularly noticeable in older games, especially when CPU limitations are also involved.

DXVK can translate supported Direct3D workloads to Vulkan, which can make older games a better fit for modern hardware.

DXVK Companion is intended to remove as much of the manual work as possible:

- detect games automatically;
- determine which graphics API they use;
- identify whether DXVK is relevant;
- warn about potential anti-cheat risks;
- download and manage DXVK versions;
- apply DXVK only when it is safe to do so;
- preserve the user's game files;
- keep settings per game;
- notify the user about DXVK status and updates;
- eventually, automate the entire process with minimal interaction.

The goal is **simplicity and safety, not a large management interface**.

---

## Design goals

### Lightweight

DXVK Companion should have as little impact as practical while sitting in the tray.

Process monitoring is intended to remain inexpensive, and network checks should happen only when useful rather than continuously.

### Portable

DXVK Companion is designed to keep its own application data together with the application rather than requiring a conventional installation.

Game-specific changes are limited to the files required to use DXVK.

### Self-contained

The application is designed to operate without external helper programs. DXVK releases are downloaded and processed by the application itself using the .NET runtime and Windows APIs.

### Safety first

DXVK Companion should never make irreversible changes to a game without preserving a path back to the previous state.

DXVK changes are designed to be staged when a game is running and applied after the process exits rather than modifying DLLs underneath a running game.

Rollback and file-handling behavior are a major part of the project's ongoing development and testing.

### Automation first

Users should not have to maintain a complicated database of game profiles manually.

Profiles are intended primarily as application-managed records that allow DXVK Companion to remember what it has already detected and configured.

### Compatibility over novelty

DXVK Companion initially focuses on **DXVK-compatible Direct3D APIs**. DirectX 12 and Vulkan games should normally continue using their native rendering paths and should primarily receive informational notifications rather than DXVK deployment.

Support for additional translation layers may be considered later.

---

## Current capabilities

The current codebase already contains the foundation for the following functionality.

### Game and process detection

DXVK Companion monitors running Windows processes and filters out known launchers and obvious non-game processes.

It can inspect a detected application's executable and loaded modules to determine what graphics API it is associated with.

The monitoring layer includes:

- process monitoring;
- launcher filtering;
- executable/window checks;
- loaded-module scanning;
- PE import inspection;
- executable architecture detection;
- anti-cheat module detection;
- process-exit handling.

### Graphics API detection

The current detection system can identify or classify:

- DirectX 9;
- DirectX 10;
- DirectX 11;
- DirectX 12 / Vulkan as a modern-API category;
- unknown/unresolved cases.

Detection uses both runtime module information and executable import information where appropriate.

DXVK Companion does **not** attempt to apply DXVK to DirectX 12 or Vulkan titles.

### Per-game profiles

Detected games are stored as profiles containing information such as:

- executable path;
- executable name;
- graphics API;
- architecture;
- DXVK enabled/disabled state;
- installed DXVK version;
- HUD configuration;
- frame-limit configuration.

Profiles are stored locally as JSON.

### DXVK download and storage

DXVK Companion can retrieve DXVK releases from GitHub and maintain a local cache of release information.

DXVK archives are processed in memory using built-in .NET APIs rather than requiring a separate archive utility.

Extracted DXVK files can be stored locally by version and architecture for reuse.

### DXVK deployment

The current deployment logic supports the DXVK DLLs needed for supported Direct3D versions, including:

- `d3d9.dll` for DX9;
- `d3d11.dll` and `dxgi.dll` for DX11.

The correct x86/x64 DXVK files are selected according to the detected executable architecture.

### Queued changes for running games

When a game is running, DXVK Companion can defer a requested enable/disable operation rather than attempting to change the game's DLLs immediately.

Pending actions can then be processed after the game exits.

### Rollback support

The project contains backup and restore functionality for the DLLs it manages and can remove its generated `dxvk.conf` configuration when DXVK is disabled.

The exact long-term backup strategy is still being refined with game updates and mod compatibility in mind.

### Per-game DXVK configuration

The application can generate a portable `dxvk.conf` for a game profile.

Current configuration support includes:

- DXVK HUD on/off;
- DXVK frame limiter;
- architecture-related configuration handling.

### DXVK version checking and updates

DXVK release metadata is cached to reduce unnecessary network requests.

The application can determine whether an enabled game is using an older DXVK release and provides update-related functionality for managed games.

### Application update checking

DXVK Companion can also check for newer DXVK Companion releases on GitHub.

### Windows startup

A user-configurable startup option is present so DXVK Companion can launch with Windows. This exists specifically to support the project's automation goal.

### Tray application

The program runs as a Windows tray application and provides access to:

- game details;
- managed games;
- settings;
- DXVK management actions;
- application exit.

The tray interface is still being simplified and redesigned.

---

## Experimental automatic mode

The long-term goal of DXVK Companion is to make DXVK management largely automatic.

The current **Automatic DXVK enabling** option is therefore intentionally labeled **experimental**.

The current logic is conservative and considers factors including:

- whether automatic mode is enabled;
- whether the detected graphics API is compatible with DXVK;
- whether anti-cheat risk was detected;
- whether DXVK is already associated with the game.

The automatic-management rules are expected to evolve as compatibility testing expands.

This feature should not be considered stable yet.

---

## Anti-cheat safety

DXVK works by providing replacement graphics DLLs in the game environment. Some anti-cheat systems may react badly to this kind of modification.

DXVK Companion therefore attempts to detect known anti-cheat components and treat them as a safety concern.

When potential anti-cheat risk is detected, automatic DXVK deployment is intentionally restricted and the user can be warned before proceeding.

**DXVK Companion is not a guarantee that a game is safe to modify.** Users remain responsible for determining whether DXVK is appropriate for a particular title, especially online multiplayer games.

The detection list will evolve over time.

---

## Notifications

The intended notification experience is intentionally low-profile.

A detected game should be able to report information such as:

- current graphics API;
- DXVK status;
- whether DXVK is pending or successfully applied;
- whether a newer DXVK version is available;
- whether a potential anti-cheat risk was detected.

The desired experience is for a notification to appear shortly after a game launches, without demanding that the user constantly interact with the application.

The visual design of this notification system is still being developed.

---

## DXVK version management

Users should not be forced to use only the latest DXVK release.

A future version-management workflow is intended to support:

- downloading older DXVK releases;
- keeping multiple DXVK versions locally;
- selecting a preferred version for a specific game;
- pinning a known-good version when a newer release causes a regression;
- updating games individually rather than forcing every game onto the newest release.

The current project already has the underlying concepts for release retrieval, caching, and per-game DXVK state, but broader multi-version management is still under development.

---

## Frame limiting and the DXVK HUD

DXVK Companion is intended to make a small set of useful DXVK options accessible without forcing users to edit configuration files manually.

### Frame limiter

The planned default behavior is:

- **Disabled by default**;
- **120 FPS** as the default value when enabled.

The limit should be configurable per game.

This is intended to coexist with external limiters where possible, although compatibility with tools such as RTSS and with individual games' V-Sync behavior requires additional investigation and testing.

### Performance overlay

A per-game DXVK HUD toggle is already part of the project.

The exact information shown by the HUD and the best defaults are still being evaluated.

---

## Game updates, mods, and file safety

A major design requirement is that DXVK Companion should manage **DXVK**, not take ownership of the game itself.

The application should remain compatible with:

- normal game updates;
- game patches that replace rendering files;
- user-installed mods;
- manually managed game files.

In particular, rollback should not blindly restore an obsolete backup over a newer file that appeared later because of a game update or other legitimate user action.

The backup/rollback design is therefore still being refined around file identity, timestamps, and safe restoration rules.

---

## Detection across game updates

DXVK Companion should continue recognizing the same game when its files change during a normal update instead of creating a fresh profile every time the executable changes version.

The current project uses the executable path as an important part of its profile identity.

Longer-term, the detection system may need additional logic for cases such as:

- a game moving its executable;
- a major patch changing the rendering API;
- a game gaining DX12 support while retaining DX11;
- a launcher changing how the game executable is started.

For now, the priority is reliable detection and useful notifications rather than trying to predict every possible update pattern.

---

## Lightweight update checks

Network checks should not become a background performance cost.

DXVK release information is intended to be checked at application launch and/or at most once per day, using cached metadata when possible.

The project already uses local release caching and ETag-aware GitHub requests to reduce unnecessary traffic.

---

## Portable storage

The application is intended to keep its own data together with the application directory, including:

```text
DXVK-Companion/
├── DXVK/                 # Locally stored DXVK binaries
├── Profiles/             # Per-game profiles
├── Cache/                # Cached release metadata
├── Logs/                 # Application logs
├── settings.json         # Global settings
└── DXVK-Companion.exe
```

The optional Windows startup feature is the deliberate exception to the normal no-system-modification philosophy because enabling startup requires a Windows startup registration mechanism.

---

## Requirements

The project currently targets modern Windows systems and is being developed primarily with **Windows 11** in mind.

The application is built with **.NET 8** and is intended to be publishable as a self-contained Windows executable so that the end user does not have to install a separate .NET runtime.

DXVK releases are downloaded automatically when required.

No third-party archive manager or external helper application is intended to be required.

---

## Controller support

A controller-friendly interface would be useful for couch or handheld-style Windows setups, but controller navigation is **not a core requirement for the initial release**.

It may be added later without changing the underlying management model.

---

## Planned development

The project's future direction is focused on making the existing automation reliable before adding a large number of extra features.

### Near-term priorities

- simplify the tray menu;
- establish a stable notification design;
- improve automatic DXVK enabling;
- expand DXVK version management;
- harden backup/rollback behavior;
- improve game-update resilience;
- define sensible default DXVK HUD and frame-limit behavior;
- test compatibility across a large variety of games;
- refine anti-cheat detection and safety behavior;
- minimize background CPU, memory, disk, and network activity.

### Possible future features

- per-game DXVK version pinning;
- multiple locally available DXVK versions;
- optional custom DXVK builds/forks;
- improved launch history and status information;
- controller navigation;
- richer game compatibility information;
- expanded configuration options.

---

## Optional online compatibility database — future idea

A longer-term idea is an **opt-in online compatibility database** containing information such as:

- detected game/application;
- graphics API;
- observed GPU compatibility;
- known DXVK compatibility results;
- game API changes after major updates;
- reported compatibility problems.

Such a service could make the automatic recommendations more useful over time and reduce the need to maintain static game lists inside the application.

This is **not part of the current implementation**.

If it is ever introduced, it should be strictly opt-in, transparent about what is uploaded, and designed to collect only the minimum information required for compatibility research.

---

## Project philosophy

DXVK Companion is intentionally opinionated about what it should *not* become.

It should not require users to maintain a complicated database.

It should not constantly consume system resources while idle.

It should not modify Windows system files.

It should not silently modify games when safety conditions are unclear.

It should not force every game onto the newest DXVK version.

It should not treat DXVK as something that needs to remain permanently installed inside every game.

Instead, it should quietly manage DXVK when appropriate, preserve the user's control over their games, and make the safest useful decision it can.

---

## Credits

DXVK is developed by [doitsujin](https://github.com/doitsujin/DXVK) and its contributors.

DXVK Companion is an independent utility intended to simplify DXVK management on Windows.

## License

DXVK Companion is licensed under the MIT License.
