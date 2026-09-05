# DXVK Companion — Project Specification

## Status

**Design baseline / authoritative specification**

**Current implementation status**

- Phase A data foundation: complete and verified in Windows CI.
- Phase A.1: approved — V1 will not import earlier development profiles or `games.json`.
- Phase A.5: next design phase — transactional file safety and official DXVK identity must be specified before implementation.


This document is the current source of truth for the intended architecture, behavior, scope, and design principles of DXVK Companion.

Earlier documents remain useful as historical design notes, but when they conflict with this document, this specification takes precedence.

### Historical companion documents

- `DXVK-COMPANION-CONCEPT-DECISIONS.md`
- `DXVK-COMPANION-DESIGN-SUMMARY.md`
- `DXVK-COMPANION-MIGRATION-MAP.md`

The public `README.md` should be updated later, after implementation and testing stabilize.

---

# 1. Project Purpose

DXVK Companion is intended to be a lightweight, portable Windows application that detects games using older Direct3D APIs and automates the deployment and maintenance of DXVK for those games.

The initial motivation is to improve compatibility and performance behavior on modern GPUs, particularly hardware that performs well with native DX12/Vulkan but may benefit from DXVK when running older Direct3D APIs.

The application's intended long-term role is:

> **An automated DXVK deployment, maintenance, update, and restoration tool that requires as little user intervention as possible.**

DX12 and Vulkan games are detected and reported, but do not require an active translation layer from DXVK Companion.

---

# 2. Product Principles

These principles govern implementation decisions.

## 2.1 Lightweight

DXVK Companion should have minimal impact while idle in the Windows tray.

Normal idle operation should:

- use very little CPU;
- use very little memory;
- avoid unnecessary disk activity;
- avoid unnecessary network activity;
- perform process and release checks at sensible intervals;
- avoid repeated deep scans when nothing has changed.

Performance should be treated as a design requirement, not merely optimized later.

## 2.2 Portable

The application should be self-contained and portable.

It should not require third-party utilities when equivalent Windows-native capabilities are available.

DXVK Companion should not need to modify Windows system DLLs, system-wide graphics configuration, or other global system components.

## 2.3 Safety First

DXVK Companion must be conservative when modifying files.

The application should never silently destroy, overwrite, or remove a file that may belong to a game update, mod, user modification, or external tool.

However, safety must not eliminate automation. When an external change is detected, Automated mode should be able to ask the user for permission to reapply DXVK without requiring them to manually reopen the application and discover the issue themselves.

## 2.4 Manage DXVK, Not the Game

DXVK Companion should manage only the DXVK-related files and configuration that it deliberately controls.

The game itself remains the user's property and responsibility.

Companion should not attempt to become a general-purpose game or mod manager.

## 2.5 Automation First

The ultimate objective is highly automated DXVK management.

Manual control exists for users who prefer direct control or for cases where Automation is unsuitable.

Automation remains an **Experimental** feature until broad real-world testing demonstrates that its behavior is dependable across many games and update patterns.

## 2.6 Simple User Experience

The complexity of the implementation should not become the complexity of the user interface.

The application should normally require little user interaction.

The tray interface should remain small and stable.

---

# 3. Operating Modes

DXVK Companion has two fundamental operating behaviors.

## 3.1 Manual

Manual mode observes and reports game state.

It does not automatically decide to install, update, reapply, or restore DXVK.

The user explicitly chooses operations from the main UI.

Manual mode is intended for users who want complete control.

## 3.2 Automated (Experimental)

Automated mode allows Companion to make routine DXVK management decisions.

The intended long-term behavior includes:

- automatically applying the latest official DXVK to newly detected compatible games;
- continuously managing games unless the user overrides the behavior;
- detecting available DXVK updates;
- detecting game updates and API changes;
- detecting externally changed managed files;
- reapplying DXVK when appropriate;
- asking for permission when a potentially destructive overwrite is required;
- eventually handling compatible API transitions automatically when sufficiently tested.

Automated mode must always pass through the same safety and verification system used by Manual mode.

Automation must never bypass safety checks.

---

# 4. Global Management Policy and Per-Game Overrides

The application should have a global management policy:

```text
Manual
Automated (Experimental)
```

Every newly discovered game should default to:

```text
Use Global Policy
```

This avoids requiring manual configuration for every game.

A game can override the global policy.

Conceptually, a per-game setting should support:

```text
Use Global Policy
Automatic Management
Specific official DXVK version
Disabled
```

The exact version selector presentation is a UI decision. The underlying data model must support selecting an exact official DXVK release.

## 4.1 Disabled

Disabled means:

> Continue detecting and tracking this game, but do not automatically modify its DXVK files.

Disabled does not mean forgotten.

The profile remains persistent.

---

# 5. Game Installation and Executable Identity

A game installation and an executable are separate concepts.

## 5.1 Installation identity

The installation should primarily be identified by its normalized installation directory.

Routine game updates that replace the executable should therefore normally retain the same installation identity.

## 5.2 Executable identity

Executables within the same installation should be tracked individually using their relative path within the installation.

Example:

```text
C:\Games\Example\
    Example_DX11.exe
    Example_DX12.exe
    Benchmark.exe
```

These can be represented as:

```text
Installation:
C:\Games\Example\

Executable:
Example_DX11.exe

Executable:
Example_DX12.exe

Executable:
Benchmark.exe
```

This supports installations in which different executables use different APIs.

## 5.3 Installation vs executable state

The installation contains shared file-management state.

The executable profile contains information such as:

- executable identity;
- last known API;
- architecture;
- detection evidence;
- runtime information.

The two concepts must not be collapsed into one flat `GameProfile`.

## 5.4 Game updates

A normal executable replacement within the same installation should update the existing profile rather than create a new game profile.

Companion should re-run detection after a relevant update.

Possible transitions include:

```text
DX9  -> DX11
DX11 -> DX12
DX11 -> Vulkan
DX10 -> DX11
```

An API transition is first an observed state change.

It is not automatically an instruction to remove DXVK unless the current automation policy explicitly supports that behavior and the implementation has been sufficiently tested.

---

# 6. Process Detection

A Windows process is not automatically a game.

A process should be treated as a candidate that requires qualification.

The intended detection sequence is:

```text
New process
    ↓
Basic process filtering
    ↓
Allow initialization time
    ↓
Runtime graphics-module inspection
    ↓
Static executable inspection if necessary
    ↓
Architecture detection
    ↓
Game/application qualification
    ↓
Anti-cheat risk assessment
    ↓
Installation/executable identity resolution
    ↓
Current DXVK state determination
    ↓
Policy evaluation
```

## 6.1 Filtering

Obvious system processes and known launchers should be filtered where practical.

A process should not become a tracked game merely because it has a window.

## 6.2 Initialization window

Games may take time to initialize their rendering API.

Companion should not discard a process permanently just because the graphics modules are not visible immediately after process creation.

A lightweight, short-lived detection session should allow the application to observe initialization.

## 6.3 Parent and child processes

Launchers may start the actual game process.

Companion may inspect direct child relationships when useful, but process ancestry is evidence only.

The actual game executable should become the tracked executable profile.

The launcher does not become the game's identity merely because it started it.

## 6.4 Lightweight monitoring

After a process has been assessed, Companion should avoid repeatedly performing deep detection when no state has changed.

The monitor should remain event-oriented where practical.

---

# 7. API Detection

The initial supported detection set is:

```text
D3D9
D3D10
D3D11
D3D12
Vulkan
```

D3D8 support may be added later.

## 7.1 Runtime evidence

Runtime module inspection should normally have priority.

Examples include:

```text
d3d9.dll
d3d10core.dll
d3d11.dll
d3d12.dll
dxgi.dll
vulkan-1.dll
```

## 7.2 Static evidence

If runtime evidence is unavailable or insufficient, Companion may inspect PE imports and other executable evidence.

Static evidence is secondary to runtime evidence.

## 7.3 Evidence and confidence

API detection should internally record enough evidence to distinguish between:

- strong runtime evidence;
- weaker static evidence;
- conflicting evidence;
- unknown.

A useful conceptual result is:

```text
Primary API
Other observed APIs
Confidence
Evidence source
```

The user normally does not need to see the internal confidence value.

## 7.4 Multiple APIs

A process may legitimately expose more than one graphics API.

The detector must not automatically interpret multiple APIs as a transition.

A state change such as:

```text
DX11 -> DX12
```

should be inferred only when the observations justify it.

---

# 8. DX12 and Vulkan

DX12 and Vulkan are informational in the initial project scope.

When detected:

```text
DX12 -> native, DXVK not required
Vulkan -> native, DXVK not required
```

Companion may notify the user of the detected API and state.

It should not deploy DXVK for these APIs.

---

# 9. Anti-Cheat Risk

Anti-cheat detection should be represented with more nuance than a simple Boolean.

Conceptual states:

```text
No known risk detected
Possible/known risk
Unable to determine
```

In Automated mode, known or possible anti-cheat risk should prevent silent automatic deployment.

The user may still be allowed to make a deliberate manual decision.

Failure to inspect process information must not be represented as certainty that no risk exists.

---

# 10. Existing DXVK Detection and Adoption

Companion should recognize an existing official DXVK installation where it can identify it reliably.

The intended supported categories are:

```text
Recognized official DXVK release
Recognized as DXVK, version unknown
Not recognized as supported DXVK
```

For initial scope:

- official releases known to Companion may be adopted;
- unknown/custom/nightly/modified builds are not treated as supported DXVK releases;
- unsupported custom builds remain the responsibility of the user.

## 10.1 Official release identification

Windows file-resource metadata can provide useful evidence such as:

```text
Product name: DXVK
```

but Windows file version numbers must not be assumed to represent the DXVK release version.

The authoritative release identity should come from official DXVK release data and deterministic file information derived from official assets.

The exact implementation of release recognition remains an explicit technical design task before deployment code is rewritten.

## 10.2 Unknown DXVK

If a DLL appears to identify itself as DXVK but cannot be matched to a supported official release, Companion should not pretend to know its exact version.

It may report:

> DXVK detected — version not recognized.

For management purposes, unsupported/unrecognized builds are treated as pre-existing external files rather than silently adopted.

---

# 11. DXVK Release Management

Companion should maintain a local repository of official DXVK releases.

The user must not be restricted to the latest release.

This supports:

- regressions;
- game-specific compatibility;
- older working releases;
- explicit version pinning;
- switching versions without repeated downloads.

## 11.1 Latest release

The UI should present a simple concept:

```text
Latest
```

rather than hard-coding a specific major version into the interface.

## 11.2 Specific version

A game may be pinned to a specific official release.

Example:

```text
Game A -> Latest
Game B -> DXVK 2.6.2
Game C -> DXVK 3.1
```

## 11.3 Version retention

Downloaded official releases should remain locally available for now.

Automatic cleanup is not required initially because release packages are relatively small compared with typical game installations.

An optional cleanup mechanism may be added later.

---

# 12. Official Release Source

Companion should use official DXVK releases as its managed source.

It should not automatically manage:

- custom builds;
- nightly builds;
- modified builds;
- random third-party packages.

The release system should retain enough metadata to identify:

- release version;
- source;
- architecture payloads;
- downloadable assets;
- deterministic file identities.

---

# 13. DXVK Deployment Model

Companion should deploy only the DLLs required by the detected supported API and executable architecture.

Initial deployment targets:

| API | Initial DLL set |
|---|---|
| D3D9 | `d3d9.dll` |
| D3D10 | to be verified against current official release contents before implementation is finalized |
| D3D11 | `d3d11.dll`, `dxgi.dll` |

The D3D10 deployment set is intentionally left as a verification item rather than assumed from memory.

D3D8 is deferred.

## 13.1 Architecture

The selected DLLs must match the application's architecture.

Conceptually:

```text
Detected game
    ↓
x86 / x64
    ↓
Matching official DXVK payload
```

Companion must never deploy the wrong architecture merely as a precaution.

## 13.2 Installation location

Deployment should occur only in the relevant game application's directory.

Companion must not replace or modify system copies in:

```text
C:\Windows\System32
C:\Windows\SysWOW64
```

and should not use system-wide DLL overrides as an initial workaround.

## 13.3 Minimal deployment

Only files required for the detected target should be deployed.

Companion should not copy an entire DXVK release archive into the game directory.

---

# 14. Managed Files

Companion manages only:

1. DXVK DLLs it deploys;
2. `dxvk.conf` when Companion creates/manages it;
3. pre-existing files that those managed files replaced.

No unrelated game files should be incorporated into the management system.

---

# 15. Managed File Metadata

Every managed file should have enough metadata to determine its original and current state.

Conceptually:

```text
Game-relative path
Original state
Original backup, if any
Original identity/hash
Current expected identity/hash
DXVK release
Managed status
```

## 15.1 File existed originally

Example:

```text
Original:
d3d11.dll = game DLL
```

Companion stores the original file in its own storage before replacing it.

## 15.2 File did not exist originally

Example:

```text
Original:
dxvk.conf = absent
```

Companion records:

```text
Original state = missing
```

Restore must remove the Companion-created file rather than attempting to restore a nonexistent file.

---

# 16. Backup Storage

Original/pre-existing files must be stored inside Companion's own data/profile storage.

The primary recovery mechanism should not be `.bak` files in the game directory.

This keeps:

- game directories clean;
- Companion-owned data together;
- game updates and mods isolated from backup artifacts;
- restoration manageable from within the UI.

The exact storage structure remains an implementation decision.

---

# 17. Baseline and Restore

The key concept is the **pre-Companion baseline**.

Restore means:

> Return all Companion-managed files to the state recorded immediately before Companion began managing them, subject to the file-safety rules.

Examples:

### Native game baseline

```text
Original:
Game d3d11.dll

Managed:
DXVK 3.x

Restore:
Game d3d11.dll
```

### No-original-file baseline

```text
Original:
dxvk.conf absent

Managed:
Companion-created dxvk.conf

Restore:
dxvk.conf removed
```

### Existing official DXVK baseline

```text
Original:
Official DXVK 2.6

Managed:
Official DXVK 3.1

Restore:
Official DXVK 2.6
```

The system should preserve the baseline while changing between managed DXVK versions.

---

# 18. External File Changes

If a managed file changes externally, Companion must detect the conflict.

Possible causes include:

- game updates;
- mods;
- another utility;
- direct user changes.

The application must not silently overwrite the changed file.

However, external changes must not permanently defeat Automated mode.

## 18.1 Automated behavior

When Automation is enabled:

1. detect the changed managed file;
2. reassess the game's current API/state;
3. determine whether DXVK remains appropriate;
4. if reapplication would overwrite the externally changed file, present a temporary prompt;
5. if the user authorizes the operation, proceed through the normal safe deployment path;
6. if the user declines or the prompt expires, leave the game untouched.

Example:

> Example Game was updated.  
> `d3d11.dll` has changed since DXVK Companion last managed it.  
> The game still uses DirectX 11.  
> **Reapply DXVK?**  
> `[ Yes ] [ No ]`

The timeout is a deliberate part of the safety and automation design.

## 18.2 New baseline after an authorized reapplication

If an external game update replaces a managed file and the user authorizes reapplication, the newly observed game file should become the new restoration baseline.

This prevents Restore from bringing back an outdated version of the game file.

Example:

```text
Game version A
    ↓
Companion DXVK
    ↓
Game updates to version B
    ↓
Managed DLL replaced
    ↓
User authorizes reapplication
    ↓
Version B becomes the baseline
    ↓
DXVK reapplied
```

---

# 19. Pending Actions

Actions should be represented independently from current DXVK state.

Possible pending actions:

```text
None
Install
Update
Reapply
Restore
```

Example:

```text
Current:
DXVK 2.6

Pending:
Update to DXVK 3.1
```

Pending actions should survive application restarts.

If the game launches again while an operation is pending, the action waits for the next safe opportunity.

---

# 20. Pending-Action Conflict Rule

A new external change supersedes an older pending action that refers to the same managed file.

Example:

```text
Pending:
Update d3d11.dll to DXVK 3.1

Then:
Game update replaces d3d11.dll
```

The stale pending update must be cancelled/superseded.

Companion should:

```text
Invalidate stale pending action
    ↓
Reassess current game/file state
    ↓
Apply current policy
    ↓
Prompt if authorization is required
```

Two competing plans for the same file must never execute against stale assumptions.

---

# 21. Running Games and Safe Execution

Companion should never replace DXVK files while the target game is running.

A user action during gameplay normally means:

> Queue this action.

Then:

```text
Game running
    ↓
Action pending
    ↓
Game exits
    ↓
Pre-flight validation
    ↓
Execute
    ↓
Verify
    ↓
Commit state
```

If a new conflicting state is discovered after queueing but before execution, the pending action must be reassessed.

---

# 22. Deployment Transaction

Installation, update, reapplication, and restoration should behave like a logical transaction.

Conceptually:

```text
Pre-flight validation
    ↓
Capture required baseline
    ↓
Stage new files
    ↓
Replace target files
    ↓
Verify all expected files
    ↓
Commit managed state
```

The system should avoid ending in a silently mixed state such as:

```text
new d3d11.dll
old dxgi.dll
```

If an unexpected condition prevents reliable completion, the preferred behavior is:

```text
Stop
Attempt safe recovery
Report
Do not overwrite unrelated/unexpected files
```

The exact low-level atomicity strategy will be decided during implementation.

---

# 23. Restore All

The main UI must provide a global operation equivalent to:

> Restore all games managed by DXVK Companion.

Each installation should be handled independently.

One problematic installation must not prevent safe restoration of unrelated games.

The UI should summarize:

- safely restorable games;
- already-restored games;
- games requiring attention.

---

# 24. Game Management and Forgetting

DXVK Companion should not use a destructive **Forget Game** operation as the normal way to stop managing a game.

Instead:

```text
Management = Disabled
Visibility = Hidden (optional)
```

The profile remains.

This prevents the same game from being forgotten and then automatically rediscovered as a new game on the next launch.

A hidden or disabled game remains identifiable and retains its history and restoration data.

---

# 25. Hidden Games

The main UI may use tabs/views to reduce clutter.

A game can be marked:

```text
Visible
Hidden
```

Hidden games remain tracked.

Launching a hidden game must not recreate a profile or automatically make it visible.

The final tab names are not fixed.

Possible conceptual views include:

```text
Games
Hidden
Attention
Updates
Settings
```

The final arrangement should follow usability testing.

---

# 26. Update Management

There are two different update concepts.

## 26.1 DXVK release updates

Companion should check for official DXVK releases:

- at launch and/or;
- approximately once per day.

The check should have minimal resource impact.

The main UI should also include:

> **Check for updates**

for immediate manual checking.

## 26.2 Companion application updates

The application may continue to check for updates to DXVK Companion itself using the existing release/update infrastructure.

This is separate from DXVK release management.

---

# 27. Existing Official DXVK

When an existing official DXVK installation is detected:

```text
Game detected
+
Official DXVK recognized
```

Companion should not automatically replace it simply because a newer version exists.

Instead, the user receives a simple update prompt.

Example:

> DXVK 2.6 is installed.  
> DXVK 3.1 is available.  
> **Update?**  
> `[ Yes ] [ No ]`

Behavior:

```text
Yes      -> queue update
No       -> leave current version
Timeout  -> leave current version
```

This respects deliberate use of older working releases.

---

# 28. Automatic Installation of New Games

When Automated mode is active:

```text
Supported D3D API
+
No recognized DXVK
+
No blocking safety condition
```

Companion should automatically select the latest official DXVK release.

If the game is running, deployment is queued until the process exits.

The user should receive a low-profile status notification.

---

# 29. Automatic Maintenance

Automated mode is intended to be continuing maintenance, not a one-time installer.

The long-term automation model is:

```text
New compatible game
    ↓
Install latest DXVK

Existing managed game
    ↓
Maintain according to policy

New official release
    ↓
Determine whether update is applicable

Game update
    ↓
Reassess API and managed file state

Managed file changed
    ↓
Prompt if overwrite authorization is required

API change
    ↓
Reassess policy
```

The exact automatic behavior for API transitions and some update cases should become more aggressive only after extensive testing.

---

# 30. DXVK Version Selection

The per-game UI should allow:

```text
Latest
Specific official version
```

A pinned version is an explicit user choice.

For example:

```text
Game A -> Latest
Game B -> DXVK 2.6.2
```

Game B must remain on 2.6.2 until the user changes its policy.

The underlying model should not permanently tie version selection to a particular major-version family unless future testing proves that useful.

---

# 31. Update All

The main UI should offer:

> **Update All**

This operation must respect per-game policy.

Example:

```text
Game A -> Latest
Game B -> pinned 2.6.2
Game C -> Disabled
Game D -> Latest
```

When a new release is available:

```text
Game A -> eligible
Game B -> remain pinned
Game C -> no automatic action
Game D -> eligible
```

---

# 32. Notifications

Notifications should be low-profile and should not interfere with gameplay.

A normal game-launch notification should appear a few seconds after launch, after enough detection has completed.

Useful information includes:

- game name;
- detected API;
- architecture where relevant;
- DXVK status;
- current version;
- update availability;
- pending action;
- safety warning;
- successful operation.

## 32.1 Interactive prompts

Update/reapplication prompts should:

- contain a simple Yes/No choice;
- remain visible approximately 10–15 seconds;
- automatically expire without requiring user input;
- never block game execution.

Timeout means:

> Do not perform the prompted change.

This supports users playing with a controller or users away from the keyboard.

---

# 33. Notification and UI Consistency

Notifications and the main UI must use the same underlying state.

If a notification says:

> Update pending

the main UI should show:

```text
Pending: Update to 3.1
```

If deployment succeeds, both should reflect:

```text
DXVK 3.1
Active
```

The notification layer should not maintain separate truth about the game state.

---

# 34. Framerate Limiter

DXVK Companion should provide a per-game DXVK framerate limiter.

Preferred default:

```text
Enabled: Off
Stored default limit: 120 FPS
```

Examples:

```text
Game A -> Off
Game B -> 120 FPS
Game C -> 90 FPS
```

The enabled/disabled state and limit value should be stored independently.

The initial implementation should not attempt to control the game's V-Sync setting.

---

# 35. DXVK Performance Overlay

The user should be able to toggle the DXVK performance overlay.

The exact information displayed is not yet finalized.

The initial UI should favor a small number of useful presets rather than exposing every possible DXVK option.

---

# 36. `dxvk.conf`

Companion may create and manage `dxvk.conf` when a supported Companion feature requires it.

If no Companion-managed configuration is needed:

> Do not create `dxvk.conf`.

If an existing configuration file is present:

> Preserve it before taking control.

If Companion manages the file, its pre-Companion state becomes part of the restoration baseline.

Restore returns it to:

```text
Existing original config
```

or:

```text
Absent
```

as appropriate.

V-Sync management is outside the initial scope.

---

# 37. Main User Interface

The main UI is the primary management surface.

It should provide access to:

- detected games;
- game/API/DXVK status;
- management policy;
- version selection;
- Restore;
- Restore All;
- Update;
- Update All;
- pending actions;
- attention states;
- hidden games;
- global automation;
- update checking;
- game-specific configuration;
- frame limiting;
- overlay control.

The final visual design remains intentionally open.

---

# 38. UI Organization

The interface may be divided into tabs/views to reduce clutter.

A possible conceptual structure:

```text
Games
Hidden
Attention
Updates
Settings
```

The exact names and layout are not final.

The purpose is to keep routine game status separate from exceptions, update management, and settings.

---

# 39. Tray Interface

The tray menu should be static and minimal:

```text
DXVK Companion
---------------
Manage Games
Game Details
---------------
Exit
```

The menu should not dynamically change according to the current game.

Enable/disable/update/restore controls belong in the main UI.

The tray primarily indicates that Companion is running and provides access to the main application.

---

# 40. Startup

The user should be able to toggle:

> Start DXVK Companion with Windows

This is a Windows-native startup integration intended to support Automation.

---

# 41. Controller Support

Controller-friendly operation is desirable but not mandatory.

The architecture should not depend on keyboard/mouse interaction for automation.

Timeout-based prompts ensure that users can ignore notifications without blocking gameplay.

Controller support can be added later.

---

# 42. Privacy and Future Online Compatibility Repository

A future optional online service may collect, with explicit opt-in:

- game/application identification;
- detected API;
- GPU compatibility information;
- known problems;
- compatibility recommendations;
- API changes following game updates.

This is outside the current core implementation.

If eventually developed, it must be:

- opt-in;
- transparent;
- data-minimizing;
- governed by explicit upload rules;
- designed around privacy and user control.

---

# 43. Performance and Update-Check Policy

Normal idle operation should be extremely light.

Release checks should occur at launch and/or approximately once per day.

Manual:

> Check for updates

should force an immediate check.

The application should avoid continuous network activity.

---

# 44. Current Architecture Target

The conceptual architecture is:

```text
                    PROCESS MONITOR
                           |
                           v
                    DETECTION LAYER
                           |
             +-------------+-------------+
             |             |             |
          Identity       API         Risk Evidence
             |             |             |
             +-------------+-------------+
                           |
                           v
                    CURRENT STATE
                           |
                           v
                    POLICY ENGINE
                           |
                     +-----+-----+
                     |           |
                  Manual      Automated
                     |           |
                     +-----+-----+
                           |
                           v
                    ACTION PLANNER
                           |
                           v
                  SAFE FILE ENGINE
                           |
             +-------------+-------------+
             |             |             |
          Install       Update       Restore
             |             |             |
             +-------------+-------------+
                           |
                           v
                       VERIFY
                           |
                           v
                      PERSIST STATE
                           |
                 +---------+---------+
                 |                   |
                UI             Notifications
```

---

# 45. Target Data Model

The existing flat `GameProfile` should evolve toward concepts similar to:

```text
GameInstallation
    InstallationPath
    DisplayName
    Visibility
    ManagementPolicy
    Executables[]
    ManagedFiles[]

ExecutableProfile
    RelativeExecutablePath
    DisplayName
    LastKnownApi
    LastKnownArchitecture
    DetectionEvidence
    RuntimeState

ManagedFileRecord
    RelativePath
    OriginalState
    OriginalBackup
    OriginalIdentity
    ExpectedManagedIdentity
    ManagedDxvkVersion
    CurrentState

PendingAction
    Type
    TargetVersion
    Reason
    RequestedAt
```

The exact class names are not mandatory.

The separation of responsibilities is mandatory.

---

# 46. Existing Repository Migration

The current project should be refactored rather than rewritten from zero.

Existing useful components should be retained where they already solve a well-defined problem.

## 46.1 High-priority migration targets

```text
GameProfile.cs
ProfileStore.cs
DxvkManager.cs
DxvkInstaller.cs
DxvkRollback.cs
FileUtils.cs
ApiClassifier.cs
ManageGamesWindow.cs
TrayApp.cs
UpdateNotification.cs
```

These require substantial change.

## 46.2 Components likely to remain useful

```text
ModuleScanner.cs
PeParser.cs
Logger.cs
StartupManager.cs
CompanionVersion.cs
EnvironmentUtils.cs
ReleaseInfo.cs
CachedRelease.cs
```

These should only be changed when the new architecture requires it.

---

# 47. Development Phases

## Phase A — Data Foundation — Complete

1. Define installation/executable identity.
2. Define management policy.
3. Define managed-file records.
4. Define pending actions.
5. Define restoration baseline.
6. Define hidden/disabled state.
7. Implement persistence.
8. Verify the foundation in Windows CI.

Phase A is considered complete after the application and Phase A test project build successfully and the current Phase A test suite passes in GitHub Actions.

## Phase A.1 — Remove Pre-Release Legacy Migration

V1 starts from a clean current-format `GameLibrary`.

Because DXVK Companion has never been distributed or used on a real machine, V1 has no supported migration path from earlier development builds.

For V1:

- remove `TryMigrateLegacyProfiles`;
- remove the private `pre-release legacy profile shim` compatibility shim;
- remove migration-only reconciliation helpers;
- remove migration-specific tests;
- do not import `games.json` into the current `GameLibrary`;
- update the specification to state explicitly that V1 starts with an empty current-format library when no current-format library exists.

Historical code and data remain useful as reference material but are not part of the V1 compatibility contract.

This phase must not remove `ProfileStore` or `GameProfile` merely because they are older architecture. They remain current code until their consumers are migrated to the new architecture.

## Phase A.5 — Safety Boundary and Official DXVK Identity

Design and test these contracts before implementation.

### A. Transactional file-management engine

1. Define ownership rules.
2. Define original-baseline capture.
3. Define Companion-side backup storage.
4. Define Install / Update / Reapply / Restore transactions.
5. Define multi-file atomicity and staged deployment.
6. Define verification after each transaction.
7. Define partial-failure recovery.
8. Define interrupted-operation recovery.
9. Define external-change handling boundaries.
10. Define persistent transaction/pending state.

### B. Official DXVK identity

11. Define the official-release trust root.
12. Define package/archive identity.
13. Define exact release/version/architecture mapping.
14. Define exact DLL membership for each supported API and architecture.
15. Define the three resulting classifications:
    - Known official DXVK
    - Unknown/external
    - Not DXVK
16. Define how existing official DXVK is adopted without trusting Windows file-version metadata alone.

Phase A.5 must be complete at the design/contract level before implementation begins.

## Phase B — Observe-Only Detection

17. Refactor process monitoring.
18. Implement richer detection snapshots.
19. Improve API classification.
20. Track architecture.
21. Improve anti-cheat evidence.
22. Support multiple executables per installation.
23. Detect and report API changes.
24. Keep all behavior observe-only; no game-file modification.

## Phase C — Manual DXVK Management

25. Connect the safety engine to real disposable game directories.
26. Implement deliberate Install.
27. Implement Update.
28. Implement Reapply.
29. Implement Restore.
30. Implement version selection.
31. Implement existing-official-DXVK adoption.
32. Implement `dxvk.conf` management.
33. Implement frame-limit configuration.
34. Implement HUD/overlay configuration.
35. Refuse unsafe or ambiguous operations rather than guessing.

## Phase D — External-Change and Pending-Action Handling

36. Detect managed-file changes.
37. Detect deletion/replacement of managed files.
38. Implement user-authorized reapplication.
39. Implement game-update baseline replacement where explicitly authorized.
40. Implement persistent PendingAction handling.
41. Implement stale-action cancellation/supersession rules.
42. Verify safe behavior across application restart.

## Phase E — Automated Mode — Experimental

43. Global Automated policy.
44. Per-game overrides.
45. Automatic first-time installation.
46. Timed update prompts.
47. Safe queued operations after game exit.
48. Automatic maintenance.
49. Automatic game-update handling.
50. Safe reapplication flow.
51. Continue treating Automated mode as Experimental until broad real-world testing supports promotion.

## Phase F — UI, Notifications, and Release Hardening

52. Main status-oriented game UI.
53. Hidden/Attention/Updates views as justified.
54. Simple static tray.
55. Pending-action and safety notifications.
56. 10–15 second non-blocking decision prompts.
57. Update checks.
58. Startup behavior.
59. Portable-folder move testing.
60. First-release end-to-end testing on disposable installations.
61. Final documentation, packaging, and release checklist.

---

# 48. Testing Philosophy

Testing should proceed in layers.

First prove:

```text
Detection
```

Then:

```text
Manual deployment
```

Then:

```text
Restore
```

Then:

```text
Version switching
```

Then:

```text
Notifications
```

Only after these are reliable should Automated mode be tested extensively.

Automated mode should be tested across many games and update conditions before its status is changed from Experimental.

---

# 49. Non-Goals for the Initial Implementation

The initial implementation does not attempt to:

- manage custom/nightly DXVK builds;
- modify Windows system DLLs;
- manage V-Sync;
- provide an online compatibility database;
- support every legacy graphics API;
- provide controller support;
- automatically perform risky API-transition restoration before adequate testing;
- automatically delete unused local DXVK releases;
- become a general-purpose mod or game manager.

---

# 50. Core Behavioral Examples

## New compatible game in Automated mode

```text
Game launches
    ↓
DX11 detected
    ↓
No recognized DXVK
    ↓
No blocking safety condition
    ↓
Latest official DXVK selected
    ↓
Installation queued
    ↓
Game exits
    ↓
Safe deployment
    ↓
Verify
    ↓
DXVK Active
```

## Existing official DXVK

```text
Game launches
    ↓
DX11 detected
    ↓
DXVK 2.6 recognized
    ↓
DXVK 3.1 available
    ↓
Prompt: Update?
    ↓
Yes -> queue update
No/timeout -> leave unchanged
```

## Game update replaces managed DLL

```text
DXVK managed
    ↓
Game updates
    ↓
Managed DLL changed
    ↓
API reassessed
    ↓
Still DX11
    ↓
Automated mode
    ↓
Prompt: Reapply DXVK?
    ↓
Yes
    ↓
New game DLL becomes baseline
    ↓
DXVK reapplied safely
```

## Restore

```text
Managed installation
    ↓
User selects Restore
    ↓
Game must not be running
    ↓
Verify managed files
    ↓
Restore original files/config
    ↓
Remove Companion-created files that did not exist originally
    ↓
Verify
    ↓
Restored
```

---

# 51. Guiding Principle

The final product should feel simple because the implementation is doing the difficult work quietly.

> **DXVK Companion should know what the game is doing, know what it is allowed to change, know exactly what it changed, and be able to return the installation to the state that existed before Companion took control.**

Automation should reduce user effort without reducing user control or file safety.

