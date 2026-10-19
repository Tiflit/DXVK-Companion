using System;
using System.Threading;
using System.Threading.Tasks;

namespace DXVKCompanion.Safety
{
    public interface IOperationLeaseCoordinator
    {
        string CoordinationDirectory { get; }

        ValueTask<IOperationLease?> TryAcquireOperationScopeAsync(
            string installationRoot,
            string? storePath = null,
            TimeSpan timeout = default,
            CancellationToken ct = default);

        IOperationLease? TryAcquireOperationScope(
            string installationRoot,
            string? storePath = null,
            TimeSpan timeout = default);

        ValueTask<IOperationLease?> TryAcquireStoreLeaseAsync(
            string storePath,
            TimeSpan timeout = default,
            CancellationToken ct = default);

        IOperationLease? TryAcquireStoreLease(
            string storePath,
            TimeSpan timeout = default);
    }
}
