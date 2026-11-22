using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GoldDigger;
using HarmonyLib;
using SaveSystem;
using UnityEngine;
using Snap = GMSCoop.VehiclePlayback.Snap;

namespace GMSCoop
{
    /// <summary>
    /// Syncs the machine (truck, excavator, ...) the local player drives.
    ///
    /// Identity: the game has no network ids. The first index after a world load numbers every
    /// MachineController of the main scene (also inactive ones parked in unloaded areas), sorted by
    /// object name (ordinal) and rounded position. When the host saves its world for a joining player
    /// it does NOT renumber (players already in keep their ids): it broadcasts the id, name hash and
    /// position of every machine and trailer (sub message -5); the joiner, whose world is that save,
    /// matches each of its machines/trailers to the entry of the same name at the same place (also later,
    /// when area scenes load). Machines that appear later get the buyer's id (PurchaseSync) or an id
    /// derived from their name and position, never an arrival-order number. Destroyed (sold) machines are
    /// unregistered at once (SaveMe.OnDestroy -> CheckPointManagerBase.Remove).
    ///
    /// Driver identity: every pose carries the driver's LocalId. If two players start driving the
    /// same machine at once, the lower LocalId wins on every PC. When a player leaves, the host
    /// releases its drives at once and tells the other clients (sub message -4).
    ///
    /// The driver sends timestamped snapshots (physics pose, linear + angular velocity) 20 times
    /// per second. The receiver makes the body kinematic, buffers the snapshots and plays them back
    /// 100-300 ms in the past (adaptive, VehiclePlayback): Hermite interpolation between the two snapshots
    /// around the render time, eased extrapolation along the current turn when packets are late, and a
    /// fading correction offset instead of a snap when late data changes the path; snap only on
    /// teleports. The pose is applied with MovePosition/MoveRotation in FixedUpdate so cargo and
    /// contacts still get a real body velocity. A normal exit sends a reliable "driving=false"
    /// snapshot; the body is released once playback reaches it (safety timeout 4 s otherwise).
    /// Reliable hitch/teleport messages wait for the playback to reach them (see RunOrDefer).
    ///
    /// Packet (Msg.MachinePose, 64 bytes, 110 with a trailer): id i32, epoch u8, driver LocalId u64, sender time f64
    /// (Time.unscaledTimeAsDouble of the physics step the pose belongs to; a float clock has only ~1 ms
    /// resolution after 2-3 hours of play, which made remote vehicles judder in long sessions), pos 3xf32, rot 4xf32, vel 3xf16,
    /// angVel 3xf16, flags u8 (1 = driving, 2 = towing a trailer), machine name hash u16 (id
    /// disagreements are healed by name + position, see HealId), then if towing: trailer
    /// pos/rot/vel/angVel (same encoding), trailer id i32, trailer name hash u16.
    /// 20 Hz while moving, 4 Hz while the machine stands still.
    ///
    /// Trailer identity: trailers get ids like machines (own id space; host map for joiners, derived ids
    /// for later ones; PurchaseSync registers bought ones). Hitch messages and towing poses carry the id;
    /// name + nearest position is only the fallback, and a fallback match adopts the sender's id.
    ///
    /// Streamed areas: a remote-driven machine parked under an area that is not loaded here is shown again
    /// when it reaches a loaded area, and a machine released over an unloaded area is parked there.
    ///
    /// Trailers: a towed trailer is driven like a second remote body from the same snapshots (same
    /// timestamps and render clock as the truck), so it follows exactly instead of being dragged through
    /// a joint over locally simulated wheels. Hitch/unhitch (Trailer.Connect / Disconnect /
    /// DisconnectInstantly) is replicated as sub message -2 (reliable). Negative ids are sub messages of
    /// Msg.MachinePose: -2 = hitch, -3 = machine state (fuel, durability; MachinePartsSync),
    /// -4 = players left (host -> clients), -5 = id map for a joining player (host -> clients: kind,
    /// id, name hash, position per machine/trailer; the joiner matches by name + position),
    /// -6 = a game teleport of a machine/trailer nobody drives (Depot / laptop transport / tow service /
    /// fast travel / auto reset), -7 = mobile light trailer state (MachinePartsSync), -8 = rest pose of a
    /// machine/trailer nobody drives (host -> clients, see SyncResting).
    ///
    /// World version: every vehicle packet (MachineSync, MachinePartsSync, BedSync, PassengerSync) carries a
    /// one-byte world epoch right after its id. The host bumps it whenever its world reloads during a
    /// session; a joiner takes it from the id map. Packets of another epoch are ignored, so a player still
    /// on an older copy of the world (host reloaded, or a client reloaded its own save) never moves the
    /// wrong machines. A client that reloads its world during a session stops vehicle sync until it joins
    /// again (its world is no longer the host's).
    ///
    /// Sessions: a joiner whose loaded save put it in the host's machine (the save stores the host player's
    /// ControlledMachine) is taken out of it before it plays. Saves taken while a machine is remote-driven
    /// store its body as dynamic, not as our kinematic stand-in.
    /// </summary>
    internal static class MachineSync
    {
        private const float SendInterval = VehiclePlayback.SendInterval;
        // Safety net only (driver crashed or disconnected): a normal exit sends an explicit release.
        private const float ReleaseAfter = 4f;
        // Snapshot playback tuning lives in VehiclePlayback (pure code, tested by a console harness).
        private const float SnapDistance = 10f;       // farther than this from the target: teleport

        private static readonly Dictionary<int, MachineController> ById = new Dictionary<int, MachineController>();
        private static readonly Dictionary<MachineController, int> Ids = new Dictionary<MachineController, int>();
        // Trailers (not MachineControllers, not ObjectSync objects): their own id space, same numbering rules.
        private static readonly Dictionary<int, Trailer> TrById = new Dictionary<int, Trailer>();
        private static readonly Dictionary<Trailer, int> TrIds = new Dictionary<Trailer, int>();
        private static int _indexedScene = -1; // Scene.handle (changes on every reload, also of the same scene)
        private static bool _indexed;          // the first (ordered) index of this world was built
        private static float _nextSend;

        // Id map from the host for this PC's world (joining client): kind, id, name hash, position at the
        // host's save. Entries are consumed when a machine/trailer of that name is found at that place,
        // also later (machines of streamed area scenes appear when their area loads here).
        private class MapEntry { public byte Kind; public int Id; public ushort Name; public Vector3 Pos; public bool Used; }
        private static readonly List<MapEntry> JoinMap = new List<MapEntry>();
        private const float MapMatchRadius = 3f;

        // Host: transport peer each driver's packets came through (LocalId -> peer), for leaves on LAN.
        private static readonly Dictionary<ulong, ulong> DriverVia = new Dictionary<ulong, ulong>();
        private static readonly HashSet<ulong> KnownPeers = new HashSet<ulong>();
        // Players that left (LocalId -> local time): their late packets must not grab a machine again.
        private static readonly Dictionary<ulong, float> LeftAt = new Dictionary<ulong, float>();
        // Last driver of each machine id (kept after the drive ends), for BedSync's sender checks.
        private static readonly Dictionary<int, ulong> LastDriver = new Dictionary<int, ulong>();

        internal const int SubHitch = -2;
        internal const int SubState = -3;
        private const int SubLeft = -4;
        private const int SubIdMap = -5;
        private const int SubTeleport = -6;
        internal const int SubLightTrailer = -7;
        private const int SubRest = -8;

        // ------------------------------------------------------------------ world epoch

        private static byte _epoch;        // world version of this session (see class summary)
        private static bool _staleWorld;   // client reloaded its world during the session: no vehicle sync
        private static float _nextEpochWarn;

        /// <summary>World version written into every vehicle packet (right after its id).</summary>
        internal static byte Epoch => _epoch;

        /// <summary>True if a packet of world version <paramref name="e"/> belongs to the world this PC plays.</summary>
        internal static bool EpochOk(byte e)
        {
            if (e == _epoch) return true;
            if (Time.unscaledTime >= _nextEpochWarn)
            {
                _nextEpochWarn = Time.unscaledTime + 30f;
                Plugin.Log.LogWarning($"Machine sync: vehicle packets of world version {e} ignored (this PC plays version {_epoch}): " +
                                      "a player is still on another copy of the world (the host reloaded; the new world is on its way)");
            }
            return false;
        }

        /// <summary>A remote drive: the snapshot playback state (VehiclePlayback.Track) plus the bodies it moves.</summary>
        private class Remote : VehiclePlayback.Track
        {
            public MachineController Machine;
            public ulong Driver;       // LocalId of the player driving it
            public bool WasKinematic;
            public RigidbodyInterpolation OrigInterp;

            public float LastUpdate;   // local Time.unscaledTime of the last packet
            public float ExitDeadline; // release at the latest then (Exiting: see VehiclePlayback.Track)
            public bool Coasting;      // Exiting, and the body still moves on the driver's PC: its poses keep coming
            public bool Started;       // first pose applied (start-of-drive offset taken)
            public double LastFixedAt;  // for TryGetRenderTime between physics steps
            public float LastFixedDt;

            // Towed trailer, driven from the same snapshots.
            public Trailer Tr;
            public bool TrWasKinematic;
            public RigidbodyInterpolation TrOrigInterp;
            public bool TrStarted;
            public float NoTrailerSince = -1f; // driver tows nothing, but our pickup has a trailer hooked
            public bool LoggedUnhookHeal;      // the unhook self-heal was logged for this drive
            public float NextHeal;
            public int HealFails;      // self-heal hitches that did not stick (backoff, quiet logs)
        }

        // Local time a remote drive ended WITHOUT an exit packet (silence, or a re-index while someone
        // drove), per machine object (ids are renumbered by ResetIndex). A passenger stays seated then.
        private static readonly Dictionary<MachineController, float> TimeoutAt = new Dictionary<MachineController, float>();

        private static readonly Dictionary<int, Remote> Remotes = new Dictionary<int, Remote>();

        // Last "driving=false" per machine: driver, sender time, local receive time. Unreliable pose
        // packets of that driver that were overtaken by its reliable exit must not grab the machine again.
        private struct ExitStamp { public ulong Driver; public double Stamp; public float At; }
        private static readonly Dictionary<int, ExitStamp> ExitStamps = new Dictionary<int, ExitStamp>();

        private static float _nextScan;
        private static bool _scanDue;          // a machine/trailer appeared (SaveMe registered): scan next tick
        private const float ScanEvery = 10f;   // safety scan (objects activated without a new SaveMe)

        /// <summary>
        /// Numbers machines and trailers. The first scan after the world loads numbers every machine/trailer
        /// of the main scene (inactive ones in unloaded areas too, so both PCs see the same set) in a
        /// culture-independent order, or takes the host's id map on a joining client (matched by name and
        /// position). Later appearances (SaveMe registered, see PatchSaveMeAdd, plus a slow safety scan)
        /// get the host map's id or an id derived from name + position, so every PC picks the same one
        /// whatever order they see them in; purchases replace it with the buyer's id (Register).
        /// </summary>
        private static void EnsureIndex(bool force = false)
        {
            if (!Singleton<Player>.IsInstanced()) return;
            float now = Time.unscaledTime;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool sceneChanged = _indexedScene != scene.handle;
            if (!force && !_scanDue && !sceneChanged && now < _nextScan) return;
            // A client still in its own pre-join world, or loading the host's: ids would mean other machines.
            if (!WorldReady()) return;
            _nextScan = now + ScanEvery;
            _scanDue = false;

            if (sceneChanged)
            {
                if (Net.IsHost)
                {
                    // Our world (re)loaded: packets of players still on the previous copy must not apply.
                    _epoch++;
                    if (_indexedScene != -1 && Net.Peers.Count > 0)
                    {
                        // SaveTransfer sends the reloaded world to every client again; each gets this version with the id map.
                        Plugin.Log.LogWarning($"Machine sync: the host's world reloaded during the session (world version {_epoch}); " +
                                              "the other players get the new world");
                    }
                }
                else if (_indexedScene != -1)
                {
                    // A client reloaded its own save during the session (not the host's world any more).
                    _staleWorld = true;
                    Plugin.Log.LogWarning("Machine sync: this client reloaded its world during the session; vehicle sync is off " +
                                          "until you leave (F7) and join again");
                    Plugin.Instance?.SetStatus("Recargaste la partida: sal (F7) y vuelve a unirte para seguir jugando con el host");
                }
                _lastSentId = -1;
                ReleaseAllRemotes(driverLost: false);
                MachinePartsSync.Reset();
                BedSync.Reset();
                ById.Clear();
                Ids.Clear();
                TrById.Clear();
                TrIds.Clear();
                ExitStamps.Clear();
                LastDriver.Clear();
                _indexed = false;
                _indexedScene = scene.handle;
                if (_staleWorld) return;
            }
            PurgeDead();

            List<MachineController> freshM = null;
            foreach (MachineController m in UnityEngine.Object.FindObjectsOfType<MachineController>(true))
                if (m != null && !Ids.ContainsKey(m)) (freshM ??= new List<MachineController>()).Add(m);
            List<Trailer> freshT = null;
            foreach (Trailer t in UnityEngine.Object.FindObjectsOfType<Trailer>(true))
                if (t != null && !TrIds.ContainsKey(t)) (freshT ??= new List<Trailer>()).Add(t);
            if (freshM == null && freshT == null) { _indexed = true; return; }

            if (_indexed)
            {
                if (freshM != null) foreach (var m in freshM) Register(m, MapOrDerived(KindMachine, m, ById));
                if (freshT != null) foreach (var t in freshT) RegisterTrailer(t, MapOrDerived(KindTrailer, t, TrById));
                // Routine while driving around (area scenes stream machines in): debug level, kept out of the log file.
                Plugin.Log.LogDebug($"Machine sync: {Ids.Count} machines, {TrIds.Count} trailers indexed " +
                                    $"(+{freshM?.Count ?? 0}/+{freshT?.Count ?? 0} appeared)");
                return;
            }

            // First index of this world.
            bool mapped = !Net.IsHost && JoinMap.Count > 0;
            int fromMap = 0, total = 0;
            if (freshM != null) FirstIndex(freshM, scene, KindMachine, ById, Register, mapped, ref fromMap, ref total);
            if (freshT != null) FirstIndex(freshT, scene, KindTrailer, TrById, RegisterTrailer, mapped, ref fromMap, ref total);
            _indexed = true;
            Plugin.Log.LogInfo($"Machine sync: {Ids.Count} machines, {TrIds.Count} trailers indexed in {scene.name}" +
                               (mapped ? $" ({fromMap}/{total} ids from the host)" : ""));
            if (mapped && fromMap < total)
                Plugin.Log.LogWarning($"Machine sync: {total - fromMap} machines/trailers are not in the host's id map " +
                                      "(bought/sold during the transfer?); they got derived ids");
        }

        private const byte KindMachine = 0, KindTrailer = 1;

        private static void FirstIndex<T>(List<T> fresh, UnityEngine.SceneManagement.Scene scene, byte kind,
            Dictionary<int, T> byId, Action<T, int> register, bool mapped, ref int fromMap, ref int total) where T : Component
        {
            List<T> ordered = Ordered(fresh, scene);
            var inOrder = new HashSet<T>(ordered);
            total += fresh.Count;
            int n = 0;
            foreach (var c in ordered)
            {
                if (mapped)
                {
                    int id = MatchMap(kind, c, byId);
                    if (id >= 0) { register(c, id); fromMap++; }
                    else register(c, DerivedId(c, byId)); // must not take a number the host uses for another one
                    continue;
                }
                register(c, n < PurchasedBase ? n++ : DerivedId(c, byId));
            }
            foreach (var c in fresh)
            {
                if (inOrder.Contains(c)) continue; // area-scene objects
                int id = mapped ? MatchMap(kind, c, byId) : -1;
                if (id >= 0) fromMap++;
                register(c, id >= 0 ? id : DerivedId(c, byId));
            }
        }

        /// <summary>The objects of the main scene in index order (name ordinal, then rounded x, z, y).</summary>
        private static List<T> Ordered<T>(IEnumerable<T> all, UnityEngine.SceneManagement.Scene scene) where T : Component
        {
            return all.Where(m => m != null && m.gameObject.scene == scene)
                .OrderBy(m => m.gameObject.name, StringComparer.Ordinal)
                .ThenBy(m => Mathf.RoundToInt(m.transform.position.x * 2f))
                .ThenBy(m => Mathf.RoundToInt(m.transform.position.z * 2f))
                .ThenBy(m => Mathf.RoundToInt(m.transform.position.y * 2f))
                .ToList();
        }

        private static ushort NameHash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s ?? "") { h ^= c; h *= 16777619; }
            return (ushort)(h ^ (h >> 16));
        }

        /// <summary>Id for an object that appeared after the first index: same name + place gives the same id on every PC.</summary>
        private static int DerivedId<T>(T c, Dictionary<int, T> byId) where T : Component
        {
            Vector3 p = c.transform.position;
            string key = c.gameObject.name + "@" + Mathf.RoundToInt(p.x) + "," + Mathf.RoundToInt(p.z);
            uint h = 2166136261;
            foreach (char ch in key) { h ^= ch; h *= 16777619; }
            int id = DerivedBase + (int)(h % 100000000u);
            while (byId.TryGetValue(id, out var other) && other != null && other != c) id++;
            return id;
        }

        private static int MapOrDerived<T>(byte kind, T c, Dictionary<int, T> byId) where T : Component
        {
            int id = !Net.IsHost && JoinMap.Count > 0 ? MatchMap(kind, c, byId) : -1;
            return id >= 0 ? id : DerivedId(c, byId);
        }

        /// <summary>
        /// The host's id for <paramref name="c"/>: the nearest unused map entry of the same kind and name within
        /// MapMatchRadius (this world was loaded from the save the map was taken from). -1 if none.
        /// </summary>
        private static int MatchMap<T>(byte kind, T c, Dictionary<int, T> byId) where T : Component
        {
            if (JoinMap.Count == 0) return -1;
            ushort name = NameHash(c.gameObject.name);
            Vector3 pos = c.transform.position;
            MapEntry best = null;
            float bestD = MapMatchRadius * MapMatchRadius;
            foreach (MapEntry e in JoinMap)
            {
                if (e.Used || e.Kind != kind || e.Name != name) continue;
                if (byId.TryGetValue(e.Id, out var holder) && holder != null && holder != c) continue;
                float d = (e.Pos - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = e; }
            }
            if (best == null) return -1;
            // The same id may be listed by two maps (two joins during this load): consume all of them.
            foreach (MapEntry e in JoinMap) if (e.Kind == kind && e.Id == best.Id) e.Used = true;
            return best.Id;
        }

        /// <summary>Forgets machines/trailers that were destroyed (sold, scene unloaded).</summary>
        private static void PurgeDead()
        {
            List<MachineController> deadM = null;
            foreach (var kv in Ids) if (kv.Key == null) (deadM ??= new List<MachineController>()).Add(kv.Key);
            if (deadM != null) foreach (var m in deadM) Ids.Remove(m);
            List<int> deadIds = null;
            foreach (var kv in ById) if (kv.Value == null) (deadIds ??= new List<int>()).Add(kv.Key);
            if (deadIds != null) foreach (int id in deadIds) ById.Remove(id);
            List<Trailer> deadT = null;
            foreach (var kv in TrIds) if (kv.Key == null) (deadT ??= new List<Trailer>()).Add(kv.Key);
            if (deadT != null) foreach (var t in deadT) TrIds.Remove(t);
            deadIds?.Clear();
            foreach (var kv in TrById) if (kv.Value == null) (deadIds ??= new List<int>()).Add(kv.Key);
            if (deadIds != null) foreach (int id in deadIds) TrById.Remove(id);
        }

        /// <summary>
        /// Host, right after saving its world for a joining player: every machine and trailer id with its name
        /// hash and position in that save. Ids here and on the clients already in stay.
        /// </summary>
        private static void SendJoinMap()
        {
            EnsureIndex(force: true);
            if (!_indexed) return;
            var ms = Net.Writer(Msg.MachinePose);
            var w = new BinaryWriter(ms);
            w.Write(SubIdMap);
            w.Write(_epoch); // the joiner plays this world version
            int n = 0;
            foreach (var kv in Ids) if (kv.Key != null) n++;
            foreach (var kv in TrIds) if (kv.Key != null) n++;
            w.Write(n);
            foreach (var kv in Ids) if (kv.Key != null) WriteMapEntry(w, KindMachine, kv.Value, kv.Key);
            foreach (var kv in TrIds) if (kv.Key != null) WriteMapEntry(w, KindTrailer, kv.Value, kv.Key);
            byte[] data = ms.ToArray();
            Net.Broadcast(data, reliable: true);
            VehicleTraffic.Tx(VehicleTraffic.Machine, data.Length);
            Plugin.Log.LogInfo($"Machine sync: sent the id map of {n} machines/trailers for the joining player");
        }

        private static void WriteMapEntry(BinaryWriter w, byte kind, int id, Component c)
        {
            w.Write(kind);
            w.Write(id);
            w.Write(NameHash(c.gameObject.name));
            WriteV3(w, c.transform.position);
        }

        private static void ReceiveIdMap(BinaryReader r, byte epoch)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 100000) return;
            var entries = new List<MapEntry>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new MapEntry { Kind = r.ReadByte(), Id = r.ReadInt32(), Name = r.ReadUInt16(), Pos = ReadV3(r) };
                if (IsFinite(e.Pos)) entries.Add(e);
            }
            // Only a client that is loading the host's world uses it; players already in keep their ids.
            if (Net.IsHost || SaveTransfer.WorldReady) return;
            // Two joins during one load: keep both maps (same ids; positions of two recent saves). A map of another
            // world version (the host reloaded and renumbered, SaveTransfer sends its new world) replaces the old
            // one: unused entries of the old map (machines of areas never loaded here) carry stale ids.
            if (epoch != _epoch) JoinMap.Clear();
            JoinMap.AddRange(entries);
            _epoch = epoch;
            Plugin.Log.LogInfo($"Machine sync: received the host's id map ({entries.Count} machines/trailers, world version {epoch})");
        }

        /// <summary>
        /// Forgets every machine id and remote driver, so the next Tick numbers the machines again.
        /// Client: after loading the host's world. Host during a session (saving its world for a joining
        /// player): ids are NOT renumbered, the joiner gets the id map instead (SendJoinMap).
        /// </summary>
        internal static void ResetIndex()
        {
            if (Net.Active && Net.IsHost)
            {
                SendJoinMap();
                return;
            }
            ReleaseAllRemotes(driverLost: false);
            BedSync.Reset();
            MachinePartsSync.Reset(); // its remotes are keyed by the ids that are renumbered now
            ById.Clear();
            Ids.Clear();
            TrById.Clear();
            TrIds.Clear();
            ExitStamps.Clear();
            LastDriver.Clear();
            PurgeTimeouts();
            _indexedScene = -1;
            _indexed = false;
            _nextScan = 0f;
            _lastSentId = -1;
            _staleWorld = false; // the host's world was (re)loaded: this PC plays it again
            PendingTeleports.Clear();
            DeferredEvents.Clear();
            ExitInheritedMachine();
        }

        /// <summary>
        /// The save the host sent stores the HOST player's ControlledMachine (Player.Serialize), so a client
        /// that loads it while the host sits in a machine (driving, or riding as a passenger) wakes up in the
        /// host's cab and would start sending poses for the host's machine. Get out right after the load,
        /// before this PC counts as playing the shared world (nothing has been sent yet).
        /// </summary>
        private static void ExitInheritedMachine()
        {
            if (!Net.Active || Net.IsHost || !Singleton<Player>.IsInstanced()) return;
            MachineController m = Singleton<Player>.Instance.GetControlledMachine();
            if (m == null) return;
            try
            {
                if (m.EnterVehicle != null) m.EnterVehicle.ExitMachine(noAnim: true); // also calls m.ExitMachine()
                else
                {
                    m.ExitMachine();
                    m.SetControlled(false);
                }
                m.IsDriven = false;
                Plugin.Log.LogInfo($"Machine sync: the host's save put you in {m.gameObject.name}, got out");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Machine sync: could not leave the host's machine after the load: " + ex.Message);
            }
        }

        /// <summary>
        /// Gives every remote-driven body back to local physics (and clears BedSync/PassengerSync state).
        /// <paramref name="driverLost"/>: the drivers are gone (session ended), so their engines are stopped.
        /// </summary>
        private static void ReleaseAllRemotes(bool driverLost)
        {
            float now = Time.unscaledTime;
            foreach (var kv in Remotes)
            {
                // Re-index while driven: the drive resumes with the next pose packet.
                if (!driverLost && kv.Value.Machine != null) TimeoutAt[kv.Value.Machine] = now;
                RestoreBody(kv.Key, kv.Value, null, driverLost);
            }
            Remotes.Clear();
        }

        // Ids 0..999999: numbered from the loaded world (same on every PC).
        // Ids 1000000+: machines bought during the session, allocated by the buyer in a range of its own
        // so two players buying at the same moment never pick the same id.
        // Ids 200000000+: machines that appeared later without a purchase (DerivedId).
        // Trailers use the same rules in a separate id space.
        private const int PurchasedBase = 1000000;
        private const int DerivedBase = 200000000;
        private static int _purchaseCounter;

        /// <summary>Gives a machine a fixed id, replacing any id it had (purchase sync uses this).</summary>
        internal static void Register(MachineController m, int id)
        {
            if (m == null || id < 0) return;
            if (Ids.TryGetValue(m, out int old) && old != id && ById.TryGetValue(old, out var was) && was == m) ById.Remove(old);
            if (ById.TryGetValue(id, out var prev) && prev != null && prev != m) Ids.Remove(prev);
            ById[id] = m;
            Ids[m] = id;
        }

        /// <summary>Gives a trailer a fixed id, replacing any id it had (purchase sync: same id rules as machines).</summary>
        internal static void RegisterTrailer(Trailer t, int id)
        {
            if (t == null || id < 0) return;
            if (TrIds.TryGetValue(t, out int old) && old != id && TrById.TryGetValue(old, out var was) && was == t) TrById.Remove(old);
            if (TrById.TryGetValue(id, out var prev) && prev != null && prev != t) TrIds.Remove(prev);
            TrById[id] = t;
            TrIds[t] = id;
        }

        internal static int TrailerIdOf(Trailer t) => t != null && TrIds.TryGetValue(t, out int id) ? id : -1;

        internal static Trailer GetTrailer(int id) => id >= 0 && TrById.TryGetValue(id, out var t) && t != null ? t : null;

        // Name hash per machine (gameObject.name allocates a string on every call).
        private static readonly Dictionary<MachineController, ushort> NameHashes = new Dictionary<MachineController, ushort>();

        private static ushort NameHashOf(MachineController m)
        {
            if (NameHashes.TryGetValue(m, out ushort h)) return h;
            if (NameHashes.Count > 512) NameHashes.Clear(); // destroyed machines
            return NameHashes[m] = NameHash(m.gameObject.name);
        }

        private static readonly Dictionary<int, float> NextHealAt = new Dictionary<int, float>();

        /// <summary>
        /// A pose for an id that is unknown here or names a machine of another type: the two PCs disagree on
        /// that id (a machine re-created by its area scene at another place, a derived id rounded differently,
        /// a machine missing from the host map). Adopt the sender's id for the nearest machine of the sent name
        /// within 10 m that only has a derived (uncertain) id and nobody drives. At most every 2 s per id.
        /// </summary>
        private static MachineController HealId(int id, ushort nameHash, Vector3 pos, MachineController current)
        {
            float now = Time.unscaledTime;
            if (NextHealAt.TryGetValue(id, out float next) && now < next) return null;
            if (NextHealAt.Count > 256) NextHealAt.Clear();
            NextHealAt[id] = now + 2f;
            MachineController controlled = Singleton<Player>.IsInstanced() ? Singleton<Player>.Instance.GetControlledMachine() : null;
            MachineController best = null;
            float bestD = 10f * 10f;
            foreach (var kv in Ids)
            {
                MachineController c = kv.Key;
                if (c == null || kv.Value < DerivedBase || c == controlled || Remotes.ContainsKey(kv.Value)) continue;
                if (NameHashOf(c) != nameHash) continue;
                float d = (c.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best == null) return null;
            Plugin.Log.LogInfo($"Machine sync: {best.gameObject.name} #{IdOf(best)} matched to the driver's #{id} by name and position" +
                               (current != null ? $" (#{id} was {current.gameObject.name} here)" : ""));
            Register(best, id);
            return best;
        }

        /// <summary>A machine/trailer SaveMe is being destroyed (sold, scene unloaded): forget its id now.</summary>
        private static void Unregister(GameObject go)
        {
            var m = go.GetComponent<MachineController>();
            if (m != null && Ids.TryGetValue(m, out int id))
            {
                Ids.Remove(m);
                if (ById.TryGetValue(id, out var cur) && ReferenceEquals(cur, m)) ById.Remove(id);
                // A remote drive of it is released by ApplyRemotes once the object is gone (end of frame):
                // the hierarchy must not be touched while it is being destroyed.
            }
            var t = go.GetComponent<Trailer>();
            if (t != null && TrIds.TryGetValue(t, out int tid))
            {
                TrIds.Remove(t);
                if (TrById.TryGetValue(tid, out var cur) && ReferenceEquals(cur, t)) TrById.Remove(tid);
            }
        }

        /// <summary>SaveMe registration = an object appeared (instantiated / first activated): index it next tick.</summary>
        [HarmonyPatch]
        private static class PatchSaveMeAdd
        {
            private static MethodBase Target => AccessTools.Method(typeof(CheckPointManagerBase), nameof(CheckPointManagerBase.Add), new[] { typeof(SaveSystem.SaveMe) });
            private static bool Prepare() => Target != null;
            private static MethodBase TargetMethod() => Target;

            private static void Postfix(SaveSystem.SaveMe saveMe)
            {
                if (!Net.Active || _scanDue || saveMe == null) return;
                if (saveMe.GetComponent<MachineController>() != null || saveMe.GetComponent<Trailer>() != null) _scanDue = true;
            }
        }

        /// <summary>SaveMe.OnDestroy: a machine/trailer is gone (sold, destroyed, area scene unloaded).</summary>
        [HarmonyPatch]
        private static class PatchSaveMeRemove
        {
            private static MethodBase Target => AccessTools.Method(typeof(CheckPointManagerBase), nameof(CheckPointManagerBase.Remove), new[] { typeof(SaveSystem.SaveMe) });
            private static bool Prepare() => Target != null;
            private static MethodBase TargetMethod() => Target;

            private static void Prefix(SaveSystem.SaveMe saveMe)
            {
                if (saveMe == null || (Ids.Count == 0 && TrIds.Count == 0)) return;
                try { Unregister(saveMe.gameObject); }
                catch (Exception ex) { VehicleTraffic.Warn("unregister", "Machine sync: unregister: " + ex.Message); }
            }
        }

        /// <summary>Every indexed machine and its id (read-only view).</summary>
        internal static IReadOnlyDictionary<MachineController, int> AllIds => Ids;

        internal static int IdOf(MachineController m) => m != null && Ids.TryGetValue(m, out int id) ? id : -1;

        /// <summary>The machine with this id, or null.</summary>
        internal static MachineController Get(int id) => id >= 0 && ById.TryGetValue(id, out var m) && m != null ? m : null;

        /// <summary>True while another player drives this machine (its pose comes from the network).</summary>
        internal static bool IsRemoteDriven(int id) => Remotes.ContainsKey(id);

        /// <summary>LocalId of the remote player driving this machine right now, or 0.</summary>
        internal static ulong DriverOf(int id) => Remotes.TryGetValue(id, out var rem) ? rem.Driver : 0UL;

        /// <summary>LocalId of the last remote player that drove this machine (also after the drive ended), or 0.</summary>
        internal static ulong LastDriverOf(int id) => LastDriver.TryGetValue(id, out ulong d) ? d : 0UL;

        /// <summary>True if that player left the session within the last few seconds (ignore its late packets).</summary>
        internal static bool LeftRecently(ulong localId) =>
            localId != 0 && LeftAt.TryGetValue(localId, out float t) && Time.unscaledTime - t < LeftIgnore;

        private const float LeftIgnore = 10f;

        /// <summary>True if this machine's remote driver went silent (no exit packet) within the last <paramref name="window"/> s.</summary>
        internal static bool LostDriverRecently(MachineController m, float window) =>
            m != null && TimeoutAt.TryGetValue(m, out float t) && Time.unscaledTime - t < window;

        private static void PurgeTimeouts()
        {
            if (TimeoutAt.Count < 16) return;
            var old = new List<MachineController>();
            foreach (var kv in TimeoutAt) if (kv.Key == null || Time.unscaledTime - kv.Value > 60f) old.Add(kv.Key);
            foreach (var k in old) TimeoutAt.Remove(k);
        }

        /// <summary>
        /// The sender-clock time at which a remote-driven machine is drawn right now (render clock,
        /// corrected for rigidbody interpolation between physics steps). MachinePartsSync plays the
        /// driver's part snapshots back at this time so arm, wheels and steering match the body.
        /// </summary>
        internal static bool TryGetRenderTime(int id, out double senderTime)
        {
            senderTime = 0.0;
            if (!Remotes.TryGetValue(id, out Remote rem) || !rem.ClockSet || rem.LastFixedDt <= 0f) return false;
            // Interpolate draws lerp(previous step, current step, alpha): one physics step behind at alpha 0.
            float alpha = Mathf.Clamp01((float)(Time.unscaledTimeAsDouble - rem.LastFixedAt) / rem.LastFixedDt);
            senderTime = rem.RenderClock - rem.LastFixedDt * (1f - alpha);
            return true;
        }

        /// <summary>
        /// A new id for a machine this PC just bought. Each player gets its own block of 1000 ids
        /// (host block 0, clients one of 99000 blocks derived from LocalId), so simultaneous purchases on
        /// two PCs do not collide; ids already used here (e.g. from the host's id map) are skipped.
        /// </summary>
        internal static int AllocatePurchasedId()
        {
            int block = Net.IsHost ? 0 : 1 + (int)(Net.LocalId % 99000UL);
            for (int tries = 0; tries < 1000; tries++)
            {
                int id = PurchasedBase + block * 1000 + (_purchaseCounter++ % 1000);
                if (Get(id) == null && GetTrailer(id) == null) return id;
            }
            return PurchasedBase + block * 1000 + (_purchaseCounter++ % 1000);
        }

        /// <summary>Old name kept for callers: same as AllocatePurchasedId.</summary>
        internal static int NextId => AllocatePurchasedId();

        /// <summary>True if the machine shows the game's own seated driver model (IKControl).</summary>
        internal static bool HasDriverModel(int id)
        {
            MachineController m = Get(id);
            return m != null && m.WorkerPerson != null;
        }

        // ------------------------------------------------------------------ world readiness

        // A client must not send or apply machine poses while it still plays its own pre-join world
        // or is loading/settling the host's world: ids would refer to different machines.
        internal static bool WorldReady() => Net.WorldLive && !_staleWorld;

        // ------------------------------------------------------------------ sender

        internal static void Tick()
        {
            if (!Net.Active)
            {
                // Session ended: give every remote body back to local physics and stop the engines the
                // departed drivers left running (they would burn fuel forever).
                if (Remotes.Count > 0) ReleaseAllRemotes(driverLost: true);
                _lastSentId = -1;
                _staleWorld = false;
                JoinMap.Clear();
                DriverVia.Clear();
                KnownPeers.Clear();
                LeftAt.Clear();
                PendingTeleports.Clear();
                DeferredEvents.Clear();
                RestSent.Clear();
                Coasts.Clear();
                return;
            }
            VehicleTraffic.Tick();
            CheckDeparted();
            EnsureIndex();
            BedSync.Tick();
            ShowRemoteDrivers();
            FollowAreas();
            FlushTeleports();
            FlushDeferred();
            SyncResting();

            float now = Time.unscaledTime;
            if (now < _nextSend) return;
            _nextSend = now + SendInterval;

            MachineController m = Singleton<Player>.IsInstanced() ? Singleton<Player>.Instance.GetControlledMachine() : null;
            if (m == null) m = null; // destroyed (sold while inside)
            int id = m != null && Ids.TryGetValue(m, out int found) ? found : -1;
            if (m != null && id < 0 && WorldReady() && now >= _nextOwnScan)
            {
                // Sitting in a machine that has no id yet (just appeared): index it now, not in 10 s.
                _nextOwnScan = now + 0.5f;
                EnsureIndex(force: true);
                id = Ids.TryGetValue(m, out found) ? found : -1;
            }
            // Someone else is driving it (we are a passenger, also while its driver lags).
            if (id >= 0 && (Remotes.ContainsKey(id) || PassengerSync.IsLocalPassenger)) id = -1;
            if (!WorldReady()) id = -1;

            // Got out (or switched machine): tell the others right away so they release it now,
            // instead of waiting for the timeout. The final fuel/wear goes reliably with it.
            if (_lastSentId >= 0 && _lastSentId != id)
            {
                MachineController last = Get(_lastSentId);
                if (last != null)
                {
                    // Exit pose; then keep streaming while it rolls on (UpdateCoasts), final pose at rest.
                    SendPose(_lastSentId, last, driving: false, reliable: true, coast: true);
                    MachinePartsSync.SendFinalState(last, _lastSentId);
                    Coasts[_lastSentId] = new Coast { Until = now + CoastMax };
                }
            }
            UpdateCoasts(now);
            if (id >= 0 && (id != _lastSentId || !IdleSkip(m, now))) SendPose(id, m, driving: true, reliable: false);
            _lastSentId = id;
        }

        private static float _nextOwnScan;

        /// <summary>
        /// Keeps the game's seated driver model (WorkerPerson) visible in every remote-driven machine. The game hides
        /// it whenever the local player's state in that machine changes (EnterVehicleController.UpdateWorkerPerson),
        /// e.g. when a local passenger gets out while the other player keeps driving; PlayerSync hides the driver's
        /// avatar in machines with a driver model, so the driver would be invisible here for the rest of the drive.
        /// </summary>
        private static void ShowRemoteDrivers()
        {
            if (Remotes.Count == 0) return;
            foreach (var kv in Remotes)
            {
                MachineController m = kv.Value.Machine;
                if (m == null || m.WorkerPerson == null) continue;
                // Got out (its avatar walks next to the machine now) while the body still plays to its final
                // pose: no driver in the seat. (A local passenger still sees the seated driver until then.)
                bool show = !kv.Value.Exiting || m.IsControlled();
                if (m.WorkerPerson.gameObject.activeSelf != show) m.WorkerPerson.gameObject.SetActive(show);
            }
        }

        // Idle send rate: a machine standing still (engine idling, parked with someone inside) needs no
        // 20 Hz stream; 4 Hz keeps the receiver's timeout and clock fed. The receiver holds the pose.
        private const float IdleInterval = 0.25f;
        private static float _lastPoseAt;
        private static Vector3 _lastPosePos;
        private static Quaternion _lastPoseRot = Quaternion.identity;

        private static bool IdleSkip(MachineController m, float now)
        {
            if (now - _lastPoseAt >= IdleInterval) return false;
            Rigidbody rb = m.MyRigidbody;
            if (rb == null) return false;
            if (rb.velocity.sqrMagnitude > 0.05f * 0.05f || rb.angularVelocity.sqrMagnitude > 0.05f * 0.05f) return false;
            if ((rb.position - _lastPosePos).sqrMagnitude > 0.01f * 0.01f || Quaternion.Angle(rb.rotation, _lastPoseRot) > 0.5f) return false;
            Rigidbody trb = TowedTrailerBody(m);
            if (trb != null && (trb.velocity.sqrMagnitude > 0.05f * 0.05f || trb.angularVelocity.sqrMagnitude > 0.05f * 0.05f)) return false;
            return true;
        }

        // ------------------------------------------------------------------ authority (single source of truth)
        //
        // Who drives / simulates a machine is decided HERE only; every other file asks through this API:
        //   LocallyDriven(m) / LocalDrivenMachine: the local player drives m itself. Never true for a passenger (also
        //       not during the lag grace, PassengerSync), nor while another player's pose stream holds m. This PC then
        //       streams m's pose, parts and fuel/wear, its bed cargo, and the loose objects m pushes or tows.
        //   IsRemoteDriven(id / m): another player's pose stream holds m (also while it plays out that player's exit).
        //   DriverOf(id) / LastDriverOf(id): that player's LocalId (packet sender checks).
        //   IsAuthority(m): this PC runs m's game simulation (digging, dumping, clods, processing): its local driver,
        //       else nobody (the host) while nobody drives it or its last driver is getting out. Exactly one PC answers
        //       true once every PC has seen the same drive start/end.
        //   TowingMachine(t) / IsTrailerAuthority(t): a trailer follows the machine that tows it.
        //   DrivesNear(player, pos, r): that remote player drives a machine within r of pos (its PC simulates what
        //       that machine pushes, tows or holds on its pick rope: do not claim such objects by proximity).
        //   CopyRemoteDriven(list): every machine a remote player drives right now (no allocation).

        /// <summary>True if the local player drives <paramref name="m"/> itself (not as a passenger, nobody else drives it).</summary>
        internal static bool LocallyDriven(MachineController m)
        {
            if (m == null || !Singleton<Player>.IsInstanced() || Singleton<Player>.Instance.GetControlledMachine() != m) return false;
            if (PassengerSync.IsLocalPassenger) return false;
            int id = IdOf(m);
            return id < 0 || !Remotes.ContainsKey(id);
        }

        /// <summary>The machine the local player drives itself (see LocallyDriven), or null.</summary>
        internal static MachineController LocalDrivenMachine
        {
            get
            {
                MachineController m = Singleton<Player>.IsInstanced() ? Singleton<Player>.Instance.GetControlledMachine() : null;
                return m != null && LocallyDriven(m) ? m : null;
            }
        }

        /// <summary>True while another player's pose stream holds <paramref name="m"/> (see IsRemoteDriven(int)).</summary>
        internal static bool IsRemoteDriven(MachineController m) =>
            m != null && Remotes.Count > 0 && Ids.TryGetValue(m, out int id) && Remotes.ContainsKey(id);

        /// <summary>
        /// True if this PC runs the game simulation of <paramref name="m"/> (digging, dumping, clods, belts):
        /// the local driver; while another player drives it, that player's PC; while nobody drives it (or its
        /// last driver is getting out, which no longer simulates it), the host. True outside a session and while
        /// this PC does not play the shared world (a client before / while loading the host's world).
        /// </summary>
        internal static bool IsAuthority(MachineController m)
        {
            if (m == null || !Net.Active || !WorldReady()) return true;
            if (LocallyDriven(m)) return true;
            if (Remotes.Count > 0 && Ids.TryGetValue(m, out int id) && Remotes.TryGetValue(id, out Remote rem) && !rem.Exiting) return false;
            return Net.IsHost;
        }

        /// <summary>The machine that tows <paramref name="t"/> right now (hooked), or null.</summary>
        internal static MachineController TowingMachine(Trailer t)
        {
            if (t == null || t._Hook == null || !t._Hook.IsConnected) return null;
            Pickup p = PickupOf(t);
            return p != null && p.ConnectedTrailer == t ? p : null;
        }

        /// <summary>True if this PC simulates <paramref name="t"/>: that of its towing machine, else the host.</summary>
        internal static bool IsTrailerAuthority(Trailer t)
        {
            if (t == null || !Net.Active || !WorldReady()) return true;
            MachineController m = TowingMachine(t);
            return m != null ? IsAuthority(m) : Net.IsHost;
        }

        /// <summary>True if remote player <paramref name="player"/> (LocalId) drives a machine within <paramref name="radius"/> m of <paramref name="pos"/>.</summary>
        internal static bool DrivesNear(ulong player, Vector3 pos, float radius)
        {
            if (player == 0 || Remotes.Count == 0) return false;
            float r2 = radius * radius;
            foreach (var kv in Remotes)
            {
                Remote rem = kv.Value;
                if (rem.Driver != player || rem.Exiting || rem.Machine == null) continue;
                Rigidbody rb = rem.Machine.MyRigidbody;
                Vector3 mp = rb != null ? rb.position : rem.Machine.transform.position;
                if ((mp - pos).sqrMagnitude <= r2) return true;
            }
            return false;
        }

        /// <summary>Fills <paramref name="into"/> with every machine a remote player drives right now; returns the count.</summary>
        internal static int CopyRemoteDriven(List<MachineController> into)
        {
            into.Clear();
            foreach (var kv in Remotes) if (kv.Value.Machine != null) into.Add(kv.Value.Machine);
            return into.Count;
        }

        /// <summary>
        /// MachinePartsSync: true if this packet of <paramref name="sender"/> was sent before its exit from the machine
        /// (an unreliable straggler overtaken by the reliable exit): it must not take the parts over again.
        /// </summary>
        internal static bool SentBeforeExit(int id, ulong sender, double stamp) =>
            !Remotes.ContainsKey(id) && ExitStamps.TryGetValue(id, out var e) && e.Driver == sender &&
            Time.unscaledTime - e.At < 3f && stamp <= e.Stamp + 0.001f;

        /// <summary>
        /// MachinePartsSync: true if that player's exit from the machine already arrived here (its drive is over or
        /// playing out): unreliable driver updates of it arriving now were sent before the exit.
        /// </summary>
        internal static bool ExitSeen(int id, ulong sender) =>
            ExitStamps.TryGetValue(id, out var e) && e.Driver == sender && Time.unscaledTime - e.At < 3f &&
            (!Remotes.TryGetValue(id, out Remote rem) || rem.Exiting);

        private static int _lastSentId = -1;
        /// <summary>Id of the machine this PC streamed as its driver last tick (-1 = none).</summary>
        internal static int LastSentId => _lastSentId;

        // ------------------------------------------------------------------ coasting after an exit
        //
        // A driver who gets out of a moving machine (or parks on a slope without the handbrake) leaves a body
        // that keeps rolling. Releasing it to local physics on every PC at the exit pose let the copies roll
        // apart: measured in game, a pickup that rolled ~6 m backwards after its driver got out ended 5.7 m
        // apart, and the cargo on its bed with it. Now the exiting PC keeps streaming the body (driving flag
        // off, coast flag on, 20 Hz) until it has been still for 0.5 s (or 20 s passed), then sends the final
        // pose reliably without the coast flag. Receivers keep playing the drive (Exiting + Coasting) and
        // release the body only at that final pose.

        private const float CoastMax = 20f;
        private const float CoastStill = 0.5f;
        private sealed class Coast { public float Until, Still; }
        private static readonly Dictionary<int, Coast> Coasts = new Dictionary<int, Coast>();
        private static List<int> _coastDone;

        private static void UpdateCoasts(float now)
        {
            if (Coasts.Count == 0) return;
            _coastDone?.Clear();
            foreach (var kv in Coasts)
            {
                int cid = kv.Key;
                MachineController cm = Get(cid);
                bool end = cm == null || cm.IsControlled() || Remotes.ContainsKey(cid) || !WorldReady();
                if (!end)
                {
                    Rigidbody rb = cm.MyRigidbody;
                    bool moving = rb != null && !rb.isKinematic &&
                                  (rb.velocity.sqrMagnitude > 0.05f * 0.05f || rb.angularVelocity.sqrMagnitude > 0.01f);
                    kv.Value.Still = moving ? 0f : kv.Value.Still + SendInterval;
                    if (kv.Value.Still >= CoastStill || now >= kv.Value.Until)
                    {
                        SendPose(cid, cm, driving: false, reliable: true, coast: false);
                        end = true;
                    }
                    else SendPose(cid, cm, driving: false, reliable: false, coast: true);
                }
                if (end) (_coastDone ??= new List<int>()).Add(cid);
            }
            if (_coastDone != null) foreach (int cid in _coastDone) Coasts.Remove(cid);
        }

        private static void SendPose(int id, MachineController m, bool driving, bool reliable, bool coast = false)
        {
            Rigidbody rb = m.MyRigidbody;
            // The physics pose (not the possibly interpolated transform), stamped with the time of the
            // physics step it comes from, so the receiver's timeline matches the motion exactly.
            Vector3 pos = rb != null ? rb.position : m.transform.position;
            Quaternion rot = rb != null ? rb.rotation : m.transform.rotation;
            double sinceStep = Time.timeScale > 0.01f ? (Time.timeAsDouble - Time.fixedTimeAsDouble) / Time.timeScale : 0.0;
            double stamp = Time.unscaledTimeAsDouble - Math.Min(Math.Max(sinceStep, 0.0), 0.1);

            BinaryWriter w = VehicleTraffic.Begin(Msg.MachinePose);
            w.Write(id);
            w.Write(_epoch);
            w.Write(Net.LocalId);
            w.Write(stamp);
            WriteV3(w, pos);
            w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
            WriteHalf3(w, rb != null ? rb.velocity : Vector3.zero);
            WriteHalf3(w, rb != null ? rb.angularVelocity : Vector3.zero);
            Rigidbody trb = TowedTrailerBody(m);
            w.Write((byte)((driving ? 1 : 0) | (trb != null ? 2 : 0) | (coast ? 4 : 0)));
            w.Write(NameHashOf(m));
            if (trb != null)
            {
                WriteV3(w, trb.position);
                Quaternion tq = trb.rotation;
                w.Write(tq.x); w.Write(tq.y); w.Write(tq.z); w.Write(tq.w);
                WriteHalf3(w, trb.velocity);
                WriteHalf3(w, trb.angularVelocity);
                Trailer tr = trb.GetComponent<Trailer>();
                w.Write(TrailerIdOf(tr));
                w.Write(tr != null ? NameHash(tr.name) : (ushort)0);
            }
            VehicleTraffic.Send(VehicleTraffic.Machine, reliable);
            if (driving)
            {
                _lastPoseAt = Time.unscaledTime;
                _lastPosePos = pos;
                _lastPoseRot = rot;
            }
        }

        // ------------------------------------------------------------------ players leaving

        /// <summary>
        /// Host: a transport peer left. Releases every machine driven by a player behind it at once (no
        /// 4 s timeout) and tells the other clients, who never see that peer themselves. Called by
        /// Net.OnPeerLeft (if wired) and by the host's own per-frame peer check, whichever comes first.
        /// </summary>
        internal static void OnPeerLeft(ulong via)
        {
            if (!Net.Active || !Net.IsHost || !KnownPeers.Remove(via)) return;
            var left = new List<ulong> { via }; // Steam: LocalId == SteamID == transport peer id
            foreach (var kv in DriverVia)
                if (kv.Value == via && !left.Contains(kv.Key)) left.Add(kv.Key);
            // LAN: the leaver's LocalId (from its Hello) differs from the transport peer id; it may not have driven yet.
            ulong lid = Net.LocalIdOf(via);
            if (lid != 0 && !left.Contains(lid)) left.Add(lid);
            foreach (ulong id in left) DriverVia.Remove(id);

            var ms = Net.Writer(Msg.MachinePose);
            var w = new BinaryWriter(ms);
            w.Write(SubLeft);
            w.Write(_epoch); // not checked: a departure counts in every world version
            w.Write((byte)Mathf.Min(left.Count, 255));
            for (int i = 0; i < left.Count && i < 255; i++) w.Write(left[i]);
            byte[] data = ms.ToArray();
            Net.Broadcast(data, reliable: true);
            VehicleTraffic.Tx(VehicleTraffic.Machine, data.Length);
            PlayersLeft(left);
        }

        /// <summary>Host, every tick: detects departed peers (works even if Net does not call OnPeerLeft).</summary>
        private static void CheckDeparted()
        {
            if (!Net.IsHost) return;
            foreach (ulong p in Net.Peers)
                if (KnownPeers.Add(p) && LeftAt.Count > 0)
                {
                    // A player that left and came back (Steam: same id): its packets count again.
                    LeftAt.Remove(p);
                    if (Net.PeerLocalId.TryGetValue(p, out ulong lid)) LeftAt.Remove(lid);
                }
            if (KnownPeers.Count == Net.Peers.Count) return;
            List<ulong> gone = null;
            foreach (ulong p in KnownPeers) if (!Net.Peers.Contains(p)) (gone ??= new List<ulong>()).Add(p);
            if (gone != null) foreach (ulong p in gone) OnPeerLeft(p);
        }

        private static void ReceiveLeft(BinaryReader r)
        {
            int n = r.ReadByte();
            var left = new List<ulong>(n);
            for (int i = 0; i < n; i++) left.Add(r.ReadUInt64());
            if (!Net.IsHost) PlayersLeft(left);
        }

        /// <summary>Those players are gone: release their drives now (engine off), drop their passenger seats.</summary>
        private static void PlayersLeft(List<ulong> players)
        {
            float now = Time.unscaledTime;
            List<int> release = null;
            foreach (ulong p in players)
            {
                if (p == 0 || p == Net.LocalId) continue;
                LeftAt[p] = now;
                PassengerSync.OnPlayerLeft(p);
                foreach (var kv in Remotes)
                    if (kv.Value.Driver == p) (release ??= new List<int>()).Add(kv.Key);
            }
            if (release != null)
                foreach (int id in release)
                {
                    Remote rem = Remotes[id];
                    Remotes.Remove(id);
                    // A clean departure, not lag: a passenger takes the wheel right away (no lag grace).
                    if (rem.Machine != null) TimeoutAt.Remove(rem.Machine);
                    Plugin.Log.LogInfo($"Machine sync: driver of #{id} left the session");
                    RestoreBody(id, rem, null, driverLost: true);
                }
            if (LeftAt.Count > 32)
            {
                var old = new List<ulong>();
                foreach (var kv in LeftAt) if (now - kv.Value > LeftIgnore) old.Add(kv.Key);
                foreach (ulong k in old) LeftAt.Remove(k);
            }
        }

        // ------------------------------------------------------------------ receiver

        /// <summary>Entry point with the transport peer the packet came through (host: maps drivers to peers).</summary>
        internal static void Receive(BinaryReader r, ulong via)
        {
            if (via != 0 && Net.IsHost && r.BaseStream.CanSeek)
            {
                Stream st = r.BaseStream;
                long p = st.Position;
                if (st.Length - p >= 13)
                {
                    int id = r.ReadInt32();
                    if (id >= 0)
                    {
                        r.ReadByte(); // epoch
                        ulong driver = r.ReadUInt64();
                        if (driver != 0 && driver != Net.LocalId && !LeftRecently(driver)) DriverVia[driver] = via;
                    }
                }
                st.Position = p;
            }
            Receive(r);
        }

        internal static void Receive(BinaryReader r)
        {
            int len = (int)r.BaseStream.Length + 1;
            int id = r.ReadInt32();
            byte epoch = r.ReadByte();
            VehicleTraffic.Rx(id == SubState || id == SubLightTrailer ? VehicleTraffic.Parts : VehicleTraffic.Machine, len);
            if (id == SubLeft) { ReceiveLeft(r); return; }
            if (id == SubIdMap) { ReceiveIdMap(r, epoch); return; }
            if (!EpochOk(epoch)) return; // another copy of the world: its ids mean other machines
            if (id == SubHitch) { ReceiveHitch(r); return; }
            if (id == SubState) { MachinePartsSync.ReceiveState(r); return; }
            if (id == SubTeleport) { ReceiveTeleport(r); return; }
            if (id == SubLightTrailer) { MachinePartsSync.ReceiveLightTrailer(r); return; }
            if (id == SubRest) { ReceiveRest(r); return; }
            if (id < 0) return; // unknown sub message (newer build)
            ulong driver = r.ReadUInt64();
            var s = new Snap { Time = r.ReadDouble(), Pos = ReadV3(r) };
            s.Rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            s.Vel = ReadHalf3(r);
            s.AngVel = ReadHalf3(r);
            byte flags = r.ReadByte();
            ushort nameHash = r.ReadUInt16();
            bool driving = (flags & 1) != 0;
            bool coast = (flags & 4) != 0; // out of the machine, it still rolls: more poses follow (UpdateCoasts)
            if ((flags & 2) != 0)
            {
                s.HasTrailer = true;
                s.TPos = ReadV3(r);
                s.TRot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                s.TVel = ReadHalf3(r);
                s.TAngVel = ReadHalf3(r);
                s.TId = r.ReadInt32();
                s.TName = r.ReadUInt16();
                if (!IsFinite(s.TPos) || !IsFinite(s.TVel) || !IsFinite(s.TAngVel) || !Normalize(ref s.TRot)) s.HasTrailer = false;
            }
            if (driver == 0 || driver == Net.LocalId || LeftRecently(driver) || !WorldReady()) return;
            if (!IsFinite(s.Pos) || !IsFinite(s.Vel) || !IsFinite(s.AngVel) || double.IsNaN(s.Time) || double.IsInfinity(s.Time)) return;
            if (!Normalize(ref s.Rot)) return;
            MachineController m = Get(id);
            if (m == null || NameHashOf(m) != nameHash)
            {
                MachineController healed = HealId(id, nameHash, s.Pos, m);
                if (healed != null) m = healed;
            }
            if (m == null) return;
            if (Remotes.TryGetValue(id, out var stale) && stale.Machine != m)
            {
                // The id moved to another object (healed, or re-created by its area scene): drop the old drive.
                Remotes.Remove(id);
                RestoreBody(id, stale, null, driverLost: false);
            }
            float now = Time.unscaledTime;

            if (!driving)
            {
                ExitStamps[id] = new ExitStamp { Driver = driver, Stamp = s.Time, At = now };
                if (Remotes.TryGetValue(id, out var leaving))
                {
                    // Another player's exit (it lost the claim on this machine): not the one we show.
                    if (leaving.Driver != driver) return;
                    // Driver got out: play up to the final pose, then release (see ApplyRemotes). While the
                    // body still rolls on the driver's PC, coast poses keep coming: keep playing them.
                    // A late coast pose (older than the final one) must not reopen the coast.
                    if (coast && !leaving.Coasting && leaving.Exiting && leaving.Count > 0 && s.Time <= leaving.Buf[leaving.Count - 1].Time) return;
                    AddSnapshot(leaving, s, now);
                    leaving.Exiting = true;
                    leaving.Coasting = coast;
                    leaving.ExitDeadline = now + VehiclePlayback.MaxDelay + 0.6f; // + time for a correction to fade out
                }
                else if (coast) return; // already released here (final pose reached / deadline): the rest pose corrects it
                else if (!m.IsControlled())
                {
                    // We never applied this drive (e.g. joined late): still adopt the final resting pose.
                    if ((m.transform.position - s.Pos).sqrMagnitude > 0.25f * 0.25f)
                    {
                        PlaceExactly(m, s.Pos, s.Rot);
                        SettleInArea(m.gameObject, s.Pos);
                    }
                    if (s.HasTrailer && m is Pickup p && p.ConnectedTrailer != null && p.ConnectedTrailer.MyRigidbody != null &&
                        (p.ConnectedTrailer.MyRigidbody.position - s.TPos).sqrMagnitude > 0.25f * 0.25f)
                    {
                        PlaceBody(p.ConnectedTrailer.MyRigidbody, s.TPos, s.TRot);
                        SettleInArea(p.ConnectedTrailer.gameObject, s.TPos);
                    }
                }
                return;
            }

            // A pose sent before that driver's last exit, overtaken by the reliable exit packet: ignore it.
            if (ExitStamps.TryGetValue(id, out var exit) && exit.Driver == driver && now - exit.At < 1.5f && s.Time <= exit.Stamp) return;

            if (Remotes.TryGetValue(id, out var rem))
            {
                if (rem.Driver != driver)
                {
                    // Two players claim the same machine (both got in at the same moment): the lower
                    // LocalId wins on every PC, the other one becomes a passenger on its own PC.
                    if (driver > rem.Driver) return;
                    Plugin.Log.LogInfo($"Machine sync: #{id} claimed by two players, the lower id drives");
                    rem.Driver = driver;
                    rem.ResetStream(); // another sender clock, another path
                    rem.Started = false; // fade from what is shown now to the winner's pose
                    BedSync.OnDriverChanged(m); // the winner's cargo, not the loser's
                }
            }
            else
            {
                // We drive it ourselves (already sent poses) and have the lower id: we keep it.
                if (_lastSentId == id && Net.LocalId < driver) return;
                TimeoutAt.Remove(m);
                Rigidbody rb = m.MyRigidbody;
                rem = new Remote { Machine = m, Driver = driver, WasKinematic = rb != null && rb.isKinematic };
                if (rb != null)
                {
                    rb.isKinematic = true;
                    // The body now moves only in FixedUpdate: interpolate it so it renders smoothly
                    // at any frame rate.
                    rem.OrigInterp = rb.interpolation;
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                }
                // Show the game's own seated driver (hands on the wheel) for the remote driver.
                if (m.WorkerPerson != null) m.WorkerPerson.gameObject.SetActive(true);
                Remotes[id] = rem;
                BedSync.OnRemoteDriveStart(m); // cargo rides frozen on the bed, like for a local driver
                Plugin.Log.LogInfo($"Machine sync: remote driving {m.gameObject.name} (#{id})");
            }
            LastDriver[id] = driver;
            rem.Exiting = false; // driving again (same player got back in)
            AddSnapshot(rem, s, now);
        }

        private static void AddSnapshot(Remote rem, Snap s, float now)
        {
            rem.LastUpdate = now;
            VehiclePlayback.AddSnapshot(rem, s, Time.unscaledTimeAsDouble);
        }

        /// <summary>Called from Plugin.FixedUpdate.</summary>
        internal static void FixedTick()
        {
            if (Net.Active && Remotes.Count > 0) ApplyRemotes();
        }

        private static List<int> _release;

        private static void ApplyRemotes()
        {
            double fixedNow = Time.fixedUnscaledTimeAsDouble;
            float fixedDt = Time.fixedUnscaledDeltaTime;
            float now = Time.unscaledTime;
            _release?.Clear();

            foreach (var kv in Remotes)
            {
                Remote rem = kv.Value;
                if (rem.Machine == null || now - rem.LastUpdate > ReleaseAfter)
                {
                    (_release ??= new List<int>()).Add(kv.Key);
                    continue;
                }
                rem.LastFixedAt = fixedNow;
                rem.LastFixedDt = fixedDt;
                if (rem.Count == 0)
                {
                    if (rem.Exiting && now > rem.ExitDeadline) (_release ??= new List<int>()).Add(kv.Key);
                    continue;
                }

                // Render clock (real-time rate, slewed toward "now - offset - adaptive delay"), trimming, and
                // the correction offset that keeps the drawn pose continuous when late data changes the path.
                VehiclePlayback.Step(rem, fixedNow, fixedDt);

                Rigidbody rb = rem.Machine.MyRigidbody;
                Transform t = rem.Machine.transform;

                // Driver got out: release once playback reached the final snapshot and any correction has
                // faded out (so the exact final placement does not jump), or at the deadline.
                if (rem.Exiting && ((!rem.Coasting && VehiclePlayback.ExitReached(rem) && VehiclePlayback.Settled(rem)) || now > rem.ExitDeadline))
                {
                    Snap last = rem.Buf[rem.Count - 1];
                    PlaceExactly(rem.Machine, last.Pos, last.Rot);
                    if (last.HasTrailer && rem.Tr != null && rem.Tr.MyRigidbody != null) PlaceBody(rem.Tr.MyRigidbody, last.TPos, last.TRot);
                    (_release ??= new List<int>()).Add(kv.Key);
                    continue;
                }

                // Game code (Trailer.CreateJoint, the fly cheat, ...) may clear isKinematic: keep it set.
                if (rb != null && !rb.isKinematic) rb.isKinematic = true;

                ApplyTrailer(rem, now);

                VehiclePlayback.Sample(rem, out Vector3 pos, out Quaternion rot);

                if (!rem.Machine.gameObject.activeInHierarchy)
                {
                    // Parked under an area that is not loaded here (FollowAreas shows it once it reaches a
                    // loaded one): physics does not run for it, keep the transform where the driver is.
                    rem.Started = true;
                    rem.ClearError();
                    PlaceExactly(rem.Machine, pos, rot);
                    continue;
                }

                Vector3 cur = rb != null ? rb.position : t.position;
                if (rem.SnapPending || (cur - pos).sqrMagnitude > SnapDistance * SnapDistance)
                {
                    rem.SnapPending = false;
                    rem.Started = true;
                    rem.ClearError();
                    PlaceExactly(rem.Machine, pos, rot);
                    continue;
                }

                if (!rem.Started)
                {
                    // First applied pose (also after a driver change): fade out the difference to what is shown
                    // now instead of jumping, so nearby players/objects are not kicked.
                    rem.Started = true;
                    rem.ErrPos = cur - pos;
                    rem.ErrRot = (rb != null ? rb.rotation : t.rotation) * Quaternion.Inverse(rot);
                }
                pos += rem.ErrPos;
                rot = rem.ErrRot * rot;

                if (rb != null && rb.isKinematic)
                {
                    // Kinematic MovePosition in FixedUpdate: the physics engine gives the body a real
                    // velocity, so cargo in a truck bed and things it touches are carried/pushed.
                    rb.MovePosition(pos);
                    rb.MoveRotation(rot);
                }
                else
                {
                    t.SetPositionAndRotation(pos, rot);
                }
            }

            if (_release == null || _release.Count == 0) return;
            foreach (int id in _release)
            {
                Remote rem = Remotes[id];
                Snap? final = rem.Exiting && rem.Count > 0 ? rem.Buf[rem.Count - 1] : (Snap?)null;
                // Silence without an exit packet: the driver crashed or disconnected.
                bool lost = !rem.Exiting && rem.Machine != null;
                if (lost) { TimeoutAt[rem.Machine] = now; PurgeTimeouts(); }
                RestoreBody(id, rem, final, driverLost: lost);
                Remotes.Remove(id);
            }
            _release.Clear();
        }

        // ------------------------------------------------------------------ towed trailer (receiver)

        /// <summary>Drives the remote driver's towed trailer from the same snapshots as the truck.</summary>
        private static void ApplyTrailer(Remote rem, float now)
        {
            var pickup = rem.Machine as Pickup;
            if (pickup == null) return;
            bool towing = VehiclePlayback.SampleTrailer(rem, out Vector3 tpos, out Quaternion trot);
            Trailer hooked = pickup.ConnectedTrailer;

            if (!towing)
            {
                if (rem.Tr != null) ReleaseTrailer(rem, rem.Buf[0].HasTrailer ? rem.Buf[0].TVel : Vector3.zero, Vector3.zero);
                // The driver tows nothing but a trailer is hooked here: unhook after a grace period
                // (the driver's own unhitch takes 1 s and its hitch message may be on its way).
                if (hooked != null && !rem.Exiting)
                {
                    if (rem.NoTrailerSince < 0f) rem.NoTrailerSince = now;
                    else if (now - rem.NoTrailerSince > 2f)
                    {
                        // Logged once per drive: if the unhook keeps failing this runs every 2 s.
                        if (!rem.LoggedUnhookHeal)
                        {
                            rem.LoggedUnhookHeal = true;
                            Plugin.Log.LogInfo($"Machine sync: driver of {pickup.name} tows no trailer, unhooking {hooked.name} here");
                        }
                        ApplyHitch(pickup, hooked, false, hooked.MyRigidbody != null ? hooked.MyRigidbody.position : hooked.transform.position,
                            hooked.transform.rotation, place: false);
                        rem.NoTrailerSince = -1f;
                    }
                }
                else rem.NoTrailerSince = -1f;
                return;
            }
            rem.NoTrailerSince = -1f;

            Snap newest = rem.Buf[rem.Count - 1];
            int tid = newest.HasTrailer ? newest.TId : -1;
            if (hooked == null || (tid >= 0 && TrailerIdOf(hooked) != tid && GetTrailer(tid) != null))
            {
                // Hitch message lost or not here yet, or another trailer is hooked here: hook the driver's one.
                if (now < rem.NextHeal) { if (hooked == null) return; }
                else
                {
                    // 1 s, then backing off to 16 s while it does not stick (the trailer is missing here, or the
                    // game refuses the joint): no log line and no hitch attempt every second for a whole drive.
                    rem.NextHeal = now + Mathf.Min(16f, (float)(1 << Mathf.Min(rem.HealFails, 4)));
                    Trailer want = ResolveTrailer(tid, newest.HasTrailer ? newest.TName : (ushort)0, tpos);
                    if (want == null) { rem.HealFails++; if (hooked == null) return; }
                    else
                    {
                        bool quiet = rem.HealFails > 0;
                        if (!quiet) Plugin.Log.LogInfo($"Machine sync: driver of {pickup.name} tows {want.name} (trailer #{tid}), hooking it here");
                        ApplyHitch(pickup, want, true, tpos, trot, place: true, quiet: quiet);
                        hooked = pickup.ConnectedTrailer;
                        if (hooked == want) rem.HealFails = 0;
                        else if (rem.HealFails++ == 0) Plugin.Log.LogWarning($"Machine sync: could not hook {want.name} to {pickup.name} here, retrying less often");
                        if (hooked == null) return;
                    }
                }
            }
            else rem.HealFails = 0; // the driver's trailer is hooked here

            if (rem.Tr != hooked)
            {
                if (rem.Tr != null) ReleaseTrailer(rem, Vector3.zero, Vector3.zero);
                Rigidbody nrb = hooked.MyRigidbody;
                if (nrb == null) return;
                rem.Tr = hooked;
                rem.TrWasKinematic = nrb.isKinematic;
                rem.TrOrigInterp = nrb.interpolation;
                nrb.isKinematic = true;
                nrb.interpolation = RigidbodyInterpolation.Interpolate;
                rem.TrStarted = false;
            }

            Rigidbody trb = rem.Tr.MyRigidbody;
            if (trb == null) return;
            if (!trb.isKinematic) trb.isKinematic = true;
            Vector3 cur = trb.position;
            if (!rem.Tr.gameObject.activeInHierarchy || (cur - tpos).sqrMagnitude > SnapDistance * SnapDistance)
            {
                rem.TrStarted = true;
                rem.TrErrPos = Vector3.zero;
                rem.TrErrRot = Quaternion.identity;
                PlaceBody(trb, tpos, trot);
                return;
            }
            if (!rem.TrStarted)
            {
                rem.TrStarted = true;
                rem.TrErrPos = cur - tpos;
                rem.TrErrRot = trb.rotation * Quaternion.Inverse(trot);
            }
            // Start-of-tow and correction offsets fade out in VehiclePlayback.Step.
            tpos += rem.TrErrPos;
            trot = rem.TrErrRot * trot;
            trb.MovePosition(tpos);
            trb.MoveRotation(trot);
        }

        /// <summary>Gives the towed trailer back to local physics (still jointed to the truck if hooked).</summary>
        private static void ReleaseTrailer(Remote rem, Vector3? vel, Vector3 angVel)
        {
            Trailer t = rem.Tr;
            rem.Tr = null;
            rem.TrStarted = false;
            if (t == null || t.MyRigidbody == null) return;
            Rigidbody rb = t.MyRigidbody;
            rb.interpolation = rem.TrOrigInterp;
            // A hooked trailer is always a dynamic body (it rolls behind the truck); it may have been
            // kinematic when we took it over only because its car jacks were still up.
            rb.isKinematic = rem.TrWasKinematic && (t._Hook == null || !t._Hook.IsConnected);
            if (!rb.isKinematic && vel.HasValue)
            {
                rb.velocity = vel.Value;
                rb.angularVelocity = angVel;
            }
            SettleInArea(t.gameObject, rb.position);
        }

        // ------------------------------------------------------------------ streamed areas
        //
        // Saved objects live under an AreaStreamer's GameplayObjects, which is inactive while that area is
        // not loaded on this PC; AreaObjectParentManage re-parents only ACTIVE objects. A machine another
        // player drives out of an area that is unloaded here would stay invisible even in front of us, and
        // one released over an unloaded area (no terrain here) would fall through the world.

        private static float _nextFollow;

        /// <summary>Every 0.5 s: a remote-driven machine/trailer hidden in an unloaded area is shown where it is now.</summary>
        private static void FollowAreas()
        {
            if (Remotes.Count == 0 || Time.unscaledTime < _nextFollow) return;
            _nextFollow = Time.unscaledTime + 0.5f;
            foreach (var kv in Remotes)
            {
                Remote rem = kv.Value;
                try
                {
                    if (rem.Machine != null) ShowInArea(rem.Machine.gameObject);
                    if (rem.Tr != null) ShowInArea(rem.Tr.gameObject);
                }
                catch (Exception ex) { VehicleTraffic.Warn("area follow", "Machine sync: area follow: " + ex.Message); }
            }
        }

        private static void ShowInArea(GameObject go)
        {
            if (go.activeInHierarchy || !go.activeSelf) return; // visible, or deliberately inactive (rented, stored)
            Vector3 pos = go.transform.position;
            AreaAt(pos, out AreaStreamer loaded, out AreaStreamer any);
            if (loaded != null)
            {
                if (go.transform.parent != loaded.GameplayObjects) ParentToArea(go, loaded);
            }
            else if (any == null && go.transform.parent != null)
            {
                go.transform.SetParent(null, true); // outside every area: main scene root
            }
        }

        /// <summary>Released over an area that is not loaded here: park it there (frozen until it loads), like the game.</summary>
        private static void SettleInArea(GameObject go, Vector3 pos)
        {
            if (go == null || !go.activeInHierarchy) return;
            try
            {
                AreaAt(pos, out AreaStreamer loaded, out AreaStreamer any);
                if (loaded == null && any != null && go.transform.parent != any.GameplayObjects)
                {
                    ParentToArea(go, any);
                    Plugin.Log.LogInfo($"Machine sync: {go.name} parked in unloaded area {any.sceneName}");
                }
            }
            catch (Exception ex) { VehicleTraffic.Warn("area park", "Machine sync: area park: " + ex.Message); }
        }

        /// <summary>Puts <paramref name="go"/> under an area's GameplayObjects, as DepotPlace / AreaObjectParentManage do.</summary>
        private static void ParentToArea(GameObject go, AreaStreamer area)
        {
            go.transform.SetParent(area.GameplayObjects, true);
            var save = go.GetComponent<SaveSystem.SaveMe>();
            if (save != null) save.MySceneName = area.sceneName;
        }

        /// <summary>Areas whose load bounds contain <paramref name="pos"/> (x/z): a loaded one if any, and any one.</summary>
        private static void AreaAt(Vector3 pos, out AreaStreamer loaded, out AreaStreamer any)
        {
            loaded = null;
            any = null;
            List<AreaStreamer> all = AreaStreamer.Instances;
            if (all == null) return;
            foreach (AreaStreamer a in all)
            {
                if (a == null || a.GameplayObjects == null || a.LoadBounds == null) continue;
                bool inside = false;
                foreach (Bounds b in a.LoadBounds)
                    if (b.Contains(new Vector3(pos.x, b.center.y, pos.z))) { inside = true; break; }
                if (!inside) continue;
                if (any == null) any = a;
                if (a.GameplayObjects.gameObject.activeInHierarchy) { loaded = a; return; }
            }
        }

        // ------------------------------------------------------------------ trailer hitch replication

        private static readonly MethodInfo TrailerConnect = AccessTools.Method(typeof(Trailer), "Connect");
        private static bool _applyingHitch;

        /// <summary>The trailer body the local driver tows, or null.</summary>
        private static Rigidbody TowedTrailerBody(MachineController m)
        {
            if (!(m is Pickup p) || p.ConnectedTrailer == null) return null;
            Trailer t = p.ConnectedTrailer;
            if (t._Hook == null || !t._Hook.IsConnected) return null;
            return t.MyRigidbody;
        }

        /// <summary>The pickup a trailer is hooked to (TrailerHook.PickupCanConnect can be cleared by other pickups).</summary>
        private static Pickup PickupOf(Trailer t)
        {
            if (t == null) return null;
            Pickup p = t._Hook != null ? t._Hook.PickupCanConnect : null;
            if (p != null && p.ConnectedTrailer == t) return p;
            foreach (MachineController m in ById.Values)
                if (m is Pickup q && q != null && q.ConnectedTrailer == t) return q;
            return p;
        }

        /// <summary>
        /// The trailer nearest to <paramref name="pos"/> within <paramref name="radius"/>, preferring the
        /// same object name. Uses TrailerHook.Instances (enabled hooks), no scene scan.
        /// </summary>
        private static Trailer FindTrailer(ushort nameHash, Vector3 pos, float radius)
        {
            Trailer best = null;
            float bestD = radius * radius;
            bool bestNamed = false;
            if (TrailerHook.Instances == null) return null;
            foreach (TrailerHook h in TrailerHook.Instances)
            {
                if (h == null) continue;
                Trailer t = h.GetComponentInParent<Trailer>();
                if (t == null) continue;
                Vector3 tp = t.MyRigidbody != null ? t.MyRigidbody.position : t.transform.position;
                float d = (tp - pos).sqrMagnitude;
                bool named = NameHash(t.name) == nameHash;
                if (d > radius * radius) continue;
                if ((named && !bestNamed) || (named == bestNamed && d < bestD))
                {
                    best = t;
                    bestD = d;
                    bestNamed = named;
                }
            }
            return best;
        }

        /// <summary>
        /// The trailer the sender means: by trailer id (same on every PC) if that trailer has the sent name,
        /// else the nearest one of that name within 6 m of the sent pose. A position match adopts the
        /// sender's id, so a trailer whose id differed here (bought before PurchaseSync registered trailer ids)
        /// is matched by id from then on.
        /// </summary>
        private static Trailer ResolveTrailer(int tid, ushort nameHash, Vector3 pos)
        {
            Trailer byId = GetTrailer(tid);
            if (byId != null && NameHash(byId.name) == nameHash) return byId;
            Trailer near = FindTrailer(nameHash, pos, 6f);
            if (near != null && tid >= 0 && TrailerIdOf(near) != tid)
            {
                Plugin.Log.LogInfo($"Machine sync: trailer {near.name} matched by position, id #{TrailerIdOf(near)} -> #{tid}");
                RegisterTrailer(near, tid);
            }
            return near;
        }

        private static void SendHitch(Trailer t, Pickup p, bool connected)
        {
            if (!Net.Active || _applyingHitch || t == null || p == null || !WorldReady()) return;
            int pid = IdOf(p);
            if (pid < 0) return;
            Transform tt = t.transform;
            Vector3 pos = t.MyRigidbody != null ? t.MyRigidbody.position : tt.position;
            Quaternion rot = t.MyRigidbody != null ? t.MyRigidbody.rotation : tt.rotation;
            var ms = Net.Writer(Msg.MachinePose);
            var w = new BinaryWriter(ms);
            w.Write(SubHitch);
            w.Write(_epoch);
            w.Write((byte)(connected ? 1 : 0));
            w.Write(pid);
            w.Write(TrailerIdOf(t));
            w.Write(t.name ?? "");
            WriteV3(w, pos);
            w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
            byte[] data = ms.ToArray();
            Net.Broadcast(data, true);
            VehicleTraffic.Tx(VehicleTraffic.Machine, data.Length);
            Plugin.Log.LogInfo($"Machine sync: sent trailer {(connected ? "hitch" : "unhitch")} {t.name} <-> {p.name} (#{pid})");
        }

        private static void ReceiveHitch(BinaryReader r)
        {
            bool connected = r.ReadByte() != 0;
            int pid = r.ReadInt32();
            int tid = r.ReadInt32();
            string name = r.ReadString();
            Vector3 pos = ReadV3(r);
            var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (!WorldReady() || !IsFinite(pos) || !Normalize(ref rot)) return;
            if (!(Get(pid) is Pickup p) || p == null)
            {
                Plugin.Log.LogWarning($"Machine sync: trailer hitch for unknown pickup #{pid}");
                return;
            }
            ushort nh = NameHash(name);
            Trailer t = connected ? ResolveTrailer(tid, nh, pos) : (p.ConnectedTrailer != null ? p.ConnectedTrailer : ResolveTrailer(tid, nh, pos));
            if (t == null)
            {
                Plugin.Log.LogWarning($"Machine sync: trailer {name} near {pos} not found for {(connected ? "hitch" : "unhitch")}");
                return;
            }
            // The hitch is reliable, the poses are not: while the pickup is remote-driven its playback is still
            // ~0.1-0.3 s in the past (towing / not towing yet). Apply the hitch when playback reaches it, so the
            // trailer is not unhooked under a towing playback (and re-hooked by the self-heal) or hooked early.
            RunOrDefer(pid, null, () =>
            {
                if (p != null && t != null) ApplyHitch(p, t, connected, pos, rot, place: true);
            });
        }

        // ------------------------------------------------------------------ reliable events vs. playback
        //
        // Hitch and teleport messages are reliable; poses are unreliable and played back in the past. An event
        // for a machine whose playback has not yet reached the newest pose received before it waits for that
        // pose (and, while its driver is getting out, for the release), so it lands in order on every PC.
        // Events of one machine stay in order. A deadline bounds the wait.

        private class Deferred { public int Gate; public double Stamp; public float Deadline; public Func<bool> Hold; public Action Apply; }
        private static readonly List<Deferred> DeferredEvents = new List<Deferred>();
        private static readonly HashSet<int> DeferBlocked = new HashSet<int>();

        /// <param name="gate">machine id whose playback the event belongs to (events of one gate stay in order)</param>
        /// <param name="hold">optional: keep waiting while this is true (bounded by the deadline)</param>
        private static void RunOrDefer(int gate, Func<bool> hold, Action apply)
        {
            bool queued = false;
            foreach (Deferred d in DeferredEvents) if (d.Gate == gate) { queued = true; break; }
            Remotes.TryGetValue(gate, out Remote rem);
            double stamp = rem != null && rem.Count > 0 ? rem.Buf[rem.Count - 1].Time : double.NegativeInfinity;
            if (!queued && !MustWait(rem, stamp) && (hold == null || !hold()))
            {
                apply();
                return;
            }
            DeferredEvents.Add(new Deferred
            {
                Gate = gate,
                Stamp = stamp,
                // Exit release: playback delay + ExitDeadline margin; plus the reliable resend slack.
                Deadline = Time.unscaledTime + VehiclePlayback.MaxDelay + 1.3f,
                Hold = hold,
                Apply = apply,
            });
        }

        private static bool MustWait(Remote rem, double stamp)
        {
            if (rem == null || rem.Machine == null) return false; // not remote-driven (any more)
            if (rem.Exiting) return true;                        // getting out: after the release
            return rem.Count > 0 && (!rem.ClockSet || rem.RenderClock < stamp);
        }

        private static void FlushDeferred()
        {
            if (DeferredEvents.Count == 0) return;
            float now = Time.unscaledTime;
            DeferBlocked.Clear();
            for (int i = 0; i < DeferredEvents.Count;)
            {
                Deferred d = DeferredEvents[i];
                bool wait = DeferBlocked.Contains(d.Gate);
                if (!wait && now < d.Deadline)
                {
                    Remotes.TryGetValue(d.Gate, out Remote rem);
                    wait = MustWait(rem, d.Stamp) || (d.Hold != null && d.Hold());
                }
                if (wait) { DeferBlocked.Add(d.Gate); i++; continue; }
                DeferredEvents.RemoveAt(i);
                try { d.Apply(); }
                catch (Exception ex) { VehicleTraffic.Warn("deferred", "Machine sync: deferred event: " + ex.Message); }
            }
        }

        /// <summary>Hooks/unhooks a trailer here exactly like the game does, without re-broadcasting.</summary>
        /// <param name="quiet">a repeated self-heal attempt: no log lines</param>
        private static void ApplyHitch(Pickup p, Trailer t, bool connected, Vector3 pos, Quaternion rot, bool place, bool quiet = false)
        {
            if (t._Hook == null) return;
            _applyingHitch = true;
            try
            {
                // Do not move a trailer whose pose the network already drives (towed by a remote driver).
                bool held = false;
                foreach (var kv in Remotes) if (kv.Value.Tr == t) held = true;
                if (connected)
                {
                    if (t._Hook.IsConnected && p.ConnectedTrailer == t) return;
                    if (t._Hook.IsConnected)
                    {
                        Pickup other = PickupOf(t);
                        if (other != null) t._Hook.PickupCanConnect = other;
                        t.DisconnectInstantly();
                    }
                    if (p.ConnectedTrailer != null && p.ConnectedTrailer != t)
                    {
                        Trailer old = p.ConnectedTrailer;
                        old._Hook.PickupCanConnect = p;
                        old.DisconnectInstantly();
                    }
                    if (place && !held && t.MyRigidbody != null) PlaceBody(t.MyRigidbody, pos, rot);
                    if (TrailerConnect == null) { Plugin.Log.LogWarning("Machine sync: Trailer.Connect not found"); return; }
                    t._Hook.PickupCanConnect = p;
                    TrailerConnect.Invoke(t, null);
                    if (!quiet) Plugin.Log.LogInfo($"Machine sync: hooked {t.name} to {p.name} like the other player");
                    // Remote-driven right now: hold the trailer bed too (asks the driver for its cargo).
                    if (Remotes.ContainsKey(IdOf(p))) BedSync.OnRemoteDriveStart(p);
                }
                else
                {
                    if (!t._Hook.IsConnected) return;
                    // DisconnectInstantly dereferences _Hook.PickupCanConnect (other pickups' HookUpdate may have cleared it).
                    Pickup owner = PickupOf(t);
                    if (owner == null) owner = p; // Unity null: a destroyed pickup is not C# null
                    t._Hook.PickupCanConnect = owner;
                    if (place && !held && t.MyRigidbody != null) PlaceBody(t.MyRigidbody, pos, rot);
                    t.DisconnectInstantly();
                    Plugin.Log.LogInfo($"Machine sync: unhooked {t.name} from {owner.name} like the other player");
                }
            }
            catch (Exception ex)
            {
                if (!quiet) VehicleTraffic.Warn("hitch", "Machine sync: trailer hitch failed: " + ex.Message);
            }
            finally
            {
                _applyingHitch = false;
            }
        }

        /// <summary>Local hitch (Trailer.Connect, private; also used by ConnectToPickup and on save load).</summary>
        [HarmonyPatch]
        private static class PatchTrailerConnect
        {
            private static bool Prepare() => TrailerConnect != null;
            private static MethodBase TargetMethod() => TrailerConnect;

            private static void Postfix(Trailer __instance)
            {
                // PurchaseSync replays another PC's purchase / sale / parcel reset: that PC sends its own hitch.
                if (_applyingHitch || PurchaseApplying() || !Net.Active || GameState.IsLoading) return;
                Pickup p = __instance._Hook != null ? __instance._Hook.PickupCanConnect : null;
                try { SendHitch(__instance, p, true); }
                catch (Exception ex) { Plugin.Log.LogWarning("Machine sync: hitch send: " + ex.Message); }
                try
                {
                    // Hooked while driving: the game leaves its cargo loose until the next EnterToVehicle, and
                    // loose items on a towed trailer end up differently on every PC. Freeze it like
                    // EnterToVehicle does; BedSync sends this driver's set to the others.
                    TransportBed bed = __instance.MyTransformBed;
                    if (p != null && LocallyDriven(p) && bed != null && !bed.ObjectsFreezed && bed.isActiveAndEnabled)
                        bed.FreezeObjects();
                }
                catch (Exception ex) { Plugin.Log.LogWarning("Machine sync: trailer cargo freeze: " + ex.Message); }
            }
        }

        /// <summary>Local unhitch: Trailer.Disconnect (private, delayed 1 s) and DisconnectInstantly.</summary>
        [HarmonyPatch]
        private static class PatchTrailerDisconnect
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var m1 = AccessTools.Method(typeof(Trailer), "Disconnect");
                var m2 = AccessTools.Method(typeof(Trailer), nameof(Trailer.DisconnectInstantly));
                if (m1 != null) yield return m1;
                if (m2 != null) yield return m2;
            }

            private static bool Prepare()
            {
                foreach (var _ in TargetMethods()) return true;
                Plugin.Log.LogWarning("Machine sync: Trailer.Disconnect not found, unhitching is not synced");
                return false;
            }

            private static void Prefix(Trailer __instance)
            {
                if (_applyingHitch || PurchaseApplying() || !Net.Active || GameState.IsLoading) return;
                if (__instance._Hook == null || !__instance._Hook.IsConnected) return;
                try { SendHitch(__instance, PickupOf(__instance), false); }
                catch (Exception ex) { Plugin.Log.LogWarning("Machine sync: unhitch send: " + ex.Message); }
            }
        }

        // ------------------------------------------------------------------ rest poses (sub message -8)
        //
        // Nobody drives a machine: every PC simulates its body itself. After its driver gets out it may still
        // roll (released with the driver's last velocity), slide or creep on a slope, and the copies end up a
        // few centimetres to decimetres apart (physics is not deterministic between PCs). Measured in game: a
        // pickup that rolled 6.5 m after its driver got out rested 11 cm apart on the two PCs, and its cargo
        // with it. The host owns machines nobody drives: once per second it checks every such body; one at rest
        // (< 5 cm/s) that moved more than 2 cm since it was last sent is sent, and repeated every 4 s for 30 s
        // (a client still playing the last drive to its end ignores it); a client puts its copy there when it
        // differs by more than 3 cm. Not applied while the client sits in it, drives a machine
        // within 15 m (it may be pushing it) or stands within 3 m on foot (it may be touching it).

        private const float RestScanInterval = 1f;
        private const float RestSpeed = 0.05f;
        private const float RestSendMove = 0.02f;
        private const float RestApplyError = 0.03f;
        private static float _nextRestScan;
        private const float RestRepeat = 4f;     // a rest pose is repeated this often ...
        private const float RestRepeatFor = 30f; // ... for this long after the body stopped there
        private sealed class RestState { public Vector3 Pos; public float SentAt, Since; }
        private static readonly Dictionary<long, RestState> RestSent = new Dictionary<long, RestState>();

        private static void SyncResting()
        {
            if (!Net.IsHost || Net.Peers.Count == 0 || !WorldReady()) return;
            float now = Time.unscaledTime;
            if (now < _nextRestScan) return;
            _nextRestScan = now + RestScanInterval;
            foreach (var kv in Ids)
            {
                MachineController m = kv.Key;
                if (m == null) continue;
                bool driven = Remotes.ContainsKey(kv.Value) || m.IsControlled();
                TrySendRest(KindMachine, kv.Value, NameHashOf(m), m, m.MyRigidbody, driven);
            }
            foreach (var kv in TrById)
            {
                Trailer t = kv.Value;
                if (t == null) continue;
                Pickup p = t._Hook != null && t._Hook.IsConnected ? PickupOf(t) : null;
                bool towed = p != null && (p.IsControlled() || Remotes.ContainsKey(IdOf(p)));
                TrySendRest(KindTrailer, kv.Key, NameHash(t.name), t, t.MyRigidbody, towed || HeldRemotely(t));
            }
        }

        private static void TrySendRest(byte kind, int id, ushort name, Component c, Rigidbody rb, bool streamed)
        {
            long key = ((long)kind << 32) | (uint)id;
            // Driven / towed: its pose streams. Forget the last rest pose, so the pose after the drive is sent.
            if (streamed) { RestSent.Remove(key); return; }
            if (rb == null || rb.isKinematic || !c.gameObject.activeInHierarchy) return;
            if (rb.velocity.sqrMagnitude > RestSpeed * RestSpeed || rb.angularVelocity.sqrMagnitude > 0.01f) return;
            Vector3 pos = rb.position;
            float now = Time.unscaledTime;
            // Repeated for a while: a client still playing the last drive to its end (coasting) ignores it.
            if (RestSent.TryGetValue(key, out RestState st) && (st.Pos - pos).sqrMagnitude < RestSendMove * RestSendMove)
            {
                if (now - st.Since > RestRepeatFor || now - st.SentAt < RestRepeat) return;
                st.SentAt = now;
            }
            else RestSent[key] = new RestState { Pos = pos, SentAt = now, Since = now };
            var ms = Net.Writer(Msg.MachinePose);
            var w = new BinaryWriter(ms);
            w.Write(SubRest);
            w.Write(_epoch);
            w.Write(kind);
            w.Write(id);
            w.Write(name);
            WriteV3(w, pos);
            Quaternion q = rb.rotation;
            w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
            byte[] data = ms.ToArray();
            Net.Broadcast(data, reliable: true);
            VehicleTraffic.Tx(VehicleTraffic.Machine, data.Length);
        }

        private static void ReceiveRest(BinaryReader r)
        {
            byte kind = r.ReadByte();
            int id = r.ReadInt32();
            ushort name = r.ReadUInt16();
            Vector3 pos = ReadV3(r);
            var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (Net.IsHost || !WorldReady() || !IsFinite(pos) || !Normalize(ref rot)) return;
            Component c;
            Rigidbody rb;
            if (kind == KindMachine)
            {
                MachineController m = Get(id);
                if (m == null || NameHashOf(m) != name || Remotes.ContainsKey(id) || m.IsControlled()) return;
                c = m;
                rb = m.MyRigidbody;
            }
            else
            {
                Trailer t = GetTrailer(id);
                if (t == null || NameHash(t.name) != name || HeldRemotely(t)) return;
                Pickup p = t._Hook != null && t._Hook.IsConnected ? PickupOf(t) : null;
                if (p != null && (p.IsControlled() || Remotes.ContainsKey(IdOf(p)))) return;
                c = t;
                rb = t.MyRigidbody;
            }
            if (rb == null || rb.isKinematic || !c.gameObject.activeInHierarchy) return;
            if ((rb.position - pos).sqrMagnitude < RestApplyError * RestApplyError && Quaternion.Angle(rb.rotation, rot) < 1f) return;
            // The local player may be acting on it: let its own physics win; a later rest pose corrects it.
            MachineController mine = LocalDrivenMachine;
            if (mine != null && (mine.transform.position - pos).sqrMagnitude < 15f * 15f) return;
            if (mine == null && Singleton<RigidbodyFPSWalker>.IsInstanced() &&
                (Singleton<RigidbodyFPSWalker>.Instance.transform.position - pos).sqrMagnitude < 3f * 3f) return;
            float err = Vector3.Distance(rb.position, pos);
            PlaceBody(rb, pos, rot);
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            if (err > 0.5f) Plugin.Log.LogInfo($"Machine sync: {c.gameObject.name} (#{id}) moved {err:0.00} m to its rest pose on the host");
        }

        // ------------------------------------------------------------------ game teleports (sub message -6)
        //
        // The game moves machines and trailers nobody drives from local UI/logic on ONE PC: Depot.Teleport via
        // the laptop transport (LaptopTransport), the transport service (TransportGUI), TeleportToClosestClaim
        // (rental buyout, auto reset of lost objects, Trailer/MachineController SanityCheck), fast travel with
        // a trailer (MapMenu: the trailer is unhooked and teleported), the hotel trip (SeasonController).
        // Every Teleport(Vector3, Quaternion, DepotPlace) override is patched: the result (pose, active state,
        // area parent) is sent one frame later, once DepotPlace / MapMenu finished re-parenting it. Machines and
        // trailers whose pose already streams (driven here or remotely, towed) are not sent. A local teleport
        // of a machine another player is driving right now (fast travel by a passenger, transport of a machine
        // in use) is refused: the driver's PC owns its pose.

        private static readonly HashSet<Component> PendingTeleports = new HashSet<Component>();

        // True while PurchaseSync applies another PC's purchase / sale / parcel reset.
        private static bool PurchaseApplying() => PurchaseSync.Applying;
        private static bool _applyingTeleport;
        private static float _nextTeleportRefusedLog;
        private static readonly MethodInfo ResetMachineJoints = AccessTools.Method(typeof(MachineController), "TryResetMachineJoints");
        private static readonly MethodInfo KoparkaResetAllJoints = AccessTools.Method(typeof(Koparka), "ResetAllJoints");

        /// <summary>True if the pose of this machine/trailer comes from another player's PC right now.</summary>
        private static bool HeldRemotely(Component c)
        {
            if (Remotes.Count == 0 || c == null) return false;
            if (c is MachineController m) return Ids.TryGetValue(m, out int id) && Remotes.ContainsKey(id);
            if (c is Trailer t)
                foreach (var kv in Remotes)
                    if (kv.Value.Tr == t) return true;
            return false;
        }

        private static void FlushTeleports()
        {
            if (PendingTeleports.Count == 0) return;
            if (!WorldReady()) { PendingTeleports.Clear(); return; }
            var list = new List<Component>(PendingTeleports);
            PendingTeleports.Clear();
            foreach (Component c in list)
            {
                if (c == null) continue;
                try { SendTeleport(c); }
                catch (Exception ex) { Plugin.Log.LogWarning("Machine sync: teleport send: " + ex.Message); }
            }
        }

        private static void SendTeleport(Component c)
        {
            byte kind;
            int id;
            ushort name;
            Rigidbody rb;
            if (c is MachineController m)
            {
                id = IdOf(m);
                // Driven here: the pose stream carries the jump (snap detection on the receivers).
                if (id < 0 || Remotes.ContainsKey(id) || m.IsControlled()) return;
                kind = KindMachine;
                name = NameHashOf(m);
                rb = m.MyRigidbody;
            }
            else if (c is Trailer t)
            {
                id = TrailerIdOf(t);
                if (id < 0 || HeldRemotely(t)) return;
                // Still hooked to a pickup that is driven: the towing pose stream carries it.
                Pickup p = t._Hook != null && t._Hook.IsConnected ? PickupOf(t) : null;
                if (p != null && (p.IsControlled() || Remotes.ContainsKey(IdOf(p)))) return;
                kind = KindTrailer;
                name = NameHash(t.name);
                rb = t.MyRigidbody;
            }
            else return;

            Transform tr = c.transform;
            string area = "";
            List<AreaStreamer> areas = AreaStreamer.Instances;
            if (areas != null && tr.parent != null)
                foreach (AreaStreamer a in areas)
                    if (a != null && a.GameplayObjects == tr.parent) { area = a.sceneName ?? ""; break; }

            var ms = Net.Writer(Msg.MachinePose);
            var w = new BinaryWriter(ms);
            w.Write(SubTeleport);
            w.Write(_epoch);
            w.Write(kind);
            w.Write(id);
            w.Write(name);
            WriteV3(w, tr.position);
            Quaternion q = tr.rotation;
            w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
            w.Write((byte)(c.gameObject.activeSelf ? 1 : 0));
            w.Write(area);
            byte[] data = ms.ToArray();
            Net.Broadcast(data, reliable: true);
            VehicleTraffic.Tx(VehicleTraffic.Machine, data.Length);
            Plugin.Log.LogInfo($"Machine sync: sent teleport of {c.gameObject.name} (#{id}) to {tr.position}" +
                               (area.Length > 0 ? " in " + area : "") + (c.gameObject.activeSelf ? "" : " (stored)"));
        }

        private static void ReceiveTeleport(BinaryReader r)
        {
            byte kind = r.ReadByte();
            int id = r.ReadInt32();
            ushort name = r.ReadUInt16();
            Vector3 pos = ReadV3(r);
            var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            bool active = r.ReadByte() != 0;
            string area = r.ReadString();
            if (!WorldReady() || !IsFinite(pos) || !Normalize(ref rot)) return;
            ApplyTeleport(kind, id, name, pos, rot, active, area, mayDefer: true);
        }

        private static void ApplyTeleport(byte kind, int id, ushort name, Vector3 pos, Quaternion rot, bool active, string area, bool mayDefer)
        {
            // The sender teleports a machine/trailer nobody drives there. Here it may still be held by a playback
            // that has not caught up: its last driver getting out (released a moment later, at the exit pose), or
            // a towing pickup whose unhitch (fast travel) is still queued. Apply once nothing holds it any more.
            if (mayDefer)
            {
                Component held = null;
                int gate = -1;
                if (kind == KindMachine)
                {
                    MachineController hm = Get(id);
                    if (hm != null && Remotes.ContainsKey(id)) { held = hm; gate = id; }
                }
                else
                {
                    Trailer tq = GetTrailer(id);
                    if (tq != null)
                    {
                        foreach (var kv in Remotes)
                            if (kv.Value.Tr == tq) { held = tq; gate = kv.Key; }
                        if (gate < 0 && tq._Hook != null && tq._Hook.IsConnected)
                        {
                            Pickup tp = PickupOf(tq);
                            int pid = IdOf(tp);
                            if (pid >= 0 && Remotes.ContainsKey(pid)) { held = tq; gate = pid; }
                        }
                    }
                }
                bool queued = false;
                if (gate >= 0) foreach (Deferred d in DeferredEvents) if (d.Gate == gate) { queued = true; break; }
                if (held != null && (queued || (Remotes.TryGetValue(gate, out Remote hr) && hr.Exiting) || kind != KindMachine))
                {
                    Component hc = held;
                    RunOrDefer(gate, () => hc != null && HeldRemotely(hc), () => ApplyTeleport(kind, id, name, pos, rot, active, area, mayDefer: false));
                    return;
                }
            }

            Component c;
            Rigidbody rb;
            MachineController m = null;
            if (kind == KindMachine)
            {
                m = Get(id);
                if (m == null || NameHashOf(m) != name)
                {
                    Plugin.Log.LogWarning($"Machine sync: teleport for unknown machine #{id}");
                    return;
                }
                // Someone drives it (its PC streams the pose), or we sit in it ourselves.
                if (Remotes.ContainsKey(id) || m.IsControlled()) return;
                c = m;
                rb = m.MyRigidbody;
            }
            else
            {
                Trailer t = GetTrailer(id);
                if (t == null || NameHash(t.name) != name)
                {
                    Plugin.Log.LogWarning($"Machine sync: teleport for unknown trailer #{id}");
                    return;
                }
                if (HeldRemotely(t)) return;
                Pickup p = t._Hook != null && t._Hook.IsConnected ? PickupOf(t) : null;
                if (p != null && p.IsControlled()) return; // we tow it: our stream is authoritative
                c = t;
                rb = t.MyRigidbody;
            }

            _applyingTeleport = true;
            try
            {
                GameObject go = c.gameObject;
                if (active && !go.activeSelf) go.SetActive(true);
                if (rb != null)
                {
                    if (!rb.isKinematic)
                    {
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                    PlaceBody(rb, pos, rot);
                }
                else c.transform.SetPositionAndRotation(pos, rot);

                if (m != null)
                {
                    // What MachineController.Teleport does besides moving (holders: ObjectSync replicates the
                    // sender's detaches; fuel/wear: MachinePartsSync).
                    if (m.MyVehicleController != null) m.MyVehicleController.NotifyReposition();
                    if (go.activeInHierarchy)
                    {
                        ResetMachineJoints?.Invoke(m, null);
                        if (m is Koparka && KoparkaResetAllJoints != null && KoparkaResetAllJoints.Invoke(m, null) is System.Collections.IEnumerator e)
                            m.StartCoroutine(e);
                    }
                }

                AreaStreamer target = null;
                if (area.Length > 0 && AreaStreamer.Instances != null)
                    foreach (AreaStreamer a in AreaStreamer.Instances)
                        if (a != null && a.GameplayObjects != null && a.sceneName == area) { target = a; break; }
                if (target != null)
                {
                    if (go.transform.parent != target.GameplayObjects) ParentToArea(go, target);
                }
                else SettleInArea(go, pos);

                if (!active && go.activeSelf) go.SetActive(false); // stored (StoreDepotPlace)
                Plugin.Log.LogInfo($"Machine sync: {go.name} (#{id}) teleported like on the other PC" + (active ? "" : " (stored)"));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Machine sync: teleport apply: " + ex.Message);
            }
            finally
            {
                _applyingTeleport = false;
            }
        }

        [HarmonyPatch]
        private static class PatchTeleport
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var sig = new[] { typeof(Vector3), typeof(Quaternion), typeof(DepotPlace) };
                foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(MachineController).Assembly))
                {
                    if (t == null || (!typeof(MachineController).IsAssignableFrom(t) && !typeof(Trailer).IsAssignableFrom(t))) continue;
                    MethodInfo mi = AccessTools.DeclaredMethod(t, "Teleport", sig);
                    if (mi != null && !mi.IsAbstract) yield return mi;
                }
            }

            private static bool Prepare()
            {
                foreach (var _ in TargetMethods()) return true;
                Plugin.Log.LogWarning("Machine sync: no Teleport methods found, game teleports of vehicles are not synced");
                return false;
            }

            private static bool Prefix(Component __instance)
            {
                if (_applyingTeleport || !Net.Active || !HeldRemotely(__instance)) return true;
                if (Time.unscaledTime >= _nextTeleportRefusedLog)
                {
                    _nextTeleportRefusedLog = Time.unscaledTime + 5f;
                    Plugin.Log.LogInfo($"Machine sync: {__instance.gameObject.name} is driven by another player, local teleport refused");
                    // Transport menu / fast travel: tell the player why nothing moved.
                    Plugin.Instance?.SetStatus("Otro jugador está conduciendo ese vehículo: no se puede mover ahora");
                }
                return false;
            }

            private static void Postfix(Component __instance)
            {
                if (_applyingTeleport || !Net.Active || GameState.IsLoading || __instance == null || HeldRemotely(__instance)) return;
                // PurchaseSync replays a purchase / parcel reset / rental return of another PC through the game's own
                // Depot.Teleport -> DepotPlace.TransportGameObject -> Teleport: the clicker already sends the result.
                if (PurchaseApplying()) return;
                PendingTeleports.Add(__instance);
            }
        }

        /// <summary>
        /// MachineController.SanityCheck teleports a flipped controlled machine (a passenger's PC counts as
        /// controlled) or a fast-spinning uncontrolled one to the nearest claim. For a remote-driven machine
        /// its driver's PC decides; here it would yank the machine away from the driver's stream.
        /// </summary>
        [HarmonyPatch]
        private static class PatchSanityCheck
        {
            private static MethodBase Target => AccessTools.Method(typeof(MachineController), "SanityCheck");
            private static bool Prepare() => Target != null;
            private static MethodBase TargetMethod() => Target;
            // Also not on a passenger's PC during the lag grace (the driver's stream paused, Remotes released): the
            // machine counts as controlled here, and a flip check would teleport it on this PC only.
            private static bool Prefix(MachineController __instance) =>
                !HeldRemotely(__instance) && !(PassengerSync.IsLocalPassenger && __instance.IsControlled());
        }

        /// <summary>
        /// Game Updates that set isKinematic every frame (FrankensteinExcavator: belt on and not controlled;
        /// MaximusMachineController: belt state) would hand a remote-driven body back to physics between our
        /// FixedUpdate moves. Both call base.Update last: keep the body kinematic right after it.
        /// </summary>
        [HarmonyPatch]
        private static class PatchMachineUpdateKinematic
        {
            private static MethodBase Target => AccessTools.Method(typeof(MachineController), nameof(MachineController.Update));
            private static bool Prepare() => Target != null;
            private static MethodBase TargetMethod() => Target;

            private static void Postfix(MachineController __instance)
            {
                if (Remotes.Count == 0 || !Ids.TryGetValue(__instance, out int id) || !Remotes.ContainsKey(id)) return;
                Rigidbody rb = __instance.MyRigidbody;
                if (rb != null && !rb.isKinematic) rb.isKinematic = true;
            }
        }

        /// <summary>The isKinematic value a body held by MachineSync (remote-driven machine, towed trailer) gets back on release.</summary>
        internal static bool TryGetHeldKinematic(Rigidbody rb, out bool original)
        {
            original = false;
            if (rb == null || Remotes.Count == 0) return false;
            foreach (var kv in Remotes)
            {
                Remote rem = kv.Value;
                if (rem.Machine != null && rem.Machine.MyRigidbody == rb) { original = false; return true; } // see RestoreBody
                if (rem.Tr != null && rem.Tr.MyRigidbody == rb)
                {
                    original = rem.TrWasKinematic && (rem.Tr._Hook == null || !rem.Tr._Hook.IsConnected); // see ReleaseTrailer
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The game saves every Rigidbody's isKinematic (XmlRigidbody). A save taken while a machine is
        /// remote-driven (the host's save for a joining player, autosave, a manual save) stored our kinematic
        /// stand-in, so after loading that save the machine (or its arm parts) hung frozen in the air for
        /// good. Store the value the body gets back when the remote drive ends.
        /// </summary>
        [HarmonyPatch]
        private static class PatchXmlRigidbodySerialize
        {
            private static MethodBase Target => AccessTools.Method(typeof(SaveSystem.XmlRigidbody), nameof(SaveSystem.XmlRigidbody.Serialize), new[] { typeof(Component) });
            private static bool Prepare() => Target != null;
            private static MethodBase TargetMethod() => Target;

            private static void Postfix(SaveSystem.XmlRigidbody __instance, Component component)
            {
                if (!Net.Active || !(component is Rigidbody rb)) return;
                try
                {
                    if (TryGetHeldKinematic(rb, out bool k) || MachinePartsSync.TryGetHeldKinematic(rb, out k))
                        __instance.IsKinematic = k;
                }
                catch (Exception ex) { VehicleTraffic.Warn("save kinematic", "Machine sync: save kinematic fix: " + ex.Message); }
            }
        }

        /// <summary>Teleports a body (no MovePosition sweep, so nothing gets flung).</summary>
        private static void PlaceExactly(MachineController m, Vector3 pos, Quaternion rot)
        {
            Rigidbody rb = m.MyRigidbody;
            if (rb != null)
            {
                rb.position = pos;
                rb.rotation = rot;
            }
            m.transform.SetPositionAndRotation(pos, rot);
        }

        /// <summary>Gives the body back to local physics and undoes everything the remote drive changed.</summary>
        private static void PlaceBody(Rigidbody rb, Vector3 pos, Quaternion rot)
        {
            rb.position = pos;
            rb.rotation = rot;
            rb.transform.SetPositionAndRotation(pos, rot);
        }

        private static void RestoreBody(int id, Remote rem, Snap? final, bool driverLost)
        {
            MachineController m = rem.Machine;
            bool finalTrailer = final.HasValue && final.Value.HasTrailer;
            ReleaseTrailer(rem, finalTrailer ? final.Value.TVel : (Vector3?)null, finalTrailer ? final.Value.TAngVel : Vector3.zero);
            if (m != null)
            {
                Rigidbody rb = m.MyRigidbody;
                if (rb != null)
                {
                    rb.interpolation = rem.OrigInterp;
                    // A parked machine is a dynamic body. WasKinematic is not trusted: it may come from a save
                    // taken while the machine was remote-driven (older builds stored our kinematic stand-in)
                    // or from a game toggle caught mid-way (Frankenstein/Maximus belt, Trailer.CreateJoint).
                    // Machines that are kinematic on purpose (belt running) set it again in their own Update.
                    rb.isKinematic = false;
                    if (rem.WasKinematic) Plugin.Log.LogInfo($"Machine sync: {m.gameObject.name} was kinematic before the remote drive, released as a dynamic body");
                    if (final.HasValue)
                    {
                        // Keep rolling the way it did on the driver's PC when they got out.
                        rb.velocity = final.Value.Vel;
                        rb.angularVelocity = final.Value.AngVel;
                    }
                }
                // Hide the driver again unless the local player is the one in it.
                if (m.WorkerPerson != null && !m.IsControlled())
                    m.WorkerPerson.gameObject.SetActive(false);
                if (!m.IsControlled()) SettleInArea(m.gameObject, rb != null ? rb.position : m.transform.position);
                Plugin.Log.LogInfo($"Machine sync: released {m.gameObject.name} (#{id}){(final.HasValue ? "" : driverLost ? " (driver lost)" : " (reset)")}");
                // The driver vanished without getting out: do not leave its engine running (burning fuel) here.
                if (driverLost) MachinePartsSync.OnDriverLost(m);
            }
            BedSync.OnRemoteDriveStop(m, id);
        }

        private static bool Normalize(ref Quaternion q)
        {
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (!(mag > 0.5f) || float.IsInfinity(mag)) return false;
            q = new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
            return true;
        }

        /// <summary>
        /// Pickup.FixedUpdate calls TurnOffKinematic every physics step, which made a remote-driven
        /// pickup a dynamic body again right after we made it kinematic: it fell/rolled between our
        /// pose updates, was teleported (SetPositionAndRotation) instead of moved, and cargo on its bed
        /// was not carried. Keep it kinematic while its pose comes from the network.
        /// </summary>
        [HarmonyPatch]
        private static class PatchPickupTurnOffKinematic
        {
            private static MethodBase Target => AccessTools.Method(typeof(Pickup), "TurnOffKinematic");

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Machine sync: Pickup.TurnOffKinematic not found, remote pickups may jitter");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static bool Prefix(Pickup __instance) =>
                Remotes.Count == 0 || !Ids.TryGetValue(__instance, out int id) || !Remotes.ContainsKey(id);
        }

        private static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
            !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        private static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadV3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        private static void WriteHalf3(BinaryWriter w, Vector3 v)
        {
            w.Write(Mathf.FloatToHalf(Mathf.Clamp(v.x, -1000f, 1000f)));
            w.Write(Mathf.FloatToHalf(Mathf.Clamp(v.y, -1000f, 1000f)));
            w.Write(Mathf.FloatToHalf(Mathf.Clamp(v.z, -1000f, 1000f)));
        }

        private static Vector3 ReadHalf3(BinaryReader r) =>
            new Vector3(Mathf.HalfToFloat(r.ReadUInt16()), Mathf.HalfToFloat(r.ReadUInt16()), Mathf.HalfToFloat(r.ReadUInt16()));
    }
    /// <summary>
    /// Vehicle traffic counters (MachineSync poses and sub messages, MachinePartsSync parts and state, BedSync),
    /// logged every 5 minutes (the log file stays small over a long session), rate-limited warnings for errors that
    /// could repeat every frame, plus a reused packet writer for the per-frame senders (no MemoryStream/BinaryWriter
    /// allocation per packet; ToArray is still needed because the transport may keep the buffer).
    /// </summary>
    internal static class VehicleTraffic
    {
        internal const int Machine = 0, Parts = 1, Bed = 2;
        private static readonly string[] Names = { "MachineSync", "MachinePartsSync", "BedSync" };
        private static readonly int[] TxPk = new int[3], RxPk = new int[3];
        private static readonly long[] TxB = new long[3], RxB = new long[3];
        private static float _next = -1f;
        private const float Window = 300f;

        // Warning key -> (next time it may be logged, occurrences suppressed since).
        private static readonly Dictionary<string, KeyValuePair<float, int>> WarnGate = new Dictionary<string, KeyValuePair<float, int>>();

        /// <summary>
        /// A warning that may repeat every frame or every packet while its cause lasts (a broken game object, a
        /// missing trailer): logged at most once a minute per <paramref name="key"/>, with the count it stood for.
        /// </summary>
        internal static void Warn(string key, string message, bool error = false)
        {
            float now = Time.unscaledTime;
            int suppressed = 0;
            if (WarnGate.TryGetValue(key, out var g))
            {
                if (now < g.Key) { WarnGate[key] = new KeyValuePair<float, int>(g.Key, g.Value + 1); return; }
                suppressed = g.Value;
            }
            if (WarnGate.Count > 256) WarnGate.Clear();
            WarnGate[key] = new KeyValuePair<float, int>(now + 60f, 0);
            string text = suppressed > 0 ? $"{message} (+{suppressed} more like it in the last minute)" : message;
            if (error) Plugin.Log.LogError(text);
            else Plugin.Log.LogWarning(text);
        }

        private static readonly MemoryStream Ms = new MemoryStream(1200);
        private static readonly BinaryWriter W = new BinaryWriter(Ms);
        private static bool _busy;

        /// <summary>Starts a packet in the shared buffer. Must be finished with Send before the next Begin.</summary>
        internal static BinaryWriter Begin(Msg type)
        {
            if (_busy) Warn("nested", "Vehicle traffic: a packet build was abandoned (exception while writing it) or nested");
            _busy = true;
            Ms.SetLength(0);
            Ms.WriteByte((byte)type);
            return W;
        }

        internal static void Send(int channel, bool reliable)
        {
            W.Flush();
            byte[] data = Ms.ToArray();
            _busy = false;
            Net.Broadcast(data, reliable);
            Tx(channel, data.Length);
        }

        /// <summary>Counts one broadcast (bytes on the wire: one copy per peer).</summary>
        internal static void Tx(int channel, int bytes)
        {
            int peers = Net.Peers.Count;
            if (peers == 0) return;
            TxPk[channel] += peers;
            TxB[channel] += (long)bytes * peers;
        }

        internal static void Rx(int channel, int bytes)
        {
            RxPk[channel]++;
            RxB[channel] += bytes;
        }

        /// <summary>Called every frame by MachineSync.Tick: one log line per 5 minutes if anything moved.</summary>
        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (_next < 0f) { _next = now + Window; return; }
            if (now < _next) return;
            _next = now + Window;
            bool any = false;
            for (int i = 0; i < 3; i++) any |= TxPk[i] > 0 || RxPk[i] > 0;
            if (!any) return;
            var sb = new System.Text.StringBuilder("Vehicle traffic (5 min, own sends incl. one copy per peer):");
            for (int i = 0; i < 3; i++)
            {
                sb.Append(i == 0 ? " " : " | ").Append(Names[i])
                  .Append(" tx ").Append(TxPk[i]).Append(" pkt ").Append((TxB[i] / 1024f).ToString("0.0")).Append(" KB")
                  .Append(" (").Append((TxB[i] / Window / 1024f).ToString("0.00")).Append(" KB/s)")
                  .Append(" rx ").Append(RxPk[i]).Append(" pkt ").Append((RxB[i] / 1024f).ToString("0.0")).Append(" KB");
                TxPk[i] = RxPk[i] = 0;
                TxB[i] = RxB[i] = 0;
            }
            Plugin.Log.LogInfo(sb.ToString());
        }
    }
    #region VehiclePlayback (pure: UnityEngine Vector3/Quaternion/Mathf only, driven by the review harness)
    /// <summary>
    /// Snapshot playback of a remote-driven machine: clock offset estimate (min filter with slow upward
    /// relax and a lasting-latency window), adaptive render delay (peak-following jitter), sorted jitter
    /// buffer (loss, duplicates, reordering), cubic Hermite interpolation, eased extrapolation, and a
    /// correction offset that keeps the drawn pose continuous whenever late or missing data changes the
    /// path (end of an extrapolation, a reordered snapshot filling a gap, a render clock reset). The
    /// correction fades out exponentially instead of snapping. No game types: a console harness feeds it
    /// lossy, reordered, jittery streams and checks per-step jumps and convergence.
    /// </summary>
    internal static class VehiclePlayback
    {
        internal const float SendInterval = 0.05f;   // driver's pose rate while moving (20 Hz)
        internal static float MinDelay = 0.1f;       // render at least this far in the past (Net.AdaptRenderDelay)
        internal const float MaxDelay = 0.3f;        // adaptive delay cap
        internal const float DelayMargin = 0.02f;    // on top of one send interval + jitter
        internal const float ExtrapLinear = 0.2f;    // extrapolate with full velocity this long ...
        internal const float ExtrapTail = 0.15f;     // ... then ease out: total distance <= v * 0.35 s
        internal const float OffsetRelax = 0.0005f;  // s per packet the clock offset may rise (drift, latency)
        internal const float CorrectionRate = 8f;    // 1/s, fade of start-of-drive and correction offsets
        internal const float ClockSlew = 0.1f;       // render clock runs at most 10 % fast/slow
        internal const float ClockResetAt = 0.5f;    // render clock this far off its target: reset (corrected)
        internal const int BufSize = 16;

        internal struct Snap
        {
            public double Time; // sender clock (double: float time loses ~1 ms resolution after 2-3 h of play)
            public Vector3 Pos, Vel, AngVel;
            public Quaternion Rot;
            public bool HasTrailer;
            public int TId;      // the towed trailer's MachineSync trailer id (-1 unknown)
            public ushort TName; // NameHash of the towed trailer's object name
            public Vector3 TPos, TVel, TAngVel;
            public Quaternion TRot;
        }

        internal enum Mode : byte { None, HoldFirst, Extrapolate, Interpolate, HoldLast }

        internal class Track
        {
            public readonly Snap[] Buf = new Snap[BufSize]; // sorted by Time, oldest first
            public int Count;

            public bool HasOffset;
            public double Offset;      // estimated local receive time - sender time (min latency + clock offset)
            public float Jitter;       // peak-following extra latency above Offset
            public double WinMin = double.MaxValue, WinEnd; // 1 s window minimum of (receive - sender time)
            public bool ClockSet;
            public double RenderClock; // sender-clock time currently shown

            public bool Exiting;       // driver got out: hold the final snapshot, MachineSync releases
            public bool SnapPending;   // teleport: place without MovePosition / blending

            // Offsets added to the sampled pose (body / towed trailer), faded out by Step.
            public Vector3 ErrPos;
            public Quaternion ErrRot = Quaternion.identity;
            public Vector3 TrErrPos;
            public Quaternion TrErrRot = Quaternion.identity;

            // Segment drawn on the previous step (to detect path changes).
            public Mode SegMode;
            public Snap SegA, SegB;

            public void ResetStream()
            {
                HasOffset = false;
                Count = 0;
                ClockSet = false;
                Exiting = false;
                SegMode = Mode.None;
            }

            public void ClearError()
            {
                ErrPos = Vector3.zero;
                ErrRot = Quaternion.identity;
            }
        }

        internal static float Delay(Track t) => Mathf.Clamp(SendInterval + t.Jitter + DelayMargin, MinDelay, MaxDelay);

        /// <summary>Adds a received snapshot: clock estimate, teleport detection, sorted insert (drops duplicates).</summary>
        internal static void AddSnapshot(Track t, Snap s, double now)
        {
            // Clock offset: the smallest (local receive time - sender time) seen is "minimum latency + clock
            // difference"; it may creep up slowly (clock drift, latency growth). The sender is always the same
            // player here (driver changes reset the stream), so a lag spike never throws the buffer away.
            double o = now - s.Time;
            if (!t.HasOffset || Math.Abs(o - t.Offset) > 30.0)
            {
                // First packet of this driver, or its game restarted (other clock).
                t.HasOffset = true;
                t.Offset = o;
                t.Jitter = 0f;
                t.Count = 0;
                t.ClockSet = false;
                t.WinMin = double.MaxValue;
                t.WinEnd = now + 1.0;
            }
            else
            {
                t.Offset = Math.Min(o, t.Offset + OffsetRelax);
                // Peak follower: rises quickly with late packets and decays over a few seconds, so a burst of
                // jitter keeps the buffer deep for a while instead of being averaged away by punctual packets.
                float late = (float)Math.Min(o - t.Offset, 0.5);
                t.Jitter = late > t.Jitter ? Mathf.Lerp(t.Jitter, late, 0.3f) : Mathf.Lerp(t.Jitter, late, 0.01f);
                // Latency rose for good (route change): every packet of the last second was later than the
                // estimate by > 100 ms. Adopt the new minimum; the render clock slews to it.
                t.WinMin = Math.Min(t.WinMin, o);
                if (now >= t.WinEnd)
                {
                    if (t.WinMin - t.Offset > 0.1f) t.Offset = t.WinMin;
                    t.WinMin = double.MaxValue;
                    t.WinEnd = now + 1.0;
                }
            }

            if (t.Count > 0)
            {
                Snap newest = t.Buf[t.Count - 1];
                float dt = (float)(s.Time - newest.Time);
                if (dt > 0f)
                {
                    // Teleport (reset, tow, respawn): moved much farther than its speed allows.
                    float speed = Mathf.Max(newest.Vel.magnitude, s.Vel.magnitude);
                    float allowed = (speed * 1.5f + 10f) * dt + 2f;
                    if ((s.Pos - newest.Pos).sqrMagnitude > allowed * allowed)
                    {
                        t.Count = 0;
                        t.SnapPending = true;
                        t.ClockSet = false;
                        t.SegMode = Mode.None;
                    }
                }
            }

            // Sorted insert (unreliable packets may arrive out of order); drop duplicates and snapshots
            // playback has already passed.
            int i = t.Count;
            while (i > 0 && t.Buf[i - 1].Time > s.Time) i--;
            if (i > 0 && Math.Abs(t.Buf[i - 1].Time - s.Time) < 0.0001) return;
            if (i < t.Count && Math.Abs(t.Buf[i].Time - s.Time) < 0.0001) return;
            if (i == 0 && t.Count > 0 && t.ClockSet && s.Time <= t.RenderClock) return;
            if (t.Count == BufSize)
            {
                if (i == 0) return; // older than everything we keep
                Array.Copy(t.Buf, 1, t.Buf, 0, BufSize - 1);
                t.Count--;
                i--;
            }
            if (i < t.Count) Array.Copy(t.Buf, i, t.Buf, i + 1, t.Count - i);
            t.Buf[i] = s;
            t.Count++;
        }

        /// <summary>
        /// One physics step: advances the render clock (real-time rate, nudged at most ClockSlew toward
        /// "now - offset - delay"), drops passed snapshots, turns any discontinuity of the sampled path into a
        /// correction offset, and fades the offsets out.
        /// </summary>
        internal static void Step(Track t, double fixedNow, float fixedDt)
        {
            if (t.Count == 0) return;
            double target = fixedNow - t.Offset - Delay(t);
            double prevTr = t.RenderClock;
            bool jumped = false;
            if (!t.ClockSet || Math.Abs(target - t.RenderClock) > ClockResetAt)
            {
                jumped = t.ClockSet;
                t.RenderClock = target;
                t.ClockSet = true;
            }
            else
            {
                t.RenderClock += fixedDt * (1f + Mathf.Clamp((float)(target - t.RenderClock) * 2f, -ClockSlew, ClockSlew));
            }

            // Drop snapshots that playback has passed (keep the one right before the render time).
            int drop = 0;
            while (t.Count - drop >= 2 && t.Buf[drop + 1].Time <= t.RenderClock) drop++;
            if (drop > 0)
            {
                Array.Copy(t.Buf, drop, t.Buf, 0, t.Count - drop);
                t.Count -= drop;
            }

            double tr = t.RenderClock;
            Mode mode = Segment(t, out Snap a, out Snap b);
            if (t.SegMode != Mode.None && !Continuous(t, mode, a, b, jumped))
            {
                // The path changed under the drawn pose: keep drawing where we were (old segment at the time
                // it would show now) and fade the difference out.
                double oldTr = jumped ? prevTr + fixedDt : tr;
                Eval(t.SegMode, t.SegA, t.SegB, oldTr, out Vector3 op, out Quaternion oq);
                Eval(mode, a, b, tr, out Vector3 np, out Quaternion nq);
                t.ErrPos += op - np;
                t.ErrRot = t.ErrRot * oq * Quaternion.Inverse(nq);
                if (EvalTrailer(t.SegMode, t.SegA, t.SegB, oldTr, out Vector3 otp, out Quaternion otq) &&
                    EvalTrailer(mode, a, b, tr, out Vector3 ntp, out Quaternion ntq))
                {
                    t.TrErrPos += otp - ntp;
                    t.TrErrRot = t.TrErrRot * otq * Quaternion.Inverse(ntq);
                }
            }
            t.SegMode = mode;
            t.SegA = a;
            t.SegB = b;

            float keep = Mathf.Exp(-fixedDt * CorrectionRate);
            Fade(ref t.ErrPos, ref t.ErrRot, keep);
            Fade(ref t.TrErrPos, ref t.TrErrRot, keep);
        }

        private static void Fade(ref Vector3 p, ref Quaternion q, float keep)
        {
            if (p != Vector3.zero)
            {
                p *= keep;
                if (p.sqrMagnitude < 1e-6f) p = Vector3.zero;
            }
            if (q != Quaternion.identity)
            {
                q = Quaternion.Slerp(Quaternion.identity, q, keep);
                if (Quaternion.Angle(q, Quaternion.identity) < 0.05f) q = Quaternion.identity;
            }
        }

        /// <summary>True if going from the previous step's segment to this one keeps the path continuous.</summary>
        private static bool Continuous(Track t, Mode mode, Snap a, Snap b, bool jumped)
        {
            if (jumped) return false;
            if (mode == t.SegMode && a.Time == t.SegA.Time && (mode != Mode.Interpolate || b.Time == t.SegB.Time)) return true;
            // Playback ran over a knot into the next segment (or out of data at the newest snapshot).
            if (t.SegMode == Mode.Interpolate && a.Time == t.SegB.Time) return true;
            // From holding the oldest snapshot into interpolating from it.
            if (t.SegMode == Mode.HoldFirst && a.Time == t.SegA.Time) return true;
            return false;
        }

        private static Mode Segment(Track t, out Snap a, out Snap b)
        {
            a = t.Buf[0];
            b = a;
            if (t.RenderClock <= a.Time) return Mode.HoldFirst; // start, after a teleport
            if (t.Count == 1) return t.Exiting ? Mode.HoldLast : Mode.Extrapolate;
            b = t.Buf[1]; // a.Time < tr < b.Time after trimming
            return Mode.Interpolate;
        }

        internal static bool ExitReached(Track t) => t.Exiting && t.Count == 1 && t.RenderClock >= t.Buf[0].Time;

        /// <summary>True once the correction offset is negligible (an exact placement now does not jump).</summary>
        internal static bool Settled(Track t) =>
            t.ErrPos.sqrMagnitude < 0.02f * 0.02f && Quaternion.Angle(t.ErrRot, Quaternion.identity) < 0.5f;

        /// <summary>Raw pose at the render clock (add ErrPos/ErrRot to draw it).</summary>
        internal static void Sample(Track t, out Vector3 pos, out Quaternion rot)
        {
            Mode mode = Segment(t, out Snap a, out Snap b);
            Eval(mode, a, b, t.RenderClock, out pos, out rot);
        }

        /// <summary>Raw pose of the towed trailer at the render clock; false if the driver tows nothing then.</summary>
        internal static bool SampleTrailer(Track t, out Vector3 pos, out Quaternion rot)
        {
            Mode mode = Segment(t, out Snap a, out Snap b);
            return EvalTrailer(mode, a, b, t.RenderClock, out pos, out rot);
        }

        private static void Eval(Mode mode, Snap a, Snap b, double tr, out Vector3 pos, out Quaternion rot)
        {
            if (mode == Mode.Interpolate) Interpolate(a.Pos, a.Rot, a.Vel, a.Time, b.Pos, b.Rot, b.Vel, b.Time, tr, out pos, out rot);
            else if (mode == Mode.Extrapolate) Extrapolate(a.Pos, a.Rot, a.Vel, a.AngVel, (float)(tr - a.Time), out pos, out rot);
            else { pos = a.Pos; rot = a.Rot; }
        }

        private static bool EvalTrailer(Mode mode, Snap a, Snap b, double tr, out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = Quaternion.identity;
            if (mode != Mode.Interpolate)
            {
                if (!a.HasTrailer) return false;
                if (mode == Mode.Extrapolate) Extrapolate(a.TPos, a.TRot, a.TVel, a.TAngVel, (float)(tr - a.Time), out pos, out rot);
                else { pos = a.TPos; rot = a.TRot; }
                return true;
            }
            if (a.HasTrailer && b.HasTrailer)
            {
                Interpolate(a.TPos, a.TRot, a.TVel, a.Time, b.TPos, b.TRot, b.TVel, b.Time, tr, out pos, out rot);
                return true;
            }
            // Hitched/unhitched between these two snapshots: keep the older state until the newer one.
            if (!a.HasTrailer) return false;
            pos = a.TPos;
            rot = a.TRot;
            return true;
        }

        /// <summary>Extrapolation time actually used <paramref name="dt"/> s past the newest snapshot (eased, at most 0.35 s).</summary>
        internal static float ExtrapTime(float dt)
        {
            if (dt <= ExtrapLinear) return Mathf.Max(dt, 0f);
            return ExtrapLinear + ExtrapTail * (1f - Mathf.Exp(-(dt - ExtrapLinear) / ExtrapTail));
        }

        internal static void Extrapolate(Vector3 p, Quaternion q, Vector3 v, Vector3 w, float dt, out Vector3 pos, out Quaternion rot)
        {
            // Late packets: keep moving with the last velocities, easing to a stop (no hard halt). The velocity
            // turns with the angular velocity (constant turn rate), so a vehicle in a curve stays on its arc.
            float ex = ExtrapTime(dt);
            rot = q;
            float wm = w.magnitude;
            if (wm * ex < 1e-3f)
            {
                pos = p + v * ex;
                return;
            }
            Vector3 axis = w / wm;
            const int n = 4; // midpoint sub-steps
            float h = ex / n;
            pos = p;
            for (int k = 0; k < n; k++) pos += Quaternion.AngleAxis(wm * (k + 0.5f) * h * Mathf.Rad2Deg, axis) * v * h;
            rot = Quaternion.AngleAxis(wm * ex * Mathf.Rad2Deg, axis) * rot;
        }

        internal static void Interpolate(Vector3 aP, Quaternion aR, Vector3 aV, double aT, Vector3 bP, Quaternion bR, Vector3 bV, double bT,
            double tr, out Vector3 pos, out Quaternion rot)
        {
            float dt = (float)(bT - aT);
            float s = dt > 0f ? Mathf.Clamp01((float)((tr - aT) / (bT - aT))) : 1f;
            rot = Quaternion.Slerp(aR, bR, s);
            if (dt > 0.3f || dt <= 0f)
            {
                // Long gap (packet loss): the velocities do not describe the path between them.
                pos = Vector3.Lerp(aP, bP, s);
                return;
            }
            // A collision changes the velocity abruptly. When a snapshot's velocity disagrees with the chord
            // between the two snapshots, Hermite would overshoot past the impact point: use the chord.
            Vector3 chord = (bP - aP) / dt;
            float tol = 2f + 0.5f * chord.magnitude;
            if ((aV - chord).sqrMagnitude > tol * tol || (bV - chord).sqrMagnitude > tol * tol)
            {
                pos = Vector3.Lerp(aP, bP, s);
                return;
            }
            // Cubic Hermite with the sent velocities: smooth through curves, no corner at each snapshot.
            float s2 = s * s, s3 = s2 * s;
            float h00 = 2f * s3 - 3f * s2 + 1f, h10 = s3 - 2f * s2 + s, h01 = -2f * s3 + 3f * s2, h11 = s3 - s2;
            pos = h00 * aP + (h10 * dt) * aV + h01 * bP + (h11 * dt) * bV;
        }
    }
    #endregion
}
