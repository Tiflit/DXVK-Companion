using System;
using System.Threading.Tasks;

namespace DXVKCompanion.Safety
{
    public interface IOperationLease : IAsyncDisposable, IDisposable
    {
        string InstallationRoot { get; }
        string LeaseKey { get; }
        bool IsAcquired { get; }
        bool IsDisposed { get; }
        IOperationLease? StoreLease { get; }
        bool IsValidForTarget(string targetPath);
        bool IsValidForStore(string storePath);
    }
}
