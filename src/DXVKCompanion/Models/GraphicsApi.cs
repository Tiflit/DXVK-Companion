using System.Text.Json.Serialization;

namespace DXVKCompanion.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum GraphicsApi
    {
        Unknown = 0,
        DX9 = 1,
        DX10 = 2,
        DX11 = 3,
        ModernAPI = 4,
        DX12 = 5,
        Vulkan = 6,

        // Direct3D naming aliases
        D3D9 = DX9,
        D3D10 = DX10,
        D3D11 = DX11,
        D3D12 = DX12
    }
}

