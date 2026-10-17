# AI Engineering Journal: Show Recorded Pending Action in Game Details

**Date**: 2026-10-17  
**Session**: Gemini Session 1  
**Primary Issue**: [#147](https://github.com/Tiflit/DXVK-Companion/issues/147)  
**Base Revision**: `745e47fff79ffdbd531f6019ffb9801cdb883fdf` (`origin/main`)  

---

### 1. Objective & Defect Context

Manage Games shows a pending-action column, but Game Details previously lacked any display of the installation's recorded pending action. After acknowledging a queued reapply or restore action (or queueing an install/update), users reopening Game Details could not see that recorded intent.

Acceptance criteria:
- Display a clearly labelled recorded pending-action value: `None`, `Install`, `Update`, `Reapply`, or `Restore`, using explicit mappings of `PendingActionType`.
- An existing installation with no `PendingAction` object, or an action of type `None`, displays `None`. A missing installation record displays `Unavailable`.
- Preserve the distinction for an unrecorded profile whose installation object was created while opening Game Details: use existing `isPreexistingInstallation`.
- Unknown enum values display `Unknown`, rather than `None` or a recognized action.
- A historical `LastCancelledAction` must not appear as current pending work. Current pending work can coexist with cancellation history; display the current `PendingAction`.
- The label describes recorded intent only. Do not claim a game is running, an action has completed, or execution is guaranteed. Do not include arbitrary action reasons, target-version strings, timestamps or other record dumps.
- Mapping and rendering add no persistence, queue consumption/cancellation/retry, inspection, file/process/network access or state mutation. Preserve policy/configuration controls and all action behavior.
- Integrate the tested helper in `GameDetailsWindow`; adjust vertical layout minimally so following controls do not overlap the added line.
- Focused pure tests cover unavailable/preexisting distinction, absent action, None and each named action, positive/negative unknown values, cancellation-history separation and read-only behavior. No window/process/game execution or source-text tests.

---

### 2. Implementation

1. **`src/DXVKCompanion/UI/PendingActionPresentation.cs`**:
   - Implemented pure presentation helper `PendingActionPresentation` in `DXVKCompanion.UI`.
   - `GetRecordedPendingAction(GameInstallation? installation, bool isPreexisting = true)`:
     - Returns `"Unavailable"` if `installation == null` or `!isPreexisting`.
     - Returns `"None"` if `installation.PendingAction == null`.
     - Maps `installation.PendingAction.Type` via `FormatPendingActionType`.
     - Historical `LastCancelledAction` is intentionally excluded from current pending work.
   - `FormatPendingAction(PendingAction? action)`:
     - Returns `"None"` if `action == null`.
     - Otherwise returns `FormatPendingActionType(action.Type)`.
   - `FormatPendingActionType(PendingActionType type)`:
     - Explicitly maps known enum values:
       - `PendingActionType.None` -> `"None"`
       - `PendingActionType.Install` -> `"Install"`
       - `PendingActionType.Update` -> `"Update"`
       - `PendingActionType.Reapply` -> `"Reapply"`
       - `PendingActionType.Restore` -> `"Restore"`
       - Unrecognized/undefined values (`_`) -> `"Unknown"`
   - `FormatRecordedPendingActionLabel(GameInstallation? installation, bool isPreexisting = true)`:
     - Formats full label text: `"Recorded Pending Action: {actionText}"`.

2. **`src/DXVKCompanion/UI/GameDetailsWindow.cs`**:
   - Added read-only pending action label right below the version section:
     ```csharp
     var pendingActionLabel = new Label
     {
         Text = PendingActionPresentation.FormatRecordedPendingActionLabel(installation, isPreexistingInstallation),
         AutoSize = true,
         Top = top,
         Left = 20
     };
     Controls.Add(pendingActionLabel);
     top += 25;
     ```
   - Dynamically advances `top += 25` so following health status, refusal labels, panels, policy combos, and buttons shift downward without overlap.
   - Preserves unrecorded profile in-memory installation creation while respecting `isPreexistingInstallation`.

3. **`tests/DXVKCompanion.PhaseA.Tests/PendingActionPresentationTests.cs`**:
   - Added focused headless unit test suite covering:
     - `NullInstallation_ReturnsUnavailable`
     - `UnrecordedGame_NotPreexisting_ReturnsUnavailable`
     - `PreexistingInstallation_WithAbsentPendingAction_ReturnsNone`
     - `PreexistingInstallation_WithPendingActionTypeOfNone_ReturnsNone`
     - `PreexistingInstallation_WithNamedActionTypes_ReturnsExplicitMapping` (Theories for `Install`, `Update`, `Reapply`, `Restore`)
     - `FormatPendingActionType_MapsKnownEnumsDirectly`
     - `PositiveAndNegativeUnknownValues_ReturnUnknown` (Theories for `99`, `1000`, `-1`, `-999`)
     - `HistoricalLastCancelledAction_MustNotAppearAsCurrentPendingWork`
     - `DoesNotLeakTargetVersionReasonOrTimestamp`
     - `PresentationIsReadOnly_LeavesInstallationAndActionsUnmodified`

---

### 3. Verification & Governance

- Local host execution: `NOT RUN (pending human authorization)` under AGENTS.md.
- Verified via GitHub Actions Cloud CI on Windows runners (`build-and-test`, `ai-scope-check`, `ai-pr-hygiene`, `Run Python workflow tests`).
