# AI Development Pilot Log

This file records durable lessons from the multi-agent development experiments.

The goal is not to preserve complete conversations. Record decisions, observations, defects, and workflow lessons that future development sessions need to know.

## Pilot #1 — CompanionVersion version ordering

### Task

Correct `CompanionVersion.IsOutdatedComparedTo` so update detection uses numeric MAJOR.MINOR.PATCH ordering rather than string inequality.

Tracked by Issue #5 and implemented in PR #4.

### Agents

- Implementation: Gemini / Antigravity
- Independent review: Claude
- Architecture / workflow coordination: ChatGPT
- Deterministic validation: GitHub Actions

### Implementation outcome

The implementation was isolated onto a clean task branch rather than leaving the work on a branch containing unrelated changes.

The production change:

- accepts stable three-part numeric versions;
- accepts one leading `v` or `V`;
- compares major, minor, then patch numerically;
- returns false for malformed or unsupported inputs.

Regression coverage included equal, older, newer, numeric ordering, prefix handling, and malformed/unsupported inputs.

### Review outcome

The independent review found no Critical, High, or Medium defects.

It identified two low-severity/future-facing concerns:

1. prerelease versions are treated as unsupported rather than ordered;
2. build metadata would become unsupported if the project version pin were ever removed.

Neither contradicted the task contract, so the implementation was not expanded to address them.

### Workflow lessons

#### 1. The task contract must live in GitHub

The initial contract existed primarily in the AI conversation.

The independent reviewer therefore had to reconstruct requirements from conversation context, code, and tests.

**Decision:** GitHub Issue is the authoritative task contract.

#### 2. Task branches must be isolated

The original working branch contained unrelated changes.

**Decision:** Each pilot/task should use a branch whose starting point is the intended base and whose diff contains only task-related work.

#### 3. CI claims need evidence

An implementation agent's report is not sufficient evidence that CI passed.

**Decision:** Important workflow reports should identify the exact commit and actual GitHub Actions result.

#### 4. Independent review should remain independent

The reviewer should not receive another agent's reasoning as an authority.

**Decision:** Give Claude the Issue, actual branch/PR evidence, and focused review instructions, but do not give it Gemini's conclusions beforehand.

#### 5. Documentation is part of completion

The useful result of a pilot includes the workflow lesson, not just the code change.

**Decision:** Significant pilots update durable repository documentation.


### Status and provenance as of 2026-10-03

PR #4 remains open at 944abc08c8722bdfc3b13bf7fda8fdd5a8a25b65. The historical external review summary does not establish its exact reviewed SHA; do not retrospectively stamp the current head as reviewed without original evidence. Before this documentation update PR #4 had no conversation comments or submitted GitHub reviews.

## Workflow foundation — Issue #7 / PR #8

PR #8 merged into main at 3a66376446d847434542410a2a3626a070639aa8. It added AGENTS.md, AI-DEVELOPMENT-WORKFLOW.md, this pilot log, the structured AI Issue form and advisory hygiene/scope plus post-CI review-packet workflows. No external orchestrator or automatic model invocation/merge was introduced. The packet has been exercised, but its source/CI association needs hardening.

## Checkpoint supersession — 2026-10-03

This update replaces PR #10's old d9051cab/build-failure checkpoint. Those failures remain historical below. PR #9 now has successful integration CI and external Claude PASS. PR #4 and PR #9 remain open. Documentation is proposed through PR #10; human merge authority is unchanged.

The imported review is attributed to Claude and the arbitration to ChatGPT. Neither is a native GitHub approval. Issue #6 already contains three earlier checkpoint comments; the final review had not previously been recorded on PR #9.

## 4. Pilot #2 contract and implementation history

Issue #6: detect/report DX12 and Vulkan but prevent DXVK deployment through automated, manual/direct, update, Reapply, adoption and pending-action paths. Retain DX9/DX10/DX11 support and safe Disable/Restore behavior. No new translation layer or expanded DX12/Vulkan support.

The original broad product invariant needs careful qualification: PR #9 enforces compatibility given the target profile's classified API. It does not prove safety for every executable sharing a directory, nor correct classification of every late-loaded graphics runtime.

Compatibility contract at the reviewed revision:

| GraphicsApi | Ordinal | DXVK behavior |
|---|---:|---|
| Unknown | 0 | unsupported |
| DX9 | 1 | d3d9.dll |
| DX10 | 2 | d3d11.dll + dxgi.dll |
| DX11 | 3 | d3d11.dll + dxgi.dll |
| ModernAPI | 4 | retained legacy informational value; unsupported |
| DX12 | 5 | native/informational; unsupported |
| Vulkan | 6 | native/informational; unsupported |

Before the fix, ModernAPI could pass the compatibility/deployment boundary and receive d3d11.dll + dxgi.dll.

History:
1. First implementation at fc5ca58e9ebef34f28ad71a8a6afece60d2333a4; Claude returned CHANGES REQUIRED.
2. The clear material catch was a vacuous installer Reapply test: it could return false because the installation was unmanaged, before exercising the API guard.
3. Other first-review priorities: pending execution, restore safety after reclassification, supported positive controls, mixed-module classification, removal of unnecessary enum aliases/serialization changes, and real CI evidence.
4. A planned pre-implementation Claude design review did not happen because quota ran out.
5. Head fb04a0cb3139bc915876a6fd110d8b15b9bed429 failed Phase A test compilation with malformed/duplicated test content.
6. Head d9051cab30d5e5bde3d066fa8e04527e5e386f04 cleaned the malformed file and removed enum aliases/converter, then Build/Test #70 failed application compilation: Logger was not in scope at DxvkManager.cs lines 219 and 295. Tests did not run. The tested synthetic merge was 93dab27f030584e496e7517400f0d8a5135544f6.
7. A later CI failure involved a missing PeParser import, as reported in this conversation; its intermediate SHA/run is not reconstructed here.
8. Final reviewed head c4d0f846b4031b08e9e3444c803abe37cc171890 passed Build/Test #74.
9. Claude's final review returned PASS, with F1–F9 below.

Do not call these merely “two compile failures”: the conversation records malformed tests, missing Logger, and missing PeParser as three distinct compile problems.

## 5. Exact CI evidence and the head/merge correction

Build and Test #74:
https://github.com/Tiflit/DXVK-Companion/actions/runs/37091615107

- Run ID: 37091615107.
- Job ID: 111112972258; job name: build-and-test.
- Event: pull_request.
- Run metadata head_sha: c4d0f846b4031b08e9e3444c803abe37cc171890.
- Actual checkout/tested SHA: 8156ffa10f071f8fcc7b9a20f81c7564b9c58f25.
- Merge parents: main 3a66376446d847434542410a2a3626a070639aa8 and PR head c4d0f846b4031b08e9e3444c803abe37cc171890.
- Application and Phase A test builds: success; each reports 0 warnings / 0 errors.
- Full Phase A execution: 96 total / 96 passed.
- Portable self-contained win-x64 publish and artifact upload: successful.

These details were rechecked in the job log and merge commit metadata during this handoff. This is integration evidence for the exact PR head/base pair, NOT evidence of a test run directly checking out the head alone.

Actual test command:
    dotnet test tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj --configuration Release --no-build --no-restore --logger "console;verbosity=detailed" --logger "trx;LogFileName=phase-a-tests.trx"

TRX artifact name: phase-a-test-results.
Executable artifact name: DXVK-Companion-win-x64.

Other successful runs associated with this head:
- AI PR Hygiene #19, run 37091615109.
- AI Scope Check #13, run 37091615185.

Both policy workflows are advisory. Their green status is not proof of compliance. The conversation's inspected logs reported missing Scope and Documentation PR headings, and no parseable Allowed paths in Issue #6. The current scripts and pre-edit PR/Issue bodies confirm why those warnings occur. Neither workflow was an effective blocking policy gate in this pilot.

Claude said it did not execute tests or inspect CI job logs; its 96-test count was explicitly unverified. The coordinator's log verification is distinct evidence and must not be attributed to Claude.

## 6. Claude's final review: provenance, coverage and full finding register

Source: the user's pasted Claude review of PR #9 at c4d0f84, whose full head SHA is recorded above. This is an external AI review, not a GitHub approval submitted by Claude. Its second pass knew the first pass's findings; classify it as REVISION VERIFICATION, not a fresh independent review.

Claude read the full diff, head DxvkManager.cs and DxvkInstaller.cs, MultiFileTransactionEngine.cs and ExistingDxvkAssessment.cs. It did not run code. It found no contract violation or deployment bypass for the target exe under its recorded API and returned PASS.

Coverage traced:
- Automated enable: TrayApp.HandleGameDetected IsDxvkSupported check plus manager.
- Manual enable/update/UpdateAll: QueueOrApplyAsync and EnableDxvkAsync.
- Reapply: QueueOrApplyReapplyAsync, manager ReapplyAsync and installer.
- Pending/persisted execution: ApplyPendingAsync and ProcessAllPendingActionsAsync; guards redundant with deeper boundaries.
- Adoption: manager and installer.
- Restore/Disable: deliberately ungated.
- Transaction engine callers: DxvkInstaller and DxvkRollback; no new caller in this PR.
- ApplyToGameAsync may download to cache before rejecting unsupported APIs, but rejects before game-directory writes/state changes. Only manager calls it in the traced code.
- Direct install tests genuinely cover unsupported APIs and DX9/10/11 positive behavior.
- Revised installer Reapply test uses a managed installation, verifies unchanged files and has a DX11 positive control on the same fixture.

| Finding | Severity / scope | Detail, disposition and next action |
|---|---|---|
| F1: direct adoption test is vacuous | Low; test weakness, nonmaterial | DxvkInstaller_AdoptExisting_DirectCall_RefusesUnsupportedApis has no d3d11.dll/dxgi.dll, so it can reject for missing DLLs with the API gate removed. Manager adoption test is meaningful. Follow-up: create DLLs and add DX11 positive control. Adoption itself writes no game DLLs. |
| F2: pending Reapply subcase is vacuous | Low; test weakness, nonmaterial | Case 2 of the pending BlocksExecutionForUnsupportedApis test never manages the installation; “not managed” can reject before the guard. Meaningful installer-level Reapply coverage exists. Follow-up: establish a real DX11 install first. |
| F3: restore coverage incomplete | Low–Medium; nonmaterial now | DisableDxvkAsync after reclassification is covered; queued RequestDisable with a running game, RestoreAllAsync and persisted Restore are not. Production guards intentionally exclude Restore. Follow-up: managed DX11 → reclassify DX12 → verify Restore All and pending Restore succeed. |
| F4: missing remaining uncertainty in PR body | Low; process | Issue #6 requires remaining design uncertainty. Add Scope/Documentation and residual limitations before merge. Correct the unclosed test-command fence and evidence wording as part of metadata maintenance. |
| F5: shared-directory mixed executables | Medium; architecture, outside accepted target-profile scope | Game_DX11.exe and Game_DX12.exe in one directory can both encounter deployed dxgi.dll. The selected profile gate does not isolate siblings. GameInstallation records each exe's LastKnownApi. Decide whether compatibility is installation-wide; consider refusing recorded modern-API siblings and add a test. Spec §5.2 is relevant. |
| F6: classification precedence/timing | Medium uncertainty | DX11 + Vulkan now selects Vulkan, conservatively rejecting a D3D11 process that loads Vulkan incidentally. This precedence behavior IS changed/pinned by the PR, even though late-load limitations predate it. One-time classification at first window can miss dynamic/late DX12/Vulkan loading; pending execution does not reclassify. Document precedence and investigate reclassification at execution (spec §21). |
| F7: blocked pending actions retained | Low; behavior/follow-up | Test pins retained pending work; safe from the blocked operation, but retried/logged on exits/startup, including older persisted actions. Spec §§20–21 expects reassessment. Consider explicit superseded/incompatible state rather than silently retrying forever. No hurried deletion patch. |
| F8: Reapply baseline/backup gap | Medium; pre-existing, outside #6; unverified by reproducing test | For a newly required DLL lacking a managed record, Reapply may record DidNotExist even if a file already exists, capture identity without backing it up, overwrite it and later delete it on Restore. Example: DX9 installation reclassified DX11 with existing native d3d11.dll/dxgi.dll. Prioritize a separate reproducer and safety issue; do not describe it as a newly introduced or experimentally confirmed defect. |
| F9: enum persistence coverage/downgrade limit | Low; out of scope | Explicit 0–6 ordinals preserve ProfileStore numeric mapping. GameLibraryStore writes enum names; older builds cannot parse DX12/Vulkan, preserve invalid library copy and start empty. Numeric stability does not prove downgrade compatibility. Add legacy ModernAPI/new-member round-trip tests and document downgrade behavior. |

Claude's closing qualification: PASS means Issue #6 acceptance criteria met for the target exe. It does not establish “never deployed” for mixed-exe folders or late API detection. F4 should be fixed before merge; F1/F2 optional follow-ups; F5–F8 deserve follow-up issues.

ChatGPT arbitration: accept the scoped PASS, freeze implementation at c4d0f84, fix documentation and track residual work. Do not spend another focused implementation/review cycle on F1/F2 solely to polish nonmaterial coverage. Broader safety findings remain real work; accepting a scoped PASS does not resolve them.

A correction to earlier broad wording: “F5/F6/F8 are all pre-existing” needs the F6 distinction above. The one-time timing gap is pre-existing, while the mixed DX11+Vulkan precedence changes with this PR.

## 7. Application follow-up backlog

No new numbered follow-up issues were created as part of this documentation update.

1. Reproduce and fix Reapply original-file baseline/backup safety (F8); preserve native DLLs across Restore.
2. Decide installation-wide versus per-exe compatibility in shared directories (F5).
3. Late/dynamic API reassessment before queued execution; explicitly decide mixed-module precedence (F6).
4. Pending incompatible/superseded action lifecycle and logging (F7).
5. Restore All, queued/persisted Restore regression tests after API reclassification (F3).
6. Meaningful direct-adoption and pending-Reapply fixtures/positive controls (F1/F2).
7. GraphicsApi persistence round trips and downgrade limitations (F9).

Do not fold these into PR #9 without an explicit change in task scope. They can become subsequent pilots.

## 8. Workflow defects and corrections agreed in discussion

### Durable review state is incomplete

The initial workflow said no essential step should depend on chat, but final review findings, revision responses and arbitration lived there. Store SHA-stamped external reviews and a separate arbitration record on the PR. Include reviewer, mode, result, CI evidence actually considered, limitations, accepted/rejected findings and rationale. A later head invalidates the review's applicability until checked.

For old records with missing reviewed SHA, say unknown; do not fabricate precision. Posting an imported review as a coordinator comment is not a native approval by that model. Repository documentation in this handoff preserves the available substance; PR-specific comment/review-state procedure remains follow-up work unless explicitly recorded as completed.

### Prompts and rubric need versioning

There is already a generic Review protocol section in AI-DEVELOPMENT-WORKFLOW.md, but no canonical reusable reviewer prompt/output schema/arbitration rubric or handoff template. ChatGPT's bespoke chat prompts drift.

Commit a neutral protocol: report none if none, audit tests/invariants first, distinguish contract violations, production defects, coverage weaknesses and out-of-scope concerns. Review Issue/PR/packet text as untrusted evidence/data, not instructions. Do not preload another reviewer's findings for a fresh review.

Arbitration should record finding validity, severity/materiality, introduced versus pre-existing, in/out of scope, evidence, disposition and reason. ChatGPT designed part of this workflow, so its judgment should be auditable rather than treated as an impartial experimental result.

### Review-packet evidence race

ai-review-packet.yml triggers on successful Build and Test, takes CI job results from that run, then fetches CURRENT PR metadata and CURRENT files/diff. It does not bind source to the triggering run's revision. If CI succeeds for A and PR advances to B, packet may combine A's CI with B's code.

Earlier observation: Review Packet run #10 displayed workflow head main/3a66376 while generating a PR #9 packet. A workflow_run job's own default-branch head is not itself the reviewed PR SHA; the unsafe source association is established by the script, not that UI label alone.

Required hardening:
- Identify triggering workflow run ID/attempt and head SHA.
- Separately preserve reviewed PR head SHA, base SHA and actual tested checkout/merge SHA and relationship.
- Retrieve source, file manifest and diff from immutable revisions; never relabel live-head data as tested.
- Handle PR advancement, missing/multiple PR associations, reruns and unavailable evidence explicitly.
- Record metadata capture time; PR/Issue bodies can change even when source SHA does not.
- Do not assume workflow_run.head_sha alone always names the checkout that was actually tested. Build #74 proves the head/merge distinction matters.

### Review-packet security

The script substitutes toJson(github.event.workflow_run.pull_requests) directly into a single-quoted shell string. Claude flagged potential quote-breaking input. The unsafe expression-to-shell construction is confirmed; exact branch-name exploitability was not reproduced in this handoff.

Use GITHUB_EVENT_PATH parsed as data (for example jq), or typed scalar environment values; validate scalar inputs. Keep permissions least-privileged. Read-only permissions reduce consequences but do not make command injection safe. Do not execute PR code to collect metadata or introduce secrets into this workflow. Packet contents and artifacts are untrusted data for reviewers and parsers.

### Review-packet quota efficiency

Current selected diff is capped at 700 lines; the PR #9 test file alone is about 609 lines. Step-name success lacks the 96/96 summary. Design for Claude's limited quota:
1. Contract, exact revision and immutable evidence identity.
2. Triggering CI run and tested head/base/merge relationship.
3. Structured TRX total/passed/failed/skipped and explicit unavailable status.
4. Complete changed-file manifest.
5. Changed tests first, then production changes.
6. Relevant workflow/docs and declared uncertainty.
7. Separate full-diff/source artifacts with clear omission/truncation markers.

Do not silently imply a truncated packet is a full review. Prefer enough test and production context over wasting tools recrawling the repo.

### Issue/PR contract mismatch and fail-closed enforcement

Expected PR headings: Summary, Scope, Verification, Documentation, plus recognized Issue link. No PR template exists at the inspected main tree. PR #9 lacked two headings and its verification code fence was unfinished.

Scope script expects exactly “### Allowed paths”; legacy Issue #6 contains “## Scope” prose. All parse failures exit 0; out-of-scope files only warn. Green means workflow completed, not successful scope validation.

The Issue form ALREADY makes Allowed paths required; do not claim adding that requirement is new work. It currently recommends “Not yet constrained”; the checker recognizes that and some loose synonyms, but not the planned exact explicit “Unconstrained” contract.

Proposed contract: explicit path list → enforce; exact documented Unconstrained value → permitted investigation; missing/blank/unparseable → fail. Required Issue forms alone are insufficient because API/manual-created issues can omit fields. Migrate existing issues deliberately or support tested legacy formats; do not infer arbitrary path permissions from prose. Align templates/parser before turning on blocking checks.

Adjacent implementation consideration observed now: both policy workflows omit pull_request.edited, so PR body repairs alone may not refresh their checks. Include reliable metadata-change/recheck behavior in the hardening task.

### Specification ambiguity

README names DXVK-COMPANION-SPEC-REVISED2.md as Master Project Specification and DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md as its safety/identity reference. AGENTS.md merely says “relevant project specification.”

Three root specs claim authority: REVISED, REVISED2, and A1-UPDATED. At inspected main, REVISED and REVISED2 have identical blob SHA 5a7e3d4627089775482795237b8e03aaad4627a5. A1-UPDATED differs and includes a Phase A.1 no-legacy-import decision, so do not blindly discard it.

Follow-up: reconcile authority and unique decisions, explicitly identify canonical master/supplement and precedence in AGENTS.md, mark old copies historical/superseded. Do not silently choose precedence for substantive conflicting requirements based only on filenames.

### Enforcement and cost

main's protected flag is false. Repository is public. Prior discussion checked GitHub documentation and concluded public GitHub Free repos can use required checks/protection; before changing settings, recheck actual account/visibility/features and available administration permission. Private-repo reuse may require a paid plan. This handoff does not claim rulesets or branch protection were enabled.

Require mature deterministic gates after parser and packet fixes. Review remains risk-based; do not globally require a Claude account approval. A provider, quota or reviewer failure must not silently become PASS.

## 9. What the experiment does and does not show

Proven mechanically: Issue → Gemini implementation → CI catches compile defects → corrections → successful integration CI → Claude review/revision verification → arbitration.

A useful reviewer catch occurred: the first Reapply test was vacuous. Pilot #1 had no material finding. There was no fresh Gemini control review. Therefore added value of Claude over a fresh Gemini session/CI is not established, and no general dependable-workflow claim is warranted.

Review modes:
- Fresh independent review: clean session, same neutral evidence, no other reviewer's conclusions.
- Revision verification: knows prior findings and checks repairs/regressions; this describes Claude's second Pilot #2 pass.
- Consider a new fresh review for high-risk work or substantial design changes beyond the original fixes, with quota tradeoff explicit.

Pilot #3 proposal: choose a different kind of task (feature/refactor), freeze one SHA and identical packet/protocol, run fresh Gemini reviewer and fresh Claude reviewer without cross-contamination, then adjudicate. Record material valid, nonmaterial valid, false-positive/rejected and unique findings, quota/tool burden and resulting implementation changes. Do not claim this experiment has happened.

For future extraction: template repository should contain AGENTS.md, Issue form, PR template, three workflows, workflow docs, neutral review prompt and pilot-log template. Parameterize Build and Test workflow name, C# file filters/build/test commands and review thresholds. Keep DXVK-specific safety invariants/spec separate. Extraction follows validation; it is not complete.

## 10. Ordered continuation plan

1. Recheck live main/PR #4/#9/#10 heads, CI and documentation changes recorded in PR #10.
2. Preserve revision-specific external review and arbitration records on the relevant PRs. Do not fabricate Pilot #1's reviewed SHA. The current imported documentation is a bridge, not a GitHub native reviewer approval.
3. Verify the completed PR #9 metadata correction (F4) and inspect its current CI relationship. Keep implementation frozen unless material new evidence appears.
4. Workflow-hardening task/PR: immutable packet binding; safe event parsing; test summary; quota-efficient packet; fail-closed scope with tested format alignment and metadata refresh.
5. Workflow-contract task/PR: canonical PR template, neutral versioned reviewer prompt, output schema, arbitration rubric, handoff template, explicit review-state/freshness protocol, spec authority reconciliation.
6. Human decides PR #4 and #9 merges; then close linked Issues as appropriate. Refresh or supersede PR #10 instead of merging an obsolete checkpoint. Record final outcomes rather than declaring pilots complete while PRs remain open.
7. Enable suitable required checks/protection once checks are tested and settings access is confirmed. Preserve human merge authority and risk-based review.
8. Run Pilot #3 with the Gemini control and fresh Claude review; assess effectiveness.
9. Extract a reusable parameterized template only when justified. Automatic model dispatch remains optional and constrained by cost/authentication/security.

No need to ask the user to restate the project. The next session can start from this document and repository evidence.


## Evidence links

- [Pilot #1 PR #4](https://github.com/Tiflit/DXVK-Companion/pull/4)
- [Pilot #2 Issue #6](https://github.com/Tiflit/DXVK-Companion/issues/6)
- [Pilot #2 PR #9](https://github.com/Tiflit/DXVK-Companion/pull/9)
- [Build/Test #74](https://github.com/Tiflit/DXVK-Companion/actions/runs/37091615107)
- [Tested integration commit](https://github.com/Tiflit/DXVK-Companion/commit/8156ffa10f071f8fcc7b9a20f81c7564b9c58f25)
- [Documentation PR #10](https://github.com/Tiflit/DXVK-Companion/pull/10)

## Future pilot record fields

Record task/contract; implementation/reviewer/arbitrator and review mode; exact source/base/tested SHA and CI run; tests; findings and evidence; materiality and scope; accepted/rejected disposition; false positives/unique findings; quota/tool burden; implementation changes caused by review; human merge/reject/escalate outcome; documentation impact. Use “none” or “unknown” when appropriate. Do not invent findings or precision.

## Documentation actions completed on 2026-10-03

PR #10 was refreshed in place, preserving the existing branch and human merge review. It now updates this pilot log and AI-DEVELOPMENT-WORKFLOW.md. PR #9's body now includes the missing headings, a closed command fence, precise CI identity, external-review attribution and residual limitations. Its implementation head remains c4d0f846b4031b08e9e3444c803abe37cc171890. No application/workflow implementation or main-branch changes were made. No native reviews or PR comments were posted; review substance is durable in these documents and the PR body, while a standardized comment/review-state protocol is still pending. No new follow-up Issues, merge, closure, branch protection, paid dispatch or Pilot #3 experiment was performed.

## Continuation checkpoint — 2026-10-03: manual review contract v1

Rechecked live main and PRs #4/#9/#10. Main remains 3a66376446d847434542410a2a3626a070639aa8; PR #9 remains c4d0f846b4031b08e9e3444c803abe37cc171890, with successful Build/Test run 37091615107. PRs remain open; PR #9's corrected metadata is present. This check reconfirmed run status, not a new execution or another review of implementation.

Added Review contract v1 to AI-DEVELOPMENT-WORKFLOW.md on the existing documentation branch: neutral reviewer prompt, required output, separate arbitration rubric, manual states/freshness and compact continuation template. Read back the committed workflow document and verified its exact contents. This is protocol documentation, not automated enforcement or a fresh external review.

Historical review comments, follow-up Issues, canonical PR template, immutable/safe review-packet generation, fail-closed scope checks, specification reconciliation, protection and the controlled Pilot #3 remain pending. PR #4 still needs current-base integration evidence before a human merge decision. Application implementation remains frozen; no merge, closure, message to another agent or paid dispatch occurred.

Next implementation handoff should focus on packet identity/security and contract parser alignment, in a separately scoped Issue/PR. Do not mix these workflow changes into PR #9.

## Workflow hardening checkpoint — Issue #11 initial implementation (SHA: 0abef8d34a836494cdd882b844c781e32fa35322, 2026-10-03)

### Implementation summary

Implemented hardened review packet provenance, blocking scope/hygiene checks, safe structured TRX parsing, and regression fixtures under Issue #11.

- **Offline regression test suite**: 42 automated tests in `tests/ai-workflow/` verifying contract parsing, allowed-path syntax, directory boundary safety, rename checks, PR hygiene headings, TRX counting, zip-slip defense, packet identity, staleness detection, and diff budgeting.
- **Workflow integration**:
  - `.github/workflows/ai-workflow-tests.yml`: runs test suite on push and PR.
  - `.github/workflows/build-and-test.yml`: instruments minimal `build-provenance` artifact (`build-provenance.json`) capturing checked-out SHA (`git rev-parse HEAD`), ref, run ID, and attempt.
  - `.github/workflows/ai-review-packet.yml`: checks out trusted default-branch scripts, parses event JSON safely via Python, generates verified packet and full diff artifact.
  - `.github/workflows/ai-scope-check.yml`: blocking check enforcing repository-relative allowed paths, `/**`, trailing slash, and exact standalone `Unconstrained`. Checks old and new paths for renames.
  - `.github/workflows/ai-pr-hygiene.yml`: blocking check enforcing `Primary Issue`, `Summary`, `Scope`, `Verification`, and `Documentation`.
  - `.github/ISSUE_TEMPLATE/ai-task.yml` & `.github/pull_request_template.md`: aligned template grammar.

### Open PR inventory and legacy contract migrations

Before switching policy workflows from advisory to blocking, open PRs were inventoried:

1. **PR #4** (`pilot/companion-version-ordering`):
   - Head: `944abc08c8722bdfc3b13bf7fda8fdd5a8a25b65`
   - Task contract: Issue #5 (`CompanionVersion.IsOutdatedComparedTo`).
   - Migration action: Edit PR #4 body to add `## Primary Issue` with `Fixes #5`. Check allowed paths against Issue #5.
2. **PR #9** (`issue-6-prevent-dx12-vulkan-deployment`):
   - Head: `c4d0f846b4031b08e9e3444c803abe37cc171890`
   - Task contract: Issue #6 (`Prevent DXVK deployment for DX12/Vulkan`).
   - Migration action: Edit PR #9 body to add `## Primary Issue` with `Fixes #6`. All changed files match Issue #6 allowed paths.
3. **PR #10** (`docs/review-contract-v1`):
   - Merged to `main` at `e7b6e06`. No action required.

Coordinator can update PR #4 and PR #9 descriptions via GitHub UI to satisfy the new blocking checks without needing code changes or recreation.

### Human intervention metrics for Issue #11

- Human intervention count: 1 (initial handoff prompt and worktree setup).
- Human elapsed time: ~5 minutes.
- Model execution: Fully autonomous implementation across fixtures, parsers, CI workflows, and documentation.
- Residual limitations: Model dispatch remains manual; human retains final merge authority.

## Workflow hardening revision checkpoint — Issue #11 revision (F1–F12, A1) (2026-10-03)

### Review and arbitration context

External review by Claude of initial implementation commit `0abef8d34a836494cdd882b844c781e32fa35322` yielded 12 findings (F1–F12). Independent arbitration by ChatGPT confirmed findings and established arbitration ruling A1 (prohibiting mixed `Unconstrained` and explicit path entries). This focused revision addresses findings F1–F12 and ruling A1 within the existing workflow scope without touching application code.

### Findings and arbitration dispositions (F1–F12, A1)

| Finding / Item | Description | Disposition | Verification & Implementation |
|---|---|---|---|
| **F1** | Provenance parsing failure swallowed via missing `import io` | Accepted | Verified `NameError` empirically. Added top-level `import io` in `generate_review_packet.py`; surfaced download/parse failures. Tested via unit tests and real CI run 37164136438. |
| **F2** | Monolithic network calls untestable offline | Accepted | Introduced injectable `GitHubClient` class with dedicated `fetch_json` and `download_bytes` methods. Validated with offline unit tests mocking all API interactions. |
| **F3** | Ambiguous PR associations across multiple PRs | Accepted | Declined ambiguity: `generate_review_packet.py` exits with status 1 if a workflow run maps to multiple PRs unless explicit `--pr-number` is supplied. Tested with ambiguity fixtures. |
| **F4** | Tested Base SHA bound to live PR branch instead of triggering event | Accepted | Extracted tested base SHA from triggering event payload (`workflow_run.pull_requests[].base.sha`). Live PR base SHA is reported separately; emits warning if base branch moved. |
| **F5** | Run attempt mismatch and job log attribution | Accepted | Queried attempt-specific jobs API (`/actions/runs/{run_id}/attempts/{attempt}/jobs`). Verified `run_id` and `run_attempt` inside `build-provenance.json` match triggering execution; mismatches marked incomplete. |
| **F6** | Azure Blob SAS redirect authentication failure & size limits | Accepted | Verified empirically that urllib forwarded Authorization header to Azure Blob Storage, triggering HTTP 401. Implemented host-aware redirect handler stripping Authorization when redirecting off-domain. Enforced 50 MB total / 10 MB per-file limits, canonical destination containment (Zip-Slip defense), and uncompressed size bounds. |
| **F7** | Legacy contract migration rollout gap | Accepted | Restored original meaning: Issues #5/#6 lack '### Allowed paths' and PRs #4/#9 lack required hygiene headings. Documented exact Issue and PR edits and on-demand rechecks. External edits remain coordinator actions. |
| **F8** | PR template HTML comments & naked numbers accepted | Accepted | Stripped HTML comments (`<!-- ... -->`) before regex parsing. Enforced `#<number>` format in `extract_primary_issue`, rejecting naked numbers and placeholder `Fixes #`. Tested against shipped templates. |
| **F9** | Loose opt-out synonyms rejected valid file paths | Accepted | Replaced substring search with exact whole-entry set membership for loose synonyms (`LOOSE_SYNONYMS = {"not yet constrained", "none", "any", "n/a", "open", "all", "tbd"}`). Valid paths containing words like `open` or `all` (e.g. `src/open/all.cs`) are preserved. |
| **F10** | Lack of manual/advisory recheck mechanism | Accepted | Added `workflow_dispatch` trigger with `pr_number` input to both `ai-scope-check.yml` and `ai-pr-hygiene.yml`, enabling on-demand verification alongside `pull_request.edited`. |
| **F11** | Direct workflow script interpolation injection risks | Accepted | Replaced inline GitHub Actions context interpolation (`"${{ github.ref }}"`) with environment variables (`$env:GITHUB_REF`, etc.) in `build-and-test.yml`. |
| **F12** | TRX outcome definitions, categorization, manifest completeness & input validation | Accepted | Standardized TRX result counting (effective failed: failed+error+timeout+aborted; skipped: notExecuted+notRunnable+inconclusive). Enforced regex validation on CLI inputs. Fixed file categorization (`src/` always production). Added full manifest diff and omitted patch flags. |
| **A1** | Mixed `Unconstrained` and explicit path declarations | Accepted | Enforced arbitration ruling: contracts with mixed `Unconstrained` and explicit paths are strictly rejected. Only a standalone `Unconstrained` entry is permitted as a contract opt-out. |

### Real CI run 37164136438 local execution evidence

The revised generator was executed locally using authenticated GitHub credentials against real Build and Test run `37164136438`:

- **Run Identity**: Run ID `37164136438`, Run Attempt `1`
- **Head SHA**: `0abef8d34a836494cdd882b844c781e32fa35322`
- **Tested Checkout SHA**: `faec613332c3a7d5fcee44fc8b257839d150dddf` (synthetic merge ref `refs/pull/19/merge`)
- **Tested Base SHA**: `e7b6e0640d9a22077fb515b1dfc2a277e987785e`
- **Live PR Base SHA**: `e7b6e0640d9a22077fb515b1dfc2a277e987785e` (Status: Current, base has not moved)
- **TRX Test Totals**: 67 passed, 0 failed, 0 skipped, 67 total (across 2 TRX files in `test-results` artifact)
- **Manifest Completeness**: 21 files changed, 21 diffs included in full diff artifact, 0 omitted patches
- **Security & Diagnostics**: Zero credentials, tokens, or signed URLs leaked in logs or packet markdown. SAS redirect successfully downloaded build-provenance artifact.

### Trust boundaries and enforcement clarification

GitHub Actions workflows execute as advisory status checks on pull requests. Workflows cannot enforce repository-level merge blocking on their own; blocking branch protection requires GitHub repository settings (Branch Protection Rules or Rulesets) configured with mandatory passing status checks by a repository administrator. The term "blocking check" in workflow descriptions refers to the check concluding with exit code 1 / failure status, not automated platform-level merge prevention.

### Complete open PR and legacy contract migration inventory

To ensure smooth operation when checks are configured as required:

1. **PR #4** (`pilot/companion-version-ordering`):
   - Head SHA: `944abc08c8722bdfc3b13bf7fda8fdd5a8a25b65`
   - Primary Task Contract: Issue #5 (`CompanionVersion.IsOutdatedComparedTo`)
   - Migration Action: Update PR #4 description via GitHub UI to add `## Primary Issue` with `Fixes #5`, along with `## Summary`, `## Scope`, `## Verification`, and `## Documentation`. Verify allowed paths in Issue #5 encompass all PR #4 modified files.
   - Recheck: Re-run `ai-pr-hygiene` and `ai-scope-check` using `workflow_dispatch` with PR number `4`.
2. **PR #9** (`issue-6-prevent-dx12-vulkan-deployment`):
   - Head SHA: `c4d0f846b4031b08e9e3444c803abe37cc171890`
   - Primary Task Contract: Issue #6 (`Prevent DXVK deployment for DX12/Vulkan`)
   - Migration Action: Update PR #9 description via GitHub UI to add `## Primary Issue` with `Fixes #6`, along with required hygiene headings.
   - Recheck: Re-run `ai-pr-hygiene` and `ai-scope-check` using `workflow_dispatch` with PR number `9`.

## Bounded repair exception checkpoint — Issue #11 (F4, F5, F7, F12) (2026-10-03)

### Context & bounded repair scope

Claude's verification pass on revised head `358f9154c2db784e4fead4af4767fd184e8c09a7` noted that four material requirements remained incompletely resolved: F4 (live base fallback), F5 (unmatched job fallback, missing provenance identity, unscoped TRX artifacts & duplicate selection), F7 (restoring original legacy contract migration meaning and documenting exact edits), and F12 (separating manifest completeness from patch availability and testing capped compare responses). The coordinator granted a bounded repair exception targeting strictly these four existing requirements.

### Bounded repair implementation & evidence

| Item | Requirement & Defect | Bounded Repair Implementation | Verification Evidence |
|---|---|---|---|
| **F4** | Missing event base previously fell back to live base and was labeled tested. Base disagreement was not flagged. | If event base is missing/empty, `tested_base_sha` is strictly marked `unknown`—never assigned `live_base_sha`. Source comparison is explicitly declined (`compare_commits` not called, diffs report declined status). Base SHA disagreements between event and provenance are detected and flagged. | `test_missing_event_base_declines_comparison_and_reports_unknown`, `test_base_provenance_disagreement_detected`. |
| **F5** | Unmatched job fallback, missing provenance identifiers, unscoped TRX artifacts and duplicate selection. | In `GitHubClient.get_workflow_run_jobs`, fallback filtering by attempt returns `[]` when no jobs match (unmatched jobs from other attempts are never returned). In `generate_packet`, `build-provenance.json` must contain `run_id`, `run_attempt`, and `head_sha`; missing identifiers cause checkout identity to be marked `incomplete`. Multiple TRX artifacts without unambiguous attempt window attribution are marked `UNAVAILABLE` with an ambiguous attribution diagnostic rather than silently taking the first entry. | `test_github_client_jobs_attempt_filtering_rejects_unmatched_jobs`, `test_missing_provenance_identifiers_marked_incomplete`, `test_duplicate_phase_a_artifacts_marked_unavailable`. |
| **F7** | Legacy contract migration rollout gap: Issues #5/#6 lack `### Allowed paths` section. | Restored original meaning of F7. Documented exact required metadata edits for Issue #5, PR #4, Issue #6, and PR #9, along with exact recheck commands. Preserved application requirements intact; external metadata edits remain coordinator actions. | Documented in `docs/AI-PILOT-LOG.md`, `docs/AI-DEVELOPMENT-WORKFLOW.md`, and PR #19 description. |
| **F12** | Conflated manifest completeness with patch availability; capped compare responses were falsely reported as complete. | Separated manifest completeness (`complete` vs `incomplete (capped)`) from patch availability (`all returned patches present` vs `partial`). Verified that 300 files returned with 300 patches is marked `incomplete`, not `complete`, when PR changed files exceed 300. | `test_capped_compare_response_with_all_patches_present_is_not_complete_manifest`. |

### Exact legacy contract migration instructions (Coordinator actions)

1. **Issue #5**: Coordinator adds `### Allowed paths` section:
   ```markdown
   ### Allowed paths

   - src/DXVKCompanion/Utils/CompanionVersion.cs
   - tests/DXVKCompanion.PhaseA.Tests/CompanionVersionTests.cs
   ```
2. **PR #4**: Coordinator adds `## Primary Issue` with `Fixes #5`, and `## Documentation` heading.
   - Recheck: `gh workflow run ai-pr-hygiene.yml -f pr_number=4` and `gh workflow run ai-scope-check.yml -f pr_number=4`.
3. **Issue #6**: Coordinator adds `### Allowed paths` section:
   ```markdown
   ### Allowed paths

   - src/DXVKCompanion/DXVK/DxvkCompatibility.cs
   - src/DXVKCompanion/DXVK/DxvkInstaller.cs
   - src/DXVKCompanion/DXVK/DxvkManager.cs
   - src/DXVKCompanion/Models/GraphicsApi.cs
   - src/DXVKCompanion/Monitoring/ApiClassifier.cs
   - src/DXVKCompanion/UI/GameDetailsWindow.cs
   - src/DXVKCompanion/UI/TrayApp.cs
   - tests/DXVKCompanion.PhaseA.Tests/DxvkModernApiCompatibilityTests.cs
   ```
4. **PR #9**: Coordinator adds `## Primary Issue` with `Fixes #6`.
   - Recheck: `gh workflow run ai-pr-hygiene.yml -f pr_number=9` and `gh workflow run ai-scope-check.yml -f pr_number=9`.

### Remaining uncertainties and escalation note

- This repair pass is bounded strictly to F4, F5, F7, and F12 per coordinator arbitration. No claim is made that every original finding has been independently verified across the repository.
- GitHub Actions workflows execute as advisory status checks; platform-level merge blocking requires explicit repository branch protection rulesets configured by administrators.
- If any material defect remains unresolved after this bounded repair exception, the matter will be escalated explicitly rather than initiating another open-ended revision loop.

## Centralized TRX artifact-attempt attribution and review allocation checkpoint — Issue #11 (2026-10-03)

### Context & coordinator execution check

Following commit `20b0ea81e3fbd5f02e4c39f0720741121ca2930a`, ChatGPT independently executed the 60 offline fixtures (all passed) and conducted two execution checks that reproduced an unresolved F5 defect in `generate_review_packet.py`:
1. **Reproducer 1**: Trigger attempt 2; `get_workflow_run_jobs` returns `[]`; singleton TRX artifact has an old created_at. Packet previously reported `PASSED`. Expected: `UNAVAILABLE`, because attempt identity cannot be established.
2. **Reproducer 2**: Trigger attempt 2; matching jobs start `2026-01-02T00:00:00Z` and complete `2026-01-02T00:10:00Z`; singleton TRX artifact created `2026-01-03T00:00:00Z`. Packet previously reported `PASSED`. Expected: `UNAVAILABLE`, because artifact lies outside the attempt interval.

Both reproducers were added to `tests/ai-workflow/test_review_packet.py` and confirmed failing on `20b0ea8` before implementing production fixes.

### Centralized attribution implementation

Attribution logic was removed from ad-hoc branches in `generate_packet` and centralized into `resolve_trx_artifact_for_attempt`:
- **Missing matching jobs**: If `ci_jobs` is empty, returns `UNAVAILABLE` (`no matching jobs found for attempt`).
- **Missing/invalid job timestamps**: If jobs lack `started_at` or `completed_at`, returns `UNAVAILABLE` (`cannot establish attempt execution interval`).
- **Missing artifact metadata**: If candidate artifacts lack `created_at` or have unparseable timestamps, returns `UNAVAILABLE`.
- **Interval containment**: Candidate artifact `created_at` must fall within `[interval_start, interval_end]`. Validates both lower and upper boundaries. Artifacts outside the interval return `UNAVAILABLE`.
- **Ambiguous candidates**: If multiple candidate artifacts fall within the attempt window, returns `UNAVAILABLE`.
- **No singleton or list order inference**: Attribution is never inferred from singleton count or list position. A singleton artifact outside the interval or without matching jobs fails closed as `UNAVAILABLE`.
- **Prior attempt disambiguation**: When artifacts from prior attempts exist alongside the current attempt's artifact, only the artifact within the current attempt's interval is selected.

### Limits of timestamp evidence

GitHub Actions artifact and job timestamps (`started_at`, `completed_at`, `created_at`) are server-assigned metadata that correlate an artifact's upload time with the execution window of a specific job run attempt. They establish chronological containment within execution windows, not cryptographic signatures or immutable content attestations. If API metadata cannot reliably prove attribution within the attempt window, evidence fails closed as `UNAVAILABLE` rather than guessing.

### Updated review allocation

Per user decision and coordinator execution check:
- **Gemini**: Primary implementation layer (repository code, unit tests, local verification, focused repairs).
- **ChatGPT**: Verification, architecture, reproduction analysis, and arbitration layer.
- **Claude**: Reserved for occasional independent audits and high-risk material decisions (due to quota scarcity).
- **Human**: Retains final merge authority and policy governance.

### Verification evidence

- **Local test suite**: 68/68 unit tests passing in `tests/ai-workflow/` (including both reproducers and 6 dedicated `resolve_trx_artifact_for_attempt` edge-case tests).
- **Live acquisition verification**: Executed `generate_review_packet.py` locally against real GitHub Actions runs `37169107191` and `37164136438`. Both runs successfully attributed the TRX artifacts within job execution intervals and extracted all 67 passing test results without leaking credentials or signed URLs.



