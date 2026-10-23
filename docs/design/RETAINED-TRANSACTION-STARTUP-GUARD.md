# Retained transaction startup guard

Approved behavior: [Issue #272](https://github.com/Tiflit/DXVK-Companion/issues/272), owner decision [comment6372228912](https://github.com/Tiflit/DXVK-Companion/issues/272#issuecomment-6372228912). This is `retained-transaction-startup-guard-v1`, a conservative startup refusal, not durable transaction replay or automatic recovery.

The caught-failure ownership repair retains `.transactions/<id>` evidence when commit/recovery is uncertain and protects the current store instance. A fresh process loses that in-memory guard. Startup previously checked newer library schemas, then constructed stores, inspected files and processed pending work without considering retained transaction evidence. No crash or resulting overwrite was reproduced during this repair.

## Behavior and boundary

`Program.PrepareStartup` checks the existing newer-schema refusal first, then the configured backup root's `.transactions` namespace, before startup argument acceptance, application directory creation, logging, stores, monitoring, scheduling or pending processing. Missing/empty ordinary recovery storage permits existing argument behavior. No normal application initialization occurs on refusal; the Windows Forms warning itself may have runtime/OS effects.

`RetainedTransactionProbe` checks attributes from filesystem root through each ancestor before descending. A positively missing component permits startup; access/other observation errors, a non-directory component or reparse-point ambiguity refuse. An existing ordinary transaction directory is inspected only far enough to find the first immediate entry; any file/directory/name blocks startup. No artifact content, transaction target path or child subtree is read. Internal per-instance delegates support deterministic observation-failure tests without permissions changes or junction creation.

The warning uses no raw path, artifact content or inferred game name. It requests preservation and reviewed recovery assessment, and suggests normal exit/retry if another instance is running. There is no ignore switch, automatic cleanup, rollback, replay, migration or new persisted format. Newer-schema message/title/priority remain unchanged. Explicit custom library paths default to sibling `Backups`; production supplies its actual backup root explicitly.

## Deliberate limitations

- Whole-app refusal affects all installations in this data root; no read-only tray is provided.
- Harmless cleanup residue and another instance's active transaction can trigger the same warning as interrupted/uncertain work. The guard does not distinguish them or prove a crash.
- This is a bounded startup observation, not a lock. Filesystem state can change after observation; no continuous or cross-process race prevention is added.
- Existing transaction plans are not complete replay journals. This does not guarantee crash/power-loss consistency, compensate targets or resolve retained evidence. Those require a separate recovery design and explicit approval.
- Product transaction cleanup is unchanged. User/agent deletion of evidence is not a recovery procedure.

## Verification

Synthetic tests cover absent backup/transaction roots, empty namespaces, malformed/planless/unknown entries, a namespace file, attribute/enumeration failures, ancestor/namespace reparse ambiguity, exact file/directory/byte preservation, configured-root routing, warning privacy/presentation, newer-schema priority and startup argument ordering. Fixtures are data only; no product/game/device/crash experiment is required. Existing hosted build/test checks supply revision-bound results in the PR Conversation; no local suite or GUI validation is claimed.
