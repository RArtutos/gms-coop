using System;

namespace GMSCoop
{
    /// <summary>
    /// A snapshot with a sender-clock time stamp in seconds (for the time-sorted buffers). Double: a float
    /// game clock has only ~1 ms resolution after 2-3 hours (~4 ms after 8 h), which shows as judder.
    /// </summary>
    internal interface ITimed
    {
        double Stamp { get; }
    }

    /// <summary>
    /// Pure real-time sync rules (no Unity types), used by ObjectSync and checked by an offline test
    /// harness with lossy / reordered / duplicated input:
    ///  - ownership tie-break by epoch, then LocalId;
    ///  - staleness guards for unreliable poses that arrive after a reliable Rest / Drop / Attach
    ///    (Steam's reliable and unreliable channels are not ordered with each other);
    ///  - time-sorted snapshot buffers that drop duplicates and too-old packets.
    /// </summary>
    internal static class SyncLogic
    {
        /// <summary>
        /// Ownership tie-break, identical on every PC: higher epoch wins (u16 wrap-around safe), equal
        /// epoch -> lower LocalId wins; anything beats "nobody" (owner 0) at the same epoch.
        /// </summary>
        internal static bool ClaimWins(ulong owner, ushort curEpoch, ulong who, ushort epoch)
        {
            short d = (short)(epoch - curEpoch);
            if (d < 0) return false;
            if (d == 0 && owner != 0 && who > owner) return false;
            return true;
        }

        /// <summary>
        /// A pose from <paramref name="sender"/> stamped at or before the newest rest pose it sent is
        /// older than that rest pose (overtaken: the rest pose is reliable, poses are not).
        /// </summary>
        internal static bool OlderThanRest(ulong lastRestFrom, double lastRest, ulong sender, double stamp) =>
            lastRestFrom != 0 && lastRestFrom == sender && stamp <= lastRest;

        /// <summary>
        /// A "held" pose of the same epoch as a Drop from the same player was sent before that Drop: a
        /// new hold always takes a new epoch (grab = epoch + 2). Without this guard a late unreliable pose
        /// re-marks the object as held and blocks everybody's grab for seconds.
        /// </summary>
        internal static bool HeldAfterDrop(ulong dropFrom, int dropEpoch, ulong sender, ushort epoch) =>
            dropFrom != 0 && dropFrom == sender && dropEpoch == epoch;

        /// <summary>
        /// Poses with the epoch of an attach applied from the network are older than it: the owner stops
        /// streaming an attached item, so they can only be late packets from before the attach.
        /// </summary>
        internal static bool StaleForAttach(bool attached, int attachEpoch, ushort epoch) => attached && attachEpoch == epoch;

        /// <summary>
        /// Inserts <paramref name="s"/> into the time-sorted buffer. Drops exact duplicates (same stamp)
        /// and, when the buffer is full, anything older than all it keeps. Returns false if dropped.
        /// </summary>
        internal static bool InsertByTime<T>(T[] buf, ref int count, T s) where T : struct, ITimed
        {
            double t = s.Stamp;
            int i = count;
            while (i > 0 && buf[i - 1].Stamp > t) i--;
            if (i > 0 && Math.Abs(buf[i - 1].Stamp - t) < 0.0001f) return false;
            if (i < count && Math.Abs(buf[i].Stamp - t) < 0.0001f) return false;
            if (count == buf.Length)
            {
                if (i == 0) return false; // older than everything we keep
                Array.Copy(buf, 1, buf, 0, buf.Length - 1);
                count--;
                i--;
            }
            if (i < count) Array.Copy(buf, i, buf, i + 1, count - i);
            buf[i] = s;
            count++;
            return true;
        }

        /// <summary>Drops snapshots playback has passed, keeping the one right before <paramref name="render"/>.</summary>
        internal static void DropPassed<T>(T[] buf, ref int count, double render) where T : struct, ITimed
        {
            int drop = 0;
            while (count - drop >= 2 && buf[drop + 1].Stamp <= render) drop++;
            if (drop == 0) return;
            Array.Copy(buf, drop, buf, 0, count - drop);
            count -= drop;
        }

        /// <summary>
        /// Interpolation segment for render time <paramref name="tr"/>: index of the snapshot before it and
        /// the blend factor towards the next one. Mode 0 = before the first snapshot (hold it), 1 =
        /// between two snapshots, 2 = past the newest (extrapolate / hold).
        /// </summary>
        internal static int Segment<T>(T[] buf, int count, double tr, out int index, out float blend) where T : struct, ITimed
        {
            index = 0;
            blend = 0f;
            if (count == 0 || tr <= buf[0].Stamp) return 0;
            for (int i = 0; i + 1 < count; i++)
            {
                if (tr > buf[i + 1].Stamp) continue;
                double dt = buf[i + 1].Stamp - buf[i].Stamp;
                index = i;
                blend = dt > 0.0 ? (float)Math.Min(1.0, Math.Max(0.0, (tr - buf[i].Stamp) / dt)) : 1f;
                return 1;
            }
            index = count - 1;
            blend = (float)(tr - buf[count - 1].Stamp);
            return 2;
        }
    }

    /// <summary>
    /// Playback clock for one sender (snapshot interpolation). Pure: every input is passed in.
    ///
    /// Offset = minimum (local receive time - sender stamp) over a sliding 2..4 s window: the one-way
    /// latency plus the clock difference. A min filter ignores late packets, follows clock drift and
    /// adopts a higher base latency (route change) within 4 s; a lower one at once.
    /// Delay = how far behind the newest data playback runs: the worst recent packet gap (a lost
    /// packet doubles it) + the worst recent lateness above the minimum (jitter), as decaying peaks,
    /// clamped to [MinDelay, MaxDelay].
    /// Render time = now - Lag, where Lag (Offset + Delay) follows its target proportionally (time constant
    /// 0.5 s) and never faster than 10 % of real time, so playback
    /// never jumps or runs backwards when the estimates change; only a change over 0.5 s re-seats it.
    /// A packet much earlier than possible, or packets that stay over 1 s late for 1.5 s, mean the
    /// sender's clock restarted: everything is re-learned (Feed returns true).
    /// Packets that arrived through a reliable (retransmitting) channel do not teach the timing.
    /// Times are doubles (sender stamps from Time.unscaledTimeAsDouble, local times likewise): offsets and
    /// render times stay exact after hours of play. Durations (delay, gap, lateness) are small floats.
    /// </summary>
    internal sealed class SyncClock
    {
        // Floor of the render delay; Net lowers it on clean direct/LAN links (Net.AdaptRenderDelay).
        internal static float MinDelay = 0.1f;
        internal const float MaxDelay = 0.3f;
        private const float Window = 2f;
        private const float SlewRate = 0.1f;
        private const float SlewGain = 2f;     // 1/s: proportional part of the lag slew
        private const float SnapError = 0.5f;
        private const float RestartLate = 1f;
        private const float RestartHold = 1.5f;
        private const float PeakFade = 1.5f;   // time constant of the decaying peaks
        private const float MaxGap = 0.35f;    // larger stamp gaps are pauses between streams, not loss
        private const float Margin = 0.015f;
        private const float NominalGap = 0.05f;

        internal bool HasOffset;
        internal double Offset;
        internal float Late;              // decaying peak of lateness above Offset
        internal float Gap = NominalGap;  // decaying peak of stamp gaps between consecutive packets
        internal bool Set;
        internal double Lag;              // render time = now - Lag
        internal int Restarts;

        private double _minCur, _minPrev, _bucketStart;
        private double _lastStamp = double.NegativeInfinity;
        private double _lastFeed;
        private double _lateSince = -1.0;
        private double _lagAt;

        internal float TargetDelay => Clamp(Gap + Late + Margin, MinDelay, MaxDelay);

        /// <summary>
        /// One packet stamped <paramref name="stamp"/> (sender clock) received at local time
        /// <paramref name="now"/>. <paramref name="timing"/> = false for reliable packets (they may have
        /// waited for a retransmission). Returns true when the sender's clock restarted.
        /// </summary>
        internal bool Feed(double stamp, double now, bool timing = true)
        {
            if (double.IsNaN(stamp) || double.IsInfinity(stamp)) return false;
            double o = now - stamp;
            if (!HasOffset)
            {
                Init(o, now, stamp);
                return false;
            }
            if (o < Offset - RestartLate)
            {
                // Arrived earlier than any packet could: the sender's clock jumped forward.
                Init(o, now, stamp);
                Restarts++;
                return true;
            }
            if (o > Offset + RestartLate)
            {
                // Over a second late: a stall burst (Wi-Fi) or a restarted sender (its clock went back).
                // Only a lasting one is a restart; a burst teaches nothing (it cannot be buffered anyway).
                if (_lateSince < 0.0) _lateSince = now;
                else if (now - _lateSince >= RestartHold)
                {
                    Init(o, now, stamp);
                    Restarts++;
                    return true;
                }
                return false;
            }
            _lateSince = -1.0;
            if (!timing) return false;

            float dt = (float)Math.Max(0.0, now - _lastFeed);
            _lastFeed = now;
            if (now - _bucketStart >= Window)
            {
                _minPrev = _minCur;
                _minCur = o;
                _bucketStart = now;
            }
            else if (o < _minCur) _minCur = o;
            Offset = Math.Min(_minCur, _minPrev);

            Peak(ref Late, (float)Math.Min(o - Offset, 0.5), dt);
            if (stamp > _lastStamp)
            {
                float g = (float)(stamp - _lastStamp);
                if (g <= MaxGap) Peak(ref Gap, g, dt);
                _lastStamp = stamp;
            }
            return false;
        }

        /// <summary>Sender-clock time to show at local time <paramref name="now"/>.</summary>
        internal double RenderAt(double now)
        {
            double target = Offset + TargetDelay;
            if (!Set || Math.Abs(target - Lag) > SnapError)
            {
                Lag = target;
                Set = true;
                _lagAt = now;
            }
            else if (now > _lagAt)
            {
                // Proportional slew (time constant 1 / SlewGain), capped at SlewRate: a small estimate change
                // is followed gently instead of switching playback between 0.9x and 1.1x speed (surging).
                double dt = now - _lagAt;
                double step = SlewRate * dt;
                double d = (target - Lag) * Math.Min(1.0, SlewGain * dt);
                Lag += d < -step ? -step : d > step ? step : d;
                _lagAt = now;
            }
            return now - Lag;
        }

        private void Init(double o, double now, double stamp)
        {
            HasOffset = true;
            Offset = o;
            _minCur = _minPrev = o;
            _bucketStart = now;
            _lastFeed = now;
            _lastStamp = stamp;
            _lateSince = -1.0;
            Late = 0f;
            Gap = NominalGap;
            Set = false;
        }

        private static void Peak(ref float p, float v, float dt)
        {
            if (v >= p) p = v;
            else p -= (p - v) * Math.Min(1f, dt / PeakFade);
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
