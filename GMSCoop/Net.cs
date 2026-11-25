using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;

namespace GMSCoop
{
    internal enum Msg : byte
    {
        Hello = 1,
        PlayerPose = 2,
        TerrainHeights = 3,
        TerrainAlphas = 4,
        Cash = 5,
        MachinePose = 6,
        Tutorial = 7,
        SaveChunk = 8,    // SaveTransfer: world chunks, host -> one client, never relayed
        SaveControl = 9,  // SaveTransfer: control (Preparing, Loaded, Resend, Failed, CatchUp), never relayed
        Purchase = 10,    // PurchaseSync
        MachineParts = 11, // MachinePartsSync
        Objects = 12,     // ObjectSync (+ PassengerSync sub 4)
        Bed = 13,         // BedSync
        Economy = 14,     // GoldSync (+ ContainerSync, DirtSync, TerrainSync drill/reset, WorldSync, FuelSync subs)
        PlayerLeft = 15,
        Ping = 16, // keep-alive + quality probe: byte kind (0 ping, 1 pong), u32 seq, f32 time; never relayed
        Bye = 17,  // string reason: the sender ends the session with us (or refuses us), never relayed
    }

    /// <summary>
    /// Session layer over a transport (Steam P2P or LAN TCP).
    /// Star topology: host relays every client message to the other clients.
    /// Peer ids are transport-level (SteamID for Steam, connection id for LAN).
    /// LocalId identifies this player inside game messages (pose packets).
    ///
    /// Lifecycle: Hello carries the mod protocol and the game version (a different protocol is refused
    /// with Bye). Every peer pings twice a second; a peer silent for 30 s (120 s while a world is being
    /// sent / loaded) is dropped, so a crashed host or client never leaves the session hanging. On the
    /// host, a client's world messages are dropped until it loaded the host's current world
    /// (SaveTransfer.PeerReady); on that client, the host's world messages are dropped between its Loaded
    /// and the host's CatchUp marker (SaveTransfer.HoldWorldMessages), because the catch-up carries them.
    ///
    /// Relay: the host relays every client world message to the other clients, except the client -> host
    /// requests listed in HostOnly. Ping, Hello, Bye, PlayerLeft and the save transfer are never relayed.
    /// Reliable host broadcasts (own and relayed) pass SaveTransfer.JournalWorld for joiners.
    ///
    /// Link quality (overlay): pings are unreliable and numbered; the peer answers each with a pong
    /// carrying our send time back. Round trip = smoothed (TCP-style SRTT / RTTVAR); loss = share of the
    /// peer's numbered pings that never arrived, per 10 s window, smoothed.
    /// </summary>
    internal static class Net
    {
        /// <summary>Wire protocol of this build. Both PCs need the same one.</summary>
        internal const int Protocol = 14;

        private const float PingInterval = 0.5f;
        private const byte PingKind = 0, PongKind = 1;
        private const int LossWindow = 20; // pings per loss sample (10 s)
        private const float PeerTimeout = 30f;
        private const float LoadTimeout = 120f;

        internal static bool Active;
        internal static bool IsHost;
        internal static ulong LocalId;

        /// <summary>Number of the current session (grows with every Begin): per-session state can tell a new session from the old one.</summary>
        internal static int SessionId;

        /// <summary>
        /// Why the last session ended, in Spanish for the overlay (null = the player left itself, or still
        /// running). Set by the first cause: the host's Bye, a lost host, or the reason given to Shutdown.
        /// </summary>
        internal static string EndReason;

        // Default Bye reasons (the receiver shows just "Salió: X" / "El host cerró la sesión" for these).
        private const string ByeHostLeft = "el host salió de la partida";
        private const string ByeClientLeft = "salió de la partida";

        /// <summary>
        /// THE "world sync is live" gate for every system: in a session, this PC plays the shared world
        /// (host: in a world; client: the host's world received, loaded, settled) and the game is not
        /// loading. Before that nothing world-related may be sent or applied.
        /// </summary>
        internal static bool WorldLive => Active && SaveTransfer.WorldReady && !GameState.IsLoading;

        /// <summary>LocalId (game-message identity, from its Hello) of a transport peer, or 0 if not known yet.</summary>
        internal static ulong LocalIdOf(ulong peer) => PeerLocalId.TryGetValue(peer, out ulong id) ? id : 0UL;
        internal static ITransport Transport;
        internal static readonly List<ulong> Peers = new List<ulong>();
        internal static readonly Dictionary<ulong, string> Names = new Dictionary<ulong, string>();

        // Last time (Time.unscaledTime) a packet arrived from each peer.
        internal static readonly Dictionary<ulong, float> LastSeen = new Dictionary<ulong, float>();

        // Same, but also set when the peer is added (timeout base before its first packet).
        private static readonly Dictionary<ulong, float> LastHeard = new Dictionary<ulong, float>();

        // Transport peer -> that player's LocalId (from its Hello). Lets the host tell everyone who left.
        internal static readonly Dictionary<ulong, ulong> PeerLocalId = new Dictionary<ulong, ulong>();

        // Refused peers: disconnected a moment after their Bye, so the Bye is delivered first.
        private static readonly List<KeyValuePair<ulong, float>> PendingDisconnects = new List<KeyValuePair<ulong, float>>();
        private static readonly List<ulong> _tmp = new List<ulong>();
        private static readonly float[] NextPacketErrorLog = new float[256];
        private static readonly int[] PacketErrors = new int[256];
        private static float _nextPing;
        private static uint _pingSeq;

        /// <summary>Link quality to one peer (from pings / pongs).</summary>
        private sealed class Quality
        {
            public float Srtt = -1f, RttVar;   // seconds
            public float Loss = -1f;           // 0..1, -1 = not known yet
            public bool HasIn;
            public uint InBase, InMax;         // inbound ping numbers of the current loss window
            public int InGot;
        }

        private static readonly Dictionary<ulong, Quality> Qualities = new Dictionary<ulong, Quality>();

        /// <summary>Round trip (ms), its variation (ms) and the inbound loss (%, -1 if not known yet) to a peer.</summary>
        internal static bool TryGetQuality(ulong peer, out float rttMs, out float jitterMs, out float lossPct)
        {
            rttMs = jitterMs = lossPct = -1f;
            if (!Qualities.TryGetValue(peer, out Quality q) || q.Srtt < 0f) return false;
            rttMs = q.Srtt * 1000f;
            jitterMs = q.RttVar * 1000f;
            lossPct = q.Loss < 0f ? -1f : q.Loss * 100f;
            return true;
        }

        private static float _nextDelayAdapt;

        /// <summary>
        /// The render delay of remote players, vehicles and objects already follows each sender's measured
        /// packet gaps and jitter (SyncClock, VehiclePlayback). This sets its floor from the links: 60 ms when
        /// every peer is on a clean direct or LAN link (round-trip variation under 10 ms, loss under 1 %),
        /// 100 ms otherwise (tunnel, Steam relay, Wi-Fi jitter). The clocks slew to a new target smoothly.
        /// </summary>
        private static void AdaptRenderDelay()
        {
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextDelayAdapt) return;
            _nextDelayAdapt = now + 1f;
            bool clean = Peers.Count > 0;
            var h = Transport as HybridTransport;
            foreach (ulong p in Peers)
            {
                string path = h?.LinkLabel(p);
                bool direct = path == "Directo" || (path != null && path.StartsWith("LAN", StringComparison.Ordinal));
                bool good = TryGetQuality(p, out float rtt, out float jit, out float loss) && jit < 10f && loss >= 0f && loss < 1f;
                if (!direct || !good) { clean = false; break; }
            }
            float floor = clean ? 0.06f : 0.1f;
            if (SyncClock.MinDelay != floor)
            {
                SyncClock.MinDelay = floor;
                VehiclePlayback.MinDelay = floor;
                Plugin.Log.LogInfo($"Render delay floor now {floor * 1000:0} ms ({(clean ? "clean direct/LAN links" : "tunnel, relay or jittery link")})");
            }
        }

        private static Quality QualityOf(ulong peer)
        {
            if (!Qualities.TryGetValue(peer, out Quality q)) Qualities[peer] = q = new Quality();
            return q;
        }

        private static byte[] PingPacket(byte kind, uint seq, float time)
        {
            var b = new byte[10];
            b[0] = (byte)Msg.Ping;
            b[1] = kind;
            Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, b, 2, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(time), 0, b, 6, 4);
            return b;
        }

        /// <summary>A ping or pong from <paramref name="from"/> (an empty Ping is a plain keep-alive).</summary>
        private static void OnPing(ulong from, BinaryReader r)
        {
            if (r.BaseStream.Length - r.BaseStream.Position < 9) return;
            byte kind = r.ReadByte();
            uint seq = r.ReadUInt32();
            float t = r.ReadSingle();
            Quality q = QualityOf(from);
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (kind == PingKind)
            {
                Transport.Send(from, PingPacket(PongKind, seq, t), false);
                // Loss: numbered pings that never arrived (late reordered ones of this window still count).
                if (!q.HasIn || seq + 1000u < q.InBase) { q.HasIn = true; q.InBase = q.InMax = seq; q.InGot = 1; } // first / peer restarted
                else if (seq >= q.InBase)
                {
                    if (seq > q.InMax) q.InMax = seq;
                    q.InGot++;
                    long expected = (long)q.InMax - q.InBase + 1;
                    if (expected >= LossWindow)
                    {
                        float sample = Math.Max(0f, Math.Min(1f, 1f - q.InGot / (float)expected));
                        q.Loss = q.Loss < 0f ? sample : q.Loss * 0.6f + sample * 0.4f;
                        q.InBase = q.InMax + 1;
                        q.InGot = 0;
                    }
                }
            }
            else if (kind == PongKind)
            {
                float rtt = now - t;
                if (!(rtt >= 0f) || rtt > 30f) return; // not ours (an earlier game run)
                if (q.Srtt < 0f) { q.Srtt = rtt; q.RttVar = rtt * 0.5f; }
                else
                {
                    q.RttVar = 0.75f * q.RttVar + 0.25f * Math.Abs(q.Srtt - rtt);
                    q.Srtt = 0.875f * q.Srtt + 0.125f * rtt;
                }
            }
        }

        internal static string LocalName
        {
            get
            {
                if (!string.IsNullOrEmpty(Plugin.CustomName)) return Plugin.CustomName; // --coop-name from the launcher
                try { return SteamFriends.GetPersonaName() + " (" + Environment.MachineName + ")"; }
                catch (Exception) { return Environment.MachineName; }
            }
        }

        // ---------- Steam ----------

        internal static void StartSteamHost()
        {
            Begin(new SteamTransport(IsLobbyMember), host: true, localId: SteamUser.GetSteamID().m_SteamID);
        }

        internal static void StartSteamClient(CSteamID host)
        {
            Begin(new SteamTransport(IsLobbyMember), host: false, localId: SteamUser.GetSteamID().m_SteamID);
            AddPeer(host.m_SteamID, SteamFriends.GetFriendPersonaName(host));
            SendHello(host.m_SteamID);
        }

        private static bool IsLobbyMember(CSteamID id)
        {
            int n = SteamMatchmaking.GetNumLobbyMembers(SteamLobby.Lobby);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(SteamLobby.Lobby, i) == id) return true;
            return false;
        }

        // ---------- LAN ----------

        internal static void StartLanHost(int port)
        {
            var t = LanTransport.Host(port);
            // Direct UDP on top: each client may offer a hole-punched link; the TCP path stays as the fallback.
            Begin(new HybridTransport(t, isHost: true, offerDirect: false, baseLabel: p => PathLabel(t.RemoteAddress(p))), host: true, localId: RandomId());
        }

        internal static void StartLanClient(string ip, int port)
        {
            LanTransport t = null;
            HybridTransport h = null;
            t = LanTransport.Join(ip, port, err =>
            {
                // Worker thread: only enqueue work for the main thread.
                MainThread.Run(() =>
                {
                    // The player left (F7) or started another session while connecting: not ours anymore.
                    if (!Active || !ReferenceEquals(Transport, h)) return;
                    if (err != null)
                    {
                        string hint = ip == "127.0.0.1" || ip == "localhost"
                            ? " Si usaste un código de Cloudflare, revisa la ventana de GMS Co-op.exe."
                            : IsPrivateIp(ip)
                                ? " ¿El host ya creó la partida? Revisa que estén en la misma red y que su firewall permita el juego."
                                : " Por internet, el router del host bloquea la entrada si no abrió el puerto: usa Steam o Cloudflare en GMS Co-op.exe, o una VPN (Tailscale, ZeroTier, Radmin).";
                        Plugin.Instance.SetStatus("No se pudo conectar a " + ip + ": " + err + "." + hint);
                        Shutdown();
                        return;
                    }
                    AddPeer(LanTransport.HostPeerId, "host " + ip);
                    SendHello(LanTransport.HostPeerId);
                    Plugin.Instance.SetStatus("Conectado al host " + ip);
                });
            });
            // A direct link is worth trying through the Cloudflare bridge (127.0.0.1) or to a public address;
            // on a LAN or VPN address the TCP path is already direct.
            bool loopback = ip == "localhost" || (System.Net.IPAddress.TryParse(ip, out var a) && System.Net.IPAddress.IsLoopback(a));
            string label = PathLabel(loopback ? System.Net.IPAddress.Loopback : System.Net.IPAddress.TryParse(ip, out var b) ? b : null);
            h = new HybridTransport(t, isHost: false, offerDirect: loopback || !IsPrivateIp(ip), baseLabel: p => label);
            Begin(h, host: false, localId: RandomId());
        }

        /// <summary>Name of the TCP path for the overlay: the Cloudflare bridge is local (127.0.0.1).</summary>
        private static string PathLabel(System.Net.IPAddress a)
        {
            if (a == null) return "Internet";
            if (System.Net.IPAddress.IsLoopback(a)) return "Cloudflare";
            return IsPrivateIp(a.ToString()) ? "LAN" : "Internet";
        }

        /// <summary>The path to a peer for the overlay ("Directo", "Cloudflare", "LAN", "Internet"), or null (Steam).</summary>
        internal static string LinkLabel(ulong peer) => (Transport as HybridTransport)?.LinkLabel(peer);

        /// <summary>True for addresses outside the internet: private, VPN/CGNAT range 100.64.0.0/10, link-local.</summary>
        private static bool IsPrivateIp(string ip)
        {
            if (!System.Net.IPAddress.TryParse(ip, out var a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            byte[] b = a.GetAddressBytes();
            return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        private static ulong RandomId()
        {
            var b = Guid.NewGuid().ToByteArray();
            return BitConverter.ToUInt64(b, 0) | 1UL << 63;
        }

        // ---------- Session ----------

        private static void Begin(ITransport t, bool host, ulong localId)
        {
            Transport = t;
            IsHost = host;
            LocalId = localId;
            Active = true;
            SessionId++;
            EndReason = null;
            Peers.Clear();
            Names.Clear();
            LastSeen.Clear();
            LastHeard.Clear();
            PeerLocalId.Clear();
            PendingDisconnects.Clear();
            Qualities.Clear();
            _nextPing = 0f;
        }

        private static void SendHello(ulong to)
        {
            var ms = Writer(Msg.Hello);
            var w = new BinaryWriter(ms);
            w.Write(LocalName);
            w.Write(LocalId);
            w.Write(Protocol);
            w.Write(UnityEngine.Application.version ?? "");
            Transport.Send(to, ms.ToArray(), true);
        }

        private static void SendBye(ulong to, string reason)
        {
            if (Transport == null) return;
            var ms = Writer(Msg.Bye);
            new BinaryWriter(ms).Write(reason ?? "");
            Transport.Send(to, ms.ToArray(), true);
        }

        private static void AddPeer(ulong id, string name)
        {
            if (!Peers.Contains(id)) Peers.Add(id);
            Names[id] = name;
            if (!LastHeard.ContainsKey(id)) LastHeard[id] = UnityEngine.Time.unscaledTime;
        }

        internal static void OnPeerJoined(ulong id, string name)
        {
            if (!Active) return;
            bool isNew = !Peers.Contains(id);
            AddPeer(id, name);
            if (isNew && IsHost)
            {
                Plugin.Instance.SetStatus("Entró: " + name);
                SendHello(id); // tell the client our name
                SaveTransfer.OnPeerJoinedHost(id); // client loads the host's world
            }
        }

        internal static void OnPeerLeft(ulong id) => OnPeerLeft(id, disconnect: true);

        /// <param name="disconnect">False when the same peer already opened a new session (rejoin).</param>
        private static void OnPeerLeft(ulong id, bool disconnect)
        {
            if (!Peers.Remove(id)) return;
            ObjectSync.OnPeerLeft(id);  // host takes over the objects the leaver owned
            MachineSync.OnPeerLeft(id); // release the machines it was driving right away
            SaveTransfer.OnPeerLeft(id);
            Names.TryGetValue(id, out string name);
            Names.Remove(id);
            LastSeen.Remove(id);
            LastHeard.Remove(id);
            Qualities.Remove(id);
            PlayerSync.RemoveGhostsFrom(id);
            if (PeerLocalId.TryGetValue(id, out ulong leaverLocalId))
            {
                PeerLocalId.Remove(id);
                if (IsHost)
                {
                    // Other clients only see the host as a peer: tell them who left.
                    var ms = Writer(Msg.PlayerLeft);
                    new BinaryWriter(ms).Write(leaverLocalId);
                    Broadcast(ms.ToArray(), reliable: true);
                }
                OnPlayerLeft(leaverLocalId);
            }
            if (disconnect) Transport?.Disconnect(id);
            if (!IsHost)
            {
                if (EndReason == null) EndReason = "se perdió la conexión con el host";
                Plugin.Instance?.SetStatus("Se perdió la conexión con el host (" + (name ?? id.ToString()) + ")");
                Shutdown(); // lost the host
            }
            else Plugin.Instance?.SetStatus("Salió: " + (name ?? id.ToString()));
        }

        /// <summary>A player (by LocalId) left the session: drop everything that belongs to it.</summary>
        private static void OnPlayerLeft(ulong localId)
        {
            PlayerSync.RemovePlayer(localId);
        }

        internal static string NameOf(ulong id) => Names.TryGetValue(id, out string n) ? n : id.ToString();

        internal static MemoryStream Writer(Msg type)
        {
            var ms = new MemoryStream();
            ms.WriteByte((byte)type);
            return ms;
        }

        internal static void Broadcast(byte[] data, bool reliable, ulong except = 0)
        {
            if (!Active) return;
            // Host: reliable world events a joiner's save does not contain yet (see SaveTransfer.JournalWorld).
            if (reliable && IsHost) SaveTransfer.JournalWorld(data);
            foreach (var p in Peers)
            {
                if (p == except) continue;
                // Host: a client that does not play the host's current world yet (receiving / loading it)
                // drops every world message; unreliable ones (poses, ~90 % of the traffic) would only take
                // bandwidth from its world transfer on a slow link. Reliable ones still go: a client may
                // already apply them in the moment between its load and the host seeing its Loaded.
                if (!reliable && IsHost && !SaveTransfer.PeerReady(p)) continue;
                Transport.Send(p, data, reliable);
            }
        }

        internal static void Poll()
        {
            SteamTransport.PumpDeferred(UnityEngine.Time.unscaledTime);
            if (!Active) return;
            AdaptRenderDelay();
            Transport.Poll(p =>
            {
                if (!Active) return; // the session ended while draining (Bye, rejected world...)
                if (p.Data == null)
                {
                    OnPeerLeft(p.From);
                    return;
                }
                try
                {
                    Handle(p);
                }
                catch (Exception ex)
                {
                    // One bad packet kind must not flood the log at 20 Hz: first error, then one per 10 s per kind.
                    int k = p.Length > 0 ? p.Data[0] : 0;
                    PacketErrors[k]++;
                    float t = UnityEngine.Time.unscaledTime;
                    if (t >= NextPacketErrorLog[k])
                    {
                        NextPacketErrorLog[k] = t + 10f;
                        Plugin.Log.LogError($"Packet error (message {k}, {PacketErrors[k]} so far): {ex}");
                    }
                }
            });
            if (Active) KeepAlive(UnityEngine.Time.unscaledTime);
        }

        /// <summary>
        /// Pings every peer each second and drops peers that went silent (crash, cable, sleep): no
        /// session waits forever. Packets that queued up during a long frame of our own were drained
        /// just before, so our own hitch never times anybody out.
        /// </summary>
        private static void KeepAlive(float now)
        {
            for (int i = PendingDisconnects.Count - 1; i >= 0; i--)
            {
                if (now < PendingDisconnects[i].Value) continue;
                ulong peer = PendingDisconnects[i].Key;
                PendingDisconnects.RemoveAt(i);
                if (!Peers.Contains(peer)) Transport.Disconnect(peer);
            }

            if (now < _nextPing) return;
            _nextPing = now + PingInterval;
            _tmp.Clear();
            byte[] ping = PingPacket(PingKind, _pingSeq++, UnityEngine.Time.realtimeSinceStartup);
            foreach (ulong p in Peers)
            {
                Transport.Send(p, ping, false);
                if (!LastHeard.TryGetValue(p, out float t)) { LastHeard[p] = now; continue; }
                float limit = SaveTransfer.InTransit(p) ? LoadTimeout : PeerTimeout;
                if (now - t > limit) _tmp.Add(p);
            }
            foreach (ulong p in _tmp)
            {
                if (!Active) break;
                Plugin.Log.LogWarning($"{NameOf(p)} sent nothing for {now - LastHeard[p]:0} s: dropping it");
                OnPeerLeft(p);
            }
        }

        private static void Handle(Packet p)
        {
            if (p.Length < 1) return;
            var type = (Msg)p.Data[0];
            // Only peers of this session may talk (Hello is how they become one). Drops stragglers
            // from players that already left and anything from strangers.
            if (type != Msg.Hello && !Peers.Contains(p.From)) return;
            var r = new BinaryReader(new MemoryStream(p.Data, 1, p.Length - 1));
            float now = UnityEngine.Time.unscaledTime;
            if (type != Msg.Hello || Peers.Contains(p.From)) { LastSeen[p.From] = now; LastHeard[p.From] = now; }

            // Host: a client's world messages count only once it plays the host's current world. Before
            // (joining, or still in the host's previous world after a host load) they would name objects,
            // machines and terrain of another world. They are neither applied nor relayed.
            bool world = IsWorldMessage(type);
            if (IsHost && world && !SaveTransfer.PeerReady(p.From)) return;
            // Client that just loaded the host's world: what the host sent before it saw our Loaded is
            // either in its catch-up replay (journals) or superseded by its full-state catch-up, which both
            // start with Ctl.CatchUp. Applying those live copies too would apply relative events twice.
            if (!IsHost && world && SaveTransfer.HoldWorldMessages) return;

            // Host relays world messages to the other clients BEFORE applying them, so an exception in a
            // handler (logged by Poll) can never stop the relay.
            if (world && IsHost && Active && !HostOnly(type, p)) Broadcast(Copy(p), p.Reliable, p.From);

            switch (type)
            {
                case Msg.Ping:
                    OnPing(p.From, r);
                    return; // never relayed
                case Msg.Bye:
                    {
                        string reason = r.BaseStream.Position < r.BaseStream.Length ? r.ReadString() : "";
                        string who = NameOf(p.From);
                        bool host = IsHost;
                        Plugin.Log.LogInfo($"{who} ended the session: {reason}");
                        // The plain "left" reasons add nothing to "Salió: X" / "El host cerró la sesión".
                        bool plain = reason.Length == 0 || reason == ByeClientLeft || reason == ByeHostLeft;
                        string status = host ? "Salió: " + who + (plain ? "" : " (" + reason + ")")
                                             : "El host cerró la sesión" + (plain ? "" : ": " + reason);
                        if (!host) EndReason = plain ? "el host cerró la sesión" : "el host cerró la sesión: " + reason;
                        OnPeerLeft(p.From); // on a client this ends the session
                        Plugin.Instance.SetStatus(status);
                        return;
                    }
                case Msg.Hello:
                    {
                        string name = r.ReadString();
                        long left = r.BaseStream.Length - r.BaseStream.Position;
                        ulong lid = left >= 8 ? r.ReadUInt64() : 0;
                        int protocol = left >= 12 ? r.ReadInt32() : 0;
                        string version = r.BaseStream.Position < r.BaseStream.Length ? r.ReadString() : "?";
                        if (protocol != Protocol)
                        {
                            string why = $"Versión distinta: abre GMS Co-op para actualizar (host {(IsHost ? Protocol : protocol)}, cliente {(IsHost ? protocol : Protocol)})";
                            Plugin.Log.LogWarning($"Hello from {name}: protocol {protocol}, this build {Protocol}: refused");
                            if (IsHost)
                            {
                                SendBye(p.From, why);
                                PendingDisconnects.Add(new KeyValuePair<ulong, float>(p.From, now + 2f));
                                Plugin.Instance.SetStatus(name + " no pudo entrar: " + why);
                            }
                            else
                            {
                                Plugin.Instance.SetStatus("No se puede jugar con este host: " + why);
                                Shutdown(why);
                            }
                            return;
                        }
                        if (version != UnityEngine.Application.version)
                            Plugin.Instance.SetStatus($"Aviso: {name} tiene otra versión del juego ({version}, tú {UnityEngine.Application.version})");
                        // A client sends Hello once per session. Another one from a peer the host still lists
                        // is that player back with a new session (restarted the game before the old one timed
                        // out, same Steam id): end the old one first, so it gets the world again.
                        if (IsHost && Peers.Contains(p.From))
                        {
                            Plugin.Log.LogInfo($"{NameOf(p.From)} joined again: restarting its session");
                            OnPeerLeft(p.From, disconnect: false);
                        }
                        if (lid != 0) PeerLocalId[p.From] = lid;
                        OnPeerJoined(p.From, name);
                        LastSeen[p.From] = now;
                        LastHeard[p.From] = now;
                        return; // never relayed
                    }
                case Msg.PlayerLeft:
                    if (!IsHost) OnPlayerLeft(r.ReadUInt64());
                    return; // sent by the host itself, never relayed
                case (Msg)8:
                case (Msg)9:
                    SaveTransfer.Receive(p.From, (Msg)p.Data[0], r);
                    return; // point-to-point, never relayed
                case (Msg)19:
                    CoopSession.Receive(p.From, r);
                    return; // point-to-point, never relayed
                case Msg.PlayerPose:
                    PlayerSync.Receive(p.From, r);
                    break;
                case Msg.TerrainHeights:
                    TerrainSync.ReceiveHeights(r, Copy(p));
                    break;
                case Msg.TerrainAlphas:
                    TerrainSync.ReceiveAlphas(r, Copy(p));
                    break;
                case Msg.Cash:
                    CashSync.Receive(r);
                    break;
                case Msg.MachinePose:
                    MachineSync.Receive(r, p.From);
                    break;
                case Msg.Tutorial:
                    TutorialSync.Receive(r);
                    break;
                case (Msg)10:
                    PurchaseSync.Receive(r);
                    break;
                case (Msg)11:
                    MachinePartsSync.Receive(r);
                    break;
                case (Msg)12:
                    ObjectSync.Receive(r, p.From);
                    break;
                case (Msg)13:
                    BedSync.Receive(r);
                    break;
                case (Msg)14:
                    GoldSync.Receive(r);
                    break;
                case Social.MsgId: // markers + report logs: not a world message, relayed by Social itself
                    Social.Receive(p.From, r, Copy(p));
                    return;
                default:
                    return; // unknown: never relayed
            }
        }

        /// <summary>Session-level messages pass every world gate; everything else names things of one world.</summary>
        private static bool IsWorldMessage(Msg type) =>
            type != Msg.Hello && type != Msg.Ping && type != Msg.Bye && type != Msg.PlayerLeft &&
            type != Msg.SaveChunk && type != Msg.SaveControl &&
            (byte)type >= (byte)Msg.PlayerPose && (byte)type <= 14; // unknown ids are neither applied nor relayed

        /// <summary>
        /// Client -> host requests that only the host acts on (every other client ignores a relayed copy):
        /// not relayed, they would only cost the other clients bandwidth. The sub-kind is the first byte
        /// after the message id for these message types.
        ///  - Tutorial: StepRequest (3), MissionRequest (4).
        ///  - Objects: SyncReq (9).
        ///  - Economy: ledger delta (0), container delta (2), mud / rent gold / minerals forwards (5, 6, 7),
        ///    container full-state request (10), sleep time jump (12), season end (13), fuel purchase op (24).
        /// </summary>
        private static bool HostOnly(Msg type, Packet p)
        {
            if (p.Length < 2) return false;
            byte sub = p.Data[1];
            switch (type)
            {
                case Msg.Tutorial: return sub == 3 || sub == 4;
                case Msg.Objects: return sub == ObjectSync.SubSyncReq;
                case Msg.Economy:
                    return sub == GoldSync.SubDelta || sub == GoldSync.SubContDelta || sub == GoldSync.SubMud ||
                           sub == GoldSync.SubRentGold || sub == GoldSync.SubMinerals || sub == GoldSync.SubContRequest ||
                           sub == GoldSync.SubTimeJump || sub == GoldSync.SubSeasonEnd || sub == WorldSync.SubFuelOp ||
                           sub == GoldSync.SubForgeClaim;
                default: return false;
            }
        }

        /// <summary>Copy of a packet's bytes (Steam transport reuses its receive buffer).</summary>
        private static byte[] Copy(Packet p)
        {
            var copy = new byte[p.Length];
            Buffer.BlockCopy(p.Data, 0, copy, 0, p.Length);
            return copy;
        }

        /// <param name="reason">Told to the other side (Bye); null = the player left.</param>
        internal static void Shutdown(string reason = null)
        {
            if (!Active && Transport == null) return;
            bool wasHost = IsHost;
            if (EndReason == null && reason != null) EndReason = reason;
            if (Transport != null)
            {
                // Last money/resource changes still waiting in the ledger go out before the goodbye.
                try { GoldSync.FlushNow(); } catch (Exception ex) { Plugin.Log.LogWarning("Flush on leave failed: " + ex.Message); }
                // Tell the others right away (LAN flushes it before closing; on Steam the lobby leave and
                // the keep-alive timeout back it up).
                string bye = reason ?? (wasHost ? ByeHostLeft : ByeClientLeft);
                foreach (var peer in Peers) SendBye(peer, bye);
                // Steam: closing the P2P session drops what is still queued (the Bye): close it a moment
                // later. LAN flushes the queue before closing the socket by itself.
                float now = UnityEngine.Time.unscaledTime;
                foreach (var peer in Peers)
                {
                    if (Transport is SteamTransport) SteamTransport.CloseLater(peer, now);
                    else Transport.Disconnect(peer);
                }
                Transport.Close();
                Transport = null;
            }
            Peers.Clear();
            Names.Clear();
            LastSeen.Clear();
            LastHeard.Clear();
            PeerLocalId.Clear();
            PendingDisconnects.Clear();
            Qualities.Clear();
            PlayerSync.RemoveAllGhosts();
            Active = false;
            if (SteamLobby.Lobby.IsValid())
            {
                SteamMatchmaking.LeaveLobby(SteamLobby.Lobby);
                SteamFriends.ClearRichPresence();
                SteamLobby.Lobby = CSteamID.Nil;
            }
            SaveTransfer.OnSessionEnded(wasHost);
        }
    }
}
