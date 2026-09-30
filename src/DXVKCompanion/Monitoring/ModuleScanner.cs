using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DXVKCompanion.Monitoring
{
    /// <summary>
    /// Enumerates a process's loaded modules ONCE per call and checks every tracked
    /// graphics-API DLL against that single snapshot.
    /// </summary>
    public class ModuleScanner
    {
        private static readonly string[] TrackedModules =
        {
            "d3d8.dll", "d3d9.dll", "d3d10.dll", "d3d10core.dll", "d3d10_1.dll",
            "d3d11.dll", "d3d12.dll", "dxgi.dll", "vulkan-1.dll", "opengl32.dll",
            "ddraw.dll", "dgvoodoo.dll"
        };

        public virtual HashSet<string> GetLoadedGraphicsModules(Process process)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    string name = module.ModuleName.ToLowerInvariant();
                    foreach (var tracked in TrackedModules)
                    {
                        if (name.Contains(tracked))
                            found.Add(tracked);
                    }
                }
            }
            catch
            {
                // Access denied — return whatever was found before the failure (often nothing).
            }

            return found;
        }

        public bool UsesMultipleApis(Process process)
        {
            var modules = GetLoadedGraphicsModules(process);
            int apiCount = 0;

            if (modules.Contains("d3d8.dll")) apiCount++;
            if (modules.Contains("d3d9.dll")) apiCount++;
            if (modules.Contains("d3d10.dll") || modules.Contains("d3d10core.dll")) apiCount++;
            if (modules.Contains("d3d11.dll")) apiCount++;
            if (modules.Contains("vulkan-1.dll")) apiCount++;
            if (modules.Contains("d3d12.dll")) apiCount++;

            return apiCount > 1;
        }

        public bool UsesDx8(Process process) => GetLoadedGraphicsModules(process).Contains("d3d8.dll");
        public bool UsesDx9(Process process) => GetLoadedGraphicsModules(process).Contains("d3d9.dll");
        public bool UsesDx10(Process process) => GetLoadedGraphicsModules(process).Contains("d3d10.dll") || GetLoadedGraphicsModules(process).Contains("d3d10core.dll");
        public bool UsesDx11(Process process) => GetLoadedGraphicsModules(process).Contains("d3d11.dll");
        public bool UsesDx12(Process process) => GetLoadedGraphicsModules(process).Contains("d3d12.dll");
        public bool UsesVulkan(Process process) => GetLoadedGraphicsModules(process).Contains("vulkan-1.dll");
        public bool UsesOpenGL(Process process) => GetLoadedGraphicsModules(process).Contains("opengl32.dll");

        public bool UsesDgVoodoo(Process process)
        {
            var m = GetLoadedGraphicsModules(process);
            return m.Contains("dgvoodoo.dll") || m.Contains("ddraw.dll") || m.Contains("d3d8.dll");
        }
    }
}
