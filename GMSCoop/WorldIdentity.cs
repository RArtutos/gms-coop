using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using GameSaveManager = SaveSystem.SaveManager;

namespace GMSCoop
{
    /// <summary>
    /// Co-op world identity: gmscoop_world.json inside a save slot folder. The same world carries the same
    /// worldId everywhere (the host's slot, Coop_Host on clients, Coop_Respaldo_* backups); revision goes
    /// up by one on every co-op save (host save or client backup), so a group can tell which copy is newest.
    /// The file travels with the world: SaveTransfer packs every file of the slot folder.
    /// The game ignores it (it only reads CheckpointData.sav, Screenshot.jpg and SmallDescription.txt).
    /// </summary>
    internal sealed class WorldIdentity
    {
        internal const string FileName = "gmscoop_world.json";
        internal const string BackupPrefix = "Coop_Respaldo_";

        public string WorldId;
        public int Revision;
        public DateTime LastSavedUtc;
        public string LastSavedBy = "";
        public double MinutesPlayed;
        // A copy a client kept playing alone after the host was lost: never overwritten by routine backups.
        public bool Solo;
        // Solo copies: the host revision the solo play started from.
        public int BaseRevision;

        internal static string SlotDir(string slot) => GameSaveManager.GetSlotSavePath(slot);

        internal static WorldIdentity Read(string slot)
        {
            try
            {
                if (string.IsNullOrEmpty(slot)) return null;
                string path = Path.Combine(SlotDir(slot), FileName);
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path, Encoding.UTF8);
                var id = new WorldIdentity
                {
                    WorldId = Str(json, "worldId"),
                    LastSavedBy = Str(json, "lastSavedBy") ?? "",
                };
                if (string.IsNullOrEmpty(id.WorldId)) return null;
                int.TryParse(Raw(json, "revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out id.Revision);
                double.TryParse(Raw(json, "minutesPlayed"), NumberStyles.Float, CultureInfo.InvariantCulture, out id.MinutesPlayed);
                int.TryParse(Raw(json, "baseRevision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out id.BaseRevision);
                id.Solo = Regex.IsMatch(json, "\"solo\"\\s*:\\s*true");
                DateTime.TryParse(Str(json, "lastSaved"), CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out id.LastSavedUtc);
                return id;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("World identity: could not read " + slot + ": " + ex.Message);
                return null;
            }
        }

        internal void Write(string slot)
        {
            string dir = SlotDir(slot);
            if (!Directory.Exists(dir)) return;
            string json = "{\n" +
                "  \"worldId\": \"" + Esc(WorldId) + "\",\n" +
                "  \"revision\": " + Revision.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"lastSaved\": \"" + LastSavedUtc.ToString("o", CultureInfo.InvariantCulture) + "\",\n" +
                "  \"lastSavedBy\": \"" + Esc(LastSavedBy) + "\",\n" +
                "  \"minutesPlayed\": " + MinutesPlayed.ToString("0.#", CultureInfo.InvariantCulture) + ",\n" +
                "  \"solo\": " + (Solo ? "true" : "false") + ",\n" +
                "  \"baseRevision\": " + BaseRevision.ToString(CultureInfo.InvariantCulture) + "\n" +
                "}\n";
            File.WriteAllText(Path.Combine(dir, FileName), json, new UTF8Encoding(false));
        }

        internal WorldIdentity Clone() => (WorldIdentity)MemberwiseClone();

        /// <summary>
        /// The json belongs to the save next to it: false when CheckpointData.sav was written well after the
        /// json (the slot was reused by another world, for example "AutoSave", and the json is stale).
        /// </summary>
        internal static bool IsFresh(string slot, WorldIdentity id)
        {
            try
            {
                if (id == null || id.LastSavedUtc == default) return false;
                string data = Path.Combine(SlotDir(slot), "CheckpointData.sav");
                return File.Exists(data) && File.GetLastWriteTimeUtc(data) <= id.LastSavedUtc.AddMinutes(3);
            }
            catch { return false; }
        }

        /// <summary>Reads the identity of a slot only if it still belongs to the save in it.</summary>
        internal static WorldIdentity ReadFresh(string slot)
        {
            WorldIdentity id = Read(slot);
            return IsFresh(slot, id) ? id : null;
        }

        internal static void Delete(string slot)
        {
            try
            {
                string path = Path.Combine(SlotDir(slot), FileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        /// <summary>Local slots with this worldId (name, identity), newest revision first.</summary>
        internal static List<KeyValuePair<string, WorldIdentity>> FindCopies(string worldId)
        {
            var list = new List<KeyValuePair<string, WorldIdentity>>();
            try
            {
                string root = GameSaveManager.GetSavePath();
                if (string.IsNullOrEmpty(worldId) || !Directory.Exists(root)) return list;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string slot = Path.GetFileName(dir);
                    if (!File.Exists(Path.Combine(dir, FileName)) || !File.Exists(Path.Combine(dir, "CheckpointData.sav"))) continue;
                    WorldIdentity id = ReadFresh(slot);
                    if (id != null && id.WorldId == worldId) list.Add(new KeyValuePair<string, WorldIdentity>(slot, id));
                }
                list.Sort((a, b) => b.Value.Revision != a.Value.Revision ? b.Value.Revision.CompareTo(a.Value.Revision)
                                                                        : b.Value.LastSavedUtc.CompareTo(a.Value.LastSavedUtc));
            }
            catch (Exception ex) { Plugin.Log.LogWarning("World identity: slot scan failed: " + ex.Message); }
            return list;
        }

        internal static string BackupSlotFor(string hostName)
        {
            var sb = new StringBuilder();
            foreach (char c in hostName ?? "host")
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            string s = sb.ToString().Trim('_');
            if (s.Length == 0) s = "host";
            if (s.Length > 32) s = s.Substring(0, 32);
            return BackupPrefix + s;
        }

        internal static string Friendly(DateTime utc) => utc == default ? "?" : utc.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);

        private static string Str(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
        }

        private static string Raw(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*([-0-9.eE]+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
    }
}
