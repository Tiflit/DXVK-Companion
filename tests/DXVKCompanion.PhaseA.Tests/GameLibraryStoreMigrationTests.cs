using System.Text.Json;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class GameLibraryStoreMigrationTests : IDisposable
{
    private readonly string _installationPath;
    private readonly string _profilesDir;
    private readonly string _libraryPath;
    private readonly string _legacyPath;

    public GameLibraryStoreMigrationTests()
    {
        _profilesDir = Paths.ProfilesDir;
        _libraryPath = GameLibraryPaths.GameLibraryFile;
        _legacyPath = Paths.ProfilesFile;
        _installationPath = Path.Combine(Path.GetTempPath(), "DXVKCompanion-PhaseA-" + Guid.NewGuid().ToString("N"));

        Paths.EnsureDirectories();
        Directory.CreateDirectory(_installationPath);

        DeleteIfExists(_libraryPath);
        DeleteIfExists(_legacyPath);
    }

    [Fact]
    public void LegacyDxvkProfile_MigratesManagedFileRecordsAndBackupEvidence()
    {
        var exePath = Path.Combine(_installationPath, "Game.exe");
        var legacy = new[]
        {
            new
            {
                ExePath = exePath,
                ExeName = "Game.exe",
                Api = GraphicsApi.DX11,
                Architecture = "x64",
                DxvkEnabled = true,
                DxvkVersion = "3.0.2",
                HudEnabled = false,
                FrameLimit = 0
            }
        };

        // Legacy Companion placed backups beside the game executable.
        File.WriteAllBytes(Path.Combine(_installationPath, "d3d11.dll.bak"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(_installationPath, "dxgi.dll.bak"), new byte[] { 4, 5, 6 });
        File.WriteAllText(_legacyPath, JsonSerializer.Serialize(legacy));

        var store = new GameLibraryStore();
        var installation = Assert.Single(store.GetAll());

        Assert.Equal("3.0.2", installation.ManagedDxvkVersion);
        Assert.Equal("x64", installation.ManagedDxvkArchitecture);
        Assert.Equal(2, installation.ManagedFiles.Count);
        Assert.All(installation.ManagedFiles, f =>
        {
            Assert.Equal(FileOriginalState.Existing, f.OriginalState);
            Assert.Equal(ManagedFileState.Unknown, f.CurrentState);
            Assert.Null(f.BackupRelativePath); // copy is intentionally deferred to the new file engine
            Assert.Equal("3.0.2", f.ManagedDxvkVersion);
        });
        Assert.Equal(RestorationState.Managed, installation.RestorationState);
    }

    [Fact]
    public void ConflictingLegacyDxvkVersions_RemainAttentionRequired_EvenWhenBackupsExist()
    {
        var legacy = new[]
        {
            new
            {
                ExePath = Path.Combine(_installationPath, "Game_DX11.exe"),
                ExeName = "Game_DX11.exe",
                Api = GraphicsApi.DX11,
                Architecture = "x64",
                DxvkEnabled = true,
                DxvkVersion = "2.7",
                HudEnabled = false,
                FrameLimit = 0
            },
            new
            {
                ExePath = Path.Combine(_installationPath, "Game_DX11_Old.exe"),
                ExeName = "Game_DX11_Old.exe",
                Api = GraphicsApi.DX11,
                Architecture = "x64",
                DxvkEnabled = true,
                DxvkVersion = "2.6.2",
                HudEnabled = false,
                FrameLimit = 0
            }
        };

        File.WriteAllBytes(Path.Combine(_installationPath, "d3d11.dll.bak"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_installationPath, "dxgi.dll.bak"), new byte[] { 2 });
        File.WriteAllText(_legacyPath, JsonSerializer.Serialize(legacy));

        var store = new GameLibraryStore();
        var installation = Assert.Single(store.GetAll());

        Assert.Equal(RestorationState.AttentionRequired, installation.RestorationState);
        Assert.Null(installation.ManagedDxvkVersion);
        Assert.NotEmpty(installation.ManagedFiles);
    }

    [Fact]
    public void CorruptCurrentLibrary_IsPreserved_AndLegacySnapshotIsNotUsedAutomatically()
    {
        File.WriteAllText(_libraryPath, "{ this is not valid json");

        var exePath = Path.Combine(_installationPath, "Game.exe");
        var legacy = new[]
        {
            new
            {
                ExePath = exePath,
                ExeName = "Game.exe",
                Api = GraphicsApi.DX11,
                Architecture = "x64",
                DxvkEnabled = false,
                DxvkVersion = (string?)null,
                HudEnabled = false,
                FrameLimit = 0
            }
        };
        File.WriteAllText(_legacyPath, JsonSerializer.Serialize(legacy));

        var store = new GameLibraryStore();

        Assert.Empty(store.GetAll());

        var recoveryFiles = Directory.GetFiles(_profilesDir, "game-library.json.recovery.*.json");
        Assert.NotEmpty(recoveryFiles);
    }

    public void Dispose()
    {
        TryDeleteDirectory(_installationPath);
        DeleteIfExists(_libraryPath);
        DeleteIfExists(_legacyPath);

        foreach (var recovery in Directory.Exists(_profilesDir)
                     ? Directory.GetFiles(_profilesDir, "game-library.json.recovery.*.json")
                     : Array.Empty<string>())
        {
            DeleteIfExists(recovery);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Test cleanup should not hide the test result.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Test cleanup should not hide the test result.
        }
    }
}
