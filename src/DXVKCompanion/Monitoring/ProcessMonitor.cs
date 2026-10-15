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
        private bool _isPolling;

        private readonly Dictionary<int, DateTime> _nonGamePids = new();
        private readonly Dictionary<int, CandidateProcessState> _candidates = new();
        private readonly Dictionary<int, (DateTime StartTime, int RetryCount)> _windowRetries = new();
        private readonly Dictionary<int, (Process Process, DateTime StartTime, string ExePath, Guid RegistrationToken)> _attachedExitProcesses = new();

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

        private Func<Process, string?>? _exePathResolver;
        internal Func<Process, string?>? ExePathResolver
        {
            get => _exePathResolver;
            set
            {
                _exePathResolver = value;
                if (_exitHandler != null && _exitHandler.ExePathResolver == null)
                {
                    _exitHandler.ExePathResolver = value;
                }
            }
        }
        private Func<Process, int>? _processIdResolver;
        internal Func<Process, int>? ProcessIdResolver
        {
            get => _processIdResolver;
            set
            {
                _processIdResolver = value;
                if (_exitHandler != null && _exitHandler.ProcessIdResolver == null)
                {
                    _exitHandler.ProcessIdResolver = value;
                }
            }
        }

        internal int GetProcessId(Process proc)
        {
            if (_processIdResolver != null) return _processIdResolver(proc);
            try { return proc.Id; } catch { return 0; }
        }

        internal Func<Process, DateTime>? StartTimeResolver { get; set; }
        internal Func<Process, bool>? GameProcessFilter { get; set; }
        internal Func<Process, bool>? WindowFilter { get; set; }
        internal Func<Process, string, AntiCheatAssessment>? AntiCheatAssessor { get; set; }
        internal bool TreatProcessesAsOwnedForTesting { get; set; }

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
            if (_exePathResolver != null && _exitHandler.ExePathResolver == null)
            {
                _exitHandler.ExePathResolver = _exePathResolver;
            }
            if (_processIdResolver != null && _exitHandler.ProcessIdResolver == null)
            {
                _exitHandler.ProcessIdResolver = _processIdResolver;
            }
            _classifier = classifier ?? new ApiClassifier(new ModuleScanner(), new PeParser());
            _libraryStore = libraryStore;
            _clock = clock ?? DefaultClock;
            _processEnumerator = processEnumerator;

            _exitHandler.ProcessInstanceExited += OnProcessInstanceExited;

            if (startTimer)
            {
                _timer = new System.Threading.Timer(PollProcesses, null, 0, 2000);
            }
        }

        private void OnProcessInstanceExited(Guid token, int pid, string exePath) => HandleExit(token, pid, exePath);

        private void HandleExit(Guid token, int pid, string exePath)
        {
            if (_disposed) return;

            bool shouldFire = false;
            lock (_pollLock)
            {
                if (_disposed) return;

                if (token != Guid.Empty && pid > 0)
                {
                    if (_attachedExitProcesses.TryGetValue(pid, out var existing))
                    {
                        if (existing.RegistrationToken == token)
                        {
                            // Active registration matching this exit delivery: remove current state
                            _attachedExitProcesses.Remove(pid);
                            _candidates.Remove(pid);
                            _windowRetries.Remove(pid);
                            _nonGamePids.Remove(pid);
                            shouldFire = true;
                        }
                        else
                        {
                            // Stale exit delivery from an earlier instance: do not remove newer registration or candidate
                            shouldFire = true;
                        }
                    }
                    else
                    {
                        shouldFire = true;
                    }
                }
                else if (pid > 0)
                {
                    if (_attachedExitProcesses.TryGetValue(pid, out var existing))
                    {
                        _attachedExitProcesses.Remove(pid);
                        _candidates.Remove(pid);
                        _windowRetries.Remove(pid);
                        _nonGamePids.Remove(pid);
                        shouldFire = true;
                    }
                    else
                    {
                        shouldFire = true;
                    }
                }
                else
                {
                    var match = _attachedExitProcesses.FirstOrDefault(kv => string.Equals(kv.Value.ExePath, exePath, StringComparison.OrdinalIgnoreCase));
                    if (match.Key != 0)
                    {
                        _attachedExitProcesses.Remove(match.Key);
                        _candidates.Remove(match.Key);
                        _windowRetries.Remove(match.Key);
                        _nonGamePids.Remove(match.Key);
                        shouldFire = true;
                    }
                    else
                    {
                        shouldFire = true;
                    }
                }
            }

            if (!_disposed && shouldFire)
            {
                OnGameExited?.Invoke(exePath);
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

        internal bool IsExitWatcherAttached(int processId)
        {
            lock (_pollLock)
            {
                return _attachedExitProcesses.ContainsKey(processId);
            }
        }

        internal void PollProcessesOnce() => PollProcesses(null);

        private void PollProcesses(object? state)
        {
            if (_disposed) return;
            if (!Monitor.TryEnter(_pollLock)) return;

            try
            {
                if (_isPolling || _disposed) return;
                _isPolling = true;

                try
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
                    var currentPids = processList
                        .Select(p =>
                        {
                            try { return GetProcessId(p); } catch { return -1; }
                        })
                        .Where(id => id > 0)
                        .ToHashSet();

                    // Clean up transient state for exited or missing processes across unattached collections
                    var staleNonGames = _nonGamePids.Keys.Where(p => !currentPids.Contains(p)).ToList();
                    foreach (var p in staleNonGames) _nonGamePids.Remove(p);

                    var staleRetries = _windowRetries.Keys.Where(p => !currentPids.Contains(p)).ToList();
                    foreach (var p in staleRetries) _windowRetries.Remove(p);

                    var staleCandidates = _candidates.Keys.Where(p => !currentPids.Contains(p)).ToList();
                    foreach (var p in staleCandidates) _candidates.Remove(p);

                    var now = _clock();

                    foreach (var proc in processList)
                    {
                        if (_disposed)
                        {
                            DisposeIfOwned(proc);
                            continue;
                        }

                        int pid;
                        try
                        {
                            pid = GetProcessId(proc);
                        }
                        catch
                        {
                            DisposeIfOwned(proc);
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
                            if (_nonGamePids.TryGetValue(pid, out var cachedStart))
                            {
                                if (startTime != cachedStart)
                                {
                                    // Start time differs or transitioned between unknown/known: reset cached non-game state
                                    _nonGamePids.Remove(pid);
                                }
                                else if (startTime != DateTime.MinValue)
                                {
                                    DisposeIfOwned(proc);
                                    continue;
                                }
                            }

                            bool isGame = GameProcessFilter?.Invoke(proc) ?? _detector.IsGameProcess(proc);
                            if (!isGame)
                            {
                                _nonGamePids[pid] = startTime;
                                DisposeIfOwned(proc);
                                continue;
                            }

                            // R5: Window check with instance-aware retries and retirement
                            bool hasWindow = WindowFilter?.Invoke(proc) ?? _detector.HasWindow(proc);
                            if (!hasWindow)
                            {
                                if (_windowRetries.TryGetValue(pid, out var retryEntry))
                                {
                                    if (startTime != retryEntry.StartTime)
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
                                    _nonGamePids[pid] = startTime;
                                    _windowRetries.Remove(pid);
                                }
                                else
                                {
                                    _windowRetries[pid] = (startTime, newRetries);
                                }

                                DisposeIfOwned(proc);
                                continue;
                            }

                            _windowRetries.Remove(pid);

                            // Candidate process with window: track qualification state
                            if (_candidates.TryGetValue(pid, out var candidate))
                            {
                                bool isSameInstance;
                                if (candidate.IsQualified || candidate.IsExpired)
                                {
                                    if (startTime != DateTime.MinValue && candidate.ProcessStartTime != DateTime.MinValue)
                                    {
                                        isSameInstance = (startTime == candidate.ProcessStartTime);
                                    }
                                    else if (candidate.ProcessStartTime == DateTime.MinValue && startTime == DateTime.MinValue)
                                    {
                                        // Both unresolved: preserve terminal candidate state (exhausted budget remains without restart)
                                        isSameInstance = true;
                                    }
                                    else
                                    {
                                        // Identity resolution transition (MinValue -> known) or known -> MinValue
                                        isSameInstance = false;
                                    }
                                }
                                else
                                {
                                    // In-progress candidate:
                                    if (startTime != DateTime.MinValue && candidate.ProcessStartTime != DateTime.MinValue)
                                    {
                                        isSameInstance = (startTime == candidate.ProcessStartTime);
                                    }
                                    else
                                    {
                                        // At least one start time is unresolved (MinValue):
                                        if (candidate.ProcessStartTime == DateTime.MinValue && startTime != DateTime.MinValue)
                                        {
                                            candidate.ProcessStartTime = startTime;
                                        }
                                        isSameInstance = true;
                                    }
                                }

                                if (!isSameInstance)
                                {
                                    // PID reused by OS or identity resolved: reset candidate state
                                    candidate = new CandidateProcessState(pid, startTime, now);
                                    _candidates[pid] = candidate;
                                }
                            }
                            else
                            {
                                candidate = new CandidateProcessState(pid, startTime, now);
                                _candidates[pid] = candidate;
                            }

                            if (candidate.IsQualified || candidate.IsExpired)
                            {
                                DisposeIfOwned(proc);
                                continue;
                            }

                            if (!candidate.IsAssessmentDue(now))
                            {
                                DisposeIfOwned(proc);
                                continue;
                            }

                            if (_disposed)
                            {
                                DisposeIfOwned(proc);
                                break;
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
                                Logger.Log($"ProcessMonitor: snapshot assessment failed for PID {pid}: {ex.GetType().Name}");
                            }

                            if (_disposed)
                            {
                                DisposeIfOwned(proc);
                                break;
                            }

                            bool qualifies = snapshot != null &&
                                             startTime != DateTime.MinValue &&
                                             ProcessQualification.IsRecognizedGraphicsApi(snapshot.Classification.PrimaryApi);

                            if (qualifies && snapshot != null)
                            {
                                if (_disposed)
                                {
                                    DisposeIfOwned(proc);
                                    break;
                                }

                                var (status, retainedProc) = EnsureExitHandlerAttached(proc, startTime, snapshot.ExecutablePath);

                                if (_disposed)
                                {
                                    DisposeIfOwned(proc);
                                    break;
                                }

                                if ((status != AttachmentStatus.NewlyAttached && status != AttachmentStatus.AlreadyAttached) || retainedProc == null)
                                {
                                    // Attachment setup failed or process finished/exited synchronously during setup.
                                    // Do NOT pass an already finished/disposed or unmonitored candidate to async consumer.
                                    // If setup failed, record inspection failure and advance candidate.
                                    candidate.IsQualified = false;
                                    candidate.QualifiedSnapshot = null;
                                    candidate.LastInspectionHadFailure = true;
                                    candidate.AdvanceAfterAssessment(now);

                                    DisposeIfOwned(proc);
                                    continue;
                                }

                                candidate.IsQualified = true;
                                candidate.QualifiedSnapshot = snapshot;

                                if (_libraryStore != null)
                                {
                                    _libraryStore.RecordDetectionSnapshot(snapshot);
                                }

                                if (!_disposed)
                                {
                                    OnSnapshotDetected?.Invoke(snapshot);
                                }

                                if (!_disposed)
                                {
                                    OnQualifiedGameDetected?.Invoke(retainedProc, snapshot);
                                }

                                if (!_disposed)
                                {
                                    OnGameDetected?.Invoke(retainedProc);
                                }
                            }
                            else
                            {
                                candidate.LastInspectionHadFailure = (snapshot == null) ||
                                    (startTime == DateTime.MinValue) ||
                                    (snapshot.Classification.EvidenceSource == "RuntimeIncomplete") ||
                                    (snapshot.Classification.Evidence.Any(e => e.Contains("scan incomplete", StringComparison.OrdinalIgnoreCase)));

                                candidate.AdvanceAfterAssessment(now);

                                if (candidate.IsExpired)
                                {
                                    string safeName = GetSafeProcessName(proc);
                                    if (candidate.LastInspectionHadFailure)
                                    {
                                        Logger.Log($"Candidate process PID {pid} ({safeName}) qualification budget expired: inspection unresolved.");
                                    }
                                    else
                                    {
                                        Logger.Log($"Candidate process PID {pid} ({safeName}) qualification budget expired: no recognized graphics API evidence observed.");
                                    }
                                }
                            }

                            DisposeIfOwned(proc);
                        }
                        catch
                        {
                            DisposeIfOwned(proc);
                        }
                    }
                }
                finally
                {
                    _isPolling = false;
                }
            }
            finally
            {
                Monitor.Exit(_pollLock);
            }
        }

        private void DisposeIfOwned(Process proc)
        {
            if (_processEnumerator != null && !TreatProcessesAsOwnedForTesting) return;
            if (!IsCurrentInstanceAttached(proc))
            {
                try
                {
                    proc.Dispose();
                }
                catch
                {
                }
            }
        }

        private enum AttachmentStatus
        {
            NewlyAttached,
            AlreadyAttached,
            SetupFailed,
            SynchronouslyExited
        }

        private (AttachmentStatus Status, Process? RetainedProcess) EnsureExitHandlerAttached(Process proc, DateTime startTime, string exePath)
        {
            try
            {
                int pid = GetProcessId(proc);
                if (_attachedExitProcesses.TryGetValue(pid, out var existing))
                {
                    bool isSameInstance;
                    if (startTime != DateTime.MinValue && existing.StartTime != DateTime.MinValue)
                    {
                        isSameInstance = (startTime == existing.StartTime);
                    }
                    else
                    {
                        bool hasExited = false;
                        try { hasExited = existing.Process.HasExited; } catch { hasExited = true; }
                        isSameInstance = !hasExited && string.Equals(existing.ExePath, exePath, StringComparison.OrdinalIgnoreCase);
                    }

                    if (isSameInstance && _exitHandler.HasRegistration(existing.RegistrationToken))
                    {
                        return (AttachmentStatus.AlreadyAttached, existing.Process);
                    }

                    // Stale attachment (different process instance or registration already completed/cancelled)
                    _attachedExitProcesses.Remove(pid);
                }

                var token = Guid.NewGuid();
                _attachedExitProcesses[pid] = (proc, startTime, exePath, token);

                Guid establishedToken;
                try
                {
                    establishedToken = _exitHandler.AttachCore(proc, token, exePath);
                }
                catch
                {
                    establishedToken = Guid.Empty;
                }

                if (establishedToken == Guid.Empty || establishedToken != token)
                {
                    // Setup failed: unwind only this failed token
                    if (_attachedExitProcesses.TryGetValue(pid, out var current) && current.RegistrationToken == token)
                    {
                        _attachedExitProcesses.Remove(pid);
                    }
                    return (AttachmentStatus.SetupFailed, null);
                }

                // If synchronous exit occurred during AttachCore, CompleteExit will have removed it from registrations
                if (!_exitHandler.HasRegistration(token) || !_attachedExitProcesses.ContainsKey(pid))
                {
                    return (AttachmentStatus.SynchronouslyExited, null);
                }

                return (AttachmentStatus.NewlyAttached, proc);
            }
            catch
            {
                return (AttachmentStatus.SetupFailed, null);
            }
        }

        private bool AttachExitHandlerOnce(Process proc, DateTime startTime, string exePath)
        {
            var (status, _) = EnsureExitHandlerAttached(proc, startTime, exePath);
            return status == AttachmentStatus.NewlyAttached || status == AttachmentStatus.AlreadyAttached;
        }

        private bool IsCurrentInstanceAttached(Process proc)
        {
            try
            {
                int pid = GetProcessId(proc);
                if (_attachedExitProcesses.TryGetValue(pid, out var existing))
                {
                    return ReferenceEquals(existing.Process, proc);
                }
            }
            catch
            {
                return false;
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
                ProcessId = GetProcessId(process),
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
            lock (_pollLock)
            {
                if (_disposed) return;
                _disposed = true;

                _exitHandler.ProcessInstanceExited -= OnProcessInstanceExited;

                _timer?.Dispose();

                foreach (var item in _attachedExitProcesses.Values)
                {
                    _exitHandler.CancelRegistration(item.RegistrationToken);
                    if (_processEnumerator == null)
                    {
                        try { item.Process.Dispose(); } catch { }
                    }
                }

                _candidates.Clear();
                _nonGamePids.Clear();
                _windowRetries.Clear();
                _attachedExitProcesses.Clear();
            }
        }
    }
}
