using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PPTimer.Core
{
    /// <summary>
    /// A named layout preset: where the timer goes (a presenter view, or a screen), the timer's rectangle in % of
    /// that area, and how it looks. Exactly one layout is active; the flat settings (xPercent, opacity, ...) read
    /// and write the active one. Mutable, but only ever changed on a clone inside <see cref="SettingsStore"/>.
    /// </summary>
    public sealed class Layout
    {
        /// <summary>presenter = PowerPoint or Keynote, whichever is showing one; screen = a whole display.</summary>
        public static readonly string[] Targets = { "presenter", "powerpoint", "keynote", "screen" };

        /// <summary>Settings that belong to a layout (also readable / writable as flat settings).</summary>
        public static readonly string[] Keys =
        {
            "target", "screen", "xPercent", "yPercent", "widthPercent", "heightPercent",
            "opacity", "transparentBackground", "textOutline", "blackout",
        };

        public string Name = "Layout";
        public string Target = "powerpoint";
        /// <summary>For target "screen": "main", "external", a number (1 = main, then left to right) or part of the screen's name.</summary>
        public string Screen = "main";
        public double XPercent = 22;
        public double YPercent = 74;
        public double WidthPercent = 16;
        public double HeightPercent = 9;
        public double Opacity = 1;
        public bool TransparentBackground = true;
        public bool TextOutline = true;
        /// <summary>Paints the whole target area black behind the timer (fullscreen display).</summary>
        public bool Blackout;

        public bool IsScreen => Target == "screen";

        public Layout Clone() => (Layout)MemberwiseClone();

        public static bool IsKey(string key) => Keys.Contains(key, StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, object> ToDictionary(bool includeName = true)
        {
            var d = new Dictionary<string, object>();
            if (includeName) d["name"] = Name;
            d["target"] = Target;
            d["screen"] = Screen;
            d["xPercent"] = XPercent;
            d["yPercent"] = YPercent;
            d["widthPercent"] = WidthPercent;
            d["heightPercent"] = HeightPercent;
            d["opacity"] = Opacity;
            d["transparentBackground"] = TransparentBackground;
            d["textOutline"] = TextOutline;
            d["blackout"] = Blackout;
            return d;
        }

        /// <summary>Applies one layout key. Returns an error message, or null on success. Throws on unparsable values.</summary>
        internal string Apply(string key, object value)
        {
            switch (key.ToLowerInvariant())
            {
                case "target":
                    var target = Text(value).ToLowerInvariant();
                    if (!Targets.Contains(target)) return $"Invalid value for 'target': use {string.Join(", ", Targets)}";
                    Target = target;
                    return null;
                case "screen":
                    var screen = Text(value);
                    if (screen.Length == 0) return "Invalid value for 'screen': use main, external, a screen number or part of its name";
                    Screen = screen.Equals("main", StringComparison.OrdinalIgnoreCase) || screen.Equals("external", StringComparison.OrdinalIgnoreCase)
                        ? screen.ToLowerInvariant() : screen;
                    return null;
                case "xpercent": XPercent = Settings.Range(Settings.ToDouble(value), 0, 100); return null;
                case "ypercent": YPercent = Settings.Range(Settings.ToDouble(value), 0, 100); return null;
                case "widthpercent": WidthPercent = Settings.Range(Settings.ToDouble(value), 2, 100); return null;
                case "heightpercent": HeightPercent = Settings.Range(Settings.ToDouble(value), 2, 100); return null;
                case "opacity": Opacity = Settings.Range(Settings.ToDouble(value), 0.2, 1); return null;
                case "transparentbackground": TransparentBackground = Settings.ToBool(value); return null;
                case "textoutline": TextOutline = Settings.ToBool(value); return null;
                case "blackout": Blackout = Settings.ToBool(value); return null;
                default: return $"Unknown layout setting '{key}'";
            }
        }

        /// <summary>One entry of config.json's "layouts" list.</summary>
        internal static Layout FromObject(object value, out string error)
        {
            error = null;
            if (!(value is Dictionary<string, object> d))
            {
                error = "each layout must be an object like {\"name\": \"PowerPoint\", \"target\": \"powerpoint\", ...}";
                return null;
            }
            var layout = new Layout();
            if (!d.TryGetValue("name", out var name) || Text(name).Length == 0)
            {
                error = "a layout has no name";
                return null;
            }
            layout.Name = Text(name);
            foreach (var kv in d)
            {
                if (kv.Key.Equals("name", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var e = layout.Apply(kv.Key, kv.Value);
                    if (e != null) Log.Warn($"config.json layout '{layout.Name}': {e} (using default)");
                }
                catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is ArgumentException)
                {
                    Log.Warn($"config.json layout '{layout.Name}': invalid value for '{kv.Key}': {ex.Message} (using default)");
                }
            }
            return layout;
        }

        static string Text(object v) => (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Trim();

        /// <summary>The presets a new install starts with (Windows: no Keynote).</summary>
        public static Layout[] Defaults() => new[]
        {
            new Layout { Name = "PowerPoint", Target = "powerpoint" },
            new Layout { Name = "Screen", Target = "screen", Screen = "main", XPercent = 80, YPercent = 2, WidthPercent = 18, HeightPercent = 10, TransparentBackground = false },
            new Layout { Name = "Fullscreen", Target = "screen", Screen = "external", XPercent = 5, YPercent = 15, WidthPercent = 90, HeightPercent = 70, TextOutline = false, Blackout = true },
        };
    }
}
