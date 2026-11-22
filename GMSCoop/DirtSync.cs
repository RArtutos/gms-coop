using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Dirt lumps (VirtualVolumeDirt) that a bucket, blade or truck bed drops: (Msg)14 sub 8.
    ///
    /// A lump carries real material (DirtVolume + Minerals) only on the PC that dropped it, i.e. the
    /// PC that owns the machine (ContainerSync: the driver). Where it lands (truck bed, hopper, wash
    /// plant, terrain) that PC adds the material to the container (ContainerSync forwards it to the
    /// container's owner) or to the terrain (TerrainSync heights + voxels).
    ///
    /// The other PCs get a visual twin: same prefab, position, velocity and size, but DirtVolume = 0
    /// and no minerals, so whatever it falls into adds nothing (no double material, no double gold).
    /// Unreliable, batched every 0.1 s, max 48 lumps per packet (20 bytes each, 973 bytes at most).
    /// A lost packet only loses a few visual twins. Duplicated packets are dropped (sender id + 16-bit
    /// sequence, SyncCore.SeqWindow); reordered ones are still spawned once (a twin is purely visual).
    /// Packet: origin u64 (sender LocalId), seq u16, count u8, then per lump: position 3 x f32,
    /// velocity 3 x f16, volume f16.
    /// </summary>
    internal static class DirtSync
    {
        internal const byte SubLumps = 8;
        private const float SendInterval = 0.1f;
        private const int MaxPerPacket = 48;
        private const int MaxQueued = 256;

        private struct Lump
        {
            public Vector3 Pos, Vel;
            public float Volume;
        }

        private static readonly List<Lump> Queue = new List<Lump>();
        private static readonly Dictionary<int, float> TwinVolume = new Dictionary<int, float>();
        private static readonly Dictionary<ulong, SeqWindow> Seen = new Dictionary<ulong, SeqWindow>();
        private static ushort _seq;
        private static GameObject _template;
        private static float _nextSend, _nextTemplateSearch;

        private static bool _ready;
        // Every clod passes the Start hook: errors are rate-limited (log size).
        private static readonly SubsystemGuard GDirt = new SubsystemGuard("Dirt sync");

        /// <summary>
        /// The world is loading / not shared yet (called by GoldSync every frame meanwhile): clods queued
        /// for sending and twins waiting for their Start belong to the world that is going away.
        /// </summary>
        internal static void NotSynced()
        {
            _ready = false;
            if (Queue.Count > 0) Queue.Clear();
            if (TwinVolume.Count > 0) TwinVolume.Clear();
        }

        /// <summary>
        /// Client, once the host's world is ready: clods restored from the save are the host's clods
        /// (the host keeps the real ones); here they become visual only, or their material would count twice.
        /// </summary>
        private static void NeutralizeSavedClods()
        {
            int n = 0;
            // Inactive ones too: clods saved in an area not loaded here sit under its inactive
            // AreaStreamer.GameplayObjects and would bring their material back when this PC enters the area.
            foreach (VirtualVolumeDirt v in UnityEngine.Object.FindObjectsOfType<VirtualVolumeDirt>(true))
            {
                if (v == null || (v.DirtVolume <= 0f && (v.Minerals == null || v.Minerals.IsEmpty))) continue;
                // Not started yet (inactive): Start would size it from DirtVolume = 0; the Start postfix
                // gives it its real size instead, like a twin.
                if (!v.gameObject.activeInHierarchy && TwinVolume.Count < 1024) TwinVolume[v.GetInstanceID()] = v.DirtVolume;
                v.DirtVolume = 0f;
                v.Minerals = new ResourceBag();
                v.SmallVolume = -1f;
                n++;
            }
            if (n > 0) Plugin.Log.LogInfo($"Dirt sync: {n} saved dirt clods are visual only on this PC (the host has them)");
        }

        internal static void Tick()
        {
            if (!_ready)
            {
                _ready = true;
                if (!Net.IsHost)
                {
                    try { NeutralizeSavedClods(); }
                    catch (Exception ex) { GDirt.Fail(ex); }
                }
            }
            if (Queue.Count == 0 || Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + SendInterval;
            if (!GoldSync.Synced()) { Queue.Clear(); return; }
            int i = 0;
            while (i < Queue.Count)
            {
                int n = Mathf.Min(MaxPerPacket, Queue.Count - i);
                var ms = GoldSync.Begin(SubLumps, out BinaryWriter w);
                w.Write(Net.LocalId);
                w.Write(++_seq);
                w.Write((byte)n);
                for (int k = 0; k < n; k++)
                {
                    Lump l = Queue[i + k];
                    w.Write(l.Pos.x); w.Write(l.Pos.y); w.Write(l.Pos.z);
                    w.Write(Mathf.FloatToHalf(Mathf.Clamp(l.Vel.x, -60000f, 60000f)));
                    w.Write(Mathf.FloatToHalf(Mathf.Clamp(l.Vel.y, -60000f, 60000f)));
                    w.Write(Mathf.FloatToHalf(Mathf.Clamp(l.Vel.z, -60000f, 60000f)));
                    w.Write(Mathf.FloatToHalf(Mathf.Clamp(l.Volume, 0f, 60000f)));
                }
                TrafficC.Broadcast(TrafficC.Cat.Dirt, ms.ToArray(), false);
                i += n;
            }
            Queue.Clear();
        }

        internal static void Reset()
        {
            Queue.Clear();
            TwinVolume.Clear();
            Seen.Clear();
            _ready = false;
        }

        internal static void Receive(BinaryReader r)
        {
            ulong origin = r.ReadUInt64();
            ushort seq = r.ReadUInt16();
            int n = r.ReadByte();
            var lumps = new Lump[n];
            for (int k = 0; k < n; k++)
            {
                lumps[k].Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                lumps[k].Vel = new Vector3(Mathf.HalfToFloat(r.ReadUInt16()), Mathf.HalfToFloat(r.ReadUInt16()), Mathf.HalfToFloat(r.ReadUInt16()));
                lumps[k].Volume = Mathf.HalfToFloat(r.ReadUInt16());
            }
            if (!GoldSync.Synced() || origin == Net.LocalId) return;
            if (!Seen.TryGetValue(origin, out SeqWindow win)) Seen[origin] = win = new SeqWindow();
            if (!win.Accept(seq)) return; // duplicated packet: its twins exist already
            GameObject template = Template();
            if (template == null) return;
            foreach (Lump l in lumps)
            {
                if (float.IsNaN(l.Pos.x) || float.IsInfinity(l.Pos.x) || float.IsNaN(l.Pos.y) || float.IsNaN(l.Pos.z)) continue;
                try { SpawnTwin(template, l); }
                catch (Exception ex) { GDirt.Fail(ex); return; }
            }
        }

        private static readonly AccessTools.FieldRef<MachineController, List<Controller>> ControllersRef =
            GoldSync.TryRef<MachineController, List<Controller>>("Controllers");

        private static GameObject Template()
        {
            if (_template != null) return _template;
            if (Time.unscaledTime < _nextTemplateSearch) return null;
            _nextTemplateSearch = Time.unscaledTime + 5f;
            foreach (MachineController m in UnityEngine.Object.FindObjectsOfType<MachineController>())
            {
                List<Controller> list = ControllersRef != null ? ControllersRef(m) : null;
                if (list == null) continue;
                foreach (Controller c in list)
                    if (c is DiggingController dc && dc.volumeDirt != null) return _template = dc.volumeDirt;
            }
            foreach (GoldDigger.Shovel s in UnityEngine.Object.FindObjectsOfType<GoldDigger.Shovel>())
                if (s.volumeDirtPrefab != null) return _template = s.volumeDirtPrefab;
            return null;
        }

        private static void SpawnTwin(GameObject template, Lump l)
        {
            GameObject go = UnityEngine.Object.Instantiate(template, l.Pos, Quaternion.identity);
            var v = go.GetComponent<VirtualVolumeDirt>();
            if (v == null) { UnityEngine.Object.Destroy(go); return; }
            // No material: landing anywhere adds nothing (the dropping PC adds the real amount).
            v.DirtVolume = 0f;
            v.Minerals = new ResourceBag();
            v.SmallVolume = -1f; // the game lets tiny clods fall through wash plants; a twin keeps its real look
            if (TwinVolume.Count > 1024) TwinVolume.Clear(); // twins destroyed before their Start never remove themselves
            TwinVolume[v.GetInstanceID()] = l.Volume; // read (and removed) by the Start postfix
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) rb.velocity = l.Vel;
        }

        [HarmonyPatch]
        private static class PatchLumpStart
        {
            private static MethodBase TargetMethod() =>
                GoldSync.Need(typeof(VirtualVolumeDirt), "Start", "dirt clod sync", Type.EmptyTypes);

            private static bool Prepare() => TargetMethod() != null;

            private static void Postfix(VirtualVolumeDirt __instance)
            {
                try
                {
                    int id = __instance.GetInstanceID();
                    if (TwinVolume.TryGetValue(id, out float vol))
                    {
                        // Start sized it from DirtVolume = 0: give it the real size and mass.
                        TwinVolume.Remove(id);
                        float s = Mathf.Pow(3f * vol / ((float)Math.PI * 4f), 0.3333333f);
                        __instance.transform.localScale = new Vector3(s, s, s);
                        var trb = __instance.GetComponent<Rigidbody>();
                        if (trb != null) trb.mass = Mathf.Max(0.01f, 10f * vol);
                        return;
                    }
                    if (!GoldSync.Synced() || __instance.DirtVolume <= 0f) return;
                    if (Queue.Count >= MaxQueued) return;
                    var rb = __instance.GetComponent<Rigidbody>();
                    Queue.Add(new Lump
                    {
                        Pos = __instance.transform.position,
                        Vel = rb != null ? rb.velocity : Vector3.zero,
                        Volume = __instance.DirtVolume,
                    });
                }
                catch (Exception ex) { GDirt.Fail(ex); }
            }
        }
    }
}
