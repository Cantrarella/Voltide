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
    /// Draws the tray glyphs and the app icon at any pixel size, and packs the app
    /// icon into a multi-size .ico.
    ///
    /// Each size is drawn on its own bitmap. Downscaling one 256 px plate is what
    /// made the 16 px tray (and the 20/24 px sizes at 125/150% DPI) go soft.
    /// </summary>
    public static class IconFactory
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXICON = 11;      // large icon, caption / Alt-Tab
        private const int SM_CXSMICON = 49;    // small icon, tray / title bar

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
        /// No-device tray glyph: the brand lightning bolt (dark violet gradient, no
        /// plate) centred inside the same thin corner-bracket frame as the battery
        /// digits. Transparent background. The full-charge tray uses
        /// <see cref="RenderAppIcon"/> instead — this is not the app icon.
        /// </summary>
        public static Bitmap RenderLightning(int size)
        {
            EnsurePaths();

            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);

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
                DrawBrackets(g, size);
            }

            return bmp;
        }

        /// <summary>No-device tray icon. Transparent plate, thin bracket, violet bolt.</summary>
        public static Icon ToLightningIcon(int size)
        {
            using (Bitmap bmp = RenderLightning(size))
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

        // --------------------------------------------------------------- app icon
        //
        // Rounded squircle, diagonal lime (#D8F59A, top-left) to mint (#4FD8C8,
        // bottom-right), white geometric bolt centred. No frame, no shadow. This is
        // the exe / taskbar / Alt-Tab icon, and the tray glyph when the lowest
        // online device is at 100%.

        private static readonly Color AppLime = Color.FromArgb(255, 0xD8, 0xF5, 0x9A);
        private static readonly Color AppMint = Color.FromArgb(255, 0x4F, 0xD8, 0xC8);

        // Corner radius / side. The reference has long flat edges and a circular
        // corner of about this size (an iOS-style rounded square, not a pure
        // superellipse).
        private const float AppCornerRatio = 0.22f;

        // Bolt in icon space (0..1), clockwise from the top tip. Traced from the
        // reference so the slash, the notch and the two tips land in the same place.
        private static readonly PointF[] AppBoltUnit = new PointF[]
        {
            new PointF(0.623f, 0.187f),
            new PointF(0.543f, 0.440f),
            new PointF(0.723f, 0.441f),
            new PointF(0.381f, 0.801f),
            new PointF(0.464f, 0.550f),
            new PointF(0.279f, 0.549f)
        };

        /// <summary>
        /// Small-icon pixels for the DPI this process is aware of.
        /// 16 at 100%, 20 at 125%, 24 at 150%, 32 at 200%.
        /// </summary>
        public static int SystemSmallIconSize()
        {
            int s = GetSystemMetrics(SM_CXSMICON);
            if (s < 16) s = 16;
            return s;
        }

        /// <summary>
        /// Large-icon pixels for the DPI this process is aware of.
        /// 32 at 100%, 40 at 125%, 48 at 150%, 64 at 200%.
        /// </summary>
        public static int SystemLargeIconSize()
        {
            int s = GetSystemMetrics(SM_CXICON);
            if (s < 32) s = 32;
            return s;
        }

        /// <summary>App icon as a single-size HICON, drawn natively at <paramref name="size"/>.</summary>
        public static Icon ToAppIcon(int size)
        {
            if (size < 1) size = 1;
            using (Bitmap bmp = RenderAppIcon(size))
            {
                IntPtr handle = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(handle).Clone();
                DestroyIcon(handle);
                return icon;
            }
        }

        /// <summary>
        /// App icon bitmap. Anti-aliased, and the bolt vertices are snapped to the
        /// half-pixel grid so the slanted edges don't shimmer between sizes.
        /// At 16 and 24 px (and 20, which is the 125% tray size) the bolt is scaled
        /// up a little; below that the waist drops under two pixels and disappears.
        /// </summary>
        public static Bitmap RenderAppIcon(int size)
        {
            if (size < 1) size = 1;

            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.CompositingQuality = CompositingQuality.HighQuality;

                // Opaque gradient first. The squircle alpha is applied afterwards
                // from the corner geometry, so the silhouette does not depend on
                // how GDI+ rasterises a path (libgdiplus and Windows disagree, and
                // both bias the bottom-right corner).
                using (LinearGradientBrush grad = new LinearGradientBrush(
                    new RectangleF(-1f, -1f, size + 2f, size + 2f),
                    AppLime, AppMint, LinearGradientMode.ForwardDiagonal))
                {
                    // Brush is larger than the plate so the end colour is not
                    // wrapped back onto the first pixel. Gamma off: Windows GDI+
                    // and libgdiplus then both blend in sRGB, matching the art.
                    grad.GammaCorrection = false;
                    g.FillRectangle(grad, -1, -1, size + 2, size + 2);
                }

                float k = BoltScaleFor(size);
                PointF[] pts = new PointF[AppBoltUnit.Length];
                for (int i = 0; i < AppBoltUnit.Length; i++)
                {
                    float x = 0.5f + (AppBoltUnit[i].X - 0.5f) * k;
                    float y = 0.5f + (AppBoltUnit[i].Y - 0.5f) * k;
                    pts[i] = new PointF(Snap(x * size), Snap(y * size));
                }

                using (GraphicsPath bolt = new GraphicsPath())
                using (SolidBrush white = new SolidBrush(Color.White))
                {
                    bolt.AddPolygon(pts);
                    g.FillPath(white, bolt);
                }
            }

            ApplySquircleAlpha(bmp, size * AppCornerRatio);
            return bmp;
        }

        /// <summary>Slightly bolder bolt at the sizes where a 1 px waist vanishes.</summary>
        private static float BoltScaleFor(int size)
        {
            if (size <= 16) return 1.16f;
            if (size <= 24) return 1.08f;
            return 1f;
        }

        /// <summary>Snap to the half-pixel grid so anti-aliased edges stay symmetric.</summary>
        private static float Snap(float value)
        {
            return (float)(Math.Round(value * 2.0) / 2.0);
        }

        /// <summary>
        /// Writes the squircle into the alpha channel. 4×4 coverage samples, so the
        /// four corners match and a 16 px icon still has a real radius.
        /// </summary>
        private static void ApplySquircleAlpha(Bitmap bmp, float radius)
        {
            int size = bmp.Width;
            if (radius < 0.5f) radius = 0.5f;
            if (radius > size / 2f) radius = size / 2f;

            BitmapData data = bmp.LockBits(
                new Rectangle(0, 0, size, size),
                ImageLockMode.ReadWrite,
                PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                byte[] buf = new byte[stride * size];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);

                const int N = 4;
                int samples = N * N;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int hit = 0;
                        for (int sy = 0; sy < N; sy++)
                        {
                            for (int sx = 0; sx < N; sx++)
                            {
                                float px = x + (sx + 0.5f) / N;
                                float py = y + (sy + 0.5f) / N;
                                if (InsideRoundRect(px, py, size, radius)) hit++;
                            }
                        }

                        buf[y * stride + x * 4 + 3] = (byte)(hit * 255 / samples);
                    }
                }

                Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        /// <summary>Point-in-rounded-rect. The plate is the canvas, radius from the corner.</summary>
        private static bool InsideRoundRect(float px, float py, float size, float radius)
        {
            float dx = px < radius ? radius - px : (px > size - radius ? px - (size - radius) : 0f);
            float dy = py < radius ? radius - py : (py > size - radius ? py - (size - radius) : 0f);
            if (dx == 0f && dy == 0f) return true;
            if (dx == 0f) return py >= 0f && py <= size;
            if (dy == 0f) return px >= 0f && px <= size;
            return dx * dx + dy * dy <= radius * radius;
        }

        /// <summary>
        /// Writes the app icon as a multi-size .ico. Every size is drawn on its own
        /// canvas — the 16 px image is not a scaled 256.
        /// </summary>
        public static void WriteAppIco(string path, int[] sizes)
        {
            List<byte[]> images = new List<byte[]>();

            foreach (int size in sizes)
            {
                using (Bitmap bmp = RenderAppIcon(size))
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
