using System;
using System.Diagnostics;
using System.Linq;

namespace DXVKCompanion.Monitoring
{
    public class GameDetector
    {
        private static readonly string[] AntiCheatModules =
        {
            "easyanticheat.dll", "eac.dll", "battleye.dll", "bedaisy.sys",
            "riotclientservices.exe", "vgk.sys", "nvanti.dll"
        };

        private static readonly string[] LauncherNames =
        {
            "steam.exe", "epicgameslauncher.exe", "origin.exe", "uplay.exe", "goggalaxy.exe"
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

                // Requires a visible top-level window. Filters out most background
                // processes/services, which were previously able to silently become the
                // tray menu's "active game" just by being launched while a real game was running.
                if (process.MainWindowHandle == IntPtr.Zero)
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
                        if (name.Contains(ac))
                            return true;
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
