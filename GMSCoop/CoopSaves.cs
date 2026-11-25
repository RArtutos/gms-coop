using System;
using System.IO;
using BepInEx.Configuration;
using HarmonyLib;
using SaveSystem;
using UnityEngine;
using UnityEngine.SceneManagement;
using GameSaveManager = SaveSystem.SaveManager;

namespace GMSCoop
{
    /// <summary>
    /// Saving in co-op, so a crash or a lost host never costs progress:
    ///  - Host: saves the shared world every few minutes (config "Saves/HostAutosaveMinutes", default 5),
    ///    when a player leaves, and on F7, with the game's own QuickSave (the pause-menu save). Never
    ///    while loading, saving or sending the world to a joiner.
    ///  - Client: keeps a rolling backup of the shared world in its own slot "Coop_Respaldo_&lt;host&gt;_&lt;world&gt;":
    ///    every few minutes and when the session ends. The game saves the client's copy into Coop_Host
    ///    (SaveTransfer redirects it there); the folder is then copied to the backup slot. A personal save,
    ///    a copy kept alone (solo) or a newer copy of the world is never overwritten.
    ///  - Host lost (no Bye: crash, connection gone): the client gets "[G] Guardar y seguir solo /
    ///    [M] Volver al menú" (game input is paused while it is shown). G copies the world into a new
    ///    slot "Coop_Respaldo_&lt;host&gt;_solo_&lt;date&gt;", marks it solo and keeps playing it alone.
    ///
    /// World identity follows the LOADED world, not a slot name: when a world finishes loading from a save
    /// (a new game has none), the identity of the slot it came from is recorded (only if that json still
    /// belongs to the save, see WorldIdentity.IsFresh). Every save of that world (pause menu, autosave into
    /// "AutoSave", ours) writes the identity with revision + 1 into the slot saved, once the game reports
    /// the save written; saving a world without identity deletes a stale json left in that slot. Mod saves
    /// on the host go to the world's own slot, not to the rotating "AutoSave" / "QuickSave". The host creates the identity on its first save in a session and sends it to
    /// every client after each save (CoopSession), so Coop_Host on clients always carries the host's
    /// current revision. A solo copy records the host revision it started from (BaseRevision).
    /// </summary>
    internal static class CoopSaves
    {
        private static ConfigEntry<float> _hostMinutes;
        private static ConfigEntry<float> _clientMinutes;

        private static float _nextHostSave = -1f, _nextClientBackup = -1f;
        private static int _lastPeerCount;
        private static DateTime _lastSaveUtc;          // last co-op save (host save or client backup)
        private static string _pendingBackup;          // client: copy Coop_Host -> this slot once the save is done
        private static float _pendingSince;
        private static bool _hostLostPrompt;
        private static bool _keepAlone;                // G pressed: the pending copy becomes the player's solo world
        private static string _hostName;               // client: the host of the current session
        private static bool _wasClient;

        // Identity of the loaded world (by scene handle).
        private static int _trackScene = -1;
        private static WorldIdentity _tracked;
        private static int _loadedRevision;            // revision the world had when it was loaded
        private static float _playStart = -1f;
        private static string _loadSlot;               // slot of the save load the game accepted: the next world comes from it
        private static string _worldSlot;              // slot the loaded world belongs to (loaded from, or last saved to by name)

        // A save was started: its identity is written once the game reports it written (OnSave OK).
        private static string _stampSlot;
        private static int _stampScene;
        private static bool _inBusySave;               // inside a QuickSave the game refuses (IS_BUSY)

        private static string _pendingWorldId;         // client: worldId of Coop_Host when the backup save started
        private static bool _cleaned;

        /// <summary>SaveTransfer waits with leaving the host's world while this is true.</summary>
        internal static bool HoldRestore => _pendingBackup != null || _hostLostPrompt;

        /// <summary>A prompt that reads plain keys is shown: game input is paused (FocusInput).</summary>
        internal static bool PromptOpen => _hostLostPrompt;

        internal static void Init(ConfigFile config)
        {
            _hostMinutes = config.Bind("Saves", "HostAutosaveMinutes", 5f, "Host: save the shared world every N minutes during a session (0 = off).");
            _clientMinutes = config.Bind("Saves", "ClientBackupMinutes", 5f, "Client: back up the shared world into Coop_Respaldo_<host> every N minutes (0 = off).");
        }

        // ---------------------------------------------------------------- tick

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (!_cleaned && !Net.Active && !string.IsNullOrEmpty(GameSaveManager.GetSavePath()))
            {
                _cleaned = true;
                WorldIdentity.CleanLeftovers();
            }
            TrackLoadedWorld(now);

            if (Net.Active && Net.IsHost) HostTick(now);
            else { _nextHostSave = -1f; _lastPeerCount = 0; }

            if (SaveTransfer.ClientSession)
            {
                _wasClient = true;
                if (Net.Peers.Count > 0) _hostName = Net.NameOf(Net.Peers[0]);
                ClientTick(now);
            }
            else if (!Net.Active) _nextClientBackup = -1f;

            PendingBackupTick(now);
            PromptTick();
        }

        /// <summary>
        /// A world finished loading: record the identity of the slot it was loaded from. Only after a real
        /// save load (OnLoadSaveData accepted it): a new game keeps the previous slot name, and must not
        /// inherit the identity of the world that slot holds.
        /// </summary>
        private static void TrackLoadedWorld(float now)
        {
            if (GameState.IsLoading) return;
            Scene scene = SceneManager.GetActiveScene();
            if (scene.buildIndex == 0) { _trackScene = -1; _tracked = null; _loadSlot = null; _worldSlot = null; return; }
            if (scene.handle == _trackScene) return;
            _trackScene = scene.handle;
            string slot = _loadSlot;
            _loadSlot = null;
            _worldSlot = slot;
            _tracked = slot != null ? WorldIdentity.ReadFresh(slot) : null;
            _loadedRevision = _tracked?.Revision ?? 0;
            _playStart = now;
            if (_tracked != null) Plugin.Log.LogInfo($"World identity: loaded co-op world {_tracked.WorldId.Substring(0, 8)} rev {_tracked.Revision} from '{slot}'");
            else if (slot == null) Plugin.Log.LogInfo("World identity: new world (not loaded from a save)");
        }

        /// <summary>
        /// Host, before a mod save: the game's autosave / F5 leave the current slot on "AutoSave" / "QuickSave"
        /// (rotating slots any other world overwrites). Mod saves go back to the slot this world belongs to.
        /// </summary>
        internal static void UseWorldSlot()
        {
            if (SceneManager.GetActiveScene().handle != _trackScene) return;
            if (_worldSlot == null)
            {
                // A new game keeps the previous world's slot name current: never save it over that world.
                _worldSlot = WorldIdentity.FreeSlotName("Coop_Partida_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Plugin.Instance.SetStatus("Mundo nuevo: se guarda en la ranura '" + _worldSlot + "'");
            }
            if (_worldSlot == SaveTransfer.CoopSlot) return;
            string cur = GameSaveManager.GetCurrentSlotName();
            if (cur == _worldSlot) return;
            Plugin.Log.LogInfo($"Co-op save: current slot '{cur}' set back to the world's slot '{_worldSlot}'");
            GameSaveManager.SetCurrentSlot(_worldSlot);
        }

        private static bool CanSaveNow()
        {
            if (GameState.IsLoading || SaveBusy() || CoopSession.InstallPending) return false;
            if (SceneManager.GetActiveScene().buildIndex == 0) return false;
            CheckpointManager cpm = Checkpoints();
            return cpm != null && cpm.GetLoadingState() == LoadingState.ReadyToPlay;
        }

        private static void HostTick(float now)
        {
            int peers = Net.Peers.Count;
            bool someoneLeft = peers < _lastPeerCount;
            _lastPeerCount = peers;
            if (_nextHostSave < 0f) _nextHostSave = now + Minutes(_hostMinutes) * 60f;
            bool due = Minutes(_hostMinutes) > 0f && now >= _nextHostSave && peers > 0;
            if (!someoneLeft && !due) return;
            if (!SaveTransfer.HostTransferIdle || !CanSaveNow()) { if (someoneLeft) _lastPeerCount = peers + 1; return; } // retry
            _nextHostSave = now + Math.Max(1f, Minutes(_hostMinutes)) * 60f;
            QuickSave(someoneLeft ? "un jugador salió" : "autoguardado");
        }

        /// <summary>F7 / leaving on the host: one last save of the shared world.</summary>
        internal static void BeforeHostLeaves()
        {
            if (!Net.Active || !Net.IsHost || Net.Peers.Count == 0) return;
            if (SaveTransfer.HostTransferIdle && CanSaveNow()) QuickSave("fin de la sesión");
        }

        private static void ClientTick(float now)
        {
            if (_nextClientBackup < 0f) _nextClientBackup = now + Minutes(_clientMinutes) * 60f;
            if (Minutes(_clientMinutes) <= 0f || now < _nextClientBackup || _pendingBackup != null) return;
            // Not while a (new) world is announced, received or loaded: Coop_Host is about to be replaced.
            if (!SaveTransfer.ClientIdle || !CanSaveNow()) return;
            _nextClientBackup = now + Math.Max(1f, Minutes(_clientMinutes)) * 60f;
            StartBackup(RoutineSlot(), silent: true);
        }

        // ---------------------------------------------------------------- client backups

        /// <summary>Routine backup slot: one per host and world.</summary>
        private static string RoutineSlot()
        {
            WorldIdentity src = WorldIdentity.Read(SaveTransfer.CoopSlot);
            string slot = WorldIdentity.BackupSlotFor(_hostName ?? "host");
            return src != null ? slot + "_" + src.WorldId.Substring(0, Math.Min(6, src.WorldId.Length)) : slot;
        }

        private static string SoloSlot() =>
            WorldIdentity.FreeSlotName(WorldIdentity.BackupSlotFor(_hostName ?? "host") + "_solo_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        /// <summary>Saves the shared world into Coop_Host (SaveTransfer redirects it), then copies it to <paramref name="slot"/>.</summary>
        private static void StartBackup(string slot, bool silent)
        {
            try
            {
                CheckpointManager cpm = Checkpoints();
                if (cpm == null) return;
                _pendingBackup = slot;
                _pendingSince = Time.unscaledTime;
                _pendingWorldId = WorldIdentity.Read(SaveTransfer.CoopSlot)?.WorldId;
                cpm.QuickSave();
                if (!silent) Plugin.Instance.SetStatus("Guardando una copia del mundo co-op...");
            }
            catch (Exception ex)
            {
                _pendingBackup = null;
                Plugin.Log.LogWarning("Co-op backup: save failed: " + ex.Message);
            }
        }

        private static void PendingBackupTick(float now)
        {
            if (_pendingBackup == null) return;
            if (now - _pendingSince < 1f || SaveBusy())
            {
                if (now - _pendingSince > 60f) { Plugin.Log.LogWarning("Co-op backup: the save did not finish"); _pendingBackup = null; _keepAlone = false; }
                return;
            }
            string slot = _pendingBackup;
            _pendingBackup = null;
            try
            {
                WorldIdentity src = WorldIdentity.Read(SaveTransfer.CoopSlot);
                if (src?.WorldId != _pendingWorldId)
                {
                    // Coop_Host was replaced by another world meanwhile (a new world from the host).
                    Plugin.Log.LogWarning($"Co-op backup: Coop_Host holds another world now: '{slot}' not written");
                    if (_keepAlone) Plugin.Instance.SetStatus("No se pudo guardar la copia del mundo co-op: el mundo cambió");
                    _keepAlone = false;
                    return;
                }
                if (!_keepAlone)
                {
                    // Routine backup: never over a solo copy or a newer copy of the world.
                    WorldIdentity dst = WorldIdentity.Read(slot);
                    if (dst != null && (dst.Solo || (src != null && dst.WorldId == src.WorldId && dst.Revision > src.Revision)))
                    {
                        Plugin.Log.LogWarning($"Co-op backup: '{slot}' holds a solo or newer copy: not overwritten");
                        return;
                    }
                }
                CopySlot(SaveTransfer.CoopSlot, slot);
                if (_keepAlone)
                {
                    _keepAlone = false;
                    WorldIdentity id = (_tracked ?? src)?.Clone() ?? new WorldIdentity { WorldId = Guid.NewGuid().ToString("N") };
                    id.BaseRevision = (_tracked ?? src)?.Revision ?? 0;
                    id.Solo = true;
                    id.Revision = id.BaseRevision + 1;
                    id.LastSavedUtc = DateTime.UtcNow;
                    id.LastSavedBy = Net.LocalName ?? Environment.MachineName;
                    id.Write(slot);
                    _tracked = id; // this loaded world is now the player's solo copy
                    _loadedRevision = id.BaseRevision;
                    _worldSlot = slot;
                    SaveTransfer.KeepWorldAsOwn(slot);
                    Plugin.Instance.SetStatus("Mundo guardado en '" + slot + "'. Sigues jugando solo; puedes abrirlo como host desde GMS Co-op.");
                }
                else
                {
                    WorldIdentity.Touch(slot); // the json's file time follows the copied save (IsFresh)
                    Plugin.Log.LogInfo($"Co-op backup: {slot} (rev {src?.Revision})");
                }
                _lastSaveUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _keepAlone = false;
                Plugin.Log.LogWarning("Co-op backup: copy failed: " + ex.Message);
                Plugin.Instance.SetStatus("No se pudo guardar la copia del mundo co-op: " + ex.Message);
            }
        }

        private static void CopySlot(string from, string to)
        {
            string src = WorldIdentity.SlotDir(from), dst = WorldIdentity.SlotDir(to);
            if (!File.Exists(Path.Combine(src, "CheckpointData.sav"))) throw new IOException("no hay copia del mundo en " + from);
            if (!to.StartsWith(WorldIdentity.BackupPrefix, StringComparison.Ordinal)) throw new InvalidOperationException("solo se escriben ranuras de respaldo");
            string tmp = dst + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)), true);
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
            Directory.Move(tmp, dst);
        }

        /// <summary>SaveTransfer: the client's session just ended (any reason), still inside the host's world.</summary>
        internal static void OnClientSessionEnded()
        {
            if (!_wasClient) return;
            _wasClient = false;
            bool hostLost = Net.EndReason != null && Net.EndReason.StartsWith("se perdió la conexión", StringComparison.Ordinal);
            if (hostLost)
            {
                _hostLostPrompt = true;
                Plugin.Instance.SetStatus("El host se desconectó.  [G] Guardar y seguir solo  /  [M] Volver al menú");
                return;
            }
            if (CanSaveNow()) StartBackup(RoutineSlot(), silent: false);
        }

        private static void PromptTick()
        {
            if (!_hostLostPrompt) return;
            if (Net.Active) { _hostLostPrompt = false; return; } // joined again meanwhile
            // Game input is paused while the prompt is up (FocusInput), so G / M reach only this prompt.
            if (Input.GetKeyDown(KeyCode.G))
            {
                if (!CanSaveNow()) { Plugin.Instance.SetStatus("El juego está ocupado: vuelve a pulsar G en un momento"); return; }
                _hostLostPrompt = false;
                _keepAlone = true;
                StartBackup(SoloSlot(), silent: false);
            }
            else if (Input.GetKeyDown(KeyCode.M))
            {
                _hostLostPrompt = false;
                if (CanSaveNow()) StartBackup(RoutineSlot(), silent: false); // a copy anyway, then leave
                Plugin.Instance.SetStatus("Volviendo al menú (se guardó una copia del mundo co-op)");
            }
        }

        // ---------------------------------------------------------------- saves + identity

        /// <summary>Host: saves the shared world into the slot it belongs to.</summary>
        private static void QuickSave(string why)
        {
            try
            {
                UseWorldSlot();
                Checkpoints()?.QuickSave();
                Plugin.Log.LogInfo("Co-op save: " + why);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Co-op save failed: " + ex.Message); }
        }

        [HarmonyPatch(typeof(CheckpointManager), nameof(CheckpointManager.QuickSave))]
        private static class QuickSaveStamp
        {
            /// <summary>No save while an uploaded world replaces the host's slot (CoopSession.Install).</summary>
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(out bool __state)
            {
                // The game refuses a save while another one is being written (IS_BUSY): nothing to stamp.
                __state = SaveBusy();
                _inBusySave = __state;
                if (!CoopSession.InstallPending) return true;
                Plugin.Log.LogInfo("Co-op save skipped: a world from another player is being installed");
                return false;
            }

            [HarmonyPriority(Priority.Low)]
            private static void Postfix(bool __runOriginal, bool __state)
            {
                _inBusySave = false;
                if (!__runOriginal || __state) return; // a prefix skipped this save, or the game refused it
                if (!SaveBusy()) return;               // the save did not start
                // The file is written on a worker task: the identity follows once the game reports OK.
                _stampSlot = GameSaveManager.GetCurrentSlotName();
                _stampScene = SceneManager.GetActiveScene().handle;
            }
        }

        /// <summary>The game accepted a save load: the world that loads next comes from the current slot.</summary>
        [HarmonyPatch(typeof(CheckpointManager), "OnLoadSaveData")]
        private static class LoadSlotTrack
        {
            private static void Postfix(CheckpointManager __instance)
            {
                if (__instance.GetLoadingState() != LoadingState.ReadyToPlay) _loadSlot = GameSaveManager.GetCurrentSlotName();
            }
        }

        /// <summary>The game finished a save: stamp its identity only if it was written (not busy / disk full).</summary>
        [HarmonyPatch(typeof(CheckpointManager), nameof(CheckpointManager.OnSave))]
        private static class OnSaveStamp
        {
            private static void Postfix(GameSaveManager.ERRORS err)
            {
                if (_inBusySave) return; // the refusal of a second save, not the end of the pending one
                string slot = _stampSlot;
                int scene = _stampScene;
                _stampSlot = null;
                if (slot == null) return;
                if (err != GameSaveManager.ERRORS.OK)
                {
                    Plugin.Log.LogWarning($"World identity: save to '{slot}' failed ({err}), identity not updated");
                    return;
                }
                try { Stamp(slot, scene); }
                catch (Exception ex) { Plugin.Log.LogWarning("World identity: stamp failed: " + ex.Message); }
            }
        }

        /// <summary>Writes the loaded world's identity (revision + 1) into the slot just saved.</summary>
        private static void Stamp(string slot, int scene)
        {
            if (string.IsNullOrEmpty(slot)) return;
            // Coop_Host: the host's identity (OnHostIdentity); its file time follows the save (IsFresh).
            if (slot == SaveTransfer.CoopSlot) { WorldIdentity.Touch(slot); return; }
            if (Net.Active && !Net.IsHost) return;                                   // client saves go to Coop_Host
            if (SceneManager.GetActiveScene().handle != scene) return;               // another world was loaded meanwhile
            bool hostSession = Net.Active && Net.IsHost;
            if (scene != _trackScene) { _trackScene = scene; _tracked = null; }
            if (slot != "AutoSave" && slot != "QuickSave") _worldSlot = slot;      // saved by name: the world's slot now
            if (_tracked == null)
            {
                if (!hostSession) { WorldIdentity.Delete(slot); return; } // not a co-op world: drop a stale json
                _tracked = new WorldIdentity { WorldId = Guid.NewGuid().ToString("N") };
                _loadedRevision = 0;
            }
            if (hostSession && _tracked.Solo) _tracked.Solo = false; // hosting it makes it the group's world again
            _tracked.Revision++;
            _tracked.LastSavedUtc = DateTime.UtcNow;
            _tracked.LastSavedBy = Net.LocalName ?? Environment.MachineName;
            if (_playStart >= 0f) _tracked.MinutesPlayed += (Time.unscaledTime - _playStart) / 60f;
            _playStart = Time.unscaledTime;
            _tracked.Write(slot);
            if (hostSession)
            {
                _lastSaveUtc = DateTime.UtcNow;
                CoopSession.BroadcastIdentity(_tracked, _loadedRevision);
            }
        }

        /// <summary>Host: identity of the world it plays (null before its first save in a session).</summary>
        internal static WorldIdentity HostIdentity =>
            _trackScene == SceneManager.GetActiveScene().handle ? _tracked : null;

        /// <summary>Host: revision the world had when the host loaded it.</summary>
        internal static int HostLoadedRevision => _loadedRevision;

        /// <summary>Client: the host saved; Coop_Host's json and the tracked identity follow it.</summary>
        internal static void OnHostIdentity(WorldIdentity id)
        {
            if (!SaveTransfer.ClientSession || id == null) return;
            if (_tracked == null || _tracked.WorldId == id.WorldId)
            {
                _tracked = id;
                _trackScene = SceneManager.GetActiveScene().handle;
            }
            try { id.Write(SaveTransfer.CoopSlot); }
            catch (Exception ex) { Plugin.Log.LogWarning("World identity: Coop_Host update failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------- overlay

        internal static void AppendOverlay(System.Text.StringBuilder sb)
        {
            if (Net.Active && _lastSaveUtc != default)
            {
                double min = (DateTime.UtcNow - _lastSaveUtc).TotalMinutes;
                sb.Append("\n<color=#A89A86>").Append(Net.IsHost ? "Guardado " : "Copia guardada ")
                  .Append(min < 1 ? "hace menos de 1 min" : "hace " + (int)min + " min").Append("</color>");
            }
            if (_hostLostPrompt)
                sb.Append("\n<color=#FFE14C><b>El host se desconectó.</b>  [G] Guardar y seguir solo   [M] Volver al menú  (el juego espera tu elección)</color>");
        }

        // ---------------------------------------------------------------- helpers

        private static float Minutes(ConfigEntry<float> e) => e == null ? 5f : Mathf.Max(0f, e.Value);

        internal static bool SaveBusy() =>
            SaveSystem.Singleton<GameSaveManager>.IsInstanced() && SaveSystem.Singleton<GameSaveManager>.Instance.CheckIfBusy();

        internal static CheckpointManager Checkpoints() =>
            global::Singleton<CheckPointManagerBase>.IsInstanced() ? global::Singleton<CheckPointManagerBase>.Instance as CheckpointManager : null;
    }
}
