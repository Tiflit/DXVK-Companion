using System;
using System.Threading;
using System.Threading.Tasks;
using DXVKCompanion.Models;
using DXVKCompanion.Utils;

namespace DXVKCompanion.DXVK
{
    public class DxvkReleaseCheckScheduler : IDisposable
    {
        private readonly Func<CancellationToken, Task<ReleaseInfo?>> _checkAction;
        private readonly Func<ReleaseInfo, Task>? _onUpdateChecked;
        private readonly TimeSpan _checkInterval;
        private readonly System.Threading.Timer? _timer;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _tickLock = new(1, 1);
        private bool _disposed;

        public DxvkReleaseCheckScheduler(
            Func<CancellationToken, Task<ReleaseInfo?>> checkAction,
            Func<ReleaseInfo, Task>? onUpdateChecked = null,
            TimeSpan? checkInterval = null,
            bool startImmediately = true)
        {
            _checkAction = checkAction ?? throw new ArgumentNullException(nameof(checkAction));
            _onUpdateChecked = onUpdateChecked;
            _checkInterval = checkInterval ?? TimeSpan.FromHours(1);

            if (startImmediately)
            {
                _timer = new System.Threading.Timer(OnTimerTick, null, TimeSpan.Zero, _checkInterval);
            }
        }

        private async void OnTimerTick(object? state)
        {
            if (_disposed) return;
            try
            {
                await TriggerCheckAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected during application shutdown / cancellation
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkReleaseCheckScheduler: periodic check error: {ex.GetType().Name} - {ex.Message}");
            }
        }

        public async Task<ReleaseInfo?> TriggerCheckAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) return null;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            bool lockAcquired = false;
            try
            {
                lockAcquired = await _tickLock.WaitAsync(0, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (!lockAcquired)
            {
                // A check is already active under this scheduler instance
                return null;
            }

            try
            {
                if (_disposed) return null;

                var latest = await _checkAction(linkedCts.Token);
                if (latest != null && !_disposed && _onUpdateChecked != null)
                {
                    try
                    {
                        await _onUpdateChecked(latest);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancelled
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"DxvkReleaseCheckScheduler: update notification callback error: {ex.GetType().Name} - {ex.Message}");
                    }
                }

                return latest;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"DxvkReleaseCheckScheduler: check action execution error: {ex.GetType().Name} - {ex.Message}");
                return null;
            }
            finally
            {
                _tickLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _cts.Cancel();
            }
            catch { }

            _timer?.Dispose();
            _cts.Dispose();
            _tickLock.Dispose();
        }
    }
}
