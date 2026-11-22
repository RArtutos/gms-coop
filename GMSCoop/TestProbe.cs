using System;
using System.Globalization;
using SaveSystem;
using UnityEngine;
using GameSaveManager = SaveSystem.SaveManager;

namespace GMSCoop
{
    /// <summary>
    /// Command-line helpers (inactive unless the flag is given). --coop-load is a normal feature (the launcher uses it); the rest is for tests:
    ///  --coop-load &lt;slot&gt;  load that save slot from the main menu automatically (host side of an
    ///                       unattended two-instance test).
    ///  --coop-probe         host: once a client is connected and has loaded the world, walk the local
    ///                       player around a 4 m circle (10 s period). Every PC logs "PROBE" lines with
    ///                       the wall clock (DateTime.UtcNow, shared by two instances on one PC): the host
    ///                       its own position, a client the position of the remote avatar it shows. Comparing
    ///                       the two logs gives the real end-to-end delay and error of the player sync.
    /// </summary>
    internal static class TestProbe
    {
        private static string _loadSlot;
        private static bool _loadDone;
        private static bool _probe;
        private static Vector3 _center;
        private static bool _hasCenter;
        private static float _probeStart = -1f;
        private static float _nextLog;
        private static int _scenarioErrors;

        internal static void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--coop-load" && i + 1 < args.Length) _loadSlot = args[i + 1];
                if (args[i] == "--coop-probe") _probe = true;
                if (args[i] == "--coop-test") TestScenarios.Enabled = true;
            }
            if (_loadSlot != null) Plugin.Log.LogInfo("Will load save slot '" + _loadSlot + "' from the main menu");
            if (_probe) Plugin.Log.LogInfo("Test: probe mode on");
            if (TestScenarios.Enabled) Plugin.Log.LogInfo("Test: scenario mode on");
        }

        internal static void Tick()
        {
            TryAutoLoad();
            if (_probe) ProbeTick();
            if (TestScenarios.Enabled)
                try { TestScenarios.Tick(); }
                catch (Exception ex) { if (_scenarioErrors++ < 3) Plugin.Log.LogWarning("Test scenarios: " + ex); }
        }

        private static void TryAutoLoad()
        {
            if (_loadSlot == null || _loadDone || Time.unscaledTime < 10f) return;
            if (!Singleton<CheckPointManagerBase>.IsInstanced()) return;
            var cpm = Singleton<CheckPointManagerBase>.Instance as CheckpointManager;
            if (cpm == null || cpm.GetLoadingState() != LoadingState.ReadyToPlay) return;
            _loadDone = true;
            try
            {
                // Same steps as the load menu (MainMenuSave) and SaveTransfer's client load.
                GameSaveManager.SetCurrentSlot(_loadSlot);
                cpm.RequestLoad();
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex == 0)
                {
                    InputManager.EnableMap("Walk");
                    if (Singleton<CursorManager>.IsInstanced()) Singleton<CursorManager>.Instance.SetCursorMenu(isInMenu: false);
                }
                Plugin.Log.LogInfo("Loading save slot '" + _loadSlot + "'");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Auto load of the save slot failed: " + ex);
            }
        }

        private static void ProbeTick()
        {
            if (!Net.Active || GameState.IsLoading || !Singleton<RigidbodyFPSWalker>.IsInstanced()) return;
            Transform walker = Singleton<RigidbodyFPSWalker>.Instance.transform;
            string now = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

            if (Net.IsHost)
            {
                // Start moving once a client plays our world.
                bool clientReady = false;
                foreach (ulong p in Net.Peers) if (SaveTransfer.PeerReady(p)) clientReady = true;
                if (!clientReady) return;
                if (!_hasCenter) { _center = walker.position; _hasCenter = true; _probeStart = Time.unscaledTime; }
                float t = Time.unscaledTime - _probeStart;
                float a = t * Mathf.PI * 2f / 10f;
                Vector3 pos = _center + new Vector3(Mathf.Cos(a) * 4f, 0f, Mathf.Sin(a) * 4f);
                var rb = walker.GetComponent<Rigidbody>();
                if (rb != null) { rb.velocity = Vector3.zero; rb.position = pos; }
                walker.position = pos;
                if (Time.unscaledTime >= _nextLog)
                {
                    _nextLog = Time.unscaledTime + 0.5f;
                    Plugin.Log.LogInfo($"PROBE host {now} pos {pos.x:F3} {pos.z:F3}");
                }
            }
            else if (Time.unscaledTime >= _nextLog)
            {
                _nextLog = Time.unscaledTime + 0.5f;
                foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>())
                {
                    if (tr.parent != null || !tr.name.StartsWith("CoopAvatar_", StringComparison.Ordinal)) continue;
                    Vector3 p = tr.position;
                    Plugin.Log.LogInfo($"PROBE client {now} avatar {p.x:F3} {p.z:F3}");
                    break;
                }
            }
        }
    }
}
