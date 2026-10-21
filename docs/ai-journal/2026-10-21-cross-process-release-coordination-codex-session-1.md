# Cross-process release coordination — Codex session 1

## Authority

Owner merged #223 and instructed “Merged / Continue”; standing Codex implementation assignment continues. #224 uses the already approved #159 shared checks/acquisition behavior and #177/#178/#179 same-user bounded-busy package, including bounded cloud-only helper processes. It reuses the existing 500 ms lease budget/format/namespace rather than introducing a longer production wait or new policy. Acquired main `b778c1cf2dde726f5e47d2e184a95f650bb9461d`, policy `1fae6e06f94111f67fa1da6df87a43257a37609c`; main application/workflow runs 39666833053/39666833147 succeeded. Human alone merges; #94 stays paused.

## Implementation and grouped self-review

Due Latest checks take the existing OS store lease for canonical cache path after the local gates, re-read disk and hold through HTTP/cache/failed-attempt persistence. Busy/unavailable coordination returns current cache fallback/null without HTTP or invented attempts. Pre-HTTP cancellation records no failed HTTP attempt. Unnormalizable unusable storage preserves the previous private failure fallback. Fresh cache reads remain independent of coordination.

Missing-release acquisition takes the store lease for canonical exact destination after process-local destination gating/initial eligibility checks, rechecks after acquisition and holds through staging/publication/owned cleanup. Busy/unavailable acquisition gives a fixed retry refusal and preserves source bytes. Complete eligible local payloads remain reusable without that lease. Both services inject internal coordinators for private tests and lazily construct production coordinators. No public constructor, config/schema, namespace/format, endpoint, dependency, service or privilege change.

Review covers instance/shared/OS lease ordering, disposal on all exits, cancellation ownership, lease-before-HTTP/recheck boundaries, persisted authority, no acquisition-to-game inversion and unchanged parser/integrity/retention/resource/game guarantees. The existing 500 ms acquisition budget is not a proven hard wall-clock bound under blocking filesystem calls; no timeout is widened. Direct CacheStore writers and noncooperating/older/other-user processes remain outside this cooperative guarantee; persisted state still cannot be invented on unwritable storage.

## Verification and next ownership

Initial head `81cd7514ce9f247e9aabed9bbda3ab783f71778a` passed all 1,123 application tests (run 39671338140 attempt 1) and 386 workflow tests (39671338037). Downloaded TRX/provenance preserve that evidence, including both executed child-process cases. Review then identified a coverage gap: exclusion alone does not prove reuse of a peer's state change during acquisition waiting. A test-only delta adds two deterministic rendezvous cases using the existing injectable coordinator and real OS lease: simulate peer cache commit or copy a fully verified acquired release while the peer owns the lease, then require zero caller HTTP after acquisition and retained peer identity. This is simulated peer publication, not a second GUI/process claim. Product code stays unchanged.

Twelve new cases total: ten real-file/fake-HTTP cases covering peer recheck, busy/unavailable coordination, preserved fallback/bytes, cancellation and retry, plus two Windows GitHub Actions-only cache/destination cases using the unchanged existing helper script/protocol/20-second total/four-second cleanup reservation and private resources. Assert zero HTTP while a different process holds the relevant lock, then progress after natural exit. Existing assertions/helper cases/budgets remain. No observed-red old-source execution claim. Grouped review/delta check is implementing-agent self-review, not independent audit.

Exact final head/base/actual checkout cloud results follow in the completion comment. No local tests/apps/helpers/scanners/packages/devices, cleanup, protected policy/dependency/project/workflow/permission change. Human reviews/merges, then reacquire main and prepare a newly authorized packaged/device validation profile. #167's intermittent historical cause remains unresolved; #94 stays paused.
