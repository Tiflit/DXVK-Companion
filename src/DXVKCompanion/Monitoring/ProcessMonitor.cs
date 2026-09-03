using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace DXVKCompanion.Monitoring
{
    public class ProcessMonitor
    {
        private readonly HashSet<int> _seenPids = new();
        private readonly Timer _timer;
        private readonly GameDetector _detector;
        private readonly ProcessExitHandler _exitHandler;

        public event Action<Process>? OnGameDetected;

        public ProcessMonitor(GameDetector detector, ProcessExitHandler exitHandler)
        {
            _detector = detector;
            _exitHandler = exitHandler;

            _timer = new Timer(PollProcesses, null, 0, 2000);
        }

        private void PollProcesses(object? state)
        {
            foreach (var proc in Process.GetProcesses())
            {
                if (_seenPids.Contains(proc.Id))
                    continue;

                _seenPids.Add(proc.Id);

                if (_detector.IsGameProcess(proc))
                {
                    _exitHandler.Attach(proc);
                    OnGameDetected?.Invoke(proc);
                }
            }
        }
    }
}
