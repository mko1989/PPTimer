using System;
using System.Diagnostics;
using System.Drawing;
using PPTimer.Core;
using static PPTimer.Windows.NativeMethods;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace PPTimer.Windows
{
    /// <summary>
    /// Runs on PowerPoint's UI thread. Every 100 ms: find/track the presenter view window,
    /// create/destroy the overlay with it, keep it positioned, and hand it the latest timer state.
    /// Polling (instead of PowerPoint events) also covers Alt+F5, "Swap displays" and window moves.
    /// </summary>
    internal sealed class OverlayController : IDisposable
    {
        const int TickMs = 100;
        const int BlinkTickMs = 33;          // ~30 fps while fading
        const double BlinkPeriodMs = 2000;   // one full fade out + fade in
        const double BlinkMinLevel = 0.12;
        const int ScanIntervalMs = 400;

        readonly TimerModel timer;
        readonly SettingsStore settings;
        readonly WinFormsTimer tick;
        readonly Stopwatch clock = Stopwatch.StartNew();

        OverlayForm form;
        IntPtr host;
        long lastScanMs = -ScanIntervalMs;
        string lastClassSummary;
        long expiredSinceMs = -1;

        public OverlayController(TimerModel timer, SettingsStore settings)
        {
            this.timer = timer;
            this.settings = settings;

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

            if (host != IntPtr.Zero && (!IsWindow(host) || !IsWindowVisible(host)))
                Detach("presenter view closed");

            if (host == IntPtr.Zero && clock.ElapsedMilliseconds - lastScanMs >= ScanIntervalMs)
            {
                lastScanMs = clock.ElapsedMilliseconds;
                LogWindowClassesIfChanged();
                var found = PresenterWindowFinder.Find(cfg.PresenterWindowClasses);
                if (found != IntPtr.Zero) Attach(found, cfg);
            }

            if (host == IntPtr.Zero || !GetClientRect(host, out var client))
            {
                timer.SetPresenterView(false, 0, 0);
                return;
            }
            timer.SetPresenterView(true, client.Right, client.Bottom);

            var snap = timer.Snapshot();
            if (!snap.Visible || IsIconic(host))
            {
                if (form.Visible) form.Hide();
                return;
            }

            var bounds = ComputeBounds(host, client, cfg);
            if (bounds.Width < 4 || bounds.Height < 4) return;
            form.Render(snap, cfg, BlinkLevel(snap, cfg), bounds);
            if (!form.Visible) form.Show();
        }

        void Attach(IntPtr hwnd, Settings cfg)
        {
            host = hwnd;
            form = new OverlayForm(hwnd, cfg.ClickThrough);
            _ = form.Handle; // create now, with the owner set via CreateParams
            Log.Info($"Presenter view found (hwnd 0x{hwnd.ToInt64():X}); overlay attached");
        }

        void Detach(string reason)
        {
            if (form != null)
            {
                form.Close();
                form.Dispose();
                form = null;
            }
            host = IntPtr.Zero;
            Log.Info($"Overlay detached: {reason}");
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

        /// <summary>Logs PowerPoint's visible window classes when they change, to verify the presenter view class on a new machine.</summary>
        void LogWindowClassesIfChanged()
        {
            var summary = PresenterWindowFinder.VisibleClassSummary();
            if (summary == lastClassSummary) return;
            lastClassSummary = summary;
            Log.Info($"PowerPoint visible window classes: {summary}");
        }

        public void Dispose()
        {
            tick.Stop();
            tick.Dispose();
            if (host != IntPtr.Zero || form != null) Detach("shutdown");
        }
    }
}
