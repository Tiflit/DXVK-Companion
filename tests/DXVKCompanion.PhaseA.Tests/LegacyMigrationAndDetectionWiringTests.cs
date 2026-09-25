using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DXVKCompanion.Models;
using DXVKCompanion.PhaseATests;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests
{
    public class LegacyMigrationAndDetectionWiringTests
    {
        [Fact]
        public void LegacyMigration_WhenGameLibraryMissing_MigratesGamesJsonProfilesCorrectly()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(testDir.RootPath, "backups");
            string legacyPath = Path.Combine(testDir.RootPath, "games.json");

            string skyrimDir = Path.Combine(testDir.RootPath, "Games", "Skyrim");
            Directory.CreateDirectory(skyrimDir);
            string skyrimExe = Path.Combine(skyrimDir, "SkyrimSE.exe");
            File.WriteAllText(skyrimExe, "fake exe");

            string oblivionDir = Path.Combine(testDir.RootPath, "Games", "Oblivion");
            Directory.CreateDirectory(oblivionDir);
            string oblivionExe = Path.Combine(oblivionDir, "Oblivion.exe");
            File.WriteAllText(oblivionExe, "fake exe");

            var legacyProfiles = new List<GameProfile>
            {
                new(skyrimExe)
                {
                    Api = GraphicsApi.DX11,
                    Architecture = "x64",
                    DxvkEnabled = true,
                    DxvkVersion = "2.6",
                    HudEnabled = true,
                    FrameLimit = 60
                },
                new(oblivionExe)
                {
                    Api = GraphicsApi.DX9,
                    Architecture = "x32",
                    DxvkEnabled = false,
                    DxvkVersion = null,
                    HudEnabled = false,
                    FrameLimit = 0
                }
            };

            File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacyProfiles, new JsonSerializerOptions { WriteIndented = true }));

            // Act: load GameLibraryStore with missing game-library.json but existing games.json
            var store = new GameLibraryStore(libraryPath, backupsPath, legacyPath);

            // Assert: library was migrated and saved
            Assert.True(File.Exists(libraryPath));
            Assert.True(File.Exists(legacyPath), "Legacy games.json must be preserved non-destructively");

            var all = store.GetAll();
            Assert.Equal(2, all.Count);

            var skyrimInstall = store.FindByInstallationPath(skyrimDir);
            Assert.NotNull(skyrimInstall);
            Assert.Equal(RestorationState.Managed, skyrimInstall.RestorationState);
            Assert.Equal("2.6", skyrimInstall.ManagedDxvkVersion);
            Assert.True(skyrimInstall.Configuration.FrameLimitEnabled);
            Assert.Equal(60, skyrimInstall.Configuration.FrameLimit);
            Assert.True(skyrimInstall.Configuration.HudEnabled);

            var skyrimExeProfile = skyrimInstall.Executables.FirstOrDefault(e => e.RelativePath.Equals("SkyrimSE.exe", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(skyrimExeProfile);
            Assert.Equal(GraphicsApi.DX11, skyrimExeProfile.LastKnownApi);
            Assert.Equal("x64", skyrimExeProfile.LastKnownArchitecture);

            var oblivionInstall = store.FindByInstallationPath(oblivionDir);
            Assert.NotNull(oblivionInstall);
            Assert.Equal(RestorationState.None, oblivionInstall.RestorationState);
            Assert.False(oblivionInstall.Configuration.FrameLimitEnabled);

            var oblivionExeProfile = oblivionInstall.Executables.FirstOrDefault(e => e.RelativePath.Equals("Oblivion.exe", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(oblivionExeProfile);
            Assert.Equal(GraphicsApi.DX9, oblivionExeProfile.LastKnownApi);
            Assert.Equal("x32", oblivionExeProfile.LastKnownArchitecture);
        }

        [Fact]
        public void LegacyMigration_WhenGameLibraryAlreadyExists_DoesNotOverwriteWithLegacy()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(testDir.RootPath, "backups");
            string legacyPath = Path.Combine(testDir.RootPath, "games.json");

            string witcherDir = Path.Combine(testDir.RootPath, "Games", "Witcher3");
            Directory.CreateDirectory(witcherDir);

            // Pre-existing game-library.json
            var library = new GameLibrary
            {
                SchemaVersion = GameLibrary.CurrentSchemaVersion,
                Installations = new List<GameInstallation>
                {
                    new()
                    {
                        InstallationPath = GameInstallation.NormalizeInstallationPath(witcherDir),
                        DisplayName = "Witcher 3"
                    }
                }
            };
            File.WriteAllText(libraryPath, JsonSerializer.Serialize(library, new JsonSerializerOptions { WriteIndented = true }));

            // Legacy games.json with different game
            var legacyProfiles = new List<GameProfile>
            {
                new(@"C:\Games\Skyrim\SkyrimSE.exe")
                {
                    Api = GraphicsApi.DX11,
                    Architecture = "x64"
                }
            };
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacyProfiles, new JsonSerializerOptions { WriteIndented = true }));

            // Act
            var store = new GameLibraryStore(libraryPath, backupsPath, legacyPath);

            // Assert: only existing library items are present; legacy was not imported
            var all = store.GetAll();
            Assert.Single(all);
            Assert.Equal("Witcher 3", all.First().DisplayName);
        }

        [Fact]
        public void DetectionSnapshot_WhenRecorded_PreservesEvidenceAndArchitecture()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(testDir.RootPath, "backups");

            string gameDir = Path.Combine(testDir.RootPath, "Games", "DemoGame");
            Directory.CreateDirectory(gameDir);
            string exePath = Path.Combine(gameDir, "Demo.exe");
            File.WriteAllText(exePath, "fake exe");

            var store = new GameLibraryStore(libraryPath, backupsPath);

            var classification = new ApiClassificationResult
            {
                PrimaryApi = GraphicsApi.DX11,
                ObservedApis = new List<GraphicsApi> { GraphicsApi.DX11 },
                Confidence = ApiDetectionConfidence.High,
                Architecture = "x64",
                Evidence = new List<string> { "Loaded runtime module: d3d11.dll" },
                EvidenceSource = "RuntimeModules"
            };

            var antiCheat = AntiCheatAssessment.None();

            var snapshot = new DetectionSnapshot
            {
                ProcessId = 1234,
                ProcessName = "Demo",
                ExecutablePath = exePath,
                InstallationRoot = gameDir,
                ExecutableRelativePath = "Demo.exe",
                Classification = classification,
                AntiCheat = antiCheat,
                TimestampUtc = DateTime.UtcNow
            };

            // Act
            var installation = store.RecordDetectionSnapshot(snapshot);

            // Assert
            Assert.NotNull(installation);
            Assert.Equal(GameInstallation.NormalizeInstallationPath(gameDir), installation.InstallationPath);
            Assert.Single(installation.Executables);

            var exe = installation.Executables[0];
            Assert.Equal("Demo.exe", exe.RelativePath);
            Assert.Equal(GraphicsApi.DX11, exe.LastKnownApi);
            Assert.Equal("x64", exe.LastKnownArchitecture);
            Assert.Equal(ApiDetectionConfidence.High, exe.ApiConfidence);
            Assert.Contains("Loaded runtime module: d3d11.dll", exe.DetectionEvidence);
        }
    }
}
