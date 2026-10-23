# Portable managed-state lifecycle acceptance — Gemini, 2026-10-23

Task [#284](https://github.com/Tiflit/DXVK-Companion/issues/284), base main `46e6f22363cddcfa88d805b588b6ac30173bc9dc`, policy `c40eb13d0d89cb4860b4ed50fd81561acf2fb0d9`. Attributed coordinator transcription of owner instruction: "My Antigravity quotas have reset. We can task Gemini with implementation again. Continue." Owner prioritizes complete standalone DXVK functionality/tests and defers UI polish.

Implemented service-level acceptance coverage in `tests/DXVKCompanion.PhaseA.Tests/PortableManagedLifecycleAcceptanceTests.cs` for the specification's portable-folder move requirement (§47 Phase F, item 59). Created documentation `docs/validation/PORTABLE-MANAGED-LIFECYCLE.md`.

The suite tests persisted service/storage portability by reconstructing services against an app-data bundle relocated within an isolated `SyntheticTestDirectory` while synthetic games stay in place:
1. Synthetic app-data bundle with `Profiles` (`game-library.json`, `games.json`, relative `Backups/`), `Cache`, and shared `Sources/`. Acquired distinguishable official releases (`v2.5` and `v2.6`) via the real acquirer with fake HTTP transport and verified archive digests.
2. Initial deployment into a DX11/x64 game with HUD and frame limiting enabled, covering native DLL/config baselines and an originally absent file (`dxgi.dll`).
3. Bundle relocation to a distinct path under the test directory with no operation in flight, verifying no old-path recreation/dependency, coherent persisted identity/selection/settings, and unchanged source/backup bytes.
4. Transport marked unavailable, asserting zero additional HTTP calls across the moved lifecycle. Updated through the manager to `v2.6`. Restarted/reloaded, disabled the last HUD/frame feature, and reapplied, verifying exact-key removal in `dxvk.conf` while preserving unrelated user lines and prefix lookalikes. Restarted/reloaded and restored, verifying exact original bytes, absence of non-original wrappers, cleared active managed version, truthful historical fields, and byte-identical source and backup bytes throughout.
5. Theory-driven relocated corrupted and missing original backup test asserting safe restore refusal without partial target mutation or false Restored/disabled success, recording truthful `AttentionRequired` health.

Tests first policy: no production code defects were demonstrated by the new acceptance tests; production files remain untouched.

Grouped implementing-agent self-review covers approved scope, privacy, integration, and test effectiveness; not independent review. Source/link/whitespace inspection completed; hosted current-head compile/tests pending submission. No local host restore, build, test, app, helper, or device execution was authorized or run.
