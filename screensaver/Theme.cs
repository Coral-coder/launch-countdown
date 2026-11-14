// Colour and type treatments. They are looks only: no agency logos or insignia.

using System;
using System.Drawing;

namespace LaunchCountdownSaver
{
    internal sealed class Theme
    {
        public string Name, Font = "Segoe UI", Mono = "Consolas", Marking;
        public Color Bg, Panel, Line, Ink, Ink2, Muted, Accent, Good, Warn, Bad, Info, Track, MarkBg;
        public bool Caps; public float Radius = 10, Rule;

        private static Color C(int rgb) { return Color.FromArgb(255, Color.FromArgb(rgb)); }

        public static readonly Theme[] All =
        {
            new Theme { Name = "Mission Control", Bg = C(0x0d0d0d), Panel = C(0x1a1a19), Line = C(0x383835), Ink = C(0xf0efec), Ink2 = C(0xc3c2b7), Muted = C(0x898781),
                Accent = C(0x3987e5), Good = C(0x0ca30c), Warn = C(0xfab219), Bad = C(0xd03b3b), Info = C(0x3987e5), Track = C(0xfab219) },
            new Theme { Name = "Agency", Bg = C(0x05070d), Panel = C(0x0d1424), Line = C(0x26324d), Ink = C(0xffffff), Ink2 = C(0xc9d3e6), Muted = C(0x7d8aa6),
                Accent = C(0xff4b33), Good = C(0x2fbf71), Warn = C(0xffb81c), Bad = C(0xff4b33), Info = C(0x4f8dfd), Track = C(0xffb81c), Caps = true, Radius = 4, Rule = 3 },
            new Theme { Name = "Deep Blue", Bg = C(0x01060f), Panel = C(0x061226), Line = C(0x1b3358), Ink = C(0xf4f8ff), Ink2 = C(0xb9cbe8), Muted = C(0x6f86ab),
                Accent = C(0x4aa3ff), Good = C(0x39d0a0), Warn = C(0xf2c14e), Bad = C(0xff6b6b), Info = C(0x4aa3ff), Track = C(0x7cc4ff), Font = "Segoe UI Light", Radius = 2 },
            new Theme { Name = "Tactical", Bg = C(0x0a0c0a), Panel = C(0x121612), Line = C(0x3a463a), Ink = C(0xd7e0c8), Ink2 = C(0xa9b59a), Muted = C(0x77826b),
                Accent = C(0x9fbf5a), Good = C(0x7fbf3f), Warn = C(0xe0b030), Bad = C(0xd9482f), Info = C(0x9fbf5a), Track = C(0xe0b030), Font = "Consolas", Caps = true, Radius = 0, Rule = 3 },
            new Theme { Name = "Restricted", Bg = C(0x0b0b0c), Panel = C(0x161618), Line = C(0x3b3b40), Ink = C(0xf2f2f2), Ink2 = C(0xc4c4c8), Muted = C(0x8a8a92),
                Accent = C(0xc8102e), Good = C(0x1f9d55), Warn = C(0xe3a008), Bad = C(0xc8102e), Info = C(0x5b8def), Track = C(0xe3a008), Caps = true, Radius = 0,
                Marking = "UNCLASSIFIED  //  PUBLIC DATA", MarkBg = C(0x007a33) },
            new Theme { Name = "Console", Bg = C(0x000000), Panel = C(0x061006), Line = C(0x1f4a1f), Ink = C(0x66ff66), Ink2 = C(0x3fcf3f), Muted = C(0x2c8f2c),
                Accent = C(0x66ff66), Good = C(0x66ff66), Warn = C(0xffd24a), Bad = C(0xff5a4a), Info = C(0x66ff66), Track = C(0xffd24a), Font = "Consolas", Caps = true, Radius = 0 },
            new Theme { Name = "Daylight", Bg = C(0xf3f2ee), Panel = C(0xffffff), Line = C(0xcfcdc4), Ink = C(0x1b1b19), Ink2 = C(0x44443f), Muted = C(0x77756c),
                Accent = C(0x1f6fd1), Good = C(0x0b8a0b), Warn = C(0xb97800), Bad = C(0xc22f2f), Info = C(0x1f6fd1), Track = C(0xd98200) },
        };

        public static Theme Cur
        {
            get { foreach (var t in All) if (string.Equals(t.Name, Settings.Theme, StringComparison.OrdinalIgnoreCase)) return t; return All[0]; }
        }
    }
}
