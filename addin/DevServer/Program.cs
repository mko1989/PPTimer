using System;
using System.IO;
using System.Threading;
using PPTimer.Core;

namespace PPTimer.DevServer
{
    /// <summary>
    /// dotnet run --project addin/DevServer [-- --no-presenter]
    /// Same API as the add-in, with the overlay replaced by a console line.
    /// </summary>
    internal static class Program
    {
        static int Main(string[] args)
        {
            var dataDir = Path.Combine(Directory.GetCurrentDirectory(), ".devserver");
            Directory.CreateDirectory(dataDir);
            Log.Init(dataDir);
            Log.EchoToConsole = true;

            var settings = new SettingsStore(Path.Combine(dataDir, "config.json"));
            settings.Load();
            var timer = new TimerModel(settings);
            timer.SetPresenterView(Array.IndexOf(args, "--no-presenter") < 0, 1920, 1080);
            timer.ZeroReached += () => Log.Info(settings.Current.SoundEnabled ? "(would play sound)" : "(sound disabled)");
            timer.SoundTestRequested += () => Log.Info("(would play test sound)");

            using var api = new ApiServer(timer, settings);
            api.Start();
            if (api.ListeningOn == null) return 1;

            Console.WriteLine($"Remote page: http://localhost:{settings.Current.Port}/   (Ctrl+C to quit)");
            var quit = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                quit.Set();
            };

            var interactive = !Console.IsOutputRedirected;
            while (!quit.Wait(250))
            {
                if (!interactive) continue;
                var s = timer.Snapshot();
                Console.Write($"\r  {s.Display,9}  {s.Phase,-8}  {(s.Running ? "running" : "paused "),-7}  overlay {(s.Visible ? "on " : "off")}  speed {s.SpeedPercent,5}%   ");
            }
            Console.WriteLine();
            return 0;
        }
    }
}
