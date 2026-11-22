using System;
using System.Collections.Generic;
using System.IO;
using FMOD.Studio;
using FMODUnity;
using HarmonyLib;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Other players' sounds, heard from where they are. The game plays every tool, footstep and pan
    /// sound through FMOD StudioEventEmitters on the local player's own objects (tools under the camera,
    /// the walker's footstep emitter), so the other PCs never heard them. When an emitter that belongs to
    /// the local player plays, its event path, parameters and offset from the player's feet are sent
    /// (Social msg 18). The receivers play the same FMOD event on that player's avatar: a 3D event gets
    /// FMOD's own distance attenuation and direction (min/max distance clamped for player sounds); a 2D
    /// event gets a volume that falls with distance. The game's buses and volume options apply, because
    /// they are the game's own events.
    ///
    /// One-shot events play once. Looping events (gold pan movement and so on) are started, kept alive
    /// every 2 s while they play, follow the avatar, and stop on the sender's Stop or when the keep-alive
    /// or the avatar goes away. Machines are not handled here: their engine sound already runs on every
    /// PC from the synced RPM/load (MachinePartsSync patches GetEngineRPM01/GetEngineLoad).
    /// </summary>
    internal static class RemoteSounds
    {
        internal const byte SubPlay = 3, SubStop = 4, SubParam = 5;

        private const float MaxOffset = 3f;        // tool sounds sit near the player
        private const float ToolMin = 1.5f, ToolMax = 50f;
        private const float StepMax = 22f, DetectorMax = 18f;
        private const float LoopTimeout = 6f;

        // ---------------- sending ----------------

        private static float _windowStart;
        private static int _windowCount;
        private static float _nextStep;
        private static readonly Dictionary<int, float> LoopAlive = new Dictionary<int, float>(); // emitter id -> next keep-alive

        private static bool CanSend =>
            Net.Active && Net.WorldLive && !GameState.IsLoading && Net.Peers.Count > 0;

        /// <summary>The emitter belongs to the local player: walker (footsteps), camera (held tools) or Player.</summary>
        private static bool IsLocalPlayers(StudioEventEmitter em)
        {
            if (em == null) return false;
            Transform t = em.transform;
            if (Singleton<RigidbodyFPSWalker>.IsInstanced() && t.IsChildOf(Singleton<RigidbodyFPSWalker>.Instance.transform)) return true;
            if (Singleton<Player>.IsInstanced() && t.IsChildOf(Singleton<Player>.Instance.transform)) return true;
            Camera cam = Camera.main;
            return cam != null && t.IsChildOf(cam.transform);
        }

        private static bool IsFootstep(StudioEventEmitter em) =>
            Singleton<RigidbodyFPSWalker>.IsInstanced() && em.transform == Singleton<RigidbodyFPSWalker>.Instance.transform;

        private static bool TryFeet(out Vector3 feet, out Quaternion yaw)
        {
            feet = Vector3.zero; yaw = Quaternion.identity;
            if (!Singleton<RigidbodyFPSWalker>.IsInstanced()) return false;
            if (Singleton<Player>.IsInstanced() && Singleton<Player>.Instance.GetControlledMachine() != null) return false;
            Transform t = Singleton<RigidbodyFPSWalker>.Instance.transform;
            var col = t.GetComponent<Collider>();
            feet = col != null ? new Vector3(t.position.x, col.bounds.min.y, t.position.z) : t.position - Vector3.up;
            Camera cam = Camera.main;
            yaw = Quaternion.Euler(0f, cam != null ? cam.transform.eulerAngles.y : t.eulerAngles.y, 0f);
            return true;
        }

        private static bool RateOk(bool footstep)
        {
            float now = Time.unscaledTime;
            if (footstep)
            {
                if (now < _nextStep) return false;
                _nextStep = now + 0.25f;
            }
            if (now - _windowStart > 1f) { _windowStart = now; _windowCount = 0; }
            return ++_windowCount <= 14;
        }

        internal static void OnLocalPlay(StudioEventEmitter em)
        {
            if (!CanSend || !IsLocalPlayers(em)) return;
            string path = em.Event;
            if (string.IsNullOrEmpty(path) || path.StartsWith("snapshot:", StringComparison.OrdinalIgnoreCase)) return;
            if (!Describe(path, out EventDescription desc, out string realPath, out bool oneshot, out _)) return;
            if (realPath.IndexOf("/UI/", StringComparison.OrdinalIgnoreCase) >= 0) return; // menus and HUD are personal
            bool step = IsFootstep(em);
            if (!RateOk(step)) return;
            if (!TryFeet(out Vector3 feet, out Quaternion yaw)) return;
            Vector3 off = Quaternion.Inverse(yaw) * (em.transform.position - feet);
            if (off.magnitude > MaxOffset) off = off.normalized * MaxOffset;

            var ms = Begin(SubPlay, out BinaryWriter w);
            w.Write(Net.LocalId);
            w.Write(em.GetInstanceID());
            w.Write(path);
            w.Write(oneshot);
            w.Write(step);
            w.Write(off.x); w.Write(off.y); w.Write(off.z);
            ParamRef[] ps = em.Params ?? new ParamRef[0];
            int n = Math.Min(ps.Length, 8);
            w.Write((byte)n);
            for (int i = 0; i < n; i++) { w.Write(ps[i]?.Name ?? ""); w.Write(ps[i]?.Value ?? 0f); }
            Net.Broadcast(ms.ToArray(), reliable: !oneshot);
            if (!oneshot) LoopAlive[em.GetInstanceID()] = Time.unscaledTime + 2f;
        }

        internal static void OnLocalStop(StudioEventEmitter em)
        {
            if (em == null || !LoopAlive.Remove(em.GetInstanceID()) || !Net.Active) return;
            var ms = Begin(SubStop, out BinaryWriter w);
            w.Write(Net.LocalId);
            w.Write(em.GetInstanceID());
            Net.Broadcast(ms.ToArray(), reliable: true);
        }

        internal static void OnLocalParam(StudioEventEmitter em, string name, float value)
        {
            if (em == null || !Net.Active || !LoopAlive.ContainsKey(em.GetInstanceID())) return;
            float now = Time.unscaledTime;
            int key = em.GetInstanceID() ^ (name?.GetHashCode() ?? 0);
            if (ParamNext.TryGetValue(key, out float next) && now < next) return;
            ParamNext[key] = now + 0.2f;
            var ms = Begin(SubParam, out BinaryWriter w);
            w.Write(Net.LocalId);
            w.Write(em.GetInstanceID());
            w.Write(name ?? "");
            w.Write(value);
            Net.Broadcast(ms.ToArray(), reliable: false);
        }

        private static readonly Dictionary<int, float> ParamNext = new Dictionary<int, float>();

        private static MemoryStream Begin(byte sub, out BinaryWriter w)
        {
            var ms = Net.Writer(Social.MsgId);
            w = new BinaryWriter(ms);
            w.Write(sub);
            return ms;
        }

        // ---------------- receiving ----------------

        private sealed class Loop
        {
            public EventInstance Inst;
            public ulong Owner;
            public Vector3 Offset;
            public float Until;
            public bool Is3D;
            public float Max;
            public float MissingSince = -1f;
        }

        private static readonly Dictionary<long, Loop> Loops = new Dictionary<long, Loop>();
        private static readonly Dictionary<string, EventDescription> Descs = new Dictionary<string, EventDescription>();
        private static readonly HashSet<string> BadPaths = new HashSet<string>();

        private static long LoopKey(ulong owner, int id) => (long)(owner * 1000003UL) ^ id;

        private static bool Describe(string path, out EventDescription desc, out string realPath, out bool oneshot, out bool is3D)
        {
            realPath = path; oneshot = true; is3D = true;
            if (!Descs.TryGetValue(path, out desc))
            {
                if (BadPaths.Contains(path)) return false;
                try { desc = RuntimeManager.GetEventDescription(path); }
                catch { BadPaths.Add(path); return false; }
                if (!desc.isValid()) { BadPaths.Add(path); return false; }
                Descs[path] = desc;
            }
            desc.getPath(out string p);
            if (!string.IsNullOrEmpty(p)) realPath = p;
            desc.isOneshot(out oneshot);
            desc.is3D(out is3D);
            return true;
        }

        /// <summary>Called by Social.Receive for the sound sub-kinds. The host relays them to the other clients.</summary>
        internal static void Receive(byte sub, ulong from, BinaryReader r, byte[] raw)
        {
            ulong owner = r.ReadUInt64();
            int id = r.ReadInt32();
            if (Net.IsHost) Net.Broadcast(raw, reliable: sub != SubParam && !(sub == SubPlay && PeekOneshot(raw)), except: from);
            if (owner == Net.LocalId || !Net.WorldLive || GameState.IsLoading) return;
            switch (sub)
            {
                case SubPlay: ReceivePlay(owner, id, r); break;
                case SubStop: StopLoop(LoopKey(owner, id)); break;
                case SubParam:
                    {
                        string name = r.ReadString();
                        float value = r.ReadSingle();
                        if (Loops.TryGetValue(LoopKey(owner, id), out Loop l) && l.Inst.isValid())
                        {
                            l.Inst.setParameterValue(name, value);
                            l.Until = Time.unscaledTime + LoopTimeout;
                        }
                        break;
                    }
            }
        }

        // The one-shot flag sits after the path; relay one-shots unreliably like the sender did.
        private static bool PeekOneshot(byte[] raw)
        {
            try
            {
                using (var r = new BinaryReader(new MemoryStream(raw)))
                {
                    r.ReadByte(); r.ReadByte(); // msg id, sub
                    r.ReadUInt64(); r.ReadInt32(); r.ReadString();
                    return r.ReadBoolean();
                }
            }
            catch { return false; }
        }

        private static void ReceivePlay(ulong owner, int id, BinaryReader r)
        {
            string path = r.ReadString();
            bool oneshot = r.ReadBoolean();
            bool step = r.ReadBoolean();
            var off = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            int n = r.ReadByte();
            var names = new string[n];
            var values = new float[n];
            for (int i = 0; i < n; i++) { names[i] = r.ReadString(); values[i] = r.ReadSingle(); }
            if (float.IsNaN(off.x) || off.magnitude > MaxOffset + 0.1f) off = Vector3.zero;

            Transform avatar = PlayerSync.AvatarRoot(owner);
            if (avatar == null) return;
            if (!Describe(path, out EventDescription desc, out string realPath, out bool isOneshot, out bool is3D)) return;
            float max = step ? StepMax : realPath.IndexOf("detector", StringComparison.OrdinalIgnoreCase) >= 0 ? DetectorMax : ToolMax;
            Vector3 pos = avatar.position + avatar.rotation * off;
            float dist = DistanceToListener(pos);
            if (dist > max) { if (oneshot) return; }

            if (desc.createInstance(out EventInstance inst) != FMOD.RESULT.OK || !inst.isValid()) return;
            for (int i = 0; i < n; i++) inst.setParameterValue(names[i], values[i]);
            Place(inst, pos, is3D, max, dist, step);
            inst.start();
            if (oneshot || isOneshot)
            {
                inst.release();
                return;
            }
            long key = LoopKey(owner, id);
            StopLoop(key);
            Loops[key] = new Loop { Inst = inst, Owner = owner, Offset = off, Until = Time.unscaledTime + LoopTimeout, Is3D = is3D, Max = max };
        }

        private static void Place(EventInstance inst, Vector3 pos, bool is3D, float max, float dist, bool step)
        {
            if (is3D)
            {
                inst.set3DAttributes(pos.To3DAttributes());
                inst.setProperty(EVENT_PROPERTY.MINIMUM_DISTANCE, ToolMin);
                inst.setProperty(EVENT_PROPERTY.MAXIMUM_DISTANCE, max);
                if (step) inst.setVolume(0.6f);
            }
            else
            {
                // A first-person 2D event: no direction possible, so only fade with distance (log curve).
                float v = dist <= ToolMin ? 1f : Mathf.Clamp01(1f - Mathf.Log(dist / ToolMin) / Mathf.Log(max / ToolMin));
                inst.setVolume(v * v * (step ? 0.5f : 0.8f));
            }
        }

        private static float DistanceToListener(Vector3 pos)
        {
            Camera cam = Camera.main;
            return cam != null ? Vector3.Distance(cam.transform.position, pos) : 0f;
        }

        private static void StopLoop(long key)
        {
            if (!Loops.TryGetValue(key, out Loop l)) return;
            Loops.Remove(key);
            if (l.Inst.isValid())
            {
                l.Inst.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
                l.Inst.release();
            }
        }

        private static readonly List<long> Dead = new List<long>();

        /// <summary>Plugin.Update: keep-alive for local loops; move and expire remote loops.</summary>
        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (!Net.Active || !Net.WorldLive)
            {
                if (Loops.Count > 0) StopAll();
                LoopAlive.Clear();
                return;
            }

            // Local loops still playing: keep them alive on the other PCs (re-send the play as a refresh).
            if (LoopAlive.Count > 0)
            {
                Dead.Clear();
                foreach (var kv in LoopAlive) if (now >= kv.Value) Dead.Add(kv.Key);
                foreach (long idL in Dead)
                {
                    int id = (int)idL;
                    var em = FindEmitter(id);
                    if (em == null || !em.IsPlaying()) { LoopAlive.Remove(id); SendStop(id); continue; }
                    LoopAlive[id] = now + 2f;
                    var ms = Begin(SubParam, out BinaryWriter w); // a no-op parameter refresh extends the remote loop
                    w.Write(Net.LocalId); w.Write(id); w.Write("__alive"); w.Write(0f);
                    Net.Broadcast(ms.ToArray(), reliable: false);
                }
            }

            if (Loops.Count == 0) return;
            Dead.Clear();
            foreach (var kv in Loops)
            {
                Loop l = kv.Value;
                Transform avatar = PlayerSync.AvatarRoot(l.Owner);
                if (avatar == null)
                {
                    if (l.MissingSince < 0f) l.MissingSince = now;
                    if (now - l.MissingSince > 2f) Dead.Add(kv.Key);
                    continue;
                }
                l.MissingSince = -1f;
                if (now > l.Until) { Dead.Add(kv.Key); continue; }
                Vector3 pos = avatar.position + avatar.rotation * l.Offset;
                Place(l.Inst, pos, l.Is3D, l.Max, DistanceToListener(pos), false);
            }
            foreach (long k in Dead) StopLoop(k);
        }

        private static void SendStop(int id)
        {
            var ms = Begin(SubStop, out BinaryWriter w);
            w.Write(Net.LocalId); w.Write(id);
            Net.Broadcast(ms.ToArray(), reliable: true);
        }

        private static readonly Dictionary<int, StudioEventEmitter> EmitterCache = new Dictionary<int, StudioEventEmitter>();

        private static StudioEventEmitter FindEmitter(int id) =>
            EmitterCache.TryGetValue(id, out var em) && em != null ? em : null;

        internal static void Remember(StudioEventEmitter em)
        {
            if (em == null) return;
            if (EmitterCache.Count > 256) EmitterCache.Clear();
            EmitterCache[em.GetInstanceID()] = em;
        }

        internal static void StopAll()
        {
            foreach (long k in new List<long>(Loops.Keys)) StopLoop(k);
        }

        // ---------------- patches ----------------

        [HarmonyPatch(typeof(StudioEventEmitter), nameof(StudioEventEmitter.Play))]
        private static class PatchPlay
        {
            private static void Postfix(StudioEventEmitter __instance)
            {
                try
                {
                    if (!Net.Active) return;
                    Remember(__instance);
                    OnLocalPlay(__instance);
                }
                catch (Exception ex) { Plugin.Log.LogWarning("Remote sounds: " + ex.Message); }
            }
        }

        [HarmonyPatch(typeof(StudioEventEmitter), nameof(StudioEventEmitter.Stop))]
        private static class PatchStop
        {
            private static void Prefix(StudioEventEmitter __instance)
            {
                try { if (Net.Active) OnLocalStop(__instance); } catch { }
            }
        }

        [HarmonyPatch(typeof(StudioEventEmitter), nameof(StudioEventEmitter.SetParameter), new[] { typeof(string), typeof(float) })]
        private static class PatchParam
        {
            private static void Postfix(StudioEventEmitter __instance, string name, float value)
            {
                try { if (Net.Active) OnLocalParam(__instance, name, value); } catch { }
            }
        }
    }
}
