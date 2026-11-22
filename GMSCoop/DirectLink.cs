using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace GMSCoop
{
    /// <summary>
    /// Session transport over a TCP base (LAN, IP or the Cloudflare tunnel) with an automatic direct UDP link
    /// (hole punching: no server of ours, no port forwarding). The TCP path is the signaling channel and the
    /// fallback. Full design in docs/ARCHITECTURE.md ("Direct link").
    ///
    /// Channels. Every message gets a channel and a per-channel sequence number, whichever path carries it:
    ///   0 control (Hello, Bye) · 1 gameplay (every other reliable message) · 2 bulk (world chunks) ·
    ///   unreliable (poses, snapshots: sequenced, duplicates and stale copies dropped).
    /// Each reliable channel is delivered in order on its own, so a lost world chunk never delays gameplay.
    /// The receiver drops duplicates, so a message may travel on TCP and on UDP (switchover, fallback re-send)
    /// with no loss and no duplicate.
    ///
    /// TCP path (ILaneTransport): lane 0 control + gameplay, lane 2 bulk, lanes 1 and 3 the same unreliable
    /// snapshots twice (a stalled TCP connection no longer delays poses). Without lanes, lane 0 carries all.
    ///   tags: 0xE0 reliable [u8 ch][u32 seq][payload] · 0xE1 unreliable [u32 useq][payload] ·
    ///         0xE3 control [sub][...] · untagged: a peer without this layer (passed through, version check).
    ///
    /// UDP path: "GC" [type][u32 link id] then
    ///   1 PUNCH / 2 PUNCH_ACK [u64 nonce][8-byte HMAC tag]
    ///   16 SECURE [u32 packet number][AES-128-CTR ciphertext of frames][8-byte HMAC-SHA256 tag]
    ///      (encrypt-then-MAC; keys from the 16-byte session secret sent over the tunnel; a sliding window
    ///       of packet numbers rejects replays; an authenticated packet from a new address = NAT rebinding)
    ///   frames: 1 STREAM [u8 ch][u32 seq][u16 frag][u16 count][u16 len][bytes] · 2 UNREL [u32 useq][u16 len][bytes] ·
    ///           3 ACK [u16 delay ms][u8 n][(u32 first, u16 count) x n] · 4 PING / 5 PONG [f64] · 6 PAD [u16 n][zeros]
    /// Loss recovery: selective ACK ranges; a packet is lost once 3 later packets are acknowledged or after
    /// 9/8 RTT behind the newest acknowledged one; RTO from smoothed RTT and variance (RFC 6298) with
    /// back-off. Lost fragments are queued once (no storms) in new packets.
    /// Congestion: slow start, then additive increase; cut on a high loss rate (a full router queue) or a
    /// growing RTT, not on random loss. Paced sending. Priority: control, snapshots, gameplay, then bulk.
    /// MTU: 1200 bytes, probed up to 1400 after the link is up.
    /// </summary>
    internal sealed class HybridTransport : ITransport
    {
        // ---------------- tuning (static so the no-game harness can change them) ----------------
        internal static Action<string> Log = s => { };
        internal static double StallTimeout = 10.0; // the peer's main thread may freeze for seconds (world load)
        internal static double PunchTimeout = 10.0;
        internal static double PunchInterval = 0.1;
        internal static double[] RetryDelays = { 20, 45, 90, 180, 300 };
        internal static double PingInterval = 0.5;
        internal static double BaseKeepAlive = 2.0;
        internal static double LegacyHelloAfter = 5.0; // no answer to a tagged Hello: try an untagged copy (old host)  // tunnel RTT probe + keep-alive while UDP carries the traffic
        internal static bool UseLanes = true;

        internal const int ChControl = 0, ChGame = 1, ChBulk = 2, Channels = 3;
        private const byte TagReliable = 0xE0, TagUnreliable = 0xE1, TagControl = 0xE3;
        private const byte CtlOffer = 1, CtlAnswer = 2, CtlKeepAlive = 4, CtlLaneToken = 5, CtlPing = 6, CtlPong = 7, CtlDown = 8, CtlChanAck = 9;
        private const byte UPunch = 1, UPunchAck = 2, USecure = 16;
        private const byte FStream = 1, FUnrel = 2, FAck = 3, FPing = 4, FPong = 5, FPad = 6;
        private const int BaseMtu = 1200, ProbeMtu = 1400;
        private const int Header = 7;                  // "GC" + type + link id
        private const int SecureOverhead = Header + 4 + 8; // + packet number + tag
        private const int StreamFrameHeader = 12;
        private const int UnrelFrameHeader = 7;
        private const int MaxDirectMessage = 16 * 1024 * 1024;
        private const double MinCwnd = 32 * 1024, MaxCwnd = 16 * 1024 * 1024, InitialCwnd = 64 * 1024;
        private const int ReplayWindow = 4096;
        private const int Lane0 = 0, LaneSnapA = 1, LaneBulk = 2, LaneSnapB = 3;

        private static readonly Stopwatch Watch = Stopwatch.StartNew();
        internal static Func<double> Clock = () => Watch.Elapsed.TotalSeconds;

        /// <summary>Channel of a reliable message, from its message id (first byte, see Net.Msg).</summary>
        internal static int ChannelOf(byte[] data)
        {
            if (data.Length == 0) return ChGame;
            switch (data[0])
            {
                case 1: return ChControl;           // Hello
                // Bye stays behind the last gameplay messages (GoldSync.FlushNow sends the ledger right before it).
                case 8: case 19: return ChBulk;     // SaveChunk, CoopSession uploads
                // SaveControl Preparing / Failed belong to the chunk stream (a chunk must not overtake its
                // Preparing). CatchUp, Loaded and Resend stay ordered with gameplay (the journal replays).
                case 9: return data.Length > 1 && (data[1] == 1 || data[1] == 4) ? ChBulk : ChGame;
                default: return ChGame;
            }
        }

        private readonly ITransport _base;
        private readonly ILaneTransport _lanes;
        private readonly bool _isHost;
        private readonly bool _offerDirect;
        private readonly Func<ulong, string> _baseLabel;
        private readonly Func<IDatagramSocket> _socketFactory;
        private readonly object _lock = new object();
        private readonly Dictionary<ulong, Link> _links = new Dictionary<ulong, Link>();
        private readonly Dictionary<uint, Link> _byLinkId = new Dictionary<uint, Link>();
        private readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();
        private IDatagramSocket _udp;
        private Task<List<IPEndPoint>> _gather;
        private List<IPEndPoint> _myCandidates;
        private double _candidatesAt = double.NegativeInfinity;
        private bool _closed;

        /// <param name="offerDirect">Client: try a direct connection to the host (false on a LAN/VPN address).</param>
        /// <param name="baseLabel">Name of the TCP path to a peer for the overlay ("Cloudflare", "LAN"...).</param>
        /// <param name="socketFactory">UDP socket (the harness passes a simulated one).</param>
        internal HybridTransport(ITransport baseTransport, bool isHost, bool offerDirect, Func<ulong, string> baseLabel,
                                 Func<IDatagramSocket> socketFactory = null)
        {
            _base = baseTransport;
            _lanes = UseLanes ? baseTransport as ILaneTransport : null;
            _isHost = isHost;
            _offerDirect = offerDirect;
            _baseLabel = baseLabel ?? (p => baseTransport.Name);
            _socketFactory = socketFactory ?? (() => new UdpDatagramSocket());
        }

        public string Name => _base.Name;

        /// <summary>The path gameplay uses to a peer, for the overlay: "Directo" or the TCP path's name.</summary>
        internal string LinkLabel(ulong peer)
        {
            lock (_lock)
            {
                if (!_links.TryGetValue(peer, out Link l)) return _baseLabel(peer);
                string tcp = _baseLabel(peer) + (_lanes != null && _lanes.HasLane(peer, LaneSnapA) ? "+" : "");
                if (!l.SendDirect) return tcp;
                return l.GameOnDirect ? "Directo" : tcp + " (directo de respaldo)";
            }
        }

        internal bool IsDirect(ulong peer)
        {
            lock (_lock) return _links.TryGetValue(peer, out Link l) && l.SendDirect;
        }

        /// <summary>Round trip (s) and its variation on the path gameplay uses now, or false if not measured yet.</summary>
        internal bool TryGetPathQuality(ulong peer, out double rtt, out double jitter)
        {
            lock (_lock)
            {
                rtt = jitter = 0;
                if (!_links.TryGetValue(peer, out Link l)) return false;
                if (l.SendDirect && l.GameOnDirect && l.HasRtt) { rtt = l.Srtt; jitter = l.Rttvar; return true; }
                if (l.TcpRttSamples > 0) { rtt = l.TcpRtt; jitter = l.TcpRttVar; return true; }
                return false;
            }
        }

        // =====================================================================================
        // Per-peer state
        // =====================================================================================

        private enum DState { Idle, Gathering, WaitAnswer, Punching, Up, Failed }

        private sealed class OutMsg
        {
            public int Ch;
            public uint Seq;
            public byte[] Data;
            public int FragSize, Frags, NextFrag, AckedCount;
            public byte[] State; // 0 waiting, 1 in flight, 2 acknowledged
            public int FragLen(int i) => i < Frags - 1 ? FragSize : Data.Length - FragSize * (Frags - 1);
        }

        private struct FragRef
        {
            public OutMsg Msg;
            public int Frag;
        }

        private sealed class SentPacket
        {
            public uint Pn;
            public double Time;
            public int Size;
            public bool AckEliciting;
            public bool Probe;
            public List<FragRef> Frags;
        }

        private sealed class Assembly
        {
            public int Count, Have, Bytes;
            public byte[][] Parts;
        }

        private sealed class RecvChannel
        {
            public uint Next;
            public readonly Dictionary<uint, byte[]> Ready = new Dictionary<uint, byte[]>();
            public readonly Dictionary<uint, Assembly> Partial = new Dictionary<uint, Assembly>();
        }

        private sealed class SendChannel
        {
            public uint NextSeq;
            public readonly List<OutMsg> Out = new List<OutMsg>();      // over UDP, not fully acknowledged
            public int Cursor;                                           // first message in Out with unsent fragments
            public int Done;                                             // fully acknowledged messages still in Out
            public readonly Queue<FragRef> Lost = new Queue<FragRef>(); // to send again
            public int PendingBytes;                                     // never sent yet
        }

        private sealed class Link
        {
            public ulong Peer;
            public readonly SendChannel[] Tx = { new SendChannel(), new SendChannel(), new SendChannel() };
            public readonly RecvChannel[] Rx = { new RecvChannel(), new RecvChannel(), new RecvChannel() };
            // unreliable: sequence out, replay window in
            public uint NextUseq;
            public uint UnrelNewest;
            public ulong UnrelSeen;
            public bool UnrelAny;
            public readonly Queue<KeyValuePair<double, byte[]>> UnrelQueue = new Queue<KeyValuePair<double, byte[]>>();
            // direct path setup
            public DState State;
            public uint LinkId;
            public byte[] Secret, MacKey;
            public ICryptoTransform Aes;
            public HMACSHA256 Mac;
            public List<IPEndPoint> RemoteCands = new List<IPEndPoint>();
            public IPEndPoint Remote;
            public bool SendDirect, GameOnDirect = true;
            public double PunchStart, LastPunch, LastUdpRecv, LastPing, NextRetry = double.NegativeInfinity, LastBaseSend, NextStats;
            public int Retries;
            public bool BaseSeen, Tagged, Legacy, LanesAsked;
            public uint OfferId;                          // client: id of its pending offer; host: of the offer answered
            public byte[] FirstHello;                     // client: its Hello, re-sent untagged if nobody answers (old host)
            public double FirstSendAt = -1;
            public bool HeardAny, LegacyHelloSent;
            // TCP path, bulk channel: messages sent on lane 2 until the receiver acknowledges them (a lane can drop)
            public readonly Queue<KeyValuePair<uint, byte[]>> TcpBulk = new Queue<KeyValuePair<uint, byte[]>>();
            public bool BulkLaneUp;
            public uint ChanAckSent;
            public double ChanAckAt;
            public bool WasUp;                            // the direct link worked at least once
            public readonly Queue<ulong> SentNonces = new Queue<ulong>(), SeenNonces = new Queue<ulong>();
            public int BigLost;
            public double BigLostSince;
            // UDP packets out
            public uint NextPn = 1;
            public readonly Dictionary<uint, SentPacket> Inflight = new Dictionary<uint, SentPacket>();
            public int BytesInFlight;
            public uint LargestAcked;
            public double LargestAckedTime;
            public double Cwnd = InitialCwnd, Ssthresh = MaxCwnd, Tokens, LastPace;
            public double Srtt = 0.2, Rttvar = 0.1, MinRtt = double.MaxValue;
            public bool HasRtt;
            public int RtoBackoff;
            // Reordering tolerance (RACK-like): grows when a packet declared lost is acknowledged after all.
            public int ReorderThreshold = 3;
            public double TimeFactor = 9.0 / 8.0;
            public readonly Dictionary<uint, SentPacket> RecentlyLost = new Dictionary<uint, SentPacket>();
            public long Spurious;
            public double IntervalStart;
            public int IntSent, IntLost;
            public long IntAcked;                         // bytes acknowledged in this round trip
            public readonly double[] BwSamples = new double[10]; // delivery rate per round trip (bytes/s)
            public int BwIndex;
            public double BtlBw;                          // max of the last 10 round trips
            public double MinRttAt;
            public double LastAckProgress;                // last time an ACK acknowledged something new
            public int Mtu = BaseMtu, ProbeTries;
            public double NextProbe;
            // UDP packets in
            public uint RecvLargest;
            public readonly uint[] RecvSeen = new uint[ReplayWindow]; // packet numbers seen, by pn % window
            public readonly List<uint> AckPending = new List<uint>();
            public readonly List<uint> AckPrevious = new List<uint>();
            public bool AckEliciting;
            public double FirstUnackedRecv;
            public double PendingPong = double.NaN;
            // TCP path RTT (control ping on lane 0)
            public double TcpRtt, TcpRttVar;
            public int TcpRttSamples;
            public double LastTcpPing;
            // stats
            public long UdpBytesOut, UdpBytesIn, PacketsSent, PacketsLost, FragsResent;

            public double Rto => Math.Min(3.0, Math.Max(0.1, Srtt + 4 * Rttvar)) * (1 << Math.Min(RtoBackoff, 5));
            public int MaxPlain => Mtu - SecureOverhead;
        }

        private readonly HashSet<ulong> _removed = new HashSet<ulong>(); // disconnected peers: never re-created

        private Link GetLink(ulong peer)
        {
            if (_closed || _removed.Contains(peer)) return null;
            if (!_links.TryGetValue(peer, out Link l))
            {
                l = new Link { Peer = peer };
                _links[peer] = l;
            }
            return l;
        }

        // =====================================================================================
        // ITransport
        // =====================================================================================

        public bool Send(ulong peer, byte[] data, bool reliable)
        {
            lock (_lock)
            {
                if (_closed) return false;
                Link l = GetLink(peer);
                if (l == null) return false; // a peer that left
                // A version without this layer: plain packets, so it can still read our version-mismatch answer.
                if (l.Legacy) return _base.Send(peer, data, reliable);
                double now = Clock();
                if (!reliable) return SendUnreliable(l, data, now);

                int ch = ChannelOf(data);
                if (!_isHost && data.Length > 0 && data[0] == 1 && l.FirstHello == null) { l.FirstHello = data; l.FirstSendAt = now; }
                SendChannel tx = l.Tx[ch];
                uint seq = tx.NextSeq++;
                if (UdpFor(l, ch) && data.Length <= MaxDirectMessage)
                {
                    // Fragments always fit the base MTU (1200), so a step down never strands a re-send; a larger
                    // proven MTU only packs more frames per packet. Room is left for an ACK frame.
                    int fs = BaseMtu - SecureOverhead - StreamFrameHeader - 24;
                    var m = new OutMsg { Ch = ch, Seq = seq, Data = data, FragSize = fs, Frags = Math.Max(1, (data.Length + fs - 1) / fs) };
                    m.State = new byte[m.Frags];
                    tx.Out.Add(m);
                    tx.PendingBytes += data.Length;
                    if (ch != ChBulk) Flush(l, now); // small gameplay messages leave right away
                    return true;
                }
                return SendTcpReliable(l, ch, seq, data);
            }
        }

        /// <summary>
        /// The path for a channel: UDP when the direct link is up, except gameplay and control when the tunnel
        /// measured clearly faster (a direct link that routes badly). Bulk always prefers UDP (throughput).
        /// </summary>
        private static bool UdpFor(Link l, int ch) => l.SendDirect && (ch == ChBulk || l.GameOnDirect);

        private bool SendTcpReliable(Link l, int ch, uint seq, byte[] data)
        {
            var b = new byte[6 + data.Length];
            b[0] = TagReliable;
            b[1] = (byte)ch;
            WriteU32(b, 2, seq);
            Buffer.BlockCopy(data, 0, b, 6, data.Length);
            l.LastBaseSend = Clock();
            if (_lanes != null)
            {
                if (ch == ChBulk && _lanes.HasLane(l.Peer, LaneBulk))
                {
                    // Kept until the receiver acknowledges it: if lane 2 drops, it goes again on lane 0.
                    l.TcpBulk.Enqueue(new KeyValuePair<uint, byte[]>(seq, data));
                    l.BulkLaneUp = true;
                    return _lanes.SendLane(l.Peer, LaneBulk, b, false);
                }
                return _lanes.SendLane(l.Peer, Lane0, b, false);
            }
            return _base.Send(l.Peer, b, true);
        }

        private bool SendUnreliable(Link l, byte[] data, double now)
        {
            uint useq = l.NextUseq++;
            if (l.SendDirect && l.GameOnDirect && data.Length <= l.MaxPlain - UnrelFrameHeader)
            {
                l.UnrelQueue.Enqueue(new KeyValuePair<double, byte[]>(now, data));
                // keep the sequence with the payload
                l.UnrelQueue.Enqueue(new KeyValuePair<double, byte[]>(-1, BitConverter.GetBytes(useq)));
                Flush(l, now);
                return true;
            }
            var b = new byte[5 + data.Length];
            b[0] = TagUnreliable;
            WriteU32(b, 1, useq);
            Buffer.BlockCopy(data, 0, b, 5, data.Length);
            l.LastBaseSend = now;
            if (_lanes != null && _lanes.HasLane(l.Peer, LaneSnapA))
            {
                // The same snapshot on two connections: the receiver keeps the first copy, so a stall on one
                // TCP connection (a lost segment waiting for its re-send) does not delay poses.
                bool a = _lanes.SendLane(l.Peer, LaneSnapA, b, true);
                if (_lanes.HasLane(l.Peer, LaneSnapB)) _lanes.SendLane(l.Peer, LaneSnapB, b, true);
                return a;
            }
            return _base.Send(l.Peer, b, false);
        }

        public int QueuedBytes(ulong peer)
        {
            lock (_lock)
            {
                int q = _base.QueuedBytes(peer);
                if (_links.TryGetValue(peer, out Link l))
                {
                    q += l.BytesInFlight;
                    foreach (SendChannel tx in l.Tx) q += tx.PendingBytes;
                }
                return q;
            }
        }

        public void Poll(Action<Packet> onPacket)
        {
            lock (_lock)
            {
                if (_closed) return;
                double now = Clock();
                // A callback may end the session (Close) at any point: check after each step.
                _base.Poll(p => { if (!_closed) OnBasePacket(p, onPacket, now); });
                int budget = 8192;
                while (!_closed && _udp != null && budget-- > 0 && _udp.TryReceive(out IPEndPoint from, out byte[] d))
                    OnDatagram(from, d, onPacket, now);
                if (_closed) return;
                if (_gather != null && _gather.IsCompleted) GatherDone();
                foreach (Link l in _links.Values.ToList())
                {
                    if (_closed) return;
                    if (_links.ContainsKey(l.Peer)) TickLink(l, now);
                }
            }
        }

        public void Disconnect(ulong peer)
        {
            lock (_lock)
            {
                if (_links.TryGetValue(peer, out Link l))
                {
                    if (l.SendDirect) Fallback(l, "disconnect", retry: false); // a last message (Bye) still goes out on TCP
                    RemoveLink(l);
                }
                _removed.Add(peer);
                _base.Disconnect(peer);
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                foreach (Link l in _links.Values.ToList())
                    if (l.SendDirect) Fallback(l, "session end", retry: false);
                _closed = true;
                foreach (Link l in _links.Values) DisposeCrypto(l);
                _links.Clear();
                _byLinkId.Clear();
                try { _udp?.Dispose(); } catch { }
                _udp = null;
                _base.Close();
            }
        }

        private void RemoveLink(Link l)
        {
            _links.Remove(l.Peer);
            if (l.LinkId != 0 && _byLinkId.TryGetValue(l.LinkId, out Link b) && b == l) _byLinkId.Remove(l.LinkId);
            DisposeCrypto(l);
        }

        private static void DisposeCrypto(Link l)
        {
            try { l.Aes?.Dispose(); } catch { }
            try { l.Mac?.Dispose(); } catch { }
            l.Aes = null;
            l.Mac = null;
        }

        // =====================================================================================
        // TCP path
        // =====================================================================================

        private void OnBasePacket(Packet p, Action<Packet> onPacket, double now)
        {
            if (p.Data == null)
            {
                if (_links.TryGetValue(p.From, out Link gone)) RemoveLink(gone);
                _removed.Add(p.From);
                onPacket(p);
                return;
            }
            Link l = GetLink(p.From);
            if (l == null) return; // late packet from a peer that left
            byte[] d = p.Data;
            int len = p.Length > 0 ? p.Length : d.Length;
            if (len == 0) return;
            l.HeardAny = true;
            if (d[0] == TagReliable || d[0] == TagUnreliable || d[0] == TagControl) l.Tagged = true;
            else if (!l.Tagged) l.Legacy = true;
            else return; // untagged after tagged traffic (a late untagged Hello copy): ignored
            if (!l.BaseSeen)
            {
                l.BaseSeen = true;
                // Host: hand the client a token for its extra TCP lanes.
                if (_isHost && l.Tagged && _lanes != null && !l.LanesAsked)
                {
                    l.LanesAsked = true;
                    ulong token = RandomU64();
                    _lanes.RegisterLaneToken(l.Peer, token);
                    SendControl(l, CtlLaneToken, w => w.Write(token));
                }
            }
            switch (d[0])
            {
                case TagReliable:
                    if (len < 6 || d[1] >= Channels) return;
                    AcceptComplete(l, d[1], ReadU32(d, 2), Slice(d, 6, len - 6), onPacket);
                    return;
                case TagUnreliable:
                    if (len < 5) return;
                    if (AcceptUnreliable(l, ReadU32(d, 1)))
                        onPacket(new Packet { From = p.From, Data = Slice(d, 5, len - 5), Length = len - 5, Reliable = false });
                    return;
                case TagControl:
                    try { OnControl(l, d, len, now); }
                    catch (Exception ex) { Log("Direct: bad control message: " + ex.Message); }
                    return;
                default:
                    onPacket(p); // untagged: a peer without this layer (version check)
                    return;
            }
        }

        private void AcceptComplete(Link l, int ch, uint seq, byte[] payload, Action<Packet> onPacket)
        {
            RecvChannel rx = l.Rx[ch];
            if (seq < rx.Next || rx.Ready.ContainsKey(seq)) return; // duplicate (sent on both paths)
            rx.Partial.Remove(seq);
            rx.Ready[seq] = payload;
            while (rx.Ready.TryGetValue(rx.Next, out byte[] msg))
            {
                rx.Ready.Remove(rx.Next);
                rx.Next++;
                onPacket(new Packet { From = l.Peer, Data = msg, Length = msg.Length, Reliable = true });
            }
        }

        /// <summary>Unreliable messages: drop duplicates (two lanes, two paths) and copies older than the window.</summary>
        private static bool AcceptUnreliable(Link l, uint useq)
        {
            if (!l.UnrelAny) { l.UnrelAny = true; l.UnrelNewest = useq; l.UnrelSeen = 1; return true; }
            if (useq > l.UnrelNewest)
            {
                uint shift = useq - l.UnrelNewest;
                l.UnrelSeen = shift >= 64 ? 1UL : (l.UnrelSeen << (int)shift) | 1UL;
                l.UnrelNewest = useq;
                return true;
            }
            uint back = l.UnrelNewest - useq;
            if (back >= 64) return false; // stale
            ulong bit = 1UL << (int)back;
            if ((l.UnrelSeen & bit) != 0) return false;
            l.UnrelSeen |= bit;
            return true;
        }

        private void SendControl(Link l, byte sub, Action<BinaryWriter> body)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(TagControl);
            w.Write(sub);
            body?.Invoke(w);
            l.LastBaseSend = Clock();
            _base.Send(l.Peer, ms.ToArray(), true);
        }

        private static void WriteCands(BinaryWriter w, List<IPEndPoint> cands)
        {
            var list = cands.Where(c => c.AddressFamily == AddressFamily.InterNetwork).Take(16).ToList();
            w.Write((byte)list.Count);
            foreach (var c in list) { w.Write(c.Address.GetAddressBytes()); w.Write((ushort)c.Port); }
        }

        private static List<IPEndPoint> ReadCands(BinaryReader r)
        {
            int n = r.ReadByte();
            var list = new List<IPEndPoint>();
            for (int i = 0; i < n; i++) list.Add(new IPEndPoint(new IPAddress(r.ReadBytes(4)), r.ReadUInt16()));
            return list;
        }

        private void OnControl(Link l, byte[] d, int len, double now)
        {
            var r = new BinaryReader(new MemoryStream(d, 2, len - 2));
            switch (d[1])
            {
                case CtlOffer when _isHost:
                    l.RemoteCands = ReadCands(r);
                    l.OfferId = r.BaseStream.Position + 4 <= r.BaseStream.Length ? r.ReadUInt32() : 0;
                    if (l.SendDirect) Fallback(l, "the client asked for a new direct connection", retry: false);
                    ResetDirect(l);
                    l.State = DState.Gathering; // answer once our own candidates are known
                    Log($"Direct: offer from {l.Peer} with {l.RemoteCands.Count} candidate(s): {string.Join(", ", l.RemoteCands)}");
                    EnsureGather(now);
                    return;
                case CtlAnswer when !_isHost:
                {
                    uint id = r.ReadUInt32();
                    byte[] secret = r.ReadBytes(16);
                    List<IPEndPoint> cands = ReadCands(r);
                    uint offer = r.BaseStream.Position + 4 <= r.BaseStream.Length ? r.ReadUInt32() : 0;
                    if (l.State != DState.WaitAnswer || offer != l.OfferId) return; // an answer to an older offer
                    SetLinkId(l, id);
                    SetSecret(l, secret);
                    l.RemoteCands = cands;
                    StartPunch(l, now);
                    Log($"Direct: answer from the host with {cands.Count} candidate(s): {string.Join(", ", cands)}");
                    return;
                }
                case CtlLaneToken when !_isHost:
                    if (_lanes != null && !l.LanesAsked)
                    {
                        l.LanesAsked = true;
                        _lanes.OpenLanes(l.Peer, r.ReadUInt64());
                    }
                    return;
                case CtlPing:
                {
                    double t = r.ReadDouble();
                    SendControl(l, CtlPong, w => w.Write(t));
                    return;
                }
                case CtlPong:
                {
                    double rtt = now - r.ReadDouble();
                    if (rtt < 0 || rtt > 10) return;
                    if (l.TcpRttSamples == 0) { l.TcpRtt = rtt; l.TcpRttVar = rtt / 2; }
                    else { l.TcpRttVar = 0.75 * l.TcpRttVar + 0.25 * Math.Abs(l.TcpRtt - rtt); l.TcpRtt = 0.875 * l.TcpRtt + 0.125 * rtt; }
                    l.TcpRttSamples++;
                    return;
                }
                case CtlDown:
                    // The peer went back to the tunnel: do the same (both sides always agree on the path); the
                    // client tries again soon.
                    if (l.SendDirect || l.State == DState.Up)
                        Fallback(l, "the other side went back to the tunnel", retry: true, tell: false);
                    return;
                case CtlChanAck:
                {
                    uint next = r.ReadUInt32();
                    while (l.TcpBulk.Count > 0 && l.TcpBulk.Peek().Key < next) l.TcpBulk.Dequeue();
                    return;
                }
                case CtlKeepAlive:
                    return;
            }
        }

        // =====================================================================================
        // Direct setup
        // =====================================================================================

        private void EnsureUdp()
        {
            if (_closed) return;
            if (_udp == null) _udp = _socketFactory();
        }

        private void EnsureGather(double now)
        {
            EnsureUdp();
            if (_myCandidates != null && now - _candidatesAt < 60) { GatherReady(); return; }
            if (_gather == null) _gather = _udp.GatherCandidatesAsync();
        }

        private void GatherDone()
        {
            try { _myCandidates = _gather.Result ?? new List<IPEndPoint>(); }
            catch (Exception ex) { _myCandidates = new List<IPEndPoint>(); Log("Direct: candidate gathering failed: " + ex.Message); }
            _gather = null;
            _candidatesAt = Clock();
            Log("Direct: my candidates: " + string.Join(", ", _myCandidates) + (_udp?.NatInfo != null ? " (" + _udp.NatInfo + ")" : ""));
            GatherReady();
        }

        private void GatherReady()
        {
            double now = Clock();
            foreach (Link l in _links.Values.ToList())
            {
                if (l.State != DState.Gathering) continue;
                if (_isHost)
                {
                    var id = new byte[4];
                    uint linkId;
                    do { _rng.GetBytes(id); linkId = BitConverter.ToUInt32(id, 0); } while (linkId == 0 || _byLinkId.ContainsKey(linkId));
                    SetLinkId(l, linkId);
                    var secret = new byte[16];
                    _rng.GetBytes(secret);
                    SetSecret(l, secret);
                    SendControl(l, CtlAnswer, w => { w.Write(l.LinkId); w.Write(l.Secret); WriteCands(w, _myCandidates); w.Write(l.OfferId); });
                    StartPunch(l, now);
                }
                else
                {
                    l.OfferId = (uint)RandomU64() | 1u;
                    SendControl(l, CtlOffer, w => { WriteCands(w, _myCandidates); w.Write(l.OfferId); });
                    l.State = DState.WaitAnswer;
                    l.PunchStart = now; // the answer timeout runs from here
                }
            }
        }

        private void SetLinkId(Link l, uint id)
        {
            if (l.LinkId != 0 && _byLinkId.TryGetValue(l.LinkId, out Link b) && b == l) _byLinkId.Remove(l.LinkId);
            l.LinkId = id;
            _byLinkId[id] = l;
        }

        /// <summary>Keys from the session secret: AES-128 (CTR) for the payload, HMAC-SHA256 for the tag.</summary>
        private static void SetSecret(Link l, byte[] secret)
        {
            DisposeCrypto(l);
            l.Secret = secret;
            using (var kdf = new HMACSHA256(secret))
            {
                byte[] enc = kdf.ComputeHash(System.Text.Encoding.ASCII.GetBytes("gmscoop direct enc v1"));
                l.MacKey = kdf.ComputeHash(System.Text.Encoding.ASCII.GetBytes("gmscoop direct mac v1"));
                var aes = System.Security.Cryptography.Aes.Create();
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                var key = new byte[16];
                Buffer.BlockCopy(enc, 0, key, 0, 16);
                l.Aes = aes.CreateEncryptor(key, null);
            }
            l.Mac = new HMACSHA256(l.MacKey);
        }

        private void ResetDirect(Link l)
        {
            if (l.SendDirect) Fallback(l, "reset", retry: false, tell: false);
            l.Remote = null;
            l.State = DState.Idle;
        }

        private void StartPunch(Link l, double now)
        {
            EnsureUdp();
            if (l.SendDirect) Fallback(l, "new punch", retry: false, tell: false);
            l.State = DState.Punching;
            l.PunchStart = now;
            l.LastPunch = double.NegativeInfinity;
            l.Remote = null;
            l.RecvLargest = 0;
            Array.Clear(l.RecvSeen, 0, l.RecvSeen.Length);
        }

        private byte[] PunchTag(Link l, byte type, ulong nonce)
        {
            var msg = new byte[13];
            msg[0] = type;
            WriteU32(msg, 1, l.LinkId);
            Buffer.BlockCopy(BitConverter.GetBytes(nonce), 0, msg, 5, 8);
            byte[] full = l.Mac.ComputeHash(msg);
            var tag = new byte[8];
            Buffer.BlockCopy(full, 0, tag, 0, 8);
            return tag;
        }

        private void SendPunch(Link l, byte type, ulong nonce, IPEndPoint to)
        {
            var d = new byte[Header + 16];
            d[0] = (byte)'G'; d[1] = (byte)'C'; d[2] = type;
            WriteU32(d, 3, l.LinkId);
            Buffer.BlockCopy(BitConverter.GetBytes(nonce), 0, d, Header, 8);
            Buffer.BlockCopy(PunchTag(l, type, nonce), 0, d, Header + 8, 8);
            SendRaw(l, d, d.Length, to);
        }

        private void TickLink(Link l, double now)
        {
            // Client: offer a direct connection once the session is up, and again after a failure.
            if (!_isHost && _offerDirect && l.BaseSeen && !l.Legacy)
            {
                if (l.State == DState.Idle || (l.State == DState.Failed && now >= l.NextRetry))
                {
                    l.State = DState.Gathering;
                    EnsureGather(now);
                }
                else if (l.State == DState.WaitAnswer && now - l.PunchStart > 15) Fail(l, "no answer from the host", now);
            }

            if (l.State == DState.Punching && !l.SendDirect)
            {
                if (now - l.PunchStart > PunchTimeout) Fail(l, "no UDP path (both routers block it, or a strict NAT)", now);
                else if (now - l.LastPunch >= PunchInterval)
                {
                    l.LastPunch = now;
                    var targets = new List<IPEndPoint>(l.RemoteCands);
                    if (l.Remote != null && !targets.Contains(l.Remote)) targets.Add(l.Remote);
                    ulong nonce = RandomU64();
                    Remember(l.SentNonces, nonce);
                    foreach (IPEndPoint t in targets) SendPunch(l, UPunch, nonce, t);
                }
            }

            // Client with no answer at all to its tagged Hello: an old host drops tagged packets. Send the Hello once
            // untagged, so an old host answers with its version-mismatch Bye.
            if (!_isHost && !l.HeardAny && !l.LegacyHelloSent && l.FirstHello != null && now - l.FirstSendAt > LegacyHelloAfter)
            {
                l.LegacyHelloSent = true;
                _base.Send(l.Peer, l.FirstHello, true);
                Log("Direct: no answer from the host to the Hello; sent an untagged copy (an older GMS Co-op version?)");
            }

            // Lane 2 (bulk) closed: what it may have lost goes again on lane 0 (the receiver drops duplicates).
            if (l.BulkLaneUp && _lanes != null && !_lanes.HasLane(l.Peer, LaneBulk))
            {
                l.BulkLaneUp = false;
                int n = l.TcpBulk.Count;
                var again = l.TcpBulk.ToList();
                l.TcpBulk.Clear();
                foreach (var kv in again) SendTcpReliable(l, ChBulk, kv.Key, kv.Value);
                Log($"Direct: bulk lane to {l.Peer} closed; {n} unacknowledged message(s) re-sent on the main connection");
            }

            // Receiver side of that: acknowledge the bulk channel over the tunnel (cumulative).
            uint bulkNext = l.Rx[ChBulk].Next;
            if (l.Tagged && bulkNext != l.ChanAckSent && now - l.ChanAckAt >= 0.25)
            {
                l.ChanAckSent = bulkNext;
                l.ChanAckAt = now;
                SendControl(l, CtlChanAck, w => w.Write(bulkNext));
            }

            foreach (SendChannel tx in l.Tx)
                if (tx.Done > 32 && tx.Done * 2 >= tx.Out.Count) Compact(tx);

            // Tunnel RTT probe and keep-alive on lane 0 (also while UDP carries everything).
            if (l.Tagged && !l.Legacy && now - l.LastTcpPing >= BaseKeepAlive)
            {
                l.LastTcpPing = now;
                SendControl(l, CtlPing, w => w.Write(now));
            }

            if (l.SendDirect)
            {
                if (now - l.LastUdpRecv > StallTimeout) { Fallback(l, $"no UDP for {StallTimeout:0.#} s", retry: true); return; }
                ChoosePath(l);
                DetectLoss(l, now);
                if (l.SendDirect) Flush(l, now);
                if (now >= l.NextStats) { l.NextStats = now + 120; Log("Direct: " + l.Peer + ": " + StatsOf(l)); }
            }
            else if (l.Remote != null && (l.AckPending.Count > 0 || !double.IsNaN(l.PendingPong))) Flush(l, now); // acks for a peer already sending on UDP
        }

        /// <summary>Drops fully acknowledged messages from the send list (O(n) once, not per acknowledgement).</summary>
        private static void Compact(SendChannel tx)
        {
            tx.Out.RemoveAll(m => m.AckedCount == m.Frags);
            tx.Done = 0;
            tx.Cursor = 0;
            while (tx.Cursor < tx.Out.Count && tx.Out[tx.Cursor].NextFrag >= tx.Out[tx.Cursor].Frags) tx.Cursor++;
        }

        /// <summary>Gameplay takes the faster healthy path: the direct link, unless the tunnel is clearly faster.</summary>
        private void ChoosePath(Link l)
        {
            if (!l.HasRtt || l.TcpRttSamples < 3) { l.GameOnDirect = true; return; }
            bool direct = l.GameOnDirect;
            if (direct && l.TcpRtt + 0.015 < l.Srtt * 0.8) direct = false;          // tunnel 20 % and 15 ms faster
            else if (!direct && l.Srtt <= l.TcpRtt + 0.005) direct = true;
            if (direct != l.GameOnDirect)
            {
                l.GameOnDirect = direct;
                Log($"Direct: gameplay with {l.Peer} now on {(direct ? "the direct link" : _baseLabel(l.Peer))} " +
                    $"(direct {l.Srtt * 1000:0} ms, tunnel {l.TcpRtt * 1000:0} ms)");
            }
        }

        private void Fail(Link l, string why, double now, bool quick = false)
        {
            if (l.SendDirect) { Fallback(l, why, retry: true); return; }
            // After a link that worked, the first retry comes soon (a freeze, a router hiccup).
            double delay = quick ? 3 : RetryDelays[Math.Min(l.Retries, RetryDelays.Length - 1)];
            if (!quick) l.Retries++;
            l.State = DState.Failed;
            l.NextRetry = now + delay;
            Log($"Direct: no direct connection with {l.Peer}: {why}." + (_isHost ? "" : $" Staying on the current path; next try in {delay:0} s."));
        }

        /// <summary>Back to the TCP path: every message not yet acknowledged over UDP is re-sent there, in order.</summary>
        private void Fallback(Link l, string why, bool retry, bool tell = true)
        {
            bool wasUp = l.SendDirect;
            l.SendDirect = false;
            int n = 0;
            for (int ch = 0; ch < Channels; ch++)
            {
                SendChannel tx = l.Tx[ch];
                foreach (OutMsg m in tx.Out)
                    if (m.AckedCount < m.Frags) { SendTcpReliable(l, ch, m.Seq, m.Data); n++; }
                tx.Out.Clear();
                tx.Lost.Clear();
                tx.PendingBytes = 0;
                tx.Cursor = 0;
                tx.Done = 0;
            }
            // Tell the other side, so it does not keep sending on UDP while this side only reads the tunnel.
            if (tell && !_closed) SendControl(l, CtlDown, null);
            l.UnrelQueue.Clear();
            l.Inflight.Clear();
            l.RecentlyLost.Clear();
            l.BytesInFlight = 0;
            l.Mtu = BaseMtu;
            l.ProbeTries = 0;
            Log($"Direct: back to {_baseLabel(l.Peer)} with {l.Peer} ({why}); {n} message(s) re-sent there. {StatsOf(l)}");
            if (retry) Fail(l, why, Clock(), quick: wasUp);
            else l.State = DState.Idle;
        }

        // =====================================================================================
        // UDP: receive
        // =====================================================================================

        private void SendRaw(Link l, byte[] d, int len, IPEndPoint to)
        {
            if (_udp == null || to == null) return;
            try { _udp.SendTo(d, len, to); l.UdpBytesOut += len; }
            catch (Exception) { /* unreachable candidate: ignored */ }
        }

        private void OnDatagram(IPEndPoint from, byte[] d, Action<Packet> onPacket, double now)
        {
            if (d.Length < Header || d[0] != 'G' || d[1] != 'C') return;
            byte type = d[2];
            if (!_byLinkId.TryGetValue(ReadU32(d, 3), out Link l) || l.Mac == null) return;

            if (type == UPunch || type == UPunchAck)
            {
                if (d.Length < Header + 16) return;
                ulong nonce = BitConverter.ToUInt64(d, Header);
                byte[] tag = PunchTag(l, type, nonce);
                for (int i = 0; i < 8; i++) if (tag[i] != d[Header + 8 + i]) return; // not from our peer
                // Freshness: a PUNCH_ACK must answer a nonce this side sent; a PUNCH nonce is used once (a
                // replayed copy from another address must not move the link).
                if (type == UPunchAck ? !l.SentNonces.Contains(nonce) : l.SeenNonces.Contains(nonce)) return;
                l.UdpBytesIn += d.Length;
                l.LastUdpRecv = now;
                if (type == UPunch)
                {
                    Remember(l.SeenNonces, nonce);
                    if (!l.SendDirect || l.Remote == null) l.Remote = from; // also a peer-reflexive address (symmetric NAT)
                    SendPunch(l, UPunchAck, nonce, from);
                }
                else if (!l.SendDirect && l.State == DState.Punching)
                {
                    l.Remote = from;
                    l.SendDirect = true;
                    l.GameOnDirect = true;
                    l.State = DState.Up;
                    l.WasUp = true;
                    l.Retries = 0;
                    l.BigLost = 0;
                    l.LastPing = double.NegativeInfinity;
                    l.Cwnd = InitialCwnd;
                    l.Ssthresh = MaxCwnd;
                    l.RtoBackoff = 0;
                    l.NextProbe = now + 1;
                    l.NextStats = now + 30;
                    l.LastPace = now;
                    l.Tokens = l.Cwnd;
                    Log($"Direct: connected with {l.Peer} at {from} after {now - l.PunchStart:0.00} s (was {_baseLabel(l.Peer)})");
                }
                return;
            }
            if (type != USecure || d.Length < SecureOverhead + 1) return;

            // Authenticate, check freshness (replay window), then decrypt.
            int tagAt = d.Length - 8;
            byte[] mac = l.Mac.ComputeHash(d, 0, tagAt);
            int diff = 0;
            for (int i = 0; i < 8; i++) diff |= mac[i] ^ d[tagAt + i];
            if (diff != 0) return;
            uint pn = ReadU32(d, Header);
            if (!FreshPacket(l, pn)) return;
            if (l.Remote == null || !l.Remote.Equals(from))
            {
                if (l.Remote != null) Log($"Direct: {l.Peer} now sends from {from} (was {l.Remote}): router changed the port, following it");
                l.Remote = from;
            }
            l.UdpBytesIn += d.Length;
            l.LastUdpRecv = now;
            int plainLen = tagAt - Header - 4;
            byte[] plain = Crypt(l, pn, d, Header + 4, plainLen, fromHost: !_isHost);
            ParseFrames(l, pn, plain, onPacket, now);
        }

        /// <summary>Sliding window of 4096 packet numbers: a replayed or very old packet is dropped.</summary>
        private static bool FreshPacket(Link l, uint pn)
        {
            if (pn == 0) return false;
            if (pn + ReplayWindow <= l.RecvLargest) return false; // older than the window
            int slot = (int)(pn % ReplayWindow);
            if (l.RecvSeen[slot] == pn) return false;            // seen: a replay or a duplicate
            l.RecvSeen[slot] = pn;
            if (pn > l.RecvLargest) l.RecvLargest = pn;
            return true;
        }

        private void ParseFrames(Link l, uint pn, byte[] p, Action<Packet> onPacket, double now)
        {
            int i = 0;
            bool eliciting = false;
            while (i < p.Length)
            {
                byte f = p[i++];
                switch (f)
                {
                    case FStream:
                    {
                        if (i + 11 > p.Length) return;
                        int ch = p[i];
                        uint seq = ReadU32(p, i + 1);
                        int frag = BitConverter.ToUInt16(p, i + 5), count = BitConverter.ToUInt16(p, i + 7), len = BitConverter.ToUInt16(p, i + 9);
                        i += 11;
                        if (i + len > p.Length || ch >= Channels) return;
                        OnStream(l, ch, seq, frag, count, p, i, len, onPacket);
                        i += len;
                        eliciting = true;
                        break;
                    }
                    case FUnrel:
                    {
                        if (i + 6 > p.Length) return;
                        uint useq = ReadU32(p, i);
                        int len = BitConverter.ToUInt16(p, i + 4);
                        i += 6;
                        if (i + len > p.Length) return;
                        if (AcceptUnreliable(l, useq))
                            onPacket(new Packet { From = l.Peer, Data = Slice(p, i, len), Length = len, Reliable = false });
                        i += len;
                        break;
                    }
                    case FAck:
                    {
                        if (i + 3 > p.Length) return;
                        double delay = BitConverter.ToUInt16(p, i) / 1000.0;
                        int n = p[i + 2];
                        i += 3;
                        if (i + n * 6 > p.Length) return;
                        OnAck(l, p, i, n, delay, now);
                        i += n * 6;
                        break;
                    }
                    case FPing:
                        if (i + 8 > p.Length) return;
                        l.PendingPong = BitConverter.ToDouble(p, i);
                        i += 8;
                        eliciting = true;
                        break;
                    case FPong:
                        if (i + 8 > p.Length) return;
                        // RTT samples come from acknowledgements; PONG only proves the path is alive.
                        i += 8;
                        break;
                    case FPad:
                        if (i + 2 > p.Length) return;
                        i += 2 + BitConverter.ToUInt16(p, i);
                        eliciting = true;
                        break;
                    default:
                        return; // unknown frame: ignore the rest
                }
            }
            if (eliciting)
            {
                if (l.AckPending.Count == 0) l.FirstUnackedRecv = now;
                l.AckPending.Add(pn);
                l.AckEliciting = true;
            }
        }

        private void OnStream(Link l, int ch, uint seq, int frag, int count, byte[] p, int off, int len, Action<Packet> onPacket)
        {
            if (count <= 0 || frag >= count) return;
            RecvChannel rx = l.Rx[ch];
            if (seq < rx.Next || rx.Ready.ContainsKey(seq)) return; // duplicate
            if (count == 1) { AcceptComplete(l, ch, seq, Slice(p, off, len), onPacket); return; }
            if (!rx.Partial.TryGetValue(seq, out Assembly a))
            {
                a = new Assembly { Count = count, Parts = new byte[count][] };
                rx.Partial[seq] = a;
            }
            if (a.Count != count || a.Parts[frag] != null) return;
            a.Parts[frag] = Slice(p, off, len);
            a.Have++;
            a.Bytes += len;
            if (a.Have < a.Count) return;
            var msg = new byte[a.Bytes];
            int q = 0;
            foreach (byte[] part in a.Parts) { Buffer.BlockCopy(part, 0, msg, q, part.Length); q += part.Length; }
            rx.Partial.Remove(seq);
            AcceptComplete(l, ch, seq, msg, onPacket);
        }

        private void OnAck(Link l, byte[] p, int off, int n, double ackDelay, double now)
        {
            uint largestNew = 0;
            double largestNewTime = 0;
            bool any = false;
            for (int k = 0; k < n; k++)
            {
                uint first = ReadU32(p, off + k * 6);
                int count = BitConverter.ToUInt16(p, off + k * 6 + 4);
                for (int j = 0; j < count; j++)
                {
                    uint pn = first + (uint)j;
                    if (!l.Inflight.TryGetValue(pn, out SentPacket sp))
                    {
                        // Declared lost, then acknowledged: it was only reordered. Tolerate more reordering,
                        // and count its fragments as delivered (their re-sent copies are dropped as duplicates).
                        if (l.RecentlyLost.TryGetValue(pn, out SentPacket late))
                        {
                            l.RecentlyLost.Remove(pn);
                            l.Spurious++;
                            int distance = (int)Math.Min(256, Math.Max(0, (long)l.LargestAcked - pn + 1));
                            if (distance + 1 > l.ReorderThreshold) l.ReorderThreshold = Math.Min(256, distance + 1);
                            l.TimeFactor = Math.Min(2.0, l.TimeFactor * 1.25);
                            if (late.Frags != null) foreach (FragRef fr in late.Frags) AckFrag(l, fr);
                        }
                        continue;
                    }
                    l.Inflight.Remove(pn);
                    any = true;
                    l.IntAcked += sp.Size;
                    if (sp.AckEliciting) l.BytesInFlight -= sp.Size;
                    if (pn > largestNew) { largestNew = pn; largestNewTime = sp.Time; }
                    if (sp.Probe && l.Mtu < ProbeMtu)
                    {
                        l.Mtu = ProbeMtu;
                        Log($"Direct: path to {l.Peer} carries {ProbeMtu}-byte packets");
                    }
                    if (sp.Frags != null)
                        foreach (FragRef fr in sp.Frags) AckFrag(l, fr);
                }
            }
            if (!any) return;
            l.RtoBackoff = 0;
            l.LastAckProgress = now;
            if (largestNew > l.LargestAcked) { l.LargestAcked = largestNew; l.LargestAckedTime = largestNewTime; }
            if (largestNew != 0) RttSample(l, Math.Max(0, now - largestNewTime - ackDelay), now - largestNewTime, now);
        }

        private static void AckFrag(Link l, FragRef fr)
        {
            OutMsg m = fr.Msg;
            if (m.State[fr.Frag] == 2) return;
            m.State[fr.Frag] = 2;
            m.AckedCount++;
            int fl = m.FragLen(fr.Frag);
            // Slow start, then additive increase (about 4 packets per round trip).
            if (l.Cwnd < l.Ssthresh) l.Cwnd += fl;
            else l.Cwnd += 4.0 * l.Mtu * fl / l.Cwnd;
            if (l.Cwnd > MaxCwnd) l.Cwnd = MaxCwnd;
            if (m.AckedCount == m.Frags) l.Tx[m.Ch].Done++; // removed in bulk by Compact
        }

        private static void RttSample(Link l, double rtt, double rawRtt, double now)
        {
            if (rawRtt < 0 || rawRtt > 10) return;
            // Minimum RTT. It may rise only slowly (5 % per 30 s without a lower sample: a route change), so
            // the short standing queue the window allows does not inflate it.
            if (rawRtt < l.MinRtt) { l.MinRtt = rawRtt; l.MinRttAt = now; }
            else if (now - l.MinRttAt > 30) { l.MinRtt = Math.Min(rawRtt, l.MinRtt * 1.05); l.MinRttAt = now; }
            if (!l.HasRtt) { l.Srtt = rtt; l.Rttvar = rtt / 2; l.HasRtt = true; return; }
            l.Rttvar = 0.75 * l.Rttvar + 0.25 * Math.Abs(l.Srtt - rtt);
            l.Srtt = 0.875 * l.Srtt + 0.125 * rtt;
        }

        /// <summary>
        /// Packet threshold (3 newer packets acknowledged = the duplicate-ACK rule), time threshold (9/8 RTT
        /// behind the newest acknowledged packet), and the retransmission timeout with back-off.
        /// </summary>
        private void DetectLoss(Link l, double now)
        {
            if (l.Inflight.Count == 0) return;
            double timeThreshold = Math.Max(l.TimeFactor * (l.HasRtt ? l.Srtt : 0.1), 0.005);
            if (l.RecentlyLost.Count > 0)
            {
                var old = l.RecentlyLost.Where(kv => now - kv.Value.Time > 3).Select(kv => kv.Key).ToList();
                foreach (uint k in old) l.RecentlyLost.Remove(k);
            }
            List<SentPacket> lost = null;
            double oldest = double.MaxValue;
            foreach (SentPacket sp in l.Inflight.Values)
            {
                if (sp.Time < oldest) oldest = sp.Time;
                bool byCount = sp.Pn + (uint)l.ReorderThreshold <= l.LargestAcked;
                bool byTime = sp.Pn < l.LargestAcked && l.LargestAckedTime - sp.Time > timeThreshold && now - sp.Time > timeThreshold;
                if (byCount || byTime) (lost ?? (lost = new List<SentPacket>())).Add(sp);
            }
            bool rto = false;
            // The timeout counts from the last ACK progress: while ACKs keep coming, losses are found by the
            // packet and time thresholds above (a wide reordering tolerance must not turn into timeouts).
            if (lost == null && now - oldest > l.Rto && now - l.LastAckProgress > Math.Max(l.Rto, timeThreshold))
            {
                // Nothing acknowledged for a whole timeout: everything that old is lost; back off.
                rto = true;
                foreach (SentPacket sp in l.Inflight.Values)
                    if (now - sp.Time > l.Rto) (lost ?? (lost = new List<SentPacket>())).Add(sp);
                l.RtoBackoff++;
            }
            if (lost == null) return;
            foreach (SentPacket sp in lost)
            {
                l.Inflight.Remove(sp.Pn);
                if (sp.AckEliciting) l.BytesInFlight -= sp.Size;
                if (l.RecentlyLost.Count < 20000) l.RecentlyLost[sp.Pn] = sp;
                l.PacketsLost++;
                l.IntLost++;
                if (sp.Probe) { if (++l.ProbeTries >= 3 && l.Mtu < ProbeMtu) l.NextProbe = now + 60; else l.NextProbe = now + 1; }
                else if (sp.Size > BaseMtu)
                {
                    // Large packets lost again and again while small ones get through: the path's MTU shrank.
                    if (now - l.BigLostSince > 2) { l.BigLost = 0; l.BigLostSince = now; }
                    if (++l.BigLost >= 6 && l.Mtu > BaseMtu && sp.Size > l.Mtu * 0.9)
                    {
                        l.Mtu = BaseMtu;
                        l.ProbeTries = 0;
                        l.NextProbe = now + 30;
                        Log($"Direct: large packets to {l.Peer} keep getting lost; back to {BaseMtu}-byte packets");
                    }
                }
                if (sp.Frags == null) continue;
                foreach (FragRef fr in sp.Frags)
                {
                    if (fr.Msg.State[fr.Frag] != 1) continue;
                    fr.Msg.State[fr.Frag] = 0; // queued once: no storm of copies
                    l.Tx[fr.Msg.Ch].Lost.Enqueue(fr);
                    l.FragsResent++;
                }
            }
            if (rto) l.Cwnd = Math.Max(MinCwnd, l.Cwnd / 2);
            if (l.RtoBackoff >= 6) Fallback(l, "no acknowledgement after 6 timeouts", retry: true);
        }

        // =====================================================================================
        // UDP: send
        // =====================================================================================

        /// <summary>
        /// Builds and sends packets: ACK and PONG first, then snapshots, then lost fragments and new fragments by
        /// channel priority (control, gameplay, bulk), within the congestion window and the pacing budget.
        /// </summary>
        private void Flush(Link l, double now)
        {
            if (l.Remote == null || l.Mac == null) return;
            CongestionTick(l, now);
            // Pacing: the window spread over a round trip (twice as fast in slow start), refilled per poll.
            double rate = Math.Max(64 * 1024, l.Cwnd / Math.Max(l.HasRtt ? l.Srtt : 0.1, 0.005)) * (l.Cwnd < l.Ssthresh ? 2.0 : 1.25);
            l.Tokens = Math.Min(l.Tokens + rate * Math.Max(0, now - l.LastPace), Math.Max(l.Mtu * 4, rate * 0.05));
            l.LastPace = now;
            // Stale snapshots (waited > 100 ms) are not worth sending.
            while (l.UnrelQueue.Count >= 2 && now - l.UnrelQueue.Peek().Key > 0.1) { l.UnrelQueue.Dequeue(); l.UnrelQueue.Dequeue(); }

            for (int guard = 0; guard < 4096; guard++)
            {
                bool ackDue = l.AckPending.Count > 0 && (l.AckEliciting || l.AckPending.Count >= 2);
                bool pongDue = !double.IsNaN(l.PendingPong);
                bool pingDue = l.SendDirect && now - l.LastPing >= PingInterval;
                bool probeDue = l.SendDirect && l.Mtu < ProbeMtu && now >= l.NextProbe && l.Inflight.Count < 64;
                bool unrel = l.UnrelQueue.Count >= 2;
                bool dataRoom = l.SendDirect && l.Tokens >= l.Mtu * 0.5 && l.BytesInFlight + l.Mtu <= l.Cwnd;
                bool canData = dataRoom && HasStreamData(l);
                if (!ackDue && !pongDue && !pingDue && !probeDue && !unrel && !canData) break;

                // A probe goes alone (ACK/PING + padding): data never travels at an unproven size.
                int budget = (probeDue ? ProbeMtu : l.Mtu) - SecureOverhead;
                var ms = new MemoryStream(budget);
                var w = new BinaryWriter(ms);
                var frags = new List<FragRef>();
                bool eliciting = false;
                if (ackDue) WriteAck(l, w, now, budget);
                if (pongDue) { w.Write(FPong); w.Write(l.PendingPong); l.PendingPong = double.NaN; }
                if (pingDue) { w.Write(FPing); w.Write(now); l.LastPing = now; eliciting = true; }
                while (!probeDue && l.UnrelQueue.Count >= 2)
                {
                    byte[] data = l.UnrelQueue.Peek().Value;
                    if (ms.Length + UnrelFrameHeader + data.Length > budget) break;
                    l.UnrelQueue.Dequeue();
                    uint useq = BitConverter.ToUInt32(l.UnrelQueue.Dequeue().Value, 0);
                    w.Write(FUnrel); w.Write(useq); w.Write((ushort)data.Length); w.Write(data);
                }
                if (dataRoom && !probeDue)
                {
                    for (int ch = 0; ch < Channels; ch++)
                    {
                        SendChannel tx = l.Tx[ch];
                        while (tx.Lost.Count > 0)
                        {
                            FragRef fr = tx.Lost.Peek();
                            if (fr.Msg.State[fr.Frag] == 2 || fr.Msg.AckedCount == fr.Msg.Frags) { tx.Lost.Dequeue(); continue; }
                            if (ms.Length + StreamFrameHeader + fr.Msg.FragLen(fr.Frag) > budget) goto full;
                            tx.Lost.Dequeue();
                            WriteFrag(w, fr.Msg, fr.Frag);
                            fr.Msg.State[fr.Frag] = 1;
                            frags.Add(fr);
                            eliciting = true;
                        }
                        for (; tx.Cursor < tx.Out.Count; tx.Cursor++)
                        {
                            OutMsg m = tx.Out[tx.Cursor];
                            while (m.NextFrag < m.Frags)
                            {
                                int fl = m.FragLen(m.NextFrag);
                                if (ms.Length + StreamFrameHeader + fl > budget) goto full;
                                int i = m.NextFrag++;
                                tx.PendingBytes -= fl;
                                WriteFrag(w, m, i);
                                m.State[i] = 1;
                                frags.Add(new FragRef { Msg = m, Frag = i });
                                eliciting = true;
                            }
                        }
                    }
                }
                full:
                if (probeDue && ms.Length < budget - 3)
                {
                    int pad = (int)(budget - ms.Length - 3);
                    w.Write(FPad); w.Write((ushort)pad); w.Write(new byte[pad]);
                    eliciting = true;
                    l.NextProbe = now + 2;
                }
                if (ms.Length == 0) break;
                SendSecure(l, ms.ToArray(), eliciting, probeDue, frags, now);
            }
        }

        private static bool HasStreamData(Link l)
        {
            foreach (SendChannel tx in l.Tx)
            {
                if (tx.Lost.Count > 0 || tx.Cursor < tx.Out.Count) return true;
            }
            return false;
        }

        private static void WriteFrag(BinaryWriter w, OutMsg m, int i)
        {
            int fl = m.FragLen(i);
            w.Write(FStream);
            w.Write((byte)m.Ch);
            w.Write(m.Seq);
            w.Write((ushort)i);
            w.Write((ushort)m.Frags);
            w.Write((ushort)fl);
            w.Write(m.Data, i * m.FragSize, fl);
        }

        /// <summary>ACK ranges: the packets received since the last ACK, and the ones of the ACK before (sent twice).</summary>
        private static void WriteAck(Link l, BinaryWriter w, double now, int budget)
        {
            var all = new List<uint>(l.AckPending.Count + l.AckPrevious.Count);
            all.AddRange(l.AckPending);
            all.AddRange(l.AckPrevious);
            all.Sort();
            var ranges = new List<KeyValuePair<uint, int>>();
            foreach (uint pn in all)
            {
                if (ranges.Count > 0)
                {
                    var last = ranges[ranges.Count - 1];
                    if (pn < last.Key + (uint)last.Value) continue; // duplicate
                    if (pn == last.Key + (uint)last.Value && last.Value < ushort.MaxValue) { ranges[ranges.Count - 1] = new KeyValuePair<uint, int>(last.Key, last.Value + 1); continue; }
                }
                ranges.Add(new KeyValuePair<uint, int>(pn, 1));
            }
            int max = Math.Min(255, (budget / 3 - 4) / 6);
            if (ranges.Count > max) ranges = ranges.Skip(ranges.Count - max).ToList(); // the newest
            w.Write(FAck);
            w.Write((ushort)Math.Min(65535, (now - l.FirstUnackedRecv) * 1000));
            w.Write((byte)ranges.Count);
            foreach (var r in ranges) { w.Write(r.Key); w.Write((ushort)r.Value); }
            l.AckPrevious.Clear();
            l.AckPrevious.AddRange(l.AckPending);
            l.AckPending.Clear();
            l.AckEliciting = false;
        }

        private void SendSecure(Link l, byte[] plain, bool eliciting, bool probe, List<FragRef> frags, double now)
        {
            uint pn = l.NextPn++;
            var d = new byte[Header + 4 + plain.Length + 8];
            d[0] = (byte)'G'; d[1] = (byte)'C'; d[2] = USecure;
            WriteU32(d, 3, l.LinkId);
            WriteU32(d, Header, pn);
            byte[] c = Crypt(l, pn, plain, 0, plain.Length, fromHost: _isHost);
            Buffer.BlockCopy(c, 0, d, Header + 4, c.Length);
            byte[] mac = l.Mac.ComputeHash(d, 0, d.Length - 8);
            Buffer.BlockCopy(mac, 0, d, d.Length - 8, 8);
            SendRaw(l, d, d.Length, l.Remote);
            l.PacketsSent++;
            l.IntSent++;
            l.Tokens -= d.Length;
            if (eliciting || frags.Count > 0)
            {
                l.Inflight[pn] = new SentPacket { Pn = pn, Time = now, Size = d.Length, AckEliciting = true, Probe = probe, Frags = frags.Count > 0 ? frags : null };
                l.BytesInFlight += d.Length;
            }
        }

        /// <summary>AES-128-CTR: counter block = link id, direction, packet number, block index.</summary>
        private static byte[] Crypt(Link l, uint pn, byte[] src, int off, int len, bool fromHost)
        {
            var outp = new byte[len];
            int blocks = (len + 15) / 16;
            var ctr = new byte[blocks * 16];
            for (int b = 0; b < blocks; b++)
            {
                int o = b * 16;
                WriteU32(ctr, o, l.LinkId);
                ctr[o + 4] = fromHost ? (byte)0 : (byte)1;
                WriteU32(ctr, o + 5, pn);
                WriteU32(ctr, o + 12, (uint)b);
            }
            var ks = new byte[ctr.Length];
            l.Aes.TransformBlock(ctr, 0, ctr.Length, ks, 0);
            for (int i = 0; i < len; i++) outp[i] = (byte)(src[off + i] ^ ks[i]);
            return outp;
        }

        /// <summary>
        /// Once per round trip: a high loss rate (a full router queue) or a growing RTT (the queue filling up)
        /// shrinks the window; random loss below that does not (common on Wi-Fi and busy ISPs).
        /// </summary>
        private static void CongestionTick(Link l, double now)
        {
            double span = now - l.IntervalStart;
            if (span < Math.Max(l.HasRtt ? l.Srtt : 0.1, 0.05)) return;
            // Delivery rate of this round trip; the bottleneck rate is the max of the last 10 (BBR-like).
            if (span < 2 && l.IntAcked > 0)
            {
                l.BwSamples[l.BwIndex] = l.IntAcked / span;
                l.BwIndex = (l.BwIndex + 1) % l.BwSamples.Length;
                l.BtlBw = l.BwSamples.Max();
            }
            l.IntAcked = 0;
            // Keep the router queue short (gameplay shares it): at most 1.5 x the bandwidth-delay product,
            // which still lets the rate grow by half per round trip until the path is full.
            if (l.BtlBw > 0 && l.HasRtt && l.MinRtt < 5)
            {
                double cap = Math.Max(MinCwnd, 1.5 * l.BtlBw * l.MinRtt + 4 * l.Mtu);
                if (l.Cwnd > cap) { l.Cwnd = cap; if (l.Ssthresh > cap) l.Ssthresh = cap; }
            }
            double rate = l.IntSent > 20 ? (double)l.IntLost / l.IntSent : 0;
            bool queueing = l.HasRtt && l.MinRtt < 5 && l.Srtt > l.MinRtt * 2 + 0.06;
            if (rate > 0.1 || queueing)
            {
                l.Ssthresh = Math.Max(MinCwnd, l.Cwnd * (rate > 0.1 ? 0.7 : 0.9));
                l.Cwnd = l.Ssthresh;
            }
            l.IntervalStart = now;
            l.IntSent = l.IntLost = 0;
        }

        // =====================================================================================
        // helpers
        // =====================================================================================

        private static void Remember(Queue<ulong> q, ulong v)
        {
            q.Enqueue(v);
            while (q.Count > 256) q.Dequeue();
        }

        private ulong RandomU64()
        {
            var b = new byte[8];
            _rng.GetBytes(b);
            return BitConverter.ToUInt64(b, 0);
        }

        private static byte[] Slice(byte[] d, int off, int len)
        {
            var r = new byte[len];
            Buffer.BlockCopy(d, off, r, 0, len);
            return r;
        }

        private static void WriteU32(byte[] b, int off, uint v)
        {
            b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
        }

        private static uint ReadU32(byte[] b, int off) =>
            (uint)(b[off] | b[off + 1] << 8 | b[off + 2] << 16 | b[off + 3] << 24);

        /// <summary>Stats line for the log (per peer).</summary>
        internal string Stats(ulong peer)
        {
            lock (_lock) return _links.TryGetValue(peer, out Link l) ? StatsOf(l) : "";
        }

        private string StatsOf(Link l)
        {
            double lossPct = l.PacketsSent > 0 ? 100.0 * l.PacketsLost / l.PacketsSent : 0;
            return $"{(l.SendDirect ? (l.GameOnDirect ? "direct" : "direct (gameplay on tunnel)") : _baseLabel(l.Peer))}, " +
                   $"rtt {l.Srtt * 1000:0} ms (min {(l.MinRtt < 5 ? l.MinRtt * 1000 : 0):0}, tunnel {l.TcpRtt * 1000:0} ms), window {l.Cwnd / 1024:0} KB, rate {l.BtlBw / 1024:0} KB/s, mtu {l.Mtu}, " +
                   $"udp out {l.UdpBytesOut / 1024} KB in {l.UdpBytesIn / 1024} KB, packets {l.PacketsSent} lost {l.PacketsLost} ({lossPct:0.0}%, {l.Spurious} only reordered), fragments re-sent {l.FragsResent}, reorder tolerance {l.ReorderThreshold}";
        }

        internal void RawStats(ulong peer, out long packets, out long lost, out long resent, out double cwnd, out int mtu)
        {
            lock (_lock)
            {
                packets = lost = resent = 0; cwnd = 0; mtu = 0;
                if (!_links.TryGetValue(peer, out Link l)) return;
                packets = l.PacketsSent; lost = l.PacketsLost; resent = l.FragsResent; cwnd = l.Cwnd; mtu = l.Mtu;
            }
        }
    }

    /// <summary>A UDP socket (real, or simulated by the no-game harness).</summary>
    internal interface IDatagramSocket : IDisposable
    {
        void SendTo(byte[] data, int length, IPEndPoint to);
        bool TryReceive(out IPEndPoint from, out byte[] data);
        /// <summary>Public (STUN) and local addresses other peers can try to reach this socket at.</summary>
        Task<List<IPEndPoint>> GatherCandidatesAsync();
        /// <summary>NAT behavior seen by STUN, for the log (null if unknown).</summary>
        string NatInfo { get; }
    }

    /// <summary>Real UDP socket: a receive thread, STUN binding requests (RFC 5389) to public servers.</summary>
    internal sealed class UdpDatagramSocket : IDatagramSocket
    {
        private static readonly string[] StunServers =
        {
            "stun.l.google.com:19302", "stun1.l.google.com:19302", "stun.cloudflare.com:3478",
        };
        private const uint MagicCookie = 0x2112A442;

        private readonly Socket _s;
        private readonly ConcurrentQueue<KeyValuePair<IPEndPoint, byte[]>> _inbox = new ConcurrentQueue<KeyValuePair<IPEndPoint, byte[]>>();
        private readonly ConcurrentDictionary<string, IPEndPoint> _stunAnswers = new ConcurrentDictionary<string, IPEndPoint>();
        private volatile bool _closed;

        public string NatInfo { get; private set; }

        internal UdpDatagramSocket()
        {
            _s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                // Windows: an ICMP "port unreachable" (a candidate nobody listens on) must not break the receive loop.
                const int SIO_UDP_CONNRESET = -1744830452;
                _s.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }
            catch { }
            _s.ReceiveBufferSize = 4 << 20;
            _s.SendBufferSize = 4 << 20;
            _s.Bind(new IPEndPoint(IPAddress.Any, 0));
            new Thread(ReceiveLoop) { IsBackground = true, Name = "GMSCoop-UDP" }.Start();
        }

        private int LocalPort => ((IPEndPoint)_s.LocalEndPoint).Port;

        private void ReceiveLoop()
        {
            var buf = new byte[2048];
            int errors = 0;
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!_closed)
            {
                int n;
                try { n = _s.ReceiveFrom(buf, ref any); errors = 0; }
                catch (SocketException)
                {
                    if (_closed) return;
                    // A persistent error (network down) must not spin a core: back off up to 200 ms.
                    Thread.Sleep(Math.Min(200, 1 << Math.Min(errors++, 8)));
                    continue;
                }
                catch (ObjectDisposedException) { return; }
                var from = (IPEndPoint)any;
                if (n >= 20 && buf[0] == 0x01 && buf[1] == 0x01 && ReadBE32(buf, 4) == MagicCookie)
                {
                    HandleStun(buf, n);
                    continue;
                }
                var d = new byte[n];
                Buffer.BlockCopy(buf, 0, d, 0, n);
                _inbox.Enqueue(new KeyValuePair<IPEndPoint, byte[]>(new IPEndPoint(from.Address, from.Port), d));
            }
        }

        public void SendTo(byte[] data, int length, IPEndPoint to) => _s.SendTo(data, 0, length, SocketFlags.None, to);

        public bool TryReceive(out IPEndPoint from, out byte[] data)
        {
            if (_inbox.TryDequeue(out var kv)) { from = kv.Key; data = kv.Value; return true; }
            from = null; data = null;
            return false;
        }

        public Task<List<IPEndPoint>> GatherCandidatesAsync() => Task.Run(() =>
        {
            var result = new List<IPEndPoint>();
            int port = LocalPort;
            // Public mapping, as each STUN server sees it.
            var asked = new List<KeyValuePair<string, string>>(); // server, transaction id
            foreach (string server in StunServers)
            {
                try
                {
                    string[] hp = server.Split(':');
                    IPAddress ip = Dns.GetHostAddresses(hp[0]).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (ip == null) continue;
                    var to = new IPEndPoint(ip, int.Parse(hp[1]));
                    var req = new byte[20];
                    req[1] = 0x01; // binding request, length 0
                    WriteBE32(req, 4, MagicCookie);
                    var tx = new byte[12];
                    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(tx);
                    Buffer.BlockCopy(tx, 0, req, 8, 12);
                    string key = Convert.ToBase64String(tx);
                    asked.Add(new KeyValuePair<string, string>(server, key));
                    for (int i = 0; i < 2; i++) _s.SendTo(req, to);
                }
                catch (Exception) { /* DNS or network: try the others */ }
            }
            var until = DateTime.UtcNow.AddSeconds(1.5);
            while (DateTime.UtcNow < until && asked.Any(a => !_stunAnswers.ContainsKey(a.Value))) Thread.Sleep(20);
            var mapped = new List<IPEndPoint>();
            foreach (var a in asked)
                if (_stunAnswers.TryGetValue(a.Value, out IPEndPoint m)) mapped.Add(m);
            foreach (IPEndPoint m in mapped) if (!result.Contains(m)) result.Add(m);
            int ports = mapped.Select(m => m.Port).Distinct().Count();
            int ips = mapped.Select(m => m.Address.ToString()).Distinct().Count();
            NatInfo = mapped.Count == 0 ? "STUN: no answer (UDP blocked?)"
                    : mapped.Count == 1 ? "STUN: one answer, NAT type unknown"
                    : ports == 1 && ips == 1 ? $"NAT keeps the same public port for every destination (good for punching), local port {port}"
                    : $"NAT changes the public port per destination (symmetric: punching may fail), local port {port}";
            // LAN addresses (same network, or both behind the same router) and loopback (two copies on one PC).
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        IPAddress a = ua.Address;
                        if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                        byte[] b = a.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254) continue;
                        var ep = new IPEndPoint(a, port);
                        if (!result.Contains(ep)) result.Add(ep);
                    }
                }
            }
            catch (Exception) { }
            result.Add(new IPEndPoint(IPAddress.Loopback, port));
            return result;
        });

        private void HandleStun(byte[] b, int n)
        {
            var tx = new byte[12];
            Buffer.BlockCopy(b, 8, tx, 0, 12);
            int len = (b[2] << 8) | b[3];
            int p = 20;
            IPEndPoint found = null;
            while (p + 4 <= Math.Min(n, 20 + len))
            {
                int type = (b[p] << 8) | b[p + 1];
                int alen = (b[p + 2] << 8) | b[p + 3];
                int v = p + 4;
                if (v + alen > n) break;
                if ((type == 0x0020 || type == 0x0001) && alen >= 8 && b[v + 1] == 0x01)
                {
                    int port = (b[v + 2] << 8) | b[v + 3];
                    var ip = new byte[4];
                    Buffer.BlockCopy(b, v + 4, ip, 0, 4);
                    if (type == 0x0020)
                    {
                        port ^= (int)(MagicCookie >> 16);
                        for (int i = 0; i < 4; i++) ip[i] ^= (byte)(MagicCookie >> (24 - 8 * i));
                    }
                    found = new IPEndPoint(new IPAddress(ip), port);
                    if (type == 0x0020) break; // XOR-MAPPED-ADDRESS wins
                }
                p = v + ((alen + 3) & ~3);
            }
            if (found != null) _stunAnswers[Convert.ToBase64String(tx)] = found;
        }

        private static uint ReadBE32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

        private static void WriteBE32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        public void Dispose()
        {
            _closed = true;
            try { _s.Close(); } catch { }
        }
    }
}
