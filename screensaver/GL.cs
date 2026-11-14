// OpenGL through opengl32.dll, which every Windows has: no graphics library to install.
// GLView is the window the 3D corridor is drawn in.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LaunchCountdownSaver
{
    internal static class GL
    {
        private const string L = "opengl32.dll";
        public const int COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x100, DEPTH_TEST = 0x0B71, BLEND = 0x0BE2, TEXTURE_2D = 0x0DE1, LINE_SMOOTH = 0x0B20,
            SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303, ONE = 1, PROJECTION = 0x1701, MODELVIEW = 0x1700, LINES = 1, LINE_LOOP = 2, LINE_STRIP = 3,
            TRIANGLES = 4, TRIANGLE_STRIP = 5, TRIANGLE_FAN = 6, QUADS = 7, POINTS = 0, RGBA = 0x1908, UNSIGNED_BYTE = 0x1401, LINEAR = 0x2601,
            TEXTURE_MAG_FILTER = 0x2800, TEXTURE_MIN_FILTER = 0x2801, NICEST = 0x1102, LINE_SMOOTH_HINT = 0x0C52, LEQUAL = 0x0203, CULL_FACE = 0x0B44,
            POINT_SMOOTH = 0x0B10, UNPACK_ALIGNMENT = 0x0CF5;

        [DllImport(L)] public static extern void glClearColor(float r, float g, float b, float a);
        [DllImport(L)] public static extern void glClear(int mask);
        [DllImport(L)] public static extern void glViewport(int x, int y, int w, int h);
        [DllImport(L)] public static extern void glMatrixMode(int mode);
        [DllImport(L)] public static extern void glLoadIdentity();
        [DllImport(L)] public static extern void glLoadMatrixd(double[] m);
        [DllImport(L)] public static extern void glOrtho(double l, double r, double b, double t, double n, double f);
        [DllImport(L)] public static extern void glEnable(int cap);
        [DllImport(L)] public static extern void glDisable(int cap);
        [DllImport(L)] public static extern void glBlendFunc(int s, int d);
        [DllImport(L)] public static extern void glDepthMask(byte on);
        [DllImport(L)] public static extern void glDepthFunc(int f);
        [DllImport(L)] public static extern void glLineWidth(float w);
        [DllImport(L)] public static extern void glPointSize(float s);
        [DllImport(L)] public static extern void glColor4f(float r, float g, float b, float a);
        [DllImport(L)] public static extern void glBegin(int mode);
        [DllImport(L)] public static extern void glEnd();
        [DllImport(L)] public static extern void glVertex3d(double x, double y, double z);
        [DllImport(L)] public static extern void glVertex2d(double x, double y);
        [DllImport(L)] public static extern void glTexCoord2d(double s, double t);
        [DllImport(L)] public static extern void glGenTextures(int n, int[] ids);
        [DllImport(L)] public static extern void glDeleteTextures(int n, int[] ids);
        [DllImport(L)] public static extern void glBindTexture(int target, int id);
        [DllImport(L)] public static extern void glTexImage2D(int target, int level, int internalFormat, int w, int h, int border, int format, int type, byte[] pixels);
        [DllImport(L)] public static extern void glTexParameteri(int target, int name, int value);
        [DllImport(L)] public static extern void glPixelStorei(int name, int value);
        [DllImport(L)] public static extern void glHint(int target, int mode);
        [DllImport(L)] public static extern void glFinish();

        public static void Color(Color c, double a = 1) { glColor4f(c.R / 255f, c.G / 255f, c.B / 255f, (float)a); }

        /// <summary>Upload a picture as a texture, stretched to a power-of-two size (which every driver accepts).</summary>
        public static int Texture(Bitmap src, int w, int h, Func<int, int, byte, byte> alpha = null)
        {
            byte[] px;
            using (var b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.Clear(System.Drawing.Color.Transparent);
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY); // no dark fringe at the edges
                        g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                    }
                }
                var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                px = new byte[w * h * 4];
                for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * w * 4, w * 4);
                b.UnlockBits(d);
            }
            for (int i = 0; i < px.Length; i += 4)
            {
                byte t = px[i]; px[i] = px[i + 2]; px[i + 2] = t; // BGRA to RGBA
                if (alpha != null) px[i + 3] = alpha((i / 4) % w, (i / 4) / w, px[i + 3]);
            }
            return Texture(px, w, h);
        }

        public static int Texture(byte[] rgba, int w, int h)
        {
            var id = new int[1];
            glGenTextures(1, id);
            glBindTexture(TEXTURE_2D, id[0]);
            glPixelStorei(UNPACK_ALIGNMENT, 1);
            glTexParameteri(TEXTURE_2D, TEXTURE_MIN_FILTER, LINEAR);
            glTexParameteri(TEXTURE_2D, TEXTURE_MAG_FILTER, LINEAR);
            glTexImage2D(TEXTURE_2D, 0, RGBA, w, h, 0, RGBA, UNSIGNED_BYTE, rgba);
            return id[0];
        }

        public static void Free(ref int tex)
        {
            if (tex != 0) glDeleteTextures(1, new[] { tex });
            tex = 0;
        }
    }

    /// <summary>4x4 matrices in OpenGL's column-major layout.</summary>
    internal static class Mat
    {
        public static double[] Perspective(double fovY, double aspect, double near, double far)
        {
            double f = 1 / Math.Tan(fovY * Math.PI / 360);
            return new[] { f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0 };
        }

        public static double[] LookAt(double ex, double ey, double ez, double tx, double ty, double tz)
        {
            double fx = tx - ex, fy = ty - ey, fz = tz - ez, fl = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            fx /= fl; fy /= fl; fz /= fl;
            double sx = -fz, sy = 0, sz = fx, sl = Math.Sqrt(sx * sx + sz * sz); // forward x up(0,1,0)
            if (sl < 1e-9) { sx = 1; sz = 0; sl = 1; }
            sx /= sl; sz /= sl;
            double ux = sy * fz - sz * fy, uy = sz * fx - sx * fz, uz = sx * fy - sy * fx;
            return new[] { sx, ux, -fx, 0, sy, uy, -fy, 0, sz, uz, -fz, 0, -(sx * ex + sy * ey + sz * ez), -(ux * ex + uy * ey + uz * ez), fx * ex + fy * ey + fz * ez, 1 };
        }

        public static double[] Mul(double[] a, double[] b)
        {
            var o = new double[16];
            for (int c = 0; c < 4; c++) for (int r = 0; r < 4; r++)
                o[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
            return o;
        }

        /// <summary>World point to window pixels (origin top-left). False when it is behind the camera.</summary>
        public static bool Project(double[] m, double x, double y, double z, int w, int h, out double sx, out double sy)
        {
            double cx = m[0] * x + m[4] * y + m[8] * z + m[12], cy = m[1] * x + m[5] * y + m[9] * z + m[13], cw = m[3] * x + m[7] * y + m[11] * z + m[15];
            sx = sy = 0;
            if (cw <= 1e-6) return false;
            sx = (cx / cw * 0.5 + 0.5) * w; sy = (1 - (cy / cw * 0.5 + 0.5)) * h;
            return true;
        }
    }

    /// <summary>A child window with its own OpenGL context. Failed is set when Windows could not give it one.</summary>
    internal sealed class GLView : Control
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PIXELFORMATDESCRIPTOR
        {
            public ushort nSize, nVersion; public uint dwFlags; public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift,
                cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits, cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
            public uint dwLayerMask, dwVisibleMask, dwDamageMask;
        }
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern int ChoosePixelFormat(IntPtr dc, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern bool SetPixelFormat(IntPtr dc, int format, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32.dll")] private static extern bool SwapBuffers(IntPtr dc);
        [DllImport("opengl32.dll", SetLastError = true)] private static extern IntPtr wglCreateContext(IntPtr dc);
        [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr dc, IntPtr rc);
        [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr rc);

        private IntPtr dc, rc;
        public bool Failed { get; private set; }
        public readonly Scene3D Scene = new Scene3D();

        public GLView()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Color.Black;
        }

        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ClassStyle |= 0x0020 | 0x0002 | 0x0001; return p; } // CS_OWNDC, redraw on resize
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                dc = GetDC(Handle);
                var pfd = new PIXELFORMATDESCRIPTOR { nSize = (ushort)Marshal.SizeOf(typeof(PIXELFORMATDESCRIPTOR)), nVersion = 1, dwFlags = 0x4 | 0x20 | 0x1, cColorBits = 32, cDepthBits = 24 }; // window, OpenGL, double buffer
                int fmt = ChoosePixelFormat(dc, ref pfd);
                if (fmt == 0 || !SetPixelFormat(dc, fmt, ref pfd)) { Failed = true; return; }
                rc = wglCreateContext(dc);
                if (rc == IntPtr.Zero) Failed = true;
            }
            catch { Failed = true; }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try
            {
                if (rc != IntPtr.Zero) { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); wglDeleteContext(rc); rc = IntPtr.Zero; }
                if (dc != IntPtr.Zero) { ReleaseDC(Handle, dc); dc = IntPtr.Zero; }
            }
            catch { }
            base.OnHandleDestroyed(e);
        }

        protected override void OnPaint(PaintEventArgs e) { Render(); }

        public void Render()
        {
            if (Failed || rc == IntPtr.Zero || Width < 2 || Height < 2 || !Visible) return;
            try
            {
                if (!wglMakeCurrent(dc, rc)) { Failed = true; return; }
                Scene.Draw(Width, Height);
                SwapBuffers(dc);
            }
            catch { Failed = true; }
        }
    }

#if TEST
    /// <summary>Off-screen rendering through Mesa, used to check the 3D drawing without Windows.</summary>
    internal static class OffScreen
    {
        [DllImport("libOSMesa.so.8")] private static extern IntPtr OSMesaCreateContextExt(int format, int depth, int stencil, int accum, IntPtr share);
        [DllImport("libOSMesa.so.8")] private static extern bool OSMesaMakeCurrent(IntPtr ctx, IntPtr buffer, int type, int w, int h);
        [DllImport("libOSMesa.so.8")] private static extern void OSMesaPixelStore(int name, int value);

        public static Bitmap Render(Scene3D scene, int w, int h, int frames)
        {
            var ctx = OSMesaCreateContextExt(GL.RGBA, 24, 0, 0, IntPtr.Zero);
            var buf = Marshal.AllocHGlobal(w * h * 4);
            if (!OSMesaMakeCurrent(ctx, buf, GL.UNSIGNED_BYTE, w, h)) throw new Exception("no Mesa context");
            OSMesaPixelStore(0x11, 0); // rows top to bottom
            for (int i = 0; i < frames; i++) { scene.Draw(w, h); System.Threading.Thread.Sleep(30); }
            GL.glFinish();
            var px = new byte[w * h * 4];
            Marshal.Copy(buf, px, 0, px.Length);
            for (int i = 0; i < px.Length; i += 4) { byte t = px[i]; px[i] = px[i + 2]; px[i + 2] = t; px[i + 3] = 255; }
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, d.Scan0 + y * d.Stride, w * 4);
            b.UnlockBits(d);
            return b;
        }
    }
#endif
}
