using System;
using System.IO;
using System.Threading.Tasks;
using DXVKCompanion.Utils;

namespace DXVKCompanion.Utils
{
    public class FileUtils
    {
        public void BackupIfNeeded(string path)
        {
            if (!File.Exists(path)) return;
            string backup = path + ".bak";
            if (!File.Exists(backup))
                File.Move(path, backup);
        }

        public void RestoreBackup(string path)
        {
            string backup = path + ".bak";
            if (File.Exists(backup))
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(backup, path);
            }
        }

        public void Copy(string src, string dst) => File.Copy(src, dst, overwrite: true);

        public void WriteBytes(string dst, byte[] bytes) => File.WriteAllBytes(dst, bytes);

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
                        // If another process wins a race to create this file first,
                        // File.Copy(overwrite:false) throws — caught by the outer catch below,
                        // which aborts THIS ENTIRE ITERATION before ever reaching the
                        // destructive overwrite further down. The next retry re-checks
                        // File.Exists(backupPath) and proceeds safely once it's actually there.
                        // Deliberately not narrowing this further — any failure here, race or
                        // otherwise, should abort this attempt rather than risk an overwrite
                        // with no confirmed-good backup in place.
                        File.Copy(targetPath, backupPath, overwrite: false);
                    }

                    File.Copy(sourcePath, targetPath, overwrite: true);
                    return true;
                }
                catch (IOException ex)
                {
                    Logger.Log($"SafeReplaceWithBackupAsync: attempt {i + 1}/{maxRetries} for {targetPath} failed: {ex.Message}");
                    await Task.Delay(delayMs);
                    delayMs *= 2;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Logger.Log($"SafeReplaceWithBackupAsync: permission denied for {targetPath}: {ex.Message}");
                    return false;
                }
            }

            Logger.Log($"SafeReplaceWithBackupAsync: giving up on {targetPath} after {maxRetries} attempts.");
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
                    if (File.Exists(targetPath)) File.Delete(targetPath);
                    File.Move(sourcePath, targetPath);
                    return true;
                }
                catch (IOException ex)
                {
                    Logger.Log($"SafeReplaceAsync: attempt {i + 1}/{maxRetries} for {targetPath} failed: {ex.Message}");
                    await Task.Delay(delayMs);
                    delayMs *= 2;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Logger.Log($"SafeReplaceAsync: permission denied for {targetPath}: {ex.Message}");
                    return false;
                }
            }

            Logger.Log($"SafeReplaceAsync: giving up on {targetPath} after {maxRetries} attempts.");
            return false;
        }
    }
}
