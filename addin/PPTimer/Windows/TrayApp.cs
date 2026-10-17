using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;
using Microsoft.Win32;
using PPTimer.Core;
using static PPTimer.Windows.NativeMethods;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace PPTimer.Windows
{
    /// <summary>
    /// Wires the timer, the API server and the overlay together, and owns the notification area icon and its menu
    /// (the Windows counterpart of the Mac menu bar item).
    /// </summary>
    internal sealed class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly SettingsStore settings;
        readonly TimerModel timer;
        readonly PowerPointInfo powerPoint;
        readonly OverlayController overlay;
        readonly ApiServer api;
        readonly NotifyIcon tray;
        readonly WinFormsTimer refresh;
        string iconKey;
        IntPtr iconHandle;

        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PPTimer");

        public TrayApp()
        {
            Directory.CreateDirectory(DataDirectory);
            Log.Init(DataDirectory);
            Log.Info($"PPTimer {ApiServer.Version} starting ({(Environment.Is64BitProcess ? "64" : "32")}-bit, CLR {Environment.Version}) on {WindowsVersion()}");

            settings = new SettingsStore(Path.Combine(DataDirectory, "config.json"));
            settings.Load();
            timer = new TimerModel(settings);

            SoundAlert.Init(DataDirectory);
            timer.ZeroReached += () =>
            {
                if (settings.Current.SoundEnabled) SoundAlert.Play(settings.Current.SoundFile);
            };
            timer.SoundTestRequested += () => SoundAlert.Play(settings.Current.SoundFile);

            powerPoint = new PowerPointInfo();
            overlay = new OverlayController(timer, settings, powerPoint);
            api = new ApiServer(timer, settings, overlay.Diagnostics, "Windows");
            api.Start();

            tray = new NotifyIcon { ContextMenuStrip = new ContextMenuStrip(), Visible = true };
            tray.ContextMenuStrip.Opening += (_, e) =>
            {
                BuildMenu(tray.ContextMenuStrip);
                e.Cancel = false;
            };
            tray.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowMenu();
            };
            tray.DoubleClick += (_, __) => OpenPage("");
            UpdateIcon();

            refresh = new WinFormsTimer { Interval = 250 };
            refresh.Tick += (_, __) => UpdateIcon();
            refresh.Start();
        }

        // ---- Icon -------------------------------------------------------------------------------

        void UpdateIcon()
        {
            var snap = timer.Snapshot();
            var color = snap.Phase == "warning" ? Color.FromArgb(255, 176, 0)
                : snap.Phase == "critical" || snap.Phase == "expired" ? Color.FromArgb(235, 45, 45)
                : snap.Running ? Color.FromArgb(60, 190, 90) : Color.FromArgb(150, 150, 150);
            var key = $"{color.ToArgb()}|{snap.PresenterView}";
            if (key != iconKey)
            {
                iconKey = key;
                SetIcon(color, snap.PresenterView);
            }
            // Max 63 characters.
            var text = $"PPTimer {snap.Display} · {(snap.Running ? "running" : "paused")}\n" +
                       (snap.PresenterView ? $"On {snap.Target}" : $"{snap.Layout}: target not found");
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        /// <summary>A ring in the phase colour; filled while the timer is placed on something.</summary>
        void SetIcon(Color color, bool placed)
        {
            var size = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    var stroke = Math.Max(2f, size / 8f);
                    var r = new RectangleF(stroke / 2 + 0.5f, stroke / 2 + 0.5f, size - stroke - 1, size - stroke - 1);
                    using (var pen = new Pen(color, stroke))
                        g.DrawEllipse(pen, r);
                    if (placed)
                    {
                        var inset = size * 0.3f;
                        using (var brush = new SolidBrush(color))
                            g.FillEllipse(brush, inset, inset, size - 2 * inset, size - 2 * inset);
                    }
                }
                var handle = bmp.GetHicon();
                var old = tray.Icon;
                tray.Icon = Icon.FromHandle(handle);
                old?.Dispose();
                if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                iconHandle = handle;
            }
        }

        // ---- Menu -------------------------------------------------------------------------------

        void ShowMenu()
        {
            // NotifyIcon only opens the menu on a right click; this is how WinForms opens it on a left click too.
            typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(tray, null);
        }

        void BuildMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();
            var snap = timer.Snapshot();
            var cfg = settings.Current;

            menu.Items.Add(Info($"PPTimer {ApiServer.Version}  ·  {snap.Display}"));
            menu.Items.Add(Info(snap.PresenterView ? $"On {snap.Target}" : $"Layout {cfg.ActiveLayout.Name}: {Missing(cfg.ActiveLayout)}"));
            if (api.ListeningOn == null)
                menu.Items.Add(Info($"⚠ Not listening on port {cfg.Port} (in use?)"));
            else
            {
                foreach (var address in LanAddresses()) menu.Items.Add(Info($"Remote: http://{address}:{cfg.Port}/"));
                if (!api.LanAccess) menu.Items.Add(Info("⚠ Only this PC can connect: run install.cmd again"));
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item(snap.Running ? "Pause" : "Start", () => Run("toggle")));
            menu.Items.Add(Item($"Reset to {TimerModel.Format((int)(snap.DurationMs / 1000), true)}", () => Run("reset")));
            menu.Items.Add(Item("Add 1 Minute", () => Run("add", ("seconds", "60"))));
            menu.Items.Add(Item("Remove 1 Minute", () => Run("add", ("seconds", "-60"))));

            // Operator-only: the presenter's overlay never shows the speed.
            menu.Items.Add(Info(snap.SpeedPercent == 100 ? "Speed: normal" : $"Speed: {snap.SpeedPercent:0.#}% of real time"));
            menu.Items.Add(Item("Run Faster (+5%)", () => Run("speed", ("step", "5"))));
            menu.Items.Add(Item("Run Slower (−5%)", () => Run("speed", ("step", "-5"))));
            if (snap.SpeedPercent != 100) menu.Items.Add(Item("Back to Normal Speed", () => Run("speed", ("percent", "100"))));
            var visible = Item("Show Timer", () => Run("togglevisible"));
            visible.Checked = snap.Visible;
            menu.Items.Add(visible);

            menu.Items.Add(new ToolStripSeparator());
            var layouts = new ToolStripMenuItem("Layout");
            foreach (var layout in cfg.Layouts)
            {
                var name = layout.Name;
                var choice = Item($"{name}  ({TargetLabel(layout)})", () => Run("layout", ("name", name)));
                choice.Checked = layout == cfg.ActiveLayout;
                layouts.DropDownItems.Add(choice);
            }
            layouts.DropDownItems.Add(new ToolStripSeparator());
            layouts.DropDownItems.Add(Item("Edit Layouts in Browser…", () => OpenPage("")));
            menu.Items.Add(layouts);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Open Remote in Browser", () => OpenPage("")));
            menu.Items.Add(Item("Open Display Page", () => OpenPage("display")));
            menu.Items.Add(Item("Open Settings Folder", () => Process.Start("explorer.exe", $"/select,\"{Path.Combine(DataDirectory, "config.json")}\"")));
            var login = Item("Start with Windows", ToggleStartWithWindows);
            login.Checked = StartsWithWindows();
            menu.Items.Add(login);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Quit PPTimer", ExitThread));
        }

        static string TargetLabel(Layout layout)
        {
            switch (layout.Target)
            {
                case "screen":
                    var screen = layout.Screen == "main" ? "main screen" : layout.Screen == "external" ? "external screen" : $"screen {layout.Screen}";
                    return layout.Blackout ? $"{screen}, fullscreen" : screen;
                case "keynote": return "Keynote, Mac only";
                default: return "PowerPoint presenter view";
            }
        }

        static string Missing(Layout layout) =>
            layout.IsScreen ? "screen not connected" : layout.Target == "keynote" ? "Keynote is Mac only" : "no presenter view (start a slide show)";

        static ToolStripMenuItem Info(string text) => new ToolStripMenuItem(text) { Enabled = false };

        static ToolStripMenuItem Item(string text, Action action)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (_, __) =>
            {
                try { action(); }
                catch (Exception ex) { Log.Error($"Menu '{text}' failed", ex); }
            };
            return item;
        }

        void Run(string cmd, params (string Key, string Value)[] args)
        {
            var error = Commands.Execute(timer, settings, cmd, args.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));
            if (error != null) Log.Warn($"Menu {cmd}: {error}");
            api.NotifyChanged();
            UpdateIcon();
        }

        void OpenPage(string path)
        {
            var cfg = settings.Current;
            var url = $"http://localhost:{cfg.Port}/{path}";
            if (cfg.ApiToken.Length > 0) url += "?token=" + Uri.EscapeDataString(cfg.ApiToken);
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error($"Could not open {url}", ex); }
        }

        static bool StartsWithWindows()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key?.GetValue("PPTimer") != null;
        }

        static void ToggleStartWithWindows()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key.GetValue("PPTimer") != null) key.DeleteValue("PPTimer", false);
                else key.SetValue("PPTimer", $"\"{Application.ExecutablePath}\"");
            }
        }

        /// <summary>IPv4 addresses of the network interfaces that are up (for the remote URL in the menu).</summary>
        static List<string> LanAddresses()
        {
            var result = new List<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    {
                        var ip = a.Address.ToString();
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && !ip.StartsWith("169.254.", StringComparison.Ordinal)) result.Add(ip);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not list network addresses: {ex.Message}");
            }
            return result.Count == 0 ? new List<string> { "localhost" } : result;
        }

        /// <summary>"Windows 11 Pro 24H2 (build 26100.4061)". Environment.OSVersion is unreliable without an app manifest.</summary>
        static string WindowsVersion()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    var build = Convert.ToString(key?.GetValue("CurrentBuildNumber"));
                    var name = Convert.ToString(key?.GetValue("ProductName"));
                    // ProductName still says "Windows 10" on Windows 11.
                    if (int.TryParse(build, out var b) && b >= 22000) name = name?.Replace("Windows 10", "Windows 11");
                    return $"{name} {key?.GetValue("DisplayVersion")} (build {build}.{key?.GetValue("UBR")})";
                }
            }
            catch
            {
                return Environment.OSVersion.ToString();
            }
        }

        protected override void ExitThreadCore()
        {
            refresh.Stop();
            tray.Visible = false;
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refresh.Dispose();
                tray.Dispose();
                if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                api.Dispose();
                overlay.Dispose();
                powerPoint.Dispose();
                Log.Info("PPTimer stopped");
            }
            base.Dispose(disposing);
        }
    }
}
