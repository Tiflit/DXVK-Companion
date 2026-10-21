# Shared Latest cache gates — Codex session 1

## Authority

Owner merged #221 and instructed “Merged / Continue”; standing Codex implementation assignment continues. #222 is a four-path step under #159's approved daily/no-redundant-check behavior. Acquired main `45af92c88b6253db3d48e0791520c6e601202299`, policy `1fae6e06f94111f67fa1da6df87a43257a37609c`. Main application/workflow runs 39664405770/39664406028 succeeded. Human alone merges; #94 stays paused.

## Scope and review

Separate metadata clients previously had independent gates for the same cache. Add a process-local reference-counted gate keyed by normalized full cache-file path with Windows case semantics. Preserve instance gate/private failure timestamp, acquiring instance then shared gate. Re-read disk after shared acquisition. Existing strict parser, validators, due calculation, fallback, resource/cancellation/disposal and persisted timestamps remain unchanged. Successful writes continue clearing private failure state.

Already registered participants share an unpersisted failed-attempt timestamp under the shared semaphore and retain the most recent private/shared time. Entries retire when no holders/waiters remain; unsaved state is not global restart memory and cannot pass to a fresh client after retirement. Cancelled/pre-wait-failed callers return registrations without releasing unacquired semaphores or writing failed attempts. Registry locks cover no waits/I/O. Grouped implementing-agent self-review covers lock order, rental/retirement interleavings, timestamp handoff, disk authority and all cancellation/error exits.

## Coverage and verification

Sixteen added cases use fake HTTP/clock barriers and separate CacheStore/client objects: 200/conditional 304/500 with existing/absent cache and failed writes, normalized/case aliases, request coalescing, unchanged fallback/bytes, participating-client private windows, exact boundary and successful reconstruction, independent caches, cancelled waiters/leaders and pre-wait exception recovery. Review also preserved existing private failure fallback for an unusable cache path that cannot be normalized; a regression checks one request, null fallback and no created fixture files. Existing assertions remain. The old source was inspected, not executed as an observed-red baseline. Exact final head/base/actual-checkout cloud evidence follows in the completion comment. No local tests/apps/helpers/scanners/packages/devices, cleanup, public API/schema/policy/dependency/project/workflow/permission change or independent audit claim.

## Next owner and limits

Human reviews/merges after existing cloud verification. Then reacquire main for remaining #159 separate-process coordination and a concrete newly authorized packaged/device profile. Normalized strings do not establish every physical-file alias. Fresh reconstruction cannot inherit failed writes that never persisted. #167's historical intermittent cause remains unresolved; #94 stays paused.
