# AI Engineering Journal: Restore-Target Ownership Verification

**Date**: 2026-10-15 (updated 2026-10-16)  
**Session**: Gemini Session 1 & Focused Repairs (Merged with main)  
**Primary Issue**: [#127](https://github.com/Tiflit/DXVK-Companion/issues/127)  
**Pull Request**: [#128](https://github.com/Tiflit/DXVK-Companion/pull/128)  
**Base Revision**: `1f90221da09dc842898563c7b763750c5ea7d87e` (`origin/main`)  

---

### 1. Objective & Defect Context

Investigate and implement a focused repair for restore-target ownership verification:
- `DxvkRollback` previously captured a target's current identity from disk via `FileIdentity.Capture(targetPath)` and supplied it as the expected target identity.
- This allowed externally replaced or injected files in the game directory to be treated as authorized for deletion (if recorded as originally absent) or overwrite (if recorded as originally existing).
- The ownership reference must instead derive strictly from recorded management evidence (`ManagedFileRecord.ExpectedManagedSha256`), respecting the last committed managed identity.

**Required behavior**:
- Verify actively managed targets against their recorded managed hashes (`ExpectedManagedSha256`) before authorizing deletion or overwrite.
- Do not use freshly observed target hashes to establish ownership.
- Missing/malformed managed-hash evidence, mismatched contents, or a missing target in an actively managed installation must produce safe refusal.
- Validate the entire restore set before modifying any game file. Preserve the original-backup checks introduced by PR #126.
- On refusal, preserve all game files and backups, retain management/version information, and persist an understandable attention reason (`RestorationState.AttentionRequired`, `LastRefusalReason`).
- Unknown original state must not authorize destructive restoration or be silently reported as restored.
- Preserve successful ordinary restoration and safe repeat/no-op behavior for correctly restored installations. Use appropriate original-baseline evidence for already-restored files; do not confuse them with actively managed targets.
- Minimal in-memory request-field changes are allowed; zero persisted schema changes.

---

### 2. Implementation & Focused Repairs

1. **`src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs`**:
   - Added `public string? ExpectedManagedSha256 { get; init; }` to `MultiFileTransactionFile` (in-memory request field; zero persisted schema change).
   - In `Execute` for `TransactionOperation.Restore`:
     - Closed the null-hash bypass: unconditional validation on every restore file. Every mutating engine restore must require valid recorded managed-hash evidence (`!IsValidSha256(file.ExpectedManagedSha256)` returns `SafeFailure`).
     - Validates that target exists on disk; returns `SafeFailure` if missing.
     - Validates that target SHA-256 on disk matches `file.ExpectedManagedSha256`; returns `SafeFailure` if mismatched.
     - Preserves existing pre-apply original-backup validation for `OriginalFileState.Existing`.

2. **`src/DXVKCompanion/DXVK/DxvkRollback.cs`**:
   - Refactored `RestoreOriginalDllsAsync` to handle both actively managed and already-restored installations while preserving lifecycle through attention states:
     - **Already-Restored / Resolved Candidates (`installation.RestorationState == RestorationState.Restored || string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion)`)**:
       - Evaluates against verified original-baseline evidence (does not trust current bytes or assume attention means DXVK is deployed):
         - Unknown original state: safe refusal.
         - Originally missing files must remain absent on disk; unexpected files cause safe refusal.
         - Originally existing files must be present on disk with SHA-256 matching `mf.OriginalSha256`; missing files or hash mismatches cause safe refusal.
       - If verified clean, transitions state to `RestorationState.Restored`, clears `LastRefusalReason = null`, saves store, and returns `true` as a verified safe no-op.
     - **Actively Managed Installations (`!string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion)`)**:
       - Validates every managed file before building the transaction request:
         - `mf.OriginalState == FileOriginalState.Unknown`: safe refusal (`RefuseRollback`).
         - Missing or malformed `mf.ExpectedManagedSha256`: safe refusal.
         - Missing target file on disk: safe refusal.
         - Target content hash mismatch against `mf.ExpectedManagedSha256`: safe refusal.
         - Missing or malformed `mf.OriginalSha256` for `FileOriginalState.Existing`: safe refusal.
       - Carries `ExpectedManagedSha256 = mf.ExpectedManagedSha256`, `ExpectedTargetIdentity = currentTargetIdentity`, and `ExpectedOriginalSha256 = mf.OriginalSha256` into `MultiFileTransactionFile`.
       - On transaction failure or refusal: sets `RestorationState.AttentionRequired`, records `LastRefusalReason`, saves to `_gameLibraryStore`, retains `ManagedDxvkVersion`, and preserves all game files and backups.
   - Added private helper `RefuseRollback(GameInstallation, string)` for consistent logging, state updates, reason persistence, and refusal.

3. **Engine Fixtures & Regression Updates (`tests/DXVKCompanion.PhaseA.Tests/MultiFileTransactionEngineTests.cs`)**:
   - Updated `RestoreFile` helper and `Restore_MultiFile_InvalidLaterBackup_RefusesBeforeAnyApply_PreservesGameFilesAndBackups` to supply known managed content hash in `ExpectedManagedSha256`.
   - Added direct regression `Restore_NullManagedHashEvidence_RefusesSafely_CannotAuthorizeDeletionOrOverwrite` proving null evidence cannot authorize deletion or overwrite.

4. **Existing Fixture Updates (`tests/DXVKCompanion.PhaseA.Tests/`)**:
   - `RestoreBackupIdentityVerificationTests.cs`: Populated `ExpectedManagedSha256` on manually created `ManagedFileRecord` instances in multi-file test so actively managed target check succeeds and isolates the tested backup failure.
   - `AutomatedModeAndRestoreAllTests.cs`: Populated `ExpectedManagedSha256` on `inst1.ManagedFiles` fixture.
   - `SystemIntegrityAndEdgeCaseTests.cs`: Populated `ExpectedManagedSha256` on `SingleGame` fixture.
   - `SharedDirectoryCompatibilityTests.cs`: Populated `ExpectedManagedSha256` on `managedRecord` fixture.

5. **Test Suite (`tests/DXVKCompanion.PhaseA.Tests/RestoreTargetOwnershipVerificationTests.cs`)**:
   - Added tests covering all acceptance criteria and repair findings:
     - `Restore_InjectedDllOriginallyAbsent_ExternallyReplaced_RefusesAndSurvivesRestore`: Externally replaced file originally absent is not deleted.
     - `Restore_DllOriginallyPresent_ExternallyReplaced_RefusesAndNotOverwrittenEvenWithValidOriginalBackup`: Externally replaced file originally existing is not overwritten with backup.
     - `Restore_MultiFile_LaterTargetMismatch_PreventsEarlierValidFilesFromMutation`: Multi-file ordering where later target mismatch protects earlier valid file from deletion.
     - `Restore_MalformedOrMissingManagedHash_RefusesSafely`: Null, empty, short, non-hex 64 strings safely refuse.
     - `Restore_MissingActivelyManagedTarget_RefusesSafely`: Missing target on disk safely refuses.
     - `Restore_UnknownOriginalState_RefusesSafely_NotSilentlyReportedAsRestored`: Unknown original state is not skipped or marked restored.
     - `Restore_OrdinaryRestoration_AndVerifiedRepeatRestoration_Succeeds`: Full lifecycle test: restore successfully → introduce unexpected file → refuse → remove unexpected file in synthetic fixture → verify safe no-op restoration succeeds and clears resolved attention reason.
     - `Restore_Persistence_RefusalAndVersionSurviveStoreReload`: Refusal reason, attention state, and version survive fresh `GameLibraryStore` reload.
     - `Restore_ManagerBoundary_FailedDisable_DoesNotClearProfileDxvkEnabled`: Fixed compilation via `EnableDxvkAsync(profile, "2.5")` with isolated cache path; verified persisted profile bytes before/after failed disable, and reloaded through new `ProfileStore`.
     - `Restore_MultiFileTransactionEngine_ExpectedManagedSha256_EnforcesTargetOwnershipDirectly`: Direct engine execution validates `ExpectedManagedSha256` before file modification.
     - `Restore_MultiFileTransactionEngine_NullManagedHashEvidence_RefusesSafely`: Direct engine execution verifies null evidence fails safely.

---

### 3. Verification

- **Local Execution**: `NOT RUN (pending human authorization)` per repository execution policies.
- **Remote Integration**: Cleanly merged current `origin/main` (`1f90221da09dc842898563c7b763750c5ea7d87e`). No conflict with upstream config management changes (`DxvkConfigManager`).
- **Verification Plan**: Authorized Cloud CI via GitHub Actions on PR submission.
