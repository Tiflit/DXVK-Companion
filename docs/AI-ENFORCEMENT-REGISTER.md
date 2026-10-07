# DXVK-Companion Deployment Enforcement Register

> **Adopted Kernel Version:** `v0.4` ([`docs/AI-WORKFLOW-KERNEL.md`](AI-WORKFLOW-KERNEL.md))  
> **Historical Inspected Baseline:** `bee62aa8aef47ab539c82a07a668919d736842a5` (immutable inspected baseline)  
> **Policy Revision Derivation:** Dynamically derived from the latest commit touching policy files on the acquired revision via:  
> `git log -1 --format=%H <acquired-revision> -- AGENTS.md docs/AI-WORKFLOW-KERNEL.md docs/AI-ENFORCEMENT-REGISTER.md docs/AI-DEVELOPMENT-WORKFLOW.md`  
> **Adoption Contract & Authority:** [Issue #63](https://github.com/Tiflit/DXVK-Companion/issues/63) (human approval recorded 2026-10-06 18:13 America/Toronto)  
> **Preset Profile:** Manual Starter Preset (Cooperative baseline; unattended automation disabled)

---

## 1. Project Roles, Control Channels & Invariants

- **Roles & Authority:**
  - **Human Principal / Owner:** Retains sole and final merge authority, policy governance, and explicit local-test authorization.
  - **Gemini / Antigravity:** Implementation layer (repository code, test suites, local verification, bounded repairs).
  - **ChatGPT:** Coordination, verification, review packets, arbitration.
  - **Claude:** Reserved for independent audits / high-risk policy questions (quota-conserving; not assigned for routine adoption).
- **Authorized Control Channels:**
  - Direct human-authored GitHub Issue and PR comments.
  - Verbatim coordinator transcriptions in GitHub task Issues (clearly marked as attributable, but not independently authenticated GitHub human identity due to shared-credential operation).
- **Stricter Project Invariants:**
  - **Human Final Merge Authority:** No unattended or agent-driven merges.
  - **Local Test Execution Authorization Gate:** Executing test suites, applications, or device tests on the host machine strictly requires explicit human authorization recorded in the task Issue ([`AGENTS.md`](../AGENTS.md)).
  - **No Unattended Automation:** Consequential actions remain human-reviewed and human-mediated.
  - **No Background Work Assigned:** No unbudgeted background research or active agent execution while idle.
  - **Precedence Rule:** Configuration and project rules remain subject to the adopted kernel (`v0.4`). Project rules add stricter restrictions. Incompatible requirements block dependent actions until an authorized-human resolution is recorded.

### Policy Revision Identity & Loading Route
- **Historical Inspected Baseline:** `bee62aa8aef47ab539c82a07a668919d736842a5` remains the immutable baseline inspected during initial desk exercise and adoption.
- **Dynamic Policy Revision:** To avoid self-referential commits inside the register, the current policy revision is determined dynamically from the latest commit touching applicable policy files on the verified acquired revision:
  ```bash
  git log -1 --format=%H <acquired-revision> -- AGENTS.md docs/AI-WORKFLOW-KERNEL.md docs/AI-ENFORCEMENT-REGISTER.md docs/AI-DEVELOPMENT-WORKFLOW.md
  ```
  The path set includes the kernel, register, `AGENTS.md`, and workflow guidance because all four define or constrain operating rules. Compare this hash against a previously trusted policy checkpoint (not unverified local HEAD). If the checkpoint is missing, changed, or divergent, refresh affected guidance before relying on changed authority.
- **Separate Checks:** Task activity, recent PRs, and external permission/credential changes remain separate checks; unchanged policy files do not prove unchanged runtime enforcement.
- **On-Demand Loading:** Sessions load the compact protected kernel (~800 words target) and mandatory project operating rules (`AGENTS.md`) at startup. Register entries below are retrieved on demand by guarantee ID or boundary relevant to proposed actions. New agents orient to roles, authority, and unresolved gaps; returning agents reuse reliable unchanged context. On-demand loading must never hide required authorization, provider/identity limitations, or stricter project rules.

---

## 2. Enforcement Register (Manual Starter Preset — DXVK Pilot)

No unattended automation is enabled by this preset. These declarations reflect cooperative operation and narrow tool checks, not completed adversarial enforcement tests. Initial probes require explicit human authorization; no probe is required merely to read, draft, or review documents.

| Boundary & Kernel IDs | State & Permitted Route | Mechanism, Gaps & Failure Response |
|---|---|---|
| **Authority and Identity**<br>`K1`, `K7` | **Cooperative**<br>Named human approves consequential actions through the configured channel. | *State:* Cooperative.<br>*Mechanism:* Attributed coordinator transcriptions and direct human comments.<br>*Gap:* Shared credentials/tokens cannot authenticate human-only decisions. Actions display repository owner identity. Use human-mediated execution. Agent consensus confers no authority.<br>*Failure response:* Block unapproved consequential actions; request explicit human decision. |
| **Task Scope & Host Execution**<br>`K1` | **Partially Enforced** (Scope CI)<br>**Cooperative** (Host execution)<br>Changes bounded to task contract `Allowed paths`. Host execution requires explicit human permission. | *State:* Scope check partially enforced; host execution cooperative.<br>*Mechanism:* `.github/workflows/ai-scope-check.yml` runs `evaluate_scope.py` on pull requests.<br>*Gap:* Failing scope CI detects violations on pull requests, but as of 2026-10-06 / 2026-10-07 baseline inspection, required-status-check merge protection is not configured on `main`; human review remains the actual integration barrier. Allowed paths do not constrain every command effect. Host isolation is unverified. All proposed adversarial probes are **NOT RUN**.<br>*Failure response:* Withhold unapproved actions; request bounded decision; preserve existing work. |
| **Disclosure & Privacy**<br>`K2`, `K5` | **Partially Enforced** (PR body helper)<br>**Cooperative** (Prompts/egress)<br>Approved provider/destinations only, minimized data, human-reviewed publication. | *State:* Publication partially enforced; prompt egress cooperative.<br>*Mechanism:* `scripts/ai-workflow/update_pr_body.py` performs fail-closed regex-based PII, secret, and absolute personal path scanning before preview/PATCH.<br>*Gap:* Scanning is incomplete. Online hosted model prompts are external disclosures; hosted provider data retention/processing policies remain uninspected. Uncertain disclosures remain blocked.<br>*Failure response:* Block uncertain agent-controlled transmission; report sanitized concern. |
| **Evidence & Records**<br>`K3`, `K4` | **Partially Enforced** (Review packets/helper)<br>**Cooperative** (Acceptance)<br>Readable, revision-bound acceptance and retained corrections. | *State:* Provenance binding partially enforced; acceptance cooperative.<br>*Mechanism:* `.github/workflows/ai-review-packet.yml` binds commit SHAs and Visual Studio TRX test totals. `update_pr_body.py` uses expected-hash checks and pre-write backups.<br>*Gap:* Check-then-write is not distributed locking. No claim of tamper resistance; human verifies consequential acceptance.<br>*Failure response:* Stop conflicting writes; preserve recovery evidence. Mismatched evidence blocks acceptance. |
| **Hold & Revocation**<br>`K6` | **Cooperative**<br>Human directs each agent. Human hold is global across all lanes unless explicitly scoped. | *State:* Cooperative.<br>*Mechanism:* Agents inspect task issues and comments before acting.<br>*Gap:* No instantaneous cross-agent interrupt mechanism. Agents report in-flight work and safe stop boundary. Only authorized human can lift hold.<br>*Failure response:* Report what stopped, what remains in flight, and residual effects. Do not promise instant cancellation. |
| **Execution & Cost**<br>`K1`, `K6` | **Cooperative**<br>No unapproved human-host tests/apps; bounded sessions and human-mediated consequential actions. | *State:* Cooperative.<br>*Mechanism:* Turn-by-turn agent invocation; worktree directory separation.<br>*Gap:* No hard spend ceiling or sandbox isolation claim without validated controls. Worktree isolation does not prove host isolation.<br>*Failure response:* Halt when limits reached; request bounded decision. |

---

## 3. Boundary Verification Status & Limitations

- **Adversarial Probes:** **NOT RUN**. No synthetic penetration or permission-bypass probes were executed against the live project.
- **Repository Protections & Scope Gate Limits:** Read-only branch inspection confirms required-status-check merge protection is off on `main`; failing scope CI reports PR failures but does not automatically block merges without human gatekeeping. Read-only API queries previously returned an empty repository ruleset inventory, but this does not establish the absence of inherited organization controls or legacy branch settings.
- **Hosted Providers:** External hosted model providers (Google/Gemini, OpenAI/ChatGPT, Anthropic/Claude) process submitted context. Transmitting proprietary or sensitive data to hosted providers constitutes external transmission.
- **Permitted Operation:** Under this cooperative/partially-enforced baseline, all consequential actions (merges, releases, host test executions) remain strictly human-mediated.
