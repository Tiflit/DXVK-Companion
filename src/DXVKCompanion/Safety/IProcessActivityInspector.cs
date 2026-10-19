using System.Collections.Generic;

namespace DXVKCompanion.Safety
{
    internal interface IProcessActivityInspector
    {
        ProcessActivityEvidence GetActivityEvidence(
            string installationRoot,
            IEnumerable<string>? candidateExecutableNames = null);
    }
}
