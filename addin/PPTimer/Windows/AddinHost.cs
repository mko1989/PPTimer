using System;
using System.Diagnostics;
using System.IO;
using PPTimer.Core;

namespace PPTimer.Windows
{
    /// <summary>Wires the timer, the API server and the overlay together for the lifetime of PowerPoint.</summary>
    internal sealed class AddinHost : IDisposable
    {
        ApiServer api;
        OverlayController overlay;

        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PPTimer");

        public void Start()
        {
            Directory.CreateDirectory(DataDirectory);
            Log.Init(DataDirectory);
            Log.Info($"PPTimer {ApiServer.Version} starting in {Process.GetCurrentProcess().ProcessName} " +
                     $"({(Environment.Is64BitProcess ? "64" : "32")}-bit, CLR {Environment.Version})");

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
            overlay = new OverlayController(timer, settings);

            api = new ApiServer(timer, settings, PresenterWindowFinder.DescribeProcessWindows);
            api.Start();
        }

        public void Dispose()
        {
            api?.Dispose();
            overlay?.Dispose();
            Log.Info("PPTimer stopped");
        }
    }
}
