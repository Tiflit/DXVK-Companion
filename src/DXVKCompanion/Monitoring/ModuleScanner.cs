using System;
using System.Diagnostics;

namespace DXVKCompanion.Monitoring
{
    public class ModuleScanner
    {
        public bool UsesDirectX(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    string name = module.ModuleName.ToLowerInvariant();

                    if (name.Contains("d3d9.dll") ||
                        name.Contains("d3d10.dll") ||
                        name.Contains("d3d11.dll") ||
                        name.Contains("dxgi.dll"))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Access denied → fallback to PE parsing via ApiClassifier/PeParser
            }

            return false;
        }

        public bool UsesDx12(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    string name = module.ModuleName.ToLowerInvariant();

                    if (name.Contains("d3d12.dll"))
                        return true;
                }
            }
            catch
            {
                // Ignore
            }

            return false;
        }

        public bool UsesVulkan(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    string name = module.ModuleName.ToLowerInvariant();

                    if (name.Contains("vulkan-1.dll"))
                        return true;
                }
            }
            catch
            {
                // Ignore
            }

            return false;
        }

        public bool UsesOpenGL(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    string name = module.ModuleName.ToLowerInvariant();

                    if (name.Contains("opengl32.dll"))
                        return true;
                }
            }
            catch
            {
                // Ignore
            }

            return false;
        }

        public bool UsesDgVoodoo(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    string name = module.ModuleName.ToLowerInvariant();

                    if (name.Contains("dgvoodoo.dll") ||
                        name.Contains("ddraw.dll") ||
                        name.Contains("d3d8.dll"))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore
            }

            return false;
        }
    }
}
