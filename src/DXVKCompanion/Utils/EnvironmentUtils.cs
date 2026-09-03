using System.Collections.Generic;

namespace DXVKCompanion.Utils
{
    public static class EnvironmentUtils
    {
        public static Dictionary<string, string> BuildDxvkEnvironment(bool hudEnabled, int frameLimit)
        {
            var env = new Dictionary<string, string>();

            if (hudEnabled)
                env["DXVK_HUD"] = "fps,version";

            if (frameLimit > 0)
                env["DXVK_MAX_FRAME_RATE"] = frameLimit.ToString();

            return env;
        }
    }
}
