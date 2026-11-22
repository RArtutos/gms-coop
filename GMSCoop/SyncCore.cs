using System;
using System.Collections.Generic;
using UnityEngine;

namespace GMSCoop
{
    // Pure network-quality helpers used by area C (no game types, only UnityEngine.Vector3 / Mathf).
    // They are compiled unchanged into an offline test harness that feeds them lossy, reordered and
    // jittery input, so keep this file free of any game, Harmony or Unity engine-object reference.
    // Generic snapshot-buffer rules (time-sorted insert, dropping passed snapshots) and the playback
    // clock live in SyncLogic.cs (shared with ObjectSync, double seconds); this file only adds what area C
    // needs on top.

    // ====================================================================== remote player poses

    /// <summary>One received pose (sender clock, seconds as double: Time.unscaledTimeAsDouble).</summary>
    internal struct PoseSnap : ITimed
    {
        public double Time; // sender Time.unscaledTimeAsDouble
        public Vector3 Pos, Vel;
        public float Yaw, Pitch;
        public byte Flags, Tool;
        public double Stamp => Time;
    }

    /// <summary>
    /// Snapshot interpolation of one remote player. Receive side of PlayerSync.
    ///  - Timing: SyncClock (SyncLogic.cs, shared rules with ObjectSync, so a held object and the holder's
    ///    hand play back with the same delay): min-filter clock offset, delay = decaying peaks of packet gap
    ///    + lateness (100..300 ms), render lag slews smoothly, never jumps or runs backwards on a stall.
    ///  - Everything in DOUBLE seconds (stamps travel as f64): float seconds (Time.unscaledTime) have ~1 ms
    ///    steps after 2.3 h and ~2 ms after 4.6 h, and every stamp, offset and render time here is a
    ///    difference of two such numbers, so remote motion got juddery over a long session (harness:
    ///    review\round-9-C.md). Only what Unity gets (positions, blend factors) is float.
    ///  - Hermite interpolation with the sent velocity; at most 250 ms extrapolation, then hold.
    ///  - Error smoothing: when a packet arrives that changes the pose at the current render time (it ends
    ///    an extrapolation, or a reordered packet lands inside the current interval) the difference is kept
    ///    as an offset that decays with a 0.1 s time constant, so a late packet never makes the avatar jump.
    ///  - Teleports (moved farther than its speed allows), sender clock restarts and walk/drive switches
    ///    clear the buffer: the next pose is shown exactly (snap).
    ///  - Buffer rules: SyncLogic (time-sorted insert dropping duplicates and, when full, too-old poses;
    ///    passed poses dropped keeping the one before the render time).
    ///  Offline harness: review\round-7-C.md (bad network) and round-9-C.md (3 h / 8 h sessions).
    /// </summary>
    internal sealed class PoseBuffer
    {
        internal const float MaxExtrapolate = 0.25f;
        internal static float ErrTau = 0.1f;         // correction smoothing time constant (static: the harness sweeps it)
        internal const float SnapDistance = 5f;
        internal const int BufSize = 16;

        public readonly PoseSnap[] Buf = new PoseSnap[BufSize]; // sorted by Time, oldest first
        public int Count;
        public readonly SyncClock Clock = new SyncClock();
        public bool ClockSet;   // RenderClock holds a rendered time for the current buffer
        public double RenderClock;
        public Vector3 Err;     // smoothing offset added to the sampled position

        /// <summary>Forget the buffered poses (teleport, walk/drive switch): the next pose is shown exactly.</summary>
        public void Clear()
        {
            Count = 0;
            ClockSet = false;
            Err = Vector3.zero;
        }

        /// <summary>Adds a received pose. now = local receive time (Time.unscaledTimeAsDouble).</summary>
        public void Add(PoseSnap s, double now)
        {
            if (Clock.Feed(s.Time, now)) Clear(); // the sender's clock restarted

            if (Count > 0)
            {
                PoseSnap newest = Buf[Count - 1];
                double dt = s.Time - newest.Time;
                if (dt > 0.0)
                {
                    // Teleport (respawn, fast travel, ladder snap): moved much farther than its speed allows.
                    float speed = Mathf.Max(newest.Vel.magnitude, s.Vel.magnitude);
                    float allowed = (speed * 1.5f + 10f) * (float)dt + 2f;
                    if ((s.Pos - newest.Pos).sqrMagnitude > allowed * allowed) Clear();
                }
            }

            // Pose at the current render time before and after the insert: a change is kept as a decaying offset.
            bool smooth = ClockSet && Count > 0;
            Vector3 before = Vector3.zero;
            if (smooth) Sample(RenderClock, out before, out _, out _);
            // Time-sorted insert (SyncLogic, same rule as ObjectSync): duplicates and, with a full buffer,
            // anything older than all kept poses are dropped.
            if (!SyncLogic.InsertByTime(Buf, ref Count, s)) return;
            if (smooth)
            {
                Sample(RenderClock, out Vector3 after, out _, out _);
                Err += before - after;
                if (Err.sqrMagnitude > SnapDistance * SnapDistance) Err = Vector3.zero;
            }
        }

        /// <summary>
        /// Advances to this frame and returns the pose to show (false: nothing buffered).
        /// cur = the snapshot values at the render time (velocity / flags / tool / pitch for the animation).
        /// </summary>
        public bool Advance(double now, float dt, out Vector3 pos, out float yaw, out PoseSnap cur)
        {
            pos = Vector3.zero;
            yaw = 0f;
            cur = default;
            if (Count == 0) return false;
            RenderClock = Clock.RenderAt(now);
            ClockSet = true;

            SyncLogic.DropPassed(Buf, ref Count, RenderClock);
            Sample(RenderClock, out pos, out yaw, out cur);
            if (Err.sqrMagnitude > 1e-8f)
            {
                Err *= Mathf.Exp(-Mathf.Max(dt, 0f) / ErrTau);
                pos += Err;
            }
            else Err = Vector3.zero;
            return true;
        }

        /// <summary>Pose at sender time tr (interpolated / extrapolated / held).</summary>
        public void Sample(double tr, out Vector3 pos, out float yaw, out PoseSnap cur)
        {
            int mode = SyncLogic.Segment(Buf, Count, tr, out int k, out float s);
            PoseSnap a = Buf[k];
            cur = a;
            if (mode == 0)
            {
                pos = a.Pos;
                yaw = a.Yaw;
                return;
            }
            if (mode == 2)
            {
                double past = tr - a.Time;
                float ex = (float)Math.Min(past, MaxExtrapolate);
                pos = a.Pos + a.Vel * ex;
                yaw = a.Yaw;
                if (past > MaxExtrapolate) cur.Vel = Vector3.zero; // holding: stop the walk cycle
                return;
            }

            PoseSnap b = Buf[k + 1];
            float dt = (float)(b.Time - a.Time);
            if (dt > 0.3f)
            {
                pos = Vector3.Lerp(a.Pos, b.Pos, s);
            }
            else
            {
                float s2 = s * s, s3 = s2 * s;
                float h00 = 2f * s3 - 3f * s2 + 1f, h10 = s3 - 2f * s2 + s, h01 = -2f * s3 + 3f * s2, h11 = s3 - s2;
                pos = h00 * a.Pos + (h10 * dt) * a.Vel + h01 * b.Pos + (h11 * dt) * b.Vel;
            }
            yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, s);
            cur.Vel = Vector3.Lerp(a.Vel, b.Vel, s);
            cur.Pitch = Mathf.Lerp(a.Pitch, b.Pitch, s);
            if (s > 0.5f) { cur.Flags = b.Flags; cur.Tool = b.Tool; }
        }
    }

    // ====================================================================== predicted ledgers

    /// <summary>One unacknowledged delta of a predicted state: its sequence and local send time.</summary>
    internal abstract class PendBase
    {
        public uint Seq;
        public float T; // local send time
    }

    /// <summary>One unacknowledged delta of a predicted ledger (decimal stock values).</summary>
    internal class LedgerPend : PendBase
    {
        public decimal[] D;
    }

    /// <summary>One unacknowledged container delta (float contents).</summary>
    internal sealed class ContPend : PendBase
    {
        public float[] D;
    }

    /// <summary>
    /// Client-side prediction arithmetic shared by the stock ledger (GoldSync) and the containers
    /// (ContainerSync). Shown value = host value + local changes not sent yet + sent deltas the host has
    /// not acknowledged. The host's state and its acknowledgement travel in ONE reliable, ordered message,
    /// so on that message a delta leaves the prediction exactly when it enters the host value: no flicker,
    /// no double count, whatever the latency.
    /// A delta expires only if the host kept TALKING for the timeout after it was sent without
    /// acknowledging it (it never will: host loading / unknown key). A link stall (Wi-Fi, retransmits) of
    /// any length never expires a prediction: nothing arrives meanwhile, and the bunch that arrives after
    /// it (sent before the host could see the delta) starts a new talk period (HostLink).
    /// </summary>
    internal static class Predict
    {
        public static bool Acked(uint seq, uint ack) => (int)(seq - ack) <= 0;

        /// <summary>Folds the change since the last look into accum (cur = values read from the game).</summary>
        public static void Capture(decimal[] cur, decimal[] expected, decimal[] accum)
        {
            for (int i = 0; i < cur.Length; i++)
            {
                decimal d = cur[i] - expected[i];
                if (d == 0m) continue;
                accum[i] += d;
                expected[i] = cur[i];
            }
        }

        /// <summary>Drops every delta the host has acknowledged (ack = newest sequence it applied).</summary>
        public static void PruneAcked<T>(List<T> pend, uint ack) where T : PendBase
        {
            int kept = 0;
            for (int i = 0; i < pend.Count; i++)
                if (!Acked(pend[i].Seq, ack)) pend[kept++] = pend[i];
            if (kept < pend.Count) pend.RemoveRange(kept, pend.Count - kept);
        }

        /// <summary>Drops deltas the host kept talking about for <paramref name="timeout"/> without acknowledging them.</summary>
        public static void Expire<T>(List<T> pend, HostLink link, float timeout) where T : PendBase
        {
            int kept = 0;
            for (int i = 0; i < pend.Count; i++)
                if (link.Talked(pend[i].T) <= timeout) pend[kept++] = pend[i];
            if (kept < pend.Count) pend.RemoveRange(kept, pend.Count - kept);
        }

        // ---- overdraft check (host) ----

        /// <summary>Ledger stocks the game keeps at or above zero: gold, diamonds, magnetite (0 is cash).</summary>
        public static bool IsCheckedStock(int i) => i >= 1 && i <= 3;

        /// <summary>Rounding room of the game's fixed-point stocks (well below one displayed unit).</summary>
        public const decimal OverdraftTolerance = 0.0001m;

        /// <summary>
        /// Host: true if a player's delta spends more than the host's balance holds (another player spent
        /// it a moment before). Checked are deltas that spend gold / diamonds / magnetite (sale, smelting,
        /// forge upgrade) or carry a loan operation: each is ONE action whose every effect rolls back with
        /// the ledger (or has an undo), so the whole delta can be refused; its cash is checked too then.
        /// Other deltas (cash for a purchase: the bought object exists already and cannot be taken back)
        /// are never refused: two purchases at the same instant beyond the balance leave a debt, the game's
        /// own negative-cash state (rent and loan rates can cause it too), never money from nothing.
        /// </summary>
        public static bool Overdraws(decimal[] host, decimal[] d, bool hasLoanOps)
        {
            bool checkable = hasLoanOps;
            for (int i = 1; i <= 3 && i < d.Length; i++) if (d[i] < 0m) checkable = true;
            if (!checkable) return false;
            for (int i = 0; i <= 3 && i < d.Length; i++)
                if (d[i] < 0m && host[i] + d[i] < -OverdraftTolerance) return true;
            return false;
        }

        public static decimal Want<T>(decimal host, int i, decimal[] accum, List<T> pend) where T : LedgerPend
        {
            decimal want = host + accum[i];
            for (int k = 0; k < pend.Count; k++) want += pend[k].D[i];
            return want;
        }

        // ---- float containers ----

        public static void Capture(float[] cur, float[] expected, float[] accum, float eps)
        {
            for (int i = 0; i < cur.Length; i++)
            {
                float d = cur[i] - expected[i];
                if (Mathf.Abs(d) <= eps) continue;
                accum[i] += d;
                expected[i] = cur[i];
            }
        }

        public static void Want(float[] host, float[] accum, List<ContPend> pend, float[] into)
        {
            for (int i = 0; i < host.Length; i++)
            {
                float want = host[i] + accum[i];
                for (int k = 0; k < pend.Count; k++) want += pend[k].D[i];
                into[i] = want;
            }
        }
    }

    /// <summary>
    /// Client: when the host was heard. The host sends at least its clock every second, so a gap of more
    /// than 2 s is a stall; the talk period starts again at the first message after it.
    /// </summary>
    internal sealed class HostLink
    {
        public const float Gap = 2f;
        public float Last = -1000f, Since = -1000f;

        public void Reset() { Last = Since = -1000f; }

        public void Heard(float now)
        {
            if (now - Last > Gap) Since = now;
            Last = now;
        }

        /// <summary>How long the host has been talking without a stall since <paramref name="sentAt"/>.</summary>
        public float Talked(float sentAt) => Last - Math.Max(sentAt, Since);
    }

    // ====================================================================== processing authority per area

    /// <summary>
    /// Which PC runs the processing (wash plants, sluices, conveyors, ...) of world objects in a streamed
    /// area. The game deactivates an area's objects on a PC that is far away (AreaStreamer), so a host far
    /// from its friend would run nothing there. Every PC announces the areas it has loaded; the processing
    /// of an area runs on the HOST if it has the area loaded, else on the client with the lowest id that
    /// has it loaded; objects outside streamed areas (area null) always on the host.
    /// Clean handover: a PC takes the authority itself only SelfDelay seconds after it announced the area
    /// (and after it heard the host's list), while it gives it up as soon as it hears that a PC before it
    /// has the area. A handover therefore leaves a short pause, never two PCs processing the same plant
    /// (unless the link stalls for longer than SelfDelay). The host takes an area no client lists at once.
    /// </summary>
    internal sealed class AreaAuthority
    {
        public const float SelfDelay = 3f;
        public const float PeerTimeout = 25f;

        private sealed class Peer
        {
            public bool IsHost;
            public readonly HashSet<string> Areas = new HashSet<string>();
            public float Heard;
        }

        private readonly Dictionary<ulong, Peer> _peers = new Dictionary<ulong, Peer>();
        private readonly Dictionary<string, float> _mine = new Dictionary<string, float>(); // area -> time it joined my list
        private readonly List<string> _drop = new List<string>();
        private readonly List<ulong> _dropPeers = new List<ulong>();
        private float _heardHostAt = float.NegativeInfinity;
        public ulong MyId;
        public bool IAmHost;

        public void Clear()
        {
            _peers.Clear();
            _mine.Clear();
            _heardHostAt = float.NegativeInfinity;
        }

        public int PeerCount => _peers.Count;

        /// <summary>This PC's loaded areas now; true if the list changed (announce it).</summary>
        public bool SetMine(List<string> areas, float now)
        {
            bool changed = false;
            _drop.Clear();
            foreach (string a in _mine.Keys) if (!areas.Contains(a)) _drop.Add(a);
            foreach (string a in _drop) { _mine.Remove(a); changed = true; }
            foreach (string a in areas)
                if (a != null && !_mine.ContainsKey(a)) { _mine[a] = now; changed = true; }
            return changed;
        }

        public void MineInto(List<string> into)
        {
            into.Clear();
            into.AddRange(_mine.Keys);
        }

        /// <summary>Another PC's list. True if that PC was not known yet (answer with our own list).</summary>
        public bool SetPeer(ulong id, bool isHost, List<string> areas, float now)
        {
            if (id == MyId) return false;
            bool fresh = !_peers.TryGetValue(id, out Peer p);
            if (fresh) _peers[id] = p = new Peer();
            p.IsHost = isHost;
            p.Heard = now;
            p.Areas.Clear();
            foreach (string a in areas) p.Areas.Add(a);
            if (isHost && fresh) _heardHostAt = now;
            return fresh;
        }

        /// <summary>Forgets PCs not heard for PeerTimeout (they left; lists are re-sent every 10 s).</summary>
        public void Expire(float now)
        {
            _dropPeers.Clear();
            foreach (var kv in _peers) if (now - kv.Value.Heard > PeerTimeout) _dropPeers.Add(kv.Key);
            foreach (ulong id in _dropPeers)
            {
                if (_peers[id].IsHost) _heardHostAt = float.NegativeInfinity;
                _peers.Remove(id);
            }
        }

        /// <summary>True if this PC runs the processing of world objects in <paramref name="area"/>.</summary>
        public bool IsMine(string area, float now)
        {
            if (area == null) return IAmHost;
            if (!_mine.TryGetValue(area, out float since)) return false;
            if (IAmHost)
            {
                // No client lists the area: nobody else can be processing it, take it at once.
                bool other = false;
                foreach (var kv in _peers) if (kv.Value.Areas.Contains(area)) { other = true; break; }
                if (!other) return true;
            }
            else
            {
                if (float.IsNegativeInfinity(_heardHostAt)) return false; // the host's list not heard yet
                since = Math.Max(since, _heardHostAt);
            }
            if (now - since < SelfDelay) return false;
            if (IAmHost) return true;
            foreach (var kv in _peers)
            {
                if (!kv.Value.Areas.Contains(area)) continue;
                if (kv.Value.IsHost || kv.Key < MyId) return false;
            }
            return true;
        }
    }

    // ====================================================================== terrain host ordering

    /// <summary>
    /// Client side of the terrain host ordering: the cells this PC sent that the host has not ordered
    /// (acknowledged) yet. A cell maps to the newest sequence that carried it; an ack for seq N releases
    /// every cell whose newest carrier is &lt;= N.
    /// </summary>
    internal sealed class AckTracker
    {
        private struct Item
        {
            public uint Seq;
            public Dictionary<int, uint> Map;
            public int[] Cells;
        }

        private readonly List<Item> _queue = new List<Item>();

        public int Queued => _queue.Count;

        public void Clear() => _queue.Clear();

        public void Track(Dictionary<int, uint> map, uint seq, List<int> cells)
        {
            if (cells.Count == 0) return;
            foreach (int c in cells) map[c] = seq;
            _queue.Add(new Item { Seq = seq, Map = map, Cells = cells.ToArray() });
        }

        public void OnAck(uint seq)
        {
            int n = 0;
            while (n < _queue.Count && Predict.Acked(_queue[n].Seq, seq))
            {
                Item a = _queue[n++];
                foreach (int c in a.Cells)
                    if (a.Map.TryGetValue(c, out uint s) && s == a.Seq) a.Map.Remove(c);
            }
            if (n > 0) _queue.RemoveRange(0, n);
        }

        /// <summary>
        /// Host ordering rule for one incoming cell: keep this PC's own value if it sent the cell and the
        /// host has not ordered it yet (unacked), or changed it and has not sent it yet (current differs
        /// from the shadow = what every peer is believed to have).
        /// </summary>
        public static bool KeepLocal(bool unacked, bool hasShadow, int shadow, int current) =>
            unacked || (hasShadow && shadow != current);
    }

    // ====================================================================== request / reply ordering

    /// <summary>
    /// Client: requests sent to the host (tutorial step / mission stage "from -> to"). The host answers
    /// each one with exactly one state message tagged with the request's sequence (reliable, ordered);
    /// its other state messages carry no tag. While requests of this PC are unanswered, a host state that
    /// lies on this PC's own path (from, to or between them of an unanswered request) is older than the host's
    /// handling of those requests and is NOT applied: this PC is already further, and applying it would
    /// pull it back for a round trip (step C -> B -> C, B's enter actions run again). The answer to the
    /// newest request is always applied (the host's final word). A state off the path (the host is on
    /// another branch) is applied at once. Unanswered requests expire (the host ignores requests while it
    /// loads).
    /// </summary>
    internal sealed class ReplyQueue<T>
    {
        private struct Req
        {
            public uint Seq;
            public T From, To;
            public float At;
        }

        private readonly List<Req> _q = new List<Req>();
        private readonly float _timeout;
        private readonly EqualityComparer<T> _eq = EqualityComparer<T>.Default;
        private readonly Func<T, T, T, bool> _between; // (from, to, state): state lies between them (one request can skip steps)
        private uint _seq;

        public ReplyQueue(float timeout, Func<T, T, T, bool> between = null)
        {
            _timeout = timeout;
            _between = between;
        }

        public int Count => _q.Count;

        public void Clear() => _q.Clear();

        /// <summary>Registers a request; returns the sequence to send with it.</summary>
        public uint Sent(T from, T to, float now)
        {
            if (++_seq == 0) _seq = 1;
            _q.Add(new Req { Seq = _seq, From = from, To = to, At = now });
            return _seq;
        }

        /// <summary>A host state arrived (replySeq = our request it answers, 0 = none): true = apply it.</summary>
        public bool OnState(T state, uint replySeq, float now)
        {
            while (_q.Count > 0 && now - _q[0].At > _timeout) _q.RemoveAt(0);
            if (replySeq != 0)
                while (_q.Count > 0 && Predict.Acked(_q[0].Seq, replySeq)) _q.RemoveAt(0);
            if (_q.Count == 0) return true;
            foreach (Req r in _q)
                if (_eq.Equals(r.From, state) || _eq.Equals(r.To, state) || (_between != null && _between(r.From, r.To, state)))
                    return false; // on our path: our requests follow
            _q.Clear(); // the host is elsewhere: its state rules (our requests will be refused)
            return true;
        }
    }

    // ====================================================================== unreliable duplicates

    /// <summary>
    /// Duplicate filter for unreliable packets of one sender: 16-bit sequence, 64-packet window. Late
    /// (reordered) packets inside the window are accepted once, duplicates never; a sequence far behind
    /// the window means the sender restarted and starts a new window.
    /// </summary>
    internal sealed class SeqWindow
    {
        private ushort _newest;
        private ulong _seen; // bit k = newest - k received
        private bool _any;

        public bool Accept(ushort seq)
        {
            if (!_any)
            {
                _any = true;
                _newest = seq;
                _seen = 1;
                return true;
            }
            int d = (short)(ushort)(seq - _newest);
            if (d > 0)
            {
                _seen = d >= 64 ? 1UL : (_seen << d) | 1UL;
                _newest = seq;
                return true;
            }
            int back = -d;
            // Far behind: the sender restarted (new game process, sequence from 1). A genuinely reordered
            // packet is never 64 packets late (6.4 s at 10 Hz).
            if (back >= 64) { _newest = seq; _seen = 1; return true; }
            ulong bit = 1UL << back;
            if ((_seen & bit) != 0) return false;
            _seen |= bit;
            return true;
        }
    }

    // ====================================================================== one-way delay

    /// <summary>
    /// Extra delay of a message compared with the fastest one seen (min receive - send clock offset,
    /// relaxed slowly so clock drift is followed). Used to compensate jitter on time-stamped state.
    /// </summary>
    internal sealed class LatenessEstimator
    {
        private bool _has;
        private double _offset;

        public void Reset() => _has = false;

        /// <summary>Returns how much later than the fastest message this one arrived (s, &gt;= 0).</summary>
        public double Late(double senderTime, double localTime, double relaxPerCall)
        {
            double o = localTime - senderTime;
            if (!_has || Math.Abs(o - _offset) > 5.0) { _has = true; _offset = o; return 0.0; }
            _offset = Math.Min(o, _offset + relaxPerCall);
            return Math.Max(0.0, o - _offset);
        }
    }

    // ====================================================================== error throttling

    /// <summary>
    /// Rate limit for error logs of one sub-system that runs every frame: the first error is logged at
    /// once, then at most one line per interval with the number of errors swallowed meanwhile (a feature
    /// that throws every frame must neither flood the log nor stop the other features). While the errors
    /// go on, the interval doubles after every report (10 s, 20 s, 40 s ... at most 10 min), so a
    /// persistent failure costs a few dozen lines over a 3-hour session, not a thousand; after a quiet
    /// spell (no error for twice the current interval) it starts again at the base interval.
    /// </summary>
    internal sealed class ErrorGate
    {
        internal const float MaxInterval = 600f;
        private readonly float _base;
        private float _interval;
        private float _next = float.NegativeInfinity, _last = float.NegativeInfinity, _lastReport = float.NegativeInfinity;
        private int _swallowed;

        public ErrorGate(float interval = 10f) { _base = _interval = interval; }

        /// <summary>Number of reports so far (the caller logs the full error only on the first).</summary>
        public int Reports { get; private set; }

        /// <summary>True if this error should be logged now; <paramref name="swallowed"/> = errors skipped since the last log.</summary>
        public bool Pass(float now, out int swallowed)
        {
            if (now - _last > 2f * _interval) _interval = _base; // quiet for a while: a new problem
            _last = now;
            if (now < _next) { _swallowed++; swallowed = 0; return false; }
            swallowed = _swallowed;
            // Reported recently already: the failure goes on (every frame, or every few seconds): report less often.
            if (now - _lastReport < 4f * _interval) _interval = Math.Min(_interval * 2f, MaxInterval);
            _lastReport = now;
            _swallowed = 0;
            _next = now + _interval;
            Reports++;
            return true;
        }
    }
}
