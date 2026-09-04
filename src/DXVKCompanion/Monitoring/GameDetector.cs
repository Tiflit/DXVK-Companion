using System;
using System.Diagnostics;
using System.IO;

namespace DXVKCompanion.Monitoring
{
    public class GameDetector
    {
        private readonly string[] _launcherBlacklist =
        {
            "steam.exe",
            "epicgameslauncher.exe",
            "origin.exe",
            "eaapp.exe",
            "battle.net.exe",
            "ubisoftconnect.exe",
            "goggalaxy.exe"
        };

        private readonly string[] _systemBlacklist =
        {
            "explorer.exe",
            "cmd.exe",
            "powershell.exe",
            "conhost.exe",
            "svchost.exe",
            "rundll32.exe",
            "taskmgr.exe",
            "services.exe",
            "lsass.exe",
            "wininit.exe",
            "winlogon.exe",
            "dwm.exe",
            "searchapp.exe",
            "ctfmon.exe"
        };

        private readonly string[] _antiCheatProcesses =
        {
            "easyanticheat.exe",
            "eaclauncher.exe",
            "battleye.exe",
            "beservice.exe",
            "vgc.exe",
            "vgk.exe",
            "faceitclient.exe",
            "riotclientservices.exe"
        };

        public bool IsGameProcess(Process process)
        {
            try
            {
                string path = process.MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                string exe = Path.GetFileName(path).ToLowerInvariant();

                if (Array.Exists(_launcherBlacklist, x => exe.Contains(x)))
                    return false;

                if (Array.Exists(_systemBlacklist, x => exe.Equals(x)))
                    return false;

                // Heuristic: treat non-system, non-launcher .exe with a valid path as a potential game
                return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
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
                string exe = Path.GetFileName(process.MainModule?.FileName ?? string.Empty).ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(exe))
                    return false;

                if (Array.Exists(_antiCheatProcesses, x => exe.Contains(x)))
                    return true;

                foreach (ProcessModule module in process.Modules)
                {
                    string name = module.ModuleName.ToLowerInvariant();

                    if (name.Contains("easyanticheat") ||
                        name.Contains("battleye") ||
                        name.Contains("vgk") ||
                        name.Contains("vgc") ||
                        name.Contains("faceit") ||
                        name.Contains("riotclient"))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Access denied → assume no explicit anti-cheat detected
            }

            return false;
        }
    }
}
