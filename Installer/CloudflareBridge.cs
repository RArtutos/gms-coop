using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GMSCoopInstaller
{
    /// <summary>
    /// Internet play through a free Cloudflare quick tunnel: works behind any router or CGNAT, with no
    /// account and no open ports.
    ///
    ///   host PC:   game (LAN host, TCP 27777) ← HostBridge (WebSocket server on 127.0.0.1) ← cloudflared
    ///              ← https://xxxx.trycloudflare.com
    ///   friend PC: game (--coop-join 127.0.0.1:p) → ClientBridge (TCP listener) → wss://xxxx.trycloudflare.com
    ///
    /// The WebSocket link carries the game's TCP byte stream in BINARY frames (message boundaries do not
    /// matter: it is a stream). TEXT frames carry the bridges' own side-channel: the tunnel ping and the
    /// resume protocol (see Resume), which keeps the game's connection open across a WebSocket cut.
    /// </summary>
    internal static class Cloudflare
    {
        internal const string CodePrefix = "CF-";
        private const string DownloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

        internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GMSCoop");
        internal static string ExePath => Path.Combine(Folder, "cloudflared.exe");

        /// <summary>"CF-sub" or a trycloudflare URL → the subdomain; null if the text is neither.</summary>
        internal static string ParseCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = text.Trim();
            Match m = Regex.Match(t, @"^(?:https?://|wss?://)?([a-z0-9-]+)\.trycloudflare\.com/?$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
            if (t.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string sub = t.Substring(CodePrefix.Length).Trim().ToLowerInvariant();
                if (Regex.IsMatch(sub, @"^[a-z0-9]+(-[a-z0-9]+)*$")) return sub;
            }
            return null;
        }

        /// <summary>Downloads cloudflared.exe if missing (or broken) and checks that it runs. Throws with a Spanish message.</summary>
        internal static void EnsureCloudflared(Action<string> progress)
        {
            if (File.Exists(ExePath) && Runs(ExePath)) return;
            Directory.CreateDirectory(Folder);
            string tmp = ExePath + ".download";
            progress?.Invoke("Descargando cloudflared (de Cloudflare, ~50 MB, solo la primera vez)...");
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var wc = new WebClient())
                {
                    long lastShown = -1;
                    wc.DownloadProgressChanged += (s, e) =>
                    {
                        long mb = e.BytesReceived / (1024 * 1024);
                        if (mb != lastShown) { lastShown = mb; progress?.Invoke($"Descargando cloudflared... {mb} MB"); }
                    };
                    var done = new ManualResetEventSlim();
                    Exception err = null;
                    wc.DownloadFileCompleted += (s, e) => { err = e.Error; done.Set(); };
                    wc.DownloadFileAsync(new Uri(DownloadUrl), tmp);
                    if (!done.Wait(TimeSpan.FromMinutes(10))) { wc.CancelAsync(); throw new TimeoutException("la descarga tardó demasiado"); }
                    if (err != null) throw err;
                }
                if (File.Exists(ExePath)) File.Delete(ExePath);
                File.Move(tmp, ExePath);
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                throw new InvalidOperationException("No se pudo descargar cloudflared: " + ex.Message +
                                                    "\n\nRevisa tu internet o el antivirus, o usa Steam.");
            }
            if (!Runs(ExePath))
                throw new InvalidOperationException("cloudflared se descargó pero no arranca (¿lo bloqueó el antivirus?).");
        }

        private static bool Runs(string exe)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, "--version")
                       { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    if (p == null) return false;
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return false; }
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // ---------- WebSocket helpers ----------

        /// <summary>
        /// TCP port the game hosts on: LAN.Port of the mod's BepInEx config (BepInEx\config\gmscoop.multiplayer.cfg),
        /// 27777 if the file or the setting is missing.
        /// </summary>
        internal static int GamePort(string game)
        {
            const int def = 27777;
            try
            {
                string cfg = Path.Combine(game ?? "", "BepInEx", "config", "gmscoop.multiplayer.cfg");
                if (!File.Exists(cfg)) return def;
                bool inLan = false;
                foreach (string raw in File.ReadAllLines(cfg))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[")) { inLan = line.Equals("[LAN]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (!inLan || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || !line.Substring(0, eq).Trim().Equals("Port", StringComparison.OrdinalIgnoreCase)) continue;
                    return int.TryParse(line.Substring(eq + 1).Trim(), out int p) && p > 0 && p <= 65535 ? p : def;
                }
            }
            catch { }
            return def;
        }

        /// <summary>Throws (Spanish message) if another program already listens on the game's port.</summary>
        internal static void CheckPortFree(int port)
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
                l.Start();
                l.Stop();
            }
            catch (SocketException)
            {
                throw new InvalidOperationException("El puerto " + port + " ya lo está usando otro programa, así que el juego no podría crear la partida." +
                                                    "\n\nCierra ese programa (o la otra copia del juego) y vuelve a intentarlo. También puedes cambiar el puerto en " +
                                                    "BepInEx\\config\\gmscoop.multiplayer.cfg, sección [LAN], línea Port.");
            }
        }

        internal static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }

    /// <summary>
    /// Host side: a minimal WebSocket server on 127.0.0.1 (RFC 6455, hand-rolled on TcpListener so no
    /// URL reservation or admin rights are needed), exposed by cloudflared. Each stream of a friend is piped
    /// to the game's LAN host on 127.0.0.1:&lt;gamePort&gt;; a resumable stream (Resume) survives its WebSocket.
    /// </summary>
    internal sealed class HostBridge : IDisposable
    {
        private readonly int _gamePort;
        private readonly Action<string> _log;
        private TcpListener _listener;
        private Process _cloudflared;
        private volatile bool _closed;
        private readonly List<TcpClient> _open = new List<TcpClient>();

        internal int Port { get; private set; }
        internal string Subdomain { get; private set; }
        internal int Connections;

        internal HostBridge(int gamePort, Action<string> log)
        {
            _gamePort = gamePort;
            _log = log;
        }

        internal string Location { get; private set; }  // Cloudflare edge the tunnel registered at, e.g. "qro01"
        internal string Protocol { get; private set; }  // "quic" or "http2"

        /// <summary>
        /// Starts the WebSocket server and the tunnel; returns the trycloudflare subdomain. Throws on failure.
        /// QUIC first (lower latency, no head-of-line blocking between the game's connections); if it does
        /// not register with an edge within 10 s (some networks block UDP), again over HTTP/2.
        /// </summary>
        internal string Start(TimeSpan timeout)
        {
            StartLocal();
            DateTime deadline = DateTime.UtcNow + timeout;
            string error = null;
            foreach (string protocol in new[] { "quic", "http2" })
            {
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left < TimeSpan.FromSeconds(5)) left = TimeSpan.FromSeconds(5);
                TimeSpan budget = protocol == "quic" ? TimeSpan.FromSeconds(Math.Min(15, left.TotalSeconds)) : left;
                string sub = TryTunnel(protocol, budget, out error);
                if (sub != null)
                {
                    Subdomain = sub;
                    Protocol = protocol;
                    _log("Túnel de Cloudflare listo" + (Location != null ? " (" + EdgeName(Location) + ", " + protocol.ToUpperInvariant() + ")" : "") + ".");
                    return sub;
                }
                _log(protocol == "quic" ? "Cloudflare por QUIC no respondió; probando por HTTP/2..." : "Cloudflare por HTTP/2 tampoco respondió.");
                KillTunnel();
            }
            Dispose();
            throw new InvalidOperationException("El túnel de Cloudflare no arrancó en " + (int)timeout.TotalSeconds + " s." +
                                                (string.IsNullOrEmpty(error) ? "" : "\n\nÚltimo mensaje de cloudflared: " + error) +
                                                "\n\nRevisa tu internet, o usa Steam.");
        }

        /// <summary>The WebSocket server alone, without the tunnel (Start; also the offline test harness).</summary>
        internal void StartLocal()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            new Thread(AcceptLoop) { IsBackground = true, Name = "WS accept" }.Start();
            new Thread(Janitor) { IsBackground = true, Name = "WS sessions" }.Start();
        }

        /// <summary>"qro01" -> "Querétaro (qro)" for the codes near Mexico, else the code.</summary>
        internal static string EdgeName(string loc)
        {
            string code = new string(loc.TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
            switch (code)
            {
                case "qro": return "Querétaro (qro)";
                case "mex": return "Ciudad de México (mex)";
                case "gdl": return "Guadalajara (gdl)";
                case "mty": return "Monterrey (mty)";
                case "dfw": return "Dallas (dfw)";
                case "iah": return "Houston (iah)";
                case "lax": return "Los Ángeles (lax)";
                case "mfe": return "McAllen (mfe)";
                default: return code;
            }
        }

        /// <summary>One cloudflared run: the trycloudflare subdomain once an edge connection registered, or null.</summary>
        private string TryTunnel(string protocol, TimeSpan budget, out string lastLine)
        {
            var psi = new ProcessStartInfo(Cloudflare.ExePath, $"tunnel --no-autoupdate --protocol {protocol} --url http://127.0.0.1:{Port}")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            string sub = null, last = "";
            bool registered = false;
            var done = new ManualResetEventSlim();
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler onLine = (s, e) =>
            {
                if (e.Data == null) return;
                last = e.Data.Trim();
                CloudflareLog.Add(e.Data); // for "Enviar reporte" (MainWindow.Report.cs)
                Match m = Regex.Match(e.Data, @"https://([a-z0-9-]+)\.trycloudflare\.com", RegexOptions.IgnoreCase);
                if (m.Success && sub == null && !m.Groups[1].Value.Equals("api", StringComparison.OrdinalIgnoreCase))
                    sub = m.Groups[1].Value.ToLowerInvariant();
                // "Registered tunnel connection connIndex=0 ... location=qro01 protocol=quic"
                if (e.Data.IndexOf("Registered tunnel connection", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Match loc = Regex.Match(e.Data, @"location=([A-Za-z0-9]+)");
                    if (loc.Success && Location == null) Location = loc.Groups[1].Value;
                    registered = true;
                }
                if (sub != null && registered) done.Set();
            };
            proc.OutputDataReceived += onLine;
            proc.ErrorDataReceived += onLine;
            proc.Exited += (s, e) => { if (!_closed && ReferenceEquals(_cloudflared, proc) && done.IsSet) _log("El túnel de Cloudflare se cerró."); done.Set(); };
            _cloudflared = proc;
            proc.Start();
            ChildProcesses.Track(proc);
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            done.Wait(budget);
            lastLine = last;
            return sub != null && registered && !proc.HasExited ? sub : null;
        }

        private void KillTunnel()
        {
            try { if (_cloudflared != null && !_cloudflared.HasExited) _cloudflared.Kill(); } catch { }
            _cloudflared = null;
            Location = null;
        }

        private void AcceptLoop()
        {
            while (!_closed)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { if (_closed) return; continue; }
                lock (_open) _open.Add(c);
                new Thread(() => Serve(c)) { IsBackground = true, Name = "WS serve" }.Start();
            }
        }

        /// <summary>One WebSocket connection of a friend (several over time for one resumable session).</summary>
        private sealed class WsConn
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object SendLock = new object();
            public long LastRecvTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>
        /// A resumable stream: one game connection that outlives its WebSocket. Bytes to the friend stay in Out
        /// until the friend's bridge acknowledges them, so a new WebSocket ("resume") continues where the old
        /// one stopped and the game never notices the cut.
        /// </summary>
        private sealed class Session
        {
            public string Id;
            public readonly object Lock = new object();   // the current WebSocket, and game -> friend writes
            public readonly object InLock = new object(); // friend -> game writes and RecvFromClient
            public WsConn Ws;
            public TcpClient Game;
            public NetworkStream GameStream;
            public long RecvFromClient, AckedToClient;
            public DateTime AckAt = DateTime.UtcNow;
            public readonly ReplayBuffer Out = new ReplayBuffer();
            public DateTime DetachedAt = DateTime.UtcNow;
            public volatile bool Ended;
            public bool Counted;
        }

        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>();

        private void Serve(TcpClient ws)
        {
            TcpClient game = null;
            bool counted = false;
            Session session = null;
            WsConn me = null;
            try
            {
                ws.NoDelay = true;
                ws.SendBufferSize = ws.ReceiveBufferSize = 1 << 20; // the world transfer: keep cloudflared fed
                NetworkStream wsStream = ws.GetStream();
                string request = ReadHeaders(wsStream);
                Match key = Regex.Match(request ?? "", @"^Sec-WebSocket-Key:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                bool upgrade = request != null && Regex.IsMatch(request, @"^Upgrade:\s*websocket", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (!upgrade || !key.Success)
                {
                    // A browser or a health check: say what this is and close.
                    byte[] page = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: close\r\nContent-Length: 30\r\n\r\nGMS Co-op: partida en marcha. ");
                    wsStream.Write(page, 0, page.Length);
                    return;
                }
                string accept;
                using (var sha = SHA1.Create())
                    accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key.Groups[1].Value + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                byte[] resp = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                wsStream.Write(resp, 0, resp.Length);

                // Connect to the game only when the first game bytes arrive (the joining game always speaks
                // first: its Hello). A connection check (ClientBridge.Probe) thus never reaches the game.
                // A friend's bridge that speaks first with "gmsr1 hello/resume" gets a resumable session; an
                // older one (only binary frames) the plain pipe of one WebSocket = one game connection.
                me = new WsConn { Client = ws, Stream = wsStream };
                object sendLock = me.SendLock;
                NetworkStream gameStream = null;
                bool inText = false;
                while (!_closed)
                {
                    WsFrames.Frame f = WsFrames.Read(wsStream);
                    if (f == null || f.Opcode == 0x8) break;
                    Interlocked.Exchange(ref me.LastRecvTicks, DateTime.UtcNow.Ticks);
                    if (f.Opcode == 0x9) { lock (sendLock) WsFrames.Write(wsStream, 0xA, f.Payload, f.Payload.Length, mask: false); continue; }
                    // 0x1 (text): "ping:..." is echoed as "pong:..." (the joining launcher measures the tunnel RTT);
                    // "gmsr1 ..." is the resume side-channel. 0xA (pong): ignored.
                    // A fragmented text message: its continuation frames (opcode 0) are text too, never game bytes.
                    if (f.Opcode == 0x1) inText = !f.Fin;
                    else if (f.Opcode == 0x0 && inText) { inText = !f.Fin; continue; }
                    else if (f.Opcode == 0x2) inText = false;
                    if (f.Opcode == 0x1 && !f.Fin) continue;
                    if (f.Opcode == 0x1 && f.Payload.Length >= 5 && f.Payload[0] == (byte)'p' && f.Payload[1] == (byte)'i')
                    {
                        byte[] pong = (byte[])f.Payload.Clone();
                        pong[1] = (byte)'o';
                        lock (sendLock) WsFrames.Write(wsStream, 0x1, pong, pong.Length, mask: false);
                        continue;
                    }
                    if (f.Opcode == 0x1)
                    {
                        string[] t = Encoding.UTF8.GetString(f.Payload).Split(' ');
                        if (t.Length < 2 || t[0] != Resume.Tag) continue; // reserved signaling: ignored
                        if (session == null && gameStream == null && (t[1] == "hello" || t[1] == "resume") && t.Length >= 4 &&
                            long.TryParse(t[3], out long clientRecv))
                        {
                            session = Attach(me, t[2], clientRecv, create: t[1] == "hello");
                            if (session == null)
                            {
                                lock (sendLock) WsFrames.WriteText(wsStream, Resume.Tag + " gone");
                                break;
                            }
                        }
                        else if (session != null && t[1] == "ack" && t.Length >= 3 && long.TryParse(t[2], out long acked))
                            session.Out.Ack(acked);
                        else if (session != null && t[1] == "end")
                        {
                            EndSession(session, notifyFriend: false);
                            break;
                        }
                        continue;
                    }
                    if ((f.Opcode != 0x2 && f.Opcode != 0x0) || f.Payload.Length == 0) continue;
                    if (session != null)
                    {
                        if (!ToGame(session, me, f.Payload)) break;
                        continue;
                    }
                    if (gameStream == null)
                    {
                        game = new TcpClient { NoDelay = true, SendBufferSize = 1 << 20, ReceiveBufferSize = 1 << 20 };
                        game.Connect(IPAddress.Loopback, _gamePort);
                        gameStream = game.GetStream();
                        Interlocked.Increment(ref Connections);
                        counted = true;
                        _log("Un amigo se conectó por Cloudflare.");
                        NetworkStream gsLocal = gameStream;
                        new Thread(() =>
                        {
                            // game → friend: binary frames (server frames are not masked).
                            var buf = new byte[65536];
                            try
                            {
                                int n;
                                while (!_closed && (n = gsLocal.Read(buf, 0, buf.Length)) > 0)
                                    lock (sendLock) WsFrames.Write(wsStream, 0x2, buf, n, mask: false);
                            }
                            catch { }
                            try { lock (sendLock) WsFrames.Write(wsStream, 0x8, new byte[0], 0, mask: false); } catch { }
                            try { ws.Close(); } catch { }
                        }) { IsBackground = true, Name = "WS up" }.Start();
                    }
                    gameStream.Write(f.Payload, 0, f.Payload.Length);
                }
            }
            catch { }
            finally
            {
                try { game?.Close(); } catch { }
                try { ws.Close(); } catch { }
                lock (_open) _open.Remove(ws);
                if (counted) { Interlocked.Decrement(ref Connections); _log("Un amigo se desconectó de Cloudflare."); }
                if (session != null) Detach(session, me);
            }
        }

        /// <summary>
        /// Puts a WebSocket on a session (new with "hello", existing with "resume"): answers "ok" with what
        /// the game already got from the friend, then re-sends what the friend has not got yet. Null = gone.
        /// </summary>
        private Session Attach(WsConn me, string id, long friendGot, bool create)
        {
            Session s;
            bool existed;
            lock (_sessions)
            {
                if (_closed) return null;
                existed = _sessions.TryGetValue(id, out s);
                if (!existed)
                {
                    if (!create || friendGot != 0) return null; // a session that ended (or expired)
                    s = new Session { Id = id };
                    _sessions[id] = s;
                }
            }
            if (s.Ended) return null;
            WsConn old;
            lock (s.InLock)
                lock (s.Lock)
                {
                    List<ArraySegment<byte>> replay = s.Out.From(friendGot);
                    if (replay == null) { EndLater(s); return null; }
                    s.Out.Ack(friendGot);
                    old = s.Ws;
                    s.Ws = me;
                    lock (me.SendLock)
                    {
                        WsFrames.WriteText(me.Stream, Resume.Tag + " ok " + s.RecvFromClient);
                        foreach (ArraySegment<byte> seg in replay)
                            WsFrames.Write(me.Stream, 0x2, seg.Array, seg.Offset, seg.Count, mask: false);
                    }
                    s.AckedToClient = s.RecvFromClient;
                    s.AckAt = DateTime.UtcNow;
                }
            if (old != null) try { old.Client.Close(); } catch { }
            if (existed && s.Counted) _log("Un amigo se reconectó por Cloudflare.");
            return s;
        }

        private void EndLater(Session s) => ThreadPool.QueueUserWorkItem(_ => EndSession(s, notifyFriend: false));

        /// <summary>Friend → game bytes of the current WebSocket (a replaced one's late frames are dropped). False = end.</summary>
        private bool ToGame(Session s, WsConn me, byte[] payload)
        {
            lock (s.InLock)
            {
                if (s.Ended) return false;
                if (s.Ws != me) return false; // replaced by a newer WebSocket of the same friend
                if (s.Game == null)
                {
                    try
                    {
                        var game = new TcpClient { NoDelay = true, SendBufferSize = 1 << 20, ReceiveBufferSize = 1 << 20 };
                        game.Connect(IPAddress.Loopback, _gamePort);
                        s.Game = game;
                        s.GameStream = game.GetStream();
                    }
                    catch
                    {
                        EndLater(s);
                        return false;
                    }
                    s.Counted = true;
                    Interlocked.Increment(ref Connections);
                    _log("Un amigo se conectó por Cloudflare.");
                    new Thread(() => GamePump(s)) { IsBackground = true, Name = "WS session up" }.Start();
                }
                try { s.GameStream.Write(payload, 0, payload.Length); }
                catch { EndLater(s); return false; }
                Interlocked.Add(ref s.RecvFromClient, payload.Length); // read without the lock by the janitor
                if (s.RecvFromClient - s.AckedToClient >= Resume.AckEvery || (DateTime.UtcNow - s.AckAt).TotalSeconds >= 0.5)
                {
                    s.AckedToClient = s.RecvFromClient;
                    s.AckAt = DateTime.UtcNow;
                    // Best effort (the janitor acknowledges every second too): never block the reader on a write.
                    if (Monitor.TryEnter(me.SendLock))
                    {
                        try { WsFrames.WriteText(me.Stream, Resume.Tag + " ack " + s.RecvFromClient); }
                        catch { return false; }
                        finally { Monitor.Exit(me.SendLock); }
                    }
                }
                return true;
            }
        }

        /// <summary>Game → friend for a session: kept in Out, written to the current WebSocket if there is one.</summary>
        private void GamePump(Session s)
        {
            var buf = new byte[65536];
            try
            {
                int n;
                while (!_closed && !s.Ended && (n = s.GameStream.Read(buf, 0, buf.Length)) > 0)
                {
                    s.Out.WaitForRoom(() => _closed || s.Ended);
                    lock (s.Lock)
                    {
                        s.Out.Append(buf, n);
                        WsConn w = s.Ws;
                        if (w == null) continue; // the friend reconnects: the bytes wait in Out
                        try { lock (w.SendLock) WsFrames.Write(w.Stream, 0x2, buf, n, mask: false); }
                        catch { try { w.Client.Close(); } catch { } } // its reader detaches it
                    }
                }
            }
            catch { }
            EndSession(s, notifyFriend: true); // the game closed this connection
        }

        /// <summary>The WebSocket of a session went away: the session waits for a resume (see Janitor).</summary>
        private void Detach(Session s, WsConn me)
        {
            lock (s.Lock)
            {
                if (s.Ws != me) return;
                s.Ws = null;
                s.DetachedAt = DateTime.UtcNow;
            }
            if (!s.Ended && !_closed && s.Counted) _log("Se cortó la conexión de un amigo por Cloudflare; esperando a que vuelva...");
        }

        private void EndSession(Session s, bool notifyFriend)
        {
            lock (_sessions)
            {
                if (s.Ended) return;
                s.Ended = true;
                _sessions.Remove(s.Id);
            }
            WsConn w;
            lock (s.Lock) { w = s.Ws; s.Ws = null; }
            if (w != null)
            {
                if (notifyFriend)
                    try { lock (w.SendLock) { WsFrames.WriteText(w.Stream, Resume.Tag + " end"); WsFrames.Write(w.Stream, 0x8, new byte[0], 0, mask: false); } } catch { }
                try { w.Client.Close(); } catch { }
            }
            try { s.Game?.Close(); } catch { }
            s.Out.Release();
            if (s.Counted) { Interlocked.Decrement(ref Connections); _log("Un amigo se desconectó de Cloudflare."); }
        }

        /// <summary>
        /// Once a second: acknowledges what each session got (also a keep-alive for the friend's bridge), closes a
        /// WebSocket that went silent (a dead connection after a network change), and ends sessions whose friend
        /// did not come back in time.
        /// </summary>
        private void Janitor()
        {
            while (!_closed)
            {
                Thread.Sleep(1000);
                List<Session> all;
                lock (_sessions) all = _sessions.Values.ToList();
                DateTime now = DateTime.UtcNow;
                foreach (Session s in all)
                {
                    WsConn w;
                    lock (s.Lock) w = s.Ws;
                    if (w == null)
                    {
                        if ((now - s.DetachedAt).TotalSeconds > Resume.HostWaitSeconds) EndSession(s, notifyFriend: false);
                        continue;
                    }
                    if ((now - new DateTime(Interlocked.Read(ref w.LastRecvTicks), DateTimeKind.Utc)).TotalSeconds > Resume.SilenceSeconds)
                    {
                        try { w.Client.Close(); } catch { }
                        continue;
                    }
                    long got = Interlocked.Read(ref s.RecvFromClient);
                    // Busy writing game bytes: skip this keep-alive rather than wait on a slow connection.
                    if (!Monitor.TryEnter(w.SendLock)) continue;
                    try { WsFrames.WriteText(w.Stream, Resume.Tag + " ack " + got); }
                    catch { try { w.Client.Close(); } catch { } }
                    finally { Monitor.Exit(w.SendLock); }
                }
            }
        }

        private static string ReadHeaders(Stream s)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (sb.Length < 16384)
            {
                if (s.Read(one, 0, 1) <= 0) return null;
                sb.Append((char)one[0]);
                if (sb.Length >= 4 && sb[sb.Length - 1] == '\n' && sb[sb.Length - 2] == '\r' && sb[sb.Length - 3] == '\n' && sb[sb.Length - 4] == '\r')
                    return sb.ToString();
            }
            return null;
        }

        internal bool TunnelAlive => _cloudflared != null && !_cloudflared.HasExited;

        public void Dispose()
        {
            _closed = true;
            try { _listener?.Stop(); } catch { }
            lock (_open) { foreach (var c in _open) try { c.Close(); } catch { } _open.Clear(); }
            List<Session> sessions;
            lock (_sessions) sessions = _sessions.Values.ToList();
            foreach (Session se in sessions) EndSession(se, notifyFriend: false);
            try { if (_cloudflared != null && !_cloudflared.HasExited) _cloudflared.Kill(); } catch { }
            _cloudflared = null;
        }
    }

    /// <summary>
    /// Friend side: a TCP listener on 127.0.0.1 for the game; each game connection opens a WebSocket to
    /// wss://&lt;sub&gt;.trycloudflare.com and pipes bytes both ways. No cloudflared needed.
    /// </summary>
    internal sealed class ClientBridge : IDisposable
    {
        private readonly string _sub;
        private readonly Action<string> _log;
        private TcpListener _listener;
        private volatile bool _closed;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        internal int Port { get; private set; }

        internal ClientBridge(string subdomain, Action<string> log)
        {
            _sub = subdomain;
            _log = log;
        }

        /// <summary>Any WebSocket URL instead of the tunnel's (the offline test harness).</summary>
        internal ClientBridge(Uri url, Action<string> log)
        {
            _url = url;
            _log = log;
        }

        private readonly Uri _url;

        internal Uri Url => _url ?? new Uri("wss://" + _sub + ".trycloudflare.com/");

        /// <summary>Checks that the host's tunnel answers (a new tunnel can take a few seconds to be reachable). Throws on failure.</summary>
        internal void Probe(TimeSpan timeout)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            DateTime until = DateTime.UtcNow + timeout;
            Exception last = null;
            while (DateTime.UtcNow < until)
            {
                try
                {
                    using (var ws = new ClientWebSocket())
                    {
                        ws.ConnectAsync(Url, CancellationToken.None).Wait(TimeSpan.FromSeconds(10));
                        if (ws.State == WebSocketState.Open)
                        {
                            RttMs = MeasureRtt(ws);
                            // Close at once: the host's bridge connects to the game only when game bytes arrive.
                            try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe", CancellationToken.None).Wait(2000); } catch { }
                            return;
                        }
                    }
                }
                catch (Exception ex) { last = ex.GetBaseException(); }
                Thread.Sleep(2000);
            }
            throw new InvalidOperationException("No pude conectar con la partida de Cloudflare (" + Url.Host + ")." +
                                                (last != null ? "\n\nDetalle: " + last.Message : "") +
                                                "\n\nRevisa el código. El host debe tener GMS Co-op.exe y el juego abiertos.");
        }

        /// <summary>Round trip through the tunnel to the host's bridge (best of 3 pings), or -1 (older host launcher).</summary>
        internal double RttMs { get; private set; } = -1;

        private static double MeasureRtt(ClientWebSocket ws)
        {
            double best = -1;
            var buf = new byte[64];
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    byte[] ping = Encoding.ASCII.GetBytes("ping:" + i);
                    if (!ws.SendAsync(new ArraySegment<byte>(ping), WebSocketMessageType.Text, true, CancellationToken.None).Wait(2000)) break;
                    var rx = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (!rx.Wait(2000) || rx.Result.MessageType != WebSocketMessageType.Text) break; // host launcher without the echo
                    double ms = sw.Elapsed.TotalMilliseconds;
                    if (best < 0 || ms < best) best = ms;
                }
                catch { break; }
            }
            return best;
        }

        internal void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            new Thread(AcceptLoop) { IsBackground = true, Name = "Bridge accept" }.Start();
        }

        private void AcceptLoop()
        {
            while (!_closed)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { if (_closed) return; continue; }
                Task.Run(() => Pipe(c));
            }
        }

        /// <summary>One game connection piped over the tunnel (one WebSocket at a time; a new one on a cut).</summary>
        private sealed class TunnelStream
        {
            public string Id;
            public TcpClient Game;
            public NetworkStream Gs;
            public ClientWebSocket Ws;                    // current WebSocket, null while reconnecting
            public readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1); // one sender at a time; guards Ws
            public readonly ReplayBuffer Out = new ReplayBuffer();
            public long RecvFromHost, AckSent;
            public long LastRecvTicks = DateTime.UtcNow.Ticks;
            public volatile bool Ended;
        }

        private ClientWebSocket NewSocket()
        {
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            ws.Options.SetBuffer(65536, 65536);
            return ws;
        }

        private static Task SendText(ClientWebSocket ws, string text, CancellationToken ct) =>
            ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);

        private static string TextOf(byte[] buf, WebSocketReceiveResult r) =>
            r.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(buf, 0, r.Count) : null;

        /// <summary>
        /// One game connection. With a host bridge of this version the stream is resumable: when the WebSocket
        /// drops (internet cut, Wi-Fi change, Cloudflare hiccup) the game's connection stays open, a new
        /// WebSocket resumes it and both sides re-send what the other did not get. An older host bridge does not
        /// answer the hello: then a cut closes the game's connection, as before.
        /// </summary>
        private async Task Pipe(TcpClient game)
        {
            var s = new TunnelStream { Id = Guid.NewGuid().ToString("N"), Game = game };
            ClientWebSocket ws = NewSocket();
            try
            {
                game.NoDelay = true;
                game.SendBufferSize = game.ReceiveBufferSize = 1 << 20;
                await ws.ConnectAsync(Url, _cts.Token).ConfigureAwait(false);
                _log("Conectado a la partida por Cloudflare.");
                s.Gs = game.GetStream();

                await SendText(ws, Resume.Tag + " hello " + s.Id + " 0", _cts.Token).ConfigureAwait(false);
                var buf = new byte[65536];
                Task<WebSocketReceiveResult> first = ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                bool resumable = false;
                if (await Task.WhenAny(first, Task.Delay(8000)).ConfigureAwait(false) == first)
                {
                    WebSocketReceiveResult r = await first.ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    string t = TextOf(buf, r);
                    if (t != null && t.StartsWith(Resume.Tag + " ok ", StringComparison.Ordinal)) resumable = true;
                    else if (t != null && t == Resume.Tag + " gone") return;
                    first = null; // consumed
                }
                if (!resumable)
                {
                    await PlainPipe(s, ws, first, buf).ConfigureAwait(false);
                    return;
                }

                s.Ws = ws;
                Task up = Task.Run(() => UpPump(s));
                Task keepAlive = Task.Run(() => KeepAlive(s));
                while (!_closed && !s.Ended)
                {
                    bool end = await DownLoop(s, ws, buf).ConfigureAwait(false);
                    if (end || s.Ended || _closed) break;
                    await s.SendLock.WaitAsync().ConfigureAwait(false);
                    try { if (s.Ws == ws) s.Ws = null; } finally { s.SendLock.Release(); }
                    try { ws.Abort(); ws.Dispose(); } catch { }
                    _log("Cloudflare: se cortó la conexión; reconectando...");
                    ws = await Reconnect(s).ConfigureAwait(false);
                    if (ws == null)
                    {
                        if (!_closed && !s.Ended) _log("Cloudflare: no se pudo reconectar con la partida.");
                        break;
                    }
                    _log("Cloudflare: reconectado, la partida sigue.");
                }
            }
            catch (Exception ex)
            {
                if (!_closed && !s.Ended) _log("Cloudflare: se cortó la conexión (" + ex.GetBaseException().Message + ").");
            }
            finally
            {
                s.Ended = true;
                try { game.Close(); } catch { }
                try { s.Ws?.Abort(); s.Ws?.Dispose(); } catch { }
                try { ws?.Abort(); ws?.Dispose(); } catch { }
                s.Out.Release();
            }
        }

        /// <summary>An older host bridge: one WebSocket = one game connection, a cut ends it.</summary>
        private async Task PlainPipe(TunnelStream s, ClientWebSocket ws, Task<WebSocketReceiveResult> pending, byte[] buf)
        {
            Task up = Task.Run(async () =>
            {
                var b = new byte[65536];
                int n;
                while (!_closed && (n = await s.Gs.ReadAsync(b, 0, b.Length, _cts.Token).ConfigureAwait(false)) > 0)
                    await ws.SendAsync(new ArraySegment<byte>(b, 0, n), WebSocketMessageType.Binary, true, _cts.Token).ConfigureAwait(false);
            });
            Task down = Task.Run(async () =>
            {
                while (!_closed && ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult r = pending != null ? await pending.ConfigureAwait(false)
                                                               : await ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token).ConfigureAwait(false);
                    pending = null;
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (r.MessageType == WebSocketMessageType.Binary && r.Count > 0)
                        await s.Gs.WriteAsync(buf, 0, r.Count, _cts.Token).ConfigureAwait(false);
                    // Text: reserved signaling side-channel, ignored.
                }
            });
            await Task.WhenAny(up, down).ConfigureAwait(false);
        }

        /// <summary>Game → host: kept in Out until acknowledged, sent on the current WebSocket if there is one.</summary>
        private async Task UpPump(TunnelStream s)
        {
            var buf = new byte[65536];
            try
            {
                int n;
                while (!_closed && !s.Ended && (n = await s.Gs.ReadAsync(buf, 0, buf.Length, _cts.Token).ConfigureAwait(false)) > 0)
                {
                    s.Out.WaitForRoom(() => _closed || s.Ended);
                    await s.SendLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        s.Out.Append(buf, n);
                        ClientWebSocket w = s.Ws;
                        if (w == null) continue; // reconnecting: the bytes wait in Out
                        try { await w.SendAsync(new ArraySegment<byte>(buf, 0, n), WebSocketMessageType.Binary, true, _cts.Token).ConfigureAwait(false); }
                        catch { try { w.Abort(); } catch { } } // the receive side notices and reconnects
                    }
                    finally { s.SendLock.Release(); }
                }
            }
            catch { }
            if (s.Ended || _closed) return;
            // The game closed its connection (it left the session): tell the host bridge, then end.
            s.Ended = true;
            await s.SendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                ClientWebSocket w = s.Ws;
                if (w != null)
                {
                    try { await SendText(w, Resume.Tag + " end", CancellationToken.None).ConfigureAwait(false); } catch { }
                    try { w.Abort(); } catch { }
                }
            }
            finally { s.SendLock.Release(); }
        }

        /// <summary>Host → game on one WebSocket. True = the stream is over; false = the WebSocket broke (resume).</summary>
        private async Task<bool> DownLoop(TunnelStream s, ClientWebSocket ws, byte[] buf)
        {
            try
            {
                while (!_closed && !s.Ended)
                {
                    WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token).ConfigureAwait(false);
                    Interlocked.Exchange(ref s.LastRecvTicks, DateTime.UtcNow.Ticks);
                    if (r.MessageType == WebSocketMessageType.Close) return false; // resume: "gone" if the host ended it
                    if (r.MessageType == WebSocketMessageType.Binary)
                    {
                        if (r.Count == 0) continue;
                        try { await s.Gs.WriteAsync(buf, 0, r.Count, _cts.Token).ConfigureAwait(false); }
                        catch { return true; } // the game is gone
                        long got = Interlocked.Add(ref s.RecvFromHost, r.Count);
                        if (got - s.AckSent >= Resume.AckEvery) await SendAck(s, ws).ConfigureAwait(false);
                        continue;
                    }
                    string t = TextOf(buf, r);
                    if (t == null || !t.StartsWith(Resume.Tag + " ", StringComparison.Ordinal)) continue;
                    if (t == Resume.Tag + " end") return true;
                    if (t.StartsWith(Resume.Tag + " ack ", StringComparison.Ordinal) && long.TryParse(t.Substring(Resume.Tag.Length + 5), out long acked))
                        s.Out.Ack(acked);
                }
                return true;
            }
            catch
            {
                return s.Ended || _closed;
            }
        }

        private async Task SendAck(TunnelStream s, ClientWebSocket ws)
        {
            // Best effort: never wait for the send lock (a replay or a large write may hold it while this
            // side has to keep reading). The keep-alive acknowledges again two seconds later.
            if (!s.SendLock.Wait(0)) return;
            try
            {
                if (s.Ws != ws) return;
                long got = Interlocked.Read(ref s.RecvFromHost);
                s.AckSent = got;
                await SendText(ws, Resume.Tag + " ack " + got, _cts.Token).ConfigureAwait(false);
            }
            catch { try { ws.Abort(); } catch { } }
            finally { s.SendLock.Release(); }
        }

        /// <summary>Every 2 s: acknowledges what arrived (also a keep-alive), and drops a WebSocket that went silent.</summary>
        private async Task KeepAlive(TunnelStream s)
        {
            while (!_closed && !s.Ended)
            {
                try { await Task.Delay(2000, _cts.Token).ConfigureAwait(false); } catch { return; }
                ClientWebSocket w = s.Ws;
                if (w == null) continue;
                double silent = (DateTime.UtcNow - new DateTime(Interlocked.Read(ref s.LastRecvTicks), DateTimeKind.Utc)).TotalSeconds;
                if (silent > Resume.SilenceSeconds) { try { w.Abort(); } catch { } continue; } // dead connection: resume
                await SendAck(s, w).ConfigureAwait(false);
            }
        }

        /// <summary>New WebSockets until one resumes the stream; null = the host ended it, or no luck in time.</summary>
        private async Task<ClientWebSocket> Reconnect(TunnelStream s)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(Resume.ClientRetrySeconds);
            var buf = new byte[1024];
            while (!_closed && !s.Ended && DateTime.UtcNow < until)
            {
                ClientWebSocket ws = NewSocket();
                try
                {
                    Task connect = ws.ConnectAsync(Url, _cts.Token);
                    if (await Task.WhenAny(connect, Task.Delay(10000)).ConfigureAwait(false) != connect) throw new TimeoutException();
                    await connect.ConfigureAwait(false);
                    await SendText(ws, Resume.Tag + " resume " + s.Id + " " + Interlocked.Read(ref s.RecvFromHost), _cts.Token).ConfigureAwait(false);
                    Task<WebSocketReceiveResult> rx = ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                    if (await Task.WhenAny(rx, Task.Delay(10000)).ConfigureAwait(false) != rx) throw new TimeoutException();
                    string t = TextOf(buf, await rx.ConfigureAwait(false));
                    if (t == Resume.Tag + " gone") { try { ws.Abort(); ws.Dispose(); } catch { } return null; }
                    if (t == null || !t.StartsWith(Resume.Tag + " ok ", StringComparison.Ordinal) ||
                        !long.TryParse(t.Substring(Resume.Tag.Length + 4), out long hostGot)) throw new IOException("unexpected answer");
                    await s.SendLock.WaitAsync().ConfigureAwait(false);
                    List<ArraySegment<byte>> replay = s.Out.From(hostGot);
                    if (replay == null)
                    {
                        s.SendLock.Release();
                        try { ws.Abort(); ws.Dispose(); } catch { }
                        return null;
                    }
                    s.Out.Ack(hostGot);
                    Interlocked.Exchange(ref s.LastRecvTicks, DateTime.UtcNow.Ticks);
                    s.AckSent = Interlocked.Read(ref s.RecvFromHost);
                    s.Ws = ws;
                    // The replay goes first (the lock stays taken until it is out), but in the background: the
                    // host replays its side at the same time, so this side must already be reading (DownLoop),
                    // or both would wait on full buffers.
                    ClientWebSocket target = ws;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            foreach (ArraySegment<byte> seg in replay)
                                await target.SendAsync(seg, WebSocketMessageType.Binary, true, _cts.Token).ConfigureAwait(false);
                        }
                        catch { try { target.Abort(); } catch { } }
                        finally { s.SendLock.Release(); }
                    });
                    return ws;
                }
                catch
                {
                    try { ws.Abort(); ws.Dispose(); } catch { }
                }
                try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); } catch { return null; }
            }
            return null;
        }

        public void Dispose()
        {
            _closed = true;
            try { _cts.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
        }
    }

    /// <summary>
    /// Resumable streams between the two bridges, in TEXT frames next to the game's BINARY bytes:
    ///   friend → host  "gmsr1 hello &lt;id&gt; 0"        a new stream (an older host bridge ignores it: plain pipe)
    ///   friend → host  "gmsr1 resume &lt;id&gt; &lt;got&gt;"  a new WebSocket for a stream cut off
    ///   host → friend  "gmsr1 ok &lt;got&gt;" | "gmsr1 gone"
    ///   both ways      "gmsr1 ack &lt;got&gt;"            bytes received so far (also a keep-alive, every 2 s)
    ///   both ways      "gmsr1 end"                  the game side closed the connection: end the stream
    /// &lt;got&gt; = game bytes received on that stream so far. After "ok" each side re-sends what the other
    /// did not get, from its ReplayBuffer, so the game's TCP stream continues without a gap or a duplicate.
    /// </summary>
    internal static class Resume
    {
        internal const string Tag = "gmsr1";
        internal const int AckEvery = 64 * 1024;
        internal const double SilenceSeconds = 10;     // nothing from the other bridge for this long: dead WebSocket
        internal const double ClientRetrySeconds = 60; // friend: how long to try new WebSockets
        internal const double HostWaitSeconds = 90;    // host: how long a cut stream waits for its friend
    }

    /// <summary>Bytes sent on a resumable stream and not yet acknowledged by the other bridge.</summary>
    internal sealed class ReplayBuffer
    {
        private const long MaxBytes = 16L * 1024 * 1024; // the game waits (back-pressure) beyond this
        private readonly LinkedList<byte[]> _chunks = new LinkedList<byte[]>();
        private readonly object _lock = new object();
        private long _start, _end; // stream offsets of the first kept byte and of the next byte
        private bool _released;

        /// <summary>Blocks while too much is unacknowledged (a long cut), until acks, release or stop().</summary>
        internal void WaitForRoom(Func<bool> stop)
        {
            lock (_lock)
                while (!_released && _end - _start > MaxBytes && !stop()) Monitor.Wait(_lock, 500);
        }

        internal void Append(byte[] data, int count)
        {
            var c = new byte[count];
            Buffer.BlockCopy(data, 0, c, 0, count);
            lock (_lock)
            {
                if (_released) return;
                _chunks.AddLast(c);
                _end += count;
            }
        }

        /// <summary>The other side got everything before <paramref name="offset"/>.</summary>
        internal void Ack(long offset)
        {
            lock (_lock)
            {
                if (offset > _end) offset = _end;
                while (_chunks.Count > 0 && _start + _chunks.First.Value.Length <= offset)
                {
                    _start += _chunks.First.Value.Length;
                    _chunks.RemoveFirst();
                }
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>The kept bytes from <paramref name="offset"/> on, or null if some of them are gone already.</summary>
        internal List<ArraySegment<byte>> From(long offset)
        {
            lock (_lock)
            {
                if (_released || offset < _start || offset > _end) return null;
                var list = new List<ArraySegment<byte>>();
                long pos = _start;
                foreach (byte[] c in _chunks)
                {
                    if (pos + c.Length > offset)
                    {
                        int skip = (int)Math.Max(0, offset - pos);
                        list.Add(new ArraySegment<byte>(c, skip, c.Length - skip));
                    }
                    pos += c.Length;
                }
                return list;
            }
        }

        internal void Release()
        {
            lock (_lock)
            {
                _released = true;
                _chunks.Clear();
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>RFC 6455 frame reading/writing for the host bridge.</summary>
    internal static class WsFrames
    {
        internal sealed class Frame
        {
            public int Opcode;
            public bool Fin;
            public byte[] Payload;
        }

        private const int MaxPayload = 16 * 1024 * 1024;

        internal static Frame Read(Stream s)
        {
            var h = new byte[2];
            if (!ReadExact(s, h, 2)) return null;
            int opcode = h[0] & 0x0F;
            bool masked = (h[1] & 0x80) != 0;
            long len = h[1] & 0x7F;
            if (len == 126)
            {
                var b = new byte[2];
                if (!ReadExact(s, b, 2)) return null;
                len = (b[0] << 8) | b[1];
            }
            else if (len == 127)
            {
                var b = new byte[8];
                if (!ReadExact(s, b, 8)) return null;
                len = 0;
                for (int i = 0; i < 8; i++) len = (len << 8) | b[i];
            }
            if (len < 0 || len > MaxPayload) return null;
            var mask = new byte[4];
            if (masked && !ReadExact(s, mask, 4)) return null;
            var payload = new byte[len];
            if (!ReadExact(s, payload, (int)len)) return null;
            if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
            return new Frame { Opcode = opcode, Fin = (h[0] & 0x80) != 0, Payload = payload };
        }

        internal static void WriteText(Stream s, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            Write(s, 0x1, b, 0, b.Length, mask: false);
        }

        internal static void Write(Stream s, int opcode, byte[] data, int count, bool mask) => Write(s, opcode, data, 0, count, mask);

        internal static void Write(Stream s, int opcode, byte[] data, int offset, int count, bool mask)
        {
            var header = new byte[14];
            int n = 0;
            header[n++] = (byte)(0x80 | opcode);
            byte m = mask ? (byte)0x80 : (byte)0;
            if (count < 126) header[n++] = (byte)(m | count);
            else if (count <= 0xFFFF) { header[n++] = (byte)(m | 126); header[n++] = (byte)(count >> 8); header[n++] = (byte)count; }
            else
            {
                header[n++] = (byte)(m | 127);
                for (int i = 7; i >= 0; i--) header[n++] = (byte)((long)count >> (i * 8));
            }
            s.Write(header, 0, n);
            if (count > 0) s.Write(data, offset, count);
            s.Flush();
        }

        private static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }
    }

    /// <summary>Child processes (cloudflared) die with the launcher, even if it crashes: Windows job object.</summary>
    internal static class ChildProcesses
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr a, string name);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        private static IntPtr _job;

        internal static void Track(Process p)
        {
            try
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (0x2000).
                    int size = IntPtr.Size == 8 ? 144 : 112;
                    IntPtr info = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
                    try
                    {
                        for (int i = 0; i < size; i++) System.Runtime.InteropServices.Marshal.WriteByte(info, i, 0);
                        System.Runtime.InteropServices.Marshal.WriteInt32(info, IntPtr.Size == 8 ? 16 : 16, 0x2000);
                        SetInformationJobObject(_job, 9, info, (uint)size);
                    }
                    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(info); }
                }
                AssignProcessToJobObject(_job, p.Handle);
            }
            catch { } // best effort: the launcher also kills it on close
        }
    }
}
