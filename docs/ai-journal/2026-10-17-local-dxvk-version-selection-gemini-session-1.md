# AI Engineering Journal: Per-Game DXVK Local Version Selection Offline

**Date**: 2026-10-17  
**Session**: Gemini Session 1  
**Primary Issue**: [#163](https://github.com/Tiflit/DXVK-Companion/issues/163)  
**Base Revision**: `bceac1c8f5c2e88e159f654062f03432c533a285` (`origin/main`, merged #162)  

---

### 1. Objective & Scope

Issue [#163](https://github.com/Tiflit/DXVK-Companion/issues/163) delivers per-installation selection of "Latest" or an already-downloaded DXVK version for compatibility, using the shared local library offline. This connects the user preference to actual immediate and queued Enable and Update behavior, completing the next bounded phase of [#159](https://github.com/Tiflit/DXVK-Companion/issues/159).
- **Core Requirements**:
  1. Add a clear DXVK version selector to Game Details: "Latest" plus eligible locally available exact version tags from the installer's configured source root. Keep desired selection separate from recorded installed version and historical `GameProfile.DxvkVersion`. Changing or saving selection must never prematurely deploy DLLs, mutate recorded installation state/version, or silently enable a game.
  2. Version choice is independent of management mode. Use the existing optional `ManagementPolicy.PinnedDxvkVersion` preference without adding new library schemas or fields. Null means Latest; a nonempty selected exact version is the requested version for that installation, including under `UseGlobal`, `Automatic`, and `Disabled`. Preserve existing legacy `Mode = PinnedVersion` records and automatic eligibility. When clearing a legacy pinned-mode choice to Latest, normalize to `Automatic` (preserving automated eligibility without ever turning Manual/Disabled automatic).
  3. Persist through staged `GameLibraryStore.Save` and survive reopen/restart. Do not repurpose `GameProfile.DxvkVersion` as a preference or rewrite actual managed versions on Save Settings.
  4. Local choice discovery: scan the manager/installer's actual shared source root (not hardcoded or per-game cache). Require selected architecture and full required DLL set for the target's supported API (`d3d9.dll` for DX9; `d3d11.dll` and `dxgi.dll` for DX10/11); ignore unrelated/empty/incomplete directories. Reject path traversal, reserved dot paths, rooted/separator-bearing names, and link escapes. Deterministic descending sort, zero archive/network requests. Unsupported/unknown API or architecture must return empty/ineligible.
  5. Unavailable pinned versions: a persisted selected version whose payload becomes unavailable must remain visibly selected with `(unavailable)` status; do not silently revert to Latest. Explicit enable must fail clearly without target mutation if that exact local payload is unusable.
  6. Read-only while pending: while an installation has pending work queued, lock version selection with a clear explanatory note. Retain the saved choice when Save Settings is used.
  7. Public request methods: honor selected exact version through `RequestEnable/RequestEnableByPath` and `RequestUpdate/RequestUpdateByPath`, persisted exit/startup execution, and `UpdateAllEnabledAsync`. Explicit nonempty `targetVersion` arguments remain exact request intent. When queueing work while running, capture chosen exact tag or Latest intent when unpinned; never mistake historical `profile.DxvkVersion` for an implicit pin.
  8. UpdateAvailable and Update All: up-to-date pinned installation is not outdated merely because latest is newer. Local pinned operations need zero metadata/archive HTTP; avoid unconditional latest lookup for all-pinned batches. Mixed batches tolerate latest metadata failure without blocking local pinned updates.
  9. Retain baseline/ownership/API/anti-cheat guards, inspection/refusal behavior, safe exit queueing, actual version persistence, restore, all older downloaded payloads and restoration backups.
- **Allowed Paths**:
  - `src/DXVKCompanion/Models/ManagementPolicy.cs`
  - `src/DXVKCompanion/DXVK/DxvkManager.cs`
  - `src/DXVKCompanion/DXVK/DxvkLocalReleaseLibrary.cs`
  - `src/DXVKCompanion/UI/GameDetailsWindow.cs`
  - `src/DXVKCompanion/UI/GameDetailsPolicySelection.cs`
  - `src/DXVKCompanion/UI/GameDetailsVersionSelection.cs`
  - `tests/DXVKCompanion.PhaseA.Tests/DxvkLocalReleaseLibraryTests.cs`
  - `tests/DXVKCompanion.PhaseA.Tests/GameDetailsVersionSelectionTests.cs`
  - `tests/DXVKCompanion.PhaseA.Tests/GameDetailsPolicySelectionTests.cs`
  - `tests/DXVKCompanion.PhaseA.Tests/PerGameVersionSelectionTests.cs`
  - `docs/ai-journal/2026-10-17-local-dxvk-version-selection-gemini-session-1.md`

---

### 2. Implementation

1. **`DxvkLocalReleaseLibrary.cs`**:
   - `IsSafeReleaseDirectoryName(string? name)`: validates release directory name against directory traversal (`..`, `.`), path separators (`/`, `\`, `:`), invalid file name characters, and null/whitespace.
   - `NormalizeArchitecture(string? architecture)`: maps case-insensitively `"x32"`, `"x86"`, `"win32"` to `"x32"`; `"x64"`, `"amd64"`, `"win64"` to `"x64"`. Returns `null` for `"Unknown"` or unrecognized values, preventing arbitrary folder qualification.
   - `GetVersionDirectory(string sourceRoot, string? versionTag)`: validates canonical path boundaries under `sourceRoot`, ensures directory existence, and verifies that reparse points/symlinks resolve strictly within `sourceRoot`.
   - `IsVersionAvailable(string sourceRoot, string? versionTag, GraphicsApi api, string? architecture)`: checks that the API is supported via `DxvkCompatibility.IsDxvkSupported(api)` and architecture is valid. Verifies existence and non-zero byte length for all required DLLs (`d3d9.dll` for DX9; `d3d11.dll` and `dxgi.dll` for DX10/DX11), verifying that file reparse points do not escape `sourceRoot`.
   - `GetAvailableVersions(string sourceRoot, GraphicsApi api, string? architecture)`: enumerates subdirectories of `sourceRoot`, filters to valid release directories containing all required architecture DLLs with size > 0 and no link escapes, and returns a deterministically sorted descending list of version tags.

2. **`GameDetailsVersionSelection.cs`**:
   - Pure UI helper:
     - `GetAvailableLabels(IReadOnlyList<string>? availableVersions, string? currentPinnedVersion)`: formats list starting with `"Latest"`, followed by available version tags. If a persisted `currentPinnedVersion` is not found among available local directories, appends `FormatUnavailableLabel(currentPinnedVersion)` (`"{version} (unavailable)"`) so it remains visibly selected without silent reversion.
     - `GetInitialSelectedIndex(...)`: resolves the matching label index for `currentPinnedVersion` (including unavailable format), defaulting to 0 (`"Latest"`).
     - `ResolveSelectedVersion(...)`: maps selected index back to version string (`null` for Latest, exact version string for versions including unavailable tags).
     - `ApplyVersionSelection(ManagementPolicy policy, string? selectedVersion)`: sets `policy.PinnedDxvkVersion`. When setting an exact version, preserves existing `policy.Mode` (`UseGlobal`, `Automatic`, `Disabled`, or legacy `PinnedVersion`). When clearing to Latest, clears `PinnedDxvkVersion = null` and normalizes legacy `PinnedVersion` mode to `Automatic` (preserving automatic eligibility without ever turning Disabled/Manual automatic).

3. **`GameDetailsWindow.cs`**:
   - Increased dialog height from 580 to 650 to maintain clear layout scale with zero control overlap.
   - Populated DXVK Version dropdown using `DxvkLocalReleaseLibrary.GetAvailableVersions(_dxvk.DxvkSourceDir, _profile.Api, _profile.Architecture)`.
   - Locked version combo box with informative text (`"Version selection is locked while pending work is queued."`) when `installation.PendingAction.IsPending` is true.
   - In Save Settings click handler: resolves version selection and applies it to `installation.ManagementPolicy` (retaining existing choice if locked due to pending work), saving to `_gameLibraryStore` without modifying recorded deployed version or copying DLLs.

4. **`DxvkManager.cs`**:
   - Exposed `public string DxvkSourceDir => _installer.DxvkSourceDir;`.
   - Updated `UpdateAvailable(GameProfile profile, ReleaseInfo? latest = null)`:
     - Checks `installation?.ManagementPolicy?.PinnedDxvkVersion`. If pinned, compares `profile.DxvkVersion` with the pinned target. An up-to-date pinned installation is NOT outdated merely because `latest` is newer.
     - For unpinned profiles, checks against `latest.Version`. Returns false if `latest == null`.
   - Updated `UpdateAllEnabledAsync()`:
     - Inspects all enabled profiles: if all enabled profiles are pinned, skips `GetLatestReleaseAsync()` completely, guaranteeing zero HTTP calls for local batches.
     - In mixed batches, isolates `GetLatestReleaseAsync()` in a try/catch block so that metadata failures do not prevent eligible local pinned updates.
   - Updated `QueueOrApplyAsync(GameProfile profile, bool isRunning, PendingAction action, string? targetVersion = null)`:
     - Resolves `effectiveTarget = !string.IsNullOrWhiteSpace(targetVersion) ? targetVersion.Trim() : (installation?.ManagementPolicy?.PinnedDxvkVersion?.Trim() ?? "latest")`.
     - When queueing work while game is running: saves `PendingAction.Install(effectiveTarget, "Queued while game running")` (or `Restore`), strictly avoiding treating historical `profile.DxvkVersion` as an implicit preference.
     - Passes `targetVersion` through `RequestEnableAsync`, `RequestUpdateAsync`, `RequestEnableByPathAsync`, and `RequestUpdateByPathAsync`.
   - Updated `EnableDxvkAsync(GameProfile profile, string? targetVersion = null)`:
     - Resolves `effectiveTarget` from `targetVersion ?? installation?.ManagementPolicy?.PinnedDxvkVersion`.
     - When an exact version tag is requested: validates local availability via `DxvkLocalReleaseLibrary.IsVersionAvailable`. If unavailable, sets `LastRefusalReason = $"Requested DXVK version '{effectiveTarget}' is not available locally."` and returns `false` without mutating the game directory, profile, or recorded versions.
     - If available, constructs `ReleaseInfo { Version = effectiveTarget, DownloadUrl = "" }`, which `DxvkInstaller.ApplyToGameAsync` deploys from local library without network access.

---

### 3. Verification & Evidence

- **`DxvkLocalReleaseLibraryTests.cs`**:
  - `IsSafeReleaseDirectoryName_ValidatesCorrectly`: tests validation and rejection of traversal, invalid characters, separators, empty strings.
  - `NormalizeArchitecture_NormalizesOrReturnsNull`: tests x32/x86/win32, x64/amd64/win64 normalization, and rejection of Unknown/arm64.
  - `GetVersionDirectory_RejectsEscapesAndNonExistent`: tests safe directory resolution and rejection of path escapes.
  - `IsVersionAvailable_UnsupportedApiOrArchitecture_ReturnsFalse`: tests rejection for DX12, Vulkan, Unknown APIs, and Unknown architecture.
  - `IsVersionAvailable_MissingDllOrZeroByteDll_ReturnsFalse`: tests rejection when DLLs are missing or 0-bytes, and acceptance when present with length > 0.
  - `IsVersionAvailable_Dx9_RequiresD3D9`: tests DX9 requiring `d3d9.dll`.
  - `GetAvailableVersions_DeterministicDescendingAndExcludesIncomplete`: tests directory enumeration, exclusion of incomplete or 0-byte versions, and descending sorting.

- **`GameDetailsVersionSelectionTests.cs`**:
  - `GetAvailableLabels_WhenNoPinnedVersion_ReturnsLatestPlusAvailableVersions`: tests Latest prefix and available version list.
  - `GetAvailableLabels_WhenPinnedVersionIsAvailable_ReturnsWithoutUnavailableSuffix`: tests label generation when pinned version is present locally.
  - `GetAvailableLabels_WhenPinnedVersionIsUnavailable_AppendsUnavailableSuffix`: tests appending `(unavailable)` for missing pinned payload.
  - `GetInitialSelectedIndex_MapsCorrectly`: tests index selection for Latest, available tags, and unavailable tags.
  - `ResolveSelectedVersion_MapsLabelsToVersions`: tests roundtrip resolution from combo box indices to version tags.
  - `ApplyVersionSelection_PreservesModes_WhenSettingExactVersion`: tests setting exact version across Automatic, UseGlobal, Disabled, and legacy PinnedVersion modes.
  - `ApplyVersionSelection_WhenClearingToLatest_ClearsPinAndNormalizesLegacyMode`: tests clearing pin and normalizing legacy PinnedVersion mode to Automatic while preserving Disabled.

- **`GameDetailsPolicySelectionTests.cs`**:
  - `LegacyPinnedMode_PreservesAutomaticEligibility_AndKeepsPin`: verifies legacy pinned mode automated eligibility and pin preservation.

- **`PerGameVersionSelectionTests.cs`**:
  - `ImmediatePinnedDeploy_DeploysExactOlderVersion_WhenNewerLatestMetadataExists`: proves exact older bytes deployed (0 archive downloads), and verifies `UpdateAvailable` returns false despite newer latest release.
  - `OfflinePinnedDeploy_IssuesZeroHttpRequests`: proves immediate deployment of pinned release issues zero HTTP requests.
  - `AllPinnedBatch_UpdateAllEnabledAsync_IssuesZeroHttpRequests`: proves `UpdateAllEnabledAsync` makes zero HTTP calls when all enabled installations are pinned.
  - `MixedBatch_WhenLatestFails_ProceedsWithPinnedUpdates`: proves mixed batch with failing latest metadata updates pinned installations without throwing or blocking.
  - `ExitStartupPendingQueuedApply_PreservesSnapshotTargetAndZeroNetwork`: proves queued target snapshot survives restart and deploys offline with zero network requests.
  - `UnpinnedHistoricalOlderProfile_QueuesLatestIntent_NotMistakenForPin`: proves unpinned game with historical version queues `"latest"` rather than mistaking historical version as a pin.
  - `UnavailableVersion_FailsClearlyWithoutTargetMutation`: proves missing payload fails with clear refusal reason without mutating game directory or silently reverting policy.
  - `SavePreferenceWithoutMutatingInstalledVersion_AndDeployOnlyOnExplicitAction`: proves saving preference does not mutate disk DLLs, explicit update deploys target, and rollback restores original baseline while older source payloads remain intact.

---

### 4. Codex Grouped Review P2 Repairs

Codex reviewed PR #164 at head `858bd2ccd1bc0eb56c9ae9e67651b563149d24c0` with 3 P2 findings addressed in this grouped repair cycle:

1. **[P2 - 1] Architecture-directory links:**
   - **Fix:** In `DxvkLocalReleaseLibrary.cs`, extracted `ValidateReleasePayload(string fullSource, string versionDir, string arch, string[] requiredDlls)` and `IsPathWithinRoot(string path, string fullSourceRootWithSlash)`. Shared this validation across `IsVersionAvailable` and `GetAvailableVersions`, ensuring no drift. Both `versionDir`, `archDir`, and leaf DLL paths must be within `sourceRoot`, and if any are reparse points (junctions/symlinks), their resolved link targets are verified to reside strictly within `sourceRoot`.
   - **Regressions:**
     - `DxvkLocalReleaseLibraryTests.ArchitectureDirectoryReparsePoint_PointingOutsideRoot_IsExcludedFromListingAndAvailability`: verifies that an architecture directory junction pointing outside `sourceRoot` is excluded from both `GetAvailableVersions` and `IsVersionAvailable`, and external sentinel bytes are untouched.
     - `DxvkLocalReleaseLibraryTests.ArchitectureDirectoryReparsePoint_PointingInsideRoot_IsAccepted`: verifies that internal reparse targets remain available and listed.
     - `PerGameVersionSelectionTests.ArchitectureDirectoryJunction_PointingOutsideRoot_RefusesDeploymentAndLeavesExternalAndGameFilesUnchanged`: tests manager-level deployment refusal when an architecture directory escapes `sourceRoot`, leaving game baseline files and external directory files completely untouched.

2. **[P2 - 2] Consistent architecture alias deployment:**
   - **Fix:** In `DxvkManager.cs`, updated `ReassessEffectiveApi` to normalize `profile.Architecture`, `recordedExe.LastKnownArchitecture`, and `installation.ManagedDxvkArchitecture` using `DxvkLocalReleaseLibrary.NormalizeArchitecture`. When `profile.Architecture` contains accepted aliases (`x86`, `win32`, `amd64`, `win64`), it is normalized to canonical `"x32"` or `"x64"`. This ensures `DxvkInstaller` selects the matching `"x32"` or `"x64"` directory, copies the correct architecture DLLs, and records matching architecture metadata.
   - **Regressions:**
     - `PerGameVersionSelectionTests.AcceptedArchitectureAliases_DeployCorrectDistinctArchitectureBytes_AndRecordMatchingArchitecture`: Theory testing `"x86"`, `"win32"`, `"amd64"`, and `"win64"` with distinct x32 and x64 bytes, proving that deployed DLL bytes and recorded architecture agree.
     - `PerGameVersionSelectionTests.AcceptedX32Alias_WhenOnlyX32PayloadExists_DeploysSuccessfully`: proves that when only an x32 payload exists, a profile with `"x86"` deploys x32 successfully instead of failing after claiming local availability.

3. **[P2 - 3] Authoritative installation version comparison:**
   - **Fix:** In `DxvkManager.UpdateAvailable`, resolved `currentVersion = (installation != null && !string.IsNullOrWhiteSpace(installation.ManagedDxvkVersion)) ? installation.ManagedDxvkVersion.Trim() : profile.DxvkVersion`. Pinned and latest version comparisons are now made against the active recorded installation version when present, falling back to historical profile for legacy/untracked profiles. In `UpdateAllEnabledAsync`, `enabledProfiles` checks both `p.DxvkEnabled` and actively managed installations (`installation.RestorationState == RestorationState.Managed`), ensuring games with stale profile flags are appropriately updated or skipped.
   - **Regressions:**
     - `PerGameVersionSelectionTests.StaleProfile_Direction1_ManagedV24_ProfileV23_CorrectlySkipped`: sets up a real synthetic managed install with `ManagedDxvkVersion = "v2.4"` and stale `profile.DxvkVersion = "v2.3"`. With target pinned to v2.4, asserts `UpdateAvailable` is false, `UpdateAllEnabledAsync` performs 0 updates, zero HTTP requests are issued, and deployed DLLs and baseline backup are preserved.
     - `PerGameVersionSelectionTests.StaleProfile_Direction2_ManagedV23_ProfileV24_CorrectlyUpdated`: sets up a real synthetic managed install with `ManagedDxvkVersion = "v2.3"` and stale `profile.DxvkVersion = "v2.4"`. With target pinned to v2.4, asserts `UpdateAvailable` is true, `UpdateAllEnabledAsync` updates to v2.4, zero HTTP requests are issued, deployed DLLs and `ManagedDxvkVersion` are updated to v2.4, and subsequent rollback restores original baseline bytes cleanly.

---

### 5. Codex Delta Review P2 Repair (Test-Only)

Codex reviewed PR #164 at head `b4f9e4a8e48a92935ed1d3a1c4eedfb15326b013` (run `39090838491`), accepting all three original production repairs. One P2 test-only finding was raised:
- **Finding:** In `DxvkLocalReleaseLibraryTests.cs` (lines 250, 298) and `PerGameVersionSelectionTests.cs` (line 514), tests returned early when `TryCreateDirectoryLink` failed (`if (!linkCreated) return;`). Under Windows CI runners, swallowed exceptions or failure return codes produced `Passed` rather than a setup failure, leaving junction execution unproven in TRX.
- **Repair:**
  - Updated `TryCreateDirectoryLink(string linkPath, string targetPath, out string? diagnostics)` across both test suites to capture stdout, stderr, exit code, and exceptions when creating junctions via `cmd.exe /c mklink /J`, and to assert that the created directory has `FileAttributes.ReparsePoint`.
  - Replaced early `return;` with:
    - `Assert.True(linkCreated, $"... Diagnostics: {diag}")`
    - `Assert.True(Directory.Exists(archLink), ...)`
    - `Assert.True((linkInfo.Attributes & FileAttributes.ReparsePoint) != 0, ...)`
  - Retained all existing negative/positive behavior assertions and fixture cleanup in `finally` blocks.
  - No production code, dependency, or workflow modifications.
