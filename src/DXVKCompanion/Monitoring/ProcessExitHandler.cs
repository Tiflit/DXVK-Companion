using System;
using System.Diagnostics;

namespace DXVKCompanion.Monitoring
{
    public class ProcessExitHandler
    {
        // Raised when a process we attached to exits, with its exe path — lets subscribers
        // act on it without holding a live Process reference around.
        public event Action<string>? ProcessExited;

        public void Attach(Process process)
        {
            string? exePath = null;
            try
            {
                exePath = process.MainModule?.FileName;
            }
            catch
            {
                // Access denied — we still attach below, we just won't be able to report the path.
            }

            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    if (exePath != null)
                        ProcessExited?.Invoke(exePath);

                    process.Dispose();
                };
            }
            catch
            {
                // Ignore failures (e.g. process already exited before we could subscribe)
            }
        }
    }
}
