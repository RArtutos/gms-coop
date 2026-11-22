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
    /// Water tanks ((Msg)14 sub 40 / 41): CurrentCapacity of water towers (WaterTowerController) and water
    /// cisterns (WaterCistern), the water the wash plants and pumps take. Before this, each PC simulated
    /// its own level: water a client bought filled only its own tower, and the host (which runs the world
    /// wash plants) ran dry while the client still saw a full tank.
    ///  - HOST: its levels are the truth, sent as absolute values (1 Hz while they change, all every 10 s).
    ///  - CLIENT: follows the host's values. Water it BUYS (WaterStationGUI.Submit) is one operation the host
    ///    adds once (the money is a ledger delta, GoldSync). The tank keeps the client's own value until the
    ///    host acknowledged the operation (same hold as the fuel tanks, FuelSync).
    /// Identity: the tank's saved root (RootKey) plus its path below the root.
    /// </summary>
    internal static partial class WorldSync
    {
        internal const byte SubWater = 40;
        internal const byte SubWaterOp = 41;

        private sealed class WaterTank
        {
            public string Key;
            public WaterTowerController Tower;
            public WaterCistern Cistern;
            public float SentCap = float.NaN;
            public uint PendSeq;
            public float PendAt;

            public bool Alive => Tower != null || Cistern != null;
            public float Cap
            {
                get => Tower != null ? Tower.CurrentCapacity : Cistern != null ? Cistern.CurrentCapacity : 0f;
                set
                {
                    if (Tower != null) Tower.CurrentCapacity = Mathf.Clamp(value, 0f, Tower.MaxCapacity);
                    else if (Cistern != null) Cistern.CurrentCapacity = Mathf.Clamp(value, 0f, Cistern.MaxCapacity);
                }
            }
        }

        private static readonly List<WaterTank> WaterTanks = new List<WaterTank>();
        private static readonly Dictionary<string, WaterTank> WaterByKey = new Dictionary<string, WaterTank>();
        private static readonly Dictionary<Component, WaterTank> WaterOf = new Dictionary<Component, WaterTank>();
        private static readonly Dictionary<ulong, FuelAck> WaterAcks = new Dictionary<ulong, FuelAck>(); // host
        private static readonly List<WaterTank> WaterChanged = new List<WaterTank>();
        private static float _nextWaterIndex, _nextWaterSend, _nextWaterFull, _nextForcedWaterIndex;
        private static bool _waterSendAll, _waterAcksDirty;
        private static uint _waterOpSeq;

        private static void ResetWater()
        {
            WaterTanks.Clear();
            WaterByKey.Clear();
            WaterOf.Clear();
            _nextWaterIndex = 0f;
        }

        private static void IndexWater(float now)
        {
            _nextWaterIndex = now + 10f;
            var old = new Dictionary<Component, WaterTank>(WaterOf);
            WaterTanks.Clear();
            WaterByKey.Clear();
            WaterOf.Clear();
            foreach (WaterTowerController t in Object.FindObjectsOfType<WaterTowerController>(true)) AddWaterTank(t, t, null, old);
            foreach (WaterCistern c in Object.FindObjectsOfType<WaterCistern>(true)) AddWaterTank(c, null, c, old);
        }

        private static void AddWaterTank(Component c, WaterTowerController tower, WaterCistern cistern, Dictionary<Component, WaterTank> old)
        {
            SaveMe root = c.GetComponentInParent<SaveMe>(true);
            string rk = RootKey(root, true);
            if (rk == null) return;
            string key = rk + "|" + PathBelow(root.transform, c.transform) + "|" + (tower != null ? "t" : "c");
            if (WaterByKey.ContainsKey(key)) return;
            var w = new WaterTank { Key = key, Tower = tower, Cistern = cistern };
            if (old.TryGetValue(c, out WaterTank prev) && prev.Key == key)
            {
                w.SentCap = prev.SentCap;
                w.PendSeq = prev.PendSeq;
                w.PendAt = prev.PendAt;
            }
            WaterTanks.Add(w);
            WaterByKey[key] = w;
            WaterOf[c] = w;
        }

        private static void WaterTick(float now)
        {
            if (now >= _nextWaterIndex) IndexWater(now);
            if (!Net.IsHost || now < _nextWaterSend) return;
            _nextWaterSend = now + 1f;
            if (now >= _nextWaterFull) { _nextWaterFull = now + 10f; _waterSendAll = true; }
            WaterChanged.Clear();
            foreach (WaterTank t in WaterTanks)
            {
                if (!t.Alive) continue;
                float cap = t.Cap;
                if (!_waterSendAll && Mathf.Abs(cap - t.SentCap) < 1f) continue;
                t.SentCap = cap;
                WaterChanged.Add(t);
            }
            if (WaterChanged.Count == 0 && !_waterAcksDirty) { _waterSendAll = false; return; }
            _waterSendAll = false;
            _waterAcksDirty = false;
            if (WaterAcks.Count > 0)
            {
                List<ulong> stale = null;
                foreach (var kv in WaterAcks) if (now - kv.Value.At > 60f) (stale ??= new List<ulong>()).Add(kv.Key);
                if (stale != null) foreach (ulong k in stale) WaterAcks.Remove(k);
            }
            int start = 0;
            do
            {
                int n = Mathf.Min(80, WaterChanged.Count - start);
                var ms = GoldSync.Begin(SubWater, out BinaryWriter w);
                int na = Mathf.Min(WaterAcks.Count, 255), ka = 0;
                w.Write((byte)na);
                foreach (var kv in WaterAcks)
                {
                    if (ka++ == na) break;
                    w.Write(kv.Key);
                    w.Write(kv.Value.Seq);
                }
                w.Write((ushort)n);
                for (int i = 0; i < n; i++)
                {
                    WaterTank t = WaterChanged[start + i];
                    w.Write(t.Key);
                    w.Write(t.Cap);
                }
                TrafficC.Broadcast(TrafficC.Cat.World, ms.ToArray(), true);
                start += n;
            } while (start < WaterChanged.Count);
        }

        private static void ReceiveWater(BinaryReader r)
        {
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
            if (apply)
                foreach (WaterTank pt in WaterTanks)
                    if (pt.PendSeq != 0 && ((hasAck && Predict.Acked(pt.PendSeq, ack)) || GoldSync.Host.Talked(pt.PendAt) > FuelHoldMax))
                        pt.PendSeq = 0;
            for (int i = 0; i < n; i++)
            {
                string key = r.ReadString();
                float cap = r.ReadSingle();
                if (!apply || float.IsNaN(cap) || float.IsInfinity(cap)) continue;
                if (!WaterByKey.TryGetValue(key, out WaterTank t) || !t.Alive)
                {
                    float now = Time.unscaledTime;
                    if (now >= _nextForcedWaterIndex) { _nextForcedWaterIndex = now + 5f; _nextWaterIndex = 0f; }
                    continue;
                }
                if (t.PendSeq != 0) continue; // our purchase is not in the host's value yet
                t.Cap = cap;
            }
        }

        private struct WaterBuy
        {
            public WaterTank T;
            public float Cap;
        }

        private static void WaterBefore(WaterStationGUI gui, out WaterBuy st)
        {
            st = default;
            if (gui == null || Net.IsHost || !Synced()) return;
            Component c = gui.WaterControllerCistern != null ? (Component)gui.WaterControllerCistern : gui.WaterController;
            if (c == null) return;
            if (!WaterOf.TryGetValue(c, out WaterTank t))
            {
                IndexWater(Time.unscaledTime);
                if (!WaterOf.TryGetValue(c, out t)) return;
            }
            st.T = t;
            st.Cap = t.Cap;
        }

        /// <summary>Client: water bought into a tower / cistern becomes one operation the host adds.</summary>
        private static void WaterAfter(WaterBuy st)
        {
            if (st.T == null || !st.T.Alive || Net.IsHost || !Synced()) return;
            float d = st.T.Cap - st.Cap;
            if (Mathf.Abs(d) < 0.01f) return;
            if (++_waterOpSeq == 0) _waterOpSeq = 1;
            st.T.PendSeq = _waterOpSeq;
            st.T.PendAt = Time.unscaledTime;
            var ms = GoldSync.Begin(SubWaterOp, out BinaryWriter w);
            w.Write(GoldSync.StreamId);
            w.Write(_waterOpSeq);
            w.Write(st.T.Key);
            w.Write(d);
            TrafficC.Broadcast(TrafficC.Cat.World, ms.ToArray(), true);
        }

        private static void ReceiveWaterOp(BinaryReader r)
        {
            ulong origin = r.ReadUInt64();
            uint opSeq = r.ReadUInt32();
            string key = r.ReadString();
            float d = r.ReadSingle();
            if (!Net.IsHost) return;
            WaterAcks[origin] = new FuelAck { Seq = opSeq, At = Time.unscaledTime };
            _waterAcksDirty = true;
            _nextWaterSend = 0f;
            if (!Synced()) return;
            if (!WaterByKey.TryGetValue(key, out WaterTank t) || !t.Alive)
            {
                IndexWater(Time.unscaledTime);
                if (!WaterByKey.TryGetValue(key, out t) || !t.Alive) return;
            }
            if (!float.IsNaN(d) && !float.IsInfinity(d)) t.Cap = t.Cap + d;
            Plugin.Log.LogInfo("World sync: water bought by another player applied");
        }

        [HarmonyPatch]
        private static class PatchWaterBuy
        {
            private static MethodBase TargetMethod() => GoldSync.Need(typeof(WaterStationGUI), nameof(WaterStationGUI.Submit), "water bought into towers / cisterns");
            private static bool Prepare() => TargetMethod() != null;
            private static void Prefix(WaterStationGUI __instance, out WaterBuy __state) => WaterBefore(__instance, out __state);
            private static void Postfix(WaterBuy __state) => WaterAfter(__state);
        }
    }
}
