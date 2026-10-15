# AI Engineering Journal: Restore-Target Ownership Verification

**Date**: 2026-10-15  
**Session**: Gemini Session 1  
**Primary Issue**: [#127](https://github.com/Tiflit/DXVK-Companion/issues/127)  
**Base Revision**: `288c3f301369bf8bcbeaa274f18e02b4fa55663b` (`origin/main`)  

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

### 2. Implementation Changes

1. **`src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs`**:
   - Added `public string? ExpectedManagedSha256 { get; init; }` to `MultiFileTransactionFile` (in-memory request field; zero persisted schema change).
   - In `Execute` for `TransactionOperation.Restore`:
     - If `file.ExpectedManagedSha256` is provided:
       - Validates format via `IsValidSha256`; returns `SafeFailure` if missing or malformed.
       - Validates that target exists on disk; returns `SafeFailure` if missing.
       - Validates that target SHA-256 on disk matches `file.ExpectedManagedSha256`; returns `SafeFailure` if mismatched.
     - Preserves existing pre-apply original-backup validation for `OriginalFileState.Existing`.

2. **`src/DXVKCompanion/DXVK/DxvkRollback.cs`**:
   - Refactored `RestoreOriginalDllsAsync` to handle both actively managed and already-restored installations:
     - **Already-Restored Installations (`installation.RestorationState == RestorationState.Restored`)**:
       - Evaluates against original-baseline evidence:
         - Unknown original state: safe refusal.
         - Originally missing files must remain absent on disk; unexpected files cause safe refusal.
         - Originally existing files must be present on disk with SHA-256 matching `mf.OriginalSha256`; missing files or hash mismatches cause safe refusal.
       - If verified clean, returns `true` as a safe verified no-op.
     - **Actively Managed Installations (`installation.RestorationState != RestorationState.Restored`)**:
       - Validates every managed file before building the transaction request:
         - `mf.OriginalState == FileOriginalState.Unknown`: safe refusal (`RefuseRollback`).
         - Missing or malformed `mf.ExpectedManagedSha256`: safe refusal.
         - Missing target file on disk: safe refusal.
         - Target content hash mismatch against `mf.ExpectedManagedSha256`: safe refusal.
         - Missing or malformed `mf.OriginalSha256` for `FileOriginalState.Existing`: safe refusal.
       - Carries `ExpectedManagedSha256 = mf.ExpectedManagedSha256`, `ExpectedTargetIdentity = currentTargetIdentity`, and `ExpectedOriginalSha256 = mf.OriginalSha256` into `MultiFileTransactionFile`.
       - On transaction failure or refusal: sets `RestorationState.AttentionRequired`, records `LastRefusalReason`, saves to `_gameLibraryStore`, retains `ManagedDxvkVersion`, and preserves all game files and backups.
   - Added private helper `RefuseRollback(GameInstallation, string)` for consistent logging, state updates, reason persistence, and refusal.

3. **Existing Fixture Updates (`tests/DXVKCompanion.PhaseA.Tests/`)**:
   - `RestoreBackupIdentityVerificationTests.cs`: Populated `ExpectedManagedSha256` on manually created `ManagedFileRecord` instances in multi-file test so actively managed target check succeeds and isolates the tested backup failure.
   - `AutomatedModeAndRestoreAllTests.cs`: Populated `ExpectedManagedSha256` on `inst1.ManagedFiles` fixture.
   - `SystemIntegrityAndEdgeCaseTests.cs`: Populated `ExpectedManagedSha256` on `SingleGame` fixture.
   - `SharedDirectoryCompatibilityTests.cs`: Populated `ExpectedManagedSha256` on `managedRecord` fixture.

4. **New Test Suite (`tests/DXVKCompanion.PhaseA.Tests/RestoreTargetOwnershipVerificationTests.cs`)**:
   - Added tests covering all acceptance criteria:
     - `Restore_InjectedDllOriginallyAbsent_ExternallyReplaced_RefusesAndSurvivesRestore`: Externally replaced file originally absent is not deleted.
     - `Restore_DllOriginallyPresent_ExternallyReplaced_RefusesAndNotOverwrittenEvenWithValidOriginalBackup`: Externally replaced file originally existing is not overwritten with backup.
     - `Restore_MultiFile_LaterTargetMismatch_PreventsEarlierValidFilesFromMutation`: Multi-file ordering where later target mismatch protects earlier valid file from deletion.
     - `Restore_MalformedOrMissingManagedHash_RefusesSafely`: Null, empty, short, non-hex 64 strings safely refuse.
     - `Restore_MissingActivelyManagedTarget_RefusesSafely`: Missing target on disk safely refuses.
     - `Restore_UnknownOriginalState_RefusesSafely_NotSilentlyReportedAsRestored`: Unknown original state is not skipped or marked restored.
     - `Restore_OrdinaryRestoration_AndVerifiedRepeatRestoration_Succeeds`: Successful normal restoration followed by verified repeat no-op restoration, and refusal upon unexpected file introduction.
     - `Restore_Persistence_RefusalAndVersionSurviveStoreReload`: Refusal reason, attention state, and version survive fresh `GameLibraryStore` reload.
     - `Restore_ManagerBoundary_FailedDisable_DoesNotClearProfileDxvkEnabled`: `manager.DisableDxvkAsync` failure preserves `profile.DxvkEnabled = true`.
     - `Restore_MultiFileTransactionEngine_ExpectedManagedSha256_EnforcesTargetOwnershipDirectly`: Direct engine execution validates `ExpectedManagedSha256` before file modification.

---

### 3. Verification

- **Local Execution**: `NOT RUN (pending human authorization)` per repository execution policies.
- **Verification Plan**: Authorized Cloud CI via GitHub Actions on PR submission.
