using System;
using System.Diagnostics;
using System.IO;
using DXVKCompanion.Models;
using DXVKCompanion.Utils;

namespace DXVKCompanion.DXVK
{
    public class DxvkRollback
    {
        private readonly FileUtils _files;

        public DxvkRollback(FileUtils files)
        {
            _files = files;
        }

        public void Restore(Process game, GameProfile profile)
        {
            string exePath = game.MainModule.FileName;
            string gameDir = Path.GetDirectoryName(exePath)!;

            foreach (var dll in profile.InstalledDlls)
            {
                string path = Path.Combine(gameDir, dll);
                _files.RestoreBackup(path);
            }

            string conf = Path.Combine(gameDir, "dxvk.conf");
            if (File.Exists(conf))
                File.Delete(conf);
        }
    }
}
