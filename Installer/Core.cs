using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace GMSCoopInstaller
{
    /// <summary>Finds the game and installs / updates / removes BepInEx + GMSCoop.dll. Writes only inside the game folder.</summary>
    internal static class GameInstall
    {
        internal const string AppId = "451340";
        internal const string GameExe = "GoldMiningSimulator.exe";
        private const string GameProcess = "GoldMiningSimulator";
        private const string DefaultInstallDir = "Gold Rush The Game";
        internal const string PluginFile = "GMSCoop.dll";
        private const string ConfigFile = "gmscoop.multiplayer.cfg";
        // Written into BepInEx\ when this launcher installed BepInEx itself: uninstall removes BepInEx only then.
        private const string MarkerFile = "gmscoop_installed_bepinex.txt";
        private static readonly string[] RootFiles = { "winhttp.dll", "doorstop_config.ini", ".doorstop_version" };

        internal static string VersionText
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "" : v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        internal static bool GameRunning()
        {
            Process[] ps = Process.GetProcessesByName(GameProcess);
            foreach (Process p in ps) p.Dispose();
            return ps.Length > 0;
        }

        internal static bool SteamRunning()
        {
            Process[] ps = Process.GetProcessesByName("steam");
            foreach (Process p in ps) p.Dispose();
            return ps.Length > 0;
        }

        internal static bool IsGameDir(string dir) => dir != null && File.Exists(Path.Combine(dir, GameExe));

        internal static bool IsInstalled(string game) => game != null && File.Exists(Path.Combine(game, "BepInEx", "plugins", PluginFile));

        internal static bool HasBepInEx(string game) => game != null && File.Exists(Path.Combine(game, "BepInEx", "core", "BepInEx.dll"));

        /// <summary>True if the installed GMSCoop.dll is byte-for-byte the one inside this launcher.</summary>
        internal static bool IsUpToDate(string game)
        {
            string ours = Path.Combine(game, "BepInEx", "plugins", PluginFile);
            if (!File.Exists(ours) || !File.Exists(Path.Combine(game, "winhttp.dll"))) return false;
            // A BepInEx we installed must be complete: a first install that stopped halfway is repaired.
            if (File.Exists(Path.Combine(game, "BepInEx", MarkerFile)))
                foreach (string rel in PayloadLoaderFiles())
                    if (!File.Exists(Path.Combine(game, rel.Replace('/', Path.DirectorySeparatorChar)))) return false;
            byte[] payload = PayloadPluginHash();
            if (payload == null) return true; // a launcher without payload cannot do better
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(ours))
                return sha.ComputeHash(f).SequenceEqual(payload);
        }

        private static byte[] _payloadHash;
        private static List<string> _loaderFiles;

        /// <summary>The payload's BepInEx files (everything except our plugin folder), as relative paths.</summary>
        private static List<string> PayloadLoaderFiles()
        {
            if (_loaderFiles != null) return _loaderFiles;
            var list = new List<string>();
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            {
                if (s != null)
                    using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
                        foreach (ZipArchiveEntry e in zip.Entries)
                        {
                            string name = e.FullName.Replace('\\', '/').TrimStart('/');
                            if (name.Length == 0 || name.EndsWith("/") || name.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase)) continue;
                            list.Add(name);
                        }
            }
            return _loaderFiles = list;
        }

        private static byte[] PayloadPluginHash()
        {
            if (_payloadHash != null) return _payloadHash;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            {
                if (s == null) return null;
                using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry e = zip.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/').EndsWith("plugins/" + PluginFile, StringComparison.OrdinalIgnoreCase));
                    if (e == null) return null;
                    using (var es = e.Open())
                    using (var sha = SHA256.Create())
                        _payloadHash = sha.ComputeHash(es);
                }
            }
            return _payloadHash;
        }

        /// <summary>The game starts directly (not through Steam) only with this file next to the exe; otherwise Steam restarts it and drops the arguments.</summary>
        internal static void EnsureSteamAppId(string game)
        {
            string f = Path.Combine(game, "steam_appid.txt");
            try
            {
                if (!File.Exists(f) || File.ReadAllText(f).Trim() != AppId) File.WriteAllText(f, AppId);
            }
            catch { }
        }

        private static string Inside(string game, string relative)
        {
            string root = Path.GetFullPath(game).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, relative));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        private static void MakeWritable(string file)
        {
            if (!File.Exists(file)) return;
            FileAttributes a = File.GetAttributes(file);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, a & ~FileAttributes.ReadOnly);
        }

        private static void DeleteFile(string file)
        {
            if (!File.Exists(file)) return;
            MakeWritable(file);
            File.Delete(file);
        }

        private static void DeleteTree(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) MakeWritable(f);
            Directory.Delete(dir, recursive: true);
        }

        /// <summary>Installs or updates. Returns a short Spanish summary. Throws with a Spanish message.</summary>
        internal static string Install(string game)
        {
            string bep = Path.Combine(game, "BepInEx");
            string core = Path.Combine(bep, "core");
            if (File.Exists(Path.Combine(core, "BepInEx.Core.dll")) || File.Exists(Path.Combine(core, "BepInEx.Unity.Mono.dll")))
                throw new InvalidOperationException("En la carpeta del juego hay BepInEx 6, y este mod necesita BepInEx 5. Quita BepInEx 6 y vuelve a intentarlo.");
            bool hadBepInEx = File.Exists(Path.Combine(core, "BepInEx.dll"));
            bool hadLoader = File.Exists(Path.Combine(game, "winhttp.dll"));
            string marker = Path.Combine(bep, MarkerFile);
            // Our own BepInEx is always rewritten whole, so a first install that stopped halfway gets repaired.
            bool ourBepInEx = !hadBepInEx || File.Exists(marker);
            if (ourBepInEx)
            {
                Directory.CreateDirectory(bep);
                File.WriteAllText(marker,
                    "BepInEx fue instalado por GMS Co-op. Al desinstalar el mod, BepInEx se quita solo si no quedan otros mods." + Environment.NewLine);
            }

            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
            if (s == null) throw new InvalidOperationException("El launcher está incompleto (falta payload.zip).");

            // An older copy of the mod in a sub folder of plugins would be loaded too: only ours stays.
            string plugins = Path.Combine(bep, "plugins");
            string ours = Path.Combine(plugins, PluginFile);
            if (Directory.Exists(plugins))
                foreach (string f in Directory.GetFiles(plugins, PluginFile, SearchOption.AllDirectories))
                    if (!string.Equals(Path.GetFullPath(f), Path.GetFullPath(ours), StringComparison.OrdinalIgnoreCase))
                        DeleteFile(f);

            int written = 0;
            using (s)
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    string name = e.FullName.Replace('\\', '/').TrimStart('/');
                    if (name.Length == 0 || name.EndsWith("/")) continue;
                    bool isPlugin = name.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase);
                    bool isRoot = name.IndexOf('/') < 0;
                    // Keep an existing BepInEx (and its other mods) as it is; only a missing loader is put back.
                    if (!isPlugin && !ourBepInEx && (!isRoot || hadLoader)) continue;
                    string target = Inside(game, name.Replace('/', Path.DirectorySeparatorChar));
                    if (target == null) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    MakeWritable(target);
                    e.ExtractToFile(target, overwrite: true);
                    written++;
                }
            }
            if (!File.Exists(ours)) throw new InvalidOperationException("El launcher está incompleto (falta " + PluginFile + ").");
            EnsureSteamAppId(game);
            return "Mod " + VersionText + " instalado (" + written + " archivos" + (!ourBepInEx ? ", tu BepInEx se queda" : "") + ").";
        }

        internal static string Uninstall(string game)
        {
            string bep = Path.Combine(game, "BepInEx");
            string plugins = Path.Combine(bep, "plugins");
            string patchers = Path.Combine(bep, "patchers");
            bool hadMod = false;
            if (Directory.Exists(plugins))
                foreach (string f in Directory.GetFiles(plugins, PluginFile, SearchOption.AllDirectories)) { DeleteFile(f); hadMod = true; }
            DeleteTree(Path.Combine(plugins, "GMSCoop_avatars"));
            DeleteFile(Path.Combine(bep, "config", ConfigFile));
            bool ourBepInEx = File.Exists(Path.Combine(bep, MarkerFile));
            bool otherMods = (Directory.Exists(plugins) && Directory.EnumerateFiles(plugins, "*.dll", SearchOption.AllDirectories).Any()) ||
                             (Directory.Exists(patchers) && Directory.EnumerateFiles(patchers, "*.dll", SearchOption.AllDirectories).Any());
            bool removeBepInEx = ourBepInEx && !otherMods;
            if (removeBepInEx)
            {
                DeleteTree(bep);
                foreach (string f in RootFiles) DeleteFile(Path.Combine(game, f));
                DeleteFile(Path.Combine(game, "steam_appid.txt"));
            }
            string head = hadMod ? "Mod quitado." : "El mod no estaba instalado.";
            return removeBepInEx ? (hadMod ? "Mod y BepInEx quitados: el juego quedó como antes." : head + " BepInEx quitado.")
                 : otherMods ? head + " BepInEx se queda porque tienes otros mods."
                 : head + " BepInEx se queda porque ya lo tenías antes.";
        }

        // ---------------- find the game and Steam ----------------

        internal static string FindGame()
        {
            foreach (string lib in SteamLibraries())
            {
                try
                {
                    string apps = Path.Combine(lib, "steamapps");
                    string acf = Path.Combine(apps, "appmanifest_" + AppId + ".acf");
                    if (File.Exists(acf))
                    {
                        Match m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            string dir = Path.Combine(apps, "common", m.Groups[1].Value.Replace(@"\\", @"\"));
                            if (IsGameDir(dir)) return dir;
                        }
                    }
                    string def = Path.Combine(apps, "common", DefaultInstallDir);
                    if (IsGameDir(def)) return def;
                }
                catch { }
            }
            return null;
        }

        internal static List<string> SteamRoots()
        {
            var roots = new List<string>();
            foreach (var (hive, key) in new[]
            {
                (Registry.CurrentUser, @"Software\Valve\Steam"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                (Registry.LocalMachine, @"SOFTWARE\Valve\Steam"),
            })
            {
                try
                {
                    using (RegistryKey k = hive.OpenSubKey(key))
                    {
                        if (k?.GetValue("SteamPath") is string sp && sp.Length > 0) roots.Add(sp);
                        if (k?.GetValue("InstallPath") is string ip && ip.Length > 0) roots.Add(ip);
                    }
                }
                catch { }
            }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
            return roots.Select(x => x.Replace('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var libs = new List<string>();
            foreach (string r in SteamRoots())
            {
                try
                {
                    libs.Add(r);
                    foreach (string vdf in new[] { Path.Combine(r, "steamapps", "libraryfolders.vdf"), Path.Combine(r, "config", "libraryfolders.vdf") })
                    {
                        if (!File.Exists(vdf)) continue;
                        string text = File.ReadAllText(vdf);
                        foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
                            libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                        foreach (Match m in Regex.Matches(text, "\"\\d+\"\\s+\"([A-Za-z]:[^\"]*)\""))
                            libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                    }
                }
                catch { }
            }
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        string p = Path.Combine(d.RootDirectory.FullName, "SteamLibrary");
                        if (Directory.Exists(p)) libs.Add(p);
                    }
                    catch { }
                }
            }
            catch { }
            return libs.Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>The game's art from the user's own Steam library cache (never shipped with the launcher).</summary>
    internal static class SteamArt
    {
        internal static string Hero => Find("library_hero.jpg", "_library_hero.jpg");
        internal static string Logo => Find("logo.png", "_logo.png");
        internal static string Header => Find("library_header.jpg", "_header.jpg");

        private static string Find(string newName, string oldSuffix)
        {
            try
            {
                foreach (string root in GameInstall.SteamRoots())
                {
                    string cache = Path.Combine(root, "appcache", "librarycache");
                    string old = Path.Combine(cache, GameInstall.AppId + oldSuffix);
                    if (File.Exists(old)) return old;
                    string dir = Path.Combine(cache, GameInstall.AppId);
                    if (!Directory.Exists(dir)) continue;
                    string hit = Directory.GetFiles(dir, newName, SearchOption.AllDirectories)
                                          .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }
    }

    internal sealed class SaveSlot
    {
        public string Name;
        public DateTime When;
        public string Screenshot;
        public string Cash;
        public string Days;
        // Co-op world identity (gmscoop_world.json in the slot folder), null for a plain save.
        public string WorldId;
        public int Revision;
        public DateTime LastSavedUtc;
        public string LastSavedBy;
        public bool IsBackup => Name != null && Name.StartsWith("Coop_Respaldo_", StringComparison.OrdinalIgnoreCase);

        /// <summary>"respaldo co-op · mundo co-op · rev N · último: nombre fecha", or null for a plain save.</summary>
        public string CoopLine
        {
            get
            {
                if (WorldId == null && !IsBackup) return null;
                string s = IsBackup ? "respaldo co-op" : "mundo co-op";
                if (WorldId != null)
                {
                    s += "  ·  rev " + Revision;
                    if (!string.IsNullOrEmpty(LastSavedBy)) s += "  ·  último: " + LastSavedBy;
                    if (LastSavedUtc != default) s += " " + LastSavedUtc.ToLocalTime().ToString("dd/MM HH:mm");
                }
                return s;
            }
        }
    }

    internal static class SaveSlots
    {
        internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                                      "AppData", "LocalLow", "CodeHorizon", "GoldMiningSimulator", "Saves");

        /// <summary>Save slots, newest first (Coop_Host, the copy a joining player receives, is skipped).</summary>
        internal static List<SaveSlot> List()
        {
            var list = new List<SaveSlot>();
            try
            {
                if (!Directory.Exists(Folder)) return list;
                foreach (string dir in Directory.GetDirectories(Folder))
                {
                    string name = Path.GetFileName(dir);
                    if (name.Equals("Coop_Host", StringComparison.OrdinalIgnoreCase)) continue;
                    string data = Path.Combine(dir, "CheckpointData.sav");
                    if (!File.Exists(data)) continue;
                    var slot = new SaveSlot { Name = name, When = File.GetLastWriteTime(data) };
                    string shot = Directory.GetFiles(dir).FirstOrDefault(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                    slot.Screenshot = shot;
                    try
                    {
                        string desc = Path.Combine(dir, "SmallDescription.txt");
                        if (File.Exists(desc))
                        {
                            string x = File.ReadAllText(desc);
                            Match c = Regex.Match(x, @"<Cash>\s*<Value>([^<]+)</Value>");
                            Match d = Regex.Match(x, @"<DayCount>([^<]+)</DayCount>");
                            if (c.Success) slot.Cash = c.Groups[1].Value.Trim();
                            if (d.Success) slot.Days = d.Groups[1].Value.Trim();
                        }
                    }
                    catch { }
                    try
                    {
                        string idFile = Path.Combine(dir, "gmscoop_world.json");
                        if (File.Exists(idFile))
                        {
                            string j = File.ReadAllText(idFile);
                            Match w = Regex.Match(j, @"""worldId""\s*:\s*""([^""]+)""");
                            if (w.Success)
                            {
                                slot.WorldId = w.Groups[1].Value;
                                Match r = Regex.Match(j, @"""revision""\s*:\s*([0-9]+)");
                                if (r.Success) int.TryParse(r.Groups[1].Value, out slot.Revision);
                                Match by = Regex.Match(j, @"""lastSavedBy""\s*:\s*""((?:[^""\\]|\\.)*)""");
                                if (by.Success) slot.LastSavedBy = Regex.Unescape(by.Groups[1].Value);
                                Match t = Regex.Match(j, @"""lastSaved""\s*:\s*""([^""]+)""");
                                if (t.Success && DateTime.TryParse(t.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime u))
                                    slot.LastSavedUtc = u;
                                // A json older than the save next to it belongs to another world that used this slot.
                                if (!slot.IsBackup && slot.LastSavedUtc != default && File.GetLastWriteTimeUtc(data) > slot.LastSavedUtc.AddMinutes(3))
                                {
                                    slot.WorldId = null; slot.Revision = 0; slot.LastSavedBy = null; slot.LastSavedUtc = default;
                                }
                            }
                        }
                    }
                    catch { }
                    list.Add(slot);
                }
            }
            catch { }
            return list.OrderByDescending(s => s.When).ToList();
        }

        /// <summary>A newer copy (higher revision) of the same co-op world as <paramref name="slot"/>, or null.</summary>
        internal static SaveSlot NewerCopyOf(SaveSlot slot, List<SaveSlot> all)
        {
            if (slot?.WorldId == null) return null;
            return all.Where(x => x.Name != slot.Name && x.WorldId == slot.WorldId && x.Revision > slot.Revision)
                      .OrderByDescending(x => x.Revision).FirstOrDefault();
        }
    }

    /// <summary>Remembers the last choices in %APPDATA%\GMSCoop\launcher.ini (key=value).</summary>
    internal static class Settings
    {
        private static readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        private static string FilePath => Path.Combine(Cloudflare.Folder, "launcher.ini");

        internal static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) Values[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch { }
        }

        internal static string Get(string key, string def = "") => Values.TryGetValue(key, out string v) ? v : def;

        internal static void Set(string key, string value)
        {
            Values[key] = (value ?? "").Replace("\r", "").Replace("\n", " ");
            try
            {
                Directory.CreateDirectory(Cloudflare.Folder);
                File.WriteAllLines(FilePath, Values.Select(kv => kv.Key + "=" + kv.Value));
            }
            catch { }
        }
    }

    /// <summary>A LAN game announced by the mod's beacon (LanDiscovery in the mod, UDP 27778).</summary>
    internal sealed class LanHost
    {
        public string Ip;
        public int Port;
        public string Name;
        public string Version;
        public int Players;
        public DateTime Seen;
        public long PingMs = -1;
        public string Key => Ip + ":" + Port;
    }

    internal sealed class LanBrowser : IDisposable
    {
        private const int Port = 27778;
        private volatile bool _run = true;
        private readonly Dictionary<string, LanHost> _hosts = new Dictionary<string, LanHost>();

        internal LanBrowser()
        {
            new Thread(Listen) { IsBackground = true, Name = "LAN browser" }.Start();
            new Thread(PingLoop) { IsBackground = true, Name = "LAN ping" }.Start();
        }

        internal List<LanHost> Snapshot()
        {
            lock (_hosts)
            {
                DateTime old = DateTime.UtcNow.AddSeconds(-4);
                foreach (string k in _hosts.Where(kv => kv.Value.Seen < old).Select(kv => kv.Key).ToList()) _hosts.Remove(k);
                return _hosts.Values.OrderBy(h => h.Name).Select(h => (LanHost)h.MemberwiseCloneHost()).ToList();
            }
        }

        private void Listen()
        {
            while (_run)
            {
                UdpClient udp = null;
                try
                {
                    udp = new UdpClient();
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                    udp.Client.ReceiveTimeout = 500;
                    while (_run)
                    {
                        IPEndPoint from = null;
                        byte[] data;
                        try { data = udp.Receive(ref from); }
                        catch (SocketException) { continue; }
                        string[] p;
                        try { p = Encoding.UTF8.GetString(data).Split('|'); } catch { continue; }
                        if (p.Length < 7 || p[0] != "GMSCOOP1" || !int.TryParse(p[5], out int port) || p[6] != "1") continue;
                        int.TryParse(p[4], out int players);
                        var h = new LanHost { Ip = from.Address.ToString(), Port = port, Version = p[2], Name = p[3], Players = players, Seen = DateTime.UtcNow };
                        lock (_hosts)
                        {
                            // The same host heard on 127.0.0.1 and on its LAN address: keep one entry, prefer the LAN address.
                            if (h.Ip == "127.0.0.1" && _hosts.Values.Any(x => x.Name == h.Name && x.Port == h.Port && x.Ip != "127.0.0.1")) continue;
                            foreach (string k in _hosts.Where(kv => kv.Value.Ip == "127.0.0.1" && kv.Value.Name == h.Name && h.Ip != "127.0.0.1").Select(kv => kv.Key).ToList())
                                _hosts.Remove(k);
                            if (_hosts.TryGetValue(h.Key, out LanHost old)) h.PingMs = old.PingMs;
                            _hosts[h.Key] = h;
                        }
                    }
                }
                catch { Thread.Sleep(2000); } // port busy or no network: try again
                finally { try { udp?.Close(); } catch { } }
            }
        }

        private void PingLoop()
        {
            using (var ping = new Ping())
            {
                while (_run)
                {
                    List<LanHost> hosts;
                    lock (_hosts) hosts = _hosts.Values.ToList();
                    foreach (LanHost h in hosts)
                    {
                        long ms = -1;
                        try
                        {
                            PingReply r = ping.Send(h.Ip, 800);
                            if (r != null && r.Status == IPStatus.Success) ms = r.RoundtripTime;
                        }
                        catch { }
                        lock (_hosts) if (_hosts.TryGetValue(h.Key, out LanHost cur)) cur.PingMs = ms;
                    }
                    Thread.Sleep(2000);
                }
            }
        }

        public void Dispose() => _run = false;
    }

    internal static class LanHostExt
    {
        internal static LanHost MemberwiseCloneHost(this LanHost h) => new LanHost
        {
            Ip = h.Ip, Port = h.Port, Name = h.Name, Version = h.Version, Players = h.Players, Seen = h.Seen, PingMs = h.PingMs,
        };
    }

    /// <summary>What the player typed in "Código de partida o IP".</summary>
    internal sealed class JoinTarget
    {
        public enum Kind { Auto, SteamLobby, Cloudflare, Address, Invalid }
        public Kind Type;
        public string Value;

        internal static JoinTarget Parse(string text)
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0) return new JoinTarget { Type = Kind.Auto };
            string digits = t.Replace(" ", "");
            if (digits.Length >= 15 && digits.Length <= 20 && digits.All(char.IsDigit))
                return new JoinTarget { Type = Kind.SteamLobby, Value = digits };
            string sub = Cloudflare.ParseCode(t);
            if (sub != null) return new JoinTarget { Type = Kind.Cloudflare, Value = sub };
            if (Regex.IsMatch(t, @"^[A-Za-z0-9.\-]+(:\d{1,5})?$") || Regex.IsMatch(t, @"^\[[0-9a-fA-F:]+\](:\d{1,5})?$"))
                return new JoinTarget { Type = Kind.Address, Value = t };
            return new JoinTarget { Type = Kind.Invalid, Value = t };
        }
    }
}
