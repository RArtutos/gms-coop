using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using SaveSystem;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Work tables ((Msg)14 sub 42). A broken part put on the table (WorkTable.AtachObject) marked the table
    /// occupied on that PC only: the other player saw the part lying there (pose sync) but its table was
    /// free, so it could put a second part on it. Now the occupied / free state follows the PC that put the
    /// part on (or finished the repair). The repaired durability itself travels as an object state refresh
    /// (ObjectSyncState.cs). The repair runs on the PC of the player who repairs; the other PCs show the
    /// table occupied until that player's repair ends: OnFix, AtachObject and the repair Update are blocked
    /// there (game hint "table not empty"). The occupier re-sends the state every 10 s (late joiners: HasPart
    /// is not saved); a remote occupation not confirmed for 35 s (that player left) is released.
    /// Identity: the table's saved root (RootKey) + path, else its scene path.
    /// </summary>
    internal static partial class WorldSync
    {
        internal const byte SubTable = 42;

        private static readonly AccessTools.FieldRef<WorkTable, bool> TableHasPart = GoldSync.TryRef<WorkTable, bool>("HasPart");
        private static readonly AccessTools.FieldRef<WorkTable, bool> TableFixing = GoldSync.TryRef<WorkTable, bool>("IsFixing");
        private static readonly AccessTools.FieldRef<WorkTable, float> TableFixPct = GoldSync.TryRef<WorkTable, float>("FixPct");
        private static bool _tableApplying;

        private static string TableKey(WorkTable t)
        {
            SaveMe root = t.GetComponentInParent<SaveMe>(true);
            string rk = root != null ? RootKey(root, true) : null;
            if (rk != null) return rk + "|" + PathBelow(root.transform, t.transform);
            return "s" + t.gameObject.scene.name + "|" + PathBelow(null, t.transform);
        }

        private static WorkTable FindTable(string key)
        {
            int bar = key.IndexOf('|');
            if (bar < 0) return null;
            string head = key.Substring(0, bar), path = key.Substring(bar + 1);
            if (head.Length > 0 && head[0] == 's')
            {
                foreach (WorkTable t in UnityEngine.Object.FindObjectsOfType<WorkTable>(true))
                    if (t.gameObject.scene.name == head.Substring(1) && PathBelow(null, t.transform) == path) return t;
                return null;
            }
            GameObject root = FindRoot(head);
            if (root == null) return null;
            Transform tr = path.Length == 0 ? root.transform : root.transform.Find(path);
            return tr != null ? tr.GetComponent<WorkTable>() : null;
        }

        private static void SendTable(WorkTable t, bool occupied, GameObject part)
        {
            if (_tableApplying || t == null || !Synced()) return;
            var ms = GoldSync.Begin(SubTable, out BinaryWriter w);
            w.Write(GoldSync.StreamId);
            w.Write(TableKey(t));
            w.Write(occupied);
            w.Write(occupied && part != null ? ObjectSync.KeyOf(part) ?? "" : "");
            TrafficC.Broadcast(TrafficC.Cat.World, ms.ToArray(), true);
        }

        // Tables a REMOTE player occupies (part on it, repair running on that player's PC): nobody else may run
        // the repair here. Value: when the owner last confirmed it (it re-sends every 10 s while occupied).
        private static readonly Dictionary<WorkTable, float> RemoteTables = new Dictionary<WorkTable, float>();
        // Tables this PC occupies: re-sent every 10 s (late joiners: HasPart is not saved; lost packets).
        private static readonly HashSet<WorkTable> LocalTables = new HashSet<WorkTable>();
        private static float _nextTableResend;
        private const float TableResend = 10f, TableExpire = 35f;

        internal static bool TableBusyRemotely(WorkTable t) => t != null && RemoteTables.ContainsKey(t);

        private static void ResetTables()
        {
            foreach (WorkTable t in new List<WorkTable>(RemoteTables.Keys)) if (t != null) ReleaseRemote(t);
            RemoteTables.Clear();
            LocalTables.Clear();
        }

        private static void TableTick(float now)
        {
            if (RemoteTables.Count > 0)
            {
                List<WorkTable> stale = null;
                foreach (var kv in RemoteTables)
                    if (kv.Key == null || now - kv.Value > TableExpire) (stale ??= new List<WorkTable>()).Add(kv.Key);
                if (stale != null)
                    foreach (WorkTable t in stale) { RemoteTables.Remove(t); if (t != null) ReleaseRemote(t); }
            }
            if (LocalTables.Count == 0 || now < _nextTableResend) return;
            _nextTableResend = now + TableResend;
            LocalTables.RemoveWhere(t => t == null || TableHasPart == null || !TableHasPart(t));
            foreach (WorkTable t in LocalTables) SendTable(t, true, t.BrokenPart);
        }

        private static void ReceiveTable(BinaryReader r)
        {
            ulong origin = r.ReadUInt64();
            string key = r.ReadString();
            bool occupied = r.ReadBoolean();
            string partKey = r.ReadString();
            if (origin == GoldSync.StreamId || !Synced() || TableHasPart == null) return;
            WorkTable t = FindTable(key);
            if (t == null) return;
            if (occupied)
            {
                if (LocalTables.Contains(t)) return; // our own part is on it here
                bool fresh = !RemoteTables.ContainsKey(t);
                RemoteTables[t] = Time.unscaledTime;
                if (!fresh) return;
                _tableApplying = true;
                try
                {
                    TableHasPart(t) = true;
                    if (TableFixing != null) TableFixing(t) = false; // the repair runs on the owner's PC only
                    if (TableFixPct != null) TableFixPct(t) = 0f;
                    t.BrokenPart = partKey.Length > 0 ? ObjectSync.Find(partKey) : null;
                    SetTableIcons(t, true);
                }
                catch (Exception ex) { Plugin.Log.LogWarning("World sync: work table apply failed: " + ex.Message); }
                finally { _tableApplying = false; }
            }
            else
            {
                if (!RemoteTables.Remove(t)) return;
                ReleaseRemote(t);
            }
        }

        /// <summary>A remote player's repair ended (or that player left): free the table here, part loose again.</summary>
        private static void ReleaseRemote(WorkTable t)
        {
            _tableApplying = true;
            try
            {
                GameObject part = t.BrokenPart;
                TableHasPart(t) = false;
                if (TableFixing != null) TableFixing(t) = false;
                if (TableFixPct != null) TableFixPct(t) = 0f;
                t.BrokenPart = null;
                if (part != null)
                {
                    // Parented to the table (only if this PC ever put it there): loose again. Physics back on,
                    // unless the network stream moves it (ObjectSync keeps followed objects kinematic itself).
                    if (part.transform.IsChildOf(t.transform)) part.transform.SetParent(null, true);
                    var rb = part.GetComponent<Rigidbody>();
                    if (rb != null && !ObjectSync.IsRemotelyHeld(part) && ObjectSync.KeyOf(part) == null) rb.isKinematic = false;
                }
                SetTableIcons(t, false);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("World sync: work table release failed: " + ex.Message); }
            finally { _tableApplying = false; }
        }

        private static void SetTableIcons(WorkTable t, bool occupied)
        {
            if (t.FixIcons != null)
                foreach (var icon in t.FixIcons) if (icon != null) icon.CrossFadeAlpha(occupied ? 1f : 0f, 0.5f, ignoreTimeScale: false);
            t.gameObject.layer = LayerMask.NameToLayer(occupied ? "Usable" : "Default");
        }

        /// <summary>Fixing a table another player occupies: refused, with the game's own "table not empty" hint.</summary>
        [HarmonyPatch]
        private static class PatchTableFix
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(WorkTable), nameof(WorkTable.OnFix));
            private static bool Prepare() => TargetMethod() != null;

            private static bool Prefix(WorkTable __instance)
            {
                if (_tableApplying || !TableBusyRemotely(__instance)) return true;
                try { if (global::Singleton<HUD>.IsInstanced()) global::Singleton<HUD>.Instance.ShowHint("WORK_TABLE_NOT_EMPTY"); } catch { }
                return false;
            }
        }

        /// <summary>The remote occupier's repair must not run here (WorkTable.Update would finish it and hand the part over).</summary>
        [HarmonyPatch]
        private static class PatchTableUpdateBlock
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(WorkTable), "Update");
            private static bool Prepare() => TargetMethod() != null;
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(WorkTable __instance) => !TableBusyRemotely(__instance);
        }

        [HarmonyPatch]
        private static class PatchTableAttach
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(WorkTable), nameof(WorkTable.AtachObject));
            private static bool Prepare() => TargetMethod() != null && TableHasPart != null;
            private static bool Prefix(WorkTable __instance, out bool __state)
            {
                __state = __instance != null && TableHasPart(__instance);
                return !TableBusyRemotely(__instance); // HasPart already refuses it; explicit for safety
            }

            private static void Postfix(WorkTable __instance, bool __state)
            {
                try { if (!__state && __instance != null && TableHasPart(__instance)) { LocalTables.Add(__instance); SendTable(__instance, true, __instance.BrokenPart); } }
                catch (Exception ex) { Plugin.Log.LogWarning("World sync: work table: " + ex.Message); }
            }
        }

        /// <summary>The repair finished in WorkTable.Update (HasPart true -> false): the table is free again.</summary>
        [HarmonyPatch]
        private static class PatchTableUpdate
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(WorkTable), "Update");
            private static bool Prepare() => TargetMethod() != null && TableHasPart != null;
            private static void Prefix(WorkTable __instance, out bool __state) => __state = __instance != null && TableHasPart(__instance);

            private static void Postfix(WorkTable __instance, bool __state)
            {
                try { if (__state && __instance != null && !TableHasPart(__instance) && LocalTables.Remove(__instance)) SendTable(__instance, false, null); }
                catch (Exception ex) { Plugin.Log.LogWarning("World sync: work table: " + ex.Message); }
            }
        }
    }
}
