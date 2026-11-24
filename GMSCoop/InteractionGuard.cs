using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GoldDigger;
using HarmonyLib;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Never leaves the local player locked by a game interaction that another player broke.
    ///
    /// Some uses run a coroutine that sets InputManager.AllInputBlocked, waits, then clears it:
    /// emptying the shovel into the starter hog pan's dirt box or a bucket (ShovelToolHolder:
    /// shovel parented to the holder, input blocked 1 s), the small shovel (ShovelSmallToolHolder)
    /// and the magnetite separator bucket animation. Unity stops a coroutine for good when its
    /// GameObject is deactivated, and the cleanup never runs: input stays blocked forever (no walk,
    /// no actions, no pause menu) while the rest of the game keeps running.
    ///
    /// In single player that cannot happen (the input block stops the player from breaking it). In
    /// co-op the other player can: picking the dirt box off the hog pan replicates here as
    /// Holdable.Detach -> HogPan.OnDetachMudBox -> HogPanDirtBox.OnDetach, which deactivates the
    /// box's ShovelToolHolder unconditionally, killing the local Unscrewing coroutine (and the
    /// local shovel, parented under that inactive holder, vanishes with it).
    ///
    /// The coroutines are wrapped: when one dies (its object went inactive or was destroyed, or it
    /// threw) or runs far too long, the guard does what its tail would have done: unblock input,
    /// give the shovel back to the player, end the use action.
    ///
    /// Same class of problem, also handled here:
    ///  - Getting into / out of a machine (EnterVehicleController.CycleToCameras / CycleCamerasOut /
    ///    InterpolateCameraToPath, coroutines on the machine): input blocked, player object hidden and
    ///    the camera detached until the animation ends. Another player selling / returning / resetting
    ///    (PurchaseSync) or storing (MachineSync) that machine meanwhile destroys or deactivates it.
    ///    Prevented by BeforeRemoteRemove (the local player is taken out first); a dead or stuck
    ///    animation is finished by the watchdog (back to walking, camera on the player, input free).
    ///  - Gold panning (PanningSystem + PanningCamera: player hidden, camera parented to the tub, released
    ///    only by PanningSystem.Update). GoldPan.Attach starts the panning view for whoever runs it, so
    ///    another player's attach replicated by ObjectSync put the LOCAL player into that player's tub.
    ///    A remote attach is now applied without the panning view, and the tub / pan another player is
    ///    panning with cannot be grabbed here (that would move the other player's view). A tub removed
    ///    while the local player pans in it ends the panning first.
    ///  - Shovel holders on an object another player removes (sold, destroyed): the shovel is given back
    ///    before the holder (and the shovel parented under it) is destroyed.
    ///  - Input blocked by game code that ran for another player's packet (BucketHolderMagnetiteSeparator
    ///    blocks input in its attach): restored right after the packet (AfterRemoteApply).
    ///  - A ladder that went inactive / far away under the player (machine stored or moved by another
    ///    player: no OnTriggerExit for a disabled trigger): the player is let off it.
    ///  - Last resort: input blocked for 20 s while the player walks freely with no menu, laptop, bug report,
    ///    loading, pause or guarded interaction running: released (logged).
    /// </summary>
    internal static class InteractionGuard
    {
        private enum Kind { Shovel, SmallShovel, Separator, Vehicle }

        private sealed class Token
        {
            public Kind Kind;
            public MonoBehaviour Owner;
            public EnterVehicleController Vehicle; // Kind.Vehicle
            public bool Scaled;  // timed with Time.time (the game animation uses scaled time)
            public float Start;
            public float Timeout;
            public bool Done;    // ran to the end
            public bool Failed;  // threw
            public bool Aborted; // recovered by the guard: the wrapper stops at its next step
        }

        private static readonly List<Token> Tokens = new List<Token>();

        private static readonly AccessTools.FieldRef<ToolUseComponent, PlayerTool> MyToolRef =
            SafeRef<ToolUseComponent, PlayerTool>("_myTool");
        private static readonly AccessTools.FieldRef<Controller, MachineController> MachineRef =
            SafeRef<Controller, MachineController>("MyMachineController");
        private static readonly AccessTools.FieldRef<PanningSystem, bool> IsPanningRef =
            SafeRef<PanningSystem, bool>("_IsPannning");
        private static readonly AccessTools.FieldRef<PanningSystem, float> PadSensRef =
            SafeRef<PanningSystem, float>("_PadSensivity");
        private static readonly AccessTools.FieldRef<GoldPan, PanningSystem> PanSystemRef =
            SafeRef<GoldPan, PanningSystem>("_panningsystem");
        private static readonly AccessTools.FieldRef<RigidbodyFPSWalker, Ladder> LadderRef =
            SafeRef<RigidbodyFPSWalker, Ladder>("MyLadder");

        private static AccessTools.FieldRef<TC, TF> SafeRef<TC, TF>(string name) where TC : class
        {
            try { return AccessTools.FieldRefAccess<TC, TF>(name); }
            catch (Exception) { return null; }
        }

        /// <summary>Wraps a game coroutine so the guard knows when it finished, died or threw.</summary>
        private static IEnumerator Wrap(IEnumerator inner, Token t)
        {
            while (true)
            {
                if (t.Aborted) yield break;
                bool more;
                try { more = inner.MoveNext(); }
                catch (Exception ex)
                {
                    t.Failed = true;
                    Plugin.Log.LogWarning($"Interaction guard: {t.Kind} coroutine threw: {ex.Message}");
                    yield break;
                }
                if (!more) break;
                yield return inner.Current;
            }
            t.Done = true;
        }

        /// <summary>
        /// <paramref name="replace"/>: a new interaction of the same holder replaces the old one (the old
        /// coroutine is gone). Machine animations nest (one starts the next on the same machine): kept.
        /// </summary>
        private static Token Track(Kind kind, MonoBehaviour owner, float timeout, bool replace = true, bool scaled = false)
        {
            if (replace)
                for (int i = Tokens.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(Tokens[i].Owner, owner)) { Tokens[i].Aborted = true; Tokens.RemoveAt(i); }
            var t = new Token
            {
                Kind = kind, Owner = owner, Scaled = scaled, Timeout = timeout,
                Start = scaled ? Time.time : Time.unscaledTime
            };
            Tokens.Add(t);
            return t;
        }

        private static bool InputBlocked =>
            Singleton<InputManager>.IsInstanced() && Singleton<InputManager>.Instance.AllInputBlocked;

        private static void FreeInput()
        {
            if (Singleton<InputManager>.IsInstanced()) Singleton<InputManager>.Instance.AllInputBlocked = false;
        }

        /// <summary>For ObjectSync: input state before a remote packet is applied.</summary>
        internal static bool InputBlockedNow => InputBlocked;

        private static float _nextErrLog;

        private static void LogRare(string msg)
        {
            float now = Time.unscaledTime;
            if (now < _nextErrLog) return;
            _nextErrLog = now + 10f;
            Plugin.Log.LogWarning("Interaction guard: " + msg);
        }

        /// <summary>Called from Plugin.Update.</summary>
        internal static void Tick()
        {
            float now = Time.unscaledTime;
            for (int i = Tokens.Count - 1; i >= 0; i--)
            {
                if (i >= Tokens.Count) continue; // a recovery removed tokens
                Token t = Tokens[i];
                bool remove;
                try { remove = Check(t, now); }
                catch (Exception ex)
                {
                    remove = true;
                    Plugin.Log.LogWarning("Interaction guard: " + ex.Message);
                }
                if (remove) Tokens.Remove(t);
            }
            if (!Net.Active) { _blockedSince = -1f; return; }
            try { LadderWatchdog(now); } catch (Exception ex) { LogRare("ladder check failed: " + ex.Message); }
            try { InputWatchdog(now); } catch (Exception ex) { LogRare("input check failed: " + ex.Message); }
        }

        /// <summary>True when the token is finished with (normally or recovered).</summary>
        private static bool Check(Token t, float now)
        {
            if (t.Done || t.Aborted) return true;
            // The separator is not wrapped (see PatchSeparator): it is over once input is free again.
            if (t.Kind == Kind.Separator && !InputBlocked) return true;
            bool dead = t.Failed || t.Owner == null || !t.Owner.gameObject.activeInHierarchy;
            bool late = (t.Scaled ? Time.time : now) - t.Start > t.Timeout;
            if (!dead && !late) return false;
            t.Aborted = true;
            Plugin.Log.LogWarning($"Interaction guard: {Describe(t.Kind)} on {(t.Owner != null ? t.Owner.name : "a destroyed object")} " +
                                  $"{(t.Failed ? "threw" : dead ? "was interrupted (object disabled or removed, e.g. taken by another player)" : "timed out")}" +
                                  ", unlocking the local player");
            Recover(t);
            return true;
        }

        private static string Describe(Kind k) => k == Kind.Vehicle ? "Machine enter/exit animation" : k + " use";

        /// <summary>Does what the end of the interrupted coroutine would have done.</summary>
        private static void Recover(Token t)
        {
            try
            {
                if (t.Kind == Kind.Shovel) RecoverShovel(t.Owner as ShovelToolHolder);
                else if (t.Kind == Kind.SmallShovel) RecoverSmallShovel(t.Owner as ShovelSmallToolHolder);
                else if (t.Kind == Kind.Vehicle)
                {
                    AbortVehicle(t.Vehicle); // the nested animations of the same enter / exit
                    RecoverVehicle(t.Vehicle, t.Owner as MachineController);
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Interaction guard: recovery failed: " + ex.Message); }
            finally
            {
                // Always free the player, even if the rest could not be put back.
                FreeInput();
            }
            if (t.Kind == Kind.Shovel || t.Kind == Kind.SmallShovel)
            {
                try
                {
                    if (Singleton<GravityGun>.IsInstanced()) Singleton<GravityGun>.Instance.StopUsingObject();
                }
                catch (Exception ex) { Plugin.Log.LogWarning("Interaction guard: StopUsingObject failed: " + ex.Message); }
            }
        }

        private static PlayerTool ToolOf(ToolUseComponent c)
        {
            if (c == null || MyToolRef == null) return null;
            PlayerTool tool = MyToolRef(c);
            return tool != null ? tool : null;
        }

        private static bool LocallyPicked(PlayerTool tool) =>
            Singleton<ToolGun>.IsInstanced() && Singleton<ToolGun>.Instance.PickedObject == tool.gameObject;

        private static void RecoverShovel(ShovelToolHolder h)
        {
            if (h != null) h.isUnscrewing = false;
            Shovel shovel = ToolOf(h) as Shovel;
            if (shovel == null) return;
            if (shovel.DropCorutine != null)
            {
                // Its throw coroutine (hog pan path) died with it, half done: the dirt it still holds was
                // partly dropped already, so empty it (as the coroutine's end does) instead of duplicating.
                try { shovel.StopCoroutine(shovel.DropCorutine); } catch (Exception) { }
                shovel.DropCorutine = null;
                shovel.CurrentVolume = 0f;
                shovel.Minerals = new ResourceBag();
            }
            // Still parented to the (inactive) holder: put it back in the player's hands.
            if (!LocallyPicked(shovel) && (h == null || shovel.transform.IsChildOf(h.transform)))
                shovel.SnapToPlayer();
            if (shovel.gameObject.activeInHierarchy) shovel.SetDirtOnShovel(shovel.CurrentVolume > 0f);
        }

        private static void RecoverSmallShovel(ShovelSmallToolHolder h)
        {
            if (h != null) h.IsWorking = false;
            ShovelSmall shovel = ToolOf(h) as ShovelSmall;
            if (shovel == null) return;
            bool snapped = h != null && h.ShovelSnap != null && shovel.transform.IsChildOf(h.ShovelSnap);
            if (shovel.IsAnimating || snapped)
            {
                // Its WaitForAnim coroutine ends with SnapToPlayer + IsAnimating = false.
                shovel.IsAnimating = false;
                if (!LocallyPicked(shovel)) shovel.SnapToPlayer();
            }
        }

        // =====================================================================================
        // Machines (enter / exit)
        // =====================================================================================

        private static void AbortVehicle(EnterVehicleController ev)
        {
            if (ev == null) return;
            foreach (Token t in Tokens)
                if (t.Kind == Kind.Vehicle && ReferenceEquals(t.Vehicle, ev)) t.Aborted = true;
        }

        private static bool InTransition(PlayerState s) =>
            s == PlayerState.GettingInMachine || s == PlayerState.GettingOutMachine;

        /// <summary>The local player sits in, or is getting into / out of, <paramref name="m"/>.</summary>
        private static bool LocallyInvolved(MachineController m)
        {
            if (m == null) return false;
            if (m.IsControlled()) return true;
            EnterVehicleController ev = m.EnterVehicle;
            return ev != null && ev.Enabled && ev.MyPlayerState != PlayerState.FreeWalk && ev.MyPlayerState != PlayerState.Undefined;
        }

        /// <summary>
        /// A machine enter / exit animation died or hung: finish it the way ChangeState would (back to
        /// walking), or get the player out of a machine that is gone.
        /// </summary>
        private static void RecoverVehicle(EnterVehicleController ev, MachineController m)
        {
            if (ev == null) return;
            PlayerState st = ev.MyPlayerState;
            bool alive = m != null && m.gameObject.activeInHierarchy;
            if (!InTransition(st) && (alive || st == PlayerState.FreeWalk || st == PlayerState.Undefined)) return;
            if (alive)
            {
                try { ev.SetFreeWalkState(); }
                catch (Exception ex) { Plugin.Log.LogWarning("Interaction guard: SetFreeWalkState failed: " + ex.Message); }
            }
            EnsureWalking(ev, m);
        }

        /// <summary>
        /// Takes the local player out of <paramref name="m"/> right now (in it, getting in or getting out),
        /// before another player's action removes or deactivates the machine.
        /// </summary>
        internal static void LeaveMachineNow(MachineController m)
        {
            if (m == null || !LocallyInvolved(m)) return;
            EnterVehicleController ev = m.EnterVehicle;
            bool alive = m.gameObject.activeInHierarchy;
            PlayerState before = ev != null ? ev.MyPlayerState : PlayerState.Undefined;
            try
            {
                if (ev == null || !ev.Enabled)
                {
                    if (m.IsControlled()) { m.ExitMachine(); m.SetControlled(false); }
                }
                else
                {
                    // The game's own exit (MachineController.ExitMachine, exit events) when really inside.
                    if (alive && m.IsControlled()) ev.ExitMachine(noAnim: true);
                    // Its camera animation must not run on (or be cut half way) once the player walks.
                    AbortVehicle(ev);
                    if (alive && ev.MyPlayerState != PlayerState.FreeWalk) ev.SetFreeWalkState();
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Interaction guard: leaving " + m.name + " failed: " + ex.Message); }
            EnsureWalking(ev, m);
            FreeInput();
            Plugin.Log.LogInfo($"Interaction guard: left {m.name} ({before}) because another player removed or stored it");
        }

        /// <summary>
        /// Restores the plain walking state (what SetPlayerState(FreeWalk) does) without needing the machine,
        /// for whatever is still off: player object hidden, camera not on the player, mouse look off.
        /// </summary>
        private static void EnsureWalking(EnterVehicleController ev, MachineController m)
        {
            if (!Singleton<Player>.IsInstanced()) return;
            Player p = Singleton<Player>.Instance;
            if (ev != null && ev.MyPlayerState != PlayerState.FreeWalk) ev.MyPlayerState = PlayerState.FreeWalk;
            MachineController cm = p.GetControlledMachine();
            if (!ReferenceEquals(cm, null) && (cm == null || ReferenceEquals(cm, m)))
            {
                if (cm != null) { cm.SetControlled(false); cm.IsDriven = false; cm.IsOperated = false; }
                else p.SetControlledMachine(null); // destroyed
            }
            if (!p.gameObject.activeSelf) p.gameObject.SetActive(true);
            Camera cam = BlitGlobals.cameraMain;
            if (cam == null) { Plugin.Log.LogWarning("Interaction guard: no main camera to give back to the player"); return; }
            Transform ct = cam.transform;
            if (ct.parent != p.transform)
            {
                ct.SetParent(p.transform, false);
                ct.localPosition = new Vector3(0f, 1.4f, 0f);
                ct.localRotation = Quaternion.identity;
                p.CameraSettingsFor = null;
                var cabin = cam.GetComponent<CabinMouseLook>();
                if (cabin != null) cabin.enabled = false;
                var orbit = cam.GetComponent<MouseOrbit>();
                if (orbit != null) orbit.enabled = false;
                var follow = cam.GetComponent<SmoothFollowDriver>();
                if (follow != null) follow.enabled = false;
                var lowPass = cam.GetComponent<AudioLowPassFilter>();
                if (lowPass != null) lowPass.enabled = false;
            }
            MouseLook look = cam.GetComponent<MouseLook>();
            if (look != null && !look.enabled)
            {
                look.enabled = true;
                look.Clear();
            }
        }

        // =====================================================================================
        // Gold panning
        // =====================================================================================

        /// <summary>Pans another player put into a panning tub here (attached without the panning view).</summary>
        private static readonly HashSet<GoldPan> RemotePans = new HashSet<GoldPan>();

        private static bool LocallyPanningAt(PanningSystem ps)
        {
            if (ps == null) return false;
            Camera cam = BlitGlobals.cameraMain;
            if (cam != null && cam.transform.IsChildOf(ps.transform)) return true;
            return IsPanningRef != null && IsPanningRef(ps);
        }

        /// <summary>PanningSystem.OnDestroy's cleanup, also for the camera move at the start (not panning yet).</summary>
        private static void ForceEndPanning(PanningSystem ps)
        {
            Player p = Singleton<Player>.IsInstanced() ? Singleton<Player>.Instance : null;
            bool inTake = p != null && p.InGoldTake;
            PanningCamera pc = ps.GetComponent<PanningCamera>();
            if (pc != null)
            {
                pc.StopAllCoroutines();
                pc.IsCameraMoving = false;
                pc.OnExit(); // player shown, camera back on the player, mouse look on
            }
            if (IsPanningRef != null) IsPanningRef(ps) = false;
            if (SaveSystem.Singleton<SaveSystem.CheckPointManagerBase>.IsInstanced())
                SaveSystem.Singleton<SaveSystem.CheckPointManagerBase>.Instance.DontAllowSave(cannotsave: false, "GoldPanning");
            InputManager.EnableMap("Walk");
            if (inTake && PadSensRef != null && PadSensRef(ps) > 0f) InputManager.PadSensivity = PadSensRef(ps);
            if (p != null)
            {
                p.InGoldPanning = false;
                p.InGoldTake = false;
            }
            RigidbodyFPSWalker.GetControlScheme(forceReload: true, !InputManager.IsKeyboard());
            EnsureWalking(null, null);
            Plugin.Log.LogInfo("Interaction guard: panning ended because another player removed " + ps.name);
        }

        private static void AttachPanInert(GoldPan pan, Holder h, PanningSystem ps)
        {
            if (pan.IsAttached()) return;
            // What Holdable.Attach does (the holder already set ObjectInHolder), minus the panning view.
            pan.MyHolder = h;
            if (PanSystemRef != null) PanSystemRef(pan) = ps; // GoldPan.FixedUpdate reads it while attached
            ConfigurableJoint j = pan.gameObject.AddComponent<ConfigurableJoint>();
            j.connectedBody = h.MyRigidbody;
            j.angularXMotion = ConfigurableJointMotion.Locked;
            j.angularYMotion = ConfigurableJointMotion.Locked;
            j.angularZMotion = ConfigurableJointMotion.Locked;
            j.xMotion = ConfigurableJointMotion.Locked;
            j.yMotion = ConfigurableJointMotion.Locked;
            j.zMotion = ConfigurableJointMotion.Locked;
            j.breakForce = 5000000f;
            if (pan.MyBoxCollider != null) pan.MyBoxCollider.enabled = true;
            RemotePans.Add(pan);
            Plugin.Log.LogInfo($"Interaction guard: {pan.name} put into {ps.name} by another player, attached without the local panning view");
        }

        /// <summary>The object (a pan or a tub) is in use by another player who pans with it.</summary>
        private static bool UsedByRemotePanner(GameObject hit)
        {
            if (hit == null || RemotePans.Count == 0) return false;
            RemotePans.RemoveWhere(p => p == null || p.MyHolder == null);
            if (RemotePans.Count == 0) return false;
            GoldPan gp = hit.GetComponentInParent<GoldPan>();
            if (gp != null && RemotePans.Contains(gp)) return true;
            PanningSystem ps = hit.GetComponentInParent<PanningSystem>();
            if (ps == null) return false;
            foreach (GoldPan p in RemotePans)
                if (p.MyHolder.transform.IsChildOf(ps.transform)) return true;
            return false;
        }

        // =====================================================================================
        // Entry points for the sync systems
        // =====================================================================================

        /// <summary>
        /// Another player's action is about to destroy or deactivate <paramref name="go"/> (sold, returned,
        /// reset, stored, network destroy). Ends every local use of it first, while it still exists.
        /// </summary>
        internal static void BeforeRemoteRemove(GameObject go)
        {
            if (go == null || !Net.Active) return;
            Transform root = go.transform;
            try
            {
                foreach (MachineController m in go.GetComponentsInChildren<MachineController>(true))
                    if (LocallyInvolved(m)) LeaveMachineNow(m);
                foreach (PanningSystem ps in go.GetComponentsInChildren<PanningSystem>(true))
                    if (LocallyPanningAt(ps)) ForceEndPanning(ps);
                foreach (Token t in Tokens.ToArray())
                {
                    if (t.Done || t.Aborted || t.Kind == Kind.Vehicle || t.Owner == null || !t.Owner.transform.IsChildOf(root)) continue;
                    t.Aborted = true;
                    Plugin.Log.LogInfo($"Interaction guard: {Describe(t.Kind)} on {t.Owner.name} ended because another player removed {go.name}");
                    Recover(t); // gives the shovel back before it is destroyed with the holder
                }
                // Anything else that parented the camera under it: never let it be destroyed with it.
                Camera cam = BlitGlobals.cameraMain;
                if (cam != null && cam.transform.IsChildOf(root))
                {
                    Plugin.Log.LogWarning("Interaction guard: the camera was inside " + go.name + ", which another player removed; giving it back to the player");
                    EnsureWalking(null, null);
                    FreeInput();
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Interaction guard: before remove of " + go.name + ": " + ex.Message); }
        }

        /// <summary>
        /// After an ObjectSync packet: game code run for another player's action (holder attach of the
        /// magnetite separator, ...) must not leave the LOCAL player's input blocked.
        /// </summary>
        internal static void AfterRemoteApply(bool inputWasBlocked, byte sub)
        {
            if (inputWasBlocked || !InputBlocked) return;
            FreeInput();
            Plugin.Log.LogInfo($"Interaction guard: another player's action (object sync kind {sub}) blocked the local input, released it");
        }

        // =====================================================================================
        // Watchdogs (co-op only)
        // =====================================================================================

        private const float LadderMaxDistance = 3f;
        private static float _nextLadderCheck;

        /// <summary>
        /// RigidbodyFPSWalker leaves a ladder on Ladder.OnTriggerExit, which Unity never sends for a trigger
        /// that was disabled. A ladder on a machine another player stored or drove away keeps the player
        /// snapped to an invisible line.
        /// </summary>
        private static void LadderWatchdog(float now)
        {
            if (LadderRef == null || now < _nextLadderCheck || !Singleton<RigidbodyFPSWalker>.IsInstanced()) return;
            _nextLadderCheck = now + 0.5f;
            RigidbodyFPSWalker w = Singleton<RigidbodyFPSWalker>.Instance;
            Ladder l = LadderRef(w);
            if (ReferenceEquals(l, null)) return;
            string why = null;
            if (l == null) why = "was removed";
            else if (!l.gameObject.activeInHierarchy) why = "was disabled";
            else
            {
                BoxCollider bc = l.GetComponent<BoxCollider>();
                if (bc != null && !bc.enabled) why = "was disabled";
                else if (bc != null)
                {
                    Vector3 a = l.GetLadderBegin(), b = l.GetLadderEnd(), pos = w.transform.position;
                    Vector3 ab = b - a;
                    float k = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(pos - a, ab) / ab.sqrMagnitude) : 0f;
                    if ((a + ab * k - pos).sqrMagnitude > LadderMaxDistance * LadderMaxDistance) why = "moved away";
                }
            }
            if (why == null) return;
            w.ExitLadder();
            Plugin.Log.LogInfo($"Interaction guard: the ladder the player was on {why}, let the player off it");
        }

        private const float StuckInputTime = 20f;
        private static float _blockedSince = -1f;

        /// <summary>
        /// Input blocked for a long time while nothing that legitimately blocks it is going on: plain
        /// walking (player shown, camera on the player, no machine, no panning), no menu / map / journal,
        /// laptop, bug report, debug panel, environment GUI, pause, loading, mod prompt or guarded
        /// interaction. Every game use of AllInputBlocked outside those lasts a second or two.
        /// </summary>
        private static void InputWatchdog(float now)
        {
            if (!InputBlocked || Tokens.Count > 0 || !LooksLikeFreeWalk()) { _blockedSince = -1f; return; }
            if (_blockedSince < 0f) { _blockedSince = now; return; }
            if (now - _blockedSince < StuckInputTime) return;
            _blockedSince = -1f;
            FreeInput();
            Plugin.Log.LogWarning($"Interaction guard: input was blocked for {StuckInputTime:0} s with no menu, loading or interaction running, released it");
        }

        private static bool LooksLikeFreeWalk()
        {
            if (!Net.WorldLive || CoopUi.ModalOpen) return false;
            if (Time.timeScale <= 0f || (Singleton<PauseManager>.IsInstanced() && PauseManager.IsPaused())) return false;
            if (!Singleton<Player>.IsInstanced()) return false;
            Player p = Singleton<Player>.Instance;
            if (!p.gameObject.activeInHierarchy || p.GetControlledMachine() != null || p.InGoldPanning) return false;
            Camera cam = BlitGlobals.cameraMain;
            if (cam == null || cam.transform.parent != p.transform) return false;
            if (Singleton<CursorManager>.IsInstanced())
            {
                CursorManager c = Singleton<CursorManager>.Instance;
                if (c.Menu || c.LaptopEnabled || c.SRBugReport || c.SRDebugerVisible ||
                    (c.enviromantgatorGUI != null && c.enviromantgatorGUI.isGuiOn)) return false;
            }
            if (Singleton<HUD>.IsInstanced() && Singleton<HUD>.Instance.InLaptop) return false;
            if (Singleton<MenuManager>.IsInstanced())
            {
                MenuManager mm = Singleton<MenuManager>.Instance;
                if (mm.isShowing || (mm.MyCanvasGroup != null && mm.MyCanvasGroup.alpha > 0f)) return false;
            }
            return true;
        }

        // =====================================================================================
        // Harmony
        // =====================================================================================

        [HarmonyPatch]
        private static class PatchShovelHolder
        {
            private static MethodBase Target => AccessTools.DeclaredMethod(typeof(ShovelToolHolder), "Unscrewing");

            private static bool Prepare()
            {
                if (Target != null && MyToolRef != null) return true;
                Plugin.Log.LogWarning("Interaction guard: ShovelToolHolder.Unscrewing / _myTool not found, an interrupted shovel use may lock the player");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static void Postfix(ShovelToolHolder __instance, ref IEnumerator __result)
            {
                if (__result == null || !Net.Active) return;
                // Unscrewing is a fixed 1 s; 6 s means it is stuck.
                __result = Wrap(__result, Track(Kind.Shovel, __instance, 6f));
            }
        }

        [HarmonyPatch]
        private static class PatchSmallShovelHolder
        {
            private static MethodBase Target => AccessTools.DeclaredMethod(typeof(ShovelSmallToolHolder), "Unscrewing");

            private static bool Prepare()
            {
                if (Target != null && MyToolRef != null) return true;
                Plugin.Log.LogWarning("Interaction guard: ShovelSmallToolHolder.Unscrewing not found, an interrupted small shovel use may lock the player");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static void Postfix(ShovelSmallToolHolder __instance, ref IEnumerator __result)
            {
                if (__result == null || !Net.Active) return;
                // A couple of seconds plus the shovel animation.
                __result = Wrap(__result, Track(Kind.SmallShovel, __instance, 15f));
            }
        }

        // HogPanDirtBox.OnDetach deactivates the box's ShovelToolHolder even in the middle of the
        // local shovel use (HogPanDirtBox.Update itself waits for !isUnscrewing). In co-op the other
        // player can take the box off the hog pan at that moment: leave the holder alone while it is
        // working, Update hides it once the use has finished (the guard above is the safety net).
        [HarmonyPatch]
        private static class PatchDirtBoxDetach
        {
            private static MethodBase Target => AccessTools.DeclaredMethod(typeof(HogPanDirtBox), nameof(HogPanDirtBox.OnDetach));

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Interaction guard: HogPanDirtBox.OnDetach not found");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static void Prefix(HogPanDirtBox __instance, out ShovelToolHolder __state)
            {
                __state = null;
                ShovelToolHolder h = __instance.ShovelHolder;
                if (h != null && h.isUnscrewing)
                {
                    __state = h;
                    __instance.ShovelHolder = null; // OnDetach skips a missing holder
                }
            }

            private static void Finalizer(HogPanDirtBox __instance, ShovelToolHolder __state)
            {
                if (__state != null) __instance.ShovelHolder = __state;
            }
        }

        // The separator's RunAnimation is a tiny non-virtual iterator factory (may be inlined by the
        // JIT), so the guard hooks AddContentFromBucket, which blocks input and starts it.
        [HarmonyPatch]
        private static class PatchSeparator
        {
            private static MethodBase Target => AccessTools.DeclaredMethod(typeof(BucketHolderMagnetiteSeparator), nameof(BucketHolderMagnetiteSeparator.AddContentFromBucket));

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Interaction guard: BucketHolderMagnetiteSeparator.AddContentFromBucket not found");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static void Postfix(BucketHolderMagnetiteSeparator __instance)
            {
                if (!Net.Active) return;
                Track(Kind.Separator, __instance, 30f);
            }
        }

        // Machine enter / exit camera animations: coroutines on the machine that block input (and keep
        // the player hidden, camera detached) until they end. Wrapped like the shovel uses; nested ones
        // (CycleToCameras starts InterpolateCameraToPath on the same machine) are kept side by side.
        [HarmonyPatch]
        private static class PatchVehicleTransition
        {
            private static readonly string[] Names = { "CycleToCameras", "CycleCamerasOut", "InterpolateCameraToPath" };

            private static bool Prepare()
            {
                bool ok = MachineRef != null;
                foreach (string n in Names) ok &= AccessTools.DeclaredMethod(typeof(EnterVehicleController), n) != null;
                if (ok) return true;
                Plugin.Log.LogWarning("Interaction guard: EnterVehicleController animations not found, an interrupted machine enter/exit may lock the player");
                return false;
            }

            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string n in Names) yield return AccessTools.DeclaredMethod(typeof(EnterVehicleController), n);
            }

            private static void Postfix(EnterVehicleController __instance, ref IEnumerator __result)
            {
                if (__result == null || !Net.Active || __instance == null) return;
                MachineController m = MachineRef(__instance);
                if (m == null) return;
                // About 1 s of game time; 20 s means it is stuck (scaled: a paused game is not stuck).
                Token t = Track(Kind.Vehicle, m, 20f, replace: false, scaled: true);
                t.Vehicle = __instance;
                __result = Wrap(__result, t);
            }
        }

        // GoldPan.Attach starts the panning view (PanningSystem.StartPanning: player hidden, camera into the
        // tub, panning controls) for whoever runs it. Another player's pan going into a tub is replicated by
        // ObjectSync with Holder.AttachObject: attach it like a plain holdable instead.
        [HarmonyPatch]
        private static class PatchRemotePanAttach
        {
            private static MethodBase Target => AccessTools.DeclaredMethod(typeof(GoldPan), nameof(GoldPan.Attach), new[] { typeof(GameObject) });

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Interaction guard: GoldPan.Attach not found, another player's panning may move the local view into their tub");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static bool Prefix(GoldPan __instance, GameObject obj)
            {
                if (!Net.Active || !ObjectSync.ApplyingHolderChange || __instance == null || obj == null) return true;
                Holder h = obj.GetComponent<Holder>();
                PanningSystem ps = obj.GetComponentInParent<PanningSystem>();
                if (h == null || ps == null) return true;
                try { AttachPanInert(__instance, h, ps); }
                catch (Exception ex) { Plugin.Log.LogWarning("Interaction guard: remote pan attach failed: " + ex.Message); }
                return false;
            }
        }

        // The pan / tub another player is panning with: grabbing it here would pull it out of their tub
        // (their view is parented to the tub and their panning keeps the pan).
        [HarmonyPatch]
        private static class PatchPickRemotePanning
        {
            private static MethodBase Target => AccessTools.Method(typeof(GravityGun), "PickOutlinedObject");

            private static bool Prepare() => Target != null;

            private static MethodBase TargetMethod() => Target;

            private static bool Prefix(GravityGun __instance)
            {
                return !(Net.Active && UsedByRemotePanner(__instance.ObjectHit));
            }
        }
    }
}
