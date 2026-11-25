using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GoldDigger;
using HarmonyLib;
using SixTeenLeft.EGSystem;
using UnityEngine;
using Gsm = GoldDigger.GameStateManager;

namespace GMSCoop
{
    /// <summary>
    /// Shared economy, host-authoritative. (Msg)14, first byte = sub-kind.
    ///
    /// LEDGER (sub 0 Delta, sub 1 State): the stock values of GoldDigger.GameStateManager:
    /// Cash, Gold, Diamonds, Magnetide, SumOfMeltedGold and SumofGoldNuggets.
    ///  - The host's values are the truth. The host broadcasts them (absolute) at most 10 times per
    ///    second when they changed, together with, per client, the last delta sequence it applied.
    ///  - A client never sends absolute values. Every local change (sale, purchase, panning, crafting,
    ///    blacksmith, drill, ...) is captured as a difference to the value it expected and sent to the
    ///    host as a numbered delta. The host adds it to its own value. Two players spending/earning at the
    ///    same moment therefore both count.
    ///  - Prediction: a client shows host value + its own deltas the host has not acknowledged yet, so
    ///    its money never jumps back while a delta is in flight.
    ///  - Nothing is captured or applied while loading or while a client still plays its own pre-join
    ///    world (SaveTransfer.WorldReady). After a load the expected values are re-based (no delta).
    ///
    /// NO DOUBLE COUNTING: code that runs on every PC at the same time (day change: rent, loan rates,
    /// machine rent; random events; end of season; season restore gold; worker refuel; tutorial/season
    /// reward effects; parcel money on load) changes the stock on the host only: on a client the stock
    /// setters are skipped inside those methods, and the loan rates (Finance.GetCreditPart), the return
    /// of unpaid rented machines (Finance.ReturnMachine) and the worker salary (Worker.CollectSalary) do
    /// not touch the client's lists at all: the host's results arrive (loan list in State, machines
    /// through ObjectSync's destroy sync, workers through the roster sync below).
    /// Processing machines are host-authoritative too, see ContainerSync.
    /// GameStatistics.AddMud, GameStatistics.AddMinerals and Finance.AddGold that a client's own action
    /// triggers are forwarded to the host (sub 5/6/7) and run there only.
    ///
    /// LOANS (Finance.Credits): the host's list travels with every State. A client that takes, pays or
    /// partly pays a loan sends the OPERATION (add / pay first / pay part of first) with its next Delta;
    /// the host applies it to its own list, so a day's rate charged meanwhile is never undone. A client
    /// keeps its own list while such a Delta is unacknowledged.
    ///
    /// STATISTICS (GameStatistics): the host's are the truth (sub 16, every 5 s while they change, with
    /// the ledger acks). What only a client sees (its own sales: EarnMoney, its own driving:
    /// DriveKilometers) travels with its Delta and is predicted like the ledger.
    ///
    /// WORKERS (sub 15): when a worker leaves a PC's roster (unpaid salary or random event on the host,
    /// fired in the laptop on any PC) the others remove the same worker (name + sex + portrait).
    ///
    /// CLOCK AND WEATHER (sub 11, host -> all, 1 Hz and at once after a jump): GameTime, TimeScale,
    /// Season, SeasonStartDay, current/next weather. Clients follow the host. Real time the game did not
    /// count on a PC (pause menu, shop, fast travel prompt, slow motion, frame hitch: Time.timeScale or
    /// maximumDeltaTime) is added to the clock on every PC, so one player's menu never stops or slows the
    /// shared clock. A client that is slightly ahead slows down instead of stepping back (a step back over
    /// midnight would run the day change twice). A client that sleeps (GameTime jumps forward) sends the
    /// new time (sub 12); a client that ends the season asks the host to end it (sub 13).
    ///
    /// OVERDRAFT: a client's frame that spends gold / diamonds / magnetite (sale, smelting, forge upgrade) and a
    /// loan operation travel as their own delta; the host refuses such a delta whole when its balance no
    /// longer covers it (another player spent the same stock a moment before, SyncCore.Predict.Overdraws):
    /// acknowledged without applying, so the client's prediction gives everything back, and SubReject (26)
    /// first runs the action's undo (smelting cancelled, upgrade level back). Forge bars: one collector per
    /// remelt, the host grants it (27 claim / 28 grant).
    ///
    /// Other sub-kinds: 2/3 container delta/state (ContainerSync), 4 parcel reset (TerrainSync),
    /// 8 dirt lumps (DirtSync), 9 drill points (TerrainSync), 10 container full-state request,
    /// 18..22 world features (WorldSync: saved states, explosions, switches, forge, paint), 23/24 fuel (FuelSync),
    /// 25 loaded areas (ContainerSync processing authority), 26 refused spend, 27/28 forge bar claim / grant.
    /// Global settings a save load overwrites are restored on a client after its session: SessionLifecycle.
    /// Predictions that the host never acknowledges expire once the host kept talking for 10 s after them
    /// (the host shows the truth); a stalled link never expires them (SyncCore.Predict).
    /// </summary>
    internal static class GoldSync
    {
        internal const Msg MsgId = (Msg)14;

        internal const byte SubDelta = 0;
        internal const byte SubState = 1;
        internal const byte SubContDelta = 2;
        internal const byte SubContState = 3;
        internal const byte SubParcelReset = 4;
        internal const byte SubMud = 5;
        internal const byte SubRentGold = 6;
        internal const byte SubMinerals = 7;
        internal const byte SubDrill = 9;
        internal const byte SubContRequest = 10;
        internal const byte SubTime = 11;
        internal const byte SubTimeJump = 12;
        internal const byte SubSeasonEnd = 13;
        internal const byte SubWorkerGone = 15;
        internal const byte SubStats = 16;
        internal const byte SubNugget = 17;
        internal const byte SubReject = 26;     // host -> all: stream u64, seq u32 (that ledger delta was refused)
        internal const byte SubForgeClaim = 27; // client -> host: stream u64, claim u32 (collect the remelted bars)
        internal const byte SubForgeGrant = 28; // host -> all: stream u64, claim u32, granted bool, amount decimal
        private const float PendingTimeout = 10f;
        private const float AckKeep = 30f;

        private const int N = 6; // 0 cash, 1 gold, 2 diamonds, 3 magnetite, 4 melted gold, 5 nuggets
        private const float SendInterval = 0.1f;
        private static readonly string[] Names = { "cash", "gold", "diamonds", "magnetite", "melted", "nuggets" };

        // > 0 while code runs whose stock changes this PC must not make (host-only / host-simulated).
        internal static int SuppressDepth;
        private static bool _applying;

        // Client
        private static readonly decimal[] Expected = new decimal[N];
        private static readonly decimal[] Accum = new decimal[N];
        private static readonly decimal[] Cur = new decimal[N];
        private sealed class Pend : LedgerPend
        {
            public bool Loans;
            public float Earn, Km;
            public List<Action> Undo; // runs if the host refuses this delta (SubReject)
        }

        /// <summary>
        /// Client: when the host was heard (any (Msg)14 sub-kind; the clock alone comes every second). A
        /// prediction expires only after the host kept talking for the timeout without acknowledging it,
        /// never because of a link stall (SyncCore.Predict / HostLink).
        /// </summary>
        internal static readonly HostLink Host = new HostLink();

        private static readonly List<Pend> Pending = new List<Pend>();
        private static uint _seq;

        /// <summary>
        /// Id of this PC's delta stream (ledger and containers). New random value every time this PC
        /// starts playing the shared world, so a player that reconnects or reloads (sequence numbers
        /// start again at 1) is never mistaken for its old stream by the host's duplicate check.
        /// </summary>
        internal static ulong StreamId;
        private static readonly System.Random Rng = new System.Random();
        private static bool _based;          // Expected holds a valid baseline
        private static bool _wasSynced;

        // Host
        private static readonly decimal[] Sent = new decimal[N];
        private struct AckInfo
        {
            public uint Seq;
            public float At;
        }

        private static readonly Dictionary<ulong, AckInfo> Acks = new Dictionary<ulong, AckInfo>();
        private static int _sentCredits;
        private static bool _hostSentOnce, _acksDirty, _forceSend;

        private static float _nextSend;

        /// <summary>The shared world is live on this PC (Net.WorldLive, the single gate of every system).</summary>
        internal static bool Synced() => Net.WorldLive;
        private static bool IsClientSynced() => !Net.IsHost && Synced();

        private static Gsm GsmInst =>
            global::Singleton<Gsm>.IsInstanced() ? global::Singleton<Gsm>.Instance : null;

        // ================================================================== reflection helpers

        /// <summary>
        /// Method declared on <paramref name="t"/> (no HarmonyX "not found" warning spam). With
        /// <paramref name="args"/> null the name must be unique; an overloaded name returns null.
        /// </summary>
        internal static MethodInfo Find(Type t, string name, Type[] args = null)
        {
            if (t == null) return null;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            if (args != null) return t.GetMethod(name, F, null, args, null);
            MethodInfo found = null;
            foreach (MethodInfo m in t.GetMethods(F))
            {
                if (m.Name != name) continue;
                if (found != null) return null; // overloaded: ambiguous
                found = m;
            }
            return found;
        }

        /// <summary>Same as <see cref="Find"/>, logs a warning naming the feature when it is missing.</summary>
        internal static MethodInfo Need(Type t, string name, string feature, Type[] args = null)
        {
            MethodInfo m = Find(t, name, args);
            if (m == null || m.IsAbstract)
            {
                Plugin.Log.LogWarning($"Patch target {t?.Name}.{name} not found: {feature} is OFF");
                return null;
            }
            return m;
        }

        // ================================================================== values

        private static decimal Read(Gsm g, int i)
        {
            switch (i)
            {
                case 0: return g.Cash?.Value ?? 0m;
                case 1: return g.Gold?.Value ?? 0m;
                case 2: return g.Diamonds?.Value ?? 0m;
                case 3: return g.Magnetide?.Value ?? 0m;
                case 4: return g.SumOfMeltedGold?.Value ?? 0m;
                default:
                    float n = g.SumofGoldNuggets;
                    return float.IsNaN(n) || float.IsInfinity(n) ? 0m : (decimal)n;
            }
        }

        private static void Write(Gsm g, int i, decimal v)
        {
            _applying = true;
            try
            {
                switch (i)
                {
                    case 0: g.Cash = new FixedCash(v); break;
                    case 1: g.Gold = new FixedGold(v); break;
                    case 2: g.Diamonds = new FixedGold(v); break;
                    case 3: g.Magnetide = new FixedGold(v); break;
                    case 4: g.SumOfMeltedGold = new FixedGold(v); break;
                    default: g.SumofGoldNuggets = (float)v; break;
                }
            }
            catch (Exception ex) { GSet.Fail(new Exception("setting " + Names[i] + ": " + ex.Message, ex)); }
            finally { _applying = false; }
        }

        // ================================================================== tick

        // One error gate per sub-system: a feature that throws (every frame) is logged at most every 10 s
        // and never stops the features ticked after it (GoldSync ticks the whole economy / world hub).
        private static readonly SubsystemGuard GReport = new SubsystemGuard("Patch report"),
            GTraffic = new SubsystemGuard("Traffic"), GSession = new SubsystemGuard("Session settings"),
            GLedger = new SubsystemGuard("Gold sync: ledger"), GStats = new SubsystemGuard("Gold sync: statistics"),
            GClock = new SubsystemGuard("Gold sync: clock"), GWorkers = new SubsystemGuard("Gold sync: workers"),
            GContainers = new SubsystemGuard("Container sync"), GDirt = new SubsystemGuard("Dirt sync"),
            GWorld = new SubsystemGuard("World sync"), GReset = new SubsystemGuard("Gold sync: session reset"),
            GRecv = new SubsystemGuard("Gold sync: receive"), GSet = new SubsystemGuard("Gold sync: stock write"),
            GWeather = new SubsystemGuard("Gold sync: weather");

        private static bool _sessionLive; // Net.Active was seen: the session state must be cleared when it ends

        /// <summary>
        /// Every frame (Plugin.Update). Each sub-system runs in its own try/catch with its own state; the
        /// game's GameStateManager is only needed by the ledger, statistics, clock and workers parts, the
        /// containers / dirt / world features depend on the shared world being ready, nothing else.
        /// </summary>
        internal static void Tick()
        {
            try { PatchReport.LogOnce(); } catch (Exception ex) { GReport.Fail(ex); }
            try { TrafficC.Tick(); } catch (Exception ex) { GTraffic.Fail(ex); }
            try { SessionLifecycle.Tick(); } catch (Exception ex) { GSession.Fail(ex); }
            if (!Net.Active)
            {
                // Any end of a session (also one that never reached a synced world) clears every
                // sub-system's session state once: acks, pending maps, stream ids.
                if (_sessionLive) { _sessionLive = false; ResetSession(); }
                return;
            }
            _sessionLive = true;
            bool synced = Synced();
            if (synced && !_wasSynced)
            {
                // Started playing the shared world (join, reconnect, reload): a new delta stream.
                _wasSynced = true;
                var b = new byte[8];
                Rng.NextBytes(b);
                StreamId = BitConverter.ToUInt64(b, 0) ^ Net.LocalId;
                _seq = 0;
                LoanOps.Clear();
                _statEarn = _statKm = 0f;
                _based = false; // re-based on the first ledger tick (no delta for what the load wrote)
                Pending.Clear();
                if (Net.IsHost) { _forceSend = true; _statsForce = true; }
            }
            else if (!synced) _wasSynced = false;

            Gsm g = synced ? GsmInst : null;
            if (g == null)
            {
                // Loading / pre-join world: the values come from a save, not from players.
                _based = false;
                _timeBased = false;
                _rosterBased = false;
            }
            else
            {
                try
                {
                    if (!_based) Rebase(g);
                    if (Net.IsHost) HostTick(g);
                    else ClientTick(g);
                }
                catch (Exception ex) { GLedger.Fail(ex); }
                if (Net.IsHost)
                {
                    try { HostStatsTick(g); }
                    catch (Exception ex) { GStats.Fail(ex); }
                }
                try { TimeTick(g); }
                catch (Exception ex) { GClock.Fail(ex); }
                try { RosterTick(); }
                catch (Exception ex) { GWorkers.Fail(ex); }
            }
            try { ContainerSync.Tick(synced); }
            catch (Exception ex) { GContainers.Fail(ex); }
            try
            {
                if (synced) DirtSync.Tick();
                else DirtSync.NotSynced();
            }
            catch (Exception ex) { GDirt.Fail(ex); }
            try { WorldSync.Tick(synced); }
            catch (Exception ex) { GWorld.Fail(ex); }
        }

        private static void ResetSession()
        {
            Acks.Clear();
            Pending.Clear();
            LoanOps.Clear();
            Array.Clear(Accum, 0, N);
            _based = false;
            _wasSynced = false;
            _hostSentOnce = false;
            _acksDirty = _forceSend = false;
            _seq = 0;
            _sentCredits = 0;
            _timeBased = false;
            _slew = 0;
            _jumpSentAt = -100f;
            ClockLate.Reset();
            Host.Reset();
            _rosterBased = false;
            Roster.Clear();
            RosterSeen.Clear();
            _statEarn = _statKm = 0f;
            _statsBody = null;
            _statsSeasonSent = -1;
            SuppressDepth = 0;
            _hostOnlyDepth = 0;
            ResetForgeClaims();
            // Each sub-system's own session state, independently (one failing never keeps another's stale).
            try { ContainerSync.ResetSession(); } catch (Exception ex) { GReset.Fail(ex); }
            try { DirtSync.Reset(); } catch (Exception ex) { GReset.Fail(ex); }
            try { WorldSync.ResetSession(); } catch (Exception ex) { GReset.Fail(ex); }
        }

        private static void Rebase(Gsm g)
        {
            for (int i = 0; i < N; i++) { Expected[i] = Read(g, i); Accum[i] = 0m; }
            Pending.Clear();
            _based = true;
        }

        /// <summary>
        /// Client: fold every local change since the last look into the outgoing delta. A frame that SPENDS
        /// gold, diamonds or magnetite (sale, smelting, forge upgrade, ...) is sent at once as its own
        /// delta, after a delta with the earlier changes: the host checks each spend against its balance
        /// (Predict.Overdraws) and a refused spend must never take unrelated changes along.
        /// </summary>
        private static void Capture(Gsm g)
        {
            if (!_based) { Rebase(g); return; }
            if (TakeFrame(g)) SendIsolated(Frame, null);
            else for (int i = 0; i < N; i++) Accum[i] += Frame[i];
        }

        private static readonly decimal[] Frame = new decimal[N];

        /// <summary>Change since the last look into Frame (Expected follows). True if it spends a checked stock.</summary>
        private static bool TakeFrame(Gsm g)
        {
            bool spend = false;
            for (int i = 0; i < N; i++)
            {
                Cur[i] = Read(g, i);
                Frame[i] = Cur[i] - Expected[i];
                Expected[i] = Cur[i];
                if (Frame[i] < 0m && Predict.IsCheckedStock(i)) spend = true;
            }
            return spend;
        }

        private static bool NonZero(decimal[] a)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] != 0m) return true;
            return false;
        }

        /// <summary>
        /// Client: sends the earlier changes (Accum) as one delta, then <paramref name="frame"/> (one action:
        /// its stock changes, the loan operations in LoanOps and the statistics) as its own delta, so the host
        /// can refuse exactly that action. <paramref name="undo"/> runs if the host refuses it.
        /// </summary>
        private static void SendIsolated(decimal[] frame, List<Action> undo)
        {
            if (NonZero(Accum))
            {
                var before = (decimal[])Accum.Clone();
                Array.Clear(Accum, 0, N);
                SendDelta(before, false, false, null);
            }
            if (NonZero(frame) || LoanOps.Count > 0) SendDelta((decimal[])frame.Clone(), true, true, undo);
        }

        /// <summary>Client: one numbered delta to the host (reliable), predicted until acknowledged.</summary>
        private static void SendDelta(decimal[] d, bool withOps, bool withStats, List<Action> undo)
        {
            float now = Time.unscaledTime;
            uint seq = ++_seq;
            int nOps = withOps ? Mathf.Min(LoanOps.Count, 255) : 0;
            bool loans = false;
            for (int i = 0; i < nOps; i++) loans |= LoanOps[i].Kind != SpendCheck;
            var p = new Pend { Seq = seq, D = d, T = now, Loans = loans, Undo = undo };
            if (withStats) { p.Earn = _statEarn; p.Km = _statKm; _statEarn = _statKm = 0f; }
            Pending.Add(p);
            var ms = Begin(SubDelta, out BinaryWriter w);
            w.Write(StreamId);
            w.Write(seq);
            for (int i = 0; i < N; i++) w.Write(d[i]);
            w.Write((byte)nOps);
            for (int i = 0; i < nOps; i++) { w.Write(LoanOps[i].Kind); w.Write(LoanOps[i].A); w.Write(LoanOps[i].B); }
            if (withOps) LoanOps.Clear();
            w.Write(p.Earn);
            w.Write(p.Km);
            TrafficC.Broadcast(TrafficC.Cat.Ledger, ms.ToArray(), true);
        }

        /// <summary>
        /// Client, around one game action that spends (forge, loan): everything before it is folded into
        /// Accum now, so that the action's own change can be sent alone by <see cref="IsolateEnd"/>.
        /// </summary>
        internal static void IsolateBegin()
        {
            if (!IsClientSynced() || !_based) return;
            Gsm g = GsmInst;
            if (g != null) Capture(g);
        }

        /// <summary>Client: sends the action's change (since IsolateBegin) alone; <paramref name="undo"/> runs if the host refuses it.</summary>
        internal static void IsolateEnd(List<Action> undo)
        {
            if (!IsClientSynced() || !_based) return;
            Gsm g = GsmInst;
            if (g == null) return;
            TakeFrame(g);
            if (NonZero(Frame) || LoanOps.Count > 0) SendIsolated(Frame, undo);
            else if (undo != null) Plugin.Log.LogInfo("Gold sync: an action with an undo spent nothing");
        }

        private static void ClientTick(Gsm g)
        {
            Capture(g);
            float now = Time.unscaledTime;
            // Never acknowledged although the host kept talking (it was loading): stop predicting, the
            // host's value rules. A stalled link expires nothing.
            if (Pending.Count > 0) Predict.Expire(Pending, Host, PendingTimeout);
            if (now < _nextSend) return;
            bool any = LoanOps.Count > 0 || _statEarn != 0f || _statKm != 0f || NonZero(Accum);
            if (!any) return;
            _nextSend = now + SendInterval;
            var d = (decimal[])Accum.Clone();
            Array.Clear(Accum, 0, N);
            SendDelta(d, true, true, null);
        }

        // ================================================================== loans

        private const byte LoanAdd = 1, LoanPayFirst = 2, LoanPartFirst = 3;
        /// <summary>Marker op (no loan): the host checks this delta's CASH against its balance too (purchases).</summary>
        private const byte SpendCheck = 0;

        /// <summary>Client, before a purchase: earlier changes are folded away so the purchase travels alone.</summary>
        internal static void CheckedSpendBegin() => IsolateBegin();

        /// <summary>
        /// Client, after a purchase: its money change is sent alone, marked so the host refuses it when its own
        /// cash no longer covers it (two players buying with the same money at once). Returns the delta's
        /// sequence (0 = nothing spent / not a client). <paramref name="onRefused"/> runs if the host refuses it.
        /// </summary>
        internal static uint CheckedSpendEnd(Action onRefused)
        {
            if (!IsClientSynced() || !_based) return 0;
            Gsm g = GsmInst;
            if (g == null) return 0;
            TakeFrame(g);
            if (!NonZero(Frame)) return 0;
            LoanOps.Add(new LoanOp { Kind = SpendCheck });
            SendIsolated(Frame, onRefused != null ? new List<Action> { onRefused } : null);
            return _seq;
        }

        /// <summary>Client: the host has not answered this delta yet (neither applied nor refused).</summary>
        internal static bool SpendPending(uint seq)
        {
            foreach (Pend p in Pending) if (p.Seq == seq) return true;
            return false;
        }

        private struct LoanOp
        {
            public byte Kind;
            public int A, B;
        }

        private static readonly List<LoanOp> LoanOps = new List<LoanOp>();

        private static List<Finance.Credit> CreditList() =>
            global::Singleton<Finance>.IsInstanced() ? global::Singleton<Finance>.Instance.Credits : null;

        private static int CreditHash()
        {
            List<Finance.Credit> list = CreditList();
            if (list == null) return 0;
            int h = 17 + list.Count;
            foreach (Finance.Credit c in list)
                if (c != null) h = (h * 31 + c.MoneyToPay) * 31 + c.DayToPay;
            return h;
        }

        private static void WriteCredits(BinaryWriter w)
        {
            List<Finance.Credit> list = CreditList();
            int n = list != null ? Mathf.Min(list.Count, 255) : 0;
            w.Write((byte)n);
            for (int i = 0; i < n; i++)
            {
                w.Write(list[i] != null ? list[i].MoneyToPay : 0);
                w.Write(list[i] != null ? list[i].DayToPay : 0);
            }
        }

        private static List<KeyValuePair<int, int>> ReadCredits(BinaryReader r)
        {
            int n = r.ReadByte();
            var list = new List<KeyValuePair<int, int>>(n);
            for (int i = 0; i < n; i++) list.Add(new KeyValuePair<int, int>(r.ReadInt32(), r.ReadInt32()));
            return list;
        }

        private static void ApplyCredits(List<KeyValuePair<int, int>> credits)
        {
            List<Finance.Credit> list = CreditList();
            if (list == null || credits == null) return;
            bool same = list.Count == credits.Count;
            for (int i = 0; same && i < credits.Count; i++)
                same = list[i] != null && list[i].MoneyToPay == credits[i].Key && list[i].DayToPay == credits[i].Value;
            if (same) return;
            list.Clear();
            foreach (var c in credits) list.Add(new Finance.Credit(c.Key, c.Value));
        }

        /// <summary>Host: a client's loan operation, applied to the host's own list.</summary>
        private static void ApplyLoanOp(LoanOp op)
        {
            if (op.Kind == SpendCheck) return; // marker only (the overdraft check already ran)
            List<Finance.Credit> list = CreditList();
            if (list == null) return;
            switch (op.Kind)
            {
                case LoanAdd:
                    if (op.B > 0) list.Add(new Finance.Credit(op.A, op.B));
                    break;
                case LoanPayFirst:
                    if (list.Count > 0) list.RemoveAt(0);
                    break;
                case LoanPartFirst:
                    if (list.Count > 0 && list[0] != null)
                    {
                        list[0].MoneyToPay -= op.A;
                        if (list[0].MoneyToPay <= 0) list.RemoveAt(0);
                    }
                    break;
            }
            Plugin.Log.LogInfo($"Gold sync: loan operation {op.Kind} from a player applied ({list.Count} loan(s) now)");
        }

        private struct LoanSnap
        {
            public bool Valid;
            public int Count, FirstMoney;
        }

        /// <summary>Client: what the loan UI did to the list becomes an operation for the host.</summary>
        [HarmonyPatch]
        private static class PatchLoanActions
        {
            private static List<MethodBase> _targets;

            private static List<MethodBase> Targets()
            {
                if (_targets != null) return _targets;
                _targets = new List<MethodBase>();
                void Add(Type t, string n)
                {
                    MethodInfo m = Need(t, n, "loan sync (" + n + ")");
                    if (m != null) _targets.Add(m);
                }
                Add(typeof(LaptopCredit), "GetCredit");
                Add(typeof(BankLoanGUI), "TakeLoan");
                Add(typeof(BankLoanGUI), "PayLoan");
                Add(typeof(BankLoanGUI), "PartialPayLoan");
                return _targets;
            }

            private static IEnumerable<MethodBase> TargetMethods() => Targets();
            private static bool Prepare() => Targets().Count > 0;

            private static void Prefix(out LoanSnap __state)
            {
                __state = default;
                if (!IsClientSynced()) return;
                IsolateBegin(); // the loan's cash travels alone, with its operation (the host checks it)
                List<Finance.Credit> list = CreditList();
                if (list == null) return;
                __state.Valid = true;
                __state.Count = list.Count;
                __state.FirstMoney = list.Count > 0 && list[0] != null ? list[0].MoneyToPay : 0;
            }

            private static void Postfix(LoanSnap __state)
            {
                if (!__state.Valid || !IsClientSynced()) return;
                List<Finance.Credit> list = CreditList();
                if (list == null) return;
                if (list.Count == __state.Count + 1 && list[list.Count - 1] != null)
                    LoanOps.Add(new LoanOp { Kind = LoanAdd, A = list[list.Count - 1].MoneyToPay, B = list[list.Count - 1].DayToPay });
                else if (list.Count == __state.Count - 1)
                    LoanOps.Add(new LoanOp { Kind = LoanPayFirst });
                else if (list.Count == __state.Count && list.Count > 0 && list[0] != null && list[0].MoneyToPay < __state.FirstMoney)
                    LoanOps.Add(new LoanOp { Kind = LoanPartFirst, A = __state.FirstMoney - list[0].MoneyToPay });
                IsolateEnd(null); // a refused loan payment rolls back by itself: cash and list come from the host
            }
        }

        /// <summary>
        /// Client: the day's loan rate (GetCreditPart) and the return of rented machines the money does
        /// not cover (ReturnMachine) are the host's job. The client's copy used to change its own loan
        /// list a second time (or a day too early) and destroy machines on its own judgement.
        /// </summary>
        [HarmonyPatch]
        private static class PatchFinanceHostOnly
        {
            private static List<MethodBase> _targets;

            private static List<MethodBase> Targets()
            {
                if (_targets != null) return _targets;
                _targets = new List<MethodBase>();
                MethodInfo m = Need(typeof(Finance), "GetCreditPart", "host-only loan rates");
                if (m != null) _targets.Add(m);
                m = Need(typeof(Finance), "ReturnMachine", "host-only rented machine return");
                if (m != null) _targets.Add(m);
                return _targets;
            }

            private static IEnumerable<MethodBase> TargetMethods() => Targets();
            private static bool Prepare() => Targets().Count > 0;
            private static bool Prefix() => !IsClientSynced();
        }

        private static void HostTick(Gsm g)
        {
            if (!_forceSend && Time.unscaledTime < _nextSend) return;
            int credits = CreditHash();
            bool changed = _forceSend || _acksDirty || !_hostSentOnce || credits != _sentCredits;
            if (!changed)
                for (int i = 0; i < N; i++) if (Read(g, i) != Sent[i]) { changed = true; break; }
            if (!changed) return;
            float now = Time.unscaledTime;
            _nextSend = now + SendInterval;
            _forceSend = _acksDirty = false;
            _hostSentOnce = true;
            _sentCredits = credits;
            PruneAcks(now);
            var ms = Begin(SubState, out BinaryWriter w);
            for (int i = 0; i < N; i++) { Sent[i] = Read(g, i); w.Write(Sent[i]); }
            WriteAcks(w);
            WriteCredits(w);
            TrafficC.Broadcast(TrafficC.Cat.Ledger, ms.ToArray(), true);
        }

        private static void PruneAcks(float now)
        {
            if (Acks.Count == 0) return;
            List<ulong> old = null;
            foreach (var kv in Acks) if (now - kv.Value.At > AckKeep) (old ??= new List<ulong>()).Add(kv.Key);
            if (old != null) foreach (ulong k in old) Acks.Remove(k);
        }

        private static void WriteAcks(BinaryWriter w)
        {
            w.Write((byte)Mathf.Min(Acks.Count, 255));
            int n = 0;
            foreach (var kv in Acks)
            {
                if (n++ == 255) break;
                w.Write(kv.Key);
                w.Write(kv.Value.Seq);
            }
        }

        /// <summary>Reads an ack list; true if it holds this PC's stream (its ack in <paramref name="ack"/>).</summary>
        private static bool ReadAcks(BinaryReader r, out uint ack)
        {
            int n = r.ReadByte();
            ack = 0;
            bool has = false;
            for (int k = 0; k < n; k++)
            {
                ulong id = r.ReadUInt64();
                uint s = r.ReadUInt32();
                if (id == StreamId) { ack = s; has = true; }
            }
            return has;
        }

        /// <summary>
        /// Client: send every change captured so far right now (ledger and containers), without waiting
        /// for the next 0.1 s tick. Called just before the session ends, so a sale made in the last moment
        /// still reaches the host.
        /// </summary>
        internal static void FlushNow()
        {
            if (!IsClientSynced()) return;
            Gsm g = GsmInst;
            try
            {
                _nextSend = 0f;
                if (g != null) ClientTick(g);
            }
            catch (Exception ex) { Plugin.Log.LogError("Gold sync: final flush: " + ex.Message); }
            try { ContainerSync.FlushNow(); }
            catch (Exception ex) { Plugin.Log.LogError("Container sync: final flush: " + ex.Message); }
        }

        /// <summary>Host: send the values now (a player finished loading the world).</summary>
        internal static void SendCurrent()
        {
            if (!Net.Active || !Net.IsHost) return;
            _forceSend = true;
            _timeForce = true;
            _statsForce = true;
            // Each part on its own (called by SaveTransfer): one failing never keeps the others from the joiner.
            try { ContainerSync.SendAll(); } catch (Exception ex) { GContainers.Fail(ex); }
            try { WorldSync.SendAll(); } catch (Exception ex) { GWorld.Fail(ex); }
            try { if (Synced() && GsmInst != null) HostTick(GsmInst); }
            catch (Exception ex) { GLedger.Fail(ex); }
        }

        internal static MemoryStream Begin(byte sub, out BinaryWriter w)
        {
            var ms = Net.Writer(MsgId);
            ms.WriteByte(sub);
            w = new BinaryWriter(ms);
            return ms;
        }

        // ================================================================== receive

        /// <summary>
        /// (Msg)14 from the network. A failing handler is caught here, so it never stops the host's Net
        /// layer from relaying the message to the other players and never affects another sub-kind.
        /// </summary>
        internal static void Receive(BinaryReader r)
        {
            int len = (int)r.BaseStream.Length + 1;
            byte sub = r.ReadByte();
            TrafficC.Recv(TrafficC.CatOfSub(sub), len);
            if (!Net.IsHost) Host.Heard(Time.unscaledTime); // clients only hear the host (it relays the others)
            try { Dispatch(sub, r); }
            catch (Exception ex) { GRecv.Fail(new Exception("sub-kind " + sub + ": " + ex.Message, ex)); }
        }

        private static void Dispatch(byte sub, BinaryReader r)
        {
            switch (sub)
            {
                case SubDelta: ReceiveDelta(r); break;
                case SubState: ReceiveState(r); break;
                case SubContDelta:
                case SubContState:
                case SubContRequest: ContainerSync.Receive(sub, r); break;
                case SubParcelReset: TerrainSync.ReceiveParcelReset(r); break;
                case SubMud:
                case SubRentGold:
                case SubMinerals: ReceiveForward(sub, r); break;
                case DirtSync.SubLumps: DirtSync.Receive(r); break;
                case SubDrill: TerrainSync.ReceiveDrill(r); break;
                case SubTime: ReceiveTime(r); break;
                case SubTimeJump: ReceiveTimeJump(r); break;
                case SubSeasonEnd: ReceiveSeasonEnd(r); break;
                case SubWorkerGone: ReceiveWorkerGone(r); break;
                case SubStats: ReceiveStats(r); break;
                case SubNugget: ReceiveNugget(r); break;
                case SubReject: ReceiveReject(r); break;
                case SubForgeClaim: ReceiveForgeClaim(r); break;
                case SubForgeGrant: ReceiveForgeGrant(r); break;
                case ContainerSync.SubAreas: ContainerSync.ReceiveAreas(r); break;
                case WorldSync.SubFlags:
                case WorldSync.SubBoom:
                case WorldSync.SubUse:
                case WorldSync.SubForge:
                case WorldSync.SubPaint:
                case WorldSync.SubFuel:
                case WorldSync.SubFuelOp:
                case WorldSync.SubWater:
                case WorldSync.SubWaterOp:
                case WorldSync.SubTable:
                case WorldSync.SubHood: WorldSync.Receive(sub, r); break;
                default: Plugin.Log.LogWarning("Gold sync: unknown sub-kind " + sub); break;
            }
        }

        private static void ReceiveDelta(BinaryReader r)
        {
            ulong origin = r.ReadUInt64();
            uint seq = r.ReadUInt32();
            var d = new decimal[N];
            for (int i = 0; i < N; i++) d[i] = r.ReadDecimal();
            int nOps = r.ReadByte();
            List<LoanOp> ops = nOps > 0 ? new List<LoanOp>(nOps) : null;
            for (int i = 0; i < nOps; i++) ops.Add(new LoanOp { Kind = r.ReadByte(), A = r.ReadInt32(), B = r.ReadInt32() });
            float earn = r.ReadSingle(), km = r.ReadSingle();
            if (!Net.IsHost) return; // relayed copy
            if (Acks.TryGetValue(origin, out AckInfo last) && (int)(seq - last.Seq) <= 0) return; // duplicate
            Gsm g = GsmInst;
            if (!Synced() || g == null)
            {
                // The host is loading a save: that change belonged to the world being replaced. Acknowledge
                // it without applying, so the player stops predicting it as soon as the new world's values
                // arrive (instead of showing it for 10 s on top of them).
                Acks[origin] = new AckInfo { Seq = seq, At = Time.unscaledTime };
                _acksDirty = true;
                return;
            }
            for (int i = 0; i < N; i++) HostVals[i] = Read(g, i);
            if (Predict.Overdraws(HostVals, d, ops != null))
            {
                // Spent what is not there any more (another player spent the same gold / money a moment
                // before): the whole action is refused. Acknowledged without applying, so the player's
                // prediction drops it (its stock comes back); SubReject first lets it undo the game side
                // effects (forge upgrade, smelting) before that acknowledgement arrives.
                var rm = Begin(SubReject, out BinaryWriter rw);
                rw.Write(origin);
                rw.Write(seq);
                TrafficC.Broadcast(TrafficC.Cat.Ledger, rm.ToArray(), true);
                Acks[origin] = new AckInfo { Seq = seq, At = Time.unscaledTime };
                _acksDirty = true;
                Plugin.Log.LogInfo($"Gold sync: refused a player's spend (overdraft: cash {d[0]:0.##}, gold {d[1]:0.####}, diamonds {d[2]:0.####}, magnetite {d[3]:0.####})");
                return;
            }
            for (int i = 0; i < N; i++)
                if (d[i] != 0m) Write(g, i, Read(g, i) + d[i]);
            if (ops != null) foreach (LoanOp op in ops) ApplyLoanOp(op);
            AddStats(earn, km);
            Acks[origin] = new AckInfo { Seq = seq, At = Time.unscaledTime };
            _acksDirty = true;
        }

        private static readonly decimal[] HostVals = new decimal[N];

        /// <summary>Client: the host refused one of this PC's spends: undo its game side effects and tell the player.</summary>
        private static void ReceiveReject(BinaryReader r)
        {
            ulong origin = r.ReadUInt64();
            uint seq = r.ReadUInt32();
            if (Net.IsHost || origin != StreamId) return;
            foreach (Pend p in Pending)
            {
                if (p.Seq != seq) continue;
                if (p.Undo != null)
                    foreach (Action a in p.Undo)
                    {
                        try { a(); }
                        catch (Exception ex) { Plugin.Log.LogWarning("Gold sync: undo of a refused action failed: " + ex.Message); }
                    }
                p.Undo = null;
                break;
            }
            Plugin.Log.LogInfo("Gold sync: the host refused a spend of this PC (another player spent it first): undone");
            ShowHint("Otro jugador gastó ese oro o dinero al mismo tiempo: la operación se anuló");
        }

        /// <summary>Player-visible message (overlay status line, like the other co-op notices).</summary>
        internal static void ShowHint(string text) => Plugin.Instance.SetStatus(text);

        private static void ReceiveState(BinaryReader r)
        {
            var v = new decimal[N];
            for (int i = 0; i < N; i++) v[i] = r.ReadDecimal();
            bool hasAck = ReadAcks(r, out uint ack);
            List<KeyValuePair<int, int>> credits = ReadCredits(r);
            if (Net.IsHost || !Synced()) return; // a client in its own world takes nothing; the host re-sends after Loaded
            Gsm g = GsmInst;
            if (g == null) return;
            Capture(g); // local changes not yet seen stay in Accum
            if (hasAck) Predict.PruneAcked(Pending, ack);
            bool ownLoans = LoanOps.Count > 0;
            foreach (var p in Pending) ownLoans |= p.Loans;
            if (!ownLoans) ApplyCredits(credits);
            for (int i = 0; i < N; i++)
            {
                decimal want = Predict.Want(v[i], i, Accum, Pending);
                if (Read(g, i) != want) Write(g, i, want);
                Expected[i] = Read(g, i); // the game may clamp (gold >= 0)
            }
        }

        // ================================================================== forwarded statistics

        internal static int LotIndex(LotDescriptor lot)
        {
            if (lot == null || !global::Singleton<LaptopGlobalManager>.IsInstanced()) return -1;
            List<LotDescriptor> all = AllLotsRef != null ? AllLotsRef(global::Singleton<LaptopGlobalManager>.Instance) : null;
            return all != null ? all.IndexOf(lot) : -1;
        }

        internal static LotDescriptor LotAt(int idx)
        {
            if (idx < 0 || !global::Singleton<LaptopGlobalManager>.IsInstanced()) return null;
            List<LotDescriptor> all = AllLotsRef != null ? AllLotsRef(global::Singleton<LaptopGlobalManager>.Instance) : null;
            return all != null && idx < all.Count ? all[idx] : null;
        }

        private static readonly AccessTools.FieldRef<LaptopGlobalManager, List<LotDescriptor>> AllLotsRef =
            TryRef<LaptopGlobalManager, List<LotDescriptor>>("AllLots");
        private static readonly AccessTools.FieldRef<ResourceBag, float[]> BagData = TryRef<ResourceBag, float[]>("_data");

        internal static AccessTools.FieldRef<T, F> TryRef<T, F>(string field)
        {
            try { return AccessTools.FieldRefAccess<T, F>(field); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Gold sync: field {typeof(T).Name}.{field} not found ({ex.Message})");
                return null;
            }
        }

        internal static float[] BagArray(ResourceBag bag) => bag != null && BagData != null ? BagData(bag) : null;

        private static bool _forwardApplying;

        /// <summary>
        /// Client: a statistics/bonus call that its own action caused. True = run it here (host / not in
        /// a session), false = it was forwarded (or belongs to host-simulated processing) and must be skipped.
        /// </summary>
        private static bool HostRunsIt(byte sub, LotDescriptor lot, float value, ResourceBag bag)
        {
            if (_forwardApplying || !Synced()) return true;
            if (SuppressDepth > 0) return false; // the simulating PC / the host does the same thing
            if (Net.IsHost) return true;
            int idx = LotIndex(lot);
            if (idx < 0) return true; // unknown lot: keep the local behaviour
            var ms = Begin(sub, out BinaryWriter w);
            w.Write(idx);
            if (sub == SubMinerals)
            {
                float[] data = BagArray(bag) ?? new float[0];
                w.Write((byte)data.Length);
                foreach (float f in data) w.Write(f);
            }
            else w.Write(value);
            TrafficC.Broadcast(TrafficC.Cat.Stats, ms.ToArray(), true);
            return false;
        }

        private static void ReceiveForward(byte sub, BinaryReader r)
        {
            int idx = r.ReadInt32();
            float value = 0f;
            float[] data = null;
            if (sub == SubMinerals)
            {
                int n = r.ReadByte();
                data = new float[n];
                for (int i = 0; i < n; i++) data[i] = r.ReadSingle();
            }
            else value = r.ReadSingle();
            if (!Net.IsHost || !Synced()) return;
            LotDescriptor lot = LotAt(idx);
            if (lot == null) return;
            _forwardApplying = true;
            try
            {
                switch (sub)
                {
                    case SubMud:
                        if (global::Singleton<GameStatistics>.IsInstanced()) global::Singleton<GameStatistics>.Instance.AddMud(lot, value);
                        break;
                    case SubRentGold:
                        if (global::Singleton<Finance>.IsInstanced()) global::Singleton<Finance>.Instance.AddGold(lot, value);
                        break;
                    case SubMinerals:
                        if (global::Singleton<GameStatistics>.IsInstanced())
                        {
                            var bag = new ResourceBag();
                            float[] dst = BagArray(bag);
                            if (dst != null) Array.Copy(data, dst, Mathf.Min(dst.Length, data.Length));
                            global::Singleton<GameStatistics>.Instance.AddMinerals(lot, bag);
                        }
                        break;
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("Gold sync: forwarded statistics failed: " + ex.Message); }
            finally { _forwardApplying = false; }
        }

        [HarmonyPatch]
        private static class PatchAddMud
        {
            private static MethodBase TargetMethod() => Need(typeof(GameStatistics), nameof(GameStatistics.AddMud), "nugget bonus / mud statistics forwarding");
            private static bool Prepare() => TargetMethod() != null;
            private static bool Prefix(GameStatistics __instance, LotDescriptor lot, float mud, out NuggetState __state)
            {
                bool run = HostRunsIt(SubMud, lot, mud, null);
                __state = default;
                Gsm g = GsmInst;
                if (run && Net.IsHost && Synced() && g != null && __instance != null)
                {
                    __state.Valid = true;
                    __state.Nugget = __instance.CurrentNugget;
                    __state.Gold = Read(g, 1);
                }
                return run;
            }

            /// <summary>Host: a gold nugget was found (the game shows its hint here only): tell the others.</summary>
            private static void Postfix(GameStatistics __instance, NuggetState __state)
            {
                if (!__state.Valid || __instance == null || __instance.CurrentNugget <= __state.Nugget) return;
                Gsm g = GsmInst;
                if (g == null) return;
                float grams = (float)(Read(g, 1) - __state.Gold);
                if (grams <= 0f) return;
                var ms = Begin(SubNugget, out BinaryWriter w);
                w.Write(grams);
                TrafficC.Broadcast(TrafficC.Cat.Stats, ms.ToArray(), true);
            }
        }

        private struct NuggetState
        {
            public bool Valid;
            public int Nugget;
            public decimal Gold;
        }

        private static void ReceiveNugget(BinaryReader r)
        {
            float grams = r.ReadSingle();
            if (Net.IsHost || !Synced() || !global::Singleton<HUD>.IsInstanced()) return;
            try
            {
                global::Singleton<HUD>.Instance.ShowHint(new LocalizationKey("GOLD_NUGGET_INFO").GetLocalized() + " " + ResourceFormatter.GetFormatedGold((FixedGold)grams));
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Gold sync: nugget hint: " + ex.Message); }
        }

        [HarmonyPatch]
        private static class PatchAddMinerals
        {
            private static MethodBase TargetMethod() => Need(typeof(GameStatistics), nameof(GameStatistics.AddMinerals), "claim statistics forwarding");
            private static bool Prepare() => TargetMethod() != null;
            private static bool Prefix(LotDescriptor lot, ResourceBag bagIn) => HostRunsIt(SubMinerals, lot, 0f, bagIn);
        }

        [HarmonyPatch]
        private static class PatchRentGold
        {
            private static MethodBase TargetMethod() => Need(typeof(Finance), nameof(Finance.AddGold), "claim rent gold forwarding");
            private static bool Prepare() => TargetMethod() != null;
            private static bool Prefix(LotDescriptor desc, float gold) => HostRunsIt(SubRentGold, desc, gold, null);
        }

        // ================================================================== statistics

        private const float StatsInterval = 5f;
        private const float StatsRefresh = 30f;  // real play time changes every frame: refresh at least this often
        private static float _statEarn, _statKm; // client: own sales / driving not yet sent
        private static bool _statsApplying, _statsForce;
        private static float _nextStats, _statsSentAt;
        private static byte[] _statsBody;
        private static int _statsSeasonSent = -1;

        private static GameStatistics Stats =>
            global::Singleton<GameStatistics>.IsInstanced() ? global::Singleton<GameStatistics>.Instance : null;

        /// <summary>Host: a client's sales (EarnMoney) and driving (DriveKilometers).</summary>
        private static void AddStats(float earn, float km)
        {
            GameStatistics s = Stats;
            if (s == null || (earn == 0f && km == 0f)) return;
            if (float.IsNaN(earn) || float.IsInfinity(earn) || float.IsNaN(km) || float.IsInfinity(km)) return;
            _statsApplying = true;
            try
            {
                if (earn != 0f) s.EarnMoney += earn;
                if (km != 0f) s.DriveKilometers += km;
            }
            finally { _statsApplying = false; }
        }

        private static void CaptureStat(ref float acc, float value, float current)
        {
            if (_statsApplying || SuppressDepth > 0 || !IsClientSynced()) return;
            float d = value - current;
            if (!float.IsNaN(d) && !float.IsInfinity(d)) acc += d;
        }

        [HarmonyPatch]
        private static class PatchEarnMoney
        {
            private static MethodBase TargetMethod()
            {
                MethodInfo m = AccessTools.PropertySetter(typeof(GameStatistics), nameof(GameStatistics.EarnMoney));
                if (m == null) Plugin.Log.LogWarning("Patch target GameStatistics.EarnMoney not found: client sales statistics are OFF");
                return m;
            }

            private static bool Prepare() => TargetMethod() != null;
            private static void Prefix(GameStatistics __instance, float value) => CaptureStat(ref _statEarn, value, __instance.EarnMoney);
        }

        [HarmonyPatch]
        private static class PatchDriveKm
        {
            private static MethodBase TargetMethod()
            {
                MethodInfo m = AccessTools.PropertySetter(typeof(GameStatistics), nameof(GameStatistics.DriveKilometers));
                if (m == null) Plugin.Log.LogWarning("Patch target GameStatistics.DriveKilometers not found: client driving statistics are OFF");
                return m;
            }

            private static bool Prepare() => TargetMethod() != null;
            private static void Prefix(GameStatistics __instance, float value) => CaptureStat(ref _statKm, value, __instance.DriveKilometers);
        }

        private static void WriteSeasonBody(BinaryWriter w, GameStatistics.SeasonStats s, int season)
        {
            w.Write(season);
            w.Write(s.DriveKilometers); w.Write(s.UsedFuel); w.Write(s.EarnMoney); w.Write(s.CollectedMagnetite);
            w.Write(s.WorkersSalary); w.Write(s.DayEarnedGold); w.Write(s.DayEarndedDiamonds); w.Write(s.DayWorkersGold);
            w.Write(s.DayWorkersMoney); w.Write(s.DayMud); w.Write(s.EndSeasonGold); w.Write(s.EndSeasonDiamonds);
            int days = s.DayStats != null ? Mathf.Min(s.DayStats.Count, 2000) : 0;
            w.Write((ushort)days);
            int skip = s.DayStats != null ? s.DayStats.Count - days : 0;
            for (int i = 0; i < days; i++)
            {
                GameStatistics.DayStat d = s.DayStats[skip + i];
                w.Write(d != null ? d.Gold : 0f);
                w.Write(d != null ? d.Money : 0f);
            }
            var claims = new List<KeyValuePair<int, GameStatistics.ClaimStatistics>>();
            if (s.Claims != null)
                foreach (var kv in s.Claims)
                {
                    int idx = LotIndex(kv.Key);
                    if (idx >= 0 && kv.Value != null) claims.Add(new KeyValuePair<int, GameStatistics.ClaimStatistics>(idx, kv.Value));
                }
            w.Write((ushort)claims.Count);
            foreach (var c in claims)
            {
                w.Write(c.Key);
                w.Write(c.Value.CleanMud);
                w.Write(c.Value.CollectedGold);
                w.Write(c.Value.CollectedDiamonds);
            }
        }

        /// <summary>Host: statistics snapshot (current season, and the previous one right after a season change).</summary>
        private static void HostStatsTick(Gsm g)
        {
            float now = Time.unscaledTime;
            if (!_statsForce && now < _nextStats) return;
            GameStatistics st = Stats;
            if (st == null) return;
            _nextStats = now + StatsInterval;
            int season = g.Season;
            bool withPrev = _statsSeasonSent >= 0 && _statsSeasonSent != season;
            int prev = _statsSeasonSent;
            var bodyMs = new MemoryStream();
            var bw = new BinaryWriter(bodyMs);
            bw.Write((byte)(withPrev ? 2 : 1));
            WriteSeasonBody(bw, st.SeasonsStats[season], season);
            if (withPrev) WriteSeasonBody(bw, st.SeasonsStats[prev], prev);
            bw.Write(st.CurrentMudCount);
            bw.Write(st.CurrentNugget);
            bw.Write(st.GoldCollectedAtNight);
            // Claim rent gold owed (Finance.AddGold runs on the host): the client's day panel shows it.
            var owed = new List<KeyValuePair<int, float>>();
            if (global::Singleton<Finance>.IsInstanced() && global::Singleton<Finance>.Instance.CollectedGold != null)
                foreach (var kv in global::Singleton<Finance>.Instance.CollectedGold)
                {
                    int idx = LotIndex(kv.Key);
                    if (idx >= 0) owed.Add(new KeyValuePair<int, float>(idx, kv.Value));
                }
            bw.Write((ushort)owed.Count);
            foreach (var kv in owed) { bw.Write(kv.Key); bw.Write(kv.Value); }
            byte[] body = bodyMs.ToArray();
            bool same = _statsBody != null && body.Length == _statsBody.Length;
            for (int i = 0; same && i < body.Length; i++) same = body[i] == _statsBody[i];
            if (same && !_statsForce && now - _statsSentAt < StatsRefresh) return;
            _statsForce = false;
            _statsBody = body;
            _statsSentAt = now;
            _statsSeasonSent = season;
            PruneAcks(now);
            var ms = Begin(SubStats, out BinaryWriter w);
            WriteAcks(w);
            w.Write(body);
            // Real play time changes every frame: sent, but not part of the change check.
            w.Write(st.SeasonsStats[season].RealTimePlay);
            w.Write(withPrev ? st.SeasonsStats[prev].RealTimePlay : 0f);
            TrafficC.Broadcast(TrafficC.Cat.Stats, ms.ToArray(), true);
        }

        private static void ReadSeasonBody(BinaryReader r, GameStatistics st, bool apply, out int season)
        {
            season = r.ReadInt32();
            var f = new float[12];
            for (int i = 0; i < f.Length; i++) f[i] = r.ReadSingle();
            int days = r.ReadUInt16();
            var dayStats = new List<GameStatistics.DayStat>(days);
            for (int i = 0; i < days; i++) dayStats.Add(new GameStatistics.DayStat(r.ReadSingle(), r.ReadSingle()));
            int nc = r.ReadUInt16();
            var claims = new Dictionary<LotDescriptor, GameStatistics.ClaimStatistics>();
            for (int i = 0; i < nc; i++)
            {
                int idx = r.ReadInt32();
                float mud = r.ReadSingle(), gold = r.ReadSingle(), dia = r.ReadSingle();
                LotDescriptor lot = LotAt(idx);
                if (lot == null || claims.ContainsKey(lot)) continue;
                claims[lot] = new GameStatistics.ClaimStatistics(new ResourceBag(), mud) { CollectedGold = gold, CollectedDiamonds = dia };
            }
            if (!apply || st == null || season < 0 || season > 1000) return;
            GameStatistics.SeasonStats s = st.SeasonsStats[season];
            s.DriveKilometers = f[0]; s.UsedFuel = f[1]; s.EarnMoney = f[2]; s.CollectedMagnetite = f[3];
            s.WorkersSalary = f[4]; s.DayEarnedGold = f[5]; s.DayEarndedDiamonds = f[6]; s.DayWorkersGold = f[7];
            s.DayWorkersMoney = f[8]; s.DayMud = f[9]; s.EndSeasonGold = f[10]; s.EndSeasonDiamonds = f[11];
            s.DayStats = dayStats;
            s.Claims = claims;
        }

        private static void ReceiveStats(BinaryReader r)
        {
            bool hasAck = ReadAcks(r, out uint ack);
            bool apply = !Net.IsHost && Synced();
            GameStatistics st = Stats;
            Gsm g = GsmInst;
            int blocks = r.ReadByte();
            var seasons = new int[blocks];
            for (int b = 0; b < blocks; b++) ReadSeasonBody(r, st, apply && g != null, out seasons[b]);
            float mudCount = r.ReadSingle();
            int nugget = r.ReadInt32();
            float atNight = r.ReadSingle();
            int nOwed = r.ReadUInt16();
            var owed = new Dictionary<LotDescriptor, float>();
            for (int i = 0; i < nOwed; i++)
            {
                int idx = r.ReadInt32();
                float v = r.ReadSingle();
                LotDescriptor lot = LotAt(idx);
                if (lot != null) owed[lot] = v;
            }
            float rtp0 = r.ReadSingle(), rtp1 = r.ReadSingle();
            if (!apply || st == null || g == null) return;
            if (global::Singleton<Finance>.IsInstanced()) global::Singleton<Finance>.Instance.CollectedGold = owed;
            st.CurrentMudCount = mudCount;
            st.CurrentNugget = nugget;
            st.GoldCollectedAtNight = atNight;
            if (blocks > 0) st.SeasonsStats[seasons[0]].RealTimePlay = rtp0;
            if (blocks > 1) st.SeasonsStats[seasons[1]].RealTimePlay = rtp1;
            // Own sales/driving the host has not counted yet stay visible (current season).
            if (blocks > 0 && seasons[0] == g.Season)
            {
                float earn = _statEarn, km = _statKm;
                foreach (Pend p in Pending)
                    if (!hasAck || (int)(p.Seq - ack) > 0) { earn += p.Earn; km += p.Km; }
                GameStatistics.SeasonStats s = st.SeasonsStats[seasons[0]];
                s.EarnMoney += earn;
                s.DriveKilometers += km;
            }
        }

        // ================================================================== stock setters

        /// <summary>Skips a stock change on a client inside host-only / host-simulated code.</summary>
        /// <remarks>On the host SuppressDepth is only raised while it replays processing of a machine a
        /// client drives (ContainerSync): that client makes the stock change.</remarks>
        private static bool AllowSet() => _applying || SuppressDepth <= 0 || !Synced();

        [HarmonyPatch]
        private static class PatchStockSetters
        {
            private static List<MethodBase> _targets;

            private static List<MethodBase> Targets()
            {
                if (_targets != null) return _targets;
                _targets = new List<MethodBase>();
                foreach (string name in new[] { "SetCash", "SetGold", "SetDiamonds", "SetMagnetide" })
                {
                    MethodInfo m = Need(typeof(Gsm), name, "host-only stock changes (" + name + ")");
                    if (m != null) _targets.Add(m);
                }
                MethodInfo nug = AccessTools.PropertySetter(typeof(Gsm), nameof(Gsm.SumofGoldNuggets));
                if (nug != null) _targets.Add(nug);
                else Plugin.Log.LogWarning("Patch target Gsm.SumofGoldNuggets not found: host-only nugget counter is OFF");
                return _targets;
            }

            private static IEnumerable<MethodBase> TargetMethods() => Targets();
            private static bool Prepare() => Targets().Count > 0;
            private static bool Prefix() => AllowSet();
        }

        // ================================================================== host-only code

        private static int _hostOnlyDepth;

        private static void Enter(ref bool state)
        {
            state = IsClientSynced();
            if (!state) return;
            // Changes the player made just before stay deltas; changes made inside are the host's job.
            if (_hostOnlyDepth++ == 0) ContainerSync.CaptureAll();
            SuppressDepth++;
        }

        private static void Leave(bool state)
        {
            if (!state) return;
            if (SuppressDepth > 0) SuppressDepth--;
            if (_hostOnlyDepth > 0 && --_hostOnlyDepth == 0) ContainerSync.AbsorbAll();
        }

        /// <summary>
        /// Code every PC runs for the shared world at the same moment. Its stock changes are made by the
        /// host only (the client's copy would count them a second time).
        /// </summary>
        [HarmonyPatch]
        private static class PatchHostOnly
        {
            private static List<MethodBase> _targets;

            private static List<MethodBase> Targets()
            {
                if (_targets != null) return _targets;
                _targets = new List<MethodBase>();
                void Add(Type t, string name)
                {
                    MethodInfo m = Need(t, name, "host-only money of " + name);
                    if (m != null) _targets.Add(m);
                }
                Add(typeof(Finance), "CollectDailyData");          // machine rent, loan rates, claim rent
                Add(typeof(SeasonController), "EndSeasonAndGoToNext"); // gold left in machines, season bonus
                Add(typeof(SeasonRestoreGold), "Submit");
                Add(typeof(MachineController), "WorkerRefuel");
                Add(typeof(LaptopGlobalManager), "Start");          // MoneyToAdd
                Add(typeof(LaptopGlobalManager), "AfterDeserialize");
                Add(typeof(AddCash_OnChestOpenEffectSO), "ApplySeasonEffect");
                Add(typeof(AddGoldAmount_OnAllChestCollected), "ApplySeasonEffect");
                Add(typeof(SetStartingMoney_OnNewSaveEffectSO), "ApplySeasonEffect");
                return _targets;
            }

            private static IEnumerable<MethodBase> TargetMethods() => Targets();
            private static bool Prepare() => Targets().Count > 0;

            private static void Prefix(ref bool __state) => Enter(ref __state);

            private static Exception Finalizer(Exception __exception, bool __state)
            {
                Leave(__state);
                return __exception;
            }
        }

        /// <summary>
        /// Random events (gold/money theft, workers quitting, ...) are rolled by the host only. A client
        /// rolling its own would remove other workers and steal other amounts.
        /// </summary>
        [HarmonyPatch]
        private static class PatchRandomEvents
        {
            private static MethodBase TargetMethod() => Need(typeof(RandomEvents), nameof(RandomEvents.InvokeRandomEvent), "host-only random events");
            private static bool Prepare() => TargetMethod() != null;
            private static bool Prefix() => !IsClientSynced();
        }

        // ================================================================== workers

        private static readonly MethodInfo DetachWorkerM = Find(typeof(Worker), "DetachWorker", Type.EmptyTypes);
        private static readonly HashSet<string> Roster = new HashSet<string>();
        private static readonly HashSet<string> RosterNow = new HashSet<string>();
        private static readonly List<Worker> RosterSeen = new List<Worker>(); // worker objects of the last check
        private static bool _rosterBased;
        private static float _nextRoster;

        private static string WorkerKey(Worker w)
        {
            if (w == null) return null;
            Worker.WorkerImageInfo i = w.ImageInfo;
            // WorkerImageInfo overloads == / != without a null check (it reads obj2.Unique): "i == null"
            // threw a NullReferenceException for every worker, so the roster check failed every time the
            // crew changed and no departure was ever sent. Reference comparison only.
            return ReferenceEquals(i, null)
                ? w.Name + "|" + w.IsMen
                : w.Name + "|" + w.IsMen + "|" + i.Unique + "," + i.Eyes + "," + i.Hair + "," + i.Head + "," + i.Nose + "," + i.Mouth + "," + i.Clothes + "," + i.Background;
        }

        /// <summary>A worker that leaves this PC's roster leaves everyone's (hires: PurchaseSync).</summary>
        private static void RosterTick()
        {
            float now = Time.unscaledTime;
            if (now < _nextRoster) return;
            _nextRoster = now + 1f;
            if (!global::Singleton<WorkersManager>.IsInstanced()) return;
            List<Worker> list = global::Singleton<WorkersManager>.Instance.MyWorkers;
            if (list == null) return;
            // Same worker objects as last time: nothing left, nothing to build (no strings every second).
            bool same = _rosterBased && list.Count == RosterSeen.Count;
            for (int i = 0; same && i < list.Count; i++) same = ReferenceEquals(list[i], RosterSeen[i]);
            if (same) return;
            RosterSeen.Clear();
            RosterSeen.AddRange(list);
            RosterNow.Clear();
            foreach (Worker w in list) { string k = WorkerKey(w); if (k != null) RosterNow.Add(k); }
            if (_rosterBased)
                foreach (string k in Roster)
                {
                    if (RosterNow.Contains(k)) continue;
                    var ms = Begin(SubWorkerGone, out BinaryWriter bw);
                    bw.Write(k);
                    TrafficC.Broadcast(TrafficC.Cat.Workers, ms.ToArray(), true);
                    Plugin.Log.LogInfo("Gold sync: worker left the crew: " + k.Split('|')[0]);
                }
            Roster.Clear();
            Roster.UnionWith(RosterNow);
            _rosterBased = true;
        }

        private static void ReceiveWorkerGone(BinaryReader r)
        {
            string key = r.ReadString();
            if (!Synced() || !global::Singleton<WorkersManager>.IsInstanced()) return;
            List<Worker> list = global::Singleton<WorkersManager>.Instance.MyWorkers;
            if (list == null) return;
            Roster.Remove(key); // not detected (and sent back) as our own removal
            for (int i = 0; i < list.Count; i++)
            {
                if (WorkerKey(list[i]) != key) continue;
                try { DetachWorkerM?.Invoke(list[i], null); }
                catch (Exception ex) { Plugin.Log.LogWarning("Gold sync: worker detach failed: " + ex.Message); }
                list.RemoveAt(i);
                Plugin.Log.LogInfo("Gold sync: worker left the crew (other player): " + key.Split('|')[0]);
                return;
            }
        }

        /// <summary>
        /// Daily salary: the host pays (its stock) and decides who leaves for lack of money (the roster
        /// sync removes that worker here). A client only books the payment on the worker.
        /// </summary>
        [HarmonyPatch]
        private static class PatchCollectSalary
        {
            private static MethodBase TargetMethod() => Need(typeof(Worker), nameof(Worker.CollectSalary), "host-only worker salary");
            private static bool Prepare() => TargetMethod() != null;

            private static bool Prefix(Worker __instance, bool oneDollar)
            {
                if (IsClientSynced())
                {
                    __instance.MyDailyEarning = 0f;
                    __instance.Preformance += 0.01f;
                    return false;
                }
                // Game bug: a worker that cannot be paid removes itself from WorkersManager.MyWorkers inside
                // WorkersManager.Update's foreach. The enumerator then throws, LastPaymentDay is not advanced and
                // the next frame runs payday again (with the one-dollar promo every worker is charged again).
                // The departure is deferred to the end of that Update (PatchWorkersUpdate): each worker is paid
                // exactly once per payday, single player included.
                try
                {
                    if (__instance == null || !global::Singleton<GoldDigger.WorkersManager>.IsInstanced()) return true;
                    if (oneDollar) __instance.MyDailyEarning = 1f;
                    if (!(global::Singleton<GoldDigger.GameStateManager>.Instance.Cash < __instance.MyDailyEarning)) return true;
                    if (!WorkersLeaving.Contains(__instance)) WorkersLeaving.Add(__instance);
                    string left = __instance.Name + new LocalizationKey("WORKER_LEFT_YOU").GetLocalized();
                    global::Singleton<HUD>.Instance.ShowHint(left, left);
                    return false;
                }
                catch (Exception) { return true; }
            }
        }

        private static readonly List<Worker> WorkersLeaving = new List<Worker>();

        [HarmonyPatch]
        private static class PatchWorkersUpdate
        {
            private static MethodBase TargetMethod() => Need(typeof(GoldDigger.WorkersManager), "Update", "worker departures on payday", Type.EmptyTypes);
            private static bool Prepare() => TargetMethod() != null;

            private static Exception Finalizer(Exception __exception, GoldDigger.WorkersManager __instance)
            {
                if (WorkersLeaving.Count > 0)
                {
                    try { if (__instance != null) foreach (Worker w in WorkersLeaving) __instance.MyWorkers.Remove(w); }
                    catch (Exception) { /* list gone with the world */ }
                    WorkersLeaving.Clear();
                }
                return __exception;
            }
        }

        // ================================================================== forge: refusable spends, bar collection

        private static global::GameStateManager ForgeGsm() =>
            global::Singleton<global::GameStateManager>.IsInstanced() ? global::Singleton<global::GameStateManager>.Instance : null;

        private struct RemeltState
        {
            public bool Valid, Remelting;
            public HashSet<int> Bars;
        }

        private static HashSet<int> BarIds()
        {
            var ids = new HashSet<int>();
            foreach (GoldBar b in UnityEngine.Object.FindObjectsOfType<GoldBar>()) if (b != null) ids.Add(b.gameObject.GetInstanceID());
            foreach (GoldBarChest c in UnityEngine.Object.FindObjectsOfType<GoldBarChest>()) if (c != null) ids.Add(c.gameObject.GetInstanceID());
            return ids;
        }

        /// <summary>Undo of a refused smelting: the remelt stops (its coroutine ends on the flag), the forge sync tells the others.</summary>
        private static void CancelRemelt(BlacksmithCraftGUI gui)
        {
            global::GameStateManager f = ForgeGsm();
            if (f != null)
            {
                f.remeltingInProgress = false;
                f.goldIsRemelted = false;
                f.timeSave = 0f;
            }
            if (gui != null) gui.goldIsRemelting = false; // its OnDisable copies this back into remeltingInProgress
        }

        /// <summary>Undo of a refused tutorial smelting: the bars it handed out go away.</summary>
        private static void RemoveBars(List<GameObject> bars)
        {
            foreach (GameObject go in bars)
            {
                if (go == null) continue;
                if (global::Singleton<GravityGun>.IsInstanced() && global::Singleton<GravityGun>.Instance.PickedObject == go)
                    global::Singleton<GravityGun>.Instance.DropObject(false);
                UnityEngine.Object.Destroy(go);
            }
        }

        /// <summary>
        /// Client: smelting at the blacksmith spends gold (and cash) the host may have spent a moment before.
        /// The spend travels alone; if the host refuses it the remelt is cancelled (tutorial: its bars removed)
        /// while the ledger gives the gold and cash back.
        /// </summary>
        [HarmonyPatch]
        private static class PatchRemelt
        {
            private static MethodBase TargetMethod() => Need(typeof(BlacksmithCraftGUI), nameof(BlacksmithCraftGUI.StartGoldRemelt), "refusable smelting");
            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(out RemeltState __state)
            {
                __state = default;
                if (!IsClientSynced()) return;
                IsolateBegin();
                global::GameStateManager f = ForgeGsm();
                __state.Valid = true;
                __state.Remelting = f != null && f.remeltingInProgress;
                __state.Bars = BarIds();
            }

            private static Exception Finalizer(Exception __exception, BlacksmithCraftGUI __instance, RemeltState __state)
            {
                if (!__state.Valid) return __exception;
                try
                {
                    var undo = new List<Action>();
                    global::GameStateManager f = ForgeGsm();
                    BlacksmithCraftGUI gui = __instance;
                    if (!__state.Remelting && ((f != null && f.remeltingInProgress) || (gui != null && gui.goldIsRemelting)))
                        undo.Add(() => CancelRemelt(gui));
                    var bars = new List<GameObject>();
                    foreach (GoldBar b in UnityEngine.Object.FindObjectsOfType<GoldBar>())
                        if (b != null && !__state.Bars.Contains(b.gameObject.GetInstanceID())) bars.Add(b.gameObject);
                    foreach (GoldBarChest c in UnityEngine.Object.FindObjectsOfType<GoldBarChest>())
                        if (c != null && !__state.Bars.Contains(c.gameObject.GetInstanceID())) bars.Add(c.gameObject);
                    if (bars.Count > 0) undo.Add(() => RemoveBars(bars));
                    IsolateEnd(undo.Count > 0 ? undo : null);
                }
                catch (Exception ex) { GLedger.Fail(ex); }
                return __exception;
            }
        }

        private struct LevelState
        {
            public bool Valid;
            public int Time, Cost, Loss, Max;
        }

        /// <summary>Client: a forge upgrade spends magnetite; refused by the host, the level goes back.</summary>
        [HarmonyPatch]
        private static class PatchForgeUpgrade
        {
            private static MethodBase TargetMethod() => Need(typeof(BlacksmithUpgradeGUI), nameof(BlacksmithUpgradeGUI.Upgrade), "refusable forge upgrades");
            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(out LevelState __state)
            {
                __state = default;
                if (!IsClientSynced()) return;
                global::GameStateManager f = ForgeGsm();
                if (f == null) return;
                IsolateBegin();
                __state = new LevelState { Valid = true, Time = f.BlTimeLv, Cost = f.BlCostLv, Loss = f.BlLossLv, Max = f.BlMaxLv };
            }

            private static Exception Finalizer(Exception __exception, LevelState __state)
            {
                if (!__state.Valid) return __exception;
                try
                {
                    global::GameStateManager f = ForgeGsm();
                    List<Action> undo = null;
                    if (f != null && (f.BlTimeLv != __state.Time || f.BlCostLv != __state.Cost || f.BlLossLv != __state.Loss || f.BlMaxLv != __state.Max))
                    {
                        LevelState before = __state;
                        int t = f.BlTimeLv, c = f.BlCostLv, l = f.BlLossLv, m = f.BlMaxLv;
                        undo = new List<Action>
                        {
                            () =>
                            {
                                global::GameStateManager g = ForgeGsm();
                                if (g == null) return;
                                if (g.BlTimeLv == t) g.BlTimeLv = before.Time;
                                if (g.BlCostLv == c) g.BlCostLv = before.Cost;
                                if (g.BlLossLv == l) g.BlLossLv = before.Loss;
                                if (g.BlMaxLv == m) g.BlMaxLv = before.Max;
                                BlacksmithUpgradeGUI gui = UnityEngine.Object.FindObjectOfType<BlacksmithUpgradeGUI>();
                                if (gui != null) { gui.RefreshLevels(); gui.SetButtons(); }
                            },
                        };
                    }
                    IsolateEnd(undo);
                }
                catch (Exception ex) { GLedger.Fail(ex); }
                return __exception;
            }
        }

        // ---- bar collection: one remelt, one collector (the host decides) ----

        private static bool _claimOut, _forgeGranted;
        private static float _claimAt;
        private static uint _claimSeq;
        private static decimal _grantAmount;

        private static void ResetForgeClaims()
        {
            _claimOut = _forgeGranted = false;
            _grantAmount = 0m;
        }

        /// <summary>
        /// Client: opening / closing the forge window hands out the remelted bars (goldIsRemelted). Two
        /// players doing it at the same moment would both get them. The client asks the host first: the
        /// window does not hand them out until the host grants this PC the collection.
        /// </summary>
        [HarmonyPatch]
        private static class PatchForgeCollect
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string n in new[] { "OnEnable", "OnDisable" })
                {
                    MethodInfo m = Need(typeof(BlacksmithCraftGUI), n, "one collector per forge remelt", Type.EmptyTypes);
                    if (m != null) yield return m;
                }
            }

            private static bool Prepare()
            {
                foreach (var _ in TargetMethods()) return true;
                return false;
            }

            private static void Prefix(out bool __state)
            {
                __state = false;
                try
                {
                    if (!IsClientSynced()) return;
                    global::GameStateManager f = ForgeGsm();
                    if (f == null) return;
                    if (_forgeGranted)
                    {
                        // Granted while the window was closed: this call hands the bars out (the host's amount).
                        _forgeGranted = false;
                        f.tmpGoldAmount = new FixedGold(_grantAmount);
                        f.goldIsRemelted = true;
                        return;
                    }
                    if (!f.goldIsRemelted) return;
                    f.goldIsRemelted = false; // not handed out here before the host agrees
                    __state = true;
                    float now = Time.unscaledTime;
                    if (_claimOut && now - _claimAt < 5f) return;
                    _claimOut = true;
                    _claimAt = now;
                    if (++_claimSeq == 0) _claimSeq = 1;
                    var ms = Begin(SubForgeClaim, out BinaryWriter w);
                    w.Write(StreamId);
                    w.Write(_claimSeq);
                    TrafficC.Broadcast(TrafficC.Cat.World, ms.ToArray(), true);
                }
                catch (Exception ex) { GLedger.Fail(ex); }
            }

            private static Exception Finalizer(Exception __exception, bool __state)
            {
                if (__state)
                {
                    global::GameStateManager f = ForgeGsm();
                    if (f != null) f.goldIsRemelted = true; // still waiting for the host (no forge state change)
                }
                return __exception;
            }
        }

        /// <summary>Host: a player wants the remelted bars. The first one asking (or the host's own window) gets them.</summary>
        private static void ReceiveForgeClaim(BinaryReader r)
        {
            ulong origin = r.ReadUInt64();
            uint claim = r.ReadUInt32();
            if (!Net.IsHost || !Synced()) return;
            global::GameStateManager f = ForgeGsm();
            bool granted = f != null && f.goldIsRemelted;
            decimal amount = 0m;
            if (granted)
            {
                // FixedDecimal overloads != (AlignAndOperate reads both operands): "tmpGoldAmount != null"
                // threw, the claim was never answered and a client could never collect its remelted bars.
                amount = !ReferenceEquals(f.tmpGoldAmount, null) ? f.tmpGoldAmount.Value : 0m;
                f.goldIsRemelted = false; // collected (the forge sync tells everyone)
                f.remeltingInProgress = false;
                BlacksmithCraftGUI gui = UnityEngine.Object.FindObjectOfType<BlacksmithCraftGUI>();
                if (gui != null) gui.goldIsRemelting = false;
            }
            var ms = Begin(SubForgeGrant, out BinaryWriter w);
            w.Write(origin);
            w.Write(claim);
            w.Write(granted);
            w.Write(amount);
            TrafficC.Broadcast(TrafficC.Cat.World, ms.ToArray(), true);
            Plugin.Log.LogInfo(granted ? "Gold sync: forge bars collected by a player" : "Gold sync: forge bars claim refused (already collected)");
        }

        private static void ReceiveForgeGrant(BinaryReader r)
        {
            ulong origin = r.ReadUInt64();
            uint claim = r.ReadUInt32();
            bool granted = r.ReadBoolean();
            decimal amount = r.ReadDecimal();
            if (Net.IsHost || origin != StreamId || !Synced()) return;
            if (claim == _claimSeq) _claimOut = false;
            if (!granted)
            {
                ShowHint("Otro jugador ya recogió las barras de la forja");
                return;
            }
            global::GameStateManager f = ForgeGsm();
            if (f == null) return;
            BlacksmithCraftGUI gui = UnityEngine.Object.FindObjectOfType<BlacksmithCraftGUI>();
            if (gui == null || !gui.isActiveAndEnabled)
            {
                _forgeGranted = true; // handed out the next time the forge window opens
                _grantAmount = amount;
                return;
            }
            // Window open: hand the bars out now, as its OnEnable / OnDisable would.
            f.tmpGoldAmount = new FixedGold(amount);
            gui.AmountOfGold = f.tmpGoldAmount / Gsm.Gram2oz;
            gui.Submit();
            f.goldIsRemelted = false;
            f.remeltingInProgress = false;
            gui.goldIsRemelting = false;
            gui.AmountOfGold = 0;
        }

        // ================================================================== clock, season, weather

        private const float TimeInterval = 1f;
        private const double JumpHours = 0.25;      // a forward step larger than this in one frame = sleep / season end
        private const double TimeTolerance = 0.005; // hours (18 game seconds) before a client is corrected
        private const double SlewMax = 0.25;        // client ahead by more: step back at once

        private static bool _timeForce, _timeBased, _applyingSeason;
        private static float _nextTime, _nextEngineSearch;
        private static double _lastGameTime, _jumpTarget, _slew;
        private static float _jumpSentAt = -100f;
        private static readonly LatenessEstimator ClockLate = new LatenessEstimator();
        private static EGEngine _engine;

        private static EGEngine Engine()
        {
            if (_engine != null) return _engine;
            if (Time.unscaledTime < _nextEngineSearch) return null;
            _nextEngineSearch = Time.unscaledTime + 5f;
            try
            {
                GameObject go = GameObject.FindGameObjectWithTag("EGEngine");
                _engine = go != null ? go.GetComponent<EGEngine>() : null;
            }
            catch (Exception) { _engine = null; }
            return _engine;
        }

        private static void TimeTick(Gsm g)
        {
            double gt = g.GameTime;
            bool jumped = false;
            if (_timeBased)
            {
                double expected = _lastGameTime + Time.deltaTime * g.TimeScale / 3600.0;
                jumped = gt - expected > JumpHours;
                if (!jumped && g.TimeScale > 0 && g.TimeScale < 1000)
                {
                    // Real time this PC's clock did not count (pause / slow motion / capped frame time).
                    float lost = Mathf.Min(Time.unscaledDeltaTime, 1f) - Time.deltaTime;
                    if (lost > 0.0005f) g.GameTime += lost * g.TimeScale / 3600.0;
                    // Client a little ahead of the host: run at half speed until it is back in line. Far ahead
                    // (only when stepping back would cross midnight, see ReceiveTime): the clock stands still.
                    if (_slew > 0)
                    {
                        double adv = g.GameTime - _lastGameTime;
                        if (adv > 0)
                        {
                            double take = Math.Min(_slew, adv * (_slew > SlewMax ? 1.0 : 0.5));
                            g.GameTime -= take;
                            _slew -= take;
                        }
                    }
                }
            }
            _timeBased = true;
            _lastGameTime = g.GameTime;

            if (Net.IsHost)
            {
                if (jumped) _timeForce = true;
                if (!_timeForce && Time.unscaledTime < _nextTime) return;
                _timeForce = false;
                _nextTime = Time.unscaledTime + TimeInterval;
                SendTime(g);
            }
            else if (jumped)
            {
                // This player slept (or the season ended here): the host adopts the new time.
                _slew = 0;
                _jumpTarget = gt;
                _jumpSentAt = Time.unscaledTime;
                var ms = Begin(SubTimeJump, out BinaryWriter w);
                w.Write(gt);
                w.Write(g.Season);
                w.Write(g.SeasonStartDay);
                TrafficC.Broadcast(TrafficC.Cat.Clock, ms.ToArray(), true);
            }
        }

        /// <summary>Day number of a game time, the way GameStateManager.DayCount computes it (minus the constant 1).</summary>
        private static int DayOf(double gameTime) => Mathf.FloorToInt((float)gameTime - (float)Gsm.StartGameTime) / 24;

        private static void SendTime(Gsm g)
        {
            var ms = Begin(SubTime, out BinaryWriter w);
            w.Write(Time.unscaledTimeAsDouble); // send stamp (f64: float seconds lose ms after hours): the receiver compensates the jitter
            w.Write(g.GameTime);
            w.Write(g.TimeScale);
            w.Write(g.TimeScaleMax);
            w.Write(g.Season);
            w.Write(g.SeasonStartDay);
            EGEngine e = Engine();
            EGPreset preset = e != null ? e.LoadedPreset : null;
            bool weather = preset != null && preset.WeatherNow != null && preset.WeatherForecast != null && e.WeatherModule != null;
            w.Write(weather);
            if (weather)
            {
                w.Write(preset.WeatherNow.Name ?? "");
                w.Write(preset.WeatherForecast.Name ?? "");
                w.Write(e.WeatherModule._NextWeaterChangeTime);
                w.Write(preset.UseThunderSystem);
            }
            TrafficC.Broadcast(TrafficC.Cat.Clock, ms.ToArray(), true);
        }

        private static void ReceiveTime(BinaryReader r)
        {
            double stamp = r.ReadDouble();
            double gt = r.ReadDouble(), scale = r.ReadDouble(), scaleMax = r.ReadDouble();
            int season = r.ReadInt32(), seasonStart = r.ReadInt32();
            bool weather = r.ReadBoolean();
            string now = null, next = null;
            float change = 0f;
            bool thunder = false;
            if (weather)
            {
                now = r.ReadString();
                next = r.ReadString();
                change = r.ReadSingle();
                thunder = r.ReadBoolean();
            }
            if (Net.IsHost || !Synced()) return;
            Gsm g = GsmInst;
            if (g == null || double.IsNaN(gt) || double.IsInfinity(gt)) return;
            if (scale > 0 && scale < 1000)
            {
                g.TimeScale = scale;
                // This message came late by the link's jitter (a Wi-Fi spike of 300 ms is 4 game seconds at
                // the fastest time scale): add the game time the host counted meanwhile, so a late message
                // never sets the clock back or forth.
                double late = Math.Min(ClockLate.Late(stamp, Time.unscaledTimeAsDouble, 0.002), 2.0);
                gt += late * scale / 3600.0;
            }
            if (scaleMax > 0 && scaleMax < 1000) g.TimeScaleMax = scaleMax;
            // Our own jump (sleep) is on its way to the host: do not step back meanwhile.
            bool behindOwnJump = Time.unscaledTime - _jumpSentAt < 3f && gt < _jumpTarget - 0.05;
            if (!behindOwnJump)
            {
                double diff = gt - g.GameTime;
                // Far ahead (the host's game stood still: a long hitch, alt-tab without running in the
                // background): step back, unless that crosses midnight. Finance.Update would run the day
                // change (day panel, daily statistics) a second time after a step back over a day boundary:
                // then this PC's clock stands still until the host has caught up (TimeTick).
                bool farAhead = diff < -SlewMax && DayOf(gt) == DayOf(g.GameTime);
                if (diff > TimeTolerance || farAhead) { g.GameTime = gt; _slew = 0; }
                else if (diff < -TimeTolerance) _slew = -diff; // ahead: slow down / stand still, never step back over midnight
                else _slew = 0;
                g.Season = season;
                g.SeasonStartDay = seasonStart;
            }
            _lastGameTime = g.GameTime; // not a local jump
            if (!weather) return;
            try
            {
                EGEngine e = Engine();
                EGPreset preset = e != null ? e.LoadedPreset : null;
                if (preset == null || e.WeatherModule == null) return;
                bool changed = false;
                if (preset.WeatherNow == null || preset.WeatherNow.Name != now) { e.WeatherModule.SetCurrentWeather(now); changed = true; }
                if (preset.WeatherForecast == null || preset.WeatherForecast.Name != next) e.WeatherModule.SetNextWeather(next);
                if (preset.UseThunderSystem != thunder) { preset.UseThunderSystem = thunder; changed = true; }
                // Never change on our own before the host does (its change arrives with the next message).
                e.WeatherModule._NextWeaterChangeTime = change + 0.5f;
                if (changed && EGEvents.Instance != null) EGEvents.Instance.TrigWeatherIsChanged();
            }
            catch (Exception ex) { GWeather.Fail(ex); }
        }

        private static void ReceiveTimeJump(BinaryReader r)
        {
            double gt = r.ReadDouble();
            r.ReadInt32(); // season (the season changes only through sub 13: EndSeasonAndGoToNext runs on the host)
            r.ReadInt32(); // season start day
            if (!Net.IsHost || !Synced()) return;
            Gsm g = GsmInst;
            if (g == null || double.IsNaN(gt) || double.IsInfinity(gt)) return;
            if (gt <= g.GameTime + 0.05) return; // the host is already there (both slept, season end done)
            if (gt - g.GameTime > 24.0 * 400) return; // nonsense
            g.GameTime = gt;
            _lastGameTime = gt;
            _timeForce = true;
            Plugin.Log.LogInfo("Gold sync: a player slept, clock moved forward");
        }

        private static void ReceiveSeasonEnd(BinaryReader r)
        {
            int before = r.ReadInt32();
            if (!Net.IsHost || !Synced()) return;
            Gsm g = GsmInst;
            if (g == null || g.Season != before || SeasonController.Instance == null) return; // already done
            var items = new List<CheckAndRepair>();
            try
            {
                if (global::Singleton<HangarManager>.IsInstanced()) global::Singleton<HangarManager>.Instance.CheckObjectstToBrake(out items);
                _applyingSeason = true;
                SeasonController.Instance.EndSeasonAndGoToNext(items ?? new List<CheckAndRepair>());
                Plugin.Log.LogInfo("Gold sync: season ended by another player");
            }
            catch (Exception ex) { Plugin.Log.LogError("Gold sync: season end failed: " + ex.Message); }
            finally { _applyingSeason = false; }
            _lastGameTime = g.GameTime;
            _timeForce = true;
            _statsForce = true;
        }

        /// <summary>A client ended the season (hotel / winter): the host runs the real season change.</summary>
        [HarmonyPatch]
        private static class PatchSeasonEnd
        {
            private static MethodBase TargetMethod() => Need(typeof(SeasonController), nameof(SeasonController.EndSeasonAndGoToNext), "shared season end");
            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(out int __state)
            {
                Gsm g = GsmInst;
                __state = g != null ? g.Season : -1;
            }

            private static void Postfix(int __state)
            {
                if (_applyingSeason || __state < 0 || !IsClientSynced()) return;
                var ms = Begin(SubSeasonEnd, out BinaryWriter w);
                w.Write(__state);
                TrafficC.Broadcast(TrafficC.Cat.Clock, ms.ToArray(), true);
            }
        }
    }

    /// <summary>Error isolation of one sub-system: logs through an ErrorGate (SyncCore), never throws.</summary>
    internal sealed class SubsystemGuard
    {
        private readonly string _name;
        private readonly ErrorGate _gate = new ErrorGate();

        internal SubsystemGuard(string name) { _name = name; }

        internal void Fail(Exception ex)
        {
            try
            {
                if (!_gate.Pass(Time.unscaledTime, out int swallowed)) return;
                // Full stack trace once; later reports of the same sub-system only the message (log size).
                string what = _gate.Reports == 1 ? ex.ToString() : ex.GetType().Name + ": " + ex.Message;
                Plugin.Log.LogError(_name + " failed: " + what + (swallowed > 0 ? " (+" + swallowed + " more errors since the last report)" : ""));
            }
            catch (Exception) { /* logging must never throw into a game loop */ }
        }
    }

    /// <summary>
    /// Traffic of the player &amp; world features (PlayerSync, TerrainSync, TutorialSync, GoldSync and its
    /// container/dirt parts): one log line after the first minute, then one per 5 min. Sent bytes count every copy (one per peer);
    /// relays made by the host's Net layer are not included.
    /// </summary>
    internal static class TrafficC
    {
        internal enum Cat { Player, Terrain, Ledger, Clock, Containers, Dirt, Tutorial, Workers, Stats, World, Count }

        private static readonly string[] CatNames = { "player", "terrain", "ledger", "clock", "containers", "dirt", "tutorial", "workers", "stats", "world" };
        private static readonly int[] SentN = new int[(int)Cat.Count], RecvN = new int[(int)Cat.Count];
        private static readonly long[] SentB = new long[(int)Cat.Count], RecvB = new long[(int)Cat.Count];
        internal static long TerrainCells;
        private static float _since = -1f;
        private static bool _reportedOnce;
        private const float FirstReport = 60f, ReportInterval = 300f; // log size: one line per 5 min after the first minute

        internal static Cat CatOfSub(byte sub)
        {
            switch (sub)
            {
                case GoldSync.SubDelta: case GoldSync.SubState: case GoldSync.SubReject: return Cat.Ledger;
                case ContainerSync.SubAreas: return Cat.Containers;
                case GoldSync.SubForgeClaim: case GoldSync.SubForgeGrant: return Cat.World;
                case GoldSync.SubContDelta: case GoldSync.SubContState: case GoldSync.SubContRequest: return Cat.Containers;
                case GoldSync.SubParcelReset: case GoldSync.SubDrill: return Cat.Terrain;
                case DirtSync.SubLumps: return Cat.Dirt;
                case GoldSync.SubTime: case GoldSync.SubTimeJump: case GoldSync.SubSeasonEnd: return Cat.Clock;
                case GoldSync.SubWorkerGone: return Cat.Workers;
                case WorldSync.SubFlags: case WorldSync.SubBoom: case WorldSync.SubUse: case WorldSync.SubForge: case WorldSync.SubPaint: case WorldSync.SubFuel: case WorldSync.SubFuelOp: case WorldSync.SubWater: case WorldSync.SubWaterOp: case WorldSync.SubTable: case WorldSync.SubHood: return Cat.World;
                default: return Cat.Stats;
            }
        }

        internal static void Sent(Cat c, int bytes)
        {
            int peers = Net.Peers.Count;
            if (peers == 0) return;
            SentN[(int)c]++;
            SentB[(int)c] += (long)bytes * peers;
        }

        internal static void Recv(Cat c, int bytes)
        {
            RecvN[(int)c]++;
            RecvB[(int)c] += bytes;
        }

        internal static void Broadcast(Cat c, byte[] data, bool reliable)
        {
            Sent(c, data.Length);
            Net.Broadcast(data, reliable);
        }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (!Net.Active) { if (_since >= 0f) { Clear(-1f); _reportedOnce = false; } return; }
            if (_since < 0f) { Clear(now); return; }
            if (now - _since < (_reportedOnce ? ReportInterval : FirstReport)) return;
            _reportedOnce = true;
            var sb = new System.Text.StringBuilder();
            float span = now - _since;
            for (int i = 0; i < (int)Cat.Count; i++)
            {
                if (SentN[i] == 0 && RecvN[i] == 0) continue;
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(CatNames[i]).Append(' ')
                  .Append(SentN[i]).Append(" out ").Append(SentB[i] / 1024).Append(" KB, ")
                  .Append(RecvN[i]).Append(" in ").Append(RecvB[i] / 1024).Append(" KB");
                if (i == (int)Cat.Terrain && TerrainCells > 0) sb.Append(" (").Append(TerrainCells).Append(" cells)");
            }
            if (sb.Length > 0) Plugin.Log.LogInfo($"Traffic player & world, last {span:0} s: {sb}");
            Clear(now);
        }

        private static void Clear(float since)
        {
            Array.Clear(SentN, 0, SentN.Length);
            Array.Clear(RecvN, 0, RecvN.Length);
            Array.Clear(SentB, 0, SentB.Length);
            Array.Clear(RecvB, 0, RecvB.Length);
            TerrainCells = 0;
            _since = since;
        }
    }

    /// <summary>
    /// Logs once at startup which Harmony patches of the player &amp; world features are active and which
    /// are not (a game method that is gone turns its feature off; each missing target is also logged
    /// with the feature it disables).
    /// </summary>
    internal static class PatchReport
    {
        private static bool _done;

        internal static void LogOnce()
        {
            if (_done) return;
            _done = true;
            try
            {
                Type[] owners = { typeof(GoldSync), typeof(ContainerSync), typeof(DirtSync), typeof(TerrainSync), typeof(TutorialSync), typeof(PlayerSync), typeof(WorldSync) };
                var counts = new Dictionary<Type, int>();
                foreach (MethodBase m in Harmony.GetAllPatchedMethods())
                {
                    Patches info = Harmony.GetPatchInfo(m);
                    if (info == null) continue;
                    var seen = new HashSet<Type>();
                    foreach (var list in new[] { info.Prefixes, info.Postfixes, info.Finalizers, info.Transpilers })
                        foreach (Patch p in list)
                        {
                            if (p.owner != Plugin.Guid || p.PatchMethod == null) continue;
                            Type t = p.PatchMethod.DeclaringType;
                            if (t == null || !seen.Add(t)) continue;
                            counts.TryGetValue(t, out int c);
                            counts[t] = c + 1;
                        }
                }
                var active = new System.Text.StringBuilder();
                var inactive = new System.Text.StringBuilder();
                int total = 0;
                foreach (Type owner in owners)
                    foreach (Type t in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                        string name = owner.Name + "." + t.Name;
                        if (counts.TryGetValue(t, out int c) && c > 0)
                        {
                            total += c;
                            if (active.Length > 0) active.Append(", ");
                            active.Append(name);
                            if (c > 1) active.Append(" x").Append(c);
                        }
                        else
                        {
                            if (inactive.Length > 0) inactive.Append(", ");
                            inactive.Append(name);
                        }
                    }
                Plugin.Log.LogInfo($"Player & world patches active ({total} game methods): {active}");
                if (inactive.Length > 0) Plugin.Log.LogWarning("Player & world patches NOT active (feature off): " + inactive);
                else Plugin.Log.LogInfo("Player & world patches: all active");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Patch report failed: " + ex.Message); }
        }
    }
}
