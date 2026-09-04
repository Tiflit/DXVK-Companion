using Microsoft.Win32;

namespace DXVKCompanion.Utils
{
    // Rewritten to use the registry directly instead of Windows Script Host COM interop —
    // WSH needs an extra COM reference configured in the .csproj, which contradicted the
    // README's "zero external dependencies" goal and would fail to build without that setup.
    public class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DXVK Companion";

        private readonly string _exePath;

        public StartupManager()
        {
            _exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
        }

        public void EnableStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.SetValue(ValueName, $"\"{_exePath}\"");
            }
            catch
            {
                // Ignore failures
            }
        }

        public void DisableStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch
            {
                // Ignore failures
            }
        }
    }
}
