# AI Engineering Journal: Include Actively Managed Attention-State Games in Restore All

**Date**: 2026-10-16  
**Session**: Gemini Session 1  
**Primary Issue**: [#137](https://github.com/Tiflit/DXVK-Companion/issues/137)  
**Base Revision**: `7fb601ab558c9ca6c6cc040f808192748c96704b` (`origin/main`)  

---

### 1. Objective & Defect Context

Fix `RestoreAllAsync` so that actively managed games whose installation state is `AttentionRequired` are included in `TotalManaged` and processed for restoration even when their profile flag `DxvkEnabled` is `false`:
- Previously, `RestoreAllAsync` checked:
  `bool isManaged = profile.DxvkEnabled || (installation != null && installation.RestorationState == RestorationState.Managed);`
- After inspection or file mismatches marked an active installation `AttentionRequired`, a disabled profile (stale or reconstructed by Manage Games) resulted in the game being skipped and counted as `AlreadyRestored` while `ManagedDxvkVersion` still recorded active management.
- The fix updates `RestoreAllAsync` to recognize `AttentionRequired` installations with a non-blank `ManagedDxvkVersion`, routes eligible installations through ownership-validated restoration, and prevents unverified legacy `.bak` restoration when ownership evidence is absent or unusable.

---

### 2. Implementation

1. **`src/DXVKCompanion/DXVK/DxvkManager.cs`**:
   - Updated profile selection in `RestoreAllAsync`:
     ```csharp
     bool isManaged = profile.DxvkEnabled
         || (installation != null && (
             installation.RestorationState == RestorationState.Managed
             || (installation.RestorationState == RestorationState.AttentionRequired && !string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion))));
     ```
   - In `DisableDxvkAsync`:
     - Added guard for actively managed installations (`!string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion) || installation.RestorationState == RestorationState.Managed`): if `installation.ManagedFiles` is empty or null, refuse safely rather than executing the unverified legacy `.bak` fallback.
     - Preserved `LastRefusalReason` from `_rollback.LastRefusalReason` on failed rollback.
     - Cleared refusal state (`LastRefusalReason = null;`) at entry of `DisableDxvkAsync` and unconditionally assigned `LastRefusalReason = _rollback.LastRefusalReason;` on rollback failure so reasonless failures cannot retain or leak an earlier game's refusal reason.
   - In `RestoreAllAsync`:
     - Added `LastRefusalReason` propagation into `summary.Messages` when available.

2. **`tests/DXVKCompanion.PhaseA.Tests/RestoreAllAttentionTests.cs`**:
   - Added dedicated headless regression suite covering all acceptance criteria:
     1. `RestoreAll_DisabledProfile_AttentionRequiredWithActiveVersion_RestoresSuccessfully`: disabled profile with active attention version counted in `TotalManaged` and restored.
     2. `RestoreAll_DisabledProfile_AttentionRequiredWithActiveVersion_ReplacedTarget_RefusesSafely`: tampered target DLL refuses safely, preserves bytes and backups.
     3. `RestoreAll_DisabledProfile_AttentionRequiredWithActiveVersion_MissingTarget_RefusesSafely`: missing target DLL refuses safely, retains attention and active version.
     4. `RestoreAll_DisabledProfile_AttentionRequiredWithActiveVersion_MissingOwnershipEvidence_RefusesWithoutMutation`: empty `ManagedFiles` refuses safely without file mutation or legacy `.bak` fallback.
     5. `RestoreAll_PersistedAndReloaded_DisabledProfileWithActiveAttention_RestoresSuccessfully`: full round-trip disk persistence and reload verification.
     6. `RestoreAll_DisabledProfile_RestoredOrNativeWithoutActiveVersion_CountedAsAlreadyRestored`: native and restored installations without active version remain `AlreadyRestored`.
     7. `RestoreAll_MultipleGames_IsolationBetweenActiveAttentionAndOtherStates`: per-entry error isolation across multiple games in mixed states.
     8. `RestoreAll_SequentialFailures_SecondReasonlessFailure_DoesNotInheritFirstRefusalReason`: sequential regression verifying that a second reasonless failure clears `LastRefusalReason` and does not inherit the preceding game's refusal reason.

3. **`tests/DXVKCompanion.PhaseA.Tests/AutomatedModeAndRestoreAllTests.cs`**:
   - Added `RestoreAllAsync_DisabledProfile_WithActiveAttentionInstallation_IsIncludedAndRestored` to verify multi-game RestoreAll with disabled profile and active attention installation.

---

### 3. Verification & Governance

- Local host execution: `NOT RUN (pending human authorization)` under AGENTS.md.
- Verified via GitHub Actions Cloud CI on Windows runners (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`, `Run Python workflow tests`).
