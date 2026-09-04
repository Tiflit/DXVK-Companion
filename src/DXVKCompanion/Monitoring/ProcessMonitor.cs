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

        public ProcessMonitor(GameDetector detector, ProcessExitHandler exitHandler)
        {
            _detector = detector;
            _exitHandler = exitHandler;

            // Poll every 2 seconds; low overhead for a tray app
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

            // Cleanup: remove PIDs that no longer exist
            foreach (var pid in _seenPids.Keys)
            {
                if (!currentPids.Contains(pid))
                    _seenPids.TryRemove(pid, out _);
            }

            foreach (var proc in processes)
            {
                if (_seenPids.ContainsKey(proc.Id))
                    continue;

                _seenPids[proc.Id] = 1;

                if (!_detector.IsGameProcess(proc))
                    continue;

                try
                {
                    _exitHandler.Attach(proc);
                    OnGameDetected?.Invoke(proc);
                }
                catch
                {
                    // Ignore failures for individual processes
                }
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
