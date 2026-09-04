# DXVK Companion — Concept & Design Decisions
## Revision Notes Following `DXVK-COMPANION-SPEC.md`

> This document records the design decisions and clarifications agreed upon after the initial project specification was created.
>
> It is a companion document to `DXVK-COMPANION-SPEC.md`, not a replacement for it.
>
> These are design decisions and intended behaviors. They do not necessarily describe functionality that is already implemented in the current codebase.

---

## 1. Core Product Direction

DXVK Companion should remain:

- lightweight when idle in the Windows system tray;
- portable and self-contained;
- usable on broadly supported Windows 11 systems;
- free of external third-party dependencies where Windows-native functionality is sufficient;
- simple to operate, with automation as the long-term goal;
- safety-first when modifying game files;
- focused on managing DXVK rather than managing the games themselves.

The application should perform as little background work as reasonably possible.

Normal idle operation should therefore avoid unnecessary CPU activity, disk access, and network traffic.

---

## 2. Manual and Automated Operating Modes

DXVK Companion should have two clearly defined operating behaviors.

### Manual

In Manual mode, Companion detects and reports game state but does not automatically decide to install, update, or restore DXVK.

The user explicitly chooses actions such as:

- enable DXVK;
- update DXVK;
- select a DXVK version;
- restore the game's previous state.

### Automated (Experimental)

Automated mode is intended to be the long-term defining feature of DXVK Companion.

It should be as self-driving as reasonably possible.

The goal is not merely to automate the initial installation. Automated mode should eventually be capable of continuously maintaining DXVK for supported games, including:

- automatically applying DXVK to newly detected compatible games;
- automatically maintaining games that continue to use a supported API;
- detecting game updates and reassessing compatibility;
- keeping DXVK current unless the user explicitly selects another version;
- handling recoverable changes to managed DXVK files;
- notifying the user when intervention is required.

Because this behavior will require extensive testing across many games, the feature must remain explicitly labeled **Experimental** until confidence is established.

---

## 3. Global Policy and Per-Game Overrides

The application should use a global policy with optional per-game overrides.

The default behavior for a newly detected game should be:

> **Use global policy**

This avoids requiring users to configure every game individually.

The per-game policy should eventually support choices such as:

- **Automatic Management**
- **Specific DXVK version**
- **Disabled**

The exact presentation of version families such as DXVK 1.x or DXVK 2.x should be decided later. The underlying model should support selecting an exact official DXVK release.

### Disabled

`Disabled` means:

> Companion continues to detect and report the game, but does not automatically change its DXVK files.

Disabled does not mean that the game is forgotten or no longer monitored.

---

## 4. Game Installation and Executable Identity

A game installation and an executable profile should be treated as separate concepts.

### Installation identity

The primary identity of an installation should be based on its normalized installation directory.

This allows normal game updates to replace the executable without creating a new installation profile.

### Executable identity

Individual executables within the same installation should be tracked separately using their relative path within the installation.

Conceptually:

```text
Game Installation
|
+-- Example.exe
|   +-- API
|   +-- Architecture
|   +-- DXVK state
|   +-- Configuration
|
+-- Benchmark.exe
|   +-- API
|   +-- Architecture
|   +-- DXVK state
|   +-- Configuration
|
+-- Shared installation state
```

This is important because some games may contain multiple executables that use different graphics APIs while sharing the same installation folder.

For example:

```text
Example_DX11.exe   -> DX11
Example_DX12.exe   -> DX12
Example_Vulkan.exe -> Vulkan
```

These should be separate executable profiles under the same installation, not separate unrelated games.

### Game updates

A replacement executable in the same installation should normally remain the same profile.

After an update, Companion should re-run detection and record any changes such as:

```text
DX11 -> DX12
DX11 -> Vulkan
DX9  -> DX11
```

A graphics API transition should initially be treated as a state change to report, not as an automatic reason to destroy or restore DXVK files.

---

## 5. Existing DXVK Adoption

DXVK Companion should be able to recognize and adopt an existing official DXVK installation.

If a game already contains files matching a known official DXVK release, Companion should be able to identify:

- that the files are DXVK;
- the release version, where reliably identifiable;
- the appropriate architecture where applicable.

The installation can then be brought under Companion's management.

This should avoid unnecessary reinstallation.

### Unknown and non-official DLLs

For the initial project scope:

- recognized official DXVK releases are treated as DXVK;
- unknown DLLs are treated as pre-existing/external files;
- custom builds, nightly builds, and other modified DXVK variants are not treated as supported DXVK releases unless they are explicitly recognized in the future.

A user installing custom or nightly builds is responsible for those files.

This intentionally keeps detection simple and avoids trying to identify every possible unofficial DXVK build.

---

## 6. DXVK File Ownership Model

DXVK Companion should manage only:

1. the DXVK DLLs it deploys;
2. the `dxvk.conf` file it creates;
3. the pre-existing files that those managed files replaced.

Companion should not attempt to take ownership of unrelated files in the game installation.

The game directory is therefore not a general-purpose Companion workspace.

### Managed files

For each file Companion changes, it should know:

- whether the file existed before Companion changed it;
- the pre-Companion state;
- what Companion installed;
- the expected identity of the installed file;
- which DXVK release supplied it.

A file that did not exist originally must be recorded as:

> Original state = file did not exist

rather than receiving a fabricated backup.

---

## 7. Backup Storage

Original/pre-existing files that are replaced by Companion should be stored **inside DXVK Companion's own data/profile storage**.

Do not rely on `.bak` files left in game directories as the primary recovery mechanism.

Reasons:

- keeps game directories clean;
- avoids confusing backup files with game files;
- reduces interaction with game updates and mod managers;
- keeps Companion-owned data together;
- allows the UI to manage restoration centrally;
- supports a genuinely portable application structure.

A conceptual structure may look like:

```text
DXVK Companion/
|
+-- DXVK/
|   +-- <version>/
|       +-- x32/
|       +-- x64/
|
+-- Profiles/
|   +-- <game installation>/
|       +-- profile data
|       +-- managed file metadata
|       +-- original file backups
|
+-- Settings/
```

The exact directory names and layout remain implementation details to be decided.

---

## 8. Restore, Not Remove

The user-facing concept should be **Restore**, not Remove or Delete.

### Restore a specific game

The operation means:

> Return every DXVK-managed file in that game installation to the state recorded immediately before Companion took control of those files.

It does **not** mean:

> Simply delete DXVK DLLs.

Restoration must therefore:

- restore original files that existed before Companion;
- remove Companion-created files that did not exist originally;
- restore or remove `dxvk.conf` according to its pre-Companion state;
- preserve unrelated files.

### Restore all games

The main UI should provide a global operation to restore every managed installation.

This should work on a per-game basis and should not fail the whole operation simply because one game requires attention.

A pre-operation summary should eventually indicate which installations can be restored automatically and which require user attention.

---

## 9. Safety When Managed Files Change

Safety must not prevent automation from functioning.

If a Companion-managed file changes externally, Companion should detect the conflict.

Possible causes include:

- a game update;
- a mod;
- another tool;
- direct user modification.

Companion should not silently overwrite the changed file.

However, in **Automated** mode, this should not force the user to manually reopen the main application.

Instead:

1. detect the changed file;
2. reassess the game;
3. determine whether DXVK is still appropriate;
4. if automatic reapplication appears appropriate, present a temporary prompt;
5. allow the user to authorize the overwrite;
6. if the user does not respond, leave the game untouched.

Example notification:

> **Example Game was updated**
>
> `d3d11.dll` has changed since DXVK Companion last managed it.
>
> The game is still using DirectX 11.
>
> **Reapply DXVK?**
>
> `[ Yes ] [ No ]`

### Core rule

> Companion must never overwrite an externally changed managed file silently.

A user-authorized overwrite is valid.

---

## 10. Failure Must Be Non-Destructive

If Companion encounters an unexpected situation during installation, update, or restoration, it should prefer:

```text
Stop
Report
Leave the unexpected file alone
```

rather than continuing blindly.

The application should never assume that its previous expectations are still valid.

---

## 11. DXVK Version Management

DXVK Companion should manage multiple official DXVK releases locally.

The user should not be forced to use only the newest release.

This supports:

- regressions;
- compatibility problems;
- game-specific version requirements;
- experimentation with older official releases.

### Storage

Downloaded official DXVK releases should remain available locally.

For now, there is no need for automatic deletion or complex cleanup rules because the releases consume relatively little storage compared with typical game installations.

An optional cleanup system may be introduced later.

### Selecting a version

The main UI should eventually allow a specific game to use a selected DXVK release.

Conceptually:

```text
DXVK Version:
[ Latest ]
```

or:

```text
DXVK Version:
[ 2.5 ]
```

A game's policy may therefore be:

- follow latest official DXVK release;
- use a specific official DXVK release;
- disable automatic management.

---

## 12. Initial DXVK Installation Behavior

When a new compatible game is detected:

```text
DX9 / DX10 / DX11
+
No recognized DXVK
+
Automated mode enabled
```

Companion should automatically select the latest official DXVK release.

If the game is currently running, the actual file modification should wait until it is safe to perform.

The user should receive a low-profile notification indicating that DXVK is scheduled for installation.

---

## 13. Existing DXVK and Update Behavior

When an official DXVK installation is already present:

```text
Game detected
+
Official DXVK recognized
```

Companion should not automatically replace it merely because a newer DXVK release exists.

Instead, the user should receive a simple update prompt.

Example:

> **DXVK update available**
>
> Example Game is using DXVK 2.5.
>
> DXVK 2.6 is available.
>
> **Update?**
>
> `[ Yes ] [ No ]`

### Prompt behavior

- Yes → queue the update;
- No → leave the current version unchanged;
- no response → leave the current version unchanged;
- prompt disappears automatically after approximately 10–15 seconds.

The timeout is important so users are never forced to interact with a notification, including users playing primarily with a controller.

---

## 14. DXVK Updates Must Not Modify a Running Game

DXVK installation, version replacement, restoration, and configuration changes should not modify the target game files while the game process is running.

Instead:

```text
Game running
    |
    +-- Action requested
    |
    +-- Action becomes Pending
    |
Game exits
    |
    +-- Revalidate game/files
    |
    +-- Perform action
    |
    +-- Verify result
```

The user's response to a notification therefore normally means:

> "Schedule this action"

rather than:

> "Modify the files immediately."

This is the safer interpretation of update prompts.

---

## 15. Pending Actions

The application should distinguish the current DXVK state from a requested future action.

Possible pending actions include:

```text
None
Install
Update
Restore
```

Example:

```text
DXVK:
2.5 installed

Pending action:
Update to 2.6
```

If the game launches again before the pending action is executed, the action should remain pending and wait for the next safe opportunity.

The user should eventually be able to cancel a pending action from the main UI.

---

## 16. Automated Maintenance Philosophy

Automated mode should be a continuing management policy, not a one-time installer.

Its long-term goals include:

- first-time automatic DXVK deployment;
- maintaining the latest official DXVK for games configured for automatic management;
- detecting game updates;
- detecting API changes;
- reapplying DXVK when an update replaces managed DLLs and the game remains compatible;
- prompting the user when an unexpected file conflict requires authorization;
- avoiding action when the game no longer requires DXVK.

The exact rules should evolve as real-world testing progresses.

For this reason, policy logic should remain separate from detection and file operations.

---

## 17. Detection, Decision, Action

The architecture should clearly separate three responsibilities.

### Detection

Determine what is happening.

Examples:

```text
Game: Example
API: DX11
Architecture: x64
Anti-cheat: none detected
DXVK: 2.5
Managed files: unchanged
```

### Decision

Determine what Companion should do according to the selected policy.

Examples:

```text
Policy: Automated
DXVK missing
=> install latest
```

or:

```text
Policy: Automated
DXVK 2.5 installed
DXVK 2.6 available
=> update according to automation policy
```

or:

```text
Policy: Manual
=> notify only
```

### Action

Perform the requested operation through the same safety system regardless of whether the request originated from automation or from the user.

This separation is important because the automation rules will evolve substantially during testing.

---

## 18. Global and Per-Game Automation Model

The intended user experience is:

```text
Global setting:
Automated (Experimental)

New Game A:
Use global

New Game B:
Use global

Game C:
Disabled

Game D:
Specific DXVK version
```

This allows the user to keep automation active globally while making exceptions for particular games.

A newly detected game should not require manual configuration unless the user has deliberately disabled automation.

---

## 19. DX12 and Vulkan

DX12 and Vulkan should remain visible to the user but should not receive an active DXVK translation layer.

For the current scope:

```text
DX12 -> detect and notify
Vulkan -> detect and notify
```

The presence of DX12/Vulkan should therefore not be interpreted as an error.

These games are expected to use their native modern graphics API.

---

## 20. Anti-Cheat Handling

Potential anti-cheat risks should be detected before automatic DXVK deployment.

In Automated mode:

```text
Potential anti-cheat risk
=> Do not silently deploy DXVK
=> Warn the user
```

The warning may allow a deliberate user decision later.

Detection should distinguish between:

- no known risk detected;
- known/possible risk;
- unable to determine.

The system should not claim certainty when process inspection is unavailable.

---

## 21. Notifications

Notifications should be intentionally low-profile and should not interrupt gameplay.

A notification should normally appear a few seconds after game launch, after Companion has enough time to perform detection.

Useful information includes:

- game name;
- detected API;
- DXVK status;
- installed DXVK version;
- newer DXVK version available;
- pending action;
- safety warning;
- successful result.

Interactive update prompts should disappear automatically after approximately 10–15 seconds.

Informational notifications may use shorter lifetimes.

The notification system should report state changes rather than continuously generating UI work from the process-monitoring loop.

---

## 22. Tray Interface

The tray interface should remain intentionally minimal and consistent.

Preferred menu:

```text
DXVK Companion
---------------
Manage Games
Game Details
---------------
Exit
```

The tray menu should not dynamically replace its contents depending on the currently running game.

More complex operations should live in the main UI.

---

## 23. Main UI Responsibilities

The main UI should eventually provide:

- game list/management;
- current API and DXVK status;
- per-game DXVK mode;
- per-game DXVK version selection;
- update controls;
- restore controls;
- pending action visibility;
- game-specific configuration;
- global automation setting;
- startup setting;
- update checking;
- visibility into blocked or unsafe operations.

The user should not have to manually maintain the existence of game profiles.

---

## 24. Explicit Check for Updates

DXVK Companion should check official DXVK releases automatically at a low frequency:

- at application launch; and/or
- approximately once per day.

The check should have minimal CPU, disk, and network impact.

The main UI should also provide:

> **Check for updates**

This allows the user to immediately request a fresh DXVK release check without waiting for the next scheduled check.

---

## 25. Future Controller Support

Controller-driven interaction is desirable but not a prerequisite for the core application.

The UI and notification architecture should avoid depending on keyboard/mouse interaction for basic automation.

In particular, timeout-based notifications ensure that users can ignore prompts without being blocked.

Controller support can be added later if it provides clear value.

---

## 26. Future Online Compatibility Repository

A future optional online system may provide:

- game/application detection information;
- detected APIs;
- GPU compatibility information;
- known problems;
- compatibility recommendations;
- API changes after game updates.

This feature must be:

- strictly opt-in;
- transparent;
- governed by clear upload rules;
- designed with privacy and personal-data minimization as primary requirements.

This is a future project and should not influence the architecture of the current core application unless necessary.

---

## 27. Important Design Principle

A central product rule emerging from these decisions is:

> **DXVK Companion should manage DXVK, not manage the user's games.**

The game belongs to the user.

Companion should manage only:

- the DXVK files it deploys;
- the `dxvk.conf` it creates;
- the original files those files replaced;
- metadata necessary to safely maintain or restore those changes.

Nothing beyond that should be changed without an explicit reason.

---

## 28. Current Conceptual Workflow

The resulting intended workflow is:

```text
Game launches
    |
    v
Detect installation + executable
    |
    v
Detect architecture
    |
    v
Detect graphics API
    |
    +--------------------+
    |                    |
 DX12/Vulkan         DX9/10/11
    |                    |
 Notify only         Inspect DXVK
                         |
                +--------+--------+
                |                 |
          DXVK absent       DXVK recognized
                |                 |
         Automated mode?    New version?
                |                 |
              Yes          Prompt user
                |                 |
         Queue latest       Yes -> queue update
                |            No -> leave alone
                |
         Wait for game exit
                |
                v
        Revalidate file state
                |
        +-------+--------+
        |                |
      Safe           External change
        |                |
     Execute        Prompt user in
        |           Automated mode
        v
      Verify
        |
        v
   Record state
        |
        v
   Notify user
```

---

## 29. Decisions Intentionally Left Open

The following should remain undecided until the core architecture and testing give us enough information:

- exact backup directory structure;
- exact method of identifying official DXVK release files;
- exact notification technology and visual design;
- exact list of DXVK configuration parameters;
- exact frame limiter UI;
- exact behavior for automatic restoration after a DX11 -> DX12/Vulkan transition;
- whether version-family choices such as DXVK 1.x / 2.x should be exposed directly;
- exact supported Windows 11 compatibility range;
- logo and tray icon;
- controller integration;
- online compatibility repository;
- cleanup of unused downloaded DXVK versions.

These should not be allowed to complicate the core implementation prematurely.

---

## 30. Development Philosophy

The project should evolve in this order:

1. Establish safe and reliable detection.
2. Establish reliable file ownership and restoration.
3. Establish reliable manual DXVK management.
4. Establish reliable version management.
5. Establish reliable notifications.
6. Enable and test Automated mode.
7. Expand automated rules based on real-world game testing.
8. Add optional advanced functionality only when it provides clear value.

Automated mode should remain experimental until real-world testing demonstrates that its behavior is dependable across a broad range of games and update patterns.
