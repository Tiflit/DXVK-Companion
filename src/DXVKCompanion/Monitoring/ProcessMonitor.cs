using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace DXVKCompanion.Monitoring
{
    public class ProcessMonitor : IDisposable
    {
        private readonly ConcurrentDictionary<int, byte> _seenPids = new();
        private readonly System.Threading.Timer _timer;
        private readonly GameDetector _detector;
        private readonly ProcessExitHandler _exitHandler;

        public event Action<Process>? OnGameDetected;
        public event Action<string>? OnGameExited;

        public ProcessMonitor(GameDetector detector, ProcessExitHandler exitHandler)
        {
            _detector = detector;
            _exitHandler = exitHandler;
            _exitHandler.ProcessExited += exePath => OnGameExited?.Invoke(exePath);
            _timer = new System.Threading.Timer(PollProcesses, null, 0, 2000);
        }

        private void PollProcesses(object? state)
        {
            Process[] processes;

            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                return;
            }

            var currentPids = processes.Select(p => p.Id).ToHashSet();

            foreach (var pid in _seenPids.Keys)
                if (!currentPids.Contains(pid))
                    _seenPids.TryRemove(pid, out _);

            foreach (var proc in processes)
            {
                try
                {
                    if (_seenPids.ContainsKey(proc.Id))
                    {
                        proc.Dispose();
                        continue;
                    }

                    if (!_detector.IsGameProcess(proc))
                    {
                        // Permanently excluded (launcher/system process) — never worth rechecking.
                        _seenPids[proc.Id] = 1;
                        proc.Dispose();
                        continue;
                    }

                    if (!_detector.HasWindow(proc))
                    {
                        // Might still be launching. Deliberately NOT marked as seen, so the next
                        // poll tick re-examines this same PID once its window appears.
                        proc.Dispose();
                        continue;
                    }

                    _seenPids[proc.Id] = 1;
                    _exitHandler.Attach(proc);
                    OnGameDetected?.Invoke(proc);
                }
                catch
                {
                    proc.Dispose();
                }
            }
        }

        public void Dispose() => _timer.Dispose();
    }
}
