# DXVK Companion — Technical Specification

**Version:** 0.1 — Foundation Draft  
**Status:** Design baseline; not a testing or release specification yet

## 1. Purpose

DXVK Companion is a lightweight, portable Windows utility intended to automate the safe management of DXVK for compatible games.

The initial focus is on DirectX 9, DirectX 10, and DirectX 11 games, particularly where translation to Vulkan may benefit systems such as Intel Arc GPUs. DirectX 12 and Vulkan applications may be detected and reported, but do not require an active translation layer from DXVK Companion.

The long-term objective is reliable automatic DXVK management across a large number of games, while keeping user interaction, resource usage, and risk as low as practical.

This document defines the foundation of the project before further implementation changes are made.

---

## 2. Core Design Principles

### 2.1 Lightweight

DXVK Companion must have minimal impact while idle in the Windows system tray.

Requirements:

- Avoid high-frequency polling.
- Avoid continuous CPU-intensive scanning.
- Avoid unnecessary disk writes.
- Avoid unnecessary network activity.
- Perform release/update checks at launch and/or no more than approximately once per day.
- Keep background services and resident logic as small as practical.

### 2.2 Portable

DXVK Companion should be usable by extracting its files and running the application.

The application should:

- Keep application data in its own portable data area where practical.
- Avoid requiring a traditional installer.
- Avoid third-party utilities that are not normally available on Windows 11.
- Avoid requiring a separately installed .NET runtime when the release is published self-contained.

The optional Windows startup feature is an intentional exception because Windows startup registration is required for that feature.

### 2.3 Safety First

DXVK Companion must prioritize protection of the user's games and files over automation.

It must never blindly overwrite, restore, or delete a DLL merely because it has the same filename as a file previously touched by Companion.

When Companion cannot prove that a file is safe to modify, it should stop, preserve the user's file, and report the situation.

### 2.4 Manage DXVK, Not the Game

DXVK Companion should manage only the files and configuration necessary for DXVK.

It must avoid unnecessarily changing:

- game configuration files;
- save data;
- mods;
- unrelated DLLs;
- system files;
- unrelated application data.

Game updates and mods must remain under the user's control.

### 2.5 Automation First

The intended experience is that the user does not have to manually create or maintain game profiles for ordinary use.

Manual controls should exist for exceptions, troubleshooting, and user preference.

Automatic management is an experimental feature and will evolve as compatibility data and real-world testing accumulate.

### 2.6 Simple Interface

The tray interface should remain small, predictable, and consistent.

The intended top-level tray menu is:

- Manage Games
- Game Details
- Exit

Actions such as enabling DXVK, changing configuration, and updating versions should primarily live in the main application UI rather than appearing and disappearing dynamically from the tray menu.

### 2.7 Windows 11 First

The initial compatibility target is ordinary Windows 11 systems.

Support for other Windows versions may be considered later but must not compromise the primary design.

---

## 3. Initial Scope

### 3.1 Active translation target

DXVK Companion initially manages DXVK for:

- DirectX 9
- DirectX 10
- DirectX 11

### 3.2 Informational detection

DXVK Companion may detect and notify the user about:

- DirectX 12
- Vulkan
- other detected graphics APIs where useful

A DirectX 12 or Vulkan application should normally receive an informational notification rather than an attempted DXVK deployment.

### 3.3 Out of scope for the initial foundation

The following are not required for the first implementation pass:

- support for alternative translation layers;
- custom DXVK forks;
- an online compatibility database;
- cloud synchronization;
- user accounts;
- mandatory telemetry;
- controller support;
- finalized branding, logo, and tray icon;
- advanced performance tuning beyond the initial DXVK HUD and frame-limit controls.

These may be considered later.

---

## 4. Game Identity

Game identity is a foundational requirement because executable files can change when a game updates.

### 4.1 Goal

A normal game update must not create a new Companion profile simply because the executable has changed.

### 4.2 Identity should be location-oriented

Companion should primarily identify a tracked game using durable installation characteristics such as:

- game installation directory;
- executable filename/path within that installation;
- stable Companion-generated profile identity.

A complete executable hash must not be the sole identity mechanism because legitimate updates would change the hash.

### 4.3 Executable changes

When a tracked executable changes, Companion should update the existing profile when the installation identity remains consistent.

The system should be able to notice significant changes such as:

- architecture changing;
- API changing;
- executable being replaced;
- executable moving within a recognized installation;
- DXVK state changing.

### 4.4 API transitions

A major game update may change a game from one graphics API to another, for example DX11 to DX12.

For the initial version, Companion should detect and report the new state rather than attempting aggressive automatic cleanup or profile restructuring.

This behavior can become more sophisticated after testing.

### 4.5 Profile principle

A profile represents the tracked game installation/application, not one immutable executable build.

---

## 5. Detection Pipeline

Detection should be layered and conservative.

### 5.1 Stage 1 — Process observation

Monitor running processes using an efficient periodic mechanism.

The monitor should avoid repeatedly processing the same process instance unnecessarily.

### 5.2 Stage 2 — Candidate filtering

Before detailed inspection, filter obvious non-game processes such as:

- Windows system processes;
- known game launchers;
- helper processes where practical;
- applications that clearly do not represent a game session.

False positives should be treated as a major design concern because later automation can modify files.

### 5.3 Stage 3 — Application readiness

A candidate should normally be inspected after it has become a usable application process, such as having a meaningful main window when applicable.

Startup timing must not permanently exclude a game merely because the process initially lacks a window.

### 5.4 Stage 4 — Architecture detection

Determine whether the relevant executable/process is:

- x86 / 32-bit;
- x64 / 64-bit;
- unknown.

Architecture is required to select the correct DXVK binaries.

### 5.5 Stage 5 — Runtime API detection

Inspect loaded modules to identify the graphics API actually in use where possible.

Relevant indicators include graphics DLLs such as:

- `d3d9.dll`;
- `d3d10.dll`;
- `d3d11.dll`;
- `d3d12.dll`;
- `vulkan-1.dll`.

Runtime evidence should have priority when reliable.

### 5.6 Stage 6 — Static executable inspection

When runtime inspection is inconclusive, inspect executable imports using PE parsing as a secondary source of evidence.

### 5.7 Stage 7 — Anti-cheat risk detection

Inspect available process information for known anti-cheat indicators.

The result should be represented as a risk assessment, not as proof that a particular game is safe or unsafe.

A failure to inspect protected process information should be treated conservatively.

### 5.8 Stage 8 — Existing DXVK state

Determine:

- whether DXVK appears to be active;
- which DXVK files are present;
- whether Companion installed those files;
- which Companion-managed DXVK version is associated with the profile;
- whether a safe change is possible.

### 5.9 Stage 9 — Profile reconciliation

Match the observation to an existing game profile or create a new one only when the game identity cannot be reconciled safely.

---

## 6. Detection Results

Detection should produce a structured result rather than an immediate file-modification command.

At minimum, the result should be able to describe:

- game/application identity;
- executable path;
- installation directory;
- architecture;
- current graphics API;
- API confidence/source where useful;
- anti-cheat risk status;
- current DXVK status;
- installed Companion-managed DXVK version;
- update availability;
- whether automatic action is permitted.

Detection must not itself perform destructive file operations.

---

## 7. Application State Model

Companion should maintain explicit user-visible states rather than relying on scattered boolean flags.

Suggested foundation states:

### Unknown

Companion does not yet have enough information to classify the application safely.

### Detected

A game/application has been recognized and recorded.

### Unsupported

The detected application does not currently require or support DXVK Companion's active management path.

Examples include normal DX12/Vulkan applications.

### DXVK Not Applied

The game is compatible with the active DXVK scope, but DXVK is not currently active.

### DXVK Applied

Companion knows that its DXVK deployment is currently active and managed.

### Update Available

A newer compatible DXVK release is available for the game/profile.

### Pending

A requested operation has been deferred until a safe point, normally after the game exits.

### Risk Detected

Potential anti-cheat or another safety concern has been identified. Automatic deployment should be blocked unless an explicit user action overrides the policy.

### Failed

A requested operation could not be completed safely.

The original game files should remain preserved.

These states may later be represented by richer sub-states, but the state model should remain understandable to users.

---

## 8. Detection → Decision → Action Separation

The architecture should keep three concepts separate.

### Detection

Answers:

> What is happening right now?

Example:

`Game A / DX11 / x64 / anti-cheat risk none / DXVK not applied`

### Decision

Answers:

> What is Companion allowed and expected to do?

Example:

`Automatic mode enabled + DX11 + no detected risk + safe profile + DXVK absent = automatic deployment permitted.`

### Action

Performs the actual operation.

Example:

`Download/select DXVK → verify source → prepare deployment → modify game DLLs safely → record resulting state.`

The action layer must never assume that detection alone authorizes a destructive operation.

---

## 9. DLL Safety Foundation

This area takes priority over convenience.

### 9.1 Companion-managed files must be identifiable

For every DLL that Companion changes, it must retain enough metadata to determine:

- original file identity, when an original file existed;
- file identity of the Companion-installed version;
- DXVK version used;
- time/operation information where useful;
- whether Companion still considers the file safe to replace or remove.

### 9.2 Do not rely solely on `.bak`

A `.bak` filename is not sufficient proof that restoration is safe.

A game may be updated or modified after Companion installs DXVK.

### 9.3 Never overwrite an unknown newer file

Before rollback or replacement, Companion should compare the current file against its recorded Companion-managed identity.

If the current file differs unexpectedly:

1. do not overwrite it;
2. do not silently delete it;
3. record the conflict;
4. inform the user that manual intervention may be required.

### 9.4 Installation sequence

Deployment should follow a safe transaction-like sequence:

1. verify that the game is not running;
2. verify target paths;
3. verify DXVK source files;
4. inspect existing target DLLs;
5. preserve originals safely;
6. place Companion-managed files;
7. verify the resulting files;
8. record metadata only after success.

Failure during the process should leave the user's original files recoverable.

### 9.5 Rollback principle

Rollback means:

> Restore the last known user-owned state that Companion can prove it replaced.

It does **not** mean:

> Restore whatever is sitting in a `.bak` file regardless of what happened afterward.

### 9.6 Game updates and mods

Companion must assume that the game directory can change independently of Companion.

A detected conflict is safer than a guessed restoration.

The system should prefer asking the user to resolve a conflict rather than risking loss of newer game/mod files.

### 9.7 Storage location of originals

The final choice between:

- keeping protected originals in the game folder;
- keeping copies in Companion's profile storage;
- or a hybrid approach

remains open at this stage.

The decision must be made based on recoverability, update compatibility, portability, permissions, and the ability to detect stale files.

---

## 10. Automatic DXVK Management

Automatic management is an experimental feature and the long-term goal of the project.

### 10.1 Initial philosophy

Automatic enabling should be conservative.

The initial policy should broadly require:

- compatible API;
- known game identity;
- valid architecture;
- no detected anti-cheat risk;
- safe file state;
- no conflicting Companion operation;
- a valid DXVK release.

### 10.2 Risk overrides

Potential anti-cheat conflicts should block automatic deployment.

Manual override may exist, but it must clearly communicate risk and remain an explicit user decision.

### 10.3 Policy evolution

Automatic-management rules are expected to evolve as real-world testing produces compatibility knowledge.

The decision engine should therefore be designed so policy rules can change without rewriting the underlying detector and DLL deployment system.

---

## 11. Running Games and Pending Actions

DXVK DLL changes should not be made while the target game is running.

When the user or automatic policy requests a change during a running session:

1. record the requested action as pending;
2. leave the running game's files untouched;
3. detect process termination;
4. revalidate the game's state;
5. perform the action only if the safety conditions still hold.

The state should be visible to the user as **Pending**.

---

## 12. DXVK Version Strategy

The project should support multiple DXVK releases rather than forcing a single global latest version.

### Initial requirements

Companion should be architecturally capable of:

- discovering releases;
- downloading releases;
- retaining locally cached versions;
- associating a chosen version with a game;
- detecting when an update exists;
- switching a game to another installed release;
- keeping an older release available when needed.

The first UI may expose only a limited subset of this functionality, but the underlying storage model should not prevent version pinning later.

### Update checks

Checks should occur:

- at application launch; and/or
- approximately once per day.

Network activity while idle should otherwise be negligible.

---

## 13. User Notification Requirements

A detected game should receive a low-profile notification after launch, allowing a short delay for reliable detection.

The notification should be visible over the game and communicate the most important information without requiring the user to open the main UI.

At minimum, it should be capable of showing:

- game name;
- detected API;
- DXVK status;
- whether a newer DXVK version exists;
- important safety warnings when relevant.

Examples:

> Game A — DirectX 11 — DXVK enabled

> Game B — DirectX 12 — native API; DXVK not required

> Game C — DirectX 11 — DXVK pending

> Game D — DirectX 11 — anti-cheat risk detected; automatic deployment disabled

Final visual design is intentionally undecided.

---

## 14. DXVK Configuration

The initial per-game configuration scope includes:

- DXVK HUD on/off;
- DXVK frame limiter on/off;
- configurable frame limit.

### Frame limiter defaults

Preferred behavior:

- limiter disabled by default;
- when enabled, default limit is 120 FPS;
- limit should be configurable per game.

The exact UI and range of valid values remain to be finalized.

### V-Sync

V-Sync behavior requires further investigation and should not be promised as a Companion-controlled feature until the interaction between DXVK, the game, and external frame limiters is better understood.

---

## 15. Main UI and Tray UI

### Tray

The tray interface should remain stable and minimal:

- Manage Games
- Game Details
- Exit

The tray should not dynamically change its menu based on whether a game is running.

### Main UI

The main application is expected to contain more detailed controls, including:

- game status;
- DXVK version management;
- per-game configuration;
- pending/failed operations;
- safety information;
- settings.

The final visual design is intentionally open.

---

## 16. Controller Support

Controller operation is desirable but not mandatory for the initial project.

It should not drive architecture decisions that significantly increase complexity or idle resource usage.

---

## 17. Privacy and Future Online Services

An online repository of detection and compatibility information may be considered in the future.

Possible future information could include:

- game/application identification;
- detected API;
- GPU compatibility observations;
- compatibility status;
- API changes over time;
- known issues.

This must be strictly optional and opt-in.

No such feature should exist as a silent background upload mechanism.

Future design must define:

- exactly what is uploaded;
- what is never uploaded;
- anonymization requirements;
- retention rules;
- user controls;
- deletion procedures;
- authentication requirements, if any.

This is not part of the initial implementation scope.

---

## 18. Build and Development Infrastructure

Because development is being performed remotely, the project should eventually use GitHub Actions to provide a repeatable build process.

The intended workflow is:

1. code changes are committed to the repository;
2. GitHub Actions builds the application;
3. the build produces a self-contained Windows artifact;
4. the artifact can be downloaded for testing;
5. test results feed back into the next development iteration.

The build system should be established early enough that every major implementation change can be built reproducibly.

---

## 19. Current Repository Alignment

The existing project already contains substantial foundations for:

- process monitoring;
- game detection;
- runtime module scanning;
- PE inspection;
- API classification;
- architecture information;
- anti-cheat indicators;
- per-game profiles;
- DXVK downloading;
- DXVK extraction;
- DXVK deployment;
- rollback;
- DXVK configuration;
- release caching;
- tray and game-management UI;
- startup behavior;
- application update checking.

The purpose of this specification is not to discard that work. Future implementation should compare the existing code against this design and preserve useful components where they satisfy the requirements.

The repository currently advertises itself as a lightweight portable Windows tray application with automated DXVK management and contains the corresponding monitoring, DXVK, storage, UI, and utility components. citeturn225456view0turn225456view1turn225456view2

Some existing README claims are intentionally not treated as requirements here because the specification describes the desired product behavior rather than assuming that every currently documented feature is complete. citeturn225456view0

---

## 20. Foundation Milestone

Before major feature expansion, the project should establish these four foundations:

### A. Reliable Game Identity

A routine game update must not automatically produce a duplicate profile.

### B. Conservative Detection

Companion must reliably distinguish likely games from unrelated applications and determine API/architecture before management decisions are made.

### C. Safe DLL Management

Companion must know what it changed and refuse unsafe rollback or replacement when the current file cannot be verified.

### D. Explicit State and Policy

Detection, automatic-management decisions, and file actions must remain separate and expose clear states to the UI.

These foundations should be considered prerequisites for treating automatic DXVK enabling as a serious testing target.

---

## 21. Open Decisions for Later

The following questions are intentionally unresolved:

- Exact algorithm for persistent game identity.
- Exact storage design for protected/original DLLs.
- How to detect modded game directories reliably without false positives.
- How to detect game API transitions robustly.
- Exact notification technology and appearance.
- Exact HUD fields to expose.
- V-Sync interaction and whether Companion should expose related controls.
- Exact DXVK version-selection UI.
- Exact controller support scope.
- Packaging and release presentation.
- Logo, tray icon, and branding.
- Future optional online compatibility service.

These should be resolved from technical evidence and testing rather than assumed prematurely.

---

## 22. Guiding Rule

> **DXVK Companion manages DXVK. It must never become the thing that breaks the game it is trying to improve.**

Everything that follows—automation, detection, rollback, updating, notifications, and UI—should support that rule.
