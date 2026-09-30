using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PPTimer.Core
{
    public sealed class TimerSnapshot
    {
        public bool Running;
        public bool Visible;
        public bool PresenterView;
        public int PresenterWidth;
        public int PresenterHeight;
        public long DurationMs;
        public long RemainingMs;
        public int RemainingSeconds;
        public string Display;
        /// <summary>normal | warning | critical | expired</summary>
        public string Phase;
        public bool Overtime;
        public double Progress;

        /// <summary>Changes whenever anything a client would display changes.</summary>
        public string ChangeKey =>
            $"{Display}|{Phase}|{Running}|{Visible}|{PresenterView}|{PresenterWidth}x{PresenterHeight}|{DurationMs}";

        public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
        {
            ["running"] = Running,
            ["visible"] = Visible,
            ["presenterView"] = PresenterView,
            ["presenterWidth"] = PresenterWidth,
            ["presenterHeight"] = PresenterHeight,
            ["durationMs"] = DurationMs,
            ["duration"] = TimerModel.Format((int)Math.Ceiling(DurationMs / 1000.0), true),
            ["remainingMs"] = RemainingMs,
            ["remainingSeconds"] = RemainingSeconds,
            ["display"] = Display,
            ["phase"] = Phase,
            ["overtime"] = Overtime,
            ["progress"] = Math.Round(Progress, 4),
        };
    }

    /// <summary>
    /// Thread-safe countdown. Time is derived from a monotonic clock, so a missed UI tick or
    /// a dropped network message never makes the timer drift.
    /// </summary>
    public sealed class TimerModel
    {
        readonly object gate = new object();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly SettingsStore settings;

        long durationMs;
        long remainingAtAnchorMs;
        long anchorMs;
        bool running;
        bool visible = true;
        bool presenterView;
        int presenterWidth;
        int presenterHeight;
        bool zeroFired;

        public TimerModel(SettingsStore settings)
        {
            this.settings = settings;
            durationMs = remainingAtAnchorMs = settings.Current.DefaultDurationSeconds * 1000L;
        }

        /// <summary>Raised once each time a running countdown reaches zero (on whichever thread noticed).</summary>
        public event Action ZeroReached;

        /// <summary>Raised by the "testsound" command.</summary>
        public event Action SoundTestRequested;

        long Now => clock.ElapsedMilliseconds;

        long RemainingLocked()
        {
            var remaining = running ? remainingAtAnchorMs - (Now - anchorMs) : remainingAtAnchorMs;
            return !settings.Current.CountUp && remaining < 0 ? 0 : remaining;
        }

        void Rebase(long remainingMs)
        {
            remainingAtAnchorMs = remainingMs;
            anchorMs = Now;
        }

        public void Start()
        {
            lock (gate)
            {
                if (running) return;
                Rebase(remainingAtAnchorMs);
                running = true;
            }
        }

        public void Pause()
        {
            lock (gate)
            {
                if (!running) return;
                Rebase(RemainingLocked());
                running = false;
            }
        }

        public void Toggle()
        {
            lock (gate)
            {
                if (running) Pause();
                else Start();
            }
        }

        /// <summary>Back to the full duration, paused.</summary>
        public void Reset()
        {
            lock (gate)
            {
                Rebase(durationMs);
                running = false;
            }
        }

        /// <summary>Back to the full duration and running.</summary>
        public void Restart()
        {
            lock (gate)
            {
                Rebase(durationMs);
                running = true;
            }
        }

        /// <summary>Sets a new duration and remaining time. Keeps the running state unless <paramref name="start"/> is given.</summary>
        public void Set(long ms, bool? start)
        {
            lock (gate)
            {
                durationMs = ms;
                Rebase(ms);
                if (start.HasValue) running = start.Value;
            }
        }

        /// <summary>Adds (or with a negative value, removes) time from the remaining time.</summary>
        public void Add(long ms)
        {
            lock (gate)
            {
                Rebase(RemainingLocked() + ms);
            }
        }

        public void SetVisible(bool value)
        {
            lock (gate) visible = value;
        }

        public void ToggleVisible()
        {
            lock (gate) visible = !visible;
        }

        public void SetPresenterView(bool detected, int width, int height)
        {
            lock (gate)
            {
                presenterView = detected;
                if (detected)
                {
                    presenterWidth = width;
                    presenterHeight = height;
                }
            }
        }

        public void RequestSoundTest() => SoundTestRequested?.Invoke();

        public TimerSnapshot Snapshot()
        {
            var cfg = settings.Current;
            TimerSnapshot snap;
            var fireZero = false;
            lock (gate)
            {
                var remaining = RemainingLocked();
                if (remaining > 0)
                {
                    zeroFired = false;
                }
                else if (running && !zeroFired)
                {
                    zeroFired = true;
                    fireZero = true;
                }

                var seconds = (int)Math.Ceiling(remaining / 1000.0);
                string phase;
                if (remaining <= 0) phase = "expired";
                else if (cfg.CriticalEnabled && seconds <= cfg.CriticalSeconds) phase = "critical";
                else if (cfg.WarnEnabled && seconds <= cfg.WarnSeconds) phase = "warning";
                else phase = "normal";

                snap = new TimerSnapshot
                {
                    Running = running,
                    Visible = visible,
                    PresenterView = presenterView,
                    PresenterWidth = presenterWidth,
                    PresenterHeight = presenterHeight,
                    DurationMs = durationMs,
                    RemainingMs = remaining,
                    RemainingSeconds = seconds,
                    Display = Format(seconds, cfg.ShowMinus),
                    Phase = phase,
                    Overtime = remaining < 0,
                    Progress = durationMs > 0 ? Math.Max(0, Math.Min(1, remaining / (double)durationMs)) : 0,
                };
            }

            if (fireZero)
            {
                try { ZeroReached?.Invoke(); }
                catch (Exception ex) { Log.Error("ZeroReached handler failed", ex); }
            }
            return snap;
        }

        /// <summary>MM:SS, or H:MM:SS from one hour. Negative values (overtime) get a '-' only if <paramref name="showMinus"/>.</summary>
        public static string Format(int seconds, bool showMinus)
        {
            var sign = seconds < 0 && showMinus ? "-" : "";
            var a = Math.Abs((long)seconds);
            var h = a / 3600;
            var m = a % 3600 / 60;
            var s = a % 60;
            return h > 0 ? $"{sign}{h}:{m:00}:{s:00}" : $"{sign}{m:00}:{s:00}";
        }
    }
}
