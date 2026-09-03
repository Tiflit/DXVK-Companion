using System.Collections.Generic;

namespace DXVKCompanion.Models
{
    public class DxvkState
    {
        public string Version { get; set; } = "";
        public List<string> InstalledDlls { get; set; } = new();

        public DxvkState() { }

        public DxvkState(string version, List<string> dlls)
        {
            Version = version;
            InstalledDlls = dlls;
        }
    }
}
