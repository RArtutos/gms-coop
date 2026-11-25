// WIRING: Net.cs Handle(): case (Msg)11: MachinePartsSync.Receive(r);  Plugin.Update(): MachinePartsSync.Tick()
// after MachineSync.Tick(). Machine state (sub message -3 of Msg.MachinePose) arrives via MachineSync.Receive.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GoldDigger;
using HarmonyLib;
using SaveSystem;
using UnityEngine;
using VehiclePhysics;

namespace GMSCoop
{
    /// <summary>
    /// Syncs the moving parts and the state of the machine a player drives (MachineSync moves the root body).
    ///
    /// The game moves parts in several ways, all only while the local player controls the machine:
    ///  - Backhoe arms (JointsController.Arms): HingeJoint spring targets / motors, ConfigurableJoint
    ///    target rotation or position, LimitJoint, AnimatedJoint (pure transform rotation), AnimRipper2.
    ///  - AnimatedJoint is also used for articulated steering (loaders, dump trucks) and drill arms.
    ///  - Plain transforms: drill rigs (DrillController / DrillScript), Frankenstein parts, cabin joysticks.
    ///  - Wheels: VPP VPWheelCollider visual spin/steer. Tracks: TrackAngularSpeedL/R.
    ///  - Cabin gauges, steering wheel, pedals: ShiftersController reads Steer/Accelerate/Brake/Gear.
    ///
    /// Per machine a "layout" is built once with stable keys (hierarchy paths):
    ///  - Float channels: AnimatedJoint angles, Backhoe joint targets and arm input (hydraulic sound),
    ///    a few known float fields. Written into the game's own state, so nothing snaps back on exit.
    ///  - Pose channels: jointed child Rigidbodies whose joint is driven (spring/motor/drive, arms) plus
    ///    known part transforms. Free-swinging bodies (doors, hooks, chains) stay local physics.
    ///  - Drive state: engine, RPM/load, lights, handbrake, steer/accel/brake, gear, steering mode,
    ///    track speeds, per-wheel spin and steer angle. Applied through Harmony right before the game reads it.
    ///
    /// Sender: every packet carries the driver's LocalId; only the driver MachineSync shows is applied.
    /// Timing: every packet carries the sender's Time.unscaledTimeAsDouble (a float clock loses ~1 ms resolution
    /// after 2-3 hours, which made parts judder in long sessions). The receiver buffers the snapshots and
    /// plays them back at the same sender time MachineSync draws the body at (MachineSync.TryGetRenderTime),
    /// interpolating values, poses, wheels and steering, so parts never run ahead of the body.
    ///
    /// Machine state (driver authoritative, sub message -3 of Msg.MachinePose, 2 Hz while driving):
    /// fuel (FuelController.TankPct) and the durability of every CheckAndRepair in
    /// MachineRepairController._WearingItems (incl. the battery, BatteryController.BatteryRepair).
    /// Refuels and repairs done on foot on a parked machine are sent too (increase detection every 2 s,
    /// CheckAndRepair.Repair immediately and forced).
    ///
    /// Each channel carries a 16-bit hash of its key; unknown hashes are skipped, so a layout mismatch
    /// degrades gracefully instead of corrupting poses.
    /// </summary>
    internal static class MachinePartsSync
    {
        private const float SendInterval = 1f / 15f;
        private const float ReleaseAfter = 1f;
        private const int MaxPacketBytes = 1100; // Steam P2P unreliable limit is 1200
        private const Msg MsgId = (Msg)11;
        private const int BufN = 8;              // part snapshots kept per channel (~0.5 s at 15 Hz)
        private const float FallbackDelay = 0.12f; // playback delay when MachineSync has no render clock
        private const float StateInterval = 0.5f;
        private const float ScanInterval = 2f;
        private const float KeyInterval = 1f;      // full set of joint values/poses at least once a second
        private const float FloatEps = 0.001f;     // joint value change worth sending (deg / m / progress)
        private const float PosEps = 0.0005f;      // part position change worth sending (m)
        private const float RotEps = 0.05f;        // part rotation change worth sending (deg)
        // Deltas over a lossy link: a value is sent while it changes and in this many packets after it stopped
        // (the last change is not lost with one packet: 5 % loss -> 1 in 160 000 instead of 1 in 20), and a drive
        // starts with this many keyframes. Without it the receiver could hold a stale arm angle until the next
        // 1 s keyframe.
        private const byte SettleRepeats = 3;

        // ------------------------------------------------------------------ layout

        private enum FloatKind : byte { AnimJoint, HingeSpring, ConfigAngle, ConfigLinear, Limit, Field, ArmInput }

        private class FloatEntry
        {
            public ushort Hash;
            public FloatKind Kind;
            public AnimatedJoint Anim;
            public HingeJoint Hinge;
            public ConfigurableJoint Config;
            public LimitJoint Limit;
            public Backhoe Arm;
            public object Owner;
            public FieldInfo Field;
            // Receiver side: snapshot buffer (sender time, value), oldest first.
            public readonly double[] BT = new double[BufN];
            public readonly float[] BV = new float[BufN];
            public int BN;
            public bool SoundOn; // ArmInput: hydraulic sound started by us
            // Sender side: last value sent (unchanged values are skipped between keyframes).
            public bool HasSent;
            public float SentV;
            public byte Repeat; // packets left that repeat a value that just stopped changing (loss cover)
        }

        private class PoseEntry
        {
            public ushort Hash;
            public Transform T;
            public Rigidbody Rb;
            // Receiver side
            public readonly double[] BT = new double[BufN];
            public readonly Vector3[] BP = new Vector3[BufN];
            public readonly Quaternion[] BR = new Quaternion[BufN];
            public int BN;
            public bool MadeKinematic;
            public bool WasKinematic;
            // Pose sampled this frame, written again in LateUpdate (script Updates of a "controlled"
            // passenger PC, e.g. drills and Frankenstein parts, may move the transform after us).
            public bool HasCur;
            public Vector3 CurP;
            public Quaternion CurR;
            // Sender side: last pose sent.
            public bool HasSent;
            public Vector3 SentP;
            public Quaternion SentR;
            public byte Repeat; // packets left that repeat a pose that just stopped changing (loss cover)
        }

        private class Layout
        {
            public readonly List<FloatEntry> Floats = new List<FloatEntry>();
            public readonly List<PoseEntry> Poses = new List<PoseEntry>();
            public readonly Dictionary<ushort, FloatEntry> FloatByHash = new Dictionary<ushort, FloatEntry>();
            public readonly Dictionary<ushort, PoseEntry> PoseByHash = new Dictionary<ushort, PoseEntry>();
            public VPWheelCollider[] Wheels = new VPWheelCollider[0];
            public int PoseCursor; // round robin when poses do not fit in one packet
            public float NextKey;  // sender: next keyframe (every value/pose, changed or not)
            public int KeyBurst;   // sender: keyframes still to send at the start of a drive (loss cover)
            // Conveyor of FrankensteinExcavator / MaximusMachineController: on/off and speed are switched by
            // the driver's input (Machine Axis 4/5) and only on the driver's PC.
            public FrankensteinBelt Belt;
        }

        private static readonly Dictionary<MachineController, Layout> Layouts = new Dictionary<MachineController, Layout>();

        // Transform fields (on the machine component, DrillScript, or their Controller fields) that are
        // moved by script code from local input.
        private static readonly HashSet<string> KnownTransformFields = new HashSet<string>
        {
            "Part1", "Part2",                                  // FrankensteinExcavator
            "DrillSystem", "DrillBit", "Drill_Piston_Up",      // DrillController (dozer drill)
            "DrillBucket_Visual", "DrillSupport",              // DrillScript (bobcat / mini drill)
            "LeftJoystick", "RightJoystick", "LeftPedal", "RightPedal",
            "LeftJoystickFront", "RightJoystickFront", "LeftJoystickBack", "RightJoystickBack",
        };

        private static readonly HashSet<string> KnownFloatFields = new HashSet<string>
        {
            "Part1Progress", "Part2Progress", // FrankensteinExcavator
            "_PistonOffset",                  // DrillController
        };

        // ------------------------------------------------------------------ remote state

        private class DriveSnap
        {
            public double T;
            public byte Flags, SteeringMode;
            public float Rpm01, Load, Steer, Accel, Brake, TrackL, TrackR;
            public int Gear;
            public float BeltSpeed;  // FrankensteinBelt.SpeedMultiplier (flags 32 = has belt, 64 = belt on)
            public int WheelCount;
            public readonly float[] WheelAng = new float[32];
            public readonly float[] WheelSteer = new float[32];
        }

        private class Remote
        {
            public int Id;
            public MachineController Machine;
            public ulong Sender;      // LocalId of the driver whose parts these are
            public Layout Layout;
            public float LastUpdate;
            public double NewestStamp = double.NegativeInfinity;

            // Fallback clock (no MachineSync render clock): min(local receive - sender time).
            public bool HasOffset;
            public double Offset;

            public readonly DriveSnap[] Drive = new DriveSnap[BufN]; // ring of preallocated snapshots, oldest first
            public int DriveN;

            // Sampled drive state at the render time (what the patches write into the game).
            public bool HasDrive;
            public bool Engine, LightsOn, LongLights, Handbrake, Reverse;
            public byte SteeringMode;
            public float Rpm01, Load, Steer, Accel, Brake, TrackL, TrackR;
            public int Gear;
            public bool HasBelt, BeltOn;
            public float BeltSpeed;
            public float[] WheelAng = new float[0];
            public float[] WheelSteer = new float[0];
            public float NextEngineFix;
            public byte WorkerMode; // steering mode the seated driver model (WorkerPositionChanger) shows on this PC

            public Remote()
            {
                for (int i = 0; i < BufN; i++) Drive[i] = new DriveSnap();
            }
        }

        private static readonly Dictionary<int, Remote> Remotes = new Dictionary<int, Remote>();
        private static readonly Dictionary<MachineController, Remote> ByMachine = new Dictionary<MachineController, Remote>();
        private static readonly HashSet<AnimatedJoint> FrozenAnimJoints = new HashSet<AnimatedJoint>();
        private static readonly Dictionary<VPWheelCollider, KeyValuePair<Remote, int>> WheelMap =
            new Dictionary<VPWheelCollider, KeyValuePair<Remote, int>>();

        private static float _nextSend;
        private static float _nextPurge;
        private static bool _applyingEngine;

        // ------------------------------------------------------------------ reflection helpers

        private static readonly AccessTools.FieldRef<AnimatedJoint, float> AnimAngle =
            SafeFieldRef<AnimatedJoint, float>("_angle");
        private static readonly AccessTools.FieldRef<LimitJoint, float> LimitSpringPos =
            SafeFieldRef<LimitJoint, float>("_springpos");

        private static readonly Action<MachineController, float> SetSteer = Setter<MachineController, float>("Steer");
        private static readonly Action<MachineController, float> SetAccelerate = Setter<MachineController, float>("Accelerate");
        private static readonly Action<MachineController, float> SetBrake = Setter<MachineController, float>("Brake");
        private static readonly Action<MachineController, bool> SetHandbrake = Setter<MachineController, bool>("HandbrakeOn");
        private static readonly Action<MachineController, bool> SetTryingReverse = Setter<MachineController, bool>("IsTryingToReverse");
        private static readonly Action<MachineController, bool> SetEngineStarted = Setter<MachineController, bool>("IsEngineStarted");
        private static readonly Action<MachineController, int> SetGear = Setter<MachineController, int>("Gear");
        private static readonly Action<MachineController, SteeringMode> SetSteeringMode = Setter<MachineController, SteeringMode>("MySteeringMode");
        private static readonly Action<TrackMachineController, float> SetTrackL = Setter<TrackMachineController, float>("TrackAngularSpeedL");
        private static readonly Action<TrackMachineController, float> SetTrackR = Setter<TrackMachineController, float>("TrackAngularSpeedR");

        private static AccessTools.FieldRef<T, F> SafeFieldRef<T, F>(string name) where T : class
        {
            try { return AccessTools.FieldRefAccess<T, F>(name); }
            catch (Exception ex) { Plugin.Log?.LogWarning($"MachinePartsSync: no field {typeof(T).Name}.{name}: {ex.Message}"); return null; }
        }

        private static Action<T, V> Setter<T, V>(string property)
        {
            try
            {
                MethodInfo mi = AccessTools.PropertySetter(typeof(T), property);
                return mi == null ? null : AccessTools.MethodDelegate<Action<T, V>>(mi);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"MachinePartsSync: no setter {typeof(T).Name}.{property}: {ex.Message}");
                return null;
            }
        }

        private static MachineController LocalMachine()
        {
            return Singleton<Player>.IsInstanced() ? Singleton<Player>.Instance.GetControlledMachine() : null;
        }

        /// <summary>The machine the local player drives itself (null when riding as a passenger). Decided by MachineSync.</summary>
        private static MachineController LocalDriven() => MachineSync.LocalDrivenMachine;

        // ------------------------------------------------------------------ layout building

        private static Layout GetLayout(MachineController m)
        {
            if (Layouts.TryGetValue(m, out var l)) return l;
            l = BuildLayout(m);
            Layouts[m] = l;
            Plugin.Log.LogInfo($"MachinePartsSync: {m.gameObject.name}: {l.Floats.Count} joint values, {l.Poses.Count} parts, {l.Wheels.Length} wheels");
            return l;
        }

        private static bool Driven(JointDrive d) => d.positionSpring > 0f && d.maximumForce > 0f;

        /// <summary>True if the joint is actively driven (spring/motor/drive). Free joints (doors, hooks) are not synced.</summary>
        private static bool IsDrivenJoint(Joint j)
        {
            if (j is HingeJoint h) return h.useSpring || h.useMotor;
            if (j is ConfigurableJoint c)
                return c.GetComponent<LimitJoint>() != null || Driven(c.xDrive) || Driven(c.yDrive) || Driven(c.zDrive) ||
                       Driven(c.angularXDrive) || Driven(c.angularYZDrive) || Driven(c.slerpDrive);
            return false;
        }

        private static Layout BuildLayout(MachineController m)
        {
            var layout = new Layout();
            layout.Belt = m is FrankensteinExcavator fe ? fe.MyBelt : m is MaximusMachineController mx ? mx.MyBelt : null;
            Transform root = m.transform;
            var keys = new HashSet<string>();
            var posed = new HashSet<Transform>();
            var ambiguousF = new HashSet<ushort>();
            var ambiguousP = new HashSet<ushort>();

            // JointsController fields (Koparka.ControledJoints, KoparkoLadowarka has two, ...).
            // Loaders/bobcats detach a "virtual" physics rig (VirtualReference) from the machine and copy
            // its joint rotations onto the visible model; parts of that rig are addressed relative to it.
            var joints = new List<KeyValuePair<string, JointsController>>();
            var rigs = new List<KeyValuePair<string, Transform>>();
            for (Type t = m.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType != typeof(JointsController)) continue;
                    if (!(f.GetValue(m) is JointsController jc)) continue;
                    joints.Add(new KeyValuePair<string, JointsController>(f.Name, jc));
                    if (jc.VirtualReference != null && !jc.VirtualReference.IsChildOf(root))
                        rigs.Add(new KeyValuePair<string, Transform>("V:" + f.Name, jc.VirtualReference));
                }
            }

            string PathOf(Transform t)
            {
                if (t == null) return null;
                string p = null;
                if (t.IsChildOf(root)) p = RelPath(t, root);
                else
                    foreach (var rig in rigs)
                        if (t != rig.Value && t.IsChildOf(rig.Value)) { p = rig.Key + "/" + RelPath(t, rig.Value); break; }
                // Children instantiated at runtime (ropes, dirt, attached items) differ between PCs.
                if (p == null || p.Contains("(Clone)")) return null;
                return p;
            }

            string UniqueKey(string k)
            {
                string u = k;
                for (int n = 1; !keys.Add(u); n++) u = k + "#" + n;
                return u;
            }

            void AddFloat(FloatEntry e, string key)
            {
                e.Hash = Hash16(UniqueKey(key));
                layout.Floats.Add(e);
                if (ambiguousF.Contains(e.Hash)) return;
                if (layout.FloatByHash.ContainsKey(e.Hash)) { layout.FloatByHash.Remove(e.Hash); ambiguousF.Add(e.Hash); return; }
                layout.FloatByHash[e.Hash] = e;
            }

            void AddPose(Transform t, Rigidbody rb, string key)
            {
                if (t == null || t == root || !posed.Add(t)) return;
                var e = new PoseEntry { T = t, Rb = rb, Hash = Hash16(UniqueKey(key)) };
                layout.Poses.Add(e);
                if (ambiguousP.Contains(e.Hash)) return;
                if (layout.PoseByHash.ContainsKey(e.Hash)) { layout.PoseByHash.Remove(e.Hash); ambiguousP.Add(e.Hash); return; }
                layout.PoseByHash[e.Hash] = e;
            }

            // 1) AnimatedJoints: transform-only joints driven by an angle (arms, articulated steering).
            var anims = new List<AnimatedJoint>(root.GetComponentsInChildren<AnimatedJoint>(true));
            foreach (var rig in rigs) anims.AddRange(rig.Value.GetComponentsInChildren<AnimatedJoint>(true));
            if (AnimAngle != null)
                foreach (var aj in anims)
                {
                    string p = PathOf(aj.transform);
                    if (p != null) AddFloat(new FloatEntry { Kind = FloatKind.AnimJoint, Anim = aj }, "A:" + p);
                }

            // 2) Backhoe arm targets, so the joint drives hold the synced pose after the remote driver leaves,
            //    and the arm input (AxisValue) that drives the hydraulic sound.
            foreach (var kv in joints)
            {
                Backhoe[] arms = kv.Value.Arms;
                if (arms == null) continue;
                for (int i = 0; i < arms.Length; i++)
                {
                    Backhoe arm = arms[i];
                    if (arm == null) continue;
                    AddFloat(new FloatEntry { Kind = FloatKind.ArmInput, Arm = arm }, "X:" + kv.Key + "/" + i);
                    if (arm.Arm == null) continue;
                    string key = "B:" + kv.Key + "/" + i;
                    GameObject go = arm.Arm;
                    // Same precedence as Backhoe.SetUp (the last matching component wins there).
                    FloatEntry e = null;
                    var hinge = go.GetComponent<HingeJoint>();
                    if (hinge != null && hinge.useSpring) e = new FloatEntry { Kind = FloatKind.HingeSpring, Hinge = hinge };
                    var cj = go.GetComponent<ConfigurableJoint>();
                    if (cj != null)
                    {
                        if (cj.angularXMotion != ConfigurableJointMotion.Locked) e = new FloatEntry { Kind = FloatKind.ConfigAngle, Config = cj };
                        if (cj.yMotion != ConfigurableJointMotion.Locked) e = new FloatEntry { Kind = FloatKind.ConfigLinear, Config = cj };
                    }
                    var lj = go.GetComponent<LimitJoint>();
                    if (lj != null && LimitSpringPos != null) e = new FloatEntry { Kind = FloatKind.Limit, Limit = lj };
                    if (go.GetComponent<AnimatedJoint>() != null) e = null; // covered by (1)
                    var ripper = go.GetComponent<AnimRipper2>();
                    if (ripper != null)
                        e = new FloatEntry { Kind = FloatKind.Field, Owner = ripper, Field = AccessTools.Field(typeof(AnimRipper2), "CurrentProgress") };
                    if (e == null || (e.Kind == FloatKind.Field && e.Field == null)) continue;
                    e.Arm = arm;
                    AddFloat(e, key);
                }
            }

            // 3) Known script-moved transforms and float fields (drills, Frankenstein, cabin joysticks).
            var owners = new List<object> { m };
            owners.AddRange(root.GetComponentsInChildren<DrillScript>(true));
            for (int oi = 0; oi < owners.Count; oi++)
            {
                object owner = owners[oi];
                Type ot = owner.GetType();
                foreach (var f in ot.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    string key = "F:" + ot.Name + "." + f.Name;
                    if (f.FieldType == typeof(Transform) && KnownTransformFields.Contains(f.Name))
                    {
                        var t = f.GetValue(owner) as Transform;
                        string p = PathOf(t);
                        if (p != null) AddPose(t, null, "T:" + p);
                    }
                    else if (f.FieldType == typeof(float) && KnownFloatFields.Contains(f.Name))
                    {
                        AddFloat(new FloatEntry { Kind = FloatKind.Field, Owner = owner, Field = f }, key + "@" + oi);
                    }
                    else if (oi == 0 && typeof(Controller).IsAssignableFrom(f.FieldType) && f.FieldType != typeof(JointsController))
                    {
                        // Controllers held by the machine (e.g. Doozer_D6H_Drill.Drill : DrillController).
                        object c = f.GetValue(owner);
                        if (c != null && !owners.Contains(c)) owners.Add(c);
                    }
                }
            }

            // 4) Jointed child rigidbodies moved by a joint drive (boom, stick, bucket, blade, loader arms, ...).
            //    Free-swinging joints (doors, hooks, chains: no spring/motor/drive) keep local physics.
            var jointList = new List<Joint>(root.GetComponentsInChildren<Joint>(true));
            foreach (var rig in rigs) jointList.AddRange(rig.Value.GetComponentsInChildren<Joint>(true));
            foreach (var j in jointList)
            {
                if (j is FixedJoint) continue; // runtime attachments / arm locks, not moving parts
                if (!IsDrivenJoint(j)) continue;
                Rigidbody rb = j.GetComponent<Rigidbody>();
                if (rb == null || rb == m.MyRigidbody) continue;
                if (j.GetComponent<AnimatedJoint>() != null) continue;
                if (j.GetComponentInParent<Holdable>() != null) continue; // items held on the machine
                string p = PathOf(j.transform);
                if (p != null) AddPose(j.transform, rb, "R:" + p);
            }
            foreach (var kv in joints)
            {
                if (kv.Value.Arms == null) continue;
                foreach (var arm in kv.Value.Arms)
                {
                    if (arm?.Arm == null) continue;
                    Rigidbody rb = arm.Arm.GetComponent<Rigidbody>();
                    if (rb == null || rb == m.MyRigidbody || arm.Arm.GetComponent<AnimatedJoint>() != null) continue;
                    string p = PathOf(arm.Arm.transform);
                    if (p != null) AddPose(arm.Arm.transform, rb, "R:" + p);
                }
            }

            // 5) Wheels (VPP). Order is the vehicle's wheel order, identical on both PCs.
            try
            {
                var vc = m.MyVehicleController;
                if (vc != null && vc.wheelState != null)
                {
                    layout.Wheels = new VPWheelCollider[vc.wheelState.Length];
                    for (int i = 0; i < layout.Wheels.Length; i++) layout.Wheels[i] = vc.wheelState[i]?.wheelCol;
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("MachinePartsSync: wheels: " + ex.Message); }

            return layout;
        }

        private static string RelPath(Transform t, Transform stop)
        {
            if (t == stop) return ".";
            var parts = new List<string>();
            for (Transform c = t; c != null && c != stop; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static ushort Hash16(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return (ushort)(h ^ (h >> 16));
        }

        // ------------------------------------------------------------------ float channel access

        private static float GetValue(FloatEntry e)
        {
            switch (e.Kind)
            {
                case FloatKind.AnimJoint: return e.Anim != null ? AnimAngle(e.Anim) : 0f;
                case FloatKind.HingeSpring: return e.Hinge != null ? e.Hinge.spring.targetPosition : 0f;
                case FloatKind.ConfigAngle:
                case FloatKind.ConfigLinear: return e.Arm._angle;
                case FloatKind.Limit: return e.Limit != null ? LimitSpringPos(e.Limit) : 0f;
                case FloatKind.Field: return e.Owner != null ? (float)e.Field.GetValue(e.Owner) : 0f;
                case FloatKind.ArmInput: return e.Arm.AxisValue;
            }
            return 0f;
        }

        private static void SetValue(FloatEntry e, float v)
        {
            switch (e.Kind)
            {
                case FloatKind.AnimJoint:
                    if (e.Anim == null) return;
                    // AnimatedJoint.Update turns _angle into the local rotation; its FixedUpdate is frozen.
                    e.Anim.TargetSpeed = 0f;
                    AnimAngle(e.Anim) = v;
                    break;
                case FloatKind.HingeSpring:
                    if (e.Hinge == null) return;
                    JointSpring s = e.Hinge.spring;
                    s.targetPosition = v;
                    e.Hinge.spring = s;
                    break;
                case FloatKind.ConfigAngle:
                    if (e.Config == null) return;
                    e.Arm._angle = v;
                    e.Config.targetRotation = Quaternion.Euler(v, 0f, 0f);
                    break;
                case FloatKind.ConfigLinear:
                    if (e.Config == null) return;
                    e.Arm._angle = v;
                    e.Config.targetPosition = new Vector3(0f, v, 0f);
                    break;
                case FloatKind.Limit:
                    if (e.Limit == null) return;
                    LimitSpringPos(e.Limit) = v;
                    e.Arm._angle = v;
                    if (e.Limit._myjoint != null) e.Limit._myjoint.targetPosition = new Vector3(0f, v, 0f);
                    break;
                case FloatKind.Field:
                    if (e.Owner is UnityEngine.Object uo && uo == null) return;
                    if (e.Owner != null) e.Field.SetValue(e.Owner, v);
                    break;
                case FloatKind.ArmInput:
                    e.Arm.AxisValue = v;
                    ArmSound(e, v);
                    break;
            }
        }

        /// <summary>
        /// Backhoe.Sounds runs only for the local operator (JointsController calls Backhoe.Update only when
        /// controlled) and reads the local input axis: replay it with the driver's arm input.
        /// </summary>
        private static void ArmSound(FloatEntry e, float v)
        {
            var em = e.Arm.SoundEmiter;
            if (em == null) return;
            if (Mathf.Abs(v) > 0.01f)
            {
                em.SetParameter("Speed", Mathf.Abs(v));
                if (!e.SoundOn || !em.IsPlaying())
                {
                    em.SetParameter("Release", 0f);
                    em.Play();
                    e.SoundOn = true;
                }
            }
            else if (e.SoundOn)
            {
                em.SetParameter("Speed", 0f);
                em.SetParameter("Release", 1f);
                e.SoundOn = false;
            }
        }

        // ------------------------------------------------------------------ sender

        /// <summary>Called from Plugin.Update. Sends the driven machine's parts/state and plays back remote ones.</summary>
        internal static void Tick()
        {
            if (!Net.Active)
            {
                if (Remotes.Count > 0) ReleaseAll();
                return;
            }

            PurgeDestroyed();
            ApplyRemotes();
            if (Remotes.Count > 0 && _late == null) _late = LateRunner.Create();

            if (!MachineSync.WorldReady()) { LightWatch.Clear(); LightOwned.Clear(); return; } // client still in its own pre-join world
            StateTick();
            RemoteFuelWatch();
            LightTick();
            LightRefresh();

            if (Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + SendInterval;

            MachineController m = LocalDriven();
            if (m == null) { _lastPartsId = -1; return; }
            int id = MachineSync.IdOf(m);
            if (id < 0) return; // not indexed yet

            try { Send(m, id); }
            catch (Exception ex) { VehicleTraffic.Warn("parts send", "MachinePartsSync send: " + ex, error: true); _nextSend = Time.unscaledTime + 2f; }
        }

        private static int _lastPartsId = -1;

        /// <summary>
        /// One parts packet. Drive state and wheels always; joint values and part poses only when they changed
        /// since they were last sent, plus a keyframe with everything once a second (lost packets, late
        /// joiners) and at the start of every drive. The receiver holds an absent value (see Receive).
        /// </summary>
        private static void Send(MachineController m, int id)
        {
            Layout l = GetLayout(m);
            float now = Time.unscaledTime;
            if (id != _lastPartsId) l.KeyBurst = SettleRepeats + 1;
            _lastPartsId = id;
            bool key = now >= l.NextKey || l.KeyBurst > 0;
            if (l.KeyBurst > 0) l.KeyBurst--;
            if (key) l.NextKey = now + KeyInterval;

            BinaryWriter w = VehicleTraffic.Begin(MsgId);
            Stream ms = w.BaseStream;
            w.Write(id);
            w.Write(MachineSync.Epoch);
            w.Write(Net.LocalId);
            w.Write(Time.unscaledTimeAsDouble); // same clock as MachineSync's pose stamps

            // Drive state.
            byte flags = 0;
            if (m.IsEngineStarted) flags |= 1;
            if (m.Lights != null && m.Lights.SwitchedOn) flags |= 2;
            if (m.Lights != null && m.Lights.LongLightsOn) flags |= 4;
            if (m.HandbrakeOn) flags |= 8;
            if (m.IsTryingToReverse) flags |= 16;
            FrankensteinBelt belt = l.Belt;
            if (belt != null) flags |= 32;
            if (belt != null && belt.IsEnabled) flags |= 64;
            w.Write(flags);
            w.Write((byte)m.MySteeringMode);
            w.Write(Q01(SafeFloat(m.GetEngineRPM01)));
            w.Write(Q01(SafeFloat(m.GetEngineLoad)));
            w.Write(QS(m.Steer, 127f));
            w.Write(Q01(m.Accelerate));
            w.Write(Q01(m.Brake));
            w.Write((sbyte)Mathf.Clamp(m.Gear, -128, 127));
            var track = m as TrackMachineController;
            w.Write(Q16(track != null ? track.TrackAngularSpeedL : 0f, 100f));
            w.Write(Q16(track != null ? track.TrackAngularSpeedR : 0f, 100f));
            w.Write(belt != null ? belt.SpeedMultiplier : 0f); // exact: the game compares it with ==

            // Wheels: visual spin (rad/s) and steer angle (deg).
            int wc = Mathf.Min(l.Wheels.Length, 32);
            w.Write((byte)wc);
            for (int i = 0; i < wc; i++)
            {
                VPWheelCollider wcol = l.Wheels[i];
                w.Write(Q16(wcol != null ? wcol.angularVelocity : 0f, 10f));
                w.Write(Q16(wcol != null ? wcol.steerAngle : 0f, 100f));
            }

            // Joint values (6 bytes each), capped so poses still get some room.
            w.Flush();
            int maxFloats = Mathf.Max(0, (MaxPacketBytes - (int)ms.Length - 4) / 6);
            long nfAt = ms.Position;
            w.Write((ushort)0);
            int nf = 0;
            for (int i = 0; i < l.Floats.Count && nf < maxFloats; i++)
            {
                FloatEntry e = l.Floats[i];
                float v = GetValue(e);
                bool changed = !e.HasSent || Mathf.Abs(v - e.SentV) > FloatEps;
                if (changed) e.Repeat = SettleRepeats;
                else if (e.Repeat > 0) e.Repeat--;
                else if (!key) continue;
                w.Write(e.Hash);
                w.Write(v);
                e.SentV = v;
                e.HasSent = true;
                nf++;
            }
            PatchCount(w, nfAt, nf);

            // Part poses (23 bytes each), round robin if they do not fit in one packet.
            w.Flush();
            int budget = Mathf.Max(0, (MaxPacketBytes - (int)ms.Length - 2) / 23);
            long npAt = ms.Position;
            w.Write((ushort)0);
            int count = 0;
            int np = l.Poses.Count;
            int start = np > 0 ? l.PoseCursor % np : 0;
            for (int k = 0; k < np; k++)
            {
                if (count >= budget) { l.PoseCursor = (start + k) % np; break; }
                PoseEntry e = l.Poses[(start + k) % np];
                if (e.T == null) continue;
                Vector3 p = e.T.localPosition;
                Quaternion q = e.T.localRotation;
                bool changed = !e.HasSent || (p - e.SentP).sqrMagnitude > PosEps * PosEps || Quaternion.Angle(q, e.SentR) > RotEps;
                if (changed) e.Repeat = SettleRepeats;
                else if (e.Repeat > 0) e.Repeat--;
                else if (!key) continue;
                e.SentP = p;
                e.SentR = q;
                e.HasSent = true;
                w.Write(e.Hash);
                w.Write(p.x); w.Write(p.y); w.Write(p.z);
                if (q.w < 0f) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
                w.Write(QS16(q.x)); w.Write(QS16(q.y)); w.Write(QS16(q.z)); w.Write(QS16(q.w));
                count++;
            }
            PatchCount(w, npAt, count);

            VehicleTraffic.Send(VehicleTraffic.Parts, reliable: false);
        }

        private static void PatchCount(BinaryWriter w, long at, int n)
        {
            w.Flush();
            Stream st = w.BaseStream;
            long end = st.Position;
            st.Position = at;
            w.Write((ushort)n);
            w.Flush();
            st.Position = end;
        }

        private static float SafeFloat(Func<float> f)
        {
            try { return f(); } catch { return 0f; }
        }

        private static byte Q01(float v) => (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f);
        private static sbyte QS(float v, float scale) => (sbyte)Mathf.Clamp(Mathf.RoundToInt(v * scale), -127, 127);
        private static short Q16(float v, float scale) => (short)Mathf.Clamp(Mathf.RoundToInt(v * scale), -32767, 32767);
        private static short QS16(float v) => (short)Mathf.Clamp(Mathf.RoundToInt(v * 32767f), -32767, 32767);

        // ------------------------------------------------------------------ receiver

        internal static void Receive(BinaryReader r)
        {
            VehicleTraffic.Rx(VehicleTraffic.Parts, (int)r.BaseStream.Length + 1);
            int id = r.ReadInt32();
            byte epoch = r.ReadByte();
            ulong sender = r.ReadUInt64();
            double stamp = r.ReadDouble();
            byte flags = r.ReadByte();
            byte steeringMode = r.ReadByte();
            float rpm = r.ReadByte() / 255f;
            float load = r.ReadByte() / 255f;
            float steer = r.ReadSByte() / 127f;
            float accel = r.ReadByte() / 255f;
            float brake = r.ReadByte() / 255f;
            int gear = r.ReadSByte();
            float trackL = r.ReadInt16() / 100f;
            float trackR = r.ReadInt16() / 100f;
            float beltSpeed = r.ReadSingle();
            int wc = r.ReadByte();
            if (wc > 32) return;

            if (!MachineSync.WorldReady() || double.IsNaN(stamp) || double.IsInfinity(stamp)) return;
            if (!MachineSync.EpochOk(epoch)) return;
            if (sender == 0 || sender == Net.LocalId || MachineSync.LeftRecently(sender)) return;
            MachineController m = MachineSync.Get(id);
            if (m == null) return;
            if (m == LocalDriven())
            {
                // We drive it ourselves: never let remote values fight local control.
                if (Remotes.ContainsKey(id)) Release(id);
                return;
            }
            // Only the driver MachineSync shows (two players may briefly claim the same machine).
            ulong driver = MachineSync.DriverOf(id);
            if (driver != 0 && driver != sender) return;
            // A straggler sent before that player got out (overtaken by its reliable exit): ignore.
            if (driver == 0 && MachineSync.SentBeforeExit(id, sender, stamp)) return;

            if (!Remotes.TryGetValue(id, out Remote rem) || rem.Machine != m)
            {
                if (rem != null) Release(id);
                rem = Begin(id, m);
            }
            float now = Time.unscaledTime;

            // Another driver: other clock, other parts state.
            if (rem.Sender != sender)
            {
                rem.Sender = sender;
                rem.HasOffset = false;
                ClearBuffers(rem);
            }
            // Fallback clock (used only while MachineSync has no render clock). Same sender, same clock:
            // a lag spike only moves the estimate, the buffered snapshots stay valid.
            double o = Time.unscaledTimeAsDouble - stamp;
            if (!rem.HasOffset || o - rem.Offset > 1.0 || o - rem.Offset < -30.0)
            {
                rem.HasOffset = true;
                rem.Offset = o;
            }
            else rem.Offset = Math.Min(o, rem.Offset + 0.0005);

            // Unreliable packets may arrive late: a snapshot older than the newest one is dropped.
            if (stamp <= rem.NewestStamp) return;
            // Values/poses absent from a packet were unchanged. If one was also absent from the previous
            // packet (received, not lost), hold its last value until then, so playback does not slide
            // from an old sample to the new one over the whole idle time.
            double prev = rem.NewestStamp;
            bool prevOk = prev > double.NegativeInfinity && stamp - prev < 0.5;
            rem.NewestStamp = stamp;
            rem.LastUpdate = now;
            Layout l = rem.Layout;

            DriveSnap d = NextDrive(rem);
            d.T = stamp;
            d.Flags = flags;
            d.SteeringMode = steeringMode;
            d.Rpm01 = rpm; d.Load = load; d.Steer = steer; d.Accel = accel; d.Brake = brake;
            d.Gear = gear; d.TrackL = trackL; d.TrackR = trackR;
            d.BeltSpeed = beltSpeed;
            d.WheelCount = wc;
            for (int i = 0; i < wc; i++)
            {
                d.WheelAng[i] = r.ReadInt16() / 10f;
                d.WheelSteer[i] = r.ReadInt16() / 100f;
            }

            int nf = r.ReadUInt16();
            for (int i = 0; i < nf; i++)
            {
                ushort h = r.ReadUInt16();
                float v = r.ReadSingle();
                if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                if (!l.FloatByHash.TryGetValue(h, out FloatEntry e)) continue;
                if (prevOk && e.BN > 0 && e.BT[e.BN - 1] < prev - 0.0005f) Push(e.BT, e.BV, ref e.BN, prev, e.BV[e.BN - 1]);
                Push(e.BT, e.BV, ref e.BN, stamp, v);
                if (e.Kind == FloatKind.AnimJoint && e.Anim != null) FrozenAnimJoints.Add(e.Anim);
            }

            int np = r.ReadUInt16();
            for (int i = 0; i < np; i++)
            {
                ushort h = r.ReadUInt16();
                var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var rot = new Quaternion(r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f);
                if (!l.PoseByHash.TryGetValue(h, out PoseEntry e) || e.T == null) continue;
                float mag = Mathf.Sqrt(rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w);
                if (mag < 0.5f || float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z)) continue;
                rot = new Quaternion(rot.x / mag, rot.y / mag, rot.z / mag, rot.w / mag);
                if (prevOk && e.BN > 0 && e.BT[e.BN - 1] < prev - 0.0005f) PushPose(e, prev, e.BP[e.BN - 1], e.BR[e.BN - 1]);
                PushPose(e, stamp, pos, rot);
                if (e.Rb != null && !e.MadeKinematic)
                {
                    // Physics joints would fight the received pose: hold the part kinematically.
                    e.WasKinematic = e.Rb.isKinematic;
                    e.Rb.isKinematic = true;
                    e.MadeKinematic = true;
                }
            }
        }

        private static DriveSnap NextDrive(Remote rem)
        {
            if (rem.DriveN == BufN)
            {
                DriveSnap first = rem.Drive[0];
                Array.Copy(rem.Drive, 1, rem.Drive, 0, BufN - 1);
                rem.Drive[BufN - 1] = first; // reuse the oldest object
                rem.DriveN--;
            }
            return rem.Drive[rem.DriveN++];
        }

        private static void Push(double[] bt, float[] bv, ref int bn, double t, float v)
        {
            if (bn == BufN) { Shift(bt, bn); Shift(bv, bn); bn--; }
            bt[bn] = t;
            bv[bn] = v;
            bn++;
        }

        private static void PushPose(PoseEntry e, double t, Vector3 pos, Quaternion rot)
        {
            if (e.BN == BufN) { Shift(e.BT, e.BN); Shift(e.BP, e.BN); Shift(e.BR, e.BN); e.BN--; }
            e.BT[e.BN] = t; e.BP[e.BN] = pos; e.BR[e.BN] = rot; e.BN++;
        }

        private static void Shift<T>(T[] a, int n) => Array.Copy(a, 1, a, 0, n - 1);

        private static void ClearBuffers(Remote rem)
        {
            rem.DriveN = 0;
            rem.NewestStamp = double.NegativeInfinity;
            foreach (var e in rem.Layout.Floats) e.BN = 0;
            foreach (var e in rem.Layout.Poses) e.BN = 0;
        }

        private static Remote Begin(int id, MachineController m)
        {
            var rem = new Remote { Id = id, Machine = m, Layout = GetLayout(m), LastUpdate = Time.unscaledTime, WorkerMode = (byte)m.MySteeringMode };
            ClearBuffers(rem);
            Remotes[id] = rem;
            ByMachine[m] = rem;
            return rem;
        }

        private static void MapWheels(Remote rem)
        {
            Wheels(rem, remove: true);
            Wheels(rem, remove: false);
        }

        private static void Wheels(Remote rem, bool remove)
        {
            VPWheelCollider[] wheels = rem.Layout.Wheels;
            for (int i = 0; i < wheels.Length; i++)
            {
                if (wheels[i] == null) continue;
                if (remove) WheelMap.Remove(wheels[i]);
                else if (i < rem.WheelAng.Length) WheelMap[wheels[i]] = new KeyValuePair<Remote, int>(rem, i);
            }
        }

        private static void SetEngine(MachineController m, bool on)
        {
            _applyingEngine = true;
            try
            {
                if (!on)
                {
                    m.StopEngine(StopEngineCause.Operator);
                    return;
                }
                // Like MachineController.StartEngine, minus the battery/fuel checks and HUD hints:
                // the driver already passed them on their PC.
                if (SetEngineStarted == null) return;
                SetEngineStarted(m, true);
                m.EngineStartedTime = Time.realtimeSinceStartup;
                m.MyVehicleController?.data.Set(0, 9, 1);
                m.Shifters?.EngineKey?.Set(on: true, m);
                m.OnEngineStartStop?.Invoke(true); // SoundController starts the engine sound here
            }
            catch (Exception ex)
            {
                VehicleTraffic.Warn("parts engine", "MachinePartsSync engine: " + ex.Message);
            }
            finally
            {
                _applyingEngine = false;
            }
        }

        /// <summary>
        /// MachineSync: the remote driver vanished (disconnect/crash, no exit packet) or the session ended.
        /// Stop the engine it left running here (it would burn fuel forever) and give the parts back.
        /// </summary>
        internal static void OnDriverLost(MachineController m)
        {
            if (m == null) return;
            if (ByMachine.TryGetValue(m, out Remote rem)) Release(rem.Id);
            if (m.IsEngineStarted && !m.IsControlled())
            {
                SetEngine(m, false);
                Plugin.Log.LogInfo($"MachinePartsSync: driver of {m.gameObject.name} left, engine stopped");
            }
        }

        /// <summary>Forgets every remote (machine ids are about to be renumbered or the world changes).</summary>
        internal static void Reset()
        {
            ReleaseAll();
            States.Clear();
            LightWatch.Clear();
            LightOwned.Clear();
            LightSegments.Clear();
        }

        /// <summary>
        /// The isKinematic value a part body this class holds kinematically (driven joint of a remote-driven
        /// machine) gets back on release; MachineSync writes it into saves instead of our stand-in.
        /// </summary>
        internal static bool TryGetHeldKinematic(Rigidbody rb, out bool original)
        {
            original = false;
            if (rb == null || Remotes.Count == 0) return false;
            foreach (var kv in Remotes)
                foreach (var e in kv.Value.Layout.Poses)
                    if (e.MadeKinematic && e.Rb == rb) { original = e.WasKinematic; return true; }
            return false;
        }

        // ------------------------------------------------------------------ mobile light trailer (sub message -7)
        //
        // The light trailer (MobileLightTrailer, a Trailer) is worked from its own panel: engine on/off, stand
        // legs, mast up/down/stop, reflectors, and segment rotation (LightSegment.startRotate/stopRotate). All
        // of it ran only on the PC of the player who pressed the button, and the segments move per frame
        // (frame-rate dependent), so even replayed calls would end at different poses. Instead the PC where an
        // operation happened streams the trailer's state (engine, lights, every segment's pose and motion
        // flags) every 0.2 s while a segment moves, and a final exact state once all stopped. The panel lever
        // (Expand_Lever) is cosmetic and is left alone: its On flag also flips on the "can't operate" wiggle
        // (pingPong toggle), so copying it would show the lever in the wrong position.

        private static readonly Dictionary<MobileLightTrailer, float> LightWatch = new Dictionary<MobileLightTrailer, float>();
        private static readonly Dictionary<MobileLightTrailer, LightSegment[]> LightSegments = new Dictionary<MobileLightTrailer, LightSegment[]>();
        private static bool _applyingLight;
        private static float _nextLightSend;
        // Light trailers this PC operated last (value: isWorking when last sent). While one works, its generator
        // burns fuel on every PC; the tank itself is a FuelStationController, which FuelSync keeps host-authoritative
        // (one owner, no fight with this stream). The operating PC re-sends the exact state every few seconds so an
        // engine that stopped for lack of fuel stays stopped everywhere.
        private static readonly Dictionary<MobileLightTrailer, bool> LightOwned = new Dictionary<MobileLightTrailer, bool>();
        private static float _nextLightRefresh;
        private const float LightRefreshInterval = 5f;
        private static readonly MethodInfo LightRefreshJoint = AccessTools.Method(typeof(LightSegment), "RefreshJoint");

        private static LightSegment[] SegmentsOf(MobileLightTrailer t)
        {
            if (LightSegments.TryGetValue(t, out var segs)) return segs;
            if (LightSegments.Count > 64) LightSegments.Clear(); // destroyed trailers
            // Hierarchy order: the same prefab on every PC.
            return LightSegments[t] = t.GetComponentsInChildren<LightSegment>(true);
        }

        private static void MarkLight(MobileLightTrailer t)
        {
            if (_applyingLight || t == null || !Net.Active || GameState.IsLoading) return;
            LightWatch[t] = Time.unscaledTime + 30f; // safety: stop streaming after 30 s
        }

        private static void LightTick()
        {
            if (LightWatch.Count == 0 || Time.unscaledTime < _nextLightSend) return;
            float now = Time.unscaledTime;
            _nextLightSend = now + 0.2f;
            List<MobileLightTrailer> done = null;
            foreach (var kv in LightWatch)
            {
                MobileLightTrailer t = kv.Key;
                if (t == null) { (done ??= new List<MobileLightTrailer>()).Add(t); continue; }
                bool moving = false;
                foreach (LightSegment seg in SegmentsOf(t)) if (seg != null && seg.enableMoving && t.isWorking) moving = true;
                bool final = !moving || now > kv.Value;
                try { SendLight(t, final); }
                catch (Exception ex) { VehicleTraffic.Warn("light send", "MachinePartsSync light trailer: " + ex.Message); final = true; }
                if (final)
                {
                    (done ??= new List<MobileLightTrailer>()).Add(t);
                    LightOwned[t] = t.isWorking;
                }
            }
            if (done != null) foreach (var t in done) LightWatch.Remove(t);
        }

        /// <summary>Every 5 s: exact state of the working light trailers this PC operated last.</summary>
        private static void LightRefresh()
        {
            if (LightOwned.Count == 0 || Time.unscaledTime < _nextLightRefresh) return;
            _nextLightRefresh = Time.unscaledTime + LightRefreshInterval;
            List<MobileLightTrailer> keys = null;
            foreach (var kv in LightOwned) (keys ??= new List<MobileLightTrailer>()).Add(kv.Key);
            foreach (MobileLightTrailer t in keys)
            {
                if (t == null) { LightOwned.Remove(t); continue; }
                if (LightWatch.ContainsKey(t)) continue;      // streaming anyway
                bool working = t.isWorking;
                if (!working && !LightOwned[t]) continue;     // off and already sent as off
                try { SendLight(t, final: true); }
                catch (Exception ex) { VehicleTraffic.Warn("light refresh", "MachinePartsSync light trailer refresh: " + ex.Message); }
                LightOwned[t] = working;
            }
        }

        private static void SendLight(MobileLightTrailer t, bool final)
        {
            int id = MachineSync.TrailerIdOf(t);
            if (id < 0) return;
            LightSegment[] segs = SegmentsOf(t);
            int n = Mathf.Min(segs.Length, 24); // 24 x 33 B: well under the packet limit
            var ms = Net.Writer(Msg.MachinePose);
            var w = new BinaryWriter(ms);
            w.Write(MachineSync.SubLightTrailer);
            w.Write(MachineSync.Epoch);
            w.Write(id);
            w.Write(Hash16(t.name));
            byte flags = 0;
            if (t.isWorking) flags |= 1;
            if (t.LightsOn) flags |= 2;
            if (final) flags |= 8;
            w.Write(flags);
            w.Write(-1f); // fuel: none. The generator tank is a FuelStationController, host-authoritative in FuelSync (sub 23)
            w.Write((byte)n);
            for (int i = 0; i < n; i++)
            {
                LightSegment seg = segs[i];
                byte sf = 0;
                if (seg != null && seg.enableMoving) sf |= 1;
                if (seg != null && seg.up) sf |= 2;
                if (seg != null && seg.isSpreaded) sf |= 4;
                if (seg != null && seg.isClosed) sf |= 8;
                w.Write(sf);
                w.Write(seg != null ? seg.NormalizedPosition : 0f);
                Vector3 p = seg != null ? seg.transform.localPosition : Vector3.zero;
                Quaternion q = seg != null ? seg.transform.localRotation : Quaternion.identity;
                w.Write(p.x); w.Write(p.y); w.Write(p.z);
                w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
            }
            byte[] data = ms.ToArray();
            Net.Broadcast(data, reliable: true);
            VehicleTraffic.Tx(VehicleTraffic.Parts, data.Length);
        }

        /// <summary>Sub message -7 (after MachineSync read the id and epoch): a light trailer's state.</summary>
        internal static void ReceiveLightTrailer(BinaryReader r)
        {
            int id = r.ReadInt32();
            ushort name = r.ReadUInt16();
            byte flags = r.ReadByte();
            float fuel = r.ReadSingle();
            int n = r.ReadByte();
            if (!MachineSync.WorldReady()) return;
            var t = MachineSync.GetTrailer(id) as MobileLightTrailer;
            if (t == null || Hash16(t.name) != name)
            {
                VehicleTraffic.Warn("light missing " + id, $"MachinePartsSync: light trailer #{id} not found here"); // re-sent every 5 s while it works
                return;
            }
            bool final = (flags & 8) != 0;
            LightSegment[] segs = SegmentsOf(t);
            _applyingLight = true;
            try
            {
                LightWatch.Remove(t); // the other PC operates it now
                LightOwned.Remove(t);
                t.isWorking = (flags & 1) != 0;
                t.TurnOnLights((flags & 2) != 0);
                _ = fuel; // always -1 (FuelSync owns the generator tank); kept for the packet layout
                for (int i = 0; i < n; i++)
                {
                    byte sf = r.ReadByte();
                    float norm = r.ReadSingle();
                    var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    var q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    if (i >= segs.Length || segs[i] == null) continue;
                    float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                    if (mag < 0.5f || float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) || float.IsNaN(norm)) continue;
                    LightSegment seg = segs[i];
                    // Between snapshots the segment keeps moving locally (smooth); the final one is exact.
                    seg.enableMoving = !final && (sf & 1) != 0;
                    seg.up = (sf & 2) != 0;
                    seg.isSpreaded = (sf & 4) != 0;
                    seg.isClosed = (sf & 8) != 0;
                    seg.NormalizedPosition = norm;
                    seg.startTime = 0f;
                    seg.transform.localPosition = p;
                    seg.transform.localRotation = new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
                    LightRefreshJoint?.Invoke(seg, null);
                }
            }
            catch (Exception ex)
            {
                VehicleTraffic.Warn("light apply", "MachinePartsSync light trailer apply: " + ex.Message);
            }
            finally
            {
                _applyingLight = false;
            }
        }

        /// <summary>Panel operations of the light trailer: stream its state from this PC (see LightTick).</summary>
        [HarmonyPatch]
        private static class LightTrailerOpPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                // ProtruseDown(): the panel button (ProtruseDown(bool force) also runs every frame on every PC while a
                // broken hydraulic lowers the mast; that needs no stream).
                foreach (string n in new[] { "EngineOnOff", "StartStanding", "ProtruseUp", "ProtruseStop", "ProtruseDown", "ReflectorsUp", "ReflectorsStop", "ReflectorsDown" })
                {
                    MethodInfo mi = AccessTools.DeclaredMethod(typeof(MobileLightTrailer), n, Type.EmptyTypes);
                    if (mi != null) yield return mi;
                }
                MethodInfo start = AccessTools.DeclaredMethod(typeof(LightSegment), nameof(LightSegment.startRotate), new[] { typeof(int) });
                if (start != null) yield return start;
                MethodInfo stop = AccessTools.DeclaredMethod(typeof(LightSegment), nameof(LightSegment.stopRotate), Type.EmptyTypes);
                if (stop != null) yield return stop;
            }

            private static bool Prepare()
            {
                foreach (var _ in TargetMethods()) return true;
                Plugin.Log.LogWarning("MachinePartsSync: light trailer methods not found, its panel is not synced");
                return false;
            }

            private static void Postfix(MonoBehaviour __instance)
            {
                if (_applyingLight || !Net.Active) return;
                if (__instance is MobileLightTrailer t) MarkLight(t);
                else if (__instance is LightSegment seg && seg.trailer != null) MarkLight(seg.trailer);
            }
        }

        /// <summary>Sender time at which this remote machine is drawn right now.</summary>
        private static double RenderTime(Remote rem)
        {
            if (MachineSync.TryGetRenderTime(rem.Id, out double t)) return t;
            return Time.unscaledTimeAsDouble - rem.Offset - FallbackDelay;
        }

        /// <summary>Plays the buffered part snapshots back at the body's render time; releases stale machines.</summary>
        private static void ApplyRemotes()
        {
            if (Remotes.Count == 0) return;
            List<int> release = null;
            MachineController local = LocalDriven();
            float now = Time.unscaledTime;

            foreach (var kv in Remotes)
            {
                Remote rem = kv.Value;
                if (rem.Machine == null || rem.Machine == local ||
                    (now - rem.LastUpdate > ReleaseAfter && !MachineSync.IsRemoteDriven(kv.Key)))
                {
                    (release ??= new List<int>()).Add(kv.Key);
                    continue;
                }
                double tr = RenderTime(rem);

                foreach (var e in rem.Layout.Floats)
                {
                    if (e.BN == 0) continue;
                    try { SetValue(e, SampleFloat(e, tr)); }
                    catch (Exception ex)
                    {
                        e.BN = 0;
                        VehicleTraffic.Warn("parts value", "MachinePartsSync value: " + ex.Message);
                    }
                }

                foreach (var e in rem.Layout.Poses)
                {
                    if (e.BN == 0 || e.T == null) continue;
                    SamplePose(e, tr, out Vector3 p, out Quaternion q);
                    e.T.localPosition = p;
                    e.T.localRotation = q;
                    e.HasCur = true;
                    e.CurP = p;
                    e.CurR = q;
                }

                SampleDrive(rem, tr);
            }

            if (release != null)
                foreach (int id in release) Release(id);
        }

        /// <summary>Index i with BT[i] &lt;= tr &lt; BT[i+1] after dropping passed samples; interpolation factor in s.</summary>
        private static int Locate(double[] bt, ref int bn, double tr, out float s)
        {
            int drop = 0;
            while (bn - drop >= 2 && bt[drop + 1] <= tr) drop++;
            if (drop > 0)
            {
                Array.Copy(bt, drop, bt, 0, bn - drop);
                bn -= drop;
            }
            s = 0f;
            if (bn < 2 || tr <= bt[0]) return drop; // hold the oldest (start) or the newest (late packets)
            double dt = bt[1] - bt[0];
            s = dt > 0.0 ? Mathf.Clamp01((float)((tr - bt[0]) / dt)) : 1f;
            return drop;
        }

        private static float SampleFloat(FloatEntry e, double tr)
        {
            int drop = Locate(e.BT, ref e.BN, tr, out float s);
            if (drop > 0) Array.Copy(e.BV, drop, e.BV, 0, e.BN);
            float a = e.BV[0];
            if (e.BN < 2 || s <= 0f) return a;
            float b = e.BV[1];
            // A jump (wrap, reset, arm swap) is not interpolated through.
            if (Mathf.Abs(b - a) > 90f) return s < 0.5f ? a : b;
            return Mathf.Lerp(a, b, s);
        }

        private static void SamplePose(PoseEntry e, double tr, out Vector3 p, out Quaternion q)
        {
            int drop = Locate(e.BT, ref e.BN, tr, out float s);
            if (drop > 0)
            {
                Array.Copy(e.BP, drop, e.BP, 0, e.BN);
                Array.Copy(e.BR, drop, e.BR, 0, e.BN);
            }
            p = e.BP[0];
            q = e.BR[0];
            if (e.BN < 2 || s <= 0f) return;
            p = Vector3.Lerp(p, e.BP[1], s);
            q = Quaternion.Slerp(q, e.BR[1], s);
        }

        private static void SampleDrive(Remote rem, double tr)
        {
            if (rem.DriveN == 0) return;
            int drop = 0;
            while (rem.DriveN - drop >= 2 && rem.Drive[drop + 1].T <= tr) drop++;
            for (int i = 0; i < drop; i++)
            {
                DriveSnap first = rem.Drive[0];
                Array.Copy(rem.Drive, 1, rem.Drive, 0, BufN - 1);
                rem.Drive[BufN - 1] = first;
                rem.DriveN--;
            }
            DriveSnap a = rem.Drive[0];
            DriveSnap b = rem.DriveN >= 2 ? rem.Drive[1] : a;
            float s = 0f;
            if (b != a && tr > a.T && b.T > a.T) s = Mathf.Clamp01((float)((tr - a.T) / (b.T - a.T)));

            // Discrete state from the snapshot already reached; continuous values interpolated.
            DriveSnap cur = s >= 1f ? b : a;
            rem.HasDrive = true;
            rem.Engine = (cur.Flags & 1) != 0;
            rem.LightsOn = (cur.Flags & 2) != 0;
            rem.LongLights = (cur.Flags & 4) != 0;
            rem.Handbrake = (cur.Flags & 8) != 0;
            rem.Reverse = (cur.Flags & 16) != 0;
            rem.SteeringMode = cur.SteeringMode;
            rem.Gear = cur.Gear;
            rem.HasBelt = (cur.Flags & 32) != 0;
            rem.BeltOn = (cur.Flags & 64) != 0;
            rem.BeltSpeed = cur.BeltSpeed;
            rem.Rpm01 = Mathf.Lerp(a.Rpm01, b.Rpm01, s);
            rem.Load = Mathf.Lerp(a.Load, b.Load, s);
            rem.Steer = Mathf.Lerp(a.Steer, b.Steer, s);
            rem.Accel = Mathf.Lerp(a.Accel, b.Accel, s);
            rem.Brake = Mathf.Lerp(a.Brake, b.Brake, s);
            rem.TrackL = Mathf.Lerp(a.TrackL, b.TrackL, s);
            rem.TrackR = Mathf.Lerp(a.TrackR, b.TrackR, s);

            int wc = a.WheelCount;
            if (rem.WheelAng.Length != wc)
            {
                rem.WheelAng = new float[wc];
                rem.WheelSteer = new float[wc];
                MapWheels(rem);
            }
            bool both = b.WheelCount == wc;
            for (int i = 0; i < wc; i++)
            {
                rem.WheelAng[i] = both ? Mathf.Lerp(a.WheelAng[i], b.WheelAng[i], s) : a.WheelAng[i];
                rem.WheelSteer[i] = both ? Mathf.Lerp(a.WheelSteer[i], b.WheelSteer[i], s) : a.WheelSteer[i];
            }

            // Driver cab vs operator cab ("Machine Mode", MySteeringMode, applied by ApplyDriveState): the game moves its
            // seated driver model to the other seat only on the toggling PC (MachineController.Update ->
            // WorkerPositionChanger.SetWorkerPosition). Do the same here when the driver's mode changes. Left as is after
            // the drive, like the machine's steering mode (the game repositions it only on the next toggle).
            if (cur.SteeringMode != rem.WorkerMode && cur.SteeringMode <= (byte)SteeringMode.Operator)
            {
                rem.WorkerMode = cur.SteeringMode;
                try
                {
                    if (rem.Machine.WorkerPerson != null && rem.Machine.WorkerPerson.TryGetComponent<WorkerPositionChanger>(out var seat))
                        seat.SetWorkerPosition(isDriverMode: cur.SteeringMode == (byte)SteeringMode.Driver);
                }
                catch (Exception ex) { VehicleTraffic.Warn("parts worker seat", "MachinePartsSync driver seat: " + ex.Message); }
            }

            // Engine follows the driver (local fuel/battery/damage stops are blocked for remote-driven
            // machines, see StopEnginePatch). Retry at most once a second.
            MachineController m = rem.Machine;
            if (rem.Engine != m.IsEngineStarted && Time.unscaledTime >= rem.NextEngineFix)
            {
                rem.NextEngineFix = Time.unscaledTime + 1f;
                SetEngine(m, rem.Engine);
            }

            // Conveyor on/off and speed (the belt's processing on this PC is ContainerSync's: it only shows
            // what the driver's PC simulates). Kept after the drive, like the game keeps a running belt.
            FrankensteinBelt belt = rem.Layout.Belt;
            if (rem.HasBelt && belt != null)
            {
                if (belt.IsEnabled != rem.BeltOn) belt.IsEnabled = rem.BeltOn;
                if (rem.BeltSpeed > 0f && !float.IsNaN(rem.BeltSpeed) && !float.IsInfinity(rem.BeltSpeed) && belt.SpeedMultiplier != rem.BeltSpeed)
                {
                    // SetBeltSpeedMultiplier also redraws the cab HUD icon: only for a passenger sitting in it.
                    if (m.IsControlled()) belt.SetBeltSpeedMultiplier(rem.BeltSpeed);
                    else belt.SpeedMultiplier = rem.BeltSpeed;
                }
            }
        }

        private static void Release(int id)
        {
            if (!Remotes.TryGetValue(id, out Remote rem)) return;
            Remotes.Remove(id);
            if (rem.Machine != null) ByMachine.Remove(rem.Machine);
            else PurgeByMachine();
            Wheels(rem, remove: true);

            foreach (var e in rem.Layout.Floats)
            {
                if (e.Anim != null) FrozenAnimJoints.Remove(e.Anim);
                if (e.Kind == FloatKind.ArmInput && e.Arm != null)
                {
                    try { e.Arm.AxisValue = 0f; ArmSound(e, 0f); } catch (Exception) { /* emitter destroyed */ }
                }
                e.BN = 0;
            }
            foreach (var e in rem.Layout.Poses)
            {
                if (e.MadeKinematic && e.Rb != null)
                {
                    e.Rb.isKinematic = e.WasKinematic;
                    if (!e.Rb.isKinematic)
                    {
                        e.Rb.velocity = Vector3.zero;
                        e.Rb.angularVelocity = Vector3.zero;
                    }
                }
                e.MadeKinematic = false;
                e.HasCur = false;
                e.BN = 0;
            }
        }

        private static void ReleaseAll()
        {
            foreach (int id in new List<int>(Remotes.Keys)) Release(id);
        }

        private static void PurgeByMachine()
        {
            var dead = new List<MachineController>();
            foreach (var kv in ByMachine) if (kv.Key == null) dead.Add(kv.Key);
            foreach (var d in dead) ByMachine.Remove(d);
        }

        private static void PurgeDestroyed()
        {
            if (Time.unscaledTime < _nextPurge) return;
            _nextPurge = Time.unscaledTime + 5f;
            var dead = new List<MachineController>();
            foreach (var kv in Layouts) if (kv.Key == null) dead.Add(kv.Key);
            foreach (var d in dead) Layouts.Remove(d);
            dead.Clear();
            foreach (var kv in States) if (kv.Key == null) dead.Add(kv.Key);
            foreach (var d in dead) States.Remove(d);
            FrozenAnimJoints.RemoveWhere(a => a == null);
            var deadWheels = new List<VPWheelCollider>();
            foreach (var kv in WheelMap) if (kv.Key == null) deadWheels.Add(kv.Key);
            foreach (var d in deadWheels) WheelMap.Remove(d);
        }

        /// <summary>Writes the remote driver's inputs where the game reads them (cabin, lights, tracks).</summary>
        private static void ApplyDriveState(MachineController m, Remote rem)
        {
            if (!rem.HasDrive) return;
            SetSteer?.Invoke(m, rem.Steer);
            SetAccelerate?.Invoke(m, rem.Accel);
            SetBrake?.Invoke(m, rem.Brake);
            SetHandbrake?.Invoke(m, rem.Handbrake);
            SetTryingReverse?.Invoke(m, rem.Reverse); // SoundController.ReverseBeepControl reads it in this Update
            SetGear?.Invoke(m, rem.Gear);
            SetSteeringMode?.Invoke(m, (SteeringMode)rem.SteeringMode);
            if (m.Lights != null)
            {
                m.Lights.SwitchedOn = rem.LightsOn;
                m.Lights.LongLightsOn = rem.LongLights;
            }
            if (m is TrackMachineController t)
            {
                SetTrackL?.Invoke(t, rem.TrackL);
                SetTrackR?.Invoke(t, rem.TrackR);
            }
        }

        // ------------------------------------------------------------------ machine state (fuel, durability)

        private class StateInfo
        {
            public int ListCount = -1;
            public readonly List<KeyValuePair<ushort, CheckAndRepair>> Items = new List<KeyValuePair<ushort, CheckAndRepair>>();
            public readonly Dictionary<ushort, CheckAndRepair> ByHash = new Dictionary<ushort, CheckAndRepair>();
            public float LastFuel = -1f;
            public float[] LastDur = new float[0];
            public float FrameFuel = -1f; // remote-driven machine: fuel seen on the previous frame (see RemoteFuelWatch)
        }

        private static readonly Dictionary<MachineController, StateInfo> States = new Dictionary<MachineController, StateInfo>();
        private static float _nextState, _nextScan;
        private static bool _applyingState;

        /// <summary>Wear items of a machine keyed by hierarchy path (same on both PCs); rebuilt when the list changes.</summary>
        private static StateInfo GetState(MachineController m)
        {
            if (!States.TryGetValue(m, out StateInfo s)) States[m] = s = new StateInfo();
            List<CheckAndRepair> list = m.Repair != null && m.Repair.Enabled ? m.Repair._WearingItems : null;
            int count = list?.Count ?? 0;
            if (count == s.ListCount) return s;
            s.ListCount = count;
            s.Items.Clear();
            s.ByHash.Clear();
            var ambiguous = new HashSet<ushort>();
            if (list != null)
                foreach (CheckAndRepair c in list)
                {
                    if (c == null || !c.transform.IsChildOf(m.transform)) continue; // detached parts are loose objects
                    string p = RelPath(c.transform, m.transform);
                    if (p.Contains("(Clone)")) continue;
                    ushort h = Hash16(p + "|" + c.WearingCondition);
                    if (ambiguous.Contains(h)) continue;
                    if (s.ByHash.ContainsKey(h)) { s.ByHash.Remove(h); ambiguous.Add(h); continue; }
                    s.ByHash[h] = c;
                }
            foreach (var kv in s.ByHash) s.Items.Add(new KeyValuePair<ushort, CheckAndRepair>(kv.Key, kv.Value));
            s.LastDur = new float[s.Items.Count];
            for (int i = 0; i < s.LastDur.Length; i++) s.LastDur[i] = -1f;
            return s;
        }

        private static float FuelOf(MachineController m) =>
            m.Fuel != null && m.Fuel.Enabled ? m.Fuel.TankPct : -1f;

        private static void StateTick()
        {
            float now = Time.unscaledTime;
            if (now >= _nextState)
            {
                _nextState = now + StateInterval;
                // The driver's PC simulates fuel use and wear of the machine it drives: it is authoritative.
                MachineController m = LocalDriven();
                int id = MachineSync.IdOf(m);
                if (m != null && id >= 0)
                {
                    try { SendState(m, id, force: false, only: null, reliable: false, driver: true); }
                    catch (Exception ex) { VehicleTraffic.Warn("parts state", "MachinePartsSync state: " + ex.Message); }
                }
            }

            if (now < _nextScan) return;
            _nextScan = now + ScanInterval;
            // Machines nobody drives: publish increases (refuel, repair, battery swap) done here on foot.
            // Passive decreases (idle fuel burn, battery drain) are simulated alike on every PC.
            MachineController driven = LocalMachine();
            foreach (var kv in MachineSync.AllIds)
            {
                MachineController m = kv.Key;
                if (m == null || m == driven || MachineSync.IsRemoteDriven(kv.Value)) continue;
                try
                {
                    StateInfo s = GetState(m);
                    float fuel = FuelOf(m);
                    bool up = s.LastFuel >= 0f && fuel > s.LastFuel + 0.005f;
                    for (int i = 0; i < s.Items.Count && !up; i++)
                    {
                        CheckAndRepair c = s.Items[i].Value;
                        if (c != null && s.LastDur[i] >= 0f && c.Durability > s.LastDur[i] + 0.005f) up = true;
                    }
                    if (up) SendState(m, kv.Value, force: false, only: null, reliable: true);
                    else Remember(m, s);
                }
                catch (Exception ex) { VehicleTraffic.Warn("parts state scan", "MachinePartsSync state scan: " + ex.Message); }
            }
        }

        private static readonly List<MachineController> RemoteDrivenTmp = new List<MachineController>();

        /// <summary>
        /// Every frame, machines another player drives: fuel added here in one go (fuel bought through the pump GUI,
        /// FuelStationGUI.Submit into the machine; the host's worker refuel, MachineController.WorkerRefuel) is not
        /// simulated on the driver's PC, whose 2 Hz state would overwrite it. Sent at once, forced (the driver applies
        /// it). A pistol that fills the tank continuously is attached on every PC (ObjectSync), so the driver's PC
        /// fills it itself; its per-frame steps stay far below the threshold.
        /// </summary>
        private static void RemoteFuelWatch()
        {
            if (MachineSync.CopyRemoteDriven(RemoteDrivenTmp) == 0) return;
            foreach (MachineController m in RemoteDrivenTmp)
            {
                float fuel = FuelOf(m);
                if (fuel < 0f) continue;
                StateInfo s = GetState(m);
                if (s.FrameFuel >= 0f && fuel > s.FrameFuel + 0.02f)
                {
                    int id = MachineSync.IdOf(m);
                    if (id >= 0)
                    {
                        try { SendFuel(m, id); }
                        catch (Exception ex) { VehicleTraffic.Warn("parts fuel", "MachinePartsSync fuel: " + ex.Message); }
                    }
                }
                s.FrameFuel = fuel;
            }
            RemoteDrivenTmp.Clear();
        }

        /// <summary>Forced fuel-only state (no wear items): applied on every PC, also by the driver.</summary>
        private static void SendFuel(MachineController m, int id)
        {
            if (!Net.Active || _applyingState) return;
            float fuel = FuelOf(m);
            if (fuel < 0f) return;
            BinaryWriter w = VehicleTraffic.Begin(Msg.MachinePose);
            w.Write(MachineSync.SubState);
            w.Write(MachineSync.Epoch);
            w.Write(id);
            w.Write(Net.LocalId);
            w.Write((byte)1); // force
            w.Write((ushort)Mathf.RoundToInt(Mathf.Clamp01(fuel) * 65534f));
            w.Write((byte)0); // no items
            VehicleTraffic.Send(VehicleTraffic.Parts, reliable: true);
            Plugin.Log.LogInfo($"MachinePartsSync: fuel added to {m.gameObject.name} (#{id}), driven by another player, sent");
        }

        private static void Remember(MachineController m, StateInfo s)
        {
            s.LastFuel = FuelOf(m);
            s.FrameFuel = s.LastFuel;
            for (int i = 0; i < s.Items.Count; i++)
                s.LastDur[i] = s.Items[i].Value != null ? s.Items[i].Value.Durability : -1f;
        }

        /// <summary>
        /// Sub message -3: id, sender LocalId, flags (1 = force: apply even on the driver's PC, 2 = sent by
        /// the driver), fuel u16, items (hash, durability u16). At most 250 items: 1023 bytes, under the
        /// 1200 byte unreliable limit.
        /// </summary>
        private static void SendState(MachineController m, int id, bool force, CheckAndRepair only, bool reliable, bool driver = false, bool final = false)
        {
            if (!Net.Active || _applyingState) return;
            StateInfo s = GetState(m);
            BinaryWriter w = VehicleTraffic.Begin(Msg.MachinePose);
            w.Write(MachineSync.SubState);
            w.Write(MachineSync.Epoch);
            w.Write(id);
            w.Write(Net.LocalId);
            w.Write((byte)((force ? 1 : 0) | (driver ? 2 : 0) | (final ? 4 : 0)));
            float fuel = only != null ? -1f : FuelOf(m);
            w.Write(fuel < 0f ? ushort.MaxValue : (ushort)Mathf.RoundToInt(Mathf.Clamp01(fuel) * 65534f));
            int n = 0;
            foreach (var kv in s.Items) if (kv.Value != null && (only == null || kv.Value == only)) n++;
            n = Mathf.Min(n, 250);
            w.Write((byte)n);
            int written = 0;
            foreach (var kv in s.Items)
            {
                if (written >= n) break;
                if (kv.Value == null || (only != null && kv.Value != only)) continue;
                w.Write(kv.Key);
                w.Write((ushort)Mathf.RoundToInt(Mathf.Clamp01(kv.Value.Durability) * 65535f));
                written++;
            }
            VehicleTraffic.Send(VehicleTraffic.Parts, reliable);
            Remember(m, s);
        }

        /// <summary>
        /// The local driver got out (MachineSync sends the exit pose): the final fuel and wear go reliably,
        /// so a lost 2 Hz update does not leave the others with slightly different values.
        /// </summary>
        internal static void SendFinalState(MachineController m, int id)
        {
            if (m == null || id < 0 || !MachineSync.WorldReady()) return;
            try { SendState(m, id, force: false, only: null, reliable: true, driver: true, final: true); }
            catch (Exception ex) { Plugin.Log.LogWarning("MachinePartsSync final state: " + ex.Message); }
        }

        internal static void ReceiveState(BinaryReader r)
        {
            int id = r.ReadInt32();
            ulong sender = r.ReadUInt64();
            byte sflags = r.ReadByte();
            bool force = (sflags & 1) != 0;
            bool fromDriver = (sflags & 2) != 0;
            bool final = (sflags & 4) != 0;
            ushort fuelQ = r.ReadUInt16();
            int n = r.ReadByte();
            MachineController m = MachineSync.Get(id);
            if (m == null || !MachineSync.WorldReady() || sender == Net.LocalId) return;
            // Driving updates only from the driver this PC shows (a player that lost a double claim
            // may have sent one or two); refuels/repairs on foot come from anyone.
            ulong driver = MachineSync.DriverOf(id);
            if (fromDriver && !force && driver != 0 && driver != sender) return;
            // A 2 Hz update of that driver overtaken by its reliable exit (and final state): stale, ignore it.
            if (fromDriver && !force && !final && MachineSync.ExitSeen(id, sender)) return;
            // We drive it ourselves: our simulation is authoritative (a forced repair / fuel purchase still applies).
            bool authoritative = m == LocalDriven();
            StateInfo s = GetState(m);
            _applyingState = true;
            try
            {
                if ((!authoritative || force) && fuelQ != ushort.MaxValue && m.Fuel != null && m.Fuel.Enabled)
                {
                    float f = fuelQ / 65534f;
                    // A refuel on foot of a parked machine runs on every PC (the fuel pistol attach is replayed), so
                    // both PCs publish their rising value: taking the other's older, lower value stepped the tank back
                    // and forth while filling. On-foot updates only ever raise it (passive burn runs alike here).
                    bool onFootLower = !fromDriver && !force && f < m.Fuel.TankPct;
                    if (!onFootLower && Mathf.Abs(m.Fuel.TankPct - f) > 0.0005f) m.Fuel.TankPct = f;
                }
                for (int i = 0; i < n; i++)
                {
                    ushort h = r.ReadUInt16();
                    float d = r.ReadUInt16() / 65535f;
                    if (authoritative && !force) continue;
                    if (!s.ByHash.TryGetValue(h, out CheckAndRepair c) || c == null) continue;
                    // SetCurrLifetime fires gameplay events and state changes: only on a real difference.
                    float cur = c.Durability;
                    bool destroyedThere = d <= 0f;
                    if (Mathf.Abs(cur - d) > 0.002f || destroyedThere != c.IsDestroyed) c.Durability = d;
                }
                Remember(m, s);
            }
            finally
            {
                _applyingState = false;
            }
        }

        // ------------------------------------------------------------------ Harmony patches
        // Applied by Plugin's PatchAll. Each Prepare() skips its patch if the target is missing,
        // so a game update cannot break the other patches of the mod.

        /// <summary>
        /// MachineController.Update runs the Controllers (Shifters, Lights, Sound) and, for track machines,
        /// is called right before MoveWheels/MoveTracks: set the remote inputs just before they are read.
        /// </summary>
        [HarmonyPatch]
        private static class MachineUpdatePatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(MachineController), "Update");
            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(MachineController __instance)
            {
                if (ByMachine.Count == 0 || !ByMachine.TryGetValue(__instance, out Remote rem)) return;
                try { ApplyDriveState(__instance, rem); }
                catch (Exception ex) { rem.HasDrive = false; VehicleTraffic.Warn("parts drive", "MachinePartsSync drive: " + ex.Message); }
            }
        }

        /// <summary>
        /// While a remote player drives a machine its engine follows the driver: local stops (fuel, battery,
        /// EngineStopDestroyed, which would also show HUD hints to a player who is not even driving) are
        /// skipped. The driver's PC decides and its engine state is applied by SetEngine.
        /// </summary>
        [HarmonyPatch]
        private static class StopEnginePatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(MachineController), nameof(MachineController.StopEngine));
            private static bool Prepare() => TargetMethod() != null;

            private static bool Prefix(MachineController __instance, ref bool __result)
            {
                if (_applyingEngine || ByMachine.Count == 0 || !ByMachine.ContainsKey(__instance)) return true;
                __result = false;
                return false;
            }
        }

        /// <summary>A repair done here (also on a machine someone else drives) is sent at once and forced.</summary>
        [HarmonyPatch]
        private static class RepairPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(CheckAndRepair), nameof(CheckAndRepair.Repair));
            private static bool Prepare() => TargetMethod() != null;

            private static void Postfix(CheckAndRepair __instance)
            {
                if (_applyingState || !Net.Active || GameState.IsLoading || !MachineSync.WorldReady()) return;
                try
                {
                    MachineController m = __instance._myMachine != null ? __instance._myMachine : __instance.GetComponentInParent<MachineController>();
                    int id = MachineSync.IdOf(m);
                    if (id < 0) return;
                    SendState(m, id, force: true, only: __instance, reliable: true);
                }
                catch (Exception ex) { Plugin.Log.LogWarning("MachinePartsSync repair: " + ex.Message); }
            }
        }

        private static readonly AccessTools.FieldRef<Backhoe, MachineController> BackhoeParent =
            SafeFieldRef<Backhoe, MachineController>("Parent");

        /// <summary>
        /// A passenger's PC reports the machine as controlled, so JointsController runs the arms from the
        /// (blocked, zero) local input: Backhoe.Update would stop the replayed hydraulic sound and
        /// DoFixedUpdate would drive the joints. While the machine's parts come from the network, skip both.
        /// </summary>
        [HarmonyPatch]
        private static class BackhoeLocalInputPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var u = AccessTools.Method(typeof(Backhoe), nameof(Backhoe.Update));
                var f = AccessTools.Method(typeof(Backhoe), nameof(Backhoe.DoFixedUpdate));
                if (u != null) yield return u;
                if (f != null) yield return f;
            }

            private static bool Prepare()
            {
                if (BackhoeParent == null) return false;
                foreach (var _ in TargetMethods()) return true;
                return false;
            }

            private static bool Prefix(Backhoe __instance)
            {
                if (ByMachine.Count == 0) return true;
                MachineController p = BackhoeParent(__instance);
                return p == null || !ByMachine.ContainsKey(p);
            }
        }

        /// <summary>Engine sound and RPM gauge read GetEngineRPM01/GetEngineLoad (overridden per machine).</summary>
        [HarmonyPatch]
        private static class EngineRpmPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var result = new List<MethodBase>();
                foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(MachineController).Assembly))
                {
                    if (t == null || !typeof(MachineController).IsAssignableFrom(t)) continue;
                    foreach (string name in new[] { "GetEngineRPM01", "GetEngineLoad" })
                    {
                        MethodInfo mi = AccessTools.DeclaredMethod(t, name, Type.EmptyTypes);
                        if (mi != null && !mi.IsAbstract && mi.ReturnType == typeof(float)) result.Add(mi);
                    }
                }
                return result;
            }

            private static bool Prepare()
            {
                foreach (var _ in TargetMethods()) return true;
                return false;
            }

            private static void Postfix(MachineController __instance, MethodBase __originalMethod, ref float __result)
            {
                if (ByMachine.Count == 0 || !ByMachine.TryGetValue(__instance, out Remote rem) || !rem.HasDrive) return;
                __result = __originalMethod.Name == "GetEngineLoad" ? rem.Load : rem.Rpm01;
            }
        }

        /// <summary>Wheel visuals: replace spin and steer angle right before VPP draws the wheel.</summary>
        [HarmonyPatch]
        private static class WheelVisualPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(VPWheelCollider), "UpdateVisualWheel");
            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(VPWheelCollider __instance)
            {
                if (WheelMap.Count == 0 || !WheelMap.TryGetValue(__instance, out var entry)) return;
                Remote rem = entry.Key;
                int i = entry.Value;
                if (i >= rem.WheelAng.Length) return;
                __instance.angularVelocity = rem.WheelAng[i];
                __instance.steerAngle = rem.WheelSteer[i];
            }
        }

        /// <summary>Stops AnimatedJoint's own speed/spring integration while the angle comes from the network.</summary>
        [HarmonyPatch]
        private static class AnimatedJointPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(AnimatedJoint), "FixedUpdate");
            private static bool Prepare() => TargetMethod() != null;

            private static bool Prefix(AnimatedJoint __instance) =>
                FrozenAnimJoints.Count == 0 || !FrozenAnimJoints.Contains(__instance);
        }

        // ------------------------------------------------------------------ LateUpdate re-apply

        private static LateRunner _late;

        /// <summary>Writes the sampled part poses again after every game Update ran this frame.</summary>
        private static void LateApply()
        {
            if (Remotes.Count == 0) return;
            foreach (var kv in Remotes)
                foreach (var e in kv.Value.Layout.Poses)
                {
                    if (!e.HasCur || e.T == null) continue;
                    if (e.T.localPosition != e.CurP) e.T.localPosition = e.CurP;
                    if (e.T.localRotation != e.CurR) e.T.localRotation = e.CurR;
                }
        }

        private sealed class LateRunner : MonoBehaviour
        {
            internal static LateRunner Create()
            {
                var go = new GameObject("GMSCoop.MachinePartsLate") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                return go.AddComponent<LateRunner>();
            }

            private void LateUpdate()
            {
                try { LateApply(); }
                catch (Exception ex) { VehicleTraffic.Warn("parts late", "MachinePartsSync late: " + ex.Message); }
            }
        }
    }
}
