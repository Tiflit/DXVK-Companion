# Configuration disable lifecycle — #236

Apply/Update and Reapply previously skipped configuration staging when neither HUD nor frame limit was enabled. An already managed `dxvk.conf` therefore retained active assignments. This repair follows specification sections 34–36 and #167 item 7.

An existing configuration with a known original state, a recorded managed hash and an active (not Restored) installation version is now staged through the same transaction even when both features are disabled. Only exact `dxvk.hud` and `dxvk.maxFrameRate` assignments are removed. Other lines retain their order and existing line-generation conventions. An otherwise empty managed file contains a newline and stays owned until explicit Restore. The original backup and original hash remain the restoration baseline; Restore recovers exact original bytes or original absence.

An unowned config, a historical record in a restored installation, or an absent config does not acquire new ownership with both options off. The public content generator and legacy WriteConfig retain their existing no-feature behavior. No schema, public interface, new option or file-deletion transaction is introduced.

Regression coverage: HUD-only, limiter-only and both enabled become both disabled through Apply and Reapply; unrelated lines and prefix lookalikes remain; original bytes/absence restore exactly; ownership hash and disabled configuration persist; unowned/absent/restored-only files remain untouched; pre-replacement ownership-save failures compensate configuration bytes and retain library/baseline bytes before successful retry. Existing generator assertions remain unchanged.

Verification is pending cloud CI at initial submission. Final revision-bound results belong in the PR completion comment. Self-review only; no local app, test, build, helper or device execution. Synthetic filesystem tests do not establish rendered DXVK HUD/frame-limit behavior, GUI delivery, crash recovery or GPU compatibility. Existing activity, lease, backup and uncertain-persistence safeguards remain in place.
