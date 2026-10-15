# AI Engineering Journal: Refuse Adoption of Mixed DXVK and Unrelated DLLs

**Date**: 2026-10-15  
**Session**: Gemini Session 1  
**Primary Issue**: [#123](https://github.com/Tiflit/DXVK-Companion/issues/123)  
**Related Context**: External Audit Finding, [#121](https://github.com/Tiflit/DXVK-Companion/issues/121), [#122](https://github.com/Tiflit/DXVK-Companion/pull/122)  
**Base Revision**: `763aa754f1ab7d4fe622fe9e7fa4ba0dd162f3fc` (`origin/main`)  

---

### 1. Objective & Task Context

Address the mixed-DLL adoption defect identified in the external audit:
- `ExistingDxvkDetector` previously allowed a directory to be classified as `ExistingDxvkStatus.OfficialRelease` when one relevant DLL matched a known/local release and another relevant DLL was present but unmatched and lacked DXVK metadata (e.g., standard native or third-party DLL).
- When classified as `OfficialRelease`, `AdoptExisting` claimed ownership of all detected DLLs in `assessment.DetectedDlls`, marking them with `OriginalState = FileOriginalState.Missing`.
- Consequently, a subsequent restore/rollback operation would delete the unmatched/unrelated DLL under the false assumption that it did not exist prior to DXVK installation.

**Required behavior**:
- Refuse adoption of a mixed detected set without introducing a new partial-adoption feature or conflict-resolution UI.
- A matching `d3d11.dll` alongside an unrelated `dxgi.dll` must not be classified as `OfficialRelease` and must not be adoptable (`CanBeAdopted == false`).
- Refused adoption must leave physical files, ownership records, and profile enabled/version state completely unchanged.
- Consistently matched, supported adoption (e.g., matching `d3d11.dll` alone or matching `d3d11.dll` + `dxgi.dll`) continues to work.
- Tests exercise detection and the adoption boundary together, verifying preservation of the unrelated file.

---

### 2. Changes Made

1. **`src/DXVKCompanion/DXVK/ExistingDxvkDetector.cs`**:
   - In `AssessDirectory`, updated the DLL evaluation loop:
     - When `FindMatchingOfficialVersion` does not match an official release (`version == null`), `matchedAllToOfficial` is now set to `false` for all unmatched DLLs, including standard native or third-party DLLs lacking DXVK PE metadata.
     - When `FindMatchingOfficialVersion` matches an official release (`version != null`), `anyDxvkSignature` is explicitly set to `true`.
   - As a result, any directory containing a mixed set of DLLs (e.g., official `d3d11.dll` + unrelated `dxgi.dll`, or mismatched versions) cannot satisfy `matchedVersion != null && matchedAllToOfficial`.
   - Mixed sets with at least one DXVK signature are classified as `ExistingDxvkStatus.RecognizedUnknownVersion` (`CanBeAdopted == false`, `MatchedVersion == null`).

2. **`src/DXVKCompanion/DXVK/DxvkInstaller.cs`**:
   - In `AdoptExisting`, recorded `LastRefusalReason = $"Existing DLLs cannot be adopted (status: {assessment.Status})."` when `!assessment.CanBeAdopted || string.IsNullOrEmpty(assessment.MatchedVersion)`.

3. **`src/DXVKCompanion/DXVK/DxvkManager.cs`**:
   - In `AdoptExistingAsync`, recorded `LastRefusalReason = $"Existing DLLs cannot be adopted (status: {assessment.Status})."` when `!assessment.CanBeAdopted` prior to calling `_installer.AdoptExisting`.

4. **`tests/DXVKCompanion.PhaseA.Tests/PhaseCManualManagementTests.cs`**:
   - Added comprehensive regression tests:
     - `ExistingDxvkDetector_MatchingD3D11AlongsideUnrelatedDxgi_NotAdoptable`: verifies that a matching `d3d11.dll` alongside an unrelated native `dxgi.dll` yields `RecognizedUnknownVersion`, `MatchedVersion == null`, and `CanBeAdopted == false`.
     - `DxvkInstaller_AdoptExisting_MixedDlls_RefusesAdoption_LeavesFilesAndOwnershipUnchanged_AndRollbackPreservesUnrelatedFile`: verifies that the adoption boundary refuses adoption, creates zero ownership records in `GameLibraryStore`, leaves profile state unchanged, leaves disk files intact, and preserves the unrelated file across restore/rollback.
     - `DxvkManager_AdoptExistingAsync_MixedDlls_RefusesAdoption_LeavesFilesAndProfileUnchanged`: verifies that `DxvkManager.AdoptExistingAsync` refuses adoption, sets `LastRefusalReason`, leaves profile storage unchanged on disk, and does not touch files or library records.
     - `ExistingDxvkDetector_OfficialRelease_SingleD3D11Dll_RecognizesAndCanBeAdopted`: positive control verifying that a game directory with only a single matching official `d3d11.dll` remains recognized as `OfficialRelease` and adoptable.

---

### 3. Verification

- Verification route: Cloud CI on GitHub Actions (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`, `ai-workflow-tests`).
- Local test execution: `NOT RUN (pending human authorization)`.
