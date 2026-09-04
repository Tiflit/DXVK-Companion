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

            if (Array.Exists(LauncherNames, x => x == name))
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

    // NOTE: returns InspectionResult now (Safe / Risky / Unknown).
    // Access-denied or other failures return Unknown so the caller can be conservative.
    public InspectionResult HasAntiCheatRisk(Process process)
    {
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                string name = module.ModuleName.ToLowerInvariant();

                foreach (var ac in AntiCheatModules)
                {
                    if (name.Contains(ac))
                        return InspectionResult.Risky;
                }
            }

            // enumeration succeeded, and nothing suspicious found
            return InspectionResult.Safe;
        }
        catch (UnauthorizedAccessException)
        {
            // Access denied → process might be protected by anti-cheat; return Unknown
            return InspectionResult.Unknown;
        }
        catch
        {
            // Any other error during inspection should be treated conservatively
            return InspectionResult.Unknown;
        }
    }
}
