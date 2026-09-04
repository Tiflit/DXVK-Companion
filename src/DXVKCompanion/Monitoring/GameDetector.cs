using System;
using System.Diagnostics;

namespace DXVKCompanion.Monitoring
{
    public class GameDetector
    {
        private static readonly string[] AntiCheatModules =
        {
            "easyanticheat.dll",
            "eac.dll",
            "battleye.dll",
            "bedaisy.sys",
            "riotclientservices.exe",
            "vgk.sys",
            "nvanti.dll"
        };

        private static readonly string[] LauncherNames =
        {
            "steam.exe",
            "epicgameslauncher.exe",
            "origin.exe",
            "uplay.exe",
            "goggalaxy.exe"
        };

        public bool IsGameProcess(Process process)
        {
            try
            {
                string name = process.ProcessName.ToLowerInvariant();

                if (LauncherNames.Contains(name))
                    return false;

                if (name == "explorer" || name == "system" || name == "idle")
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool HasAntiCheatRisk(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    string name = module.ModuleName.ToLowerInvariant();

                    foreach (var ac in AntiCheatModules)
                    {
                        if (name.Contains(ac))
                            return true;
                    }
                }
            }
            catch
            {
                // Access denied → assume safe
            }

            return false;
        }
    }
}
