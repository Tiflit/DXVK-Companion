# Configuration disable repair — #236

Codex implements and self-reviews under the human's continued implementation direction. Verified #235 merged at main `8d5fba569d79c8be4fb05d16aef78615bfa5ca91`; main cloud checks succeeded. Policy identity remains `1fae6e06f94111f67fa1da6df87a43257a37609c`.

Source inspection confirmed #167 item 7: both installer entry points skip configuration staging when the last feature is disabled. Created a six-path contract before implementation. Added an internal generator route for present, actively owned configuration; retained public no-feature behavior, exact-key matching, original baselines and transaction compensation. Added unit and service-level lifecycle/restoration/failure regressions without changing old assertions.

Changed files: the two configuration/installer sources, two matching test files, dedicated validation note and this journal. Verification pending approved cloud CI; no local apps/tests/builds/helpers or downloads. Prior packaged profiles remain ended, #94 paused and #233 queued. Existing worktrees and recovery evidence preserved. No out-of-scope findings added. Final evidence and any corrections will be linked from the PR completion record; next action is cloud verification and grouped self-review, then human review/merge. Human decision/status interventions: none this repair; provider tokens and human time not measured.
