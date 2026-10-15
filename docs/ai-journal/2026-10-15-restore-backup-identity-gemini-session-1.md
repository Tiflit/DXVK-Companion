# AI Engineering Journal: Original-Backup Identity Verification During Restore

**Date**: 2026-10-15  
**Session**: Gemini Session 1  
**Primary Issue**: [#125](https://github.com/Tiflit/DXVK-Companion/issues/125)  
**Base Revision**: `cf30974b9d4ecde627efd2ab8d8e6d74a651cb5a` (`origin/main`)  

---

### 1. Objective & Defect Context

Investigate and implement a focused repair for original-backup identity verification during restore:
- `DxvkRollback` previously did not pass `ManagedFileRecord.OriginalSha256` into the restore transaction request.
- `MultiFileTransactionEngine` previously hashed whatever backup file was present on disk at restore time and treated that observed hash as authoritative.
- Consequently, an altered or corrupted backup file could be restored successfully, overwriting the game file with corrupted contents.

**Required behavior**:
- Carry the already persisted original SHA-256 into the restore request (`ExpectedOriginalSha256` on `MultiFileTransactionFile` as an in-memory field; no persisted schema change).
- For files recorded as originally existing, require a valid recorded original hash (64 hex digits) and an available backup matching it.
- Validate all required backups before modifying or deleting any game file.
- On missing, malformed, or mismatched baseline evidence, return failure, preserve game files and backups, retain management/version information, and persist an understandable reason using existing attention/error fields (`RestorationState.AttentionRequired`, `LastRefusalReason`).
- Verify restored content against the recorded original hash after application.
- Never reconstruct the expected hash from the backup being checked, silently replace the baseline, or weaken verification to accommodate incomplete records.
- Preserve successful restoration with valid backups and existing behavior for files recorded as originally absent.

---

### 2. Implementation Changes

1. **`src/DXVKCompanion/Safety/MultiFileTransactionEngine.cs`**:
   - Added `public string? ExpectedOriginalSha256 { get; init; }` to `MultiFileTransactionFile` (in-memory request field).
   - In `Execute`: Added pre-application validation loop for `TransactionOperation.Restore` with `OriginalFileState.Existing`:
     - Checks `IsValidSha256(file.ExpectedOriginalSha256)`; returns `SafeFailure` if missing or malformed.
     - Checks `File.Exists(backupPath)`; returns `SafeFailure` if unavailable.
     - Checks `string.Equals(backupIdentity.Sha256, file.ExpectedOriginalSha256, StringComparison.OrdinalIgnoreCase)`; returns `SafeFailure` if backup hash does not match expected original hash.
   - In `Verify`: For `TransactionOperation.Restore` with `OriginalFileState.Existing`, verifies that the target file on disk matches `item.File.ExpectedOriginalSha256`.
   - Added `IsValidSha256` helper method.

2. **`src/DXVKCompanion/DXVK/DxvkRollback.cs`**:
   - Populated `ExpectedOriginalSha256 = mf.OriginalSha256` when building `MultiFileTransactionFile`.
   - Added validation check for `mf.OriginalState == FileOriginalState.Existing`: if `!IsValidSha256(mf.OriginalSha256)`, marks `installation.RestorationState = RestorationState.AttentionRequired`, records `installation.LastRefusalReason`, saves to `_gameLibraryStore`, and returns `false`.
   - Handled transaction failure (`result.State != TransactionState.Committed || result.Outcome != TransactionOutcome.Success`): records refusal reason, sets `RestorationState.AttentionRequired`, saves to `_gameLibraryStore`, retains `ManagedDxvkVersion`, and returns `false`.
   - Added `LastRefusalReason` property to `DxvkRollback`.

3. **`tests/DXVKCompanion.PhaseA.Tests/MultiFileTransactionEngineTests.cs`**:
   - Updated `RestoreThreeFiles_ReturnsAllFilesToOriginalState` and `RestoreFile` helper to pass known original content hash captured prior to install.

4. **`tests/DXVKCompanion.PhaseA.Tests/RestoreBackupIdentityVerificationTests.cs`**:
   - Added comprehensive synthetic tests covering the 5 acceptance criteria:
     1. `Restore_BackupModifiedAfterInstallation_RefusesBeforeGameFileMutation`
     2. `Restore_MissingBackup_RefusesSafely`
     3. `Restore_MissingOrMalformedRecordedHash_RefusesSafely`
     4. `Restore_MultiFile_InvalidLaterBackup_LeavesEarlierFilesAndOriginallyAbsentFileUntouched`
     5. `Restore_ValidBackups_ExactOriginalContentsRestoredSuccessfully`
     6. `Restore_FailureStateAndManagementInfo_SurviveReloadingLibrary`

---

### 3. Verification

- Verification route: Cloud CI on GitHub Actions (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`, `ai-workflow-tests`).
- Local host test execution: `NOT RUN (pending human authorization)`.
