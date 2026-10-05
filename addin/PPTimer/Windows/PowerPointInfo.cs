using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using PPTimer.Core;

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
    /// Late-bound (IDispatch) reads of PowerPoint's object model, so the add-in needs no interop assemblies.
    /// Call on PowerPoint's UI thread only. Never throws: failures return null and are logged once.
    /// </summary>
    internal sealed class PowerPointInfo : IDisposable
    {
        object app;
        string lastError;

        public PowerPointInfo(object application)
        {
            app = application;
        }

        public bool Available => app != null;

        /// <summary>"16.0 build 18227" or null.</summary>
        public string Version()
        {
            if (app == null) return null;
            try
            {
                var version = Get(app, "Version");
                var build = Get(app, "Build");
                return $"{version} build {build}";
            }
            catch (Exception ex)
            {
                Fail("Application.Version", ex);
                return null;
            }
        }

        /// <summary>Running slide shows, or null when the object model could not be read (unknown, not "none").</summary>
        public List<SlideShowInfo> SlideShows()
        {
            if (app == null) return null;
            object windows = null;
            try
            {
                windows = Get(app, "SlideShowWindows");
                var count = Convert.ToInt32(Get(windows, "Count"));
                var list = new List<SlideShowInfo>(count);
                for (var i = 1; i <= count; i++)
                    list.Add(ReadSlideShow(windows, i));
                lastError = null;
                return list;
            }
            catch (Exception ex)
            {
                Fail("SlideShowWindows", ex);
                return null;
            }
            finally
            {
                Release(windows);
            }
        }

        SlideShowInfo ReadSlideShow(object windows, int index)
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
                Release(view);
                Release(showSettings);
                Release(presentation);
                Release(window);
            }
        }

        void Fail(string what, Exception ex)
        {
            var message = $"{what}: {(ex is TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : ex.Message)}";
            if (message == lastError) return;
            lastError = message;
            Log.Warn($"PowerPoint object model read failed ({message})");
        }

        static object Get(object target, string name) =>
            target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

        static object Call(object target, string name, params object[] args) =>
            target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

        static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }

        public void Dispose()
        {
            // The Application reference is PowerPoint's own; just drop it.
            app = null;
        }
    }
}
