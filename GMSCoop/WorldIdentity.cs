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
        /// Both times are file times of this PC (never LastSavedUtc, which may come from another PC's clock
        /// or from a save made long before the files were copied / received).
        /// </summary>
        internal static bool IsFresh(string slot, WorldIdentity id)
        {
            try
            {
                if (id == null) return false;
                string dir = SlotDir(slot);
                string data = Path.Combine(dir, "CheckpointData.sav"), json = Path.Combine(dir, FileName);
                return File.Exists(data) && File.Exists(json) &&
                       File.GetLastWriteTimeUtc(data) <= File.GetLastWriteTimeUtc(json).AddMinutes(3);
            }
            catch { return false; }
        }

        /// <summary>
        /// Rewrites the slot's json unchanged, so its file time follows the save next to it (after a copy, an
        /// unpack or a save into the slot that is not stamped, e.g. a client's save into Coop_Host).
        /// </summary>
        internal static void Touch(string slot)
        {
            try
            {
                WorldIdentity id = Read(slot);
                if (id != null) id.Write(slot);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("World identity: could not refresh " + slot + ": " + ex.Message); }
        }

        /// <summary>A slot name not used yet: <paramref name="name"/>, else name_2, name_3...</summary>
        internal static string FreeSlotName(string name)
        {
            if (!Directory.Exists(SlotDir(name))) return name;
            for (int i = 2; i < 1000; i++)
                if (!Directory.Exists(SlotDir(name + "_" + i))) return name + "_" + i;
            return name + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>
        /// Startup: removes the mod's own temp folders left by a crash or a failed step (they hold a
        /// CheckpointData.sav, so the game's load menu would list them as slots). Only these exact patterns,
        /// only directly inside the Saves folder: "*.incoming", "*.rechazada", "Coop_Respaldo_*.tmp".
        /// </summary>
        internal static void CleanLeftovers()
        {
            try
            {
                string root = GameSaveManager.GetSavePath();
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    bool leftover = name.EndsWith(".incoming", StringComparison.Ordinal) ||
                                    name.EndsWith(".rechazada", StringComparison.Ordinal) ||
                                    (name.StartsWith(BackupPrefix, StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal));
                    if (!leftover) continue;
                    try
                    {
                        Directory.Delete(dir, true);
                        Plugin.Log.LogInfo("Saves: removed leftover folder " + name);
                    }
                    catch (Exception ex) { Plugin.Log.LogWarning("Saves: could not remove " + name + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Saves: leftover scan failed: " + ex.Message); }
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
                    if (slot.EndsWith(".incoming", StringComparison.Ordinal) || slot.EndsWith(".rechazada", StringComparison.Ordinal) ||
                        slot.EndsWith(".tmp", StringComparison.Ordinal)) continue; // the mod's temp folders
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
