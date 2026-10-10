# Handoff Snapshot Reference: Markdown Formatter & CLI

> **Source Reference**: Inspected from `scripts/ai-workflow/generate_handoff.py` at pinned baseline commit `19a59d6d90bb69a84afb4c12aaf312c192f866e4` (static inspection only; pure documentation without helper execution).

---

## Document Outline

- **Section 1**: CLI Synopsis, Parameters & Invocation
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
| `--pr` | `int` | **Required** | None | Target Pull Request number to inspect. Parsed via `type=int` without positive-integer validation in `main`. |
| `--issue` | `int` | Optional | `None` | Primary task issue number. If omitted, extracted from PR body contract if present. |
| `--repo` | `str` | Optional | `$GITHUB_REPOSITORY` or `Tiflit/DXVK-Companion` | Repository identity in `owner/repo` format. Validated against repository pattern. |
| `--token` | `str` | Optional | Evaluates `get_default_github_token()` | GitHub API token. Evaluated during parser construction even if an explicit token is supplied. Falls back from `GITHUB_TOKEN` and `GH_TOKEN` to `gh auth token` (3s timeout). |
| `--worktree` | `Path` | Optional | `None` | Optional path to local workspace. If omitted, local inspection defaults to current working directory (`os.getcwd()`). |
| `--output` | `Path` | Optional | `stdout` | File path destination for output snapshot. Parent directories are created automatically. |
| `--json` | flag | Optional | `False` | Outputs structured JSON representation instead of standard formatted Markdown. |
| `--max-words` | `int` | Optional | `300` | Word limit for Markdown snapshot output (excluding URLs). Default defined by `MAX_HANDOFF_WORDS`. |

### 1.3 Argument Validation & Environment Discovery

- **Repository Validation**: Validates `--repo` matching `^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$`. Invalid formats abort execution with exit code 1.
- **Token Discovery**: The function `get_default_github_token()` is evaluated during argument parser definition as the default value for `--token`. It inspects `GITHUB_TOKEN` and `GH_TOKEN` environment variables, then attempts fallback to `gh auth token` with a 3-second timeout.
- **Local Workspace Inspection**: The function `inspect_local_workspace(worktree_path)` runs unconditionally. When `--worktree` is omitted, it defaults to the current working directory (`os.getcwd()`). It executes non-destructive read-only queries (`git rev-parse HEAD`, `git rev-parse --abbrev-ref HEAD`, `git status --porcelain` with 5s timeouts) to determine if the target directory is a Git repository, capturing branch name, HEAD commit SHA, and a clean/dirty status summary.

---

## Section 2: Markdown Output Schema, Word-Budget & Evidence Limits

### 2.1 Document Header & Metadata (Illustrative Pattern)
The generated Markdown document opens with a level-1 heading and an evidence capture banner:

```text
# Task Handoff Snapshot: PR #<PR_NUMBER>

> **Captured**: <ISO8601_UTC_TIME> | **Repo**: `<OWNER>/<REPO>`
```

- **`capture_time_utc`**: Formatted as `YYYY-MM-DD HH:MM:SS UTC`.
- **`repo`**: Canonical repository string (`owner/repo`).

---

### 2.2 Schema Section 1: Verified GitHub Revisions & Task State
Heading: `## 1. Verified GitHub Revisions & Task State`

This section reports acquired revision identities of the task contract, pull request, remote branches, and local workspace:

- **Task Contract**:
  - Emitted line format (illustrative): `- **Task Contract**: Issue #<ISSUE_NUMBER> (<SANITIZED_TITLE>) [<STATE>]`
  - Fallback: `- **Task Contract**: UNRESOLVED (No primary issue link found)`
  - Title is sanitized and truncated to 60 characters via `sanitize_display_text(..., max_len=60)`; state is capitalized (`OPEN`, `CLOSED`). Parsed from the Issue description body, not comments.
- **Pull Request**:
  - Emitted line format (illustrative): `- **Pull Request**: PR #<PR_NUMBER> (<SANITIZED_TITLE>) [<STATE>]`
  - Title sanitized and truncated to 60 characters; state capitalized.
- **Live PR Head SHA**:
  - Emitted line format: `- **Live PR Head SHA**: <40_HEX_DIGIT_SHA>`
  - Acquired directly from GitHub API pull request `head.sha`.
- **Live PR Base Branch**:
  - Emitted line format: `- **Live PR Base Branch (<BASE_REF>)**: <40_HEX_DIGIT_SHA>`
  - Identifies target branch name (e.g., `main`) and its current commit SHA.
- **Live Default Branch & Sync Status**:
  - Emitted line format: `- **Live Default Branch (<DEFAULT_BRANCH>)**: <40_HEX_DIGIT_SHA> (<SYNC_STATUS>)`
  - Sync status is computed using the following exact branch classifications (the implementation does not compute ahead/behind commit counts):
    - `UNKNOWN (default branch ref unavailable)`: When the default branch SHA cannot be resolved (`unknown`).
    - Base ref equals default branch:
      - `synced`: When `live_default_sha == live_pr_base_sha`.
      - `BASE MOVED`: When `live_default_sha != live_pr_base_sha`.
    - Base ref differs from default branch (`pr_base_ref != repo_default_branch`):
      - `NON-DEFAULT PR TARGET (<base_ref> != <default_branch>; synced to <base_ref>)`
      - `NON-DEFAULT PR TARGET (<base_ref> != <default_branch>; BASE MOVED: target is <target_sha[:7]>)`
      - `NON-DEFAULT PR TARGET (<base_ref> != <default_branch>; target ref unavailable)`
- **Local Workspace (Separate Identity)**:
  - Emitted line format: `- **Local Workspace (Separate Identity)**: branch <BRANCH> @ <HEAD_SHA> (<STATUS_SUMMARY>)`
  - Included whenever any Git identity is acquired (`is_git=True`), whether from an explicit `--worktree` argument or from the current working directory fallback (`os.getcwd()`).
  - Reports branch name, commit SHA, and worktree cleanliness (`clean` or `dirty`). Labeled as a separate identity to avoid conflating local worktree state with remote PR state.

---

### 2.3 Schema Section 2: CI Verification & Evidence Provenance
Heading: `## 2. CI Verification & Evidence Provenance`

This section documents verified test and build evidence, distinguishing direct head runs from synthetic merge refs:

- **Triggering Run**:
  - Emitted line format: `- **Triggering Run**: ID <RUN_ID> (attempt <RUN_ATTEMPT>) (<RUN_NAME>) -> **<CONCLUSION>**`
  - Fallback: `- **Triggering Run**: UNAVAILABLE (No 'Build and Test' workflow run found for PR head)`
  - Queries GitHub Actions runs for the workflow named `"Build and Test"` matching the live PR head SHA.
- **Tested Checkout SHA**:
  - Direct Head: `- **Tested Checkout SHA**: <CHECKOUT_SHA> (direct head checkout)`
  - Synthetic Merge: `- **Tested Checkout SHA**: <CHECKOUT_SHA> (synthetic merge ref refs/pull/<PR>/merge (verified parents: base <BASE_SHA[:7]>, head <HEAD_SHA[:7]>))`
  - Synthetic Merge (Moved Base): `- **Tested Checkout SHA**: <CHECKOUT_SHA> (synthetic merge ref refs/pull/<PR>/merge (verified parents: older base <OLDER_BASE[:7]>, head <HEAD_SHA[:7]>; BASE MOVED))`
  - Fallback (Unproven / Missing):
    `- **Tested Checkout SHA**: UNAVAILABLE / UNPROVEN`
    Followed by `- **Unverified Self-Reported Checkout**: <UNVERIFIED_DETAILS>` when provenance records a commit whose parent relationship to live PR head cannot be established.
- **TRX Test Totals**:
  - Emitted line format: `- **TRX Test Totals**: <PASSED> passed, <FAILED> failed, <SKIPPED> skipped (total <TOTAL>)`
  - Fallback: `- **TRX Test Totals**: UNAVAILABLE`
  - Test counts are extracted from the `.trx` file matching the specific triggering run attempt via `resolve_trx_artifact_for_attempt`.
- **Attributed Review Records (PR Body)**:
  - Scans PR description body strictly for marked review blocks delimited by `<!-- AI-REVIEW-RECORD: <record_id> -->` and `<!-- AI-REVIEW-RECORD-END -->`, masking inline code spans and code blocks via `mask_code_spans` prior to matching.
  - Summarizes each record ID, review outcome (`PASS`, `CHANGES REQUIRED`, `INCOMPLETE`, `UNKNOWN`), and reviewed commit SHA.
  - Emitted line format: `- **Attributed Review Records (PR Body)**: <COUNT> record(s) found in PR body (<RECORDS_SUMMARY>) (Conversation comments and formal reviews not inspected; absence in body does not prove absence of review)`
  - Fallback when zero records present: `- **Attributed Review Records (PR Body)**: 0 recorded in PR body (Conversation comments and formal reviews not inspected; absence in body does not prove absence of review)`

---

### 2.4 Schema Section 3: Decision Prerequisites & Governance
Heading: `## 3. Decision Prerequisites & Governance`

This section reports architectural and governance decision status extracted strictly from the `Decision Governance Block` in the primary Issue description **body** (subsequent issue conversation comments, such as Gate activations or human approvals, are not parsed by the generator):

- **`PENDING`**:
  - `- **Decision Required**: <DECISION_TEXT>`
  - `- **Decision Status**: PENDING (BLOCKED: explicit human approval pending)`
  - `- **Human Approval Source**: <APPROVAL_SOURCE_OR_NONE>`
- **`DECIDED`**:
  - `- **Decision Required**: <DECISION_TEXT>`
  - `- **Decision Status**: DECIDED`
  - `- **Human Approval Source**: <APPROVAL_SOURCE>`
- **`UNMIGRATED_PROSE_DECISION`**:
  - `- **Decision Governance**: UNMIGRATED PROSE DECISION (Preflight verification required)`
  - `- **Details**: <DECISION_TEXT>`
- **`NO_DECISION_BLOCK`**:
  - `- **Decision Governance**: UNRECORDED / NO DECISION BLOCK (Standard workflow if no architectural policy applies)`
- **`UNAVAILABLE` / Other**:
  - `- **Decision Governance**: UNAVAILABLE (<DETAILS>)` or `- **Decision Governance**: <STATUS>`

---

### 2.5 Schema Section 4: Next Ownership & Action
Heading: `## 4. Next Ownership & Action`

This section determines the next operational role and concrete task using a deterministic state machine:

- **Role & Action Determination Rules**:
  1. **Failing / Incomplete CI**: If CI conclusion is `failure`, `timed_out`, or `cancelled`:
     - **Next Owner**: `Gemini`
     - **Exact Action**: `Bounded repair for failing CI check`
  2. **Pending CI**: If CI conclusion is `in_progress`, `queued`, `waiting`, or `pending`:
     - **Next Owner**: `CI`
     - **Exact Action**: `Await workflow completion`
  3. **Review Findings**: Evaluates the **last eligible matching-head record** in `current_head_reviews` (filtering records where `reviewed_head_sha` is a valid 40-character SHA matching `live_pr_head_sha`, and `reviewed_base_sha`, if present, is a valid 40-character SHA matching `live_pr_base_sha`). If that last eligible record's result is `CHANGES REQUIRED`:
     - **Next Owner**: `Gemini`
     - **Exact Action**: `Focused revision addressing reviewer findings (<RECORD_ID>)`
  4. **Blocked Governance**: If decision status is `PENDING` or `UNMIGRATED_PROSE_DECISION`:
     - **Next Owner**: `Human`
     - **Exact Action**: `Explicit decision required on Issue #<ISSUE_NUMBER>`
  5. **Open Pull Request**: If PR state is `open`:
     - **Next Owner**: `ChatGPT`
     - **Exact Action**: `Coordinator verification of current head`
     - *Note*: This branch is reached whenever none of the preceding conditions trigger, including when CI evidence is unavailable or missing. It does **not** establish that all checks passed or that the PR is ready for merge (the generator performs no automatic merge-readiness inference).
  6. **Closed Pull Request / Other**:
     - **Next Owner**: `Human`
     - **Exact Action**: `Lifecycle closeout`
- **Remaining Uncertainties**:
  - Format: `- **Remaining Uncertainties**: <UNCERTAINTY_1>; <UNCERTAINTY_2>; ...`
  - Aggregates all anomalies recorded during inspection (e.g., CI head SHA mismatch, missing provenance, unproven merge ref parents, TRX parse failure, ambiguous artifacts, base branch movement).

---

### 2.6 Word-Budget Truncation Algorithm

Handoff snapshots enforce a configurable word-budget limit:

1. **Word-Counting Metric (`count_words_excluding_urls`)**:
   - Strips HTTP and HTTPS URLs (`https?://\S+`).
   - Strips Markdown formatting symbols (`#`, `|`, `-`, `*`, `` ` ``, `_`, `>`, `~`).
   - Splits the remaining text on whitespace and counts tokens.
2. **Default & Configurable Limits**:
   - Default budget is defined by `MAX_HANDOFF_WORDS = 300`.
   - Accepts arbitrary integer values via `--max-words <INT>`.
3. **Line-Prefix Truncation Heuristic**:
   - The full Markdown text is formatted in memory, and its word count is evaluated.
   - If `words > max_words`:
     - Lines are accumulated iteratively while `cur_words + line_w <= (max_words - 15)`.
     - When adding a line would exceed `(max_words - 15)`, iteration terminates, and the trailing notice is appended:
       `> ... [Handoff truncated to meet <MAX_WORDS>-word budget; total was <WORDS> words]`
4. **Behavioral Caveats & Limitations**:
   - Line-prefix accumulation is a heuristic approach, not an unconditional guarantee. For very small or nonpositive `--max-words` values (e.g., `<= 15`), zero lines are emitted before appending the notice, and the notice itself will exceed the budget.
   - Truncation halts at the first line that breaches the budget threshold; there is no guarantee that all four sections will survive truncation if earlier sections consume the word allowance.

---

### 2.7 Evidence Boundaries & Operational Effects

- **Operational Effects of Evidence Collection**:
  - While `generate_handoff.py` never modifies tracked project or product files, executing the script produces the following operational effects:
    1. Subprocess execution for token discovery (`gh auth token` fallback).
    2. Downloading artifact archives (up to 50MB) and extracting `.trx` zip contents into a temporary directory (`tempfile.TemporaryDirectory`).
    3. Writing output files and creating parent directories if `--output` is specified.
  - *Note*: This documentation reference exercise performs static source inspection only and does not execute these operations.
- **Static Issue Body Parsing Scope**:
  - Decision governance is parsed strictly from the `Decision Governance Block` in the primary Issue description **body**. Authoritative subsequent comments (such as Gate activations or human approvals posted as issue comments) are not queried or parsed by the generator.
- **Separation of Local and Remote Identities**:
  - Local workspace facts (`branch`, `head_sha`, `status_summary`) are reported under their own separate identity. Remote PR state (`head.sha`, `base.sha`) is never assumed to match local disk without explicit verification.
- **Synthetic Merge vs. Direct Head Provenance**:
  - GitHub Actions merge commits (`refs/pull/<PR>/merge`) are verified by inspecting commit parentage against live PR base and head SHAs. If parentage cannot be proven, the checkout is explicitly labeled `UNPROVEN`.
- **Review Record Parsing Limits**:
  - Attributed reviews are extracted solely from structured HTML comment blocks in the PR body. Formal GitHub pull request reviews and discussion comments are not queried; absence in the body does not prove absence of review.
- **Advisory Role**:
  - Handoff snapshots record evidence and recommend the next operational role. They do not grant execution authority, perform automatic merges, or supersede human governance decisions.
