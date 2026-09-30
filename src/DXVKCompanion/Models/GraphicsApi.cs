using System;
using System.Text.Json.Serialization;

namespace DXVKCompanion.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum GraphicsApi
    {
        Unknown = 0,
        D3D8 = 1,
        D3D9 = 2,
        D3D10 = 3,
        D3D11 = 4,
        D3D12 = 5,
        Vulkan = 6,

        // Backward compatibility aliases
        DX9 = D3D9,
        DX10 = D3D10,
        DX11 = D3D11,
        ModernAPI = 7
    }
}
