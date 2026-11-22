using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;

namespace GMSCoop
{
    /// <summary>
    /// The game ships Spanish with about 360 empty lines (they show in English). After the game loads its
    /// languages, fill the empty or missing Spanish entries from es_extra.csv (embedded in this DLL). Only
    /// empty entries are filled: the game's own translations and every other language stay untouched,
    /// and the player keeps choosing "Español" in Opciones as usual. Not tied to a co-op session.
    /// </summary>
    [HarmonyPatch(typeof(LocaleManager), nameof(LocaleManager.LoadData))]
    internal static class SpanishFill
    {
        private static readonly AccessTools.FieldRef<LocaleManager, Dictionary<string, Dictionary<string, string>>> TextsRef =
            AccessTools.FieldRefAccess<LocaleManager, Dictionary<string, Dictionary<string, string>>>("Texts");

        private static void Postfix(LocaleManager __instance)
        {
            try
            {
                var texts = TextsRef(__instance);
                if (texts == null || !texts.TryGetValue("es", out var es)) return;
                using (Stream s = typeof(SpanishFill).Assembly.GetManifestResourceStream("GMSCoop.es_extra.csv"))
                {
                    if (s == null) return;
                    int filled = 0;
                    using (var reader = new CsvFileReader(s))
                    {
                        var row = new CsvRow();
                        while (reader.ReadRow(row))
                        {
                            if (row.Count < 2 || string.IsNullOrEmpty(row[1])) continue;
                            if (es.TryGetValue(row[0], out string cur) && !string.IsNullOrWhiteSpace(cur)) continue;
                            es[row[0]] = row[1];
                            filled++;
                        }
                    }
                    Plugin.Log.LogInfo($"Spanish: filled {filled} untranslated game lines");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Spanish fill skipped: " + ex.Message);
            }
        }
    }
}
