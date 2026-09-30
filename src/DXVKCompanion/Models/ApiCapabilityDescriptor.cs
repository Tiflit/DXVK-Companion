using System;
using System.Collections.Generic;

namespace DXVKCompanion.Models
{
    public sealed class ApiCapabilityDescriptor
    {
        public GraphicsApi Api { get; init; }
        public bool IsSupported { get; init; }
        public IReadOnlyList<string> RequiredDlls { get; init; } = Array.Empty<string>();
        public string Description { get; init; } = string.Empty;
    }

    public static class DxvkCapabilityMatrix
    {
        private static readonly Dictionary<GraphicsApi, ApiCapabilityDescriptor> Descriptors = new()
        {
            [GraphicsApi.D3D8] = new ApiCapabilityDescriptor
            {
                Api = GraphicsApi.D3D8,
                IsSupported = true,
                RequiredDlls = new[] { "d3d8.dll", "d3d9.dll" },
                Description = "Direct3D 8 (supported via DXVK d3d8 + d3d9)"
            },
            [GraphicsApi.D3D9] = new ApiCapabilityDescriptor
            {
                Api = GraphicsApi.D3D9,
                IsSupported = true,
                RequiredDlls = new[] { "d3d9.dll" },
                Description = "Direct3D 9 (supported via DXVK d3d9)"
            },
            [GraphicsApi.D3D10] = new ApiCapabilityDescriptor
            {
                Api = GraphicsApi.D3D10,
                IsSupported = true,
                RequiredDlls = new[] { "d3d10core.dll", "d3d11.dll", "dxgi.dll" },
                Description = "Direct3D 10 (supported via DXVK d3d10core + d3d11 + dxgi)"
            },
            [GraphicsApi.D3D11] = new ApiCapabilityDescriptor
            {
                Api = GraphicsApi.D3D11,
                IsSupported = true,
                RequiredDlls = new[] { "d3d11.dll", "dxgi.dll" },
                Description = "Direct3D 11 (supported via DXVK d3d11 + dxgi)"
            },
            [GraphicsApi.D3D12] = new ApiCapabilityDescriptor
            {
                Api = GraphicsApi.D3D12,
                IsSupported = false,
                RequiredDlls = Array.Empty<string>(),
                Description = "Direct3D 12 (native - observe only)"
            },
            [GraphicsApi.Vulkan] = new ApiCapabilityDescriptor
            {
                Api = GraphicsApi.Vulkan,
                IsSupported = false,
                RequiredDlls = Array.Empty<string>(),
                Description = "Vulkan (native - observe only)"
            },
            [GraphicsApi.ModernAPI] = new ApiCapabilityDescriptor
            {
                Api = GraphicsApi.ModernAPI,
                IsSupported = false,
                RequiredDlls = Array.Empty<string>(),
                Description = "Modern API (observe only)"
            },
            [GraphicsApi.Unknown] = new ApiCapabilityDescriptor
            {
                Api = GraphicsApi.Unknown,
                IsSupported = false,
                RequiredDlls = Array.Empty<string>(),
                Description = "Unknown API (observe only)"
            }
        };

        public static ApiCapabilityDescriptor GetDescriptor(GraphicsApi api)
        {
            if (Descriptors.TryGetValue(api, out var desc))
                return desc;
            return Descriptors[GraphicsApi.Unknown];
        }

        public static bool IsSupported(GraphicsApi api) => GetDescriptor(api).IsSupported;

        public static IReadOnlyList<string> GetRequiredDlls(GraphicsApi api) => GetDescriptor(api).RequiredDlls;
    }
}
