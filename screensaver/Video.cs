// The live video panel. The pad stream and launch webcasts are web players, so this one panel is a
// WebView2 surface; everything else in the screensaver is drawn natively. It takes no input.

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LaunchCountdownSaver
{
    internal sealed class WebVideo : VideoHost
    {
        private readonly WebView2 web; private bool ready, starting; private string url, loaded;

        public WebVideo(Control parent)
        {
            web = new WebView2 { Visible = false, Enabled = false, DefaultBackgroundColor = Color.Black };
            parent.Controls.Add(web);
        }

        private async void Start()
        {
            if (starting) return; starting = true;
            try
            {
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchCountdownSaver");
                var env = await CoreWebView2Environment.CreateAsync(null, data, new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
                await web.EnsureCoreWebView2Async(env);
                var s = web.CoreWebView2.Settings;
                s.AreDefaultContextMenusEnabled = false; s.AreDevToolsEnabled = false; s.IsStatusBarEnabled = false; s.AreBrowserAcceleratorKeysEnabled = false;
                web.CoreWebView2.NewWindowRequested += (o, n) => n.Handled = true;
                web.CoreWebView2.IsMuted = true;
                ready = true;
                Go();
            }
            catch { Failed = true; web.Visible = false; }
        }

        private void Go()
        {
            if (!ready || url == loaded) return;
            loaded = url;
            // The players are opened inside a small page on the countdown site rather than directly: YouTube
            // refuses (error 153) when its player has no embedding page to name as the referrer.
            try { web.CoreWebView2.Navigate(url == null ? "about:blank" : Settings.SiteBase() + "player.html#" + Uri.EscapeDataString(url)); } catch { Failed = true; }
        }

        public override void Show(string address, Rectangle bounds)
        {
            if (Failed) return;
            if (web.Bounds != bounds) web.Bounds = bounds;
            if (!web.Visible) web.Visible = true;
            url = address;
            if (!ready) Start(); else Go();
        }

        /// <summary>Unload the player, so nothing is playing (or holding the display awake) while it is not shown.</summary>
        public override void Hide()
        {
            if (web.Visible) web.Visible = false;
            url = null; Go();
        }
    }
}
