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
    /// matter: it is a stream). TEXT frames are reserved for a future signaling side-channel (for example
    /// the exchange of addresses for a direct UDP connection); both ends ignore them today.
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
    /// URL reservation or admin rights are needed), exposed by cloudflared. Each WebSocket connection is
    /// piped to the game's LAN host on 127.0.0.1:<gamePort>.
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
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            new Thread(AcceptLoop) { IsBackground = true, Name = "WS accept" }.Start();

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

        private void Serve(TcpClient ws)
        {
            TcpClient game = null;
            bool counted = false;
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
                var sendLock = new object();
                NetworkStream gameStream = null;
                bool inText = false;
                while (!_closed)
                {
                    WsFrames.Frame f = WsFrames.Read(wsStream);
                    if (f == null || f.Opcode == 0x8) break;
                    if (f.Opcode == 0x9) { lock (sendLock) WsFrames.Write(wsStream, 0xA, f.Payload, f.Payload.Length, mask: false); continue; }
                    // 0x1 (text): "ping:..." is echoed as "pong:..." (the joining launcher measures the tunnel RTT);
                    // other text is reserved for signaling. 0xA (pong): ignored.
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
                    if ((f.Opcode != 0x2 && f.Opcode != 0x0) || f.Payload.Length == 0) continue;
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

        internal Uri Url => new Uri("wss://" + _sub + ".trycloudflare.com/");

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

        private async Task Pipe(TcpClient game)
        {
            var ws = new ClientWebSocket();
            try
            {
                game.NoDelay = true;
                game.SendBufferSize = game.ReceiveBufferSize = 1 << 20;
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                ws.Options.SetBuffer(65536, 65536);
                await ws.ConnectAsync(Url, _cts.Token).ConfigureAwait(false);
                _log("Conectado a la partida por Cloudflare.");
                NetworkStream gs = game.GetStream();

                Task up = Task.Run(async () =>
                {
                    var buf = new byte[65536];
                    int n;
                    while (!_closed && (n = await gs.ReadAsync(buf, 0, buf.Length, _cts.Token).ConfigureAwait(false)) > 0)
                        await ws.SendAsync(new ArraySegment<byte>(buf, 0, n), WebSocketMessageType.Binary, true, _cts.Token).ConfigureAwait(false);
                });
                Task down = Task.Run(async () =>
                {
                    var buf = new byte[65536];
                    while (!_closed && ws.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        if (r.MessageType == WebSocketMessageType.Binary && r.Count > 0)
                            await gs.WriteAsync(buf, 0, r.Count, _cts.Token).ConfigureAwait(false);
                        // Text: reserved signaling side-channel, ignored for now.
                    }
                });
                await Task.WhenAny(up, down).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!_closed) _log("Cloudflare: se cortó la conexión (" + ex.GetBaseException().Message + ").");
            }
            finally
            {
                try { game.Close(); } catch { }
                try { ws.Abort(); ws.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            _closed = true;
            try { _cts.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
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

        internal static void Write(Stream s, int opcode, byte[] data, int count, bool mask)
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
            if (count > 0) s.Write(data, 0, count);
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
