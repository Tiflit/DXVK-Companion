using System;
using System.Threading.Tasks;

namespace DXVKCompanion.Safety
{
    internal enum OperationLeaseKind
    {
        Target,
        Store,
        Composite
    }

    internal interface IOperationLease : IAsyncDisposable, IDisposable
    {
        OperationLeaseKind Kind { get; }
        string InstallationRoot { get; }
        string LeaseKey { get; }
        bool IsAcquired { get; }
        bool IsDisposed { get; }
        IOperationLease? StoreLease { get; }
        bool IsValidForTarget(string targetPath);
        bool IsValidForStore(string storePath);
    }
}
