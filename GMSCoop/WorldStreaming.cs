using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using GoldDigger;
using HarmonyLib;
using SaveSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GMSCoop
{
    /// <summary>
    /// Smoother world streaming (single player and co-op; local only, nothing is sent).
    ///
    /// How the game streams: Build_Stream_Main is the base scene; every area (ALLOTMENT I-IV, Town, FLOODING,
    /// the buff* / View* filler scenes, WestBridge, DLC parcels) is an additive scene owned by an AreaStreamer
    /// in the base scene. AreaStreamer.Update loads the scene when the player (or the machine he drives) enters
    /// one of its box colliders (LoadBounds) and unloads it 10 s after he leaves the boxes grown by 25 m.
    /// The hitches come from:
    ///   1. AreaStreamerBase.Load: once LoadSceneAsync reaches 0.9 it calls PauseManager.SetGamePaused(true)
    ///      (Time.timeScale = 0) and only unpauses 30 frames after the scene activated (AreaStreamerBase.Unpause).
    ///   2. MemoryCleaner calls Resources.UnloadUnusedAssets on every sceneLoaded / sceneUnloaded, and
    ///      AreaStreamerBase.UnloadScene calls GC.Collect + Resources.UnloadUnusedAssets (main-thread scans).
    ///   3. The scene activation itself (Awake/Start, terrain collider build, SaveTerrainState copying a whole
    ///      tile), which only starts once the player already is inside the trigger box.
    ///   4. Fast travel holds the loading screen 4.5 s (6.5 s with a trailer) after the area loaded.
    ///
    /// What this does (config section "Streaming"):
    ///   - Load pause: instead of stopping time, only the bodies that could fall or pop during the activation
    ///     are frozen (Rigidbody.constraints = FreezeAll, then restored exactly): non-kinematic bodies of the new
    ///     scene, of the area's saved objects (GameplayObjects) and any other non-kinematic body inside the
    ///     area's boxes or the new terrain tiles. Kinematic bodies (ObjectSync / MachineSync followers, frozen
    ///     cargo, remote-driven machines) and held objects are never touched. The local player, his machine and
    ///     its trailer are excluded, unless they stand on a newly loaded terrain tile: then the game's own pause
    ///     is kept for that load (exactly the vanilla behaviour). The freeze lasts until 30 frames after the
    ///     activation, the area finished loading (objects enabled) and TerrainSync has applied the edits queued
    ///     for the new tiles (+3 frames); 20 s at most.
    ///   - Preloading with the game's own AreaStreamer.Load (same scenes, objects, names and paths: saves and
    ///     co-op keys are unchanged), one area at a time, nearest first, around EVERY player (remote avatars
    ///     too), so objects moved by another player always have terrain under them. Auto = all areas on PCs with
    ///     >= 12 GB RAM and >= 3 GB VRAM (DLC areas too at >= 16 GB), else nearby areas. The game's LoadBounds /
    ///     UnloadBounds are never modified (ObjectSync.AreaAt and others read them).
    ///   - Fast travel: the destination area starts loading as soon as its point is selected on the map; the
    ///     loading screen closes 1 s after the destination is loaded and stable instead of 4.5 / 6.5 s.
    ///   - A second AreaStreamer.Load while one is running (fast travel during a preload) waits for the running
    ///     one instead of loading the scene a second time.
    ///   - Asset cleanup of streamed loads/unloads is deferred to a calm moment (never during a load).
    ///   - Lower background integration budget for non-urgent loads, larger async upload buffer.
    /// </summary>
    internal static class WorldStreaming
    {
        internal enum PreloadKind { Auto, Off, Nearby, All }
        internal enum AutoYesNo { Auto, Yes, No }

        private static ConfigEntry<bool> _enabled, _removePause, _keepVisited, _deferCleanup, _quickTravel;
        private static ConfigEntry<PreloadKind> _mode;
        private static ConfigEntry<AutoYesNo> _dlc;
        private static ConfigEntry<float> _distance, _maxDefer;
        private static ConfigEntry<UnityEngine.ThreadPriority> _bgPriority;
        private static ConfigEntry<int> _uploadSlice, _uploadBuffer;

        private static int _ramMb, _vramMb;
        private static bool _lowMemory;
        private static int _inLoadStep;         // > 0 while AreaStreamerBase.Load's iterator runs a step
        private static bool _ownPause;          // our own SetGamePaused call (never skipped)
        private static int _skippedPauses;
        private static bool _cleanupPending;
        private static int _cleanupRequests;
        private static float _cleanupSince;
        private static Vector3 _stillAnchor;
        private static float _stillTime;
        private static float _nextQuality, _nextPreloadAt, _nextPlayers;
        private static AreaStreamer _preloading, _prefetch;
        private static float _preloadStarted, _prefetchUntil;
        private static readonly HashSet<string> Streamed = new HashSet<string>();
        private static readonly List<Vector3> Players = new List<Vector3>();

        private static AccessTools.FieldRef<AreaStreamerBase, float> _timeLoaded;
        private static AccessTools.FieldRef<MapMenu, CollisionListener> _oldCol;
        private static FieldInfo _ghostsField, _pendingField;
        private static bool _ghostsWarned, _pendingWarned;

        internal static void Init(ConfigFile cfg, GameObject host)
        {
            _enabled = cfg.Bind("Streaming", "Enabled", true,
                "Smoother world streaming: no freeze when an area loads, preloading, quick fast travel, deferred cleanup. Local only, no effect on saves or sync.");
            _removePause = cfg.Bind("Streaming", "RemoveLoadPause", true,
                "Keep time running while a streamed area activates; only the bodies in / near that area are frozen until its terrain and objects are restored. The game's pause is kept when you stand on the new terrain, on loading screens and in fast travel.");
            _mode = cfg.Bind("Streaming", "PreloadMode", PreloadKind.Auto,
                "Auto: All on PCs with >= 12 GB RAM and >= 3 GB VRAM, else Nearby. Off: game default. Nearby: load areas within PreloadDistance of any player. All: load every area gradually at world start and keep it loaded.");
            _dlc = cfg.Bind("Streaming", "PreloadDlcAreas", AutoYesNo.Auto,
                "With All: also keep the DLC areas loaded (Auto: when the PC has >= 16 GB RAM). Otherwise they load on approach.");
            _distance = cfg.Bind("Streaming", "PreloadDistance", 120f, new ConfigDescription(
                "Metres outside an area's own trigger box at which it starts loading (and stays loaded), around every player.", new AcceptableValueRange<float>(0f, 600f)));
            _keepVisited = cfg.Bind("Streaming", "KeepVisitedAreasLoaded", false,
                "Never unload an area once it is loaded (memory grows with every area visited).");
            _quickTravel = cfg.Bind("Streaming", "QuickFastTravel", true,
                "Fast travel: start loading the destination when its point is selected on the map, and close the loading screen 1 s after the destination is ready (game: 4.5 s, 6.5 s with a trailer).");
            _deferCleanup = cfg.Bind("Streaming", "DeferAssetCleanup", true,
                "Move the asset cleanup (Resources.UnloadUnusedAssets / GC.Collect) of streamed loads and unloads to a calm moment instead of mid-drive.");
            _maxDefer = cfg.Bind("Streaming", "MaxCleanupDelaySeconds", 240f, new ConfigDescription(
                "Longest time a deferred cleanup waits for a calm moment.", new AcceptableValueRange<float>(10f, 1800f)));
            _bgPriority = cfg.Bind("Streaming", "BackgroundLoadPriority", UnityEngine.ThreadPriority.BelowNormal,
                "Per-frame budget for integrating an area no player needs yet (Low 2 ms, BelowNormal 4 ms, Normal 10 ms = game default). Urgent loads use Normal, loading screens High.");
            _uploadSlice = cfg.Bind("Streaming", "AsyncUploadTimeSliceMs", 3, new ConfigDescription(
                "Milliseconds per frame for uploading textures/meshes to the GPU (Unity default 2).", new AcceptableValueRange<int>(1, 33)));
            _uploadBuffer = cfg.Bind("Streaming", "AsyncUploadBufferMB", 32, new ConfigDescription(
                "Async upload ring buffer size (Unity default 4..16). A texture larger than the buffer forces a reallocation hitch.", new AcceptableValueRange<int>(2, 512)));

            _ramMb = SystemInfo.systemMemorySize;
            _vramMb = SystemInfo.graphicsMemorySize;
            _lowMemory = _ramMb > 0 && _ramMb < 7000;

            if (!_enabled.Value)
            {
                Plugin.Log.LogInfo("World streaming: disabled in config");
                return;
            }
            try
            {
                _timeLoaded = AccessTools.FieldRefAccess<AreaStreamerBase, float>("timeloaded");
                _oldCol = AccessTools.FieldRefAccess<MapMenu, CollisionListener>("oldCol");
                _ghostsField = AccessTools.Field(typeof(PlayerSync), "Ghosts");
                _pendingField = AccessTools.Field(typeof(TerrainSync), "Pending");
                var h = new Harmony(Plugin.Guid + ".streaming");
                MethodInfo loadM = AccessTools.Method(typeof(AreaStreamerBase), "Load");
                h.Patch(AccessTools.EnumeratorMoveNext(loadM), prefix: new HarmonyMethod(typeof(WorldStreaming), nameof(LoadStepPrefix)),
                    finalizer: new HarmonyMethod(typeof(WorldStreaming), nameof(LoadStepFinalizer)));
                h.Patch(loadM, prefix: new HarmonyMethod(typeof(WorldStreaming), nameof(LoadPrefix)));
                MethodInfo unload = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(AreaStreamerBase), "UnloadScene"));
                h.Patch(unload, transpiler: new HarmonyMethod(typeof(WorldStreaming), nameof(UnloadTranspiler)));
                h.Patch(AccessTools.Method(typeof(PauseManager), nameof(PauseManager.SetGamePaused)),
                    prefix: new HarmonyMethod(typeof(WorldStreaming), nameof(SetGamePausedPrefix)));
                h.Patch(AccessTools.Method(typeof(MemoryCleaner), "OnSceneLoad"),
                    prefix: new HarmonyMethod(typeof(WorldStreaming), nameof(CleanerSceneLoadPrefix)));
                h.Patch(AccessTools.Method(typeof(MemoryCleaner), "OnSceneUnload"),
                    prefix: new HarmonyMethod(typeof(WorldStreaming), nameof(CleanerSceneUnloadPrefix)));
                h.Patch(AccessTools.Method(typeof(MapMenu), "ForceLoadWithoutTrailer"),
                    prefix: new HarmonyMethod(typeof(WorldStreaming), nameof(ForceLoadWithoutTrailerPrefix)));
                h.Patch(AccessTools.Method(typeof(MapMenu), "ForceLoad"),
                    prefix: new HarmonyMethod(typeof(WorldStreaming), nameof(ForceLoadPrefix)));
                h.Patch(AccessTools.Method(typeof(MapObject), "SetupResetButton"),
                    postfix: new HarmonyMethod(typeof(WorldStreaming), nameof(SetupResetButtonPostfix)));
                SceneManager.sceneLoaded += OnSceneLoaded;
                host.AddComponent<WorldStreamingRunner>();
                Plugin.Log.LogInfo($"World streaming: on (physics-safe no load pause {_removePause.Value}, preload {EffectiveMode()}" +
                    $"{(EffectiveMode() == PreloadKind.All ? (IncludeDlc() ? " incl. DLC" : " without DLC") : "")} {EffectiveDistance():0} m around every player, " +
                    $"quick fast travel {_quickTravel.Value}, keep visited {_keepVisited.Value}, deferred cleanup {_deferCleanup.Value}, " +
                    $"bg priority {_bgPriority.Value}, upload {_uploadSlice.Value} ms / {_uploadBuffer.Value} MB, RAM {_ramMb} MB, VRAM {_vramMb} MB" +
                    $"{(_lowMemory ? " (low RAM: preload limited)" : "")}" +
                    $"{(_ghostsField == null ? ", remote players unknown" : "")}{(_pendingField == null ? ", terrain queue unknown" : "")})");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("World streaming: could not hook the game's streaming, game defaults stay: " + ex.Message);
            }
        }

        private static PreloadKind EffectiveMode()
        {
            PreloadKind m = _mode.Value;
            if (m == PreloadKind.Auto)
                m = _ramMb >= 12000 && (_vramMb <= 0 || _vramMb >= 3000) ? PreloadKind.All : PreloadKind.Nearby;
            if (_lowMemory && m == PreloadKind.All) m = PreloadKind.Nearby;
            return m;
        }

        private static bool IncludeDlc() =>
            _dlc.Value == AutoYesNo.Yes || _dlc.Value == AutoYesNo.Auto && _ramMb >= 16000;

        private static float EffectiveDistance() => _lowMemory ? Mathf.Min(_distance.Value, 60f) : _distance.Value;

        // ------------------------------------------------------------------ helpers

        private static bool Ready()
        {
            if (!SaveSystem.Singleton<CheckPointManagerBase>.IsInstanced()) return false;
            CheckPointManagerBase cpm = SaveSystem.Singleton<CheckPointManagerBase>.Instance;
            return cpm != null && cpm.GetLoadingState() == LoadingState.ReadyToPlay;
        }

        private static Player LocalPlayer() =>
            global::Singleton<Player>.IsInstanced() ? global::Singleton<Player>.Instance : null;

        private static bool PlayerPos(out Vector3 pos)
        {
            pos = default;
            Player p = LocalPlayer();
            if (p == null) return false;
            MachineController m = p.GetControlledMachine();
            pos = m != null ? m.transform.position : p.transform.position;
            return true;
        }

        /// <summary>Local player first, then every remote avatar (PlayerSync ghosts, read only).</summary>
        private static void RefreshPlayers(Vector3 local, float now)
        {
            if (now < _nextPlayers && Players.Count > 0) { Players[0] = local; return; }
            _nextPlayers = now + 0.5f;
            Players.Clear();
            Players.Add(local);
            if (!Net.Active || _ghostsField == null) return;
            try
            {
                if (!(_ghostsField.GetValue(null) is IDictionary ghosts)) return;
                foreach (object k in ghosts.Keys)
                {
                    Transform t = PlayerSync.AvatarRoot((ulong)k);
                    if (t != null) Players.Add(t.position);
                }
            }
            catch (Exception ex)
            {
                if (!_ghostsWarned) { _ghostsWarned = true; Plugin.Log.LogWarning("World streaming: remote player positions unavailable: " + ex.Message); }
            }
        }

        private static float MinDistance(Bounds[] boxes)
        {
            float best = float.MaxValue;
            foreach (Vector3 p in Players) best = Mathf.Min(best, Distance(boxes, p));
            return best;
        }

        private static bool FastTravelScreen()
        {
            if (!global::Singleton<MenuManager>.IsInstanced()) return false;
            MenuManager mm = global::Singleton<MenuManager>.Instance;
            return mm != null && mm.MyMenuLoading != null && mm.MyMenuLoading.IsOpen;
        }

        /// <summary>Horizontal distance from <paramref name="p"/> to the nearest box (0 inside).</summary>
        private static float Distance(Bounds[] boxes, Vector3 p)
        {
            float best = float.MaxValue;
            if (boxes == null) return best;
            foreach (Bounds b in boxes)
            {
                float dx = Mathf.Max(0f, Mathf.Abs(p.x - b.center.x) - b.extents.x);
                float dz = Mathf.Max(0f, Mathf.Abs(p.z - b.center.z) - b.extents.z);
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < best) best = d;
            }
            return best;
        }

        private static bool AnyAreaBusy()
        {
            foreach (AreaStreamer a in AreaStreamer.Instances)
                if (a != null && (a.loading || a.unloading || a.LoadingSubScenes > 0)) return true;
            if (global::Singleton<LevelLoadingManager>.IsInstanced())
            {
                LevelLoadingManager l = global::Singleton<LevelLoadingManager>.Instance;
                if (l != null && (!string.IsNullOrEmpty(l.SubSceneNameToLoad) || !string.IsNullOrEmpty(l.SubSceneNameLoading))) return true;
            }
            return Windows.Count > 0;
        }

        private static AreaStreamer AreaOf(string scene)
        {
            if (string.IsNullOrEmpty(scene) || AreaStreamer.Instances == null) return null;
            foreach (AreaStreamer a in AreaStreamer.Instances)
                if (a != null && a.sceneName == scene) return a;
            return null;
        }

        private static bool IsStreamedScene(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (Streamed.Contains(name)) return true;
            if (AreaOf(name) == null) return false;
            Streamed.Add(name);
            return true;
        }

        private static bool IsDlc(AreaStreamer a) =>
            a.sceneName != null && a.sceneName.IndexOf("DLC", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool WantAll(AreaStreamer a) => EffectiveMode() == PreloadKind.All && (!IsDlc(a) || IncludeDlc());

        // ------------------------------------------------------------------ patches: load / pause

        private static void LoadStepPrefix() => _inLoadStep++;

        private static Exception LoadStepFinalizer(Exception __exception)
        {
            if (_inLoadStep > 0) _inLoadStep--;
            return __exception;
        }

        /// <summary>A Load() while the same area is already loading (fast travel during a preload) waits for it.</summary>
        private static bool LoadPrefix(AreaStreamerBase __instance, ref IEnumerator __result)
        {
            if (__instance == null || !__instance.loading || __instance.loaded) return true;
            __result = WaitForRunningLoad(__instance);
            return false;
        }

        private static IEnumerator WaitForRunningLoad(AreaStreamerBase a)
        {
            Plugin.Log.LogInfo($"World streaming: '{a.sceneName}' is already loading, waiting for it");
            while (a != null && a.loading) yield return null;
            yield return new WaitForEndOfFrame();
            // As the game's already-loaded branch: release a pause the caller (fast travel) set for this area.
            if (a != null && global::Singleton<PauseManager>.IsInstanced())
            {
                _ownPause = true;
                try { global::Singleton<PauseManager>.Instance.SetGamePaused(false, a.sceneName); }
                finally { _ownPause = false; }
            }
        }

        /// <summary>
        /// Replaces the timeScale = 0 pause AreaStreamerBase.Load requests during normal play by a load window
        /// (bodies near the area frozen). The unpause marks the end of the game's window.
        /// </summary>
        private static bool SetGamePausedPrefix(bool pause, string reason)
        {
            if (_ownPause) return true;
            if (!pause)
            {
                Window w = FindWindow(reason);
                if (w != null) w.UnpauseSeen = true;
                return true;
            }
            if (_inLoadStep <= 0 || !_removePause.Value) return true;
            AreaStreamer area;
            try
            {
                if (!Ready() || FastTravelScreen()) return true;
                area = AreaOf(reason);
                if (area == null) return true;
                BeginWindow(area);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("World streaming: load window failed, keeping the game's pause: " + ex.Message);
                return true;
            }
            _skippedPauses++;
            if (_skippedPauses <= 5) Plugin.Log.LogInfo($"World streaming: '{reason}' activates without pausing the game (#{_skippedPauses})");
            return false;
        }

        // ------------------------------------------------------------------ load windows (physics-safe no-pause)

        private sealed class FrozenBody
        {
            public Rigidbody Rb;
            public RigidbodyConstraints Prev;
            public Vector3 V, W;
            public int Owners;
        }

        private sealed class Window
        {
            public AreaStreamer Area;
            public string Scene;
            public float Started, NextScan;
            public bool Activated, UnpauseSeen, PausedForPlayer;
            public int ActivatedFrame, ClearFrame = -1;
            public readonly List<Rect> Rects = new List<Rect>();
            public readonly List<long> Tiles = new List<long>();
            public readonly HashSet<int> Mine = new HashSet<int>();
        }

        private static readonly List<Window> Windows = new List<Window>();
        private static readonly Dictionary<int, FrozenBody> Frozen = new Dictionary<int, FrozenBody>();

        private static Window FindWindow(string scene)
        {
            if (scene == null) return null;
            foreach (Window w in Windows) if (w.Scene == scene) return w;
            return null;
        }

        private static void BeginWindow(AreaStreamer area)
        {
            if (FindWindow(area.sceneName) != null) return;
            var w = new Window { Area = area, Scene = area.sceneName, Started = Time.realtimeSinceStartup };
            Windows.Add(w);
            Scan(w);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                Window w = FindWindow(scene.name);
                if (w == null || w.Activated) return;
                w.Activated = true;
                w.ActivatedFrame = Time.frameCount;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Terrain t in root.GetComponentsInChildren<Terrain>(true))
                    {
                        if (t == null || t.terrainData == null) continue;
                        Vector3 p = t.transform.position, s = t.terrainData.size;
                        w.Rects.Add(new Rect(p.x, p.z, s.x, s.z));
                        w.Tiles.Add(((long)Mathf.RoundToInt(p.x) << 32) | (uint)Mathf.RoundToInt(p.z));
                    }
                    foreach (Rigidbody rb in root.GetComponentsInChildren<Rigidbody>(true)) FreezeUnit(w, rb);
                }
                if (w.Area != null && w.Area.GameplayObjects != null)
                    foreach (Rigidbody rb in w.Area.GameplayObjects.GetComponentsInChildren<Rigidbody>(true)) FreezeUnit(w, rb);
                // The local player (or his machine) stands on a new tile: the game's pause for this load, as vanilla.
                if (PlayerPos(out Vector3 me) && OnNewTerrain(w, me, 3f) && global::Singleton<PauseManager>.IsInstanced())
                {
                    w.PausedForPlayer = true;
                    _ownPause = true;
                    try { global::Singleton<PauseManager>.Instance.SetGamePaused(true, w.Scene); }
                    finally { _ownPause = false; }
                    Plugin.Log.LogInfo($"World streaming: you are on a new tile of '{w.Scene}', keeping the game's load pause");
                }
                Scan(w);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("World streaming: scene load window: " + ex.Message); }
        }

        private static bool OnNewTerrain(Window w, Vector3 p, float margin)
        {
            foreach (Rect r in w.Rects)
                if (p.x >= r.xMin - margin && p.x <= r.xMax + margin && p.z >= r.yMin - margin && p.z <= r.yMax + margin) return true;
            return false;
        }

        /// <summary>Freezes every loose non-kinematic body inside the area's boxes or on its new tiles.</summary>
        private static void Scan(Window w)
        {
            w.NextScan = Time.realtimeSinceStartup + 0.25f;
            ReleasePicked(w);
            if (w.Area == null) return;
            foreach (Rigidbody rb in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
            {
                if (rb == null || rb.isKinematic || !rb.detectCollisions || w.Mine.Contains(rb.GetInstanceID())) continue;
                Vector3 p = rb.position;
                if (Distance(w.Area.LoadBounds, p) <= 0f || OnNewTerrain(w, p, 2f)) FreezeUnit(w, rb);
            }
        }

        /// <summary>A machine / trailer is frozen as a whole (never half of a jointed body).</summary>
        private static void FreezeUnit(Window w, Rigidbody rb)
        {
            if (rb == null) return;
            Component unit = (Component)rb.GetComponentInParent<MachineController>() ?? rb.GetComponentInParent<Trailer>();
            if (unit != null)
            {
                if (IsPlayerUnit(unit.gameObject)) return;
                foreach (Rigidbody r in unit.GetComponentsInChildren<Rigidbody>(true)) Freeze(w, r);
                return;
            }
            if (IsPlayerBody(rb)) return;
            Freeze(w, rb);
        }

        private static bool IsPlayerBody(Rigidbody rb)
        {
            Player p = LocalPlayer();
            if (rb.GetComponent<RigidbodyFPSWalker>() != null) return true;
            if (p != null && rb.transform.IsChildOf(p.transform)) return true;
            MachineController cm = p != null ? p.GetControlledMachine() : null;
            return cm != null && rb.transform.IsChildOf(cm.transform);
        }

        private static bool IsPlayerUnit(GameObject unit)
        {
            Player p = LocalPlayer();
            if (p == null) return false;
            if (unit.transform.IsChildOf(p.transform)) return true;
            MachineController cm = p.GetControlledMachine();
            if (cm == null) return false;
            if (unit == cm.gameObject || unit.transform.IsChildOf(cm.transform) || cm.transform.IsChildOf(unit.transform)) return true;
            if (cm is Pickup pk && pk.ConnectedTrailer != null && unit == pk.ConnectedTrailer.gameObject) return true;
            foreach (Joint j in unit.GetComponentsInChildren<Joint>())
                if (j != null && j.connectedBody != null && j.connectedBody.transform.IsChildOf(cm.transform)) return true;
            foreach (Joint j in cm.GetComponentsInChildren<Joint>())
                if (j != null && j.connectedBody != null && j.connectedBody.transform.IsChildOf(unit.transform)) return true;
            return false;
        }

        private static bool IsPicked(Rigidbody rb)
        {
            GameObject a = global::Singleton<GravityGun>.IsInstanced() ? global::Singleton<GravityGun>.Instance.PickedObject : null;
            GameObject b = global::Singleton<ToolGun>.IsInstanced() ? global::Singleton<ToolGun>.Instance.PickedObject : null;
            return a != null && (rb.gameObject == a || rb.transform.IsChildOf(a.transform))
                || b != null && (rb.gameObject == b || rb.transform.IsChildOf(b.transform));
        }

        private static void Freeze(Window w, Rigidbody rb)
        {
            if (rb == null || rb.isKinematic || !rb.detectCollisions || IsPicked(rb)) return;
            int id = rb.GetInstanceID();
            if (!w.Mine.Add(id)) return;
            if (Frozen.TryGetValue(id, out FrozenBody f) && f.Rb != null) { f.Owners++; return; }
            f = new FrozenBody { Rb = rb, Prev = rb.constraints, V = rb.velocity, W = rb.angularVelocity, Owners = 1 };
            Frozen[id] = f;
            rb.constraints = RigidbodyConstraints.FreezeAll;
        }

        private static void Unfreeze(int id)
        {
            if (!Frozen.TryGetValue(id, out FrozenBody f)) return;
            if (--f.Owners > 0) return;
            Frozen.Remove(id);
            Rigidbody rb = f.Rb;
            // Someone else changed the constraints meanwhile (game code): theirs win.
            if (rb == null || rb.constraints != RigidbodyConstraints.FreezeAll) return;
            rb.constraints = f.Prev;
            if (!rb.isKinematic)
            {
                rb.velocity = f.V;
                rb.angularVelocity = f.W;
                rb.WakeUp();
            }
        }

        /// <summary>An object picked up while frozen goes back to normal physics at once.</summary>
        private static void ReleasePicked(Window w)
        {
            if (w.Mine.Count == 0) return;
            List<int> drop = null;
            foreach (int id in w.Mine)
                if (Frozen.TryGetValue(id, out FrozenBody f) && f.Rb != null && IsPicked(f.Rb)) (drop ??= new List<int>()).Add(id);
            if (drop == null) return;
            foreach (int id in drop)
            {
                w.Mine.Remove(id);
                Frozen.TryGetValue(id, out FrozenBody f);
                if (f != null) f.Owners = 1; // fully released, whoever else froze it
                Unfreeze(id);
            }
        }

        private static bool TerrainQueued(Window w)
        {
            if (w.Tiles.Count == 0 || _pendingField == null) return false;
            try
            {
                if (!(_pendingField.GetValue(null) is IDictionary pending) || pending.Count == 0) return false;
                foreach (long k in w.Tiles) if (pending.Contains(k)) return true;
            }
            catch (Exception ex)
            {
                if (!_pendingWarned) { _pendingWarned = true; Plugin.Log.LogWarning("World streaming: terrain queue unavailable: " + ex.Message); }
            }
            return false;
        }

        private static void TickWindows(bool ready)
        {
            if (Windows.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = Windows.Count - 1; i >= 0; i--)
            {
                Window w = Windows[i];
                string why = null;
                if (!ready) why = "world unloading";
                else if (w.Area == null) why = "area gone";
                else if (now - w.Started > 20f) why = "timeout";
                else if (!w.Activated)
                {
                    if (!w.Area.loading && !w.Area.loaded) why = "load cancelled";
                }
                else if ((w.UnpauseSeen || Time.frameCount - w.ActivatedFrame >= 30) && Time.frameCount - w.ActivatedFrame >= 30
                         && w.Area.loaded && !w.Area.loading && w.Area.LoadingSubScenes == 0 && !TerrainQueued(w))
                {
                    if (w.ClearFrame < 0) w.ClearFrame = Time.frameCount;
                    else if (Time.frameCount - w.ClearFrame >= 3) why = "ready";
                }
                else w.ClearFrame = -1;

                if (why == null)
                {
                    if (now >= w.NextScan) { try { Scan(w); } catch (Exception ex) { Plugin.Log.LogWarning("World streaming: scan: " + ex.Message); } }
                    continue;
                }
                int n = w.Mine.Count;
                foreach (int id in w.Mine) { try { Unfreeze(id); } catch { /* destroyed body */ } }
                w.Mine.Clear();
                Windows.RemoveAt(i);
                if (w.PausedForPlayer && global::Singleton<PauseManager>.IsInstanced() && global::Singleton<PauseManager>.Instance.IsPaused(w.Scene))
                {
                    _ownPause = true;
                    try { global::Singleton<PauseManager>.Instance.SetGamePaused(false, w.Scene); }
                    finally { _ownPause = false; }
                }
                string msg = $"World streaming: '{w.Scene}' load window closed ({why}) after {now - w.Started:0.0} s, {n} bodies frozen and restored";
                if (why == "ready") Plugin.Log.LogInfo(msg); else Plugin.Log.LogWarning(msg);
            }
        }

        // ------------------------------------------------------------------ patches: cleanup

        private static bool CleanerSceneLoadPrefix(Scene scene) => !DeferFor(scene.name);
        private static bool CleanerSceneUnloadPrefix(Scene scene) => !DeferFor(scene.name);

        private static bool DeferFor(string scene)
        {
            try
            {
                if (!_deferCleanup.Value || !Ready() || !IsStreamedScene(scene)) return false;
            }
            catch { return false; }
            MarkCleanup();
            return true;
        }

        private static void MarkCleanup()
        {
            if (!_cleanupPending) { _cleanupPending = true; _cleanupSince = Time.realtimeSinceStartup; }
            _cleanupRequests++;
        }

        /// <summary>AreaStreamerBase.UnloadScene: GC.Collect / Resources.UnloadUnusedAssets -> deferred versions.</summary>
        private static IEnumerable<CodeInstruction> UnloadTranspiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo gc = AccessTools.Method(typeof(GC), nameof(GC.Collect), Type.EmptyTypes);
            MethodInfo una = AccessTools.Method(typeof(Resources), nameof(Resources.UnloadUnusedAssets));
            int n = 0;
            foreach (CodeInstruction ci in code)
            {
                if (ci.Calls(gc)) { ci.opcode = OpCodes.Call; ci.operand = AccessTools.Method(typeof(WorldStreaming), nameof(DeferredGcCollect)); n++; }
                else if (ci.Calls(una)) { ci.opcode = OpCodes.Call; ci.operand = AccessTools.Method(typeof(WorldStreaming), nameof(DeferredUnloadUnusedAssets)); n++; }
                yield return ci;
            }
            if (n != 2) Plugin.Log.LogWarning($"World streaming: unload cleanup hook matched {n} calls (expected 2)");
        }

        internal static void DeferredGcCollect()
        {
            if (_deferCleanup.Value && Ready()) { MarkCleanup(); return; }
            GC.Collect();
        }

        internal static AsyncOperation DeferredUnloadUnusedAssets()
        {
            if (_deferCleanup.Value && Ready()) { MarkCleanup(); return null; }
            return Resources.UnloadUnusedAssets();
        }

        // ------------------------------------------------------------------ patches: fast travel

        /// <summary>Selecting a fast travel point on the map starts loading its area.</summary>
        private static void SetupResetButtonPostfix(MapObject __instance)
        {
            try
            {
                if (!_quickTravel.Value || __instance == null || __instance.localeKey != "MAP_FAST_TRAVEL_POINT" || !Ready()) return;
                if (__instance.objectTransform == null || __instance.objectTransform.parent == null) return;
                FastTravelPoint ft = __instance.objectTransform.parent.GetComponent<FastTravelPoint>();
                AreaStreamer a = ft != null ? ft.area : null;
                if (a == null) return;
                _prefetch = a;
                _prefetchUntil = Time.realtimeSinceStartup + 90f;
                if (a.loaded || a.loading || a.unloading || !a.isActiveAndEnabled) return;
                a.StartCoroutine(a.Load());
                Plugin.Log.LogInfo($"World streaming: fast travel point selected, loading '{a.sceneName}' now");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("World streaming: fast travel prefetch: " + ex.Message); }
        }

        private static bool ForceLoadWithoutTrailerPrefix(MapMenu __instance, AreaStreamer sc, CollisionListener col, ref IEnumerator __result)
        {
            if (!_quickTravel.Value || sc == null) return true;
            __result = QuickTravel(__instance, null, default, sc, col, default);
            return false;
        }

        private static bool ForceLoadPrefix(MapMenu __instance, Trailer tr, Vector3 destination, AreaStreamer sc, CollisionListener col, Quaternion rot, ref IEnumerator __result)
        {
            if (!_quickTravel.Value || sc == null || tr == null) return true;
            __result = QuickTravel(__instance, tr, destination, sc, col, rot);
            return false;
        }

        /// <summary>
        /// MapMenu.ForceLoad / ForceLoadWithoutTrailer with the fixed 6.5 / 4.5 s loading-screen hold replaced by
        /// "destination loaded and stable, then 1 s". Everything else is the game's sequence.
        /// </summary>
        private static IEnumerator QuickTravel(MapMenu mm, Trailer tr, Vector3 destination, AreaStreamer sc, CollisionListener col, Quaternion rot)
        {
            MenuManager menus = global::Singleton<MenuManager>.Instance;
            float t0 = Time.realtimeSinceStartup;
            if (tr != null)
            {
                menus.MyMenuLoading.Show(selectFirst: true);
                global::Singleton<PauseManager>.Instance.SetGamePaused(pause: true, sc.sceneName);
                yield return menus.StartCoroutine(sc.Load());
                tr.Teleport(destination, rot);
                tr.transform.SetParent(sc.GameplayObjects);
            }
            else if (!sc.loaded)
            {
                yield return menus.StartCoroutine(sc.Load());
            }
            menus.Close();
            // Destination stable: its area and every area around the player loaded, no load window open.
            float until = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < until && !DestinationStable(sc)) yield return null;
            yield return new WaitForSecondsRealtime(tr != null ? 1.5f : 1f);
            menus.MyMenuLoading.Hide();
            Plugin.Log.LogInfo($"World streaming: fast travel to '{sc.sceneName}' ready after {Time.realtimeSinceStartup - t0:0.0} s");
            yield return new WaitForSecondsRealtime(1f);
            if (mm != null)
            {
                if (tr == null)
                {
                    CollisionListener old = _oldCol(mm);
                    if (old != null) old.OnTriggerExit(null);
                }
                _oldCol(mm) = col;
            }
        }

        private static bool DestinationStable(AreaStreamer sc)
        {
            if (sc == null) return true;
            if (!sc.loaded || sc.loading || sc.LoadingSubScenes > 0 || FindWindow(sc.sceneName) != null) return false;
            if (!PlayerPos(out Vector3 me)) return true;
            foreach (AreaStreamer a in AreaStreamer.Instances)
            {
                if (a == null || Distance(a.LoadBounds, me) > 0f) continue;
                if (!a.loaded || a.loading || FindWindow(a.sceneName) != null) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ per frame

        internal static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            bool ready = Ready();
            TickWindows(ready);

            if (now >= _nextQuality)
            {
                _nextQuality = now + 5f; // re-applied: changing the graphics quality resets these
                if (QualitySettings.asyncUploadTimeSlice != _uploadSlice.Value) QualitySettings.asyncUploadTimeSlice = _uploadSlice.Value;
                if (QualitySettings.asyncUploadBufferSize < _uploadBuffer.Value) QualitySettings.asyncUploadBufferSize = _uploadBuffer.Value;
                if (!QualitySettings.asyncUploadPersistentBuffer) QualitySettings.asyncUploadPersistentBuffer = true;
            }

            List<AreaStreamer> areas = AreaStreamer.Instances;
            if (!ready || areas == null || !PlayerPos(out Vector3 pos))
            {
                // Loading screen: load as fast as possible; a cleanup here costs nothing visible.
                Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.High;
                _preloading = null;
                Players.Clear();
                if (_cleanupPending && !ready) FlushCleanup("loading screen");
                return;
            }

            RefreshPlayers(pos, now);
            bool busy = AnyAreaBusy();
            UpdatePriority(areas);
            KeepAlive(areas, now);
            if (!busy && now >= _nextPreloadAt) StartPreload(areas, now);
            if (_preloading != null && _preloading.loaded && !_preloading.loading)
            {
                Plugin.Log.LogInfo($"World streaming: preloaded '{_preloading.sceneName}' in {now - _preloadStarted:0.0} s");
                _preloading = null;
                _nextPreloadAt = now + 0.75f; // spread consecutive activations
            }
            else if (_preloading != null && !_preloading.loading && !_preloading.loaded) _preloading = null;

            CleanupTick(pos, busy, now);
        }

        private static void UpdatePriority(List<AreaStreamer> areas)
        {
            UnityEngine.ThreadPriority want = _bgPriority.Value;
            if (FastTravelScreen()) want = UnityEngine.ThreadPriority.High;
            else
                foreach (AreaStreamer a in areas)
                {
                    if (a == null || !a.loading) continue;
                    // A player already is in the area's own box (game-triggered, fast travel or preload too late).
                    if (MinDistance(a.LoadBounds) <= 0f) { want = UnityEngine.ThreadPriority.Normal; break; }
                }
            if (Application.backgroundLoadingPriority != want) Application.backgroundLoadingPriority = want;
        }

        private static void KeepAlive(List<AreaStreamer> areas, float now)
        {
            PreloadKind mode = EffectiveMode();
            float keep = EffectiveDistance() + 40f; // hysteresis beyond the preload distance
            if (_prefetch != null && now > _prefetchUntil) _prefetch = null;
            foreach (AreaStreamer a in areas)
            {
                if (a == null || !a.loaded || a.unloading) continue;
                bool hold = _keepVisited.Value || WantAll(a) || a == _prefetch
                    || Distance(a.UnloadBounds, Players[0]) <= (mode == PreloadKind.Off ? 0f : keep)
                    || MinDistance(a.UnloadBounds) <= (mode == PreloadKind.Off ? 0f : keep); // co-op: where any player is
                // The game unloads once timeloaded runs out while the player is outside UnloadBounds.
                if (hold && _timeLoaded(a) < 1f) _timeLoaded(a) = 1f;
            }
        }

        private static void StartPreload(List<AreaStreamer> areas, float now)
        {
            PreloadKind mode = EffectiveMode();
            float reach = mode == PreloadKind.Off ? 0f : EffectiveDistance();
            AreaStreamer best = null;
            float bestD = float.MaxValue;
            foreach (AreaStreamer a in areas)
            {
                if (a == null || a.loaded || a.loading || a.unloading || !a.isActiveAndEnabled || string.IsNullOrEmpty(a.sceneName)) continue;
                float local = Distance(a.LoadBounds, Players[0]);
                float d = MinDistance(a.LoadBounds);
                // Local player inside its box: the game loads it itself. A remote player inside it: we must.
                if (local <= 0f) continue;
                bool want = d <= reach || (Net.Active && d <= 0f) || WantAll(a);
                if (want && d < bestD) { best = a; bestD = d; }
            }
            if (best == null) return;
            try
            {
                _preloading = best;
                _preloadStarted = now;
                best.StartCoroutine(best.Load());
                Plugin.Log.LogInfo($"World streaming: preloading '{best.sceneName}' (nearest player {bestD:0} m from its trigger)");
            }
            catch (Exception ex)
            {
                _preloading = null;
                _nextPreloadAt = now + 10f;
                Plugin.Log.LogWarning($"World streaming: preload of '{best.sceneName}' failed: {ex.Message}");
            }
        }

        private static void CleanupTick(Vector3 pos, bool busy, float now)
        {
            if ((pos - _stillAnchor).sqrMagnitude > 1f) { _stillAnchor = pos; _stillTime = 0f; }
            else _stillTime += Time.unscaledDeltaTime;
            if (!_cleanupPending) return;
            if (busy || Windows.Count > 0) return; // never during a load or a load window
            if (!_deferCleanup.Value) { FlushCleanup("deferral turned off"); return; }
            bool paused = global::Singleton<PauseManager>.IsInstanced() && PauseManager.IsPaused();
            if (paused) FlushCleanup("game paused");
            else if (_stillTime >= 4f) FlushCleanup("player standing still");
            else if (now - _cleanupSince >= _maxDefer.Value) FlushCleanup("max delay");
            else if (_cleanupRequests >= 8 && now - _cleanupSince >= 20f) FlushCleanup("many areas unloaded");
        }

        private static void FlushCleanup(string why)
        {
            int n = _cleanupRequests;
            float waited = Time.realtimeSinceStartup - _cleanupSince;
            _cleanupPending = false;
            _cleanupRequests = 0;
            try { Resources.UnloadUnusedAssets(); } // also runs a GC
            catch (Exception ex) { Plugin.Log.LogWarning("World streaming: cleanup failed: " + ex.Message); return; }
            Plugin.Log.LogInfo($"World streaming: asset cleanup ({n} deferred requests, waited {waited:0} s, {why})");
        }
    }

    /// <summary>Per-frame driver for <see cref="WorldStreaming"/> (added to the plugin's GameObject).</summary>
    internal sealed class WorldStreamingRunner : MonoBehaviour
    {
        private float _quietUntil;

        private void Update()
        {
            if (Time.realtimeSinceStartup < _quietUntil) return;
            try { WorldStreaming.Tick(); }
            catch (Exception ex)
            {
                _quietUntil = Time.realtimeSinceStartup + 5f; // one warning per 5 s at most
                Plugin.Log.LogWarning("World streaming: " + ex.Message);
            }
        }
    }
}
