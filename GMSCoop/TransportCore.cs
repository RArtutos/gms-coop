using System;

namespace GMSCoop
{
    /// <summary>Connection events of a lane transport (Packet.Data == null).</summary>
    internal enum LaneEvent : byte
    {
        Disconnected = 0, // the peer is gone
        Detached = 1,     // lane 0 dropped; the peer is kept and may re-attach (LanTransport, consumed by HybridTransport)
        Reattached = 2,   // a new lane 0 replaced the dropped one (consumed by HybridTransport)
    }

    /// <summary>A received packet or a connection event (Data == null, see Event).</summary>
    internal sealed class Packet
    {
        public ulong From;
        public byte[] Data;
        public int Length;
        public bool Reliable;
        public int Lane; // TCP lane it came on (LanTransport), 0 otherwise
        public LaneEvent Event; // only with Data == null; Net only ever sees Disconnected
    }

    /// <summary>
    /// A TCP transport with several connections ("lanes") per peer, so bulk data and lost-segment stalls on
    /// one connection do not delay the others: 0 control + gameplay, 1 and 3 unreliable snapshots (the same
    /// data on both: the newest copy wins), 2 bulk (world transfer).
    /// </summary>
    internal interface ILaneTransport : ITransport
    {
        /// <summary>Sends on that lane, or on lane 0 if it is not open. dropStale: the lane may drop older queued
        /// frames when it backs up (snapshots: only the newest matters).</summary>
        bool SendLane(ulong peer, int lane, byte[] data, bool dropStale);
        bool HasLane(ulong peer, int lane);
        /// <summary>Host: extra connections that start with this token belong to that peer.</summary>
        void RegisterLaneToken(ulong peer, ulong token);
        /// <summary>Client: opens the extra lanes to the host with the token it sent.</summary>
        void OpenLanes(ulong peer, ulong token);
        System.Net.IPAddress RemoteAddress(ulong peer);
    }

    internal interface ITransport
    {
        string Name { get; }

        /// <summary>Queues a packet. Returns false if it could not be queued (caller may retry later).</summary>
        bool Send(ulong peer, byte[] data, bool reliable);

        /// <summary>Bytes queued for this peer and not yet sent (used to throttle bulk transfers).</summary>
        int QueuedBytes(ulong peer);

        /// <summary>Drains received packets. Called on the Unity main thread.</summary>
        void Poll(Action<Packet> onPacket);

        void Disconnect(ulong peer);
        void Close();
    }
}
