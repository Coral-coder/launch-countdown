#if TEST
// Test harness: draws a slide or the 3D view to a PNG without a window.
//   /shot <slide name | 3d> <width> <height> <file> [seconds to wait for data] [T-0 offset in seconds]
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;

namespace LaunchCountdownSaver
{
    internal static class Shots
    {
        public static void Run(string[] a)
        {
            int w = int.Parse(a[2]), h = int.Parse(a[3]), wait = a.Length > 5 ? int.Parse(a[5]) : 25;
            Store.Start(false);
            Store.TrafficWanted = DateTime.UtcNow.AddSeconds(wait);
            Thread.Sleep(wait * 1000);
            var l = Store.Current;
            if (a.Length > 6 && l != null) Count.SetT0(l, DateTime.UtcNow.AddSeconds(-double.Parse(a[6])));
            Console.WriteLine("launch " + (l == null ? "none" : l.Name + " az " + l.Heading + (l.HeadingEstimated ? " est" : "")) + " · wx " + (Store.Weather != null) + " · clouds " + (Store.Cloud != null) +
                " · map " + (Store.Map != null) + " · radar " + (Store.Radar != null) + " · planes " + Store.Planes.Count + " · areas " + Store.Areas.Count + " · " + Store.LaunchNote);
            var scene = new Scene3D();
            Bitmap view = null;
            foreach (var name in a[1].Split(','))
            {
                string file = a[4].Replace("*", name);
                if (name == "3d" || name == "chase" || name == "pad")
                {
                    scene.View = name == "chase" ? Scene3D.Mode.Chase : name == "pad" ? Scene3D.Mode.Pad : Scene3D.Mode.Orbit;
                    using (var b = OffScreen.Render(scene, w, h, 3)) b.Save(file, ImageFormat.Png);
                    continue;
                }
                var slide = name == "help" ? Slide.Count : (Slide)Enum.Parse(typeof(Slide), name, true);
                using (var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(b))
                    {
                        Layout lay;
                        using (var p = new Painter(g, w, h) { Scene = scene, VideoAvailable = true }) { p.Draw(slide, 0, 6); p.Banner(l, false, null, name == "help"); lay = p.Out; }
                        Painter.DrawClock(g, lay, h / 1080f, DateTime.UtcNow);
                        if (lay.View3D.Width > 4)
                        {
                            var r = Rectangle.Round(lay.View3D);
                            scene.View = slide == Slide.Terminal ? Scene3D.Mode.Chase : Scene3D.Mode.Orbit;
                            view = OffScreen.Render(scene, r.Width, r.Height, 3);
                            g.DrawImageUnscaled(view, r.X, r.Y);
                            // the numbers beside the view come from the scene, so draw the slide again now that it has run
                            using (var p = new Painter(g, w, h) { Scene = scene, VideoAvailable = true }) { p.Draw(slide, 0, 6); lay = p.Out; }
                            Painter.DrawClock(g, lay, h / 1080f, DateTime.UtcNow);
                            g.DrawImageUnscaled(view, r.X, r.Y);
                        }
                        if (lay.Video.Width > 4) using (var br = new SolidBrush(Color.FromArgb(30, 30, 40))) g.FillRectangle(br, lay.Video);
                    }
                    b.Save(file, ImageFormat.Png);
                }
            }
            Console.WriteLine("done");
        }
    }
}
#endif
