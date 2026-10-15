using System;
using System.Collections.Generic;
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

        private readonly object _pollLock = new();
        private volatile bool _disposed;

        private readonly Dictionary<int, DateTime> _nonGamePids = new();
        private readonly Dictionary<int, CandidateProcessState> _candidates = new();
        private readonly Dictionary<int, (DateTime StartTime, int RetryCount)> _windowRetries = new();
        private readonly Dictionary<int, (Process Process, DateTime StartTime, string ExePath)> _attachedExitProcesses = new();

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

        internal Func<Process, string?>? ExePathResolver { get; set; }
        internal Func<Process, DateTime>? StartTimeResolver { get; set; }
        internal Func<Process, bool>? GameProcessFilter { get; set; }
        internal Func<Process, bool>? WindowFilter { get; set; }
        internal Func<Process, string, AntiCheatAssessment>? AntiCheatAssessor { get; set; }

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

            _exitHandler.ProcessExited += exePath =>
            {
                lock (_pollLock)
                {
                    var match = _attachedExitProcesses.FirstOrDefault(kv => string.Equals(kv.Value.ExePath, exePath, StringComparison.OrdinalIgnoreCase));
                    if (match.Key != 0)
                    {
                        _attachedExitProcesses.Remove(match.Key);
                    }
                }
                OnGameExited?.Invoke(exePath);
            };

            if (startTimer)
            {
                _timer = new System.Threading.Timer(PollProcesses, null, 0, 2000);
            }
        }

        internal bool TryGetQualifiedSnapshot(int processId, out DetectionSnapshot? snapshot)
        {
            lock (_pollLock)
            {
                if (_candidates.TryGetValue(processId, out var state) && state.IsQualified && state.QualifiedSnapshot != null)
                {
                    snapshot = state.QualifiedSnapshot;
                    return true;
                }
            }

            snapshot = null;
            return false;
        }

        internal CandidateProcessState? GetCandidateState(int processId)
        {
            lock (_pollLock)
            {
                return _candidates.TryGetValue(processId, out var state) ? state : null;
            }
        }

        internal void PollProcessesOnce() => PollProcesses(null);

        private void PollProcesses(object? state)
        {
            if (_disposed) return;
            if (!Monitor.TryEnter(_pollLock)) return;

            try
            {
                if (_disposed) return;

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

                // Clean up transient state for exited or missing processes across all collections
                var staleNonGames = _nonGamePids.Keys.Where(p => !currentPids.Contains(p)).ToList();
                foreach (var p in staleNonGames) _nonGamePids.Remove(p);

                var staleRetries = _windowRetries.Keys.Where(p => !currentPids.Contains(p)).ToList();
                foreach (var p in staleRetries) _windowRetries.Remove(p);

                var staleCandidates = _candidates.Keys.Where(p => !currentPids.Contains(p)).ToList();
                foreach (var p in staleCandidates) _candidates.Remove(p);

                var staleAttached = _attachedExitProcesses.Where(kv => !currentPids.Contains(kv.Key)).ToList();
                foreach (var (pid, item) in staleAttached)
                {
                    if (item.Process.HasExited)
                    {
                        _attachedExitProcesses.Remove(pid);
                    }
                }

                var now = _clock();

                foreach (var proc in processList)
                {
                    if (_disposed)
                    {
                        if (!IsCurrentInstanceAttached(proc)) proc.Dispose();
                        continue;
                    }

                    try
                    {
                        DateTime startTime = TryGetStartTime(proc);
                        string? exePath = TryGetExecutablePath(proc);

                        // R5: Preserve exit tracking for known installations with pending actions,
                        // even before checking window presence or game classification.
                        if (!string.IsNullOrEmpty(exePath) && _libraryStore != null)
                        {
                            var knownInst = _libraryStore.FindInstallationForExecutable(exePath);
                            if (knownInst?.PendingAction != null)
                            {
                                AttachExitHandlerOnce(proc, startTime, exePath);
                            }
                        }

                        // R5: Process-instance-aware non-game filtering with start-time identity
                        if (_nonGamePids.TryGetValue(proc.Id, out var cachedStart))
                        {
                            if (startTime != DateTime.MinValue && cachedStart != DateTime.MinValue && startTime != cachedStart)
                            {
                                // PID was reused by a new process instance! Reset cached state
                                _nonGamePids.Remove(proc.Id);
                            }
                            else if (startTime == cachedStart && startTime != DateTime.MinValue)
                            {
                                if (!IsCurrentInstanceAttached(proc)) proc.Dispose();
                                continue;
                            }
                        }

                        bool isGame = GameProcessFilter?.Invoke(proc) ?? _detector.IsGameProcess(proc);
                        if (!isGame)
                        {
                            _nonGamePids[proc.Id] = startTime;
                            if (!IsCurrentInstanceAttached(proc)) proc.Dispose();
                            continue;
                        }

                        // R5: Window check with instance-aware retries and retirement
                        bool hasWindow = WindowFilter?.Invoke(proc) ?? _detector.HasWindow(proc);
                        if (!hasWindow)
                        {
                            if (_windowRetries.TryGetValue(proc.Id, out var retryEntry))
                            {
                                if (startTime != DateTime.MinValue && retryEntry.StartTime != DateTime.MinValue && startTime != retryEntry.StartTime)
                                {
                                    retryEntry = (startTime, 0);
                                }
                            }
                            else
                            {
                                retryEntry = (startTime, 0);
                            }

                            int newRetries = retryEntry.RetryCount + 1;
                            if (newRetries > MaxWindowWaitTicks)
                            {
                                _nonGamePids[proc.Id] = startTime;
                                _windowRetries.Remove(proc.Id);
                            }
                            else
                            {
                                _windowRetries[proc.Id] = (startTime, newRetries);
                            }

                            if (!IsCurrentInstanceAttached(proc)) proc.Dispose();
                            continue;
                        }

                        _windowRetries.Remove(proc.Id);

                        // Candidate process with window: track qualification state
                        if (_candidates.TryGetValue(proc.Id, out var candidate))
                        {
                            if (startTime != DateTime.MinValue && candidate.ProcessStartTime != DateTime.MinValue && startTime != candidate.ProcessStartTime)
                            {
                                // PID reused by OS: reset candidate state
                                candidate = new CandidateProcessState(proc.Id, startTime, now);
                                _candidates[proc.Id] = candidate;
                            }
                        }
                        else
                        {
                            candidate = new CandidateProcessState(proc.Id, startTime, now);
                            _candidates[proc.Id] = candidate;
                        }

                        if (candidate.IsQualified || candidate.IsExpired)
                        {
                            if (!IsCurrentInstanceAttached(proc)) proc.Dispose();
                            continue;
                        }

                        if (!candidate.IsAssessmentDue(now))
                        {
                            if (!IsCurrentInstanceAttached(proc)) proc.Dispose();
                            continue;
                        }

                        // Assessment is due on monotonic schedule
                        DetectionSnapshot? snapshot = null;
                        try
                        {
                            snapshot = CreateSnapshot(proc);
                        }
                        catch (Exception ex)
                        {
                            candidate.LastInspectionHadFailure = true;
                            Logger.Log($"ProcessMonitor: snapshot assessment failed for PID {proc.Id}: {ex.GetType().Name}");
                        }

                        bool qualifies = snapshot != null && ProcessQualification.IsRecognizedGraphicsApi(snapshot.Classification.PrimaryApi);

                        if (qualifies && snapshot != null)
                        {
                            candidate.IsQualified = true;
                            candidate.QualifiedSnapshot = snapshot;

                            AttachExitHandlerOnce(proc, startTime, snapshot.ExecutablePath);

                            if (_libraryStore != null)
                            {
                                _libraryStore.RecordDetectionSnapshot(snapshot);
                            }

                            if (!_disposed)
                            {
                                OnSnapshotDetected?.Invoke(snapshot);
                                OnQualifiedGameDetected?.Invoke(proc, snapshot);
                                OnGameDetected?.Invoke(proc);
                            }
                        }
                        else
                        {
                            candidate.LastInspectionHadFailure = (snapshot == null) ||
                                (snapshot.Classification.EvidenceSource == "RuntimeIncomplete") ||
                                (snapshot.Classification.Evidence.Any(e => e.Contains("scan incomplete", StringComparison.OrdinalIgnoreCase)));

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

                        if (!IsCurrentInstanceAttached(proc))
                        {
                            proc.Dispose();
                        }
                    }
                    catch
                    {
                        if (!IsCurrentInstanceAttached(proc))
                        {
                            proc.Dispose();
                        }
                    }
                }
            }
            finally
            {
                Monitor.Exit(_pollLock);
            }
        }

        private bool AttachExitHandlerOnce(Process proc, DateTime startTime, string exePath)
        {
            if (_attachedExitProcesses.TryGetValue(proc.Id, out var existing))
            {
                if (startTime != DateTime.MinValue && existing.StartTime != DateTime.MinValue)
                {
                    if (startTime == existing.StartTime) return false;
                }
                else if (!existing.Process.HasExited && string.Equals(existing.ExePath, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            _attachedExitProcesses[proc.Id] = (proc, startTime, exePath);
            _exitHandler.Attach(proc);
            return true;
        }

        private bool IsCurrentInstanceAttached(Process proc)
        {
            if (_attachedExitProcesses.TryGetValue(proc.Id, out var existing))
            {
                return ReferenceEquals(existing.Process, proc);
            }
            return false;
        }

        private string? TryGetExecutablePath(Process proc)
        {
            if (ExePathResolver != null) return ExePathResolver(proc);
            try { return proc.MainModule?.FileName; } catch { return null; }
        }

        private DateTime TryGetStartTime(Process proc)
        {
            if (StartTimeResolver != null) return StartTimeResolver(proc);
            try { return proc.StartTime; } catch { return DateTime.MinValue; }
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
            string? exePath = TryGetExecutablePath(process);
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
            var antiCheat = AntiCheatAssessor?.Invoke(process, installationRoot) ?? _detector.AssessAntiCheatRisk(process, installationRoot);

            return new DetectionSnapshot
            {
                ProcessId = process.Id,
                ProcessName = GetSafeProcessName(process),
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
            if (_disposed) return;
            _disposed = true;

            _timer?.Dispose();

            lock (_pollLock)
            {
                _candidates.Clear();
                _nonGamePids.Clear();
                _windowRetries.Clear();
                _attachedExitProcesses.Clear();
            }
        }
    }
}
