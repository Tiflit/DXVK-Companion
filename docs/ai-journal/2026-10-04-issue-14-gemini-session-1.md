# Session Record: 2026-10-04 — Issue #14 Installation-Wide API Compatibility in Shared Directories

- **Date / Timestamp**: 2026-10-05 00:15:00 UTC
- **Agent Role & Model**: Gemini (Implementer)
- **Task / Issue**: Issue #14 — [AI] F5: Decide installation-wide vs per-exe API compatibility in shared directories
- **Starting Head**: `3a0fbcffaad0c0c4ef5a9d2bc4916c77e19ead38` (`origin/main`)
- **Branch / Worktree**: `fix/issue-14-shared-directory-compatibility` (`D:\dev\DXVK-Companion-issue-14`)

### Purpose & Startup Recovery
Initiated session from freshly fetched `origin/main` following PR #34 merge. Proceeded under explicit human approval recorded in the Decision Governance Block of Issue #14 approving Option A (installation-wide refusal). Scope strictly bounded to Issue #14: enforce installation-wide refusal when target executable or any recorded sibling executable in `GameInstallation` is outside the `DX9`/`DX10`/`DX11` allowlist (`DX12`, `Vulkan`, `ModernAPI`, `Unknown`, or undefined enum value); preserve Restore/RestoreAll availability; refuse strictly prior to writes/backups/intent-creation; surface user-visible refusal reasons (`LastRefusalReason`). Issues #15 and #16 deferred to separate follow-on tasks.

### Actions & Implementation
1. **Canonical Specification**:
   - Added §5.2.1 to `docs/spec/DXVK-COMPANION-SPEC.md` documenting the approved shared-directory deployment policy, supported allowlist, installation-wide refusal, user-visible refusal reasons, pre-execution guard, Restore availability invariant, and observation boundaries.
2. **Tests-First Suite**:
   - Implemented `tests/DXVKCompanion.PhaseA.Tests/SharedDirectoryCompatibilityTests.cs` (12 test methods) covering:
     - Installation-wide refusal with DX11 target and DX12 sibling, Vulkan sibling, recorded Unknown sibling, ModernAPI sibling, and undefined enum value sibling.
     - User-visible refusal reason asserting identification of blocking executable and API.
     - Positive controls (clean DX11-only, mixed DX9 + DX11 siblings, and DX12 sibling in a separate installation).
     - Refusal occurs strictly prior to file writes, backups, or pending action creation.
     - Refusal of `AdoptExisting` and `Reapply` when sibling is incompatible.
     - Invariant verification: `DisableDxvkAsync` (Restore) succeeds and restores baseline even when sibling is incompatible.
   - Initial test execution against unmodified codebase confirmed expected gap (8 failures, 4 passes).
3. **Core Engine & Models**:
   - Added `IsInstallationSupported(GameInstallation? installation, GraphicsApi? targetApi, string? targetExeName, out string? refusalReason)` to `DxvkCompatibility.cs`.
   - Added `LastRefusalReason` string property to `GameInstallation.cs`, `DxvkInstaller.cs`, and `DxvkManager.cs`.
4. **Integration Wiring**:
   - Updated `DxvkInstaller.cs`: wired `IsInstallationSupported` into `ApplyToGameAsync`, `ReapplyAsync`, and `AdoptExisting`. Set and persisted `LastRefusalReason` on refusal, cleared on success.
   - Updated `DxvkManager.cs`: wired `IsInstallationSupported` into `UpdateAvailable`, `QueueOrApplyAsync` (before `isRunning` check to prevent pending intent creation), `QueueOrApplyReapplyAsync`, `ApplyPendingAsync`, `ProcessAllPendingActionsAsync`, `EnableDxvkAsync`, `AdoptExistingAsync`, and `ReapplyAsync`.
   - Ensured `DisableDxvkAsync` and `RestoreAllAsync` remain completely ungated.
5. **Executed Verification**:
   - Application tests: 230/230 passed (all 12 new tests passing, 0 failures).
   - Workflow tests: 130/130 passed.

### Observable Measurements
- Human decision/action interventions: 0
- Human status queries: 0
- Repeated investigations from missing context: 0
- Session blocked: No
- Journal word count: ~380 words

### Outcome & Next Steps
- Result: Issue #14 implementation complete; all 230 application tests and 130 workflow tests passing.
- Next Action & Owner: Commit changes, push branch, open PR referencing Issue #14, and emit generated handoff snapshot for ChatGPT verification.
