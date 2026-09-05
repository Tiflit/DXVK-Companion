# Phase A Test Harness

The `tests/DXVKCompanion.PhaseA.Tests` project is a focused verification layer for the new data model.

## Intended checks

- safe default values (`Unknown`, not `Missing`);
- global management policy defaults;
- 120 FPS stored default with limiting disabled;
- multiple executable profiles under one installation;
- persistence round-tripping;
- migration of legacy DXVK profiles into `ManagedFileRecord` entries;
- legacy backup detection;
- conflicting legacy DXVK versions remaining `AttentionRequired`;
- corrupt current-library preservation without falling back to stale legacy data.

## Expected CI environment

The application currently targets `net8.0-windows` and Windows Forms. The test project therefore also targets `net8.0-windows` and should run on a Windows GitHub Actions runner.

Example command once placed in the repository:

```powershell
dotnet test tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj --configuration Release
```

The test project is intentionally separate from the application and does not alter production code.
