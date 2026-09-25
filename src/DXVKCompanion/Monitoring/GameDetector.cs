using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DXVKCompanion.Models;

namespace DXVKCompanion.Monitoring
{
    public class GameDetector
    {
        private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            // Windows system and shell processes
            "system", "idle", "registry", "smss", "csrss", "wininit", "services", "lsass",
            "winlogon", "fontdrvhost", "dwm", "svchost", "taskhostw", "runtimebroker",
            "shellexperiencehost", "searchhost", "startmenuexperiencehost", "sihost",
            "ctfmon", "conhost", "dllhost", "wudfhost", "spoolsv", "audiodg", "explorer",
            "applicationframehost", "systemsettings", "securityhealthservice",

            // Game launchers and store clients
            "steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper", "origin",
            "originthinsetupinternal", "uplay", "upc", "goggalaxy", "galaxyclient",
            "eadesktop", "ealauncher", "battlenet", "riotclientux", "riotclientservices"
        };

        private static readonly string[] AntiCheatSignatures =
        {
            "easyanticheat", "eac", "eac_server", "battleye", "bedaisy", "beservice",
            "vgk", "vgc", "ricochet", "randgrid", "denuvo", "gamemon", "gameguard",
            "xigncode", "punkbuster", "pbsvc", "equ8", "ace-base", "anti-cheat-expert",
            "nvanti"
        };

        private static readonly string[] AntiCheatDirectorySignatures =
        {
            "easyanticheat", "battleye", "easyanticheat_setup.exe", "beservice.exe"
        };

        /// <summary>
        /// Permanent exclusion filter (system processes, launchers).
        /// </summary>
        public bool IsGameProcess(Process process)
        {
            try
            {
                string name = process.ProcessName.ToLowerInvariant();
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = Path.GetFileNameWithoutExtension(name);

                return !ExcludedProcessNames.Contains(name);
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

        public AntiCheatAssessment AssessAntiCheatRisk(Process process, string? exeDirectory = null)
        {
            var evidence = new List<string>();

            // 1. Inspect live process modules
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    string moduleName = module.ModuleName.ToLowerInvariant();
                    foreach (var signature in AntiCheatSignatures)
                    {
                        if (moduleName.Contains(signature))
                        {
                            evidence.Add($"Loaded module: {module.ModuleName}");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Module enumeration blocked by kernel driver or OS security
                return AntiCheatAssessment.UnableToDetermine(
                    $"Process module inspection was blocked ({ex.GetType().Name}): anti-tamper or security protection active.");
            }

            // 2. Inspect game directory indicators if path is available
            string? dir = exeDirectory;
            if (string.IsNullOrEmpty(dir))
            {
                try { dir = Path.GetDirectoryName(process.MainModule?.FileName); }
                catch { }
            }

            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                    {
                        string name = Path.GetFileName(entry).ToLowerInvariant();
                        foreach (var sig in AntiCheatDirectorySignatures)
                        {
                            if (name.Contains(sig))
                            {
                                evidence.Add($"Game directory contains anti-cheat component: {Path.GetFileName(entry)}");
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    // Directory enumeration non-fatal
                }
            }

            if (evidence.Count > 0)
            {
                return AntiCheatAssessment.SuspectedOrKnown(evidence.ToArray());
            }

            return AntiCheatAssessment.None();
        }

        public bool HasAntiCheatRisk(Process process)
        {
            var assessment = AssessAntiCheatRisk(process);
            return assessment.Risk != AntiCheatRisk.None;
        }
    }
}
