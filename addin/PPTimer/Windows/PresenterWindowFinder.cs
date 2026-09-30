using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using static PPTimer.Windows.NativeMethods;

namespace PPTimer.Windows
{
    /// <summary>Finds PowerPoint's presenter view among the process's top-level windows.</summary>
    internal static class PresenterWindowFinder
    {
        static readonly uint ProcessId = (uint)Process.GetCurrentProcess().Id;

        /// <summary>First visible top-level window of this process whose class is in <paramref name="classNames"/>.</summary>
        public static IntPtr Find(string[] classNames)
        {
            var found = IntPtr.Zero;
            var sb = new StringBuilder(256);
            EnumWindows((hWnd, _) =>
            {
                if (!BelongsToUs(hWnd) || !IsWindowVisible(hWnd)) return true;
                sb.Clear();
                GetClassName(hWnd, sb, sb.Capacity);
                var name = sb.ToString();
                if (!classNames.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase))) return true;
                found = hWnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Sorted, de-duplicated class names of visible top-level windows (for the log).</summary>
        public static string VisibleClassSummary()
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            var sb = new StringBuilder(256);
            EnumWindows((hWnd, _) =>
            {
                if (BelongsToUs(hWnd) && IsWindowVisible(hWnd))
                {
                    sb.Clear();
                    GetClassName(hWnd, sb, sb.Capacity);
                    names.Add(sb.ToString());
                }
                return true;
            }, IntPtr.Zero);
            return string.Join(", ", names);
        }

        /// <summary>All top-level windows of this process, for GET /api/debug/windows.</summary>
        public static object DescribeProcessWindows()
        {
            var list = new List<object>();
            var cls = new StringBuilder(256);
            var title = new StringBuilder(256);
            EnumWindows((hWnd, _) =>
            {
                if (!BelongsToUs(hWnd)) return true;
                cls.Clear();
                title.Clear();
                GetClassName(hWnd, cls, cls.Capacity);
                GetWindowText(hWnd, title, title.Capacity);
                GetWindowRect(hWnd, out var r);
                list.Add(new Dictionary<string, object>
                {
                    ["hwnd"] = "0x" + hWnd.ToInt64().ToString("X"),
                    ["class"] = cls.ToString(),
                    ["title"] = title.ToString(),
                    ["visible"] = IsWindowVisible(hWnd),
                    ["rect"] = new[] { r.Left, r.Top, r.Right, r.Bottom },
                });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        static bool BelongsToUs(IntPtr hWnd)
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            return pid == ProcessId;
        }
    }
}
