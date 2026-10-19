using System;
using System.Threading;
using System.Threading.Tasks;

namespace DXVKCompanion.Safety
{
    internal interface IOperationLeaseCoordinator
    {
        string CoordinationDirectory { get; }
        string? LastAcquisitionError { get; }

        ValueTask<IOperationLease?> TryAcquireOperationScopeAsync(
            string installationRoot,
            string? storePath = null,
            TimeSpan timeout = default,
            CancellationToken ct = default);

        ValueTask<IOperationLease?> TryAcquireOperationScopeAsync(
            string installationRoot,
            System.Collections.Generic.IEnumerable<string>? storePaths,
            TimeSpan timeout = default,
            CancellationToken ct = default);

        IOperationLease? TryAcquireOperationScope(
            string installationRoot,
            string? storePath = null,
            TimeSpan timeout = default);

        IOperationLease? TryAcquireOperationScope(
            string installationRoot,
            System.Collections.Generic.IEnumerable<string>? storePaths,
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
