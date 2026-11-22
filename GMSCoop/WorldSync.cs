using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GoldDigger;
using HarmonyLib;
using SaveSystem;
using UnityEngine;
using Gsm = GoldDigger.GameStateManager;

namespace GMSCoop
{
    /// <summary>
    /// World features that no other sync covers ((Msg)14, sub 18..22). Host-ordered: a client sends,
    /// the host applies and relays; for saved states the host answers with its own state, so every PC
    /// converges on the host's order.
    ///
    /// SWITCHES AND LEVERS (sub 20, UseAction replay). Every "use" on a world object goes through
    /// UseAction.StartUsing / StopUsing / StartUsingSecond / StopUsingSecond, which SendMessage a method
    /// to the object. When the receiving method belongs to a world switch type (magnetite separator, mini
    /// / mobile wash plant, wave table, mat scrubber, water station / tower, pump building, power
    /// generator / socket / building switch / mobile power station, power box doors, container doors,
    /// openable objects, hangar doors), the same call is replayed on the same UseAction on every PC.
    /// Machines and trailers are left to the vehicle sync; pickups, shops and anything else are never
    /// replayed (they would open menus or create items twice). The UseAction is found by its root
    /// (ObjectSync key, trailer id for trailer-mounted plants, or save id + position for fixed objects) and its
    /// path below the root. Switch and paint events made while a player joins are journaled by the host and
    /// replayed in order after its load, exactly once (see the events section at the end of this file).
    ///
    /// ONE-WAY SAVED STATES (sub 18, polled every 0.5 s, never reverted): fast travel points bought
    /// (also GameStateManager.fastTravelUnlocked), story notes found (journal: StoriesController), the
    /// Gems and Glory blocked pass blown up, bridge parts repaired (the matching part is attached),
    /// treasure chests opened (their money effect runs on the host only: GoldSync), golden bar bonuses,
    /// gold / big pumpkins, water towers bought. Applied through the game's own Deserialize /
    /// AfterDeserialize (the same code a load runs). A joining player gets them all after it loaded.
    ///
    /// BLACKSMITH (sub 21): the forge state (global GameStateManager: upgrade levels, remelt in
    /// progress, its start time and amount, bars ready). Money / gold / magnetite spent are ledger deltas
    /// (GoldSync); the bars themselves are objects (ObjectSync). Applied through Deserialize +
    /// AfterDeserialize, which restarts the remelt timer from the shared clock.
    /// A PC sends only the keys it changed (the remelt keys always as one group), merged into the
    /// receiver's own state, so a client upgrading while the host starts a remelt keeps both. Every
    /// message carries the author's stream id + sequence; the host answers a player's change with its
    /// full merged state tagged with that id + sequence. A player with its own change in flight applies
    /// nothing but the host's answer to its newest change: it is never pulled back for a round trip
    /// (two quick upgrades used to show level 2 -> 1 -> 2), and a change it made in the last 0.5 s is
    /// sent before a remote state is applied (it used to be overwritten and lost).
    ///
    /// EXPLOSIONS (sub 19): dynamite explosions are shown on every PC (flash, sound, light) and their
    /// pusher runs there, so objects that PC simulates are thrown and rocks / machines in the blast are
    /// hit; the crater (terrain), the gold voxels and the debris clods are synced by TerrainSync /
    /// DirtSync, so the replay does not dig and spawns no debris.
    ///
    /// FUEL TANKS (sub 23 / 24): see FuelSync.cs.
    ///
    /// PAINT (sub 22): a machine painted in the paint shop is painted on every PC (Steam Workshop paints
    /// only where that paint is installed).
    ///
    /// Local by design (checked): inspection tool (hint only), laptop notifications and transaction
    /// history (UI of each PC, derived from synced events), DigTube (videos per player), journal pages
    /// (static; only story notes unlock, synced here), falling trees (TreeFall: not saved, cosmetic),
    /// drill points (TerrainSync), leaderboard upload (only the ranked owner of the save can upload).
    /// </summary>
    internal static partial class WorldSync
    {
        internal const byte SubFlags = 18;
        internal const byte SubBoom = 19;
        internal const byte SubUse = 20;
        internal const byte SubForge = 21;
        internal const byte SubPaint = 22;

        private const float PollInterval = 0.5f;
        private const float IndexCheck = 2f; // at most this often after a change;
        private const float Settle = 2f;

        private static bool Synced() => GoldSync.Synced();

        // ================================================================== roots (identity)

        private static readonly Dictionary<string, SaveMe> FixedByKey = new Dictionary<string, SaveMe>();
        private static readonly Dictionary<SaveMe, string> FixedKeyOf = new Dictionary<SaveMe, string>();

        /// <summary>
        /// Driven machines and the light trailer panel belong to the vehicle sync (MachineSync /
        /// MachinePartsSync). Plants mounted on other trailers (mobile wash plant, mobile power station)
        /// are world switches replayed here, keyed by the trailer's id ("t" root).
        /// </summary>
        private static bool IsVehicle(Component c) =>
            c.GetComponentInParent<MachineController>(true) != null || c.GetComponentInParent<MobileLightTrailer>(true) != null;

        private static string FixedKey(SaveMe sm, int dup)
        {
            Vector3 p = sm.transform.position;
            string k = sm.SaveID + "@" + Mathf.RoundToInt(p.x * 2f) + "," + Mathf.RoundToInt(p.y * 2f) + "," + Mathf.RoundToInt(p.z * 2f);
            return dup > 0 ? k + "#" + dup : k;
        }

        /// <summary>Network name of the saved object <paramref name="sm"/>: ObjectSync key, machine id or fixed key.</summary>
        private static string RootKey(SaveMe sm, bool allowMachine)
        {
            if (sm == null) return null;
            // includeInactive: the same key whether this object's area is loaded here or not.
            Trailer tr = sm.GetComponentInParent<Trailer>(true);
            if (tr != null)
            {
                int tid = MachineSync.TrailerIdOf(tr);
                return allowMachine && tid >= 0 ? "t" + tid : null;
            }
            MachineController m = sm.GetComponentInParent<MachineController>(true);
            if (m != null)
            {
                int id = MachineSync.IdOf(m);
                return allowMachine && id >= 0 ? "m" + id : null;
            }
            string ok = ObjectSync.KeyOf(sm.gameObject);
            if (ok != null) return "o" + ok;
            if (!FixedKeyOf.TryGetValue(sm, out string fk)) { _indexDirty = true; return null; }
            return "f" + fk;
        }

        private static GameObject FindRoot(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            switch (key[0])
            {
                case 'm':
                    return int.TryParse(key.Substring(1), out int id) ? MachineSync.Get(id)?.gameObject : null;
                case 't':
                    return int.TryParse(key.Substring(1), out int tid) ? MachineSync.GetTrailer(tid)?.gameObject : null;
                case 'o':
                    return ObjectSync.Find(key.Substring(1));
                case 'f':
                    return FixedByKey.TryGetValue(key.Substring(1), out SaveMe sm) && sm != null ? sm.gameObject : null;
            }
            return null;
        }

        private static string PathBelow(Transform root, Transform t)
        {
            if (t == root) return "";
            var parts = new List<string>();
            for (Transform x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ================================================================== index

        private sealed class FlagEntry
        {
            public string Key;
            public Component C;
            public FlagKind K;
            public int Known;
        }

        private sealed class FlagKind
        {
            public Type T;
            public string Name;
            public AccessTools.FieldRef<object, bool>[] Fields;
            public string[] SaveKeys;
            public bool After;
            public Action<FlagEntry, int> Apply; // custom apply (null = Deserialize [+ AfterDeserialize])
        }

        private static readonly List<FlagKind> Kinds = new List<FlagKind>();
        private static readonly Dictionary<Type, FlagKind> KindOf = new Dictionary<Type, FlagKind>();
        private static readonly List<FlagEntry> Flags = new List<FlagEntry>();
        private static readonly Dictionary<string, FlagEntry> FlagByKey = new Dictionary<string, FlagEntry>();
        private static readonly Dictionary<string, int> PendingFlags = new Dictionary<string, int>();
        private static readonly Dictionary<Component, int> OldKnown = new Dictionary<Component, int>();
        private static bool _kindsBuilt, _indexed, _indexDirty;
        private static int _world = -1, _objKeyVersion = -1;
        private static bool _wasSynced;
        private static float _readySince = -1f, _nextIndexCheck, _nextPoll, _nextFullIndex;

        [HarmonyPatch]
        private static class PatchSavedAdd
        {
            private static MethodBase TargetMethod() =>
                GoldSync.Need(typeof(CheckPointManagerBase), nameof(CheckPointManagerBase.Add), "world state index of new objects", new[] { typeof(SaveMe) });

            private static bool Prepare() => TargetMethod() != null;

            // Shares CheckPointManagerBase.Add with ContainerSync, MachineSync and ObjectSync (all postfixes):
            // must never throw into the game's registration, and stays cheap for the dirt clods that
            // register all the time while digging.
            private static void Postfix(SaveMe saveMe)
            {
                if (!_indexed || saveMe == null) return;
                try
                {
                    if (saveMe.GetComponent<VirtualVolumeDirt>() != null) return; // dirt clod: no flag, no tank
                    if (saveMe.GetComponentInParent<Rigidbody>(true) == null || saveMe.GetComponentInChildren<FuelStationController>(true) != null)
                    {
                        _indexDirty = true;
                        _nextTankIndex = 0f;
                    }
                }
                catch (Exception) { _indexDirty = true; }
            }
        }

        private static void AddKind(Type t, string[] fields, string[] keys, bool after, Action<FlagEntry, int> apply = null)
        {
            if (t == null) return;
            var refs = new AccessTools.FieldRef<object, bool>[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = AccessTools.Field(t, fields[i]);
                if (f == null || f.FieldType != typeof(bool))
                {
                    Plugin.Log.LogWarning($"World sync: {t.Name}.{fields[i]} not found: {t.Name} state is OFF");
                    return;
                }
                refs[i] = AccessTools.FieldRefAccess<bool>(f.DeclaringType, f.Name);
            }
            var k = new FlagKind { T = t, Name = t.Name, Fields = refs, SaveKeys = keys, After = after, Apply = apply };
            Kinds.Add(k);
            KindOf[t] = k;
        }

        private static void BuildKinds()
        {
            if (_kindsBuilt) return;
            _kindsBuilt = true;
            try
            {
                AddKind(typeof(FastTravelPoint), new[] { "isPurchased" }, new[] { "isPurchased" }, true, ApplyFastTravel);
                AddKind(typeof(StoryPartItem), new[] { "_isTaken" }, new[] { "Taken" }, true);
                AddKind(typeof(BlockedPass), new[] { "alreadyExploded" }, new[] { "AlreadyExploded" }, false);
                AddKind(typeof(GoldPumpkin), new[] { "Unlocked" }, new[] { "Unlocked" }, false);
                AddKind(typeof(BigPumpkin), new[] { "Unlocked" }, new[] { "Unlocked" }, false);
                AddKind(typeof(GoldenBarBonus), new[] { "Unlocked" }, new[] { "Unlocked" }, true);
                AddKind(typeof(TreasureChestOpen), new[] { "_wasEffectsApplied" }, new[] { "WasEffectsApplied" }, false, ApplyChest);
                AddKind(typeof(BridgePartToRepair), new[] { "isRepaired" }, new[] { "BridgePartIsFixed" }, false, ApplyBridge);
                AddKind(typeof(WaterTowerController), new[] { "isBought" }, new[] { "isBought" }, false, ApplyTower);
            }
            catch (Exception ex) { Plugin.Log.LogError("World sync: " + ex); }
        }

        private static int ReadFlags(FlagEntry e)
        {
            int v = 0;
            for (int i = 0; i < e.K.Fields.Length; i++) if (e.K.Fields[i](e.C)) v |= 1 << i;
            return v;
        }

        private static void Reset()
        {
            Flags.Clear();
            FlagByKey.Clear();
            FixedByKey.Clear();
            FixedKeyOf.Clear();
            _indexed = false;
            _indexDirty = false;
            _readySince = -1f;
            _forgeKnown = null;
            ResetFuel();
            ResetWater();
            ResetHood();
            ResetTables();
        }

        /// <summary>
        /// The shared world went away (load, host reload, session end): answers and states that arrived
        /// for objects not indexed yet belong to the old world. The host sends everything again once the
        /// new world is loaded (SendAll / its first polls).
        /// </summary>
        private static void DropWorldPending()
        {
            PendingFlags.Clear();
            PendingForge.Clear();
            _forgeWait = 0;
            DropEvents();
        }

        internal static void ResetSession()
        {
            Reset();
            DropWorldPending();
            ResetJournal();
            FuelAcks.Clear();
            WaterAcks.Clear();
            _fuelAcksDirty = false;
            _wasSynced = false;
            _world = -1;
        }

        private static void Index(List<SaveMe> toSave)
        {
            BuildKinds();
            // Keep what was last sent / seen per component: a change made just before a re-index must
            // still be detected (and sent) by the next poll.
            OldKnown.Clear();
            foreach (FlagEntry old in Flags) if (old.C != null) OldKnown[old.C] = old.Known;
            Flags.Clear();
            FlagByKey.Clear();
            FixedByKey.Clear();
            FixedKeyOf.Clear();
            for (int i = 0; i < toSave.Count; i++)
            {
                SaveMe sm = toSave[i];
                if (sm == null) continue;
                if (sm.GetComponentInParent<Rigidbody>(true) != null || IsVehicle(sm)) continue; // physics objects: ObjectSync keys
                int dup = 0;
                string fk = FixedKey(sm, 0);
                while (FixedByKey.ContainsKey(fk)) fk = FixedKey(sm, ++dup);
                FixedByKey[fk] = sm;
                FixedKeyOf[sm] = fk;
            }
            for (int i = 0; i < toSave.Count; i++)
            {
                SaveMe sm = toSave[i];
                if (sm == null || sm.MySerializableComponentsMB == null) continue;
                foreach (MonoBehaviour mb in sm.MySerializableComponentsMB)
                {
                    if (mb == null || !KindOf.TryGetValue(mb.GetType(), out FlagKind k)) continue;
                    string root = RootKey(sm, false);
                    if (root == null) continue;
                    string key = root + "|" + k.Name + "|" + PathBelow(sm.transform, mb.transform);
                    if (FlagByKey.ContainsKey(key)) continue;
                    var e = new FlagEntry { Key = key, C = mb, K = k };
                    e.Known = OldKnown.TryGetValue(mb, out int was) ? was : ReadFlags(e);
                    Flags.Add(e);
                    FlagByKey[key] = e;
                    if (PendingFlags.TryGetValue(key, out int want)) { PendingFlags.Remove(key); ApplyFlags(e, want); }
                }
            }
            _indexed = true;
            _indexDirty = false;
            _objKeyVersion = ObjectSync.KeyVersion;
        }

        // ================================================================== tick

        // Each part polls on its own: an error in one (a destroyed component, a game method that throws)
        // never stops the others (flags, forge, fuel, index).
        private static readonly SubsystemGuard GIndex = new SubsystemGuard("World sync: index"),
            GFlags = new SubsystemGuard("World sync: saved states"), GForge = new SubsystemGuard("World sync: forge"),
            GFuel = new SubsystemGuard("World sync: fuel"), GEvents = new SubsystemGuard("World sync: switch / paint events");

        internal static void Tick(bool synced)
        {
            if (!synced)
            {
                if (_indexed || _readySince >= 0f) Reset();
                if (_wasSynced) { _wasSynced = false; DropWorldPending(); }
                return;
            }
            float now = Time.unscaledTime;
            int world = GameState.WorldVersion;
            if (world != _world)
            {
                // A new world (also a scene reload without a loading frame): nothing of the old one stays.
                if (_world >= 0) DropWorldPending();
                _world = world;
                Reset();
            }
            if (!_wasSynced) OnSyncStart(now); // after the world check: the hold belongs to the new world
            _wasSynced = true;
            if (_readySince < 0f) _readySince = now;
            if (now - _readySince < Settle) return;
            try
            {
                if (!SaveSystem.Singleton<CheckPointManagerBase>.IsInstanced()) return;
                List<SaveMe> toSave = SaveSystem.Singleton<CheckPointManagerBase>.Instance.ToSave;
                // Re-index only when a fixed saved object appeared (PatchSavedAdd: dirt clods and other
                // physics objects, which come and go all the time while digging, do not count), a root key
                // went stale, ObjectSync renamed its keys, or once a minute.
                if (!_indexed || (now >= _nextIndexCheck && (_indexDirty || ObjectSync.KeyVersion != _objKeyVersion)) || now >= _nextFullIndex)
                {
                    _nextIndexCheck = now + IndexCheck;
                    _nextFullIndex = now + 60f;
                    Index(toSave);
                }
            }
            catch (Exception ex) { _indexed = _indexDirty = true; GIndex.Fail(ex); } // retried at the next index check (2 s)
            try { EventTick(now); } catch (Exception ex) { Inbox.Clear(); GEvents.Fail(ex); }
            if (now < _nextPoll) return;
            _nextPoll = now + PollInterval;
            try { PollFlags(); } catch (Exception ex) { GFlags.Fail(ex); }
            try { PollForge(); } catch (Exception ex) { GForge.Fail(ex); }
            try { FuelTick(now); } catch (Exception ex) { _nextTankIndex = now + 10f; GFuel.Fail(ex); }
            try { WaterTick(now); } catch (Exception ex) { _nextWaterIndex = now + 10f; GFuel.Fail(ex); }
            try { TableTick(now); } catch (Exception ex) { GFuel.Fail(ex); }
            try { HoodTick(now); } catch (Exception ex) { _nextHoodPoll = now + 10f; GFuel.Fail(ex); }
        }

        private static readonly List<FlagEntry> Changed = new List<FlagEntry>();

        private static void PollFlags()
        {
            Changed.Clear();
            for (int i = 0; i < Flags.Count; i++)
            {
                FlagEntry e = Flags[i];
                if (e.C == null) { _indexDirty = true; continue; }
                int v = ReadFlags(e);
                if (v == e.Known) continue;
                e.Known = v;
                Changed.Add(e);
            }
            if (Changed.Count > 0) SendFlags(Changed);
        }

        private static void SendFlags(List<FlagEntry> list)
        {
            for (int start = 0; start < list.Count; start += 100)
            {
                int n = Mathf.Min(100, list.Count - start);
                var ms = GoldSync.Begin(SubFlags, out BinaryWriter w);
                w.Write((ushort)n);
                for (int i = 0; i < n; i++) { w.Write(list[start + i].Key); w.Write(list[start + i].Known); }
                TrafficC.Broadcast(TrafficC.Cat.World, ms.ToArray(), true);
            }
        }

        /// <summary>Host: everything a player that just loaded the world may have missed during the transfer.</summary>
        internal static void SendAll()
        {
            if (!Net.IsHost || !Synced() || !_indexed) return;
            Changed.Clear();
            foreach (FlagEntry e in Flags) if (e.C != null && e.Known != 0) Changed.Add(e);
            if (Changed.Count > 0) SendFlags(Changed);
            if (_forgeKnown != null) SendForge(_forgeKnown, 0UL, 0u);
            _fuelSendAll = true;
        }

        private static void ReceiveFlags(BinaryReader r)
        {
            int n = r.ReadUInt16();
            bool ready = Synced();
            List<FlagEntry> answer = null;
            for (int i = 0; i < n; i++)
            {
                string key = r.ReadString();
                int v = r.ReadInt32();
                if (!ready) continue;
                if (!_indexed || !FlagByKey.TryGetValue(key, out FlagEntry e) || e.C == null)
                {
                    if (PendingFlags.Count < 4096) PendingFlags[key] = PendingFlags.TryGetValue(key, out int old) ? old | v : v;
                    continue;
                }
                ApplyFlags(e, v);
                if (Net.IsHost && e.Known != v) (answer ??= new List<FlagEntry>()).Add(e); // host order wins
            }
            if (answer != null) SendFlags(answer);
        }

        private static void ApplyFlags(FlagEntry e, int want)
        {
            int cur = ReadFlags(e);
            int set = want & ~cur; // one-way states: only false -> true
            if (set == 0) { e.Known = cur; return; }
            try
            {
                if (e.K.Apply != null) e.K.Apply(e, cur | set);
                else
                {
                    var data = new List<XmlKey>();
                    for (int i = 0; i < e.K.SaveKeys.Length; i++) data.Add(new XmlKey(e.K.SaveKeys[i], ((cur | set) & (1 << i)) != 0));
                    var sc = (ISerializableComponent)e.C;
                    sc.Deserialize(data);
                    if (e.K.After) sc.AfterDeserialize();
                }
                Plugin.Log.LogInfo($"World sync: {e.K.Name} changed by another player");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"World sync: applying {e.K.Name} failed: {ex.Message}"); }
            e.Known = e.C != null ? ReadFlags(e) : want;
        }

        private static void ApplyFastTravel(FlagEntry e, int want)
        {
            var sc = (ISerializableComponent)e.C;
            sc.Deserialize(new List<XmlKey> { new XmlKey("isPurchased", (want & 1) != 0) });
            sc.AfterDeserialize();
            if ((want & 1) != 0 && global::Singleton<Gsm>.IsInstanced()) global::Singleton<Gsm>.Instance.fastTravelUnlocked = true;
        }

        /// <summary>A chest another player opened opens here too (its money effect: host only, GoldSync).</summary>
        private static void ApplyChest(FlagEntry e, int want)
        {
            if ((want & 1) == 0) return;
            try { ((TreasureChestOpen)e.C).UseChest(); }
            finally
            {
                // No effects configured for this difficulty, or the open animation threw (chest in an area
                // not loaded here: inactive): take the state anyway, so the chest can never be opened twice.
                if (!e.K.Fields[0](e.C)) e.K.Fields[0](e.C) = true;
            }
        }

        private static readonly AccessTools.FieldRef<BridgePartToRepair, BridgePart.BridgePartType> PartTypeRef =
            GoldSync.TryRef<BridgePartToRepair, BridgePart.BridgePartType>("partToRepairType");
        private static readonly MethodInfo AttachPart = AccessTools.Method(typeof(BridgePartToRepair), "AttachBridgePart");

        /// <summary>A bridge part another player placed: attach the nearest loose part of that type here too.</summary>
        private static void ApplyBridge(FlagEntry e, int want)
        {
            if ((want & 1) == 0) return;
            var slot = (BridgePartToRepair)e.C;
            BridgePart best = null;
            float bestD = 60f * 60f;
            if (PartTypeRef != null && AttachPart != null)
            {
                BridgePart.BridgePartType type = PartTypeRef(slot);
                foreach (BridgePart p in UnityEngine.Object.FindObjectsOfType<BridgePart>())
                {
                    if (p == null || p.isFixed || p.partType != type) continue;
                    float d = (p.transform.position - slot.transform.position).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = p; }
                }
            }
            if (best != null) AttachPart.Invoke(slot, new object[] { best });
            else slot.Deserialize(new List<XmlKey> { new XmlKey("BridgePartIsFixed", true) });
        }

        private static void ApplyTower(FlagEntry e, int want)
        {
            if ((want & 1) != 0) ((WaterTowerController)e.C).isBought = true;
        }

        // ================================================================== blacksmith

        private static readonly MemoryStream ForgeMs = new MemoryStream();
        private static readonly BinaryWriter ForgeW = new BinaryWriter(ForgeMs);
        private static byte[] _forgeKnown;
        private static readonly List<byte[]> PendingForge = new List<byte[]>(); // arrived before the first poll
        private static uint _forgeSeq;   // own changes sent
        private static uint _forgeWait;  // client: newest own change the host has not answered (0 = none)
        private static float _forgeWaitAt;
        private const float ForgeWaitMax = 10f;

        // Written together by the remelt GUI / coroutine: always sent as one group.
        private static readonly HashSet<string> RemeltKeys = new HashSet<string>
        {
            "GoldTimer", "timeSave", "goldAmoutSave", "isRemeltingTime", "goldIsRemelted", "remeltingInProgress", "GUItmpTime", "tmpGoldAmount",
        };

        private static global::GameStateManager Forge() =>
            global::Singleton<global::GameStateManager>.IsInstanced() ? global::Singleton<global::GameStateManager>.Instance : null;

        /// <summary>The forge's saved keys (GUI end time left out: it is rewritten by the open GUI).</summary>
        private static List<XmlKey> ForgeKeys(global::GameStateManager f)
        {
            List<XmlKey> keys = f.Serialize();
            keys.RemoveAll(k => k == null || k.Key == "GUIendTime");
            return keys;
        }

        private static void EncodeKeys(List<XmlKey> keys, MemoryStream ms, BinaryWriter w)
        {
            ms.SetLength(0);
            int count = Mathf.Min(keys.Count, 255);
            w.Write((byte)count);
            for (int n = 0; n < count; n++)
            {
                XmlKey k = keys[n];
                w.Write(k.Key);
                switch (k.Value)
                {
                    case bool b: w.Write((byte)1); w.Write(b); break;
                    case int i: w.Write((byte)2); w.Write(i); break;
                    case float fl: w.Write((byte)3); w.Write(fl); break;
                    case string s: w.Write((byte)4); w.Write(s); break;
                    case double d: w.Write((byte)5); w.Write(d); break;
                    default: w.Write((byte)0); break;
                }
            }
            w.Flush();
        }

        private static List<XmlKey> DecodeKeys(byte[] body)
        {
            var r = new BinaryReader(new MemoryStream(body));
            int n = r.ReadByte();
            var data = new List<XmlKey>(n);
            for (int i = 0; i < n; i++)
            {
                string key = r.ReadString();
                object v;
                switch (r.ReadByte())
                {
                    case 1: v = r.ReadBoolean(); break;
                    case 2: v = r.ReadInt32(); break;
                    case 3: v = r.ReadSingle(); break;
                    case 4: v = r.ReadString(); break;
                    case 5: v = r.ReadDouble(); break;
                    default: v = null; break;
                }
                if (v != null) data.Add(new XmlKey(key, v));
            }
            return data;
        }

        private static bool SameAs(MemoryStream ms, byte[] b)
        {
            if (b == null || ms.Length != b.Length) return false;
            byte[] buf = ms.GetBuffer();
            for (int i = 0; i < b.Length; i++) if (buf[i] != b[i]) return false;
            return true;
        }

        private static void PollForge()
        {
            global::GameStateManager f = Forge();
            if (f == null) return;
            List<XmlKey> cur = ForgeKeys(f);
            EncodeKeys(cur, ForgeMs, ForgeW);
            if (_forgeKnown == null)
            {
                _forgeKnown = ForgeMs.ToArray(); // baseline: what the save loaded
                if (PendingForge.Count > 0)
                {
                    foreach (byte[] p in PendingForge) ApplyForge(f, p);
                    PendingForge.Clear();
                }
                return;
            }
            if (SameAs(ForgeMs, _forgeKnown)) return;
            // Only what changed since the last known state (the remelt keys as one group).
            var known = new Dictionary<string, object>();
            foreach (XmlKey k in DecodeKeys(_forgeKnown)) known[k.Key] = k.Value;
            _forgeKnown = ForgeMs.ToArray();
            bool remelt = false;
            var changed = new List<XmlKey>();
            foreach (XmlKey k in cur)
            {
                if (known.TryGetValue(k.Key, out object was) && Equals(was, k.Value)) continue;
                if (RemeltKeys.Contains(k.Key)) remelt = true;
                else changed.Add(k);
            }
            if (remelt) foreach (XmlKey k in cur) if (RemeltKeys.Contains(k.Key)) changed.Add(k);
            if (changed.Count == 0) return;
            if (++_forgeSeq == 0) _forgeSeq = 1;
            if (!Net.IsHost) { _forgeWait = _forgeSeq; _forgeWaitAt = Time.unscaledTime; }
            EncodeKeys(changed, ForgeMs, ForgeW);
            SendForge(ForgeMs.ToArray(), GoldSync.StreamId, _forgeSeq);
        }

        private static void SendForge(byte[] body, ulong origin, uint seq)
        {
            var ms = GoldSync.Begin(SubForge, out BinaryWriter w);
            w.Write(origin);
            w.Write(seq);
            w.Write(body.Length);
            w.Write(body);
            TrafficC.Broadcast(TrafficC.Cat.World, ms.ToArray(), true);
        }

        private static void ReceiveForge(BinaryReader r)
        {
            ulong origin = r.ReadUInt64();
            uint seq = r.ReadUInt32();
            byte[] body = r.ReadBytes(r.ReadInt32());
            if (!Synced()) return;
            global::GameStateManager f = Forge();
            if (f == null || _forgeKnown == null) { if (PendingForge.Count < 64) PendingForge.Add(body); return; }
            PollForge(); // a change made here since the last poll goes out first (it would be overwritten)
            if (!Net.IsHost && _forgeWait != 0)
            {
                bool ours = origin == GoldSync.StreamId;
                if (ours && Predict.Acked(_forgeWait, seq)) _forgeWait = 0; // the host's answer to our newest change
                else if (GoldSync.Host.Talked(_forgeWaitAt) <= ForgeWaitMax) return; // older state: our change is on its way
                else _forgeWait = 0;
            }
            ApplyForge(f, body);
            // Host order wins: answer a player's change with the full merged state, tagged with its id + seq.
            if (Net.IsHost) SendForge(_forgeKnown, origin, seq);
        }

        /// <summary>Merges the received keys into this PC's forge state and applies it like a load.</summary>
        private static void ApplyForge(global::GameStateManager f, byte[] body)
        {
            try
            {
                List<XmlKey> data = ForgeKeys(f);
                foreach (XmlKey k in DecodeKeys(body))
                {
                    int at = data.FindIndex(x => x.Key == k.Key);
                    if (at >= 0) data[at] = k;
                    else data.Add(k);
                }
                f.Deserialize(data);
                f.AfterDeserialize();
                Plugin.Log.LogInfo("World sync: forge state from another player applied");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("World sync: forge state failed: " + ex.Message); }
            EncodeKeys(ForgeKeys(f), ForgeMs, ForgeW);
            _forgeKnown = ForgeMs.ToArray();
        }

        // ================================================================== switches (UseAction replay)

        private static readonly HashSet<Type> SwitchTypes = new HashSet<Type>
        {
            typeof(MagnetiteSeparator), typeof(MiniWashplant), typeof(MobileWashplant), typeof(WaveTable), typeof(MatScrubber),
            typeof(WaterStationController), typeof(PumpBuilding), typeof(PowerGenerator), typeof(PowerGeneratorSocket),
            typeof(PowerBuildingSwitch), typeof(MobilePowerStationController), typeof(PowerStationController), typeof(PowerBoxDoor), typeof(ItemContainerDoors),
            typeof(OpenableObject), typeof(OpenableObject2), typeof(OpenableObject3), typeof(CodeHorizon.Optimization.HangarOpenableDoors),
            // Interaction audit (review\interaction-audit.md): water valves (river flow), lamps / fires, the dredge
            // bucket switches and the car jack handle (it lifts the truck: every PC must lift it the same way).
            typeof(RotateValve), typeof(EnableLightSource), typeof(DredgeBucket), typeof(CarJack),
        };

        private static readonly Dictionary<string, bool> SwitchMethodCache = new Dictionary<string, bool>();
        private static bool _replaying;

        private static readonly AccessTools.FieldRef<UseAction, bool> UseActiveRef = GoldSync.TryRef<UseAction, bool>("Active");

        private static bool IsSwitchMethod(GameObject target, string method)
        {
            if (target == null || string.IsNullOrEmpty(method)) return false;
            // A switch type's method that opens a menu (water tower / cistern "OpenWateringMenu") is the user's
            // own UI: replayed, it popped the watering menu open on the other players' screens.
            if (method.IndexOf("Menu", StringComparison.Ordinal) >= 0 || method.StartsWith("Show", StringComparison.Ordinal)) return false;
            foreach (MonoBehaviour mb in target.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                for (Type t = mb.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    if (!SwitchTypes.Contains(t)) continue;
                    string ck = t.FullName + "." + method;
                    if (!SwitchMethodCache.TryGetValue(ck, out bool has))
                    {
                        has = false;
                        foreach (MethodInfo m in mb.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            if (m.Name == method) { has = true; break; }
                        SwitchMethodCache[ck] = has;
                    }
                    if (has) return true;
                }
            }
            return false;
        }

        private static string MethodFor(UseAction u, byte kind)
        {
            switch (kind)
            {
                case 0: return u.UseFunctionName;
                case 1: return u.MyUseBehaviour == UseBehaviour.Hold ? u.ReleaseFunctionName : null;
                case 2: return u.UseFunctionName2 == "Brake" ? u.ReleaseFunctionName : u.UseFunctionName2;
                default: return u.MyUseBehaviour == UseBehaviour.Hold ? u.ReleaseFunctionName2 : null;
            }
        }

        private static void OnUse(UseAction u, byte kind, bool wasActive)
        {
            if (_replaying || !wasActive || u == null || !Synced()) return;
            string method = MethodFor(u, kind);
            GameObject target = u.GameObjectReference != null ? u.GameObjectReference : u.gameObject;
            // A car jack under a truck sits below the machine, but it is a loose tool, not the vehicle sync's.
            if (string.IsNullOrEmpty(method) || (IsVehicle(u) && u.GetComponentInParent<CarJack>(true) == null)) return;
            if (!IsSwitchMethod(target, method)) return; // only world switches; never pickups, shops, menus
            SaveMe root = u.GetComponentInParent<SaveMe>();
            string rootKey = RootKey(root, true); // trailer-mounted plants: "t<id>"
            if (rootKey == null) return;
            UseAction[] onGo = u.GetComponents<UseAction>();
            int idx = Array.IndexOf(onGo, u);
            var e = new WorldEvent
            {
                Sub = SubUse, RootKey = rootKey, Path = PathBelow(root.transform, u.transform), Idx = idx, Kind = kind,
                Origin = GoldSync.StreamId, Seq = NextEventSeq(),
            };
            byte[] msg = Encode(e, 0);
            Journal(msg);
            TrafficC.Broadcast(TrafficC.Cat.World, msg, true);
        }

        private static void ApplyUse(WorldEvent e)
        {
            GameObject root = FindRoot(e.RootKey);
            if (root == null) return;
            Transform t = e.Path.Length == 0 ? root.transform : root.transform.Find(e.Path);
            if (t == null) return;
            UseAction[] onGo = t.GetComponents<UseAction>();
            if (e.Idx >= onGo.Length) return;
            UseAction u = onGo[e.Idx];
            _replaying = true;
            try
            {
                switch (e.Kind)
                {
                    case 0: u.StartUsing(); break;
                    case 1: u.StopUsing(); break;
                    case 2: u.StartUsingSecond(); break;
                    default: u.StopUsingSecond(); break;
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("World sync: switch replay failed: " + ex.Message); }
            finally { _replaying = false; }
        }

        [HarmonyPatch]
        private static class PatchUse
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string n in new[] { "StartUsing", "StopUsing", "StartUsingSecond", "StopUsingSecond" })
                {
                    MethodInfo m = GoldSync.Need(typeof(UseAction), n, "switch / lever sync (" + n + ")", Type.EmptyTypes);
                    if (m != null) yield return m;
                }
            }

            private static bool Prepare()
            {
                foreach (var _ in TargetMethods()) return true;
                return false;
            }

            private static void Prefix(UseAction __instance, out bool __state) =>
                __state = __instance != null && UseActiveRef != null && UseActiveRef(__instance);

            private static void Postfix(UseAction __instance, MethodBase __originalMethod, bool __state)
            {
                try
                {
                    byte kind = __originalMethod.Name == "StartUsing" ? (byte)0
                        : __originalMethod.Name == "StopUsing" ? (byte)1
                        : __originalMethod.Name == "StartUsingSecond" ? (byte)2 : (byte)3;
                    OnUse(__instance, kind, __state);
                }
                catch (Exception ex) { Plugin.Log.LogError("World sync: " + ex.Message); }
            }
        }

        // ================================================================== explosions

        private static GameObject _explosionPrefab;
        private static bool _boomReplay;

        [HarmonyPatch]
        private static class PatchExplosion
        {
            private static MethodBase TargetMethod() => GoldSync.Need(typeof(Explosion), nameof(Explosion.PlayExplosionEffects), "explosions on every PC");
            private static bool Prepare() => TargetMethod() != null;

            private static void Postfix(Explosion __instance)
            {
                if (_boomReplay || __instance == null || !Synced()) return;
                try
                {
                    Vector3 p = __instance.transform.position, s = __instance.ExplosionSize;
                    var ms = GoldSync.Begin(SubBoom, out BinaryWriter w);
                    w.Write(p.x); w.Write(p.y); w.Write(p.z);
                    w.Write(s.x); w.Write(s.y); w.Write(s.z * 2f); // the setter halves z
                    TrafficC.Broadcast(TrafficC.Cat.World, ms.ToArray(), true);
                }
                catch (Exception ex) { Plugin.Log.LogError("World sync: " + ex.Message); }
            }
        }

        private static void ReceiveBoom(BinaryReader r)
        {
            var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var s = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (!Synced() || float.IsNaN(p.x) || float.IsNaN(s.x)) return;
            if (_explosionPrefab == null)
                foreach (Dynamite d in Resources.FindObjectsOfTypeAll<Dynamite>())
                    if (d != null && d.explosionPrefab != null) { _explosionPrefab = d.explosionPrefab; break; }
            if (_explosionPrefab == null) return;
            _boomReplay = true;
            try
            {
                GameObject go = UnityEngine.Object.Instantiate(_explosionPrefab, p, Quaternion.identity);
                Explosion ex = go.GetComponent<Explosion>();
                if (ex == null) { UnityEngine.Object.Destroy(go); return; }
                ex.ExplosionSize = s;
                if (ex.explosionArea != null) ex.explosionArea.enabled = false; // no crater: TerrainSync brings it
                go.SetActive(true);
                go.transform.parent = null;
                Camera cam = Camera.main;
                ex.PlayExplosionEffects(cam != null ? Vector3.Distance(cam.transform.position, p) : 100f);
                if (ex.objectPusher != null) ex.objectPusher.SetActive(true); // objects this PC simulates, rocks, machines
                UnityEngine.Object.Destroy(go, 1f);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("World sync: explosion replay failed: " + ex.Message); }
            finally { _boomReplay = false; }
        }

        // ================================================================== paint

        private static int _paintShop;
        private static bool _paintApplying;

        [HarmonyPatch]
        private static class PatchPaintShop
        {
            private static MethodBase TargetMethod() => GoldSync.Need(typeof(PaintGUI), "BuyConfirm", "paint sync");
            private static bool Prepare() => TargetMethod() != null;
            private static void Prefix() => _paintShop++;

            private static Exception Finalizer(Exception __exception)
            {
                if (_paintShop > 0) _paintShop--;
                return __exception;
            }
        }

        [HarmonyPatch]
        private static class PatchPaintMe
        {
            private static MethodBase TargetMethod() => GoldSync.Need(typeof(MachinePainter), nameof(MachinePainter.PaintMe), "paint sync");
            private static bool Prepare() => TargetMethod() != null;

            private static void Postfix(MachinePainter __instance, string paint_name, string fullPath, string workshopName)
            {
                if (_paintShop <= 0 || _paintApplying || __instance == null || !Synced()) return;
                try
                {
                    SaveMe root = __instance.GetComponentInParent<SaveMe>();
                    string key = RootKey(root, true);
                    if (key == null) return;
                    var e = new WorldEvent
                    {
                        Sub = SubPaint, RootKey = key, Path = PathBelow(root.transform, __instance.transform),
                        Name = paint_name ?? "", Full = fullPath ?? "", Ws = workshopName ?? "",
                        Origin = GoldSync.StreamId, Seq = NextEventSeq(),
                    };
                    byte[] msg = Encode(e, 0);
                    Journal(msg);
                    TrafficC.Broadcast(TrafficC.Cat.World, msg, true);
                }
                catch (Exception ex) { Plugin.Log.LogError("World sync: " + ex.Message); }
            }
        }

        private static void ApplyPaint(WorldEvent e)
        {
            GameObject root = FindRoot(e.RootKey);
            Transform t = root == null ? null : e.Path.Length == 0 ? root.transform : root.transform.Find(e.Path);
            MachinePainter painter = t != null ? t.GetComponent<MachinePainter>() : null;
            if (painter == null) return;
            _paintApplying = true;
            try { painter.PaintMe(e.Name, e.Full.Length > 0 ? e.Full : null, e.Ws.Length > 0 ? e.Ws : null); }
            catch (Exception ex) { Plugin.Log.LogWarning("World sync: paint failed (paint not installed here?): " + ex.Message); }
            finally { _paintApplying = false; }
        }

        // ================================================================== receive

        internal static void Receive(byte sub, BinaryReader r)
        {
            switch (sub)
            {
                case SubFlags: ReceiveFlags(r); break;
                case SubBoom: ReceiveBoom(r); break;
                case SubUse:
                case SubPaint: ReceiveEvent(sub, r); break;
                case SubForge: ReceiveForge(r); break;
                case SubFuel: ReceiveFuel(r); break;
                case SubFuelOp: ReceiveFuelOp(r); break;
                case SubWater: ReceiveWater(r); break;
                case SubWaterOp: ReceiveWaterOp(r); break;
                case SubTable: ReceiveTable(r); break;
                case SubHood: ReceiveHood(r); break;
            }
        }

        // ================================================================== random world events

        /// <summary>
        /// Lightning hitting the power generator is rolled when this PC's first thunder of a storm
        /// strikes (the weather is the host's). The roll is seeded with the shared day, so every PC
        /// destroys the same consumers instead of each rolling its own.
        /// </summary>
        [HarmonyPatch]
        private static class PatchThunder
        {
            private static MethodBase TargetMethod() => GoldSync.Need(typeof(SeasonController), "Thunder_OnFirsthunder", "same lightning damage on every PC");
            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(out UnityEngine.Random.State? __state)
            {
                __state = null;
                if (!Synced() || !global::Singleton<Gsm>.IsInstanced()) return;
                Gsm g = global::Singleton<Gsm>.Instance;
                __state = UnityEngine.Random.state;
                UnityEngine.Random.InitState(g.DayCount * 7919 + g.Season * 104729 + 31);
            }

            private static Exception Finalizer(Exception __exception, UnityEngine.Random.State? __state)
            {
                if (__state.HasValue) UnityEngine.Random.state = __state.Value;
                return __exception;
            }
        }

        /// <summary>
        /// Bear attacks (a worker hurt, a power cable broken) are random: rolled by the host only, a client
        /// rolling its own would hurt other workers / break other cables than the host.
        /// </summary>
        [HarmonyPatch]
        private static class PatchBears
        {
            private static MethodBase TargetMethod() => GoldSync.Need(typeof(SeasonController), nameof(SeasonController.Penalties), "host-only bear attacks");
            private static bool Prepare() => TargetMethod() != null;

            private static void Postfix(SeasonController __instance)
            {
                if (__instance == null || Net.IsHost || !Synced()) return;
                __instance.BearAttackWorker = false;
                __instance.BearAttackCable = false;
            }
        }
    }

    /// <summary>
    /// Switch / lever replays (sub 20) and paint (sub 22) are EVENTS: unlike saved states they cannot be
    /// re-sent as a state, and a switch replay is not idempotent (a toggle applied twice is undone).
    /// Every event carries its author (GoldSync.StreamId: new for every sync session of a PC, so a player that
    /// rejoins is never mistaken for its old self) and a per-author sequence and a flags byte (last byte):
    /// 0 = live, 1 = journal replay, 2 = end of the journal replay.
    ///
    /// JOIN JOURNAL (host): events made after the world was saved for a joining player are not in that
    /// save, and the joiner drops live events while it loads. The host journals every event (own and
    /// relayed) from each transfer save on (generations like PurchaseSync: a slow joiner replays from
    /// the save it actually received) and replays them to the player once it reports the world as
    /// loaded, followed by an end marker. SaveTransfer drives it: ClearJournal at the save,
    /// AssignJournal per peer of that save, ReplayJournal at its Loaded.
    /// JOINER: from the moment it plays the shared world (only when a host catch-up follows, i.e. it
    /// just reported Loaded), live events are held back until the end marker arrives (at most HoldMax):
    /// the journal (older) is applied first, then the held live events in their order, every event exactly
    /// once ((author, sequence) already applied = skipped). So a switch toggled during the transfer ends
    /// in the same position on every PC, and a toggle the joiner saw live is never applied a second time.
    /// Events wait until the world index is built (fixed objects are found through it).
    ///
    /// INTERPLAY WITH THE CORE CATCH-UP (SaveTransfer, Net): between its Loaded and the host's CatchUp
    /// marker the joiner's Net layer drops every live world message, and the host sends, on the same
    /// ordered reliable channel: CatchUp, this journal (flag 1) + end marker (flag 2), then its own world
    /// journal (SaveTransfer.JournalWorld), which holds the SAME switch / paint events again as the raw
    /// live messages (flag 0, same author + sequence). Those copies arrive after the end marker and are
    /// skipped by the (author, sequence) check, so nothing is applied twice; live events after them are
    /// newer than both journals. The (author, sequence) set is kept only for that catch-up window
    /// (DedupeWindow after the end marker), not for the whole session.
    /// </summary>
    internal static partial class WorldSync
    {
        private const byte EvLive = 0, EvJournal = 1, EvEnd = 2;
        private const float HoldMax = 8f;
        private const int InboxMax = 512;

        private sealed class WorldEvent
        {
            public byte Sub;
            public string RootKey = "", Path = "";
            public int Idx;
            public byte Kind;
            public string Name = "", Full = "", Ws = "";
            public ulong Origin;
            public uint Seq;
            public byte Flags;
        }

        private static uint _eventSeq;
        private static readonly List<WorldEvent> Inbox = new List<WorldEvent>();
        private static readonly List<WorldEvent> Held = new List<WorldEvent>();
        private static readonly HashSet<KeyValuePair<ulong, uint>> Applied = new HashSet<KeyValuePair<ulong, uint>>();
        private static bool _holding;
        private static float _holdUntil, _dedupeUntil;
        private const float DedupeWindow = 60f; // after the end marker: the core journal's copies arrive right behind it

        private static uint NextEventSeq()
        {
            if (++_eventSeq == 0) _eventSeq = 1;
            return _eventSeq;
        }

        private static byte[] Encode(WorldEvent e, byte flags)
        {
            var ms = GoldSync.Begin(e.Sub, out BinaryWriter w);
            w.Write(e.RootKey);
            w.Write(e.Path);
            if (e.Sub == SubUse)
            {
                w.Write((byte)e.Idx);
                w.Write(e.Kind);
            }
            else
            {
                w.Write(e.Name);
                w.Write(e.Full);
                w.Write(e.Ws);
            }
            w.Write(e.Origin);
            w.Write(e.Seq);
            w.Write(flags); // always the last byte (ReplayJournal rewrites it)
            return ms.ToArray();
        }

        private static WorldEvent Decode(byte sub, BinaryReader r)
        {
            var e = new WorldEvent { Sub = sub, RootKey = r.ReadString(), Path = r.ReadString() };
            if (sub == SubUse)
            {
                e.Idx = r.ReadByte();
                e.Kind = r.ReadByte();
            }
            else
            {
                e.Name = r.ReadString();
                e.Full = r.ReadString();
                e.Ws = r.ReadString();
            }
            e.Origin = r.ReadUInt64();
            e.Seq = r.ReadUInt32();
            e.Flags = r.ReadByte();
            return e;
        }

        private static void ReceiveEvent(byte sub, BinaryReader r)
        {
            WorldEvent e = Decode(sub, r);
            if (!Synced()) return; // loading: the host's journal brings it after the load
            if (Net.IsHost && e.Flags == EvLive && e.Origin != GoldSync.StreamId) Journal(Encode(e, EvLive)); // relayed by Net
            if (Inbox.Count >= InboxMax) Inbox.RemoveAt(0);
            Inbox.Add(e);
            ProcessInbox(Time.unscaledTime);
        }

        /// <summary>Applies queued events in arrival order (journal first, live ones held until its end).</summary>
        private static void ProcessInbox(float now)
        {
            if (!_indexed) return;
            for (int i = 0; i < Inbox.Count; i++)
            {
                WorldEvent e = Inbox[i];
                if (e.Flags == EvEnd) { if (!Net.IsHost) EndHold(); }
                else if (e.Flags == EvJournal) ApplyEvent(e);
                else if (_holding && !Net.IsHost) Held.Add(e);
                else ApplyEvent(e);
            }
            Inbox.Clear();
            if (_holding && now > _holdUntil) EndHold(); // no journal from this host (or a stalled link)
        }

        private static void EndHold()
        {
            _holding = false;
            _dedupeUntil = Time.unscaledTime + DedupeWindow;
            for (int i = 0; i < Held.Count; i++) ApplyEvent(Held[i]);
            Held.Clear();
        }

        private static void ApplyEvent(WorldEvent e)
        {
            // Duplicates exist only during a catch-up (journal + core journal + held live copies): outside
            // that window nothing is recorded, so the set does not grow over a long session.
            if (e.Origin != 0 && (_holding || Time.unscaledTime < _dedupeUntil))
            {
                if (Applied.Count > 8192) Applied.Clear();
                if (!Applied.Add(new KeyValuePair<ulong, uint>(e.Origin, e.Seq))) return; // seen live and in the journal
            }
            else if (Applied.Count > 0) Applied.Clear();
            if (e.Sub == SubUse) ApplyUse(e);
            else ApplyPaint(e);
        }

        /// <summary>This PC starts playing a shared world: a client holds live events until the journal is in.</summary>
        private static void OnSyncStart(float now)
        {
            Inbox.Clear();
            Held.Clear();
            Applied.Clear();
            // Only a client that just reported Loaded gets a journal + end marker (SaveTransfer, same frame as
            // its world became ready); a client that only came back from a load of its own gets none and must
            // not hold live events for HoldMax.
            _holding = !Net.IsHost && SaveTransfer.HoldWorldMessages;
            _holdUntil = now + HoldMax;
            _dedupeUntil = _holding ? now + HoldMax + DedupeWindow : 0f;
        }

        private static void DropEvents()
        {
            Inbox.Clear();
            Held.Clear();
            Applied.Clear();
            _holding = false;
            _dedupeUntil = 0f;
        }

        // ---------------------------------------------------------------- host journal

        private const int JournalMax = 2000;
        private const float JoinTimeout = 600f;
        private static readonly List<byte[]> JournalList = new List<byte[]>();
        private static long _journalBase, _genStart = -1;
        private static bool _journalOn, _genUnassigned, _journalWarned;
        private static readonly Dictionary<ulong, long> PeerStart = new Dictionary<ulong, long>();
        private static readonly Dictionary<ulong, float> PeerSince = new Dictionary<ulong, float>();
        private static readonly List<ulong> GonePeers = new List<ulong>();
        private static float _nextTrim;

        private static void Journal(byte[] msg)
        {
            if (!_journalOn || !Net.IsHost) return;
            if (JournalList.Count >= JournalMax)
            {
                JournalList.RemoveAt(0);
                _journalBase++;
                if (!_journalWarned) { _journalWarned = true; Plugin.Log.LogWarning("World sync: join journal full, oldest switch events dropped"); }
            }
            JournalList.Add(msg);
        }

        /// <summary>Host: the world is being saved for joining players; journal events from now on.</summary>
        internal static void ClearJournal()
        {
            if (!Net.Active || !Net.IsHost) return;
            _genStart = _journalBase + JournalList.Count;
            _genUnassigned = true;
            _journalOn = true;
            TrimJournal();
        }

        /// <summary>Host: <paramref name="peer"/> receives the save of the newest generation.</summary>
        internal static void AssignJournal(ulong peer)
        {
            if (!Net.Active || !Net.IsHost || _genStart < 0) return;
            _genUnassigned = false;
            PeerStart[peer] = _genStart;
            PeerSince[peer] = Time.unscaledTime;
        }

        /// <summary>Host: <paramref name="peer"/> loaded the world: the events it missed, then the end marker.</summary>
        internal static void ReplayJournal(ulong peer)
        {
            if (!Net.Active || !Net.IsHost || Net.Transport == null) return;
            try
            {
                long start = PeerStart.TryGetValue(peer, out long st) ? st : _genStart;
                PeerStart.Remove(peer);
                PeerSince.Remove(peer);
                int n = 0;
                if (start >= 0)
                    for (int i = (int)Math.Max(0, Math.Min(JournalList.Count, start - _journalBase)); i < JournalList.Count; i++)
                    {
                        byte[] copy = (byte[])JournalList[i].Clone();
                        copy[copy.Length - 1] = EvJournal;
                        Net.Transport.Send(peer, copy, true);
                        TrafficC.Sent(TrafficC.Cat.World, copy.Length);
                        n++;
                    }
                byte[] end = Encode(new WorldEvent { Sub = SubUse }, EvEnd);
                Net.Transport.Send(peer, end, true);
                if (n > 0) Plugin.Log.LogInfo($"World sync: replayed {n} switch / paint events to {Net.NameOf(peer)}");
                TrimJournal();
            }
            catch (Exception ex) { Plugin.Log.LogError("World sync: journal replay failed: " + ex.Message); }
        }

        /// <summary>Drops entries no pending joiner needs; stops journaling once nobody waits.</summary>
        private static void TrimJournal()
        {
            GonePeers.Clear();
            foreach (ulong p in PeerStart.Keys)
                if (!Net.Peers.Contains(p) ||
                    (PeerSince.TryGetValue(p, out float t) && Time.unscaledTime - t > JoinTimeout && !SaveTransfer.InTransit(p)))
                    GonePeers.Add(p);
            foreach (ulong p in GonePeers) { PeerStart.Remove(p); PeerSince.Remove(p); }
            long keep = _genStart < 0 ? _journalBase + JournalList.Count : _genStart;
            foreach (long v in PeerStart.Values) keep = Math.Min(keep, v);
            int drop = (int)Math.Max(0, Math.Min(JournalList.Count, keep - _journalBase));
            if (drop > 0) { JournalList.RemoveRange(0, drop); _journalBase += drop; }
            if (PeerStart.Count == 0 && !_genUnassigned)
            {
                _journalOn = false;
                _journalBase += JournalList.Count;
                JournalList.Clear();
                _genStart = -1;
            }
        }

        private static void ResetJournal()
        {
            JournalList.Clear();
            _journalBase = 0;
            _genStart = -1;
            _journalOn = _genUnassigned = false;
            PeerStart.Clear();
            PeerSince.Clear();
        }

        /// <summary>Every frame while synced: queued events, the joiner's hold timeout, the host's journal trim.</summary>
        private static void EventTick(float now)
        {
            if (Inbox.Count > 0 || _holding) ProcessInbox(now);
            if (Net.IsHost && _journalOn && now >= _nextTrim)
            {
                _nextTrim = now + 5f;
                TrimJournal();
            }
        }
    }
}
