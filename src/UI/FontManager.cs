using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Voltide.UI
{
    /// <summary>
    /// Loads the exact typefaces used in the Illustrator file.
    /// Uses PrivateFontCollection so we depend on the TTF files rather than on
    /// whatever Windows decides to substitute.
    ///
    /// The release build embeds all three TTFs as manifest resources, so a single
    /// Voltide.exe carries its own typography. File lookup stays as the fallback for
    /// the in-memory build (run.cmd), which has no embedded resources.
    /// </summary>
    public static class FontManager
    {
        private static readonly PrivateFontCollection _pfc = new PrivateFontCollection();
        private static FontFamily _montserratSemiBold;
        private static FontFamily _montserratMedium;
        private static FontFamily _hyqihei;   // CJK face: MiSans (unified with Latin)
        private static FontFamily _misans;    // MiSans digit subset: the tray battery digit
        private static bool _ready;

        // AddMemoryFont keeps a pointer into the buffer, so these blocks must live as
        // long as the collection. Freeing them corrupts the face on next use.
        private static readonly List<IntPtr> _fontBlocks = new List<IntPtr>();

        // Cached Font instances. Creating a Font per frame is the single biggest
        // cost in the render loop; these are few and never disposed.
        private static readonly Dictionary<string, Font> _fontCache = new Dictionary<string, Font>();

        // Bundled fonts win: the design must look identical on a machine where
        // MiSans was never installed. The folder is copied next to the exe (and next
        // to run.cmd for the in-memory build) so the project is portable.
        private static readonly string[] SearchRoots = new string[]
        {
            Path.Combine(Environment.CurrentDirectory, "Fonts"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts"),
            @"C:\Windows\Fonts",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Microsoft\Windows\Fonts"
        };

        public static void Init()
        {
            if (_ready) return;
            _ready = true;

            // The whole UI is unified on MiSans Medium: Latin, digits and CJK all come
            // from the one face, so mixed text like "GPW3 鼠标 89%" no longer switches
            // typeface mid-string. Medium is one weight heavier than Regular, which
            // keeps the 14-17 px menu text crisp instead of the grey fringes a thin
            // Regular weight leaves after the menu's 0.92x shrink.
            FontFamily misans = LoadEmbedded("MiSans-Medium.ttf")
                                ?? LoadAny(new string[] { "MiSans-Medium.ttf" })
                                ?? LoadEmbedded("MiSans-Regular.ttf")
                                ?? LoadAny(new string[] { "MiSans-Regular.ttf" });

            _montserratSemiBold = misans;
            _montserratMedium = misans;
            _hyqihei = misans;

            // The tray icon only ever draws 0-9 and %, so it keeps the tiny digit
            // subset rather than the full face.
            _misans = LoadEmbedded("MiSans-Digits.ttf")
                      ?? LoadAny(new string[] { "MiSans-Digits.ttf" })
                      ?? misans;

            // fallbacks
            if (_montserratSemiBold == null) _montserratSemiBold = NewFamily("MiSans");
            if (_montserratMedium == null) _montserratMedium = NewFamily("MiSans");
            if (_hyqihei == null) _hyqihei = NewFamily("Microsoft YaHei UI");
            if (_misans == null) _misans = _montserratSemiBold ?? NewFamily("MiSans");
        }

        private static FontFamily NewFamily(string name)
        {
            try { return new FontFamily(name); }
            catch { return FontFamily.GenericSansSerif; }
        }

        /// <summary>
        /// Loads a typeface strapped into the exe. Returns null when the build did not
        /// embed it, so the file lookup below can take over.
        /// </summary>
        private static FontFamily LoadEmbedded(string resourceName)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly()
                                             .GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;

                    byte[] data = new byte[stream.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = stream.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }

                    // Deliberately never freed: PrivateFontCollection points into this
                    // buffer for as long as the family is in use.
                    IntPtr block = Marshal.AllocCoTaskMem(data.Length);
                    Marshal.Copy(data, 0, block, data.Length);

                    HashSet<string> before = new HashSet<string>();
                    foreach (FontFamily existing in _pfc.Families) before.Add(existing.Name);

                    _pfc.AddMemoryFont(block, data.Length);
                    _fontBlocks.Add(block);

                    foreach (FontFamily added in _pfc.Families)
                    {
                        if (!before.Contains(added.Name)) return added;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static FontFamily LoadAny(string[] candidates)
        {
            foreach (string root in SearchRoots)
            {
                if (!Directory.Exists(root)) continue;

                foreach (string file in candidates)
                {
                    string full = Path.Combine(root, file);
                    if (!File.Exists(full)) continue;

                    try
                    {
                        // PrivateFontCollection.Families lists the newest face first,
                        // so taking the last entry returned SemiBold when Medium was
                        // added. Diff the set by name instead of trusting the index.
                        HashSet<string> before = new HashSet<string>();
                        foreach (FontFamily f in _pfc.Families) before.Add(f.Name);

                        _pfc.AddFontFile(full);

                        foreach (FontFamily f in _pfc.Families)
                        {
                            if (!before.Contains(f.Name)) return f;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            return null;
        }

        /// <summary>Font at an exact pixel size, matching SVG font-size units. Cached.</summary>
        public static Font Get(FontKind kind, float pixelSize)
        {
            Init();

            if (pixelSize < 1f) pixelSize = 1f;

            string key = ((int)kind).ToString() + "_" + pixelSize.ToString("F1");
            Font cached;
            if (_fontCache.TryGetValue(key, out cached)) return cached;

            FontFamily family;
            switch (kind)
            {
                case FontKind.MontserratSemiBold: family = _montserratSemiBold; break;
                case FontKind.MontserratMedium: family = _montserratMedium; break;
                case FontKind.Chinese: family = _hyqihei; break;
                case FontKind.MiSans: family = _misans; break;
                default: family = _montserratMedium; break;
            }

            Font font = new Font(family, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontCache[key] = font;
            return font;
        }

        /// <summary>Distance from the text baseline to the top of the em box, in pixels.</summary>
        public static float Ascent(Font font)
        {
            float em = font.FontFamily.GetEmHeight(font.Style);
            if (em <= 0f) return font.Size * 0.8f;
            return font.FontFamily.GetCellAscent(font.Style) * font.Size / em;
        }
    }

    public enum FontKind
    {
        MontserratSemiBold,
        MontserratMedium,
        Chinese,
        MiSans
    }
}
