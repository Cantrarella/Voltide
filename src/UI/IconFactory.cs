using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace Voltide.UI
{
    /// <summary>
    /// Renders Logo.svg (256 x 256: a green rounded plate with the bolt cut out in
    /// dark violet) at any size, and packs the results into a multi-size .ico.
    ///
    /// Drawn rather than downscaled from one bitmap: the tray asks for 16 px at 100%
    /// and 32 px at 200%, and a single 256 px source blurred badly at 16.
    /// </summary>
    public static class IconFactory
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const string BoltPath =
            "M104.13,163.15h-52.65c-4.24,0-7-4.46-5.1-8.25L130.53-13.1h96.53l-72.07,113.8" +
            "h63.06c4.87,0,7.5,5.71,4.33,9.41L33.7,330.7l70.43-167.55Z";

        private static readonly float[] Stops =
            { 0f, 0.07f, 0.15f, 0.24f, 0.32f, 0.41f, 0.5f, 0.59f, 0.66f, 0.73f, 0.8f, 0.87f, 0.94f, 1f };

        private static readonly Color[] Colors = new Color[]
        {
            Color.Black,
            Color.FromArgb(0x11, 0x0f, 0x24),
            Color.FromArgb(0x22, 0x1f, 0x46),
            Color.FromArgb(0x2f, 0x2a, 0x61),
            Color.FromArgb(0x38, 0x33, 0x74),
            Color.FromArgb(0x3e, 0x38, 0x80),
            Color.FromArgb(0x40, 0x3a, 0x84),
            Color.FromArgb(0x3e, 0x38, 0x80),
            Color.FromArgb(0x38, 0x33, 0x75),
            Color.FromArgb(0x30, 0x2b, 0x63),
            Color.FromArgb(0x23, 0x20, 0x49),
            Color.FromArgb(0x13, 0x11, 0x28),
            Color.Black,
            Color.Black
        };

        private static GraphicsPath _bolt;

        private static void EnsurePaths()
        {
            if (_bolt != null) return;

            _bolt = SvgPath.Parse(BoltPath);
        }

        // ------------------------------------------------------------ tray battery

        // The corner-bracket "viewfinder" that frames the digit, matching the
        // reference the user picked. Drawn as four L-shaped strokes instead of a
        // closed rectangle so the middle of each edge stays open.
        private const float BracketInset = 0.06f;    // bracket sits 6% in from the edge
        private const float BracketArm = 0.22f;      // each arm reaches 22% of the size
        private const float BracketWidth = 1f;       // stroke width, in pixels (tray)

        // The app icon uses a bolder, softer frame: thicker legs and a visibly
        // rounded outer corner. Kept separate so the tray glyph stays crisp.
        private const float AppBracketArm = 0.24f;
        // Leg thickness as a fraction of the canvas so the frame scales cleanly from
        // 16 px up to 256 px (a fixed 2 px was invisible at small sizes / chunky at
        // large). 0.035 -> ~9 px at 256, ~0.6 px at 16. The rounded elbow radius is
        // the stroke width / 2, produced by the Pen's round line-join.
        private const float AppBracketWidthRatio = 0.035f;

        private static readonly Color BracketColor = Color.FromArgb(0x27, 0x24, 0x51);
        private static readonly Color DigitGreen = Color.FromArgb(0x03, 0xC0, 0x78);
        private static readonly Color DigitYellow = Color.FromArgb(0xF2, 0xC1, 0x4E);
        private static readonly Color DigitRed = Color.FromArgb(0xE5, 0x56, 0x56);

        /// <summary>Picks the digit colour from the user's three-tier rule.</summary>
        public static Color DigitColorFor(int percent)
        {
            if (percent >= 30) return DigitGreen;
            if (percent >= 20) return DigitYellow;
            return DigitRed;
        }

        public static Icon ToBatteryIcon(int percent, int size)
        {
            using (Bitmap bmp = RenderBattery(percent, size))
            {
                IntPtr handle = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(handle).Clone();
                DestroyIcon(handle);
                return icon;
            }
        }

        /// <summary>
        /// Renders the tray icon: a transparent plate, a corner-bracket viewfinder in
        /// #272451, and the lowest device's percentage in MiSans Regular dead-centre.
        /// Drawn at 32 px (the 200% tray size) so the digit stays crisp; Windows
        /// downscales to 16 px at 100% DPI exactly like it does for every tray icon.
        /// </summary>
        public static Bitmap RenderBattery(int percent, int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                DrawBrackets(g, size);

                string text = percent.ToString();
                Color color = DigitColorFor(percent);

                // NOTE: FontManager.Get returns a shared, cached Font that must not
                // be disposed — a using() here would poison the cache for every other
                // renderer on the next frame.
                // Size by digit count so every width fits inside the bracket: one digit
                // gets the most air, two is the common case, and "100" shrinks so the
                // three glyphs don't overflow the frame.
                float px;
                if (text.Length >= 3) px = size * 0.50f;
                else if (text.Length == 2) px = size * 0.62f;
                else px = size * 0.64f;
                Font font = FontManager.Get(FontKind.MiSans, px);

                using (SolidBrush brush = new SolidBrush(color))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;

                    // Centre the digit on the full plate. A tiny visual nudge keeps
                    // MiSans Regular from sitting a hair high at small sizes.
                    RectangleF rect = new RectangleF(0f, 0.5f, size, size);
                    g.DrawString(text, font, brush, rect, sf);
                }
            }

            return bmp;
        }

        /// <summary>
        /// Renders the "full charge" tray icon: the brand lightning bolt (dark violet
        /// gradient, no green plate) centred inside the same corner-bracket frame as
        /// the battery digits. Transparent background, so the bracket + bolt float.
        /// Also the source for the new app.ico.
        /// </summary>
        public static Bitmap RenderLightning(int size)
        {
            return RenderLightning(size, false);
        }

        /// <summary>
        /// Renders the lightning mark. When <paramref name="appIcon"/> is true the
        /// corner bracket is drawn in the bolder, rounded app-icon style; otherwise
        /// the thin tray style is used (the full-charge tray glyph shares this path).
        /// </summary>
        public static Bitmap RenderLightning(int size, bool appIcon)
        {
            EnsurePaths();

            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);

                // White square plate goes down FIRST so it sits behind the bolt and
                // bracket (drawn later in this method). Square, not rounded.
                if (appIcon)
                {
                    using (SolidBrush plate = new SolidBrush(Color.White))
                    {
                        g.FillRectangle(plate, 0f, 0f, size, size);
                    }
                }

                // The bolt path lives in 256-unit design space and runs diagonally
                // across most of it. Measure its bounds, then scale it down to fit
                // inside the bracket with a little air on every side, and translate
                // so its centre lands dead-centre of the canvas.
                RectangleF bounds = _bolt.GetBounds();
                float boltW = bounds.Width;
                float boltH = bounds.Height;
                if (boltW <= 0f) boltW = 1f;
                if (boltH <= 0f) boltH = 1f;

                // Target the bolt at ~72% of the canvas: large enough to lead, but
                // with even air on every side so the tip never crowds the bracket.
                float target = size * 0.72f;
                float k = Math.Min(target / boltW, target / boltH);

                float cx = (bounds.Left + bounds.Right) / 2f;
                float cy = (bounds.Top + bounds.Bottom) / 2f;

                g.TranslateTransform(size / 2f, size / 2f);
                g.ScaleTransform(k, k);
                g.TranslateTransform(-cx, -cy);

                using (LinearGradientBrush grad = new LinearGradientBrush(
                    new PointF(32.18f, 320.43f), new PointF(344.18f, -267.28f),
                    Color.Black, Color.Black))
                {
                    ColorBlend blend = new ColorBlend();
                    blend.Positions = Stops;
                    blend.Colors = Colors;
                    grad.InterpolationColors = blend;

                    g.FillPath(grad, _bolt);
                }

                g.ResetTransform();
                if (appIcon) DrawAppBrackets(g, size);
                else DrawBrackets(g, size);
            }

            return bmp;
        }

        public static Icon ToLightningIcon(int size)
        {
            return ToLightningIcon(size, false);
        }

        /// <summary>
        /// Lightning mark as a tray/app Icon. When <paramref name="appIcon"/> is true
        /// the white square plate + rounded app-style bracket are used, matching the
        /// app.ico; otherwise the thin transparent tray style.
        /// </summary>
        public static Icon ToLightningIcon(int size, bool appIcon)
        {
            using (Bitmap bmp = RenderLightning(size, appIcon))
            {
                IntPtr handle = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(handle).Clone();
                DestroyIcon(handle);
                return icon;
            }
        }

        /// <summary>Draws the four L-shaped bracket strokes.</summary>
        /// <remarks>
        /// Drawn as axis-aligned filled rectangles snapped to whole pixels and NOT
        /// anti-aliased. An anti-aliased Pen blurs the 1px stroke into translucent
        /// fringes that dissolve once Windows downscales the 32px canvas to the
        /// 16px tray size — the exact "blurry bracket" the user saw. Solid,
        /// pixel-aligned fills stay crisp through the downscale.
        /// </remarks>
        private static void DrawBrackets(Graphics g, int size)
        {
            DrawBrackets(g, size, BracketArm, BracketWidth, 0f);
        }

        private static void DrawBrackets(Graphics g, int size, float armRatio, float widthPx, float cornerRadiusPx)
        {
            float inset = size * BracketInset;
            float arm = size * armRatio;
            float w = widthPx;                       // leg thickness
            float r = cornerRadiusPx;                // corner cap radius (0 = sharp)

            float left = inset;
            float top = inset;
            float right = size - inset;
            float bottom = size - inset;

            using (SolidBrush brush = new SolidBrush(BracketColor))
            {
                DrawCorner(g, brush, left, top, right, bottom, arm, w, r, Quadrant.TopLeft);
                DrawCorner(g, brush, left, top, right, bottom, arm, w, r, Quadrant.TopRight);
                DrawCorner(g, brush, left, top, right, bottom, arm, w, r, Quadrant.BottomRight);
                DrawCorner(g, brush, left, top, right, bottom, arm, w, r, Quadrant.BottomLeft);
            }
        }

        private enum Quadrant { TopLeft, TopRight, BottomRight, BottomLeft }

        /// <summary>
        /// Fills one L-shaped corner. Two straight arms plus a rounded corner cap of
        /// radius <paramref name="r"/>. The cap is a rounded square of the stroke
        /// width so the elbow turns smoothly without ever swelling into a solid block;
        /// keep r close to (or smaller than) the stroke width.
        /// </summary>
        private static void DrawCorner(Graphics g, SolidBrush brush,
            float left, float top, float right, float bottom,
            float arm, float w, float r, Quadrant q)
        {
            float hx, hy;   // horizontal arm origin
            float vx, vy;   // vertical arm origin
            switch (q)
            {
                case Quadrant.TopLeft:
                    hx = left; hy = top; vx = left; vy = top; break;
                case Quadrant.TopRight:
                    hx = right - arm; hy = top; vx = right - w; vy = top; break;
                case Quadrant.BottomRight:
                    hx = right - arm; hy = bottom - w; vx = right - w; vy = bottom - arm; break;
                default: // BottomLeft
                    hx = left; hy = bottom - w; vx = left; vy = bottom - arm; break;
            }

            g.FillRectangle(brush, hx, hy, arm, w);   // horizontal arm
            g.FillRectangle(brush, vx, vy, w, arm);   // vertical arm

            if (r <= 0f) return;

            // Rounded corner cap: a rounded square (side = w, corner radius = r)
            // placed at the outer corner point so the two arms meet through a smooth
            // elbow no larger than the stroke itself.
            float capX, capY;
            switch (q)
            {
                case Quadrant.TopLeft:      capX = left;       capY = top;          break;
                case Quadrant.TopRight:     capX = right - w;  capY = top;          break;
                case Quadrant.BottomRight:  capX = right - w;  capY = bottom - w;   break;
                default:                    capX = left;       capY = bottom - w;   break;
            }

            using (GraphicsPath cap = RoundRect(capX, capY, w, w, r))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(brush, cap);
                g.SmoothingMode = SmoothingMode.None;
            }
        }

        /// <summary>
        /// App-icon variant: four L-shaped corner brackets whose outer elbow is
        /// rounded. Drawn as stroked two-segment polylines with a round line-join, so
        /// GDI+ rounds the corner exactly the way a design tool's "round join" does —
        /// no manual arc geometry to get wrong.
        /// </summary>
        private static void DrawAppBrackets(Graphics g, int size)
        {
            float inset = size * BracketInset;
            float arm = size * AppBracketArm;
            float w = size * AppBracketWidthRatio;   // stroke scales with the canvas

            float left = inset, top = inset;
            float right = size - inset, bottom = size - inset;

            // Each bracket is a two-segment polyline: vertical leg (to the corner)
            // then horizontal leg (out from the corner). The round join at the corner
            // point produces the rounded elbow; round caps soften the open ends.
            PointF[][] brackets = new PointF[][]
            {
                new PointF[] { new PointF(left,  top + arm), new PointF(left,  top), new PointF(left + arm, top) },    // top-left
                new PointF[] { new PointF(right, top + arm), new PointF(right, top), new PointF(right - arm, top) },   // top-right
                new PointF[] { new PointF(right, bottom - arm), new PointF(right, bottom), new PointF(right - arm, bottom) }, // bottom-right
                new PointF[] { new PointF(left,  bottom - arm), new PointF(left,  bottom), new PointF(left + arm, bottom) }    // bottom-left
            };

            using (Pen pen = new Pen(BracketColor, w))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (PointF[] bracket in brackets)
                {
                    g.DrawLines(pen, bracket);
                }
                g.SmoothingMode = SmoothingMode.None;
            }
        }

        /// <summary>Writes the lightning-and-bracket mark as a multi-size .ico.</summary>
        public static void WriteLightningIco(string path, int[] sizes)
        {
            List<byte[]> images = new List<byte[]>();

            foreach (int size in sizes)
            {
                using (Bitmap bmp = RenderLightning(size, true))
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    images.Add(ms.ToArray());
                }
            }

            WriteIcoBytes(path, sizes, images);
        }

        private static void WriteIcoBytes(string path, int[] sizes, List<byte[]> images)
        {
            using (FileStream fs = File.Create(path))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);              // reserved
                w.Write((ushort)1);              // type: icon
                w.Write((ushort)sizes.Length);

                long offset = 6 + 16 * sizes.Length;

                for (int i = 0; i < sizes.Length; i++)
                {
                    int s = sizes[i];
                    byte dim = (byte)(s >= 256 ? 0 : s);

                    w.Write(dim);                // width
                    w.Write(dim);                // height
                    w.Write((byte)0);            // palette
                    w.Write((byte)0);            // reserved
                    w.Write((ushort)1);          // colour planes
                    w.Write((ushort)32);         // bits per pixel
                    w.Write((uint)images[i].Length);
                    w.Write((uint)offset);

                    offset += images[i].Length;
                }

                foreach (byte[] image in images)
                {
                    w.Write(image);
                }
            }
        }

        private static GraphicsPath RoundRect(float x, float y, float w, float h, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;

            p.AddArc(x, y, d, d, 180f, 90f);
            p.AddArc(x + w - d, y, d, d, 270f, 90f);
            p.AddArc(x + w - d, y + h - d, d, d, 0f, 90f);
            p.AddArc(x, y + h - d, d, d, 90f, 90f);
            p.CloseFigure();

            return p;
        }
    }
}
