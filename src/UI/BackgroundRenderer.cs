using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Voltide.UI
{
    /// <summary>
    /// The panel chrome: radial backdrop, VOLTIDE wordmark and the two header icons.
    ///
    /// Everything here is expressed in the Illustrator artboard's own units
    /// (viewBox 0 0 941 573) so numbers can be copied straight out of the SVG.
    /// The backdrop is static per window size and baked once; the icons are drawn
    /// per frame because they animate (spin / hover / press).
    /// </summary>
    public static class BackgroundRenderer
    {
        public const int DesignW = 941;
        public const int DesignH = 573;

        // card strip from the SVG placeholders: x=111.5/360.5/609.5, y=166.59, 220x300
        public const int CardTop = 152;   // artboard: 151.59 -> bottom lands on 451.59
        public const int CardGap = 29;

        // radial gradient: cx 470.5 cy 286.39 r 558.99, scaled 0.63 on Y
        private const float GradCy = 286.39f;
        private const float GradRx = 558.99f;
        private const float GradRy = 352.17f;
        private static readonly Color GradCenter = Color.FromArgb(0x40, 0x3a, 0x84);

        private static readonly Color WordmarkColor = Color.FromArgb(0xf2, 0xf2, 0xf2);
        private static readonly Color IconIdle = Color.FromArgb(0x9b, 0x9b, 0x9b);
        private static readonly Color IconHot = Color.FromArgb(0xf0, 0xf0, 0xf0);

        // header type from the SVG: translate(44.87 65.24), Montserrat SemiBold 34.57
        private const float WordX = 44.87f;
        private const float WordBaseline = 65.24f;
        private const float WordSize = 34.57f;

        // ---------------------------------------------------------------- paths

        // Path data copied verbatim from 背景卡片.svg.
        private const string ClosePathA =
            "M875.59,54.37s0,0,0,0l12.38-12.38c.75-.75.75-1.96,0-2.7-.75-.75-1.96-.75-2.7,0" +
            "l-11.03,11.03-11.03-11.03c-.75-.75-1.96-.75-2.71,0-.75.75-.75,1.96,0,2.7l11.03,11.03" +
            "-11.03,11.03c-.75.75-.75,1.96,0,2.7.37.37.86.56,1.35.56s.98-.19,1.35-.56l12.38-12.38s0,0,0,0Z";

        private const string ClosePathB =
            "M887.97,64.05l-7.2-7.2c-1.49-1.49-3.92-1.49-5.41,0l9.91,9.91c.37.37.86.56,1.35.56" +
            "s.98-.19,1.35-.56c.75-.75.75-1.96,0-2.7Z";

        private const string RefreshPathData =
            "M794.97,37.19c-8.45-.14-15.43,6.66-16.05,14.97l-1.12-1.12c-.75-.75-1.96-.75-2.71,0" +
            "-.75.75-.75,1.96,0,2.7l4.33,4.33c.36.36.85.56,1.35.56s.99-.2,1.35-.56l4.33-4.33" +
            "c.75-.75.75-1.96,0-2.7-.75-.75-1.96-.75-2.7,0l-1,1c.62-5.92,5.39-10.77,11.43-11.01" +
            ",7.03-.29,12.81,5.51,12.5,12.54-.22,5.05-3.67,9.51-8.51,10.95-3.83,1.14-7.79.32-10.77-1.95" +
            "-.12-.09-.29-.25-.5-.45-1.49-1.45-3.85-1.51-5.42-.14l1.04,1.19c2.94,3.37,7.1,5.51,11.57,5.68" +
            ",9,.33,16.43-6.89,16.43-15.82s-6.96-15.69-15.56-15.83Z";

        private static GraphicsPath _closePath;
        private static GraphicsPath _refresh;
        private static RectangleF _closeBox;
        private static RectangleF _refreshBox;

        /// <summary>
        /// Centre of the refresh icon's arc, in artboard units. The glyph is a ring
        /// with an arrow hanging off its lower-left, so its bounding box is 2.3 units
        /// left of the real centre — spinning around the box makes the whole icon
        /// orbit instead of turning on the spot.
        /// Fitted to the arc points only (the arrow tip is off-circle and excluded):
        /// centre (794.73, 53.01), radius 15.82.
        /// </summary>
        private static readonly PointF RefreshPivot = new PointF(794.73f, 53.01f);

        private static readonly Dictionary<string, Bitmap> _backdropCache = new Dictionary<string, Bitmap>();
        private static readonly Dictionary<string, Bitmap> _wordmarkCache = new Dictionary<string, Bitmap>();
        private static readonly Dictionary<string, Bitmap[]> _vignetteCache = new Dictionary<string, Bitmap[]>();

        // 暗角层.svg: two 210-unit edge strips. The revised file drops the early
        // 0.06 stop, so the ramp stays fully transparent until 74% and only then
        // climbs to 0.5 and 0.8 at the window edge.
        private const float VignetteWidth = 210f;
        private static readonly float[] VignetteStops = new float[] { 0f, 0.74f, 1f };
        private static readonly float[] VignetteAlpha = new float[] { 0f, 0.5f, 0.8f };

        private static void InitIcons()
        {
            if (_closePath != null) return;

            using (GraphicsPath a = SvgPath.Parse(ClosePathA))
            using (GraphicsPath b = SvgPath.Parse(ClosePathB))
            {
                _closePath = new GraphicsPath();
                _closePath.AddPath(a, false);
                _closePath.AddPath(b, false);
                _closeBox = _closePath.GetBounds();
            }

            _refresh = SvgPath.Parse(RefreshPathData);
            _refreshBox = _refresh.GetBounds();
        }

        /// <summary>Shrinks a box around a centre and shifts it, matching DrawIcons.</summary>
        private static RectangleF ShrinkBox(RectangleF box, PointF centre, float shiftX)
        {
            float x = centre.X + (box.Left - centre.X) * IconShrink + shiftX;
            float y = centre.Y + (box.Top - centre.Y) * IconShrink;
            return new RectangleF(x, y, box.Width * IconShrink, box.Height * IconShrink);
        }

        /// <summary>Icon hit box, in artboard coordinates, with a little slack.</summary>
        private static RectangleF RefreshHit
        {
            get { InitIcons(); return Inflate(ShrinkBox(_refreshBox, RefreshPivot, RefreshShiftX), 12f); }
        }

        private static RectangleF CloseHit
        {
            get { InitIcons(); return Inflate(ShrinkBox(_closeBox, BoxCentre(_closeBox), 0f), 12f); }
        }

        /// <summary>
        /// Maps a client point back into artboard space. Returns 1 for refresh,
        /// 2 for close, 0 for nothing.
        /// </summary>
        public static int HitTest(int x, int y, int width, float scale)
        {
            InitIcons();

            float offsetX = width - DesignW * scale;
            float ax = (x - offsetX) / scale;
            float ay = y / scale;

            if (RefreshHit.Contains(ax, ay)) return 1;
            if (CloseHit.Contains(ax, ay)) return 2;
            return 0;
        }

        private static RectangleF Inflate(RectangleF r, float d)
        {
            return new RectangleF(r.X - d, r.Y - d, r.Width + d * 2f, r.Height + d * 2f);
        }

        // ----------------------------------------------------------- backdrop

        /// <summary>Gradient + wordmark, 1x, cached per window size.</summary>
        public static Bitmap GetBackdrop(int w, int h)
        {
            if (w < 1) w = 1;
            if (h < 1) h = 1;

            string key = w + "x" + h;
            Bitmap cached;
            if (_backdropCache.TryGetValue(key, out cached)) return cached;

            if (_backdropCache.Count > 4)
            {
                foreach (Bitmap b in _backdropCache.Values) b.Dispose();
                _backdropCache.Clear();
            }

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);

            using (Bitmap big = new Bitmap(w * 2, h * 2, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.ScaleTransform(2f, 2f);

                    g.Clear(Color.Black);

                    // Artboard units -> physical pixels.
                    float k = w / (float)DesignW;

                    // The design's radial gradient is an ellipse that runs off the
                    // artboard; its edge colour is pure black, so filling the window
                    // black first makes the falloff seamless.
                    float cx = w / 2f;
                    using (GraphicsPath ellipse = new GraphicsPath())
                    {
                        float rx = GradRx * k;
                        float ry = GradRy * k;
                        float cy = GradCy * k;

                        ellipse.AddEllipse(cx - rx, cy - ry, rx * 2f, ry * 2f);

                        Color[] edge = new Color[ellipse.PointCount];
                        for (int i = 0; i < edge.Length; i++) edge[i] = Color.Black;

                        using (PathGradientBrush brush = new PathGradientBrush(ellipse))
                        {
                            brush.CenterColor = GradCenter;
                            brush.SurroundColors = edge;
                            g.FillPath(brush, ellipse);
                        }
                    }

                    // The wordmark is no longer part of the backdrop: it lives above
                    // the vignette, so it gets its own cached layer.
                }

                using (Graphics tg = Graphics.FromImage(bmp))
                {
                    tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    tg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    tg.DrawImage(big, new Rectangle(0, 0, w, h));
                }
            }

            _backdropCache[key] = bmp;
            return bmp;
        }

        /// <summary>
        /// "VOLTIDE" on its own transparent layer. It sits above the vignette, so it
        /// cannot be baked into the backdrop: the edge gradient would darken it.
        /// Cached per size, blitted 1:1 every frame.
        /// </summary>
        public static Bitmap GetWordmark(int w, int h)
        {
            string key = w + "x" + h;
            Bitmap cached;
            if (_wordmarkCache.TryGetValue(key, out cached)) return cached;

            if (_wordmarkCache.Count > 4)
            {
                foreach (Bitmap b in _wordmarkCache.Values) b.Dispose();
                _wordmarkCache.Clear();
            }

            float k = w / (float)DesignW;
            int lw = (int)Math.Ceiling(240f * k);
            int lh = (int)Math.Ceiling(90f * k);

            Bitmap bmp = new Bitmap(lw, lh, PixelFormat.Format32bppPArgb);

            using (Bitmap big = new Bitmap(lw * 2, lh * 2, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.ScaleTransform(2f, 2f);
                    DrawWordmark(g, k);
                }

                using (Graphics tg = Graphics.FromImage(bmp))
                {
                    tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    tg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    tg.DrawImage(big, new Rectangle(0, 0, lw, lh));
                }
            }

            _wordmarkCache[key] = bmp;
            return bmp;
        }

        /// <summary>
        /// 暗角层: a black gradient hugging each side, opaque up to 80% at the very
        /// edge. Cards slide underneath it, so a card leaving the three visible slots
        /// fades into the dark instead of being sliced off by the window frame.
        ///
        /// Kept as two narrow bands rather than one full-width layer: compositing a
        /// 941x573 alpha bitmap every frame cost about a millisecond for pixels that
        /// are almost entirely transparent.
        /// </summary>
        public static void DrawVignette(Graphics g, int w, int h)
        {
            Bitmap[] bands = GetVignetteBands(w, h);
            g.DrawImage(bands[0], 0, 0);
            g.DrawImage(bands[1], w - bands[1].Width, 0);
        }

        private static Bitmap[] GetVignetteBands(int w, int h)
        {
            string key = w + "x" + h;
            Bitmap[] cached;
            if (_vignetteCache.TryGetValue(key, out cached)) return cached;

            if (_vignetteCache.Count > 4)
            {
                foreach (Bitmap[] b in _vignetteCache.Values)
                {
                    if (b[0] != null) b[0].Dispose();
                    if (b[1] != null) b[1].Dispose();
                }
                _vignetteCache.Clear();
            }

            int bandW = (int)Math.Round(VignetteWidth * (w / (float)DesignW));
            if (bandW < 1) bandW = 1;

            Bitmap[] pair = new Bitmap[2];

            for (int i = 0; i < 2; i++)
            {
                bool left = i == 0;
                Bitmap bmp = new Bitmap(bandW, h, PixelFormat.Format32bppPArgb);

                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    DrawVignetteStrip(g, new RectangleF(0f, 0f, bandW, h), left);
                }

                pair[i] = bmp;
            }

            _vignetteCache[key] = pair;
            return pair;
        }

        private static void DrawVignetteStrip(Graphics g, RectangleF strip, bool mirrored)
        {
            using (LinearGradientBrush brush = new LinearGradientBrush(
                new PointF(strip.Left, 0f), new PointF(strip.Right, 0f),
                Color.Transparent, Color.Transparent))
            {
                ColorBlend blend = new ColorBlend();
                int n = VignetteStops.Length;
                float[] positions = new float[n];
                blend.Colors = new Color[n];

                for (int i = 0; i < n; i++)
                {
                    int src = mirrored ? (n - 1 - i) : i;
                    positions[i] = mirrored ? 1f - VignetteStops[src] : VignetteStops[i];
                    blend.Colors[i] = Color.FromArgb(
                        (int)Math.Round(VignetteAlpha[src] * 255f), 0, 0, 0);
                }

                blend.Positions = positions;
                brush.InterpolationColors = blend;
                g.FillRectangle(brush, strip);
            }
        }

        /// <summary>
        /// "VOLTIDE" is manually kerned in the SVG (one tspan per letter group with an
        /// absolute x), so it is drawn run by run at those exact positions rather than
        /// as one string with letter-spacing.
        /// </summary>
        private static void DrawWordmark(Graphics g, float k)
        {
            Font font = FontManager.Get(FontKind.MontserratSemiBold, WordSize * k);
            float top = WordBaseline * k - FontManager.Ascent(font);
            float left = WordX * k;

            using (SolidBrush brush = new SolidBrush(WordmarkColor))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                g.DrawString("V", font, brush, left, top, sf);
                g.DrawString("O", font, brush, left + 24.44f * k, top, sf);
                g.DrawString("L", font, brush, left + 53.55f * k, top, sf);
                g.DrawString("TIDE", font, brush, left + 72.02f * k, top, sf);
            }
        }

        // -------------------------------------------------------------- icons

        // Header icons: shrunk to 70% of the artboard size and pulled closer
        // together — the stock layout spread two large glyphs across a wide gap
        // that competed with the wordmark. The close icon keeps its position
        // (shrunk around its own centre); refresh slides right to close the gap.
        private const float IconShrink = 0.70f;
        private const float RefreshShiftX = 36f;

        /// <summary>
        /// Icons stay pinned to the right edge when the window grows for extra cards,
        /// so the whole group is offset by the extra width and then scaled.
        /// </summary>
        public static void DrawIcons(Graphics g, int width, float scale,
                                     float spinAngle, float refreshHot, float refreshPress,
                                     float closeHot, float closePress)
        {
            InitIcons();

            GraphicsState state = g.Save();
            g.TranslateTransform(width - DesignW * scale, 0f);
            g.ScaleTransform(scale, scale);

            GraphicsState s1 = g.Save();
            g.TranslateTransform(RefreshPivot.X + RefreshShiftX, RefreshPivot.Y);
            g.ScaleTransform(IconShrink, IconShrink);
            g.TranslateTransform(-RefreshPivot.X, -RefreshPivot.Y);
            DrawIcon(g, _refresh, RefreshPivot, spinAngle, refreshHot, refreshPress);
            g.Restore(s1);

            PointF cc = BoxCentre(_closeBox);
            GraphicsState s2 = g.Save();
            g.TranslateTransform(cc.X, cc.Y);
            g.ScaleTransform(IconShrink, IconShrink);
            g.TranslateTransform(-cc.X, -cc.Y);
            DrawIcon(g, _closePath, cc, 0f, closeHot, closePress);
            g.Restore(s2);

            g.Restore(state);
        }

        private static PointF BoxCentre(RectangleF box)
        {
            return new PointF(box.Left + box.Width / 2f, box.Top + box.Height / 2f);
        }

        private static void DrawIcon(Graphics g, GraphicsPath path, PointF pivot,
                                     float angle, float hot, float press)
        {
            float cx = pivot.X;
            float cy = pivot.Y;

            GraphicsState state = g.Save();

            g.TranslateTransform(cx, cy);
            if (angle != 0f) g.RotateTransform(angle);
            if (press < 0.999f) g.ScaleTransform(press, press);
            g.TranslateTransform(-cx, -cy);

            using (SolidBrush brush = new SolidBrush(Blend(IconIdle, IconHot, hot)))
            {
                g.FillPath(brush, path);
            }

            g.Restore(state);
        }

        private static Color Blend(Color from, Color to, float t)
        {
            if (t <= 0f) return from;
            if (t >= 1f) return to;
            return Color.FromArgb(
                (int)(from.R + (to.R - from.R) * t),
                (int)(from.G + (to.G - from.G) * t),
                (int)(from.B + (to.B - from.B) * t));
        }
    }
}
