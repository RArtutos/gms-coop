// WIRING (for the lead; this file does not edit Net.cs / Plugin.cs):
//   Net.cs, Net.Handle switch:   case (Msg)10: PurchaseSync.Receive(r); break;
//                                (optionally add "Purchase = 10," to enum Msg and use Msg.Purchase)
//   Plugin.cs, Plugin.Update():  PurchaseSync.Tick();      // after TutorialSync.Tick();
// Harmony patches are picked up by the existing _harmony.PatchAll(typeof(Plugin).Assembly).
// WIRING (round 3, for the lead): SaveTransfer.TryStartSave "PurchaseSync.ClearJournal();" next to
//   TerrainSync.ClearJournal(), and on Ctl.Loaded (host) "PurchaseSync.ReplayJournal(from);" next to
//   TerrainSync.ReplayJournal(from). Purchases made while the world is in transit then reach the client.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GoldDigger;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GMSCoop
{
    /// <summary>
    /// Makes purchases of one player appear for the others. Money is NOT touched here:
    /// CashSync already replicates the wallet, so the receiver only recreates the result.
    ///
    /// Synced:
    ///  - Shop items   (CashRegisterGUI.BuyDelayed / BuyDelayedWithMarkers -> SetPosition)
    ///  - Trailers     (CashRegisterGUI.SpawnTrailer / TrailerSpawner.SpawnTrailer with buy=true)
    ///  - Vehicles     (CashRegisterCarGUI.BuyObject -> Depot.Teleport -> DepotPlace.TransportGameObject)
    ///    Sent: prefab SaveID, final world pose, parent path, depot place path, active state,
    ///    rent/reinforced flags and a MachineSync id for anything with a MachineController.
    ///  - Rental buyout / rental return (BuyoutRentedShopItemGUI)
    ///  - Parcel ownership / rent (polled from LaptopGlobalManager.LotList)
    ///  - Laptop tab unlocks (polled GameStateManager.*Buyed bools, unlock only)
    ///  - Worker hiring (WorkersToHire.Hire, LaptopWorkersHire.HireWorker), worker sent as JSON
    ///
    /// The game has no per-instance network id. Spawned objects are created fresh on the receiver
    /// from the prefab; machines get the sender's MachineSync id. Existing objects (rental
    /// buyout/return, sale, parcel reset) are named by ObjectSync key, else MachineSync id, else
    /// SaveID + nearest position (unkeyed objects only). Messages that name objects wait until keys
    /// are shared (a client that just joined, both directions); every message carries origin +
    /// sequence and is applied once.
    /// </summary>
    internal static class PurchaseSync
    {
        internal const Msg MsgPurchase = (Msg)10;

        private enum Kind : byte
        {
            Spawn = 1,
            RentBuyout = 2,
            RentReturn = 3,
            Lot = 4,
            Tabs = 5,
            Worker = 6,
            Sell = 7,
            ParcelReset = 8,
            Move = 9,
        }

        // Set while applying remote data so our own patches/polls do not echo it back.
        private static bool _applying;

        /// <summary>True while a remote purchase / sale / reset is applied here (other systems must not echo what it moves).</summary>
        internal static bool Applying => _applying;

        private static readonly FieldInfo IsReinforcedField = AccessTools.Field(typeof(ItemDescriptor), "IsReinforced");

        // =====================================================================
        // Detection context
        // =====================================================================

        // >0 while a BuyDelayed* coroutine step runs: SetPosition calls then place NEW items.
        // (BuyObjects itself also calls SetPosition to shuffle existing items; that is outside.)
        private static int _itemBuyDepth;

        /// <summary>State captured during one MoveNext step of a trailer/vehicle purchase coroutine.</summary>
        private class Ctx
        {
            public bool AddToClaim;        // vehicles: the game adds them to the closest claim
            public SellableObject Obj;     // first object passed to Depot.Teleport in this step
            public bool TeleportSetActive; // setActive argument of that Depot.Teleport
            public DepotPlace Place;       // place that received it (DepotPlace.TransportGameObject)
        }

        private static Ctx _ctx;

        // =====================================================================
        // Polling (parcels + laptop tabs)
        // =====================================================================

        private static float _nextPoll;
        private static LaptopGlobalManager _lotMgr;
        private static bool[] _lotOwner;
        private static bool[] _lotRented;
        private static int[] _lotTransport;
        private static GoldDigger.GameStateManager _tabGsm;
        private static int _tabMask = -1;
        private static int _objectKeyCounter;

        /// <summary>Called from Plugin.Update.</summary>
        internal static void Tick()
        {
            if (!Net.Active)
            {
                // Ids and queued messages of the session that ended.
                if (Applied.Count > 0) { Applied.Clear(); AppliedOrder.Clear(); }
                Inbox.Clear();
                Outbox.Clear();
                PendingMoves.Clear();
                Buys.Clear();
                BuyOfIter.Clear();
                // The host's journal belongs to the session that ended (absolute indexes keep growing).
                if (_journalOn || Journal.Count > 0 || PeerStart.Count > 0)
                {
                    _journalBase += Journal.Count;
                    Journal.Clear();
                    PeerStart.Clear();
                    PeerSince.Clear();
                    _genStart = -1;
                    _journalOn = false;
                    _genUnassigned = false;
                }
                return;
            }
            // Messages that name objects wait until keys mean the same object on every PC.
            if ((Inbox.Count > 0 || Outbox.Count > 0) && ObjectSync.KeysReady) FlushQueues();
            if (Buys.Count > 0) TickBuys();
            if (PendingMoves.Count > 0) FlushMoves();
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.5f;
            if (_journalOn && Net.IsHost) Trim();
            LogStats(Time.unscaledTime);
            if (!Net.WorldLive)
            {
                // Client still in its own pre-join world or loading the host's: nothing it changes
                // there is shared. Take a fresh baseline once the host world is ready.
                _lotMgr = null;
                _tabGsm = null;
                return;
            }
            try { PollLots(); } catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: lot poll failed: " + ex.Message); }
            try { PollTabs(); } catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: tab poll failed: " + ex.Message); }
        }

        private static bool LotOwner(LotDescriptor lot)
        {
            try { return lot != null && lot.IsOwner; }
            catch (Exception) { return false; } // Terrains list empty / destroyed
        }

        private static void PollLots()
        {
            if (!global::Singleton<LaptopGlobalManager>.IsInstanced()) return;
            LaptopGlobalManager mgr = global::Singleton<LaptopGlobalManager>.Instance;
            List<LotDescriptor> lots = mgr.LotList;
            if (lots == null) return;

            // New world / new manager: take a baseline without sending anything.
            if (mgr != _lotMgr || _lotOwner == null || _lotOwner.Length != lots.Count)
            {
                _lotMgr = mgr;
                _lotOwner = new bool[lots.Count];
                _lotRented = new bool[lots.Count];
                _lotTransport = new int[lots.Count];
                for (int i = 0; i < lots.Count; i++)
                {
                    _lotOwner[i] = LotOwner(lots[i]);
                    _lotRented[i] = lots[i] != null && lots[i].IsRented;
                    _lotTransport[i] = lots[i] != null ? lots[i].transportPlace : 0;
                }
                return;
            }

            for (int i = 0; i < lots.Count; i++)
            {
                LotDescriptor lot = lots[i];
                if (lot == null) continue;
                bool owner = LotOwner(lot), rented = lot.IsRented;
                int transport = lot.transportPlace;
                if (owner == _lotOwner[i] && rented == _lotRented[i] && transport == _lotTransport[i]) continue;
                _lotOwner[i] = owner;
                _lotRented[i] = rented;
                _lotTransport[i] = transport;

                var ms = BeginMsg(Kind.Lot, out BinaryWriter w);
                w.Write(i);
                w.Write(lot.TerrainObjectName ?? "");
                w.Write(owner);
                w.Write(rented);
                w.Write(transport); // machine places bought on the parcel (bank: "transport" upgrade)
                Send(ms.ToArray());
                Plugin.Log.LogInfo($"Purchase sync: sent lot {lot.TerrainObjectName} owner={owner} rented={rented} places={transport}");
            }
        }

        private static int TabMask(GoldDigger.GameStateManager g) =>
            (g.TransportBuyed ? 1 : 0) | (g.WorkersBuyed ? 2 : 0) | (g.WarehouseBuyed ? 4 : 0) |
            (g.MachineShopBuyed ? 8 : 0) | (g.NotificationsBuyed ? 16 : 0) | (g.BuyoutRentedBuyed ? 32 : 0);

        private static void PollTabs()
        {
            if (!global::Singleton<GoldDigger.GameStateManager>.IsInstanced()) return;
            var g = global::Singleton<GoldDigger.GameStateManager>.Instance;
            int mask = TabMask(g);
            if (g != _tabGsm || _tabMask < 0)
            {
                _tabGsm = g;
                _tabMask = mask; // baseline after load
                return;
            }
            if (mask == _tabMask) return;
            _tabMask = mask;

            var ms = BeginMsg(Kind.Tabs, out BinaryWriter w);
            w.Write(mask);
            Send(ms.ToArray());
            Plugin.Log.LogInfo("Purchase sync: sent laptop tabs mask " + mask);
        }

        // =====================================================================
        // Sending
        // =====================================================================

        // ---------- journal (host): purchases since the save that is being sent to a joining client ----------

        // Every save for a joiner starts a new generation (its first journal index). A peer replays from
        // the generation of the save it actually received (AssignJournal), so a slow joiner still gets
        // everything since ITS save when another save for a later joiner happened meanwhile. Replays may
        // overlap live delivery: receivers drop messages they already applied (origin + sequence).
        private const int JournalMax = 4000;
        private static readonly List<byte[]> Journal = new List<byte[]>();
        private static long _journalBase;                 // absolute index of Journal[0]
        private static long _genStart = -1;               // absolute start of the newest generation
        private static readonly Dictionary<ulong, long> PeerStart = new Dictionary<ulong, long>();
        private static readonly Dictionary<ulong, float> PeerSince = new Dictionary<ulong, float>();
        private static readonly List<ulong> _gone = new List<ulong>();
        // A joiner that never reports Loaded stops holding the journal. Not while its world is still being
        // sent / loaded (a slow link can take longer; a dead joiner is dropped by the keep-alive anyway).
        private const float JoinTimeout = 600f;
        private static bool _assignWired, _genUnassigned;
        private static bool _journalOn;
        private static bool _warnedFull;

        /// <summary>Host: the world is being saved for a joining client; record purchases from now on.</summary>
        internal static void ClearJournal()
        {
            if (!Net.Active || !Net.IsHost) return;
            _genStart = _journalBase + Journal.Count;
            _genUnassigned = true;
            _journalOn = true;
            Trim();
        }

        /// <summary>Host: <paramref name="peer"/> receives the save of the newest generation.</summary>
        internal static void AssignJournal(ulong peer)
        {
            if (!Net.Active || !Net.IsHost || _genStart < 0) return;
            _assignWired = true;
            _genUnassigned = false;
            PeerStart[peer] = _genStart;
            PeerSince[peer] = Time.unscaledTime;
        }

        /// <summary>Host: <paramref name="peer"/> loaded the world; send it the purchases it missed meanwhile.</summary>
        internal static void ReplayJournal(ulong peer)
        {
            if (!Net.Active || !Net.IsHost || Net.Transport == null) return;
            long start = PeerStart.TryGetValue(peer, out long s) ? s : _genStart;
            PeerStart.Remove(peer);
            PeerSince.Remove(peer);
            int from = (int)Math.Max(0, Math.Min(Journal.Count, start - _journalBase));
            if (start >= 0)
                for (int i = from; i < Journal.Count; i++) Net.Transport.Send(peer, Journal[i], true);
            int n = start >= 0 ? Journal.Count - from : 0;
            if (n > 0) Plugin.Log.LogInfo($"Purchase sync: replayed {n} purchase messages to {Net.NameOf(peer)}");
            Trim();
        }

        /// <summary>Drops journal entries no pending joiner needs; stops journaling once nobody waits.</summary>
        private static void Trim()
        {
            _gone.Clear();
            foreach (ulong p in PeerStart.Keys)
                if (!Net.Peers.Contains(p) ||
                    (PeerSince.TryGetValue(p, out float t) && Time.unscaledTime - t > JoinTimeout && !SaveTransfer.InTransit(p)))
                    _gone.Add(p);
            foreach (ulong p in _gone) { PeerStart.Remove(p); PeerSince.Remove(p); }

            long keep = _genStart < 0 ? _journalBase + Journal.Count : _genStart;
            foreach (long v in PeerStart.Values) keep = Math.Min(keep, v);
            int drop = (int)Math.Max(0, Math.Min(Journal.Count, keep - _journalBase));
            if (drop > 0) { Journal.RemoveRange(0, drop); _journalBase += drop; }
            // Without AssignJournal wiring the newest generation stays journaled until the next save.
            if (_assignWired && PeerStart.Count == 0 && !_genUnassigned)
            {
                _journalOn = false;
                _journalBase += Journal.Count;
                Journal.Clear();
                _genStart = -1;
            }
        }

        private static void AddToJournal(byte[] data)
        {
            if (!_journalOn || !Net.IsHost) return;
            if (Journal.Count >= JournalMax)
            {
                Journal.RemoveAt(0);
                _journalBase++;
                if (!_warnedFull) { _warnedFull = true; Plugin.Log.LogWarning("Purchase sync: journal full, oldest purchases dropped"); }
            }
            Journal.Add(data);
        }

        // ---------- message ids (replay / live overlap) ----------

        // Sequence = random per game process (high 32 bits) + counter: a restarted player with the same
        // Steam id never repeats an id the others already applied.
        private static readonly ulong SeqBase = (ulong)(uint)Guid.NewGuid().GetHashCode() << 32;
        private static uint _msgSeq;
        private readonly struct MsgId : IEquatable<MsgId>
        {
            public readonly ulong Origin, Seq;
            public MsgId(ulong origin, ulong seq) { Origin = origin; Seq = seq; }
            public bool Equals(MsgId o) => Origin == o.Origin && Seq == o.Seq;
            public override bool Equals(object o) => o is MsgId m && Equals(m);
            public override int GetHashCode() => (Origin * 0x9E3779B97F4A7C15UL ^ Seq).GetHashCode();
        }

        // Applied message ids, oldest evicted first (a replay never reaches back this far: the journal
        // holds at most JournalMax messages). A KeyValuePair key would hash by its first field only.
        private const int AppliedMax = 20000;
        private static readonly HashSet<MsgId> Applied = new HashSet<MsgId>();
        private static readonly Queue<MsgId> AppliedOrder = new Queue<MsgId>();

        private static bool MarkApplied(ulong origin, ulong seq)
        {
            var id = new MsgId(origin, seq);
            if (!Applied.Add(id)) return false;
            AppliedOrder.Enqueue(id);
            if (AppliedOrder.Count > AppliedMax) Applied.Remove(AppliedOrder.Dequeue());
            return true;
        }

        /// <summary>Starts a purchase message: kind, origin LocalId, sequence (receivers apply each once).</summary>
        private static MemoryStream BeginMsg(Kind kind, out BinaryWriter w)
        {
            var ms = Net.Writer(MsgPurchase);
            w = new BinaryWriter(ms);
            w.Write((byte)kind);
            w.Write(Net.LocalId);
            w.Write(SeqBase | ++_msgSeq);
            return ms;
        }

        private static void Send(byte[] data)
        {
            if (data == null) return;
            Net.Broadcast(data, true);
            AddToJournal(data);
            _statSent++;
            _statBytes += data.Length;
        }

        // ---------- messages that name objects (keys shared yet?) ----------

        // A client between loading the host's world and the host's key sync (a few seconds) only has
        // save ordinals as keys. What it sells / returns / resets meanwhile is sent once the sync renamed
        // them (RefKey + TranslateOrdinal), and what it receives meanwhile waits too (keys unresolvable).
        private struct Deferred
        {
            public int Gen;
            public Func<byte[]> Build;
            public Action After;
        }

        private struct Inbound
        {
            public int Gen;
            public byte[] Data; // from the kind byte on
        }

        private const int QueueMax = 512;
        private static readonly List<Deferred> Outbox = new List<Deferred>();
        private static readonly List<Inbound> Inbox = new List<Inbound>();

        private static bool NamesObjects(Kind k) =>
            k == Kind.RentBuyout || k == Kind.RentReturn || k == Kind.Sell || k == Kind.ParcelReset || k == Kind.Move;

        /// <summary>Sends now if keys are shared, else once they are (same world only). <paramref name="after"/> runs after sending.</summary>
        private static void SendNamed(Func<byte[]> build, Action after, int gen)
        {
            if (ObjectSync.KeysReady && Outbox.Count == 0)
            {
                Send(build());
                after?.Invoke();
                return;
            }
            if (Outbox.Count >= QueueMax)
            {
                Plugin.Log.LogWarning("Purchase sync: too many purchase messages waiting for the key sync, oldest dropped");
                Outbox.RemoveAt(0);
            }
            Outbox.Add(new Deferred { Gen = gen, Build = build, After = after });
            Plugin.Log.LogInfo("Purchase sync: object keys not shared yet, message sent after the host's key sync");
        }

        private static void FlushQueues()
        {
            int gen = ObjectSync.IndexGeneration;
            if (Outbox.Count > 0)
            {
                var list = new List<Deferred>(Outbox);
                Outbox.Clear();
                foreach (Deferred d in list)
                {
                    if (d.Gen != gen) { Plugin.Log.LogWarning("Purchase sync: world changed, a waiting purchase message was dropped"); continue; }
                    try { Send(d.Build()); d.After?.Invoke(); }
                    catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: deferred send failed: " + ex.Message); }
                }
            }
            if (Inbox.Count > 0)
            {
                var list = new List<Inbound>(Inbox);
                Inbox.Clear();
                foreach (Inbound m in list)
                {
                    if (m.Gen != gen) continue; // received for a world that was replaced
                    try
                    {
                        var r = new BinaryReader(new MemoryStream(m.Data));
                        var kind = (Kind)r.ReadByte();
                        ulong origin = r.ReadUInt64();
                        ulong seq = r.ReadUInt64();
                        Apply(kind, origin, seq, r);
                    }
                    catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: queued message failed: " + ex.Message); }
                }
            }
        }

        private static int _statSent, _statRecv;
        private static long _statBytes;
        private static float _statSince = -1f;

        private static void LogStats(float now)
        {
            if (_statSince < 0f) { _statSince = now; return; }
            if (now - _statSince < 60f) return;
            if (_statSent + _statRecv > 0)
                Plugin.Log.LogInfo($"Purchase sync: sent {_statSent} ({_statBytes / 1024} KB), received {_statRecv} in the last {now - _statSince:0} s; " +
                                   $"journal {Journal.Count}, waiting {Outbox.Count} out / {Inbox.Count} in");
            _statSent = _statRecv = 0;
            _statBytes = 0;
            _statSince = now;
        }

        /// <summary>Sends only from the shared world (a client's pre-join / loading world is not shared).</summary>
        private static bool CanSend => !_applying && Net.WorldLive;

        private static void SendSpawn(SellableObject so, DepotPlace place, bool placeSetActive, bool addToClaim)
        {
            if (!CanSend || so == null) return;
            var save = so.GetComponent<SaveSystem.SaveMe>();
            if (save == null || string.IsNullOrEmpty(save.SaveID))
            {
                Plugin.Log.LogWarning("Purchase sync: bought object without SaveMe/SaveID, not sent: " + so.name);
                return;
            }

            GameObject go = so.gameObject;
            Transform t = go.transform;

            // Give machines a MachineSync id now so both PCs use the same one.
            int machineId = -1;
            var mc = go.GetComponent<MachineController>();
            if (mc != null)
            {
                // Always a fresh purchased id: the periodic machine scan may already have given this
                // new machine a local-only number, which the other PC would not know.
                machineId = MachineSync.AllocatePurchasedId();
                MachineSync.Register(mc, machineId);
            }
            else if (go.GetComponent<Trailer>() is Trailer tr && tr != null)
            {
                // Trailers get stable ids too, so hitching/towing matches the same trailer everywhere.
                machineId = MachineSync.AllocatePurchasedId();
                MachineSync.RegisterTrailer(tr, machineId);
            }

            ItemDescriptor d = so.MyDescriptor;
            var ms = BeginMsg(Kind.Spawn, out BinaryWriter w);
            w.Write(save.SaveID);
            WriteV3(w, t.position);
            WriteQ(w, t.rotation);
            w.Write(go.activeSelf);
            w.Write(PathOf(t.parent));
            w.Write(place != null ? PathOf(place.transform) : "");
            w.Write(placeSetActive);
            w.Write(save.MySceneName ?? "");
            w.Write(d != null && IsReinforcedField != null && (bool)IsReinforcedField.GetValue(d));
            w.Write(so.IsRented);
            w.Write(d != null ? d.GetRawRentedPrice() : 0);
            w.Write(so.dayOfRent);
            w.Write(so.seasonOfRent);
            w.Write(so.initBuyPrice);
            w.Write(machineId);
            w.Write(addToClaim);
            // Network key for loose-object sync (grab/drop/push), same on both PCs.
            string objectKey = "buy:" + Net.LocalId.ToString("x") + ":" + ObjectSync.Salt + ":" + (_objectKeyCounter++);
            ObjectSync.RegisterObject(go, objectKey);
            w.Write(objectKey);
            // Streamed area the object belongs to (e.g. "Town" for shop items), so the receiver can
            // keep it frozen there if that area is not loaded on its PC.
            w.Write(AreaSceneOf(t));
            Buy buy = _stepBuy;
            if (buy != null && !buy.Released)
            {
                // The host has not accepted this purchase's money yet: the spawn waits (TickBuys), so a refused
                // purchase leaves no object on any PC.
                buy.Held.Add(ms.ToArray());
                buy.Objects.Add(go);
                Plugin.Log.LogInfo($"Purchase sync: spawn {save.SaveID} waits for the host to accept the payment");
                return;
            }
            Send(ms.ToArray());
            // The buyer owns the new item: its fall/settle is streamed and the exact rest pose sent,
            // so it lies at the same spot on every PC. Sent after the spawn (same reliable channel).
            ObjectSync.ClaimLocal(go);
            Plugin.Log.LogInfo($"Purchase sync: sent spawn {save.SaveID} at {t.position} (machine #{machineId})");
        }

        private static void SendRef(Kind kind, SellableObject so)
        {
            if (!CanSend || so == null) return;
            SoldRef d = RefOf(so.gameObject);
            int gen = ObjectSync.IndexGeneration; // after RefOf (may build the index)
            string name = so.name;
            SendNamed(() =>
            {
                var ms = BeginMsg(kind, out BinaryWriter w);
                WriteSoldRef(w, d);
                return ms.ToArray();
            }, () => Plugin.Log.LogInfo($"Purchase sync: sent {kind} for {name}"), gen);
        }

        // UnityEngine.JsonUtility lives in UnityEngine.JSONSerializeModule, which the csproj does not
        // reference; bind it by reflection (the module is always loaded in the game).
        private static readonly Type JsonUtilityType = Type.GetType("UnityEngine.JsonUtility, UnityEngine.JSONSerializeModule");
        private static readonly MethodInfo JsonToJson =
            JsonUtilityType != null ? AccessTools.Method(JsonUtilityType, "ToJson", new[] { typeof(object) }) : null;
        private static readonly MethodInfo JsonFromJsonOverwrite =
            JsonUtilityType != null ? AccessTools.Method(JsonUtilityType, "FromJsonOverwrite", new[] { typeof(string), typeof(object) }) : null;

        private static void SendWorker(Worker worker)
        {
            if (!CanSend || worker == null) return;
            if (JsonToJson == null) { Plugin.Log.LogWarning("Purchase sync: JsonUtility not available, worker not sent"); return; }
            var ms = BeginMsg(Kind.Worker, out BinaryWriter w);
            // Same format the save system uses (XmlKey -> JsonUtility). A fresh hire has no
            // claim / used object, so the JSON part is all there is.
            w.Write((string)JsonToJson.Invoke(null, new object[] { worker }));
            Send(ms.ToArray());
            Plugin.Log.LogInfo("Purchase sync: sent hired worker " + worker.Name);
        }

        // =====================================================================
        // Receiving
        // =====================================================================

        internal static void Receive(BinaryReader r)
        {
            // Client still in its pre-join world or loading the host's: applying would create objects in
            // a world that is about to be replaced. The save (or the host's journal replay) covers it.
            if (!Net.WorldLive) return;

            // Host: journal a copy of relayed purchases too (the packet buffer is reused afterwards).
            if (_journalOn && Net.IsHost && r.BaseStream.CanSeek)
            {
                Stream s = r.BaseStream;
                long p = s.Position;
                byte[] rest = r.ReadBytes((int)(s.Length - p));
                s.Position = p;
                var copy = new byte[rest.Length + 1];
                copy[0] = (byte)MsgPurchase;
                Buffer.BlockCopy(rest, 0, copy, 1, rest.Length);
                AddToJournal(copy);
            }

            _statRecv++;
            Stream bs = r.BaseStream;
            long start = bs.CanSeek ? bs.Position : -1;
            var kind = (Kind)r.ReadByte();
            ulong origin = r.ReadUInt64();
            ulong seq = r.ReadUInt64();
            // Own message echoed back by a replay.
            if (origin == Net.LocalId) return;
            // Names objects by key, but keys are not shared here yet (client before the host's key sync,
            // host before its index): apply it once they are, in arrival order. (Copied: the transport
            // reuses its receive buffer.)
            if (NamesObjects(kind) && start >= 0 && (!ObjectSync.KeysReady || Inbox.Count > 0))
            {
                if (Applied.Contains(new MsgId(origin, seq))) return;
                if (Inbox.Count >= QueueMax) Inbox.RemoveAt(0);
                bs.Position = start;
                Inbox.Add(new Inbound { Gen = ObjectSync.IndexGeneration, Data = r.ReadBytes((int)(bs.Length - start)) });
                return;
            }
            Apply(kind, origin, seq, r);
        }

        private static void Apply(Kind kind, ulong origin, ulong seq, BinaryReader r)
        {
            // Already applied (live delivery + journal replay overlap).
            if (!MarkApplied(origin, seq)) return;
            _applying = true;
            try
            {
                switch (kind)
                {
                    case Kind.Spawn: ReceiveSpawn(r); break;
                    case Kind.RentBuyout: ReceiveBuyout(r); break;
                    case Kind.RentReturn: ReceiveReturn(r); break;
                    case Kind.Lot: ReceiveLot(r); break;
                    case Kind.Tabs: ReceiveTabs(r); break;
                    case Kind.Worker: ReceiveWorker(r); break;
                    case Kind.Sell: ReceiveSell(r); break;
                    case Kind.ParcelReset: ReceiveParcelReset(r); break;
                    case Kind.Move: ReceiveMove(r); break;
                    default: Plugin.Log.LogWarning("Purchase sync: unknown kind " + kind); break;
                }
            }
            finally { _applying = false; }
        }

        private static void ReceiveSpawn(BinaryReader r)
        {
            string saveId = r.ReadString();
            Vector3 pos = ReadV3(r);
            Quaternion rot = ReadQ(r);
            bool active = r.ReadBoolean();
            string parentPath = r.ReadString();
            string placePath = r.ReadString();
            bool placeSetActive = r.ReadBoolean();
            string sceneName = r.ReadString();
            bool reinforced = r.ReadBoolean();
            bool rented = r.ReadBoolean();
            int rentedPrice = r.ReadInt32();
            int dayOfRent = r.ReadInt32();
            int seasonOfRent = r.ReadInt32();
            float initBuyPrice = r.ReadSingle();
            int machineId = r.ReadInt32();
            bool addToClaim = r.ReadBoolean();
            string objectKey = r.ReadString();
            string areaScene = r.ReadString();
            if (objectKey.Length > 0 && ObjectSync.HasKey(objectKey)) return; // already spawned here

            if (!SaveSystem.Singleton<SaveSystem.CheckPointManagerBase>.IsInstanced()) return;
            GameObject prefab = SaveSystem.Singleton<SaveSystem.CheckPointManagerBase>.Instance.GetPrefab(saveId);
            if (prefab == null)
            {
                Plugin.Log.LogWarning("Purchase sync: no prefab for SaveID " + saveId);
                return;
            }

            GameObject go = Object.Instantiate(prefab, pos, rot); // SaveMe.Awake registers it for saving
            var so = go.GetComponent<SellableObject>();
            if (so != null)
            {
                so.IsRented = rented;
                so.dayOfRent = dayOfRent;
                so.seasonOfRent = seasonOfRent;
                so.initBuyPrice = initBuyPrice;
                if (so.MyDescriptor != null)
                {
                    so.MyDescriptor.RentedPrice = rentedPrice;
                    IsReinforcedField?.SetValue(so.MyDescriptor, reinforced);
                }

                // Same depot place as the sender: runs the game's own side effects
                // (area parenting, SaveMe.MySceneName, worker buff claim, store depot hiding).
                DepotPlace place = placePath.Length > 0 ? Resolve(placePath)?.GetComponent<DepotPlace>() : null;
                if (place != null)
                {
                    try { place.TransportGameObject(go, so.SpawnOffsetRotation, so.SpawnOffset, placeSetActive); }
                    catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: TransportGameObject failed: " + ex.Message); }
                }
                else if (placePath.Length > 0)
                {
                    Plugin.Log.LogWarning("Purchase sync: depot place not found: " + placePath.Replace(Sep, '/'));
                }
            }

            // Force the sender's final state.
            Transform parent = parentPath.Length > 0 ? Resolve(parentPath) : null;
            if (parent != null) go.transform.SetParent(parent, true);
            else if (parentPath.Length == 0) go.transform.SetParent(null, true);
            go.transform.SetPositionAndRotation(pos, rot);

            // The sender's parent lives in a streamed scene that is not loaded here (the usual case:
            // shop items are parented to the shop in the "Town" scene while this player is far away).
            // Without its scene there is no ground, so the copy used to fall through the world and the
            // other player never found it. Do what the game does for unloaded areas: keep it under the
            // area's GameplayObjects (inactive = frozen until the area loads on this PC).
            if (parent == null)
            {
                AreaStreamer area = FindArea(areaScene) ?? ObjectSync.AreaAt(pos);
                if (area != null) ObjectSync.ParentToArea(go, area);
            }
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            var save = go.GetComponent<SaveSystem.SaveMe>();
            if (save != null && sceneName.Length > 0) save.MySceneName = sceneName;
            if (go.activeSelf != active) go.SetActive(active);

            if (addToClaim && so != null && global::Singleton<LaptopGlobalManager>.IsInstanced())
            {
                TransportClaim claim = global::Singleton<LaptopGlobalManager>.Instance.FindClosestClaim(go.transform.position);
                if (claim != null && !claim.ObjectsInClaim.Contains(so)) claim.ObjectsInClaim.Add(so);
            }

            var mc = go.GetComponent<MachineController>();
            if (mc != null && machineId >= 0) MachineSync.Register(mc, machineId);
            else if (machineId >= 0 && go.GetComponent<Trailer>() is Trailer tr2 && tr2 != null) MachineSync.RegisterTrailer(tr2, machineId);
            if (objectKey.Length > 0) ObjectSync.RegisterObject(go, objectKey);

            Plugin.Log.LogInfo($"Purchase sync: spawned {saveId} at {pos} (machine #{machineId})");
        }

        /// <summary>
        /// The other player sold/returned/reset a machine the local player sits in (or is getting into /
        /// out of): get out first, otherwise Player keeps a destroyed ControlledMachine, the camera stays
        /// in (and is destroyed with) the cab, and an interrupted enter/exit animation leaves input blocked.
        /// Also ends any other local use of the object (InteractionGuard.BeforeRemoteRemove).
        /// </summary>
        private static void ExitIfLocallyInside(GameObject go)
        {
            if (go == null) return;
            // EnterVehicleController is a plain class (MachineController.EnterVehicle), not a component:
            // the old GetComponentInChildren<EnterVehicleController> lookup never found it.
            InteractionGuard.BeforeRemoteRemove(go);
        }

        private static void ReceiveBuyout(BinaryReader r)
        {
            SellableObject so = ReadRef(r, out _);
            if (so == null) { Plugin.Log.LogWarning("Purchase sync: rental buyout target not found"); return; }
            so.IsRented = false;
            RefreshBuyoutGuis();
            Plugin.Log.LogInfo("Purchase sync: rental bought out: " + so.name);
        }

        private static void ReceiveReturn(BinaryReader r)
        {
            SellableObject so = ReadRef(r, out string key);
            if (so == null)
            {
                // Keyed object not resolvable yet (keys not shared yet after a load): ObjectSync
                // destroys it once they are.
                if (key.Length > 0) ObjectSync.DestroyKeyed(key, "", Vector3.zero);
                else Plugin.Log.LogWarning("Purchase sync: rental return target not found");
                return;
            }
            // Mirror BuyoutRentedShopItemGUI.OnSellButtonClicked (no money involved).
            ExitIfLocallyInside(so.gameObject);
            so.IsRented = false;
            so.gameObject.SetActive(true);
            if (global::Singleton<LaptopGlobalManager>.IsInstanced())
            {
                try { global::Singleton<LaptopGlobalManager>.Instance.TeleportToClosestClaim(so); }
                catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: TeleportToClosestClaim failed: " + ex.Message); }
            }
            string k = ObjectSync.KeyOf(so.gameObject);
            if (k != null) ObjectSync.DestroyKeyed(k, "", Vector3.zero);
            else Object.Destroy(so.gameObject);
            RefreshBuyoutGuis();
            Plugin.Log.LogInfo("Purchase sync: rental returned: " + so.name);
        }

        /// <summary>Best effort: refresh open buyout lists so a stale row cannot be bought twice.</summary>
        private static void RefreshBuyoutGuis()
        {
            FieldInfo parentField = AccessTools.Field(typeof(BuyoutRentedShopItemGUI), "_parentGUI");
            if (parentField == null) return;
            var done = new HashSet<object>();
            foreach (var row in Object.FindObjectsOfType<BuyoutRentedShopItemGUI>())
            {
                object gui = parentField.GetValue(row);
                if (gui == null || !done.Add(gui)) continue;
                MethodInfo m = AccessTools.Method(gui.GetType(), "UpdateItemsList");
                try { m?.Invoke(gui, null); } catch (Exception) { /* UI only */ }
            }
        }

        private static void ReceiveLot(BinaryReader r)
        {
            int index = r.ReadInt32();
            string terrainName = r.ReadString();
            bool owner = r.ReadBoolean();
            bool rented = r.ReadBoolean();
            int transport = r.ReadInt32();
            if (!global::Singleton<LaptopGlobalManager>.IsInstanced()) return;
            List<LotDescriptor> lots = global::Singleton<LaptopGlobalManager>.Instance.LotList;
            if (lots == null) return;

            // Index first, verified by TerrainObjectName; fall back to a name search.
            if (index < 0 || index >= lots.Count || lots[index] == null || (lots[index].TerrainObjectName ?? "") != terrainName)
                index = lots.FindIndex(l => l != null && (l.TerrainObjectName ?? "") == terrainName);
            if (index < 0) { Plugin.Log.LogWarning("Purchase sync: unknown lot " + terrainName); return; }

            LotDescriptor lot = lots[index];
            try { lot.IsOwner = owner; } // setter -> GameStateManager.SetOwner -> GoldTerrainDigger.IsOwner
            catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: SetOwner failed: " + ex.Message); }
            lot.IsRented = rented;
            if (transport > lot.transportPlace) lot.transportPlace = transport; // only ever bought, never lost

            // Update the poll snapshot so this change is not echoed back.
            if (_lotOwner != null && index < _lotOwner.Length)
            {
                _lotOwner[index] = LotOwner(lot);
                _lotRented[index] = lot.IsRented;
                if (_lotTransport != null && index < _lotTransport.Length) _lotTransport[index] = lot.transportPlace;
            }
            Plugin.Log.LogInfo($"Purchase sync: lot {terrainName} owner={owner} rented={rented}");
        }

        private static readonly MethodInfo TabOnEnable = AccessTools.Method(typeof(LaptopBuyableTab), "OnEnable");

        private static void ReceiveTabs(BinaryReader r)
        {
            int mask = r.ReadInt32();
            if (!global::Singleton<GoldDigger.GameStateManager>.IsInstanced()) return;
            var g = global::Singleton<GoldDigger.GameStateManager>.Instance;
            // Unlock only, never lock again.
            if ((mask & 1) != 0) g.TransportBuyed = true;
            if ((mask & 2) != 0) g.WorkersBuyed = true;
            if ((mask & 4) != 0) g.WarehouseBuyed = true;
            if ((mask & 8) != 0) g.MachineShopBuyed = true;
            if ((mask & 16) != 0) g.NotificationsBuyed = true;
            if ((mask & 32) != 0) g.BuyoutRentedBuyed = true;
            _tabGsm = g;
            _tabMask = TabMask(g);

            // Refresh the laptop tab buttons and raise the same flag events BuyMe raises.
            foreach (var tab in Resources.FindObjectsOfTypeAll<LaptopBuyableTab>())
            {
                if (tab == null || !tab.gameObject.scene.IsValid()) continue;
                try
                {
                    if (!tab.CheckIsBuy()) continue;
                    if (tab.UnlockFlag != null) TutorialGameEvents.PublishFlagChangedImmediate(tab.UnlockFlag, true);
                    if (tab.isActiveAndEnabled) TabOnEnable?.Invoke(tab, null);
                }
                catch (Exception) { /* UI only */ }
            }
            Plugin.Log.LogInfo("Purchase sync: laptop tabs mask " + mask);
        }

        private static void ReceiveWorker(BinaryReader r)
        {
            string json = r.ReadString();
            if (!global::Singleton<WorkersManager>.IsInstanced()) return;
            WorkersManager wm = global::Singleton<WorkersManager>.Instance;
            if (JsonFromJsonOverwrite == null) return;
            var worker = new Worker();
            JsonFromJsonOverwrite.Invoke(null, new object[] { json, worker });

            if (wm.MyWorkers.Exists(x => SameWorker(x, worker))) return; // already have it
            wm.MyWorkers.Add(worker);
            // Candidates are rolled randomly per PC, so usually there is no local twin; drop it if there is.
            wm.WorkersCandidate.RemoveAll(x => SameWorker(x, worker));
            Plugin.Log.LogInfo("Purchase sync: hired worker " + worker.Name);
        }

        private static void ReceiveSell(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 100000) return;
            int done = 0;
            var refs = new List<SoldRef>(n);
            for (int i = 0; i < n; i++) refs.Add(ReadSoldRef(r));
            // Resolve all first (one scene scan): a destroyed object stays findable until the frame ends.
            SellableObject[] all = AllSellables();
            var gos = new List<GameObject>(n);
            var used = new HashSet<GameObject>();
            foreach (SoldRef d in refs)
            {
                GameObject g = ResolveSold(d, all);
                gos.Add(g != null && used.Add(g) ? g : null);
            }
            for (int i = 0; i < n; i++)
            {
                string key = refs[i].Key, saveId = refs[i].SaveId;
                Vector3 pos = refs[i].Pos;
                GameObject go = gos[i];
                SellableObject so = go != null ? go.GetComponent<SellableObject>() : null;
                if (go == null)
                {
                    // Keyed object not resolvable yet (index still building after a load): ObjectSync
                    // destroys it once the key exists.
                    if (key.Length > 0) ObjectSync.DestroyKeyed(key, saveId, pos);
                    else Plugin.Log.LogWarning("Purchase sync: sold object not found here: " + saveId);
                    continue;
                }
                // Mirror SellGUI.SellObjects (no money involved).
                ExitIfLocallyInside(go);
                if (so != null)
                {
                    go.SetActive(true);
                    if (global::Singleton<LaptopGlobalManager>.IsInstanced())
                    {
                        try { global::Singleton<LaptopGlobalManager>.Instance.TeleportToClosestClaim(so); }
                        catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: TeleportToClosestClaim failed: " + ex.Message); }
                    }
                }
                string k = ObjectSync.KeyOf(go);
                if (k != null) ObjectSync.DestroyKeyed(k, saveId, pos);
                else Object.Destroy(go);
                done++;
            }
            int m = r.ReadInt32();
            if (m < 0 || m > 100000) m = 0;
            for (int i = 0; i < m; i++)
            {
                string key = r.ReadString();
                string saveId = r.ReadString();
                Vector3 pos = ReadV3(r);
                Quaternion rot = ReadQ(r);
                ObjectSync.CreateKeyed(saveId, pos, rot, key, -1);
            }
            Plugin.Log.LogInfo($"Purchase sync: sale applied, {done}/{n} objects removed, {m} buckets created");
        }

        private static bool SameWorker(Worker a, Worker b) =>
            a != null && b != null && a.Name == b.Name && a.IsMen == b.IsMen && a.Tier == b.Tier &&
            // WorkerImageInfo's == / != read both operands' fields (no null check): "x != null" threw. The
            // null tests are reference comparisons; the field comparison runs on two real objects only.
            !ReferenceEquals(a.ImageInfo, null) && !ReferenceEquals(b.ImageInfo, null) && a.ImageInfo == b.ImageInfo;

        // =====================================================================
        // Object references (existing objects)
        // =====================================================================

        private static SellableObject ReadRef(BinaryReader r, out string key)
        {
            key = r.ReadString();
            int machineId = r.ReadInt32();
            string saveId = r.ReadString();
            Vector3 pos = ReadV3(r);
            GameObject go = key.Length > 0 ? ObjectSync.Find(key) : null;
            SellableObject so = go != null ? go.GetComponent<SellableObject>() : null;
            return so != null ? so : FindRef(machineId, saveId, pos, key.Length > 0, null);
        }

        /// <summary>All SellableObjects of the world incl. inactive ones (one scan per message).</summary>
        private static SellableObject[] AllSellables()
        {
            var list = new List<SellableObject>();
            foreach (var s in Resources.FindObjectsOfTypeAll<SellableObject>())
                if (s != null && s.gameObject.scene.IsValid()) list.Add(s);
            return list.ToArray();
        }

        /// <summary>
        /// Existing object by MachineSync id, else by SaveID + nearest position (5 m). Objects the sender
        /// knew by ObjectSync key (<paramref name="keyed"/>) are not matched by position: the nearest one
        /// of the same type may be another object. Keyed objects here are never position matches either.
        /// </summary>
        private static SellableObject FindRef(int machineId, string saveId, Vector3 pos, bool keyed, SellableObject[] all)
        {
            if (all == null) all = AllSellables(); // includes inactive objects (rented machines in a store depot)
            if (machineId >= 0)
                foreach (var s in all)
                {
                    if (s == null) continue;
                    var mc = s.GetComponent<MachineController>();
                    if (mc != null && MachineSync.IdOf(mc) == machineId) return s;
                }
            if (keyed) return null;

            SellableObject best = null;
            float bestSq = 25f; // within 5 m
            foreach (var s in all)
            {
                if (s == null) continue;
                var save = s.GetComponent<SaveSystem.SaveMe>();
                if (save == null || save.SaveID != saveId || ObjectSync.KeyOf(s.gameObject) != null) continue;
                float sq = (s.transform.position - pos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = s; }
            }
            return best;
        }

        // =====================================================================
        // Hierarchy paths: "scene<US>name<RS>siblingIndex<US>name<RS>siblingIndex..."
        // Names are matched first at the recorded sibling index, then by first name match.
        // Works for inactive objects (Transform children), unlike GameObject.Find.
        // =====================================================================

        private const char Sep = '\u001f';
        private const char IdxSep = '\u001e';

        private static string PathOf(Transform t)
        {
            if (t == null) return "";
            var parts = new List<string>();
            for (Transform x = t; x != null; x = x.parent)
                parts.Add(x.name + IdxSep + x.GetSiblingIndex());
            parts.Add(t.gameObject.scene.name);
            parts.Reverse();
            return string.Join(Sep.ToString(), parts);
        }

        private static Transform Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] segs = path.Split(Sep);
            Scene scene = segs[0] == "DontDestroyOnLoad" && Plugin.Instance != null
                ? Plugin.Instance.gameObject.scene
                : SceneManager.GetSceneByName(segs[0]);
            if (!scene.IsValid() || !scene.isLoaded) return null;

            Transform cur = null;
            for (int i = 1; i < segs.Length; i++)
            {
                int k = segs[i].LastIndexOf(IdxSep);
                string name = k >= 0 ? segs[i].Substring(0, k) : segs[i];
                int idx = k >= 0 && int.TryParse(segs[i].Substring(k + 1), out int v) ? v : -1;

                Transform next = null;
                if (cur == null)
                {
                    GameObject[] roots = scene.GetRootGameObjects();
                    foreach (var root in roots)
                        if (root.name == name && root.transform.GetSiblingIndex() == idx) { next = root.transform; break; }
                    if (next == null)
                        foreach (var root in roots)
                            if (root.name == name) { next = root.transform; break; }
                }
                else
                {
                    if (idx >= 0 && idx < cur.childCount && cur.GetChild(idx).name == name) next = cur.GetChild(idx);
                    else
                        for (int c = 0; c < cur.childCount; c++)
                            if (cur.GetChild(c).name == name) { next = cur.GetChild(c); break; }
                }
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }

        /// <summary>Scene name of the streamed area <paramref name="t"/> belongs to, or "".</summary>
        private static string AreaSceneOf(Transform t)
        {
            List<AreaStreamer> all = AreaStreamer.Instances;
            if (all == null || t == null) return "";
            for (Transform x = t.parent; x != null; x = x.parent)
                foreach (AreaStreamer a in all)
                    if (a != null && a.GameplayObjects == x) return a.sceneName ?? "";
            string scene = t.gameObject.scene.name;
            foreach (AreaStreamer a in all)
                if (a != null && a.sceneName == scene) return scene;
            AreaStreamer at = ObjectSync.AreaAt(t.position);
            return at != null ? at.sceneName ?? "" : "";
        }

        private static AreaStreamer FindArea(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName) || AreaStreamer.Instances == null) return null;
            foreach (AreaStreamer a in AreaStreamer.Instances)
                if (a != null && a.GameplayObjects != null && a.sceneName == sceneName) return a;
            return null;
        }

        private static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadV3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        private static void WriteQ(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        private static Quaternion ReadQ(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        // =====================================================================
        // Harmony targets. Looked up with AccessTools; a missing target logs a warning and
        // its patch class is skipped (Prepare returns false) instead of breaking PatchAll.
        // =====================================================================

        private static MethodBase Need(MethodBase m, string what)
        {
            if (m == null) Plugin.Log.LogWarning("Purchase sync: Harmony target not found, feature disabled: " + what);
            return m;
        }

        private static MethodBase MoveNextOf(Type type, string iteratorName)
        {
            MethodInfo it = AccessTools.Method(type, iteratorName);
            return it != null ? AccessTools.EnumeratorMoveNext(it) : null;
        }

        private static class Targets
        {
            internal static readonly MethodBase SetPosition = Need(
                AccessTools.Method(typeof(CashRegisterGUI), "SetPosition", new[] { typeof(SellableObject), typeof(Vector3) }),
                "CashRegisterGUI.SetPosition(SellableObject, Vector3)");
            internal static readonly MethodBase BuyDelayed = Need(
                MoveNextOf(typeof(CashRegisterGUI), "BuyDelayed"), "CashRegisterGUI.BuyDelayed MoveNext");
            internal static readonly MethodBase BuyDelayedWithMarkers = Need(
                MoveNextOf(typeof(CashRegisterGUI), "BuyDelayedWithMarkers"), "CashRegisterGUI.BuyDelayedWithMarkers MoveNext");
            internal static readonly MethodBase RegisterSpawnTrailer = Need(
                MoveNextOf(typeof(CashRegisterGUI), "SpawnTrailer"), "CashRegisterGUI.SpawnTrailer MoveNext");
            internal static readonly MethodBase SpawnerSpawnTrailer = Need(
                MoveNextOf(typeof(TrailerSpawner), "SpawnTrailer"), "TrailerSpawner.SpawnTrailer MoveNext");
            internal static readonly MethodBase CarBuyObject = Need(
                MoveNextOf(typeof(CashRegisterCarGUI), "BuyObject"), "CashRegisterCarGUI.BuyObject MoveNext");
            internal static readonly MethodBase DepotTeleport = Need(
                AccessTools.Method(typeof(Depot), nameof(Depot.Teleport), new[] { typeof(SellableObject), typeof(bool) }),
                "Depot.Teleport(SellableObject, bool)");
            internal static readonly MethodBase PlaceTransport = Need(
                AccessTools.Method(typeof(DepotPlace), nameof(DepotPlace.TransportGameObject),
                    new[] { typeof(GameObject), typeof(Quaternion), typeof(Vector3), typeof(bool) }),
                "DepotPlace.TransportGameObject(GameObject, Quaternion, Vector3, bool)");
            internal static readonly MethodBase BuyoutBuy = Need(
                AccessTools.Method(typeof(BuyoutRentedShopItemGUI), nameof(BuyoutRentedShopItemGUI.OnBuyButtonClicked), Type.EmptyTypes),
                "BuyoutRentedShopItemGUI.OnBuyButtonClicked()");
            internal static readonly MethodBase BuyoutSell = Need(
                AccessTools.Method(typeof(BuyoutRentedShopItemGUI), nameof(BuyoutRentedShopItemGUI.OnSellButtonClicked), Type.EmptyTypes),
                "BuyoutRentedShopItemGUI.OnSellButtonClicked()");
            internal static readonly FieldInfo BuyoutMachine = AccessTools.Field(typeof(BuyoutRentedShopItemGUI), "_machine");
            internal static readonly MethodBase WorkersHire = Need(
                AccessTools.Method(typeof(WorkersToHire), nameof(WorkersToHire.Hire), Type.EmptyTypes),
                "WorkersToHire.Hire()");
            internal static readonly MethodBase LaptopHire = Need(
                AccessTools.Method(typeof(LaptopWorkersHire), nameof(LaptopWorkersHire.HireWorker), Type.EmptyTypes),
                "LaptopWorkersHire.HireWorker()");
        }

        // ---------- purchases the host may refuse (two players buying with the same money at once) ----------
        //
        // A client's shop / vehicle purchase sends its money change alone, marked for a cash check
        // (GoldSync.CheckedSpendEnd). The objects it spawns (spawn coroutines started inside BuyObjects) are
        // shown locally at once, but their Spawn messages wait until the host answered: accepted -> sent;
        // refused (the host's cash no longer covers it) -> the local objects are destroyed and nothing is sent,
        // GoldSync's prediction gives the money back and the buyer sees the refusal hint.

        private sealed class Buy
        {
            public uint Seq;
            public bool Refused, Released;
            public float Since;
            public readonly List<byte[]> Held = new List<byte[]>();
            public readonly List<GameObject> Objects = new List<GameObject>();
        }

        private static readonly List<Buy> Buys = new List<Buy>();
        private static readonly Dictionary<object, Buy> BuyOfIter = new Dictionary<object, Buy>();
        private static Buy _openBuy;   // inside BuyObjects: coroutines started now belong to it
        private static Buy _stepBuy;   // inside a step of a coroutine that belongs to a purchase

        private static void BuyOpen()
        {
            _openBuy = null;
            if (_applying || !Net.Active || Net.IsHost || !CanSend) return;
            GoldSync.CheckedSpendBegin();
            _openBuy = new Buy { Since = Time.unscaledTime };
        }

        private static void BuyClose()
        {
            Buy b = _openBuy;
            _openBuy = null;
            if (b == null) return;
            try { b.Seq = GoldSync.CheckedSpendEnd(() => b.Refused = true); }
            catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: checked spend failed: " + ex.Message); b.Seq = 0; }
            if (b.Seq == 0) { ReleaseBuy(b); return; } // nothing spent (no money): nothing to wait for
            Buys.Add(b);
        }

        /// <summary>Coroutine step prefix: which purchase (if any) this iterator belongs to.</summary>
        private static void BuyStepBegin(object iter)
        {
            _stepBuy = null;
            if (iter == null) return;
            if (_openBuy != null && !BuyOfIter.ContainsKey(iter))
            {
                if (BuyOfIter.Count > 256) BuyOfIter.Clear(); // coroutines stopped by the game never finish
                BuyOfIter[iter] = _openBuy;
            }
            BuyOfIter.TryGetValue(iter, out _stepBuy);
        }

        private static void BuyStepEnd(object iter, bool more)
        {
            Buy b = _stepBuy;
            _stepBuy = null;
            if (iter != null && !more) BuyOfIter.Remove(iter);
            if (b != null && b.Refused) DestroyBuyObjects(b); // spawned after the refusal arrived
        }

        private static void DestroyBuyObjects(Buy b)
        {
            foreach (GameObject go in b.Objects)
                if (go != null)
                {
                    try { InteractionGuard.BeforeRemoteRemove(go); } catch (Exception) { }
                    Object.Destroy(go);
                }
            b.Objects.Clear();
            b.Held.Clear();
        }

        private static void ReleaseBuy(Buy b)
        {
            b.Released = true;
            foreach (byte[] m in b.Held) Send(m);
            foreach (GameObject go in b.Objects) if (go != null) ObjectSync.ClaimLocal(go);
            b.Held.Clear();
            b.Objects.Clear();
        }

        private static void TickBuys()
        {
            float now = Time.unscaledTime;
            for (int i = Buys.Count - 1; i >= 0; i--)
            {
                Buy b = Buys[i];
                if (b.Refused)
                {
                    if (b.Objects.Count > 0) Plugin.Log.LogInfo($"Purchase sync: purchase refused by the host, {b.Objects.Count} object(s) removed here");
                    DestroyBuyObjects(b);
                    // Kept while its coroutines may still spawn (BuyStepEnd removes those too).
                    if (now - b.Since > 60f) Buys.RemoveAt(i);
                    continue;
                }
                if (!b.Released && !GoldSync.SpendPending(b.Seq)) ReleaseBuy(b); // applied (or expired: the host's value rules)
                if (b.Released && now - b.Since > 60f) Buys.RemoveAt(i);
            }
        }

        [HarmonyPatch]
        private static class PatchBuyObjects
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                MethodInfo a = AccessTools.Method(typeof(CashRegisterGUI), nameof(CashRegisterGUI.BuyObjects), Type.EmptyTypes);
                MethodInfo b = AccessTools.Method(typeof(CashRegisterCarGUI), nameof(CashRegisterCarGUI.BuyObjects), Type.EmptyTypes);
                if (a != null) yield return a;
                if (b != null) yield return b;
            }

            private static bool Prepare()
            {
                foreach (MethodBase m in TargetMethods()) return true;
                Plugin.Log.LogWarning("Purchase sync: BuyObjects not found, the host cannot refuse a purchase paid with money another player spent");
                return false;
            }

            private static void Prefix() => BuyOpen();
            private static Exception Finalizer(Exception __exception) { BuyClose(); return __exception; }
        }

        // ---------- shop items ----------

        [HarmonyPatch]
        private static class PatchItemBuyCoroutines
        {
            private static bool Prepare() => Targets.BuyDelayed != null || Targets.BuyDelayedWithMarkers != null;

            private static IEnumerable<MethodBase> TargetMethods()
            {
                if (Targets.BuyDelayed != null) yield return Targets.BuyDelayed;
                if (Targets.BuyDelayedWithMarkers != null) yield return Targets.BuyDelayedWithMarkers;
            }

            private static void Prefix(object __instance) { _itemBuyDepth++; BuyStepBegin(__instance); }
            private static void Finalizer(object __instance, bool __result) { _itemBuyDepth--; BuyStepEnd(__instance, __result); }
        }

        [HarmonyPatch]
        private static class PatchSetPosition
        {
            private static bool Prepare() => Targets.SetPosition != null;
            private static MethodBase TargetMethod() => Targets.SetPosition;

            // Inside BuyDelayed*: called last for a NEW item, which is at its final spot -> spawn message.
            // Outside (BuyObjects): an EXISTING item in the spawn area is pushed aside on the buyer only;
            // the buyer claims it, so its new pose is streamed / sent as a rest pose to every PC.
            private static void Postfix(SellableObject p)
            {
                if (p == null || !CanSend) return;
                if (_itemBuyDepth > 0) SendSpawn(p, null, false, addToClaim: false);
                else ObjectSync.ClaimLocal(p.gameObject);
            }
        }

        // ---------- laptop deliveries (LaptopRepairParts -> LaptopDelivery "accept" -> TransactionHistoryItem.SpawnDelayed) ----------
        // The delivery is instantiated at the depot on the accepting PC only. Loose physics items reached the
        // others through ObjectSync's generic create, but machines (and anything not Syncable) never did:
        // paid for, visible on one PC only. Every saved root object the step created is sent as a purchase.

        [HarmonyPatch]
        private static class PatchDeliverySpawn
        {
            private static MethodBase Target => MoveNextOf(typeof(TransactionHistoryItem), "SpawnDelayed");
            private static bool Prepare() => Need(Target, "TransactionHistoryItem.SpawnDelayed MoveNext") != null;
            private static MethodBase TargetMethod() => Target;

            private static void Prefix(out int __state)
            {
                __state = -1;
                if (!CanSend || !SaveSystem.Singleton<SaveSystem.CheckPointManagerBase>.IsInstanced()) return;
                __state = SaveSystem.Singleton<SaveSystem.CheckPointManagerBase>.Instance.ToSave.Count;
            }

            private static void Postfix(int __state)
            {
                if (__state < 0 || !CanSend || !SaveSystem.Singleton<SaveSystem.CheckPointManagerBase>.IsInstanced()) return;
                List<SaveSystem.SaveMe> all = SaveSystem.Singleton<SaveSystem.CheckPointManagerBase>.Instance.ToSave;
                for (int i = __state; i < all.Count; i++)
                {
                    SaveSystem.SaveMe sm = all[i];
                    if (sm == null || sm.transform.parent != null) continue; // parts of the delivered object
                    SellableObject so = sm.GetComponent<SellableObject>();
                    if (so != null) SendSpawn(so, null, false, addToClaim: false);
                }
            }
        }

        // ---------- trailers + vehicles ----------

        private static readonly Dictionary<Type, FieldInfo> TrailerFields = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo> BuyFields = new Dictionary<Type, FieldInfo>();

        private static FieldInfo CachedField(Dictionary<Type, FieldInfo> cache, Type t, string name)
        {
            if (!cache.TryGetValue(t, out FieldInfo f))
            {
                f = AccessTools.Field(t, name);
                if (f == null) Plugin.Log.LogWarning($"Purchase sync: field {name} missing in {t.FullName}");
                cache[t] = f;
            }
            return f;
        }

        [HarmonyPatch]
        private static class PatchTrailerCoroutines
        {
            private static bool Prepare() => Targets.RegisterSpawnTrailer != null || Targets.SpawnerSpawnTrailer != null;

            private static IEnumerable<MethodBase> TargetMethods()
            {
                if (Targets.RegisterSpawnTrailer != null) yield return Targets.RegisterSpawnTrailer;
                if (Targets.SpawnerSpawnTrailer != null) yield return Targets.SpawnerSpawnTrailer;
            }

            private static void Prefix(object __instance) { _ctx = new Ctx(); BuyStepBegin(__instance); }

            // The state machine keeps the parameter "trailer" as a field; with buy=true it is
            // reassigned to the newly instantiated trailer before the coroutine finishes.
            private static void Postfix(object __instance, bool __result)
            {
                Ctx ctx = _ctx;
                if (__result || ctx == null || !Net.Active || _applying) return;
                Type t = __instance.GetType();
                FieldInfo buyF = CachedField(BuyFields, t, "buy");
                FieldInfo trailerF = CachedField(TrailerFields, t, "trailer");
                if (buyF == null || trailerF == null || !(bool)buyF.GetValue(__instance)) return;
                var trailer = trailerF.GetValue(__instance) as SellableObject;
                if (trailer == null) return;
                bool sameObj = ctx.Obj == trailer;
                SendSpawn(trailer, sameObj ? ctx.Place : null, sameObj && ctx.TeleportSetActive, addToClaim: false);
            }

            private static void Finalizer(object __instance, bool __result) { _ctx = null; BuyStepEnd(__instance, __result); }
        }

        [HarmonyPatch]
        private static class PatchCarBuyObject
        {
            private static bool Prepare() => Targets.CarBuyObject != null;
            private static MethodBase TargetMethod() => Targets.CarBuyObject;

            private static void Prefix(object __instance) { _ctx = new Ctx { AddToClaim = true }; BuyStepBegin(__instance); }

            // Final step: the vehicle was instantiated, passed to MyDepot.Teleport and added to the claim.
            private static void Postfix(bool __result)
            {
                Ctx ctx = _ctx;
                if (__result || ctx == null || ctx.Obj == null || !Net.Active || _applying) return;
                SendSpawn(ctx.Obj, ctx.Place, ctx.TeleportSetActive, ctx.AddToClaim);
            }

            private static void Finalizer(object __instance, bool __result) { _ctx = null; BuyStepEnd(__instance, __result); }
        }

        [HarmonyPatch]
        private static class PatchDepotTeleport
        {
            private static bool Prepare() => Targets.DepotTeleport != null;
            private static MethodBase TargetMethod() => Targets.DepotTeleport;

            // Only the outermost call: GetFirstPlace may recursively teleport other objects away.
            private static void Prefix(SellableObject obj, bool setActive)
            {
                if (_ctx != null && _ctx.Obj == null && obj != null)
                {
                    _ctx.Obj = obj;
                    _ctx.TeleportSetActive = setActive;
                }
            }
        }

        [HarmonyPatch]
        private static class PatchPlaceTransport
        {
            private static bool Prepare() => Targets.PlaceTransport != null;
            private static MethodBase TargetMethod() => Targets.PlaceTransport;

            // Overrides (StoreDepotPlace) call base, so __instance is the real place.
            private static void Prefix(DepotPlace __instance, GameObject objectToTransport, Quaternion rotation, Vector3 posOffset, bool setActive)
            {
                Ctx ctx = _ctx;
                if (ctx != null && ctx.Place == null && ctx.Obj != null && objectToTransport == ctx.Obj.gameObject)
                    ctx.Place = __instance;
                // A depot move of anything but a vehicle (MachineSync sends those) outside a purchase of that
                // object and outside a parcel reset (both send their own result).
                if (_reset == null && objectToTransport != null && CanSend &&
                    !(ctx != null && ctx.Obj != null && objectToTransport == ctx.Obj.gameObject) &&
                    objectToTransport.GetComponent<MachineController>() == null && objectToTransport.GetComponent<Trailer>() == null)
                    CaptureMove(__instance, objectToTransport, rotation, posOffset, setActive);
                if (_reset != null && objectToTransport != null && !_applying)
                    _reset.Moves.Add(new ResetMove
                    {
                        Ref = RefOf(objectToTransport),
                        Place = PathOf(__instance.transform),
                        Rot = rotation,
                        Offset = posOffset,
                        SetActive = setActive,
                    });
            }
        }

        // ---------- parcel reset (BankParcelGUI.ResetConfirm) ----------
        // The game sells (destroys) every loose object of the parcel, sends its big machines to the
        // machine store depot and its trailers / pickup to the closest claim, on the PC that clicked.
        // (The laptop has no reset of its own: LaptopParcell only buys / rents; ResetConfirm is the only
        // caller of GoldTerrainDigger.ResetParcell.) Terrain, gold and money are synced by area C
        // (TerrainSync sub 4, GoldSync). This message replicates the objects: the destroyed ones by
        // ObjectSync key / MachineSync id / SaveID + position, and every depot move with the exact place,
        // offset and rotation the clicker's game used (DepotPlace.TransportGameObject, in call order,
        // including objects GetFirstPlace pushed out of a full place).

        private struct ResetMove
        {
            public SoldRef Ref;
            public string Place;
            public Quaternion Rot;
            public Vector3 Offset;
            public bool SetActive;
        }

        private sealed class ResetCapture
        {
            public int Lot;
            public int Gen; // ObjectSync.IndexGeneration of the refs
            public readonly List<SoldRef> Destroyed = new List<SoldRef>();
            public readonly List<GameObject> DestroyedGos = new List<GameObject>();
            public readonly List<KeyValuePair<SoldRef, byte>> Expected = new List<KeyValuePair<SoldRef, byte>>(); // 0 store, 1 closest claim
            public readonly List<ResetMove> Moves = new List<ResetMove>();
        }

        private static ResetCapture _reset;

        private static readonly AccessTools.FieldRef<GravityGun, bool> DontPickRef = TryDontPick();

        private static AccessTools.FieldRef<GravityGun, bool> TryDontPick()
        {
            try { return AccessTools.FieldRefAccess<GravityGun, bool>("DontPick"); }
            catch (Exception) { return null; }
        }

        /// <summary>Same layout for every object reference: key, MachineSync id, SaveID, position.</summary>
        private static void WriteSoldRef(BinaryWriter w, SoldRef d)
        {
            // A save ordinal (captured before the key sync) becomes the host's key now.
            w.Write((d.Ordinal ? ObjectSync.TranslateOrdinal(d.Key) : d.Key) ?? "");
            w.Write(d.MachineId);
            w.Write(d.SaveId ?? "");
            WriteV3(w, d.Pos);
        }

        private static SoldRef ReadSoldRef(BinaryReader r) =>
            new SoldRef { Key = r.ReadString(), MachineId = r.ReadInt32(), SaveId = r.ReadString(), Pos = ReadV3(r) };

        private static GameObject ResolveSold(SoldRef d, SellableObject[] all)
        {
            GameObject go = d.Key.Length > 0 ? ObjectSync.Find(d.Key) : null;
            if (go != null) return go;
            if (d.MachineId < 0 && d.Key.Length > 0) return null;
            SellableObject so = FindRef(d.MachineId, d.SaveId, d.Pos, d.Key.Length > 0, all);
            return so != null ? so.gameObject : null;
        }

        [HarmonyPatch]
        private static class PatchParcelReset
        {
            private static MethodBase Target => AccessTools.Method(typeof(BankParcelGUI), nameof(BankParcelGUI.ResetConfirm), Type.EmptyTypes);

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Purchase sync: Harmony target not found, feature disabled: BankParcelGUI.ResetConfirm()");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            // Same selection as the game, before it detaches / destroys / moves anything.
            private static void Prefix(BankParcelGUI __instance)
            {
                _reset = null;
                if (!CanSend || !global::Singleton<LaptopGlobalManager>.IsInstanced()) return;
                try
                {
                    List<LotDescriptor> lots = global::Singleton<LaptopGlobalManager>.Instance.LotList;
                    int lot = __instance.CurrentClaim;
                    if (lots == null || lot < 0 || lot >= lots.Count || lots[lot] == null) return;
                    var cap = new ResetCapture { Lot = lot };
                    List<GameObject> terrains = lots[lot].Terrains;
                    Transform parent = terrains != null && terrains.Count > 0 && terrains[0] != null ? terrains[0].transform.parent : null;
                    if (parent != null)
                    {
                        var list = new List<SellableObject>(parent.GetComponentsInChildren<SellableObject>());
                        SellableObject pickup = list.Find(d => d != null && d.OrginalPrefabName == "Chevrolet_CK_Pickup");
                        foreach (SellableObject so in list)
                        {
                            if (so == null) continue;
                            if (so == pickup || so.IsTrailer()) cap.Expected.Add(new KeyValuePair<SoldRef, byte>(RefOf(so.gameObject), 1));
                            else if (so.IsBigMachine()) cap.Expected.Add(new KeyValuePair<SoldRef, byte>(RefOf(so.gameObject), 0));
                            else
                            {
                                cap.Destroyed.Add(RefOf(so.gameObject));
                                cap.DestroyedGos.Add(so.gameObject);
                            }
                        }
                    }
                    cap.Gen = ObjectSync.IndexGeneration; // after RefOf (may build the index)
                    _reset = cap;
                }
                catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: parcel reset capture failed: " + ex.Message); }
            }

            private static void Postfix()
            {
                ResetCapture cap = _reset;
                _reset = null;
                if (cap == null || !Net.Active) return;
                if (cap.Destroyed.Count == 0 && cap.Moves.Count == 0 && cap.Expected.Count == 0) return;
                foreach (GameObject g in cap.DestroyedGos) ObjectSync.ForgetQuiet(g); // no generic Destroy echo
                SendNamed(() =>
                {
                    var ms = BeginMsg(Kind.ParcelReset, out BinaryWriter w);
                    w.Write(cap.Lot);
                    w.Write(cap.Destroyed.Count);
                    foreach (SoldRef d in cap.Destroyed) WriteSoldRef(w, d);
                    w.Write(cap.Expected.Count);
                    foreach (var kv in cap.Expected) { WriteSoldRef(w, kv.Key); w.Write(kv.Value); }
                    w.Write(cap.Moves.Count);
                    foreach (ResetMove m in cap.Moves)
                    {
                        WriteSoldRef(w, m.Ref);
                        w.Write(m.Place ?? "");
                        WriteQ(w, m.Rot);
                        WriteV3(w, m.Offset);
                        w.Write(m.SetActive);
                    }
                    return ms.ToArray();
                }, () => Plugin.Log.LogInfo($"Purchase sync: sent parcel reset of lot {cap.Lot}: {cap.Destroyed.Count} sold, {cap.Moves.Count} moved"),
                cap.Gen);
            }

            private static void Finalizer() => _reset = null;
        }

        /// <summary>Detaches holdables under <paramref name="go"/> like ResetConfirm does.</summary>
        private static void DetachLikeReset(GameObject go)
        {
            try
            {
                foreach (ShovelHoldable s in go.GetComponentsInChildren<ShovelHoldable>())
                    if (s.MyHolder != null) s.Detach();
                foreach (Holdable h in go.GetComponentsInChildren<Holdable>())
                    if (!(h is RepairHoldable) && h.IsAttached()) h.Detach();
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: detach on reset failed: " + ex.Message); }
            ObjectSync.AcceptHolderState(go); // the clicker did the same: not a local change
        }

        private static void ReceiveParcelReset(BinaryReader r)
        {
            int lot = r.ReadInt32();
            int nd = r.ReadInt32();
            var destroyed = new List<SoldRef>(nd);
            for (int i = 0; i < nd; i++) destroyed.Add(ReadSoldRef(r));
            int ne = r.ReadInt32();
            var expected = new List<KeyValuePair<SoldRef, byte>>(ne);
            for (int i = 0; i < ne; i++) { SoldRef d = ReadSoldRef(r); expected.Add(new KeyValuePair<SoldRef, byte>(d, r.ReadByte())); }
            int nm = r.ReadInt32();
            var moves = new List<ResetMove>(nm);
            for (int i = 0; i < nm; i++)
                moves.Add(new ResetMove { Ref = ReadSoldRef(r), Place = r.ReadString(), Rot = ReadQ(r), Offset = ReadV3(r), SetActive = r.ReadBoolean() });

            SellableObject[] all = AllSellables();
            MachineController driven = global::Singleton<Player>.IsInstanced() ? global::Singleton<Player>.Instance.GetControlledMachine() : null;
            GravityGun gun = global::Singleton<GravityGun>.IsInstanced() ? global::Singleton<GravityGun>.Instance : null;
            bool dontPick = gun != null && DontPickRef != null && DontPickRef(gun);
            if (gun != null && DontPickRef != null) DontPickRef(gun) = true; // as ResetConfirm does while detaching
            int sold = 0, moved = 0;
            try
            {
                // Resolve everything first: destroying / moving must not change what later refs match.
                var soldGos = new List<KeyValuePair<SoldRef, GameObject>>();
                foreach (SoldRef d in destroyed) soldGos.Add(new KeyValuePair<SoldRef, GameObject>(d, ResolveSold(d, all)));
                var moveGos = new List<GameObject>();
                foreach (ResetMove m in moves) moveGos.Add(ResolveSold(m.Ref, all));
                var expGos = new List<GameObject>();
                foreach (var kv in expected) expGos.Add(ResolveSold(kv.Key, all));

                foreach (var kv in soldGos)
                {
                    GameObject go = kv.Value;
                    if (go == null)
                    {
                        if (kv.Key.Key.Length > 0) ObjectSync.DestroyKeyed(kv.Key.Key, kv.Key.SaveId, kv.Key.Pos); // waits for the key sync
                        continue;
                    }
                    ExitIfLocallyInside(go);
                    DetachLikeReset(go);
                    string k = ObjectSync.KeyOf(go);
                    if (k != null) ObjectSync.DestroyKeyed(k, kv.Key.SaveId, kv.Key.Pos);
                    else Object.Destroy(go);
                    sold++;
                }
                foreach (GameObject go in expGos) if (go != null) DetachLikeReset(go);

                var done = new HashSet<GameObject>();
                for (int i = 0; i < moves.Count; i++)
                {
                    GameObject go = moveGos[i];
                    if (go == null || (driven != null && driven.gameObject == go)) continue; // driven here: MachineSync follows the driver
                    DepotPlace place = moves[i].Place.Length > 0 ? Resolve(moves[i].Place)?.GetComponent<DepotPlace>() : null;
                    if (place == null) continue; // fallback below for the parcel's own objects
                    try
                    {
                        place.TransportGameObject(go, moves[i].Rot, moves[i].Offset, moves[i].SetActive);
                        done.Add(go);
                        moved++;
                    }
                    catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: reset move failed: " + ex.Message); }
                }
                // Places not loaded here: do what ResetConfirm does with the objects it moves.
                if (global::Singleton<LaptopGlobalManager>.IsInstanced())
                {
                    LaptopGlobalManager mgr = global::Singleton<LaptopGlobalManager>.Instance;
                    TransportClaim store = mgr.AllTransportClaim?.Find(d => d.Name == "TRANSPORT_GUI_MACHINE_STORE_NAME");
                    for (int i = 0; i < expected.Count; i++)
                    {
                        GameObject go = expGos[i];
                        if (go == null || done.Contains(go) || (driven != null && driven.gameObject == go)) continue;
                        SellableObject so = go.GetComponent<SellableObject>();
                        if (so == null) continue;
                        try
                        {
                            if (expected[i].Value == 0 && store != null) store.ClaimDepot.Teleport(so);
                            else mgr.TeleportToClosestClaim(so);
                            moved++;
                        }
                        catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: reset teleport failed: " + ex.Message); }
                    }
                }
            }
            finally
            {
                if (gun != null && DontPickRef != null) DontPickRef(gun) = dontPick;
            }
            Plugin.Log.LogInfo($"Purchase sync: parcel reset of lot {lot} applied: {sold}/{destroyed.Count} sold, {moved} moved");
        }

        // ---------- depot moves of loose objects and wash plants (transport service, laptop transport) ----------
        // TransportGUI / LaptopTransport / TeleportToClosestClaim move objects with Depot.Teleport ->
        // DepotPlace.TransportGameObject on the clicking PC only. Vehicles and trailers are sent by MachineSync
        // (sub -6), but wash plants, generators, pumps and other loose objects stayed at the old claim on the
        // other PCs (a teleport zeroes the velocity, so ObjectSync's motion claim never fired either): the
        // host kept processing a wash plant that was gone for the player who moved it. The move is replayed
        // with the same place, offset and rotation one frame later (objects the same call sells / returns
        // are destroyed by then and skipped: their own message removes them).

        private sealed class PendingMove
        {
            public GameObject Go;
            public SoldRef Ref;      // captured before the first move (old position)
            public string Place;
            public Quaternion Rot;
            public Vector3 Offset;
            public bool SetActive;
            public int Frame, Gen;
        }

        private static readonly List<PendingMove> PendingMoves = new List<PendingMove>();

        private static void CaptureMove(DepotPlace place, GameObject go, Quaternion rot, Vector3 offset, bool setActive)
        {
            if (go.GetComponent<SellableObject>() == null) return;
            PendingMove m = PendingMoves.Find(x => x.Go == go);
            if (m == null)
            {
                if (PendingMoves.Count >= 256) return;
                m = new PendingMove { Go = go, Ref = RefOf(go) };
                m.Gen = ObjectSync.IndexGeneration; // after RefOf (may build the index)
                PendingMoves.Add(m);
            }
            // GetFirstPlace may move an object twice in one call: the last place is the result.
            m.Place = PathOf(place.transform);
            m.Rot = rot;
            m.Offset = offset;
            m.SetActive = setActive;
            m.Frame = Time.frameCount;
        }

        private static void FlushMoves()
        {
            for (int i = PendingMoves.Count - 1; i >= 0; i--)
            {
                PendingMove m = PendingMoves[i];
                if (m.Frame >= Time.frameCount) continue; // DepotPlace / the caller finish this frame
                PendingMoves.RemoveAt(i);
                GameObject go = m.Go;
                if (go == null || !Net.WorldLive) continue; // sold / returned meanwhile, or the world went away
                Vector3 pos = go.transform.position;
                Quaternion rot = go.transform.rotation;
                bool active = go.activeSelf;
                string area = AreaSceneOf(go.transform);
                string name = go.name;
                SendNamed(() =>
                {
                    var ms = BeginMsg(Kind.Move, out BinaryWriter w);
                    WriteSoldRef(w, m.Ref);
                    w.Write(m.Place ?? "");
                    WriteQ(w, m.Rot);
                    WriteV3(w, m.Offset);
                    w.Write(m.SetActive);
                    WriteV3(w, pos);
                    WriteQ(w, rot);
                    w.Write(active);
                    w.Write(area);
                    return ms.ToArray();
                }, () =>
                {
                    // Keyed physics object: this PC streams it / sends its exact rest pose at the new place.
                    if (go != null && go.activeInHierarchy) ObjectSync.ClaimLocal(go);
                    Plugin.Log.LogInfo("Purchase sync: sent depot move of " + name);
                }, m.Gen);
            }
        }

        private static void ReceiveMove(BinaryReader r)
        {
            SoldRef d = ReadSoldRef(r);
            string placePath = r.ReadString();
            Quaternion prot = ReadQ(r);
            Vector3 offset = ReadV3(r);
            bool setActive = r.ReadBoolean();
            Vector3 pos = ReadV3(r);
            Quaternion rot = ReadQ(r);
            bool active = r.ReadBoolean();
            string areaScene = r.ReadString();
            GameObject go = ResolveSold(d, null);
            if (go == null) { Plugin.Log.LogWarning("Purchase sync: moved object not found here: " + d.SaveId); return; }
            // Something the local player holds / uses: freed first (as for a sale).
            ExitIfLocallyInside(go);
            if (global::Singleton<GravityGun>.IsInstanced() && global::Singleton<GravityGun>.Instance.PickedObject == go)
                global::Singleton<GravityGun>.Instance.DropObject(false);
            DepotPlace place = placePath.Length > 0 ? Resolve(placePath)?.GetComponent<DepotPlace>() : null;
            bool done = false;
            if (place != null)
            {
                try { place.TransportGameObject(go, prot, offset, setActive); done = true; }
                catch (Exception ex) { Plugin.Log.LogWarning("Purchase sync: depot move failed: " + ex.Message); }
            }
            // Place not loaded here (or the move failed): the mover's final pose, under the area it belongs to.
            if (!done)
            {
                go.transform.SetPositionAndRotation(pos, rot);
                AreaStreamer area = FindArea(areaScene) ?? ObjectSync.AreaAt(pos);
                if (area != null) ObjectSync.ParentToArea(go, area);
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            }
            // StoreDepotPlace sets the active state after its base call (the captured flag is the base's).
            if (go.activeSelf != active) go.SetActive(active);
            ObjectSync.AcceptHolderState(go); // the mover's game unhooked the same hoses / ropes
            Plugin.Log.LogInfo("Purchase sync: depot move applied: " + go.name);
        }

        // ---------- rentals ----------

        [HarmonyPatch]
        private static class PatchBuyoutBuy
        {
            private static bool Prepare() => Targets.BuyoutBuy != null && Targets.BuyoutMachine != null;
            private static MethodBase TargetMethod() => Targets.BuyoutBuy;

            private static void Prefix(BuyoutRentedShopItemGUI __instance, out SellableObject __state)
            {
                var m = Targets.BuyoutMachine.GetValue(__instance) as SellableObject;
                __state = m != null && m.IsRented ? m : null;
            }

            private static void Postfix(SellableObject __state)
            {
                if (__state != null && !__state.IsRented) SendRef(Kind.RentBuyout, __state);
            }
        }

        [HarmonyPatch]
        private static class PatchBuyoutSell
        {
            private static bool Prepare() => Targets.BuyoutSell != null && Targets.BuyoutMachine != null;
            private static MethodBase TargetMethod() => Targets.BuyoutSell;

            // Before the game destroys the object, while it can still be identified.
            private static void Prefix(BuyoutRentedShopItemGUI __instance)
            {
                var m = Targets.BuyoutMachine.GetValue(__instance) as SellableObject;
                if (m == null || !CanSend) return;
                SendRef(Kind.RentReturn, m);
                ObjectSync.ForgetQuiet(m.gameObject); // the game destroys it now; no generic Destroy echo
            }
        }

        /// <summary>
        /// The sell zone's "buy rented" button (SellGUI.BuyRentedObjects -> BuyObjects) buys out every rented
        /// machine standing in the zone. Only the money travelled (ledger): the other PCs kept the machine
        /// rented, so the host went on charging its daily rent and could even take it back
        /// (Finance.ReturnMachine) although the player had paid for it. Same RentBuyout as the laptop.
        /// </summary>
        [HarmonyPatch]
        private static class PatchSellZoneBuyout
        {
            private static MethodBase Target => AccessTools.Method(typeof(SellGUI), nameof(SellGUI.BuyObjects), Type.EmptyTypes);
            private static bool Prepare() => Need(Target, "SellGUI.BuyObjects()") != null;
            private static MethodBase TargetMethod() => Target;

            private static void Prefix(SellGUI __instance, out List<SellableObject> __state)
            {
                __state = null;
                if (__instance == null || __instance.Items == null || !CanSend) return;
                foreach (CashRegisterItem item in __instance.Items)
                    if (item != null && item.isRented && item.MySellObject != null && item.MySellObject.IsRented)
                        (__state ??= new List<SellableObject>()).Add(item.MySellObject);
            }

            private static void Postfix(List<SellableObject> __state)
            {
                if (__state == null) return;
                foreach (SellableObject so in __state)
                    if (so != null && !so.IsRented) SendRef(Kind.RentBuyout, so);
            }
        }

        /// <summary>
        /// Daily rent the money does not cover: Finance.ReturnMachine takes the rented machine back. It runs
        /// on the host only (GoldSync skips it on clients), and machines are not ObjectSync objects, so the
        /// clients kept the machine for good. The host now sends the same RentReturn as the laptop button.
        /// The game spares the machine the local player sits in; the host also spares one another player
        /// drives (it would vanish under that player). Both are taken back on a later day.
        /// </summary>
        [HarmonyPatch]
        private static class PatchDailyRentalReturn
        {
            private static MethodBase Target => AccessTools.Method(typeof(Finance), "ReturnMachine", new[] { typeof(SellableObject) });
            private static bool Prepare() => Need(Target, "Finance.ReturnMachine(SellableObject)") != null;
            private static MethodBase TargetMethod() => Target;

            private static bool Prefix(SellableObject so)
            {
                if (so == null || !so.IsRented || _applying || !Net.IsHost || !Net.WorldLive) return true;
                MachineController mc = so.GetComponent<MachineController>();
                MachineController mine = global::Singleton<Player>.IsInstanced() ? global::Singleton<Player>.Instance.GetControlledMachine() : null;
                if (mc != null && mine != null && mine == mc) return true; // the game keeps it (host inside)
                if (mc != null && MachineSync.IsRemoteDriven(MachineSync.IdOf(mc)))
                {
                    Plugin.Log.LogInfo($"Purchase sync: rented {so.name} not returned today, another player drives it");
                    return false;
                }
                SendRef(Kind.RentReturn, so);
                ObjectSync.ForgetQuiet(so.gameObject); // the game destroys it now; no generic Destroy echo
                return true;
            }
        }

        // ---------- selling (SellGUI.SellObjects) ----------
        // The seller destroys the sold objects (machines, trailers, loose items) and the bucket stacks it
        // deconstructs; buckets of a stack that were not sold appear as new objects. CashSync carries the
        // money; this message makes the objects match: destroyed by ObjectSync key, MachineSync id or
        // SaveID + nearest position, created buckets with a fresh network key.

        private static readonly FieldInfo SpawnedBucketsField = AccessTools.Field(typeof(SellGUI), "spawnedBuckets");
        private static readonly FieldInfo StacksToDeconstructField = AccessTools.Field(typeof(SellGUI), "bucketStacksToDeconstruct");

        private struct SoldRef
        {
            public bool Ordinal; // Key is a save ordinal (keys not shared yet when captured)
            public string Key, SaveId;
            public int MachineId;
            public Vector3 Pos;
        }

        private sealed class SellState
        {
            public readonly List<SoldRef> Destroyed = new List<SoldRef>();
            public readonly List<GameObject> DestroyedGos = new List<GameObject>();
            public readonly List<Bucket> Spawned = new List<Bucket>();
            public readonly HashSet<GameObject> SoldGos = new HashSet<GameObject>();
            public int Gen; // ObjectSync.IndexGeneration of the refs
        }

        private static SoldRef RefOf(GameObject go)
        {
            var mc = go.GetComponent<MachineController>();
            var save = go.GetComponent<SaveSystem.SaveMe>();
            string key = ObjectSync.RefKey(go, out bool ordinal);
            return new SoldRef
            {
                Ordinal = ordinal && key != null,
                Key = key ?? "",
                MachineId = mc != null ? MachineSync.IdOf(mc) : -1,
                SaveId = save != null ? save.SaveID ?? "" : "",
                Pos = go.transform.position,
            };
        }

        [HarmonyPatch]
        private static class PatchSellObjects
        {
            private static MethodBase Target => AccessTools.Method(typeof(SellGUI), nameof(SellGUI.SellObjects), Type.EmptyTypes);

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Purchase sync: Harmony target not found, feature disabled: SellGUI.SellObjects()");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static void Prefix(SellGUI __instance, out SellState __state)
            {
                __state = null;
                if (!CanSend) return;
                var st = new SellState();
                if (SpawnedBucketsField?.GetValue(__instance) is List<Bucket> spawned)
                    foreach (Bucket b in spawned) if (b != null) st.Spawned.Add(b);
                var temp = new HashSet<GameObject>();
                foreach (Bucket b in st.Spawned) temp.Add(b.gameObject);
                foreach (SellableObject so in __instance.ObjectsToBuy)
                {
                    if (so == null) continue;
                    st.SoldGos.Add(so.gameObject);
                    if (temp.Contains(so.gameObject)) continue; // stand-in bucket that exists on this PC only
                    st.Destroyed.Add(RefOf(so.gameObject));
                    st.DestroyedGos.Add(so.gameObject);
                }
                if (StacksToDeconstructField?.GetValue(__instance) is List<BucketStack> stacks)
                    foreach (BucketStack bs in stacks)
                        if (bs != null)
                        {
                            st.Destroyed.Add(RefOf(bs.gameObject));
                            st.DestroyedGos.Add(bs.gameObject);
                        }
                st.Gen = ObjectSync.IndexGeneration; // after RefOf (may build the index)
                __state = st;
            }

            private static void Postfix(SellState __state)
            {
                if (__state == null || !Net.Active) return;
                // Unsold buckets of deconstructed stacks are now real objects on this PC.
                var buckets = new List<GameObject>();
                foreach (Bucket b in __state.Spawned)
                {
                    if (b == null || __state.SoldGos.Contains(b.gameObject) || !b.gameObject.activeSelf) continue;
                    ObjectSync.RegisterObject(b.gameObject, ObjectSync.NewLocalKey()); // a key that never changes
                    buckets.Add(b.gameObject);
                }
                if (__state.Destroyed.Count == 0 && buckets.Count == 0) return;
                foreach (GameObject g in __state.DestroyedGos) ObjectSync.ForgetQuiet(g); // no Destroy echo

                // Built when sent (at once, or after the key sync on a client that just joined).
                List<SoldRef> destroyed = __state.Destroyed;
                var created = new List<GameObject>();
                SendNamed(() =>
                {
                    created.Clear();
                    foreach (GameObject g in buckets) if (g != null && ObjectSync.KeyOf(g) != null) created.Add(g);
                    var ms = BeginMsg(Kind.Sell, out BinaryWriter w);
                    w.Write(destroyed.Count);
                    foreach (SoldRef d in destroyed) WriteSoldRef(w, d);
                    w.Write(created.Count);
                    foreach (GameObject g in created)
                    {
                        var save = g.GetComponent<SaveSystem.SaveMe>();
                        w.Write(ObjectSync.KeyOf(g));
                        w.Write(save != null ? save.SaveID ?? "" : "");
                        WriteV3(w, g.transform.position);
                        WriteQ(w, g.transform.rotation);
                    }
                    return ms.ToArray();
                }, () =>
                {
                    foreach (GameObject g in created) ObjectSync.ClaimLocal(g);
                    Plugin.Log.LogInfo($"Purchase sync: sent sale of {destroyed.Count} objects (+{created.Count} buckets)");
                }, __state.Gen);
            }
        }

        // ---------- workers ----------

        private static int WorkerCount() =>
            global::Singleton<WorkersManager>.IsInstanced() ? global::Singleton<WorkersManager>.Instance.MyWorkers.Count : -1;

        private static void SendNewWorkers(int before)
        {
            if (before < 0 || !Net.Active || _applying || !global::Singleton<WorkersManager>.IsInstanced()) return;
            List<Worker> list = global::Singleton<WorkersManager>.Instance.MyWorkers;
            for (int i = before; i < list.Count; i++) SendWorker(list[i]);
        }

        [HarmonyPatch]
        private static class PatchWorkersHire
        {
            private static bool Prepare() => Targets.WorkersHire != null || Targets.LaptopHire != null;

            private static IEnumerable<MethodBase> TargetMethods()
            {
                if (Targets.WorkersHire != null) yield return Targets.WorkersHire;
                if (Targets.LaptopHire != null) yield return Targets.LaptopHire;
            }

            private static void Prefix(out int __state) => __state = WorkerCount();
            private static void Postfix(int __state) => SendNewWorkers(__state);
        }
    }
}
