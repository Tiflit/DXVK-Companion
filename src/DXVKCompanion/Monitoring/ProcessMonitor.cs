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
        private readonly Timer _timer;
        private readonly GameDetector _detector;
        private readonly ProcessExitHandler _exitHandler;

        public event Action<Process>? OnGameDetected;

        // Raised with the exe path of a previously-detected game once it exits.
        public event Action<string>? OnGameExited;

        public ProcessMonitor(GameDetector detector, ProcessExitHandler exitHandler)
        {
            _detector = detector;
            _exitHandler = exitHandler;
            _exitHandler.ProcessExited += exePath => OnGameExited?.Invoke(exePath);

            _timer = new Timer(PollProcesses, null, 0, 2000);
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

                    _seenPids[proc.Id] = 1;

                    if (!_detector.IsGameProcess(proc))
                    {
                        proc.Dispose();
                        continue;
                    }

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
