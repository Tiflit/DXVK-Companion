# AI Engineering Journal: Resolve Latest Release for First-Run Queued Enable

**Date**: 2026-10-17  
**Session**: Gemini Session 1  
**Primary Issue**: [#158](https://github.com/Tiflit/DXVK-Companion/issues/158)  
**Base Revision**: `fdd1ef452a23ffce864f6900917b75ea81075805` (`origin/main`)  

---

### 1. Objective & Scope

Issue [#158](https://github.com/Tiflit/DXVK-Companion/issues/158) addresses the cold-start defect where a newly added profile with no pre-downloaded DXVK files queues `PendingAction.Install("latest")` while a game is running.
- In `DxvkManager.cs`, `ApplyPendingAsync` and startup processing (`ProcessAllPendingActionsAsync`) forward the queued version token into `EnableDxvkAsync(profile, targetVersion)`.
- When `targetVersion` was `"latest"`, `EnableDxvkAsync` treated it as an explicit version token and built `new ReleaseInfo { Version = "latest", DownloadUrl = "" }` instead of resolving official release metadata from GitHub or cache via `GetLatestReleaseAsync()`.
- Consequently, `DxvkInstaller.ApplyToGameAsync` attempted to find cached files at `app\DXVK\latest\x64`, failed, and then attempted download with an empty URL, resulting in deployment failure on game exit and startup.
- In addition, per authoritative task criteria and [#158 discussion](https://github.com/Tiflit/DXVK-Companion/issues/158#issuecomment-6243055085):
  - Download official release archive once and reuse across games, restarts, and re-enables.
  - Deploy downloaded releases offline with zero network requests when cache and payload are present.
  - Fall back gracefully to local source payload when metadata refresh encounters network/503 errors.
  - Retain older downloaded releases and hashes during newer release operations, honoring explicit pinned versions.
- Strict 3-path scope:
  - `src/DXVKCompanion/DXVK/DxvkManager.cs`
  - `tests/DXVKCompanion.PhaseA.Tests/FirstRunQueuedEnableTests.cs`
  - `docs/ai-journal/2026-10-17-first-run-queued-enable-gemini-session-1.md`

---

### 2. Implementation

1. **Manager Repair (`src/DXVKCompanion/DXVK/DxvkManager.cs`)**:
   - In `EnableDxvkAsync(GameProfile profile, string? targetVersion = null)`:
     Updated condition to check:
     `if (!string.IsNullOrWhiteSpace(targetVersion) && !string.Equals(targetVersion, "latest", StringComparison.OrdinalIgnoreCase))`
     When `targetVersion` is null, empty, whitespace, or `"latest"` (case-insensitive sentinel), `EnableDxvkAsync` calls `GetLatestReleaseAsync()` to resolve official metadata and valid download URLs from cache or GitHub.
     When `targetVersion` is an explicit pinned version (e.g., `"2.4"`), it preserves exact version semantics without fetching latest metadata.
   - In `QueueOrApplyAsync`:
     When the game is not running, passed `profile.DxvkVersion` to `EnableDxvkAsync(profile, profile.DxvkVersion)`, matching the queued path's intent (`profile.DxvkVersion ?? "latest"`) so pinned profile versions are respected on immediate deployment.

2. **Regression Test Suite (`tests/DXVKCompanion.PhaseA.Tests/FirstRunQueuedEnableTests.cs`)**:
   - `FirstRunQueuedEnable_ApplyPendingAsync_ResolvesLatest_DownloadsOnce_AndDeploysToGame`:
     Tests live queue while running (using current test process as live reference), asserts zero mutation while queued and durable `Install("latest")` intent; executes `ApplyPendingAsync`, asserts official metadata URL queried, synthetic tar.gz downloaded exactly once, release directory created at concrete version `v3.1.1` (never literal `"latest"`), exact synthetic bytes deployed, profile enabled with `v3.1.1`, and pending action cleared.
   - `FirstRunQueuedEnable_ProcessAllPendingActionsAsync_PersistedStartup_ResolvesLatestAndDeploys`:
     Preseeds store with persisted `Install("latest")` intent, reconstructs fresh stores and manager without in-memory state, invokes `ProcessAllPendingActionsAsync()`, and asserts identical resolution, download, and deployment.
   - `FirstRunQueuedEnable_ExplicitPinnedVersion_UsesPreexistingLocalSource_ZeroHttpRequests`:
     Pre-populates version `2.4` in source directory, queues explicit version `"2.4"`, executes `ApplyPendingAsync` with a rejecting HTTP handler, and asserts deployment succeeds with zero network requests.
   - `FirstRunQueuedEnable_MetadataUnavailable_FailsWithoutMutatingTarget_RetainsRetryablePendingIntent`:
     Negative path with HTTP 500 error on metadata fetch; asserts failure without creating game DLLs, no fake `"latest"` source cache, and durable retention of retryable pending intent.
   - `FirstRunQueuedEnable_LocalLibrary_DownloadsArchiveOnlyOnceAcrossMultipleGamesAndReEnables`:
     Deploys to Game 1 (downloads archive once), then Game 2 (reuses local source without downloading), then disables and re-enables Game 1 (reuses local source); asserts total archive downloads remain strictly 1 across all operations.
   - `FirstRunQueuedEnable_LocalLibrary_FreshCacheAndPayload_DeploysOfflineWithZeroHttp`:
     Preloads cache metadata and payload in source tree, verifies offline deployment succeeds with strict zero-HTTP assertion.
   - `FirstRunQueuedEnable_LocalLibrary_StaleMetadataRefreshFailure_PermitsLocalDeploymentWithoutArchiveDownload`:
     Simulates expired cache with metadata service failure (HTTP 503); asserts local deployment still succeeds using existing payload with zero archive download requests.
   - `FirstRunQueuedEnable_LocalLibrary_RetainsOlderReleasePayloadsDuringNewerOperations`:
     Establishes older `2.4` release with recorded SHA256 hashes, performs new `v3.1.1` download and deployment to Game 1, asserts older release payload files and hashes remain untouched, and deploys pinned `2.4` to Game 2 without network requests.

---

### 3. Verification & CI Provenance

1. **Red Test Provenance**:
   - Commit `9974bd5` (`test(dxvk): reproduce first-run queued enable latest resolution failure (#158)`) pushed to `fix/first-run-queued-enable-158`.
   - GitHub Actions workflow run `39079670207` executed and demonstrated expected test failure on the unfixed manager:
     - `FirstRunQueuedEnable_ApplyPendingAsync_ResolvesLatest_DownloadsOnce_AndDeploysToGame` failed at line 166 (expected `True`, actual `False`).
     - `FirstRunQueuedEnable_ProcessAllPendingActionsAsync_PersistedStartup_ResolvesLatestAndDeploys` failed at line 265 (expected `1`, actual `0`).
     - `FirstRunQueuedEnable_LocalLibrary_RetainsOlderReleasePayloadsDuringNewerOperations` failed at line 636 due to unforwarded pinned version on immediate enable.
2. **Green Test Verification**:
   - Applied manager fix to `src/DXVKCompanion/DXVK/DxvkManager.cs`.
   - Verified via follow-up CI workflow run.
