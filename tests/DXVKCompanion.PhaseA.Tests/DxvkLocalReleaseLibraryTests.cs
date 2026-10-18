using System;
using System.IO;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;
using Xunit;

namespace DXVKCompanion.PhaseA.Tests;

public sealed class DxvkLocalReleaseLibraryTests : IDisposable
{
    private readonly string _tempRoot;

    public DxvkLocalReleaseLibraryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "dxvk_lib_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("../2.3", false)]
    [InlineData("..\\2.3", false)]
    [InlineData("v2.3/sub", false)]
    [InlineData("v2.3\\sub", false)]
    [InlineData("C:v2.3", false)]
    [InlineData("v2.3", true)]
    [InlineData("2.4", true)]
    [InlineData("2.5.1", true)]
    [InlineData("v2.6-preview", true)]
    public void IsSafeReleaseDirectoryName_ValidatesCorrectly(string? name, bool expected)
    {
        bool actual = DxvkLocalReleaseLibrary.IsSafeReleaseDirectoryName(name);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("x32", "x32")]
    [InlineData("X32", "x32")]
    [InlineData("x86", "x32")]
    [InlineData("win32", "x32")]
    [InlineData("x64", "x64")]
    [InlineData("X64", "x64")]
    [InlineData("amd64", "x64")]
    [InlineData("win64", "x64")]
    [InlineData("Unknown", null)]
    [InlineData("arm64", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeArchitecture_NormalizesOrReturnsNull(string? arch, string? expected)
    {
        string? actual = DxvkLocalReleaseLibrary.NormalizeArchitecture(arch);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GetVersionDirectory_RejectsEscapesAndNonExistent()
    {
        string validDir = Path.Combine(_tempRoot, "v2.4");
        Directory.CreateDirectory(validDir);

        Assert.NotNull(DxvkLocalReleaseLibrary.GetVersionDirectory(_tempRoot, "v2.4"));
        Assert.Null(DxvkLocalReleaseLibrary.GetVersionDirectory(_tempRoot, "nonexistent"));
        Assert.Null(DxvkLocalReleaseLibrary.GetVersionDirectory(_tempRoot, "../escape"));
        Assert.Null(DxvkLocalReleaseLibrary.GetVersionDirectory(_tempRoot, ".."));
        Assert.Null(DxvkLocalReleaseLibrary.GetVersionDirectory("", "v2.4"));
        Assert.Null(DxvkLocalReleaseLibrary.GetVersionDirectory(_tempRoot, null));
    }

    [Fact]
    public void IsVersionAvailable_UnsupportedApiOrArchitecture_ReturnsFalse()
    {
        string vDir = Path.Combine(_tempRoot, "v2.4", "x64");
        Directory.CreateDirectory(vDir);
        File.WriteAllBytes(Path.Combine(vDir, "d3d11.dll"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(vDir, "dxgi.dll"), new byte[] { 1, 2, 3 });

        // Unsupported API
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(_tempRoot, "v2.4", GraphicsApi.Direct3D12, "x64"));
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(_tempRoot, "v2.4", GraphicsApi.Vulkan, "x64"));
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(_tempRoot, "v2.4", GraphicsApi.Unknown, "x64"));

        // Unsupported / Unknown architecture
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(_tempRoot, "v2.4", GraphicsApi.DX11, "Unknown"));
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(_tempRoot, "v2.4", GraphicsApi.DX11, null));
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(_tempRoot, "v2.4", GraphicsApi.DX11, "arm64"));
    }

    [Fact]
    public void IsVersionAvailable_MissingDllOrZeroByteDll_ReturnsFalse()
    {
        string vDir = Path.Combine(_tempRoot, "v2.4", "x64");
        Directory.CreateDirectory(vDir);

        // Missing required files
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(_tempRoot, "v2.4", GraphicsApi.DX11, "x64"));

        // Only d3d11.dll, missing dxgi.dll
        File.WriteAllBytes(Path.Combine(vDir, "d3d11.dll"), new byte[] { 1, 2, 3 });
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(_tempRoot, "v2.4", GraphicsApi.DX11, "x64"));

        // Empty (0-byte) dxgi.dll
        File.WriteAllBytes(Path.Combine(vDir, "dxgi.dll"), Array.Empty<byte>());
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(_tempRoot, "v2.4", GraphicsApi.DX11, "x64"));

        // Both populated > 0 bytes
        File.WriteAllBytes(Path.Combine(vDir, "dxgi.dll"), new byte[] { 4, 5, 6 });
        Assert.True(DxvkLocalReleaseLibrary.IsVersionAvailable(_tempRoot, "v2.4", GraphicsApi.DX11, "x64"));
    }

    [Fact]
    public void IsVersionAvailable_Dx9_RequiresD3D9()
    {
        string vDir = Path.Combine(_tempRoot, "v2.3", "x32");
        Directory.CreateDirectory(vDir);

        File.WriteAllBytes(Path.Combine(vDir, "d3d9.dll"), new byte[] { 7, 8, 9 });
        Assert.True(DxvkLocalReleaseLibrary.IsVersionAvailable(_tempRoot, "v2.3", GraphicsApi.DX9, "x32"));
        // Should not be available for x64 since x64 directory does not exist
        Assert.False(DxvkLocalReleaseLibrary.IsVersionAvailable(_tempRoot, "v2.3", GraphicsApi.DX9, "x64"));
    }

    [Fact]
    public void GetAvailableVersions_DeterministicDescendingAndExcludesIncomplete()
    {
        // 1. Complete v2.3 for x64
        string v23Dir = Path.Combine(_tempRoot, "v2.3", "x64");
        Directory.CreateDirectory(v23Dir);
        File.WriteAllBytes(Path.Combine(v23Dir, "d3d11.dll"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(v23Dir, "dxgi.dll"), new byte[] { 2 });

        // 2. Complete v2.5 for x64
        string v25Dir = Path.Combine(_tempRoot, "v2.5", "x64");
        Directory.CreateDirectory(v25Dir);
        File.WriteAllBytes(Path.Combine(v25Dir, "d3d11.dll"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(v25Dir, "dxgi.dll"), new byte[] { 2 });

        // 3. Complete v2.4 for x64
        string v24Dir = Path.Combine(_tempRoot, "v2.4", "x64");
        Directory.CreateDirectory(v24Dir);
        File.WriteAllBytes(Path.Combine(v24Dir, "d3d11.dll"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(v24Dir, "dxgi.dll"), new byte[] { 2 });

        // 4. Incomplete v2.2 (missing dxgi)
        string v22Dir = Path.Combine(_tempRoot, "v2.2", "x64");
        Directory.CreateDirectory(v22Dir);
        File.WriteAllBytes(Path.Combine(v22Dir, "d3d11.dll"), new byte[] { 1 });

        // 5. Incomplete v2.1 (0 byte d3d11)
        string v21Dir = Path.Combine(_tempRoot, "v2.1", "x64");
        Directory.CreateDirectory(v21Dir);
        File.WriteAllBytes(Path.Combine(v21Dir, "d3d11.dll"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(v21Dir, "dxgi.dll"), new byte[] { 2 });

        // 6. Unrelated / non-version folder
        string otherDir = Path.Combine(_tempRoot, "temp_cache");
        Directory.CreateDirectory(otherDir);

        var available = DxvkLocalReleaseLibrary.GetAvailableVersions(_tempRoot, GraphicsApi.DX11, "x64");

        Assert.Equal(3, available.Count);
        Assert.Equal("v2.5", available[0]);
        Assert.Equal("v2.4", available[1]);
        Assert.Equal("v2.3", available[2]);

        // Check unsupported API returns empty
        var emptyApi = DxvkLocalReleaseLibrary.GetAvailableVersions(_tempRoot, GraphicsApi.Direct3D12, "x64");
        Assert.Empty(emptyApi);

        // Check Unknown arch returns empty
        var emptyArch = DxvkLocalReleaseLibrary.GetAvailableVersions(_tempRoot, GraphicsApi.DX11, "Unknown");
        Assert.Empty(emptyArch);
    }
}
