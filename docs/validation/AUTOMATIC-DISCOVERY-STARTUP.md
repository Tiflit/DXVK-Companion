# Optional automatic-discovery startup control

Approved behavior and bounded implementation: [#228](https://github.com/Tiflit/DXVK-Companion/issues/228).

Start the portable application with:

```text
DXVK-Companion.exe --no-auto-discovery
```

The tray menu displays **Automatic discovery off**. The passive discovery timer
does not start. No automatic candidate admission, Game Detected notifications or
discovery-driven enable/reapply occurs. This can help when managing an existing
library without admitting other applications running on the computer.

The switch applies only to this invocation. Close normally and start without the
switch to restore normal discovery. Saved Manual/Automated settings and per-game
policies are unchanged. There is no new persisted setting or runtime toggle.
Only the exact switch is accepted, once; unsupported, misspelled, duplicate or
extra arguments produce a fixed usage warning before normal initialization.
The existing incompatible-newer-library refusal retains priority.

## Operations and remaining activity

Manual library browsing, official version acquisition and explicit management
remain available under their existing rules. Real activity inspection is still
required before mutation: Running and Unknown are refused or queued according to
the existing action path. Operation leases, compatibility/anti-cheat checks,
ownership, transaction and rollback protections remain active. Turning off
discovery never supplies an artificial inactive process universe to those checks.

Monitor-driven game-exit callbacks are also paused. A queued action does not
automatically run when the game exits during this invocation. Use the existing
explicit retry path or a normal restart; guarded startup pending-action processing
is unchanged. Do not interpret the status as cancellation of pending work or a
change in the saved deployment policy.

Startup inspection/pending processing, release checks and the app-update check
still run normally. Relevant safety inspection may enumerate processes and query
candidate identities. Existing program-owned locks, private logging, network
requests and ordinary runtime effects still apply. This option does not provide
host isolation, zero host access/writes, offline transport, guaranteed game
identification or a blanket ban on automation for previously stored policies.

## Verification and host acceptance

Cloud regression coverage exercises actual startup composition with injected
process enumeration, timer creation/default behavior, no passive enumeration or
admission/store mutation when paused, fixed tray-status presentation, argument
handling/newer-schema priority, and unchanged Running/Unknown enable/restore
refusals with real activity-inspector logic and synthetic process evidence.
No real game or additional helper process is required by these new tests.

The stopped [#156 host diagnostics](https://github.com/Tiflit/DXVK-Companion/issues/156#issuecomment-6327859034)
are retained evidence, not acceptance of this new behavior. A merged package and
a separately approved, revision-bound profile/current readiness are required for
another host lifecycle pass. Do not reuse the ended validation root or its
remaining nominal start count. The nonfixture stop rule remains in force; no
whitelist of the previously discovered application is introduced. Paused-discovery
testing will not establish default-discovery behavior, real-game compatibility,
GPU/driver performance or transport-level offline operation.
