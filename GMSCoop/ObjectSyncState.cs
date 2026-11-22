using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// State refresh of an existing loose object (SubState): its saved component data (SaveMe
    /// serializable components) is sent again when it changes in a way the pose stream does not carry.
    /// Used for repairs: a part repaired on the work table (or with a repair kit) got its durability back on
    /// the repairing PC only, and still showed as broken (and wore out) on the other PCs.
    /// </summary>
    internal static partial class ObjectSync
    {
        internal const byte SubState = 17;

        /// <summary>Sends the current saved state of the keyed loose object <paramref name="go"/> (no-op otherwise).</summary>
        internal static void SendStateRefresh(GameObject go)
        {
            if (!Net.Active || go == null || !Ready) return;
            string key = KeyOf(go);
            if (key == null) return;
            byte[] state = SerializeState(go);
            if (state == null) return;
            var ms = Begin(SubState, out var w);
            w.Write(Net.LocalId);
            w.Write(key);
            w.Write((ushort)state.Length);
            w.Write(state);
            Bcast(ms.ToArray(), true);
        }

        private static void ReceiveStateRefresh(System.IO.BinaryReader r)
        {
            ulong sender = r.ReadUInt64();
            string key = r.ReadString();
            int len = r.ReadUInt16();
            byte[] state = r.ReadBytes(len);
            if (sender == Net.LocalId || !Ready) return;
            GameObject go = Find(key);
            if (go == null) return;
            try { ApplyState(go, state); }
            catch (Exception ex) { Plugin.Log.LogWarning("Object sync: state refresh failed: " + ex.Message); }
        }

        /// <summary>CheckAndRepair.Repair on a loose keyed object (work table, repair kit): send its new durability.</summary>
        [HarmonyPatch]
        private static class PatchLooseRepair
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(CheckAndRepair), nameof(CheckAndRepair.Repair));
            private static bool Prepare() => TargetMethod() != null;

            private static void Postfix(CheckAndRepair __instance)
            {
                try
                {
                    if (__instance == null || !Net.Active) return;
                    var sm = __instance.GetComponentInParent<SaveSystem.SaveMe>();
                    if (sm != null) SendStateRefresh(sm.gameObject);
                }
                catch (Exception ex) { Plugin.Log.LogWarning("Object sync: repair refresh: " + ex.Message); }
            }
        }
    }
}
