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
            "discord.exe"
        };

        public bool IsGameProcess(Process process)
        {
            try
            {
                string path = process.MainModule?.FileName ?? "";
                string exe = Path.GetFileName(path).ToLower();

                if (string.IsNullOrWhiteSpace(path))
                    return false;

                if (Array.Exists(_launcherBlacklist, x => exe.Contains(x)))
                    return false;

                return exe.EndsWith(".exe");
            }
            catch
            {
                return false;
            }
        }
    }
}
