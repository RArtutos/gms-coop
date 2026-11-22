using System;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Input only reaches the focused window. Rewired (the game's input library) reads the keyboard
    /// and mouse even when the window is not focused, and a co-op session keeps an unfocused window
    /// running (runInBackground). With two copies of the game on one PC, both players walked and looked
    /// around together: the other player's avatar seemed to follow you everywhere. Every game read
    /// goes through the static InputManager, so blocking it when the window is not focused covers all of them.
    /// </summary>
    internal static class FocusInput
    {
        private static bool _rewiredSet;

        // Also while a co-op prompt that reads plain keys is open (host lost G/M, newer world U/N, kick list 1-9).
        internal static bool Blocked => !Application.isFocused || CoopUi.ModalOpen;

        private static float _nextMoveCheck;
        private static Vector3 _lastPos;
        private static bool _wasUnfocused;
        private static int _moveWarnings;

        private static readonly string[] AxisMethods = { "GetAxis", "GetAxisRaw" };
        private static readonly string[] ButtonMethods =
        {
            "GetPositiveAxis", "GetNegativeAxis", "GetDoubleAxis", "GetDoubleAxisDown", "GetButton", "GetButtonDown",
            "GetNegativeButtonDown", "GetNegativeButton", "GetButtonUp", "GetAxisNegativePositive",
        };

        /// <summary>Called once from Plugin.Awake: blocks every InputManager read while the window has no focus.</summary>
        internal static void Apply(Harmony harmony)
        {
            int ok = 0;
            var missing = new System.Collections.Generic.List<string>();
            foreach (string name in AxisMethods) ok += PatchOne(harmony, name, nameof(FocusInputPatches.BlockAxis), missing);
            foreach (string name in ButtonMethods) ok += PatchOne(harmony, name, nameof(FocusInputPatches.BlockButton), missing);
            if (missing.Count == 0) Plugin.Log.LogInfo($"Focus input: all {ok} InputManager reads blocked without focus");
            else Plugin.Log.LogWarning($"Focus input: {ok} InputManager reads blocked, not patched: {string.Join(", ", missing)}");
        }

        private static int PatchOne(Harmony harmony, string method, string prefix, System.Collections.Generic.List<string> missing)
        {
            try
            {
                var target = AccessTools.Method(typeof(InputManager), method, new[] { typeof(string) });
                if (target == null) { missing.Add(method); return 0; }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(FocusInputPatches), prefix));
                return 1;
            }
            catch (Exception ex)
            {
                missing.Add(method + " (" + ex.Message + ")");
                return 0;
            }
        }

        internal static void Tick()
        {
            CheckUnfocusedMove();
            CheckStuck();
            SetRewiredOption();
        }

        /// <summary>Warns if the local player moves while this window has no focus (input leaking in).</summary>
        private static void CheckUnfocusedMove()
        {
            if (Time.unscaledTime < _nextMoveCheck) return;
            _nextMoveCheck = Time.unscaledTime + 1f;
            if (!Net.Active || GameState.IsLoading || !Singleton<RigidbodyFPSWalker>.IsInstanced()) { _wasUnfocused = false; return; }
            Vector3 pos = Singleton<RigidbodyFPSWalker>.Instance.transform.position;
            bool unfocused = !Application.isFocused;
            if (unfocused && _wasUnfocused && (pos - _lastPos).sqrMagnitude > 0.25f && _moveWarnings < 20)
            {
                _moveWarnings++;
                bool inMachine = Singleton<Player>.IsInstanced() && Singleton<Player>.Instance.GetControlledMachine() != null;
                var walker = Singleton<RigidbodyFPSWalker>.Instance;
                var rb = walker.GetComponent<Rigidbody>();
                string raw = "?";
                try { raw = ReInput.players.Players[0].GetAnyButton() + "/" + Input.anyKey; } catch { }
                Plugin.Log.LogWarning($"Focus input: local player moved {(pos - _lastPos).magnitude:0.0} m while the window was unfocused" +
                                      (inMachine ? " (in a machine)" : "") +
                                      $" | from {_lastPos} to {pos}, vel {(rb != null ? rb.velocity.ToString() : "-")}, kinematic {(rb != null && rb.isKinematic)}" +
                                      $", parent {(walker.transform.parent != null ? walker.transform.parent.name : "-")}, rewired/unity key {raw}" +
                                      $", axis V {InputManager.GetAxis("Vertical")}, object key {ObjectSync.KeyOf(walker.gameObject) ?? "-"}" +
                                      $", remotely held {ObjectSync.IsRemotelyHeld(walker.gameObject)}, root {walker.transform.root.name}");
            }
            _lastPos = pos;
            _wasUnfocused = unfocused;
        }

        /// <summary>Also tell Rewired itself to ignore input without focus (covers reads that bypass InputManager).</summary>
        private static float _nextStuckCheck;
        private static Vector3 _stuckPos;
        private static int _stuckReports;

        /// <summary>
        /// Diagnostic: the player holds a movement key in the focused window but the body does not move.
        /// Logs everything that can stop a walk, so a "nobody can move" report has a cause in the log.
        /// </summary>
        private static void CheckStuck()
        {
            if (Time.unscaledTime < _nextStuckCheck || _stuckReports >= 10) return;
            _nextStuckCheck = Time.unscaledTime + 2f;
            if (GameState.IsLoading || !Singleton<RigidbodyFPSWalker>.IsInstanced()) return;
            var walker = Singleton<RigidbodyFPSWalker>.Instance;
            Vector3 pos = walker.transform.position;
            bool moved = (pos - _stuckPos).sqrMagnitude > 0.04f;
            _stuckPos = pos;
            bool key = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D);
            if (!key || moved || !Application.isFocused) return;
            if (Singleton<Player>.IsInstanced() && Singleton<Player>.Instance.GetControlledMachine() != null) return;
            _stuckReports++;
            var rb = walker.GetComponent<Rigidbody>();
            string map = "?", blocked = "?", raw = "?";
            try { map = InputManager.GetCurrentMap(); } catch { }
            try { blocked = Singleton<InputManager>.Instance.AllInputBlocked.ToString(); } catch { }
            try { raw = ReInput.players.Players[0].GetAxis("Move Vertical") + "/" + ReInput.players.Players[0].GetAnyButton(); } catch { }
            Plugin.Log.LogWarning("Stuck: movement key held but the player does not move | pos " + pos +
                $", walker enabled {walker.enabled}/{walker.gameObject.activeInHierarchy}, rb kinematic {(rb != null && rb.isKinematic)}" +
                $", collisions {(rb != null && rb.detectCollisions)}, constraints {(rb != null ? rb.constraints.ToString() : "-")}" +
                $", object key {ObjectSync.KeyOf(walker.gameObject) ?? "-"}, axis V {InputManager.GetAxis("Vertical")}, map {map}" +
                $", all input blocked {blocked}, rewired {raw}, timeScale {Time.timeScale}, cursor {Cursor.lockState}/{Cursor.visible}" +
                $", net {(Net.Active ? (Net.IsHost ? "host" : "client") : "off")}");
        }

        private static void SetRewiredOption()
        {
            if (_rewiredSet) return;
            try
            {
                if (!ReInput.isReady) return;
                ReInput.configuration.ignoreInputWhenAppNotInFocus = true;
                _rewiredSet = true;
            }
            catch (Exception ex)
            {
                _rewiredSet = true;
                Plugin.Log.LogWarning("Focus input: could not set Rewired option: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Patched by hand (FocusInput.Apply), one prefix per InputManager method: explicit, so the log can
    /// prove every read is covered.
    /// </summary>
    internal static class FocusInputPatches
    {
        internal static bool BlockAxis(ref float __result)
        {
            if (!FocusInput.Blocked) return true;
            __result = 0f;
            return false;
        }

        internal static bool BlockButton(ref bool __result)
        {
            if (!FocusInput.Blocked) return true;
            __result = false;
            return false;
        }
    }
}
