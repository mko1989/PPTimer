using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace PPTimer.Core
{
    /// <summary>
    /// Immutable settings snapshot. Changes go through <see cref="SettingsStore.Update"/>,
    /// which clones, validates, swaps and saves.
    /// </summary>
    public sealed class Settings
    {
        /// <summary>Bumped when defaults change meaning; older files have the affected keys reset.</summary>
        public const int CurrentConfigVersion = 2;

        /// <summary>Keys that can be changed at runtime over the API. Everything else is file-only.</summary>
        public static readonly string[] RuntimeKeys =
        {
            "xPercent", "yPercent", "widthPercent", "heightPercent", "opacity",
            "transparentBackground", "textOutline",
            "warnEnabled", "warnSeconds", "criticalEnabled", "criticalSeconds",
            "blinkAtZero", "countUp", "showMinus", "soundEnabled", "soundFile",
        };

        /// <summary>Keys from older versions, silently dropped when loading.</summary>
        static readonly string[] LegacyKeys = { "anchor", "marginPercent", "flashOvertime", "stopAtZero" };

        /// <summary>Keys whose defaults changed in version 2 (thresholds went from 2:00/0:30 to 3:00/1:00).</summary>
        static readonly string[] ResetBeforeV2 = { "warnSeconds", "criticalSeconds", "heightPercent", "opacity" };

        public int ConfigVersion { get; private set; } = CurrentConfigVersion;

        // Network (file-only, restart PowerPoint after changing)
        public int Port { get; private set; } = 9595;
        public string ApiToken { get; private set; } = "";

        // Presenter view detection (file-only)
        public string[] PresenterWindowClasses { get; private set; } = { "PodiumParent" };
        /// <summary>Title fragments of the presenter view window (localised PowerPoint: add e.g. "Referentenansicht").</summary>
        public string[] PresenterWindowTitles { get; private set; } = { "Presenter View" };
        public bool ClickThrough { get; private set; } = true;

        // Overlay rectangle, in % of the presenter view's client area (top-left corner + size)
        public double XPercent { get; private set; } = 22;
        public double YPercent { get; private set; } = 74;
        public double WidthPercent { get; private set; } = 16;
        public double HeightPercent { get; private set; } = 9;

        // Look
        public double Opacity { get; private set; } = 1;
        public bool TransparentBackground { get; private set; } = true;
        public bool TextOutline { get; private set; } = true;

        // Colour thresholds
        public bool WarnEnabled { get; private set; } = true;
        public int WarnSeconds { get; private set; } = 180;
        public bool CriticalEnabled { get; private set; } = true;
        public int CriticalSeconds { get; private set; } = 60;

        // At zero
        public bool BlinkAtZero { get; private set; } = true;
        public bool CountUp { get; private set; } = true;
        public bool ShowMinus { get; private set; }
        public bool SoundEnabled { get; private set; }
        public string SoundFile { get; private set; } = "";

        public int DefaultDurationSeconds { get; private set; } = 300;

        public Settings Clone() => (Settings)MemberwiseClone();

        public Dictionary<string, object> ToDictionary(bool includeSecrets)
        {
            var d = new Dictionary<string, object>
            {
                ["configVersion"] = ConfigVersion,
                ["port"] = Port,
                ["presenterWindowClasses"] = PresenterWindowClasses,
                ["presenterWindowTitles"] = PresenterWindowTitles,
                ["clickThrough"] = ClickThrough,
                ["xPercent"] = XPercent,
                ["yPercent"] = YPercent,
                ["widthPercent"] = WidthPercent,
                ["heightPercent"] = HeightPercent,
                ["opacity"] = Opacity,
                ["transparentBackground"] = TransparentBackground,
                ["textOutline"] = TextOutline,
                ["warnEnabled"] = WarnEnabled,
                ["warnSeconds"] = WarnSeconds,
                ["criticalEnabled"] = CriticalEnabled,
                ["criticalSeconds"] = CriticalSeconds,
                ["blinkAtZero"] = BlinkAtZero,
                ["countUp"] = CountUp,
                ["showMinus"] = ShowMinus,
                ["soundEnabled"] = SoundEnabled,
                ["soundFile"] = SoundFile,
                ["defaultDurationSeconds"] = DefaultDurationSeconds,
            };
            if (includeSecrets) d["apiToken"] = ApiToken;
            return d;
        }

        /// <summary>Applies one value. Returns an error message, or null on success.</summary>
        internal string Apply(string key, object value)
        {
            try
            {
                switch (key.ToLowerInvariant())
                {
                    case "configversion": ConfigVersion = (int)Range(ToDouble(value), 0, 1000); return null;
                    case "port": Port = (int)Range(ToDouble(value), 1, 65535); return null;
                    case "apitoken": ApiToken = ToText(value); return null;
                    case "presenterwindowclasses": PresenterWindowClasses = ToList(value); return null;
                    case "presenterwindowtitles": PresenterWindowTitles = ToList(value); return null;
                    case "clickthrough": ClickThrough = ToBool(value); return null;
                    case "xpercent": XPercent = Range(ToDouble(value), 0, 100); return null;
                    case "ypercent": YPercent = Range(ToDouble(value), 0, 100); return null;
                    case "widthpercent": WidthPercent = Range(ToDouble(value), 2, 100); return null;
                    case "heightpercent": HeightPercent = Range(ToDouble(value), 2, 100); return null;
                    case "opacity": Opacity = Range(ToDouble(value), 0.2, 1); return null;
                    case "transparentbackground": TransparentBackground = ToBool(value); return null;
                    case "textoutline": TextOutline = ToBool(value); return null;
                    case "warnenabled": WarnEnabled = ToBool(value); return null;
                    case "warnseconds": WarnSeconds = (int)Range(ToDouble(value), 0, 86400); return null;
                    case "criticalenabled": CriticalEnabled = ToBool(value); return null;
                    case "criticalseconds": CriticalSeconds = (int)Range(ToDouble(value), 0, 86400); return null;
                    case "blinkatzero": BlinkAtZero = ToBool(value); return null;
                    case "countup": CountUp = ToBool(value); return null;
                    case "showminus": ShowMinus = ToBool(value); return null;
                    case "soundenabled": SoundEnabled = ToBool(value); return null;
                    case "soundfile": SoundFile = ToText(value).Trim().Trim('"'); return null;
                    case "defaultdurationseconds": DefaultDurationSeconds = (int)Range(ToDouble(value), 0, 360000); return null;
                    default: return $"Unknown setting '{key}'";
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is ArgumentException)
            {
                return $"Invalid value for '{key}': {ex.Message}";
            }
        }

        internal static bool IsLegacyKey(string key) => LegacyKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

        internal static bool IsResetOnUpgrade(string key, int fileVersion) =>
            fileVersion < 2 && ResetBeforeV2.Contains(key, StringComparer.OrdinalIgnoreCase);

        static double Range(double v, double min, double max)
        {
            if (double.IsNaN(v) || v < min || v > max) throw new ArgumentException($"must be between {min} and {max}");
            return v;
        }

        /// <summary>A JSON array of strings, or one comma-separated string.</summary>
        static string[] ToList(object value)
        {
            var items = value is IEnumerable e && !(value is string)
                ? e.Cast<object>().Select(o => o is IEnumerable && !(o is string)
                    ? throw new FormatException("expected a flat list like [\"A\", \"B\"], not nested lists")
                    : Convert.ToString(o, CultureInfo.InvariantCulture))
                : ToText(value).Split(',');
            return items.Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
        }

        static string ToText(object v) => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";

        internal static double ToDouble(object v) => v is string s
            ? double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : Convert.ToDouble(v, CultureInfo.InvariantCulture);

        internal static bool ToBool(object v)
        {
            if (v is bool b) return b;
            var s = ToText(v).Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "on") return true;
            if (s == "false" || s == "0" || s == "no" || s == "off" || s == "") return false;
            throw new FormatException($"'{s}' is not a boolean");
        }
    }

    /// <summary>Owns the current <see cref="Settings"/> and persists them to config.json.</summary>
    public sealed class SettingsStore
    {
        readonly string path;
        readonly object gate = new object();
        Settings current = new Settings();

        public SettingsStore(string path)
        {
            this.path = path;
        }

        public event Action Changed;

        public Settings Current => Volatile.Read(ref current);

        public void Load()
        {
            if (!File.Exists(path))
            {
                Save(current);
                Log.Info($"Created default config at {path}");
                return;
            }

            try
            {
                if (!(Json.Parse(File.ReadAllText(path)) is Dictionary<string, object> values))
                    throw new FormatException("config root must be an object");

                var fileVersion = values.TryGetValue("configVersion", out var v) ? (int)Settings.ToDouble(v) : 1;
                var next = new Settings();
                foreach (var kv in values)
                {
                    if (Settings.IsLegacyKey(kv.Key) || kv.Key.Equals("configVersion", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Settings.IsResetOnUpgrade(kv.Key, fileVersion)) continue;
                    var error = next.Apply(kv.Key, kv.Value);
                    if (error != null) Log.Warn($"config.json: {error} (using default)");
                }
                Volatile.Write(ref current, next);
                Log.Info($"Loaded config from {path}");

                if (fileVersion < Settings.CurrentConfigVersion)
                {
                    Save(next);
                    Log.Info($"Upgraded config.json from version {fileVersion} to {Settings.CurrentConfigVersion}");
                }
            }
            catch (Exception ex)
            {
                // Keep the broken file: the next settings change would otherwise overwrite the user's edits.
                var backup = Path.Combine(Path.GetDirectoryName(path), "config.invalid.json");
                try { File.Copy(path, backup, true); }
                catch { backup = null; }
                Log.Error($"Could not read {path}: {ex.Message}. Using defaults for everything" +
                          (backup != null ? $"; the file was copied to {backup}" : ""));
            }
        }

        /// <summary>
        /// Applies a set of changes atomically. Returns an error message (nothing is applied), or null on success.
        /// Only <see cref="Settings.RuntimeKeys"/> are accepted unless <paramref name="allowAll"/> is set.
        /// </summary>
        public string Update(IEnumerable<KeyValuePair<string, object>> changes, bool allowAll = false)
        {
            lock (gate)
            {
                var next = current.Clone();
                var any = false;
                foreach (var kv in changes)
                {
                    if (!allowAll && !Settings.RuntimeKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                        return $"'{kv.Key}' cannot be changed at runtime (runtime settings: {string.Join(", ", Settings.RuntimeKeys)})";
                    var error = next.Apply(kv.Key, kv.Value);
                    if (error != null) return error;
                    any = true;
                }
                if (!any) return null;
                Volatile.Write(ref current, next);
                Save(next);
            }
            Changed?.Invoke();
            return null;
        }

        void Save(Settings settings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Json.Serialize(settings.ToDictionary(includeSecrets: true), indent: true) + "\n");
            }
            catch (Exception ex)
            {
                Log.Error($"Could not save {path}", ex);
            }
        }
    }
}
