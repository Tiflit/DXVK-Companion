# AI Development Session: Refuse Newer-Schema Game Library Writes and Incompatible Startup (#107)

- **Date**: 2026-10-14
- **Implementer**: Gemini (Advanced Agentic Coding)
- **Coordinator / Authority**: ChatGPT transcription of human decision approval: *"Approved, include those checks"* (recorded in Issue #106 comment 6180246896).
- **Primary Issue**: #107 (`[AI] Refuse newer-schema library writes and incompatible startup`)
- **Base Revision**: `55bcb055530dceb0a193f11aa55334fa576e214a` (`origin/main`, Workflow Kernel v0.4)
- **Worktree**: `d:\dev\DXVK-Companion-issue-107`
- **Branch**: `fix/refuse-newer-schema-library`

---

### Context and Problem

In Issue #106, source inspection revealed that when `GameLibraryStore` encountered a library with `SchemaVersion > CurrentSchemaVersion`, it classified the file as `LoadResult.FutureSchema` and left its internal installation collection empty, but remembered no write prohibition. Subsequent calls to mutating methods (`GetOrCreateInstallation`, `Save`, `SaveAll`, or `RecordDetectionSnapshot`) would invoke `WriteAllLocked()`, overwriting the newer-schema library with supported schema (version 1) and empty/incomplete data.

Furthermore, application startup in `Program.cs` created application directories, logged startup messages, and constructed stores, monitoring, DXVK installers, and tray components before performing any schema compatibility validation.

Under human approval in #106, Issue #107 establishes the contract to refuse startup on newer library schemas, guard all storage mutation entry points, and preserve newer libraries byte-for-byte without side effects.

---

### Implementation Summary

1. **`src/DXVKCompanion/Storage/GameLibrarySchemaCompatibility.cs`**:
   - Pure root schema marker inspector that evaluates raw JSON / bytes *before* typed `GameLibrary` deserialization.
   - Accurately identifies well-formed positive integers above `CurrentSchemaVersion` (1), including arbitrary-precision integers too large for `Int32`/`Int64` via `System.Numerics.BigInteger`.
   - Distinguishes explicit newer schemas from malformed markers: non-integer numbers (decimals, scientific exponents), string markers, `null`, booleans, arrays, objects, zero, and negative values are classified as `Malformed` rather than newer, preserving existing malformed-file recovery behavior.
   - Transparently handles UTF-8 BOM, leading/trailing whitespace, and case-insensitive `"SchemaVersion"` property naming.
   - Future unknown enum values and fields inside `Installations` do not cause deserialization exceptions or divert explicit newer schemas into malformed recovery.
   - Defines `IncompatibleLibrarySchemaException : InvalidOperationException` with `FoundSchemaVersionText` and `SupportedSchemaVersion`.

2. **`src/DXVKCompanion/Startup/StartupCompatibility.cs`**:
   - Pure, internally accessible startup preflight decision helper and presentation coordinator.
   - Evaluates target library file: returns `StartupAction.RefuseIncompatibleSchema` for newer schemas; `StartupAction.Continue` for missing, supported, or malformed libraries.
   - Formats user-facing message containing numeric found and supported schema versions and explaining that a compatible build of DXVK Companion is required.
   - Strict message safety: does not disclose raw file paths, file contents, unsupported recovery options, or automatic data relocation advice.
   - Provides an internal presentation hook `MessagePresenter` for headless verification without interactive UI modal blocks.

3. **`src/DXVKCompanion/Program.cs`**:
   - Evaluates `StartupCompatibility.Evaluate(GameLibraryPaths.GameLibraryFile)` before calling `Paths.EnsureDirectories()`, `Logger.Log(...)`, or constructing any stores, monitoring, release managers, or UI tray components.
   - When refused, initializes WinForms application configuration solely to present the warning message, then returns immediately.

4. **`src/DXVKCompanion/Storage/GameLibraryStore.cs`**:
   - `Load()` runs schema preflight before directory creation or logging. On refusal, sets `_isRefused = true`, leaves library byte-for-byte unchanged, does not create absent backup directories, and does not create temporary or recovery files.
   - Guards every mutating entry point (`GetOrCreateInstallation`, `Save`, `SaveAll`, `RecordDetectionSnapshot`) with `EnsureNotRefusedLocked()` under `_sync`.
   - Re-checks on-disk schema before each mutation/save to guard against external writes occurring after store construction.
   - When refused, mutating methods throw `IncompatibleLibrarySchemaException` without altering in-memory state or invoking writer/commit seams.

5. **`docs/spec/DXVK-COMPANION-SPEC.md`**:
   - Added Section 40.1 documenting library schema compatibility, startup refusal, storage mutation guards, and non-destructive file preservation.

6. **Test Coverage**:
   - `GameLibrarySchemaCompatibilityTests.cs`: comprehensive unit tests for positive integers, large integers, zero/negative, non-integers, non-numeric markers, BOM, casing, unknown enums, and missing files.
   - `StartupCompatibilityTests.cs`: tests for missing, supported, malformed, and newer schema startup decisions, message safety, and presenter hook invocation.
   - `GameLibraryStorePersistenceTests.cs`: tests for initial newer schema load, byte-for-byte preservation of files and sentinel siblings, absent backup directory non-creation, rejection across all 4 mutation entry points, and detection of on-disk newer schema appearing after construction.
   - `LegacyMigrationAndDetectionWiringTests.cs`: verified existing clean-slate tests and asserted `store.IsRefused` on future schema load.

---

### Inspected vs. Executed Evidence

- **Local Product Tests & Builds**:
  - **NOT RUN**. Local product test suites (`dotnet test`), builds (`dotnet build`), and desktop application executions were strictly NOT RUN pursuant to Issue #107 prohibitions. Correctness is verified via GitHub Actions cloud CI.
- **Host Runtime Probes**:
  - **NOT RUN**. Strictly zero host exploratory shell probes or filesystem executions were performed on the host machine.
- **Executed Workflow Helper Tool Launches & Scope Disclosure**:
  - Scoped Git commands (`git fetch`, `git worktree add`, `git status`, `git diff`, `git diff --check`, `git add`, `git commit`, `git push`).
  - Scoped GitHub CLI commands (`gh issue view`, `gh pr checks`, `gh run view`, `gh pr view`).
  - Publication helpers (`check_pr_hygiene.py`, `evaluate_scope.py`, `generate_handoff.py`, `update_pr_body.py`).

---

### Recorded Interruption Causes

- **Justified Interruptions**: None during implementation.
- **Unnecessary Interruptions**: None.
- **Uncertain Interruptions**: None.

---

### Repair Batch: Cloud CI Compilation Feedback Resolution

- **Trigger 1**: Cloud CI run `38651994365` reported compilation errors CS1061 in `src/DXVKCompanion/Storage/GameLibrarySchemaCompatibility.cs`:
  - `'JsonProperty' does not contain a definition for 'ValueKind'`
  - `'JsonProperty' does not contain a definition for 'GetRawText'`
- **Root Cause & Inspection**: `schemaProp` was declared as nullable `JsonProperty?`. Accessing `schemaProp.Value` yielded the non-nullable `JsonProperty` struct rather than its contained `JsonElement` property (`prop.Value`).
- **Resolution**: Extracted `var valueElement = schemaProp.Value.Value;` to query `valueElement.ValueKind` and `valueElement.GetRawText()`.
- **Trigger 2**: Cloud CI run `38652232353` reported compilation error CS0246 in `tests/DXVKCompanion.PhaseA.Tests/GameLibraryStorePersistenceTests.cs`:
  - `The type or namespace name 'ApiClassification' could not be found`
- **Root Cause & Inspection**: Canonical class name in `DXVKCompanion.Models` is `ApiClassificationResult`.
- **Resolution**: Updated `GameLibraryStorePersistenceTests.cs` to instantiate `new ApiClassificationResult`.
- **Trigger 3**: Cloud CI run `38652519540` reported test failure in `GameLibrarySchemaCompatibilityTests.AssessJson_Utf8BomAndLeadingWhitespace_IsHandledCleanly` (Expected: `IncompatibleNewer`, Actual: `Malformed`).
- **Root Cause & Inspection**: `JsonDocument.Parse(ReadOnlyMemory<byte>)` does not automatically strip the 3-byte UTF-8 BOM preamble (`0xEF, 0xBB, 0xBF`), causing `JsonException` on raw byte buffers.
- **Resolution**: Added explicit UTF-8 BOM detection and slicing in `AssessJson(byte[])` (`ReadOnlyMemory.Slice(3)`) and `AssessJson(string)` (`\uFEFF`).
- **Scope**: Confined strictly to `src/DXVKCompanion/Storage/GameLibrarySchemaCompatibility.cs`, `tests/DXVKCompanion.PhaseA.Tests/GameLibraryStorePersistenceTests.cs`, and this journal. Zero public API changes.

---

### Retained Limitations & Gaps

- Bounded storage compatibility guard: defends against Companion downgrade overwriting newer data; does not provide cross-process locking or multi-file transaction atomicity.
- An external concurrent writer replacing the file between the pre-commit check and `File.Move` remains an inherent OS-level race window, honestly acknowledged.

---

### Handoff & Next Owner

- **Next Owner**: **ChatGPT** (Coordinator / Verifier).
- **Action**: Verify cloud CI workflow results, confirm contract adherence and matching cloud outcomes, and conduct coordinator review.
- **Merge Authority**: Retained by **Human**.
