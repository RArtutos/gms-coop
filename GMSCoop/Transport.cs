using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Steamworks;

namespace GMSCoop
{
    /// <summary>Steam P2P (ISteamNetworking) through the Steamworks.NET copy that ships with the game.</summary>
    internal sealed class SteamTransport : ITransport
    {
        private const int ChannelReliable = 0;
        private const int ChannelFast = 1;
        private const int MaxUnreliable = 1200;       // Steam limit for unreliable P2P packets
        private const int MaxReliable = 1024 * 1024;  // Steam limit for reliable P2P packets

        private readonly byte[] _recvBuf = new byte[MaxReliable];
        private readonly Callback<P2PSessionRequest_t> _sessionRequest;
        private readonly Callback<P2PSessionConnectFail_t> _connectFail;
        private readonly ConcurrentQueue<Packet> _events = new ConcurrentQueue<Packet>();

        public string Name => "Steam";

        // Sessions of an ended session, closed a moment later so its last reliable message (Bye) still goes
        // out: CloseP2PSessionWithUser drops whatever is still queued for that peer.
        private static readonly List<KeyValuePair<ulong, float>> DeferredCloses = new List<KeyValuePair<ulong, float>>();
        private const float CloseDelay = 1.5f;

        /// <summary>Closes the P2P session with <paramref name="peer"/> after a short delay (see PumpDeferred).</summary>
        internal static void CloseLater(ulong peer, float now) => DeferredCloses.Add(new KeyValuePair<ulong, float>(peer, now + CloseDelay));

        /// <summary>Called every frame (also outside a session): closes the sessions whose delay ran out.</summary>
        internal static void PumpDeferred(float now)
        {
            for (int i = DeferredCloses.Count - 1; i >= 0; i--)
            {
                if (now < DeferredCloses[i].Value) continue;
                ulong peer = DeferredCloses[i].Key;
                DeferredCloses.RemoveAt(i);
                try { SteamNetworking.CloseP2PSessionWithUser(new CSteamID(peer)); } catch (Exception) { }
            }
        }

        public SteamTransport(Func<CSteamID, bool> acceptPeer)
        {
            DeferredCloses.Clear(); // a new session (maybe with the same peer) must not be closed by the old one
            DrainStale(); // packets left over from an earlier session must not reach this one
            _sessionRequest = Callback<P2PSessionRequest_t>.Create(e =>
            {
                if (acceptPeer(e.m_steamIDRemote))
                    SteamNetworking.AcceptP2PSessionWithUser(e.m_steamIDRemote);
            });
            // A crashed or unreachable peer: report it like a disconnect (Data == null).
            _connectFail = Callback<P2PSessionConnectFail_t>.Create(e =>
            {
                Plugin.Log.LogWarning($"Steam P2P connection to {e.m_steamIDRemote} failed (error {e.m_eP2PSessionError})");
                _events.Enqueue(new Packet { From = e.m_steamIDRemote.m_SteamID, Data = null });
            });
        }

        public bool Send(ulong peer, byte[] data, bool reliable)
        {
            if (!reliable && data.Length > MaxUnreliable) reliable = true; // too big for unreliable: send reliable
            if (data.Length > MaxReliable)
            {
                Plugin.Log.LogError($"Steam: packet of {data.Length} bytes exceeds the 1 MB limit, dropped (type {data[0]})");
                return false;
            }
            return SteamNetworking.SendP2PPacket(new CSteamID(peer), data, (uint)data.Length,
                reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay,
                reliable ? ChannelReliable : ChannelFast);
        }

        public int QueuedBytes(ulong peer) =>
            SteamNetworking.GetP2PSessionState(new CSteamID(peer), out P2PSessionState_t s) ? s.m_nBytesQueuedForSend : 0;

        public void Poll(Action<Packet> onPacket)
        {
            while (_events.TryDequeue(out Packet ev)) onPacket(ev);
            for (int ch = 0; ch <= ChannelFast; ch++)
            {
                while (SteamNetworking.IsP2PPacketAvailable(out uint size, ch))
                {
                    byte[] buf = size <= _recvBuf.Length ? _recvBuf : new byte[size];
                    if (!SteamNetworking.ReadP2PPacket(buf, size, out uint read, out CSteamID from, ch)) break;
                    onPacket(new Packet { From = from.m_SteamID, Data = buf, Length = (int)read, Reliable = ch == ChannelReliable });
                }
            }
        }

        private void DrainStale()
        {
            for (int ch = 0; ch <= ChannelFast; ch++)
                while (SteamNetworking.IsP2PPacketAvailable(out uint size, ch))
                    if (!SteamNetworking.ReadP2PPacket(_recvBuf, Math.Min(size, (uint)_recvBuf.Length), out _, out _, ch)) break;
        }

        public void Disconnect(ulong peer) => SteamNetworking.CloseP2PSessionWithUser(new CSteamID(peer));

        public void Close()
        {
            _sessionRequest?.Dispose();
            _connectFail?.Dispose();
            DrainStale();
        }
    }

    /// <summary>
    /// Direct TCP: LAN, an IP (VPN, open port) or the Cloudflare tunnel (each connection of the game is one
    /// WebSocket through the launcher's bridge). Frames: [int32 length][payload]. Peer ids: host = 1,
    /// clients = 2, 3, ... Reads and writes run on worker threads; the main thread never blocks on a socket.
    ///
    /// Lanes: a peer may have up to 4 connections. Lane 0 is the session; the host sends a token on it
    /// (HybridTransport), and the client opens lanes 1-3 to the same address, each starting with the frame
    /// [0xE5][u64 token][u8 lane]. A lane that fails or closes only falls back to lane 0.
    ///
    /// Lane 0 re-attach: once the token is known, a dropped lane 0 does not end the peer. Both sides keep it
    /// "detached" (event LaneEvent.Detached), the client reconnects every half second with the same hello and
    /// lane 0 (the host accepts it even if it has not noticed the old connection died yet), and both sides
    /// report LaneEvent.Reattached. HybridTransport re-sends what the other side has not acknowledged and
    /// decides when to give up (Disconnect).
    /// </summary>
    internal sealed class LanTransport : ILaneTransport
    {
        internal const ulong HostPeerId = 1;
        internal const int LaneCount = 4;
        private const byte LaneHelloTag = 0xE5;
        private const int StaleLimit = 16 * 1024; // snapshot lanes: queued bytes before old frames are dropped

        /// <summary>Outgoing frames of one connection; snapshot lanes drop the oldest frames when backed up.</summary>
        private sealed class LaneQueue
        {
            private readonly Queue<byte[]> _q = new Queue<byte[]>();
            private bool _done;
            public int Bytes;

            public bool Add(byte[] frame, bool dropStale)
            {
                lock (_q)
                {
                    if (_done) return false;
                    if (dropStale)
                        while (_q.Count > 0 && Bytes + frame.Length > StaleLimit) Bytes -= _q.Dequeue().Length;
                    _q.Enqueue(frame);
                    Bytes += frame.Length;
                    Monitor.Pulse(_q);
                    return true;
                }
            }

            /// <summary>Blocks until a frame is queued; null once completed and drained.</summary>
            public byte[] Take()
            {
                lock (_q)
                {
                    while (_q.Count == 0 && !_done) Monitor.Wait(_q);
                    if (_q.Count == 0) return null;
                    byte[] f = _q.Dequeue();
                    Bytes -= f.Length;
                    return f;
                }
            }

            public void Complete() { lock (_q) { _done = true; Monitor.PulseAll(_q); } }
            public bool Completed { get { lock (_q) return _done; } }
        }

        private sealed class Conn
        {
            public ulong Peer;
            public int Lane;
            public bool Bound;
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly LaneQueue Out = new LaneQueue();
        }

        private readonly ConcurrentQueue<Packet> _inbox = new ConcurrentQueue<Packet>();
        private readonly Dictionary<ulong, Conn[]> _peers = new Dictionary<ulong, Conn[]>();
        private readonly Dictionary<ulong, ulong> _tokens = new Dictionary<ulong, ulong>(); // token -> peer
        private readonly HashSet<ulong> _reattaching = new HashSet<ulong>(); // client: peers with a reconnect thread
        private ulong _clientToken; // client: the host's lane token (0 = none yet: lane 0 cannot re-attach)
        private readonly object _connsLock = new object();
        private TcpListener _listener;
        private volatile bool _closed;
        private long _nextId = 2;
        private string _hostIp;
        private int _hostPort;

        public string Name => "LAN";

        internal static LanTransport Host(int port)
        {
            var t = new LanTransport();
            t._listener = new TcpListener(IPAddress.Any, port);
            t._listener.Start();
            new Thread(t.AcceptLoop) { IsBackground = true, Name = "GMSCoop-Accept" }.Start();
            return t;
        }

        /// <summary>Connects on a worker thread; onResult(null) on success, or an error message.</summary>
        internal static LanTransport Join(string ip, int port, Action<string> onResult)
        {
            var t = new LanTransport { _hostIp = ip, _hostPort = port };
            new Thread(() =>
            {
                try
                {
                    var c = new TcpClient { NoDelay = true };
                    c.Connect(ip, port);
                    t.Start(new Conn { Peer = HostPeerId, Lane = 0, Bound = true, Client = c, Stream = c.GetStream() });
                    onResult(null);
                }
                catch (Exception ex)
                {
                    onResult(ex.Message);
                }
            }) { IsBackground = true, Name = "GMSCoop-Connect" }.Start();
            return t;
        }

        private void AcceptLoop()
        {
            while (!_closed)
            {
                try
                {
                    TcpClient c = _listener.AcceptTcpClient();
                    c.NoDelay = true;
                    // Unbound until its first frame: a lane hello (an extra lane of a known peer) or a new peer.
                    Start(new Conn { Client = c, Stream = c.GetStream() });
                }
                catch (Exception)
                {
                    if (_closed) return;
                }
            }
        }

        /// <summary>Starts the read and write threads; false (connection closed) if a bound one was refused.</summary>
        private bool Start(Conn conn, bool reattach = false)
        {
            // A peer that stops reading must not block the write thread forever (it closes the socket).
            // As long as the keep-alive limit: a Wi-Fi stall of a few seconds must not end the session.
            conn.Client.SendTimeout = 30000;
            if (conn.Bound && !Bind(conn, reattach))
            {
                try { conn.Client.Close(); } catch (Exception) { }
                return false;
            }
            new Thread(() => ReadLoop(conn)) { IsBackground = true, Name = "GMSCoop-Read" }.Start();
            new Thread(() => WriteLoop(conn)) { IsBackground = true, Name = "GMSCoop-Write" }.Start();
            return true;
        }

        /// <summary>
        /// Registers the connection as that lane of its peer. Only a first lane 0 creates a peer: an extra lane
        /// or a re-attach of a peer that is gone meanwhile is refused (false).
        /// </summary>
        private bool Bind(Conn conn, bool reattach = false)
        {
            Conn old;
            lock (_connsLock)
            {
                if (!_peers.TryGetValue(conn.Peer, out Conn[] lanes))
                {
                    if (_closed || reattach || conn.Lane != 0) return false;
                    _peers[conn.Peer] = lanes = new Conn[LaneCount];
                }
                old = lanes[conn.Lane];
                lanes[conn.Lane] = conn;
                conn.Bound = true;
                if (reattach) _inbox.Enqueue(new Packet { From = conn.Peer, Data = null, Event = LaneEvent.Reattached });
            }
            if (old != null && old != conn) CloseConn(old);
            return true;
        }

        /// <summary>Lane 0 of this peer may come back: the host gave it a token (host), or we have the host's (client).</summary>
        private bool CanReattachLocked(ulong peer) =>
            _hostIp == null ? _tokens.ContainsValue(peer) : _clientToken != 0 && peer == HostPeerId;

        private static byte[] LaneHello(ulong token, int lane)
        {
            var hello = new byte[10];
            hello[0] = LaneHelloTag;
            Buffer.BlockCopy(BitConverter.GetBytes(token), 0, hello, 1, 8);
            hello[9] = (byte)lane;
            return hello;
        }

        /// <summary>Client: reconnects lane 0 to the host until it works, the peer is dropped or the transport closes.</summary>
        private void StartReattach(ulong peer)
        {
            lock (_connsLock) if (_closed || !_reattaching.Add(peer)) return;
            new Thread(() =>
            {
                bool registered = true;
                try
                {
                    while (true)
                    {
                        Thread.Sleep(500);
                        ulong token;
                        lock (_connsLock)
                        {
                            if (_closed || !_peers.TryGetValue(peer, out Conn[] lanes) || lanes[0] != null) return;
                            token = _clientToken;
                        }
                        TcpClient c = null;
                        try
                        {
                            c = new TcpClient { NoDelay = true };
                            IAsyncResult ar = c.BeginConnect(_hostIp, _hostPort, null, null);
                            if (!ar.AsyncWaitHandle.WaitOne(5000)) { try { c.Close(); } catch { } continue; }
                            c.EndConnect(ar);
                        }
                        catch (Exception) { try { c?.Close(); } catch { } continue; }
                        var conn = new Conn { Peer = peer, Lane = 0, Bound = true, Client = c, Stream = c.GetStream() };
                        conn.Out.Add(Frame(LaneHello(token, 0)), false); // first frame on the wire
                        // Leave the set first: if this connection drops at once, its ReadLoop starts a new attempt.
                        lock (_connsLock) _reattaching.Remove(peer);
                        registered = false;
                        if (Start(conn, reattach: true))
                            for (int lane = 1; lane < LaneCount; lane++)
                                if (!HasLane(peer, lane)) OpenLane(peer, token, lane);
                        return;
                    }
                }
                catch (Exception) { }
                finally
                {
                    if (registered) lock (_connsLock) _reattaching.Remove(peer);
                }
            }) { IsBackground = true, Name = "GMSCoop-Reattach" }.Start();
        }

        private void ReadLoop(Conn conn)
        {
            var r = new BinaryReader(conn.Stream);
            try
            {
                while (!_closed)
                {
                    int len = r.ReadInt32();
                    if (len <= 0 || len > 64 * 1024 * 1024) throw new IOException("Bad frame length " + len);
                    byte[] data = r.ReadBytes(len);
                    if (data.Length != len) throw new EndOfStreamException();
                    if (!conn.Bound)
                    {
                        if (data[0] == LaneHelloTag && len >= 10)
                        {
                            ulong token = BitConverter.ToUInt64(data, 1);
                            int lane = data[9];
                            ulong peer;
                            bool known;
                            lock (_connsLock) known = _tokens.TryGetValue(token, out peer) && _peers.ContainsKey(peer);
                            if (!known || lane < 0 || lane >= LaneCount) throw new IOException("Unknown lane token");
                            conn.Peer = peer;
                            conn.Lane = lane;
                            // Lane 0: the client re-attaches its session (it may replace a connection we still
                            // think is open, a half-open one after a network change).
                            if (!Bind(conn, reattach: lane == 0)) throw new IOException("Lane of a peer that left");
                            continue;
                        }
                        conn.Peer = (ulong)Interlocked.Increment(ref _nextId) - 1;
                        conn.Lane = 0;
                        if (!Bind(conn)) throw new IOException("Transport closed");
                    }
                    _inbox.Enqueue(new Packet { From = conn.Peer, Data = data, Length = len, Reliable = true, Lane = conn.Lane });
                }
            }
            catch (Exception)
            {
                // Connection closed or broken.
            }
            if (!conn.Bound) { CloseConn(conn); return; }
            if (conn.Lane != 0)
            {
                // An extra lane closed: its traffic falls back to lane 0, the peer stays.
                lock (_connsLock)
                    if (_peers.TryGetValue(conn.Peer, out Conn[] lanes) && lanes[conn.Lane] == conn) lanes[conn.Lane] = null;
                CloseConn(conn);
                return;
            }
            bool owner = false, detached = false;
            lock (_connsLock)
            {
                if (_peers.TryGetValue(conn.Peer, out Conn[] lanes) && lanes[0] == conn)
                {
                    owner = true;
                    if (!_closed && CanReattachLocked(conn.Peer))
                    {
                        lanes[0] = null;
                        detached = true;
                    }
                }
            }
            if (!owner) { CloseConn(conn); return; } // replaced by a re-attached lane 0, or the peer left
            if (detached)
            {
                // Whatever was still queued on it is lost: HybridTransport re-sends it after the re-attach.
                conn.Out.Complete();
                try { conn.Client.Close(); } catch (Exception) { }
                _inbox.Enqueue(new Packet { From = conn.Peer, Data = null, Event = LaneEvent.Detached });
                if (_hostIp != null) StartReattach(conn.Peer);
                return;
            }
            bool wasOpen = RemovePeer(conn.Peer, conn);
            if (!_closed && wasOpen) _inbox.Enqueue(new Packet { From = conn.Peer, Data = null });
        }

        private void WriteLoop(Conn conn)
        {
            try
            {
                byte[] frame;
                while ((frame = conn.Out.Take()) != null) conn.Stream.Write(frame, 0, frame.Length);
            }
            catch (Exception)
            {
                // Socket broken: ReadLoop reports the disconnect.
            }
            // Completed (disconnect) and drained, or the socket broke: close it. Closing only here lets a
            // last message (Bye) reach the peer before the connection goes.
            try { conn.Client.Close(); } catch (Exception) { }
        }

        private static byte[] Frame(byte[] data)
        {
            // Length prefix and payload in one buffer: a single write, never interleaved.
            var frame = new byte[4 + data.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(data.Length), 0, frame, 0, 4);
            Buffer.BlockCopy(data, 0, frame, 4, data.Length);
            return frame;
        }

        private Conn LaneOf(ulong peer, int lane)
        {
            lock (_connsLock)
            {
                if (!_peers.TryGetValue(peer, out Conn[] lanes)) return null;
                Conn c = lane > 0 && lane < LaneCount ? lanes[lane] : null;
                return c != null && !c.Out.Completed ? c : lanes[0];
            }
        }

        public bool Send(ulong peer, byte[] data, bool reliable) => SendLane(peer, 0, data, false);

        public bool SendLane(ulong peer, int lane, byte[] data, bool dropStale)
        {
            Conn conn = LaneOf(peer, lane);
            if (conn == null) return false;
            return conn.Out.Add(Frame(data), dropStale && conn.Lane != 0);
        }

        public bool HasLane(ulong peer, int lane)
        {
            lock (_connsLock)
                return _peers.TryGetValue(peer, out Conn[] lanes) && lane >= 0 && lane < LaneCount &&
                       lanes[lane] != null && !lanes[lane].Out.Completed;
        }

        public void RegisterLaneToken(ulong peer, ulong token)
        {
            lock (_connsLock) _tokens[token] = peer;
        }

        public void OpenLanes(ulong peer, ulong token)
        {
            if (_hostIp == null) return;
            lock (_connsLock) _clientToken = token; // from now on lane 0 can re-attach
            for (int lane = 1; lane < LaneCount; lane++) OpenLane(peer, token, lane);
        }

        private void OpenLane(ulong peer, ulong token, int l)
        {
            new Thread(() =>
            {
                try
                {
                    var c = new TcpClient { NoDelay = true };
                    IAsyncResult ar = c.BeginConnect(_hostIp, _hostPort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(5000)) { try { c.Close(); } catch { } return; }
                    c.EndConnect(ar);
                    lock (_connsLock) if (_closed || !_peers.ContainsKey(peer)) { c.Close(); return; }
                    var conn = new Conn { Peer = peer, Lane = l, Bound = true, Client = c, Stream = c.GetStream() };
                    conn.Out.Add(Frame(LaneHello(token, l)), false); // first frame on the wire
                    Start(conn);
                }
                catch (Exception) { /* this lane stays closed: lane 0 carries its traffic */ }
            }) { IsBackground = true, Name = "GMSCoop-Lane" }.Start();
        }

        public int QueuedBytes(ulong peer)
        {
            lock (_connsLock)
            {
                if (!_peers.TryGetValue(peer, out Conn[] lanes)) return 0;
                int q = 0;
                foreach (Conn c in lanes) if (c != null) q += Volatile.Read(ref c.Out.Bytes);
                return q;
            }
        }

        /// <summary>The peer's address on lane 0 (127.0.0.1 behind the Cloudflare bridge), or null.</summary>
        public IPAddress RemoteAddress(ulong peer)
        {
            Conn conn = LaneOf(peer, 0);
            try { return (conn?.Client.Client.RemoteEndPoint as IPEndPoint)?.Address; }
            catch (Exception) { return null; }
        }

        public void Poll(Action<Packet> onPacket)
        {
            while (_inbox.TryDequeue(out Packet p)) onPacket(p);
        }

        public void Disconnect(ulong peer) => RemovePeer(peer, null);

        /// <summary>True if the peer was still registered (and its lane 0 is <paramref name="lane0"/>, if given).</summary>
        private bool RemovePeer(ulong id, Conn lane0)
        {
            Conn[] lanes;
            lock (_connsLock)
            {
                if (!_peers.TryGetValue(id, out lanes)) return false;
                if (lane0 != null && lanes[0] != lane0) return false;
                _peers.Remove(id);
                var stale = new List<ulong>();
                foreach (var kv in _tokens) if (kv.Value == id) stale.Add(kv.Key);
                foreach (ulong k in stale) _tokens.Remove(k);
            }
            foreach (Conn c in lanes) if (c != null) CloseConn(c);
            return true;
        }

        /// <summary>
        /// The write thread sends what is still queued (e.g. a Bye) and then closes the socket. With a big
        /// backlog (a world transfer in flight), or an extra lane, close at once.
        /// </summary>
        private static void CloseConn(Conn conn)
        {
            conn.Out.Complete();
            if (Volatile.Read(ref conn.Out.Bytes) > 64 * 1024 || conn.Lane != 0)
                try { conn.Client.Close(); } catch (Exception) { }
        }

        public void Close()
        {
            _closed = true;
            try { _listener?.Stop(); } catch (Exception) { }
            List<ulong> ids;
            lock (_connsLock) ids = new List<ulong>(_peers.Keys);
            foreach (var id in ids) RemovePeer(id, null);
        }
    }
}
