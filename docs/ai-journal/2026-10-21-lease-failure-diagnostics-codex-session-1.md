# Lease failure diagnostics — Codex session 1

## Authority and evidence

Owner merged #217 and instructed “Merged / Continue”; standing Codex implementation assignment continues. #218 is a five-path bounded CI diagnostic task. Acquired main `a7ce000d6566f38187aaf9e37777d18e404a73a6`; policy `1fae6e06f94111f67fa1da6df87a43257a37609c`. Human alone merges; #94 stays paused.

Main application run 39659135697 attempt 1 had 1,082/1,083 passes. SharedDirectory_SameNamedRecordedExecutable_WithUnsupportedRecordedApi_RefusesDeployment_InstallationWide received a generic busy reason, not the expected executable/API reason, at line 551 after 717 ms. Preserve downloaded provenance 12373096247 (`sha256:bb0332ca6a6a4d35269f266c0a3a2506cb88a9f885356ce5f7277b4e016a9d75`) and TRX 12372896479 (`sha256:da8cbeefb6288d1d1cadad4ef669a4f84aeaf9402b4cb885fff971e8ff1fd745`). Main workflow tests passed. No unchanged-head retry or causal diagnosis follows from that evidence.

## Scope and self-review

Source already uses a private real coordinator and explicit inactive activity for the failed fixture. The coordinator previously returned null without identifying which step ran out of budget. Add an internal fixed phase/role/elapsed/attempt/cancellation snapshot, reset at public acquisition entry; populate null-return and between-store budget paths in sync/async acquisition. Existing LastAcquisitionError, public interfaces, timers, locks, retries, fail-closed behavior and 500 ms product refusal policy stay unchanged. The snapshot is instance-last evidence, not concurrent per-caller tracing, and is not automatically logged.

Replace the manager/direct-installer reason substring assertion renderings in the affected test with equivalent case-sensitive boolean conditions and bounded snapshot/category messages. The observed failure was the direct-installer assertion at line 551; both entry points now provide the same bounded diagnostic context. All compatibility/outcome/library/no-DLL-write/direct-installer assertions remain. Five added cases use real leases and existing fault seams: slow scan, store contention (sync/async), pre-cancellation, released handles/reacquisition and cleared diagnostic state. No source-mirroring tests or raw path/exception logging. Grouped implementing-agent self-review covers these boundaries; no independent reviewer claim.

## Verification and handoff

Existing cloud CI results will be bound to exact final head/base/actual checkout in the completion comment. No host test/app/helper/scanner/package/device execution, global serialization, timeout increase, skip, project/dependency/workflow or permission change. This is an observability repair; passing new runs do not establish the historical refusal's cause or universal CI stability. #167 retains the unresolved intermittent reliability finding. After human review/merge, reacquire main CI before further #159 work and newly authorized packaged validation.
