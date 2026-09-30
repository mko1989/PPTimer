using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PPTimer.Core;
using static PPTimer.Windows.NativeMethods;

namespace PPTimer.Windows
{
    /// <summary>
    /// Borderless countdown window owned by the presenter view window: it always sits above
    /// that window (and only that window), never takes focus, and by default lets clicks through.
    /// Drawn with UpdateLayeredWindow (per-pixel alpha), so the background can be fully transparent.
    /// </summary>
    internal sealed class OverlayForm : Form
    {
        static readonly Color Amber = Color.FromArgb(255, 176, 0);
        static readonly Color Red = Color.FromArgb(235, 45, 45);
        static readonly Color SolidBack = Color.FromArgb(24, 24, 24);
        static readonly FontFamily Family = LoadFamily();

        readonly IntPtr ownerHwnd;
        readonly bool clickThrough;
        string renderKey;
        bool loggedFailure;

        public OverlayForm(IntPtr ownerHwnd, bool clickThrough)
        {
            this.ownerHwnd = ownerHwnd;
            this.clickThrough = clickThrough;

            Text = "PPTimer";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ControlBox = false;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
                if (clickThrough) cp.ExStyle |= WS_EX_TRANSPARENT;
                // For a top-level window the "parent" is the owner: keeps us above the presenter view,
                // hides us when it minimises, and destroys us with it.
                cp.Parent = ownerHwnd;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }

        // Layered windows updated with UpdateLayeredWindow don't use WM_PAINT.
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e) { }

        /// <summary>Redraws (only if something visible changed) and moves the window to <paramref name="bounds"/>.</summary>
        public void Render(TimerSnapshot snap, Settings cfg, double blinkLevel, Rectangle bounds)
        {
            var level = Math.Round(blinkLevel * 64) / 64; // quantised: skips redraws of invisible differences
            var key = $"{snap.Display}|{snap.Phase}|{snap.Running}|{level}|{bounds}|{cfg.TransparentBackground}|{cfg.TextOutline}|" +
                      $"{cfg.Opacity}|{cfg.WarnEnabled}|{cfg.CriticalEnabled}";
            if (key == renderKey) return;
            renderKey = key;

            // Keep WinForms' idea of the bounds in sync; UpdateLayeredWindow below sets the real position and content.
            if (Bounds != bounds) Bounds = bounds;
            using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                    Draw(g, bmp.Size, snap, cfg, level);
                Push(bmp, bounds.Location, (byte)Math.Round(Math.Max(0.2, Math.Min(1, cfg.Opacity)) * 255));
            }
        }

        static void Draw(Graphics g, Size size, TimerSnapshot snap, Settings cfg, double blinkLevel)
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            var alpha = (snap.Running ? 1.0 : 0.55) * blinkLevel;
            GetColors(snap.Phase, cfg, out var fore, out var back);

            if (!cfg.TransparentBackground)
            {
                using (var path = RoundedRect(new RectangleF(0, 0, size.Width, size.Height), size.Height * 0.12f))
                using (var brush = new SolidBrush(WithAlpha(back, alpha)))
                    g.FillPath(brush, path);
            }

            var outline = cfg.TextOutline ? Math.Max(1.5f, size.Height * 0.045f) : 0f;
            var pad = size.Height * 0.06f + outline;
            var area = new RectangleF(pad, pad, size.Width - 2 * pad, size.Height - 2 * pad);
            if (area.Width <= 1 || area.Height <= 1) return;

            // Size from a template with every digit replaced by '0' so the text doesn't pulse as digits change.
            const float probeEm = 100f;
            RectangleF template;
            using (var probe = new GraphicsPath())
            {
                probe.AddString(Template(snap.Display), Family, (int)FontStyle.Bold, probeEm, PointF.Empty, StringFormat.GenericTypographic);
                template = probe.GetBounds();
            }
            if (template.Width <= 0 || template.Height <= 0) return;
            var scale = Math.Min(area.Width / template.Width, area.Height / template.Height);

            using (var path = new GraphicsPath())
            {
                path.AddString(snap.Display, Family, (int)FontStyle.Bold, probeEm * scale, PointF.Empty, StringFormat.GenericTypographic);
                var actual = path.GetBounds();
                var dx = area.X + (area.Width - actual.Width) / 2 - actual.X;
                var dy = area.Y + (area.Height - template.Height * scale) / 2 - template.Y * scale;
                using (var m = new Matrix())
                {
                    m.Translate(dx, dy);
                    path.Transform(m);
                }

                if (outline > 0)
                {
                    // Twice the width: the fill covers the inner half.
                    using (var pen = new Pen(Color.FromArgb((int)(alpha * 220), 0, 0, 0), outline * 2) { LineJoin = LineJoin.Round })
                        g.DrawPath(pen, path);
                }
                using (var brush = new SolidBrush(WithAlpha(fore, alpha)))
                    g.FillPath(brush, path);
            }
        }

        static void GetColors(string phase, Settings cfg, out Color fore, out Color back)
        {
            // At zero, keep the most severe colour that is enabled.
            if (phase == "expired") phase = cfg.CriticalEnabled ? "critical" : cfg.WarnEnabled ? "warning" : "normal";

            var transparent = cfg.TransparentBackground;
            switch (phase)
            {
                case "warning":
                    back = Amber;
                    fore = transparent ? Amber : Color.Black;
                    break;
                case "critical":
                    back = Red;
                    fore = transparent ? Red : Color.White;
                    break;
                default:
                    back = SolidBack;
                    fore = Color.White;
                    break;
            }
        }

        void Push(Bitmap bmp, Point location, byte alpha)
        {
            var screenDc = GetDC(IntPtr.Zero);
            var memDc = CreateCompatibleDC(screenDc);
            var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            var old = SelectObject(memDc, hBitmap);
            try
            {
                var size = new SIZE { Width = bmp.Width, Height = bmp.Height };
                var source = new POINT();
                var target = new POINT { X = location.X, Y = location.Y };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    SourceConstantAlpha = alpha,
                    AlphaFormat = AC_SRC_ALPHA,
                };
                if (!UpdateLayeredWindow(Handle, screenDc, ref target, ref size, memDc, ref source, 0, ref blend, ULW_ALPHA) && !loggedFailure)
                {
                    loggedFailure = true;
                    Log.Warn($"UpdateLayeredWindow failed (error {Marshal.GetLastWin32Error()})");
                }
            }
            finally
            {
                SelectObject(memDc, old);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        static string Template(string text)
        {
            var chars = text.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (char.IsDigit(chars[i])) chars[i] = '0';
            return new string(chars);
        }

        static Color WithAlpha(Color c, double alpha) => Color.FromArgb((int)Math.Round(c.A * alpha), c.R, c.G, c.B);

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var d = Math.Max(1f, radius * 2);
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        static FontFamily LoadFamily()
        {
            try { return new FontFamily("Segoe UI"); }
            catch (ArgumentException) { return FontFamily.GenericSansSerif; }
        }
    }
}
