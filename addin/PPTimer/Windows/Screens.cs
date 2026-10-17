using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using PPTimer.Core;
using static PPTimer.Windows.NativeMethods;
using Screen = System.Windows.Forms.Screen;

namespace PPTimer.Windows
{
    /// <summary>One display, numbered the way a layout's "screen" setting refers to it.</summary>
    internal sealed class ScreenEntry
    {
        public ScreenDescription Description;
        public Rectangle Bounds;
        public string DeviceName;
    }

    /// <summary>
    /// The displays, numbered like on the Mac: 1 = the main (primary) display, then the others left to right.
    /// "external" = the first one that isn't a laptop's built-in panel (and isn't the main display, if that is external too).
    /// </summary>
    internal static class Screens
    {
        static bool loggedNamesFailure;

        public static List<ScreenEntry> List()
        {
            var info = MonitorInfo();
            var ordered = Screen.AllScreens
                .OrderBy(s => s.Primary ? 0 : 1).ThenBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
                .ToList();
            return ordered.Select((s, i) =>
            {
                info.TryGetValue(s.DeviceName, out var m);
                var name = string.IsNullOrWhiteSpace(m.Name) ? s.DeviceName.Replace(@"\\.\", "") : m.Name;
                return new ScreenEntry
                {
                    Bounds = s.Bounds,
                    DeviceName = s.DeviceName,
                    Description = new ScreenDescription
                    {
                        Id = (i + 1).ToString(),
                        Name = name,
                        Width = s.Bounds.Width,
                        Height = s.Bounds.Height,
                        Main = s.Primary,
                        // Unknown technology (lookup failed): only the non-primary displays count as external.
                        External = m.Name != null ? !m.Internal : !s.Primary,
                    },
                };
            }).ToList();
        }

        /// <summary>The screen a layout's "screen" setting means, or null (e.g. "external" with only the laptop panel).</summary>
        public static ScreenEntry Resolve(IList<ScreenEntry> screens, string spec)
        {
            spec = (spec ?? "").Trim();
            if (spec.Equals("main", StringComparison.OrdinalIgnoreCase))
                return screens.FirstOrDefault(s => s.Description.Main) ?? screens.FirstOrDefault();
            if (spec.Equals("external", StringComparison.OrdinalIgnoreCase))
                return screens.FirstOrDefault(s => s.Description.External && !s.Description.Main)
                       ?? screens.FirstOrDefault(s => !s.Description.Main);
            if (int.TryParse(spec, out var n))
                return n >= 1 && n <= screens.Count ? screens[n - 1] : null;
            return screens.FirstOrDefault(s => s.Description.Name.IndexOf(spec, StringComparison.OrdinalIgnoreCase) >= 0
                                               || s.DeviceName.EndsWith(spec, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Monitor name and built-in flag per GDI device name (\\.\DISPLAY1), from QueryDisplayConfig.</summary>
        static Dictionary<string, (string Name, bool Internal)> MonitorInfo()
        {
            var result = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var numPaths, out var numModes) != 0) return result;
                var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
                var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0) return result;

                for (var i = 0; i < numPaths; i++)
                {
                    var path = paths[i];
                    var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    source.header.size = Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
                    source.header.adapterId = path.sourceInfo.adapterId;
                    source.header.id = path.sourceInfo.id;
                    if (DisplayConfigGetDeviceInfo(ref source) != 0 || string.IsNullOrEmpty(source.viewGdiDeviceName)) continue;

                    var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                    target.header.size = Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
                    target.header.adapterId = path.targetInfo.adapterId;
                    target.header.id = path.targetInfo.id;
                    if (DisplayConfigGetDeviceInfo(ref target) != 0) continue;

                    var tech = target.outputTechnology;
                    var builtIn = tech == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
                                  || tech == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
                                  || tech == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED;
                    var name = (target.monitorFriendlyDeviceName ?? "").Trim();
                    if (name.Length == 0) name = builtIn ? "Built-in display" : "Display";
                    // Cloned (duplicated) displays share a source: keep the first, it is what Windows shows.
                    if (!result.ContainsKey(source.viewGdiDeviceName)) result[source.viewGdiDeviceName] = (name, builtIn);
                }
            }
            catch (Exception ex)
            {
                if (!loggedNamesFailure)
                {
                    loggedNamesFailure = true;
                    Log.Warn($"Could not read monitor names ({ex.Message}); screens are named DISPLAY1, DISPLAY2, ...");
                }
            }
            return result;
        }

        /// <summary>"1 DELL U2720Q 2560x1440 at 0,0 main; 2 ..." for the log.</summary>
        public static string Summary(IEnumerable<ScreenEntry> screens) =>
            string.Join("; ", screens.Select(s =>
                $"{s.Description.Id} {s.Description.Name} ({s.DeviceName.Replace(@"\\.\", "")}) {s.Bounds.Width}x{s.Bounds.Height} at {s.Bounds.X},{s.Bounds.Y}" +
                (s.Description.Main ? " main" : "") + (s.Description.External ? "" : " built-in")));
    }
}
