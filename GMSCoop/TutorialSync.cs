using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using SaveSystem;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Shares tutorial progress. The HOST is the authority:
    ///  - Only the host announces the current step (StepState, with the conditions already met).
    ///    Clients jump to it.
    ///  - When a client's own tutorial advances (the client did the action), it sends a Request
    ///    "from step A to step B". The host applies it only if it is still on step A and B is a
    ///    forward successor of A in the graph, then announces B. Otherwise the host re-announces its
    ///    own step, which pulls the client back in line.
    ///  - Condition progress is shared too (CondMet): a step with several conditions, a composite step
    ///    with several child steps, or a branching step can be finished by different players. Every
    ///    condition is sticky in the game (once met it stays met until the step is re-entered), so
    ///    marking it met on every PC with SetMet(true) lets each runner finish the step on its own.
    ///    Child steps of composites are completed by the runner itself once their conditions are met.
    ///  - Fail conditions: a client that meets one sends it (path "f.j", "kf{i}.j", "bf{i}.j"); only the
    ///    HOST applies it, its runner then fails the step (fail link, or re-enter for ResetCurrentStep)
    ///    and announces the result. StepState carries the host's entry serial, so a re-entry of the
    ///    same step (step reset) reaches the clients too. A client that already reset on its own does
    ///    not reset twice.
    ///  - A condition is sent at most once per entry of a step, so live world-state conditions that
    ///    flicker (FillPercent, ZoneHasItemCount, ...) do not spam messages.
    ///  - Latency: a client that advances several steps within one round trip sends one request per step
    ///    (tagged with a sequence the host echoes in its answer). A host state on the client's own path
    ///    is not applied while a request is unanswered, so the client is never pulled back to a step it
    ///    already left; an advance made since the last poll is requested before any host state is applied
    ///    (it was overwritten and lost before). ReplyQueue, SyncCore.cs.
    ///  - Changes caused by loading a save are never sent. A client that just loaded re-applies the
    ///    host's last announced step once the load has settled (the runner's resume coroutine can
    ///    overwrite a step that was applied too early).
    ///
    /// The game has two tutorial systems:
    ///  - New graph tutorial: TutorialRunner.CurrentStep (TutorialStep.StepId). Applied with the private
    ///    TutorialRunner.EnterStep(step, pushHistory, forceReloadPDA) or TransitionTo(null) for the end.
    ///  - Legacy prologue mission: MissionGR.MyMissionAnimator, one Animator state per objective.
    ///    Applied with Animator.Play(shortNameHash), like the game's own Deserialize (which also calls
    ///    StopTutorial for the "Idle" state).
    /// </summary>
    internal static class TutorialSync
    {
        private const string Done = "__done__";
        private const float QuietAfterLoad = 5f;
        private const int IdleHash = 2081823275; // Animator.StringToHash("Idle"), see MissionGR.Deserialize
        private const int MaxForwardHops = 4;

        private static readonly MethodInfo EnterStep = AccessTools.Method(typeof(TutorialRunner), "EnterStep");
        private static readonly MethodInfo TransitionTo = AccessTools.Method(typeof(TutorialRunner), "TransitionTo");
        private static readonly FieldInfo CompletedField = AccessTools.Field(typeof(TutorialRunner), "_completed");
        private static readonly MethodInfo SetMet = AccessTools.Method(typeof(TutorialConditionSO), "SetMet");
        private static readonly MethodInfo StopTutorial = AccessTools.Method(typeof(MissionGR), "StopTutorial");

        private static string _lastStep;      // last step seen locally
        private static string _lastSentStep;  // host: last step announced
        private static string _hostStep;      // client: last step the host announced
        private static List<string> _hostMet = new List<string>();
        private static int _lastMission;
        private static int _lastSentMission;
        private static int _hostMission;
        private static float _nextPoll;
        private static float _readySince = -1f;
        private static bool _settling = true;

        // Entries of a step (EnterStep calls), also re-entries of the same step.
        private static int _entrySerial;
        private static float _entryTime;
        private static bool _applyingStep;
        private static int _lastSentSerial = int.MinValue; // host
        private static int _hostEpoch = int.MinValue;      // client: host entry serial last applied
        private static int _hostStepEpoch = int.MinValue;  // client: host entry serial last announced
        private static int _localReentries;                // client: own re-entries since the last host state
        private static int _leavesSerial = -1;
        private static readonly HashSet<string> SentThisEntry = new HashSet<string>();
        private const float FailGuard = 1.5f;

        // Client: own step / mission requests the host has not answered yet. While one is in flight, a host
        // state on this PC's own path (older than the host's answer) is not applied: this PC is already
        // further (C -> B -> C for one round trip otherwise, with B's enter actions run again). The host's
        // answer to the newest request always applies. SyncCore.ReplyQueue.
        private const float ReplyTimeout = 5f;
        private static readonly ReplyQueue<string> StepReplies = new ReplyQueue<string>(ReplyTimeout,
            (from, to, state) => state != Done && IsForward(from, state) && IsForward(state, to)); // a request can skip steps
        private static readonly ReplyQueue<int> MissionReplies = new ReplyQueue<int>(ReplyTimeout);

        // Leaf conditions of the current step, cached per step: (path, condition).
        private static TutorialStep _leavesFor;
        private static readonly List<KeyValuePair<string, TutorialConditionSO>> Leaves =
            new List<KeyValuePair<string, TutorialConditionSO>>();
        private static readonly HashSet<string> MetKnown = new HashSet<string>();
        private static readonly List<string> NewlyMet = new List<string>();

        private enum Kind : byte
        {
            StepState = 1,      // host -> all: string step, byte n, n x string met path, int entry serial, bool reset, u64 reply-to, u32 reply seq
            MissionState = 2,   // host -> all: int hash, u64 reply-to, u32 reply seq
            StepRequest = 3,    // client -> host: string from, string to, u64 sender LocalId, u32 seq
            MissionRequest = 4, // client -> host: int from, int to, u64 sender LocalId, u32 seq
            CondMet = 5,        // anyone -> all (host relays): string step, byte n, n x string path
        }

        private static readonly SubsystemGuard GTick = new SubsystemGuard("Tutorial sync"),
            GRecv = new SubsystemGuard("Tutorial sync: receive");
        private static bool _wasReady;

        /// <summary>Every frame (polls every 0.25 s). Never throws into the plugin loop.</summary>
        internal static void Tick()
        {
            try { TickCore(); }
            catch (System.Exception ex) { GTick.Fail(ex); }
        }

        private static void TickCore()
        {
            if (!Net.Active)
            {
                // Forget the previous session (a stale host step must not be applied in the next one).
                _hostStep = null;
                _hostMission = 0;
                _lastSentStep = null;
                _lastSentMission = 0;
                _lastSentSerial = int.MinValue;
                _hostEpoch = _hostStepEpoch = int.MinValue;
                _localReentries = 0;
                StepReplies.Clear();
                MissionReplies.Clear();
                _wasReady = false;
                return;
            }
            // A client stays idle until it plays the host's world (its own menu/tutorial world must not leak).
            // Deliberately SaveTransfer.WorldReady, not Net.WorldLive: a load inside the shared world is
            // handled below (quiet period, then the host's step re-applied); only a replaced world (not ready
            // any more) drops the host's step and requests.
            bool ready = SaveTransfer.WorldReady;
            if (_wasReady && !ready && !Net.IsHost)
            {
                // The world this client played is being replaced (host reload / rejoin): the host's step of
                // that world must not be applied to the new one; the host sends its state again after the
                // client loaded (SaveTransfer -> SendFullState). Requests of the old world are void.
                _hostStep = null;
                _hostMet = new List<string>();
                _hostMission = 0;
                _hostEpoch = _hostStepEpoch = int.MinValue;
                _localReentries = 0;
                StepReplies.Clear();
                MissionReplies.Clear();
            }
            _wasReady = ready;
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.25f;
            if (!ready)
            {
                // Not in the shared world (pre-join, or the world is being replaced): once it is ready again,
                // the restored step is tracked during the quiet period and the host's step applied after it,
                // exactly as after an in-world load (the runner resumes from the save meanwhile).
                _readySince = -1f;
                _settling = true;
                return;
            }

            bool loading = GameState.IsLoading;
            if (loading)
            {
                _readySince = -1f;
                _settling = true;
                StepReplies.Clear();
                MissionReplies.Clear();
            }
            else if (_readySince < 0f) _readySince = Time.unscaledTime;
            bool quiet = loading || Time.unscaledTime - _readySince < QuietAfterLoad;

            string step = CurrentStepId();
            int mission = CurrentMissionHash();

            if (Net.IsHost)
            {
                // The host is the authority: whatever its runner does (also after reloading its own
                // save) is the truth. Only wait until the load is over.
                _lastStep = step;
                _lastMission = mission;
                if (!loading)
                {
                    if (step != null && (step != _lastSentStep || _entrySerial != _lastSentSerial)) SendStepState(step);
                    if (mission != 0 && mission != _lastSentMission) SendMissionState(mission);
                    PollConditions(step);
                }
                return;
            }

            if (quiet)
            {
                // A load restores the saved step: track it, never send it.
                _lastStep = step;
                _lastMission = mission;
                return;
            }

            if (_settling)
            {
                // Load is over: line up with the host's last announcement (it may have arrived while
                // the runner was still resuming from the save and been overwritten).
                _settling = false;
                if (_hostStep != null && step != _hostStep)
                {
                    ApplyStep(_hostStep, _hostMet);
                    step = CurrentStepId();
                }
                _hostEpoch = _hostStepEpoch;
                _localReentries = 0;
                if (_hostMission != 0 && mission != _hostMission)
                {
                    ApplyMission(_hostMission);
                    mission = CurrentMissionHash();
                }
                _lastStep = step;
                _lastMission = mission;
                return;
            }

            FlushLocalStep(step);
            FlushLocalMission(mission);
            PollConditions(step);
        }

        /// <summary>True while a client may send requests (host world loaded, settled, quiet period over).</summary>
        private static bool ClientMaySend() =>
            !Net.IsHost && Net.WorldLive && !_settling
            && _readySince >= 0f && Time.unscaledTime - _readySince >= QuietAfterLoad;

        /// <summary>
        /// Client: this PC's runner moved on since the last look -> ask the host. Also called right before a
        /// host step is applied: an advance made in the last poll interval (0.25 s) would otherwise be
        /// overwritten by the host's confirmation of the previous step and lost (its conditions reset).
        /// </summary>
        private static void FlushLocalStep(string step)
        {
            if (step == null || step == _lastStep) return;
            string from = _lastStep;
            _lastStep = step;
            if (from != null) SendStepRequest(from, step);
        }

        private static void FlushLocalMission(int mission)
        {
            if (mission == 0 || mission == _lastMission) return;
            int from = _lastMission;
            _lastMission = mission;
            if (from != 0) SendMissionRequest(from, mission);
        }

        /// <summary>Host: push the current state (a player just loaded the world).</summary>
        internal static void SendFullState()
        {
            if (!Net.IsHost) return;
            try // called by SaveTransfer, before the economy catch-up: never throws into it
            {
                string step = CurrentStepId();
                if (step != null) SendStepState(step);
                int mission = CurrentMissionHash();
                if (mission != 0) SendMissionState(mission);
            }
            catch (System.Exception ex) { GTick.Fail(ex); }
        }

        // ---------- send ----------

        private static BinaryWriter Begin(Kind kind, out MemoryStream ms)
        {
            ms = Net.Writer(Msg.Tutorial);
            var w = new BinaryWriter(ms);
            w.Write((byte)kind);
            return w;
        }

        private static void WritePaths(BinaryWriter w, List<string> paths)
        {
            int n = Mathf.Min(paths.Count, 255);
            w.Write((byte)n);
            for (int i = 0; i < n; i++) w.Write(paths[i]);
        }

        private static List<string> ReadPaths(BinaryReader r)
        {
            int n = r.ReadByte();
            var list = new List<string>(n);
            for (int i = 0; i < n; i++) list.Add(r.ReadString());
            return list;
        }

        private static void SendStepState(string step, ulong replyTo = 0, uint replySeq = 0)
        {
            // Same step announced again with a new entry serial = the host's runner re-entered it (reset).
            bool reset = step == _lastSentStep && _lastSentSerial != int.MinValue && _entrySerial != _lastSentSerial;
            _lastSentStep = step;
            _lastSentSerial = _entrySerial;
            var met = new List<string>();
            if (step != Done && Singleton<TutorialRunner>.IsInstanced())
            {
                RefreshLeaves(Singleton<TutorialRunner>.Instance);
                foreach (var leaf in Leaves)
                    if (leaf.Value.IsMet) met.Add(leaf.Key);
            }
            var w = Begin(Kind.StepState, out var ms);
            w.Write(step);
            WritePaths(w, met);
            w.Write(_entrySerial);
            w.Write(reset);
            w.Write(replyTo);   // the request this state answers (0: none), see ReplyQueue
            w.Write(replySeq);
            TrafficC.Broadcast(TrafficC.Cat.Tutorial, ms.ToArray(), true);
            Plugin.Log.LogInfo($"Tutorial sync: host step {step} ({met.Count} conditions met)");
        }

        private static void SendMissionState(int hash, ulong replyTo = 0, uint replySeq = 0)
        {
            _lastSentMission = hash;
            var w = Begin(Kind.MissionState, out var ms);
            w.Write(hash);
            w.Write(replyTo);
            w.Write(replySeq);
            TrafficC.Broadcast(TrafficC.Cat.Tutorial, ms.ToArray(), true);
            Plugin.Log.LogInfo("Tutorial sync: host mission stage " + hash);
        }

        private static void SendStepRequest(string from, string to)
        {
            var w = Begin(Kind.StepRequest, out var ms);
            w.Write(from);
            w.Write(to);
            w.Write(Net.LocalId);
            w.Write(StepReplies.Sent(from, to, Time.unscaledTime));
            TrafficC.Broadcast(TrafficC.Cat.Tutorial, ms.ToArray(), true); // clients only have the host as peer
            Plugin.Log.LogInfo($"Tutorial sync: asking host {from} -> {to}");
        }

        private static void SendMissionRequest(int from, int to)
        {
            var w = Begin(Kind.MissionRequest, out var ms);
            w.Write(from);
            w.Write(to);
            w.Write(Net.LocalId);
            w.Write(MissionReplies.Sent(from, to, Time.unscaledTime));
            TrafficC.Broadcast(TrafficC.Cat.Tutorial, ms.ToArray(), true);
        }

        private static void SendCondMet(string step, List<string> paths)
        {
            var w = Begin(Kind.CondMet, out var ms);
            w.Write(step);
            WritePaths(w, paths);
            TrafficC.Broadcast(TrafficC.Cat.Tutorial, ms.ToArray(), true); // the host relays it to the other clients
            Plugin.Log.LogInfo($"Tutorial sync: {paths.Count} condition(s) met in {step}");
        }

        // ---------- receive ----------

        /// <summary>A failing message is logged and dropped here: the host's Net layer still relays it.</summary>
        internal static void Receive(BinaryReader r)
        {
            try { ReceiveCore(r); }
            catch (System.Exception ex) { GRecv.Fail(ex); }
        }

        private static void ReceiveCore(BinaryReader r)
        {
            TrafficC.Recv(TrafficC.Cat.Tutorial, (int)r.BaseStream.Length + 1);
            var kind = (Kind)r.ReadByte();
            switch (kind)
            {
                case Kind.StepState:
                    {
                        string step = r.ReadString();
                        List<string> met = ReadPaths(r);
                        int epoch = r.ReadInt32();
                        bool reset = r.ReadBoolean();
                        ulong replyTo = r.ReadUInt64();
                        uint replySeq = r.ReadUInt32();
                        if (Net.IsHost) break;
                        _hostStep = step;
                        _hostMet = met;
                        _hostStepEpoch = epoch;
                        // A client in its own pre-join world, or during a load (the runner is about to be
                        // replaced/resumed): keep it, applied once the world is ready and settled.
                        if (!Net.WorldLive) break;
                        if (ClientMaySend()) FlushLocalStep(CurrentStepId());
                        if (!StepReplies.OnState(step, replyTo == Net.LocalId ? replySeq : 0u, Time.unscaledTime))
                        {
                            // On our own path, older than the host's answer to our newest request (we are ahead).
                            _hostEpoch = epoch;
                            break;
                        }
                        bool reenter = false;
                        if (reset && step == CurrentStepId() && _hostEpoch != int.MinValue && epoch != _hostEpoch)
                        {
                            // The host re-entered the same step (fail -> reset). If this PC already reset
                            // on its own, do not reset again.
                            if (_localReentries > 0) _localReentries--;
                            else reenter = true;
                        }
                        _hostEpoch = epoch;
                        ApplyStep(step, met, reenter);
                        break;
                    }
                case Kind.MissionState:
                    {
                        int hash = r.ReadInt32();
                        ulong replyTo = r.ReadUInt64();
                        uint replySeq = r.ReadUInt32();
                        if (Net.IsHost) break;
                        _hostMission = hash;
                        if (!Net.WorldLive) break;
                        if (ClientMaySend()) FlushLocalMission(CurrentMissionHash());
                        if (MissionReplies.OnState(hash, replyTo == Net.LocalId ? replySeq : 0u, Time.unscaledTime)) ApplyMission(hash);
                        break;
                    }
                case Kind.StepRequest:
                    {
                        string from = r.ReadString(), to = r.ReadString();
                        ulong origin = r.ReadUInt64();
                        uint seq = r.ReadUInt32();
                        if (!Net.IsHost || !Net.WorldLive) break; // relayed copy / host loading (it re-announces after)
                        string current = CurrentStepId();
                        // Also accepted when this host is already between from and to on the same path (its
                        // runner advanced on its own through shared conditions while the request travelled):
                        // refusing would pull the client back over steps it already finished.
                        bool between = current != null && current != Done && current != from && current != to
                                       && IsForward(from, current) && IsForward(current, to);
                        if (between) from = current;
                        if (current == from && to != from)
                        {
                            if (IsForward(from, to))
                            {
                                ApplyStep(to, null);
                                Plugin.Log.LogInfo($"Tutorial sync: client completed {from}, advancing to {to}");
                            }
                            else Plugin.Log.LogWarning($"Tutorial sync: rejected client move {from} -> {to} (not a forward step)");
                        }
                        // Accepted or not, announce where the host is now: it confirms the advance,
                        // or pulls a client that drifted back to the host's step.
                        string now = CurrentStepId();
                        if (now != null) SendStepState(now, origin, seq);
                        break;
                    }
                case Kind.MissionRequest:
                    {
                        int from = r.ReadInt32(), to = r.ReadInt32();
                        ulong origin = r.ReadUInt64();
                        uint seq = r.ReadUInt32();
                        if (!Net.IsHost || !Net.WorldLive) break;
                        if (CurrentMissionHash() == from && to != from) ApplyMission(to);
                        int now = CurrentMissionHash();
                        if (now != 0) SendMissionState(now, origin, seq);
                        break;
                    }
                case Kind.CondMet:
                    {
                        string step = r.ReadString();
                        List<string> paths = ReadPaths(r);
                        if (!Net.WorldLive || step != CurrentStepId()) break; // stale or other step
                        int n = MarkMet(paths, true);
                        if (n > 0) Plugin.Log.LogInfo($"Tutorial sync: {n} condition(s) of {step} met by another player");
                        break;
                    }
            }
        }

        // ---------- new graph tutorial ----------

        private static string CurrentStepId()
        {
            if (!Singleton<TutorialRunner>.IsInstanced()) return null;
            var runner = Singleton<TutorialRunner>.Instance;
            if (CompletedField != null && (bool)CompletedField.GetValue(runner)) return Done;
            return runner.CurrentStep != null ? runner.CurrentStep.StepId : null;
        }

        private static TutorialStep FindStep(TutorialGraph graph, string stepId)
        {
            if (graph == null || graph.Steps == null) return null;
            foreach (var s in graph.Steps)
                if (s != null && s.StepId == stepId) return s;
            return null;
        }

        private static void ApplyStep(string stepId, List<string> met, bool reenter = false)
        {
            if (!Singleton<TutorialRunner>.IsInstanced()) return;
            var runner = Singleton<TutorialRunner>.Instance;
            if (stepId != CurrentStepId() || (reenter && stepId != Done))
            {
                try
                {
                    if (stepId == Done)
                    {
                        // Same as the game's last SUCCESS transition: exit the step, _completed = true,
                        // SaveProgress(graph, null). The last step's exit actions hide its PDA/markers;
                        // hide everything anyway in case this PC was on another step.
                        TransitionTo.Invoke(runner, new object[] { null });
                        MarkerService.Instance?.HideAll();
                        if (Singleton<PDAManager>.IsInstanced()) Singleton<PDAManager>.Instance.HideMainQuest();
                        Plugin.Log.LogInfo("Tutorial sync: tutorial completed");
                    }
                    else
                    {
                        var step = FindStep(runner.Graph, stepId);
                        if (step == null)
                        {
                            Plugin.Log.LogWarning("Tutorial sync: unknown step " + stepId);
                            return;
                        }
                        // Jumping back into a tutorial that this PC had finished (host reloaded an
                        // older save): clear the flag or CurrentStepId would keep reporting Done.
                        CompletedField?.SetValue(runner, false);
                        // EnterStep(step, pushHistory, forceReloadPDA): exits the current step (its
                        // markers and PDA), runs the enter actions, resets the conditions and calls
                        // SaveProgress. forceReloadPDA rebuilds the PDA panel even if the title is the
                        // same, which matters when several steps were skipped at once.
                        var args = new object[EnterStep.GetParameters().Length];
                        args[0] = step;
                        if (args.Length > 1) args[1] = false;
                        if (args.Length > 2) args[2] = true;
                        _applyingStep = true;
                        try { EnterStep.Invoke(runner, args); }
                        finally { _applyingStep = false; }
                        Plugin.Log.LogInfo((reenter ? "Tutorial sync: step reset by host " : "Tutorial sync: now at step ") + stepId);
                    }
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError("Tutorial sync failed: " + ex);
                }
            }
            _lastStep = CurrentStepId(); // applied, not a local change: do not send it back
            if (met != null && met.Count > 0 && _lastStep == stepId) MarkMet(met, false);
        }

        /// <summary>
        /// True if 'to' can follow 'from' in the graph within a few transitions (any link type, branch
        /// targets, or the end of the tutorial). Stops a client whose runner restarted from an old save
        /// (or from the first step) from dragging the host backwards.
        /// </summary>
        private static bool IsForward(string from, string to)
        {
            if (!Singleton<TutorialRunner>.IsInstanced()) return false;
            TutorialGraph graph = Singleton<TutorialRunner>.Instance.Graph;
            TutorialStep start = FindStep(graph, from);
            if (start == null) return false;

            var visited = new HashSet<TutorialStep> { start };
            var frontier = new List<TutorialStep> { start };
            var next = new List<TutorialStep>();
            for (int hop = 0; hop < MaxForwardHops && frontier.Count > 0; hop++)
            {
                next.Clear();
                foreach (TutorialStep s in frontier)
                {
                    bool hasSuccess = false;
                    AddLinkTargets(graph.Links, s, next, ref hasSuccess);
                    AddLinkTargets(graph.ExtraLinks, s, next, ref hasSuccess);
                    if (s is BranchingAnyOfSteps br && br.Branches != null)
                        foreach (var b in br.Branches)
                            if (b != null && b.NextOnComplete != null)
                            {
                                next.Add(b.NextOnComplete);
                                hasSuccess = true;
                            }
                    if (!hasSuccess && to == Done) return true; // TransitionTo(null) ends the tutorial
                }
                frontier = new List<TutorialStep>();
                foreach (TutorialStep s in next)
                {
                    if (s == null) continue;
                    if (s.StepId == to) return true;
                    if (visited.Add(s)) frontier.Add(s);
                }
            }
            return false;
        }

        private static void AddLinkTargets(List<TutorialLink> links, TutorialStep from, List<TutorialStep> into, ref bool hasSuccess)
        {
            if (links == null) return;
            foreach (TutorialLink l in links)
            {
                if (l == null || l.From != from) continue;
                if (l.TransitionType == TransitionType.Success && l.To != null) hasSuccess = true;
                if (l.To != null) into.Add(l.To);
            }
        }

        // ---------- shared condition progress ----------

        /// <summary>
        /// Leaf conditions of the current step with a stable path (same assets on every PC):
        /// "c.j" = step condition j, "k{i}.j" = child step i of a composite, "b{i}.j" = branch i of a
        /// branching step; nested Composite_Conditions add ".j" per level (their own SetMet is a no-op).
        /// Fail conditions use "f.j" / "kf{i}.j" / "bf{i}.j". The cache and the "already sent" set are
        /// rebuilt on every entry of a step (a re-entry resets the game's conditions too).
        /// </summary>
        private static void RefreshLeaves(TutorialRunner runner)
        {
            TutorialStep cur = runner.CurrentStep;
            if (cur == _leavesFor && _leavesSerial == _entrySerial) return;
            _leavesFor = cur;
            _leavesSerial = _entrySerial;
            Leaves.Clear();
            MetKnown.Clear();
            SentThisEntry.Clear();
            if (cur == null) return;
            AddLeaves(cur.Conditions, "c");
            AddLeaves(cur.FailConditions, "f");
            if (cur is CompositeAllOfStepSO comp && comp.Children != null)
                for (int i = 0; i < comp.Children.Count; i++)
                    if (comp.Children[i] != null)
                    {
                        AddLeaves(comp.Children[i].Conditions, "k" + i);
                        AddLeaves(comp.Children[i].FailConditions, "kf" + i);
                    }
            if (cur is BranchingAnyOfSteps br && br.Branches != null)
                for (int i = 0; i < br.Branches.Count; i++)
                    if (br.Branches[i]?.Step != null)
                    {
                        AddLeaves(br.Branches[i].Step.Conditions, "b" + i);
                        AddLeaves(br.Branches[i].Step.FailConditions, "bf" + i);
                    }
        }

        private static bool IsFailPath(string path) =>
            path.StartsWith("f.") || path.StartsWith("kf") || path.StartsWith("bf");

        private static void AddLeaves(List<TutorialConditionSO> conds, string prefix)
        {
            if (conds == null) return;
            for (int j = 0; j < conds.Count; j++)
            {
                TutorialConditionSO c = conds[j];
                if (c == null) continue;
                string path = prefix + "." + j;
                if (c is Composite_ConditionsConditionSO cc) AddLeaves(cc.Children, path);
                else Leaves.Add(new KeyValuePair<string, TutorialConditionSO>(path, c));
            }
        }

        /// <summary>Send conditions of the current step that this PC newly met.</summary>
        private static void PollConditions(string step)
        {
            if (step == null || step == Done || SetMet == null || !Singleton<TutorialRunner>.IsInstanced()) return;
            RefreshLeaves(Singleton<TutorialRunner>.Instance);
            NewlyMet.Clear();
            foreach (var leaf in Leaves)
            {
                if (leaf.Value.IsMet)
                {
                    // Once per step entry: a live world-state condition that flickers is not re-sent.
                    if (MetKnown.Add(leaf.Key) && SentThisEntry.Add(leaf.Key))
                    {
                        // Fail conditions only matter to the host (it decides and announces the result).
                        if (!Net.IsHost || !IsFailPath(leaf.Key)) NewlyMet.Add(leaf.Key);
                    }
                }
                else MetKnown.Remove(leaf.Key);
            }
            if (NewlyMet.Count > 0) SendCondMet(step, NewlyMet);
        }

        /// <summary>Mark conditions of the current step met (from another player). Returns how many changed.</summary>
        private static int MarkMet(List<string> paths, bool fromRemote)
        {
            if (SetMet == null || !Singleton<TutorialRunner>.IsInstanced()) return 0;
            RefreshLeaves(Singleton<TutorialRunner>.Instance);
            int changed = 0;
            foreach (string path in paths)
            {
                if (IsFailPath(path))
                {
                    // Only the host applies a remote fail, and not right after this step was (re)entered:
                    // the same failure may already have reset it (no double reset).
                    if (!fromRemote || !Net.IsHost || Time.unscaledTime - _entryTime < FailGuard) continue;
                }
                foreach (var leaf in Leaves)
                {
                    if (leaf.Key != path) continue;
                    MetKnown.Add(path); // not a local discovery: never echo it back
                    SentThisEntry.Add(path);
                    if (!leaf.Value.IsMet)
                    {
                        try
                        {
                            SetMet.Invoke(leaf.Value, new object[] { true });
                            changed++;
                        }
                        catch (System.Exception ex)
                        {
                            Plugin.Log.LogError("Tutorial sync: SetMet failed: " + ex);
                        }
                    }
                    break;
                }
            }
            return changed;
        }

        // ---------- legacy prologue mission ----------

        private static Animator MissionAnimator()
        {
            if (!Singleton<MissionManager>.IsInstanced()) return null;
            var gr = Singleton<MissionManager>.Instance.MyMission as MissionGR;
            return gr != null ? gr.MyMissionAnimator : null;
        }

        private static int CurrentMissionHash()
        {
            Animator a = MissionAnimator();
            if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return 0;
            return a.GetCurrentAnimatorStateInfo(0).shortNameHash;
        }

        private static void ApplyMission(int hash)
        {
            Animator a = MissionAnimator();
            if (a == null || hash == 0 || hash == CurrentMissionHash()) return;
            if (hash == IdleHash && StopTutorial != null)
            {
                // The prologue ended/stopped on the host: like MissionGR.Deserialize for this state,
                // StopTutorial plays Idle, clears the marker and sets IsTutorialRunning = false.
                try
                {
                    StopTutorial.Invoke(Singleton<MissionManager>.Instance.MyMission, null);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError("Tutorial sync: StopTutorial failed: " + ex);
                    a.Play(hash);
                }
            }
            else a.Play(hash);
            _lastMission = hash; // do not send it back
            Plugin.Log.LogInfo("Tutorial sync: mission stage " + hash);
        }

        /// <summary>Counts step entries (re-entries of the same step included) and own resets on clients.</summary>
        [HarmonyPatch]
        private static class PatchEnterStep
        {
            private static MethodBase TargetMethod() => EnterStep;
            private static bool Prepare() => EnterStep != null;

            private static void Prefix(out string __state) => __state = CurrentStepId();

            private static void Postfix(string __state)
            {
                _entrySerial++;
                _entryTime = Time.unscaledTime;
                if (Net.Active && !Net.IsHost && !_applyingStep && __state != null && __state == CurrentStepId())
                    _localReentries++; // this client reset the step itself (its own fail condition)
            }
        }
    }
}
