# Provenance attempt selection — Codex session

Owner merged #198 and requested continuation under the standing Codex implementation assignment. #199 records this bounded workflow repair. Base `bc4fa8b7d6f55271cb32514d9e06e3b5df1fef81`; refreshed four-file policy `1fae6e06f94111f67fa1da6df87a43257a37609c`. Six prior workflow batches are integrated; #94 remains paused.

PR #198 evidence acquisition observed duplicate artifact names with nonchronological IDs/order. Packet selection could choose the wrong candidate; handoff rejected every duplicate. New synthetic regressions were written before the source repair, but not executed locally or claimed as observed red. Both collectors now share the existing timestamp-window attribution machinery while retaining JSON identity/relationship checks and safe refusal of ambiguity. TRX job intervals additionally require complete metadata for every supplied job.

Existing fixtures gain provenance timestamps/jobs so their identity assertions still exercise the intended guard. The ambiguity test now supplies two in-window candidates and asserts the shared diagnostic instead of the old count-only message; its unavailable-checkout assertion remains. No assertions or timeouts weakened. Documentation records metadata/availability limits.

Separate worktree; only six contracted paths modified. No host suite, app, scanner or helper run. Existing cloud CI supplies delivery evidence; exact revisions/results and grouped implementing-agent self-review will be recorded on the PR. Next owner: human merge after verification. Out-of-scope findings remain #167 application intermittency and #182 cloud-output privacy; no new audit or execution lane.
