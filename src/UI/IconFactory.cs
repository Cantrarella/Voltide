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
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapV5Header bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

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

        // BITMAPV5HEADER. biSize 124 is what tells GDI the alpha mask is real.
        // A 40-byte BITMAPINFOHEADER leaves the fourth byte unused, and any
        // 1-bit in the AND mask then makes Windows ignore alpha and paint the
        // premultiplied RGB opaquely. Rounded-corner coverage pixels are light
        // glass at partial alpha, so that paint is a dark frame on the corners
        // only — the straight sides are fully opaque and stay the glass colour.
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BitmapV5Header
        {
            public int bV5Size;
            public int bV5Width;
            public int bV5Height;
            public short bV5Planes;
            public short bV5BitCount;
            public int bV5Compression;
            public int bV5SizeImage;
            public int bV5XPelsPerMeter;
            public int bV5YPelsPerMeter;
            public int bV5ClrUsed;
            public int bV5ClrImportant;
            public uint bV5RedMask;
            public uint bV5GreenMask;
            public uint bV5BlueMask;
            public uint bV5AlphaMask;
            public uint bV5CSType;
            public int e0, e1, e2, e3, e4, e5, e6, e7, e8;
            public uint bV5GammaRed;
            public uint bV5GammaGreen;
            public uint bV5GammaBlue;
            public uint bV5Intent;
            public uint bV5ProfileData;
            public uint bV5ProfileSize;
            public uint bV5Reserved;
        }

        /// <summary>
        /// HICON with a real alpha channel. Bitmap.GetHicon drops it, so the
        /// rounded corners come out opaque black on the tray and the window.
        /// The mask stays all zeros: KB Q318876. A set mask bit switches the
        /// icon back to the classic AND/XOR blit and the corner fringe goes dark.
        /// </summary>
        private static Icon BitmapToIcon(Bitmap source)
        {
            int w = source.Width;
            int h = source.Height;

            BitmapV5Header bmi = new BitmapV5Header();
            bmi.bV5Size = Marshal.SizeOf(typeof(BitmapV5Header));
            bmi.bV5Width = w;
            bmi.bV5Height = -h;          // top-down
            bmi.bV5Planes = 1;
            bmi.bV5BitCount = 32;
            bmi.bV5Compression = 3;      // BI_BITFIELDS
            bmi.bV5RedMask = 0x00FF0000;
            bmi.bV5GreenMask = 0x0000FF00;
            bmi.bV5BlueMask = 0x000000FF;
            bmi.bV5AlphaMask = 0xFF000000;
            bmi.bV5CSType = 0x57696E20;  // LCS_WINDOWS_COLOR_SPACE 'Win '

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
                    if (a == 0)
                    {
                        b = g = r = 0;
                    }
                    else if (!premul && a < 255)
                    {
                        b = (byte)(b * a / 255);
                        g = (byte)(g * a / 255);
                        r = (byte)(r * a / 255);
                    }
                    dib[d] = b;
                    dib[d + 1] = g;
                    dib[d + 2] = r;
                    dib[d + 3] = a;
                }
            }

            Marshal.Copy(dib, 0, bits, dib.Length);
            // Empty mask. Bits are left null so every mask pixel stays 0.
            IntPtr hMask = CreateBitmap(w, h, 1, 1, null);

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
        // The full-charge / window icon is the reference plate, masked and
        // resampled. Frames live in icons/app-icon-*.png and are embedded as
        // appicon-<size>.png. Tray digits and the no-device bolt are still drawn.

        private static readonly int[] AppIconSizes = { 16, 20, 24, 32, 48, 64, 128, 256 };

        /// <summary>Tray pixels: 16 at 100%, 20 at 125%, 24 at 150%, 32 at 200%.</summary>
        public static int SystemSmallIconSize()
        {
            int s = GetSystemMetrics(49);
            if (s < 16) s = 16;
            return s;
        }

        /// <summary>Window icon pixels: 32 at 100%, 48 at 150%, 64 at 200%.</summary>
        public static int SystemLargeIconSize()
        {
            int s = GetSystemMetrics(11);
            if (s < 32) s = 32;
            return s;
        }

        /// <summary>Nearest embedded frame. Ties keep the larger one.</summary>
        public static int ClosestAppIconSize(int size)
        {
            int best = AppIconSizes[0];
            int bestDist = Abs(best - size);
            for (int i = 1; i < AppIconSizes.Length; i++)
            {
                int s = AppIconSizes[i];
                int dist = Abs(s - size);
                if (dist < bestDist || (dist == bestDist && s > best))
                {
                    best = s;
                    bestDist = dist;
                }
            }
            return best;
        }

        private static int Abs(int v)
        {
            return v < 0 ? -v : v;
        }

        private static byte[] ReadAppIconPng(int size)
        {
            int s = ClosestAppIconSize(size);
            string name = "appicon-" + s + ".png";
            using (Stream embedded = System.Reflection.Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream(name))
            {
                if (embedded != null)
                {
                    byte[] buf = new byte[embedded.Length];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = embedded.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return buf;
                }
            }

            string[] paths = new string[]
            {
                Path.Combine("icons", "app-icon-" + s + ".png"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", "app-icon-" + s + ".png")
            };
            for (int i = 0; i < paths.Length; i++)
            {
                if (File.Exists(paths[i])) return File.ReadAllBytes(paths[i]);
            }
            throw new FileNotFoundException("Missing app icon frame " + name);
        }

        /// <summary>Full-charge tray icon and the window icon, from the embedded frame.</summary>
        public static Icon ToAppIcon(int size)
        {
            using (Bitmap bmp = RenderAppIcon(size))
            {
                return BitmapToIcon(bmp);
            }
        }

        /// <summary>App icon bitmap: the embedded frame closest to <paramref name="size"/>.</summary>
        public static Bitmap RenderAppIcon(int size)
        {
            byte[] png = ReadAppIconPng(size);
            using (MemoryStream ms = new MemoryStream(png))
            using (Image src = Image.FromStream(ms))
            {
                Bitmap copy = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(copy))
                {
                    g.DrawImage(src, 0, 0, src.Width, src.Height);
                }
                return copy;
            }
        }

        public static void WriteAppIco(string path, int[] sizes)
        {
            List<byte[]> images = new List<byte[]>();
            foreach (int size in sizes)
            {
                images.Add(ReadAppIconPng(size));
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
