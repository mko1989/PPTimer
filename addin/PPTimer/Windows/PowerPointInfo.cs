using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using PPTimer.Core;
using static PPTimer.Windows.NativeMethods;

namespace PPTimer.Windows
{
    /// <summary>One running slide show, as PowerPoint's object model reports it.</summary>
    internal sealed class SlideShowInfo
    {
        /// <summary>The audience window (SlideShowWindow.HWND). Never the presenter view.</summary>
        public IntPtr Hwnd;
        public string Presentation;
        /// <summary>"Use Presenter View" for this presentation (null if PowerPoint would not say).</summary>
        public bool? ShowPresenterView;
        public int? Slide;

        public override string ToString() =>
            $"audience hwnd 0x{Hwnd.ToInt64():X}, '{Presentation}', usePresenterView={(ShowPresenterView?.ToString() ?? "?")}, slide {(Slide?.ToString() ?? "?")}";
    }

    /// <summary>
    /// Reads PowerPoint's running slide shows from outside PowerPoint, on a background thread so a busy or hung
    /// PowerPoint never freezes the overlay. Each read gets the Application object (Running Object Table, or through
    /// PowerPoint's window), reads late-bound (no interop assemblies) and releases everything straight away, so
    /// PPTimer never keeps PowerPoint running after the user closes it.
    /// </summary>
    internal sealed class PowerPointInfo : IDisposable
    {
        const int PollMs = 500;
        const uint MK_E_UNAVAILABLE = 0x800401E3;
        static Guid IID_IDispatch = new Guid("00020400-0000-0000-C000-000000000046");

        readonly Thread worker;
        readonly ManualResetEventSlim stop = new ManualResetEventSlim(false);
        volatile bool watching;
        volatile List<SlideShowInfo> shows = new List<SlideShowInfo>();
        string lastError;
        string lastSource;

        public PowerPointInfo()
        {
            worker = new Thread(Run) { IsBackground = true, Name = "PPTimer PowerPoint reader" };
            worker.Start();
        }

        /// <summary>"16.0 build 18227" once PowerPoint has been read, else null.</summary>
        public string Version { get; private set; }

        /// <summary>Whether the last read worked (false: cannot tell the audience window apart).</summary>
        public bool Available { get; private set; }

        /// <summary>
        /// Set by the overlay while PowerPoint shows slide show or presenter windows; the object model is only read
        /// then. Otherwise there is no slide show, and PowerPoint is left alone.
        /// </summary>
        public void Watch(bool value)
        {
            if (value == watching) return;
            watching = value;
            if (!value) shows = new List<SlideShowInfo>();
        }

        /// <summary>Running slide shows from the last read, or null when the object model could not be read (unknown, not "none").</summary>
        public List<SlideShowInfo> SlideShows() => shows;

        void Run()
        {
            while (!stop.Wait(PollMs))
            {
                if (!watching) continue;
                var result = Read();
                if (watching) shows = result;
            }
        }

        List<SlideShowInfo> Read()
        {
            object app = null;
            try
            {
                app = Application();
                if (app == null)
                {
                    Available = false;
                    return null;
                }
                if (Version == null)
                {
                    Version = $"{Get(app, "Version")} build {Get(app, "Build")}";
                    Log.Info($"Reading PowerPoint {Version}'s slide shows ({lastSource})");
                }
                var list = SlideShows(app);
                Available = true;
                lastError = null;
                return list;
            }
            catch (Exception ex)
            {
                Fail("SlideShowWindows", ex);
                Available = false;
                return null;
            }
            finally
            {
                Release(app, final: true);
            }
        }

        /// <summary>
        /// PowerPoint registers in the Running Object Table only after it has lost focus once, so a slide show started
        /// straight away may not be there yet; its document window hands out the object model too.
        /// </summary>
        object Application()
        {
            try
            {
                var app = Marshal.GetActiveObject("PowerPoint.Application");
                Source("Running Object Table");
                return app;
            }
            catch (COMException ex) when ((uint)ex.HResult == MK_E_UNAVAILABLE)
            {
                // Not registered (yet); try the window.
            }

            var pane = FindDocumentPane();
            if (pane == IntPtr.Zero)
            {
                Fail("Application", new Exception("PowerPoint is not in the Running Object Table and has no document window"));
                return null;
            }
            object window = null;
            try
            {
                var iid = IID_IDispatch;
                var hr = AccessibleObjectFromWindow(pane, OBJID_NATIVEOM, ref iid, out window);
                if (hr != 0 || window == null) throw new COMException($"AccessibleObjectFromWindow failed (0x{hr:X8})", hr);
                var app = Get(window, "Application");
                Source("document window");
                return app;
            }
            catch (Exception ex)
            {
                Fail("Application (through the document window)", ex);
                return null;
            }
            finally
            {
                Release(window, final: false);
            }
        }

        void Source(string source)
        {
            if (source == lastSource) return;
            if (lastSource != null) Log.Info($"Reading PowerPoint's slide shows through its {source} now");
            lastSource = source;
        }

        /// <summary>PowerPoint's document pane ("mdiClass" inside the main PPTFrameClass window).</summary>
        static IntPtr FindDocumentPane()
        {
            var pids = PresenterWindowFinder.PowerPointProcessIds();
            var pane = IntPtr.Zero;
            var sb = new StringBuilder(64);
            EnumWindows((top, _) =>
            {
                GetWindowThreadProcessId(top, out var pid);
                if (!pids.Contains(pid)) return true;
                sb.Clear();
                GetClassName(top, sb, sb.Capacity);
                if (sb.ToString() != PresenterWindowFinder.MainWindowClass) return true;
                EnumChildWindows(top, (child, __) =>
                {
                    sb.Clear();
                    GetClassName(child, sb, sb.Capacity);
                    if (sb.ToString() != "mdiClass") return true;
                    pane = child;
                    return false;
                }, IntPtr.Zero);
                return pane == IntPtr.Zero;
            }, IntPtr.Zero);
            return pane;
        }

        static List<SlideShowInfo> SlideShows(object app)
        {
            object windows = null;
            try
            {
                windows = Get(app, "SlideShowWindows");
                var count = Convert.ToInt32(Get(windows, "Count"));
                var list = new List<SlideShowInfo>(count);
                for (var i = 1; i <= count; i++)
                    list.Add(ReadSlideShow(windows, i));
                return list;
            }
            finally
            {
                Release(windows, final: false);
            }
        }

        static SlideShowInfo ReadSlideShow(object windows, int index)
        {
            object window = null, presentation = null, showSettings = null, view = null;
            try
            {
                window = Call(windows, "Item", index);
                var info = new SlideShowInfo { Hwnd = new IntPtr(Convert.ToInt64(Get(window, "HWND"))) };
                try
                {
                    presentation = Get(window, "Presentation");
                    info.Presentation = Convert.ToString(Get(presentation, "Name"));
                    showSettings = Get(presentation, "SlideShowSettings");
                    info.ShowPresenterView = Convert.ToInt32(Get(showSettings, "ShowPresenterView")) != 0;
                }
                catch { /* older PowerPoint or protected view: the HWND is what matters */ }
                try
                {
                    view = Get(window, "View");
                    info.Slide = Convert.ToInt32(Get(view, "CurrentShowPosition"));
                }
                catch { }
                return info;
            }
            finally
            {
                Release(view, false);
                Release(showSettings, false);
                Release(presentation, false);
                Release(window, false);
            }
        }

        void Fail(string what, Exception ex)
        {
            var message = $"{what}: {(ex is TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : ex.Message)}";
            if (message == lastError) return;
            lastError = message;
            Log.Warn($"PowerPoint object model read failed ({message}). Detection continues by window class and title only.");
        }

        static object Get(object target, string name) =>
            target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

        static object Call(object target, string name, params object[] args) =>
            target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

        static void Release(object com, bool final)
        {
            if (com == null || !Marshal.IsComObject(com)) return;
            if (final) Marshal.FinalReleaseComObject(com);
            else Marshal.ReleaseComObject(com);
        }

        public void Dispose()
        {
            stop.Set();
            worker.Join(1000);
        }
    }
}
