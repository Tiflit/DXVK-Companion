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
        public void CleanSlateV1_WhenGameLibraryMissingAndLegacyGamesJsonExists_DoesNotImportAndPreservesLegacyFileByteForByte()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(testDir.RootPath, "backups");
            string legacyPath = Path.Combine(testDir.RootPath, "games.json");

            string skyrimDir = Path.Combine(testDir.RootPath, "Games", "Skyrim");
            Directory.CreateDirectory(skyrimDir);
            string skyrimExe = Path.Combine(skyrimDir, "SkyrimSE.exe");
            File.WriteAllText(skyrimExe, "fake exe");

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
                }
            };

            var legacyJson = JsonSerializer.Serialize(legacyProfiles, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(legacyPath, legacyJson);
            var expectedLegacyBytes = File.ReadAllBytes(legacyPath);

            // Act: load GameLibraryStore with missing game-library.json but existing games.json
            var store = new GameLibraryStore(libraryPath, backupsPath, legacyPath);

            // Assert: V1 clean-slate policy - no installations, no executable profiles,
            // no managed files, no pending actions imported, and no startup library file created.
            Assert.Empty(store.GetAll());
            Assert.Null(store.FindByInstallationPath(skyrimDir));
            Assert.Null(store.FindInstallationForExecutable(skyrimExe));
            Assert.False(File.Exists(libraryPath), "Missing game-library.json must not be created on startup when no data is saved");

            // Legacy games.json must be preserved byte-for-byte (no deletion, overwrite, migration, or conversion)
            Assert.True(File.Exists(legacyPath), "Legacy games.json must still exist");
            Assert.Equal(expectedLegacyBytes, File.ReadAllBytes(legacyPath));
        }

        [Fact]
        public void CleanSlateV1_WhenGameLibraryMissingAndMultipleLegacyProfilesExist_DoesNotImportAndLeavesStoreEmpty()
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

            var legacyJson = JsonSerializer.Serialize(legacyProfiles, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(legacyPath, legacyJson);
            var expectedLegacyBytes = File.ReadAllBytes(legacyPath);

            // Act: load GameLibraryStore with missing game-library.json but existing games.json
            var store = new GameLibraryStore(libraryPath, backupsPath, legacyPath);

            // Assert: approved Clean-Slate V1 policy - no profiles imported, store is empty
            Assert.Empty(store.GetAll());
            Assert.Null(store.FindByInstallationPath(skyrimDir));
            Assert.Null(store.FindByInstallationPath(oblivionDir));
            Assert.False(File.Exists(libraryPath), "Missing game-library.json must not be created on startup");

            // Legacy file must remain intact byte-for-byte
            Assert.True(File.Exists(legacyPath), "Legacy games.json must be preserved non-destructively");
            Assert.Equal(expectedLegacyBytes, File.ReadAllBytes(legacyPath));
        }

        [Fact]
        public void CleanSlateV1_CorruptCurrentLibraryWithExistingLegacy_PreservesRecoveryCopy_DoesNotFallbackToLegacyImport()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(testDir.RootPath, "backups");
            string legacyPath = Path.Combine(testDir.RootPath, "games.json");

            string skyrimDir = Path.Combine(testDir.RootPath, "Games", "Skyrim");
            Directory.CreateDirectory(skyrimDir);
            string skyrimExe = Path.Combine(skyrimDir, "SkyrimSE.exe");
            File.WriteAllText(skyrimExe, "fake exe");

            // Corrupt current-format library file
            string corruptContent = "{ this is invalid json content !!! }";
            File.WriteAllText(libraryPath, corruptContent);

            var legacyProfiles = new List<GameProfile>
            {
                new(skyrimExe)
                {
                    Api = GraphicsApi.DX11,
                    Architecture = "x64",
                    DxvkEnabled = true,
                    DxvkVersion = "2.6"
                }
            };
            var legacyJson = JsonSerializer.Serialize(legacyProfiles, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(legacyPath, legacyJson);
            var expectedLegacyBytes = File.ReadAllBytes(legacyPath);

            // Act: load GameLibraryStore with corrupt library file and existing legacy games.json
            var store = new GameLibraryStore(libraryPath, backupsPath, legacyPath);

            // Assert: store is empty; never falls back to legacy import
            Assert.Empty(store.GetAll());
            Assert.Null(store.FindByInstallationPath(skyrimDir));

            // Original corrupt file is preserved
            Assert.True(File.Exists(libraryPath));
            Assert.Equal(corruptContent, File.ReadAllText(libraryPath));

            // Recovery copy was created
            var recoveryFiles = Directory.GetFiles(testDir.RootPath, "game-library.json.recovery.*.json");
            Assert.Single(recoveryFiles);
            Assert.Equal(corruptContent, File.ReadAllText(recoveryFiles[0]));

            // Legacy games.json must be untouched byte-for-byte
            Assert.True(File.Exists(legacyPath));
            Assert.Equal(expectedLegacyBytes, File.ReadAllBytes(legacyPath));
        }

        [Fact]
        public void CleanSlateV1_FutureSchemaLibraryWithExistingLegacy_DoesNotFallbackToLegacyImport()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(testDir.RootPath, "backups");
            string legacyPath = Path.Combine(testDir.RootPath, "games.json");

            string futureJson = @"{
  ""SchemaVersion"": 999,
  ""Installations"": []
}";
            File.WriteAllText(libraryPath, futureJson);

            var legacyProfiles = new List<GameProfile>
            {
                new(@"C:\Games\Skyrim\SkyrimSE.exe")
                {
                    Api = GraphicsApi.DX11,
                    Architecture = "x64"
                }
            };
            var legacyJson = JsonSerializer.Serialize(legacyProfiles, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(legacyPath, legacyJson);
            var expectedLegacyBytes = File.ReadAllBytes(legacyPath);

            // Act: load GameLibraryStore with future-schema library
            var store = new GameLibraryStore(libraryPath, backupsPath, legacyPath);

            // Assert: store is empty, future file preserved, legacy not imported
            Assert.Empty(store.GetAll());
            Assert.True(File.Exists(libraryPath));
            Assert.Equal(futureJson, File.ReadAllText(libraryPath));
            Assert.True(File.Exists(legacyPath));
            Assert.Equal(expectedLegacyBytes, File.ReadAllBytes(legacyPath));

            // No recovery file created for future schema
            var recoveryFiles = Directory.GetFiles(testDir.RootPath, "game-library.json.recovery.*");
            Assert.Empty(recoveryFiles);
        }

        [Fact]
        public void CleanSlateV1_WhenGameLibraryAlreadyExists_DoesNotOverwriteOrModifyLegacy()
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
            var legacyJson = JsonSerializer.Serialize(legacyProfiles, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(legacyPath, legacyJson);
            var expectedLegacyBytes = File.ReadAllBytes(legacyPath);

            // Act
            var store = new GameLibraryStore(libraryPath, backupsPath, legacyPath);

            // Assert: only existing library items are present; legacy was not imported
            var all = store.GetAll();
            Assert.Single(all);
            Assert.Equal("Witcher 3", all.First().DisplayName);

            // Legacy file preserved byte-for-byte
            Assert.True(File.Exists(legacyPath));
            Assert.Equal(expectedLegacyBytes, File.ReadAllBytes(legacyPath));
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
