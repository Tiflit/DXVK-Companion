using System.Text.Json;
using System.Text.Json.Serialization;
using DXVKCompanion.Models;
using Xunit;

namespace DXVKCompanion.PhaseATests;

public sealed class PhaseADataModelTests
{
    [Fact]
    public void ManagedFileRecord_DefaultOriginalState_IsUnknown()
    {
        var record = new ManagedFileRecord();

        Assert.Equal(FileOriginalState.Unknown, record.OriginalState);
        Assert.Equal(ManagedFileState.Unknown, record.CurrentState);
    }

    [Fact]
    public void NewInstallation_UsesGlobalPolicy_AndStores120FpsAsDisabledDefault()
    {
        var installation = new GameInstallation();

        Assert.Equal(ManagementMode.UseGlobal, installation.ManagementPolicy.Mode);
        Assert.False(installation.Configuration.FrameLimitEnabled);
        Assert.Equal(120, installation.Configuration.FrameLimit);
        Assert.Equal(RestorationState.None, installation.RestorationState);
    }

    [Fact]
    public void MultipleExecutables_CanShareInstallation_AndRemainDistinct()
    {
        var installation = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example"
        };

        var dx11 = installation.GetOrAddExecutable(@"bin\Game_DX11.exe");
        var dx12 = installation.GetOrAddExecutable(@"bin\Game_DX12.exe");

        dx11.LastKnownApi = GraphicsApi.DX11;
        dx12.LastKnownApi = GraphicsApi.ModernAPI;

        Assert.Equal(2, installation.Executables.Count);
        Assert.NotSame(dx11, dx12);
        Assert.Equal("bin" + Path.DirectorySeparatorChar + "Game_DX11.exe", dx11.RelativePath);
        Assert.Equal("bin" + Path.DirectorySeparatorChar + "Game_DX12.exe", dx12.RelativePath);
    }

    [Fact]
    public void JsonRoundTrip_PreservesPoliciesPendingActionAndManagedFileState()
    {
        var installation = new GameInstallation
        {
            InstallationPath = @"C:\Games\Example",
            DisplayName = "Example",
            IsHidden = true,
            ManagementPolicy = ManagementPolicy.PinVersion("3.1")
        };

        installation.PendingAction = PendingAction.Update("3.1", "UserApprovedUpdate");

        var file = installation.GetOrAddManagedFile("d3d11.dll");
        file.OriginalState = FileOriginalState.Existing;
        file.CurrentState = ManagedFileState.ExternallyChanged;
        file.OriginalSha256 = "original";
        file.ExpectedManagedSha256 = "managed";
        file.ManagedDxvkVersion = "3.1";

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var json = JsonSerializer.Serialize(installation, options);
        var restored = JsonSerializer.Deserialize<GameInstallation>(json, options);

        Assert.NotNull(restored);
        Assert.True(restored!.IsHidden);
        Assert.Equal(ManagementMode.PinnedVersion, restored.ManagementPolicy.Mode);
        Assert.Equal("3.1", restored.ManagementPolicy.PinnedDxvkVersion);
        Assert.Equal(PendingActionType.Update, restored.PendingAction!.Type);
        Assert.Equal("3.1", restored.PendingAction.TargetDxvkVersion);
        Assert.Single(restored.ManagedFiles);
        Assert.Equal(ManagedFileState.ExternallyChanged, restored.ManagedFiles[0].CurrentState);
        Assert.Equal(FileOriginalState.Existing, restored.ManagedFiles[0].OriginalState);
    }
}
