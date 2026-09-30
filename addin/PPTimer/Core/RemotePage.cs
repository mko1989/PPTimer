using System.IO;

namespace PPTimer.Core
{
    /// <summary>Browser remote served at "/" (remote.html, embedded in the assembly).</summary>
    internal static class RemotePage
    {
        public static readonly string Html = Load();

        static string Load()
        {
            using (var stream = typeof(RemotePage).Assembly.GetManifestResourceStream("PPTimer.remote.html"))
            {
                if (stream == null) return "<h1>remote.html missing from the build</h1>";
                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd();
            }
        }
    }
}
