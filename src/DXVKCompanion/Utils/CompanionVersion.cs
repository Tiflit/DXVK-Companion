using System.Reflection;

namespace DXVKCompanion.Utils
{
    public static class CompanionVersion
    {
        /// <summary>
        /// This app's own version, read from the assembly's InformationalVersion —
        /// set automatically from the &lt;Version&gt; property in the .csproj.
        /// </summary>
        public static string Current =>
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "0.0.0";

        /// <summary>
        /// GitHub release tags conventionally have a leading "v" (e.g. "v1.2.0"); strip it
        /// before comparing against our own plain "1.2.0"-style local version.
        /// </summary>
        public static bool IsOutdatedComparedTo(string localVersion, string githubTag)
        {
            string normalizedTag = githubTag.StartsWith("v") ? githubTag[1..] : githubTag;
            return !string.Equals(localVersion, normalizedTag, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
