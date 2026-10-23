# Portable managed-state lifecycle acceptance — #284 / #286

Service-level acceptance coverage for the specification's portable-folder move requirement (§47 Phase F, item 59). This tests persisted service and storage portability, verifying that the application's persisted stores, leases, configuration, relative backups, and shared source library function correctly across filesystem relocation while game installations remain in place.

## Scope and model

Production-default `AppContext.BaseDirectory` movement is not simulated by injected paths and is not claimed. The test suite exercises the storage, transaction, lease coordination, and release management layers by reconstructing all services against relocated bundle paths under an isolated `SyntheticTestDirectory`:
- App-data bundle contains `Profiles` (`game-library.json`, `games.json`, relative `Backups/`), `Cache` (`release-cache.json`), `settings.json`, and shared `Sources/` (acquired DXVK releases).
- Synthetic game directory remains in place with its native executable, original DLLs, configuration, and a meaningful empty subdirectory (`Data/EmptySubDir`).
- Synthetic payloads are never executed or loaded; releases and digests represent synthetic official-shaped metadata and archive validation rather than independent upstream authenticity.
- Normal `DxvkManager`, `GameLibraryStore`, `ProfileStore`, `MultiFileTransactionEngine`, `DxvkInstaller`, and `DxvkRollback` entry points are used with real leases configured against a private synthetic `OperationLeaseCoordinator`.
- Process activity inspection uses an explicitly inactive synthetic process universe.
- Every restart/reload checkpoint reconstructs the full stack of services (cache, GitHub client, profile store, library store, transaction engine, installer, rollback, and manager) against destination paths; no surviving service instances are reused.

## Acceptance scenarios

1. **Acquisition and baseline deployment**: Acquires two distinguishable releases (`v2.5` and `v2.6`) into the shared source library via the real acquirer with fake HTTP transport and verified archive digests. Tests both preexisting and clean-slate/absent baseline variants:
   - **Preexisting baseline**: Preexisting native `d3d11.dll` and `dxvk.conf` with distinct user comments, settings, and prefix lookalike keys (`dxvk.hudExtra = full`, `dxvk.maxFrameRateTarget = 120`). `dxgi.dll` is absent.
   - **Originally absent baseline**: Clean-slate game with only `Game.exe` and an empty subdirectory; neither `d3d11.dll`, `dxgi.dll`, nor `dxvk.conf` exist originally.
   - Persists nondefault `SettingsStore` (`GlobalPolicy = Automated`) and `CacheStore` records before relocation.
   - Deploys the older release with HUD and frame limiting enabled. Verifies relative backup paths, exact original backup bytes, installation ID, configuration, and source manifest/marker bytes. Sets and persists explicit version selector intent (`ManagementPolicy.PinVersion("v2.6")`).
2. **Bundle relocation and service reconstruction**: With no operation in flight, physically moves the app-data bundle to a distinct path under the test directory so the old bundle path is unavailable. Reconstructs all services using the relocated destination paths without mutating persisted JSON. Verifies no old-path recreation or dependency, coherent persisted state/selection, and unchanged source and backup bytes. Confirms nondefault settings and cached release persistence.
3. **Offline update, configuration disable, and restore lifecycle**: Marks fake transport unavailable and asserts zero additional HTTP calls across the moved lifecycle. Updates through the manager relying on the persisted selector intent without targetVersion method overrides. Restarts/reloads all services, disables HUD and frame limiting, and reapplies through the manager; parses exact key assignments to verify `dxvk.hud` and `dxvk.maxFrameRate` are removed while lookalike prefix keys (`dxvk.hudExtra`, `dxvk.maxFrameRateTarget`) and unrelated user configuration lines are preserved, with ownership hash verified against the on-disk config. Restarts/reloads all services and executes a full restore through the manager; asserts complete resource set restoration (including files, raw bytes, and empty subdirectories), absence of non-original wrappers, cleared active managed versions across installation and per-file records, truthful historical fields, and byte-identical preservation of acquired sources and original backups throughout.
4. **Relocated corrupted or missing backup refusal**: A separate theory test verifies that when an original backup in the relocated bundle is deleted or corrupted, restore is safely refused without partial game-target writes, false Restored status, or false profile disabling, truthfully setting `AttentionRequired` and retaining active versions and available bytes.

## Execution and boundaries

Tests are executed exclusively via hosted Windows cloud CI. No local host restore, build, test, helper, or device execution is authorized. Reported local script diagnostic deviations during session 1 did not involve host application suites. All fault injection is confined to disposable synthetic test fixtures; no production code was modified as no path defects were demonstrated within the actually exercised scenarios.
