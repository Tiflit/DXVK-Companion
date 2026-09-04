using System;
using System.IO;
using System.Text;
using DXVKCompanion.Storage;

namespace DXVKCompanion.Utils
{
    public class Logger
    {
        private readonly object _lock = new();

        public void Log(string message)
        {
            try
            {
                Paths.EnsureDirectories();

                lock (_lock)
                {
                    File.AppendAllText(
                        Paths.LogFile,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}",
                        Encoding.UTF8
                    );
                }
            }
            catch
            {
                // Ignore logging failures
            }
        }
    }
}
