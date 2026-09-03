using System;
using System.IO;

namespace DXVKCompanion.Utils
{
    public class FileUtils
    {
        public void BackupIfNeeded(string path)
        {
            if (!File.Exists(path))
                return;

            string backup = path + ".bak";

            if (!File.Exists(backup))
                File.Move(path, backup);
        }

        public void RestoreBackup(string path)
        {
            string backup = path + ".bak";

            if (File.Exists(backup))
            {
                if (File.Exists(path))
                    File.Delete(path);

                File.Move(backup, path);
            }
        }

        public void Copy(string src, string dst)
        {
            File.Copy(src, dst, overwrite: true);
        }

        public void WriteBytes(string dst, byte[] bytes)
        {
            File.WriteAllBytes(dst, bytes);
        }
    }
}
