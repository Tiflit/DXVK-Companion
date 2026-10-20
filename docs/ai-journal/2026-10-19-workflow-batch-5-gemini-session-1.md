# Bounded failure summaries — session checkpoint

Actual date: 2026-10-20. Codex implements and self-reviews under the human's temporary assignment; the reserved older filename does not attribute work to Gemini.

Task #187; base `6ba4053c81a1228a0c84f47293c017a3d4617683`, refreshed policy `1fae6e06f94111f67fa1da6df87a43257a37609c`. [Activation](https://github.com/Tiflit/DXVK-Companion/issues/187#issuecomment-6304078182). Added optional sanitized failure records and bounded Markdown/JSON projections with exact source IDs. Counters, archive protections and artifact selection are unchanged. Mismatched source context withholds details; compact handoffs preserve core context before optional messages. Offline role preview intentionally ignores details and retains import isolation.

Regression fixtures cover namespaced/plain and missing records, more than five failures, absent/multiline/oversize messages, hostile fields/parse errors, stale source IDs/head, collector/artifact integration, old snapshots and word budgets. Original assertions are retained; the existing review orchestration test gains a failure-artifact phase after its unchanged all-pass assertions. Existing offline-preview guards remain.

Cloud validation pending; no host tests/apps/scanners, real artifacts as fixtures, raw private attachments, dependencies, permissions or dispatch changes. Limits and pattern-based privacy caveats are documented; no general PII or raw-TRX safety claim. Source drafting complete within ten allowed paths; grouped self-review and cloud evidence next. No human interruptions/status queries after activation observed; quota/time not measured. Out-of-scope findings: none newly filed. Next owner: Codex verifies and repairs concrete failures; human alone merges.
