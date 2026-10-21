# #228 — optional automatic-discovery startup control

## Authority and revision

Exact coordinator transcription of the owner's direct response to the #228
package: **“Approve implementation and cloud tests”**. This activates its listed
source/documentation scope and existing cloud checks; no host execution. Shared
credentials provide attributed transcription, not independent human authentication.
Base/main `d34bcd03cef04d1944afb3cc72f9436888b4d6a9`; policy identity
`1fae6e06f94111f67fa1da6df87a43257a37609c`. Human merges; #94 stays paused.

## Implementation and grouped self-review

- Exact process-local `--no-auto-discovery`; no-argument default preserved.
  Unsupported/duplicate/extra arguments refuse with fixed usage text before
  ordinary initialization; newer-schema refusal retains priority.
- Production startup composition selects the monitor's discovery state before
  timer construction. Disabled mode creates no timer and rejects poll delivery.
  Existing test-only timer suppression still permits explicit poll tests by
  default. The public monitor/tray constructors remain compatible.
- Tray status derives from the actual monitor state. No persisted settings,
  mutation safety implementation, activity-inspector wiring, leases, dependencies,
  workflow or project files changed. Monitor lifetime is now disposed with the
  existing program scope.
- Documented discovery/exit-dispatch pause separately from existing guarded
  startup pending processing, explicit retries, metadata checks and saved policy.
  No zero-host-access or isolation claim. No classifier/exclusion rewrite.
- New tests exercise actual startup timer composition with a synthetic process
  universe, paused polling/store immutability, non-actionable status, strict CLI
  handling and preflight priority, plus real inspector Running/Unknown refusals
  for enable and restore. Existing assertions/helper budgets are unchanged.

## Verification and handoff

Local tests/apps/helpers: **NOT RUN**, outside this implementation approval.
Cloud verification: pending submission; no executed result claimed here.
Self-review is coordinator self-review, not an independent agent audit.
The host runs in #156 are ended; no new download/fixture setup/host run or cleanup
is assigned. #227 remains a separate preparation PR, not live activation.
Next: submit the bounded PR, preserve revision-bound cloud evidence and review
focused deltas if needed; human next for merge after completed evidence. A later
host validation requires a newly prepared profile/current readiness.
