using System;
using System.IO;

namespace PPTimer.Core
{
    /// <summary>Append-only file log (rotates at ~1 MB). Never throws.</summary>
    public static class Log
    {
        static readonly object Gate = new object();
        static string path;

        public static bool EchoToConsole { get; set; }

        public static string FilePath => path;

        public static void Init(string directory)
        {
            path = Path.Combine(directory, "pptimer.log");
        }

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message, Exception ex = null) =>
            Write("ERROR", ex == null ? message : message + ": " + ex);

        /// <summary>The last <paramref name="maxBytes"/> of the log (previous file first if the current one is shorter).</summary>
        public static string Tail(int maxBytes)
        {
            if (path == null) return "(no log file)";
            lock (Gate)
            {
                try
                {
                    var text = File.Exists(path) ? File.ReadAllText(path) : "";
                    if (text.Length < maxBytes && File.Exists(path + ".1"))
                        text = File.ReadAllText(path + ".1") + text;
                    return text.Length <= maxBytes ? text : text.Substring(text.Length - maxBytes);
                }
                catch (Exception ex)
                {
                    return $"(could not read {path}: {ex.Message})";
                }
            }
        }

        static void Write(string level, string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (EchoToConsole) Console.WriteLine(line);
            if (path == null) return;
            lock (Gate)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 1_000_000)
                    {
                        File.Copy(path, path + ".1", true);
                        File.Delete(path);
                    }
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch
                {
                    // Logging must never take PowerPoint down.
                }
            }
        }
    }
}
