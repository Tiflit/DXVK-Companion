using System;
using System.Diagnostics;
using DXVKCompanion.Storage;

namespace DXVKCompanion.Monitoring
{
    public class ProcessExitHandler
    {
        private readonly ProfileStore _profiles;

        public ProcessExitHandler(ProfileStore profiles)
        {
            _profiles = profiles;
        }

        public void Attach(Process process)
        {
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    // Nothing to clean in profiles, but could be extended
                };
            }
            catch
            {
                // Ignore failures
            }
        }
    }
}
