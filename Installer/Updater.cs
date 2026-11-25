using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GMSCoopInstaller
{
    /// <summary>
    /// Launcher self-update from the project's GitHub releases. The latest release carries two assets:
    /// "GMS Co-op.exe" and "GMS Co-op.exe.sha256" (hex hash, optionally followed by the file name).
    /// A newer tag (vX.Y.Z) shows a banner; Apply downloads the exe, checks the hash, swaps the running
    /// exe (Windows allows renaming a running exe: old one to .old, new one in place) and restarts.
    /// Every failure is silent: the launcher keeps working with the version it has.
    /// </summary>
    internal static class Updater
    {
        internal const string ReleasesApi = "https://api.github.com/repos/RArtutos/gms-coop/releases/latest";
        private const string UserAgent = "GMS-Coop-Launcher";
        private const string ExeAsset = "GMS Co-op.exe";

        internal sealed class Release
        {
            public Version Version;
            public string Tag;
            public string ExeUrl;
            public string ShaUrl;
        }

        internal static Version Current
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>Removes the previous exe left by an update (it was still running during the swap).</summary>
        internal static void CleanupOld()
        {
            try
            {
                string exe = ExePath;
                try { if (File.Exists(exe + ".new")) File.Delete(exe + ".new"); } catch { } // a download that stopped halfway
                string old = exe + ".old";
                if (!File.Exists(old)) return;
                // Right after an update the previous launcher may still be closing: retry for a few seconds.
                Task.Run(() =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        try { if (!File.Exists(old)) return; File.Delete(old); return; }
                        catch { System.Threading.Thread.Sleep(500); }
                    }
                });
            }
            catch { }
        }

        private static string ExePath => Process.GetCurrentProcess().MainModule.FileName;

        /// <summary>The latest release if it is newer than this launcher, else null (also on any error).</summary>
        /// <summary>True when the last check could not reach GitHub (offline, rate limit): "no update" is then unknown.</summary>
        internal static volatile bool LastCheckFailed;

        internal static Task<Release> CheckAsync() => Task.Run(() =>
        {
            LastCheckFailed = false;
            try
            {
                if (string.IsNullOrEmpty(ReleasesApi)) return null;
                string json = Get(ReleasesApi);
                Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(?:\\.[0-9]+){1,3})\"");
                if (!tag.Success || !Version.TryParse(tag.Groups[1].Value, out Version v)) return null;
                v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
                if (v <= Current) return null;
                string exe = null, sha = null;
                foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
                {
                    string url = m.Groups[1].Value;
                    string name = Uri.UnescapeDataString(url.Substring(url.LastIndexOf('/') + 1));
                    // GitHub stores asset names with spaces as dots ("GMS.Co-op.exe"): accept both spellings.
                    string norm = name.Replace(' ', '.');
                    string exeNorm = ExeAsset.Replace(' ', '.');
                    if (norm.Equals(exeNorm, StringComparison.OrdinalIgnoreCase)) exe = url;
                    else if (norm.Equals(exeNorm + ".sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
                }
                if (exe == null || sha == null) return null; // never install an unverified file
                return new Release { Version = v, Tag = "v" + v, ExeUrl = exe, ShaUrl = sha };
            }
            catch { LastCheckFailed = true; return null; }
        });

        /// <summary>Downloads, verifies and swaps in the new launcher, then starts it. Throws with a readable message on failure.</summary>
        internal static void Apply(Release r)
        {
            string expected = Regex.Match(Get(r.ShaUrl), "[0-9a-fA-F]{64}").Value.ToLowerInvariant();
            if (expected.Length != 64) throw new InvalidOperationException("la actualización no trae una huella válida");
            string exe = ExePath;
            string tmp = exe + ".new";
            try { Download(r.ExeUrl, tmp); }
            catch { try { File.Delete(tmp); } catch { } throw; }
            string actual;
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(tmp))
                actual = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
            if (actual != expected)
            {
                try { File.Delete(tmp); } catch { }
                throw new InvalidOperationException("el archivo descargado no coincide con la huella (descarga dañada)");
            }
            string old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe); throw; }
            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        }

        private static HttpWebRequest Request(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Accept = url.Contains("api.github.com") ? "application/vnd.github+json" : "*/*";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;
            return req;
        }

        private static string Get(string url)
        {
            using (var resp = Request(url).GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream()))
                return r.ReadToEnd();
        }

        private static void Download(string url, string path)
        {
            using (var resp = Request(url).GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(path))
                src.CopyTo(dst);
        }
    }
}
