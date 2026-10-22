# Module evidence integrity — #238

## Detection repair

Canonical specification section 7.1 names `d3d10core.dll` as runtime evidence. The classifier already recognized it, but the real scanner's tracked list omitted it. Tests that replaced the scanner output could conceal this gap. The scanner now retains it, and `UsesDx10` recognizes either `d3d10.dll` or `d3d10core.dll` using one scan.

Tracked evidence now requires an exact case-insensitive module basename. A name such as `notd3d12.dll` or `vulkan-1.dll.bak` no longer invents a native API observation or suppresses static fallback. Actual enumeration supplies `ProcessModule.ModuleName`, not an arbitrary full path. Both the controlled enumeration seam and normal enumeration use the same matching rule. Canonical lowercase names remain the output. Partial results survive enumeration failure; confidence and fallback rules remain unchanged.

D3D10 diagnostic evidence now names the actual runtime modules or static imports observed, recording DX10 once when both forms exist. Fixed precedence remains DX12 > Vulkan > DX11 > DX10 > DX9. This does not distinguish a DXVK-translated Vulkan process from native Vulkan or authorize deployment into either one. No new OS query, timer, API, schema, setting or dependency.

The 36 added cases exercise the real scanner filter and classifier with synthetic enumeration: all tracked names, casing/duplicates/single enumeration, lookalikes, core-only recognition, modern precedence, partial failure, static fallback, `UsesDx10`, and accurate runtime/static evidence. Controlled PE evidence is explicitly synthetic; no process module inventory is collected by these fixtures. Initial verification is pending cloud CI; final revision-bound counts/provenance belong in the PR completion record. No local execution or hardware/rendering acceptance.

## D3D10 deployment investigation remains separate

Inspected upstream primary sources:

- [v3.1.1 README](https://github.com/doitsujin/dxvk/blob/v3.1.1/README.md#dll-dependencies): its D3D10 dependency list includes `d3d10core.dll`, `d3d11.dll`, and `dxgi.dll`. Instructions concern Wine; they do not establish every Windows game's loading behavior.
- [v3.1.1 D3D10 build definition](https://github.com/doitsujin/dxvk/blob/v3.1.1/src/d3d10/meson.build) and [core implementation](https://github.com/doitsujin/dxvk/blob/v3.1.1/src/d3d10/d3d10_core.cpp): the core library is installed and links to D3D11; device creation delegates to the D3D11 core and obtains D3D10 interfaces. Source inspection is not executed Windows loading evidence.
- [v2.4 README](https://github.com/doitsujin/dxvk/blob/v2.4/README.md#how-to-use): installation guidance also includes the core override, but lacks the newer per-API dependency table. This alone is not a complete older-release compatibility matrix.
- [Upstream Windows guidance](https://github.com/doitsujin/dxvk/wiki/Windows): Windows use is not officially supported; game-specific DLL loading can bypass local files. Do not replace system-directory DLLs. This page is mutable and is guidance, not a pinned execution result.
- [v3.1.1 license](https://github.com/doitsujin/dxvk/blob/v3.1.1/LICENSE): zlib/libpng terms permit use and redistribution subject to origin, altered-source and notice conditions. This repair copies no upstream implementation and redistributes no upstream binaries. Future packaging must retain applicable notices and origin; do not treat a dependency list as a license grant for unrelated tools.

Current Companion acquisition already recognizes/extracts `d3d10core.dll`; the older #167 claim that extraction omits it is superseded by the shared acquirer implementation. However, `DxvkCompatibility.GetRequiredDlls(DX10)` still selects only `d3d11.dll` and `dxgi.dll`. Canonical section 13 deliberately leaves the D3D10 deployment set pending verification, while the safety supplement lists three files. This PR preserves that unresolved distinction and does not change payload mapping, source eligibility, ownership, restore or recognition-by-hash rules.

Next separate task: establish a version-scoped official package inventory and bounded Windows load evidence, then settle mapping, missing-file refusal, legacy-source treatment and recognition/restore regressions together. Any new executable helper/device trial needs its own execution authorization. No real-game loading, GPU performance or claim that every DX10 title is broken follows from this inspection.
