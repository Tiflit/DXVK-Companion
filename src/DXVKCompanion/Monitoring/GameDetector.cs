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

        /// <summary>
        /// Permanent exclusion filter only (launchers, system processes). Deliberately does NOT
        /// check for a window here — that's HasWindow below, kept separate so ProcessMonitor can
        /// tell "never a game" (safe to permanently ignore) apart from "might still be launching"
        /// (needs rechecking on a later poll tick).
        /// </summary>
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

        public bool HasWindow(Process process)
        {
            try
            {
                return process.MainWindowHandle != IntPtr.Zero;
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

                return false; // successfully enumerated every module, genuinely none matched
            }
            catch
            {
                // Module enumeration blocked. Kernel-mode anti-cheat drivers intentionally
                // block exactly this kind of introspection as an anti-tampering measure, so
                // being blocked is itself correlated with anti-cheat presence — assume risk
                // rather than assume safe. A false warning costs a click; a missed one risks a ban.
                return true;
            }
        }
    }
}
