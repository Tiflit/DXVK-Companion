Phase A5.3 — Multi-file atomic transaction

Merge the src/ and tests/ folders into the repository root.

This patch is isolated to the new Safety transaction engine and synthetic tests.
Do not wire it into DxvkInstaller, DxvkRollback, DxvkManager, UI, monitoring, or Automated mode yet.

Expected validation:
  dotnet build src/DXVKCompanion/DXVKCompanion.csproj --configuration Release
  dotnet build tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj --configuration Release
  dotnet test tests/DXVKCompanion.PhaseA.Tests/DXVKCompanion.PhaseA.Tests.csproj --configuration Release --no-build --no-restore --logger "trx;LogFileName=phase-a-tests.trx"
