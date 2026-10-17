# AI Engineering Journal: Acknowledge Queued Reapply in Game Details

**Date**: 2026-10-17  
**Session**: Gemini Session 1  
**Primary Issue**: [#143](https://github.com/Tiflit/DXVK-Companion/issues/143)  
**Base Revision**: `2da0df9e177af08832b188fef5b427c9a251e4c2` (`origin/main`)  

---

### 1. Objective & Defect Context

Fix `GameDetailsWindow`'s Reapply DXVK handler to distinguish between `Applied`, `Queued`, and `Failed` outcomes:
- When a managed game is currently running, `RequestReapplyByPathAsync` legitimately records a pending reapply action and returns `DxvkActionResult.Queued`.
- Previously, `GameDetailsWindow` checked only `if (res == DxvkActionResult.Applied)`, treating `Queued` as failure and presenting "DXVK reapply was refused or failed" despite successful queueing.
- The user was misled into believing their reapply request failed or was refused, and the details window remained open rather than closing to allow the parent list to refresh and show pending state.

---

### 2. Implementation

1. **`src/DXVKCompanion/UI/ReapplyResultPresentation.cs`**:
   - Implemented pure presentation record `ReapplyPresentation` with properties `Message`, `Title`, `Icon`, and `ShouldCloseWindow`.
   - Implemented static mapping helper `ReapplyResultPresentation.GetPresentation(DxvkActionResult result, string? refusalReason)`:
     - **`Applied`**: Retains existing success explanation ("DXVK reapplied successfully and baseline updated."), title "Reapply", `MessageBoxIcon.Information`, and `ShouldCloseWindow: true`. Stale refusal strings are not leaked into the message or title.
     - **`Queued`**: Displays an informational acknowledgement ("DXVK reapply is queued and will be applied when the game exits."), title "Reapply Queued", `MessageBoxIcon.Information`, and `ShouldCloseWindow: true`. It does not claim reapply or baseline update has completed, nor does it describe queueing as refusal or failure.
     - **`Failed`**: Displays warning ("DXVK reapply was refused or failed:\n\n{reason}"), title "Reapply Refused", `MessageBoxIcon.Warning`, and `ShouldCloseWindow: false`. Uses `refusalReason` when non-blank and falls back to "Reapply operation failed." when null or whitespace.
     - **Unknown result**: Fails closed via default branch to warning dialog with `ShouldCloseWindow: false`, preventing unknown codes from claiming success or queueing.

2. **`src/DXVKCompanion/UI/GameDetailsWindow.cs`**:
   - Updated the Reapply click handler to consume `ReapplyResultPresentation.GetPresentation(res, _dxvk.LastRefusalReason)`.
   - Displays `MessageBox.Show(presentation.Message, presentation.Title, MessageBoxButtons.OK, presentation.Icon)`.
   - Closes the window if `presentation.ShouldCloseWindow` is true, triggering the parent `ManageGamesWindow` refresh.
   - Preserved `RequestReapplyByPathAsync(_profile, updateBaseline: true)` call and all baseline safety invariants.

3. **`tests/DXVKCompanion.PhaseA.Tests/ReapplyResultPresentationTests.cs`**:
   - Comprehensive headless xUnit test suite covering:
     - `Applied` with null refusal reason (verifies title, message, information icon, close window).
     - `Applied` with stale refusal reason (verifies reason does not leak).
     - `Queued` with null refusal reason (verifies title, message, information icon, close window, no false completion or failure claims).
     - `Queued` with stale refusal reason (verifies reason does not leak).
     - `Failed` with non-blank refusal reason (verifies title, formatted message with reason, warning icon, keep window open).
     - `Failed` with missing, empty, or whitespace refusal reason (verifies fallback reason).
     - Unknown enum value (verifies failure fallback, warning icon, keep window open, no success/queued claim).

---

### 3. Verification & Governance

- Local host execution: `NOT RUN (pending human authorization)` under AGENTS.md.
- Verified via GitHub Actions Cloud CI on Windows runners (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`, `Run Python workflow tests`).
