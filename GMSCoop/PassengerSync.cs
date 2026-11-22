// WIRING: PassengerSync.Tick() from Plugin.Update; sub-kind 4 of (Msg)12 is routed here by ObjectSync.Receive;
// PlayerSync uses IsRemotePassenger / TryGetPassengerSeat for the seated avatar.

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Passenger seat. The game has no passenger mode: entering any door makes the player the driver
    /// (EnterVehicleController.EnterVehicle -> SetPlayerState -> MachineController.SetControlled(true),
    /// IsDriven = true). If the machine is currently driven by another player (MachineSync applies
    /// remote poses), entering it here makes the local player a passenger instead:
    ///  - MachineSync already stops sending our poses for it (it is in its Remotes list);
    ///  - IsDriven is kept false, so MachineController.FixedUpdate reads no throttle/steer and the
    ///    handbrake toggle (gated by IsDriven) is ignored;
    ///  - engine on/off (ControlEngineStart) and the operator-mode toggle are blocked for that machine;
    ///  - every machine input the game reads through InputManager (drive axes, arms, lights, diff lock,
    ///    handbrake, engine, tools, machine mode, ...) reads as released, so lights/arms/tools of the
    ///    machine cannot be worked from the passenger seat; camera, menus and exiting still work;
    ///  - the cabin camera is shifted ~0.65 m to the passenger side (side of the PassengerDoor, else
    ///    opposite the driver door), and the game's driver model (WorkerPerson) stays visible;
    ///  - the other PCs are told (sub-kind 4) so they draw our avatar seated next to the driver.
    /// If the remote driver gets out (or is gone), we become the driver. If its packets only stop
    /// (lag) while other players are still heard, we stay a passenger for up to 8 s first; a driver that
    /// leaves the session is released at once (MachineSync), so we take the wheel immediately.
    /// </summary>
    internal static class PassengerSync
    {
        private const float SeatSideOffset = 0.65f; // metres to the right of the driver's eye point
        private const float SeatEyeHeight = 1.3f;    // Avatar seated eye height above its feet pivot
        private const float ResendInterval = 4f;
        private const float RemoteStateTimeout = 12f;

        private static MachineController _passengerOf;
        private static int _passengerId = -1;
        private static float _nextResend;

        private class RemotePassenger
        {
            public int MachineId;
            public MachineController Machine;
            public float LastUpdate;
        }

        // Keyed by the remote player's LocalId.
        private static readonly Dictionary<ulong, RemotePassenger> RemotePassengers = new Dictionary<ulong, RemotePassenger>();

        private static readonly AccessTools.FieldRef<Controller, MachineController> ControllerMachine =
            AccessTools.Field(typeof(Controller), "MyMachineController") != null
                ? AccessTools.FieldRefAccess<Controller, MachineController>("MyMachineController")
                : null;

        /// <summary>True if the local player sits in <paramref name="m"/> while someone else drives it.</summary>
        internal static bool IsPassengerIn(MachineController m)
        {
            if (m == null || !Net.Active || !m.IsControlled()) return false;
            if (_passengerOf != null && ReferenceEquals(m, _passengerOf)) return true; // incl. lag grace
            return MachineSync.IsRemoteDriven(m); // who drives a machine: MachineSync only
        }

        // Which side of the cab the passenger sits on (+1 = right of the driver's eye point), per machine.
        private static readonly Dictionary<MachineController, float> Sides = new Dictionary<MachineController, float>();

        private static float SeatSide(MachineController m)
        {
            if (Sides.TryGetValue(m, out float side)) return side;
            side = 1f;
            EnterVehicleController ev = m.EnterVehicle;
            if (ev != null && ev.CabinCamera != null)
            {
                Transform root = m.transform;
                float eye = root.InverseTransformPoint(ev.CabinCamera.transform.position).x;
                Transform pd = ev.PassengerDoor?.ShifterTransform;
                Transform dd = ev.Door?.ShifterTransform;
                if (pd != null && Mathf.Abs(root.InverseTransformPoint(pd.position).x - eye) > 0.1f)
                    side = Mathf.Sign(root.InverseTransformPoint(pd.position).x - eye);
                else if (dd != null && Mathf.Abs(root.InverseTransformPoint(dd.position).x - eye) > 0.1f)
                    side = -Mathf.Sign(root.InverseTransformPoint(dd.position).x - eye);
            }
            if (Sides.Count > 64) Sides.Clear(); // destroyed machines
            Sides[m] = side;
            return side;
        }

        internal static bool IsLocalPassenger => _passengerOf != null;

        // =====================================================================================
        // Local side
        // =====================================================================================

        /// <summary>Called from Plugin.Update.</summary>
        internal static void Tick()
        {
            ExpireRemote();

            if (!Net.Active)
            {
                if (_passengerOf != null || _passengerId >= 0) End(sendState: false);
                return;
            }

            MachineController m = Singleton<Player>.IsInstanced() ? Singleton<Player>.Instance.GetControlledMachine() : null;
            if (m == null) m = null; // normalize destroyed
            int id = MachineSync.IdOf(m);
            // A passenger stays in the passenger seat until they get out themselves: also when the driver
            // gets out, lags, or the machines are being re-indexed (id briefly -1). The passenger never
            // slides into the driver seat (that looked wrong and left the exiting driver stuck); to drive,
            // get out and enter again through the driver door.
            bool passenger = m != null &&
                             ((id >= 0 && MachineSync.IsRemoteDriven(id)) || ReferenceEquals(m, _passengerOf));
            if (passenger && id >= 0) _passengerId = id;

            if (passenger && !ReferenceEquals(m, _passengerOf))
            {
                if (_passengerOf != null) End(sendState: false);
                Begin(m, id);
            }
            else if (!passenger && (_passengerOf != null || _passengerId >= 0))
            {
                End(sendState: true);
            }

            if (_passengerOf == null) { ExitToPassengerSide(); return; }

            _passengerOf.IsDriven = false;
            // Keep the game's seated driver visible for the remote driver (the cabin view hides it).
            if (_passengerOf.WorkerPerson != null && !_passengerOf.WorkerPerson.gameObject.activeSelf)
                _passengerOf.WorkerPerson.gameObject.SetActive(true);

            if (Time.unscaledTime >= _nextResend) SendState(_passengerId); // late joiners, lost packets
        }

        private static void Begin(MachineController m, int id)
        {
            _passengerOf = m;
            _passengerId = id;
            m.IsDriven = false;
            OffsetCamera(m);
            SendState(id);
            Plugin.Log.LogInfo($"Passenger: riding in {m.gameObject.name} (#{id})");
        }

        private static void End(bool sendState)
        {
            MachineController m = _passengerOf;
            _passengerOf = null;
            _passengerId = -1;
            if (sendState) SendState(-1);

            // The session ended while seated as a passenger: alone in this world now, take over as the driver.
            if (m != null && !Net.Active && m.IsControlled() && m.EnterVehicle != null)
            {
                // Like EnterVehicleController.SetPlayerState: in an operator cab the player operates (IsOperated), not drives.
                m.IsDriven = InOperatorCabin == null || !InOperatorCabin(m.EnterVehicle);
                if (m.EnterVehicle.MyPlayerState == PlayerState.InMachineCabin) m.EnterVehicle.SetCameraScripts();
                m.StartCoroutine(m.EnterVehicle.UpdateWorkerPerson());
                Plugin.Log.LogInfo($"Passenger: remote driver left {m.gameObject.name}, you are driving now (session ended)");
            }
            else if (m != null)
            {
                Plugin.Log.LogInfo($"Passenger: left {m.gameObject.name}");
                if (Net.Active) { _exitedFrom = m; _exitFixUntil = Time.unscaledTime + 6f; }
            }
        }

        private static MachineController _exitedFrom;
        private static float _exitFixUntil;

        /// <summary>
        /// The game has a single exit point, at the driver door: a passenger getting out landed exactly where
        /// the driver stands (or got out), and the two players looked like one. Once the passenger's get-out
        /// animation ends, move them to the same point mirrored to the passenger side of the machine
        /// (only if that spot is free and has ground under it; otherwise they stay at the driver door).
        /// </summary>
        private static void ExitToPassengerSide()
        {
            MachineController m = _exitedFrom;
            if (m == null) { _exitedFrom = null; return; }
            if (Time.unscaledTime > _exitFixUntil || !Net.Active) { _exitedFrom = null; return; }
            EnterVehicleController ev = m.EnterVehicle;
            if (ev == null || ev.PlayerExitPoint == null || !Singleton<RigidbodyFPSWalker>.IsInstanced()) { _exitedFrom = null; return; }
            if (ev.MyPlayerState != PlayerState.FreeWalk) return; // still getting out
            if (Singleton<Player>.IsInstanced() && Singleton<Player>.Instance.GetControlledMachine() != null) { _exitedFrom = null; return; }
            _exitedFrom = null;

            Transform root = m.transform;
            Vector3 local = root.InverseTransformPoint(ev.PlayerExitPoint.position);
            local.x = -local.x;
            Vector3 probe = root.TransformPoint(local);
            RigidbodyFPSWalker walker = Singleton<RigidbodyFPSWalker>.Instance;
            Vector3 now = walker.transform.position;
            if ((probe - now).sqrMagnitude < 0.5f * 0.5f) return; // already on that side
            if (Physics.CheckCapsule(probe + Vector3.up * 0.6f, probe + Vector3.up * 1.6f, 0.3f, ~0, QueryTriggerInteraction.Ignore)) return;
            if (!Physics.Raycast(probe + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore)) return;
            float feetToBody = Mathf.Max(0f, now.y - ev.PlayerExitPoint.position.y);
            Vector3 target = hit.point + Vector3.up * (feetToBody + 0.05f);
            var rb = walker.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.position = target; }
            walker.transform.position = target;
            Plugin.Log.LogInfo($"Passenger: got out on the passenger side of {m.gameObject.name}");
        }

        private static void SendState(int machineId)
        {
            _nextResend = Time.unscaledTime + ResendInterval;
            var ms = ObjectSync.Begin(ObjectSync.SubPassenger, out var w);
            w.Write(Net.LocalId);
            w.Write(machineId);
            w.Write(MachineSync.Epoch); // world version: the machine id means this machine only in this world
            Net.Broadcast(ms.ToArray(), reliable: true);
        }

        private static readonly AccessTools.FieldRef<EnterVehicleController, bool> InOperatorCabin =
            AccessTools.Field(typeof(EnterVehicleController), "InOperatorCabin") != null
                ? AccessTools.FieldRefAccess<EnterVehicleController, bool>("InOperatorCabin")
                : null;

        /// <summary>
        /// Moves the cabin camera to the passenger side. SetCameraScripts parents the main camera to the cab
        /// camera's parent (part of the machine), so it rides on the remote-driven body, which MachineSync moves
        /// in FixedUpdate with rigidbody interpolation: the passenger view is as smooth as the body, no extra
        /// smoothing needed here.
        /// </summary>
        private static void OffsetCamera(MachineController m)
        {
            EnterVehicleController ev = m.EnterVehicle;
            if (ev == null || ev.CabinCamera == null || ev.MyPlayerState != PlayerState.InMachineCabin) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            // The cab the game placed the camera in (operator cab of excavators, else the driver cab).
            bool op = InOperatorCabin != null && InOperatorCabin(ev) && ev.OperatorCabinCamera != null;
            Transform cabin = op ? ev.OperatorCabinCamera.transform : ev.CabinCamera.transform;
            cam.transform.position = cabin.position + m.transform.right * (SeatSideOffset * SeatSide(m));
        }

        // =====================================================================================
        // Remote side
        // =====================================================================================

        /// <summary>Sub-kind 4 of (Msg)12: player LocalId, machine id (-1 = not a passenger).</summary>
        internal static void ReceivePassenger(BinaryReader r)
        {
            ulong player = r.ReadUInt64();
            int machineId = r.ReadInt32();
            byte epoch = r.ReadByte();
            if (player == Net.LocalId || MachineSync.LeftRecently(player)) return;
            // A seat in another copy of the world (the sender plays an older world version): not a seat here.
            if (machineId >= 0 && (!MachineSync.WorldReady() || !MachineSync.EpochOk(epoch))) machineId = -1;

            if (machineId < 0)
            {
                RemotePassengers.Remove(player);
                return;
            }
            if (!RemotePassengers.TryGetValue(player, out var rp) || rp.MachineId != machineId || rp.Machine == null)
            {
                rp = new RemotePassenger { MachineId = machineId, Machine = FindMachine(machineId) };
                RemotePassengers[player] = rp;
                if (rp.Machine != null)
                    Plugin.Log.LogInfo($"Passenger: {PlayerName(player)} rides in {rp.Machine.gameObject.name} (#{machineId})");
            }
            rp.LastUpdate = Time.unscaledTime;
        }

        // LocalId equals the transport peer id on Steam; on LAN it is random, so fall back to a short hex tag.
        private static string PlayerName(ulong localId) =>
            Net.Names.TryGetValue(localId, out string n) ? n : "player " + (localId & 0xFFFFFF).ToString("x6");

        /// <summary>MachineSync: that player left the session; its avatar no longer sits in any machine.</summary>
        internal static void OnPlayerLeft(ulong localId)
        {
            if (RemotePassengers.Remove(localId)) Plugin.Log.LogInfo($"Passenger: {PlayerName(localId)} left the session");
        }

        /// <summary>True if that remote player (LocalId) reported sitting as a passenger.</summary>
        internal static bool IsRemotePassenger(ulong localId) =>
            RemotePassengers.TryGetValue(localId, out var rp) && rp.Machine != null;

        /// <summary>
        /// Where to draw a remote passenger's avatar (feet pivot, seated pose): next to the driver's eye
        /// point (EnterVehicleController.CabinCamera), shifted to the right, following the vehicle.
        /// </summary>
        internal static bool TryGetPassengerSeat(ulong localId, out Vector3 feet, out Quaternion rot)
        {
            feet = default;
            rot = Quaternion.identity;
            if (!RemotePassengers.TryGetValue(localId, out var rp) || rp.Machine == null) return false;

            Transform mt = rp.Machine.transform;
            GameObject cabin = rp.Machine.EnterVehicle != null ? rp.Machine.EnterVehicle.CabinCamera : null;
            Vector3 eye = cabin != null ? cabin.transform.position : mt.position + mt.up * 2.4f;
            feet = eye + mt.right * (SeatSideOffset * SeatSide(rp.Machine)) - mt.up * SeatEyeHeight;
            rot = mt.rotation;
            return true;
        }

        private static void ExpireRemote()
        {
            if (RemotePassengers.Count == 0) return;
            List<ulong> dead = null;
            foreach (var kv in RemotePassengers)
                if (Time.unscaledTime - kv.Value.LastUpdate > RemoteStateTimeout)
                    (dead ??= new List<ulong>()).Add(kv.Key);
            if (dead != null) foreach (ulong id in dead) RemotePassengers.Remove(id);
        }

        private static MachineController FindMachine(int id)
        {
            // MachineSync's id table (incl. purchased machines registered by PurchaseSync): no scene scan.
            MachineController m = MachineSync.Get(id);
            return m != null ? m : null;
        }

        // =====================================================================================
        // Harmony: no driving input from a passenger
        // =====================================================================================

        /// <summary>FixedUpdate reads throttle/steer only when IsDriven.</summary>
        [HarmonyPatch]
        private static class PatchFixedUpdate
        {
            private static MethodBase Target => AccessTools.Method(typeof(MachineController), nameof(MachineController.FixedUpdate));

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Passenger: MachineController.FixedUpdate not found");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static void Prefix(MachineController __instance)
            {
                if (IsPassengerIn(__instance)) __instance.IsDriven = false;
            }
        }

        /// <summary>Update: handbrake (gated by IsDriven) and the operator-mode toggle.</summary>
        [HarmonyPatch]
        private static class PatchUpdate
        {
            private static MethodBase Target => AccessTools.Method(typeof(MachineController), nameof(MachineController.Update));

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Passenger: MachineController.Update not found");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            // The machine whose HasOperatorMode this Update hid (Update calls do not nest, one is enough).
            private static MachineController _hidden;

            private static void Prefix(MachineController __instance)
            {
                RestoreHidden(); // never left over (a finalizer always runs), but cheap to be sure
                if (!IsPassengerIn(__instance)) return;
                __instance.IsDriven = false;
                // Hide "Machine Mode" from the passenger for the duration of this Update.
                if (!__instance.HasOperatorMode) return;
                __instance.HasOperatorMode = false;
                _hidden = __instance;
            }

            /// <summary>
            /// A finalizer, not a postfix: it also runs when Update throws (a postfix would not, and the machine would
            /// lose its operator mode for the rest of the session). The exception is passed on unchanged.
            /// </summary>
            private static System.Exception Finalizer(System.Exception __exception)
            {
                RestoreHidden();
                return __exception;
            }

            private static void RestoreHidden()
            {
                MachineController m = _hidden;
                if (ReferenceEquals(m, null)) return;
                _hidden = null;
                if (m != null) m.HasOperatorMode = true;
            }
        }

        /// <summary>Engine start/stop key.</summary>
        [HarmonyPatch]
        private static class PatchControlEngineStart
        {
            private static MethodBase Target => AccessTools.Method(typeof(MachineController), nameof(MachineController.ControlEngineStart));

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Passenger: MachineController.ControlEngineStart not found");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static bool Prefix(MachineController __instance) => !IsPassengerIn(__instance);
        }

        /// <summary>Re-apply the passenger camera offset whenever the game places the cabin camera.</summary>
        [HarmonyPatch]
        private static class PatchSetCameraScripts
        {
            private static MethodBase Target => AccessTools.Method(typeof(EnterVehicleController), nameof(EnterVehicleController.SetCameraScripts));

            private static bool Prepare()
            {
                if (Target != null && ControllerMachine != null) return true;
                Plugin.Log.LogWarning("Passenger: EnterVehicleController.SetCameraScripts/MyMachineController not found, no seat offset");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static void Postfix(EnterVehicleController __instance)
            {
                MachineController m = ControllerMachine(__instance);
                if (IsPassengerIn(m)) OffsetCamera(m);
            }
        }

        // Machine inputs a passenger must not work: driving (MachineController.AccelCode/SteeringCode),
        // arms (Backhoe.Axis = "Machine Axis n", mouse digging), lights (LightsController), diff lock,
        // handbrake, engine, operator mode, drill/tool actions, ripper tips.
        private static readonly HashSet<string> BlockedInputs = new HashSet<string>
        {
            "Vertical", "Horizontal", "Arm1Moving", "Arm2Moving", "ShovelCoverMoving", "LockDiff", "Lights",
            "Engine", "Handbrake", "Machine Mode", "ToolAction1", "ToolAction2", "SwitchTipLeft",
            "SwitchTipRight", "ChangeMode",
        };

        private static bool Blocked(string code) =>
            code != null && (BlockedInputs.Contains(code) || code.StartsWith("Machine Axis", System.StringComparison.Ordinal));

        private static IEnumerable<MethodBase> InputMethods(System.Type ret)
        {
            foreach (string name in new[] { "GetAxis", "GetAxisRaw", "GetButton", "GetButtonDown", "GetButtonUp",
                         "GetPositiveAxis", "GetNegativeAxis", "GetDoubleAxis", "GetDoubleAxisDown",
                         "GetNegativeButtonDown", "GetNegativeButton" })
            {
                MethodInfo mi = AccessTools.Method(typeof(InputManager), name, new[] { typeof(string) });
                if (mi != null && mi.IsStatic && mi.ReturnType == ret) yield return mi;
            }
        }

        /// <summary>Float inputs read as 0 for a passenger (only while riding; one null check otherwise).</summary>
        [HarmonyPatch]
        private static class PatchInputAxis
        {
            private static IEnumerable<MethodBase> TargetMethods() => InputMethods(typeof(float));

            private static bool Prepare()
            {
                foreach (var _ in TargetMethods()) return true;
                Plugin.Log.LogWarning("Passenger: InputManager axis methods not found, passenger input not blocked");
                return false;
            }

            private static bool Prefix(string __0, ref float __result)
            {
                if (_passengerOf == null || !Blocked(__0)) return true;
                __result = 0f;
                return false;
            }
        }

        /// <summary>Button inputs read as released for a passenger.</summary>
        [HarmonyPatch]
        private static class PatchInputButton
        {
            private static IEnumerable<MethodBase> TargetMethods() => InputMethods(typeof(bool));

            private static bool Prepare()
            {
                foreach (var _ in TargetMethods()) return true;
                Plugin.Log.LogWarning("Passenger: InputManager button methods not found, passenger input not blocked");
                return false;
            }

            private static bool Prefix(string __0, ref bool __result)
            {
                if (_foreignPick && __0 == "Pick")
                {
                    __result = false;
                    return false;
                }
                if (_passengerOf == null || !Blocked(__0)) return true;
                __result = false;
                return false;
            }
        }

        // True while PickBodyController.FixedUpdate runs for a machine the local player does not drive.
        private static bool _foreignPick;

        /// <summary>
        /// Excavator rope pick (Koparka.PickBody, PickBodyController + RopeController): its FixedUpdate runs for
        /// EVERY Koparka on every PC and reads the "Pick" button without checking who controls the machine.
        /// In a session, a player pressing Pick (also the walking pick key) would hook or drop the rope of a
        /// machine someone else drives, on this PC only. The rope belongs to whoever drives the machine: the
        /// button counts only for the machine the local player drives itself. The picked body is a loose object
        /// near the driven machine, so its pose already comes from the driver's PC (ObjectSync).
        /// </summary>
        [HarmonyPatch]
        private static class PatchPickBody
        {
            private static MethodBase Target => AccessTools.Method(typeof(PickBodyController), nameof(PickBodyController.FixedUpdate));
            private static bool Prepare() => Target != null && ControllerMachine != null;
            private static MethodBase TargetMethod() => Target;

            private static void Prefix(PickBodyController __instance)
            {
                _foreignPick = Net.Active && !MachineSync.LocallyDriven(ControllerMachine(__instance));
            }

            private static System.Exception Finalizer(System.Exception __exception)
            {
                _foreignPick = false;
                return __exception;
            }
        }
    }
}
