using System;
using System.IO;

namespace DXVKCompanion.Utils
{
    public class StartupManager
    {
        private readonly string _exePath;
        private readonly string _shortcutPath;

        public StartupManager()
        {
            _exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
            var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            _shortcutPath = Path.Combine(startupDir, "DXVK Companion.lnk");
        }

        public void EnableStartup()
        {
            try
            {
                CreateShortcut(_shortcutPath, _exePath);
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
                if (File.Exists(_shortcutPath))
                    File.Delete(_shortcutPath);
            }
            catch
            {
                // Ignore failures
            }
        }

        private void CreateShortcut(string shortcutPath, string targetPath)
        {
            var shell = new IWshRuntimeLibrary.WshShell();
            var shortcut = (IWshRuntimeLibrary.IWshShortcut)shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath)!;
            shortcut.WindowStyle = 1;
            shortcut.Description = "DXVK Companion";
            shortcut.Save();
        }
    }
}
