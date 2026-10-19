using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DXVKCompanion.Safety
{
    internal class OperationLeaseCoordinator : IOperationLeaseCoordinator
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(500);

        private static OperationLeaseCoordinator? _default;
        public static OperationLeaseCoordinator Default => _default ??= new OperationLeaseCoordinator();

        public string CoordinationDirectory { get; }
        public string? LastAcquisitionError { get; private set; }

        public OperationLeaseCoordinator(string? coordinationDirectory = null)
        {
            CoordinationDirectory = !string.IsNullOrWhiteSpace(coordinationDirectory)
                ? Path.GetFullPath(coordinationDirectory)
                : OperationResourceIdentity.GetDefaultCoordinationDirectory();
        }

        public async ValueTask<IOperationLease?> TryAcquireOperationScopeAsync(
            string installationRoot,
            string? storePath = null,
            TimeSpan timeout = default,
            CancellationToken ct = default)
        {
            var storePaths = !string.IsNullOrWhiteSpace(storePath) ? new[] { storePath } : null;
            return await TryAcquireOperationScopeAsync(installationRoot, storePaths, timeout, ct).ConfigureAwait(false);
        }

        public async ValueTask<IOperationLease?> TryAcquireOperationScopeAsync(
            string installationRoot,
            System.Collections.Generic.IEnumerable<string>? storePaths,
            TimeSpan timeout = default,
            CancellationToken ct = default)
        {
            if (timeout <= TimeSpan.Zero)
            {
                timeout = DefaultTimeout;
            }

            LastAcquisitionError = null;
            string canonicalTarget = OperationResourceIdentity.ResolveCanonicalPath(installationRoot);
            string targetKey = OperationResourceIdentity.GetTargetLockFileName(canonicalTarget);

            var sw = Stopwatch.StartNew();

            // Tier 1: Target lease first
            var targetLease = await TryAcquireFileLockAsync(targetKey, canonicalTarget, isTarget: true, timeout, ct).ConfigureAwait(false);
            if (targetLease == null)
            {
                return null;
            }

            var acquiredStoreLeases = new System.Collections.Generic.List<IOperationLease>();
            bool success = false;
            try
            {
                if (storePaths != null)
                {
                    // Deterministically sort and deduplicate canonical store paths to avoid deadlock
                    var distinctStores = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var sp in storePaths)
                    {
                        if (!string.IsNullOrWhiteSpace(sp))
                        {
                            distinctStores.Add(OperationResourceIdentity.ResolveCanonicalPath(sp));
                        }
                    }

                    foreach (var canonicalStore in distinctStores)
                    {
                        var remaining = timeout - sw.Elapsed;
                        if (remaining <= TimeSpan.Zero)
                        {
                            return null;
                        }

                        string storeKey = OperationResourceIdentity.GetStoreLockFileName(canonicalStore);
                        var storeLease = await TryAcquireFileLockAsync(storeKey, canonicalStore, isTarget: false, remaining, ct).ConfigureAwait(false);
                        if (storeLease == null)
                        {
                            return null;
                        }
                        acquiredStoreLeases.Add(storeLease);
                    }
                }

                success = true;
                return new CompositeOperationLease(targetLease, acquiredStoreLeases);
            }
            finally
            {
                if (!success)
                {
                    for (int i = acquiredStoreLeases.Count - 1; i >= 0; i--)
                    {
                        try { await acquiredStoreLeases[i].DisposeAsync().ConfigureAwait(false); } catch { }
                    }
                    try { await targetLease.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }
        }

        public IOperationLease? TryAcquireOperationScope(
            string installationRoot,
            string? storePath = null,
            TimeSpan timeout = default)
        {
            var storePaths = !string.IsNullOrWhiteSpace(storePath) ? new[] { storePath } : null;
            return TryAcquireOperationScope(installationRoot, storePaths, timeout);
        }

        public IOperationLease? TryAcquireOperationScope(
            string installationRoot,
            System.Collections.Generic.IEnumerable<string>? storePaths,
            TimeSpan timeout = default)
        {
            if (timeout <= TimeSpan.Zero)
            {
                timeout = DefaultTimeout;
            }

            LastAcquisitionError = null;
            string canonicalTarget = OperationResourceIdentity.ResolveCanonicalPath(installationRoot);
            string targetKey = OperationResourceIdentity.GetTargetLockFileName(canonicalTarget);

            var sw = Stopwatch.StartNew();

            // Tier 1: Target lease first
            var targetLease = TryAcquireFileLockSync(targetKey, canonicalTarget, isTarget: true, timeout);
            if (targetLease == null)
            {
                return null;
            }

            var acquiredStoreLeases = new System.Collections.Generic.List<IOperationLease>();
            bool success = false;
            try
            {
                if (storePaths != null)
                {
                    // Deterministically sort and deduplicate canonical store paths to avoid deadlock
                    var distinctStores = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var sp in storePaths)
                    {
                        if (!string.IsNullOrWhiteSpace(sp))
                        {
                            distinctStores.Add(OperationResourceIdentity.ResolveCanonicalPath(sp));
                        }
                    }

                    foreach (var canonicalStore in distinctStores)
                    {
                        var remaining = timeout - sw.Elapsed;
                        if (remaining <= TimeSpan.Zero)
                        {
                            return null;
                        }

                        string storeKey = OperationResourceIdentity.GetStoreLockFileName(canonicalStore);
                        var storeLease = TryAcquireFileLockSync(storeKey, canonicalStore, isTarget: false, remaining);
                        if (storeLease == null)
                        {
                            return null;
                        }
                        acquiredStoreLeases.Add(storeLease);
                    }
                }

                success = true;
                return new CompositeOperationLease(targetLease, acquiredStoreLeases);
            }
            finally
            {
                if (!success)
                {
                    for (int i = acquiredStoreLeases.Count - 1; i >= 0; i--)
                    {
                        try { acquiredStoreLeases[i].Dispose(); } catch { }
                    }
                    try { targetLease.Dispose(); } catch { }
                }
            }
        }

        public async ValueTask<IOperationLease?> TryAcquireStoreLeaseAsync(
            string storePath,
            TimeSpan timeout = default,
            CancellationToken ct = default)
        {
            if (timeout <= TimeSpan.Zero)
            {
                timeout = DefaultTimeout;
            }

            LastAcquisitionError = null;
            string canonicalStore = OperationResourceIdentity.ResolveCanonicalPath(storePath);
            string storeKey = OperationResourceIdentity.GetStoreLockFileName(canonicalStore);

            return await TryAcquireFileLockAsync(storeKey, canonicalStore, isTarget: false, timeout, ct).ConfigureAwait(false);
        }

        public IOperationLease? TryAcquireStoreLease(
            string storePath,
            TimeSpan timeout = default)
        {
            if (timeout <= TimeSpan.Zero)
            {
                timeout = DefaultTimeout;
            }

            LastAcquisitionError = null;
            string canonicalStore = OperationResourceIdentity.ResolveCanonicalPath(storePath);
            string storeKey = OperationResourceIdentity.GetStoreLockFileName(canonicalStore);

            return TryAcquireFileLockSync(storeKey, canonicalStore, isTarget: false, timeout);
        }

        private async ValueTask<IOperationLease?> TryAcquireFileLockAsync(
            string lockKey,
            string canonicalPath,
            bool isTarget,
            TimeSpan timeout,
            CancellationToken ct)
        {
            string lockFilePath = Path.Combine(CoordinationDirectory, lockKey);
            try
            {
                Directory.CreateDirectory(CoordinationDirectory);
            }
            catch (Exception ex)
            {
                LastAcquisitionError = $"Cannot create coordination directory '{CoordinationDirectory}': {ex.Message}";
                return null;
            }

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
            {
                try
                {
                    var stream = new FileStream(
                        lockFilePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.Read,
                        bufferSize: 4096,
                        FileOptions.DeleteOnClose);

                    byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(canonicalPath);
                    stream.SetLength(0);
                    stream.Write(pathBytes, 0, pathBytes.Length);
                    stream.Flush();

                    if (isTarget && HasConflictingTargetLock(lockFilePath, canonicalPath))
                    {
                        stream.Dispose();
                    }
                    else
                    {
                        return new FileLockLease(
                            canonicalPath,
                            lockKey,
                            stream,
                            isTarget ? OperationLeaseKind.Target : OperationLeaseKind.Store);
                    }
                }
                catch (IOException ex) when (IsContentionException(ex))
                {
                    // Sharing violation or delete pending: retry within budget
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (!File.Exists(lockFilePath))
                    {
                        LastAcquisitionError = $"Access denied to coordination directory or lock file '{lockFilePath}': {ex.Message}";
                        return null;
                    }
                }

                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var delayMs = Math.Min(15, (int)remaining.TotalMilliseconds);
                if (delayMs <= 0)
                {
                    break;
                }

                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }

            return null;
        }

        private IOperationLease? TryAcquireFileLockSync(
            string lockKey,
            string canonicalPath,
            bool isTarget,
            TimeSpan timeout)
        {
            string lockFilePath = Path.Combine(CoordinationDirectory, lockKey);
            try
            {
                Directory.CreateDirectory(CoordinationDirectory);
            }
            catch (Exception ex)
            {
                LastAcquisitionError = $"Cannot create coordination directory '{CoordinationDirectory}': {ex.Message}";
                return null;
            }

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                try
                {
                    var stream = new FileStream(
                        lockFilePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.Read,
                        bufferSize: 4096,
                        FileOptions.DeleteOnClose);

                    byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(canonicalPath);
                    stream.SetLength(0);
                    stream.Write(pathBytes, 0, pathBytes.Length);
                    stream.Flush();

                    if (isTarget && HasConflictingTargetLock(lockFilePath, canonicalPath))
                    {
                        stream.Dispose();
                    }
                    else
                    {
                        return new FileLockLease(
                            canonicalPath,
                            lockKey,
                            stream,
                            isTarget ? OperationLeaseKind.Target : OperationLeaseKind.Store);
                    }
                }
                catch (IOException ex) when (IsContentionException(ex))
                {
                    // Sharing violation or delete pending: retry within budget
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (!File.Exists(lockFilePath))
                    {
                        LastAcquisitionError = $"Access denied to coordination directory or lock file '{lockFilePath}': {ex.Message}";
                        return null;
                    }
                }

                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var delayMs = Math.Min(15, (int)remaining.TotalMilliseconds);
                if (delayMs <= 0)
                {
                    break;
                }

                Thread.Sleep(delayMs);
            }

            return null;
        }

        private bool HasConflictingTargetLock(string myLockFilePath, string myCanonicalPath)
        {
            try
            {
                if (!Directory.Exists(CoordinationDirectory)) return false;

                foreach (var file in Directory.EnumerateFiles(CoordinationDirectory, "target_*.lock"))
                {
                    if (string.Equals(file, myLockFilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        using var readStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(readStream, System.Text.Encoding.UTF8);
                        string otherPath = reader.ReadToEnd().Trim();
                        if (!string.IsNullOrEmpty(otherPath))
                        {
                            if (OperationResourceIdentity.PathEquals(myCanonicalPath, otherPath) ||
                                OperationResourceIdentity.IsPathInside(myCanonicalPath, otherPath) ||
                                OperationResourceIdentity.IsPathInside(otherPath, myCanonicalPath))
                            {
                                return true;
                            }
                        }
                    }
                    catch (IOException)
                    {
                        // File currently held/written exclusively by another process
                        return true;
                    }
                    catch
                    {
                        // File may have been deleted concurrently
                    }
                }
            }
            catch
            {
                // Directory enumeration issue
            }

            return false;
        }

        private static bool IsContentionException(IOException ex)
        {
            if (ex is PathTooLongException || ex is DirectoryNotFoundException)
            {
                return false;
            }

            int hr = ex.HResult;
            // 0x80070020 (ERROR_SHARING_VIOLATION) or 0x80070021 (ERROR_LOCK_VIOLATION)
            if (hr == unchecked((int)0x80070020) || hr == unchecked((int)0x80070021))
            {
                return true;
            }

            string msg = ex.Message;
            return msg.Contains("another process", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("sharing violation", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("delete pending", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class FileLockLease : IOperationLease
        {
            private readonly FileStream _stream;
            private readonly string _canonicalPath;
            private readonly string _lockKey;
            private readonly OperationLeaseKind _kind;
            private bool _isDisposed;

            public FileLockLease(string canonicalPath, string lockKey, FileStream stream, OperationLeaseKind kind)
            {
                _canonicalPath = canonicalPath;
                _lockKey = lockKey;
                _stream = stream;
                _kind = kind;
            }

            public OperationLeaseKind Kind => _kind;
            public string InstallationRoot => _canonicalPath;
            public string LeaseKey => _lockKey;
            public bool IsAcquired => !_isDisposed && _stream != null;
            public bool IsDisposed => _isDisposed;
            public IOperationLease? StoreLease => _kind == OperationLeaseKind.Store ? this : null;

            public bool IsValidForTarget(string targetPath)
            {
                if (_isDisposed || !IsAcquired) return false;
                if (_kind != OperationLeaseKind.Target && _kind != OperationLeaseKind.Composite) return false;
                return OperationResourceIdentity.PathEquals(_canonicalPath, targetPath);
            }

            public bool IsValidForStore(string storePath)
            {
                if (_isDisposed || !IsAcquired) return false;
                if (_kind != OperationLeaseKind.Store && _kind != OperationLeaseKind.Composite) return false;
                return OperationResourceIdentity.PathEquals(_canonicalPath, storePath);
            }

            public void Dispose()
            {
                if (_isDisposed) return;
                _isDisposed = true;
                try
                {
                    _stream.Dispose();
                }
                catch
                {
                    // Handle disposal exception
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }

        private sealed class CompositeOperationLease : IOperationLease
        {
            private readonly IOperationLease _targetLease;
            private readonly System.Collections.Generic.List<IOperationLease> _storeLeases;
            private bool _isDisposed;

            public CompositeOperationLease(IOperationLease targetLease, System.Collections.Generic.IEnumerable<IOperationLease>? storeLeases)
            {
                _targetLease = targetLease;
                _storeLeases = storeLeases != null ? new System.Collections.Generic.List<IOperationLease>(storeLeases) : new System.Collections.Generic.List<IOperationLease>();
            }

            public OperationLeaseKind Kind => OperationLeaseKind.Composite;
            public string InstallationRoot => _targetLease.InstallationRoot;
            public string LeaseKey => _targetLease.LeaseKey;
            public bool IsAcquired => !_isDisposed && _targetLease.IsAcquired && _storeLeases.TrueForAll(s => s.IsAcquired);
            public bool IsDisposed => _isDisposed;
            public IOperationLease? StoreLease => _storeLeases.Count > 0 ? _storeLeases[0] : null;

            public bool IsValidForTarget(string targetPath)
            {
                if (_isDisposed) return false;
                return _targetLease.IsValidForTarget(targetPath);
            }

            public bool IsValidForStore(string storePath)
            {
                if (_isDisposed || _storeLeases.Count == 0) return false;
                return _storeLeases.Exists(s => s.IsValidForStore(storePath));
            }

            public void Dispose()
            {
                if (_isDisposed) return;
                _isDisposed = true;
                // Reverse lock order: Store leases first, then Target lease
                for (int i = _storeLeases.Count - 1; i >= 0; i--)
                {
                    try { _storeLeases[i].Dispose(); } catch { }
                }
                try { _targetLease.Dispose(); } catch { }
            }

            public async ValueTask DisposeAsync()
            {
                if (_isDisposed) return;
                _isDisposed = true;
                for (int i = _storeLeases.Count - 1; i >= 0; i--)
                {
                    try { await _storeLeases[i].DisposeAsync().ConfigureAwait(false); } catch { }
                }
                try { await _targetLease.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }
}
