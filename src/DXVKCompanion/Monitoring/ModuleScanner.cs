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
                    string name = module.ModuleName.ToLower();

                    if (name.Contains("d3d9.dll") ||
                        name.Contains("d3d11.dll"))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Access denied → fallback to PE parsing
            }

            return false;
        }
    }
}
