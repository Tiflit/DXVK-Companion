using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

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

        internal int ActiveRegistrationCount
        {
            get
            {
                lock (_exitLock)
                {
                    return _registrations.Count;
                }
            }
        }

        internal bool HasRegistration(Guid registrationToken)
        {
            if (registrationToken == Guid.Empty) return false;
            lock (_exitLock)
            {
                return _registrations.ContainsKey(registrationToken);
            }
        }

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

            EventHandler? handler = null;
            bool subscribed = false;
            try
            {
                handler = (_, _) =>
                {
                    try
                    {
                        NotifyExit(registrationToken, pid, exePath);
                    }
                    finally
                    {
                        DisposeProcess(process);
                    }
                };

                SubscribeProcessExit(process, handler);
                subscribed = true;

                if (CheckProcessHasExited(process))
                {
                    try
                    {
                        NotifyExit(registrationToken, pid, exePath);
                    }
                    finally
                    {
                        DisposeProcess(process);
                    }
                }

                return registrationToken;
            }
            catch
            {
                // Setup failed (e.g. Access Denied on EnableRaisingEvents, process already exited/invalid)
                if (subscribed && handler != null)
                {
                    UnsubscribeProcessExit(process, handler);
                }

                CancelRegistration(registrationToken);
                return Guid.Empty;
            }
        }

        internal virtual void SubscribeProcessExit(Process process, EventHandler handler)
        {
            process.EnableRaisingEvents = true;
            process.Exited += handler;
        }

        internal virtual void UnsubscribeProcessExit(Process process, EventHandler handler)
        {
            try
            {
                process.Exited -= handler;
            }
            catch
            {
            }
        }

        internal virtual bool CheckProcessHasExited(Process process)
        {
            return process.HasExited;
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

            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            ProcessInstanceExited?.Invoke(registrationToken, pid, exePath);
            ProcessExited?.Invoke(exePath);
        }

        internal virtual void DisposeProcess(Process process)
        {
            try { process.Dispose(); } catch { }
        }
    }
}
