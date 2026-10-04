# Session Record: 2026-10-04 — Issue #18 GraphicsApi Persistence & Downgrade Coverage

- **Date / Timestamp**: 2026-10-04 21:35:00 UTC
- **Agent Role & Model**: Gemini (Implementer)
- **Task / Issue**: Issue #18 — F9: GraphicsApi persistence round-trips and downgrade behavior
- **Starting Head**: `56808e56429026227c99525a682e0931c2c2e7a8` (`origin/main`)
- **Branch / Worktree**: `test/issue-18-graphicsapi-persistence-downgrade` (`D:\dev\DXVK-Companion-issue-18`)

### Purpose & Startup Recovery
Initiated session from refreshed default branch after PR #29 merge. Verified live dashboard in Issue #27 (0 open PRs, 4 open task issues). Scoped strictly to Issue #18 acceptance criteria. Completed focused revision per ChatGPT coordinator review.

### Actions & Implementation
1. **Source Inspection Evidence**:
   - `ProfileStore` serializes `GameProfile.Api` using default `JsonSerializerOptions` (no `JsonStringEnumConverter`), producing integer ordinals: `Unknown=0`, `DX9=1`, `DX10=2`, `DX11=3`, `ModernAPI=4`, `DX12=5`, `Vulkan=6`.
   - `GameLibraryStore` serializes `ExecutableProfile.LastKnownApi` using `JsonStringEnumConverter`, producing case-insensitive string names.
   - Revisions prior to PR #9 (e.g. `230c8ae`) lacked `DX12` and `Vulkan` members.
2. **Executed Verification Evidence**:
   - Created `GraphicsApiPersistenceAndDowngradeTests.cs` (24 test cases across facts and theories):
     - Executed round-trip theories for all 7 `GraphicsApi` enum members in both `ProfileStore` and `GameLibraryStore`.
     - Executed ordinal stability tests pinning exact numeric literals (`0..6`) in serialized JSON.
     - Executed name-based serialization tests pinning string names in `game-library.json`.
     - Executed legacy `"ModernAPI"` loading test for direct `game-library.json` loading. (Omitted legacy profile migration test per approved clean-slate V1 policy; recorded existing migration code in `GameLibraryStore` as implementation/spec drift for separate cleanup).
     - Executed downgrade simulation: deserializing unrecognized string enums (`DX12`/`Vulkan`) with older enum stubs throws `JsonException`; `GameLibraryStore` treats payload as `LoadResult.Invalid` and preserves `.recovery.*.json` without data corruption.
     - Executed ordinal downgrade simulation: `ProfileStore` deserializes unknown integer ordinals as cast enum values without exception (noting this does not guarantee safe application behavior across older builds).
   - Cleaned up pre-existing `bin` test directory pollution in `AutomatedModeAndRestoreAllTests.cs` by ensuring explicit synthetic path isolation.
   - Application tests: 215/215 passed. Workflow tests: 92/92 passed.
3. **Documentation**:
   - Documented persistence mechanics and downgrade behavior in `docs/spec/DXVK-COMPANION-SPEC.md` §8.1 and `README.md`.

### Observable Measurements
- Human decision/action interventions: 1 (task assignment)
- Human status queries: 0
- Repeated investigations from missing context: 0
- Session blocked: No
- Journal word count: ~310 words

### Outcome & Next Steps
- Result: Bounded revision complete; 215 tests passed; attributed review preserved; ready for merge decision.
- Next Action & Owner: Push commit to PR #30, update PR body with revised evidence (preserving review record); ChatGPT confirmation; human merge.
