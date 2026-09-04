using System;
using System.Diagnostics;

namespace DXVKCompanion.Monitoring
{
    public class ModuleScanner
    {
        public bool UsesDx9(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    string name = module.ModuleName.ToLowerInvariant();
                    if (name == "d3d9.dll")
                        return true;
                }
            }
            catch { }
            return false;
        }

        public bool UsesDx11(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    string name = module.ModuleName.ToLowerInvariant();
                    if (name == "d3d11.dll" || name == "dxgi.dll")
                        return true;
                }
            }
            catch { }
            return false;
        }

        public bool UsesDx12(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (module.ModuleName.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        public bool UsesVulkan(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (module.ModuleName.Equals("vulkan-1.dll", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        public bool UsesOpenGL(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (module.ModuleName.Equals("opengl32.dll", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }
    }
}
