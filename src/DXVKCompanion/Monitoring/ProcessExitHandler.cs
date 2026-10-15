using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DXVKCompanion.Monitoring
{
    internal sealed class ProcessExitRegistration
    {
        public Guid Token { get; }
        public int ProcessId { get; }
        public string? ExePath { get; }
        private int _delivered;

        public ProcessExitRegistration(Guid token, int processId, string? exePath)
        {
            Token = token;
            ProcessId = processId;
            ExePath = exePath;
        }

        public bool TryClaimDelivery() => Interlocked.Exchange(ref _delivered, 1) == 0;
    }

    public class ProcessExitHandler
    {
        // Raised when a process we attached to exits, with its exe path — lets subscribers
        // act on it without holding a live Process reference around.
        public event Action<string>? ProcessExited;

        internal event Action<Guid, int, string>? ProcessInstanceExited;

        private readonly object _exitLock = new();
        private readonly Dictionary<Guid, ProcessExitRegistration> _registrations = new();

        public void Attach(Process process) => AttachCore(process, Guid.NewGuid());

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

            RegisterToken(registrationToken, pid, exePath);

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

        internal Guid RegisterToken(Guid registrationToken, int pid, string? exePath)
        {
            if (registrationToken == default) registrationToken = Guid.NewGuid();
            var reg = new ProcessExitRegistration(registrationToken, pid, exePath);
            lock (_exitLock)
            {
                _registrations[registrationToken] = reg;
            }
            return registrationToken;
        }

        internal void CancelRegistration(Guid registrationToken)
        {
            if (registrationToken == Guid.Empty) return;
            lock (_exitLock)
            {
                _registrations.Remove(registrationToken);
            }
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

            if (registrationToken != Guid.Empty)
            {
                ProcessExitRegistration? reg = null;
                lock (_exitLock)
                {
                    if (_registrations.TryGetValue(registrationToken, out reg))
                    {
                        _registrations.Remove(registrationToken);
                    }
                }

                if (reg == null || !reg.TryClaimDelivery())
                {
                    return; // Already delivered or unregistered (released upon completion/cancellation)
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
