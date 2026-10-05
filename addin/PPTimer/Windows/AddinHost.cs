using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using PPTimer.Core;

namespace PPTimer.Windows
{
    /// <summary>Wires the timer, the API server and the overlay together for the lifetime of PowerPoint.</summary>
    internal sealed class AddinHost : IDisposable
    {
        ApiServer api;
        OverlayController overlay;
        PowerPointInfo powerPoint;

        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PPTimer");

        /// <param name="application">PowerPoint's Application object (from OnConnection).</param>
        public void Start(object application)
        {
            Directory.CreateDirectory(DataDirectory);
            Log.Init(DataDirectory);
            Log.Info($"PPTimer {ApiServer.Version} starting in {Process.GetCurrentProcess().ProcessName} " +
                     $"({(Environment.Is64BitProcess ? "64" : "32")}-bit, CLR {Environment.Version})");
            powerPoint = new PowerPointInfo(application);
            Log.Info($"PowerPoint {powerPoint.Version() ?? "version unknown"} on {WindowsVersion()}" +
                     (powerPoint.Available ? "" : " (no Application object: cannot tell the audience window apart)"));

            var settings = new SettingsStore(Path.Combine(DataDirectory, "config.json"));
            settings.Load();
            var timer = new TimerModel(settings);

            SoundAlert.Init(DataDirectory);
            timer.ZeroReached += () =>
            {
                if (settings.Current.SoundEnabled) SoundAlert.Play(settings.Current.SoundFile);
            };
            timer.SoundTestRequested += () => SoundAlert.Play(settings.Current.SoundFile);

            // Must be created on PowerPoint's UI thread (we are inside OnConnection).
            overlay = new OverlayController(timer, settings, powerPoint);

            api = new ApiServer(timer, settings, overlay.Diagnostics);
            api.Start();
        }

        public void Dispose()
        {
            api?.Dispose();
            overlay?.Dispose();
            powerPoint?.Dispose();
            Log.Info("PPTimer stopped");
        }

        /// <summary>"Windows 11 Pro 24H2 (build 26100.4061)". Environment.OSVersion is unreliable without an app manifest.</summary>
        static string WindowsVersion()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    var build = Convert.ToString(key?.GetValue("CurrentBuildNumber"));
                    var name = Convert.ToString(key?.GetValue("ProductName"));
                    // ProductName still says "Windows 10" on Windows 11.
                    if (int.TryParse(build, out var b) && b >= 22000) name = name?.Replace("Windows 10", "Windows 11");
                    return $"{name} {key?.GetValue("DisplayVersion")} (build {build}.{key?.GetValue("UBR")})";
                }
            }
            catch
            {
                return Environment.OSVersion.ToString();
            }
        }
    }
}
