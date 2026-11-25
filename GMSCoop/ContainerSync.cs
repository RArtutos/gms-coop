using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using GoldDigger;
using HarmonyLib;
using SaveSystem;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Material (dirt, mud, water, gold, gems, magnetite) inside the economy chain: hand buckets,
    /// miner's moss mats, sluice boxes, shakers, wash plant counters, conveyors, hog pan boxes, mat
    /// scrubbers, wave tables, magnetite separator/trailer, ... and machine buckets / truck beds
    /// (DiggingController). (Msg)14 sub 2 (delta) / sub 3 (state) / sub 10 (full-state request) / sub 25 (loaded areas).
    ///
    /// Containers are found by reflection: every MonoBehaviour type of the game that has a ResourceBag
    /// field (plus MagnetiteTrailer). Their state = every ResourceBag field plus the float fields whose
    /// names describe contents (volume, mud, dirt, ground, gold, ...; tuning values like Max/Speed/Ratio
    /// are left out) plus HasMagnetite. DiggingController (a machine Controller, not a component) is
    /// handled the same way with Minerals and _currShovelTotalVolume.
    /// Not containers: player tools (the hand shovel belongs to each PC's own player), gold pans and
    /// their PanningVolume particles (a pan's contents ARE its local particles; the aggregate fields are
    /// recomputed from them, so the material exists exactly once, on the PC that filled the pan), dirt
    /// clods (DirtSync), anything under the local Player.
    ///
    /// CONTENTS AUTHORITY: the HOST, for every container (machine buckets included). It broadcasts the
    /// absolute state (10 Hz while it changes) plus, per player stream, the last delta it applied.
    /// Every other PC shows that state plus its own unacknowledged deltas (prediction). There is no
    /// ownership handover, so a change can never be lost while a driver gets in or out.
    /// A client sends DELTAS for what happens on its PC: its own digging (bucket fills), pouring a bucket
    /// into a wave table, clods it dropped landing in a hopper or truck, the processing of a machine it
    /// drives, ... The host adds them.
    ///
    /// SIMULATION AUTHORITY (who runs the processing): world machines = the host, but in a streamed area the
    /// host does not have loaded (the game deactivates it there) the client with the lowest id that has it
    /// loaded (sub 25 area lists, SyncCore.AreaAuthority: host preferred, handover with a pause, never two
    /// at once); its contents changes reach the host as deltas, its stock changes as ledger deltas. Machines
    /// with a MachineSync id (and processors mounted on them, e.g. the Frankenstein belt) = the PC whose
    /// player drives it, the host while nobody does. On every other PC the processing has no effect:
    ///  - Update / FixedUpdate / LateUpdate / UpdateRare of every non-holdable container type, plus the
    ///    processors without own contents (ConveyorElevator, HogPan's automatic mat swap) are wrapped:
    ///    contents of containers within 30 m are saved before and restored after the call, stock changes
    ///    inside are skipped (GoldSync.SuppressDepth), dirt clods it would spawn are not created (the
    ///    simulating PC's clods arrive as visual twins, DirtSync), and repeated hints are throttled.
    ///  - A machine this PC does not simulate does not dig terrain (DigWithBox / TryMove / SoftenTerrain /
    ///    ShovelAddHeights blocked inside its DiggingController / DiggingShovelController update), does
    ///    not drop dirt clods (CreateVirtualDirt) and does not dump its bed (DumpTruckDirt.CheckThrowOut).
    ///    Its bucket/bed visuals follow the synced contents.
    /// Player-triggered processing (opening the magnetite trailer valve) runs where the player is; its
    /// stock and contents changes are deltas like any other local change.
    ///
    /// Identity: ObjectSync key, else "m{MachineSync id}", else the SaveMe ordinal (save id + index in
    /// CheckPointManager.ToSave, the scheme ObjectSync uses) for movable objects, else save id + position
    /// for fixed ones; plus the index of the container among the containers below that root. The index is
    /// rebuilt when ToSave changes, when a machine/object id changes (re-index for a joining player) and
    /// every 60 s.
    /// </summary>
    internal static class ContainerSync
    {
        private const float SendInterval = 0.1f;
        private const float IndexInterval = 60f;
        private const float PendTimeout = 5f;
        private const float IndexSettle = 2f;
        private const float WrapRadius = 30f;
        private const float AckKeep = 30f;
        private const float HintThrottle = 15f;
        private const float Eps = 1e-6f;

        // ================================================================== definitions

        /// <summary>Field access without boxing (FieldRef), reflection only as a fallback.</summary>
        private sealed class Acc<F>
        {
            public readonly FieldInfo Info;
            private readonly AccessTools.FieldRef<object, F> _ref;

            public Acc(FieldInfo f)
            {
                Info = f;
                try { _ref = AccessTools.FieldRefAccess<F>(f.DeclaringType, f.Name); }
                catch (Exception) { _ref = null; }
            }

            public F Get(object o) => _ref != null ? _ref(o) : (F)Info.GetValue(o);

            public void Set(object o, F v)
            {
                if (_ref != null) _ref(o) = v;
                else Info.SetValue(o, v);
            }
        }

        private sealed class Def
        {
            public Type T;
            public readonly List<Acc<float>> Floats = new List<Acc<float>>();
            public readonly List<Acc<ResourceBag>> Bags = new List<Acc<ResourceBag>>();
            public Acc<bool> Flag; // HasMagnetite
            public readonly List<FastInvokeHandler> Refresh = new List<FastInvokeHandler>(); // compiled: no reflection per call
            public bool Holdable;
            public int Size => Floats.Count + Bags.Count * BagLen + (Flag != null ? 1 : 0);
        }

        private static readonly int BagLen = Enum.GetValues(typeof(ResourceType)).Length;
        private static readonly Regex Content = new Regex("volume|mud|dirt|ground|gold|diamond|magnet|mineral|fill|water|rent|inside|added|stack", RegexOptions.IgnoreCase);
        private static readonly Regex Tuning = new Regex("max|min|speed|ratio|multipl|delay|time|scale|radius|capacity|density|threshold|rate|perc|pct|anim|sound|particle|cheat|preview|cached|force|power|offset|size|delta|epsilon|limit|step|count|prev|target|_is|^is|^has|^was|^_has|^_was|param", RegexOptions.IgnoreCase);
        private static readonly string[] RefreshNames = { "UpdatePlaneAndMass", "UpdateDirt", "GroundIndicatorControl", "UpdatePlane" };

        private static Dictionary<Type, Def> _defs;
        private static Def _digDef;
        private static readonly AccessTools.FieldRef<MachineController, List<Controller>> ControllersRef =
            GoldSync.TryRef<MachineController, List<Controller>>("Controllers");
        private static readonly AccessTools.FieldRef<Controller, MachineController> OwnerRef =
            GoldSync.TryRef<Controller, MachineController>("MyMachineController");

        private static Dictionary<Type, Def> Defs
        {
            get
            {
                if (_defs == null) BuildDefs();
                return _defs;
            }
        }

        /// <summary>Types whose contents are local by design (see class summary).</summary>
        private static bool Excluded(Type t) =>
            t == typeof(VirtualVolumeDirt)
            || typeof(TerrainUtils.TerrainDeformerBase).IsAssignableFrom(t)
            || typeof(PlayerTool).IsAssignableFrom(t)
            || typeof(PanningVolume).IsAssignableFrom(t)
            || typeof(GoldPan).IsAssignableFrom(t);

        private static void BuildDefs()
        {
            _defs = new Dictionary<Type, Def>();
            Type holdable = typeof(Holdable);
            foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(WaveTable).Assembly))
            {
                if (t == null || t.IsAbstract || t.IsGenericTypeDefinition || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                if (Excluded(t)) continue;
                Def d = MakeDef(t, t == typeof(MagnetiteTrailer));
                if (d == null) continue;
                d.Holdable = holdable.IsAssignableFrom(t);
                _defs[t] = d;
            }
            // _stackedVolume (removed from the bucket, not yet dropped as a clod) is a transient of the
            // dropping PC: syncing it could change the size of the next clod.
            _digDef = MakeDef(typeof(DiggingController), true, new[] { "Minerals", "_currShovelTotalVolume" });
            Plugin.Log.LogInfo($"Container sync: {_defs.Count} container types");
        }

        private static Def MakeDef(Type t, bool force, string[] only = null)
        {
            var d = new Def { T = t };
            var seen = new HashSet<string>();
            for (Type c = t; c != null && c != typeof(MonoBehaviour) && c != typeof(object); c = c.BaseType)
            {
                foreach (FieldInfo f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral || f.IsInitOnly || !seen.Add(f.Name)) continue;
                    if (only != null)
                    {
                        if (Array.IndexOf(only, f.Name) < 0) continue;
                        if (f.FieldType == typeof(ResourceBag)) d.Bags.Add(new Acc<ResourceBag>(f));
                        else if (f.FieldType == typeof(float)) d.Floats.Add(new Acc<float>(f));
                        continue;
                    }
                    if (f.FieldType == typeof(ResourceBag)) d.Bags.Add(new Acc<ResourceBag>(f));
                    else if (f.FieldType == typeof(float) && Content.IsMatch(f.Name) && !Tuning.IsMatch(f.Name)) d.Floats.Add(new Acc<float>(f));
                    else if (f.FieldType == typeof(bool) && f.Name == "HasMagnetite") d.Flag = new Acc<bool>(f);
                }
            }
            if (d.Bags.Count == 0 && !force) return null;
            if (d.Size == 0) return null;
            foreach (string n in RefreshNames)
            {
                // Quiet lookup (AccessTools.Method logs a warning for every type that lacks the name).
                MethodInfo m = null;
                for (Type c = t; m == null && c != null && c != typeof(MonoBehaviour); c = c.BaseType)
                    m = GoldSync.Find(c, n, Type.EmptyTypes);
                if (m == null || m.IsAbstract) continue;
                try { d.Refresh.Add(MethodInvoker.GetHandler(m)); }
                catch (Exception ex) { Plugin.Log.LogWarning($"Container sync: refresh {t.Name}.{n} unavailable: {ex.Message}"); }
            }
            return d;
        }

        // ================================================================== entries

        private sealed class Entry
        {
            public string Key;
            public object Obj;            // Component, or DiggingController
            public Def D;
            public Transform Tr;          // for distance
            public MachineController Machine; // DiggingController's machine
            public MachineController SimMachine; // machine whose driver simulates this container (or null)
            public int MachineId = -1;    // id the key was built with ("m" roots)
            public GameObject ObjRoot;    // ObjectSync root the key was built with ("o" roots)
            public string ObjKey;
            public float[] Exp;           // expected values (client)
            public float[] Acc;           // captured, not yet sent
            public readonly List<ContPend> Pend = new List<ContPend>();
            public float[] Sent;          // host: last broadcast
            public float[] Tmp;
            public float[] DigSaved;      // PatchDiggingUpdate's restore buffer (no allocation per frame)
        }

        private struct AckInfo
        {
            public uint Seq;
            public float At;
        }

        private static readonly Dictionary<string, Entry> ByKey = new Dictionary<string, Entry>();
        private static readonly Dictionary<object, Entry> ByObj = new Dictionary<object, Entry>();
        private static readonly List<Entry> All = new List<Entry>();
        private static readonly Dictionary<ulong, AckInfo> AuthAcks = new Dictionary<ulong, AckInfo>();
        private static bool _acksDirty, _sendAll;
        private static uint _seq;
        private static float _nextSend, _nextIndex = -1f, _readySince = -1f, _nextStaleCheck;
        private static int _objKeyVersion = -1, _world = -1;
        private static bool _indexDirty;
        // Machines that appeared and have no MachineSync id yet (their bucket is indexed once they have one).
        private static readonly List<KeyValuePair<MachineController, float>> NewMachines = new List<KeyValuePair<MachineController, float>>();
        private static float _lastRebuild = -100f;
        private const float DirtyRebuildGap = 1f; // new containers are indexed within this time
        private static bool _loggedDup;
        private static bool _requested; // this sync session asked the host for every state

        /// <summary>
        /// The world this index was built for is gone (load, scene change) or not shared any more: forget
        /// every container object (and the client's predictions on them). Session state (the host's
        /// acknowledgements per player stream) is separate: see ResetSession.
        /// </summary>
        private static void ResetIndex()
        {
            ByKey.Clear();
            ByObj.Clear();
            All.Clear();
            WrapCache.Clear();
            HintTimes.Clear();
            _nextIndex = -1f;
            _readySince = -1f;
            _indexDirty = false;
            NewMachines.Clear();
            _requested = false;
            _sendAll = false;
            ForeignDepth = 0;
            BlockDig = 0;
            Areas.Clear(); // a new world: lists are announced again (the host's must be heard before taking over)
            _nextAreaSend = 0f;
            _nextAreaCheck = 0f;
        }

        /// <summary>The session ended: index and acknowledgements.</summary>
        internal static void ResetSession()
        {
            ResetIndex();
            AuthAcks.Clear();
            _acksDirty = false;
            _wasSynced = false;
            _world = -1;
        }

        /// <summary>Host: re-send every state (a player just loaded the world).</summary>
        internal static void SendAll() => _sendAll = true;

        private static void Read(Entry e, float[] into)
        {
            int k = 0;
            object o = e.Obj;
            List<Acc<float>> floats = e.D.Floats;
            for (int j = 0; j < floats.Count; j++) into[k++] = floats[j].Get(o);
            List<Acc<ResourceBag>> bags = e.D.Bags;
            for (int j = 0; j < bags.Count; j++)
            {
                float[] data = GoldSync.BagArray(bags[j].Get(o));
                for (int i = 0; i < BagLen; i++) into[k++] = data != null && i < data.Length ? data[i] : 0f;
            }
            if (e.D.Flag != null) into[k] = e.D.Flag.Get(o) ? 1f : 0f;
        }

        private static void Write(Entry e, float[] v)
        {
            int k = 0;
            object o = e.Obj;
            foreach (Acc<float> f in e.D.Floats) f.Set(o, v[k++]);
            foreach (Acc<ResourceBag> f in e.D.Bags)
            {
                ResourceBag bag = f.Get(o);
                if (bag == null) { bag = new ResourceBag(); f.Set(o, bag); }
                float[] data = GoldSync.BagArray(bag);
                for (int i = 0; i < BagLen; i++)
                {
                    if (data != null && i < data.Length) data[i] = Mathf.Max(0f, v[k]);
                    k++;
                }
            }
            if (e.D.Flag != null) e.D.Flag.Set(o, v[k] > 0.5f);
            List<FastInvokeHandler> refresh = e.D.Refresh;
            for (int j = 0; j < refresh.Count; j++)
            {
                try { refresh[j](e.Obj, NoArgs); }
                catch (Exception) { /* visual refresh only */ }
            }
            if (e.Obj is DiggingController dc && !MachineAuthority(e.Machine)) RefreshDigVisual(dc);
        }

        private static readonly object[] NoArgs = new object[0];

        /// <summary>
        /// Drops pending deltas the host never acknowledged although it kept talking for the timeout after
        /// they were sent (it did not know the container / was loading). A stalled link expires nothing.
        /// </summary>
        private static void ExpirePend(List<ContPend> pend) => Predict.Expire(pend, GoldSync.Host, PendTimeout);

        private static bool Alive(Entry e) => e.Obj is UnityEngine.Object o ? o != null : e.Machine != null;

        // ================================================================== authority

        /// <summary>The local player drives <paramref name="m"/> itself (a passenger never counts, also not during the lag grace).</summary>
        internal static bool LocalDrives(MachineController m) => MachineSync.LocallyDriven(m);

        /// <summary>
        /// True if this PC runs the simulation (digging, dumping, processing) of this machine. One source of
        /// truth for every area: MachineSync.IsAuthority (local driver; else the remote driver's PC; else the
        /// host, also while the last driver is getting out).
        /// </summary>
        internal static bool MachineAuthority(MachineController m) =>
            !GoldSync.Synced() || m == null || MachineSync.IsAuthority(m);

        /// <summary>
        /// True if this PC runs the processing of a world/machine-mounted component: a machine's = its
        /// driver's PC (MachineAuthority); a world object's = the host, or, in a streamed area the host does not
        /// have loaded, the client that has it loaded (AreaAuthority).
        /// </summary>
        private static bool SimAuthority(MachineController machine, string area)
        {
            if (!GoldSync.Synced()) return true;
            if (machine != null && MachineSync.IdOf(machine) >= 0) return MachineAuthority(machine);
            if (!Net.IsHost && !SaveTransfer.CaughtUp) return false;
            return Areas.IsMine(area, Time.unscaledTime);
        }

        // ================================================================== processing authority per area

        internal const byte SubAreas = 25; // every PC -> all: author u64, isHost bool, count u16, scene names
        private static readonly AreaAuthority Areas = new AreaAuthority();
        private static readonly List<string> AreaTmp = new List<string>();
        private static float _nextAreaCheck, _nextAreaSend;

        /// <summary>
        /// The area's objects run on this PC: its saved objects (under GameplayObjects, deactivated by
        /// CheckpointManager.DisableObjectsOnScene when the area unloads) are active; an area without that
        /// root counts as loaded while its scene is.
        /// </summary>
        private static bool AreaLoaded(AreaStreamer a) =>
            a.GameplayObjects != null
                ? a.GameplayObjects.gameObject.activeInHierarchy && !a.unloading
                : UnityEngine.SceneManagement.SceneManager.GetSceneByName(a.sceneName).isLoaded;

        /// <summary>Streamed area (AreaStreamer scene name) a world object belongs to; null = not streamed (always the host's).</summary>
        private static string AreaOf(Transform t)
        {
            List<AreaStreamer> list = AreaStreamer.Instances;
            if (t == null || list == null) return null;
            string scene = t.gameObject.scene.name;
            foreach (AreaStreamer a in list) if (a != null && a.sceneName == scene) return a.sceneName;
            for (Transform p = t; p != null; p = p.parent)
                foreach (AreaStreamer a in list) if (a != null && a.GameplayObjects == p) return a.sceneName;
            return null;
        }

        /// <summary>Announces this PC's loaded areas when they change (and every 10 s, for players that join).</summary>
        private static void AreaTick()
        {
            float now = Time.unscaledTime;
            if (now < _nextAreaCheck) return;
            _nextAreaCheck = now + 0.25f;
            if (!Net.IsHost && !SaveTransfer.CaughtUp) return; // the answers would be dropped before the catch-up
            Areas.MyId = Net.LocalId;
            Areas.IAmHost = Net.IsHost;
            AreaTmp.Clear();
            List<AreaStreamer> list = AreaStreamer.Instances;
            if (list != null)
                foreach (AreaStreamer a in list)
                    if (a != null && !string.IsNullOrEmpty(a.sceneName) && AreaLoaded(a) && !AreaTmp.Contains(a.sceneName)) AreaTmp.Add(a.sceneName);
            bool changed = Areas.SetMine(AreaTmp, now);
            Areas.Expire(now);
            if (!changed && now < _nextAreaSend) return;
            _nextAreaSend = now + 10f;
            Areas.MineInto(AreaTmp);
            var ms = GoldSync.Begin(SubAreas, out BinaryWriter w);
            w.Write(Net.LocalId);
            w.Write(Net.IsHost);
            int n = Mathf.Min(AreaTmp.Count, 1000);
            w.Write((ushort)n);
            for (int i = 0; i < n; i++) w.Write(AreaTmp[i]);
            TrafficC.Broadcast(TrafficC.Cat.Containers, ms.ToArray(), true);
        }

        internal static void ReceiveAreas(BinaryReader r)
        {
            ulong author = r.ReadUInt64();
            bool isHost = r.ReadBoolean();
            int n = r.ReadUInt16();
            var areas = new List<string>(n);
            for (int i = 0; i < n; i++) areas.Add(r.ReadString());
            if (!GoldSync.Synced() || author == Net.LocalId) return;
            Areas.MyId = Net.LocalId;
            Areas.IAmHost = Net.IsHost;
            if (Areas.SetPeer(author, isHost, areas, Time.unscaledTime))
                _nextAreaSend = 0f; // a PC we did not know: it needs our list too (next tick)
        }

        // ================================================================== index

        private static void MaintainIndex()
        {
            // A new world (scene change, reload of the same scene, save load): no container object of the
            // old one may be written any more (GameState.WorldVersion).
            int world = GameState.WorldVersion;
            if (world != _world) { _world = world; ResetIndex(); }
            if (!SaveSystem.Singleton<CheckPointManagerBase>.IsInstanced()) return;
            var cpm = SaveSystem.Singleton<CheckPointManagerBase>.Instance;
            float now = Time.unscaledTime;
            if (_readySince < 0f) _readySince = now;
            if (now - _readySince < IndexSettle) return;
            bool stale = false;
            if (now >= _nextStaleCheck)
            {
                _nextStaleCheck = now + 1f;
                stale = ObjectSync.KeyVersion != _objKeyVersion || AnyStale();
                for (int i = NewMachines.Count - 1; i >= 0; i--)
                {
                    MachineController m = NewMachines[i].Key;
                    bool gone = m == null || now - NewMachines[i].Value > 30f;
                    if (!gone && MachineSync.IdOf(m) < 0) continue;
                    if (!gone) _indexDirty = true;
                    NewMachines.RemoveAt(i);
                }
            }
            // A new saved object with containers (bought, spawned) marks the index dirty (PatchSavedAdd).
            // Dirt clods and other saved objects without containers come and go all the time while
            // digging; they no longer trigger a full scene scan.
            bool dirty = _indexDirty && now - _lastRebuild >= DirtyRebuildGap;
            if (!stale && !dirty && now < _nextIndex) return;
            _nextIndex = now + IndexInterval;
            _indexDirty = false;
            _lastRebuild = now;
            _objKeyVersion = ObjectSync.KeyVersion;
            Rebuild(cpm.ToSave);
            if (!_requested && !Net.IsHost)
            {
                // States sent while this PC was loading found no index yet: ask the host again.
                _requested = true;
                var ms = GoldSync.Begin(GoldSync.SubContRequest, out BinaryWriter w);
                w.Write(Net.LocalId);
                TrafficC.Broadcast(TrafficC.Cat.Containers, ms.ToArray(), true);
            }
        }

        /// <summary>A machine or object id a key was built from changed (re-index for a joining player).</summary>
        private static bool AnyStale()
        {
            foreach (Entry e in All)
            {
                if (e.MachineId >= 0 && MachineSync.IdOf(e.SimMachine) != e.MachineId) return true;
                if (e.ObjKey != null && (e.ObjRoot == null || ObjectSync.KeyOf(e.ObjRoot) != e.ObjKey)) return true;
            }
            return false;
        }

        private sealed class KeyInfo
        {
            public string Key;
            public MachineController Sim;
            public int MachineId = -1;
            public GameObject ObjRoot;
            public string ObjKey;
        }

        private static void Rebuild(List<SaveMe> toSave)
        {
            // SaveMe ordinals (save id + running index), as ObjectSync numbers its objects.
            var ordinal = new Dictionary<SaveMe, string>();
            var counts = new Dictionary<string, int>();
            if (toSave != null)
                foreach (SaveMe sm in toSave)
                {
                    if (sm == null) continue;
                    string sid = sm.GetSaveID();
                    if (string.IsNullOrEmpty(sid)) continue;
                    counts.TryGetValue(sid, out int k);
                    counts[sid] = k + 1;
                    ordinal[sm] = sid + "#" + k;
                }

            var found = new List<KeyValuePair<KeyInfo, object>>();
            var transforms = new Dictionary<object, Transform>();
            var machines = new Dictionary<object, MachineController>();
            var rootLists = new Dictionary<GameObject, List<Component>>();
            var keys = new HashSet<string>();
            var dups = new HashSet<string>();
            Player player = global::Singleton<Player>.IsInstanced() ? global::Singleton<Player>.Instance : null;

            // One scene query for all container types (indexing runs on load / ToSave changes / 60 s).
            // Inactive objects too: the game keeps the saved objects of an area that is not loaded on this
            // PC under its inactive AreaStreamer.GameplayObjects (CheckpointManager.DisableObjectsOnScene).
            // The host must still apply a player's deltas to them (else the material is lost and the
            // host's save misses it), and a client must still take the host's states for them.
            foreach (MonoBehaviour c in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true))
            {
                if (c == null || !Defs.ContainsKey(c.GetType())) continue;
                if (player != null && c.transform.IsChildOf(player.transform)) continue; // this PC's own player
                KeyInfo ki = KeyFor(c, ordinal, rootLists);
                if (ki == null) continue;
                if (!keys.Add(ki.Key)) { dups.Add(ki.Key); continue; }
                found.Add(new KeyValuePair<KeyInfo, object>(ki, c));
                transforms[c] = c.transform;
            }

            // Machine buckets / beds.
            if (ControllersRef != null && _digDef != null)
                foreach (MachineController m in UnityEngine.Object.FindObjectsOfType<MachineController>(true))
                {
                    int id = MachineSync.IdOf(m);
                    if (id < 0) continue;
                    List<Controller> list = ControllersRef(m);
                    if (list == null) continue;
                    int idx = 0;
                    foreach (Controller ctl in list)
                    {
                        if (!(ctl is DiggingController dc)) continue;
                        var ki = new KeyInfo { Key = "m" + id + "/d" + idx++, Sim = m, MachineId = id };
                        if (!keys.Add(ki.Key)) { dups.Add(ki.Key); continue; }
                        found.Add(new KeyValuePair<KeyInfo, object>(ki, dc));
                        transforms[dc] = m.transform;
                        machines[dc] = m;
                    }
                }

            if (dups.Count > 0 && !_loggedDup)
            {
                _loggedDup = true;
                Plugin.Log.LogWarning($"Container sync: {dups.Count} ambiguous container ids skipped");
            }

            var old = new Dictionary<string, Entry>(ByKey);
            ByKey.Clear();
            ByObj.Clear();
            All.Clear();
            foreach (var kv in found)
            {
                KeyInfo ki = kv.Key;
                if (dups.Contains(ki.Key)) continue;
                Def d = kv.Value is DiggingController ? _digDef : Defs[kv.Value.GetType()];
                if (!old.TryGetValue(ki.Key, out Entry e) || !ReferenceEquals(e.Obj, kv.Value) || e.D != d)
                {
                    e = new Entry { Key = ki.Key, Obj = kv.Value, D = d };
                    e.Exp = new float[d.Size];
                    e.Acc = new float[d.Size];
                    e.Tmp = new float[d.Size];
                    Read(e, e.Exp);
                }
                e.Tr = transforms[kv.Value];
                machines.TryGetValue(kv.Value, out e.Machine);
                e.SimMachine = ki.Sim;
                e.MachineId = ki.MachineId;
                e.ObjRoot = ki.ObjRoot;
                e.ObjKey = ki.ObjKey;
                ByKey[e.Key] = e;
                ByObj[e.Obj] = e;
                All.Add(e);
            }
            WrapCache.Clear();
        }

        private static KeyInfo KeyFor(Component c, Dictionary<SaveMe, string> ordinal, Dictionary<GameObject, List<Component>> rootLists)
        {
            GameObject root;
            var ki = new KeyInfo();
            string rootKey;
            // includeInactive: without it Unity 2020 finds nothing above an inactive object, so the
            // key would differ between a PC that has the area loaded and one that has not.
            MachineController m = c.GetComponentInParent<MachineController>(true);
            int mid = m != null ? MachineSync.IdOf(m) : -1;
            if (mid >= 0) ki.Sim = m;
            SaveMe sm = c.GetComponentInParent<SaveMe>(true);
            string objKey = sm != null ? ObjectSync.KeyOf(sm.gameObject) : null;
            if (objKey != null) { root = sm.gameObject; rootKey = "o" + objKey; ki.ObjRoot = root; ki.ObjKey = objKey; }
            else if (mid >= 0) { root = m.gameObject; rootKey = "m" + mid; ki.MachineId = mid; }
            else if (sm != null)
            {
                root = sm.gameObject;
                if (sm.GetComponent<Rigidbody>() != null && ordinal.TryGetValue(sm, out string ord)) rootKey = "s" + ord;
                else
                {
                    Vector3 p = sm.transform.position;
                    rootKey = "p" + sm.GetSaveID() + "@" + Mathf.RoundToInt(p.x * 2f) + "," + Mathf.RoundToInt(p.y * 2f) + "," + Mathf.RoundToInt(p.z * 2f);
                }
            }
            else return null; // spawned locally: not part of the shared world
            if (mid >= 0 && ki.MachineId < 0) ki.MachineId = mid;
            if (!rootLists.TryGetValue(root, out List<Component> list))
            {
                list = new List<Component>();
                foreach (Component x in root.GetComponentsInChildren<Component>(true))
                    if (x != null && Defs.ContainsKey(x.GetType())) list.Add(x);
                rootLists[root] = list;
            }
            int idx = list.IndexOf(c);
            if (idx < 0) return null;
            ki.Key = rootKey + "/" + idx;
            return ki;
        }

        /// <summary>A saved object appeared (SaveMe registers itself): re-index if it carries containers.</summary>
        [HarmonyPatch]
        private static class PatchSavedAdd
        {
            private static MethodBase TargetMethod() =>
                GoldSync.Need(typeof(CheckPointManagerBase), nameof(CheckPointManagerBase.Add), "container index of new objects", new[] { typeof(SaveMe) });

            private static bool Prepare() => TargetMethod() != null;

            private static void Postfix(SaveMe saveMe)
            {
                if (_indexDirty || saveMe == null || !_wasSynced || _defs == null) return;
                try
                {
                    if (saveMe.GetComponent<VirtualVolumeDirt>() != null) return; // dirt clod: never a container
                    MachineController mc = saveMe.GetComponentInChildren<MachineController>(true);
                    if (mc != null && NewMachines.Count < 64) NewMachines.Add(new KeyValuePair<MachineController, float>(mc, Time.unscaledTime));
                    foreach (MonoBehaviour c in saveMe.GetComponentsInChildren<MonoBehaviour>(true))
                        if (c != null && _defs.ContainsKey(c.GetType())) { _indexDirty = true; return; }
                }
                catch (Exception) { _indexDirty = true; }
            }
        }

        // ================================================================== tick

        private static bool _wasSynced;

        internal static void Tick(bool synced)
        {
            if (!synced)
            {
                if (_wasSynced) { _wasSynced = false; ResetIndex(); }
                return;
            }
            _wasSynced = true;
            // Errors go to GoldSync's rate-limited guard of this sub-system (it calls Tick inside try/catch).
            AreaTick();
            MaintainIndex();
            if (Time.unscaledTime < _nextSend && !_sendAll) return;
            _nextSend = Time.unscaledTime + SendInterval;
            Flush();
        }

        // Harmony hooks that run every frame per processor: their errors are rate-limited (log size).
        private static readonly SubsystemGuard GWrap = new SubsystemGuard("Container sync: processing wrap"),
            GElevator = new SubsystemGuard("Container sync: elevator");

        private static void Capture(Entry e)
        {
            Read(e, e.Tmp);
            Predict.Capture(e.Tmp, e.Exp, e.Acc, Eps);
        }

        /// <summary>Client: fold every local change made so far into the outgoing deltas.</summary>
        internal static void CaptureAll()
        {
            if (Net.IsHost || !GoldSync.Synced()) return;
            foreach (Entry e in All) if (Alive(e)) Capture(e);
        }

        /// <summary>
        /// Client: contents changes made since CaptureAll by host-only code (end of season, ...) are not
        /// sent: the host makes the same changes itself.
        /// </summary>
        internal static void AbsorbAll()
        {
            if (Net.IsHost || !GoldSync.Synced()) return;
            foreach (Entry e in All) if (Alive(e)) Read(e, e.Exp);
        }

        private static bool NonZero(float[] a)
        {
            foreach (float f in a) if (Mathf.Abs(f) > Eps) return true;
            return false;
        }

        private static readonly MemoryStream StateBody = new MemoryStream(), DeltaBody = new MemoryStream();
        private static readonly BinaryWriter StateW = new BinaryWriter(StateBody), DeltaW = new BinaryWriter(DeltaBody);

        /// <summary>Client: send the captured changes now (the session is about to end).</summary>
        internal static void FlushNow()
        {
            if (Net.IsHost || !GoldSync.Synced() || All.Count == 0) return;
            Flush();
        }

        private static void Flush()
        {
            int stateN = 0, deltaN = 0;
            uint seq = _seq + 1;
            // Reused bodies: nothing is allocated on the ticks where no container changed.
            MemoryStream stateBody = StateBody, deltaBody = DeltaBody;
            stateBody.SetLength(0);
            deltaBody.SetLength(0);
            BinaryWriter sw = StateW, dw = DeltaW;
            bool sendAll = _sendAll;
            _sendAll = false;
            float now = Time.unscaledTime;

            foreach (Entry e in All)
            {
                if (!Alive(e)) continue;
                if (Net.IsHost)
                {
                    Read(e, e.Tmp);
                    if (!sendAll && e.Sent != null && Same(e.Sent, e.Tmp)) continue;
                    if (e.Sent == null || e.Sent.Length != e.Tmp.Length) e.Sent = new float[e.Tmp.Length];
                    Array.Copy(e.Tmp, e.Sent, e.Tmp.Length);
                    sw.Write(e.Key);
                    WriteFloats(sw, e.Tmp);
                    stateN++;
                }
                else
                {
                    Capture(e);
                    if (e.Pend.Count > 0) ExpirePend(e.Pend);
                    if (!NonZero(e.Acc)) continue;
                    var d = (float[])e.Acc.Clone();
                    Array.Clear(e.Acc, 0, e.Acc.Length);
                    e.Pend.Add(new ContPend { Seq = seq, D = d, T = now });
                    dw.Write(e.Key);
                    WriteFloats(dw, d);
                    deltaN++;
                }
            }

            if (Net.IsHost && (stateN > 0 || _acksDirty))
            {
                _acksDirty = false;
                PruneAcks(now);
                var ms = GoldSync.Begin(GoldSync.SubContState, out BinaryWriter w);
                w.Write(Net.LocalId);
                w.Write((byte)Mathf.Min(AuthAcks.Count, 255));
                int n = 0;
                foreach (var kv in AuthAcks)
                {
                    if (n++ == 255) break;
                    w.Write(kv.Key);
                    w.Write(kv.Value.Seq);
                }
                w.Write(stateN);
                w.Write(stateBody.GetBuffer(), 0, (int)stateBody.Length);
                TrafficC.Broadcast(TrafficC.Cat.Containers, ms.ToArray(), true);
            }
            if (deltaN > 0)
            {
                _seq = seq;
                var ms = GoldSync.Begin(GoldSync.SubContDelta, out BinaryWriter w);
                w.Write(GoldSync.StreamId);
                w.Write(seq);
                w.Write(deltaN);
                w.Write(deltaBody.GetBuffer(), 0, (int)deltaBody.Length);
                TrafficC.Broadcast(TrafficC.Cat.Containers, ms.ToArray(), true);
            }
        }

        private static void PruneAcks(float now)
        {
            if (AuthAcks.Count == 0) return;
            List<ulong> old = null;
            foreach (var kv in AuthAcks) if (now - kv.Value.At > AckKeep) (old ??= new List<ulong>()).Add(kv.Key);
            if (old != null) foreach (ulong k in old) AuthAcks.Remove(k);
        }

        private static bool Same(float[] a, float[] b)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void WriteFloats(BinaryWriter w, float[] v)
        {
            w.Write((byte)v.Length);
            foreach (float f in v) w.Write(f);
        }

        private static float[] ReadFloats(BinaryReader r)
        {
            int n = r.ReadByte();
            var v = new float[n];
            for (int i = 0; i < n; i++) v[i] = r.ReadSingle();
            return v;
        }

        // ================================================================== receive

        internal static void Receive(byte sub, BinaryReader r)
        {
            if (sub == GoldSync.SubContDelta) ReceiveDelta(r);
            else if (sub == GoldSync.SubContState) ReceiveState(r);
            else
            {
                ulong who = r.ReadUInt64();
                if (who != Net.LocalId && Net.IsHost) { _sendAll = true; _nextAreaSend = 0f; } // every state (and the area list) goes out next tick
            }
        }

        private static void ReceiveDelta(BinaryReader r)
        {
            ulong origin = r.ReadUInt64();
            uint seq = r.ReadUInt32();
            int n = r.ReadInt32();
            bool ready = Net.IsHost && GoldSync.Synced() && origin != GoldSync.StreamId;
            if (ready && AuthAcks.TryGetValue(origin, out AckInfo last) && (int)(seq - last.Seq) <= 0) ready = false; // duplicate
            bool any = false;
            for (int k = 0; k < n; k++)
            {
                string key = r.ReadString();
                float[] d = ReadFloats(r);
                if (!ready || !ByKey.TryGetValue(key, out Entry e) || !Alive(e) || d.Length != e.D.Size) continue;
                Read(e, e.Tmp);
                for (int i = 0; i < d.Length; i++)
                {
                    float v = e.Tmp[i] + d[i];
                    e.Tmp[i] = float.IsNaN(v) || float.IsInfinity(v) ? e.Tmp[i] : Mathf.Max(0f, v);
                }
                Write(e, e.Tmp);
                e.Sent = null; // always answer with this container's state: it carries the acknowledgement
                any = true;
            }
            if (!ready) return;
            // Acknowledge even if no container was known here: the sender stops predicting at once.
            AuthAcks[origin] = new AckInfo { Seq = seq, At = Time.unscaledTime };
            _acksDirty = true;
            if (any) _nextSend = 0f; // answer at once
        }

        private static void ReceiveState(BinaryReader r)
        {
            ulong author = r.ReadUInt64();
            int na = r.ReadByte();
            uint ack = 0;
            bool hasAck = false;
            for (int k = 0; k < na; k++)
            {
                ulong id = r.ReadUInt64();
                uint s = r.ReadUInt32();
                if (id == GoldSync.StreamId) { ack = s; hasAck = true; }
            }
            int n = r.ReadInt32();
            bool ready = !Net.IsHost && GoldSync.Synced() && author != Net.LocalId;
            if (!ready) return;
            if (hasAck)
                foreach (Entry e in All)
                    if (e.Pend.Count > 0) Predict.PruneAcked(e.Pend, ack);
            for (int k = 0; k < n; k++)
            {
                string key = r.ReadString();
                float[] v = ReadFloats(r);
                if (!ByKey.TryGetValue(key, out Entry e) || !Alive(e) || v.Length != e.D.Size) continue;
                Capture(e);
                // Never answered (the host did not know the container): stop predicting it.
                if (e.Pend.Count > 0) ExpirePend(e.Pend);
                Predict.Want(v, e.Acc, e.Pend, e.Tmp);
                Write(e, e.Tmp);
                Read(e, e.Exp);
            }
        }

        // ================================================================== bucket / bed visuals

        private static readonly AccessTools.FieldRef<DirtBase, float> TargetVolumeRef = GoldSync.TryRef<DirtBase, float>("_targetvolume");

        /// <summary>
        /// Bucket / truck bed of a machine this PC does not simulate: the game updates the dirt model only
        /// when material is added or dumped locally, so make it follow the synced contents.
        /// </summary>
        private static void RefreshDigVisual(DiggingController dc)
        {
            try
            {
                DirtBase dirt = dc.Dirt;
                if (dirt == null || dc._maxShovelVolume <= 0f) return;
                float target = Mathf.Clamp01(dc._currShovelTotalVolume / dc._maxShovelVolume);
                if (dirt is DumpTruckDirt bed)
                {
                    float avg = 0f;
                    int n = bed.MyPlanePoints != null ? bed.MyPlanePoints.Count : 0;
                    if (n == 0) return;
                    foreach (var p in bed.MyPlanePoints) avg += p.ProgressInPoint;
                    avg /= n;
                    if (Mathf.Abs(avg - target) > 0.005f)
                    {
                        float scale = avg > 0.01f ? target / avg : 0f;
                        foreach (var p in bed.MyPlanePoints)
                        {
                            p.ProgressInPoint = avg > 0.01f ? Mathf.Clamp01(p.ProgressInPoint * scale) : target;
                            p.PreparePoint();
                        }
                    }
                    if (TargetVolumeRef != null) TargetVolumeRef(bed) = target;
                    if (bed.CollisionMesh != null)
                    {
                        if (target > 0f) bed.CollisionMesh.gameObject.SetActive(true);
                        bed.CollisionMesh.UpdateCollisionMesh();
                        if (target <= 0f) bed.CollisionMesh.gameObject.SetActive(false);
                    }
                    if (bed.MyVehicleRigidbody != null) bed.MyVehicleRigidbody.mass = bed.EmptyVehicleMass + target * bed.LoadMass;
                    return;
                }
                if (target <= 0f) { if (dirt.gameObject.activeSelf) dirt.gameObject.SetActive(false); }
                else
                {
                    if (!dirt.gameObject.activeSelf) dirt.gameObject.SetActive(true);
                    dirt.SetDirtVolume(target);
                }
            }
            catch (Exception) { /* visual only */ }
        }

        // ================================================================== simulation wraps

        private sealed class WrapSet
        {
            public float BuiltAt, MachineChecked;
            public MachineController Machine; // machine the owner is mounted on (MachineSync id) or null
            public string Area;               // streamed area of a world owner (AreaAuthority), null = not streamed
            public readonly List<Entry> Entries = new List<Entry>();
            public readonly List<float[]> Saved = new List<float[]>();
        }

        private static readonly Dictionary<object, WrapSet> WrapCache = new Dictionary<object, WrapSet>();

        /// <summary>> 0 while a processing call runs that this PC does not simulate.</summary>
        internal static int ForeignDepth;

        private static WrapSet Near(Component owner)
        {
            float now = Time.unscaledTime;
            if (!WrapCache.TryGetValue(owner, out WrapSet set))
            {
                set = new WrapSet { BuiltAt = -10f };
                MachineController m = owner.GetComponentInParent<MachineController>();
                set.Machine = m != null && MachineSync.IdOf(m) >= 0 ? m : null;
                set.Area = AreaOf(owner.transform);
                set.MachineChecked = now;
                WrapCache[owner] = set;
            }
            else if (now - set.MachineChecked > 10f)
            {
                set.MachineChecked = now;
                MachineController m = owner.GetComponentInParent<MachineController>();
                set.Machine = m != null && MachineSync.IdOf(m) >= 0 ? m : null;
                set.Area = AreaOf(owner.transform); // a placed object can be moved / re-parented
            }
            return set;
        }

        private static void FillNear(WrapSet set, Component owner)
        {
            float now = Time.unscaledTime;
            if (now - set.BuiltAt <= 2f) return;
            set.BuiltAt = now;
            set.Entries.Clear();
            Vector3 pos = owner.transform.position;
            float r2 = WrapRadius * WrapRadius;
            int n = 0;
            foreach (Entry e in All)
            {
                if (e.Tr == null || (e.Tr.position - pos).sqrMagnitude > r2) continue;
                set.Entries.Add(e);
                // Restore buffers are reused across rebuilds (no allocation every 2 s per processor).
                int size = e.D.Size;
                if (n < set.Saved.Count) { if (set.Saved[n].Length != size) set.Saved[n] = new float[size]; }
                else set.Saved.Add(new float[size]);
                n++;
            }
            if (set.Saved.Count > n) set.Saved.RemoveRange(n, set.Saved.Count - n);
        }

        /// <summary>Start of a processing call. Returns the saved set (null = this PC simulates it).</summary>
        private static WrapSet BeginWrap(Component owner)
        {
            if (!GoldSync.Synced() || All.Count == 0) return null;
            WrapSet set = Near(owner);
            if (SimAuthority(set.Machine, set.Area)) return null;
            FillNear(set, owner);
            for (int i = 0; i < set.Entries.Count; i++)
                if (Alive(set.Entries[i])) Read(set.Entries[i], set.Saved[i]);
            GoldSync.SuppressDepth++;
            ForeignDepth++;
            return set;
        }

        private static void EndWrap(WrapSet set)
        {
            if (set == null) return;
            if (GoldSync.SuppressDepth > 0) GoldSync.SuppressDepth--;
            if (ForeignDepth > 0) ForeignDepth--;
            for (int i = 0; i < set.Entries.Count; i++)
            {
                Entry e = set.Entries[i];
                if (!Alive(e)) continue;
                Read(e, e.Tmp);
                if (!Same(e.Tmp, set.Saved[i])) Write(e, set.Saved[i]);
            }
        }

        /// <summary>
        /// Start of game code that replays ANOTHER player's action here for show only (a bucket poured by a
        /// remote attach): contents changes inside are undone by EndShowOnly and stock changes skipped. The
        /// acting player's PC makes the real change (its deltas / the host's state bring it), so the material
        /// moves once. Null = nothing to wrap.
        /// </summary>
        internal static object BeginShowOnly(Component owner)
        {
            if (owner == null || !GoldSync.Synced() || All.Count == 0) return null;
            WrapSet set = Near(owner);
            FillNear(set, owner);
            for (int i = 0; i < set.Entries.Count; i++)
                if (Alive(set.Entries[i])) Read(set.Entries[i], set.Saved[i]);
            GoldSync.SuppressDepth++;
            ForeignDepth++;
            return set;
        }

        internal static void EndShowOnly(object set) => EndWrap(set as WrapSet);

        /// <summary>World processing of every non-holdable container type, plus processors without own contents.</summary>
        [HarmonyPatch]
        private static class PatchProcessing
        {
            private static List<MethodBase> _targets;

            private static List<MethodBase> Targets()
            {
                if (_targets != null) return _targets;
                var list = _targets = new List<MethodBase>();
                void Add(MethodInfo m)
                {
                    if (m != null && !m.IsAbstract && m.GetMethodBody() != null && !list.Contains(m)) list.Add(m);
                }
                foreach (var kv in Defs)
                {
                    if (kv.Value.Holdable) continue;
                    foreach (string name in new[] { "Update", "FixedUpdate", "LateUpdate", "UpdateRare" })
                        Add(GoldSync.Find(kv.Key, name, Type.EmptyTypes));
                }
                // Base-class processing shared by several container types.
                Add(GoldSync.Find(typeof(WashplantShakerBase), "Update", Type.EmptyTypes));
                Add(GoldSync.Find(typeof(WashplantShakerBase), "UpdateRare", Type.EmptyTypes));
                Add(GoldSync.Find(typeof(WashplantShakerBase), "FixedUpdate", Type.EmptyTypes));
                // Processors without contents of their own: the elevator buckets take from the ground
                // conveyor and throw clods; the hog pan swaps full mats automatically.
                Add(GoldSync.Need(typeof(ConveyorElevator), "Update", "elevator processing on the host only", Type.EmptyTypes));
                Add(GoldSync.Need(typeof(HogPan), "UpdateRare", "hog pan mat swap on the host only", Type.EmptyTypes));
                if (list.Count == 0) Plugin.Log.LogWarning("Container sync: no processing methods found: host-only processing is OFF");
                return list;
            }

            private static IEnumerable<MethodBase> TargetMethods() => Targets();
            private static bool Prepare() => Targets().Count > 0;

            private static void Prefix(Component __instance, ref WrapSet __state)
            {
                __state = null;
                try
                {
                    if (__instance != null) __state = BeginWrap(__instance);
                }
                catch (Exception ex) { GWrap.Fail(ex); __state = null; }
            }

            private static Exception Finalizer(Exception __exception, WrapSet __state)
            {
                try { EndWrap(__state); }
                catch (Exception ex) { GWrap.Fail(ex); }
                return __exception;
            }
        }

        // ---------- clods a processing call on this PC must not create ----------

        /// <summary>Frankenstein belt: the clod a belt load turns into at the end of the belt.</summary>
        [HarmonyPatch]
        private static class PatchBeltClod
        {
            private static MethodBase TargetMethod() =>
                GoldSync.Need(typeof(FrankensteinBelt.BeltDirt), nameof(FrankensteinBelt.BeltDirt.UpdateDirtPosition), "no duplicate Frankenstein belt clods");

            private static bool Prepare() => TargetMethod() != null;

            private static void Postfix(ref VirtualVolumeDirt dirt)
            {
                if (ForeignDepth <= 0 || dirt == null) return;
                UnityEngine.Object.Destroy(dirt.gameObject); // the simulating PC's clod arrives as a twin
                dirt = null;
            }
        }

        private static readonly AccessTools.FieldRef<ConveyorElevator.ConveyorElevatorBucket, Transform> QuadRef =
            GoldSync.TryRef<ConveyorElevator.ConveyorElevatorBucket, Transform>("_quad");

        private static readonly FieldInfo ParticlesField = AccessTools.Field(typeof(ConveyorElevator), "Particles");
        private static readonly Dictionary<Type, MethodInfo> PlayMethods = new Dictionary<Type, MethodInfo>();

        private static MethodInfo PlayOf(Type t)
        {
            if (!PlayMethods.TryGetValue(t, out MethodInfo m))
                PlayMethods[t] = m = t.GetMethod("Play", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            return m;
        }

        /// <summary>Elevator bucket emptying at the top: visuals only on a PC that does not simulate it.</summary>
        [HarmonyPatch]
        private static class PatchElevatorThrow
        {
            private static MethodBase TargetMethod() =>
                GoldSync.Need(typeof(ConveyorElevator.ConveyorElevatorBucket), "ThrowOut", "no duplicate elevator clods");

            private static bool Prepare() => TargetMethod() != null;

            private static bool Prefix(ConveyorElevator.ConveyorElevatorBucket __instance)
            {
                if (ForeignDepth <= 0 || __instance == null) return true;
                try
                {
                    if (__instance.Dirt <= 0f) return false;
                    // Particles (ParticleSystem lives in a module this mod does not reference): by reflection.
                    if (__instance.MyElevator != null && ParticlesField?.GetValue(__instance.MyElevator) is System.Collections.IList ps)
                        for (int i = 0; i < ps.Count; i++)
                            if (ps[i] is UnityEngine.Object o && o != null) PlayOf(o.GetType())?.Invoke(o, NoArgs);
                    __instance.Dirt = 0f;
                    float[] bag = GoldSync.BagArray(__instance.Minerals);
                    if (bag != null) Array.Clear(bag, 0, bag.Length);
                    Transform quad = QuadRef != null ? QuadRef(__instance) : null;
                    if (quad != null) quad.gameObject.SetActive(false);
                }
                catch (Exception ex) { GElevator.Fail(ex); }
                return false;
            }
        }

        // ---------- hints of processing this PC does not simulate ----------

        private static readonly Dictionary<string, float> HintTimes = new Dictionary<string, float>();

        /// <summary>
        /// A processing call that this PC replays on restored contents can reach its "done"/"full" state on
        /// every frame until the host's new state arrives: show each hint at most once per 15 s.
        /// </summary>
        private static bool AllowHint(string key)
        {
            if (ForeignDepth <= 0) return true;
            key = key ?? "";
            float now = Time.unscaledTime;
            if (HintTimes.TryGetValue(key, out float t) && now - t < HintThrottle) return false;
            if (HintTimes.Count >= 64) PruneHints(now); // hint texts may carry amounts: keep the map small over hours
            HintTimes[key] = now;
            return true;
        }

        private static readonly List<string> _oldHints = new List<string>();

        private static void PruneHints(float now)
        {
            _oldHints.Clear();
            foreach (var kv in HintTimes) if (now - kv.Value >= HintThrottle) _oldHints.Add(kv.Key);
            foreach (string k in _oldHints) HintTimes.Remove(k);
            if (HintTimes.Count >= 64) HintTimes.Clear(); // 64 different hints within 15 s: start over
        }

        [HarmonyPatch]
        private static class PatchHints
        {
            private static List<MethodBase> _targets;

            private static List<MethodBase> Targets()
            {
                if (_targets != null) return _targets;
                _targets = new List<MethodBase>();
                foreach (MethodInfo m in typeof(HUD).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    if (m.Name == nameof(HUD.ShowHint)) _targets.Add(m);
                if (_targets.Count == 0) Plugin.Log.LogWarning("Patch target HUD.ShowHint not found: hint throttling is OFF");
                return _targets;
            }

            private static IEnumerable<MethodBase> TargetMethods() => Targets();
            private static bool Prepare() => Targets().Count > 0;

            private static bool Prefix(object[] __args)
            {
                if (ForeignDepth <= 0) return true;
                string key = null;
                foreach (object a in __args) if (a is string s) { key = s; break; }
                return AllowHint(key);
            }
        }

        // ---------- machines this PC does not simulate ----------

        internal static int BlockDig;

        private static MachineController MachineOf(Controller c) => c != null && OwnerRef != null ? OwnerRef(c) : null;

        private struct DigState
        {
            public bool Blocked;
            public Entry E;
            public float[] Saved;
        }

        /// <summary>Bucket / blade of a machine this PC does not simulate: no digging, contents from the host.</summary>
        [HarmonyPatch]
        private static class PatchDiggingUpdate
        {
            private static MethodBase TargetMethod() =>
                GoldSync.Need(typeof(DiggingController), nameof(DiggingController.Update), "no digging by machines another PC simulates", Type.EmptyTypes);

            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(DiggingController __instance, ref DigState __state)
            {
                __state = default;
                if (MachineAuthority(MachineOf(__instance))) return;
                __state.Blocked = true;
                BlockDig++;
                if (ByObj.TryGetValue(__instance, out Entry e))
                {
                    __state.E = e;
                    __state.Saved = e.DigSaved ??= new float[e.D.Size];
                    Read(e, __state.Saved);
                }
            }

            private static Exception Finalizer(Exception __exception, DigState __state)
            {
                if (__state.Blocked)
                {
                    if (BlockDig > 0) BlockDig--;
                    if (__state.E != null && Alive(__state.E))
                    {
                        Read(__state.E, __state.E.Tmp);
                        if (!Same(__state.E.Tmp, __state.Saved)) Write(__state.E, __state.Saved);
                    }
                }
                return __exception;
            }
        }

        [HarmonyPatch]
        private static class PatchShovelUpdate
        {
            private static MethodBase TargetMethod() =>
                GoldSync.Need(typeof(DiggingShovelController), nameof(DiggingShovelController.Update), "no blade digging by machines another PC simulates", Type.EmptyTypes);

            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(DiggingShovelController __instance, ref bool __state)
            {
                __state = !MachineAuthority(MachineOf(__instance));
                if (__state) BlockDig++;
            }

            private static Exception Finalizer(Exception __exception, bool __state)
            {
                if (__state && BlockDig > 0) BlockDig--;
                return __exception;
            }
        }

        [HarmonyPatch]
        private static class PatchTerrainDig
        {
            private static List<MethodBase> _targets;

            private static List<MethodBase> Targets()
            {
                if (_targets != null) return _targets;
                _targets = new List<MethodBase>();
                foreach (string n in new[] { "DigWithBox", "TryMove", "SoftenTerrain", "ShovelAddHeights" })
                {
                    MethodInfo m = GoldSync.Need(typeof(GoldTerrainDigger), n, "terrain block for machines another PC simulates (" + n + ")");
                    if (m != null) _targets.Add(m);
                }
                return _targets;
            }

            private static IEnumerable<MethodBase> TargetMethods() => Targets();
            private static bool Prepare() => Targets().Count > 0;

            // ResourceVolume is a struct / ShovelAddHeights returns a float: skipping leaves the
            // default (nothing dug, nothing pushed).
            private static bool Prefix() => BlockDig <= 0;
        }

        [HarmonyPatch]
        private static class PatchCreateDirt
        {
            private static MethodBase TargetMethod() =>
                GoldSync.Need(typeof(DiggingController), "CreateVirtualDirt", "no clods from machines another PC simulates");

            private static bool Prepare() => TargetMethod() != null;

            private static bool Prefix(DiggingController __instance) => MachineAuthority(MachineOf(__instance));
        }

        [HarmonyPatch]
        private static class PatchThrowOut
        {
            private static MethodBase TargetMethod() =>
                GoldSync.Need(typeof(DumpTruckDirt), "CheckThrowOut", "no bed dumping by trucks another PC simulates");

            private static bool Prepare() => TargetMethod() != null;

            private static bool Prefix(DumpTruckDirt __instance)
            {
                if (__instance == null || MachineAuthority(__instance.GetComponentInParent<MachineController>())) return true;
                // The simulating PC dumps (its clods carry the material); the bed follows the contents.
                if (__instance.Volume == 0f && __instance.CollisionMesh != null) __instance.CollisionMesh.gameObject.SetActive(false);
                return false;
            }
        }
    }
}
