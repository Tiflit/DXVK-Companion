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
                    if (name.Contains("d3d9.dll") || name.Contains("d3d10.dll") ||
                        name.Contains("d3d11.dll") || name.Contains("dxgi.dll"))
                        return true;
                }
            }
            catch { }

            return false;
        }

        public bool UsesDx9(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                    if (module.ModuleName.ToLowerInvariant().Contains("d3d9.dll"))
                        return true;
            }
            catch { }

            return false;
        }

        public bool UsesDx10(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                    if (module.ModuleName.ToLowerInvariant().Contains("d3d10.dll"))
                        return true;
            }
            catch { }

            return false;
        }

        public bool UsesDx11(Process process)
        {
            try
            {
                // Requires d3d11.dll specifically — dxgi.dll alone is also loaded by DX10
                // and DX12 titles, which was previously causing DX12 games to be
                // misclassified as DX11.
                foreach (ProcessModule module in process.Modules)
                    if (module.ModuleName.ToLowerInvariant().Contains("d3d11.dll"))
                        return true;
            }
            catch { }

            return false;
        }

        public bool UsesDx12(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                    if (module.ModuleName.ToLowerInvariant().Contains("d3d12.dll"))
                        return true;
            }
            catch { }

            return false;
        }

        public bool UsesVulkan(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                    if (module.ModuleName.ToLowerInvariant().Contains("vulkan-1.dll"))
                        return true;
            }
            catch { }

            return false;
        }

        public bool UsesOpenGL(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                    if (module.ModuleName.ToLowerInvariant().Contains("opengl32.dll"))
                        return true;
            }
            catch { }

            return false;
        }

        public bool UsesDgVoodoo(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    string name = module.ModuleName.ToLowerInvariant();
                    if (name.Contains("dgvoodoo.dll") || name.Contains("ddraw.dll") || name.Contains("d3d8.dll"))
                        return true;
                }
            }
            catch { }

            return false;
        }
    }
}
