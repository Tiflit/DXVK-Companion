using System;
using System.IO;
using DXVKCompanion.Storage;

namespace DXVKCompanion.Utils
{
    public static class Logger
    {
        private static readonly object _lock = new();

        public static void Log(string message)
        {
            try
            {
                Paths.EnsureDirectories();

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";

                lock (_lock)
                {
                    File.AppendAllText(Paths.LogFile, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never crash the app
            }
        }
    }
}
