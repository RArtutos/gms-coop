using System;
using System.Collections.Generic;
using System.IO;
using SaveSystem;
using GameSaveManager = SaveSystem.SaveManager;

namespace GMSCoop
{
    /// <summary>
    /// Client: the global game settings a save load overwrites come back after the session.
    ///
    /// GameStateManager.Deserialize copies the difficulty, random events and the nugget / viewfinder /
    /// repair / pressure / easy wash plant modes of the LOADED save into the global SettingsData.sav. While
    /// a client plays the host's world these are the host's values. SaveTransfer gives the client its own
    /// game back when the session ends (own slot or main menu); this class restores the settings this
    /// PC had before the host's world was loaded, so a client that went back to the main menu (or whose
    /// own save lacks some key) does not keep the host's modes for its next game.
    /// Driven by GoldSync.Tick every frame: a snapshot read every 0.5 s before the host's world, no allocations.
    /// </summary>
    internal static class SessionLifecycle
    {
        private static readonly string[] SettingKeys =
        {
            "OPTIONS_DIFFICULTY", "OPTIONS_ONE_PROMO", "OPTIONS_RANDOM_EVENTS", "GOLD_NUGGET_ENABLED",
            "VIEWFINDER_ENABLED", "REPAIR_MODE_ENABLED", "PRESSURE_MODE_ENABLED", "EASY_WASHPLANT_MODE_ENABLED",
        };

        private static readonly string[] Cand = new string[SettingKeys.Length];
        private static readonly string[] Own = new string[SettingKeys.Length];
        private static bool _candValid, _haveOwn, _inHostWorld;
        private static float _nextCand;

        internal static void Tick()
        {
            try
            {
                if (!SaveTransfer.ClientSession) ApplyLauncherPromo();
                if (Net.Active && Net.IsHost) { HostSync(); return; }
                if (SaveTransfer.ClientSession)
                {
                    if (!_inHostWorld)
                    {
                        _inHostWorld = true;
                        // Still in a host world of an earlier session: keep the older snapshot.
                        if (!_haveOwn && _candValid) { Array.Copy(Cand, Own, Cand.Length); _haveOwn = true; }
                    }
                    return;
                }
                if (_inHostWorld && !Net.Active)
                {
                    _inHostWorld = false;
                    Restore();
                    return;
                }
                if (_inHostWorld) return; // connected, host world not (re)loaded yet
                Snapshot();
            }
            catch (Exception ex) { Plugin.Log.LogError("Session settings: " + ex.Message); }
        }

        // ================================================================= launcher choice

        // "Promoción de un dólar" in the launcher's Ajustes (launcher.ini, key onedollar = 0 / 1; absent =
        // leave the game's own option). A save made with the promo on carries it and turns it back on at
        // every load (GameStateManager.Deserialize), so the choice is enforced here, not only once.
        // Not on a client in the host's world: there the host's value counts (OnHostSettings).
        private static float _nextIni;
        private static DateTime _iniTime;
        private static int _promoPref = -1;

        private static void ApplyLauncherPromo()
        {
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextIni) return;
            _nextIni = now + 2f;
            try
            {
                string ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GMSCoop", "launcher.ini");
                if (!File.Exists(ini)) return;
                DateTime t = File.GetLastWriteTimeUtc(ini);
                if (t != _iniTime)
                {
                    _iniTime = t;
                    _promoPref = -1;
                    foreach (string line in File.ReadAllLines(ini))
                        if (line.StartsWith("onedollar=")) _promoPref = line.EndsWith("1") ? 1 : 0;
                }
                if (_promoPref < 0) return;
                SaveData s = GameSaveManager.GetSettingsData();
                if (s == null) return;
                if (s.HasKey("OPTIONS_ONE_PROMO") && s.GetInt("OPTIONS_ONE_PROMO") == _promoPref) return;
                s.SetInt("OPTIONS_ONE_PROMO", _promoPref);
                s.Save();
                Plugin.Log.LogInfo("One dollar promo set to " + _promoPref + " (launcher)");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("One dollar promo: " + ex.Message); }
        }

        // ================================================================= host -> clients

        // The host's gameplay settings (one dollar promo, difficulty, nugget / repair / pressure modes...)
        // are read from the global SettingsData by the shops, the bank and the workers. A client gets them
        // from the host's save when it loads the world, but a change the host makes later in Options
        // (e.g. turning the one dollar promo on or off) stayed on the host only: the client saw other
        // prices than the host. The host sends its values whenever they change and to every new client.
        private static readonly int[] Sent = new int[SettingKeys.Length];
        private static readonly HashSet<ulong> SettingsSentTo = new HashSet<ulong>();
        private static float _nextHostCheck, _resendAt;
        private static int _sentWorld = int.MinValue;

        private static void HostSync()
        {
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextHostCheck || Net.Transport == null) return;
            _nextHostCheck = now + 1f;
            SaveData s = GameSaveManager.GetSettingsData();
            if (s == null) return;
            bool changed = false;
            for (int i = 0; i < SettingKeys.Length; i++)
            {
                int v = s.HasKey(SettingKeys[i]) ? s.GetInt(SettingKeys[i]) : int.MinValue;
                if (v != Sent[i]) { Sent[i] = v; changed = true; }
            }
            // The host (re)loaded a world: the clients reload it too, and the save load copies that save's values
            // into their settings (GameStateManager.Deserialize), which may be older than the host's current
            // ones. Send again once the host plays, and once more a little later for clients still loading.
            int world = GameState.WorldVersion;
            if (world != _sentWorld && !GameState.IsLoading)
            {
                _sentWorld = world;
                changed = true;
                _resendAt = now + 15f;
            }
            else if (_resendAt > 0f && now >= _resendAt)
            {
                _resendAt = 0f;
                changed = true;
            }
            if (changed) SettingsSentTo.Clear();
            SettingsSentTo.RemoveWhere(p => !Net.Peers.Contains(p));
            byte[] data = null;
            foreach (ulong p in Net.Peers)
            {
                if (SettingsSentTo.Contains(p) || !SaveTransfer.PeerReady(p)) continue;
                if (data == null)
                {
                    var ms = CoopSession.Begin(CoopSession.SubSettings, out var w);
                    w.Write((byte)SettingKeys.Length);
                    for (int i = 0; i < SettingKeys.Length; i++) { w.Write(SettingKeys[i]); w.Write(Sent[i]); }
                    data = ms.ToArray();
                }
                Net.Transport.Send(p, data, true);
                SettingsSentTo.Add(p);
            }
        }

        /// <summary>Client: the host's current gameplay settings, applied while playing the host's world.</summary>
        internal static void OnHostSettings(BinaryReader r)
        {
            int n = r.ReadByte();
            SaveData s = GameSaveManager.GetSettingsData();
            bool apply = s != null && SaveTransfer.ClientSession;
            for (int i = 0; i < n; i++)
            {
                string key = r.ReadString();
                int v = r.ReadInt32();
                if (!apply || Array.IndexOf(SettingKeys, key) < 0) continue;
                if (v == int.MinValue) { if (s.HasKey(key)) s.DeleteKey(key); }
                else if (!s.HasKey(key) || s.GetInt(key) != v)
                {
                    s.SetInt(key, v);
                    if (key == "OPTIONS_ONE_PROMO")
                        Plugin.Instance.SetStatus(v == 1 ? "El host activó la promoción de un dólar" : "El host quitó la promoción de un dólar");
                }
            }
        }

        private static void Snapshot()
        {
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextCand) return;
            _nextCand = now + 0.5f;
            if (GameSaveManager.GetCurrentSlotName() == SaveTransfer.CoopSlot) { _candValid = false; return; }
            SaveData s = GameSaveManager.GetSettingsData();
            if (s == null) return;
            for (int i = 0; i < SettingKeys.Length; i++)
                Cand[i] = s.HasKey(SettingKeys[i]) ? s.GetString(SettingKeys[i]) : null;
            _candValid = true;
        }

        private static void Restore()
        {
            if (!_haveOwn) return;
            _haveOwn = false;
            SaveData s = GameSaveManager.GetSettingsData();
            if (s == null) return;
            bool changed = false;
            for (int i = 0; i < SettingKeys.Length; i++)
            {
                bool has = s.HasKey(SettingKeys[i]);
                if (Own[i] == null) { if (has) { s.DeleteKey(SettingKeys[i]); changed = true; } }
                else if (!has || s.GetString(SettingKeys[i]) != Own[i]) { s.SetString(SettingKeys[i], Own[i]); changed = true; }
            }
            if (!changed) return;
            s.Save();
            Plugin.Log.LogInfo("Session: own game settings restored (difficulty / gameplay modes)");
        }
    }
}
