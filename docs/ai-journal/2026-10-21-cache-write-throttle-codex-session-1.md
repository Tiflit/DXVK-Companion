# Cache-write failure daily throttling — Codex session 1

## Authority and scope

Owner merged #215 and instructed “Merged / Continue”; standing Codex implementation assignment remains. #216 implements the already approved #159 daily-check/no-retry-storm behavior in four explicit paths. Acquired main `62a3544f459333627a2d7cf846564f26acb8d84b`, policy `1fae6e06f94111f67fa1da6df87a43257a37609c`. Main cloud application run 39639471308 and workflow run 39639471462 succeeded. Human alone merges; #94 stays paused.

## Finding and change

Source inspection confirmed that after both metadata save and failed-attempt persistence throw, subsequent callers find the same expired/absent disk cache and immediately retry HTTP. The existing semaphore serializes requests but cannot coalesce them without committed timestamps.

Add one private nullable attempt timestamp guarded by that same semaphore. Persisted fast path stays unchanged; gated recheck honors both disk and private daily windows before a new attempt. Set the timestamp at attempt start and clear it on a successful cache or attempt write, leaving persisted state authoritative. This also preserves the existing sequential-fetch fixture that deliberately expires persisted data to exercise client lifetime. Return current disk fallback or null during the private window, never the unsaved fresh release. No schema/public API/interval/trust/version/game changes. The private timestamp does not survive reconstruction or provide cross-instance coordination when persistence remains unavailable.

## Coverage and verification

Added twelve data cases before the source repair: repeated failed staging/replacement on 200/304/500/cancelled responses with/without prior cache, barrier-coordinated queued callers, exact 24-hour boundary and successful persistence/reconstruction, and externally repaired stale metadata. Assertions check request counts, fallback identity and unchanged cache bytes/ETag/timestamps. Existing tests/assertions remain. Old source was inspected, not executed for an observed-red baseline.

Implementing-agent grouped self-review and current cloud CI evidence will be bound to final head/base/actual checkout in the completion comment. No local test/app/helper/scanner/package/device execution. No independent review or hardware acceptance claim. Preserve evidence outside the checkout; do not add a journal-only commit after green CI.

## Next owner

Codex completes grouped review and existing cloud verification, then human reviews/merges. #159 retains separate-instance HTTP and newly authorized packaged/device acceptance gaps.
