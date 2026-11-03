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
        // Frosted-glass squircle: near-white, faint mint-to-blue vertical gradient,
        // thin bright inner rim. A thick lightning bolt with round caps and joins
        // sits on top, mint (#7EE8B0) at the top-left to cyan (#7CC8F8) at the
        // bottom-right. No drop shadow — pixels outside the squircle stay alpha 0.
        // This is the exe / taskbar / Alt-Tab icon, and the tray glyph at 100%.

        private static readonly Color BoltMint = Color.FromArgb(255, 0x7E, 0xE8, 0xB0);
        private static readonly Color BoltCyan = Color.FromArgb(255, 0x7C, 0xC8, 0xF8);
        // Deeper ends used only at tray sizes, where the spec colours wash out.
        private static readonly Color BoltMintDeep = Color.FromArgb(255, 0x1F, 0xC4, 0x84);
        private static readonly Color BoltCyanDeep = Color.FromArgb(255, 0x1E, 0x96, 0xEE);

        private const float AppCornerRatio = 0.23f;

        // Spine of the bolt in icon space. Round caps extend past these points,
        // so the tips land inside the glass rather than on the rim.
        private static readonly PointF[] BoltSpine = new PointF[]
        {
            new PointF(0.585f, 0.21f),
            new PointF(0.340f, 0.50f),
            new PointF(0.730f, 0.47f),
            new PointF(0.420f, 0.78f)
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
        /// App icon bitmap, drawn at <paramref name="size"/> (never a downscale).
        /// The glass and its alpha are written from the rounded-rect distance, so
        /// every sample that falls outside the squircle is RGBA 0 — including the
        /// four corners. The bolt is a round-capped stroke, so the tips stay soft.
        /// </summary>
        public static Bitmap RenderAppIcon(int size)
        {
            if (size < 1) size = 1;

            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            PaintGlass(bmp, size);
            PaintBolt(bmp, size);
            // The stroke is anti-aliased and can leave a faint fringe past the
            // rim. Anything geometrically outside the squircle is cleared again.
            ClearOutsideSquircle(bmp, size * AppCornerRatio);
            return bmp;
        }

        private static bool SmallIcon(int size)
        {
            return size <= 32;
        }

        private static float BoltGrow(int size)
        {
            // Wider span at tray sizes, but not so wide the arms collide.
            if (size <= 16) return 1.06f;
            if (size <= 24) return 1.04f;
            if (size <= 32) return 1.02f;
            return 1f;
        }

        private static float BoltThickness(int size)
        {
            // Thick enough to read, thin enough that the notch stays open.
            if (size <= 16) return size * 0.20f;
            if (size <= 20) return size * 0.195f;
            if (size <= 24) return size * 0.19f;
            if (size <= 32) return size * 0.185f;
            return size * 0.175f;
        }

        private static Color LerpColor(Color a, Color b, float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return Color.FromArgb(
                255,
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        private static Color BoltColorMint(int size)
        {
            if (!SmallIcon(size)) return BoltMint;
            float t = size <= 16 ? 0.62f : size <= 20 ? 0.50f : size <= 24 ? 0.40f : 0.28f;
            return LerpColor(BoltMint, BoltMintDeep, t);
        }

        private static Color BoltColorCyan(int size)
        {
            if (!SmallIcon(size)) return BoltCyan;
            float t = size <= 16 ? 0.62f : size <= 20 ? 0.50f : size <= 24 ? 0.40f : 0.28f;
            return LerpColor(BoltCyan, BoltCyanDeep, t);
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

        private static void PaintGlass(Bitmap bmp, int size)
        {
            float radius = size * AppCornerRatio;
            if (radius < 1f) radius = 1f;
            if (radius > size * 0.5f) radius = size * 0.5f;

            bool small = SmallIcon(size);
            int baseA = small ? 248 : 230;
            Color top = small
                ? Color.FromArgb(baseA, 206, 242, 226)
                : Color.FromArgb(baseA, 246, 252, 249);
            Color bot = small
                ? Color.FromArgb(baseA, 188, 216, 244)
                : Color.FromArgb(baseA, 226, 238, 250);
            float rim = small ? Math.Max(1.25f, size * 0.09f) : Math.Max(1.25f, size * 0.028f);
            float rimStrength = small ? 0.95f : 0.88f;
            float sheen = small ? 0.20f : 0.12f;

            BitmapData data = bmp.LockBits(
                new Rectangle(0, 0, size, size),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                byte[] buf = new byte[stride * size];
                const int N = 4;
                int samples = N * N;

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int hit = 0;
                        for (int sy = 0; sy < N; sy++)
                        {
                            float py = y + (sy + 0.5f) / N;
                            for (int sx = 0; sx < N; sx++)
                            {
                                float px = x + (sx + 0.5f) / N;
                                if (RoundRectDistance(px, py, size, radius) <= 0f) hit++;
                            }
                        }

                        int i = y * stride + x * 4;
                        if (hit == 0)
                        {
                            buf[i] = buf[i + 1] = buf[i + 2] = buf[i + 3] = 0;
                            continue;
                        }

                        float cov = hit / (float)samples;
                        float v = (y + 0.5f) / size;
                        int r = (int)(top.R + (bot.R - top.R) * v);
                        int g = (int)(top.G + (bot.G - top.G) * v);
                        int b = (int)(top.B + (bot.B - top.B) * v);

                        if (v < 0.42f)
                        {
                            float u = (1f - v / 0.42f) * sheen;
                            r = (int)(r + (255 - r) * u);
                            g = (int)(g + (255 - g) * u);
                            b = (int)(b + (255 - b) * u);
                        }

                        float sd = RoundRectDistance(x + 0.5f, y + 0.5f, size, radius);
                        float distIn = -sd;
                        if (distIn >= 0f && distIn < rim)
                        {
                            float u = 1f - distIn / rim;
                            u = u * u * rimStrength;
                            r = (int)(r + (255 - r) * u);
                            g = (int)(g + (255 - g) * u);
                            b = (int)(b + (255 - b) * u);
                        }

                        int a = (int)(baseA * cov + 0.5f);
                        if (a < 1)
                        {
                            buf[i] = buf[i + 1] = buf[i + 2] = buf[i + 3] = 0;
                            continue;
                        }
                        if (a > 255) a = 255;
                        if (r < 0) r = 0; if (r > 255) r = 255;
                        if (g < 0) g = 0; if (g > 255) g = 255;
                        if (b < 0) b = 0; if (b > 255) b = 255;
                        buf[i] = (byte)b;
                        buf[i + 1] = (byte)g;
                        buf[i + 2] = (byte)r;
                        buf[i + 3] = (byte)a;
                    }
                }

                Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static void PaintBolt(Bitmap bmp, int size)
        {
            float grow = BoltGrow(size);
            float thick = BoltThickness(size);
            if (thick < 1.6f) thick = 1.6f;

            PointF[] pts = new PointF[BoltSpine.Length];
            for (int i = 0; i < BoltSpine.Length; i++)
            {
                float x = 0.5f + (BoltSpine[i].X - 0.5f) * grow;
                float y = 0.5f + (BoltSpine[i].Y - 0.5f) * grow;
                pts[i] = new PointF(x * size, y * size);
            }

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.CompositingMode = CompositingMode.SourceOver;

                // Brush larger than the stroke so the end colour is not wrapped
                // back onto the first pixel. Gamma off: Windows and libgdiplus match.
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    new RectangleF(size * 0.12f, size * 0.06f, size * 0.76f, size * 0.88f),
                    BoltColorMint(size), BoltColorCyan(size),
                    LinearGradientMode.ForwardDiagonal))
                using (Pen pen = new Pen(brush, thick))
                {
                    brush.GammaCorrection = false;
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, pts);
                }
            }
        }

        /// <summary>
        /// Forces every sample outside the squircle to transparent black.
        /// Partial edge pixels keep their coverage; the four corners do not.
        /// </summary>
        private static void ClearOutsideSquircle(Bitmap bmp, float radius)
        {
            int size = bmp.Width;
            if (radius < 1f) radius = 1f;

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
                            float py = y + (sy + 0.5f) / N;
                            for (int sx = 0; sx < N; sx++)
                            {
                                float px = x + (sx + 0.5f) / N;
                                if (RoundRectDistance(px, py, size, radius) <= 0f) hit++;
                            }
                        }

                        int i = y * stride + x * 4;
                        if (hit == 0)
                        {
                            buf[i] = buf[i + 1] = buf[i + 2] = buf[i + 3] = 0;
                            continue;
                        }
                        if (hit == samples) continue;

                        int a = buf[i + 3] * hit / samples;
                        if (a < 1)
                        {
                            buf[i] = buf[i + 1] = buf[i + 2] = buf[i + 3] = 0;
                        }
                        else
                        {
                            buf[i + 3] = (byte)a;
                        }
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
