using System;
using System.Threading;
using System.Windows.Forms;
using PPTimer.Core;
using static PPTimer.Windows.NativeMethods;

namespace PPTimer.Windows
{
    /// <summary>PPTimer.exe: a notification area (tray) app, independent of PowerPoint.</summary>
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Before any window exists: real pixels for window positions, crisp digits on scaled displays.
            EnableDpiAwareness();

            using (var mutex = new Mutex(true, @"Local\PPTimer", out var first))
            {
                if (!first)
                {
                    MessageBox.Show("PPTimer is already running. Its icon is in the notification area (bottom right, maybe under ^).",
                        "PPTimer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

                using (var app = new TrayApp())
                    Application.Run(app);
                GC.KeepAlive(mutex);
            }
        }

        static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return;
            }
            catch (EntryPointNotFoundException) { } // before Windows 10 1703
            try
            {
                if (SetProcessDpiAwareness(2) == 0) return;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { }
            try { SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { }
        }
    }
}
