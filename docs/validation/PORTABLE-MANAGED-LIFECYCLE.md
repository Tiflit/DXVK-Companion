# Portable managed-state lifecycle acceptance — #284

Service-level acceptance coverage for the specification's portable-folder move requirement (§47 Phase F, item 59). This tests persisted service and storage portability, verifying that the application's persisted stores, leases, configuration, relative backups, and shared source library function correctly across filesystem relocation while game installations remain in place.

## Scope and model

Production-default `AppContext.BaseDirectory` movement is not simulated by injected paths and is not claimed. The test suite exercises the storage, transaction, lease coordination, and release management layers by reconstructing all services against relocated bundle paths under an isolated `SyntheticTestDirectory`:
- App-data bundle contains `Profiles` (`game-library.json`, `games.json`, relative `Backups/`), `Cache` (`release-cache.json`), and shared `Sources/` (acquired DXVK releases).
- Synthetic game directory remains in place with its native executable, original DLLs, and configuration.
- Synthetic payloads are never executed or loaded.
- Normal `DxvkManager`, `GameLibraryStore`, `ProfileStore`, `MultiFileTransactionEngine`, `DxvkInstaller`, and `DxvkRollback` entry points are used with real leases configured against a private synthetic `OperationLeaseCoordinator`.
- Process activity inspection uses an explicitly inactive synthetic process universe.

## Acceptance scenarios

1. **Acquisition and baseline deployment**: Acquires two distinguishable official releases (`v2.5` and `v2.6`) into the shared source library via the real acquirer with fake HTTP transport and verified archive digests. Installs the older release into a DX11/x64 game with HUD and frame limiter enabled, covering both preexisting native files (`d3d11.dll`, `dxvk.conf`) and an originally absent file (`dxgi.dll`). Verifies relative backup paths, exact original backup bytes, installation ID, configuration, and source manifest/marker bytes.
2. **Bundle relocation and service reconstruction**: With no operation in flight, physically moves the app-data bundle to a distinct path under the test directory so the old bundle path is unavailable. Reconstructs all services using the relocated destination paths without mutating persisted JSON. Verifies no old-path recreation or dependency, coherent persisted state/selection, and unchanged source and backup bytes.
3. **Offline update, configuration disable, and restore lifecycle**: Marks fake transport unavailable and asserts zero additional HTTP calls across the moved lifecycle. Updates through the manager to the already acquired newer release (`v2.6`). Restarts/reloads, disables the last HUD/frame feature, and reapplies through the manager; asserts exact-key configuration removal in `dxvk.conf` while preserving unrelated user lines and prefix lookalikes, with the original baseline and backup untouched. Restarts/reloads and executes a full restore through the manager; asserts byte-identical restoration of original game files and absence of non-original wrappers, cleared active managed version, truthful historical fields, and byte-identical preservation of acquired sources and original backups throughout.
4. **Relocated corrupted or missing backup refusal**: A separate test verifies that when an original backup in the relocated bundle is deleted or corrupted, restore is safely refused without partial game-target writes, false Restored status, or false profile disabling, truthfully setting `AttentionRequired` and retaining active versions and available bytes.

## Execution and boundaries

Tests are executed exclusively via hosted Windows cloud CI. No local host restore, build, test, helper, or device execution was authorized or run. All fault injection is confined to disposable synthetic test fixtures; no production code was modified as no path defects were demonstrated.
