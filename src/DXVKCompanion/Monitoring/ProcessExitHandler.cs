using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DXVKCompanion.Monitoring
{
    public class ProcessExitHandler
    {
        // Raised when a process we attached to exits, with its exe path — lets subscribers
        // act on it without holding a live Process reference around.
        public event Action<string>? ProcessExited;

        internal event Action<int, string>? ProcessInstanceExited;

        private readonly object _exitLock = new();
        private readonly HashSet<int> _notifiedPids = new();

        public void Attach(Process process) => AttachCore(process);

        internal virtual void AttachCore(Process process)
        {
            string? exePath = null;
            int pid = 0;
            try
            {
                pid = process.Id;
                exePath = process.MainModule?.FileName;
                lock (_exitLock)
                {
                    if (pid > 0)
                    {
                        _notifiedPids.Remove(pid);
                    }
                }
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
                    NotifyExit(pid, exePath);
                    DisposeProcess(process);
                };

                if (process.HasExited)
                {
                    NotifyExit(pid, exePath);
                    DisposeProcess(process);
                }
            }
            catch
            {
                // Ignore failures (e.g. process already exited before we could subscribe)
            }
        }

        internal void RaiseProcessExited(string exePath)
        {
            NotifyExit(0, exePath);
        }

        internal void RaiseProcessInstanceExited(int pid, string exePath)
        {
            NotifyExit(pid, exePath);
        }

        private void NotifyExit(int pid, string? exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return;
            lock (_exitLock)
            {
                if (pid > 0 && !_notifiedPids.Add(pid))
                {
                    return; // Exactly-once delivery per PID instance
                }
            }
            ProcessInstanceExited?.Invoke(pid, exePath);
            ProcessExited?.Invoke(exePath);
        }

        private void DisposeProcess(Process process)
        {
            try { process.Dispose(); } catch { }
        }
    }
}
