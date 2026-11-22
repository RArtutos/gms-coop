using System;
using System.IO;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// The tuned pickup hood ((Msg)14 sub 43): putting the tuned hood on the starter pickup
    /// (TuneHoodInPickup.SwapHood) sets the global GameStateManager.PickupHoodTuned and swaps the pickup's
    /// hood mesh. It happened on one PC only: the other players kept the old hood and the hood item.
    /// One-way flag, polled once a second: when it turns on here, every PC sets it and swaps the hood the
    /// game's way. A joining player gets it with the host's save; a flag set by a client reaches the host here.
    /// </summary>
    internal static partial class WorldSync
    {
        internal const byte SubHood = 43;

        private static bool _hoodSeen, _hoodKnown;
        private static float _nextHoodPoll;

        private static void ResetHood() { _hoodKnown = false; }

        private static void HoodTick(float now)
        {
            if (now < _nextHoodPoll || !global::Singleton<GoldDigger.GameStateManager>.IsInstanced()) return;
            _nextHoodPoll = now + 1f;
            bool tuned = global::Singleton<GoldDigger.GameStateManager>.Instance.PickupHoodTuned;
            if (!_hoodKnown) { _hoodKnown = true; _hoodSeen = tuned; return; } // baseline (loaded world)
            if (!tuned || _hoodSeen) { _hoodSeen = tuned; return; }
            _hoodSeen = true;
            if (!Synced()) return;
            var ms = GoldSync.Begin(SubHood, out BinaryWriter w);
            w.Write(true);
            TrafficC.Broadcast(TrafficC.Cat.World, ms.ToArray(), true);
            Plugin.Log.LogInfo("World sync: tuned pickup hood fitted here, sent");
        }

        private static void ReceiveHood(BinaryReader r)
        {
            bool tuned = r.ReadBoolean();
            if (!tuned || !Synced() || !global::Singleton<GoldDigger.GameStateManager>.IsInstanced()) return;
            var gsm = global::Singleton<GoldDigger.GameStateManager>.Instance;
            if (gsm.PickupHoodTuned) return;
            gsm.PickupHoodTuned = true;
            _hoodSeen = true;
            _hoodKnown = true;
            try
            {
                if (Pickup.MainPickup != null) Pickup.MainPickup.GetComponent<Pickup>()?.SwapToTuned();
                foreach (GoldDigger.TuneHoodInPickup t in UnityEngine.Object.FindObjectsOfType<GoldDigger.TuneHoodInPickup>(true))
                    if (t != null) UnityEngine.Object.Destroy(t.gameObject);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("World sync: hood swap failed: " + ex.Message); }
            Plugin.Log.LogInfo("World sync: tuned pickup hood from another player applied");
        }
    }
}
