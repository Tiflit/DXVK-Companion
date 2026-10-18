# AI Engineering Journal: Daily DXVK Release Checks with Persisted Retry Throttle and Offline Fallback

**Date**: 2026-10-17  
**Session**: Gemini Session 1  
**Primary Issue**: [#161](https://github.com/Tiflit/DXVK-Companion/issues/161)  
**Base Revision**: `ff4f02c537955b5e7fd2d417a59d5aa3ba438e9c` (`origin/main`, merged #160)  

---

### 1. Objective & Scope

Issue [#161](https://github.com/Tiflit/DXVK-Companion/issues/161) implements the daily latest-release metadata checking portion of [#159](https://github.com/Tiflit/DXVK-Companion/issues/159) while preserving the shared downloaded DXVK release library, offline deployment, and retention guarantees.
- **Problem Statement**:
  - Previously, DXVK Companion checked for latest releases only upon startup or on game detection/access, and cached successful metadata for 24h.
  - It had no continuous daily background schedule while the app remained running, no shared concurrent request gate across startup, background scheduler, detection, and manager callers, and no persisted failure-attempt throttle (meaning failed attempts offline could cause retry storms on restarts or repeated game actions).
- **Required Behavior**:
  1. Perform an automatic latest-release metadata check when due on application startup and once per 24h while the app remains running without separate Windows tasks/services. App close stops and disposes the schedule and cancels owned work without unobserved exceptions or callbacks into disposed UI.
  2. Share the latest-check decision between startup, periodic schedule, detection, and manager callers via a shared inflight gate (`SemaphoreSlim(1, 1)`). At most one metadata HTTP operation is in flight. Re-evaluate persisted state after acquiring the gate.
  3. Count any request attempt (including HTTP/network/timeout/invalid metadata failure) for the automatic 24h throttle, preventing retry storms offline.
  4. Persist last-attempt time separately from successful release validation time (`CachedAt`). Failed requests must not make stale metadata appear newly verified, discard previous release/ETag, remove local DLLs, or invent a new latest version. Preserve a failure attempt record even when no usable release has yet been obtained. On offline failure, return previously known valid metadata when available.
  5. Keep existing cache JSON backward compatible: legacy `Release`/`CachedAt`/`ETag` files without `LastAttemptAt` load cleanly and infer due behavior from `CachedAt`. Preserve `CacheStore`'s operation-owned staged-save/replace/failure retention behavior without direct truncating writes or overwriting recoverable files after parse failures. Reject malformed/empty metadata as successful latest data.
  6. Daily checks are metadata/notification work only; they never download archives, deploy/update games, alter explicit version choices, reset ownership, or delete downloaded releases or backups.
  7. Use existing UI context for update notifications with no repeated balloon storm for the same timer tick.
- **Allowed Paths**:
  - `src/DXVKCompanion/Models/CachedRelease.cs`
  - `src/DXVKCompanion/Storage/CacheStore.cs`
  - `src/DXVKCompanion/DXVK/DxvkGithubClient.cs`
  - `src/DXVKCompanion/DXVK/DxvkReleaseCheckScheduler.cs`
  - `src/DXVKCompanion/UI/TrayApp.cs`
  - `src/DXVKCompanion/Program.cs`
  - `tests/DXVKCompanion.PhaseA.Tests/DxvkGithubClientTests.cs`
  - `tests/DXVKCompanion.PhaseA.Tests/CacheStorePersistenceTests.cs`
  - `tests/DXVKCompanion.PhaseA.Tests/DailyReleaseCheckTests.cs`
  - `docs/ai-journal/2026-10-17-daily-dxvk-release-check-gemini-session-1.md`

---

### 2. Implementation

1. **`CachedRelease.cs`**:
   - Added `LastAttemptAt` (`DateTime?`) representing the timestamp of the latest check attempt (successful or failed).
   - Added `EffectiveLastAttempt` (`DateTime`) property: returns `LastAttemptAt` when set and valid; otherwise infers due behavior from `CachedAt` (supporting legacy cache files).
   - Added `HasValidRelease` (`bool`) property: validates `Release != null && !string.IsNullOrWhiteSpace(Release.Version) && !string.Equals(Release.Version, "unknown", StringComparison.OrdinalIgnoreCase)`.
   - Added `IsDue(DateTime utcNow)` method: evaluates whether `(utcNow - EffectiveLastAttempt) >= TimeSpan.FromHours(24)`.
   - Preserved `IsExpired(DateTime utcNow)` based on `CachedAt` for 24h cache TTL.

2. **`CacheStore.cs`**:
   - Refactored staged write commit into private helper `CommitStagedJson(string json)`, ensuring single-file replacement, operation-owned unique GUID temp files, and non-masking cleanup on failure.
   - Extended `SaveCachedRelease(ReleaseInfo release, string? etag, DateTime? cachedAt = null, DateTime? lastAttemptAt = null)`: preserves backward compatibility while allowing callers/tests to supply explicit validation and attempt timestamps.
   - Added `RecordFailedAttempt(DateTime? attemptAt = null)`: updates `LastAttemptAt` to the failed attempt timestamp while preserving existing `Release`, `CachedAt`, and `ETag`. If no cache exists, saves an empty release record with `LastAttemptAt`. If a corrupt/unparseable cache file exists on disk, avoids overwriting the recoverable file.

3. **`DxvkGithubClient.cs`**:
   - Added `SemaphoreSlim _gate = new(1, 1)` to enforce that at most one metadata HTTP request is in flight in the shared client.
   - Fast path before gate: if cache exists and is not due (`!cached.IsDue(now)`), returns cached valid release or null (if throttled failure) immediately without locking.
   - Gate acquisition: waits for `_gate.WaitAsync(cancellationToken)`. Upon acquisition, re-evaluates persisted state: if another concurrent caller already completed the check and satisfied due conditions, returns immediately without issuing duplicate network calls.
   - Added testable clock seam `public Func<DateTime> UtcNowSeam { get; set; } = () => DateTime.UtcNow;`.
   - Validates JSON response structure: if `tag_name` or asset download URL is empty/missing, treats response as invalid and records a failed attempt.
   - On 200 OK: saves release with `CachedAt = now` and `LastAttemptAt = now`.
   - On 304 Not Modified: refreshes `CachedAt = now` and `LastAttemptAt = now` while preserving release payload.
   - On exception (timeout, network drop, HTTP 500, invalid metadata): logs error, records failed attempt (`_cacheStore.RecordFailedAttempt(now)`), and falls back to prior known valid release if available.

4. **`DxvkReleaseCheckScheduler.cs`**:
   - Designed a periodic due-check schedule running on a background timer (default 1 hour interval).
   - `TriggerCheckAsync`: uses a non-blocking `_tickLock.WaitAsync(0)` gate to prevent overlapping ticks within the scheduler instance, invokes the check action, and forwards results to `_onUpdateChecked` callback.
   - `IDisposable`: cancels internal `CancellationTokenSource`, disposes timer and semaphores, and suppresses callbacks during or after disposal.

5. **`TrayApp.cs` & `Program.cs`**:
   - Implemented `IDisposable` on `TrayApp`.
   - Instantiated `DxvkReleaseCheckScheduler` wired to `_dxvk.GetLatestReleaseAsync()` and `NotifyIfDxvkUpdateAvailableAsync`.
   - `NotifyIfDxvkUpdateAvailableAsync`: tracks `_lastNotifiedDxvkVersion`; only shows a balloon tip if a genuinely newer release version is available and has not already been notified, preventing notification spam across timer ticks.
   - Wired `trayApp.Dispose()` to `Application.ApplicationExit` and scope exit in `Program.cs`.

6. **Test Extensions**:
   - `CacheStorePersistenceTests.cs`: added 4 new unit tests for `RecordFailedAttempt` (with and without prior cache), legacy JSON loading without `LastAttemptAt`, and corrupt file non-overwriting.
   - `DxvkGithubClientTests.cs`: aligned synthetic test cache expirations with attempt timestamps.
   - `DailyReleaseCheckTests.cs`: created 12 comprehensive unit tests validating:
     1. Fresh cache zero network calls.
     2. Initial due fetch and timestamp persistence.
     3. Exact 24-hour boundary (before vs. at due time).
     4. Concurrency gate sharing a single HTTP call across 10 concurrent tasks.
     5. 200 OK and 304 Not Modified timestamp refresh.
     6. Failure handling preserving valid release and ETag while recording attempt.
     7. Store/client reconstruction after failed check with prior metadata (24h throttle).
     8. Store/client reconstruction after failed check without prior metadata (no bogus release).
     9. Legacy JSON backward compatibility inferring due behavior from `CachedAt`.
     10. Offline deployment succeeding after due metadata failure and subsequent restarted access not rechecking.
     11. Scheduler stop and disposal cancellation without unobserved exceptions or callbacks.
     12. Scheduler periodic due check avoiding duplicate notification spam.

---

### 3. Verification & CI Status

- Verified exact base: `ff4f02c537955b5e7fd2d417a59d5aa3ba438e9c` (`origin/main`).
- Verified policy checkpoint: `f3e9974371cb08d1dd99192bd314a135010594ed`.
- Strictly 10 authorized paths modified/created.
- Cloud CI runs via GitHub Actions Windows runners.
