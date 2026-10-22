# Unknown target architecture

Graphics API evidence and payload architecture are separate. A loaded D3D module
or static import can identify an API without establishing x32 versus x64. When
the PE parser cannot supply architecture, the classifier retains **Unknown**;
it does not substitute Windows or Companion process bitness. Existing deployment
refusal for unknown architecture remains active, even with a local x64 payload.

[#234](https://github.com/Tiflit/DXVK-Companion/issues/234) removes the placeholder
fallback that previously labeled unavailable architecture x64 on a 64-bit OS.
Known PE-reported x32/x64 and existing API/evidence precedence are unchanged. No
new process query, user-selectable override, dependency or format is introduced.

Cloud tests use the real architecture parser with controlled module/import
evidence and synthetic, never-executed header/data files. Missing, non-PE,
truncated, unsupported-machine and exclusively locked cases preserve Unknown;
known declared x32/x64 remain distinct. Classification-to-installer cases require
refusal before HTTP/target mutation despite complete-looking local payloads for
both architectures, preserving target/source/library bytes and file sets.

These tests do not validate every malformed PE layout or authenticate executable
provenance. The parser's other structural-validation limits remain; no runtime
bitness fallback or ARM support is added. Previously stored architecture is not
retroactively audited or purged. Mixed Vulkan/D3D classification, late modules,
host discovery, real-game/GPU acceptance and the qualified ended packaged profile
remain separate work. No host run or repair of personal game records is assigned.
