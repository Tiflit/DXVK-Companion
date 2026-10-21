# Shared acquirer gates — Codex session 1

## Authority

Owner merged #219 and instructed “Merged / Continue”; standing Codex implementation assignment continues. #220 is a four-path step under approved #159 shared acquisition/reuse behavior. Acquired main `6665218aa666059f2bd9e7f6a8752907974062b5`, policy `1fae6e06f94111f67fa1da6df87a43257a37609c`. Main application run 39662459523 and workflow run 39662459547 succeeded. Human alone merges; #94 stays paused.

## Finding and implementation

The previous per-instance tag dictionary permitted separate acquirer/client objects targeting the same local destination to download concurrently. Replace it with a process-local registry keyed by normalized full destination path, with Windows case semantics. Count both holders and waiters under a short registry lock. Release the acquired semaphore before returning the registration; remove/dispose an entry only after the last registered user leaves. A new caller racing retirement either keeps the existing gate alive or registers a new idle destination safely. No registry lock is held across awaits or I/O.

Place waiting inside the existing try/finally with a separate entered flag. Cancellation/pre-wait exceptions release registrations without releasing an unacquired semaphore; cleanup retains its existing owned-staging behavior and gate return is guaranteed even if cleanup throws. Local eligibility is rechecked under the shared gate. All parsing, origin/integrity/bounds, extraction, publication, retention, timeouts and game/consent behavior remain. The internal pre-wait seam permits deterministic test rendezvous; it is not a public API or dispatcher.

## Review and verification

Nine added cases cover normal/aliased destination coalescing across distinct clients, cancellation before/around waiting without cancelling the leader/following waiter, cancelled leader with live waiter retry, unrelated libraries/versions proceeding independently, publication failure/retry with retained older bytes, and pre-wait exception recovery. Fake HTTP barriers verify counts, outcomes and complete manifest/payload reuse rather than registry internals. Source before the repair was inspected, not executed as an observed-red baseline. Existing test assertions remain.

Grouped implementing-agent self-review covers registry rental/retirement interleavings, cancellation ownership, key identity and unchanged acquisition safety paths. Cloud CI evidence will be bound to exact final head/base/actual checkout in the completion comment. No local tests/apps/helpers/scanners/packages/devices, cleanup, dependency/project/workflow/policy/permission change or independent audit claim.

## Limits and next owner

This closes only process-local acquirer-instance duplication. Separate Windows processes may still duplicate HTTP; Latest metadata clients have their own independent gates. Normalized strings are not physical identity for every filesystem alias. External publication collisions still refuse without overwrite. Human reviews/merges after existing cloud verification, then reacquire main for remaining #159 coordination and separately authorized packaged/device acceptance. #167 historical intermittent reliability cause remains unresolved; a passing main run does not prove universal stability.
