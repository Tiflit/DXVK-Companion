# Session Record: 2026-10-06 — Issue #48 Bounded Observations & Dashboard Separation

- **Date / Timestamp**: 2026-10-06 01:53:00 UTC
- **Agent Role & Model**: Gemini (Implementer)
- **Task / Issue**: Issue #48 — [AI] Separate bounded observations from authorized tasks, add Issue template and update dashboard
- **Starting Head**: `3ff5f70343bd48619797728d4b9f6e203f2b9c06`
- **Branch / Worktree**: `workflow/issue-48-bounded-observations-and-dashboard` / `D:\dev\DXVK-Companion-issue-48`

### Purpose & Context
Implement Issue #48 to formally decouple bounded observations from authorized task contracts. Create the `ai-observation.yml` issue template, add an automated scope guard in `evaluate_scope.py` that rejects issues labeled `ai-observation`, update the current-state workflow and dashboard to separate open observations from task issues without budget starvation, align operational guidance in `AGENTS.md`, `AI-DEVELOPMENT-WORKFLOW.md`, and `AI-ACTIVITY-JOURNAL.md`, and add comprehensive regression tests.

### Decisions & Rationale
1. **Automated Scope Guard (`evaluate_scope.py`)**: In live GitHub API mode, reject issues carrying the `ai-observation` label before parsing allowed paths, even if the body contains valid path lines. Documented this as a narrow automated guardrail rather than a replacement for human governance. Body-only local mode evaluates paths without claiming label metadata it does not acquire.
2. **Anti-Starvation Task Acquisition (`update_dashboard.py`)**: To prevent observation volume from starving open task acquisition, task fetching filters out `ai-observation` labeled issues and pages through `issues?state=open` up to a bounded ceiling (`max_pages * 3`), ensuring task issues on later pages are discovered.
3. **Observation Dashboard Separation (`update_dashboard.py` & `ai-current-state.yml`)**: Bounded separate acquisition queries `issues?state=open&labels=ai-observation`, filtering out PRs and machine dashboard issues. Renders a compact count and link under Live Repository Status (`- **Open Observations**: [{count}]({url})`). If truncated or failed, marks `completeness` as `INCOMPLETE` and displays explicit truncation/error markers without false zeroes or claiming "untriaged". Added `labeled` and `unlabeled` triggers to `ai-current-state.yml`.
4. **Issue Form (`ai-observation.yml`)**: Explicitly sets `labels: ["ai-observation"]`, contains prominent banner `Observation — unassigned; implementation not authorized`, captures evidence, trigger, expected/observed behavior, consequence, uncertainty, and existing tracking, and strictly omits `Allowed paths` and task-assignment fields.
5. **Visibility & Checkpoint**: Added required `Out-of-scope findings: none / links / pending persistence (reason and next owner)` checkpoint line to `AGENTS.md`, `AI-DEVELOPMENT-WORKFLOW.md`, and `AI-ACTIVITY-JOURNAL.md`.
6. **Heading Normalization in Scope Evaluation (`evaluate_scope.py`)**: `parse_contract.py`'s allowed path section parser terminates on level-3 headings (`###`). When an issue contract format places a level-2 heading (e.g. `## Acceptance criteria`) immediately after `### Allowed paths`, `evaluate_scope.py` normalizes the section boundary before delegating to `parse_allowed_paths`, preventing subsequent heading prose from being misparsed as path entries while keeping `parse_contract.py` unedited.
7. **Query-Level Task Acquisition Separation & Honest Incomplete Rendering (PR #49 Review 1)**: Replaced page-by-page filtering of `issues?state=open` with a bounded GitHub Search query (`search/issues?q=repo:... is:issue is:open -label:ai-observation`) that excludes observations before pagination. This genuinely decouples task and observation request budgets, guaranteeing tasks are discovered even if hundreds of observation issues precede them. Added `issues_truncated` and `prs_truncated` flags to `RepositoryFacts`, and updated `render_dashboard` to render lower-bound counts (`>=N [incomplete at limit]`), `_Incomplete_`, or `_Unavailable_` rather than false zeroes or misleading "none" states when acquisitions are incomplete or fail.
8. **Historical Index Accuracy & Template Reconciliation (PR #49 Review 1)**: Corrected `docs/AI-ACTIVITY-JOURNAL.md` session index row for Issue #15 (implemented by Merged PR #36, not PR #35). Reconciled `docs/AI-DEVELOPMENT-WORKFLOW.md` to note `proposed_investigation` is optional in the template form while required tracking fields remain mandatory.
9. **Search Incompleteness Propagation & Response Shape Validation (PR #49 Review 2 / R3)**: Handled GitHub Search API's `incomplete_results: true` timeout/partial-match flag by propagating it across every acquired page to `issues_truncated`, `is_truncated`, and `completeness = "INCOMPLETE: Search query returned incomplete results (incomplete_results=true)"`. Ensured that empty or short result pages retain this incompleteness and render an incomplete or lower-bound task inventory rather than claiming 0 or "No open task issues". In `search_issues_paged`, validated search response shapes by rejecting non-dictionary responses or missing/non-list `items` fields via `GitHubApiError` into the existing `_Unavailable_` error presentation, preventing synthesis of artificial complete empty inventories. Added comprehensive collector-to-renderer and client validation regression tests.

### Actions Executed
- Created `.github/ISSUE_TEMPLATE/ai-observation.yml`.
- Updated `.github/workflows/ai-current-state.yml` to trigger on `labeled` and `unlabeled` issue events.
- Updated `scripts/ai-workflow/evaluate_scope.py` to enforce the observation label guardrail in API mode and normalize heading boundaries for `### Allowed paths`.
- Updated `scripts/ai-workflow/update_dashboard.py` to query ordinary tasks excluding `ai-observation` before pagination, propagate `incomplete_results` search flags, validate response dictionary shapes, track task/PR truncation, and render honest lower-bound/unavailable statuses.
- Updated guidance and templates in `AGENTS.md`, `docs/AI-DEVELOPMENT-WORKFLOW.md` (reconciling field requirement and budget separation), and `docs/AI-ACTIVITY-JOURNAL.md` (fixing Issue #15 reference).
- Added regression tests in `tests/ai-workflow/test_scope_check.py` and `tests/ai-workflow/test_dashboard.py` (including pre-pagination exclusion, incomplete rendering, `incomplete_results` flag propagation with 0 and nonzero items, complete responses, and malformed response rejection).
- Ran full test suite via `python -m unittest discover -s tests/ai-workflow -v` (204 tests passing).

### Evidence & Limitations
- **Test Evidence**: 204 python unittest tests passed locally across `test_scope_check.py`, `test_dashboard.py`, `test_handoff.py`, `test_review_packet.py`, and `test_pr_body_updater.py`.
- **Scope Compliance**: All changes strictly confined to the 10 allowed paths in Issue #48.
- **Privacy Check**: Zero private paths, tokens, or personal home directories introduced.

### Observable Measurements
- Human decision/action interventions: 0
- Human status queries: 0
- Repeated investigations from missing context: 0
- Session blocked / required extra session: No
- Quota / elapsed clock time: not measured
- Journal word count: ~680 words

### Outcome & Next Steps
- Result: REVISED (PR #49 Review 2 / R3 addressed)
- Resulting Head: `workflow/issue-48-bounded-observations-and-dashboard` (PR #49)
- Next Action & Owner: ChatGPT coordinator verification of revised PR #49 head; human retains merge authority.
- Out-of-scope findings: none


