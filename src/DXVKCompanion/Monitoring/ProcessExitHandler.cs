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

        internal event Action<Guid, int, string>? ProcessInstanceExited;

        private readonly object _exitLock = new();
        private readonly HashSet<Guid> _notifiedTokens = new();

        public Guid Attach(Process process) => AttachCore(process, Guid.NewGuid());

        internal virtual Guid AttachCore(Process process, Guid registrationToken = default)
        {
            if (registrationToken == default)
            {
                registrationToken = Guid.NewGuid();
            }

            string? exePath = null;
            int pid = 0;
            try
            {
                pid = process.Id;
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
                    NotifyExit(registrationToken, pid, exePath);
                    DisposeProcess(process);
                };

                if (process.HasExited)
                {
                    NotifyExit(registrationToken, pid, exePath);
                    DisposeProcess(process);
                }
            }
            catch
            {
                // Ignore failures (e.g. process already exited before we could subscribe)
            }

            return registrationToken;
        }

        internal void RaiseProcessExited(string exePath)
        {
            NotifyExit(Guid.Empty, 0, exePath);
        }

        internal void RaiseProcessInstanceExited(Guid registrationToken, int pid, string exePath)
        {
            NotifyExit(registrationToken, pid, exePath);
        }

        private void NotifyExit(Guid registrationToken, int pid, string? exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return;
            lock (_exitLock)
            {
                if (registrationToken != Guid.Empty && !_notifiedTokens.Add(registrationToken))
                {
                    return; // Exactly-once delivery per registration token
                }
            }
            ProcessInstanceExited?.Invoke(registrationToken, pid, exePath);
            ProcessExited?.Invoke(exePath);
        }

        private void DisposeProcess(Process process)
        {
            try { process.Dispose(); } catch { }
        }
    }
}
