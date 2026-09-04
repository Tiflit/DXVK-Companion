using System;
using System.IO;
using System.Threading.Tasks;

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

        public async Task<bool> SafeReplaceWithBackupAsync(string targetPath, string sourcePath)
        {
            int maxRetries = 5;
            int delayMs = 500;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    string backupPath = targetPath + ".bak";

                    if (File.Exists(targetPath) && !File.Exists(backupPath))
                    {
                        // copy existing file to .bak (do not overwrite an existing .bak)
                        File.Copy(targetPath, backupPath, overwrite: false);
                    }

                    // Copy new file into place
                    File.Copy(sourcePath, targetPath, overwrite: true);
                    return true;
                }
                catch (IOException)
                {
                    await Task.Delay(delayMs);
                    delayMs *= 2;
                }
                catch (UnauthorizedAccessException)
                {
                    // Permission denied — do not keep retrying silently
                    return false;
                }
            }

            return false;
        }

        public async Task<bool> SafeReplaceAsync(string targetPath, string sourcePath)
        {
            int maxRetries = 5;
            int delayMs = 500;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    if (File.Exists(targetPath))
                        File.Delete(targetPath);

                    File.Move(sourcePath, targetPath);
                    return true;
                }
                catch (IOException)
                {
                    await Task.Delay(delayMs);
                    delayMs *= 2;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }

            return false;
        }
    }
}
