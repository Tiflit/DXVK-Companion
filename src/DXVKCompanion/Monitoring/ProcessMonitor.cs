using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DXVKCompanion.Models;
using DXVKCompanion.Storage;
using DXVKCompanion.Utils;

namespace DXVKCompanion.Monitoring
{
    public class ProcessMonitor : IDisposable
    {
        private const int MaxWindowWaitTicks = 5;

        private readonly ConcurrentDictionary<int, byte> _seenPids = new();
        private readonly ConcurrentDictionary<int, int> _windowRetryCounts = new();
        private readonly System.Threading.Timer _timer;
        private readonly GameDetector _detector;
        private readonly ProcessExitHandler _exitHandler;
        private readonly ApiClassifier _classifier;
        private readonly GameLibraryStore? _libraryStore;

        public event Action<Process>? OnGameDetected;
        public event Action<DetectionSnapshot>? OnSnapshotDetected;
        public event Action<string>? OnGameExited;

        public ProcessMonitor(
            GameDetector detector,
            ProcessExitHandler exitHandler,
            ApiClassifier? classifier = null,
            GameLibraryStore? libraryStore = null)
        {
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
            _exitHandler = exitHandler ?? throw new ArgumentNullException(nameof(exitHandler));
            _classifier = classifier ?? new ApiClassifier(new ModuleScanner(), new PeParser());
            _libraryStore = libraryStore;

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
            {
                if (!currentPids.Contains(pid))
                {
                    _seenPids.TryRemove(pid, out _);
                    _windowRetryCounts.TryRemove(pid, out _);
                }
            }

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
                        _seenPids[proc.Id] = 1;
                        proc.Dispose();
                        continue;
                    }

                    if (!_detector.HasWindow(proc))
                    {
                        int retries = _windowRetryCounts.AddOrUpdate(proc.Id, 1, (_, count) => count + 1);
                        if (retries > MaxWindowWaitTicks)
                        {
                            // Headless background process after grace period
                            _seenPids[proc.Id] = 1;
                            _windowRetryCounts.TryRemove(proc.Id, out _);
                        }
                        proc.Dispose();
                        continue;
                    }

                    _seenPids[proc.Id] = 1;
                    _windowRetryCounts.TryRemove(proc.Id, out _);

                    _exitHandler.Attach(proc);

                    var snapshot = CreateSnapshot(proc);
                    if (snapshot != null)
                    {
                        if (_libraryStore != null)
                        {
                            _libraryStore.RecordDetectionSnapshot(snapshot);
                        }

                        OnSnapshotDetected?.Invoke(snapshot);
                    }

                    OnGameDetected?.Invoke(proc);
                }
                catch
                {
                    proc.Dispose();
                }
            }
        }

        public DetectionSnapshot? CreateSnapshot(Process process)
        {
            string? exePath = null;
            try
            {
                exePath = process.MainModule?.FileName;
            }
            catch (Exception ex)
            {
                Logger.Log($"ProcessMonitor: could not read MainModule for PID {process.Id}: {ex.Message}");
            }

            if (string.IsNullOrEmpty(exePath))
            {
                return null;
            }

            string installationRoot;
            string relativePath;

            var existingInstallation = _libraryStore?.FindInstallationForExecutable(exePath);
            if (existingInstallation != null)
            {
                installationRoot = existingInstallation.InstallationPath;
                relativePath = Path.GetRelativePath(installationRoot, exePath);
            }
            else
            {
                installationRoot = Path.GetDirectoryName(exePath) ?? string.Empty;
                relativePath = Path.GetFileName(exePath);
            }

            var classification = _classifier.ClassifyDetailed(process, exePath);
            var antiCheat = _detector.AssessAntiCheatRisk(process, installationRoot);

            return new DetectionSnapshot
            {
                ProcessId = process.Id,
                ProcessName = process.ProcessName,
                ExecutablePath = exePath,
                InstallationRoot = installationRoot,
                ExecutableRelativePath = relativePath,
                Classification = classification,
                AntiCheat = antiCheat,
                TimestampUtc = DateTime.UtcNow
            };
        }

        public void Dispose() => _timer.Dispose();
    }
}
