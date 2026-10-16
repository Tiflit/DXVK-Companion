# Exact configuration-key preservation

Codex implemented and self-reviewed a focused configuration repair against base `288c3f301369bf8bcbeaa274f18e02b4fa55663b`. Updating HUD or frame-limit settings previously replaced unrelated lines whose names shared a managed-key prefix. The generator now compares the complete trimmed key before the first equals sign, retaining case-insensitive matching and existing exact-key update, removal, appending and duplicate-key behavior.

Added 18 xUnit cases without weakening existing assertions. On the unchanged base, these cases produced 10 failures and 8 passes; on the repaired source, all 18 passed. Release builds of the application and tests succeeded with .NET SDK 8.0.426. The full application suite passed 607 tests with zero failures or skips. Two CS1998 warnings occur in unchanged methods. An initial sandbox run could not connect to testhost; subsequent host execution succeeded. No GUI or independent-review result is claimed.

Only the configuration manager, new test class and this journal changed. Source and test bytes match the locally tested files; only this publication journal was condensed. Private coordination records, account information and raw local logs are excluded. No out-of-scope findings were identified. Next: GitHub verification and human final merge review.
