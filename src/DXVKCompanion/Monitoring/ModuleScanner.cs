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
    public class ModuleScanResult
    {
        public HashSet<string> Modules { get; }
        public bool IsComplete { get; }
        public bool HasFailed { get; }
        public string? FailureReason { get; }

        public ModuleScanResult(HashSet<string> modules, bool isComplete, bool hasFailed = false, string? failureReason = null)
        {
            Modules = modules ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IsComplete = isComplete;
            HasFailed = hasFailed;
            FailureReason = failureReason;
        }

        public static ModuleScanResult Complete(HashSet<string> modules) => new(modules, true);
        public static ModuleScanResult Incomplete(HashSet<string> modules, string? failureReason = null) => new(modules, false, true, failureReason);
    }

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

        public virtual ModuleScanResult ScanLoadedGraphicsModules(Process process)
        {
            // If a derived type overrode GetLoadedGraphicsModules (such as existing test fakes),
            // defer to GetLoadedGraphicsModules to preserve existing test seams.
            var method = GetType().GetMethod(nameof(GetLoadedGraphicsModules), new[] { typeof(Process) });
            if (method != null && method.DeclaringType != typeof(ModuleScanner))
            {
                var overriddenModules = GetLoadedGraphicsModules(process);
                return ModuleScanResult.Complete(overriddenModules);
            }

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
                return ModuleScanResult.Complete(found);
            }
            catch (Exception ex)
            {
                // Access denied or enumeration failure — return whatever was found before the failure, marked incomplete
                return ModuleScanResult.Incomplete(found, ex.GetType().Name);
            }
        }

        public virtual HashSet<string> GetLoadedGraphicsModules(Process process)
        {
            return ScanLoadedGraphicsModules(process).Modules;
        }

        // Games loading more than one graphics API at once is unusual and can indicate a
        // wrapper layer in play (e.g. dgVoodoo bridging legacy DirectDraw/D3D up to D3D11) —
        // worth knowing about even though it doesn't change classification today.
        public bool UsesMultipleApis(Process process)
        {
            var modules = GetLoadedGraphicsModules(process);
            int apiCount = 0;

            if (modules.Contains("d3d9.dll")) apiCount++;
            if (modules.Contains("d3d11.dll")) apiCount++;
            if (modules.Contains("vulkan-1.dll")) apiCount++;
            if (modules.Contains("d3d12.dll")) apiCount++;

            return apiCount > 1;
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
