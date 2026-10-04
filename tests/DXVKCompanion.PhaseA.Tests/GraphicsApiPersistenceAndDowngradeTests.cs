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
    public class GraphicsApiPersistenceAndDowngradeTests
    {
        public static readonly TheoryData<GraphicsApi> AllGraphicsApiMembers = new()
        {
            GraphicsApi.Unknown,
            GraphicsApi.DX9,
            GraphicsApi.DX10,
            GraphicsApi.DX11,
            GraphicsApi.ModernAPI,
            GraphicsApi.DX12,
            GraphicsApi.Vulkan
        };

        #region ProfileStore (Ordinal-Based Persistence)

        [Theory]
        [MemberData(nameof(AllGraphicsApiMembers))]
        public void ProfileStore_EveryGraphicsApiMember_RoundTripsSuccessfully(GraphicsApi api)
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");
            string gameExe = Path.Combine(testDir.RootPath, "Game.exe");
            File.WriteAllText(gameExe, "dummy");

            // Write profile with target API
            var store1 = new ProfileStore(profilesFile);
            var profile1 = store1.GetOrCreate(gameExe);
            profile1.Api = api;
            store1.Save(profile1);

            // Reload via fresh ProfileStore instance
            var store2 = new ProfileStore(profilesFile);
            var reloaded = store2.GetOrCreate(gameExe);

            Assert.Equal(api, reloaded.Api);
        }

        [Fact]
        public void ProfileStore_OrdinalStability_PinsExactIntegerValuesInSerializedJson()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");

            // Explicit mapping required by Issue #18 ordinal stability
            var expectedOrdinals = new Dictionary<GraphicsApi, int>
            {
                [GraphicsApi.Unknown] = 0,
                [GraphicsApi.DX9] = 1,
                [GraphicsApi.DX10] = 2,
                [GraphicsApi.DX11] = 3,
                [GraphicsApi.ModernAPI] = 4,
                [GraphicsApi.DX12] = 5,
                [GraphicsApi.Vulkan] = 6
            };

            var store = new ProfileStore(profilesFile);
            foreach (var kvp in expectedOrdinals)
            {
                // Verify underlying C# enum cast value
                Assert.Equal(kvp.Value, (int)kvp.Key);

                string exePath = Path.Combine(testDir.RootPath, $"{kvp.Key}.exe");
                File.WriteAllText(exePath, "dummy");
                var p = store.GetOrCreate(exePath);
                p.Api = kvp.Key;
                store.Save(p);
            }

            // Inspect the raw JSON string written by ProfileStore
            string json = File.ReadAllText(profilesFile);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(JsonValueKind.Array, root.ValueKind);

            foreach (var element in root.EnumerateArray())
            {
                string exeName = element.GetProperty("ExeName").GetString()!;
                int apiValue = element.GetProperty("Api").GetInt32();

                string enumName = Path.GetFileNameWithoutExtension(exeName);
                var expectedEnum = Enum.Parse<GraphicsApi>(enumName);
                Assert.Equal(expectedOrdinals[expectedEnum], apiValue);
            }
        }

        [Fact]
        public void ProfileStore_DowngradeBehavior_UnrecognizedNumericValue_DeserializesAsCastValue()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");
            string gameExe = Path.Combine(testDir.RootPath, "Game.exe");

            // Raw JSON simulating an ordinal outside the defined enum members (e.g., 99)
            string rawJson = @"[
  {
    ""ExePath"": """ + gameExe.Replace("\\", "\\\\") + @""",
    ""ExeName"": ""Game.exe"",
    ""Api"": 99,
    ""Architecture"": ""x64"",
    ""DxvkEnabled"": false,
    ""DxvkVersion"": null,
    ""HudEnabled"": false,
    ""FrameLimit"": 0
  }
]";
            File.WriteAllText(profilesFile, rawJson);

            // ProfileStore deserialization with System.Text.Json default options casts integer to enum
            var store = new ProfileStore(profilesFile);
            var profile = store.GetOrCreate(gameExe);

            Assert.Equal((GraphicsApi)99, profile.Api);
        }

        [Fact]
        public void ProfileStore_MalformedOrStringApi_CatchesExceptionAndInitializesCleanStore()
        {
            using var testDir = new SyntheticTestDirectory();
            string profilesFile = Path.Combine(testDir.RootPath, "games.json");

            // Raw JSON containing string token for Api without JsonStringEnumConverter
            string rawJson = @"[
  {
    ""ExePath"": ""C:\\Games\\Game.exe"",
    ""ExeName"": ""Game.exe"",
    ""Api"": ""DX11""
  }
]";
            File.WriteAllText(profilesFile, rawJson);

            // Default ProfileStore catches JsonException and recovers cleanly
            var store = new ProfileStore(profilesFile);
            Assert.Empty(store.GetAll());
        }

        #endregion

        #region GameLibraryStore (Name-Based Persistence)

        [Theory]
        [MemberData(nameof(AllGraphicsApiMembers))]
        public void GameLibraryStore_EveryGraphicsApiMember_RoundTripsSuccessfully(GraphicsApi api)
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(testDir.RootPath, "backups");
            string nonExistentLegacy = Path.Combine(testDir.RootPath, "no-legacy.json");
            string installPath = Path.Combine(testDir.RootPath, "Games", "TestGame");
            Directory.CreateDirectory(installPath);

            // Save installation with specified LastKnownApi
            var store1 = new GameLibraryStore(libraryPath, backupsPath, nonExistentLegacy);
            var install1 = store1.GetOrCreateInstallation(installPath, "TestGame");
            var exe1 = install1.GetOrAddExecutable("TestGame.exe", "TestGame.exe");
            exe1.LastKnownApi = api;
            store1.Save(install1);

            // Reload via fresh GameLibraryStore instance
            var store2 = new GameLibraryStore(libraryPath, backupsPath, nonExistentLegacy);
            var install2 = store2.FindByInstallationPath(installPath);

            Assert.NotNull(install2);
            var exe2 = install2.Executables.FirstOrDefault(e => e.RelativePath == "TestGame.exe");
            Assert.NotNull(exe2);
            Assert.Equal(api, exe2.LastKnownApi);
        }

        [Fact]
        public void GameLibraryStore_NameBasedSerialization_PinsExactStringNamesInSerializedJson()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(testDir.RootPath, "backups");
            string nonExistentLegacy = Path.Combine(testDir.RootPath, "no-legacy.json");
            string installPath = Path.Combine(testDir.RootPath, "Games", "TestGame");
            Directory.CreateDirectory(installPath);

            var store = new GameLibraryStore(libraryPath, backupsPath, nonExistentLegacy);
            var install = store.GetOrCreateInstallation(installPath, "TestGame");

            // Add an executable for each member
            foreach (var api in Enum.GetValues<GraphicsApi>())
            {
                var exe = install.GetOrAddExecutable($"{api}.exe", $"{api}.exe");
                exe.LastKnownApi = api;
            }
            store.Save(install);

            // Inspect the raw JSON string written by GameLibraryStore
            string json = File.ReadAllText(libraryPath);
            using var doc = JsonDocument.Parse(json);
            var installations = doc.RootElement.GetProperty("Installations");
            Assert.Equal(1, installations.GetArrayLength());

            var executables = installations[0].GetProperty("Executables");

            foreach (var exeElement in executables.EnumerateArray())
            {
                string relativePath = exeElement.GetProperty("RelativePath").GetString()!;
                string lastKnownApi = exeElement.GetProperty("LastKnownApi").GetString()!;

                string expectedName = Path.GetFileNameWithoutExtension(relativePath);
                Assert.Equal(expectedName, lastKnownApi);
            }
        }

        [Fact]
        public void GameLibraryStore_LoadingLibraryContainingLegacyModernAPI_DeserializesCleanly()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(testDir.RootPath, "backups");
            string nonExistentLegacy = Path.Combine(testDir.RootPath, "no-legacy.json");
            string installPath = Path.Combine(testDir.RootPath, "Games", "ModernGame");
            Directory.CreateDirectory(installPath);

            // Pre-seed a valid game-library.json using the legacy "ModernAPI" string identifier
            string libraryJson = @"{
  ""SchemaVersion"": 1,
  ""Installations"": [
    {
      ""Id"": ""11111111222233334444555566667777"",
      ""InstallationPath"": """ + GameInstallation.NormalizeInstallationPath(installPath).Replace("\\", "\\\\") + @""",
      ""DisplayName"": ""ModernGame"",
      ""Executables"": [
        {
          ""RelativePath"": ""ModernGame.exe"",
          ""DisplayName"": ""ModernGame.exe"",
          ""LastKnownApi"": ""ModernAPI"",
          ""ApiConfidence"": ""High"",
          ""LastKnownArchitecture"": ""x64"",
          ""DetectionEvidence"": [ ""dxgi.dll detected without D3D11"" ]
        }
      ]
    }
  ]
}";
            File.WriteAllText(libraryPath, libraryJson);

            // Act: load into GameLibraryStore
            var store = new GameLibraryStore(libraryPath, backupsPath, nonExistentLegacy);

            // Assert: loaded cleanly without creating recovery file
            var all = store.GetAll();
            Assert.Single(all);

            var loaded = store.FindByInstallationPath(installPath);
            Assert.NotNull(loaded);
            var exe = loaded.Executables.FirstOrDefault(e => e.RelativePath == "ModernGame.exe");
            Assert.NotNull(exe);
            Assert.Equal(GraphicsApi.ModernAPI, exe.LastKnownApi);

            // Verify no recovery copy was triggered
            var recoveryFiles = Directory.GetFiles(testDir.RootPath, "game-library.json.recovery.*");
            Assert.Empty(recoveryFiles);
        }

        [Fact]
        public void GameLibraryStore_LegacyProfilesMigration_ImportsLegacyModernApiCorrectly()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(testDir.RootPath, "backups");
            string legacyPath = Path.Combine(testDir.RootPath, "games.json");

            string gameDir = Path.Combine(testDir.RootPath, "Games", "LegacyGame");
            Directory.CreateDirectory(gameDir);
            string gameExe = Path.Combine(gameDir, "LegacyGame.exe");
            File.WriteAllText(gameExe, "dummy");

            // Legacy games.json containing ModernAPI (ordinal 4)
            var legacyProfiles = new List<GameProfile>
            {
                new(gameExe)
                {
                    Api = GraphicsApi.ModernAPI,
                    Architecture = "x64",
                    DxvkEnabled = false
                }
            };
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacyProfiles, new JsonSerializerOptions { WriteIndented = true }));

            // Act: load GameLibraryStore with missing game-library.json and existing games.json
            var store = new GameLibraryStore(libraryPath, backupsPath, legacyPath);

            // Assert: migrated with ModernAPI
            var all = store.GetAll();
            Assert.Single(all);

            var install = store.FindByInstallationPath(gameDir);
            Assert.NotNull(install);
            var exe = install.Executables.FirstOrDefault(e => e.RelativePath == "LegacyGame.exe");
            Assert.NotNull(exe);
            Assert.Equal(GraphicsApi.ModernAPI, exe.LastKnownApi);
        }

        [Fact]
        public void GameLibraryStore_DowngradeBehavior_UnrecognizedEnumString_FailsValidationAndPreservesRecoverySnapshot()
        {
            using var testDir = new SyntheticTestDirectory();
            string libraryPath = Path.Combine(testDir.RootPath, "game-library.json");
            string backupsPath = Path.Combine(testDir.RootPath, "backups");
            string nonExistentLegacy = Path.Combine(testDir.RootPath, "no-legacy.json");
            string installPath = Path.Combine(testDir.RootPath, "Games", "FutureGame");
            Directory.CreateDirectory(installPath);

            // JSON simulating a future enum value or older build reading an unrecognised API string
            string libraryJson = @"{
  ""SchemaVersion"": 1,
  ""Installations"": [
    {
      ""Id"": ""11111111222233334444555566667777"",
      ""InstallationPath"": """ + GameInstallation.NormalizeInstallationPath(installPath).Replace("\\", "\\\\") + @""",
      ""DisplayName"": ""FutureGame"",
      ""Executables"": [
        {
          ""RelativePath"": ""FutureGame.exe"",
          ""DisplayName"": ""FutureGame.exe"",
          ""LastKnownApi"": ""Direct3D13Unrecognized"",
          ""ApiConfidence"": ""High"",
          ""LastKnownArchitecture"": ""x64""
        }
      ]
    }
  ]
}";
            File.WriteAllText(libraryPath, libraryJson);

            // Act: load GameLibraryStore
            var store = new GameLibraryStore(libraryPath, backupsPath, nonExistentLegacy);

            // Assert:
            // 1. In-memory installations are empty to protect against partial/corrupted load
            Assert.Empty(store.GetAll());

            // 2. Original file was preserved non-destructively
            Assert.True(File.Exists(libraryPath));
            Assert.Equal(libraryJson, File.ReadAllText(libraryPath));

            // 3. A recovery snapshot copy was preserved
            var recoveryFiles = Directory.GetFiles(testDir.RootPath, "game-library.json.recovery.*");
            Assert.Single(recoveryFiles);
            Assert.Equal(libraryJson, File.ReadAllText(recoveryFiles[0]));
        }

        [Theory]
        [InlineData("DX12")]
        [InlineData("Vulkan")]
        public void OlderBuildSimulation_WhenDeserializingGameLibraryWithNewEnums_ThrowsJsonException(string newApiString)
        {
            // Simulates an older binary (prior to PR #9) that only had enum members 0..4
            string json = @"{
  ""RelativePath"": ""Game.exe"",
  ""LastKnownApi"": """ + newApiString + @"""
}";
            var options = new JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };

            // An older build fails to deserialize with JsonException because string is not in its enum definition
            var ex = Assert.Throws<JsonException>(() =>
                JsonSerializer.Deserialize<PreIssue6ExecutableProfileStub>(json, options));

            Assert.Contains("could not be converted", ex.Message);
            Assert.Contains("$.LastKnownApi", ex.Message);
        }

        [Theory]
        [InlineData(5, GraphicsApi.DX12)]
        [InlineData(6, GraphicsApi.Vulkan)]
        public void OlderBuildSimulation_WhenDeserializingProfileStoreWithNewOrdinals_DeserializesAsCastInteger(int ordinal, GraphicsApi currentApi)
        {
            // Simulates an older binary reading games.json produced by modern build with DX12 (5) or Vulkan (6)
            string json = @"{
  ""ExePath"": ""C:\\Games\\Game.exe"",
  ""Api"": " + ordinal + @"
}";
            // Default options (used by ProfileStore) cast integer without exception
            var stub = JsonSerializer.Deserialize<PreIssue6GameProfileStub>(json);
            Assert.NotNull(stub);
            Assert.Equal(ordinal, (int)stub.Api);
            Assert.Equal(ordinal, (int)currentApi);
        }

        #endregion

        #region Downgrade Simulation Stubs

        private enum PreIssue6GraphicsApiStub
        {
            Unknown = 0,
            DX9 = 1,
            DX10 = 2,
            DX11 = 3,
            ModernAPI = 4
        }

        private class PreIssue6ExecutableProfileStub
        {
            public string RelativePath { get; set; } = string.Empty;
            public PreIssue6GraphicsApiStub LastKnownApi { get; set; }
        }

        private class PreIssue6GameProfileStub
        {
            public string ExePath { get; set; } = string.Empty;
            public PreIssue6GraphicsApiStub Api { get; set; }
        }

        #endregion
    }
}
