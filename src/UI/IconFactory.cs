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
        private static extern IntPtr CreateIconIndirect(ref IconInfo icon);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        /// <summary>
        /// HICON with a real alpha channel. Bitmap.GetHicon drops it, so the
        /// rounded corners come out opaque black on the tray and the window.
        /// </summary>
        private static Icon BitmapToIcon(Bitmap source)
        {
            int w = source.Width;
            int h = source.Height;

            BitmapInfo bmi = new BitmapInfo();
            bmi.biSize = 40;
            bmi.biWidth = w;
            bmi.biHeight = -h;          // top-down
            bmi.biPlanes = 1;
            bmi.biBitCount = 32;
            bmi.biCompression = 0;

            IntPtr hdc = GetDC(IntPtr.Zero);
            IntPtr bits;
            IntPtr hColor = CreateDIBSection(hdc, ref bmi, 0, out bits, IntPtr.Zero, 0);
            ReleaseDC(IntPtr.Zero, hdc);
            if (hColor == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();

            bool premul = source.PixelFormat == PixelFormat.Format32bppPArgb;
            BitmapData src = source.LockBits(
                new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, source.PixelFormat);
            int srcStride = src.Stride;
            byte[] raw = new byte[srcStride * h];
            Marshal.Copy(src.Scan0, raw, 0, raw.Length);
            source.UnlockBits(src);

            int dibStride = w * 4;
            byte[] dib = new byte[dibStride * h];
            // CreateBitmap wants word-aligned rows.
            int maskStride = ((w + 15) / 16) * 2;
            byte[] mask = new byte[maskStride * h];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int s = y * srcStride + x * 4;
                    int d = y * dibStride + x * 4;
                    byte b = raw[s];
                    byte g = raw[s + 1];
                    byte r = raw[s + 2];
                    byte a = raw[s + 3];
                    if (!premul && a < 255)
                    {
                        b = (byte)(b * a / 255);
                        g = (byte)(g * a / 255);
                        r = (byte)(r * a / 255);
                    }
                    if (a == 0)
                    {
                        b = g = r = 0;
                        int mb = y * maskStride + (x >> 3);
                        mask[mb] |= (byte)(0x80 >> (x & 7));
                    }
                    dib[d] = b;
                    dib[d + 1] = g;
                    dib[d + 2] = r;
                    dib[d + 3] = a;
                }
            }

            Marshal.Copy(dib, 0, bits, dib.Length);
            IntPtr hMask = CreateBitmap(w, h, 1, 1, mask);

            IconInfo ii = new IconInfo();
            ii.fIcon = true;
            ii.hbmColor = hColor;
            ii.hbmMask = hMask;
            IntPtr hIcon = CreateIconIndirect(ref ii);

            Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
            DestroyIcon(hIcon);
            DeleteObject(hColor);
            DeleteObject(hMask);
            return icon;
        }

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
                return BitmapToIcon(bmp);
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
                return BitmapToIcon(bmp);
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
        // Cool-grey frosted plate (#E9ECEF to #E3E8EE), mint outer rim plus a
        // white inner highlight, soft shadow under a filled bolt. The bolt runs
        // mint #A6F2C2, cyan #7FE0E8, sky #80C8FA. Drawn at 4x/8x and boxed down
        // in premultiplied space so the corner fringe is the plate colour at a
        // partial alpha, not a grey pixel. Outside the squircle: (0,0,0,0).

        private const float AppCornerRatio = 0.22f;

        // Filled outline in icon space, clockwise from the top tip. Traced from
        // the reference plate so every size shares one silhouette.
        private static readonly PointF[] BoltFill = new PointF[]
        {
            new PointF(0.5851f, 0.1492f),
            new PointF(0.6060f, 0.1522f),
            new PointF(0.6209f, 0.1626f),
            new PointF(0.6269f, 0.1746f),
            new PointF(0.6269f, 0.2045f),
            new PointF(0.5582f, 0.4015f),
            new PointF(0.5671f, 0.4119f),
            new PointF(0.6747f, 0.4119f),
            new PointF(0.7179f, 0.4179f),
            new PointF(0.7328f, 0.4269f),
            new PointF(0.7433f, 0.4448f),
            new PointF(0.7433f, 0.4746f),
            new PointF(0.7164f, 0.5223f),
            new PointF(0.5671f, 0.6926f),
            new PointF(0.4671f, 0.8000f),
            new PointF(0.4298f, 0.8239f),
            new PointF(0.4089f, 0.8283f),
            new PointF(0.3896f, 0.8224f),
            new PointF(0.3776f, 0.8030f),
            new PointF(0.3776f, 0.7806f),
            new PointF(0.3866f, 0.7418f),
            new PointF(0.4373f, 0.5971f),
            new PointF(0.4388f, 0.5701f),
            new PointF(0.4149f, 0.5626f),
            new PointF(0.2881f, 0.5596f),
            new PointF(0.2702f, 0.5507f),
            new PointF(0.2582f, 0.5328f),
            new PointF(0.2582f, 0.4985f),
            new PointF(0.2687f, 0.4731f),
            new PointF(0.3015f, 0.4269f),
            new PointF(0.4910f, 0.2105f),
            new PointF(0.5388f, 0.1671f),
            new PointF(0.5807f, 0.1507f)
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
                return BitmapToIcon(bmp);
            }
        }

        /// <summary>
        /// App icon bitmap, drawn at <paramref name="size"/> (never a downscale of
        /// a bigger icon). The plate, rim and bolt are rasterised on a 4× or 8×
        /// grid and box-filtered in premultiplied space.
        /// </summary>
        public static Bitmap RenderAppIcon(int size)
        {
            if (size < 1) size = 1;

            int ss = size <= 32 ? 8 : 4;
            int big = size * ss;
            byte[] hi = new byte[big * big * 4];
            PaintSupersampled(hi, big, size, ss);

            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            DownsamplePremultiplied(hi, big, size, ss, bmp);
            return bmp;
        }

        /// <summary>Signed distance to the rounded square. Negative is inside.</summary>
        private static float RoundRectDistance(float px, float py, float size, float radius)
        {
            float half = size * 0.5f;
            float cx = px - half;
            float cy = py - half;
            float qx = Math.Abs(cx) - (half - radius);
            float qy = Math.Abs(cy) - (half - radius);
            float ox = qx > 0f ? qx : 0f;
            float oy = qy > 0f ? qy : 0f;
            float outside = (float)Math.Sqrt(ox * ox + oy * oy);
            float inner = Math.Max(qx, qy);
            if (inner > 0f) inner = 0f;
            return outside + inner - radius;
        }

        // 16 and 20 are hand-set so the notch stays open and no stray pixel hangs
        // off the lower tip. '#' is bolt. Same silhouette as the traced fill.
        private const string Bolt16 =
            "................" +
            "................" +
            ".........#......" +
            "........##......" +
            ".......###......" +
            "......###......." +
            ".....####......." +
            "....########...." +
            "....#######....." +
            ".......###......" +
            ".......###......" +
            "......###......." +
            "......##........" +
            "................" +
            "................" +
            "................";

        private const string Bolt20 =
            "...................." +
            "...................." +
            "...................." +
            "...........##......." +
            "..........##........" +
            ".........###........" +
            "........####........" +
            ".......####........." +
            "......#########....." +
            ".....##########....." +
            ".....#########......" +
            ".........####......." +
            ".........####......." +
            "........####........" +
            "........###........." +
            "........##.........." +
            "...................." +
            "...................." +
            "...................." +
            "....................";

        private static void PaintSupersampled(byte[] hi, int big, int size, int ss)
        {
            float radius = size * AppCornerRatio;
            float mintW = size >= 64 ? size * 0.013f : 1.05f;
            float whiteW = size >= 64 ? size * 0.008f : (size >= 24 ? 0.9f : 0f);
            bool hand = size == 16 || size == 20;
            string handGrid = size == 16 ? Bolt16 : Bolt20;

            for (int y = 0; y < big; y++)
            {
                float fy = (y + 0.5f) / ss;
                float ny = fy / size;
                for (int x = 0; x < big; x++)
                {
                    float fx = (x + 0.5f) / ss;
                    float nx = fx / size;
                    float sd = RoundRectDistance(fx, fy, size, radius);
                    int i = (y * big + x) * 4;
                    if (sd > 0f)
                    {
                        hi[i] = hi[i + 1] = hi[i + 2] = hi[i + 3] = 0;
                        continue;
                    }

                    float dist = -sd;
                    int r, g, b;
                    PlateRgb(ny, dist, mintW, whiteW, out r, out g, out b);

                    bool bolt;
                    if (hand)
                    {
                        int gx = (int)fx;
                        int gy = (int)fy;
                        if (gx < 0) gx = 0;
                        if (gy < 0) gy = 0;
                        if (gx >= size) gx = size - 1;
                        if (gy >= size) gy = size - 1;
                        bolt = handGrid[gy * size + gx] == '#';
                    }
                    else
                    {
                        bolt = PointInBolt(nx, ny);
                    }

                    // Shadow sits down-right of the bolt, on the plate only.
                    if (!bolt && PointInBolt(nx - 0.012f, ny - 0.022f))
                    {
                        r = (r * 62 + 136 * 38) / 100;
                        g = (g * 62 + 150 * 38) / 100;
                        b = (b * 62 + 162 * 38) / 100;
                    }

                    if (bolt)
                    {
                        BoltRgb(nx, ny, out r, out g, out b);
                    }

                    hi[i] = (byte)b;
                    hi[i + 1] = (byte)g;
                    hi[i + 2] = (byte)r;
                    hi[i + 3] = 255;
                }
            }
        }

        /// <summary>Cool grey body, mint outer lip, white inner lip, darker foot.</summary>
        private static void PlateRgb(float ny, float dist, float mintW, float whiteW,
            out int r, out int g, out int b)
        {
            float v = ny;
            if (v < 0f) v = 0f;
            if (v > 1f) v = 1f;
            // #E9ECEF at the top, #E3E8EE at the bottom.
            r = (int)(0xE9 + (0xE3 - 0xE9) * v);
            g = (int)(0xEC + (0xE8 - 0xEC) * v);
            b = (int)(0xEF + (0xEE - 0xEF) * v);

            if (v > 0.62f)
            {
                float u = (v - 0.62f) / 0.38f;
                // Soft inner shadow along the lower curve.
                float k = u * (dist < mintW + whiteW + 3f ? 0.72f : 0.42f);
                r = (int)(r + (0xC5 - r) * k);
                g = (int)(g + (0xCC - g) * k);
                b = (int)(b + (0xD2 - b) * k);
            }

            // Keep the outermost sliver the plate colour. The coverage fringe is
            // then that colour at partial alpha, not a mint or grey pixel.
            const float lip = 0.45f;
            if (dist >= lip && dist < lip + mintW)
            {
                r = 0xC5; g = 0xE6; b = 0xD6;
            }
            else if (dist >= lip + mintW && dist < lip + mintW + whiteW)
            {
                r = 0xFB; g = 0xFF; b = 0xFC;
            }
        }

        /// <summary>Mint, then cyan, then sky, across the bolt.</summary>
        private static void BoltRgb(float nx, float ny, out int r, out int g, out int b)
        {
            // Mostly top-to-bottom, so the upper tip is mint and the lower tip is sky.
            float t = (ny - 0.14f) / 0.70f;
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            int r1 = 0xA6, g1 = 0xF2, b1 = 0xC2;
            int r2 = 0x7F, g2 = 0xE0, b2 = 0xE8;
            int r3 = 0x80, g3 = 0xC8, b3 = 0xFA;
            if (t < 0.42f)
            {
                float u = t / 0.42f;
                r = (int)(r1 + (r2 - r1) * u);
                g = (int)(g1 + (g2 - g1) * u);
                b = (int)(b1 + (b2 - b1) * u);
            }
            else
            {
                float u = (t - 0.42f) / 0.58f;
                r = (int)(r2 + (r3 - r2) * u);
                g = (int)(g2 + (g3 - g2) * u);
                b = (int)(b2 + (b3 - b2) * u);
            }
        }

        private static bool PointInBolt(float x, float y)
        {
            bool inside = false;
            int n = BoltFill.Length;
            int j = n - 1;
            for (int i = 0; i < n; i++)
            {
                float yi = BoltFill[i].Y;
                float yj = BoltFill[j].Y;
                if ((yi > y) != (yj > y))
                {
                    float xi = BoltFill[i].X;
                    float xj = BoltFill[j].X;
                    float xint = (xj - xi) * (y - yi) / (yj - yi) + xi;
                    if (x < xint) inside = !inside;
                }
                j = i;
            }
            return inside;
        }

        /// <summary>
        /// Box-filter in premultiplied space, then store straight alpha so a
        /// half-covered edge stays the plate colour at half alpha instead of grey.
        /// </summary>
        private static void DownsamplePremultiplied(byte[] hi, int big, int size, int ss, Bitmap bmp)
        {
            int n = ss * ss;
            BitmapData data = bmp.LockBits(
                new Rectangle(0, 0, size, size),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                byte[] buf = new byte[stride * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int pr = 0, pg = 0, pb = 0, pa = 0;
                        int y0 = y * ss;
                        int x0 = x * ss;
                        for (int sy = 0; sy < ss; sy++)
                        {
                            int row = (y0 + sy) * big;
                            for (int sx = 0; sx < ss; sx++)
                            {
                                int i = (row + x0 + sx) * 4;
                                int a = hi[i + 3];
                                pb += hi[i] * a;
                                pg += hi[i + 1] * a;
                                pr += hi[i + 2] * a;
                                pa += a;
                            }
                        }

                        int o = y * stride + x * 4;
                        // One subsample at 8× is ~4/255. Drop anything that faint so
                        // a corner does not grow a grey dust pixel.
                        if (pa < n * 8)
                        {
                            buf[o] = buf[o + 1] = buf[o + 2] = buf[o + 3] = 0;
                            continue;
                        }

                        int A = pa / n;
                        if (A > 255) A = 255;
                        int R = pr / pa;
                        int G = pg / pa;
                        int B = pb / pa;
                        if (R > 255) R = 255;
                        if (G > 255) G = 255;
                        if (B > 255) B = 255;
                        buf[o] = (byte)B;
                        buf[o + 1] = (byte)G;
                        buf[o + 2] = (byte)R;
                        buf[o + 3] = (byte)A;
                    }
                }
                Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
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
