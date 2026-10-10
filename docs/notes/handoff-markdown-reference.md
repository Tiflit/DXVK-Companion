# Handoff Snapshot Reference: Markdown Formatter & CLI

> **Source Reference**: Inspected from `scripts/ai-workflow/generate_handoff.py` at pinned baseline commit `19a59d6d90bb69a84afb4c12aaf312c192f866e4` (static inspection only; pure documentation without helper execution).

---

## Document Outline

- **Section 1**: CLI Synopsis, Parameters & Invocation (Documented below)
- **Section 2**: Markdown Output Schema, Word-Budget Truncation & Evidence Limitations

---

## Section 1: CLI Synopsis & Arguments

### 1.1 Synopsis

`generate_handoff.py` generates an evidence-bound, read-only handoff snapshot summarizing current pull request state, verification evidence, decision governance, and next ownership.

```text
python scripts/ai-workflow/generate_handoff.py --pr <PR_NUMBER> [OPTIONS]
```

### 1.2 CLI Parameters

| Flag | Type | Requirement | Default | Description |
|---|---|---|---|---|
| `--pr` | `int` | **Required** | None | Target Pull Request number to inspect. Must be a positive integer. |
| `--issue` | `int` | Optional | `None` | Primary task issue number. If omitted, extracted from PR body contract if present. |
| `--repo` | `str` | Optional | `$GITHUB_REPOSITORY` or `Tiflit/DXVK-Companion` | Repository identity in `owner/repo` format. Validated against repository pattern. |
| `--token` | `str` | Optional | Environment / `gh auth token` | GitHub API access token (`GITHUB_TOKEN` or `GH_TOKEN`). Falls back to `gh` CLI token. |
| `--worktree` | `Path` | Optional | `None` | Path to local workspace worktree. Used to inspect local Git branch and head identity. |
| `--output` | `Path` | Optional | `stdout` | File path destination for output snapshot. Parent directories are created automatically. |
| `--json` | flag | Optional | `False` | Outputs structured JSON representation instead of standard formatted Markdown. |
| `--max-words` | `int` | Optional | `300` | Word limit for Markdown snapshot output (excluding URLs). Default defined by `MAX_HANDOFF_WORDS`. |

### 1.3 Argument Validation & Environment Discovery

- **Repository Validation**: Validates `--repo` matching `^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$`. Invalid formats abort execution.
- **Token Discovery**: If `--token` is not explicitly passed, inspects `GITHUB_TOKEN` and `GH_TOKEN` environment variables, then attempts fallback to `gh auth token` with a 3-second timeout.
- **Local Worktree Inspection**: When `--worktree` is provided, executes non-destructive read-only queries (`git rev-parse`, `git status --porcelain`) within the specified path to capture local branch name, local HEAD commit SHA, and clean/dirty status.

---

## Section 2: Markdown Output Schema, Word-Budget & Evidence Limits

### 2.1 Document Header & Metadata
The generated Markdown document opens with a level-1 heading and an evidence capture banner:

```markdown
# Task Handoff Snapshot: PR #<PR_NUMBER>

> **Captured**: <ISO8601_UTC_TIME> | **Repo**: `<OWNER>/<REPO>`
```

- **`capture_time_utc`**: Formatted as `YYYY-MM-DD HH:MM:SS UTC`.
- **`repo`**: Canonical repository string (`owner/repo`).

---

### 2.2 Schema Section 1: Verified GitHub Revisions & Task State
Heading: `## 1. Verified GitHub Revisions & Task State`

This section reports the acquired revision identities of the task contract, pull request, remote branches, and local workspace:

- **Task Contract**:
  - Format: `- **Task Contract**: Issue #<ISSUE_NUMBER> (<SANITIZED_TITLE>) [<STATE>]`
  - Fallback: `- **Task Contract**: UNRESOLVED (No primary issue link found)`
  - Title is sanitized and truncated to 60 characters via `sanitize_display_text(..., max_len=60)`; state is capitalized (`OPEN`, `CLOSED`).
- **Pull Request**:
  - Format: `- **Pull Request**: PR #<PR_NUMBER> (<SANITIZED_TITLE>) [<STATE>]`
  - Title sanitized and truncated to 60 characters; state capitalized.
- **Live PR Head SHA**:
  - Format: `- **Live PR Head SHA**: `<40_HEX_DIGIT_SHA>``
  - Acquired directly from GitHub API pull request `head.sha`.
- **Live PR Base Branch**:
  - Format: `- **Live PR Base Branch (<BASE_REF>)**: `<40_HEX_DIGIT_SHA>``
  - Identifies target branch name (e.g., `main`) and its current commit SHA.
- **Live Default Branch**:
  - Format: `- **Live Default Branch (<DEFAULT_BRANCH>)**: `<40_HEX_DIGIT_SHA>` (<SYNC_STATUS>)`
  - Sync status is computed relative to the base branch: `up to date`, `diverged (<N> commits ahead/behind)`, or `base moved`.
- **Local Workspace (Separate Identity)**:
  - Format: `- **Local Workspace (Separate Identity)**: branch `<BRANCH>` @ `<HEAD_SHA>` (<STATUS_SUMMARY>)`
  - Included only when `--worktree` is specified and identifies a valid Git repository.
  - Reports branch name, commit SHA, and worktree cleanliness (`clean` or `dirty`). Labeled as a separate identity to avoid conflating local worktree state with remote PR state.

---

### 2.3 Schema Section 2: CI Verification & Evidence Provenance
Heading: `## 2. CI Verification & Evidence Provenance`

This section documents verified test and build evidence, distinguishing direct head runs from synthetic merge refs:

- **Triggering Run**:
  - Format: `- **Triggering Run**: ID `<RUN_ID>` (attempt <RUN_ATTEMPT>) (<RUN_NAME>) -> **<CONCLUSION>**`
  - Fallback: `- **Triggering Run**: UNAVAILABLE (No 'Build and Test' workflow run found for PR head)`
  - Queries GitHub Actions runs for the workflow named `"Build and Test"` matching the live PR head SHA.
- **Tested Checkout SHA**:
  - Format (Direct Head): `- **Tested Checkout SHA**: `<CHECKOUT_SHA>` (direct head checkout)`
  - Format (Synthetic Merge): `- **Tested Checkout SHA**: `<CHECKOUT_SHA>` (synthetic merge ref refs/pull/<PR>/merge (verified parents: base <BASE_SHA[:7]>, head <HEAD_SHA[:7]>))`
  - Format (Synthetic Merge with Moved Base): `- **Tested Checkout SHA**: `<CHECKOUT_SHA>` (synthetic merge ref refs/pull/<PR>/merge (verified parents: older base <OLDER_BASE[:7]>, head <HEAD_SHA[:7]>; BASE MOVED))`
  - Fallback (Unproven / Missing):
    `- **Tested Checkout SHA**: UNAVAILABLE / UNPROVEN`
    Followed by `- **Unverified Self-Reported Checkout**: <UNVERIFIED_DETAILS>` when provenance records a commit whose parent relationship to live PR head cannot be established.
- **TRX Test Totals**:
  - Format: `- **TRX Test Totals**: <PASSED> passed, <FAILED> failed, <SKIPPED> skipped (total <TOTAL>)`
  - Fallback: `- **TRX Test Totals**: UNAVAILABLE`
  - Test counts are extracted from the `.trx` file matching the specific triggering run attempt via `resolve_trx_artifact_for_attempt`.
- **Attributed Review Records (PR Body)**:
  - Format: `- **Attributed Review Records (PR Body)**: <COUNT> record(s) found in PR body (<RECORDS_SUMMARY>) (Conversation comments and formal reviews not inspected; absence in body does not prove absence of review)`
  - Fallback: `- **Attributed Review Records (PR Body)**: 0 recorded in PR body (Conversation comments and formal reviews not inspected; absence in body does not prove absence of review)`
  - Scans PR description strictly for marked review blocks (`<!-- AI-<NAME>-RECORD-START --> ... <!-- AI-<NAME>-RECORD-END -->`). Summarizes each record ID, review outcome (`PASS`, `CHANGES REQUIRED`, `INCOMPLETE`, `UNKNOWN`), and reviewed commit SHA.

---

### 2.4 Schema Section 3: Decision Prerequisites & Governance
Heading: `## 3. Decision Prerequisites & Governance`

This section reports architectural and governance decision status extracted from the `Decision Governance Block` in the primary issue:

- **`PENDING`**:
  - Emits:
    `- **Decision Required**: <DECISION_TEXT>`
    `- **Decision Status**: \`PENDING\` (BLOCKED: explicit human approval pending)`
    `- **Human Approval Source**: <APPROVAL_SOURCE_OR_NONE>`
- **`DECIDED`**:
  - Emits:
    `- **Decision Required**: <DECISION_TEXT>`
    `- **Decision Status**: \`DECIDED\``
    `- **Human Approval Source**: <APPROVAL_SOURCE>`
- **`UNMIGRATED_PROSE_DECISION`**:
  - Emits:
    `- **Decision Governance**: UNMIGRATED PROSE DECISION (Preflight verification required)`
    `- **Details**: <DECISION_TEXT>`
- **`NO_DECISION_BLOCK`**:
  - Emits: `- **Decision Governance**: UNRECORDED / NO DECISION BLOCK (Standard workflow if no architectural policy applies)`
- **`UNAVAILABLE` / Other**:
  - Emits: `- **Decision Governance**: UNAVAILABLE (<DETAILS>)` or `- **Decision Governance**: \`<STATUS>\``

---

### 2.5 Schema Section 4: Next Ownership & Action
Heading: `## 4. Next Ownership & Action`

This section determines the next operational role and concrete task using a deterministic state machine:

- **Role & Action Determination Rules**:
  1. **Failing / Incomplete CI**: If CI conclusion is `failure`, `timed_out`, or `cancelled`:
     - **Next Owner**: `**Gemini**`
     - **Exact Action**: `Bounded repair for failing CI check`
  2. **Pending CI**: If CI conclusion is `in_progress`, `queued`, `waiting`, or `pending`:
     - **Next Owner**: `**CI**`
     - **Exact Action**: `Await workflow completion`
  3. **Review Findings**: If any current head review record concludes `CHANGES REQUIRED`:
     - **Next Owner**: `**Gemini**`
     - **Exact Action**: `Focused revision addressing reviewer findings (<RECORD_ID>)`
  4. **Blocked Governance**: If decision status is `PENDING` or `UNMIGRATED_PROSE_DECISION`:
     - **Next Owner**: `**Human**`
     - **Exact Action**: `Explicit decision required on Issue #<ISSUE_NUMBER>`
  5. **Open Pull Request**: If PR state is `open` and all checks pass/pending conditions cleared:
     - **Next Owner**: `**ChatGPT**`
     - **Exact Action**: `Coordinator verification of current head`
  6. **Closed Pull Request**:
     - **Next Owner**: `**Human**`
     - **Exact Action**: `Lifecycle closeout`
- **Remaining Uncertainties**:
  - Format: `- **Remaining Uncertainties**: <UNCERTAINTY_1>; <UNCERTAINTY_2>; ...`
  - Aggregates all anomalies recorded during inspection (e.g., CI head SHA mismatch, missing provenance, unproven merge ref parents, TRX parse failure, ambiguous artifacts, base branch movement).

---

### 2.6 Word-Budget Truncation Algorithm

Handoff snapshots enforce a strict, configurable word-budget limit to ensure summaries remain concise and within context limits:

1. **Word-Counting Metric (`count_words_excluding_urls`)**:
   - Strips HTTP and HTTPS URLs (`https?://\S+`).
   - Strips Markdown formatting symbols (`#`, `|`, `-`, `*`, `` ` ``, `_`, `>`, `~`).
   - Splits the remaining text on whitespace and counts tokens.
2. **Default & Configurable Limits**:
   - Default budget is defined by `MAX_HANDOFF_WORDS = 300`.
   - Overrideable via the `--max-words <INT>` CLI argument.
3. **Truncation Threshold & Execution**:
   - The total word count of the formatted Markdown is evaluated.
   - If `words > max_words`:
     - Lines are accumulated iteratively while `cur_words + line_w <= (max_words - 15)`.
     - The 15-word reserve guarantees that the truncation notice fits within the maximum word budget.
     - As soon as a line would exceed the reserve threshold, line emission halts, and the trailing notice is appended:
       ```markdown
       > ... [Handoff truncated to meet <MAX_WORDS>-word budget; total was <WORDS> words]
       ```

---

### 2.7 Evidence Boundaries & Provenance Limitations

- **Read-Only Non-Destructive Inspection**: `generate_handoff.py` performs purely non-destructive read queries against the GitHub REST API and Git CLI metadata. It never runs builds, tests, generator scripts, or destructive file modifications.
- **Strict Separation of Local and Remote Identities**: Local workspace facts (`--worktree`) are reported under their own separate identity. Remote PR state (`head.sha`, `base.sha`) is never assumed to match local disk without explicit verification.
- **Synthetic Merge vs. Direct Head Provenance**: GitHub Actions merge commits (`refs/pull/<PR>/merge`) are verified by inspecting commit parentage against live PR base and head SHAs. If parentage cannot be proven, the checkout is explicitly labeled `UNPROVEN`.
- **Review Record Parsing Limits**: Attributed reviews are extracted solely from structured HTML comment blocks within the PR body. Formal GitHub pull request reviews and discussion comments are not queried; absence in the body does not prove absence of review.
- **Advisory Role**: Handoff snapshots provide evidence-bound status summaries and recommend the next operational role. They do not grant execution authority, perform automatic merges, or supersede human governance decisions.
