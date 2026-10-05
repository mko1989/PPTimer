using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PPTimer.Core
{
    /// <summary>
    /// The command set shared by the REST API (<c>/api/{cmd}</c>) and the WebSocket (<c>{"cmd": ...}</c>).
    /// </summary>
    public static class Commands
    {
        public static readonly string[] Names =
        {
            "start", "pause", "toggle", "reset", "restart", "set", "add",
            "show", "hide", "togglevisible", "settings", "testsound", "speed",
        };

        static readonly string[] ReservedArgs = { "cmd", "id", "token" };

        /// <summary>Runs a command. Returns an error message, or null on success.</summary>
        public static string Execute(TimerModel timer, SettingsStore settings, string cmd, IDictionary<string, string> args)
        {
            switch ((cmd ?? "").Trim().ToLowerInvariant())
            {
                case "start":
                    timer.Start();
                    return null;
                case "pause":
                case "stop":
                    timer.Pause();
                    return null;
                case "toggle":
                    timer.Toggle();
                    return null;
                case "reset":
                    timer.Reset();
                    return null;
                case "restart":
                    timer.Restart();
                    return null;
                case "set":
                {
                    if (!TryGetDuration(args, out var ms, out var error)) return error;
                    if (ms < 0) return "Duration cannot be negative";
                    bool? start = null;
                    if (args.TryGetValue("start", out var startText))
                    {
                        try { start = Settings.ToBool(startText); }
                        catch (FormatException ex) { return ex.Message; }
                    }
                    timer.Set(ms, start);
                    // Remember it so a PowerPoint restart comes back with the same duration.
                    settings.Update(new[] { new KeyValuePair<string, object>("defaultDurationSeconds", (int)(ms / 1000)) }, allowAll: true);
                    return null;
                }
                case "add":
                {
                    if (!TryGetDuration(args, out var ms, out var error)) return error;
                    timer.Add(ms);
                    return null;
                }
                case "speed":
                {
                    // percent=105 or rate=1.05 sets it; step=5 / step=-5 nudges it.
                    if (args.TryGetValue("step", out var stepText) && TryParseNumber(stepText, out var step))
                    {
                        timer.SetSpeed(step, relative: true);
                        return null;
                    }
                    double percent;
                    if (args.TryGetValue("percent", out var p) && TryParseNumber(p, out percent)) { }
                    else if (args.TryGetValue("rate", out var r) && TryParseNumber(r, out var rate)) percent = rate * 100;
                    else return "Give 'percent' (e.g. 105), 'rate' (e.g. 1.05) or 'step' (e.g. 5 or -5)";
                    if (percent < TimerModel.MinSpeedPercent || percent > TimerModel.MaxSpeedPercent)
                        return $"Speed must be between {TimerModel.MinSpeedPercent} and {TimerModel.MaxSpeedPercent} %";
                    timer.SetSpeed(percent, relative: false);
                    return null;
                }
                case "show":
                    timer.SetVisible(true);
                    return null;
                case "hide":
                    timer.SetVisible(false);
                    return null;
                case "togglevisible":
                    timer.ToggleVisible();
                    return null;
                case "testsound":
                    timer.RequestSoundTest();
                    return null;
                case "settings":
                    return settings.Update(args
                        .Where(kv => !ReservedArgs.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                        .Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value)));
                default:
                    return $"Unknown command '{cmd}'. Commands: {string.Join(", ", Names)}";
            }
        }

        /// <summary>Reads a duration from <c>seconds</c>, <c>minutes</c> or <c>time</c> ("90", "5:00", "1:05:00", "-1:00").</summary>
        static bool TryGetDuration(IDictionary<string, string> args, out long ms, out string error)
        {
            ms = 0;
            error = null;
            if (args.TryGetValue("seconds", out var s) && TryParseNumber(s, out var seconds))
            {
                ms = (long)Math.Round(seconds * 1000);
                return true;
            }
            if (args.TryGetValue("minutes", out var m) && TryParseNumber(m, out var minutes))
            {
                ms = (long)Math.Round(minutes * 60_000);
                return true;
            }
            if (args.TryGetValue("time", out var t) && TryParseTime(t, out ms)) return true;

            error = "Give a duration as 'seconds', 'minutes' or 'time' (e.g. time=5:00)";
            return false;
        }

        static bool TryParseNumber(string text, out double value) =>
            double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.IsNaN(value) && Math.Abs(value) < 360000;

        public static bool TryParseTime(string text, out long ms)
        {
            ms = 0;
            text = (text ?? "").Trim();
            if (text.Length == 0) return false;

            var sign = 1;
            if (text[0] == '-' || text[0] == '+')
            {
                if (text[0] == '-') sign = -1;
                text = text.Substring(1).Trim();
            }

            var parts = text.Split(':');
            if (parts.Length > 3) return false;
            double total = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var part)) return false;
                total = total * 60 + part;
            }
            if (total >= 360000) return false;
            ms = sign * (long)Math.Round(total * 1000);
            return true;
        }
    }
}
