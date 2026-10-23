# Reusable maintenance profiles — revision 1

## Adoption and common boundaries

Prepared under [Issue #270](https://github.com/Tiflit/DXVK-Companion/issues/270), following the owner's approval of a small delegation amendment. **Candidate until human adoption.** This preparation is not a test/helper run approval. The human may adopt this exact revision by explicitly naming both profile IDs, or by personally merging the amendment PR whose description explicitly states that merge adopts these profiles. A coordinator verifies the human action and exact merged content, then records the adoption source/revision in Conversation. An agent-written status, unrelated merge or green CI is insufficient. Material profile changes need another human approval. This is cooperative authorization with shared credentials, not independently authenticated or technically enforced permission.

Profiles apply only to DXVK-Companion on the owner's existing Windows development laptop, ordinary non-administrator execution, an assigned task worktree under the designated development root, and a private coordination/evidence directory outside all Git worktrees. Publish aliases `<PROJECT_WORKTREE>` and `<PRIVATE_COORDINATION>`, never personal paths. Each task links the applicable ID/revision and verified adoption source. Valid until the owner revokes it or effects/host/commands change; headless readiness additionally expires with its session. Do not use either profile for this amendment's own preparation/review.

Before reuse, inspect relevant command inputs, helper imports, selected tests and their exercised code, project/build inputs/hooks and dependency declarations against the reviewed adoption revision. Reuse permits task code changes only when their effects stay inside this profile. Unexpected effects, authentication prompts, changed tools or uncertain inputs block affected execution; use existing cloud CI or request a bounded amendment. Source inspection is a cooperative control, not host isolation. No install/upgrade, privilege or credential/settings change, arbitrary command, new destination, personal/game/settings/save access, game discovery, GUI/product/fixture executable, Vulkan/device/GPU workload, paid service, agent dispatch, release, force termination or destructive cleanup is authorized. Existing holds and kernel guarantees prevail. Do not weaken assertions or retry unchanged green work. Never publish raw logs, inventory, backup contents or credentials.

## dxvk-metadata-v1

**Purpose/readiness:** Codex may maintain factual descriptions for the assigned task's PRs and append activity records to its Issues using the existing guarded helper. Gemini hands proposed body text to Codex. No laptop-availability acknowledgment is needed for this small maintenance operation during an active owner-requested task. This does not approve changing assignments, approvals or protected decisions through metadata. New Issue/PR creation and ordinary Conversation comments use existing task authority; do not use this profile as a publication/privacy bypass.

**Commands:** From `<PROJECT_WORKTREE>`, use an already installed working Python 3 interpreter and existing GitHub CLI authentication. `Get-Command python, gh` and `python --version` are permitted prerequisite checks; a Store alias or missing runtime stops execution without installation. Commands below permit only substitution of positive assigned target IDs, reviewed UTF-8 input-file paths under `<PRIVATE_COORDINATION>`, a 64-hex acquired remote-body SHA-256 and the task backup directory there. Require the expected hash for PR writes as well as Issue writes.

```powershell
python scripts/ai-workflow/update_pr_body.py --repo Tiflit/DXVK-Companion --pr <PR_ID> --body-file <INPUT_FILE> --expected-base-hash <SHA256> --backup-dir <BACKUP_DIR>
python scripts/ai-workflow/update_pr_body.py --repo Tiflit/DXVK-Companion --pr <PR_ID> --body-file <INPUT_FILE> --expected-base-hash <SHA256> --backup-dir <BACKUP_DIR> --write
python scripts/ai-workflow/update_pr_body.py --repo Tiflit/DXVK-Companion --issue <ISSUE_ID> --append-file <INPUT_FILE> --expected-base-hash <SHA256> --backup-dir <BACKUP_DIR>
python scripts/ai-workflow/update_pr_body.py --repo Tiflit/DXVK-Companion --issue <ISSUE_ID> --append-file <INPUT_FILE> --expected-base-hash <SHA256> --backup-dir <BACKUP_DIR> --write
```

Acquire the current body through the approved GitHub connector/API, retain its exact UTF-8 text and compute its SHA-256 with standard PowerShell/.NET hashing. Prepare and inspect the candidate, preview, inspect the sanitized diff, then write and read back. All targets remain the assigned repository/task. No `--token`, `--adopt-unmarked`, inline secret, direct replacement client or Issue-body replacement. Helper use of existing authentication (including its internal `gh auth token` child) is permitted solely in memory; do not print, persist or publish tokens. Allowed network is the existing GitHub API for this repository. Helper imports, Python bytecode/temp files, private candidate/output records and external recovery backups are allowed writes; backup directories must be outside every Git worktree. Retain all backups/evidence.

**Budget/stopping:** One helper process at a time, up to 2 minutes per invocation and 10 minutes per correction batch; small CPU/memory/disk use expected, no enforced quota claim. Use normal completion or ordinary Ctrl+C. On stale baseline, reacquire and reconcile before a new preview; never blindly repeat a write. On uncertain write/readback, stop writes and inspect remote/backup state; no automatic rollback. A changed factual candidate may be repaired within the same bounds. No cleanup or forceful termination; unresolved in-flight work requires human help. Existing hosted hygiene checks may revalidate the corrected metadata; do not rerun unrelated green tests.

## dxvk-headless-v1

This concretizes the earlier pending [Issue #100 proposal](https://github.com/Tiflit/DXVK-Companion/issues/100); that historical proposal is not itself approval. The initial test selection is deliberately limited to three inspected classes that exercise version strings and in-memory policy/configuration models. Broader synthetic integration, process/lease and workflow suites remain in already-approved hosted CI. Adding a local suite needs explicit profile amendment; this is not an unfiltered test permission.

**Readiness:** Announce this profile and task before the first local command of a development session. One human availability acknowledgment covers matching runs and repairs throughout that continuous session; no per-command permission request. Withdrawal, a hold or session end invalidates readiness. A new session needs availability, not reapproval of unchanged commands. No background run while idle.

**Prerequisites/commands:** Existing .NET 8 SDK required. `Get-Command dotnet` and `dotnet --list-sdks` are permitted after readiness; report versions only. Run sequentially from `<PROJECT_WORKTREE>`:

```powershell
dotnet restore src/DXVKCompanion/DXVKCompanion.csproj
dotnet restore tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj
dotnet build src/DXVKCompanion/DXVKCompanion.csproj --configuration Release --no-restore
dotnet build tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj --configuration Release --no-restore
dotnet test tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~CompanionVersionTests|FullyQualifiedName~GameDetailsPolicySelectionTests|FullyQualifiedName~GameDetailsConfigurationSelectionTests" --logger "trx;LogFileName=headless-profile.trx"
```

**Effects/budget:** Standard SDK/compiler/MSBuild/testhost children, project bin/obj/TestResults, ordinary .NET/NuGet cache/temp writes and existing configured package sources for declared dependencies are covered. SDK-default audit/telemetry may occur; this is not zero external writes/network. Do not inspect/change private cache/config or answer new authentication prompts. No dependency upgrades, publish or product launch. One command/local agent job at a time, 15 minutes per command and 45 minutes per batch; ordinary build CPU/memory/disk activity is expected, without enforced resource/isolation claims. No concurrent local agent test batches. Select commands relevant to the change; do not run the three classes solely for documentation. Build compiles the whole test project but the filter limits execution; inspect test assembly initialization/discovery and build hooks before reuse.

**Stopping/retention:** Normal completion or ordinary Ctrl+C; no kill/force termination. If cancellation cannot be confirmed, report in-flight work and request human help. Retain build outputs, TRX and private evidence. No manual cache/worktree/temp cleanup or raw-log upload. Record exact revision, commands/outcomes, limitations and any interruption in the task's existing journal/completion record. A successful filtered run does not replace broader required hosted coverage or certify game, GUI, device or hardware behavior.
