using System.IO;

namespace GMSCoop
{
    /// <summary>
    /// Old wallet sync (Msg.Cash, absolute value, last write wins). Money is now part of the
    /// host-authoritative ledger in GoldSync ((Msg)14), which also counts two players spending at the
    /// same moment correctly. This class only keeps the old entry points so callers keep working.
    /// </summary>
    internal static class CashSync
    {
        private static bool _warned;

        /// <summary>Msg.Cash from a peer: never applied (it would overwrite the ledger).</summary>
        internal static void Receive(BinaryReader r)
        {
            if (_warned) return;
            _warned = true;
            Plugin.Log.LogWarning("Cash sync: ignoring an old-style cash message (the other PC runs an older mod build)");
        }

        /// <summary>Host: a player finished loading the world; send money, gold and contents.</summary>
        internal static void SendCurrent() => GoldSync.SendCurrent();
    }
}
