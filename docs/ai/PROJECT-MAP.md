# Project map

Navigation only, not a specification or implementation-completeness claim. Start with [AGENTS.md](../../AGENTS.md), the assigned Issue and latest relevant evidence. Read applicable cross-cutting guarantees and binding Issue clarifications before editing a module. Links do not replace those requirements; reading every row is unnecessary.

## Product navigation

Source paths below are relative to `src/DXVKCompanion/`; test names are under [Phase A tests](../../tests/DXVKCompanion.PhaseA.Tests/). Match the concrete task to relevant tests rather than running or loading everything automatically.

| Concern | Source entry points | Specification / targeted tests |
|---|---|---|
| Observe, qualify and classify processes | [Monitoring/](../../src/DXVKCompanion/Monitoring/): ProcessMonitor, ProcessQualification, GameDetector, ApiClassifier, ModuleScanner, ProcessExitHandler | Canonical spec §§6–9; PhaseBDetection, ProcessQualification, ProcessMonitorQualification, QueuedActionApiReassessment, ProcessExitHandlerLifecycle |
| Identity, persistence and schema compatibility | [Models/](../../src/DXVKCompanion/Models/), [Storage/](../../src/DXVKCompanion/Storage/): GameLibraryStore, ProfileStore, GameLibrarySchemaCompatibility, ManagedFileInspector | §§5, 14–18, 40.1, 45; GameLibraryStorePersistence, GameLibrarySchemaCompatibility, OwnershipPersistenceFailure, ProfileStorePersistence, ManagedFileInspector* |
| Deployment, adoption, pending actions and restore | [DXVK/](../../src/DXVKCompanion/DXVK/): DxvkManager, DxvkInstaller, DxvkRollback, ExistingDxvkDetector, DxvkCompatibility | §§5.2, 10, 13–24, 27–29, 31; PerGameVersionSelection, PersistedDeploymentLifecycleAcceptance, UnownedRestoreRefusal, Restore* and OperationExclusionBoundary |
| Transactions, operation exclusion and process evidence | [Safety/](../../src/DXVKCompanion/Safety/): MultiFileTransactionEngine, OperationLeaseCoordinator, ProcessActivityInspector, FileIdentity | §§17–23 and the safety supplement; MultiFileTransactionEngine, FailedOperationRecovery, OperationLeaseCoordinator, ProcessActivityBoundary |
| Official release metadata and local acquisition | [DXVK/](../../src/DXVKCompanion/DXVK/): DxvkGithubClient, DxvkReleaseAcquirer, DxvkLocalReleaseLibrary, DxvkReleaseCache, DxvkReleaseCheckScheduler | §§11–12, 26, 30, 43; DxvkGithubClient, DxvkReleaseAcquisition, DxvkLocalReleaseLibrary, DailyReleaseCheck, FirstRunQueuedEnable |
| Settings, configuration and presentation | [UI/](../../src/DXVKCompanion/UI/), DxvkConfigManager, [Program.cs](../../src/DXVKCompanion/Program.cs), [Utils/](../../src/DXVKCompanion/Utils/) | §§3–4, 25, 32–40; GameDetails*Presentation/Selection, DxvkConfigManager, SettingsAndStartup, PhaseEUIIntegration |

Canonical authority: [DXVK-COMPANION-SPEC.md](../spec/DXVK-COMPANION-SPEC.md). Cross-cutting [safety supplement](../spec/DXVK-Companion-PhaseA5-Safety-and-Identity-Design-FINAL.md) covers ownership, baselines, transactions and recovery; it is not optional just because a module row cites another section. See the [decision index](DECISION-INDEX.md) for clarifications and remaining evidence limits.

## Workflow navigation

[scripts/ai-workflow/](../../scripts/ai-workflow/) contains `parse_contract.py` / `evaluate_scope.py` (task scope), `privacy_guard.py` / `publication_preflight.py` (bounded publication checks), `update_pr_body.py` (guarded metadata updates), `parse_trx.py` / `generate_review_packet.py` / `generate_handoff.py` (revision-bound evidence), and `update_dashboard.py` (machine snapshot). Regression fixtures are in [tests/ai-workflow/](../../tests/ai-workflow/). Read the relevant tool and tests; this map does not authorize executing a scanner or bootstrap script.

## Existing delivery procedure

The [workflow](../AI-DEVELOPMENT-WORKFLOW.md) and [activity policy](../AI-ACTIVITY-JOURNAL.md) remain controlling. These relocated implementation/review instructions retain the former orientation's procedure:

1. Check status, fetch main and work in an isolated worktree; preserve dirty/unique work. Derive actual policy/head/base separately from remote API facts. Use portable immutable evidence links, not personal paths or transcripts.
2. For a defect, write failing regression fixtures first. Run task-relevant verification only within applicable authorization. Application suite: `dotnet test tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj`; workflow suite: `python -m unittest discover -s tests/ai-workflow -v`. These are references, not host execution approval; cloud and local outcomes are distinct.
3. Submit with `Primary Issue`, `Summary`, `Scope`, `Verification`, `Documentation` headings and one initial durable snapshot from `generate_handoff.py`. Inspect status at submission/completion/blocker boundaries without busy polling. If checks are pending at submission, report status and yield honestly without polling loops; do not regenerate snapshots solely to refresh them. In-scope repairs continue autonomously; the final matching-revision coordinator comment can complete the evidence.
4. Record the factual session in its task-allowed journal path; do not expand allowlists. The reviewer inspects PR/review packet (`review_packet.md`, `full-diff.diff`), actual checkout/run/artifact provenance, TRX totals and boundary compliance. Group blocking fixes into one bounded repair cycle and distinguish nonblocking follow-ups. A separately engaged auditor reads relevant durable findings and states whether evidence was directly inspected or accepted as platform metadata.

Human activation, local readiness, guarded publication and final merge requirements are unchanged. No automatic agent dispatch or command execution follows from this page.
