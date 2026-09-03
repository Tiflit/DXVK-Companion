using System;
using System.Diagnostics;
using System.IO;
using DXVKCompanion.Models;
using DXVKCompanion.Utils;

namespace DXVKCompanion.DXVK
{
    public class DxvkInstaller
    {
        private readonly FileUtils _files;
        private readonly DxvkConfigManager _config;

        public DxvkInstaller(FileUtils files, DxvkConfigManager config)
        {
            _files = files;
            _config = config;
        }

        public void Install(Process game, GameProfile profile, ReleaseInfo release)
        {
            string exePath = game.MainModule.FileName;
            string gameDir = Path.GetDirectoryName(exePath)!;

            string arch = profile.Architecture; // "x32" or "x64"
            string sourceDir = release.GetDllDirectory(arch);

            foreach (var dll in release.GetDllList(profile.Api))
            {
                string src = Path.Combine(sourceDir, dll);
                string dst = Path.Combine(gameDir, dll);

                _files.BackupIfNeeded(dst);
                _files.Copy(src, dst);
            }

            _config.WriteLocalConfig(gameDir, profile);
        }
    }
}
