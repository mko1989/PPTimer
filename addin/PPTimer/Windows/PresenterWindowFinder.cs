using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using PPTimer.Core;
using static PPTimer.Windows.NativeMethods;

namespace PPTimer.Windows
{
    /// <summary>A window of this process, read once (for detection, the log and /api/debug/windows).</summary>
    internal sealed class WindowInfo
    {
        public IntPtr Hwnd;
        public string Class;
        public string Title;
        public bool Visible;
        public bool Cloaked;
        public bool Minimized;
        public RECT Rect;
        public IntPtr Owner;
        public string Monitor;
        public int Style;
        public int ExStyle;

        public int Width => Rect.Right - Rect.Left;
        public int Height => Rect.Bottom - Rect.Top;
        public string Handle => Hex(Hwnd);

        public static string Hex(IntPtr h) => "0x" + h.ToInt64().ToString("X");

        public static WindowInfo Read(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            var info = new WindowInfo { Hwnd = hWnd };
            GetClassName(hWnd, sb, sb.Capacity);
            info.Class = sb.ToString();
            sb.Clear();
            GetWindowText(hWnd, sb, sb.Capacity);
            info.Title = sb.ToString();
            info.Visible = IsWindowVisible(hWnd);
            info.Minimized = IsIconic(hWnd);
            GetWindowRect(hWnd, out info.Rect);
            info.Owner = GetWindow(hWnd, GW_OWNER);
            info.Style = GetWindowLong(hWnd, GWL_STYLE);
            info.ExStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            try
            {
                info.Cloaked = DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
                info.Monitor = System.Windows.Forms.Screen.FromHandle(hWnd).DeviceName.Replace(@"\\.\", "");
            }
            catch { }
            return info;
        }

        /// <summary>One log line: everything needed to tell the presenter view from the audience window.</summary>
        public string Describe(ISet<IntPtr> audience)
        {
            var flags = new List<string>();
            if (!Visible) flags.Add("hidden");
            if (Cloaked) flags.Add("cloaked");
            if (Minimized) flags.Add("minimized");
            if (audience != null && audience.Contains(Hwnd)) flags.Add("AUDIENCE");
            if (Owner != IntPtr.Zero) flags.Add("owner " + Hex(Owner));
            return $"{Handle} {Class} \"{Title}\" [{Rect.Left},{Rect.Top} {Width}x{Height}] on {Monitor ?? "?"}" +
                   $" style 0x{Style:X8}/0x{ExStyle:X8}" + (flags.Count > 0 ? " " + string.Join(", ", flags) : "");
        }

        public Dictionary<string, object> ToDictionary(ISet<IntPtr> audience) => new Dictionary<string, object>
        {
            ["hwnd"] = Handle,
            ["class"] = Class,
            ["title"] = Title,
            ["visible"] = Visible,
            ["cloaked"] = Cloaked,
            ["minimized"] = Minimized,
            ["audienceSlideShow"] = audience != null && audience.Contains(Hwnd),
            ["owner"] = Owner == IntPtr.Zero ? null : Hex(Owner),
            ["monitor"] = Monitor,
            ["rect"] = new[] { Rect.Left, Rect.Top, Rect.Right, Rect.Bottom },
            ["style"] = "0x" + Style.ToString("X8"),
            ["exStyle"] = "0x" + ExStyle.ToString("X8"),
        };
    }

    /// <summary>Which window the overlay should sit on, and why.</summary>
    internal sealed class Detection
    {
        /// <summary>Top-level window that owns the overlay (keeps it above the presenter view, minimises with it).</summary>
        public IntPtr Owner;
        /// <summary>Window whose client area is the presenter view; the same as <see cref="Owner"/> unless it is a child window.</summary>
        public IntPtr Target;
        public string Rule;
        public WindowInfo Window;

        public override string ToString() =>
            $"{Window?.Describe(null)} via {Rule}" + (Target != Owner ? $", inside top-level {WindowInfo.Hex(Owner)}" : "");
    }

    /// <summary>
    /// Finds PowerPoint's presenter view among this process's windows. Rules, strongest first:
    /// 1. a visible top-level window whose class is in presenterWindowClasses (in the order listed),
    /// 2. a visible child window with such a class (newer PowerPoint builds may nest it),
    /// 3. a visible top-level window whose title contains one of presenterWindowTitles,
    /// 4. during a slide show, the one other visible "screenClass" window besides the audience window.
    /// The audience window (SlideShowWindow.HWND from the object model) is never used, whatever the config says.
    /// </summary>
    internal static class PresenterWindowFinder
    {
        public const string SlideShowClass = "screenClass";
        public const string PresenterClass = "PodiumParent";
        public const string MainWindowClass = "PPTFrameClass";
        const int MinWidth = 320, MinHeight = 200;
        const int MaxChildClasses = 30;

        static readonly uint ProcessId = (uint)Process.GetCurrentProcess().Id;

        /// <summary>Top-level windows of this process, in z-order, minus <paramref name="exclude"/> (our own overlay).</summary>
        public static List<WindowInfo> TopLevelWindows(bool visibleOnly, IntPtr exclude)
        {
            var handles = new List<IntPtr>();
            EnumWindows((hWnd, _) =>
            {
                if (hWnd != exclude && BelongsToUs(hWnd) && (!visibleOnly || IsWindowVisible(hWnd))) handles.Add(hWnd);
                return true;
            }, IntPtr.Zero);
            return handles.Select(WindowInfo.Read).ToList();
        }

        public static Detection Detect(Settings cfg, IList<WindowInfo> windows, ISet<IntPtr> audience, bool slideShowRunning)
        {
            audience = audience ?? new HashSet<IntPtr>();
            var usable = windows.Where(w => w.Visible && !w.Cloaked && !IsAudience(w, audience)).ToList();

            foreach (var cls in cfg.PresenterWindowClasses)
            {
                var w = usable.FirstOrDefault(x => SameClass(x.Class, cls));
                if (w != null) return Found(w, w.Hwnd, $"class {cls}");
            }

            foreach (var top in usable)
            {
                var child = VisibleDescendants(top.Hwnd, cfg.PresenterWindowClasses).FirstOrDefault(Big);
                if (child != null)
                    return new Detection { Owner = top.Hwnd, Target = child.Hwnd, Rule = $"child window of class {child.Class} in {top.Class}", Window = child };
            }

            foreach (var w in usable.Where(x => !SameClass(x.Class, MainWindowClass) && Big(x)))
            {
                var title = cfg.PresenterWindowTitles.FirstOrDefault(t => w.Title.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
                if (title != null) return Found(w, w.Hwnd, $"title contains '{title}'");
            }

            // Needs the object model to know which screenClass window is the audience one; without it, guessing could
            // put the timer on the projector.
            if (slideShowRunning && audience.Count > 0)
            {
                var others = usable.Where(x => SameClass(x.Class, SlideShowClass) && Big(x)).ToList();
                if (others.Count == 1) return Found(others[0], others[0].Hwnd, "second screenClass window (not the audience window)");
            }

            return null;
        }

        /// <summary>"PPTFrameClass, screenClass x2": which kinds of window are visible, to log only when that changes.</summary>
        public static string ClassSignature(IEnumerable<WindowInfo> windows) =>
            string.Join(", ", windows.Where(w => w.Visible)
                .GroupBy(w => w.Class, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));

        /// <summary>Distinct classes of a window's visible descendants ("+N more" when long), for the log.</summary>
        public static string ChildClassSummary(IntPtr hwnd)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sb = new StringBuilder(256);
            EnumChildWindows(hwnd, (child, _) =>
            {
                if (!IsWindowVisible(child)) return true;
                sb.Clear();
                GetClassName(child, sb, sb.Capacity);
                if (seen.Add(sb.ToString())) names.Add(sb.ToString());
                return true;
            }, IntPtr.Zero);
            if (names.Count == 0) return "none";
            return names.Count <= MaxChildClasses
                ? string.Join(", ", names)
                : string.Join(", ", names.Take(MaxChildClasses)) + $", +{names.Count - MaxChildClasses} more";
        }

        /// <summary>Multi-line dump of every visible window (with child classes), for the log.</summary>
        public static string DescribeAll(IList<WindowInfo> windows, ISet<IntPtr> audience)
        {
            var sb = new StringBuilder();
            foreach (var w in windows.Where(x => x.Visible))
            {
                sb.Append(Environment.NewLine).Append("    ").Append(w.Describe(audience));
                sb.Append(Environment.NewLine).Append("      children: ").Append(ChildClassSummary(w.Hwnd));
            }
            return sb.Length == 0 ? " (no visible windows)" : sb.ToString();
        }

        /// <summary>All top-level windows of this process (hidden too), for GET /api/debug/windows.</summary>
        public static List<object> DescribeProcessWindows(ISet<IntPtr> audience) =>
            TopLevelWindows(visibleOnly: false, IntPtr.Zero).Select(w =>
            {
                var d = w.ToDictionary(audience);
                if (w.Visible) d["childClasses"] = ChildClassSummary(w.Hwnd);
                return (object)d;
            }).ToList();

        /// <summary>Visible descendants whose class is one of <paramref name="classes"/> (class checked first: the main window has hundreds).</summary>
        static IEnumerable<WindowInfo> VisibleDescendants(IntPtr parent, string[] classes)
        {
            var handles = new List<IntPtr>();
            var sb = new StringBuilder(256);
            EnumChildWindows(parent, (child, _) =>
            {
                sb.Clear();
                GetClassName(child, sb, sb.Capacity);
                var name = sb.ToString();
                if (classes.Any(c => SameClass(c, name)) && IsWindowVisible(child)) handles.Add(child);
                return true;
            }, IntPtr.Zero);
            return handles.Select(WindowInfo.Read);
        }

        /// <summary>PodiumParent is the presenter view by definition, so the audience check never excludes it.</summary>
        static bool IsAudience(WindowInfo w, ISet<IntPtr> audience) =>
            audience.Contains(w.Hwnd) && !SameClass(w.Class, PresenterClass);

        static bool Big(WindowInfo w) => w.Width >= MinWidth && w.Height >= MinHeight;

        static bool SameClass(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static Detection Found(WindowInfo w, IntPtr target, string rule) =>
            new Detection { Owner = w.Hwnd, Target = target, Rule = rule, Window = w };

        static bool BelongsToUs(IntPtr hWnd)
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            return pid == ProcessId;
        }
    }
}
