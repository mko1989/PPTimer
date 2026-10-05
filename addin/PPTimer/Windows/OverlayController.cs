using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using PPTimer.Core;
using static PPTimer.Windows.NativeMethods;
using Screen = System.Windows.Forms.Screen;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace PPTimer.Windows
{
    /// <summary>
    /// Runs on PowerPoint's UI thread. Every 100 ms: find/track the presenter view window,
    /// create/destroy the overlay with it, keep it positioned, and hand it the latest timer state.
    /// Polling (instead of PowerPoint events) also covers Alt+F5, "Swap displays" and window moves.
    /// Logs enough (slide shows, every visible window when they change, why nothing matched) to fix
    /// detection on a machine we cannot see.
    /// </summary>
    internal sealed class OverlayController : IDisposable
    {
        const int TickMs = 100;
        const int BlinkTickMs = 33;          // ~30 fps while fading
        const double BlinkPeriodMs = 2000;   // one full fade out + fade in
        const double BlinkMinLevel = 0.12;
        const int ScanIntervalMs = 400;
        const int RecheckIntervalMs = 2000;  // while attached: is there a better match now?
        const int MissingWarnMs = 3000;      // slide show running this long without a presenter view -> warn

        readonly TimerModel timer;
        readonly SettingsStore settings;
        readonly PowerPointInfo powerPoint;
        readonly WinFormsTimer tick;
        readonly Stopwatch clock = Stopwatch.StartNew();

        OverlayForm form;
        IntPtr host;    // top-level owner of the overlay
        IntPtr target;  // window whose client area the overlay is placed in (usually == host)
        long lastScanMs = -ScanIntervalMs;
        string lastWindowSignature;
        string lastShowsSummary;
        long showStartedMs = -1;
        bool warnedMissing;
        bool? overlayShown;
        long expiredSinceMs = -1;

        // Read by the API thread for /api/debug/windows.
        volatile List<SlideShowInfo> lastShows;
        volatile Detection attached;

        public OverlayController(TimerModel timer, SettingsStore settings, PowerPointInfo powerPoint)
        {
            this.timer = timer;
            this.settings = settings;
            this.powerPoint = powerPoint;
            LogDetectionConfig(settings.Current);

            tick = new WinFormsTimer { Interval = TickMs };
            tick.Tick += OnTick;
            tick.Start();
        }

        void OnTick(object sender, EventArgs e)
        {
            try
            {
                Update();
            }
            catch (Exception ex)
            {
                Log.Error("Overlay update failed", ex);
            }
        }

        void Update()
        {
            var cfg = settings.Current;

            if (host != IntPtr.Zero)
            {
                if (!IsWindow(host) || !IsWindow(target)) Detach("presenter view window destroyed");
                else if (!IsWindowVisible(host) || !IsWindowVisible(target)) Detach("presenter view window hidden");
            }

            var interval = host == IntPtr.Zero ? ScanIntervalMs : RecheckIntervalMs;
            if (clock.ElapsedMilliseconds - lastScanMs >= interval)
            {
                lastScanMs = clock.ElapsedMilliseconds;
                Scan(cfg);
            }

            if (host == IntPtr.Zero || !GetClientRect(target, out var client))
            {
                timer.SetPresenterView(false, 0, 0);
                return;
            }
            timer.SetPresenterView(true, client.Right, client.Bottom);

            var snap = timer.Snapshot();
            if (!snap.Visible || IsIconic(host))
            {
                SetOverlayShown(false, !snap.Visible ? "timer hidden (show/hide command)" : "presenter view minimized", Rectangle.Empty);
                return;
            }

            var bounds = ComputeBounds(target, client, cfg);
            if (bounds.Width < 4 || bounds.Height < 4)
            {
                SetOverlayShown(false, $"presenter view too small ({client.Right}x{client.Bottom})", Rectangle.Empty);
                return;
            }
            form.Render(snap, cfg, BlinkLevel(snap, cfg), bounds);
            SetOverlayShown(true, null, bounds);
        }

        /// <summary>Reads slide shows and windows, logs what changed, and attaches to (or switches to) the best match.</summary>
        void Scan(Settings cfg)
        {
            var shows = powerPoint.SlideShows();
            lastShows = shows;
            var audience = new HashSet<IntPtr>(shows?.Select(s => s.Hwnd) ?? Enumerable.Empty<IntPtr>());
            var running = shows != null && shows.Count > 0;
            LogSlideShowChanges(shows);

            var windows = PresenterWindowFinder.TopLevelWindows(visibleOnly: true, exclude: form?.Handle ?? IntPtr.Zero);
            var signature = PresenterWindowFinder.ClassSignature(windows);
            if (signature != lastWindowSignature)
            {
                lastWindowSignature = signature;
                Log.Info($"PowerPoint visible windows changed: {signature}" +
                         (running ? $" (slide show running, {shows.Count})" : "") +
                         PresenterWindowFinder.DescribeAll(windows, audience));
            }

            var found = PresenterWindowFinder.Detect(cfg, windows, audience, running);

            if (found != null && (found.Owner != host || found.Target != target))
            {
                if (host != IntPtr.Zero) Detach($"better match found ({found.Rule})");
                Attach(found, cfg);
            }

            if (host == IntPtr.Zero && running && !warnedMissing && clock.ElapsedMilliseconds - showStartedMs >= MissingWarnMs)
            {
                warnedMissing = true;
                Log.Warn($"Slide show running for {MissingWarnMs / 1000} s but no presenter view found. {MissingHint(shows, windows, audience)}" +
                         Environment.NewLine + $"    presenterWindowClasses=[{string.Join(", ", cfg.PresenterWindowClasses)}]" +
                         $" presenterWindowTitles=[{string.Join(", ", cfg.PresenterWindowTitles)}]" +
                         Environment.NewLine + "    Monitors: " + MonitorSummary() +
                         PresenterWindowFinder.DescribeAll(windows, audience));
            }
        }

        static string MissingHint(List<SlideShowInfo> shows, List<WindowInfo> windows, HashSet<IntPtr> audience)
        {
            if (shows.Any(s => s.ShowPresenterView == false))
                return "\"Use Presenter View\" is off for this presentation (Slide Show tab), so PowerPoint did not open one.";
            var showWindows = windows.Count(w => string.Equals(w.Class, PresenterWindowFinder.SlideShowClass, StringComparison.OrdinalIgnoreCase));
            if (showWindows <= 1 && Screen.AllScreens.Length < 2)
                return "Only one monitor is connected, so there is only the full-screen slide show (Alt+F5 opens presenter view on one screen).";
            if (showWindows <= 1)
                return "Only the audience window is visible; PowerPoint may be duplicating displays or presenter view is not open.";
            return "Unknown window layout: please send this log section so detection can be extended.";
        }

        void LogSlideShowChanges(List<SlideShowInfo> shows)
        {
            // Slide number left out: it changes on every click.
            var summary = shows == null ? "unknown"
                : shows.Count == 0 ? "none"
                : string.Join("; ", shows.Select(s => $"0x{s.Hwnd.ToInt64():X} '{s.Presentation}' usePresenterView={(s.ShowPresenterView?.ToString() ?? "?")}"));
            if (summary == lastShowsSummary) return;

            var wasRunning = lastShowsSummary != null && lastShowsSummary != "none" && lastShowsSummary != "unknown";
            var running = shows != null && shows.Count > 0;
            lastShowsSummary = summary;

            if (running && !wasRunning)
            {
                showStartedMs = clock.ElapsedMilliseconds;
                warnedMissing = false;
                Log.Info($"Slide show started: {string.Join("; ", shows)}. Monitors: {MonitorSummary()}");
            }
            else if (running)
                Log.Info($"Slide shows changed: {string.Join("; ", shows)}");
            else if (wasRunning && shows != null)
                Log.Info("Slide show ended");
        }

        static string MonitorSummary() =>
            string.Join("; ", Screen.AllScreens.Select(s =>
                $"{s.DeviceName.Replace(@"\\.\", "")} {s.Bounds.Width}x{s.Bounds.Height} at {s.Bounds.X},{s.Bounds.Y}{(s.Primary ? " primary" : "")}"));

        void SetOverlayShown(bool shown, string reason, Rectangle bounds)
        {
            if (shown && !form.Visible) form.Show();
            if (!shown && form.Visible) form.Hide();
            if (overlayShown == shown) return;
            overlayShown = shown;
            Log.Info(shown ? $"Overlay shown at {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}" : $"Overlay hidden: {reason}");
        }

        void Attach(Detection found, Settings cfg)
        {
            host = found.Owner;
            target = found.Target;
            overlayShown = null;
            attached = found;
            form = new OverlayForm(host, cfg.ClickThrough);
            _ = form.Handle; // create now, with the owner set via CreateParams
            Log.Info($"Presenter view found: {found}; overlay attached");
        }

        void Detach(string reason)
        {
            if (form != null)
            {
                form.Close();
                form.Dispose();
                form = null;
            }
            host = target = IntPtr.Zero;
            attached = null;
            Log.Info($"Overlay detached: {reason}");
        }

        /// <summary>Startup: the detection settings in use, and warnings for ones that point at the wrong window.</summary>
        static void LogDetectionConfig(Settings cfg)
        {
            Log.Info($"Presenter view detection: classes [{string.Join(", ", cfg.PresenterWindowClasses)}], " +
                     $"titles [{string.Join(", ", cfg.PresenterWindowTitles)}]. Monitors: {MonitorSummary()}");
            foreach (var cls in cfg.PresenterWindowClasses)
            {
                if (string.Equals(cls, PresenterWindowFinder.MainWindowClass, StringComparison.OrdinalIgnoreCase))
                    Log.Warn($"config.json: presenterWindowClasses contains {cls}, PowerPoint's main editing window. The timer will show there instead of the presenter view; remove it.");
                else if (string.Equals(cls, PresenterWindowFinder.SlideShowClass, StringComparison.OrdinalIgnoreCase))
                    Log.Warn($"config.json: presenterWindowClasses contains {cls}, the class of the audience slide show. The add-in skips the audience window, " +
                             "and finds a presenter view of this class by itself; it is safer to remove it.");
                else if (!string.Equals(cls, PresenterWindowFinder.PresenterClass, StringComparison.OrdinalIgnoreCase))
                    Log.Warn($"config.json: presenterWindowClasses contains {cls}, which is not a known presenter view class.");
            }
        }

        /// <summary>For GET /api/debug/windows (API thread): what the last scan saw and chose, plus a fresh window list.</summary>
        public Dictionary<string, object> Diagnostics()
        {
            var shows = lastShows;
            var found = attached;
            var audience = new HashSet<IntPtr>(shows?.Select(s => s.Hwnd) ?? Enumerable.Empty<IntPtr>());
            return new Dictionary<string, object>
            {
                ["attached"] = found == null ? null : new Dictionary<string, object>
                {
                    ["owner"] = WindowInfo.Hex(found.Owner),
                    ["target"] = WindowInfo.Hex(found.Target),
                    ["rule"] = found.Rule,
                    ["class"] = found.Window?.Class,
                    ["title"] = found.Window?.Title,
                },
                ["objectModel"] = powerPoint.Available,
                ["slideShows"] = shows?.Select(s => (object)new Dictionary<string, object>
                {
                    ["audienceHwnd"] = WindowInfo.Hex(s.Hwnd),
                    ["presentation"] = s.Presentation,
                    ["usePresenterView"] = s.ShowPresenterView,
                    ["slide"] = s.Slide,
                }).ToList(),
                ["monitors"] = Screen.AllScreens.Select(s => (object)new Dictionary<string, object>
                {
                    ["name"] = s.DeviceName,
                    ["bounds"] = new[] { s.Bounds.Left, s.Bounds.Top, s.Bounds.Right, s.Bounds.Bottom },
                    ["primary"] = s.Primary,
                }).ToList(),
                ["windows"] = PresenterWindowFinder.DescribeProcessWindows(audience),
            };
        }

        /// <summary>The configured rectangle (percentages of the client area), in screen pixels, kept inside the presenter view.</summary>
        static Rectangle ComputeBounds(IntPtr hwnd, RECT client, Settings cfg)
        {
            var origin = new POINT();
            ClientToScreen(hwnd, ref origin);

            int cw = client.Right, ch = client.Bottom;
            var width = Clamp((int)Math.Round(cw * cfg.WidthPercent / 100), 8, cw);
            var height = Clamp((int)Math.Round(ch * cfg.HeightPercent / 100), 8, ch);
            var x = Clamp((int)Math.Round(cw * cfg.XPercent / 100), 0, cw - width);
            var y = Clamp((int)Math.Round(ch * cfg.YPercent / 100), 0, ch - height);
            return new Rectangle(origin.X + x, origin.Y + y, width, height);
        }

        static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));

        /// <summary>
        /// 1 = fully visible. While blinking at zero, a cosine fade that starts at full brightness
        /// when zero is reached; the tick speeds up meanwhile so the fade is smooth.
        /// </summary>
        double BlinkLevel(TimerSnapshot snap, Settings cfg)
        {
            var blinking = snap.Phase == "expired" && snap.Running && cfg.BlinkAtZero;
            var interval = blinking ? BlinkTickMs : TickMs;
            if (tick.Interval != interval) tick.Interval = interval;
            if (!blinking)
            {
                expiredSinceMs = -1;
                return 1;
            }

            var now = clock.ElapsedMilliseconds;
            if (expiredSinceMs < 0) expiredSinceMs = now;
            var wave = 0.5 + 0.5 * Math.Cos(2 * Math.PI * (now - expiredSinceMs) / BlinkPeriodMs);
            return BlinkMinLevel + (1 - BlinkMinLevel) * wave;
        }

        public void Dispose()
        {
            tick.Stop();
            tick.Dispose();
            if (host != IntPtr.Zero || form != null) Detach("shutdown");
        }
    }
}
