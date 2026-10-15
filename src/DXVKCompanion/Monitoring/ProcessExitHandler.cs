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
        public Process Process { get; }
        public EventHandler? Handler { get; set; }
        private int _delivered;

        public ProcessExitRegistration(Guid token, int processId, string? exePath, Process process)
        {
            Token = token;
            ProcessId = processId;
            ExePath = exePath;
            Process = process;
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

        internal Func<Process, string?>? ExePathResolver { get; set; }
        internal Func<Process, int>? ProcessIdResolver { get; set; }

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

        internal virtual int GetProcessId(Process process)
        {
            if (ProcessIdResolver != null) return ProcessIdResolver(process);
            try
            {
                return process.Id;
            }
            catch
            {
                return 0;
            }
        }

        internal virtual string? GetProcessExecutablePath(Process process)
        {
            if (ExePathResolver != null) return ExePathResolver(process);
            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        internal virtual Guid AttachCore(Process process, Guid registrationToken = default, string? knownExePath = null)
        {
            if (registrationToken == default)
            {
                registrationToken = Guid.NewGuid();
            }

            int pid = GetProcessId(process);
            string? exePath = knownExePath ?? GetProcessExecutablePath(process);

            var reg = RegisterToken(registrationToken, pid, exePath, process);

            EventHandler handler = (_, _) =>
            {
                CompleteExit(registrationToken);
            };

            reg.Handler = handler;

            // R2/R3: Setup step strictly isolated in try/catch.
            // If SubscribeProcessExit throws (before or after partial hooking), unwinds subscription, cancels registration, and returns Guid.Empty.
            try
            {
                SubscribeProcessExit(process, handler);
            }
            catch
            {
                try
                {
                    UnsubscribeProcessExit(process, handler);
                }
                catch
                {
                }

                CancelRegistration(registrationToken);
                return Guid.Empty;
            }

            // Check if process has already exited.
            bool hasExited = false;
            try
            {
                hasExited = CheckProcessHasExited(process);
            }
            catch
            {
                // Process check failed after subscription: unwind subscription and cancel registration
                try
                {
                    UnsubscribeProcessExit(process, handler);
                }
                catch
                {
                }

                CancelRegistration(registrationToken);
                return Guid.Empty;
            }

            if (hasExited)
            {
                // R3: Synchronous exit delivery.
                // Any subscriber exception will propagate out naturally!
                // Owned cleanup (unsubscription + process disposal) runs in finally inside CompleteExit.
                CompleteExit(registrationToken);
            }

            return registrationToken;
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

        internal ProcessExitRegistration RegisterToken(Guid registrationToken, int pid, string? exePath, Process? process = null)
        {
            if (registrationToken == default) registrationToken = Guid.NewGuid();
            var reg = new ProcessExitRegistration(registrationToken, pid, exePath, process ?? Process.GetCurrentProcess());
            lock (_exitLock)
            {
                _registrations[registrationToken] = reg;
            }
            return reg;
        }

        internal void CancelRegistration(Guid registrationToken)
        {
            if (registrationToken == Guid.Empty) return;
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
                return; // Already delivered or cancelled
            }

            // R2: Explicitly detach the subscription upon cancellation so delayed callback cannot fire
            if (reg.Handler != null)
            {
                try
                {
                    UnsubscribeProcessExit(reg.Process, reg.Handler);
                }
                catch
                {
                }
            }
        }

        internal void RaiseProcessExited(string exePath)
        {
            NotifyExitDirect(exePath);
        }

        internal void RaiseProcessInstanceExited(Guid registrationToken, int pid, string exePath)
        {
            if (registrationToken != Guid.Empty && HasRegistration(registrationToken))
            {
                CompleteExit(registrationToken);
            }
            else
            {
                NotifyExitDirect(exePath, registrationToken, pid);
            }
        }

        private void CompleteExit(Guid registrationToken)
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
                return; // Already delivered or cancelled — do not re-execute cleanup
            }

            try
            {
                // R2: Explicitly detach the subscription
                if (reg.Handler != null)
                {
                    try
                    {
                        UnsubscribeProcessExit(reg.Process, reg.Handler);
                    }
                    catch
                    {
                    }
                }

                // If executable path is missing/unreportable, do not raise misleading path events
                if (!string.IsNullOrEmpty(reg.ExePath))
                {
                    ProcessInstanceExited?.Invoke(reg.Token, reg.ProcessId, reg.ExePath);
                    ProcessExited?.Invoke(reg.ExePath);
                }
            }
            finally
            {
                // R2/R3: Owned cleanup runs in finally only when delivery/cleanup ownership was claimed
                DisposeProcess(reg.Process);
            }
        }

        private void NotifyExitDirect(string exePath, Guid token = default, int pid = 0)
        {
            if (string.IsNullOrEmpty(exePath)) return;
            ProcessInstanceExited?.Invoke(token, pid, exePath);
            ProcessExited?.Invoke(exePath);
        }

        internal virtual void DisposeProcess(Process process)
        {
            try { process.Dispose(); } catch { }
        }
    }
}
