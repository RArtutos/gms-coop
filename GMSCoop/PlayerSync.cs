using System;
using System.Collections.Generic;
using System.IO;
using GoldDigger;
using HarmonyLib;
using SaveSystem;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Sends the local player's feet position, yaw/pitch, velocity, movement flags and held tool
    /// 20 times per second and shows remote players as an animated humanoid (Avatar).
    /// Walking body = Singleton&lt;RigidbodyFPSWalker&gt;; while driving, Player.GetControlledMachine().
    ///
    /// Playback: snapshot interpolation (PoseBuffer in SyncCore.cs, tested offline with lossy, reordered
    /// and jittery input) on SyncClock (SyncLogic.cs, the rules of ObjectSync, so a held object and the
    /// holder's hand play back with the same delay; double seconds for long sessions): adaptive 100..300 ms delay, never jumps or runs backwards
    /// on a stall; Hermite interpolation using the sent velocity, at most 250 ms extrapolation, a late
    /// packet's correction is blended in over ~0.1 s instead of jumping, snaps on teleports or walk/drive
    /// switches.
    /// Unreliable: lost packets are interpolated over, duplicates and reordered packets are sorted in or
    /// dropped by their sender stamp.
    ///
    /// Packet (Msg.PlayerPose): id u64, stamp f64 (Time.unscaledTimeAsDouble), feet 3xf32, yaw f32, pitch i8, vel 3xf16,
    /// state u8 (0 walk, 1 drive; bit 0x80 = name follows), flags u8 (see Flag*), tool u8
    /// (PlayerTool.EPlayerToolType), machineId i32, [name string: once per second, not 20 times].
    /// </summary>
    internal static class PlayerSync
    {
        private const float SendInterval = 0.05f;
        private static float _nextSend;

        private const byte StateWalk = 0;
        private const byte StateDrive = 1;
        private const byte StateHasName = 0x80;
        private const float NameInterval = 1f;
        private static float _nextName;

        // One writer for every pose packet (only the final byte[] for the transport is allocated).
        private static readonly MemoryStream PoseMs = new MemoryStream(64);
        private static readonly BinaryWriter PoseW = new BinaryWriter(PoseMs);

        internal const byte FlagCrouch = 1;
        internal const byte FlagGrounded = 2;
        internal const byte FlagSprint = 4;
        internal const byte FlagUsingTool = 8;
        internal const byte FlagCarrying = 16;
        internal const byte FlagLadder = 32;

        private const float SnapDistance = PoseBuffer.SnapDistance;
        private const float DriverSeatHeight = 1.2f; // sender puts the driver this high above the machine origin

        private class Ghost
        {
            public Avatar Avatar;
            public ulong Via; // transport peer the pose arrived from (host relays for clients)
            public float LastPacket; // Time.unscaledTime of the last pose
            public double Newest = double.NegativeInfinity; // newest sender stamp seen (reordered packets are older)
            public bool Driving;
            public int MachineId = -1;
            public readonly PoseBuffer Poses = new PoseBuffer(); // snapshot interpolation (SyncCore)
        }

        // Keyed by the player's LocalId written inside the pose packet.
        private static readonly Dictionary<ulong, Ghost> Ghosts = new Dictionary<ulong, Ghost>();

        // ------------------------------------------------------------------ game state readers
        // Player.CurrentToolType / CurrentTool and RigidbodyFPSWalker.IsCrouching / IsOnGround are
        // internal or private in Assembly-CSharp.
        private static readonly AccessTools.FieldRef<Player, PlayerTool.EPlayerToolType> ToolTypeRef =
            TryRef<Player, PlayerTool.EPlayerToolType>("CurrentToolType");
        private static readonly AccessTools.FieldRef<Player, PlayerTool> ToolRef = TryRef<Player, PlayerTool>("CurrentTool");
        private static readonly AccessTools.FieldRef<RigidbodyFPSWalker, bool> CrouchRef = TryRef<RigidbodyFPSWalker, bool>("IsCrouching");
        private static readonly AccessTools.FieldRef<RigidbodyFPSWalker, bool> GroundRef = TryRef<RigidbodyFPSWalker, bool>("IsOnGround");
        private static readonly AccessTools.FieldRef<RigidbodyFPSWalker, bool> LadderRef = TryRef<RigidbodyFPSWalker, bool>("IsOnLadder");

        // Net.LocalName asks Steam and builds a string: refreshed every 10 s instead of 20 times a second.
        private static string _name;
        private static float _nameAt = -100f;

        private static string LocalName()
        {
            if (_name == null || Time.unscaledTime - _nameAt > 10f)
            {
                _name = Net.LocalName;
                _nameAt = Time.unscaledTime;
            }
            return _name;
        }
        private static bool _buttonsBroken;

        private static AccessTools.FieldRef<T, F> TryRef<T, F>(string field)
        {
            try { return AccessTools.FieldRefAccess<T, F>(field); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Player sync: field {typeof(T).Name}.{field} not found ({ex.Message})");
                return null;
            }
        }

        /// <summary>True while the local player is actually working with the tool in hand.</summary>
        private static bool ToolInUse(PlayerTool tool)
        {
            if (tool == null) return false;
            // Shovels: the game's own dig/throw animation runs while not in the idle state.
            Animator anim = tool is Shovel s ? s.animator : tool is ShovelSmall ss ? ss.MyAnimator : null;
            if (anim != null && anim.isActiveAndEnabled)
            {
                AnimatorStateInfo st = anim.GetCurrentAnimatorStateInfo(0);
                if (!st.IsName("Ilde") && !st.IsName("Idle")) return true; // "Ilde" is the game's typo
            }
            // Every tool: Player.Update calls MouseLeft/MouseRight while these are held.
            if (_buttonsBroken) return false;
            try { return InputManager.GetButton("ToolAction1") || InputManager.GetButton("ToolAction2"); }
            catch (Exception) { _buttonsBroken = true; return false; }
        }

        // ------------------------------------------------------------------ tick

        // A client is not told when ANOTHER client leaves (only the host sees the transport drop), so a
        // ghost that stops sending is hidden after a short silence and removed after a long one.
        private const float GhostHideAfter = 3f;
        private const float GhostRemoveAfter = 20f;
        private static readonly List<ulong> _deadGhosts = new List<ulong>();

        private static readonly SubsystemGuard GGhosts = new SubsystemGuard("Player sync: remote players"),
            GSend = new SubsystemGuard("Player sync: send"), GRecv = new SubsystemGuard("Player sync: receive");

        /// <summary>Every frame. Remote players and the own pose are isolated: an error in one never stops the other.</summary>
        internal static void Tick()
        {
            try { UnfreezeOwnBody(); } catch (Exception ex) { GSend.Fail(ex); }
            if (Ghosts.Count > 0)
            {
                try
                {
                    ExpireGhosts();
                    UpdateGhosts();
                }
                catch (Exception ex) { GGhosts.Fail(ex); }
            }
            try { SendPose(); }
            catch (Exception ex) { GSend.Fail(ex); }
        }

        private static float _nextBodyCheck;

        /// <summary>
        /// The game never makes the walking player's body kinematic. Older mod versions synced that body as an
        /// object and froze it (kinematic) while following the other player's stream, and a save made in such
        /// a session stores the frozen body: every later load of that save left the player unable to move.
        /// Runs in and out of a session, so such a save heals on its own.
        /// </summary>
        private static void UnfreezeOwnBody()
        {
            if (Time.unscaledTime < _nextBodyCheck) return;
            _nextBodyCheck = Time.unscaledTime + 0.5f;
            if (GameState.IsLoading || !Singleton<RigidbodyFPSWalker>.IsInstanced()) return;
            if (Singleton<Player>.IsInstanced() && Singleton<Player>.Instance.GetControlledMachine() != null) return;
            RigidbodyFPSWalker walker = Singleton<RigidbodyFPSWalker>.Instance;
            var rb = walker.GetComponent<Rigidbody>();
            // Only a kinematic body is the old frozen state. Collisions off alone is the game's own fly
            // cheat (SuperManMode, RigidbodyFPSWalker.UpdateFlying): leave that untouched.
            if (rb == null || !rb.isKinematic) return;
            rb.isKinematic = false;
            rb.detectCollisions = true;
            Plugin.Log.LogWarning("Player body was frozen (kinematic, from an old save or mod version): unfrozen");
        }

        private static void SendPose()
        {
            // Unscaled time: the game sets timeScale = 0 in pause menus, which would stop sending.
            if (!Net.Active || Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + SendInterval;
            if (!Net.WorldLive) return; // client still in its own pre-join world, or loading

            Vector3 feet, vel;
            float yaw, pitch = 0f;
            byte state, flags = 0, tool = 0;
            int machineId = -1;
            Player player = Singleton<Player>.IsInstanced() ? Singleton<Player>.Instance : null;
            MachineController driving = player != null ? player.GetControlledMachine() : null;
            Camera cam = Camera.main;
            if (driving != null)
            {
                // Seat position is not known; sit the avatar on top of the vehicle's origin.
                feet = driving.transform.position + driving.transform.up * DriverSeatHeight;
                yaw = driving.transform.eulerAngles.y;
                Rigidbody mrb = driving.MyRigidbody;
                vel = mrb != null ? mrb.velocity * Time.timeScale : Vector3.zero;
                state = StateDrive;
                machineId = MachineSync.IdOf(driving);
            }
            else if (Singleton<RigidbodyFPSWalker>.IsInstanced())
            {
                RigidbodyFPSWalker walker = Singleton<RigidbodyFPSWalker>.Instance;
                Transform t = walker.transform;
                var col = t.GetComponent<Collider>();
                feet = col != null ? new Vector3(t.position.x, col.bounds.min.y, t.position.z) : t.position - Vector3.up;
                yaw = cam != null ? cam.transform.eulerAngles.y : t.eulerAngles.y;
                if (cam != null) pitch = Mathf.DeltaAngle(0f, cam.transform.eulerAngles.x);
                var rb = t.GetComponent<Rigidbody>();
                // Velocity in real time: a paused game (menu, shop, fast travel prompt: timeScale 0) keeps the
                // body's velocity but does not move, so the avatar must stand still; slow motion moves slower.
                vel = rb != null ? rb.velocity * Time.timeScale : Vector3.zero;
                state = StateWalk;
                if (CrouchRef != null && CrouchRef(walker)) flags |= FlagCrouch;
                bool ladder = LadderRef != null && LadderRef(walker);
                if (ladder) flags |= FlagLadder | FlagGrounded;
                else if (GroundRef == null || GroundRef(walker)) flags |= FlagGrounded;
                if (walker.IsRunning) flags |= FlagSprint;

                if (player != null && ToolTypeRef != null)
                {
                    var type = ToolTypeRef(player);
                    if (type != PlayerTool.EPlayerToolType.NONE)
                    {
                        tool = (byte)type;
                        // HideTool does not clear CurrentTool, only CurrentToolType.
                        if (ToolRef != null && ToolInUse(ToolRef(player))) flags |= FlagUsingTool;
                    }
                }
                if (Singleton<GravityGun>.IsInstanced() && Singleton<GravityGun>.Instance.PickedObject != null) flags |= FlagCarrying;
            }
            else return;

            bool withName = Time.unscaledTime >= _nextName;
            if (withName) { _nextName = Time.unscaledTime + NameInterval; state |= StateHasName; }
            MemoryStream ms = PoseMs;
            ms.SetLength(0);
            ms.WriteByte((byte)Msg.PlayerPose);
            BinaryWriter w = PoseW;
            w.Write(Net.LocalId);
            w.Write(Time.unscaledTimeAsDouble); // f64: float seconds judder after a few hours (PoseBuffer)
            w.Write(feet.x); w.Write(feet.y); w.Write(feet.z);
            w.Write(yaw);
            w.Write((sbyte)Mathf.Clamp(Mathf.RoundToInt(pitch), -90, 90));
            w.Write(Mathf.FloatToHalf(Mathf.Clamp(vel.x, -500f, 500f)));
            w.Write(Mathf.FloatToHalf(Mathf.Clamp(vel.y, -500f, 500f)));
            w.Write(Mathf.FloatToHalf(Mathf.Clamp(vel.z, -500f, 500f)));
            w.Write(state);
            w.Write(flags);
            w.Write(tool);
            w.Write(machineId);
            if (withName) w.Write(AvatarModels.PackName(LocalName())); // name + chosen character
            w.Flush();
            TrafficC.Broadcast(TrafficC.Cat.Player, ms.ToArray(), false);
        }

        private static void ExpireGhosts()
        {
            float now = Time.unscaledTime;
            _deadGhosts.Clear();
            foreach (var kv in Ghosts)
            {
                Ghost g = kv.Value;
                float silent = now - g.LastPacket;
                // Avatar object destroyed from outside (it is DontDestroyOnLoad, but a scene cleanup or
                // another mod may still remove it): drop the ghost, the next pose builds a new one.
                if (!g.Avatar.Alive || silent > GhostRemoveAfter) _deadGhosts.Add(kv.Key);
                else if (silent > GhostHideAfter && g.Avatar.Root.activeSelf) g.Avatar.Root.SetActive(false);
            }
            foreach (ulong id in _deadGhosts)
            {
                bool alive = Ghosts[id].Avatar.Alive;
                Ghosts[id].Avatar.Destroy();
                Ghosts.Remove(id);
                Plugin.Log.LogInfo(alive ? "Remote player gone (no data for " + GhostRemoveAfter + " s): " + id : "Remote player avatar was destroyed, rebuilt with the next pose: " + id);
            }
        }

        private static void UpdateGhosts()
        {
            double now = Time.unscaledTimeAsDouble;
            float dt = Time.unscaledDeltaTime;
            foreach (var kv in Ghosts)
            {
                Ghost g = kv.Value;
                if (!g.Avatar.Alive) continue; // removed by the next ExpireGhosts
                if (!g.Poses.Advance(now, dt, out Vector3 pos, out float yaw, out PoseSnap cur)) continue;
                Transform t = g.Avatar.Root.transform;
                Quaternion rot = Quaternion.Euler(0f, yaw, 0f);

                if (g.Driving && PassengerSync.TryGetPassengerSeat(kv.Key, out Vector3 seatPos, out Quaternion seatRot))
                {
                    // Passenger: seated next to the driver, follows the cab.
                    pos = seatPos;
                    rot = seatRot;
                }
                else if (g.Driving && MachineSync.IsRemoteDriven(g.MachineId) && MachineSync.Get(g.MachineId) is MachineController m)
                {
                    // Follow the machine as MachineSync renders it (its own playback delay may differ).
                    pos = m.transform.position + m.transform.up * DriverSeatHeight;
                    rot = Quaternion.Euler(0f, m.transform.eulerAngles.y, 0f);
                }

                // The sampled pose is already smooth (late packets are blended in by PoseBuffer); teleports
                // cleared the buffer, so setting it directly snaps exactly then. A big jump also resets the walk cycle.
                if ((t.position - pos).sqrMagnitude > SnapDistance * SnapDistance) g.Avatar.ResetMotion();
                t.SetPositionAndRotation(pos, rot);

                g.Avatar.Animate(g.Driving, cur.Vel, cur.Flags, cur.Tool, cur.Pitch);
            }
        }

        // ------------------------------------------------------------------ receive

        /// <summary>A bad pose is logged and dropped here: the host's Net layer still relays it.</summary>
        internal static void Receive(ulong from, BinaryReader r)
        {
            try { ReceivePose(from, r); }
            catch (Exception ex) { GRecv.Fail(ex); }
        }

        private static void ReceivePose(ulong from, BinaryReader r)
        {
            TrafficC.Recv(TrafficC.Cat.Player, (int)r.BaseStream.Length + 1);
            ulong id = r.ReadUInt64();
            var s = new PoseSnap { Time = r.ReadDouble() };
            s.Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            s.Yaw = r.ReadSingle();
            s.Pitch = r.ReadSByte();
            s.Vel = new Vector3(Mathf.HalfToFloat(r.ReadUInt16()), Mathf.HalfToFloat(r.ReadUInt16()), Mathf.HalfToFloat(r.ReadUInt16()));
            byte state = r.ReadByte();
            s.Flags = r.ReadByte();
            s.Tool = r.ReadByte();
            int machineId = r.ReadInt32();
            string name = (state & StateHasName) != 0 ? r.ReadString() : null;
            string character = null;
            if (name != null) name = AvatarModels.UnpackName(name, out character);
            state &= 0x7F;
            if (id == Net.LocalId || !Net.WorldLive) return;
            if (!IsFinite(s.Pos) || !IsFinite(s.Vel) || double.IsNaN(s.Time) || double.IsInfinity(s.Time) || float.IsNaN(s.Yaw) || float.IsInfinity(s.Yaw)) return;

            if (Ghosts.TryGetValue(id, out var old) && !old.Avatar.Alive)
            {
                Ghosts.Remove(id); // its avatar was destroyed from outside: build a new one
            }
            if (!Ghosts.TryGetValue(id, out var g))
            {
                g = new Ghost { Avatar = new Avatar(name ?? "...") };
                g.Avatar.SetColor(PlayerColors.For(id));
                g.Avatar.Root.transform.position = s.Pos;
                Ghosts[id] = g;
                Plugin.Log.LogInfo($"Remote player visible: {name ?? id.ToString("x")} at {s.Pos}");
                StepAside(s.Pos, s.Yaw, (state & 0x7F) == StateDrive);
            }
            if (name != null) { g.Avatar.SetName(name); g.Avatar.SetCharacter(character); }
            g.Via = from;
            g.LastPacket = Time.unscaledTime;

            bool driving = state == StateDrive;
            bool switched = driving != g.Driving || machineId != g.MachineId;
            // An unreliable packet from before a quick get-in/get-out that arrives late must not switch the
            // avatar back (and clear its buffer twice). A much older stamp means the sender restarted.
            if (switched && s.Time < g.Newest && g.Newest - s.Time < 5f) return;
            if (s.Time > g.Newest || g.Newest - s.Time >= 5f) g.Newest = s.Time;
            // Got in/out of a vehicle: the pose source changes, do not glide between them (next sample snaps).
            if (switched) g.Poses.Clear();
            g.Driving = driving;
            g.MachineId = machineId;
            g.Poses.Add(s, Time.unscaledTimeAsDouble);

            // In a vehicle with the game's own seated driver model: hide our avatar, the
            // machine sync turns that driver model on instead.
            bool hide = g.Driving && MachineSync.HasDriverModel(machineId) && !PassengerSync.IsRemotePassenger(id);
            if (g.Avatar.Root.activeSelf == hide) g.Avatar.Root.SetActive(!hide);
        }

        private static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
            !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        /// <summary>Removes every ghost whose poses came through this transport peer.</summary>
        /// <summary>
        /// A joining player loads the host's save, so it spawns exactly where the host stands: both players
        /// stood inside each other, and each one's avatar filled the other's view and seemed to follow every
        /// move. When the first pose of a nearby walking player arrives, step 2.5 m to its side (onto the
        /// ground found by a raycast). Only the client moves; the host never does.
        /// </summary>
        private static void StepAside(Vector3 other, float otherYaw, bool otherDriving)
        {
            if (Net.IsHost || otherDriving || GameState.IsLoading || !Singleton<RigidbodyFPSWalker>.IsInstanced()) return;
            if (Singleton<Player>.IsInstanced() && Singleton<Player>.Instance.GetControlledMachine() != null) return;
            RigidbodyFPSWalker walker = Singleton<RigidbodyFPSWalker>.Instance;
            Vector3 mine = walker.transform.position;
            Vector3 flat = mine - other;
            flat.y = 0f;
            if (flat.sqrMagnitude > 1.5f * 1.5f || Mathf.Abs(mine.y - other.y) > 2f) return;

            Vector3 right = Quaternion.Euler(0f, otherYaw, 0f) * Vector3.right;
            foreach (Vector3 dir in new[] { right, -right, Quaternion.Euler(0f, otherYaw, 0f) * Vector3.back })
            {
                Vector3 probe = other + dir * 2.5f;
                // Clear space for a standing player at the spot, then the ground under it.
                if (Physics.CheckCapsule(probe + Vector3.up * 0.6f, probe + Vector3.up * 1.6f, 0.35f, ~0, QueryTriggerInteraction.Ignore)) continue;
                if (!Physics.Raycast(probe + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore)) continue;
                Vector3 target = hit.point + Vector3.up * (mine.y - other.y > 0f ? mine.y - other.y : 0f) + Vector3.up * 0.05f;
                var rb = walker.GetComponent<Rigidbody>();
                if (rb != null) { rb.velocity = Vector3.zero; rb.position = target; }
                walker.transform.position = target;
                Plugin.Log.LogInfo($"Stepped aside from the other player: {mine} -> {target}");
                return;
            }
        }

        internal static void RemoveGhostsFrom(ulong peer)
        {
            var dead = new List<ulong>();
            foreach (var kv in Ghosts)
                if (kv.Value.Via == peer) dead.Add(kv.Key);
            foreach (var id in dead)
            {
                Ghosts[id].Avatar.Destroy();
                Ghosts.Remove(id);
            }
        }

        /// <summary>A player left (host broadcast, keyed by the LocalId used in pose packets): drop its avatar now.</summary>
        internal static void RemovePlayer(ulong localId)
        {
            if (!Ghosts.TryGetValue(localId, out Ghost g)) return;
            g.Avatar.Destroy();
            Ghosts.Remove(localId);
            Plugin.Log.LogInfo("Remote player left: " + localId);
        }

        internal static void RemoveAllGhosts()
        {
            foreach (var g in Ghosts.Values) g.Avatar.Destroy();
            Ghosts.Clear();
        }
    }
}
