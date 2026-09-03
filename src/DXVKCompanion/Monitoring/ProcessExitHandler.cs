using System;
using System.Diagnostics;

namespace DXVKCompanion.Monitoring
{
    public class ProcessExitHandler
    {
        public event Action<Process>? OnGameExited;

        public void Attach(Process process)
        {
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => OnGameExited?.Invoke(process);
            }
            catch
            {
                // Some processes cannot be hooked; ignore
            }
        }
    }
}
