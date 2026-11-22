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
    /// Fuel tanks ((Msg)14 sub 23 / 24): FuelStationController.CurrentCapacity and the fuel ordered for
    /// delivery, of fuel station tanks, fuel / light trailers, generator / pump tanks and jerrycans.
    /// Machine tanks (FuelController) belong to the vehicle sync (driver authority; refuels on foot of a
    /// parked machine are detected there).
    ///  - HOST: its tanks are the truth. Every change it simulates (consumption of generators, pumps,
    ///    wash plants and belts, pistol refuels, jerrycan fills, deliveries arriving, leaks) goes out as
    ///    absolute values (2 Hz while it changes, everything once a player loaded the world), together
    ///    with RandomEvents.hasFuel (the "fuel station has no fuel" event is rolled by the host only).
    ///  - CLIENT: follows the host's values; its own simulation of the same consumption / transfers is
    ///    overwritten, so nothing counts twice. Fuel it BUYS (FuelStationGUI.Submit into a tank,
    ///    FuelStationStationaryGUI.Submit ordering a delivery) is one operation the host applies once;
    ///    the money is a ledger delta (GoldSync). The tank shows the purchase at once; host values for
    ///    that tank are not applied until the host acknowledges the operation (stream id + sequence,
    ///    acks travel with the tank values), so the gauge never drops back for a round trip, whatever the
    ///    latency. Safety net: the hold ends once the host kept talking for 10 s without an ack (a link
    ///    stall never ends it).
    /// Identity: the tank's saved root (ObjectSync key, machine / trailer id, or save id + position)
    /// plus its path below the root.
    /// </summary>
    internal static partial class WorldSync
    {
        internal const byte SubFuel = 23;
        internal const byte SubFuelOp = 24;

        private sealed class Tank
        {
            public string Key;
            public FuelStationController F;
            public float SentCap = float.NaN;
            public int SentDeliv = -1;
            public uint PendSeq;  // client: own operation the host has not acknowledged (0 = none)
            public float PendAt;
        }

        private struct FuelAck
        {
            public uint Seq;
            public float At;
        }

        private const float FuelHoldMax = 10f;
        private static readonly Dictionary<ulong, FuelAck> FuelAcks = new Dictionary<ulong, FuelAck>(); // host
        private static uint _fuelOpSeq;
        private static bool _fuelAcksDirty;

        private static readonly List<Tank> Tanks = new List<Tank>();
        private static readonly Dictionary<string, Tank> TankByKey = new Dictionary<string, Tank>();
        private static readonly Dictionary<FuelStationController, Tank> TankOf = new Dictionary<FuelStationController, Tank>();
        private static readonly List<Tank> FuelChanged = new List<Tank>();
        private static readonly Dictionary<FuelStationController, Tank> OldTanks = new Dictionary<FuelStationController, Tank>();
        private static float _nextTankIndex, _nextFuelSend, _nextFuelFull, _nextForcedTankIndex;
        private static bool _fuelSendAll, _sentHasFuel = true, _hasFuelSent;

        /// <summary>
        /// The tank index (world objects) is rebuilt. The host's acknowledgements of the players' fuel
        /// purchases are session state, kept (an operation acknowledged while the host was loading must
        /// still reach its buyer): they are cleared by WorldSync.ResetSession only.
        /// </summary>
        private static void ResetFuel()
        {
            Tanks.Clear();
            TankByKey.Clear();
            TankOf.Clear();
            _nextTankIndex = 0f;
            _hasFuelSent = false;
        }

        private static void IndexTanks(float now)
        {
            _nextTankIndex = now + 10f;
            // Keep what the old index knew per tank (last sent values, an unacknowledged purchase).
            OldTanks.Clear();
            foreach (var kv in TankOf) if (kv.Key != null) OldTanks[kv.Key] = kv.Value;
            Tanks.Clear();
            TankByKey.Clear();
            TankOf.Clear();
            // Inactive tanks too (saved objects of an area not loaded on this PC): a purchase a player
            // makes there must still reach the host's tank (it was paid for) and the host's save.
            foreach (FuelStationController f in Object.FindObjectsOfType<FuelStationController>(true))
            {
                SaveMe root = f.GetComponentInParent<SaveMe>(true);
                string rk = RootKey(root, true);
                if (rk == null) continue;
                string key = rk + "|" + PathBelow(root.transform, f.transform);
                if (TankByKey.ContainsKey(key)) continue;
                var t = new Tank { Key = key, F = f };
                if (OldTanks.TryGetValue(f, out Tank old) && old.Key == key)
                {
                    t.SentCap = old.SentCap;
                    t.SentDeliv = old.SentDeliv;
                    t.PendSeq = old.PendSeq;
                    t.PendAt = old.PendAt;
                }
                Tanks.Add(t);
                TankByKey[key] = t;
                TankOf[f] = t;
            }
            OldTanks.Clear();
        }

        private static int DelivHash(List<FuelStationController.IncomingFueling> list)
        {
            int h = 17;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) h = (h * 31 + list[i].Capacity.GetHashCode()) * 31 + list[i].DeliveryTime.GetHashCode();
            return h;
        }

        private static void FuelTick(float now)
        {
            if (now >= _nextTankIndex) IndexTanks(now);
            if (!Net.IsHost || now < _nextFuelSend) return;
            _nextFuelSend = now + 0.5f;
            // Everything every 10 s: a player whose tank index was not built yet when a change came gets it.
            if (now >= _nextFuelFull) { _nextFuelFull = now + 10f; _fuelSendAll = true; }
            FuelChanged.Clear();
            for (int i = 0; i < Tanks.Count; i++)
            {
                Tank t = Tanks[i];
                if (t.F == null) continue;
                int dh = DelivHash(t.F.IncomingFuelDelivery);
                if (!_fuelSendAll && Mathf.Abs(t.F.CurrentCapacity - t.SentCap) < 0.05f && dh == t.SentDeliv) continue;
                t.SentCap = t.F.CurrentCapacity;
                t.SentDeliv = dh;
                FuelChanged.Add(t);
            }
            bool hasFuel = RandomEvents.instance == null || RandomEvents.instance.hasFuel;
            if (FuelChanged.Count == 0 && _hasFuelSent && hasFuel == _sentHasFuel && !_fuelSendAll && !_fuelAcksDirty) return;
            _fuelSendAll = false;
            _fuelAcksDirty = false;
            if (FuelAcks.Count > 0)
            {
                List<ulong> old = null;
                foreach (var kv in FuelAcks) if (now - kv.Value.At > 60f) (old ??= new List<ulong>()).Add(kv.Key);
                if (old != null) foreach (ulong k in old) FuelAcks.Remove(k);
            }
            _sentHasFuel = hasFuel;
            _hasFuelSent = true;
            int start = 0;
            do
            {
                int n = Mathf.Min(60, FuelChanged.Count - start);
                var ms = GoldSync.Begin(SubFuel, out BinaryWriter w);
                w.Write(hasFuel);
                int na = Mathf.Min(FuelAcks.Count, 255), ka = 0;
                w.Write((byte)na);
                foreach (var kv in FuelAcks)
                {
                    if (ka++ == na) break;
                    w.Write(kv.Key);
                    w.Write(kv.Value.Seq);
                }
                w.Write((ushort)n);
                for (int i = 0; i < n; i++)
                {
                    Tank t = FuelChanged[start + i];
                    w.Write(t.Key);
                    w.Write(t.F.CurrentCapacity);
                    int nd = Mathf.Min(t.F.IncomingFuelDelivery.Count, 255);
                    w.Write((byte)nd);
                    for (int k = 0; k < nd; k++)
                    {
                        w.Write(t.F.IncomingFuelDelivery[k].Capacity);
                        w.Write(t.F.IncomingFuelDelivery[k].DeliveryTime);
                    }
                }
                TrafficC.Broadcast(TrafficC.Cat.World, ms.ToArray(), true);
                start += n;
            } while (start < FuelChanged.Count);
        }

        private static void ReceiveFuel(BinaryReader r)
        {
            bool hasFuel = r.ReadBoolean();
            int na = r.ReadByte();
            bool hasAck = false;
            uint ack = 0;
            for (int k = 0; k < na; k++)
            {
                ulong id = r.ReadUInt64();
                uint sq = r.ReadUInt32();
                if (id == GoldSync.StreamId) { hasAck = true; ack = sq; }
            }
            int n = r.ReadUInt16();
            bool apply = !Net.IsHost && Synced();
            if (apply && RandomEvents.instance != null) RandomEvents.instance.hasFuel = hasFuel;
            if (apply)
                foreach (Tank pt in Tanks)
                    if (pt.PendSeq != 0 && ((hasAck && Predict.Acked(pt.PendSeq, ack)) || GoldSync.Host.Talked(pt.PendAt) > FuelHoldMax))
                        pt.PendSeq = 0;
            for (int i = 0; i < n; i++)
            {
                string key = r.ReadString();
                float cap = r.ReadSingle();
                int nd = r.ReadByte();
                var deliv = new List<FuelStationController.IncomingFueling>(nd);
                for (int k = 0; k < nd; k++) deliv.Add(new FuelStationController.IncomingFueling(r.ReadSingle(), r.ReadSingle()));
                if (!apply) continue;
                if (!TankByKey.TryGetValue(key, out Tank t) || t.F == null)
                {
                    // Not indexed here (new, or in an area not loaded on this PC: then it stays unknown).
                    // Re-index soon, but at most every 5 s: a tank of an unloaded area that keeps changing on
                    // the host must not cost a full scene scan twice a second for the whole session.
                    float now = Time.unscaledTime;
                    if (now >= _nextForcedTankIndex) { _nextForcedTankIndex = now + 5f; _nextTankIndex = 0f; }
                    continue;
                }
                if (t.PendSeq != 0 || float.IsNaN(cap) || float.IsInfinity(cap)) continue; // our purchase is not in it yet
                t.F.CurrentCapacity = Mathf.Clamp(cap, 0f, t.F.MaxCapacity);
                if (DelivHash(t.F.IncomingFuelDelivery) != DelivHash(deliv))
                {
                    t.F.IncomingFuelDelivery.Clear();
                    t.F.IncomingFuelDelivery.AddRange(deliv);
                }
            }
        }

        private struct FuelBuy
        {
            public FuelStationController F;
            public float Cap;
            public int Deliv;
        }

        private static void FuelBefore(FuelStationController f, out FuelBuy st)
        {
            st = default;
            if (f == null || Net.IsHost || !Synced()) return;
            st.F = f;
            st.Cap = f.CurrentCapacity;
            st.Deliv = f.IncomingFuelDelivery.Count;
        }

        /// <summary>Client: fuel bought into a tank / a delivery ordered becomes one operation for the host.</summary>
        private static void FuelAfter(FuelBuy st)
        {
            if (st.F == null || Net.IsHost || !Synced()) return;
            float d = st.F.CurrentCapacity - st.Cap;
            int added = st.F.IncomingFuelDelivery.Count - st.Deliv;
            if (Mathf.Abs(d) < 0.001f && added <= 0) return;
            if (!TankOf.TryGetValue(st.F, out Tank t))
            {
                IndexTanks(Time.unscaledTime);
                if (!TankOf.TryGetValue(st.F, out t)) return;
            }
            if (++_fuelOpSeq == 0) _fuelOpSeq = 1;
            t.PendSeq = _fuelOpSeq;
            t.PendAt = Time.unscaledTime;
            var ms = GoldSync.Begin(SubFuelOp, out BinaryWriter w);
            w.Write(GoldSync.StreamId);
            w.Write(_fuelOpSeq);
            w.Write(t.Key);
            w.Write(d);
            int na = Mathf.Clamp(added, 0, 16);
            w.Write((byte)na);
            for (int k = st.F.IncomingFuelDelivery.Count - na; k < st.F.IncomingFuelDelivery.Count; k++)
            {
                w.Write(st.F.IncomingFuelDelivery[k].Capacity);
                w.Write(st.F.IncomingFuelDelivery[k].DeliveryTime);
            }
            TrafficC.Broadcast(TrafficC.Cat.World, ms.ToArray(), true);
        }

        private static void ReceiveFuelOp(BinaryReader r)
        {
            ulong origin = r.ReadUInt64();
            uint opSeq = r.ReadUInt32();
            string key = r.ReadString();
            float d = r.ReadSingle();
            int na = r.ReadByte();
            var add = new List<FuelStationController.IncomingFueling>(na);
            for (int k = 0; k < na; k++) add.Add(new FuelStationController.IncomingFueling(r.ReadSingle(), r.ReadSingle()));
            if (!Net.IsHost) return;
            // Acknowledge in any case (also unknown tank / loading): the buyer stops holding its own value.
            FuelAcks[origin] = new FuelAck { Seq = opSeq, At = Time.unscaledTime };
            _fuelAcksDirty = true;
            _nextFuelSend = 0f;
            if (!Synced()) return;
            if (!TankByKey.TryGetValue(key, out Tank t) || t.F == null)
            {
                IndexTanks(Time.unscaledTime);
                if (!TankByKey.TryGetValue(key, out t) || t.F == null) return;
            }
            if (!float.IsNaN(d) && !float.IsInfinity(d)) t.F.CurrentCapacity = Mathf.Clamp(t.F.CurrentCapacity + d, 0f, t.F.MaxCapacity);
            t.F.IncomingFuelDelivery.AddRange(add);
            _nextFuelSend = 0f; // answer at once
            Plugin.Log.LogInfo("World sync: fuel bought by another player applied");
        }

        [HarmonyPatch]
        private static class PatchFuelBuy
        {
            private static MethodBase TargetMethod() => GoldSync.Need(typeof(FuelStationGUI), nameof(FuelStationGUI.Submit), "fuel bought into tanks");
            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(FuelStationGUI __instance, out FuelBuy __state)
            {
                __state = default;
                if (__instance != null && __instance.MyFuelConsumer != null) FuelBefore(__instance.MyFuelConsumer.MyFuelStation, out __state);
            }

            private static void Postfix(FuelBuy __state) => FuelAfter(__state);
        }

        [HarmonyPatch]
        private static class PatchFuelOrder
        {
            private static MethodBase TargetMethod() => GoldSync.Need(typeof(FuelStationStationaryGUI), nameof(FuelStationStationaryGUI.Submit), "fuel deliveries ordered");
            private static bool Prepare() => TargetMethod() != null;
            private static void Prefix(FuelStationStationaryGUI __instance, out FuelBuy __state) => FuelBefore(__instance != null ? __instance.MyFuelStation : null, out __state);
            private static void Postfix(FuelBuy __state) => FuelAfter(__state);
        }
    }
}
