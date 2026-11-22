// WIRING:
//   Net.cs, Net.Handle switch:  case (Msg)13: BedSync.Receive(r); break;   (relayed by the host like the others)
//   MachineSync calls OnRemoteDriveStart (first pose, and again when a trailer gets hooked during a remote
//   drive), OnRemoteDriveStop (MachineSync.RestoreBody, every release path), Tick and Reset.

using System;
using System.Collections;
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
    /// Keeps cargo on a pickup bed (and its trailer bed) identical on every PC.
    ///
    /// The game freezes cargo only for the local driver: Pickup.EnterToVehicle calls
    /// TransportBed.FreezeObjects (one physics step later FreezeNow deactivates the items found by the
    /// bed trigger, parents them to the bed/MeshRoot and draws mesh copies) and Pickup.ExitMachine
    /// calls Unfreeze (items become active physics objects again where they were).
    ///
    /// The set the trigger finds, and the item poses, differ between PCs (each PC simulates thrown
    /// items itself). So the DRIVER's set is authoritative ((Msg)13, all reliable):
    ///   Freeze   (sub 0) driver froze a bed: machine id, driver LocalId, slot (0 = truck bed, 1 = trailer
    ///            bed), items (ObjectSync key, SaveID, pose LOCAL to the bed transform).
    ///            Receiver: places each listed item at that local pose on its copy of the bed and
    ///            freezes exactly those items (re-freezes if it had frozen something else).
    ///   Unfreeze (sub 1) driver got out: id, driver LocalId, slot, bed world pose, items (key, SaveID, local pose).
    ///            Receiver: when MachineSync has placed the truck at its final pose and released it,
    ///            unfreezes and puts every listed item at the same world pose.
    ///   Request  (sub 2) receiver started showing a remote drive without a snapshot: id. The driver
    ///            answers with Freeze snapshots of its frozen beds.
    /// Every packet: sub byte, world epoch byte (MachineSync.Epoch; other world versions are ignored), then the above.
    /// Freeze/Unfreeze are applied only from the driver MachineSync shows for that machine (two players may
    /// briefly claim the same machine; the one that loses must not overwrite the cargo).
    /// Without any snapshot (lost driver) the receiver freezes its own trigger set after 1.5 s.
    /// While a bed is held for a remote driver, the game's own Freeze/Unfreeze calls on it (10 s
    /// auto-unfreeze, a local passenger entering/leaving the cab) are blocked.
    /// </summary>
    internal static class BedSync
    {
        internal const Msg MsgId = (Msg)13;
        private const byte SubFreeze = 0;
        private const byte SubUnfreeze = 1;
        private const byte SubRequest = 2;

        private const float FallbackAfter = 1.5f;  // s without snapshot -> freeze local trigger set
        private const float PendingLifetime = 5f;  // snapshot waiting for its remote drive start
        // An Unfreeze waits for the release of the truck here, which comes only after it stopped rolling on the
        // driver's PC (MachineSync coasting, up to 20 s). With 5 s the snapshot of a truck that rolled on for a
        // few seconds expired and its cargo was unfrozen wherever it happened to be on this PC.
        private const float PendingUnfreezeLifetime = 30f;

        private class Held
        {
            public MachineController Machine;
            public int Id, Slot;
            public float Since;
            public bool Done; // snapshot applied or fallback started
        }

        private struct Entry
        {
            public string Key, Sid;
            public Vector3 Pos;      // local to the bed transform
            public Quaternion Rot;
        }

        private class Snapshot
        {
            public float Received;
            public ulong Sender;     // driver LocalId
            public Vector3 BedPos;   // Unfreeze only: sender's bed world pose
            public Quaternion BedRot;
            public List<Entry> Items;
        }

        // Beds frozen because their machine is remote-driven.
        private static readonly Dictionary<TransportBed, Held> HeldBeds = new Dictionary<TransportBed, Held>();
        // Snapshots that arrived before the remote drive started (Freeze) / before it ended (Unfreeze).
        private static readonly Dictionary<long, Snapshot> PendingFreeze = new Dictionary<long, Snapshot>();
        private static readonly Dictionary<long, Snapshot> PendingUnfreeze = new Dictionary<long, Snapshot>();
        // Every bed currently frozen on this PC (by the game or by us), for IsFrozenItem.
        private static readonly HashSet<TransportBed> FrozenBeds = new HashSet<TransportBed>();
        // Item -> bed it is frozen on (rebuilt whenever a bed freezes/unfreezes): O(1) IsFrozenItem,
        // which ObjectSync asks for many objects every frame.
        private static readonly Dictionary<GameObject, TransportBed> FrozenItems = new Dictionary<GameObject, TransportBed>();

        private static void RebuildFrozenItems()
        {
            FrozenItems.Clear();
            FrozenBeds.RemoveWhere(b => b == null || !b.ObjectsFreezed);
            foreach (TransportBed bed in FrozenBeds)
                foreach (GameObject go in bed.GameObjects)
                    if (go != null) FrozenItems[go] = bed;
        }

        private static bool _allowUnfreeze, _allowFreeze;
        private static readonly Predicate<GameObject> IsDestroyed = go => go == null;

        private static long Slot(int id, int slot) => ((long)id << 2) | (uint)slot;

        // =====================================================================================
        // Queries
        // =====================================================================================

        internal static bool IsHeld(TransportBed bed) => bed != null && HeldBeds.ContainsKey(bed);

        /// <summary>
        /// True if <paramref name="go"/> is cargo frozen on a bed on this PC (inactive, parented to the
        /// bed, drawn as mesh copies). Loose-object sync must neither send nor apply poses for it.
        /// </summary>
        internal static bool IsFrozenItem(GameObject go)
        {
            if (go == null || go.activeSelf || FrozenItems.Count == 0) return false;
            return FrozenItems.TryGetValue(go, out TransportBed bed) && bed != null && bed.ObjectsFreezed && bed.GameObjects.Contains(go);
        }

        // =====================================================================================
        // MachineSync hooks
        // =====================================================================================

        /// <summary>MachineSync started applying remote poses to <paramref name="m"/>.</summary>
        internal static void OnRemoteDriveStart(MachineController m)
        {
            if (m == null) return;
            int id = MachineSync.IdOf(m);

            bool request = false;
            foreach (var (bed, slot) in BedsOf(m))
            {
                if (HeldBeds.ContainsKey(bed)) continue;
                var h = new Held { Machine = m, Id = id, Slot = slot, Since = Time.unscaledTime };
                HeldBeds[bed] = h;
                if (id >= 0 && PendingFreeze.TryGetValue(Slot(id, slot), out var snap) &&
                    (snap.Sender == MachineSync.DriverOf(id) || MachineSync.DriverOf(id) == 0))
                {
                    PendingFreeze.Remove(Slot(id, slot));
                    ApplyFreeze(bed, h, snap);
                }
                else request = true;
            }
            if (request && id >= 0)
            {
                var ms = Begin(SubRequest, out var w);
                w.Write(id);
                Send(ms);
            }
        }

        /// <summary>
        /// MachineSync: another player now drives <paramref name="m"/> (two claimed it at once, the lower id won).
        /// The cargo frozen so far may be the loser's set: ask the winner for its snapshot.
        /// </summary>
        internal static void OnDriverChanged(MachineController m)
        {
            int id = MachineSync.IdOf(m);
            if (id < 0) return;
            foreach (var (bed, _) in BedsOf(m))
                if (HeldBeds.TryGetValue(bed, out var h)) { h.Done = false; h.Since = Time.unscaledTime; }
            var ms = Begin(SubRequest, out var w);
            w.Write(id);
            Send(ms);
        }

        /// <summary>MachineSync released <paramref name="m"/> (driver left or timed out).</summary>
        /// <param name="id">Machine id; pass it when <paramref name="m"/> may already be destroyed.</param>
        internal static void OnRemoteDriveStop(MachineController m, int id = -1)
        {
            if (id < 0) id = MachineSync.IdOf(m);

            // The local player sits in it (passenger taking over as driver): keep the cargo frozen,
            // the game unfreezes it when we get out, and tell the others what we carry.
            bool localInside = m != null && m.IsControlled();

            List<KeyValuePair<TransportBed, Held>> release = null;
            foreach (var kv in HeldBeds)
                if (kv.Value.Machine == m || kv.Value.Machine == null || kv.Key == null)
                    (release ??= new List<KeyValuePair<TransportBed, Held>>()).Add(kv);
            if (release == null) return;

            foreach (var kv in release)
            {
                TransportBed bed = kv.Key;
                Held h = kv.Value;
                HeldBeds.Remove(bed);
                long k = Slot(h.Id, h.Slot);
                PendingUnfreeze.TryGetValue(k, out var snap);
                PendingUnfreeze.Remove(k);
                if (bed == null) continue;
                if (localInside)
                {
                    if (bed.ObjectsFreezed) SendFreeze(bed, h.Id, h.Slot);
                    continue;
                }
                if (snap != null) ApplyUnfreeze(bed, h.Slot, snap);
                else UnfreezeBed(bed); // timeout / old build: items stay where they are on this PC
            }
        }

        /// <summary>Called from MachineSync.Tick (Update): fallback freeze, expiry.</summary>
        internal static void Tick()
        {
            if (HeldBeds.Count == 0 && PendingFreeze.Count == 0 && PendingUnfreeze.Count == 0) return;
            float now = Time.unscaledTime;

            List<TransportBed> dead = null;
            foreach (var kv in HeldBeds)
            {
                TransportBed bed = kv.Key;
                Held h = kv.Value;
                if (bed == null) { (dead ??= new List<TransportBed>()).Add(bed); continue; }
                if (h.Done || now - h.Since < FallbackAfter) continue;
                h.Done = true;
                if (bed.ObjectsFreezed || !bed.isActiveAndEnabled) continue;
                Plugin.Log.LogInfo($"Bed sync: no cargo snapshot for #{h.Id}/{h.Slot}, freezing local cargo");
                bed.StartCoroutine(FallbackFreeze(bed));
            }
            if (dead != null) foreach (var b in dead) HeldBeds.Remove(b);

            Expire(PendingFreeze, now, PendingLifetime);
            Expire(PendingUnfreeze, now, PendingUnfreezeLifetime);
        }

        private static void Expire(Dictionary<long, Snapshot> d, float now, float lifetime)
        {
            if (d.Count == 0) return;
            List<long> old = null;
            foreach (var kv in d)
                if (now - kv.Value.Received > lifetime) (old ??= new List<long>()).Add(kv.Key);
            if (old != null) foreach (long k in old) d.Remove(k);
        }

        /// <summary>Like FreezeObjectsCorutine: one physics step so the bed trigger has listed what lies on it.</summary>
        private static IEnumerator FallbackFreeze(TransportBed bed)
        {
            yield return new WaitForFixedUpdate();
            if (bed == null || bed.ObjectsFreezed || !HeldBeds.ContainsKey(bed)) yield break;
            FreezeBed(bed);
        }

        /// <summary>Forgets everything (scene change / session end).</summary>
        internal static void Reset()
        {
            HeldBeds.Clear();
            PendingFreeze.Clear();
            PendingUnfreeze.Clear();
            RebuildFrozenItems();
        }

        // =====================================================================================
        // Sender (driver)
        // =====================================================================================

        /// <summary>The bed slot of the machine the local player drives (not as passenger), or -1.</summary>
        private static int LocalDriverSlot(TransportBed bed, out int id, Pickup exiting = null)
        {
            id = -1;
            if (!Net.Active || !MachineSync.WorldReady()) return -1;
            Pickup p = MachineSync.LocalDrivenMachine as Pickup;
            // Getting out without the animation (EnterVehicleController.ExitMachine(noAnim: true): forced stop,
            // fast travel) ends the local drive before Pickup.ExitMachine runs: the pickup we were streaming
            // as its driver still is ours here. (Without this the cargo snapshot was not sent.)
            if (p == null && exiting != null && MachineSync.LastSentId >= 0 && MachineSync.LastSentId == MachineSync.IdOf(exiting) &&
                !MachineSync.IsRemoteDriven(exiting))
                p = exiting;
            if (p == null) return -1; // not as a passenger
            id = MachineSync.IdOf(p);
            if (id < 0) return -1;
            foreach (var (b, slot) in BedsOf(p))
                if (b == bed) return slot;
            return -1;
        }

        private static void SendFreeze(TransportBed bed, int id, int slot)
        {
            var ms = Begin(SubFreeze, out var w);
            w.Write(id);
            w.Write(Net.LocalId);
            w.Write((byte)slot);
            WriteItems(w, bed, bed.GameObjects);
            Send(ms);
            Plugin.Log.LogInfo($"Bed sync: sent cargo of #{id}/{slot} ({bed.GameObjects.Count} items)");
        }

        private static void SendUnfreeze(TransportBed bed, int id, int slot, List<GameObject> items)
        {
            var ms = Begin(SubUnfreeze, out var w);
            w.Write(id);
            w.Write(Net.LocalId);
            w.Write((byte)slot);
            Transform t = bed.transform;
            ObjectSync.WriteV3(w, t.position);
            ObjectSync.WriteQ(w, t.rotation);
            WriteItems(w, bed, items);
            Send(ms);
        }

        private static void WriteItems(BinaryWriter w, TransportBed bed, List<GameObject> items)
        {
            Transform t = bed.transform;
            Quaternion inv = Quaternion.Inverse(t.rotation);
            int n = 0;
            foreach (GameObject go in items) if (go != null) n++;
            w.Write((ushort)n);
            foreach (GameObject go in items)
            {
                if (go == null) continue;
                w.Write(ObjectSync.KeyOf(go) ?? "");
                var sm = go.GetComponent<SaveSystem.SaveMe>();
                w.Write(sm != null ? sm.GetSaveID() ?? "" : "");
                ObjectSync.WriteV3(w, t.InverseTransformPoint(go.transform.position));
                ObjectSync.WriteQ(w, inv * go.transform.rotation);
            }
        }

        // =====================================================================================
        // Receiver
        // =====================================================================================

        /// <summary>Entry point for every (Msg)13 packet.</summary>
        internal static void Receive(BinaryReader r)
        {
            VehicleTraffic.Rx(VehicleTraffic.Bed, (int)r.BaseStream.Length + 1);
            byte sub = r.ReadByte();
            byte epoch = r.ReadByte();
            int id = r.ReadInt32();
            if (sub == SubRequest)
            {
                if (MachineSync.WorldReady() && MachineSync.EpochOk(epoch)) AnswerRequest(id);
                return;
            }
            ulong sender = r.ReadUInt64();
            int slot = r.ReadByte();
            var snap = new Snapshot { Received = Time.unscaledTime, Sender = sender };
            if (sub == SubUnfreeze)
            {
                snap.BedPos = ObjectSync.ReadV3(r);
                snap.BedRot = Norm(ObjectSync.ReadQ(r));
            }
            int n = r.ReadUInt16();
            snap.Items = new List<Entry>(n);
            for (int i = 0; i < n; i++)
            {
                var e = new Entry { Key = r.ReadString(), Sid = r.ReadString(), Pos = ObjectSync.ReadV3(r) };
                e.Rot = Norm(ObjectSync.ReadQ(r));
                snap.Items.Add(e);
            }
            if (!MachineSync.WorldReady() || sender == Net.LocalId || MachineSync.LeftRecently(sender)) return;
            if (!MachineSync.EpochOk(epoch)) return; // another copy of the world: the id means another machine

            MachineController m = MachineSync.Get(id);
            TransportBed bed = m != null ? BedOf(m, slot) : null;
            if (m == null || (bed == null && slot != 1))
            {
                Plugin.Log.LogWarning($"Bed sync: cargo snapshot for unknown machine/bed #{id}/{slot}");
                return;
            }
            if (bed == null)
            {
                // Trailer not hooked here yet (its hitch message or MachineSync's self-heal is on the way):
                // keep a Freeze until the hitch arrives (OnRemoteDriveStart picks it up), drop an Unfreeze.
                if (sub == SubFreeze) PendingFreeze[Slot(id, slot)] = snap;
                else Plugin.Log.LogWarning($"Bed sync: #{id} has no trailer here, {n} trailer items not unfrozen");
                return;
            }

            // Only the driver this PC shows (or, before/after its drive, the last one) owns the cargo.
            ulong driver = MachineSync.DriverOf(id);
            if (driver != 0 && driver != sender) return;
            if (sub == SubFreeze)
            {
                if (HeldBeds.TryGetValue(bed, out var h)) ApplyFreeze(bed, h, snap);
                else PendingFreeze[Slot(id, slot)] = snap; // pose packets not here yet
            }
            else if (sub == SubUnfreeze)
            {
                PendingFreeze.Remove(Slot(id, slot));
                if (HeldBeds.ContainsKey(bed)) PendingUnfreeze[Slot(id, slot)] = snap; // applied on release
                else if (!m.IsControlled())
                {
                    ulong last = MachineSync.LastDriverOf(id);
                    if (last == 0 || last == sender) ApplyUnfreeze(bed, slot, snap);
                }
            }
        }

        /// <summary>Someone shows our drive without a snapshot: send ours if we drive that machine.</summary>
        private static void AnswerRequest(int id)
        {
            MachineController m = MachineSync.Get(id);
            if (m == null || !m.IsControlled()) return;
            foreach (var (bed, slot) in BedsOf(m))
                if (bed.ObjectsFreezed && LocalDriverSlot(bed, out int myId) == slot && myId == id)
                    SendFreeze(bed, id, slot);
        }

        /// <summary>Freezes exactly the driver's items, each at the driver's pose relative to the bed.</summary>
        private static void ApplyFreeze(TransportBed bed, Held h, Snapshot snap)
        {
            h.Done = true;
            Transform t = bed.transform;
            var lookup = new Lookup();
            var items = new List<KeyValuePair<GameObject, Entry>>(snap.Items.Count);
            int missing = 0, held = 0;
            string firstMissing = null;
            foreach (Entry e in snap.Items)
            {
                GameObject go = lookup.Resolve(e, t.TransformPoint(e.Pos), bed);
                if (go == null) { if (missing++ == 0) firstMissing = Describe(e); continue; }
                if (IsHeldByAnyone(go)) { held++; continue; }
                items.Add(new KeyValuePair<GameObject, Entry>(go, e));
            }
            // One line per snapshot, not one per item (a full bed of bags would write dozens of lines).
            if (missing > 0)
                Plugin.Log.LogWarning($"Bed sync: {missing} cargo item(s) of #{h.Id}/{h.Slot} not found here (first: {firstMissing})");
            if (held > 0) Plugin.Log.LogInfo($"Bed sync: {held} cargo item(s) of #{h.Id}/{h.Slot} held by a player, not frozen");

            if (bed.ObjectsFreezed && SameFrozenState(bed, items)) return;
            if (bed.ObjectsFreezed) UnfreezeBed(bed);

            var list = new List<SaveSystem.SaveMe>(items.Count);
            Quaternion bedRot = t.rotation;
            foreach (var kv in items)
            {
                GameObject go = kv.Key;
                Vector3 pos = t.TransformPoint(kv.Value.Pos);
                Quaternion rot = bedRot * kv.Value.Rot;
                Place(go, pos, rot);
                var sm = go.GetComponent<SaveSystem.SaveMe>();
                if (sm == null) continue;
                list.Add(sm);
                // What OnTriggerStay does for items it lists (SetGroundCheckRadius(item, 0)).
                if (go.GetComponent<SellableObject>() != null && go.TryGetComponent<CheckCanOperateAndFreeze>(out var c))
                    c.GroundRadius = 0f;
            }

            bed.ObjectsOnBedD.Clear();
            bed.ObjectsOnBedD.AddRange(list);
            FreezeBed(bed);
            int frozen = bed.GameObjects.Count;
            Plugin.Log.LogInfo($"Bed sync: froze {frozen}/{snap.Items.Count} cargo items on #{h.Id}/{h.Slot} like the driver" +
                               (missing > 0 ? $" ({missing} missing)" : ""));
        }

        /// <summary>Unfreezes and puts every listed item where it was on the driver's PC.</summary>
        private static void ApplyUnfreeze(TransportBed bed, int slot, Snapshot snap)
        {
            UnfreezeBed(bed);
            Transform t = bed.transform;
            // Truck bed: MachineSync placed the truck exactly at the driver's final pose, so use the
            // driver's world poses. If this bed is elsewhere (trailer simulated here), keep the items on
            // our own bed instead of dropping them into it / next to it.
            bool sameBed = (t.position - snap.BedPos).sqrMagnitude < 0.1f * 0.1f && Quaternion.Angle(t.rotation, snap.BedRot) < 2f;
            Vector3 bedPos = sameBed ? snap.BedPos : t.position;
            Quaternion bedRot = sameBed ? snap.BedRot : t.rotation;
            Vector3 scale = t.lossyScale;

            var lookup = new Lookup();
            int placed = 0;
            foreach (Entry e in snap.Items)
            {
                Vector3 pos = bedPos + bedRot * Vector3.Scale(scale, e.Pos);
                GameObject go = lookup.Resolve(e, pos, bed);
                if (go == null || IsHeldByAnyone(go) || IsFrozenItem(go)) continue;
                Place(go, pos, bedRot * e.Rot);
                placed++;
            }
            Plugin.Log.LogInfo($"Bed sync: unfroze cargo of {bed.name} (slot {slot}), {placed}/{snap.Items.Count} items placed like the driver");
        }

        private static bool SameFrozenState(TransportBed bed, List<KeyValuePair<GameObject, Entry>> items)
        {
            if (bed.GameObjects.Count != items.Count) return false;
            Transform t = bed.transform;
            foreach (var kv in items)
            {
                if (!bed.GameObjects.Contains(kv.Key)) return false;
                Transform it = kv.Key.transform;
                if ((t.InverseTransformPoint(it.position) - kv.Value.Pos).sqrMagnitude > 0.01f * 0.01f) return false;
                if (Quaternion.Angle(t.rotation * kv.Value.Rot, it.rotation) > 1f) return false;
            }
            return true;
        }

        private static void Place(GameObject go, Vector3 pos, Quaternion rot)
        {
            go.transform.SetPositionAndRotation(pos, rot);
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb == null) return;
            rb.position = pos;
            rb.rotation = rot;
            if (!rb.isKinematic)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        private static bool IsHeldByAnyone(GameObject go)
        {
            if (ObjectSync.IsRemotelyHeld(go)) return true;
            if (Singleton<GravityGun>.IsInstanced() && Singleton<GravityGun>.Instance.PickedObject == go) return true;
            return Singleton<ToolGun>.IsInstanced() && Singleton<ToolGun>.Instance.PickedObject == go;
        }

        private static void FreezeBed(TransportBed bed)
        {
            _allowFreeze = true;
            try { bed.FreezeNow(); }
            catch (Exception ex) { VehicleTraffic.Warn("bed freeze", "Bed sync: freeze failed: " + ex.Message); }
            finally { _allowFreeze = false; }
        }

        private static void UnfreezeBed(TransportBed bed)
        {
            if (!bed.ObjectsFreezed && bed.GameObjects.Count == 0) return;
            _allowUnfreeze = true;
            try { bed.Unfreeze(); }
            catch (Exception ex) { VehicleTraffic.Warn("bed unfreeze", "Bed sync: unfreeze failed: " + ex.Message); }
            finally { _allowUnfreeze = false; }
        }

        private static string Describe(Entry e) => e.Key.Length > 0 ? e.Key : "(" + e.Sid + ", no key)";

        /// <summary>
        /// Finds cargo items by ObjectSync key (ObjectSync.Find).
        /// Unkeyed items: nearest object of the same SaveID within 2 m of where it should be.
        /// </summary>
        private class Lookup
        {
            private readonly HashSet<GameObject> _used = new HashSet<GameObject>();

            public GameObject Resolve(Entry e, Vector3 expected, TransportBed bed)
            {
                GameObject go = null;
                if (e.Key.Length > 0) go = ObjectSync.Find(e.Key);
                if (go == null && e.Sid.Length > 0) go = Nearest(e.Sid, expected, bed);
                if (go != null && !_used.Add(go)) go = null; // listed twice
                return go;
            }

            private GameObject Nearest(string sid, Vector3 expected, TransportBed bed)
            {
                GameObject best = null;
                float bestD = 2f * 2f;
                void Consider(SaveSystem.SaveMe sm)
                {
                    if (sm == null || _used.Contains(sm.gameObject) || ObjectSync.KeyOf(sm.gameObject) != null) return;
                    if (sm.GetSaveID() != sid) return;
                    float d = (sm.transform.position - expected).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = sm.gameObject; }
                }
                foreach (var sm in bed.ObjectsOnBedD) Consider(sm);
                foreach (var go in bed.GameObjects) if (go != null) Consider(go.GetComponent<SaveSystem.SaveMe>());
                if (best == null) foreach (var sm in AllSaved()) Consider(sm);
                return best;
            }

            private static List<SaveSystem.SaveMe> AllSaved() =>
                SaveSystem.Singleton<CheckPointManagerBase>.IsInstanced()
                    ? SaveSystem.Singleton<CheckPointManagerBase>.Instance.ToSave
                    : new List<SaveSystem.SaveMe>();
        }

        // =====================================================================================
        // Helpers
        // =====================================================================================

        /// <summary>The beds the game freezes for a driver (Pickup.EnterToVehicle): 0 = truck bed, 1 = trailer bed.</summary>
        private static IEnumerable<(TransportBed bed, int slot)> BedsOf(MachineController m)
        {
            if (!(m is Pickup p) || p == null) yield break;
            if (p.MyTransportBed != null) yield return (p.MyTransportBed, 0);
            if (p.ConnectedTrailer != null && p.ConnectedTrailer.MyTransformBed != null)
                yield return (p.ConnectedTrailer.MyTransformBed, 1);
        }

        private static TransportBed BedOf(MachineController m, int slot)
        {
            foreach (var (bed, s) in BedsOf(m)) if (s == slot) return bed;
            return null;
        }

        private static void Send(MemoryStream ms)
        {
            byte[] data = ms.ToArray();
            Net.Broadcast(data, reliable: true);
            VehicleTraffic.Tx(VehicleTraffic.Bed, data.Length);
        }

        private static MemoryStream Begin(byte sub, out BinaryWriter w)
        {
            var ms = Net.Writer(MsgId);
            ms.WriteByte(sub);
            ms.WriteByte(MachineSync.Epoch); // world version (see MachineSync)
            w = new BinaryWriter(ms);
            return ms;
        }

        private static Quaternion Norm(Quaternion q)
        {
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (!(mag > 0.5f) || float.IsInfinity(mag)) return Quaternion.identity;
            return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
        }

        // =====================================================================================
        // Harmony
        // =====================================================================================

        /// <summary>
        /// FreezeNow: on a held bed only our own call may freeze (exact driver set); a local passenger's
        /// EnterToVehicle would otherwise add its trigger set. On the driver's PC: send the snapshot.
        /// </summary>
        [HarmonyPatch]
        private static class PatchFreezeNow
        {
            private static MethodBase Target => AccessTools.Method(typeof(TransportBed), nameof(TransportBed.FreezeNow));

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Bed sync: TransportBed.FreezeNow not found, truck cargo is not synced");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static bool Prefix(TransportBed __instance, out bool __state)
            {
                __state = _allowFreeze || !HeldBeds.ContainsKey(__instance);
                return __state;
            }

            private static void Postfix(TransportBed __instance, bool __state)
            {
                if (!__state) return;
                if (__instance.ObjectsFreezed) FrozenBeds.Add(__instance);
                RebuildFrozenItems();
                if (_allowFreeze || !Net.Active) return;
                int slot = LocalDriverSlot(__instance, out int id);
                if (slot >= 0) SendFreeze(__instance, id, slot);
            }
        }

        /// <summary>Blocks Unfreeze (incl. the 10 s auto-unfreeze in FixedUpdate) for held beds.</summary>
        [HarmonyPatch]
        private static class PatchUnfreeze
        {
            private static MethodBase Target => AccessTools.Method(typeof(TransportBed), nameof(TransportBed.Unfreeze));

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Bed sync: TransportBed.Unfreeze not found, cargo may drop off remote trucks");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static bool Prefix(TransportBed __instance)
            {
                // Unfreeze logs every item by name: one destroyed item (sold / consumed on another PC while frozen)
                // threw there, left the rest of the cargo inactive for good, and the game's 10 s auto-unfreeze
                // threw again every physics step. Drop destroyed items first (for the game's calls too).
                if (__instance.GameObjects.Count > 0) __instance.GameObjects.RemoveAll(IsDestroyed);
                if (_allowUnfreeze || !HeldBeds.ContainsKey(__instance)) return true;
                if (!Net.Active)
                {
                    // Session ended while the truck was remote-driven: stop holding it.
                    HeldBeds.Remove(__instance);
                    return true;
                }
                return false;
            }

            private static void Postfix(TransportBed __instance)
            {
                if (!__instance.ObjectsFreezed) FrozenBeds.Remove(__instance);
                RebuildFrozenItems();
            }
        }

        /// <summary>
        /// Driver gets out (Pickup.ExitMachine unfreezes both beds): send where every item ends up.
        /// The prefix remembers the frozen items (Unfreeze clears the list), the postfix sends their poses.
        /// </summary>
        [HarmonyPatch]
        private static class PatchExitMachine
        {
            private static MethodBase Target => AccessTools.DeclaredMethod(typeof(Pickup), nameof(Pickup.ExitMachine));

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Bed sync: Pickup.ExitMachine not found, cargo may land differently after a drive");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static void Prefix(Pickup __instance, out List<(TransportBed bed, int slot, int id, List<GameObject> items)> __state)
            {
                __state = null;
                if (!Net.Active) return;
                foreach (var (bed, _) in BedsOf(__instance))
                {
                    if (!bed.ObjectsFreezed) continue;
                    int slot = LocalDriverSlot(bed, out int id, __instance);
                    if (slot < 0) continue;
                    (__state ??= new List<(TransportBed, int, int, List<GameObject>)>())
                        .Add((bed, slot, id, new List<GameObject>(bed.GameObjects)));
                }
            }

            private static void Postfix(List<(TransportBed bed, int slot, int id, List<GameObject> items)> __state)
            {
                if (__state == null) return;
                foreach (var s in __state)
                {
                    if (s.bed == null) continue;
                    try { SendUnfreeze(s.bed, s.id, s.slot, s.items); }
                    catch (Exception ex) { Plugin.Log.LogWarning("Bed sync: unfreeze snapshot failed: " + ex.Message); }
                }
            }
        }
    }
}
