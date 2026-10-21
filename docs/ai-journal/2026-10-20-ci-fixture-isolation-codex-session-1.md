# CI fixture isolation — Codex session

Owner merged #200 and requested continuation under the standing Codex implementation assignment. #201 bounds this test-only repair. Acquired main `acdbd08384ab050c5d12a0b73e0c5d4082908d0f`; refreshed four-file policy `1fae6e06f94111f67fa1da6df87a43257a37609c`. Separate clean worktree; #94 stays paused.

Source inspection found two recorded-failure fixture classes still using shared coordination/live process discovery. Migrated only those filesystem-only classes to existing synthetic safety factories, retaining real stores/leases/transactions, reloads and original lifecycle assertions. Wrote a deterministic actual-inspector interference/isolation regression and sentinel-based diagnostic checks before helper changes; no local execution or observed-red claim. Existing same-namespace contention and Unknown-activity refusals remain.

The already-private store-lease setup and bounded helper gain fixed phase/category/elapsed diagnostics. Non-null, reference-equality and signal assertions are expressed as equivalent boolean predicates with safe messages. A helper self-exit cancellation becomes a fixed-message timeout failure; original deadline, 2-second helper sleep, 12-second total, 4-second cleanup and cleanup actions remain. No production/project/workflow/dependency changes, serialization, skip or timeout weakening.

Existing cloud suites supply evidence; exact head/base/actual checkout, outcomes and labeled self-review will be recorded on the PR. No host builds/tests/apps/helpers/scanners. Historical #167 failures retain unknown cause; this is not exhaustive resolution. Next: human merge after verification. Out-of-scope findings: existing #167 product/release work and #182 raw-cloud-output privacy; no new audit.
