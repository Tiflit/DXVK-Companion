using System;
using DXVKCompanion.Models;

namespace DXVKCompanion.DXVK
{
    /// <summary>
    /// Authoritative compatibility boundary for DXVK translation layer deployment.
    /// In this project, DXVK actively translates Direct3D 9, 10, and 11.
    /// Native modern APIs (DX12, Vulkan) are informational/observable only and do not deploy DXVK.
    /// </summary>
    public static class DxvkCompatibility
    {
        /// <summary>
        /// Determines whether the specified GraphicsApi is supported for DXVK deployment.
        /// </summary>
        public static bool IsDxvkSupported(GraphicsApi api) => GetRequiredDlls(api).Length > 0;

        /// <summary>
        /// Returns the required DXVK DLL filenames for the specified GraphicsApi.
        /// Returns an empty array if DXVK is not applicable or not supported for this API.
        /// </summary>
        public static string[] GetRequiredDlls(GraphicsApi api) => api switch
        {
            GraphicsApi.DX9 => new[] { "d3d9.dll" },
            GraphicsApi.DX10 => new[] { "d3d11.dll", "dxgi.dll" },
            GraphicsApi.DX11 => new[] { "d3d11.dll", "dxgi.dll" },
            _ => Array.Empty<string>()
        };
    }
}
