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

        private static readonly long StartTimestamp = Stopwatch.GetTimestamp();
        private static TimeSpan DefaultClock()
        {
            long elapsed = Stopwatch.GetTimestamp() - StartTimestamp;
            return TimeSpan.FromSeconds((double)elapsed / Stopwatch.Frequency);
        }

        private readonly ConcurrentDictionary<int, byte> _nonGamePids = new();
        private readonly ConcurrentDictionary<int, CandidateProcessState> _candidates = new();
        private readonly ConcurrentDictionary<int, int> _windowRetryCounts = new();
        private readonly System.Threading.Timer? _timer;
        private readonly GameDetector _detector;
        private readonly ProcessExitHandler _exitHandler;
        private readonly ApiClassifier _classifier;
        private readonly GameLibraryStore? _libraryStore;
        private readonly Func<TimeSpan> _clock;
        private readonly Func<IEnumerable<Process>>? _processEnumerator;

        public event Action<Process>? OnGameDetected;
        public event Action<DetectionSnapshot>? OnSnapshotDetected;
        public event Action<string>? OnGameExited;

        internal event Action<Process, DetectionSnapshot>? OnQualifiedGameDetected;
        internal ProfileStore? ProfileStore { get; set; }

        public ProcessMonitor(
            GameDetector detector,
            ProcessExitHandler exitHandler,
            ApiClassifier? classifier = null,
            GameLibraryStore? libraryStore = null)
            : this(detector, exitHandler, classifier, libraryStore, null, null, true)
        {
        }

        internal ProcessMonitor(
            GameDetector detector,
            ProcessExitHandler exitHandler,
            ApiClassifier? classifier,
            GameLibraryStore? libraryStore,
            Func<TimeSpan>? clock,
            Func<IEnumerable<Process>>? processEnumerator,
            bool startTimer = false)
        {
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
            _exitHandler = exitHandler ?? throw new ArgumentNullException(nameof(exitHandler));
            _classifier = classifier ?? new ApiClassifier(new ModuleScanner(), new PeParser());
            _libraryStore = libraryStore;
            _clock = clock ?? DefaultClock;
            _processEnumerator = processEnumerator;

            _exitHandler.ProcessExited += exePath => OnGameExited?.Invoke(exePath);

            if (startTimer)
            {
                _timer = new System.Threading.Timer(PollProcesses, null, 0, 2000);
            }
        }

        internal bool TryGetQualifiedSnapshot(int processId, out DetectionSnapshot? snapshot)
        {
            if (_candidates.TryGetValue(processId, out var state) && state.IsQualified && state.QualifiedSnapshot != null)
            {
                snapshot = state.QualifiedSnapshot;
                return true;
            }

            snapshot = null;
            return false;
        }

        internal CandidateProcessState? GetCandidateState(int processId) =>
            _candidates.TryGetValue(processId, out var state) ? state : null;

        internal void PollProcessesOnce() => PollProcesses(null);

        private void PollProcesses(object? state)
        {
            IEnumerable<Process> processes;

            try
            {
                processes = _processEnumerator?.Invoke() ?? Process.GetProcesses();
            }
            catch
            {
                return;
            }

            var processList = processes.ToList();
            var currentPids = processList.Select(p => p.Id).ToHashSet();

            // Clean up transient state for exited or missing processes
            foreach (var pid in _nonGamePids.Keys)
            {
                if (!currentPids.Contains(pid))
                {
                    _nonGamePids.TryRemove(pid, out _);
                }
            }

            foreach (var pid in _candidates.Keys)
            {
                if (!currentPids.Contains(pid))
                {
                    _candidates.TryRemove(pid, out _);
                    _windowRetryCounts.TryRemove(pid, out _);
                }
            }

            var now = _clock();

            foreach (var proc in processList)
            {
                try
                {
                    if (_nonGamePids.ContainsKey(proc.Id))
                    {
                        proc.Dispose();
                        continue;
                    }

                    if (!_detector.IsGameProcess(proc))
                    {
                        _nonGamePids[proc.Id] = 1;
                        proc.Dispose();
                        continue;
                    }

                    if (!_detector.HasWindow(proc))
                    {
                        int retries = _windowRetryCounts.AddOrUpdate(proc.Id, 1, (_, count) => count + 1);
                        if (retries > MaxWindowWaitTicks)
                        {
                            _nonGamePids[proc.Id] = 1;
                            _windowRetryCounts.TryRemove(proc.Id, out _);
                        }
                        proc.Dispose();
                        continue;
                    }

                    _windowRetryCounts.TryRemove(proc.Id, out _);

                    // Candidate process with window: track qualification state
                    DateTime startTime = DateTime.MinValue;
                    try { startTime = proc.StartTime; } catch { }

                    var candidate = _candidates.GetOrAdd(proc.Id, id => new CandidateProcessState(id, startTime, now));
                    if (candidate.ProcessStartTime != DateTime.MinValue && startTime != DateTime.MinValue && candidate.ProcessStartTime != startTime)
                    {
                        // PID was reused by OS for a new process: reset assessment history
                        candidate = new CandidateProcessState(proc.Id, startTime, now);
                        _candidates[proc.Id] = candidate;
                    }

                    if (candidate.IsQualified || candidate.IsExpired)
                    {
                        proc.Dispose();
                        continue;
                    }

                    if (!candidate.IsAssessmentDue(now))
                    {
                        proc.Dispose();
                        continue;
                    }

                    // Assessment is due on the monotonic schedule
                    var snapshot = CreateSnapshot(proc);
                    bool qualifies = snapshot != null && ProcessQualification.IsRecognizedGraphicsApi(snapshot.Classification.PrimaryApi);

                    // Preserve exit tracking for known installations with pending actions
                    string? exePath = snapshot?.ExecutablePath;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        var knownInstallation = _libraryStore?.FindInstallationForExecutable(exePath);
                        if (knownInstallation?.PendingAction != null)
                        {
                            _exitHandler.Attach(proc);
                        }
                    }

                    if (qualifies && snapshot != null)
                    {
                        candidate.IsQualified = true;
                        candidate.QualifiedSnapshot = snapshot;

                        _exitHandler.Attach(proc);

                        if (_libraryStore != null)
                        {
                            _libraryStore.RecordDetectionSnapshot(snapshot);
                        }

                        OnSnapshotDetected?.Invoke(snapshot);
                        OnQualifiedGameDetected?.Invoke(proc, snapshot);
                        OnGameDetected?.Invoke(proc);
                    }
                    else
                    {
                        candidate.LastInspectionHadFailure = (snapshot?.Classification.EvidenceSource == "RuntimeIncomplete");
                        candidate.AdvanceAfterAssessment(now);

                        if (candidate.IsExpired)
                        {
                            string safeName = GetSafeProcessName(proc);
                            if (candidate.LastInspectionHadFailure)
                            {
                                Logger.Log($"Candidate process PID {proc.Id} ({safeName}) qualification budget expired: inspection unresolved.");
                            }
                            else
                            {
                                Logger.Log($"Candidate process PID {proc.Id} ({safeName}) qualification budget expired: no recognized graphics API evidence observed.");
                            }
                        }
                    }

                    proc.Dispose();
                }
                catch
                {
                    proc.Dispose();
                }
            }
        }

        private static string GetSafeProcessName(Process proc)
        {
            try
            {
                return proc.ProcessName;
            }
            catch
            {
                return "Unknown";
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
                Logger.Log($"ProcessMonitor: could not read MainModule for PID {process.Id}: {ex.GetType().Name}");
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

        public void Dispose()
        {
            _timer?.Dispose();
            _candidates.Clear();
            _nonGamePids.Clear();
            _windowRetryCounts.Clear();
        }
}
