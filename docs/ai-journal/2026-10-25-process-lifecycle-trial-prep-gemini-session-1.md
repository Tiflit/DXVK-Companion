# Packaged Process-Lifecycle Trial Tooling Preparation and Candidate Profile — Gemini, 2026-10-25

Task [Issue #301](https://github.com/Tiflit/DXVK-Companion/issues/301), remote main `bcd5bc2f81ac6593c96be39b0e949fc88f388a9b`, policy `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9` (kernel v0.4).
Direct owner direction after merging PR #300: "Merged / Continue". Codex orchestrates and reviews; Gemini is the primary coding agent, implementer, and tester.

Implemented bounded process-lifecycle setup, supervisor, snapshot, and verifier script (`ProcessLifecycleTrial.ps1`), 20 hosted C# unit tests exercising pure data/guard helpers (`ProcessLifecycleTrialPreparationTests.cs`), candidate execution profile in `PACKAGED-PROCESS-LIFECYCLE-V1.md`, and this session journal, strictly confined to the four allowed paths.

## Implementation Details

1. **Trial Tooling Script (`tools/validation/ProcessLifecycleTrial.ps1`)**:
   - `Set-StrictMode -Version 3.0`, `$ErrorActionPreference = "Stop"`.
   - Embedded frozen cryptographic pins:
     - Helper Artifact ID `12609386490`, ZIP SHA256 `f75ec55738e6ca3c1a0cd0a342aefeaa0fb3addd03538f03f2ac7a43295750de` (81,717 bytes), EXE SHA256 `f720cecae0f13eeef6d9b5e2d259b51df97b37bc3347b241071e25e8c1548d70` (138,240 bytes), source SHA256 `19229f59a4ab2504634cf9cd1bfff131a89a68e539808ab68c5459451e6ce0cc`, commit `a62356d0acb918aec957463a0b670747763ae544`, tree `4ebbd76b0f30ca7768d737361ea8b540da5a8a2e`.
     - Companion Candidate Artifact ID `12532606945`, ZIP SHA256 `32660fd3e3ed87385e47940d490db2e6ab93daad7e993fe4225dd157232b3b14` (66,410,238 bytes), EXE SHA256 `25012c67ade281e0d4585282bf485e4783a1f7c486018f8d1a038c5f2dc18ca4` (71,772,524 bytes), provenance SHA256 `c9b46817bc60e89b3848ecf2705cb929b718cf950689a15071c65933d2a0537d` (323 bytes), source commit `46e6f22363cddcfa88d805b588b6ac30173bc9dc`.
     - Official DXVK v3.1.1: Release ID `381956359`, Asset `566164743`, SHA256 `40565b4a724aadc4433fa4e010b4b23916d9b1f1baeee64e17186db94f54e608` (18,041,512 bytes).
     - Official DXVK v3.1: Release ID `357524779`, Asset `534051057`, SHA256 `30f9cc326874be344285582275446968cfa4c069db31ce56df312d6644179154` (18,056,721 bytes).
   - Safe zip inspection and extraction (`Test-SafeZipArchiveInternal`, `Expand-SafeZipArchiveInternal`):
     - Maximum 1024 entries, maximum 500 MiB uncompressed size.
     - Rejects root-relative paths (`/`, `\`), drive colons (`:`), alternate data streams, and traversal segments (`..`).
     - Detects and rejects duplicate/colliding entry paths.
   - Fail-closed path containment (`Test-PathEscape`): verifies canonical target path stays inside base root and detects reparse point / junction traversals.
   - Setup action (`Setup`):
     - Confined to fresh synthetic `<FixtureRoot>`; refuses pre-existing non-empty roots.
     - Validates supplied archive hashes against frozen pins (or test pins for test fixtures).
     - Extracts helper and companion binaries into isolated subdirectories.
     - Seeds target with inert `d3d11.dll` (never loaded), noncanonical `dxvk.conf`, `sentinel.txt`, and copies immutable baseline to `baseline/` with `baseline_manifest.json` asserting absent `dxgi.dll`.
     - Seeds companion profile store with `game-library.json` (disabled Manual-policy DX11/x64 record) and `settings.json` (`GlobalPolicy = Manual`, `LaunchOnStartup = false`).
     - Initializes `state.json` recording batch start time, helper/companion start counts, duration caps, and test pin status.
   - Batch guard and supervised execution (`Start-Helper`, `Stop-Helper`, `Start-Companion`):
     - Strict test-pins guard: any fixture initialized with test pins refuses process launch (`TestPinsUsed == true`), preventing test mocks from ever launching binaries.
     - Mandatory adoption attestation token (`ADOPTED-PROCESS-LIFECYCLE-V1`) required before launch.
     - Duration cap (120 minutes), helper start cap (4 starts), companion start cap (8 starts), single process concurrency checks.
     - Helper launch validates working directory, redirects stdout to verify `READY` within 5000ms.
     - Helper stop writes regular-file `control/stop-N.request` (refusing directory markers or deletions) and awaits exit 0 (`STOPPED`).
     - Companion launch isolates single-file runtime extraction directory via child-only `DOTNET_BUNDLE_EXTRACT_BASE_DIR`.
   - Immutable snapshotting (`Take-Snapshot`): copies `target/`, `Profiles/`, and `control/` into `snapshots/<CheckpointName>/` and generates cryptographic manifest with SHA256 digests.
   - Verification (`Verify-Checkpoint`, `Compare-TargetBaseline`, `Compare-SemanticLibraryJson`):
     - Semantic comparison of `game-library.json` installations normalizing property ordering.
     - Bit-for-bit raw byte comparison of target against immutable baseline manifest.
     - Evaluates pending checkpoints (unmutated target files, absent dxgi, recorded pending action), applied checkpoints (target DLLs match DXVK version, native backups created, pending action cleared), and restored checkpoints (target matches baseline, dxgi absent, backup history preserved).

2. **Hosted Verification Suite (`tests/DXVKCompanion.PhaseA.Tests/ProcessLifecycleTrialPreparationTests.cs`)**:
   - Integrated into existing `DXVKCompanion.PhaseA.Tests` via default wildcard inclusion; zero changes to `.csproj`, dependencies, or CI workflows.
   - 20 comprehensive unit tests:
     - `Script_Exists_And_ParsesWithoutSyntaxErrors`: AST parsing of script file with zero syntax errors.
     - `Script_FrozenPins_MatchPublishedConstants`: Asserts all 8 published hash/identity constants match script text.
     - `SafeZipArchive_ValidArchive_PassesValidation`: Positive control on valid zip.
     - `SafeZipArchive_ZipSlipTraversal_IsRejected`: Negative control on `../slip.txt`.
     - `SafeZipArchive_RootRelativePath_IsRejected`: Negative control on `/escaped.txt`.
     - `SafeZipArchive_DuplicateEntries_IsRejected`: Negative control on duplicate member names.
     - `PathContainment_PathOutsideRoot_IsRejected`: Negative control on escaping base root.
     - `PathContainment_ContainedPath_Succeeds`: Positive control on contained subpaths.
     - `Setup_RefusesExistingNonEmptyRoot`: Refusal of pre-existing root directory.
     - `Setup_RefusesMismatchedArchiveHash`: Fail-closed hash mismatch on archives.
     - `Setup_SucceedsWithTestPins_InitializesDirectoryStructure`: Complete setup verification with synthetic archives, verifying all generated files, directories, manifests, and `TestPinsUsed == true`.
     - `LaunchHelper_RefusesMissingAdoptionAttestation`: Guard refusal before launch when attestation token is missing.
     - `LaunchHelper_RefusesWhenTestPinsUsed`: Guard refusal before launch on test fixture even with valid token.
     - `LaunchHelper_RefusesPreExistingStopMarker`: Refusal when `stop-1.request` already exists.
     - `BatchGuard_RefusesWhenExhaustedOrEnded`: Refusal when `HelperStartCount >= 4` or batch ended.
     - `TakeSnapshot_CapturesFilesAndManifest`: Snapshot creation and manifest verification.
     - `SemanticJson_IdentifiesEquivalentObjects_RegardlessOfKeyOrder`: Normalizes property order and detects semantic equivalence.
     - `SemanticJson_DetectsFieldDifferences`: Detects mismatched version fields.
     - `CompareTargetBaseline_DetectsMutationDuringPendingCheckpoint`: Detects target file tampering during pending state.
     - `VerifyCheckpoint_RestoredFailsIfUnexpectedFileOrMismatch`: Detects leftover `dxgi.dll` in restored state.
   - Zero helper or companion process launches; zero network access; bounded child timeouts and clean waits.

3. **Validation Specification (`docs/validation/PACKAGED-PROCESS-LIFECYCLE-V1.md`)**:
   - Added Stage B Candidate Execution Profile.
   - Explicitly marked: **CANDIDATE ONLY — PENDING SEPARATE HUMAN ADOPTION AND CURRENT READINESS**.
   - Cryptographic and artifact bindings: script SHA256 `d3299d5b69cb8cb256f1389711cf4a499655df6fcec200387da4f8086af3f018`, helper artifact `12609386490`, companion candidate `12532606945`, DXVK v3.1.1 `381956359`, DXVK v3.1 `357524779`.
   - Resource limits: 120 minutes total duration, 4 helper starts, 8 companion starts, 2 GiB storage cap, 25-minute helper watchdog.
   - Complete 16-step sequential manual operator procedure and stop/hold rules.

## Execution and Verification Record

- **Workstation Verification Policy**:
  - Strictly zero local tests, zero local binary compilation or execution, and zero artifact downloads on the workstation.
  - Zero local test commands run. All tests run in hosted Windows CI.
- **Pre-flight CI Status on Main (`bcd5bc2f81ac6593c96be39b0e949fc88f388a9b`)**:
  - Workflow run `40224152393` passed.
  - Application run `40224152390` attempt 1 and attempt 2 failed before restore/build/tests due to an external HTTP 409 endpoint error in `actions/setup-dotnet`.
  - Preserved as an external infrastructure blocker, not a codebase defect.
