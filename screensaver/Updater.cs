// Keeps the program current: it checks the GitHub release it came from and, when a newer build is
// there, downloads it and swaps the files in place. The new build runs from the next start.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace LaunchCountdownSaver
{
    internal static class Updater
    {
        private const string Release = "https://github.com/Coral-coder/launch-countdown/releases/download/screensaver/";
        /// <summary>Something worth telling the person once: an update was installed, or could not be.</summary>
        public static volatile string Note = "";
        public static volatile bool Updated;
        public static string Version = "";
        private static bool started;

        public static void Start()
        {
            if (started) return; started = true;
            try { Version = File.ReadAllText(Path.Combine(Folder, "version.txt")).Trim(); } catch { }
            new Thread(() =>
            {
                Thread.Sleep(8000); // let the display come up first
                while (true)
                {
                    try { if (Settings.AutoUpdate && !Updated) Check(); } catch { }
                    Thread.Sleep(TimeSpan.FromHours(6));
                }
            }) { IsBackground = true, Name = "update" }.Start();
        }

        private static string Folder { get { return Path.GetDirectoryName(Application.ExecutablePath); } }

        private sealed class Client : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var r = base.GetWebRequest(address); var h = r as HttpWebRequest;
                r.Timeout = 60000;
                if (h != null) { h.UserAgent = "LaunchCountdownScreensaver/2"; h.ReadWriteTimeout = 60000; }
                return r;
            }
        }

        private static void Check()
        {
            string dir = Folder;
            // files replaced by an earlier update were renamed aside, because Windows will not delete a running program
            foreach (var old in Directory.GetFiles(dir, "*.old", SearchOption.AllDirectories)) { try { File.Delete(old); } catch { } }

            string remote;
            using (var c = new Client()) remote = c.DownloadString(Release + "version.txt?t=" + DateTime.UtcNow.Ticks).Trim();
            if (remote.Length < 7 || remote.Length > 64 || remote == Version) return;

            string tmp = Path.Combine(Path.GetTempPath(), "LaunchCountdownUpdate"), unpacked = Path.Combine(tmp, "files");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            using (var c = new Client()) c.DownloadFile(Release + "LaunchCountdownScreensaver.zip", Path.Combine(tmp, "update.zip"));
            ZipFile.ExtractToDirectory(Path.Combine(tmp, "update.zip"), unpacked);
            // only install something that is plainly this program, and the build the release says it is
            if (!File.Exists(Path.Combine(unpacked, "LaunchCountdown.exe")) || !File.Exists(Path.Combine(unpacked, "LaunchCountdown.scr"))) return;
            if (File.ReadAllText(Path.Combine(unpacked, "version.txt")).Trim() != remote) return;

            var moved = new List<string[]>(); var added = new List<string>();
            try
            {
                foreach (var src in Directory.GetFiles(unpacked, "*", SearchOption.AllDirectories))
                {
                    string rel = src.Substring(unpacked.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), target = Path.Combine(dir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (File.Exists(target))
                    {
                        string aside = target + ".old";
                        if (File.Exists(aside)) File.Delete(aside);
                        File.Move(target, aside); moved.Add(new[] { aside, target });
                    }
                    File.Copy(src, target); added.Add(target);
                }
            }
            catch (Exception e)
            {
                // put everything back exactly as it was
                foreach (var f in added) { try { File.Delete(f); } catch { } }
                foreach (var m in moved) { try { if (!File.Exists(m[1])) File.Move(m[0], m[1]); } catch { } }
                Note = e is UnauthorizedAccessException
                    ? "A newer version is available but this folder cannot be written to. Move the Launch Countdown folder somewhere like Documents, or update it by hand."
                    : "A newer version is available but could not be installed: " + e.Message;
                return;
            }
            Updated = true; Version = remote;
            Note = "Updated to the newest version. It takes effect the next time Launch Countdown starts.";
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}
