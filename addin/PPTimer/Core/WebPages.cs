using System.IO;

namespace PPTimer.Core
{
    /// <summary>Browser pages embedded in the assembly (Core/*.html).</summary>
    internal static class WebPages
    {
        /// <summary>Control remote, served at "/".</summary>
        public static readonly string Remote = Load("remote.html");

        /// <summary>Timer only, black background, served at "/display".</summary>
        public static readonly string Display = Load("display.html");

        static string Load(string name)
        {
            using (var stream = typeof(WebPages).Assembly.GetManifestResourceStream("PPTimer." + name))
            {
                if (stream == null) return $"<h1>{name} missing from the build</h1>";
                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd();
            }
        }
    }
}
