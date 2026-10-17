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
        /// <summary>Bumped when defaults change meaning; older files have the affected keys reset.
        /// 3: position and look moved into "layouts".</summary>
        public const int CurrentConfigVersion = 3;

        /// <summary>Keys that can be changed at runtime over the API. Everything else is file-only.
        /// "layout" selects a layout by name; the layout keys change the active layout.</summary>
        public static readonly string[] RuntimeKeys =
        {
            "layout", "target", "screen", "xPercent", "yPercent", "widthPercent", "heightPercent", "opacity",
            "transparentBackground", "textOutline", "blackout",
            "warnEnabled", "warnSeconds", "criticalEnabled", "criticalSeconds",
            "blinkAtZero", "countUp", "showMinus", "soundEnabled", "soundFile",
        };

        /// <summary>Keys from older versions, silently dropped when loading.</summary>
        static readonly string[] LegacyKeys = { "anchor", "marginPercent", "flashOvertime", "stopAtZero" };

        /// <summary>Keys whose defaults changed in version 2 (thresholds went from 2:00/0:30 to 3:00/1:00).</summary>
        static readonly string[] ResetBeforeV2 = { "warnSeconds", "criticalSeconds", "heightPercent", "opacity" };

        public int ConfigVersion { get; private set; } = CurrentConfigVersion;

        // Network (file-only, restart PPTimer after changing)
        public int Port { get; private set; } = 9595;
        public string ApiToken { get; private set; } = "";

        // Presenter view detection (file-only)
        public string[] PresenterWindowClasses { get; private set; } = { "PodiumParent" };
        /// <summary>Title fragments of the presenter view window (localised PowerPoint: add e.g. "Referentenansicht").</summary>
        public string[] PresenterWindowTitles { get; private set; } = { "Presenter View" };
        public bool ClickThrough { get; private set; } = true;

        // Layout presets: target, overlay rectangle (in % of the target) and look. Never empty.
        public Layout[] Layouts { get; private set; } = Layout.Defaults();
        public int ActiveLayoutIndex { get; private set; }
        public Layout ActiveLayout => Layouts[ActiveLayoutIndex];

        public double XPercent => ActiveLayout.XPercent;
        public double YPercent => ActiveLayout.YPercent;
        public double WidthPercent => ActiveLayout.WidthPercent;
        public double HeightPercent => ActiveLayout.HeightPercent;
        public double Opacity => ActiveLayout.Opacity;
        public bool TransparentBackground => ActiveLayout.TransparentBackground;
        public bool TextOutline => ActiveLayout.TextOutline;

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

        public Settings Clone()
        {
            var c = (Settings)MemberwiseClone();
            c.Layouts = Layouts.Select(l => l.Clone()).ToArray();
            return c;
        }

        /// <summary>The on/off runtime settings, which <c>togglesetting</c> can flip.</summary>
        public IEnumerable<string> ToggleKeys
        {
            get
            {
                var values = ToDictionary(includeSecrets: false);
                return RuntimeKeys.Where(k => values[k] is bool);
            }
        }

        /// <summary>For the API: the active layout's values also appear flat (xPercent, ...), as before layouts existed.</summary>
        public Dictionary<string, object> ToDictionary(bool includeSecrets)
        {
            var d = ToFileDictionary(includeSecrets);
            foreach (var kv in ActiveLayout.ToDictionary(includeName: false)) d[kv.Key] = kv.Value;
            return d;
        }

        /// <summary>What config.json holds: the layouts as a list, no flat copies.</summary>
        public Dictionary<string, object> ToFileDictionary(bool includeSecrets = true)
        {
            var d = new Dictionary<string, object>
            {
                ["configVersion"] = ConfigVersion,
                ["port"] = Port,
                ["presenterWindowClasses"] = PresenterWindowClasses,
                ["presenterWindowTitles"] = PresenterWindowTitles,
                ["clickThrough"] = ClickThrough,
                ["layout"] = ActiveLayout.Name,
                ["layouts"] = Layouts.Select(l => (object)l.ToDictionary()).ToList(),
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
                    case "layouts": return SetLayouts(value);
                    case "layout": return SelectLayout(ToText(value));
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
                    default:
                        if (Layout.IsKey(key))
                        {
                            var error = ActiveLayout.Apply(key, value);
                            return error == null ? null : error.StartsWith("Invalid", StringComparison.Ordinal) ? error : $"Invalid value for '{key}': {error}";
                        }
                        return $"Unknown setting '{key}'";
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is ArgumentException)
            {
                return $"Invalid value for '{key}': {ex.Message}";
            }
        }

        // ---- Layouts ------------------------------------------------------------------------------

        public IEnumerable<string> LayoutNames => Layouts.Select(l => l.Name);

        int IndexOf(string name) =>
            Array.FindIndex(Layouts, l => l.Name.Equals((name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        string UnknownLayout(string name) => $"No layout named '{name}' (layouts: {string.Join(", ", LayoutNames)})";

        internal string SelectLayout(string name)
        {
            var i = IndexOf(name);
            if (i < 0) return UnknownLayout(name);
            ActiveLayoutIndex = i;
            return null;
        }

        /// <summary>Next (+1) or previous (-1) layout, wrapping around.</summary>
        internal void StepLayout(int step)
        {
            var n = Layouts.Length;
            ActiveLayoutIndex = ((ActiveLayoutIndex + step) % n + n) % n;
        }

        /// <summary>Copies the active layout under <paramref name="name"/> (replacing a layout of that name) and selects it.</summary>
        internal string SaveLayoutAs(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "Give the new layout a 'name'";
            if (name.Length > 40) return "Layout names are limited to 40 characters";
            var copy = ActiveLayout.Clone();
            copy.Name = name;
            var i = IndexOf(name);
            if (i >= 0)
            {
                Layouts[i] = copy;
                ActiveLayoutIndex = i;
            }
            else
            {
                Layouts = Layouts.Concat(new[] { copy }).ToArray();
                ActiveLayoutIndex = Layouts.Length - 1;
            }
            return null;
        }

        internal string DeleteLayout(string name)
        {
            var i = IndexOf(name);
            if (i < 0) return UnknownLayout(name);
            if (Layouts.Length == 1) return "The last layout cannot be deleted";
            var active = ActiveLayout;
            Layouts = Layouts.Where((_, j) => j != i).ToArray();
            ActiveLayoutIndex = Math.Max(0, Array.IndexOf(Layouts, active));
            return null;
        }

        internal string RenameLayout(string name, string to)
        {
            var i = IndexOf(name);
            if (i < 0) return UnknownLayout(name);
            to = (to ?? "").Trim();
            if (to.Length == 0) return "Give the new name as 'to'";
            if (to.Length > 40) return "Layout names are limited to 40 characters";
            var other = IndexOf(to);
            if (other >= 0 && other != i) return $"There is already a layout named '{Layouts[other].Name}'";
            Layouts[i].Name = to;
            return null;
        }

        string SetLayouts(object value)
        {
            if (!(value is List<object> list) || list.Count == 0) return "Invalid value for 'layouts': expected a non-empty list of layouts";
            var layouts = new List<Layout>();
            foreach (var item in list)
            {
                var layout = Layout.FromObject(item, out var error);
                if (layout == null) return $"Invalid value for 'layouts': {error}";
                if (layouts.Any(l => l.Name.Equals(layout.Name, StringComparison.OrdinalIgnoreCase)))
                    return $"Invalid value for 'layouts': two layouts are named '{layout.Name}'";
                layouts.Add(layout);
            }
            Layouts = layouts.ToArray();
            ActiveLayoutIndex = 0;
            return null;
        }

        /// <summary>Applies a pre-layouts flat value (e.g. xPercent) to every presenter view layout.</summary>
        internal void ApplyToPresenterLayouts(string key, object value)
        {
            foreach (var layout in Layouts.Where(l => !l.IsScreen))
            {
                try { layout.Apply(key, value); }
                catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is ArgumentException) { }
            }
        }

        internal static bool IsLegacyKey(string key) => LegacyKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

        internal static bool IsResetOnUpgrade(string key, int fileVersion) =>
            fileVersion < 2 && ResetBeforeV2.Contains(key, StringComparer.OrdinalIgnoreCase);

        internal static double Range(double v, double min, double max)
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
                var hasLayouts = values.Keys.Any(k => k.Equals("layouts", StringComparison.OrdinalIgnoreCase));
                var next = new Settings();
                foreach (var kv in InApplyOrder(values))
                {
                    if (Settings.IsLegacyKey(kv.Key) || kv.Key.Equals("configVersion", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Settings.IsResetOnUpgrade(kv.Key, fileVersion)) continue;
                    if (!hasLayouts && Layout.IsKey(kv.Key))
                    {
                        // Written before layouts existed: the position was for the presenter view.
                        next.ApplyToPresenterLayouts(kv.Key, kv.Value);
                        continue;
                    }
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
                foreach (var kv in InApplyOrder(changes))
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

        /// <summary>
        /// Runs <paramref name="change"/> on a copy of the settings; if it returns null (no error), the copy becomes
        /// current and is saved. Returns the error message, or null on success.
        /// </summary>
        public string Mutate(Func<Settings, string> change)
        {
            lock (gate)
            {
                var next = current.Clone();
                var error = change(next);
                if (error != null) return error;
                Volatile.Write(ref current, next);
                Save(next);
            }
            Changed?.Invoke();
            return null;
        }

        /// <summary>"layouts" first, then "layout" (which one is active), then the values that go into it.</summary>
        static IEnumerable<KeyValuePair<string, T>> InApplyOrder<T>(IEnumerable<KeyValuePair<string, T>> values) =>
            values.OrderBy(kv => kv.Key.Equals("layouts", StringComparison.OrdinalIgnoreCase) ? 0
                : kv.Key.Equals("layout", StringComparison.OrdinalIgnoreCase) ? 1 : 2);

        /// <summary>Flips an on/off runtime setting in one step. Returns an error message, or null on success.</summary>
        public string Toggle(string key)
        {
            lock (gate)
            {
                var name = current.ToggleKeys.FirstOrDefault(k => k.Equals((key ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                if (name == null)
                    return $"'{key}' is not an on/off setting (on/off settings: {string.Join(", ", current.ToggleKeys)})";
                var next = current.Clone();
                next.Apply(name, !(bool)current.ToDictionary(includeSecrets: false)[name]);
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
                File.WriteAllText(path, Json.Serialize(settings.ToFileDictionary(), indent: true) + "\n");
            }
            catch (Exception ex)
            {
                Log.Error($"Could not save {path}", ex);
            }
        }
    }
}
