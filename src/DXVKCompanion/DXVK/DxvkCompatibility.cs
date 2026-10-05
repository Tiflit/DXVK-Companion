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

        /// <summary>
        /// Evaluates whether an entire game installation is compatible with DXVK deployment.
        /// Under the approved installation-wide policy (Issue #14), deployment is refused if the
        /// target API or any recorded executable in the installation is outside the DX9/DX10/DX11 allowlist.
        /// </summary>
        public static bool IsInstallationSupported(
            GameInstallation? installation,
            GraphicsApi? targetApi,
            string? targetExeName,
            out string? refusalReason)
        {
            if (targetApi.HasValue && !IsDxvkSupported(targetApi.Value))
            {
                string targetName = !string.IsNullOrWhiteSpace(targetExeName) ? targetExeName : "Target executable";
                refusalReason = $"Target executable '{targetName}' has unsupported API '{targetApi.Value}'. DXVK supports Direct3D 9, 10, and 11.";
                return false;
            }

            if (installation != null && installation.Executables != null)
            {
                foreach (var exe in installation.Executables)
                {
                    if (!IsDxvkSupported(exe.LastKnownApi))
                    {
                        string exeName = !string.IsNullOrWhiteSpace(exe.DisplayName) ? exe.DisplayName : exe.RelativePath;
                        refusalReason = $"Installation contains incompatible executable '{exeName}' with API '{exe.LastKnownApi}'. Deployment refused installation-wide.";
                        return false;
                    }
                }
            }

            refusalReason = null;
            return true;
        }

        public static bool IsInstallationSupported(GameInstallation? installation, out string? refusalReason)
            => IsInstallationSupported(installation, null, null, out refusalReason);
    }
}
