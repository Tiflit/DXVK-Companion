# AI Engineering Journal: Show Per-Game Details in Restore All Results

**Date**: 2026-10-17  
**Session**: Gemini Session 1  
**Primary Issue**: [#141](https://github.com/Tiflit/DXVK-Companion/issues/141)  
**Base Revision**: `35e588194b031286be8f9640a384b3ce4ca37e4e` (`origin/main`)  

---

### 1. Objective & Defect Context

Fix the Restore All result presentation in Manage Games so users see game-specific refusal, exception, and running-game queued details alongside aggregate counts:
- `DxvkManager.RestoreAllAsync` records detailed per-game messages in `RestoreAllSummary.Messages`.
- `ManageGamesWindow.RunRestoreAll` previously discarded those messages, formatting only four aggregate outcome counts (Restored, Already clean, Queued, Attention required / failed).
- Users seeing failures or attention states could not identify which game failed or why without manually inspecting individual profiles.

---

### 2. Implementation

1. **`src/DXVKCompanion/UI/RestoreAllSummaryFormatter.cs`**:
   - Extracted a pure presentation formatter `RestoreAllSummaryFormatter` in `DXVKCompanion.UI`.
   - `Format(RestoreAllSummary? summary)`:
     - Preserves the existing completion heading and four outcome counts (Restored, Already clean, Queued running, Attention required / failed).
     - Filters `summary.Messages` for non-blank entries (`!string.IsNullOrWhiteSpace(m)`).
     - When non-blank messages exist, appends a clearly separated `\n\nDetails:\n` section containing every message in its original order.
     - When no non-blank messages exist (or messages contain only whitespace), omits the details heading and displays only the counts.
     - Preserves multiline message content verbatim without truncating or inventing success details.
     - Read-only: leaves summary counts and `Messages` list unmodified.
   - Provided `FormatSummary` alias forwarding to `Format`.

2. **`src/DXVKCompanion/UI/ManageGamesWindow.cs`**:
   - Updated `RunRestoreAll` to format the dialog message using `RestoreAllSummaryFormatter.Format(summary)`.
   - Preserved all other behavior: confirmation prompt, cancellation check, status label updates, `RefreshList()`, and warning-versus-information icon selection.

3. **`tests/DXVKCompanion.PhaseA.Tests/RestoreAllSummaryFormatterTests.cs`**:
   - Concise pure headless test suite covering:
     - Counts-only formatting when messages are empty (no details heading).
     - Counts-only formatting when messages contain only whitespace/empty strings.
     - Specific refusal message formatting with details section.
     - Mixed queued and failure messages preserving original order and exact content without conflation.
     - Multiline message content preservation.
     - Read-only preservation of summary counts and message list.
     - Null summary handling.
     - `FormatSummary` alias consistency.

---

### 3. Verification & Governance

- Local host execution: `NOT RUN (pending human authorization)` under AGENTS.md.
- Verified via GitHub Actions Cloud CI on Windows runners (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`, `Run Python workflow tests`).
