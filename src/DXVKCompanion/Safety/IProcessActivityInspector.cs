using System.Collections.Generic;

namespace DXVKCompanion.Safety
{
    public interface IProcessActivityInspector
    {
        ProcessActivityEvidence GetActivityEvidence(
            string installationRoot,
            IEnumerable<string>? candidateExecutableNames = null);
    }
}
