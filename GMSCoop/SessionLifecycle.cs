using System;
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
                if (Net.Active && Net.IsHost) return;
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
