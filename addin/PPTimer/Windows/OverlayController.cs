using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using Microsoft.Win32;
using PPTimer.Core;
using static PPTimer.Windows.NativeMethods;
using Form = System.Windows.Forms.Form;
using Screen = System.Windows.Forms.Screen;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace PPTimer.Windows
{
    /// <summary>
    /// Runs on the UI thread. Every 100 ms: resolve the active layout's target (PowerPoint's presenter view, or a
    /// screen), place the timer (and the black backdrop for blackout layouts) on it, and hand it the latest timer
    /// state. Polling (instead of PowerPoint events) also covers Alt+F5, "Swap displays" and window moves.
    /// Logs enough (slide shows, every visible PowerPoint window when they change, why nothing matched) to fix
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
        const int ZOrderCheckMs = 100;
        const int ScreensRefreshMs = 3000;

        readonly TimerModel timer;
        readonly SettingsStore settings;
        readonly PowerPointInfo powerPoint;
        readonly WinFormsTimer tick;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly OverlayForm form;
        readonly BackdropForm backdrop;
        static readonly uint OwnProcessId = (uint)Process.GetCurrentProcess().Id;

        Detection attached;
        long lastScanMs = -ScanIntervalMs;
        string lastWindowSignature;
        string lastShowsSummary;
        long showStartedMs = -1;
        bool warnedMissing;
        string shownState;
        string lastTarget;
        string lastLayout;
        long expiredSinceMs = -1;
        long lastZOrderMs;
        long screensAt = -ScreensRefreshMs;
        List<ScreenEntry> screens = new List<ScreenEntry>();
        string screensSummary;

        // Read by the API thread for /api/debug/windows.
        volatile List<SlideShowInfo> lastShows;
        volatile Detection attachedForApi;
        volatile string targetForApi;

        public OverlayController(TimerModel timer, SettingsStore settings, PowerPointInfo powerPoint)
        {
            this.timer = timer;
            this.settings = settings;
            this.powerPoint = powerPoint;
            LogDetectionConfig(settings.Current);

            form = new OverlayForm(settings.Current.ClickThrough);
            backdrop = new BackdropForm(settings.Current.ClickThrough);
            _ = form.Handle;
            _ = backdrop.Handle;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            tick = new WinFormsTimer { Interval = TickMs };
            tick.Tick += OnTick;
            tick.Start();
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e) => screensAt = -ScreensRefreshMs;

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
            var layout = cfg.ActiveLayout;
            RefreshScreens();
            if (layout.Name != lastLayout)
            {
                lastLayout = layout.Name;
                Log.Info($"Layout '{layout.Name}': {Json.Serialize(layout.ToDictionary(includeName: false))}");
            }

            // Where the timer goes: a presenter view's client area, or a whole screen.
            string target = null, missing;
            var area = Rectangle.Empty;
            var presenter = IntPtr.Zero;
            if (layout.IsScreen)
            {
                StopTrackingPresenterView("layout uses a screen");
                var screen = Screens.Resolve(screens, layout.Screen);
                missing = $"no screen matches '{layout.Screen}' ({screens.Count} connected)";
                if (screen != null)
                {
                    area = screen.Bounds;
                    target = $"Screen {screen.Description.Id} · {screen.Description.Name}";
                }
            }
            else if (layout.Target == "keynote")
            {
                StopTrackingPresenterView("layout is for Keynote");
                missing = "Keynote layouts only work in PPTimer for Mac";
            }
            else
            {
                TrackPresenterView(cfg);
                missing = "no PowerPoint presenter view";
                if (attached != null && GetClientRect(attached.Target, out var client) && client.Right > 0 && client.Bottom > 0)
                {
                    var origin = new POINT();
                    ClientToScreen(attached.Target, ref origin);
                    area = new Rectangle(origin.X, origin.Y, client.Right, client.Bottom);
                    presenter = attached.Owner;
                    target = "PowerPoint presenter view";
                }
            }

            targetForApi = target;
            timer.SetTarget(target, area.Width, area.Height);
            if (target != lastTarget)
            {
                Log.Info(target != null ? $"Timer target: {target} at {area.X},{area.Y} {area.Width}x{area.Height}" : $"Timer target: none ({missing})");
                lastTarget = target;
            }
            if (target == null)
            {
                SetShown(false, missing);
                return;
            }

            var snap = timer.Snapshot();
            var level = BlinkLevel(snap, cfg);
            if (!snap.Visible)
            {
                SetShown(false, "timer hidden (show/hide command)");
                return;
            }
            if (presenter != IntPtr.Zero && IsIconic(presenter))
            {
                SetShown(false, "presenter view minimized");
                return;
            }

            var bounds = ComputeBounds(area, layout);
            if (bounds.Width < 4 || bounds.Height < 4)
            {
                SetShown(false, $"target too small ({area.Width}x{area.Height})");
                return;
            }
            // Always on top, so it would float over another program the operator opened over the presenter view.
            if (presenter != IntPtr.Zero && !layout.Blackout && cfg.ClickThrough && CoveredByAnotherProgram(bounds))
            {
                SetShown(false, "another program covers the presenter view");
                return;
            }

            var restack = false;
            if (layout.Blackout)
            {
                if (backdrop.Bounds != area) backdrop.Bounds = area;
                if (!backdrop.Visible)
                {
                    backdrop.Show();
                    restack = true; // the timer may already be showing: it must end up above the black
                }
            }
            else if (backdrop.Visible) backdrop.Hide();

            form.Render(snap, cfg, level, bounds);
            SetShown(true, $"on {target} at {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}" + (layout.Blackout ? ", rest black" : ""));
            KeepOnTop(restack);
        }

        void RefreshScreens()
        {
            if (clock.ElapsedMilliseconds - screensAt < ScreensRefreshMs) return;
            screensAt = clock.ElapsedMilliseconds;
            screens = Screens.List();
            var summary = Screens.Summary(screens);
            if (summary == screensSummary) return;
            screensSummary = summary;
            Log.Info("Screens: " + summary);
            timer.SetScreens(screens.Select(s => s.Description).ToList());
        }

        /// <summary>Whether the window under the middle of the timer belongs to another program than PowerPoint.</summary>
        bool CoveredByAnotherProgram(Rectangle bounds)
        {
            // Click-through windows (our own) are skipped by WindowFromPoint.
            var hit = WindowFromPoint(new POINT { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 });
            if (hit == IntPtr.Zero) return false;
            var root = GetAncestor(hit, GA_ROOT);
            GetWindowThreadProcessId(root, out var pid);
            if (pid == OwnProcessId) return false; // e.g. PPTimer's own tray menu
            return !PresenterWindowFinder.IsPowerPointWindow(root);
        }

        /// <summary>
        /// Back on top whenever another visible window (e.g. a slide show that was clicked) overlaps ours, and the
        /// timer always directly above the black backdrop. <paramref name="force"/> restacks now.
        /// </summary>
        void KeepOnTop(bool force = false)
        {
            if (!force && clock.ElapsedMilliseconds - lastZOrderMs < ZOrderCheckMs) return;
            lastZOrderMs = clock.ElapsedMilliseconds;
            if (force || NeedsRestack()) Restack();
        }

        bool NeedsRestack()
        {
            // The timer must sit right above the backdrop, not under it.
            if (backdrop.Visible && GetWindow(backdrop.Handle, GW_HWNDPREV) != form.Handle) return true;
            var lowest = backdrop.Visible ? (Form)backdrop : form;
            var ours = lowest.Bounds;
            var steps = 0;
            for (var h = GetWindow(lowest.Handle, GW_HWNDPREV); h != IntPtr.Zero && steps++ < 500; h = GetWindow(h, GW_HWNDPREV))
            {
                if (h == form.Handle || h == backdrop.Handle || !IsWindowVisible(h)) continue;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
                if (GetWindowRect(h, out var r) && ours.IntersectsWith(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom))) return true;
            }
            return false;
        }

        /// <summary>Backdrop to the top, then the timer above it.</summary>
        void Restack()
        {
            if (backdrop.Visible) backdrop.BringToTop();
            if (form.Visible) form.BringToTop();
        }

        void TrackPresenterView(Settings cfg)
        {
            if (attached != null)
            {
                if (!IsWindow(attached.Owner) || !IsWindow(attached.Target)) Detach("presenter view window destroyed");
                else if (!IsWindowVisible(attached.Owner) || !IsWindowVisible(attached.Target)) Detach("presenter view window hidden");
            }

            var interval = attached == null ? ScanIntervalMs : RecheckIntervalMs;
            if (clock.ElapsedMilliseconds - lastScanMs >= interval)
            {
                lastScanMs = clock.ElapsedMilliseconds;
                Scan(cfg);
            }
        }

        void StopTrackingPresenterView(string reason)
        {
            powerPoint.Watch(false);
            if (attached != null) Detach(reason);
            lastWindowSignature = null;
            lastShowsSummary = null;
        }

        /// <summary>Reads slide shows and windows, logs what changed, and attaches to (or switches to) the best match.</summary>
        void Scan(Settings cfg)
        {
            var shows = powerPoint.SlideShows();
            lastShows = shows;
            var audience = new HashSet<IntPtr>(shows?.Select(s => s.Hwnd) ?? Enumerable.Empty<IntPtr>());
            var running = shows != null && shows.Count > 0;
            LogSlideShowChanges(shows);

            var windows = PresenterWindowFinder.TopLevelWindows(visibleOnly: true);
            // The object model is only read while PowerPoint shows slide show or presenter windows.
            powerPoint.Watch(windows.Any(w => w.Class == PresenterWindowFinder.SlideShowClass || w.Class == PresenterWindowFinder.PresenterClass ||
                                              cfg.PresenterWindowClasses.Contains(w.Class, StringComparer.OrdinalIgnoreCase)));
            var signature = PresenterWindowFinder.ClassSignature(windows);
            if (signature != lastWindowSignature)
            {
                lastWindowSignature = signature;
                Log.Info($"PowerPoint visible windows changed: {signature}" +
                         (running ? $" (slide show running, {shows.Count})" : "") +
                         PresenterWindowFinder.DescribeAll(windows, audience));
            }

            var found = PresenterWindowFinder.Detect(cfg, windows, audience, running);

            if (found != null && (found.Owner != attached?.Owner || found.Target != attached?.Target))
            {
                if (attached != null) Detach($"better match found ({found.Rule})");
                Attach(found);
            }

            if (attached == null && running && !warnedMissing && clock.ElapsedMilliseconds - showStartedMs >= MissingWarnMs)
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

        /// <summary>Shows or hides the timer (and backdrop); logs each change of where it is or why it is hidden.</summary>
        void SetShown(bool shown, string state)
        {
            if (!shown)
            {
                if (form.Visible) form.Hide();
                if (backdrop.Visible) backdrop.Hide();
            }
            else if (!form.Visible)
            {
                form.Show();
                form.BringToTop();
            }
            if (state == shownState) return;
            shownState = state;
            Log.Info(shown ? $"Timer shown {state}" : $"Timer hidden: {state}");
        }

        void Attach(Detection found)
        {
            attached = found;
            attachedForApi = found;
            Log.Info($"Presenter view found: {found}");
        }

        void Detach(string reason)
        {
            attached = null;
            attachedForApi = null;
            Log.Info($"Presenter view lost: {reason}");
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
                    Log.Warn($"config.json: presenterWindowClasses contains {cls}, the class of the audience slide show. PPTimer skips the audience window, " +
                             "and finds a presenter view of this class by itself; it is safer to remove it.");
                else if (!string.Equals(cls, PresenterWindowFinder.PresenterClass, StringComparison.OrdinalIgnoreCase))
                    Log.Warn($"config.json: presenterWindowClasses contains {cls}, which is not a known presenter view class.");
            }
        }

        /// <summary>For GET /api/debug/windows (API thread): what the last scan saw and chose, plus a fresh window list.</summary>
        public Dictionary<string, object> Diagnostics()
        {
            var shows = lastShows;
            var found = attachedForApi;
            var audience = new HashSet<IntPtr>(shows?.Select(s => s.Hwnd) ?? Enumerable.Empty<IntPtr>());
            var cfg = settings.Current;
            return new Dictionary<string, object>
            {
                ["layout"] = cfg.ActiveLayout.ToDictionary(),
                ["target"] = targetForApi,
                ["attached"] = found == null ? null : new Dictionary<string, object>
                {
                    ["owner"] = WindowInfo.Hex(found.Owner),
                    ["target"] = WindowInfo.Hex(found.Target),
                    ["rule"] = found.Rule,
                    ["class"] = found.Window?.Class,
                    ["title"] = found.Window?.Title,
                },
                ["powerPointVersion"] = powerPoint.Version,
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
                ["screens"] = screensSummary,
                ["windows"] = PresenterWindowFinder.DescribeProcessWindows(audience),
            };
        }

        /// <summary>The layout's rectangle (percentages of the target area), in screen pixels, kept inside the area.</summary>
        static Rectangle ComputeBounds(Rectangle area, Layout layout)
        {
            int cw = area.Width, ch = area.Height;
            var width = Clamp((int)Math.Round(cw * layout.WidthPercent / 100), 8, cw);
            var height = Clamp((int)Math.Round(ch * layout.HeightPercent / 100), 8, ch);
            var x = Clamp((int)Math.Round(cw * layout.XPercent / 100), 0, cw - width);
            var y = Clamp((int)Math.Round(ch * layout.YPercent / 100), 0, ch - height);
            return new Rectangle(area.X + x, area.Y + y, width, height);
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
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            tick.Stop();
            tick.Dispose();
            form.Close();
            form.Dispose();
            backdrop.Close();
            backdrop.Dispose();
        }
    }
}
