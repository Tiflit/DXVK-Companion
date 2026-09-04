using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DXVKCompanion.Monitoring
{
    /// <summary>
    /// Enumerates a process's loaded modules ONCE per call and checks every tracked
    /// graphics-API DLL against that single snapshot — replacing six near-identical methods
    /// that each independently re-enumerated process.Modules (a real syscall, not a free
    /// property read) whenever ApiClassifier called more than one of them per process.
    /// </summary>
    public class ModuleScanner
    {
        private static readonly string[] TrackedModules =
        {
            "d3d8.dll", "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll",
            "dxgi.dll", "vulkan-1.dll", "opengl32.dll", "ddraw.dll", "dgvoodoo.dll"
        };

        public HashSet<string> GetLoadedGraphicsModules(Process process)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    string name = module.ModuleName.ToLowerInvariant();
                    foreach (var tracked in TrackedModules)
                        if (name.Contains(tracked))
                            found.Add(tracked);
                }
            }
            catch
            {
                // Access denied — return whatever was found before the failure (often nothing).
            }

            return found;
        }

        // Kept for any external/future callers that only care about one specific API —
        // internally, ApiClassifier now calls GetLoadedGraphicsModules directly instead.
        public bool UsesDx9(Process process) => GetLoadedGraphicsModules(process).Contains("d3d9.dll");
        public bool UsesDx10(Process process) => GetLoadedGraphicsModules(process).Contains("d3d10.dll");
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
