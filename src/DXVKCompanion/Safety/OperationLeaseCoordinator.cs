using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DXVKCompanion.Safety
{
    public class OperationLeaseCoordinator : IOperationLeaseCoordinator
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(500);

        private static OperationLeaseCoordinator? _default;
        public static OperationLeaseCoordinator Default => _default ??= new OperationLeaseCoordinator();

        public string CoordinationDirectory { get; }

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
            if (timeout <= TimeSpan.Zero)
            {
                timeout = DefaultTimeout;
            }

            string canonicalTarget = OperationResourceIdentity.ResolveCanonicalPath(installationRoot);
            string targetKey = OperationResourceIdentity.GetTargetLockFileName(canonicalTarget);

            var sw = Stopwatch.StartNew();

            // Tier 1: Target lease first
            var targetLease = await TryAcquireFileLockAsync(targetKey, canonicalTarget, timeout, ct).ConfigureAwait(false);
            if (targetLease == null)
            {
                return null;
            }

            // Tier 2: Shared Store Root lease second (if store path provided)
            IOperationLease? storeLease = null;
            if (!string.IsNullOrWhiteSpace(storePath))
            {
                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    await targetLease.DisposeAsync().ConfigureAwait(false);
                    return null;
                }

                string canonicalStore = OperationResourceIdentity.ResolveCanonicalPath(storePath);
                string storeKey = OperationResourceIdentity.GetStoreLockFileName(canonicalStore);

                storeLease = await TryAcquireFileLockAsync(storeKey, canonicalStore, remaining, ct).ConfigureAwait(false);
                if (storeLease == null)
                {
                    await targetLease.DisposeAsync().ConfigureAwait(false);
                    return null;
                }
            }

            return new CompositeOperationLease(targetLease, storeLease);
        }

        public IOperationLease? TryAcquireOperationScope(
            string installationRoot,
            string? storePath = null,
            TimeSpan timeout = default)
        {
            if (timeout <= TimeSpan.Zero)
            {
                timeout = DefaultTimeout;
            }

            string canonicalTarget = OperationResourceIdentity.ResolveCanonicalPath(installationRoot);
            string targetKey = OperationResourceIdentity.GetTargetLockFileName(canonicalTarget);

            var sw = Stopwatch.StartNew();

            // Tier 1: Target lease first
            var targetLease = TryAcquireFileLockSync(targetKey, canonicalTarget, timeout);
            if (targetLease == null)
            {
                return null;
            }

            // Tier 2: Shared Store Root lease second
            IOperationLease? storeLease = null;
            if (!string.IsNullOrWhiteSpace(storePath))
            {
                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    targetLease.Dispose();
                    return null;
                }

                string canonicalStore = OperationResourceIdentity.ResolveCanonicalPath(storePath);
                string storeKey = OperationResourceIdentity.GetStoreLockFileName(canonicalStore);

                storeLease = TryAcquireFileLockSync(storeKey, canonicalStore, remaining);
                if (storeLease == null)
                {
                    targetLease.Dispose();
                    return null;
                }
            }

            return new CompositeOperationLease(targetLease, storeLease);
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

            string canonicalStore = OperationResourceIdentity.ResolveCanonicalPath(storePath);
            string storeKey = OperationResourceIdentity.GetStoreLockFileName(canonicalStore);

            return await TryAcquireFileLockAsync(storeKey, canonicalStore, timeout, ct).ConfigureAwait(false);
        }

        public IOperationLease? TryAcquireStoreLease(
            string storePath,
            TimeSpan timeout = default)
        {
            if (timeout <= TimeSpan.Zero)
            {
                timeout = DefaultTimeout;
            }

            string canonicalStore = OperationResourceIdentity.ResolveCanonicalPath(storePath);
            string storeKey = OperationResourceIdentity.GetStoreLockFileName(canonicalStore);

            return TryAcquireFileLockSync(storeKey, canonicalStore, timeout);
        }

        private async ValueTask<IOperationLease?> TryAcquireFileLockAsync(
            string lockKey,
            string canonicalPath,
            TimeSpan timeout,
            CancellationToken ct)
        {
            string lockFilePath = Path.Combine(CoordinationDirectory, lockKey);
            Directory.CreateDirectory(CoordinationDirectory);

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
            {
                try
                {
                    var stream = new FileStream(
                        lockFilePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.DeleteOnClose);

                    return new FileLockLease(canonicalPath, lockKey, stream);
                }
                catch (IOException ex) when (IsContentionException(ex))
                {
                    // Sharing violation or delete pending: retry within budget
                }
                catch (UnauthorizedAccessException)
                {
                    // Delete-pending on Windows can report as access denied
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
            TimeSpan timeout)
        {
            string lockFilePath = Path.Combine(CoordinationDirectory, lockKey);
            Directory.CreateDirectory(CoordinationDirectory);

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                try
                {
                    var stream = new FileStream(
                        lockFilePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.DeleteOnClose);

                    return new FileLockLease(canonicalPath, lockKey, stream);
                }
                catch (IOException ex) when (IsContentionException(ex))
                {
                    // Sharing violation or delete pending: retry within budget
                }
                catch (UnauthorizedAccessException)
                {
                    // Delete-pending on Windows can report as access denied
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
            private bool _isDisposed;

            public FileLockLease(string canonicalPath, string lockKey, FileStream stream)
            {
                _canonicalPath = canonicalPath;
                _lockKey = lockKey;
                _stream = stream;
            }

            public string InstallationRoot => _canonicalPath;
            public string LeaseKey => _lockKey;
            public bool IsAcquired => !_isDisposed && _stream != null;
            public bool IsDisposed => _isDisposed;
            public IOperationLease? StoreLease => null;

            public bool IsValidForTarget(string targetPath)
            {
                if (_isDisposed || !IsAcquired) return false;
                return OperationResourceIdentity.PathEquals(_canonicalPath, targetPath);
            }

            public bool IsValidForStore(string storePath)
            {
                if (_isDisposed || !IsAcquired) return false;
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
            private readonly IOperationLease? _storeLease;
            private bool _isDisposed;

            public CompositeOperationLease(IOperationLease targetLease, IOperationLease? storeLease)
            {
                _targetLease = targetLease;
                _storeLease = storeLease;
            }

            public string InstallationRoot => _targetLease.InstallationRoot;
            public string LeaseKey => _targetLease.LeaseKey;
            public bool IsAcquired => !_isDisposed && _targetLease.IsAcquired && (_storeLease == null || _storeLease.IsAcquired);
            public bool IsDisposed => _isDisposed;
            public IOperationLease? StoreLease => _storeLease;

            public bool IsValidForTarget(string targetPath)
            {
                if (_isDisposed) return false;
                return _targetLease.IsValidForTarget(targetPath);
            }

            public bool IsValidForStore(string storePath)
            {
                if (_isDisposed || _storeLease == null) return false;
                return _storeLease.IsValidForStore(storePath);
            }

            public void Dispose()
            {
                if (_isDisposed) return;
                _isDisposed = true;
                // Reverse lock order: Store lease first, then Target lease
                try { _storeLease?.Dispose(); } catch { }
                try { _targetLease.Dispose(); } catch { }
            }

            public async ValueTask DisposeAsync()
            {
                if (_isDisposed) return;
                _isDisposed = true;
                if (_storeLease != null)
                {
                    try { await _storeLease.DisposeAsync().ConfigureAwait(false); } catch { }
                }
                try { await _targetLease.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }
}
