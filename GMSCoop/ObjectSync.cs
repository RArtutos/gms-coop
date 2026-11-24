// WIRING (already in place): Net.Handle "case (Msg)12: ObjectSync.Receive(r)", Plugin.Update
// "ObjectSync.Tick()", Plugin.FixedUpdate "ObjectSync.FixedTick()". PurchaseSync calls RegisterObject
// with the same key on every PC for objects spawned during the session.
// WIRING (round 3, for the lead): Net.Handle "case (Msg)12: ObjectSync.Receive(r, p.From)" (maps player
// LocalIds to transport peers on the host) and Net.OnPeerLeft "ObjectSync.OnPeerLeft(id)" (first line
// after Peers.Remove). Without them the host still releases a leaver's objects once no peer is left.
// WIRING (round 4): SaveTransfer's existing ObjectSync.ResetIndex() calls are the hooks. On the HOST in a
// running world (right after its QuickSave for a joiner) ResetIndex no longer renumbers anything: it
// records the save's ordinals (OnHostSaved) so the host keeps its keys and players already in the session
// keep matching; the joiner renames its save ordinals to the host's keys from the Sync dump. On a client
// it resets as before. Optional (lead): SaveTransfer.CheckSaveFinished "ObjectSync.AssignSnapshot(p);"
// for every peer that gets the save (picks the right snapshot when transfers overlap). ContainerSync
// should re-index when ObjectSync.KeyVersion changes.
// ROUND 5: KeysReady / RefKey / TranslateOrdinal / IndexGeneration let PurchaseSync name objects a client
// sold before the host's key sync (deferred until the sync, by save ordinal). No new wiring needed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GoldDigger;
using HarmonyLib;
using SaveSystem;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Real-time sync of loose physics objects (pans, buckets, gold bags, tools, shop items...).
    ///
    /// Identity: the game has no network ids. SaveSystem.SaveMe.SaveID names the prefab TYPE only.
    /// When a save loads, CheckPointManagerBase instantiates the saved objects in file order and each
    /// SaveMe.Awake appends itself to CheckPointManagerBase.ToSave, so that list has the same order on
    /// every PC that loaded the same save. Key = "SaveID#k" where k counts earlier entries with the
    /// same SaveID. The index is built once, ~2 s after the world is ready. Objects spawned later get
    /// keys from the network through RegisterObject.
    ///
    /// Ownership: every object has at most one owner (LocalId, 0 = nobody) and an epoch (u16).
    ///  - Nobody owns it: every PC simulates it locally (the state right after a load).
    ///  - The owner simulates it and streams it; every other PC makes it kinematic and follows.
    ///    Non-owners keep it kinematic at the owner's exact rest pose afterwards (no drift).
    ///  - A player takes ownership when it grabs the object (epoch + 2) or when its body, held object
    ///    or driven machine comes near it, or when an unowned object starts moving locally (epoch + 1).
    ///  - Tie-break, identical on every PC: higher epoch wins, equal epoch -> lower LocalId wins.
    ///    Stale packets (lower epoch) are ignored. A grab beats a simultaneous proximity claim.
    ///  - Never claimed by proximity or local motion: loads tied to a machine another player drives (rope
    ///    hooks on it or on a trailer it tows, its pick rope) and objects owned by a player who drives a
    ///    machine within 15 m of them. That driver's PC simulates what its machine pushes and drags.
    ///
    /// Protocol, all inside (Msg)12 with a sub-kind byte:
    ///   Grab   (reliable)   holder, key, epoch
    ///   Drop   (reliable)   holder, key, epoch, [stamp f64, pose, vel, angVel]
    ///   Poses  (unreliable) sender, stamp f64, n x (key, epoch, flags, pos f32x3, rot smallest-three u32,
    ///                       vel f16x3, angVel f16x3); 20 Hz while owned objects move or are held,
    ///                       packets kept below ~1150 bytes
    ///   Rest   (reliable)   sender, key, epoch, stamp f64, exact pose, kinematic   (object came to rest)
    ///   Claim  (reliable)   claimer, key, epoch
    ///   Attach (reliable)   sender, key, epoch, attached, [holder ref], kinematic, pose
    ///                       (the item went into / out of a Holder: the receiver runs Holder.AttachObject
    ///                       on the same holder, so the item rides on its joint, or Holdable.Detach)
    ///   SyncReq (reliable)  client -> host after the client indexed the transferred world
    ///   Sync   (reliable)   host -> that client: key, epoch, owner, pose, holder of every object
    ///                       (objects moved / attached while the world was in transit) plus, per
    ///                       object, its ordinal key in the transferred save: the client renames its
    ///                       save-ordinal keys to the host's network keys, so the host never re-keys
    ///                       (players already in the session keep their keys on every join).
    ///                       A client neither sends nor applies keyed messages before that.
    ///   Create (reliable)   sender, key, SaveID, pose, stack size, component data: a saved physics
    ///                       object the game created on this PC only (gold bars, spawned holdables...)
    ///   Adopt  (reliable)   same layout, host -> clients: a saved object the host keyed only now (at a
    ///                       join save); a client keys its own unkeyed copy nearby or creates it
    ///   Rope   (reliable)   sender, part-0 key, coiled / laid out / dock flags, coil pose, part poses:
    ///                       a rope (Hook_Rope) or fuel hose (Fuel_Rope) changed shape. Its child holdables
    ///                       (hooks, coil, fuel pistol) are "part" entries keyed "<rope key>/<n>"
    ///   Passenger           handled by PassengerSync
    ///
    /// Non-owned objects stay kinematic only while they follow a stream; ~1 s after the owner's rest
    /// pose they are handed back to local physics (game scripts expect dynamic bodies). If one then
    /// starts moving locally it is claimed like an unowned object. The host takes over (claims) every
    /// object of a player who left, and a client drops all following when the session ends.
    ///
    /// Receiver: snapshot interpolation per sender (SyncClock: min-filtered clock offset, playback delay
    /// 100-300 ms that adapts to packet loss and jitter, never jumps or runs backwards), applied with
    /// MovePosition/MoveRotation on the kinematic body in FixedUpdate. When playback had to run past the
    /// newest snapshot (lost / late packets), the correction once data arrives is faded out (~0.1 s).
    /// Bad networks: poses are unreliable and not ordered with the reliable messages (Steam channels,
    /// resends), so a pose older than the newest Rest, a "held" pose of a hold that was already dropped,
    /// a pose of an epoch an attach was applied for, or an older stamp than the newest applied, never
    /// changes the state again (SyncLogic guards; checked offline by a lossy / reordering test harness).
    /// Objects frozen in a truck bed (BedSync.IsFrozenItem; TransportBed deactivates and re-parents
    /// cargo) or inside an unloaded streamed area are excluded (IsSuppressed); BedSync handles cargo.
    /// </summary>
    internal static partial class ObjectSync
    {
        internal const Msg MsgId = (Msg)12;

        internal const byte SubGrab = 0;
        internal const byte SubDrop = 2;
        internal const byte SubPassenger = 4;
        internal const byte SubPoses = 5;
        internal const byte SubRest = 6;
        internal const byte SubClaim = 7;
        internal const byte SubAttach = 8;
        internal const byte SubSyncReq = 9;
        internal const byte SubSync = 10;
        internal const byte SubStackOp = 11;
        internal const byte SubDestroy = 12;
        internal const byte SubCreate = 13;
        internal const byte SubAdopt = 14;
        internal const byte SubRope = 15;
        internal const byte SubRepair = 16; // RepairHolder filled / emptied (ObjectSyncRepair.cs)
        internal const byte SubJack = 18;   // car jack height (ObjectSyncRepair.cs; 17 = SubState)

        private const float RestRelease = 1f;              // follower: back to local physics after the rest pose
        private const float SyncRetry = 4f;
        private const int SyncMaxTries = 5;
        private const float SendInterval = 0.05f;          // 20 Hz
        private const float RestTime = 0.3f;               // still this long -> send the final pose
        private const float MoveEpsSqr = 0.002f * 0.002f;  // moved more than 2 mm since the last send
        private const float MoveEpsAngle = 0.3f;           // or turned more than 0.3 degrees
        private const float RemoteHoldTimeout = 5f;
        private const float FollowTimeout = 3f;            // owner went silent mid-motion: release
        private const float ReclaimCooldown = 0.75f;       // no proximity claim right after losing it
        private const float IndexSettleTime = 2f;
        private const int MaxPacketBytes = 1150;            // Steam unreliable limit is 1200

        // Snapshot interpolation tuning (playback delay / clock: SyncClock).
        private const float MaxExtrapolate = 0.2f;
        private const float SnapDistance = 5f;
        private const float ErrorFade = 0.1f;   // a correction after a gap fades out with this time constant
        private const float MaxSmoothedError = 2f;
        private const int BufSize = 8;

        // =====================================================================================
        // State
        // =====================================================================================

        private struct Snap : ITimed
        {
            public double Time; // sender clock (Time.unscaledTimeAsDouble: exact after hours of play)
            public Vector3 Pos, Vel, AngVel;
            public Quaternion Rot;
            public double Stamp => Time;
        }

        private sealed class Entry
        {
            public string Key;
            public GameObject Go;
            public int InstanceId;
            public Rigidbody Rb;
            public Holdable Hold;

            public ulong Owner;        // 0 = nobody
            public ushort Epoch;
            public float RemoteClaimAt;
            public ulong HeldBy;       // remote holder (grab-blocking), 0 = none
            public float HeldUpdate;

            // Owner side
            public bool Streaming, NeedRest;
            public float StillTime;
            public Vector3 SentPos;
            public Quaternion SentRot = Quaternion.identity;

            // Follower side
            public bool Following;     // we made it kinematic
            public bool AuthKinematic; // kinematic state the owner reports (restored if we take over)
            public RigidbodyInterpolation OrigInterp;
            public Snap[] Buf;
            public int Count;
            public bool SnapPending;
            public float LastSnapLocal;
            public bool HasRest;
            public double RestStamp;
            public ulong LastRestFrom;  // newest rest pose received (kept after the release to physics)
            public double LastRest;
            public Vector3 RestPos;
            public Quaternion RestRot;
            public Vector3 LastVel;
            public float ReleaseAt;
            // Newest pose stamp applied (a late, older pose must not change the kinematic state / hold).
            public double NewestStamp = double.NegativeInfinity;
            // Last Drop received: its sender and epoch (held poses of that epoch are older than the Drop)
            // and whether it had no pose (destroyed in the hand / put in a frozen bed: ignore that epoch).
            public ulong DropFrom;
            public int DropEpoch = -1;
            public bool DropNoPose;
            // Visual smoothing: playback ran past the newest snapshot (extrapolating / holding) and the
            // correction once data arrives again is faded out instead of jumping.
            public bool Extrapolating;
            public Vector3 ErrPos;
            public Quaternion ErrRot = Quaternion.identity;
            public Vector3 ShownPos;
            public Quaternion ShownRot = Quaternion.identity;
            public bool Shown;

            // For destroy sync: prefab id, last seen position and scene (an object of a scene that is
            // no longer loaded was unloaded, not destroyed by gameplay).
            public string Sid = "";
            public Vector3 LastPos;
            public UnityEngine.SceneManagement.Scene Scene;

            // Holder state the network knows (detects local attach / detach) and the epoch of an
            // attach applied from the network (older poses of that epoch must not detach it again).
            public Holder SentHolder;
            public int AttachEpoch = -1;

            // Ordinal key from the loaded save ("SaveID#k") while the client has not yet learned the
            // host's network key for it (null otherwise).
            public string SaveKey;
            // Created through the network / by this PC at this time (duplicate creation guard).
            public float CreatedAt = -100f;
            public bool CreatedRemote, CreatedLocal;
            // Destroyed on purpose by a path that announces it itself (sale, rental return, parcel
            // reset) before the keys were shared: kept until the host's key sync renames it (so the
            // deferred announcement can name it and the sync does not re-create it), never re-announced.
            public bool Quiet;
            // Child holdable of a rope (Hook_Rope hooks, coiled rope): keyed "<rope key>/<n>". Never
            // re-parented, never announced as destroyed (the rope root is), never created on its own.
            public bool Part;
            public int PartIndex;
            public RopeInfo Rope;

            public bool Attached => RidesHolder(Hold);
        }

        private sealed class Clock
        {
            public readonly SyncClock Sync = new SyncClock();
            public double Render;   // sender-clock time currently shown
            public double AdvancedAt = -1.0;
        }

        private static readonly Dictionary<string, Entry> ByKey = new Dictionary<string, Entry>();
        private static readonly Dictionary<int, Entry> ByInstance = new Dictionary<int, Entry>();
        private static readonly List<Entry> All = new List<Entry>();
        private static readonly HashSet<Entry> Owned = new HashSet<Entry>();
        private static readonly HashSet<Entry> Active = new HashSet<Entry>(); // followed + have snapshots
        private static readonly HashSet<Entry> Held = new HashSet<Entry>();   // HeldBy != 0
        private static readonly Dictionary<ulong, Clock> Clocks = new Dictionary<ulong, Clock>();
        private static readonly HashSet<int> Suppressed = new HashSet<int>();
        private static readonly List<Entry> _tmp = new List<Entry>();
        private static readonly HashSet<Entry> Releasing = new HashSet<Entry>(); // rested followers
        private static readonly Dictionary<ulong, ulong> OwnerVia = new Dictionary<ulong, ulong>(); // host: LocalId -> transport peer
        private static readonly List<ulong> _tmpIds = new List<ulong>();
        private static readonly List<Transform> _path = new List<Transform>();
        private static int _peerCount;
        private static bool _applyingRemote; // inside a network apply: Hook_Rope's local reactions are blocked
        private static bool _syncWanted;
        private static float _syncNext;
        private static int _syncTries;
        private static bool _applyingAttach; // GravityGun.DropObject is blocked while set
        private static bool _applyingDetach; // same, for a remote detach (Holdable.Detach run for another player)

        /// <summary>Inside the apply of another player's packet (InteractionGuard: no local-only side effects).</summary>
        internal static bool ApplyingRemote => _applyingRemote;

        /// <summary>
        /// A holder attach / detach of another player is being applied here. Game code run by it must not
        /// pick up or drop objects for the LOCAL player (RopeTrommel.Detach picks the drum up, Holdable.Attach
        /// drops what the player holds, ...).
        /// </summary>
        internal static bool ApplyingHolderChange => _applyingAttach || _applyingDetach;

        /// <summary>Holdable.Detach on behalf of another player (see ApplyingHolderChange).</summary>
        private static void RemoteDetach(Entry e)
        {
            bool old = _applyingDetach;
            _applyingDetach = true;
            try { e.Hold.Detach(); }
            finally { _applyingDetach = old; }
        }

        private static bool _indexed;
        private static bool _synced;          // client: keys renamed to the host's (host: always true)
        private static int _indexedSceneHandle = int.MinValue;
        private static float _readySince = -1f;

        /// <summary>Changes whenever object keys are (re)assigned; ContainerSync re-indexes on change.</summary>
        internal static int KeyVersion;

        private static GameObject _localHeld;
        private static Entry _localEntry;
        private static float _nextSend;
        private static int _tickCount;
        private static int _scanCursor;

        // Proximity detection caches.
        private static readonly Collider[] Hits = new Collider[64];
        private static int _mask = -1;
        private static Player _player;
        private static Rigidbody _playerRb;
        private static Collider[] _playerCols;
        private static MachineController _machine;
        private static Collider[] _machineCols;
        private static Collider[] _heldCols;

        // =====================================================================================
        // Index / identity
        // =====================================================================================

        /// <summary>
        /// Called by SaveTransfer: on a client after loading the host's world (forget every id, the index
        /// is rebuilt once the world is ready); on the host right after the save for a joiner, where the
        /// host keeps its keys and only records the save's ordinals (see OnHostSaved).
        /// </summary>
        internal static void ResetIndex()
        {
            // Host in a running world (also when its index is not built yet: OnHostSaved builds it from the
            // list the save was just written from, so the snapshot exists and later joins keep keys).
            if (Net.Active && Net.IsHost && !GameState.IsLoading && Singleton<Player>.IsInstanced() &&
                SaveSystem.Singleton<CheckPointManagerBase>.IsInstanced() &&
                SaveSystem.Singleton<CheckPointManagerBase>.Instance.GetLoadingState() == LoadingState.ReadyToPlay)
            {
                OnHostSaved();
                return;
            }
            ClearIndex();
        }

        /// <summary>Forgets every id; the index is rebuilt automatically once the world is ready.</summary>
        private static void ClearIndex()
        {
            ClearRepair();
            foreach (Entry e in All) StopFollow(e, restorePhysics: true);
            PendingRests.Clear();
            ByKey.Clear();
            ByInstance.Clear();
            All.Clear();
            Owned.Clear();
            Active.Clear();
            Held.Clear();
            Clocks.Clear();
            Suppressed.Clear();
            Releasing.Clear();
            PendingOps.Clear();
            PendingDestroys.Clear();
            _op = null;
            _syncWanted = false;
            _syncSeen.Clear();
            _syncItems.Clear();
            _spawnQueue.Clear();
            Snapshots.Clear();
            SnapshotOfPeer.Clear();
            _localHeld = null;
            _localEntry = null;
            _machine = null;
            _heldCols = null;
            _indexed = false;
            _synced = false;
            _readySince = -1f;
            _scanCursor = 0;
            _ordMap.Clear();
            _ordIdentity = false;
            Ropes.Clear();
            _remoteTowed.Clear();
            _sceneRopesDirty = true;
            KeyVersion++;
            IndexGeneration++;
        }

        /// <summary>Changes whenever the index is dropped (new world / reload): ordinals of an older index mean nothing.</summary>
        internal static int IndexGeneration;

        /// <summary>
        /// Keys mean the same object on every PC (index built; on a client, renamed to the host's keys).
        /// Purchase messages that name objects wait for this.
        /// </summary>
        internal static bool KeysReady => Net.Active && Ready && WorldReady();

        // Client: save ordinal -> host key, filled by the host's key sync (names objects that were sold /
        // returned before the sync in messages sent after it). Identity when the host has no snapshot.
        private static readonly Dictionary<string, string> _ordMap = new Dictionary<string, string>();
        private static bool _ordIdentity;

        /// <summary>
        /// Key to name <paramref name="go"/> in a message. Once keys are shared: its network key. Before
        /// (client between load and the host's key sync): a network key that never changes (purchase /
        /// stack / create keys), or else its save ordinal with <paramref name="ordinal"/> = true, which
        /// <see cref="TranslateOrdinal"/> turns into the host's key after the sync. Null if it has none.
        /// </summary>
        internal static string RefKey(GameObject go, out bool ordinal)
        {
            ordinal = false;
            if (!Net.Active || go == null) return null;
            if (Ready) return EntryOf(go)?.Key;
            if (!_indexed) TryIndexNow();
            Entry e = EntryOf(go);
            if (e == null) return null;
            ordinal = e.SaveKey != null && e.Key == e.SaveKey;
            return e.Key;
        }

        /// <summary>Client, after the host's key sync: the host's key for a save ordinal ("" if the host has none).</summary>
        internal static string TranslateOrdinal(string ord)
        {
            if (string.IsNullOrEmpty(ord) || !Ready) return "";
            if (_ordIdentity) return ord;
            return _ordMap.TryGetValue(ord, out string k) ? k : "";
        }

        /// <summary>Builds the index now if the world is ready for it (a message must name an object early).</summary>
        private static void TryIndexNow()
        {
            if (_indexed || !Net.Active || !WorldReady() || !Singleton<Player>.IsInstanced() ||
                !SaveSystem.Singleton<CheckPointManagerBase>.IsInstanced()) return;
            var cpm = SaveSystem.Singleton<CheckPointManagerBase>.Instance;
            if (cpm.GetLoadingState() != LoadingState.ReadyToPlay) return;
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene != _indexedSceneHandle) { ClearIndex(); _indexedSceneHandle = scene; }
            BuildIndex(cpm.ToSave);
            if (Ready) ApplyPendingDestroys();
        }

        /// <summary>
        /// Objects other systems sync never get a loose-object key: machines (MachineSync), trailers
        /// (MachineSync / BedSync) and dirt lumps (DirtSync: a lump carries material only on the PC that
        /// dropped it and destroys itself after 60 s or when it lands).
        /// </summary>
        // The local player's own body (RigidbodyFPSWalker, saved with a SaveMe like any object) is never a
        // synced object: PlayerSync shows each player as an avatar. Keyed, it got the same key on every PC,
        // so one player's walk was streamed into the other player's body (both players moved together),
        // and following it made the body kinematic with collisions off (the player could not move at all).
        private static bool Syncable(GameObject go) =>
            go.GetComponent<Rigidbody>() != null &&
            go.GetComponent<RigidbodyFPSWalker>() == null &&
            go.GetComponent<Player>() == null &&
            go.GetComponent<MachineController>() == null &&
            go.GetComponent<Trailer>() == null &&
            go.GetComponent<VirtualVolumeDirt>() == null;

        /// <summary>
        /// Keys mean the same object on every PC: indexed, and on a client renamed to the host's keys.
        /// Before that nothing keyed is sent, applied or handed out to other systems.
        /// </summary>
        private static bool Ready => _indexed && (_synced || !Net.Active || Net.IsHost);

        /// <summary>
        /// Gives an object a network key. Used by the index itself and by the purchase sync for objects
        /// spawned during the session (both PCs must pass the same key).
        /// </summary>
        internal static void RegisterObject(GameObject go, string key)
        {
            if (go == null || string.IsNullOrEmpty(key)) return;
            if (IsRopeRoot(go)) RegisterRopeParts(go, key, saveKeys: false);
            if (!Syncable(go)) return;
            RegisterCore(go, key);
        }

        private static void RegisterCore(GameObject go, string key)
        {
            int id = go.GetInstanceID();
            if (ByInstance.TryGetValue(id, out Entry prev) && prev.Key != key) Forget(prev);

            if (!ByKey.TryGetValue(key, out Entry e))
            {
                e = new Entry { Key = key };
                ByKey[key] = e;
                All.Add(e);
            }
            else if (e.Go != go)
            {
                StopFollow(e, restorePhysics: true);
                if (ByInstance.TryGetValue(e.InstanceId, out Entry old) && old == e) ByInstance.Remove(e.InstanceId);
            }
            e.Go = go;
            e.InstanceId = id;
            e.Rb = go.GetComponent<Rigidbody>();
            e.Hold = go.GetComponent<Holdable>();
            var sm = go.GetComponent<SaveSystem.SaveMe>();
            e.Sid = sm != null ? sm.GetSaveID() ?? "" : "";
            e.LastPos = go.transform.position;
            e.Scene = go.scene;
            e.SentHolder = CurrentHolder(e); // same on every PC (loaded from the same save / fresh spawn)
            e.AttachEpoch = -1;
            e.SaveKey = null;
            e.Quiet = false;
            e.Part = false;
            e.Rope = null;
            ByInstance[id] = e;
            KeyVersion++;
        }

        /// <summary>Gives an entry another key (late-join rename, duplicate-creation merge).</summary>
        private static void Rekey(Entry e, string key)
        {
            if (e.Key == key) return;
            if (ByKey.TryGetValue(e.Key, out Entry cur) && cur == e) ByKey.Remove(e.Key);
            e.Key = key;
            ByKey[key] = e;
            KeyVersion++;
            if (e.Go != null && Ropes.TryGetValue(e.Go.GetInstanceID(), out RopeInfo ri))
                foreach (Entry p in ri.Parts) Rekey(p, key + "/" + p.PartIndex);
        }

        /// <summary>True if this key is known here (any state; network-created keys only).</summary>
        internal static bool HasKey(string key) =>
            !string.IsNullOrEmpty(key) && (ByKey.ContainsKey(key) || ByKey.ContainsKey(key + "/0")); // a rope: its parts

        /// <summary>The holder the item is attached to right now, normalized to a real null.</summary>
        private static Holder CurrentHolder(Entry e)
        {
            if (e.Hold == null) return null;
            Holder h = e.Hold.MyHolder;
            return h != null ? h : null;
        }

        private static void Forget(Entry e)
        {
            StopFollow(e, restorePhysics: true);
            // Only the mappings that still point at this entry (a merge / re-register may have handed
            // the key or the instance to another entry meanwhile).
            if (ByKey.TryGetValue(e.Key, out Entry k) && k == e) ByKey.Remove(e.Key);
            if (ByInstance.TryGetValue(e.InstanceId, out Entry g) && g == e) ByInstance.Remove(e.InstanceId);
            All.Remove(e);
            Owned.Remove(e);
            Active.Remove(e);
            Held.Remove(e);
            Releasing.Remove(e);
            if (_localEntry == e) _localEntry = null;
        }

        private static Entry EntryOf(GameObject go) =>
            go != null && ByInstance.TryGetValue(go.GetInstanceID(), out Entry e) ? e : null;

        /// <summary>The network key of <paramref name="go"/>; null if it has none or keys are not shared yet.</summary>
        internal static string KeyOf(GameObject go) => Ready ? EntryOf(go)?.Key : null;

        /// <summary>The object with this network key, or null if unknown / destroyed / keys not shared yet.</summary>
        internal static GameObject Find(string key)
        {
            if (!Ready || string.IsNullOrEmpty(key) || !ByKey.TryGetValue(key, out Entry e) || e.Go == null) return null;
            return e.Go;
        }

        internal static bool IsRemotelyHeld(GameObject go)
        {
            Entry e = EntryOf(go);
            return e != null && e.HeldBy != 0 && e.HeldBy != Net.LocalId;
        }

        /// <summary>
        /// True if loose sync leaves this object alone: frozen in a truck bed (BedSync), inactive
        /// (TransportBed deactivates cargo; unloaded streamed area) or suppressed explicitly.
        /// </summary>
        public static bool IsSuppressed(GameObject go) =>
            go == null || !go.activeInHierarchy || Suppressed.Contains(go.GetInstanceID()) || BedSync.IsFrozenItem(go);

        /// <summary>
        /// Frozen cargo (bed) or explicitly suppressed. Unlike IsSuppressed this is false for objects that
        /// are merely inside an unloaded area (active themselves, inactive parent).
        /// </summary>
        /// <summary>
        /// True if <paramref name="h"/> sits in a holder (rides on it, the holder carries its pose).
        /// RepairHoldable.IsAttached() returns gameObject.activeSelf, so it is true for every loose spare
        /// part (excavator tooth, wheel, ...) lying around: treating those as attached stopped their pose
        /// stream after a drop (measured in game: a dropped tooth froze in mid-air on the other PC, then
        /// fell to a spot 33 cm away). A repair part is in a holder only when it has one.
        /// </summary>
        internal static bool RidesHolder(Holdable h)
        {
            if (h == null) return false;
            if (h is RepairHoldable) return h.MyHolder != null && h.gameObject.activeSelf;
            return h.IsAttached();
        }

        /// <summary>Test / diagnostics: the sync state of one object as text.</summary>
        internal static string DebugState(string key)
        {
            if (!ByKey.TryGetValue(key, out Entry e)) return "unknown";
            if (e.Go == null) return "destroyed";
            return $"own={(e.Owner == Net.LocalId ? "me" : e.Owner == 0 ? "none" : "other")} ep={e.Epoch} fol={(e.Following ? 1 : 0)} n={e.Count} " +
                   $"held={(e.HeldBy != 0 ? 1 : 0)} frz={(Frozen(e) ? 1 : 0)} sup={(Suppressed.Contains(e.InstanceId) ? 1 : 0)} att={(e.Attached ? 1 : 0)} " +
                   $"str={(e.Streaming ? 1 : 0)} rest={(e.HasRest ? 1 : 0)} kin={(e.Rb != null && e.Rb.isKinematic ? 1 : 0)} pend={(PendingRests.ContainsKey(e) ? 1 : 0)} hold={(e.Hold != null ? e.Hold.GetType().Name + (e.Hold.MyHolder != null ? "+holder" : "") : "-")}";
        }

        private static bool Frozen(Entry e) =>
            !e.Go.activeSelf || Suppressed.Contains(e.InstanceId) || BedSync.IsFrozenItem(e.Go);

        /// <summary>Excludes (or re-includes) an object from loose sync, e.g. for other sync systems.</summary>
        public static void SetSuppressed(GameObject go, bool suppressed)
        {
            if (go == null) return;
            if (suppressed)
            {
                Suppressed.Add(go.GetInstanceID());
                Entry e = EntryOf(go);
                if (e != null) { Active.Remove(e); e.Count = 0; }
            }
            else Suppressed.Remove(go.GetInstanceID());
        }

        /// <summary>
        /// The local player takes ownership of <paramref name="go"/> (right after buying it, or when a
        /// local-only game script moved it, e.g. BuyObjects pushing items aside). Its motion is streamed
        /// and its rest pose sent, so every PC ends up with the same pose.
        /// </summary>
        internal static void ClaimLocal(GameObject go)
        {
            Entry e = EntryOf(go);
            if (!Net.Active || !Ready || !WorldReady() || e == null || e.Go == null) return;
            if (e.HeldBy != 0 && e.HeldBy != Net.LocalId) return; // someone holds it: the holder wins
            if (e.Owner != Net.LocalId) LocalClaim(e, grab: false);
            else { e.NeedRest = true; e.StillTime = 0f; }
        }

        private static void EnsureIndex()
        {
            if (!Singleton<Player>.IsInstanced()) return;

            // Scene handle, not name: no string allocation per frame, and a reload of the same scene
            // counts as a new world too.
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene != _indexedSceneHandle)
            {
                ClearIndex();
                _indexedSceneHandle = scene;
            }

            if (!SaveSystem.Singleton<CheckPointManagerBase>.IsInstanced()) return;
            var cpm = SaveSystem.Singleton<CheckPointManagerBase>.Instance;
            if (cpm.GetLoadingState() != LoadingState.ReadyToPlay)
            {
                // A save is (re)loading: drop the old index and wait for the new world.
                if (_indexed) ClearIndex();
                _readySince = -1f;
                return;
            }
            if (_indexed) return;
            if (_readySince < 0f) { _readySince = Time.unscaledTime; return; }
            // Wait a bit so objects destroyed on load (Object.Destroy is deferred) are gone from the list.
            if (Time.unscaledTime - _readySince < IndexSettleTime) return;

            BuildIndex(cpm.ToSave);
            if (Ready) ApplyPendingDestroys(); // a client applies them after the host's key sync
        }

        private static void BuildIndex(List<SaveSystem.SaveMe> list)
        {
            bool client = Net.Active && !Net.IsHost;
            var counts = new Dictionary<string, int>();
            int n = 0;
            foreach (var sm in list)
            {
                if (sm == null) continue;
                // Already keyed by the network (purchase spawned during the settle window): keep that
                // key and do not count it, the buyer's index did not contain this object either.
                int iid = sm.gameObject.GetInstanceID();
                if (ByInstance.ContainsKey(iid) || Ropes.ContainsKey(iid)) continue;
                string sid = sm.GetSaveID();
                if (string.IsNullOrEmpty(sid)) continue;
                counts.TryGetValue(sid, out int k);
                counts[sid] = k + 1; // k counts every entry of this type, synced or not
                string key = sid + "#" + k;
                if (!Syncable(sm.gameObject))
                {
                    // MachineSync / trailers handle those. A rope without a body of its own: only its parts.
                    if (IsRopeRoot(sm.gameObject)) RegisterRopeParts(sm.gameObject, key, saveKeys: client);
                    continue;
                }
                RegisterObject(sm.gameObject, key);
                if (client && ByKey.TryGetValue(key, out Entry e)) e.SaveKey = key; // renamed by the host's Sync
                if (client && Ropes.TryGetValue(iid, out RopeInfo ri)) foreach (Entry p in ri.Parts) p.SaveKey = p.Key;
                n++;
            }
            _indexed = true;
            _synced = !client;
            Plugin.Log.LogInfo($"Object sync: {n} physics objects indexed ({list.Count} save entries)");
            // Client: the save ordinals are not the host's keys, and objects moved / attached / created
            // while the world was in transit are not in the save. Ask the host for its current state
            // (retried until a Sync arrives); nothing keyed is sent or applied until then.
            if (client)
            {
                _syncWanted = true;
                _syncTries = 0;
                // The host pushes its dump on our Loaded: ask only if it has not started arriving by then.
                _syncNext = Time.unscaledTime + SyncRetry;
            }
        }

        // ---------------------------------------------------------------- host save snapshots

        /// <summary>Host: which save ordinal ("SaveID#k" in the file) each object had when a save was written.</summary>
        private sealed class Snapshot
        {
            public int Gen;
            public readonly Dictionary<Entry, string> Ordinal = new Dictionary<Entry, string>();
        }

        private static readonly List<Snapshot> Snapshots = new List<Snapshot>();
        private static readonly Dictionary<ulong, int> SnapshotOfPeer = new Dictionary<ulong, int>();
        private static int _snapGen;

        /// <summary>
        /// Host, right after the game wrote the save that goes to a joining client (same frame as
        /// QuickSave). Records each object's ordinal in that file instead of re-keying the host: the
        /// joiner indexes the file by ordinal and renames to the host's keys from the Sync dump, so
        /// players already in the session keep matching keys. Saved objects that have no key yet get
        /// one now (unique, the joiner learns it).
        /// </summary>
        internal static void OnHostSaved()
        {
            if (!Net.Active || !Net.IsHost || !SaveSystem.Singleton<CheckPointManagerBase>.IsInstanced()) return;
            var cpm = SaveSystem.Singleton<CheckPointManagerBase>.Instance;
            if (cpm.GetLoadingState() != LoadingState.ReadyToPlay) return;
            List<SaveSystem.SaveMe> list = cpm.ToSave;
            if (!_indexed)
            {
                int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                if (scene != _indexedSceneHandle) { ClearIndex(); _indexedSceneHandle = scene; }
                BuildIndex(list);
                ApplyPendingDestroys();
            }
            var snap = new Snapshot { Gen = ++_snapGen };
            var counts = new Dictionary<string, int>();
            var adopted = new List<Entry>();
            foreach (var sm in list) // QuickSave keeps this order within one SaveID (stable sort by Priority)
            {
                if (sm == null) continue;
                string sid = sm.GetSaveID();
                if (string.IsNullOrEmpty(sid)) continue;
                counts.TryGetValue(sid, out int k);
                counts[sid] = k + 1;
                GameObject go = sm.gameObject;
                Entry e = EntryOf(go);
                bool rope = Ropes.TryGetValue(go.GetInstanceID(), out RopeInfo ri);
                if (e == null && Syncable(go))
                {
                    RegisterObject(go, "sv" + _snapGen + ":" + sid + "#" + k);
                    e = EntryOf(go);
                    if (e != null) adopted.Add(e);
                }
                else if (e == null && !rope && IsRopeRoot(go))
                    RegisterRopeParts(go, "sv" + _snapGen + ":" + sid + "#" + k, saveKeys: false);
                if (e != null) snap.Ordinal[e] = sid + "#" + k;
                // Rope parts: "<ordinal>/<n>" in the file, as the joiner keys them.
                if (Ropes.TryGetValue(go.GetInstanceID(), out ri))
                    foreach (Entry p in ri.Parts) snap.Ordinal[p] = sid + "#" + k + "/" + p.PartIndex;
            }
            Snapshots.Add(snap);
            while (Snapshots.Count > 4) Snapshots.RemoveAt(0);
            // Players already in the session have these objects unkeyed (or not at all): they adopt the
            // key for their own copy, or create it. (The joiner drops it: not in the shared world yet.)
            foreach (Entry e in adopted) SendCreate(e, SubAdopt);
            Plugin.Log.LogInfo($"Object sync: save snapshot {snap.Gen} ({snap.Ordinal.Count} objects, {adopted.Count} newly keyed)");
        }

        /// <summary>Host: transport peer <paramref name="peer"/> receives the save of the newest snapshot.</summary>
        internal static void AssignSnapshot(ulong peer)
        {
            if (Snapshots.Count > 0) SnapshotOfPeer[peer] = Snapshots[Snapshots.Count - 1].Gen;
        }

        private static Snapshot SnapshotFor(ulong requester)
        {
            if (Snapshots.Count == 0) return null;
            if (OwnerVia.TryGetValue(requester, out ulong via) && SnapshotOfPeer.TryGetValue(via, out int gen))
                foreach (Snapshot s in Snapshots)
                    if (s.Gen == gen) return s;
            return Snapshots[Snapshots.Count - 1];
        }

        private static bool WorldReady() => Net.WorldLive;

        // =====================================================================================
        // Ownership
        // =====================================================================================

        /// <summary>Applies a remote ownership claim if it wins the tie-break. True if <paramref name="who"/> owns it now.</summary>
        private static bool Accept(Entry e, ulong who, ushort epoch)
        {
            if (!SyncLogic.ClaimWins(e.Owner, e.Epoch, who, epoch)) return false;
            e.Epoch = epoch;
            SetOwner(e, who);
            return true;
        }

        private static void SetOwner(Entry e, ulong who)
        {
            if (e.Owner == who) return;
            if (e.Owner == Net.LocalId)
            {
                Owned.Remove(e);
                if (_localEntry == e)
                {
                    Plugin.Log.LogInfo($"Object sync: lost {e.Key} to {Net.NameOf(who)}");
                    ForceLocalDrop(e.Go);
                }
            }
            e.Owner = who;
            e.NewestStamp = double.NegativeInfinity; // stamps of another sender's clock
            if (e.HeldBy != 0 && e.HeldBy != who) { e.HeldBy = 0; Held.Remove(e); }
            if (who == Net.LocalId)
            {
                Owned.Add(e);
                e.Streaming = false;
                e.NeedRest = true; // always publish the exact pose once it settles
                e.StillTime = 0f;
                if (e.Rb != null) { e.SentPos = e.Rb.position; e.SentRot = e.Rb.rotation; }
            }
            else
            {
                e.RemoteClaimAt = Time.unscaledTime;
            }
        }

        private static void LocalClaim(Entry e, bool grab)
        {
            e.Epoch = (ushort)(e.Epoch + (grab ? 2 : 1));
            bool wasFollowing = e.Following;
            bool wasMoving = wasFollowing && Active.Contains(e);
            Vector3 vel = e.LastVel;
            SetOwner(e, Net.LocalId);
            // A grab: GravityGun/ToolGun made it dynamic at pick time (a FixedTick between the pick and
            // this Tick may have pinned it kinematic again: undo that). Otherwise give it back the
            // owner's kinematic state (and the velocity it was moving with) so local physics continues.
            StopFollow(e, restorePhysics: !grab);
            if (grab && wasFollowing && e.Rb != null && e.Rb.isKinematic) e.Rb.isKinematic = false;
            if (!grab && wasMoving && e.Rb != null && !e.Rb.isKinematic) e.Rb.velocity = vel;
            // Picking it up detached it from its holder; the Grab tells every PC to detach too.
            if (grab) { e.SentHolder = CurrentHolder(e); e.AttachEpoch = -1; }

            var ms = Begin(grab ? SubGrab : SubClaim, out var w);
            w.Write(Net.LocalId);
            w.Write(e.Key);
            w.Write(e.Epoch);
            Bcast(ms.ToArray(), true);
        }

        /// <summary>Makes a non-owned object kinematic so it follows the owner's stream.</summary>
        private static void StartFollow(Entry e)
        {
            if (e.Following || e.Rb == null) return;
            e.Following = true;
            e.AuthKinematic = e.Rb.isKinematic;
            e.OrigInterp = e.Rb.interpolation;
            e.Rb.isKinematic = true;
            // A followed object moves where its owner's PC says, as a kinematic body: with collisions on,
            // it would push this PC's own bodies with infinite force (an object carried into a parked
            // truck shoved the truck and spilled its load on the other PC only). Raycasts still hit it, so
            // it can still be looked at and grabbed.
            e.Rb.detectCollisions = false;
            e.Rb.interpolation = RigidbodyInterpolation.Interpolate;
            e.Count = 0;
            e.HasRest = false;
            ResetSmoothing(e);
        }

        private static void ResetSmoothing(Entry e)
        {
            e.Extrapolating = false;
            e.Shown = false;
            e.ErrPos = Vector3.zero;
            e.ErrRot = Quaternion.identity;
        }

        private static void StopFollow(Entry e, bool restorePhysics)
        {
            Active.Remove(e);
            Releasing.Remove(e);
            e.Count = 0;
            e.HasRest = false;
            e.NewestStamp = double.NegativeInfinity;
            if (!e.Following) return;
            e.Following = false;
            if (e.Rb == null) return;
            e.Rb.detectCollisions = true;
            e.Rb.interpolation = e.OrigInterp;
            if (restorePhysics) e.Rb.isKinematic = e.AuthKinematic;
        }

        // =====================================================================================
        // Local side
        // =====================================================================================

        /// <summary>Called from Plugin.Update.</summary>
        internal static void Tick()
        {
            if (!Net.Active)
            {
                // Session over (host left, Shutdown): give every followed object back to local
                // physics; otherwise remote-owned objects would stay kinematic in single player.
                if (All.Count > 0 || _indexed) { ClearIndex(); OwnerVia.Clear(); _peerCount = 0; }
                return;
            }
            EnsureIndex();
            if (!_indexed || !WorldReady()) { _spawnQueue.Clear(); return; }

            float now = Time.unscaledTime;
            RequestSync(now);
            if (!Ready) { _spawnQueue.Clear(); return; } // client: keys are not the host's yet

            // Stack / sell results are announced the frame after the game ran them (Object.Destroy is
            // deferred). Hold detection waits for that, so a Grab never names a key the others lack.
            FinalizeOps();
            IndexSceneRopes(now);
            PollRopes();
            ProcessSpawns(now);
            RepairTick(now);
            if (PendingOps.Count == 0) TrackLocalHold();
            ExpireRemoteHolds();
            ApplyPendingRests(now);

            ReleaseRested(now);
            CheckDeparted();

            if (now < _nextSend) return;
            _nextSend = Mathf.Max(_nextSend + SendInterval, now - SendInterval);
            _tickCount++;
            _claimBudget = MaxClaimsPerTick;
            LogStats(now);

            SendPoses();
            if ((_tickCount & 1) == 0) ProximityClaims();
            ScanUnowned();
        }

        private static GameObject CurrentLocalHeld()
        {
            GameObject go = null;
            if (Singleton<GravityGun>.IsInstanced()) go = Singleton<GravityGun>.Instance.PickedObject;
            if (go == null && Singleton<ToolGun>.IsInstanced()) go = Singleton<ToolGun>.Instance.PickedObject;
            return go != null ? go : null; // normalize destroyed objects to a real null
        }

        private static void TrackLocalHold()
        {
            GameObject now = CurrentLocalHeld();
            if (ReferenceEquals(now, _localHeld)) return;
            if (_localEntry != null) SendDrop(_localEntry);
            _localHeld = now;
            _localEntry = EntryOf(now);
            _heldCols = now != null ? now.GetComponentsInChildren<Collider>() : null;
            if (_localEntry != null) LocalClaim(_localEntry, grab: true);
        }

        private static void SendDrop(Entry e)
        {
            var ms = Begin(SubDrop, out var w);
            w.Write(Net.LocalId);
            w.Write(e.Key);
            w.Write(e.Epoch);
            Rigidbody rb = e.Go != null ? e.Rb : null;
            // No pose when the object was destroyed while held (sold, ...) or went straight into a
            // frozen bed: BedSync carries frozen cargo.
            bool hasPose = rb != null && !Frozen(e);
            w.Write(hasPose);
            if (hasPose)
            {
                w.Write(Stamp());
                WriteV3(w, rb.position);
                WriteQ(w, rb.rotation);
                WriteV3(w, rb.isKinematic ? Vector3.zero : rb.velocity);
                WriteV3(w, rb.isKinematic ? Vector3.zero : rb.angularVelocity);
                e.Streaming = true; // keep streaming the fall, then send the rest pose
                e.NeedRest = true;
                e.StillTime = 0f;
            }
            Bcast(ms.ToArray(), true);
        }

        /// <summary>Sender time of the last physics step (poses are read from the Rigidbody).</summary>
        /// Double on the wire: a float game clock has ~1 ms resolution after 2-3 hours (~4 ms after 8 h),
        /// which made remote motion judder more the longer a session ran.
        private static double Stamp()
        {
            double sinceStep = Time.timeScale > 0.01f ? (Time.timeAsDouble - Time.fixedTimeAsDouble) / Time.timeScale : 0.0;
            return Time.unscaledTimeAsDouble - Math.Max(0.0, Math.Min(0.1, sinceStep));
        }

        private static readonly List<Entry> _sendList = new List<Entry>();
        private static int _sendRot;

        /// <summary>
        /// 20 Hz: poses of owned objects that move or are held; reliable rest pose when they stop.
        /// Budgeted per tick (MaxPosePacketsPerTick, MaxRestsPerTick); objects over budget keep their
        /// state and go first next tick (rotating start), the held object always goes first.
        /// </summary>
        private static void SendPoses()
        {
            if (Owned.Count == 0) return;
            double stamp = Stamp();
            MemoryStream ms = null;
            BinaryWriter w = null;
            long countPos = 0;
            int count = 0, packets = 0, rests = 0;
            int firstSkipped = -1;

            _tmp.Clear();
            _sendList.Clear();
            if (_localEntry != null && Owned.Contains(_localEntry)) _sendList.Add(_localEntry);
            foreach (Entry e in Owned)
            {
                if (e.Go == null || e.Rb == null || e.Owner != Net.LocalId) { _tmp.Add(e); continue; }
                if (e != _localEntry) _sendList.Add(e);
            }
            int n = _sendList.Count;
            int fixedHead = n > 0 && _sendList[0] == _localEntry ? 1 : 0;
            int span = n - fixedHead;
            int rot0 = span > 0 ? _sendRot % span : 0;
            for (int i = 0; i < n; i++)
            {
                int idx = i < fixedHead ? i : fixedHead + (rot0 + i - fixedHead) % span;
                Entry e = _sendList[idx];
                if (IsSuppressed(e.Go)) continue; // frozen on a bed / unloaded area: nothing to show
                CheckHolder(e);
                Rigidbody rb = e.Rb;
                bool held = e == _localEntry;
                if (!held && e.Attached)
                {
                    // Rides on its holder (joint); MachineSync / the holder carry it. Nothing to stream.
                    e.Streaming = false;
                    e.NeedRest = false;
                    e.SentPos = rb.position;
                    e.SentRot = rb.rotation;
                    continue;
                }

                Vector3 pos = rb.position;
                Quaternion rot = rb.rotation;
                bool moved = held || (pos - e.SentPos).sqrMagnitude > MoveEpsSqr || Quaternion.Angle(rot, e.SentRot) > MoveEpsAngle;
                if (moved) { e.StillTime = 0f; e.Streaming = true; e.NeedRest = true; }
                else e.StillTime += SendInterval;

                if (e.NeedRest && !moved && e.StillTime >= RestTime)
                {
                    if (rests >= MaxRestsPerTick) { if (firstSkipped < 0) firstSkipped = i; continue; } // next tick
                    rests++;
                    SendRest(e, stamp, pos, rot, rb.isKinematic);
                    e.Streaming = false;
                    e.NeedRest = false;
                    e.SentPos = pos;
                    e.SentRot = rot;
                    continue;
                }
                if (!e.Streaming) continue;

                int need = e.Key.Length * 3 + 40;
                if (ms != null && (ms.Length + need > MaxPacketBytes || count == 255))
                {
                    FlushPoses(ms, countPos, count);
                    ms = null;
                    packets++;
                }
                if (ms == null)
                {
                    if (packets >= MaxPosePacketsPerTick) { if (firstSkipped < 0) firstSkipped = i; continue; } // next tick
                    ms = Begin(SubPoses, out w);
                    w.Write(Net.LocalId);
                    w.Write(stamp);
                    w.Flush();
                    countPos = ms.Position;
                    w.Write((byte)0);
                    count = 0;
                }
                byte flags = (byte)((held ? 1 : 0) | (rb.isKinematic ? 2 : 0));
                w.Write(e.Key);
                w.Write(e.Epoch);
                w.Write(flags);
                WriteV3(w, pos);
                w.Write(PackQ(rot));
                WriteHalf3(w, rb.isKinematic ? Vector3.zero : rb.velocity);
                WriteHalf3(w, rb.isKinematic ? Vector3.zero : rb.angularVelocity);
                w.Flush();
                count++;
                e.SentPos = pos;
                e.SentRot = rot;
            }
            if (ms != null) FlushPoses(ms, countPos, count);
            // Start next tick with the first object that did not fit.
            if (firstSkipped >= fixedHead && span > 0) _sendRot = (rot0 + firstSkipped - fixedHead) % span;
            foreach (Entry e in _tmp) Owned.Remove(e);
            _sendList.Clear();
        }

        private static void FlushPoses(MemoryStream ms, long countPos, int count)
        {
            if (count == 0) return;
            ms.GetBuffer()[countPos] = (byte)count;
            Bcast(ms.ToArray(), false);
        }

        private static void SendRest(Entry e, double stamp, Vector3 pos, Quaternion rot, bool kinematic)
        {
            var ms = Begin(SubRest, out var w);
            w.Write(Net.LocalId);
            w.Write(e.Key);
            w.Write(e.Epoch);
            w.Write(stamp);
            WriteV3(w, pos);
            WriteQ(w, rot);
            w.Write(kinematic);
            Bcast(ms.ToArray(), true);
        }

        /// <summary>
        /// 10 Hz: claims objects the local player is about to push: near the driven machine, the held
        /// object, or the player's body while walking. Cheap: up to two OverlapBox queries on pickable layers.
        /// </summary>
        private static void ProximityClaims()
        {
            if (!Singleton<Player>.IsInstanced()) return;
            if (_mask == -1) _mask = LayerMask.GetMask("Pickable", "PickedObject");
            Player p = Singleton<Player>.Instance;
            if (!ReferenceEquals(p, _player))
            {
                _player = p;
                _playerRb = p.GetComponent<Rigidbody>();
                _playerCols = p.GetComponents<CapsuleCollider>();
            }

            MachineController m = p.GetControlledMachine();
            if (m != null && !MachineSync.LocallyDriven(m)) m = null; // passenger: the driver's PC owns what the machine pushes
            if (!ReferenceEquals(m, _machine))
            {
                _machine = m;
                _machineCols = m != null ? m.GetComponentsInChildren<Collider>() : null;
            }

            RefreshRemoteTowed();
            if (m != null) ClaimAround(_machineCols, 0.3f, null);
            if (m != null) ClaimTowed(m);
            else if (_playerRb != null && _playerRb.velocity.sqrMagnitude > 0.5f * 0.5f) ClaimAround(_playerCols, 0.25f, null);

            if (_localEntry != null && _localHeld != null) ClaimAround(_heldCols, 0.15f, _localEntry);
        }

        private static void ClaimAround(Collider[] cols, float margin, Entry self)
        {
            if (cols == null) return;
            bool any = false;
            Bounds b = default;
            foreach (Collider c in cols)
            {
                if (c == null || c.isTrigger || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                if (!any) { b = c.bounds; any = true; }
                else b.Encapsulate(c.bounds);
            }
            if (!any) return;
            int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * margin, Hits, Quaternion.identity, _mask, QueryTriggerInteraction.Ignore);
            float now = Time.unscaledTime;
            for (int i = 0; i < n; i++)
            {
                Rigidbody rb = Hits[i].attachedRigidbody;
                Hits[i] = null;
                if (rb == null || !ByInstance.TryGetValue(rb.gameObject.GetInstanceID(), out Entry e)) continue;
                if (e == self || e.Owner == Net.LocalId || e.HeldBy != 0 || e.Attached || IsSuppressed(e.Go) || _remoteTowed.Contains(e) ||
                    (e.Owner != 0 && MachineSync.DrivesNear(e.Owner, rb.position, RemoteDriveRadius))) continue;
                if (e.Owner != 0 && now - e.RemoteClaimAt < ReclaimCooldown) continue; // avoid ping-pong
                if (_claimBudget <= 0) { for (int j = i + 1; j < n; j++) Hits[j] = null; return; } // rest next tick
                _claimBudget--;
                LocalClaim(e, grab: false);
            }
        }

        /// <summary>
        /// Round-robin over all objects (64 per tick): claims unowned objects that start moving locally
        /// (hit by something), and keeps non-owned objects kinematic if a game script reset them.
        /// </summary>
        private static void ScanUnowned()
        {
            int budget = Mathf.Min(64, All.Count);
            // Destroy sync only while this PC's world is intact (no scene change / unload under way).
            bool worldIntact = Singleton<Player>.IsInstanced() && !GameState.IsLoading &&
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle == _indexedSceneHandle;
            int destroyed = 0;
            for (int i = 0; i < budget && All.Count > 0; i++)
            {
                if (_scanCursor >= All.Count) _scanCursor = 0;
                Entry e = All[_scanCursor];
                if (e.Go == null)
                {
                    // Destroyed here by gameplay (consumed, merged, despawned...): destroy it everywhere.
                    // Not when its scene went away (streamed area unloaded) or many vanish at once.
                    if (e.Quiet || e.Part) { } // announced by its own message (sale, return, reset) / by its rope
                    else if (worldIntact && e.Scene.IsValid() && e.Scene.isLoaded && ++destroyed <= 16 &&
                        Time.unscaledTime >= _destroyMuteUntil)
                        SendDestroy(e.Key, e.Sid, e.LastPos);
                    else if (destroyed == 17)
                    {
                        _destroyMuteUntil = Time.unscaledTime + 5f;
                        Plugin.Log.LogWarning("Object sync: many objects vanished at once, not syncing those destroys");
                    }
                    Suppressed.Remove(e.InstanceId); // instance ids are never reused: no stale entries pile up
                    Forget(e); // Forget removes it from All
                    continue;
                }
                _scanCursor++;
                e.LastPos = e.Go.transform.position;
                e.Scene = e.Go.scene;
                Rigidbody rb = e.Rb;
                if (rb == null || e == _localEntry || IsSuppressed(e.Go)) continue;
                if (e.Owner != Net.LocalId && e.Hold != null)
                {
                    CheckHolder(e); // a local game script attached / detached it
                    if (e.Owner == Net.LocalId) continue;
                }
                // Unowned, or owned by someone else but handed back to local physics after its rest
                // pose: if it starts moving here (hit by something), this PC takes over and streams it.
                if (e.Owner == 0 || (e.Owner != Net.LocalId && !e.Following && e.HeldBy == 0))
                {
                    if (!rb.isKinematic && !rb.IsSleeping() && rb.velocity.sqrMagnitude > 0.3f * 0.3f && !e.Attached &&
                        _claimBudget > 0 && !_remoteTowed.Contains(e) &&
                        !(e.Owner != 0 && MachineSync.DrivesNear(e.Owner, rb.position, RemoteDriveRadius)))
                    {
                        _claimBudget--;
                        LocalClaim(e, grab: false);
                    }
                }
                else if (e.Owner != Net.LocalId && e.Following && !rb.isKinematic && !e.Attached)
                {
                    rb.isKinematic = true;
                }
            }
        }

        /// <summary>Drops whatever the local player holds if it is <paramref name="go"/> (lost ownership).</summary>
        private static void ForceLocalDrop(GameObject go)
        {
            if (go == null) return;
            bool dropped = false;
            if (Singleton<GravityGun>.IsInstanced() && Singleton<GravityGun>.Instance.PickedObject == go)
            {
                Singleton<GravityGun>.Instance.DropObject(setToKinematic: false);
                dropped = true;
            }
            if (Singleton<ToolGun>.IsInstanced() && Singleton<ToolGun>.Instance.PickedObject == go)
            {
                Singleton<ToolGun>.Instance.DropObject(setToKinematic: false);
                dropped = true;
            }
            // Forget it without sending a Drop (the winner owns it now / it is gone). Only if it was the
            // tracked held object: anything else picked meanwhile is still announced by TrackLocalHold.
            if (dropped || ReferenceEquals(_localHeld, go))
            {
                _localHeld = null;
                _localEntry = null;
                _heldCols = null;
            }
        }

        private static void ExpireRemoteHolds()
        {
            if (Held.Count == 0) return;
            float now = Time.unscaledTime;
            _tmp.Clear();
            foreach (Entry e in Held)
                if (e.Go == null || e.HeldBy == 0 || now - e.HeldUpdate > RemoteHoldTimeout) _tmp.Add(e);
            foreach (Entry e in _tmp)
            {
                e.HeldBy = 0;
                Held.Remove(e);
            }
        }

        /// <summary>
        /// Followers that reached the owner's rest pose go back to local physics after RestRelease
        /// (game scripts expect dynamic bodies: carried in beds, pushed, filled...). Owner unchanged.
        /// </summary>
        private static void ReleaseRested(float now)
        {
            if (Releasing.Count == 0) return;
            _tmp.Clear();
            foreach (Entry e in Releasing)
                if (now >= e.ReleaseAt) _tmp.Add(e);
            foreach (Entry e in _tmp)
            {
                Releasing.Remove(e);
                if (e.Go == null || e.Rb == null || !e.Following || e.Owner == Net.LocalId) continue;
                if (Active.Contains(e) || e.HeldBy != 0 || e.Attached || Frozen(e)) continue;
                StopFollow(e, restorePhysics: true);
                if (!e.Rb.isKinematic)
                {
                    e.Rb.velocity = Vector3.zero;
                    e.Rb.angularVelocity = Vector3.zero;
                    e.Rb.Sleep();
                }
            }
        }

        // ---------------------------------------------------------------- ropes (Hook_Rope)
        // A rope (GoldDigger.Hook_Rope) is one saved object whose moving pieces are CHILD holdables with
        // bodies of their own: the two hooks (ShovelRopeHoldable, under PhysicObject) and the coiled
        // rope (FixedHoldable, the FixedObject). The game picks / attaches those children, never the
        // root, so each is a "part" entry keyed "<rope key>/<n>" (n = order among the root's child
        // holdables with a body, the same on every PC). Parts use the normal grab / pose / rest / attach
        // sync: a hook attached to an excavator shovel (ShovelRopeHolder on the machine), a washplant or
        // a boulder replicates with Holder.AttachObject like any holder.
        // The rope's shape switch (coiled FixedObject <-> laid-out PhysicObject, Hook_Rope.OnHookPicked
        // / OnAttachFixed) is sent as Rope (reliable): sender, part-0 key, fixed active, physic active,
        // FixedObject pose, n x hook pose. Receivers apply it without the game's local reactions (those
        // drop / pick the LOCAL player's gravity gun).
        // A driver whose machine holds a hook takes ownership of the rope's parts and of the object at
        // the other end (a boulder): its PC simulates the pull, so it streams the load.

        private sealed class RopeInfo
        {
            public GameObject Root;
            // Shape objects: coiled (Fixed), laid out (Physic) and, for a fuel hose, the dock holder.
            public GameObject Fixed, Physic, Extra;
            public readonly List<Entry> Parts = new List<Entry>();
            public bool LastFixed, LastPhysic, LastExtra;
        }

        private static readonly Dictionary<int, RopeInfo> Ropes = new Dictionary<int, RopeInfo>();
        private static readonly List<int> _deadRopes = new List<int>();

        /// <summary>
        /// A saved root with a rope whose pieces are child holdables: a steel rope (Hook_Rope) or a fuel
        /// hose with its pistol (Fuel_Rope, on fuel stations / tanks / fuel trailers). Machines and trailers
        /// count too: only the parts are keyed, MachineSync keeps the body.
        /// </summary>
        private static bool IsRopeRoot(GameObject go) =>
            go.GetComponentInChildren<Hook_Rope>(true) != null || go.GetComponentInChildren<Fuel_Rope>(true) != null;

        private static bool IsPart(GameObject go) => go != null && ByInstance.TryGetValue(go.GetInstanceID(), out Entry e) && e.Part;

        private static bool On(GameObject g) => g != null && g.activeSelf;

        /// <summary>Keys the rope's child holdables "<paramref name="baseKey"/>/n" (same order on every PC).</summary>
        private static void RegisterRopeParts(GameObject root, string baseKey, bool saveKeys)
        {
            var info = new RopeInfo { Root = root };
            Hook_Rope hr = root.GetComponentInChildren<Hook_Rope>(true);
            Fuel_Rope fr = hr == null ? root.GetComponentInChildren<Fuel_Rope>(true) : null;
            if (hr != null) { info.Fixed = hr.FixedObject; info.Physic = hr.PhysicObject; }
            else if (fr != null)
            {
                info.Fixed = fr.FixedObject;
                info.Physic = fr.PhysicObject;
                info.Extra = fr.PistolHolder != null ? fr.PistolHolder.gameObject : null;
            }
            else return;
            SaveSystem.SaveMe rootSave = root.GetComponent<SaveSystem.SaveMe>();
            int n = 0;
            foreach (Holdable h in root.GetComponentsInChildren<Holdable>(true))
            {
                if (h == null || h.gameObject == root || h.GetComponent<Rigidbody>() == null) continue;
                // A nested saved object is indexed on its own (and must keep the save's numbering).
                SaveSystem.SaveMe own = h.GetComponentInParent<SaveSystem.SaveMe>();
                if (own != null && own != rootSave) continue;
                string key = baseKey + "/" + n;
                RegisterCore(h.gameObject, key);
                if (ByInstance.TryGetValue(h.gameObject.GetInstanceID(), out Entry e))
                {
                    e.Part = true;
                    e.PartIndex = n;
                    e.Rope = info;
                    if (saveKeys) e.SaveKey = key;
                    info.Parts.Add(e);
                }
                n++;
            }
            info.LastFixed = On(info.Fixed);
            info.LastPhysic = On(info.Physic);
            info.LastExtra = On(info.Extra);
            Ropes[root.GetInstanceID()] = info;
        }

        // ---------------------------------------------------------------- scene ropes (fuel stations...)
        // A rope / fuel hose placed in a scene (not a saved object: e.g. a fuel station of the map) has no
        // save ordinal. Its parts are keyed by the scene and the hierarchy path of the rope's root
        // ("scn:<scene>:<name[sibling]/...>"), the same on every PC that has that scene loaded. Streamed
        // areas load their scenes at different times on each PC: re-scanned when a scene loads.
        // Messages about a part whose area is not loaded here find no key and are dropped, as usual.

        private static bool _sceneRopesDirty = true;
        private static bool _sceneHooked;
        private static float _sceneRopesAt;
        private static readonly List<Transform> _scenePath = new List<Transform>();

        private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m) =>
            _sceneRopesDirty = true;

        /// <summary>Keys the parts of ropes placed in loaded scenes (not saved objects) that have no key yet.</summary>
        private static void IndexSceneRopes(float now)
        {
            if (!_sceneHooked)
            {
                _sceneHooked = true;
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            }
            if (!_sceneRopesDirty || now < _sceneRopesAt) return;
            _sceneRopesDirty = false;
            _sceneRopesAt = now + 1f; // a burst of area loads is one scan
            int n = 0;
            foreach (Fuel_Rope fr in UnityEngine.Object.FindObjectsOfType<Fuel_Rope>(true))
                if (fr != null && IndexSceneRope(fr.transform, fr.Pistol != null ? fr.Pistol.transform : null,
                        fr.FixedObject != null ? fr.FixedObject.transform : null)) n++;
            foreach (Hook_Rope hr in UnityEngine.Object.FindObjectsOfType<Hook_Rope>(true))
                if (hr != null && IndexSceneRope(hr.transform, hr.FixedObject != null ? hr.FixedObject.transform : null,
                        hr.PhysicObject != null ? hr.PhysicObject.transform : null)) n++;
            if (n > 0) Plugin.Log.LogInfo($"Object sync: {n} scene ropes / fuel hoses keyed");
        }

        private static bool IndexSceneRope(Transform rope, Transform a, Transform b)
        {
            if (!rope.gameObject.scene.IsValid()) return false; // a prefab asset
            // Saved roots (ropes, tanks, machines, trailers) are keyed by the save index.
            for (Transform t = rope; t != null; t = t.parent)
                if (t.GetComponent<SaveSystem.SaveMe>() != null) return false;
            // Root: lowest ancestor holding the rope and its pieces.
            Transform root = rope;
            while (root.parent != null && ((a != null && !a.IsChildOf(root)) || (b != null && !b.IsChildOf(root)))) root = root.parent;
            if (Ropes.ContainsKey(root.gameObject.GetInstanceID())) return false;
            _scenePath.Clear();
            for (Transform t = root; t != null; t = t.parent) _scenePath.Add(t);
            var sb = new System.Text.StringBuilder("scn:").Append(root.gameObject.scene.name).Append(':');
            for (int i = _scenePath.Count - 1; i >= 0; i--)
            {
                sb.Append(_scenePath[i].name).Append('[').Append(_scenePath[i].GetSiblingIndex()).Append(']');
                if (i > 0) sb.Append('/');
            }
            _scenePath.Clear();
            RegisterRopeParts(root.gameObject, sb.ToString(), saveKeys: false);
            return true;
        }

        /// <summary>Every tick: a local switch between coiled and laid-out rope is sent before the grab / attach that follows it.</summary>
        private static void PollRopes()
        {
            if (Ropes.Count == 0) return;
            _deadRopes.Clear();
            foreach (var kv in Ropes)
            {
                RopeInfo ri = kv.Value;
                if (ri.Root == null) { _deadRopes.Add(kv.Key); continue; }
                bool f = On(ri.Fixed), p = On(ri.Physic), x = On(ri.Extra);
                if (f == ri.LastFixed && p == ri.LastPhysic && x == ri.LastExtra) continue;
                ri.LastFixed = f;
                ri.LastPhysic = p;
                ri.LastExtra = x;
                if (ri.Root.activeInHierarchy) SendRope(ri);
            }
            foreach (int id in _deadRopes) Ropes.Remove(id);
        }

        private static void SendRope(RopeInfo ri)
        {
            if (ri.Parts.Count == 0 || ri.Parts[0].Key == null) return;
            var ms = Begin(SubRope, out var w);
            w.Write(Net.LocalId);
            w.Write(ri.Parts[0].Key);
            w.Write(On(ri.Fixed));
            w.Write(On(ri.Physic));
            w.Write(On(ri.Extra));
            Transform fo = ri.Fixed != null ? ri.Fixed.transform : ri.Root.transform;
            WriteV3(w, fo.position);
            WriteQ(w, fo.rotation);
            int n = Mathf.Min(ri.Parts.Count, 16);
            w.Write((byte)n);
            for (int i = 0; i < n; i++)
            {
                Transform t = ri.Parts[i].Go != null ? ri.Parts[i].Go.transform : fo;
                WriteV3(w, t.position);
                WriteQ(w, t.rotation);
            }
            Bcast(ms.ToArray(), true);
        }

        private static void ReceiveRope(BinaryReader r)
        {
            ulong sender = r.ReadUInt64();
            string key = r.ReadString();
            bool fixedOn = r.ReadBoolean(), physicOn = r.ReadBoolean(), extraOn = r.ReadBoolean();
            Vector3 fpos = ReadV3(r);
            Quaternion frot = ReadQ(r);
            int n = r.ReadByte();
            var pos = new Vector3[n];
            var rot = new Quaternion[n];
            for (int i = 0; i < n; i++) { pos[i] = ReadV3(r); rot[i] = ReadQ(r); }
            Entry e = Lookup(key);
            if (sender == Net.LocalId || e == null || e.Rope == null || e.Rope.Root == null) return;
            RopeInfo ri = e.Rope;
            ri.LastFixed = fixedOn;
            ri.LastPhysic = physicOn;
            ri.LastExtra = extraOn;
            if (On(ri.Fixed) == fixedOn && On(ri.Physic) == physicOn && On(ri.Extra) == extraOn) return;

            // Parts that are switched off here: out of the local hand, back to their own physics.
            foreach (Entry p in ri.Parts)
            {
                if (p.Go == null) continue;
                Transform t = p.Go.transform;
                bool off = (ri.Fixed != null && !fixedOn && t.IsChildOf(ri.Fixed.transform)) ||
                           (ri.Physic != null && !physicOn && t.IsChildOf(ri.Physic.transform)) ||
                           (ri.Extra != null && !extraOn && t.IsChildOf(ri.Extra.transform));
                if (off)
                {
                    ForceLocalDrop(p.Go);
                    StopFollow(p, restorePhysics: true);
                }
            }
            if (fixedOn && ri.Fixed != null && IsFinite(fpos)) ri.Fixed.transform.SetPositionAndRotation(fpos, frot);
            if (ri.Fixed != null) ri.Fixed.SetActive(fixedOn);
            if (ri.Extra != null) ri.Extra.SetActive(extraOn);
            if (ri.Physic != null) ri.Physic.SetActive(physicOn);
            // Laid out: every piece where the owner had it (as Hook_Rope / Fuel_Rope place them).
            for (int i = 0; i < n && i < ri.Parts.Count; i++)
            {
                Entry p = ri.Parts[i];
                if (p.Go == null || !p.Go.activeInHierarchy || !IsFinite(pos[i]) || p.Attached) continue;
                p.Go.transform.SetPositionAndRotation(pos[i], rot[i]);
                if (p.Rb != null && !p.Rb.isKinematic) { p.Rb.velocity = Vector3.zero; p.Rb.angularVelocity = Vector3.zero; }
            }
            foreach (Entry p in ri.Parts)
                if (p.Go != null && p.Rb != null) { p.SentPos = p.Rb.position; p.SentRot = p.Rb.rotation; p.SentHolder = CurrentHolder(p); }
        }

        /// <summary>
        /// Driver of <paramref name="m"/>: claims every rope one of whose hooks hangs on this machine, the
        /// object at the rope's other end (boulder...) and what the excavator's own pick rope holds.
        /// Its PC simulates the pull, so it streams the load; the others follow.
        /// </summary>
        private static void ClaimTowed(MachineController m)
        {
            float now = Time.unscaledTime;
            foreach (RopeInfo ri in Ropes.Values)
            {
                if (ri.Root == null || !ri.Root.activeInHierarchy) continue;
                bool tied = false;
                foreach (Entry p in ri.Parts)
                {
                    Holder h = p.Go != null && p.Hold != null ? p.Hold.MyHolder : null;
                    if (h != null && HolderMachine(h) == m) { tied = true; break; }
                }
                if (!tied) continue;
                foreach (Entry p in ri.Parts)
                {
                    ClaimTied(p, now);
                    Holder h = p.Go != null && p.Hold != null ? p.Hold.MyHolder : null;
                    if (h == null || OnVehicle(h)) continue; // machines / trailers: MachineSync
                    for (Transform x = h.transform; x != null; x = x.parent)
                    {
                        if (!ByInstance.TryGetValue(x.gameObject.GetInstanceID(), out Entry o)) continue;
                        if (o.Rope != ri) ClaimTied(o, now);
                        break;
                    }
                }
            }
            if (m is Koparka k && k.PickBody != null)
            {
                // RopeController / PickBodyController: the excavator's pick rope holds a body by a joint.
                if (k.PickBody.PickedObject != null) ClaimTied(EntryOf(k.PickBody.PickedObject.gameObject), now);
                if (k.PickBody._pickedRubber != null) ClaimTied(EntryOf(k.PickBody._pickedRubber.gameObject), now);
            }
        }

        // Rope parts and loads tied to a machine another player drives: that driver's PC simulates and
        // streams them (ClaimTowed there). Never claimed here by walking into them or by local motion,
        // which would only ping-pong ownership with the driver every 0.75 s.
        private static readonly HashSet<Entry> _remoteTowed = new HashSet<Entry>();

        private static void RefreshRemoteTowed()
        {
            _remoteTowed.Clear();
            // What the pick rope of an excavator another player drives holds (that driver claims it in ClaimTowed).
            foreach (var kv in MachineSync.AllIds)
            {
                if (!(kv.Key is Koparka k) || k == null || k.PickBody == null || !MachineSync.IsRemoteDriven(kv.Value)) continue;
                AddRemoteTowed(k.PickBody.PickedObject != null ? k.PickBody.PickedObject.gameObject : null);
                AddRemoteTowed(k.PickBody._pickedRubber != null ? k.PickBody._pickedRubber.gameObject : null);
            }
            foreach (RopeInfo ri in Ropes.Values)
            {
                if (ri.Root == null || !ri.Root.activeInHierarchy) continue;
                bool remote = false;
                foreach (Entry p in ri.Parts)
                {
                    Holder h = p.Go != null && p.Hold != null ? p.Hold.MyHolder : null;
                    if (h != null && MachineSync.IsRemoteDriven(HolderMachine(h))) { remote = true; break; }
                }
                if (!remote) continue;
                foreach (Entry p in ri.Parts)
                {
                    _remoteTowed.Add(p);
                    Holder h = p.Go != null && p.Hold != null ? p.Hold.MyHolder : null;
                    if (h == null || OnVehicle(h)) continue;
                    for (Transform x = h.transform; x != null; x = x.parent)
                    {
                        if (!ByInstance.TryGetValue(x.gameObject.GetInstanceID(), out Entry o)) continue;
                        if (o.Rope != ri) _remoteTowed.Add(o);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// The machine whose driver simulates what hangs on <paramref name="h"/>: the machine the holder is
        /// on, or, for a holder on a trailer, the machine towing that trailer (null if none / not towed).
        /// </summary>
        private static MachineController HolderMachine(Holder h)
        {
            if (h == null) return null;
            MachineController m = h.GetComponentInParent<MachineController>();
            return m != null ? m : MachineSync.TowingMachine(h.GetComponentInParent<Trailer>());
        }

        /// <summary>The holder sits on a machine or a trailer (MachineSync moves those bodies).</summary>
        private static bool OnVehicle(Holder h) =>
            h.GetComponentInParent<MachineController>() != null || h.GetComponentInParent<Trailer>() != null;

        // A remote player's PC simulates what its machine pushes or drags near it: no proximity / motion claim
        // within this radius of the machine that player drives (ping-pong with the driver otherwise).
        private const float RemoteDriveRadius = 15f;

        private static void AddRemoteTowed(GameObject go)
        {
            Entry e = go != null ? EntryOf(go) : null;
            if (e != null) _remoteTowed.Add(e);
        }

        private static void ClaimTied(Entry e, float now)
        {
            if (e == null || e.Go == null || e.Owner == Net.LocalId || e.HeldBy != 0 || !e.Go.activeInHierarchy || Frozen(e)) return;
            if (e.Owner != 0 && now - e.RemoteClaimAt < ReclaimCooldown) return;
            if (_claimBudget <= 0) return;
            _claimBudget--;
            LocalClaim(e, grab: false);
        }

        /// <summary>
        /// Hook_Rope reacts to a hook being detached / the coil being attached by dropping and picking
        /// objects with the LOCAL gravity gun and switching the rope's shape. During a network apply that
        /// must not happen here: the owner's PC did it and sends the shape (Rope) and the grab.
        /// </summary>
        [HarmonyPatch]
        private static class PatchHookRopeReactions
        {
            private static readonly MethodBase[] Targets =
            {
                AccessTools.Method(typeof(Hook_Rope), "OnHookPicked"),
                AccessTools.Method(typeof(Hook_Rope), "OnAttachFixed"),
                AccessTools.Method(typeof(Fuel_Rope), nameof(Fuel_Rope.PistolAttach)),
            };
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (MethodBase m in Targets) if (m != null) yield return m;
            }
            private static bool Prepare()
            {
                int found = 0;
                foreach (MethodBase m in Targets) if (m != null) found++;
                if (found < Targets.Length)
                    Plugin.Log.LogWarning("Object sync: Hook_Rope / Fuel_Rope reactions not all found: remote rope changes may move the local gravity gun");
                return found > 0;
            }
            private static bool Prefix() => !_applyingRemote;
        }

        /// <summary>
        /// A fuel pistol attached by another player (to a vehicle, tank or jerrycan holder) must not open
        /// the fuel purchase window here: the player who attached it buys (FuelSync sends the purchase).
        /// </summary>
        [HarmonyPatch]
        private static class PatchFuelGuiOnRemoteAttach
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                MethodBase a = AccessTools.Method(typeof(ShopGUI), nameof(ShopGUI.Show), Type.EmptyTypes);
                MethodBase b = AccessTools.Method(typeof(FuelStationGUI), nameof(FuelStationGUI.Show), Type.EmptyTypes);
                if (a != null) yield return a;
                if (b != null && b != a) yield return b;
            }
            private static bool Prepare() => AccessTools.Method(typeof(ShopGUI), nameof(ShopGUI.Show), Type.EmptyTypes) != null;
            private static bool Prefix() => !_applyingAttach;
        }

        /// <summary>The rope-length check of a hook attach is a local condition: the other PC allowed it.</summary>
        [HarmonyPatch(typeof(ShovelRopeHolder), nameof(ShovelRopeHolder.CanAttach))]
        private static class PatchRopeCanAttach
        {
            private static bool Prefix(ref bool __result)
            {
                if (!_applyingAttach) return true;
                __result = true;
                return false;
            }
        }

        // ---------------------------------------------------------------- players leaving

        /// <summary>Host: a transport peer left. Takes over every object owned by players behind it.</summary>
        internal static void OnPeerLeft(ulong via)
        {
            if (!Net.Active || !Net.IsHost) return;
            _tmpIds.Clear();
            foreach (var kv in OwnerVia)
                if (kv.Value == via) _tmpIds.Add(kv.Key);
            ulong helloId = Net.LocalIdOf(via); // Net.OnPeerLeft calls us before it forgets the peer
            if (helloId != 0 && helloId != Net.LocalId && !_tmpIds.Contains(helloId)) _tmpIds.Add(helloId);
            foreach (ulong id in _tmpIds)
            {
                OwnerVia.Remove(id);
                ReleaseOwner(id);
            }
        }

        /// <summary>Host, every tick: detects departed peers even if Net does not call OnPeerLeft.</summary>
        private static void CheckDeparted()
        {
            if (!Net.IsHost) return;
            int pc = Net.Peers.Count;
            if (pc == _peerCount) return;
            bool dropped = pc < _peerCount;
            _peerCount = pc;
            if (!dropped) return;

            _tmpIds.Clear();
            foreach (var kv in OwnerVia)
                if (!Net.Peers.Contains(kv.Value)) _tmpIds.Add(kv.Key);
            if (pc == 0)
                foreach (Entry e in All) // nobody left: every remote owner is gone
                    if (e.Owner != 0 && e.Owner != Net.LocalId && !_tmpIds.Contains(e.Owner)) _tmpIds.Add(e.Owner);
            foreach (ulong id in _tmpIds)
            {
                OwnerVia.Remove(id);
                ReleaseOwner(id);
            }
        }

        /// <summary>
        /// Host: <paramref name="who"/> left. The host claims all its objects (one deterministic PC):
        /// remote holds are cleared, they become dynamic here, and the host streams them / sends their
        /// rest pose, so every remaining PC agrees again.
        /// </summary>
        private static void ReleaseOwner(ulong who)
        {
            int n = 0;
            foreach (Entry e in All)
            {
                if (e.HeldBy == who) { e.HeldBy = 0; Held.Remove(e); }
                if (e.Owner != who || e.Go == null) continue;
                if (e.Rb == null || Frozen(e)) { e.Owner = 0; continue; } // bed cargo: BedSync
                LocalClaim(e, grab: false);
                n++;
            }
            Clocks.Remove(who);
            Plugin.Log.LogInfo($"Object sync: player {who:x} left, host took over {n} objects");
        }

        // ---------------------------------------------------------------- late-join pose sync

        private static void RequestSync(float now)
        {
            if (!_syncWanted || Net.IsHost || now < _syncNext) return;
            // The host pushes the dump right after its catch-up marker (SendCatchUp): ask only if it
            // does not come. Before the marker a request would be dropped or answered twice.
            if (!SaveTransfer.CaughtUp) { _syncNext = now + SyncRetry; return; }
            // Keep asking: until the host's dump arrives this client's keys are only save ordinals.
            if (++_syncTries % SyncMaxTries == 0)
                Plugin.Log.LogWarning($"Object sync: host has not answered the key sync request ({_syncTries} tries)");
            _syncNext = now + SyncRetry * Mathf.Min(_syncTries, 4); // 4, 8, 12, 16, 16... s: a big dump takes a while
            var ms = Begin(SubSyncReq, out var w);
            w.Write(Net.LocalId);
            Bcast(ms.ToArray(), true);
        }

        /// <summary>
        /// Host: the current state of every object, for a client that just loaded: key, epoch, owner,
        /// pose, holder, prefab id + stack size, the object's ordinal in the save that client loaded
        /// (so it renames its save-ordinal keys to the host's keys) and, for objects that are not in that
        /// save, their component data (so the client can create them). Frozen bed cargo has no pose.
        /// Packets carry first / last / has-ordinals flags; after the last one the client removes
        /// objects the host no longer has.
        /// </summary>
        /// <summary>
        /// Host, on a client's Loaded (SaveTransfer, after its catch-up marker): sends that client the
        /// key-sync dump at once instead of waiting for its request.
        /// </summary>
        internal static void SendCatchUp(ulong peer)
        {
            if (!Net.Active || !Net.IsHost || !_indexed) return;
            ulong requester = Net.LocalIdOf(peer);
            if (requester == 0 || requester == Net.LocalId) return; // unknown: its own request covers it
            OwnerVia[requester] = peer;
            SendSyncDump(requester);
        }

        private static void SendSyncDump(ulong requester)
        {
            bool direct = OwnerVia.TryGetValue(requester, out ulong via) && Net.Peers.Contains(via);
            Snapshot snap = SnapshotFor(requester);
            MemoryStream ms = null, pending = null;
            BinaryWriter w = null;
            long countPos = 0, pendingFlagsPos = 0, flagsPos = 0;
            int count = 0, total = 0;
            bool first = true;

            void Flush(bool last)
            {
                if (pending == null) return;
                byte[] buf = pending.GetBuffer();
                buf[pendingFlagsPos] = (byte)((first ? 1 : 0) | (last ? 2 : 0) | (snap != null ? 4 : 0));
                byte[] data = pending.ToArray();
                if (direct && Net.Transport != null) { Net.Transport.Send(via, data, true); CountSent(data, true); }
                else Bcast(data, true); // other clients ignore it (target id)
                first = false;
                pending = null;
            }

            void Open()
            {
                ms = Begin(SubSync, out w);
                w.Write(Net.LocalId);
                w.Write(requester);
                w.Flush();
                flagsPos = ms.Position;
                w.Write((byte)0);
                countPos = ms.Position;
                w.Write((byte)0);
                count = 0;
            }

            void Close()
            {
                if (ms == null) return;
                ms.GetBuffer()[countPos] = (byte)count;
                Flush(last: false); // the previous packet is not the last one
                pending = ms;
                pendingFlagsPos = flagsPos;
                ms = null;
            }

            foreach (Entry e in All)
            {
                if (e.Go == null || e.Rb == null) continue;
                bool frozen = Frozen(e);
                Holder h = frozen ? null : CurrentHolder(e);
                string ord = null;
                if (snap != null) snap.Ordinal.TryGetValue(e, out ord);
                byte[] blob = !frozen && !e.Part && snap != null && ord == null ? SerializeState(e.Go) : null;
                int need = e.Key.Length * 3 + e.Sid.Length * 3 + (ord?.Length ?? 0) * 3 + 80 +
                           (h != null ? 512 : 0) + (blob?.Length ?? 0);
                if (ms != null && (ms.Length + need > MaxPacketBytes || count == 255)) Close();
                if (ms == null) Open();
                w.Write(e.Key);
                w.Write(e.Epoch);
                w.Write(e.Owner);
                byte flags = (byte)((e.Rb.isKinematic ? 1 : 0) | (h != null ? 2 : 0) | (frozen ? 4 : 0) |
                                    (ord != null ? 8 : 0) | (blob != null ? 16 : 0));
                w.Write(flags);
                if (ord != null) w.Write(ord);
                if (!frozen)
                {
                    Transform t = e.Go.transform;
                    WriteV3(w, t.position);
                    WriteQ(w, t.rotation);
                    w.Write(e.Sid);
                    BucketStack bs = e.Go.GetComponent<BucketStack>();
                    w.Write((short)(bs != null ? bs.BucketCount : -1));
                    if (h != null) WriteHolderRef(w, h, e.Go);
                    if (blob != null) { w.Write((ushort)blob.Length); w.Write(blob); }
                }
                w.Flush();
                count++;
                total++;
            }
            if (ms == null && pending == null) Open(); // an empty packet still answers (and ends) the request
            Close();
            Flush(last: true);
            // Rope states (coiled / laid out) changed since the save: the joiner applies them after the dump.
            foreach (RopeInfo ri in Ropes.Values) if (ri.Root != null) SendRope(ri);
            SendRepairCatchUp(); // repair holders filled / emptied since the save
            Plugin.Log.LogInfo($"Object sync: sent {total} objects to {Net.NameOf(direct ? via : requester)}" +
                               (snap != null ? $" (save snapshot {snap.Gen})" : " (no save snapshot: keys kept)"));
        }

        // ---------------------------------------------------------------- holders (attach / detach)

        /// <summary>
        /// Detects a local attach / detach (player put the item into a holder, picked it out, a game
        /// script disconnected it) and replicates it. A non-owned item is claimed first.
        /// </summary>
        private static void CheckHolder(Entry e)
        {
            if (e.Hold == null) return;
            Holder cur = CurrentHolder(e);
            Holder sent = e.SentHolder != null ? e.SentHolder : null;
            if (ReferenceEquals(cur, sent)) return;
            if (e.Owner != Net.LocalId)
            {
                // Inside an unloaded area / frozen bed cargo / remotely held: not ours to announce.
                if (Frozen(e) || !e.Go.activeInHierarchy || e.HeldBy != 0) { e.SentHolder = cur; return; }
                LocalClaim(e, grab: false);
            }
            e.SentHolder = cur;
            e.AttachEpoch = -1;
            SendAttach(e, cur);
        }

        private static void SendAttach(Entry e, Holder holder)
        {
            var ms = Begin(SubAttach, out var w);
            w.Write(Net.LocalId);
            w.Write(e.Key);
            w.Write(e.Epoch);
            w.Write(holder != null);
            if (holder != null) WriteHolderRef(w, holder, e.Go);
            Transform t = e.Go.transform; // Holdable.Attach sets the transform; the body syncs next step
            w.Write(e.Rb.isKinematic);
            WriteV3(w, t.position);
            WriteQ(w, t.rotation);
            Bcast(ms.ToArray(), true);
            if (holder == null)
            {
                // Free again: stream its fall and publish the rest pose.
                e.Streaming = true;
                e.NeedRest = true;
                e.StillTime = 0f;
            }
            else
            {
                e.Streaming = false;
                e.NeedRest = false;
            }
            if (LogEvent(EvAttach))
                Plugin.Log.LogInfo(holder != null
                    ? $"Object sync: {e.Key} attached to {holder.name} ({holder.Type})"
                    : $"Object sync: {e.Key} detached");
        }

        private sealed class HolderRef
        {
            public byte Kind;          // 0 = under a loose object (key), 1 = under a machine (MachineSync id), 2 = scene path
            public string Key = "";
            public int Machine = -1;
            public string Scene = "", RootName = "";
            public int RootIndex;
            public string[] Names;
            public short[] Indices;
            public int Type;
            public Vector3 LocalPos;   // holder position in the root's space (fallback match)
        }

        /// <summary>
        /// Identifies a holder on every PC: nearest ancestor that is a synced loose object or a
        /// MachineSync machine (else the scene root), then the child path (name + sibling index),
        /// the holder type and its position relative to that root.
        /// </summary>
        private static void WriteHolderRef(BinaryWriter w, Holder h, GameObject item)
        {
            Transform ht = h.transform, root = null;
            byte kind = 2;
            string key = "";
            int machine = -1;
            for (Transform x = ht; x != null; x = x.parent)
            {
                Entry o = EntryOf(x.gameObject);
                if (o != null && o.Go != item) { kind = 0; key = o.Key; root = x; break; }
                MachineController mc = x.GetComponent<MachineController>();
                if (mc != null)
                {
                    int id = MachineSync.IdOf(mc);
                    if (id >= 0) { kind = 1; machine = id; root = x; break; }
                }
            }
            if (root == null)
            {
                root = ht;
                while (root.parent != null) root = root.parent;
            }
            w.Write(kind);
            if (kind == 0) w.Write(key);
            else if (kind == 1) w.Write(machine);
            else
            {
                w.Write(root.gameObject.scene.name ?? "");
                w.Write(root.name);
                w.Write(root.GetSiblingIndex());
            }
            _path.Clear();
            for (Transform x = ht; x != root && x != null && _path.Count < 255; x = x.parent) _path.Add(x);
            w.Write((byte)_path.Count);
            for (int i = _path.Count - 1; i >= 0; i--)
            {
                w.Write(_path[i].name);
                w.Write((short)_path[i].GetSiblingIndex());
            }
            w.Write((int)h.Type);
            WriteV3(w, root.InverseTransformPoint(ht.position));
        }

        private static HolderRef ReadHolderRef(BinaryReader r)
        {
            var h = new HolderRef { Kind = r.ReadByte() };
            if (h.Kind == 0) h.Key = r.ReadString();
            else if (h.Kind == 1) h.Machine = r.ReadInt32();
            else
            {
                h.Scene = r.ReadString();
                h.RootName = r.ReadString();
                h.RootIndex = r.ReadInt32();
            }
            int n = r.ReadByte();
            h.Names = new string[n];
            h.Indices = new short[n];
            for (int i = 0; i < n; i++)
            {
                h.Names[i] = r.ReadString();
                h.Indices[i] = r.ReadInt16();
            }
            h.Type = r.ReadInt32();
            h.LocalPos = ReadV3(r);
            return h;
        }

        private static Holder ResolveHolder(HolderRef hr)
        {
            if (hr == null) return null;
            Transform root = null;
            if (hr.Kind == 0) root = Find(hr.Key)?.transform;
            else if (hr.Kind == 1)
            {
                MachineController m = MachineSync.Get(hr.Machine);
                if (m != null) root = m.transform;
            }
            else
            {
                UnityEngine.SceneManagement.Scene scene = hr.Scene == "DontDestroyOnLoad" && Plugin.Instance != null
                    ? Plugin.Instance.gameObject.scene
                    : UnityEngine.SceneManagement.SceneManager.GetSceneByName(hr.Scene);
                if (scene.IsValid() && scene.isLoaded)
                {
                    GameObject[] roots = scene.GetRootGameObjects();
                    foreach (GameObject g in roots)
                        if (g.name == hr.RootName && g.transform.GetSiblingIndex() == hr.RootIndex) { root = g.transform; break; }
                    if (root == null)
                        foreach (GameObject g in roots)
                            if (g.name == hr.RootName) { root = g.transform; break; }
                }
            }
            if (root == null) return null;

            Transform cur = root;
            for (int i = 0; i < hr.Names.Length && cur != null; i++)
            {
                Transform next = null;
                int idx = hr.Indices[i];
                if (idx >= 0 && idx < cur.childCount && cur.GetChild(idx).name == hr.Names[i]) next = cur.GetChild(idx);
                else
                    for (int c = 0; c < cur.childCount; c++)
                        if (cur.GetChild(c).name == hr.Names[i]) { next = cur.GetChild(c); break; }
                cur = next;
            }
            if (cur != null)
            {
                Holder exact = cur.GetComponent<Holder>();
                if (exact != null && ((int)exact.Type == hr.Type || exact is FuelPistolHolder)) return exact;
            }

            // Fallback: the holder of that type closest to the recorded spot on the same root.
            Holder best = null;
            float bestSq = 1f; // within 1 m
            foreach (Holder c in root.GetComponentsInChildren<Holder>(true))
            {
                if ((int)c.Type != hr.Type && !(c is FuelPistolHolder)) continue;
                float sq = (root.InverseTransformPoint(c.transform.position) - hr.LocalPos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = c; }
            }
            return best;
        }

        private static readonly AccessTools.FieldRef<BlockHolder, bool> CanOperateRef = TryFieldRef();
        private static readonly MethodInfo UpdateHolderTypeM = AccessTools.Method(typeof(FuelPistolHolder), "UpdateHolderType");

        private static AccessTools.FieldRef<BlockHolder, bool> TryFieldRef()
        {
            try { return AccessTools.FieldRefAccess<BlockHolder, bool>("_canOperate"); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Attaches the item to <paramref name="h"/> with the game's own Holder.AttachObject (so the
        /// machine logic sees it: washplant buckets, sluice crates, hoses...) and its locked joint.
        /// GravityGun.DropObject is blocked meanwhile so the local player's own held object stays.
        /// </summary>
        private static void ApplyAttach(Entry e, Holder h, bool kinematic, Vector3 pos, Quaternion rot, ushort epoch)
        {
            // A rope hook hangs on a joint: kinematic (as the owner may report it in the middle of
            // ShovelRopeHoldable's attach coroutine) would pin it.
            if (e.Hold is ShovelRopeHoldable) kinematic = false;
            // A FuelPistolHolder switches its Type (pistol / jerrycan) with what the LOCAL player picked
            // last; Holdable.Attach refuses a type mismatch. Switch it to the item's type the game's way.
            if (h is FuelPistolHolder fh && e.Hold != null && fh.Type != e.Hold.Type && UpdateHolderTypeM != null)
            {
                try { UpdateHolderTypeM.Invoke(fh, new object[] { e.Hold.Type }); }
                catch (Exception ex) { Plugin.Log.LogWarning("Object sync: fuel holder type switch failed: " + ex.Message); }
            }
            if (h == null || e.Hold == null)
            {
                Plugin.Log.LogWarning($"Object sync: holder for {e.Key} not found here, item kept at the attach pose");
                e.SentHolder = CurrentHolder(e);
                StartFollow(e);
                e.AuthKinematic = kinematic;
                PlaceExactly(e, pos, rot);
                return;
            }
            if (CurrentHolder(e) == h)
            {
                // Already on that holder here: only make sure the joint is not pinned by a kinematic follow.
                if (e.Following) StopFollow(e, restorePhysics: false);
                e.Rb.isKinematic = kinematic;
            }
            else
            {
                if (e.Attached) RemoteDetach(e);
                if (!e.Part && !e.Go.activeInHierarchy && e.Go.activeSelf && h.gameObject.activeInHierarchy) e.Go.transform.SetParent(null, true);
                StopFollow(e, restorePhysics: false);
                e.Rb.isKinematic = kinematic;
                PlaceExactly(e, pos, rot);
                _applyingAttach = true;
                BlockHolder bh = h as BlockHolder;
                bool oldCan = bh != null && CanOperateRef != null && CanOperateRef(bh);
                try
                {
                    // The other player could attach it there, so its machine allows it.
                    if (bh != null && CanOperateRef != null) CanOperateRef(bh) = true;
                    h.AttachObject(e.Go);
                    if (CurrentHolder(e) != h && h.ObjectInHolder == null && !RidesHolder(e.Hold))
                    {
                        // A subclass refused (local-only condition): do what Holder.AttachObject does.
                        e.Hold.Attach(h.gameObject);
                        if (CurrentHolder(e) == h)
                        {
                            h.ObjectInHolder = e.Hold;
                            if (h.MyTrigger != null) h.MyTrigger.enabled = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Object sync: attach of {e.Key} failed: {ex.Message}");
                }
                finally
                {
                    if (bh != null && CanOperateRef != null) CanOperateRef(bh) = oldCan;
                    _applyingAttach = false;
                }
            }
            if (CurrentHolder(e) == h)
            {
                e.AttachEpoch = epoch;
            }
            else
            {
                Plugin.Log.LogWarning($"Object sync: could not attach {e.Key} to {h.name} (occupied here?)");
                StartFollow(e);
                e.AuthKinematic = kinematic;
                PlaceExactly(e, pos, rot);
            }
            e.SentHolder = CurrentHolder(e);
        }

        private static void ApplyDetach(Entry e, bool kinematic, Vector3 pos, Quaternion rot)
        {
            if (e.Attached) RemoteDetach(e);
            e.SentHolder = null;
            e.AttachEpoch = -1;
            StartFollow(e); // the owner streams the fall and sends the rest pose
            e.AuthKinematic = kinematic;
            PlaceExactly(e, pos, rot);
        }

        // =====================================================================================
        // Remote side
        // =====================================================================================

        /// <summary>
        /// Entry point with the transport peer the packet came from. The host remembers which peer
        /// each player (LocalId, first field of every non-passenger kind) sends through, so it can take
        /// over that player's objects when the peer leaves.
        /// </summary>
        internal static void Receive(BinaryReader r, ulong via)
        {
            if (via != 0 && Net.IsHost && r.BaseStream.CanSeek)
            {
                Stream s = r.BaseStream;
                long p = s.Position;
                if (s.Length - p >= 9 && r.ReadByte() != SubPassenger)
                {
                    ulong id = r.ReadUInt64();
                    if (id != 0 && id != Net.LocalId) OwnerVia[id] = via;
                }
                s.Position = p;
            }
            Receive(r);
        }

        /// <summary>Entry point for every (Msg)12 packet.</summary>
        internal static void Receive(BinaryReader r)
        {
            _statRecv++;
            if (r.BaseStream.CanSeek) _statRecvBytes += r.BaseStream.Length;
            byte sub = r.ReadByte();
            if (sub == SubPassenger) { PassengerSync.ReceivePassenger(r); return; }
            // Keys only mean the same object once both PCs run the same, indexed world, and on a client
            // only after the host's Sync renamed its save ordinals (everything the host relayed before
            // its dump is contained in the dump; everything after it arrives after it, same channel).
            // The host pushes its dump right after our Loaded, about one round trip later: SaveTransfer has
            // already let the world settle for 1 s, so index now instead of dropping the dump (and asking
            // for a second full one) while the 2 s index settle time runs.
            if (!_indexed && sub == SubSync) TryIndexNow();
            if (!_indexed || !WorldReady()) return;
            if (!Ready && sub != SubSync) return;
            bool inputWasBlocked = InteractionGuard.InputBlockedNow;
            _applyingRemote = true;
            try { ReceiveKind(sub, r); }
            finally
            {
                _applyingRemote = false;
                // Game code run for another player's action must not lock the local player's input.
                InteractionGuard.AfterRemoteApply(inputWasBlocked, sub);
            }
        }

        private static void ReceiveKind(byte sub, BinaryReader r)
        {
            switch (sub)
            {
                case SubGrab: ReceiveGrab(r); break;
                case SubDrop: ReceiveDrop(r); break;
                case SubPoses: ReceivePoses(r); break;
                case SubRest: ReceiveRest(r); break;
                case SubClaim: ReceiveClaim(r); break;
                case SubAttach: ReceiveAttach(r); break;
                case SubSyncReq:
                    ulong requester = r.ReadUInt64();
                    if (Net.IsHost && requester != Net.LocalId) SendSyncDump(requester);
                    break;
                case SubSync: ReceiveSync(r); break;
                case SubStackOp: ReceiveStackOp(r); break;
                case SubDestroy:
                    {
                        ulong from = r.ReadUInt64();
                        string key = r.ReadString();
                        string sid = r.ReadString();
                        Vector3 pos = ReadV3(r);
                        if (from != Net.LocalId) DestroyKeyed(key, sid, pos);
                        break;
                    }
                case SubCreate: ReceiveCreate(r); break;
                case SubAdopt: ReceiveAdopt(r); break;
                case SubRope: ReceiveRope(r); break;
                case SubRepair: ReceiveRepair(r); break;
                case SubJack: ReceiveJack(r); break;
                case SubState: ReceiveStateRefresh(r); break;
            }
        }

        private static void ReceiveAttach(BinaryReader r)
        {
            ulong sender = r.ReadUInt64();
            string key = r.ReadString();
            ushort epoch = r.ReadUInt16();
            bool attached = r.ReadBoolean();
            HolderRef hr = attached ? ReadHolderRef(r) : null;
            bool kinematic = r.ReadBoolean();
            Vector3 pos = ReadV3(r);
            Quaternion rot = ReadQ(r);
            var check = new Snap { Pos = pos, Rot = rot };

            Entry e = Lookup(key);
            if (sender == Net.LocalId || e == null || !Valid(ref check)) return;
            if (Frozen(e)) { Accept(e, sender, epoch); return; } // bed cargo: BedSync
            if (!Accept(e, sender, epoch)) return;
            if (e.HeldBy != 0) { e.HeldBy = 0; Held.Remove(e); }
            if (attached) ApplyAttach(e, ResolveHolder(hr), kinematic, check.Pos, check.Rot, epoch);
            else ApplyDetach(e, kinematic, check.Pos, check.Rot);
        }

        private sealed class SyncItem
        {
            public string Key, Ord, Sid = "";
            public ushort Epoch;
            public ulong Owner;
            public byte Flags;
            public Snap S;
            public int Stack = -1;
            public HolderRef Holder;
            public byte[] Blob;
        }

        private static readonly List<SyncItem> _syncItems = new List<SyncItem>();
        private static bool _syncMapped;

        /// <summary>
        /// Client: the host's current state of every object (sent after loading its world). Collected
        /// until the last packet, then applied in one go: keys renamed, states applied, leftovers removed.
        /// </summary>
        private static void ReceiveSync(BinaryReader r)
        {
            ulong host = r.ReadUInt64();
            ulong target = r.ReadUInt64();
            byte pflags = r.ReadByte();
            int count = r.ReadByte();
            // Only the first complete dump is applied: later ones answer retried requests (the live
            // messages that follow the first one already carry everything newer).
            bool mine = target == Net.LocalId && host != Net.LocalId && !Net.IsHost && !_synced;
            if (mine && (pflags & 1) != 0) { _syncItems.Clear(); _syncMapped = (pflags & 4) != 0; }
            if (mine) _syncNext = Time.unscaledTime + SyncRetry; // a dump is arriving: no retry while it flows
            for (int i = 0; i < count; i++)
            {
                var it = new SyncItem { Key = r.ReadString(), Epoch = r.ReadUInt16(), Owner = r.ReadUInt64(), Flags = r.ReadByte() };
                if ((it.Flags & 8) != 0) it.Ord = r.ReadString();
                if ((it.Flags & 4) == 0)
                {
                    it.S.Pos = ReadV3(r);
                    it.S.Rot = ReadQ(r);
                    it.Sid = r.ReadString();
                    it.Stack = r.ReadInt16();
                    if ((it.Flags & 2) != 0) it.Holder = ReadHolderRef(r);
                    if ((it.Flags & 16) != 0) it.Blob = r.ReadBytes(r.ReadUInt16());
                }
                if (mine) _syncItems.Add(it);
            }
            if (!mine || (pflags & 2) == 0) return;
            try { ApplySync(); }
            finally
            {
                _syncItems.Clear();
                _syncWanted = false;
                _synced = true;
                KeyVersion++;
                // Objects sold / returned / reset here before the sync: renamed (their deferred message
                // can name them now) and not re-created; forget them for good.
                _tmp.Clear();
                foreach (Entry e in All) if (e.Quiet) _tmp.Add(e);
                foreach (Entry e in _tmp) Forget(e);
                _tmp.Clear();
            }
            ApplyPendingDestroys();
        }

        private static void ApplySync()
        {
            // 1. Rename: save-ordinal keys -> the host's network keys (two-phase, keys may overlap).
            int renamed = 0, orphans = 0;
            if (_syncMapped)
            {
                var map = new Dictionary<string, string>();
                foreach (SyncItem it in _syncItems)
                    if (it.Ord != null) map[it.Ord] = it.Key;
                var save = new List<Entry>();
                foreach (Entry e in All)
                    if (e.SaveKey != null && e.Key == e.SaveKey)
                    {
                        save.Add(e);
                        if (ByKey.TryGetValue(e.Key, out Entry cur) && cur == e) ByKey.Remove(e.Key);
                    }
                foreach (Entry e in save)
                {
                    string ord = e.SaveKey;
                    e.SaveKey = null;
                    if (map.TryGetValue(ord, out string netKey) && !ByKey.ContainsKey(netKey))
                    {
                        e.Key = netKey;
                        _ordMap[ord] = netKey; // deferred purchase messages name objects by ordinal
                        renamed++;
                    }
                    else
                    {
                        e.Key = "orph:" + ord; // the host no longer has it (removed below) or a duplicate
                        orphans++;
                    }
                    ByKey[e.Key] = e;
                }
            }
            else
            {
                // Host without save snapshots (re-indexed itself after saving): ordinals are its keys.
                foreach (Entry e in All) e.SaveKey = null;
                _ordIdentity = true;
            }
            _synced = true; // keys are the host's from here on (holder refs resolve by key)
            KeyVersion++;

            // 2. States.
            _syncSeen.Clear();
            int applied = 0, created = 0;
            foreach (SyncItem it in _syncItems)
            {
                _syncSeen.Add(it.Key);
                if ((it.Flags & 4) != 0 || !Valid(ref it.S)) continue;
                Entry e = Lookup(it.Key);
                if (e == null)
                {
                    // Appeared on the host after the save (stacked buckets, created objects...): create it.
                    if (it.Sid.Length == 0 || ByKey.ContainsKey(it.Key)) continue;
                    if (CreateKeyed(it.Sid, it.S.Pos, it.S.Rot, it.Key, it.Stack, it.Blob) == null) continue;
                    created++;
                    e = Lookup(it.Key);
                    if (e == null) continue;
                }
                if (Frozen(e) || e.Owner == Net.LocalId || e == _localEntry) continue;
                if ((short)(it.Epoch - e.Epoch) < 0) continue;
                e.Epoch = it.Epoch;
                SetOwner(e, it.Owner == Net.LocalId ? 0 : it.Owner);
                if (e.Following && Active.Contains(e)) continue; // a live stream is newer
                bool kinematic = (it.Flags & 1) != 0;
                applied++;

                if (it.Stack >= 2) SetStackCount(e.Go, it.Stack);
                if (it.Holder != null && e.Hold != null)
                {
                    Holder h = ResolveHolder(it.Holder);
                    if (h != null) { ApplyAttach(e, h, kinematic, it.S.Pos, it.S.Rot, it.Epoch); continue; }
                }
                if (e.Attached) RemoteDetach(e);
                e.SentHolder = null;
                e.AttachEpoch = -1;
                StopFollow(e, restorePhysics: false);
                e.Rb.isKinematic = kinematic;
                UnparkIfMovedOut(e.Go, it.S.Pos);
                PlaceExactly(e, it.S.Pos, it.S.Rot);
                if (!kinematic && e.Go.activeInHierarchy)
                {
                    e.Rb.velocity = Vector3.zero;
                    e.Rb.angularVelocity = Vector3.zero;
                    e.Rb.Sleep();
                }
                ParkInUnloadedArea(e.Go, it.S.Pos);
            }

            // 3. Objects the host does not have (sold, stacked, consumed while the world was in transit).
            int removed = FinishSync();
            Plugin.Log.LogInfo($"Object sync: host sync: {renamed} keys renamed, {orphans} unknown to the host, " +
                               $"{applied} states applied, {created} created, {removed} removed");
        }

        /// <summary>
        /// Client, after the host's last Sync packet: objects that the host does not list are removed
        /// here too. Objects this PC created or owns since are kept. Skipped if the lists disagree a
        /// lot (index mismatch). Returns the number removed.
        /// </summary>
        private static int FinishSync()
        {
            string mine = ":" + Net.LocalId.ToString("x") + ":" + Salt + ":";
            _tmp.Clear();
            foreach (Entry e in All)
            {
                if (e.Go == null || e.Part || _syncSeen.Contains(e.Key) || e.Owner == Net.LocalId || e == _localEntry) continue;
                if (e.Key.IndexOf(mine, StringComparison.Ordinal) >= 0) continue; // created here meanwhile
                _tmp.Add(e);
            }
            _syncSeen.Clear();
            int limit = Mathf.Min(50, Mathf.Max(5, All.Count / 20));
            if (_tmp.Count > limit)
            {
                Plugin.Log.LogWarning($"Object sync: {_tmp.Count} local objects are unknown to the host (index mismatch?), none removed");
                return 0;
            }
            var list = new List<Entry>(_tmp);
            foreach (Entry e in list) DestroyEntry(e);
            return list.Count;
        }
        /// <summary>
        /// Takes an object out of an unloaded area's (inactive) GameplayObjects when its new position is
        /// outside that area, so it does not stay invisible there.
        /// </summary>
        private static void UnparkIfMovedOut(GameObject go, Vector3 pos)
        {
            if (IsPart(go)) return; // a rope part stays in its rope
            Transform parent = go.transform.parent;
            if (parent == null || go.activeInHierarchy || !go.activeSelf || AreaStreamer.Instances == null) return;
            foreach (AreaStreamer a in AreaStreamer.Instances)
            {
                if (a == null || a.GameplayObjects != parent) continue;
                if (AreaAt(pos) != a) go.transform.SetParent(null, true);
                return;
            }
        }

        private static Entry Lookup(string key) =>
            ByKey.TryGetValue(key, out Entry e) && e.Go != null && e.Rb != null ? e : null;

        /// <summary>Takes a remotely touched object out of a holder joint (mirrors GravityGun.PickOutlinedObject).</summary>
        private static void DetachIfAttached(Entry e)
        {
            if (e.Attached) RemoteDetach(e);
            e.SentHolder = null;
            e.AttachEpoch = -1;
        }

        /// <summary>
        /// True for poses older than an attach we applied: the owner stops streaming an attached item,
        /// so a pose with the same epoch can only be a late packet from before the attach.
        /// </summary>
        private static bool StaleForAttach(Entry e, ushort epoch) => SyncLogic.StaleForAttach(e.Attached, e.AttachEpoch, epoch);

        private static void ReceiveClaim(BinaryReader r)
        {
            ulong who = r.ReadUInt64();
            string key = r.ReadString();
            ushort epoch = r.ReadUInt16();
            Entry e = Lookup(key);
            if (who == Net.LocalId || e == null || !Accept(e, who, epoch)) return;
            // Attached objects keep their joint (making them kinematic would pin the holder's body);
            // frozen bed cargo is BedSync's (it gets the kinematic state when the bed unfreezes).
            if (!e.Attached && !Frozen(e)) StartFollow(e);
        }

        private static void ReceiveGrab(BinaryReader r)
        {
            ulong holder = r.ReadUInt64();
            string key = r.ReadString();
            ushort epoch = r.ReadUInt16();
            Entry e = Lookup(key);
            if (holder == Net.LocalId || e == null) return;
            // Frozen in a truck bed here: never pull it out of the bed (BedSync owns it). Still record
            // ownership so later poses with this epoch are accepted.
            if (Frozen(e)) { Accept(e, holder, epoch); return; }
            if (!Accept(e, holder, epoch)) return;

            GameObject go = e.Go;
            // Frozen inside an area that is not loaded on this PC (inactive GameplayObjects):
            // take it out so it shows in the holder's hands. The game re-parents it to the area
            // it is dropped in (AreaObjectParentManage), or ReceiveRest parks it if that area is unloaded.
            if (!go.activeInHierarchy && !e.Part) go.transform.SetParent(null, true);
            DetachIfAttached(e);
            e.HeldBy = holder;
            e.HeldUpdate = Time.unscaledTime;
            Held.Add(e);
            StartFollow(e);
        }

        private static void ReceiveDrop(BinaryReader r)
        {
            ulong holder = r.ReadUInt64();
            string key = r.ReadString();
            ushort epoch = r.ReadUInt16();
            bool hasPose = r.ReadBoolean();
            var s = new Snap();
            if (hasPose)
            {
                s.Time = r.ReadDouble();
                s.Pos = ReadV3(r);
                s.Rot = ReadQ(r);
                s.Vel = ReadV3(r);
                s.AngVel = ReadV3(r);
            }
            Entry e = Lookup(key);
            if (holder == Net.LocalId || e == null) return;
            if (e.HeldBy == holder) { e.HeldBy = 0; Held.Remove(e); }
            // Held poses of this epoch still in flight on the unreliable channel are older than this Drop.
            e.DropFrom = holder;
            e.DropEpoch = epoch;
            e.DropNoPose = !hasPose;
            if (Frozen(e)) return; // bed cargo: BedSync
            if (!hasPose)
            {
                // Destroyed in the holder's hands (sold...) or put into a frozen bed there: stop following.
                if (e.Owner == holder) { StopFollow(e, restorePhysics: true); e.Owner = 0; }
                return;
            }
            if (!Valid(ref s) || !Accept(e, holder, epoch) || IsSuppressed(e.Go) || StaleForAttach(e, epoch)) return;
            Clock c = UpdateClock(holder, s.Time, timing: false); // reliable: may have waited for a resend
            StartFollow(e);
            AddSnapshot(e, c, s);
        }

        private static void ReceivePoses(BinaryReader r)
        {
            ulong sender = r.ReadUInt64();
            double stamp = r.ReadDouble();
            int count = r.ReadByte();
            if (sender == Net.LocalId || double.IsNaN(stamp) || double.IsInfinity(stamp)) return;
            Clock c = UpdateClock(sender, stamp, timing: true);
            float now = Time.unscaledTime;
            for (int i = 0; i < count; i++)
            {
                string key = r.ReadString();
                ushort epoch = r.ReadUInt16();
                byte flags = r.ReadByte();
                var s = new Snap { Time = stamp, Pos = ReadV3(r), Rot = UnpackQ(r.ReadUInt32()), Vel = ReadHalf3(r), AngVel = ReadHalf3(r) };
                bool held = (flags & 1) != 0;

                Entry e = Lookup(key);
                if (e == null || !Valid(ref s) || Frozen(e)) continue; // frozen on a bed: BedSync
                // Unreliable poses can arrive after reliable messages sent later (other channel, resends):
                //  - after the Drop of this hold (same epoch): no longer held; nothing at all if the Drop
                //    said the object is gone from the hand (destroyed / frozen bed);
                //  - after the rest pose: older than it, ignored.
                if (SyncLogic.HeldAfterDrop(e.DropFrom, e.DropEpoch, sender, epoch))
                {
                    if (e.DropNoPose) continue;
                    held = false;
                }
                if (SyncLogic.OlderThanRest(e.LastRestFrom, e.LastRest, sender, stamp)) continue; // overtaken by the rest pose
                if (!SyncLogic.ClaimWins(e.Owner, e.Epoch, sender, epoch) || StaleForAttach(e, epoch)) continue;
                GameObject go = e.Go;
                if (!go.activeInHierarchy)
                {
                    if (!held || e.Part) continue; // inside an area not loaded here; ReceiveRest parks it
                    go.transform.SetParent(null, true);
                }
                Accept(e, sender, epoch);
                bool newest = stamp >= e.NewestStamp;
                if (held && newest)
                {
                    e.HeldBy = sender;
                    e.HeldUpdate = now;
                    Held.Add(e);
                }
                DetachIfAttached(e);
                StartFollow(e);
                if (newest) { e.AuthKinematic = (flags & 2) != 0; e.NewestStamp = stamp; }
                AddSnapshot(e, c, s);
            }
        }

        private static void ReceiveRest(BinaryReader r)
        {
            ulong sender = r.ReadUInt64();
            string key = r.ReadString();
            ushort epoch = r.ReadUInt16();
            var s = new Snap { Time = r.ReadDouble(), Pos = ReadV3(r), Rot = ReadQ(r) };
            bool kinematic = r.ReadBoolean();

            Entry e = Lookup(key);
            if (sender == Net.LocalId || e == null || !Valid(ref s)) return;
            // Frozen in a truck bed here: the bed owns its pose (BedSync); a stale pose would yank it off.
            // But the owner's copy may already rest because its truck stopped there while ours still plays
            // the drive to its final pose: keep the newest such rest pose and apply it once the bed unfreezes
            // (measured in game: without it, cargo of a truck that rolled on after its driver got out ended
            // 6-7 m from the owner's copy).
            if (Frozen(e))
            {
                if (BedSync.IsFrozenItem(e.Go) &&
                    (!PendingRests.TryGetValue(e, out PendingRest old) || old.From != sender || old.S.Time <= s.Time))
                    PendingRests[e] = new PendingRest { From = sender, Epoch = epoch, S = s, Kinematic = kinematic, At = Time.unscaledTime };
                return;
            }
            ApplyRest(e, sender, epoch, s, kinematic);
        }

        private struct PendingRest
        {
            public ulong From;
            public ushort Epoch;
            public Snap S;
            public bool Kinematic;
            public float At;
        }

        private const float PendingRestLifetime = 30f;
        private static readonly Dictionary<Entry, PendingRest> PendingRests = new Dictionary<Entry, PendingRest>();
        private static List<Entry> _pendingDone;

        /// <summary>Rest poses that arrived while the item was frozen on a bed here, applied once it is not.</summary>
        private static void ApplyPendingRests(float now)
        {
            if (PendingRests.Count == 0) return;
            _pendingDone?.Clear();
            foreach (var kv in PendingRests)
            {
                Entry e = kv.Key;
                bool gone = e.Go == null || e.Rb == null || now - kv.Value.At > PendingRestLifetime;
                if (!gone && (BedSync.IsFrozenItem(e.Go) || !e.Go.activeSelf)) continue; // still on the bed
                if (!gone && !Frozen(e))
                {
                    PendingRest p = kv.Value;
                    ApplyRest(e, p.From, p.Epoch, p.S, p.Kinematic, placeNow: true);
                }
                (_pendingDone ??= new List<Entry>()).Add(e);
            }
            if (_pendingDone != null) foreach (Entry e in _pendingDone) PendingRests.Remove(e);
        }

        private static void ApplyRest(Entry e, ulong sender, ushort epoch, Snap s, bool kinematic, bool placeNow = false)
        {
            if (SyncLogic.OlderThanRest(e.LastRestFrom, e.LastRest, sender, s.Time)) return;
            if (!Accept(e, sender, epoch) || StaleForAttach(e, epoch)) return;
            Clock c = UpdateClock(sender, s.Time, timing: false); // reliable: may have waited for a resend

            DetachIfAttached(e);
            StartFollow(e);
            e.AuthKinematic = kinematic;
            e.HasRest = true;
            e.RestStamp = s.Time;
            e.LastRestFrom = sender;
            e.LastRest = s.Time;
            e.RestPos = s.Pos;
            e.RestRot = s.Rot;
            if (placeNow || e.Count == 0 || !c.Sync.Set || !e.Go.activeInHierarchy) PlaceRest(e);
            else Active.Add(e); // FixedTick places it when playback reaches the rest stamp
        }

        private static void PlaceRest(Entry e)
        {
            Active.Remove(e);
            e.Count = 0;
            e.LastVel = Vector3.zero;
            ResetSmoothing(e);
            PlaceExactly(e, e.RestPos, e.RestRot);
            // Came to rest in an area whose scene (and ground) is not loaded here: freeze it in that
            // area like the game does. Never for bed cargo (checked by the callers).
            ParkInUnloadedArea(e.Go, e.RestPos);
            // Hand it back to local physics shortly (ReleaseRested); kinematic until then so the
            // exact pose is not disturbed while neighbours settle too.
            if (e.Following)
            {
                e.ReleaseAt = Time.unscaledTime + RestRelease;
                Releasing.Add(e);
            }
        }

        private static void PlaceExactly(Entry e, Vector3 pos, Quaternion rot)
        {
            e.Rb.position = pos;
            e.Rb.rotation = rot;
            e.Go.transform.SetPositionAndRotation(pos, rot);
        }

        private static bool Valid(ref Snap s)
        {
            if (!IsFinite(s.Pos) || !IsFinite(s.Vel) || !IsFinite(s.AngVel) || double.IsNaN(s.Time) || double.IsInfinity(s.Time)) return false;
            Quaternion q = s.Rot;
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (!(mag > 0.5f)) return false;
            s.Rot = new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
            return true;
        }

        // ---------------------------------------------------------------- snapshot interpolation

        /// <param name="timing">False for reliable messages: they may have waited for a retransmission,
        /// so they never teach the latency / jitter estimate (they still detect a restarted sender).</param>
        private static Clock UpdateClock(ulong sender, double stamp, bool timing)
        {
            if (!Clocks.TryGetValue(sender, out Clock c)) Clocks[sender] = c = new Clock();
            if (c.Sync.Feed(stamp, Time.unscaledTimeAsDouble, timing))
            {
                // The sender's clock restarted: its old snapshots and stamps are meaningless.
                foreach (Entry e in Active)
                    if (e.Owner == sender) { e.Count = 0; e.SnapPending = true; }
                foreach (Entry e in All)
                {
                    if (e.LastRestFrom == sender) e.LastRestFrom = 0; // old-clock stamps
                    if (e.Owner == sender) e.NewestStamp = double.NegativeInfinity;
                }
                c.AdvancedAt = -1.0;
            }
            return c;
        }

        private static void AddSnapshot(Entry e, Clock c, Snap s)
        {
            e.HasRest = false;
            e.LastSnapLocal = Time.unscaledTime;
            Active.Add(e);
            if (e.Buf == null) e.Buf = new Snap[BufSize];

            if (e.Count > 0)
            {
                Snap newest = e.Buf[e.Count - 1];
                float dt = (float)(s.Time - newest.Time);
                if (dt > 0f)
                {
                    // Teleport (respawn, reset...): moved much farther than its speed allows.
                    float speed = Mathf.Max(newest.Vel.magnitude, s.Vel.magnitude);
                    float allowed = (speed * 1.5f + 10f) * dt + 2f;
                    if ((s.Pos - newest.Pos).sqrMagnitude > allowed * allowed)
                    {
                        e.Count = 0;
                        e.SnapPending = true;
                    }
                }
            }

            // Sorted insert (unreliable packets may arrive out of order); duplicates and packets older
            // than a full buffer are dropped.
            SyncLogic.InsertByTime(e.Buf, ref e.Count, s);
        }

        /// <summary>Render time of a sender's clock for this physics step (once per step and sender).</summary>
        private static void Advance(Clock c, double fixedNow)
        {
            if (c.AdvancedAt == fixedNow) return;
            c.AdvancedAt = fixedNow;
            c.Render = c.Sync.RenderAt(fixedNow);
        }

        /// <summary>Called from Plugin.FixedUpdate: moves followed objects (kinematic).</summary>
        internal static void FixedTick()
        {
            if (!Net.Active || Active.Count == 0) return;
            double fixedNow = Time.fixedUnscaledTimeAsDouble; // same time base as the receive times fed to SyncClock
            float fixedDt = Time.fixedUnscaledDeltaTime;
            float now = Time.unscaledTime;
            float fade = Mathf.Exp(-fixedDt / ErrorFade);

            // The game may have picked an object up this frame before Tick noticed (TrackLocalHold runs
            // in Update): never pin the object in the local hand to a remote stream.
            GameObject heldNow = CurrentLocalHeld();
            _tmp.Clear();
            foreach (Entry e in Active) _tmp.Add(e);
            foreach (Entry e in _tmp)
            {
                if (e.Go == null || e.Rb == null || !e.Following || e.Owner == Net.LocalId || e.Owner == 0 ||
                    !Clocks.TryGetValue(e.Owner, out Clock c))
                {
                    Active.Remove(e);
                    continue;
                }
                if (IsSuppressed(e.Go)) { Active.Remove(e); e.Count = 0; continue; } // frozen meanwhile
                if (e == _localEntry || ReferenceEquals(e.Go, heldNow)) continue;
                Advance(c, fixedNow);

                if (e.HasRest && (e.Count == 0 || c.Render >= e.RestStamp))
                {
                    PlaceRest(e);
                    continue;
                }
                if (e.Count == 0) { Active.Remove(e); continue; }
                if (!e.HasRest && now - e.LastSnapLocal > FollowTimeout)
                {
                    // The owner went silent mid-motion (disconnected): give it back to local physics.
                    StopFollow(e, restorePhysics: true);
                    e.Owner = 0;
                    continue;
                }

                // Drop snapshots that playback has passed (keep the one right before the render time).
                SyncLogic.DropPassed(e.Buf, ref e.Count, c.Render);

                bool extrapolating = Sample(e, c.Render, out Vector3 pos, out Quaternion rot, out e.LastVel);
                Rigidbody rb = e.Rb;
                if (!rb.isKinematic) rb.isKinematic = true; // a game script reset it
                if (e.SnapPending || (rb.position - pos).sqrMagnitude > SnapDistance * SnapDistance)
                {
                    e.SnapPending = false;
                    ResetSmoothing(e);
                    PlaceExactly(e, pos, rot);
                    continue;
                }

                // Playback had run past the newest snapshot (a lost / late packet) and data arrived again:
                // the guess and the real path differ. Fade that difference out instead of jumping.
                if (e.Extrapolating && !extrapolating && e.Shown)
                {
                    e.ErrPos = e.ShownPos - pos;
                    e.ErrRot = e.ShownRot * Quaternion.Inverse(rot);
                    if (e.ErrPos.sqrMagnitude > MaxSmoothedError * MaxSmoothedError) { e.ErrPos = Vector3.zero; e.ErrRot = Quaternion.identity; }
                }
                e.Extrapolating = extrapolating;
                if (e.ErrPos.sqrMagnitude > 1e-8f)
                {
                    pos += e.ErrPos;
                    e.ErrPos *= fade;
                }
                else e.ErrPos = Vector3.zero;
                if (e.ErrRot != Quaternion.identity)
                {
                    rot = e.ErrRot * rot;
                    e.ErrRot = Quaternion.Slerp(Quaternion.identity, e.ErrRot, fade);
                    if (Quaternion.Angle(e.ErrRot, Quaternion.identity) < 0.05f) e.ErrRot = Quaternion.identity;
                }
                e.ShownPos = pos;
                e.ShownRot = rot;
                e.Shown = true;

                // MovePosition on a kinematic body gives it a real velocity, so it pushes what it touches.
                rb.MovePosition(pos);
                rb.MoveRotation(rot);
            }
        }

        /// <summary>
        /// Pose at render time <paramref name="tr"/> (interpolated / briefly extrapolated / held).
        /// Returns true while playback is past the newest snapshot.
        /// </summary>
        private static bool Sample(Entry e, double tr, out Vector3 pos, out Quaternion rot, out Vector3 vel)
        {
            int mode = SyncLogic.Segment(e.Buf, e.Count, tr, out int i, out float s);
            Snap a = e.Buf[i];
            if (mode == 0)
            {
                pos = a.Pos; rot = a.Rot; vel = Vector3.zero;
                return false;
            }
            if (mode == 2)
            {
                float ex = Mathf.Min(s, MaxExtrapolate);
                pos = a.Pos + a.Vel * ex;
                rot = a.Rot;
                float w = a.AngVel.magnitude;
                if (w > 1e-3f) rot = Quaternion.AngleAxis(w * ex * Mathf.Rad2Deg, a.AngVel / w) * rot;
                vel = s < MaxExtrapolate ? a.Vel : Vector3.zero;
                return true;
            }
            Snap b = e.Buf[i + 1];
            float dt = (float)(b.Time - a.Time);
            if (dt > 0.3f)
            {
                pos = Vector3.Lerp(a.Pos, b.Pos, s);
            }
            else
            {
                float s2 = s * s, s3 = s2 * s;
                float h00 = 2f * s3 - 3f * s2 + 1f, h10 = s3 - 2f * s2 + s, h01 = -2f * s3 + 3f * s2, h11 = s3 - s2;
                pos = h00 * a.Pos + (h10 * dt) * a.Vel + h01 * b.Pos + (h11 * dt) * b.Vel;
            }
            rot = Quaternion.Slerp(a.Rot, b.Rot, s);
            vel = Vector3.Lerp(a.Vel, b.Vel, s);
            return false;
        }

        // =====================================================================================
        // Streamed areas (Town, parcels...). Each AreaStreamer loads a scene with its own terrain and
        // keeps that area's objects under GameplayObjects, which is inactive while the scene is unloaded.
        // =====================================================================================

        /// <summary>The streamed area whose load bounds contain <paramref name="pos"/> (height ignored).</summary>
        internal static AreaStreamer AreaAt(Vector3 pos)
        {
            List<AreaStreamer> all = AreaStreamer.Instances;
            if (all == null) return null;
            foreach (AreaStreamer a in all)
            {
                if (a == null || a.GameplayObjects == null || a.LoadBounds == null) continue;
                foreach (Bounds b in a.LoadBounds)
                    if (b.Contains(new Vector3(pos.x, b.center.y, pos.z))) return a;
            }
            return null;
        }

        /// <summary>Puts <paramref name="go"/> under an area's GameplayObjects, as DepotPlace does.</summary>
        internal static void ParentToArea(GameObject go, AreaStreamer area)
        {
            go.transform.SetParent(area.GameplayObjects, true);
            var save = go.GetComponent<SaveSystem.SaveMe>();
            if (save != null) save.MySceneName = area.sceneName;
        }

        /// <summary>If <paramref name="pos"/> lies in an area that is not loaded on this PC, parks the object there.</summary>
        internal static void ParkInUnloadedArea(GameObject go, Vector3 pos)
        {
            if (BedSync.IsFrozenItem(go) || IsPart(go)) return; // never pull frozen cargo out of its bed, nor a part out of its rope
            AreaStreamer area = AreaAt(pos);
            if (area != null && !area.GameplayObjects.gameObject.activeInHierarchy && go.transform.parent != area.GameplayObjects)
                ParentToArea(go, area);
        }

        // =====================================================================================
        // Create / destroy. The game creates and destroys loose objects on one PC only when buckets
        // are stacked (Bucket.CreateStack, BucketSlot.Attach -> BucketStack.Attach / Combine,
        // BucketStack.Detach), when a stack is sold (SellGUI, see PurchaseSync) and when an object is
        // consumed. The game's methods are not re-run on the other PCs (they pick objects with the
        // local GravityGun, show hints, roll random visuals); their RESULT is replicated instead:
        //   StackOp (reliable): sender, destroyed (key, SaveID, pos), created (key, SaveID, pose,
        //                       stack size), stack sizes (key, size)
        //   Destroy (reliable): sender, key, SaveID, pos  (any other gameplay destroy, see ScanUnowned)
        // Created objects get the key "stk:<creator LocalId>:<n>" (same on every PC). The receiver
        // instantiates the same prefab (CheckPointManagerBase.GetPrefab) and sets the stack size with
        // the game's own BucketStack.AttachMultipleBuckets / DetachMultipleBuckets. The creator then
        // owns the new objects (claim, stream, rest pose) like any other object.
        // =====================================================================================

        private struct Involved
        {
            public GameObject Go;
            public string Key, Sid;
            public Vector3 Pos;
        }

        private sealed class PendingOp
        {
            public int Frame;
            public int SaveCount;
            public readonly List<Involved> Objects = new List<Involved>();
            public readonly List<KeyValuePair<BucketStack, int>> Stacks = new List<KeyValuePair<BucketStack, int>>();
            public readonly List<GameObject> Created = new List<GameObject>();
        }

        private struct PendingDestroy
        {
            public string Key, Sid;
            public Vector3 Pos;
        }

        private static readonly List<PendingOp> PendingOps = new List<PendingOp>();
        private static readonly List<PendingDestroy> PendingDestroys = new List<PendingDestroy>();
        private static readonly HashSet<string> _syncSeen = new HashSet<string>();
        private static PendingOp _op;
        private static int _opDepth;
        private static int _localKeyCounter;
        private static float _destroyMuteUntil;

        /// <summary>
        /// Random per game process: keys this PC creates never collide with keys it created in an
        /// earlier run that the host still uses (Steam ids stay the same across restarts).
        /// </summary>
        internal static readonly string Salt = ((uint)Guid.NewGuid().GetHashCode()).ToString("x");

        /// <summary>A network key for an object this PC creates; unique per creator, same on every PC.</summary>
        internal static string NewLocalKey() => "stk:" + Net.LocalId.ToString("x") + ":" + Salt + ":" + (_localKeyCounter++);

        /// <summary>Forgets an object this PC destroys itself and announces by other means (no Destroy echo).</summary>
        internal static void ForgetQuiet(GameObject go)
        {
            Entry e = EntryOf(go);
            if (e == null) return;
            if (!Ready)
            {
                // Keys not shared yet: keep it until the host's key sync renamed it (the deferred message
                // names it by the host's key, and the sync must not re-create it); the scan then drops it.
                e.Quiet = true;
                StopFollow(e, restorePhysics: false);
                return;
            }
            Forget(e);
        }

        /// <summary>
        /// After a game script detached holdables under <paramref name="root"/> on purpose on every PC
        /// (parcel reset), records the new holder state so it is not announced as a local change.
        /// </summary>
        internal static void AcceptHolderState(GameObject root)
        {
            if (root == null) return;
            foreach (Holdable h in root.GetComponentsInChildren<Holdable>(true))
            {
                Entry e = h != null ? EntryOf(h.gameObject) : null;
                if (e == null) continue;
                e.SentHolder = CurrentHolder(e);
                e.AttachEpoch = -1;
            }
        }

        /// <summary>
        /// Instantiates <paramref name="sid"/> with network key <paramref name="key"/> (no-op if it exists),
        /// applies the sender's component data (<paramref name="state"/>, may be null) and stack size.
        /// </summary>
        internal static GameObject CreateKeyed(string sid, Vector3 pos, Quaternion rot, string key, int stackCount, byte[] state = null)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (ByKey.TryGetValue(key, out Entry have) && have.Go != null) return have.Go;
            if (string.IsNullOrEmpty(sid) || !SaveSystem.Singleton<CheckPointManagerBase>.IsInstanced()) return null;
            GameObject prefab = SaveSystem.Singleton<CheckPointManagerBase>.Instance.GetPrefab(sid);
            if (prefab == null)
            {
                Plugin.Log.LogWarning("Object sync: no prefab for SaveID " + sid + ", " + key + " not created");
                return null;
            }
            GameObject go = UnityEngine.Object.Instantiate(prefab, pos, rot); // SaveMe.Awake registers it for saving
            if (state != null) ApplyState(go, state); // before the stack size (BucketStack rebuilds from its data)
            if (stackCount >= 2) SetStackCount(go, stackCount);
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            RegisterObject(go, key);
            ParkInUnloadedArea(go, pos);
            return go;
        }

        /// <summary>Sets a bucket stack's size with the game's own add / remove code.</summary>
        private static void SetStackCount(GameObject go, int count)
        {
            BucketStack bs = go != null ? go.GetComponent<BucketStack>() : null;
            if (bs == null || count < 2) return;
            int diff = Mathf.Min(count, bs.maxBucketCount) - bs.BucketCount;
            if (diff > 0) bs.AttachMultipleBuckets(diff);
            else if (diff < 0) bs.DetachMultipleBuckets(-diff);
        }

        /// <summary>
        /// Destroys the object with this network key. Until keys are shared (index built, client renamed
        /// to the host's keys) the request waits. No position fallback: a near object of the same type
        /// may well be a different object that the sender still has.
        /// </summary>
        internal static void DestroyKeyed(string key, string sid, Vector3 pos)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!Ready)
            {
                if (PendingDestroys.Count < 4096) PendingDestroys.Add(new PendingDestroy { Key = key, Sid = sid ?? "", Pos = pos });
                return;
            }
            Entry e = Lookup(key);
            if (e != null) DestroyEntry(e);
            else if (ByKey.TryGetValue(key, out Entry dead)) Forget(dead);
        }

        private static void DestroyEntry(Entry e)
        {
            GameObject go = e.Go;
            Forget(e);
            if (go == null) return;
            InteractionGuard.BeforeRemoteRemove(go); // ends a local use of it (panning tub, shovel holder...)
            ForceLocalDrop(go); // drops it if the local player holds it
            UnityEngine.Object.Destroy(go);
        }

        private static void ApplyPendingDestroys()
        {
            if (PendingDestroys.Count == 0 || !Ready) return;
            var list = new List<PendingDestroy>(PendingDestroys);
            PendingDestroys.Clear();
            foreach (PendingDestroy d in list) DestroyKeyed(d.Key, d.Sid, d.Pos);
            Plugin.Log.LogInfo($"Object sync: applied {list.Count} destroys received before the keys were shared");
        }

        // ---------------------------------------------------------------- generic create
        // Every saved object registers itself in CheckPointManagerBase.Add (SaveMe.Awake). A physics
        // object that appears here during play and that no sync path keyed by the next frame (purchase,
        // stack op, sale, network create) was created by a local game script only: gold bars made at the
        // forge, holdables taken out of holders, event rewards... It is announced with a fresh key and
        // its component data, and this PC owns it. Exceptions:
        //  - created inside a world-processing call that this PC only simulates for show
        //    (GoldSync.SuppressDepth > 0, see ContainerSync / GoldSync): the PC that runs it creates and
        //    announces the real one, so this copy is removed;
        //  - an object the same script just created on another PC too (same SaveID within 0.5 m and
        //    3 s, different creator): merged instead of doubled (the lower creator LocalId's key wins on
        //    every PC).

        private struct Spawned
        {
            public SaveSystem.SaveMe Sm;
            public bool Suppressed;
        }

        private static readonly List<Spawned> _spawnQueue = new List<Spawned>();
        private const float DupWindow = 3f;
        private const float DupRadiusSq = 0.5f * 0.5f;

        private static void OnSaveMeAdded(SaveSystem.SaveMe sm)
        {
            if (sm != null && CaptureRepairSpawn(sm.gameObject)) return; // made by a remote repair-holder apply
            if (sm == null || !Net.Active || !Ready || _spawnQueue.Count >= 256 || GameState.IsLoading) return;
            // SuppressDepth > 0: inside world processing this PC only simulates for show (a client in
            // host-only code, or either PC inside ContainerSync's wrap of a machine the other PC runs).
            _spawnQueue.Add(new Spawned { Sm = sm, Suppressed = GoldSync.SuppressDepth > 0 });
        }

        private static void ProcessSpawns(float now)
        {
            if (_spawnQueue.Count == 0) return;
            for (int i = 0; i < _spawnQueue.Count; i++)
            {
                Spawned s = _spawnQueue[i];
                SaveSystem.SaveMe sm = s.Sm;
                if (sm == null) continue;
                GameObject go = sm.gameObject;
                if (go == null || !go.activeInHierarchy || ByInstance.ContainsKey(go.GetInstanceID()) || !Syncable(go)) continue;
                string sid = sm.GetSaveID();
                if (string.IsNullOrEmpty(sid) || SaveSystem.Singleton<CheckPointManagerBase>.Instance.GetPrefab(sid) == null) continue;
                Transform parent = go.transform.parent;
                // Part of another body / saved object (rope points, attached parts): that object owns it.
                if (parent != null && (parent.GetComponentInParent<Rigidbody>() != null || parent.GetComponentInParent<SaveSystem.SaveMe>() != null)) continue;
                Vector3 pos = go.transform.position;
                if (s.Suppressed)
                {
                    if (LogEvent(EvSimCreate))
                        Plugin.Log.LogInfo($"Object sync: {sid} created by simulated-only world processing, removed (the host creates it)");
                    UnityEngine.Object.Destroy(go);
                    continue;
                }
                if (FindDuplicate(sid, pos, now, 0, remoteOnly: true) != null)
                {
                    UnityEngine.Object.Destroy(go); // another PC's copy of the same creation is already here
                    continue;
                }
                string key = NewLocalKey();
                RegisterObject(go, key);
                Entry e = EntryOf(go);
                if (e == null) continue;
                e.CreatedLocal = true;
                e.CreatedAt = now;
                SendCreate(e);
                ClaimLocal(go); // this PC streams its fall and sends its rest pose
            }
            _spawnQueue.Clear();
        }

        /// <summary>
        /// A recent creation (local or announced by another PC) of the same prefab at the same spot whose
        /// key comes from another creator than <paramref name="creator"/> (0 = any). Creations of one
        /// creator are never duplicates of each other (a forge making two bars in a row).
        /// </summary>
        private static Entry FindDuplicate(string sid, Vector3 pos, float now, ulong creator, bool remoteOnly)
        {
            foreach (Entry c in All)
            {
                if (c.Go == null || c.Sid != sid || now - c.CreatedAt > DupWindow) continue;
                if (remoteOnly ? !c.CreatedRemote : !(c.CreatedRemote || c.CreatedLocal)) continue;
                if (creator != 0 && CreatorOf(c.Key) == creator) continue;
                if ((c.Go.transform.position - pos).sqrMagnitude <= DupRadiusSq) return c;
            }
            return null;
        }

        /// <summary>LocalId of the PC that created a "stk:" / "buy:" key (0 for other keys).</summary>
        private static ulong CreatorOf(string key)
        {
            if (key == null || key.Length < 6 || key[3] != ':') return 0;
            int end = key.IndexOf(':', 4);
            if (end < 0) return 0;
            ulong id = 0;
            for (int i = 4; i < end; i++)
            {
                char ch = key[i];
                int v = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : -1;
                if (v < 0) return 0;
                id = (id << 4) | (uint)v;
            }
            return id;
        }

        /// <summary>Merge rule, identical on every PC: the key of the lower creator LocalId wins (then ordinal order).</summary>
        private static bool KeyWins(string a, string b)
        {
            ulong ca = CreatorOf(a), cb = CreatorOf(b);
            if (ca != cb) return ca < cb;
            return string.CompareOrdinal(a, b) < 0;
        }

        private static void SendCreate(Entry e, byte sub = SubCreate)
        {
            Transform t = e.Go.transform;
            byte[] state = SerializeState(e.Go);
            var ms = Begin(sub, out var w);
            w.Write(Net.LocalId);
            w.Write(e.Key);
            w.Write(e.Sid);
            WriteV3(w, t.position);
            WriteQ(w, t.rotation);
            BucketStack bs = e.Go.GetComponent<BucketStack>();
            w.Write((short)(bs != null ? bs.BucketCount : -1));
            w.Write((ushort)(state?.Length ?? 0));
            if (state != null) w.Write(state);
            Bcast(ms.ToArray(), true);
            if (sub == SubCreate && LogEvent(EvCreate)) Plugin.Log.LogInfo($"Object sync: created {e.Sid} ({e.Key}) at {t.position}");
        }

        /// <summary>
        /// Host keyed a saved object that had no key (OnHostSaved): take that key for the own unkeyed copy
        /// of the same prefab nearest to the host's pose (within 2 m), else create it.
        /// </summary>
        private static void ReceiveAdopt(BinaryReader r)
        {
            ulong sender = r.ReadUInt64();
            string key = r.ReadString();
            string sid = r.ReadString();
            var s = new Snap { Pos = ReadV3(r), Rot = ReadQ(r) };
            int stack = r.ReadInt16();
            int len = r.ReadUInt16();
            byte[] state = len > 0 ? r.ReadBytes(len) : null;
            if (sender == Net.LocalId || Net.IsHost || !Valid(ref s) || ByKey.ContainsKey(key) ||
                !SaveSystem.Singleton<CheckPointManagerBase>.IsInstanced()) return;
            GameObject best = null;
            float bestSq = 2f * 2f;
            foreach (SaveSystem.SaveMe sm in SaveSystem.Singleton<CheckPointManagerBase>.Instance.ToSave)
            {
                if (sm == null) continue;
                GameObject go = sm.gameObject;
                if (ByInstance.ContainsKey(go.GetInstanceID()) || sm.GetSaveID() != sid || !Syncable(go)) continue;
                float sq = (go.transform.position - s.Pos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = go; }
            }
            if (best == null)
            {
                CreateKeyed(sid, s.Pos, s.Rot, key, stack, state);
                return;
            }
            RegisterObject(best, key);
            Entry e = EntryOf(best);
            if (e == null || e.Attached || Frozen(e) || e == _localEntry) return;
            PlaceExactly(e, s.Pos, s.Rot); // the host's pose (its copy is the one that was saved)
        }

        private static void ReceiveCreate(BinaryReader r)
        {
            ulong sender = r.ReadUInt64();
            string key = r.ReadString();
            string sid = r.ReadString();
            var s = new Snap { Pos = ReadV3(r), Rot = ReadQ(r) };
            int stack = r.ReadInt16();
            int len = r.ReadUInt16();
            byte[] state = len > 0 ? r.ReadBytes(len) : null;
            if (sender == Net.LocalId || !Valid(ref s) || ByKey.ContainsKey(key)) return;
            float now = Time.unscaledTime;
            // The same game script created it on another PC too (here, or announced by a third PC): keep
            // one object and the winning key everywhere (every PC sees every Create and applies the same
            // rule, so 3+ players converge too).
            Entry dup = FindDuplicate(sid, s.Pos, now, sender, remoteOnly: false);
            if (dup != null)
            {
                if (KeyWins(key, dup.Key))
                {
                    if (LogEvent(EvMerge)) Plugin.Log.LogInfo($"Object sync: {dup.Key} is {key} (created on two PCs), merged");
                    Rekey(dup, key);
                }
                return;
            }
            GameObject go = CreateKeyed(sid, s.Pos, s.Rot, key, stack, state);
            Entry e = EntryOf(go);
            if (e != null) { e.CreatedRemote = true; e.CreatedAt = now; }
        }

        // ---------------------------------------------------------------- component data
        // ISerializableComponent data in the save system's own key/value form (what a save would write
        // and a load would apply to a freshly instantiated prefab): gold bar mass, contents, stack size...
        // Object references are not portable and are skipped.

        private const int MaxStateBytes = 4096;

        private static byte[] SerializeState(GameObject go)
        {
            var sm = go.GetComponent<SaveSystem.SaveMe>();
            ISerializableComponent[] comps = sm != null ? sm.MySerializableComponents : null;
            if (comps == null || comps.Length == 0) return null;
            try
            {
                var ms = new MemoryStream();
                var w = new BinaryWriter(ms);
                int n = 0;
                w.Write((byte)0);
                for (int i = 0; i < comps.Length && i < 255; i++)
                {
                    if (comps[i] == null || (comps[i] is UnityEngine.Object o && o == null)) continue;
                    List<XmlKey> keys;
                    try { keys = comps[i].Serialize(); }
                    catch (Exception) { continue; }
                    if (keys == null || keys.Count == 0) continue;
                    var good = new List<XmlKey>();
                    foreach (XmlKey k in keys)
                        if (k != null && k.Key != null && k.referenceName == null && k.XmlObj == null && k.XmlListObj == null && TypeCode(k.Value) >= 0)
                            good.Add(k);
                    if (good.Count == 0) continue;
                    w.Write((byte)i);
                    w.Write((ushort)good.Count);
                    foreach (XmlKey k in good)
                    {
                        w.Write(k.Key);
                        int tc = TypeCode(k.Value);
                        w.Write((byte)tc);
                        switch (tc)
                        {
                            case 0: w.Write((string)k.Value); break;
                            case 1: w.Write((int)k.Value); break;
                            case 2: w.Write((ulong)k.Value); break;
                            case 3: w.Write((bool)k.Value); break;
                            case 4: w.Write((float)k.Value); break;
                            case 5: var q = (XmlQuaternion)k.Value; w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); break;
                            case 6: WriteV3(w, (Vector3)k.Value); break;
                        }
                    }
                    n++;
                }
                if (n == 0 || ms.Length > MaxStateBytes) return null;
                w.Flush();
                byte[] data = ms.ToArray();
                data[0] = (byte)n;
                return data;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Object sync: could not read component data of " + go.name + ": " + ex.Message);
                return null;
            }
        }

        private static int TypeCode(object v)
        {
            switch (v)
            {
                case string _: return 0;
                case int _: return 1;
                case ulong _: return 2;
                case bool _: return 3;
                case float _: return 4;
                case XmlQuaternion _: return 5;
                case Vector3 _: return 6;
                default: return -1;
            }
        }

        private static void ApplyState(GameObject go, byte[] data)
        {
            var sm = go.GetComponent<SaveSystem.SaveMe>();
            ISerializableComponent[] comps = sm != null ? sm.MySerializableComponents : null;
            if (comps == null) return;
            try
            {
                var r = new BinaryReader(new MemoryStream(data));
                int n = r.ReadByte();
                for (int c = 0; c < n; c++)
                {
                    int idx = r.ReadByte();
                    int count = r.ReadUInt16();
                    var keys = new List<XmlKey>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var k = new XmlKey { Key = r.ReadString() };
                        switch (r.ReadByte())
                        {
                            case 0: k.Value = r.ReadString(); break;
                            case 1: k.Value = r.ReadInt32(); break;
                            case 2: k.Value = r.ReadUInt64(); break;
                            case 3: k.Value = r.ReadBoolean(); break;
                            case 4: k.Value = r.ReadSingle(); break;
                            case 5: k.Value = new XmlQuaternion { x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle(), w = r.ReadSingle() }; break;
                            case 6: k.Value = ReadV3(r); break;
                            default: return; // unknown type: stream is unreadable from here
                        }
                        keys.Add(k);
                    }
                    if (idx >= comps.Length || comps[idx] == null) continue;
                    try
                    {
                        comps[idx].Deserialize(keys);
                        comps[idx].AfterDeserialize();
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"Object sync: component data of {go.name} #{idx} not applied: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Object sync: bad component data for " + go.name + ": " + ex.Message);
            }
        }

        [HarmonyPatch]
        private static class PatchSaveMeAdded
        {
            private static MethodBase Target => AccessTools.Method(typeof(CheckPointManagerBase), nameof(CheckPointManagerBase.Add), new[] { typeof(SaveSystem.SaveMe) });
            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Object sync: CheckPointManagerBase.Add(SaveMe) not found, objects created by game scripts are not synced");
                return false;
            }
            private static MethodBase TargetMethod() => Target;
            private static void Postfix(SaveSystem.SaveMe saveMe) => OnSaveMeAdded(saveMe);
        }

        private static void SendDestroy(string key, string sid, Vector3 pos)
        {
            var ms = Begin(SubDestroy, out var w);
            w.Write(Net.LocalId);
            w.Write(key);
            w.Write(sid ?? "");
            WriteV3(w, pos);
            Bcast(ms.ToArray(), true);
        }

        // ---------------------------------------------------------------- capture window

        private static bool OpsEnabled => Net.Active && Ready && WorldReady();

        private static void BeginOp()
        {
            if (_opDepth++ > 0 || !OpsEnabled || !SaveSystem.Singleton<CheckPointManagerBase>.IsInstanced()) return;
            _op = new PendingOp
            {
                Frame = Time.frameCount,
                SaveCount = SaveSystem.Singleton<CheckPointManagerBase>.Instance.ToSave.Count,
            };
        }

        private static void AddInvolved(GameObject go)
        {
            if (_op == null || go == null) return;
            Entry e = EntryOf(go);
            if (e == null) return;
            foreach (Involved x in _op.Objects) if (x.Go == go) return;
            _op.Objects.Add(new Involved { Go = go, Key = e.Key, Sid = e.Sid, Pos = go.transform.position });
            BucketStack bs = go.GetComponent<BucketStack>();
            if (bs != null) _op.Stacks.Add(new KeyValuePair<BucketStack, int>(bs, bs.BucketCount));
        }

        private static void EndOp()
        {
            if (--_opDepth > 0) return;
            _opDepth = 0;
            PendingOp op = _op;
            _op = null;
            if (op == null || !SaveSystem.Singleton<CheckPointManagerBase>.IsInstanced()) return;
            // New saved objects (SaveMe.Awake appends to ToSave): key them right away, so a Grab of the
            // bucket BucketStack.Detach puts in the hand already names it.
            List<SaveSystem.SaveMe> list = SaveSystem.Singleton<CheckPointManagerBase>.Instance.ToSave;
            for (int i = op.SaveCount; i < list.Count; i++)
            {
                SaveSystem.SaveMe sm = list[i];
                if (sm == null) continue;
                GameObject g = sm.gameObject;
                if (!Syncable(g) || ByInstance.ContainsKey(g.GetInstanceID()) || string.IsNullOrEmpty(sm.GetSaveID())) continue;
                RegisterObject(g, NewLocalKey());
                op.Created.Add(g);
            }
            if (op.Objects.Count > 0 || op.Created.Count > 0) PendingOps.Add(op);
        }

        /// <summary>One frame after the game ran: destroyed objects are gone now. Announce the result.</summary>
        private static void FinalizeOps()
        {
            if (PendingOps.Count == 0) return;
            for (int i = 0; i < PendingOps.Count; i++)
            {
                PendingOp op = PendingOps[i];
                if (op.Frame >= Time.frameCount) continue; // Object.Destroy has not run yet
                PendingOps.RemoveAt(i--);
                SendOp(op);
            }
        }

        private static void SendOp(PendingOp op)
        {
            var destroyed = new List<Involved>();
            foreach (Involved x in op.Objects) if (x.Go == null) destroyed.Add(x);
            var created = new List<GameObject>();
            foreach (GameObject g in op.Created) if (g != null && KeyOf(g) != null) created.Add(g);
            var sizes = new List<KeyValuePair<string, int>>();
            foreach (var kv in op.Stacks)
            {
                if (kv.Key == null || kv.Key.BucketCount == kv.Value) continue;
                string k = KeyOf(kv.Key.gameObject);
                if (k != null) sizes.Add(new KeyValuePair<string, int>(k, kv.Key.BucketCount));
            }
            if (destroyed.Count == 0 && created.Count == 0 && sizes.Count == 0) return; // refused (hint shown)

            var ms = Begin(SubStackOp, out var w);
            w.Write(Net.LocalId);
            w.Write((byte)Mathf.Min(255, destroyed.Count));
            for (int i = 0; i < destroyed.Count && i < 255; i++)
            {
                w.Write(destroyed[i].Key);
                w.Write(destroyed[i].Sid ?? "");
                WriteV3(w, destroyed[i].Pos);
            }
            w.Write((byte)Mathf.Min(255, created.Count));
            for (int i = 0; i < created.Count && i < 255; i++)
            {
                GameObject g = created[i];
                Entry e = EntryOf(g);
                w.Write(e.Key);
                w.Write(e.Sid);
                WriteV3(w, g.transform.position);
                WriteQ(w, g.transform.rotation);
                BucketStack bs = g.GetComponent<BucketStack>();
                w.Write((short)(bs != null ? bs.BucketCount : -1));
            }
            w.Write((byte)Mathf.Min(255, sizes.Count));
            for (int i = 0; i < sizes.Count && i < 255; i++)
            {
                w.Write(sizes[i].Key);
                w.Write((short)sizes[i].Value);
            }
            Bcast(ms.ToArray(), true);

            foreach (Involved x in destroyed)
                if (ByKey.TryGetValue(x.Key, out Entry dead)) Forget(dead); // no Destroy echo from the scan
            foreach (GameObject g in created) ClaimLocal(g); // the creator streams it / sends its rest pose
            if (LogEvent(EvStack)) Plugin.Log.LogInfo($"Object sync: stack op: -{destroyed.Count} +{created.Count} ~{sizes.Count}");
        }

        private static void ReceiveStackOp(BinaryReader r)
        {
            ulong sender = r.ReadUInt64();
            bool self = sender == Net.LocalId;
            int nd = r.ReadByte();
            for (int i = 0; i < nd; i++)
            {
                string key = r.ReadString();
                string sid = r.ReadString();
                Vector3 pos = ReadV3(r);
                if (!self) DestroyKeyed(key, sid, pos);
            }
            int nc = r.ReadByte();
            for (int i = 0; i < nc; i++)
            {
                string key = r.ReadString();
                string sid = r.ReadString();
                Vector3 pos = ReadV3(r);
                Quaternion rot = ReadQ(r);
                int stack = r.ReadInt16();
                var s = new Snap { Pos = pos, Rot = rot };
                if (!self && Valid(ref s)) CreateKeyed(sid, s.Pos, s.Rot, key, stack);
            }
            int nk = r.ReadByte();
            for (int i = 0; i < nk; i++)
            {
                string key = r.ReadString();
                int count = r.ReadInt16();
                if (!self) SetStackCount(Find(key), count);
            }
        }

        // ---------------------------------------------------------------- Harmony: stack windows

        [HarmonyPatch]
        private static class PatchBucketCreateStack
        {
            private static MethodBase Target => AccessTools.Method(typeof(Bucket), nameof(Bucket.CreateStack), new[] { typeof(GameObject) });
            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Object sync: Bucket.CreateStack not found, bucket stacking is not synced");
                return false;
            }
            private static MethodBase TargetMethod() => Target;
            private static void Prefix(Bucket __instance, GameObject pickedObj)
            {
                BeginOp();
                AddInvolved(__instance.gameObject);
                AddInvolved(pickedObj);
            }
            private static void Finalizer() => EndOp();
        }

        [HarmonyPatch]
        private static class PatchBucketSlotAttach
        {
            private static MethodBase Target => AccessTools.Method(typeof(BucketSlot), nameof(BucketSlot.Attach), new[] { typeof(GameObject) });
            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Object sync: BucketSlot.Attach not found, adding to bucket stacks is not synced");
                return false;
            }
            private static MethodBase TargetMethod() => Target;
            private static void Prefix(BucketSlot __instance, GameObject pickedObj)
            {
                BeginOp();
                if (__instance.stack != null) AddInvolved(__instance.stack.gameObject);
                AddInvolved(pickedObj);
            }
            private static void Finalizer() => EndOp();
        }

        [HarmonyPatch]
        private static class PatchBucketStackDetach
        {
            private static MethodBase Target => AccessTools.Method(typeof(BucketStack), nameof(BucketStack.Detach), Type.EmptyTypes);
            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Object sync: BucketStack.Detach not found, taking buckets off a stack is not synced");
                return false;
            }
            private static MethodBase TargetMethod() => Target;
            private static void Prefix(BucketStack __instance)
            {
                BeginOp();
                AddInvolved(__instance.gameObject);
            }
            private static void Finalizer() => EndOp();
        }

        [HarmonyPatch]
        private static class PatchBucketStackCombine
        {
            private static MethodBase Target => AccessTools.Method(typeof(BucketStack), nameof(BucketStack.Combine), new[] { typeof(BucketStack) });
            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Object sync: BucketStack.Combine not found, combining bucket stacks is not synced");
                return false;
            }
            private static MethodBase TargetMethod() => Target;
            private static void Prefix(BucketStack __instance, BucketStack stackPicked)
            {
                BeginOp();
                AddInvolved(__instance.gameObject);
                if (stackPicked != null) AddInvolved(stackPicked.gameObject);
            }
            private static void Finalizer() => EndOp();
        }

        // =====================================================================================
        // Harmony: Holdable.Attach / BucketHolder.AttachObject drop whatever the LOCAL player holds
        // (GravityGun.DropObject). While a remote attach is applied that must not happen.
        // =====================================================================================

        [HarmonyPatch]
        private static class PatchGravityGunDrop
        {
            private static MethodBase Target => AccessTools.Method(typeof(GravityGun), nameof(GravityGun.DropObject), new[] { typeof(bool) });

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Object sync: GravityGun.DropObject(bool) not found, a remote attach may drop the local held object");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static bool Prefix() => !ApplyingHolderChange;
        }

        // Same for GravityGun.PickObject: RopeTrommel.Detach puts the drum in the hands of whoever runs it,
        // so another player's detach would hand it to the local player (dropping what they hold).
        [HarmonyPatch]
        private static class PatchGravityGunPickOnRemoteHolder
        {
            private static MethodBase Target => AccessTools.Method(typeof(GravityGun), nameof(GravityGun.PickObject), new[] { typeof(GameObject), typeof(bool) });

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Object sync: GravityGun.PickObject not found, a remote detach may put an item in the local player's hands");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static bool Prefix() => !ApplyingHolderChange;
        }

        // =====================================================================================
        // Harmony: block grabbing an object that someone else is holding.
        // =====================================================================================

        [HarmonyPatch]
        private static class PatchGravityGunPick
        {
            private static MethodBase Target => AccessTools.Method(typeof(GravityGun), "PickOutlinedObject");

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Object sync: GravityGun.PickOutlinedObject not found, remote-held objects can be grabbed");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static bool Prefix(GravityGun __instance)
            {
                return !(Net.Active && IsRemotelyHeld(__instance.ObjectHit));
            }
        }

        [HarmonyPatch]
        private static class PatchToolGunPick
        {
            private static MethodBase Target => AccessTools.Method(typeof(ToolGun), "PickOutlinedObject");

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Object sync: ToolGun.PickOutlinedObject not found, remote-held tools can be grabbed");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static bool Prefix(ToolGun __instance)
            {
                return !(Net.Active && IsRemotelyHeld(__instance.ObjectHit));
            }
        }

        // =====================================================================================
        // Helpers (Begin/WriteV3/... shared with PassengerSync and BedSync)
        // =====================================================================================

        // ---------------------------------------------------------------- traffic budget / stats
        // Budget per 20 Hz send tick (caps a busy scene): at most 4 pose packets (~90 objects, ~4.6 KB),
        // 16 rest poses and 16 proximity / motion claims. Worst case per player ~80 unreliable packets/s
        // (~92 KB/s) plus ~640 small reliable messages/s; a typical scene with ~10 moving objects sends
        // ~20 pose packets/s (~12 KB/s). The host relays every client message to the other clients.
        private const int MaxPosePacketsPerTick = 4;
        private const int MaxRestsPerTick = 16;
        private const int MaxClaimsPerTick = 16;
        private static int _claimBudget = MaxClaimsPerTick;

        private static int _statRel, _statUnrel, _statRecv;
        private static long _statBytes, _statRecvBytes;
        private static float _statSince = -1f;

        private static void CountSent(byte[] data, bool reliable)
        {
            if (reliable) _statRel++;
            else _statUnrel++;
            _statBytes += data.Length;
        }

        private static void Bcast(byte[] data, bool reliable)
        {
            if (!Net.Active) return;
            CountSent(data, reliable);
            Net.Broadcast(data, reliable);
        }

        // Per-event log lines (attach / detach, creates, merges, stack ops) are written for the first
        // EventLogLines of each kind per session; later ones are only counted in the minute statistics,
        // so a 3-hour session does not fill the BepInEx log with routine events.
        private const int EvAttach = 0, EvCreate = 1, EvSimCreate = 2, EvMerge = 3, EvStack = 4, EvKinds = 5;
        private const int EventLogLines = 20;
        private static readonly int[] _eventLogged = new int[EvKinds];
        private static int _eventMuted, _eventSession = -1;

        /// <summary>True if this event of <paramref name="kind"/> should get its own log line.</summary>
        private static bool LogEvent(int kind)
        {
            if (_eventSession != Net.SessionId)
            {
                _eventSession = Net.SessionId;
                Array.Clear(_eventLogged, 0, _eventLogged.Length);
                _eventMuted = 0;
            }
            if (_eventLogged[kind] < EventLogLines)
            {
                if (++_eventLogged[kind] == EventLogLines)
                    Plugin.Log.LogInfo("Object sync: further events of this kind are only counted in the minute statistics");
                return true;
            }
            _eventMuted++;
            return false;
        }

        private static void LogStats(float now)
        {
            if (_statSince < 0f) { _statSince = now; return; }
            if (now - _statSince < 60f) return;
            if (_statRel + _statUnrel + _statRecv > 0)
                Plugin.Log.LogInfo($"Object sync: sent {_statUnrel} pose packets + {_statRel} reliable ({_statBytes / 1024} KB), " +
                                   $"received {_statRecv} ({_statRecvBytes / 1024} KB) in the last {now - _statSince:0} s; " +
                                   $"{All.Count} objects, {Owned.Count} owned, {Active.Count} followed" +
                                   (_eventMuted > 0 ? $", {_eventMuted} routine events not logged" : ""));
            _eventMuted = 0;
            _statRel = _statUnrel = _statRecv = 0;
            _statBytes = _statRecvBytes = 0;
            _statSince = now;
        }

        internal static MemoryStream Begin(byte sub, out BinaryWriter w)
        {
            var ms = Net.Writer(MsgId);
            ms.WriteByte(sub);
            w = new BinaryWriter(ms);
            return ms;
        }

        internal static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        internal static Vector3 ReadV3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        internal static void WriteQ(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        internal static Quaternion ReadQ(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        private static void WriteHalf3(BinaryWriter w, Vector3 v)
        {
            w.Write(Mathf.FloatToHalf(Mathf.Clamp(v.x, -1000f, 1000f)));
            w.Write(Mathf.FloatToHalf(Mathf.Clamp(v.y, -1000f, 1000f)));
            w.Write(Mathf.FloatToHalf(Mathf.Clamp(v.z, -1000f, 1000f)));
        }

        private static Vector3 ReadHalf3(BinaryReader r) =>
            new Vector3(Mathf.HalfToFloat(r.ReadUInt16()), Mathf.HalfToFloat(r.ReadUInt16()), Mathf.HalfToFloat(r.ReadUInt16()));

        private const float QRange = 0.70710678f; // smallest-three components lie in [-1/sqrt2, 1/sqrt2]

        /// <summary>Smallest-three quaternion in 32 bits: 2-bit index of the largest component + 3 x 10 bits.</summary>
        private static uint PackQ(Quaternion q)
        {
            float x = q.x, y = q.y, z = q.z, w = q.w;
            float mag = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (mag < 1e-6f) { x = y = z = 0f; w = 1f; } else { x /= mag; y /= mag; z /= mag; w /= mag; }
            float ax = Mathf.Abs(x), ay = Mathf.Abs(y), az = Mathf.Abs(z), aw = Mathf.Abs(w);
            uint idx = 0; float max = ax;
            if (ay > max) { idx = 1; max = ay; }
            if (az > max) { idx = 2; max = az; }
            if (aw > max) { idx = 3; }
            float a, b, c, big;
            switch (idx)
            {
                case 0: big = x; a = y; b = z; c = w; break;
                case 1: big = y; a = x; b = z; c = w; break;
                case 2: big = z; a = x; b = y; c = w; break;
                default: big = w; a = x; b = y; c = z; break;
            }
            if (big < 0f) { a = -a; b = -b; c = -c; }
            return idx << 30 | Q10(a) << 20 | Q10(b) << 10 | Q10(c);
        }

        private static uint Q10(float v) =>
            (uint)Mathf.Clamp(Mathf.RoundToInt((v + QRange) / (2f * QRange) * 1023f), 0, 1023);

        private static float D10(uint n) => n / 1023f * (2f * QRange) - QRange;

        private static Quaternion UnpackQ(uint p)
        {
            uint idx = p >> 30;
            float a = D10((p >> 20) & 1023), b = D10((p >> 10) & 1023), c = D10(p & 1023);
            float big = Mathf.Sqrt(Mathf.Max(0f, 1f - a * a - b * b - c * c));
            switch (idx)
            {
                case 0: return new Quaternion(big, a, b, c);
                case 1: return new Quaternion(a, big, b, c);
                case 2: return new Quaternion(a, b, big, c);
                default: return new Quaternion(a, b, c, big);
            }
        }

        private static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
            !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
    }
}
