// WIRING (in place): Net.OnPeerJoined -> SaveTransfer.OnPeerJoinedHost(id); Net.OnPeerLeft ->
// SaveTransfer.OnPeerLeft(id); Net.Shutdown -> SaveTransfer.OnSessionEnded(wasHost); Net.Handle routes
// (Msg)8 / (Msg)9 here (point-to-point, never relayed) and drops a client's world messages on the host
// until SaveTransfer.PeerReady(peer); Plugin.Update calls SaveTransfer.Tick() right after Net.Poll().

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GoldDigger;
using HarmonyLib;
using SaveSystem;
using UnityEngine;
using UnityEngine.SceneManagement;
using GameSaveManager = SaveSystem.SaveManager;

namespace GMSCoop
{
    /// <summary>
    /// "Join = receive the host's world".
    ///
    /// Host: when a new peer says Hello, the host saves its game to its current slot with the game's
    /// own CheckPointManagerBase.QuickSave (same call as the pause menu "Save game"), waits for
    /// SaveManager.CheckIfBusy() to clear, packs every file of the slot folder into a tiny container
    /// and streams it to that peer in reliable chunks of 64 KB, never more than 512 KB queued in the
    /// transport (flow control: the link's own speed paces it).
    ///
    /// Slow / lossy links: the transfer never restarts. The client keeps every chunk it got; if nothing
    /// arrives for a while (4x the usual gap between chunks, at least 8 s) it asks for exactly the
    /// chunks it lacks, and the host resends only those, and only once everything it queued has left
    /// (a slow link is not a lost chunk). Progress and speed are shown on both sides, time left on the client.
    ///
    /// Client: rebuilds the files into the slot folder "Coop_Host" under SaveManager.GetSavePath(),
    /// then loads it exactly like the load menu does (MainMenuSave.SaveLoadToOldSlot):
    /// SaveManager.SetCurrentSlot("Coop_Host") + CheckpointManager.RequestLoad(). This works from the
    /// main menu and from inside a world (it reloads). After the load, MachineSync is re-indexed.
    ///
    /// Session lifecycle:
    ///  - A peer counts as "ready" on the host only after it reported Loaded for its current transfer;
    ///    until then Net drops its world messages (they would name objects of another world).
    ///  - Catch-up: on Loaded the host sends CatchUp, then replays what happened since that peer's save
    ///    (terrain journal, purchase journal, switch / paint journal (WorldSync), world journal of vehicle /
    ///    bed / passenger / worker events) and sends full states (tutorial, money and containers, world flags, objects). The
    ///    client drops live world messages between its Loaded and CatchUp: they are all in that catch-up.
    ///  - Host loads another world (QuickLoad, load menu, new game, back to the main menu...): every
    ///    client is told at once (Preparing: stop syncing, a new world follows) and gets the new world
    ///    as soon as the host is in it again.
    ///  - Client leaves the shared world itself (quits to the main menu, loads one of its own slots):
    ///    that ends its session. A QuickLoad / reload of the shared world is refused (it would load a
    ///    stale copy).
    ///  - Session over on a client (F7, host gone, world rejected): the client gets its own game back:
    ///    the slot it played before joining is loaded again (or the main menu when it joined from
    ///    there), and its current slot name is restored ("Continue" never loads Coop_Host).
    ///
    /// Safety: while the client plays the host's world, every save the game makes is redirected to
    /// "Coop_Host" (Harmony prefix on CheckpointManager.QuickSave), so the host's world never lands in
    /// one of the client's own slots; the client's autosave is skipped (the host's world is sent again on
    /// every join). This matters too when both instances run on the same PC and share the Saves folder.
    ///
    /// Wire format:
    ///   (Msg)8 chunk:   int transferId, int index, int count, int totalLength, int offset, int len, bytes
    ///   (Msg)9 control: byte kind, then per kind (see Ctl); Resend: int transferId, int ranges,
    ///                   ranges x (int first, int count) of missing chunks; CatchUp: nothing
    /// Container (after reassembly): "GMSW", int version, string hostSlot, int files, {string path, int len, bytes}*
    /// </summary>
    internal static class SaveTransfer
    {
        internal const string CoopSlot = "Coop_Host";

        private const Msg MsgChunk = (Msg)8;
        private const Msg MsgControl = (Msg)9;
        private const int ChunkSize = 64 * 1024;      // payload per message: fine-grained progress / resends
        private const int ChunksPerTick = 8;          // at most ~512 KB per frame per peer (queue limit first)
        private const int MaxResendRanges = 64;
        private const int MaxWorldBytes = 512 * 1024 * 1024;
        private const string Magic = "GMSW";
        private const int Version = 1;
        private const string MainMenuScene = "Main_Menu_Scene";

        private enum Ctl : byte
        {
            Preparing = 1, // host -> client: a (new) world follows; stop syncing the current one
            Loaded = 2,    // client -> host: int transferId; world received and loaded
            Resend = 3,    // client -> host: int transferId, int ranges, ranges x (int first, int count)
            Failed = 4,    // host -> client: string reason
            CatchUp = 5,   // host -> client: right after its Loaded; the catch-up (journal replays, full states) follows
        }

        // =====================================================================
        // Host side
        // =====================================================================

        private enum HostPhase { Idle, Saving }

        private sealed class Outgoing
        {
            public ulong Peer;
            public int Id;
            public byte[] Data;
            public int Count;
            public int Next;
            public float DoneAt; // > 0 once every chunk went out; kept a while for resend requests
            public int LastPercent = -1;
            public float StartedAt;
            public readonly Queue<int> Redo = new Queue<int>();     // chunks the client asked for again
            public readonly HashSet<int> RedoSet = new HashSet<int>();
        }

        private static readonly List<ulong> PendingPeers = new List<ulong>();
        private static readonly List<Outgoing> Sending = new List<Outgoing>();
        // Peer -> transfer id it was sent and has not reported Loaded for yet.
        private static readonly Dictionary<ulong, int> Awaiting = new Dictionary<ulong, int>();
        // Peers that loaded the host's current world (their world messages are accepted).
        private static readonly HashSet<ulong> ReadyPeers = new HashSet<ulong>();
        private static readonly List<ulong> _tmpPeers = new List<ulong>();
        private static HostPhase _hostPhase;
        private static float _saveStarted;
        private static float _nextWaitStatus;
        private static int _nextTransferId = 1;
        private static string _savingSlot;  // slot the transfer save was written to
        private static bool _ownSave;       // inside our own QuickSave call

        // Host world tracking: away = not in a playable world (loading, main menu).
        private static bool _hostAway;
        private static int _hostSceneHandle;
        private static float _hostInWorldSince;
        private static float _nextJournalClear;
        private const float HostSettle = 3f; // after a load, objects destroyed on load must be gone first

        // ---- world journal (host) ----
        // Reliable world events that no system catches up on its own (vehicle exits / hitches / teleports /
        // final machine state, bed cargo, passengers, workers leaving) and that happen between the save a
        // joiner receives and its Loaded. That joiner drops them while it receives and loads the world, so
        // they are replayed to it, in order, right after its Loaded. Terrain, purchases and switches / paint
        // keep their own journals (TerrainSync / PurchaseSync / WorldSync); objects, money, containers, world
        // flags, forge, fuel and the tutorial send their full current state instead.
        private static readonly List<byte[]> WorldJournal = new List<byte[]>();
        private static readonly Dictionary<ulong, int> WorldJournalStart = new Dictionary<ulong, int>(); // peer -> absolute index
        private static int _worldJournalBase;   // absolute index of WorldJournal[0]
        private static int _worldJournalMark;   // absolute index when the current transfer save was taken
        private static long _worldJournalBytes;
        private const long WorldJournalMaxBytes = 16L * 1024 * 1024;
        private static bool _worldJournalFull;

        /// <summary>Host: true once <paramref name="peer"/> plays the host's current world.</summary>
        internal static bool PeerReady(ulong peer) => ReadyPeers.Contains(peer);

        /// <summary>A long silence from <paramref name="peer"/> is expected (a world is being saved / sent / loaded).</summary>
        internal static bool InTransit(ulong peer) =>
            Net.IsHost ? PendingPeers.Contains(peer) || Awaiting.ContainsKey(peer) || _hostAway
                       : _clientPhase != ClientPhase.Idle;

        /// <summary>Host: a new peer joined. Save the world and send it to that peer.</summary>
        internal static void OnPeerJoinedHost(ulong peer)
        {
            if (!Net.Active || !Net.IsHost) return;
            if (!PendingPeers.Contains(peer)) PendingPeers.Add(peer);
            Sending.RemoveAll(o => o.Peer == peer); // a rejoin restarts from scratch
            Awaiting.Remove(peer);
            ReadyPeers.Remove(peer);
            SendControl(peer, Ctl.Preparing, null);
        }

        /// <summary>Host: a peer left (or restarts its session).</summary>
        internal static void OnPeerLeft(ulong peer)
        {
            PendingPeers.Remove(peer);
            Sending.RemoveAll(o => o.Peer == peer);
            Awaiting.Remove(peer);
            ReadyPeers.Remove(peer);
            WorldJournalStart.Remove(peer);
        }

        /// <summary>
        /// Host, from Net.Broadcast (own and relayed reliable messages): records the world events listed
        /// above while a joiner is between its save and its Loaded.
        /// </summary>
        internal static void JournalWorld(byte[] data)
        {
            if (data == null || data.Length < 2 || (_hostPhase != HostPhase.Saving && WorldJournalStart.Count == 0)) return;
            if (!Journaled(data)) return;
            if (_worldJournalBytes + data.Length > WorldJournalMaxBytes)
            {
                if (!_worldJournalFull)
                {
                    _worldJournalFull = true;
                    Plugin.Log.LogWarning("Save transfer: world journal full (16 MB), later vehicle / switch events are not replayed to the joining player");
                }
                return;
            }
            var copy = new byte[data.Length]; // callers may reuse their buffers
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            WorldJournal.Add(copy);
            _worldJournalBytes += copy.Length;
        }

        /// <summary>Message kinds the world journal keeps (see WorldJournal).</summary>
        private static bool Journaled(byte[] d)
        {
            switch ((Msg)d[0])
            {
                case Msg.MachinePose:
                    // int id / sub-kind first. Poses (id >= 0) reach here only reliably: the exit pose,
                    // which a late joiner adopts as the parked pose. Not the id map (-5): joiner-only, sent before.
                    return d.Length >= 5 && BitConverter.ToInt32(d, 1) != -5;
                case Msg.MachineParts:
                case Msg.Bed:
                    return true;
                case Msg.Objects:
                    return d[1] == ObjectSync.SubPassenger; // the rest: ObjectSync's own key-sync dump
                case Msg.Economy:
                    // Switches / paint (subs 20 / 22): WorldSync's own journal, with its end marker and dedupe.
                    return d[1] == GoldSync.SubWorkerGone;
                default:
                    return false;
            }
        }

        /// <summary>Host: sends <paramref name="peer"/> the journaled world events since its save.</summary>
        private static void ReplayWorldJournal(ulong peer)
        {
            if (!WorldJournalStart.TryGetValue(peer, out int start)) return;
            WorldJournalStart.Remove(peer);
            int from = Math.Max(0, start - _worldJournalBase), n = 0;
            for (int i = from; i < WorldJournal.Count; i++)
            {
                Net.Transport.Send(peer, WorldJournal[i], true);
                n++;
            }
            if (n > 0) Plugin.Log.LogInfo($"Save transfer: replayed {n} world events to {Net.NameOf(peer)}");
            TrimWorldJournal();
        }

        /// <summary>Drops the journal entries no waiting peer needs any more.</summary>
        private static void TrimWorldJournal()
        {
            if (_hostPhase == HostPhase.Saving) return;
            int keepFrom = int.MaxValue;
            foreach (int s0 in WorldJournalStart.Values) keepFrom = Math.Min(keepFrom, s0);
            int drop = keepFrom == int.MaxValue ? WorldJournal.Count : Math.Min(WorldJournal.Count, Math.Max(0, keepFrom - _worldJournalBase));
            if (drop <= 0) return;
            for (int i = 0; i < drop; i++) _worldJournalBytes -= WorldJournal[i].Length;
            WorldJournal.RemoveRange(0, drop);
            _worldJournalBase += drop;
            if (WorldJournal.Count == 0) { _worldJournalBytes = 0; _worldJournalFull = false; }
        }

        private static void ClearWorldJournal()
        {
            _worldJournalBase += WorldJournal.Count;
            WorldJournal.Clear();
            WorldJournalStart.Clear();
            _worldJournalBytes = 0;
            _worldJournalFull = false;
        }

        private static void HostTick()
        {
            float now = Time.unscaledTime;
            // Forget peers that left.
            PendingPeers.RemoveAll(p => !Net.Peers.Contains(p));
            Sending.RemoveAll(o => !Net.Peers.Contains(o.Peer) || (o.DoneAt > 0f && now - o.DoneAt > 120f));
            if (Awaiting.Count + ReadyPeers.Count > Net.Peers.Count)
            {
                _tmpPeers.Clear();
                foreach (ulong p in Awaiting.Keys) if (!Net.Peers.Contains(p)) _tmpPeers.Add(p);
                foreach (ulong p in ReadyPeers) if (!Net.Peers.Contains(p)) _tmpPeers.Add(p);
                foreach (ulong p in _tmpPeers) OnPeerLeft(p);
            }

            TrackHostWorld(now);

            if (_hostPhase == HostPhase.Idle && PendingPeers.Count > 0) TryStartSave(now);
            else if (_hostPhase == HostPhase.Saving) CheckSaveFinished();

            PumpChunks();
            if (WorldJournal.Count > 0 && WorldJournalStart.Count == 0) TrimWorldJournal();

            // Nobody between a save and its Loaded: the terrain journal is not needed; keep it empty so a
            // long session does not pile up every dig (it is only replayed to joiners).
            if (_hostPhase == HostPhase.Idle && PendingPeers.Count == 0 && Awaiting.Count == 0 && !_hostAway &&
                now >= _nextJournalClear)
            {
                _nextJournalClear = now + 30f;
                TerrainSync.ClearJournal();
            }
        }

        /// <summary>
        /// Host: notices when its own world goes away (a load, the main menu) or is replaced. Clients are
        /// told at once (they stop syncing) and every client gets the new world once the host is in it.
        /// </summary>
        private static void TrackHostWorld(float now)
        {
            Scene scene = SceneManager.GetActiveScene();
            CheckpointManager cpm = scene.buildIndex != 0 ? Checkpoints() : null;
            bool inWorld = cpm != null && cpm.GetLoadingState() == LoadingState.ReadyToPlay &&
                           global::Singleton<Player>.IsInstanced();
            if (!inWorld)
            {
                if (!_hostAway)
                {
                    _hostAway = true;
                    if (_hostSceneHandle != 0) RestartAllPeers("el host está cargando otra partida");
                }
                return;
            }
            if (_hostAway)
            {
                _hostAway = false;
                _hostInWorldSince = now;
            }
            if (_hostSceneHandle != 0 && scene.handle != _hostSceneHandle)
            {
                // Replaced without a frame we saw as "away" (scene reload): same treatment.
                RestartAllPeers("el host cambió de partida");
                _hostInWorldSince = now;
            }
            _hostSceneHandle = scene.handle;
        }

        /// <summary>Host: the world every client plays is gone. Each gets the new one once the host is in it.</summary>
        private static void RestartAllPeers(string why)
        {
            _hostPhase = HostPhase.Idle; // a save in progress belongs to the old world
            Sending.Clear();
            Awaiting.Clear();
            ReadyPeers.Clear();
            ClearWorldJournal(); // events of the old world
            if (Net.Peers.Count == 0) return;
            Plugin.Instance.SetStatus("Cambiaste de partida: se enviará a los jugadores cuando estés en ella");
            Plugin.Log.LogInfo("Save transfer: host world changed (" + why + "), every client gets the new world");
            foreach (ulong p in Net.Peers) OnPeerJoinedHost(p);
        }

        /// <summary>Starts the game's own save once the host is in a world and the save system is idle.</summary>
        private static void TryStartSave(float now)
        {
            CheckpointManager cpm = Checkpoints();
            bool inWorld = !_hostAway && cpm != null && now - _hostInWorldSince >= HostSettle;
            if (!inWorld || SaveBusy())
            {
                if (now > _nextWaitStatus)
                {
                    _nextWaitStatus = now + 10f;
                    Plugin.Instance.SetStatus("Un jugador espera tu mundo: entra en una partida para enviárselo...");
                }
                return;
            }

            // A host playing a Coop_Host world (loaded by hand) must not save into the folder a client
            // on the same PC writes the received world to.
            if (GameSaveManager.GetCurrentSlotName() == CoopSlot) GameSaveManager.SetCurrentSlot(CoopSlot + "_Hosted");
            _savingSlot = GameSaveManager.GetCurrentSlotName();
            Plugin.Instance.SetStatus("Guardando la partida (" + _savingSlot + ") para enviarla...");
            _ownSave = true;
            try
            {
                // Same call the pause menu uses (MenuManager.SaveGame). Saves into the current slot.
                cpm.QuickSave();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Save transfer: QuickSave failed: " + ex);
            }
            finally { _ownSave = false; }
            // The save captured the machines where they are right now; re-index them now too, so the
            // host's machine ids match the ones the client computes from this save.
            MachineSync.ResetIndex();
            ObjectSync.ResetIndex();
            // Digs from now on are not in this save: journal them for replay after the client loads.
            // A peer that is still between its (older) save and Loaded needs the journal since ITS save:
            // keep it. Terrain edits are absolute writes in host order (drill points are idempotent), so
            // replaying a longer journal onto this newer save ends in the same terrain.
            if (Awaiting.Count == 0) TerrainSync.ClearJournal();
            PurchaseSync.ClearJournal();
            WorldSync.ClearJournal();
            TrimWorldJournal();
            _worldJournalMark = _worldJournalBase + WorldJournal.Count;
            _hostPhase = HostPhase.Saving;
            _saveStarted = now;
        }

        private static void CheckSaveFinished()
        {
            float elapsed = Time.unscaledTime - _saveStarted;
            // The main file is written on a worker task (SaveManager busy); the screenshot and
            // SmallDescription follow one frame later from a coroutine. 1.5 s covers both.
            bool busy = SaveBusy();
            if ((busy || elapsed < 1.5f) && elapsed < 60f) return;
            if (busy) Plugin.Log.LogWarning("Save transfer: save still busy after 60 s, sending what is on disk");

            _hostPhase = HostPhase.Idle;
            byte[] world;
            try
            {
                world = PackSlot(_savingSlot); // not the current slot: an autosave may have switched it
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Save transfer: packing failed: " + ex);
                foreach (ulong p in PendingPeers) SendControl(p, Ctl.Failed, "el host no pudo leer su partida");
                PendingPeers.Clear();
                TrimWorldJournal();
                Plugin.Instance.SetStatus("No se pudo leer la partida para enviarla (ver el log)");
                return;
            }

            int count = Math.Max(1, (world.Length + ChunkSize - 1) / ChunkSize);
            foreach (ulong p in PendingPeers)
            {
                // Tie this peer to THIS save's object snapshot and purchase journal start, so two
                // overlapping joins each get the catch-up that matches the save they received.
                ObjectSync.AssignSnapshot(p);
                PurchaseSync.AssignJournal(p);
                WorldSync.AssignJournal(p);
                WorldJournalStart[p] = _worldJournalMark;
                int id = _nextTransferId++;
                Sending.Add(new Outgoing { Peer = p, Id = id, Data = world, Count = count, StartedAt = Time.unscaledTime });
                Awaiting[p] = id;
                Plugin.Log.LogInfo($"Save transfer: sending {world.Length / 1024} KB ({count} chunks) to {Net.NameOf(p)}");
            }
            PendingPeers.Clear();
        }

        // Keep at most this much of the world queued in the transport per peer, so game traffic
        // (poses, digs) is not stuck behind megabytes of save data.
        private const int MaxQueuedBytes = 512 * 1024;

        /// <summary>
        /// Sends chunks only while the peer's send queue is short (flow control): first the chunks the
        /// client asked for again, then the next new ones.
        /// </summary>
        private static void PumpChunks()
        {
            foreach (Outgoing o in Sending)
            {
                if (o.DoneAt > 0f) continue;
                for (int i = 0; i < ChunksPerTick && (o.Redo.Count > 0 || o.Next < o.Count); i++)
                {
                    if (Net.Transport.QueuedBytes(o.Peer) > MaxQueuedBytes) break;
                    bool redo = o.Redo.Count > 0;
                    int index = redo ? o.Redo.Peek() : o.Next;
                    if (!SendChunk(o, index)) break; // queue full: retry next frame
                    if (redo) o.RedoSet.Remove(o.Redo.Dequeue());
                    else o.Next++;
                }

                int percent = o.Next * 100 / o.Count;
                bool done = o.Next >= o.Count && o.Redo.Count == 0;
                if (percent / 10 != o.LastPercent / 10 || (done && o.LastPercent != 100))
                {
                    o.LastPercent = percent;
                    float secs = Mathf.Max(0.1f, Time.unscaledTime - o.StartedAt);
                    float rate = Math.Max(0, o.Next * ChunkSize - Net.Transport.QueuedBytes(o.Peer)) / secs; // bytes/s out on the wire
                    Plugin.Instance.SetStatus($"Enviando el mundo a {Net.NameOf(o.Peer)}... {percent}% ({rate / 1024f:0} KB/s)");
                }
                if (done) o.DoneAt = Time.unscaledTime;
            }
        }

        private static bool SendChunk(Outgoing o, int index)
        {
            int offset = index * ChunkSize;
            int len = Math.Min(ChunkSize, o.Data.Length - offset);
            var ms = Net.Writer(MsgChunk);
            var w = new BinaryWriter(ms);
            w.Write(o.Id);
            w.Write(index);
            w.Write(o.Count);
            w.Write(o.Data.Length);
            w.Write(offset);
            w.Write(len);
            w.Write(o.Data, offset, len);
            return Net.Transport.Send(o.Peer, ms.ToArray(), true);
        }

        /// <summary>
        /// Packs every file of a slot folder. Skips the game's .tmp/.backup leftovers.
        /// With Steam cloud saves (SaveManager.UseCloud, unused by this build) the files live in
        /// Steam remote storage, so the known slot files are read through the game's SteamSaveManager.
        /// </summary>
        private static byte[] PackSlot(string slot)
        {
            var files = new List<KeyValuePair<string, byte[]>>();
            string dir = GameSaveManager.GetSlotSavePath(slot);
            if (Directory.Exists(dir))
            {
                string root = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".backup", StringComparison.OrdinalIgnoreCase)) continue;
                    string rel = Path.GetFullPath(f).Substring(root.Length).Replace('\\', '/');
                    files.Add(new KeyValuePair<string, byte[]>(rel, File.ReadAllBytes(f)));
                }
            }

            if (GameSaveManager.UseCloud && !files.Exists(kv => kv.Key == "CheckpointData.sav"))
            {
                foreach (string name in new[] { "CheckpointData.sav", "SmallDescription.txt", "Screenshot.jpg" })
                {
                    byte[] data = GameSaveManager.MySteamSaveManager.Load(slot + "/" + name, out var err);
                    if (data != null && err == GameSaveManager.ERRORS.OK) files.Add(new KeyValuePair<string, byte[]>(name, data));
                }
            }

            if (!files.Exists(kv => kv.Key == "CheckpointData.sav"))
                throw new FileNotFoundException("CheckpointData.sav not found in slot " + slot + " (" + dir + ")");

            var ms = new MemoryStream();
            var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(Encoding.ASCII.GetBytes(Magic));
            w.Write(Version);
            w.Write(slot ?? "");
            w.Write(files.Count);
            foreach (var kv in files)
            {
                w.Write(kv.Key);
                w.Write(kv.Value.Length);
                w.Write(kv.Value);
            }
            return ms.ToArray();
        }

        // =====================================================================
        // Client side
        // =====================================================================

        // Waiting: the host announced a (new) world; nothing of it arrived yet.
        private enum ClientPhase { Idle, Waiting, Receiving, WaitToLoad, Loading, Settling }

        private static ClientPhase _clientPhase;
        private static int _rxId;
        private static byte[] _rxBuf;
        private static bool[] _rxGot;
        private static int _rxReceived;
        private static float _rxLastChunk;
        private static float _rxInterval;   // smoothed time between two chunks (stall detection)
        private static float _rxStarted;
        private static long _rxBytes;
        private static float _nextProgress;
        private static int _rxLoggedQuarter = -1;
        private static byte[] _rxWorld;
        private static float _phaseStarted;
        private static bool _sawLoading;
        private static int _loadedId; // transfer id of the world being loaded (reported with Loaded)

        // Client: Loaded sent, the host's CatchUp marker not received yet (see HoldWorldMessages).
        private static bool _holdWorld;
        private static float _holdSince;
        private const float HoldTimeout = 30f;

        // Our own RequestLoad (host world, or the client's own slot afterwards): not a player's load.
        private static bool _ownLoad;
        // Result of our RequestLoad, from the game's own checks (null = accepted / not known yet).
        private static bool _myLoadPending;
        private static string _rejectReason;
        private static bool _rejectToMenu;  // the game itself goes back to the main menu after this failure
        private static bool _prevCoop;      // a host world was loaded before the current load started

        // The client's own game, to give back when the session ends.
        private static string _ownSlot;
        private static bool _ownInWorld;
        private static bool _restore;
        private static float _restoreSince;
        private static float _restoreLoadAt = -1f;
        private static int _coopSceneHandle;

        /// <summary>True while this client plays (or is loading) the world it received from the host.</summary>
        private static bool _hostWorldLoaded;

        internal static bool ClientSession => Net.Active && !Net.IsHost && _hostWorldLoaded;

        /// <summary>
        /// True when this PC plays the shared world: on the host while it is in a world (not loading,
        /// not in the main menu); on a client only once the host's world has been received and loaded.
        /// World-state sync (poses, objects, parts) must neither send nor apply before that, or the
        /// client's own menu/tutorial world leaks in.
        /// </summary>
        internal static bool WorldReady => Net.IsHost ? !_hostAway : _sessionWorld && _hostWorldLoaded && _clientPhase == ClientPhase.Idle;

        /// <summary>
        /// Client, between its Loaded and the host's CatchUp marker: Net drops incoming world messages.
        /// The host sent those before it knew we play its world; they are in its catch-up (journals,
        /// full states) that follows the marker, so applying them live too would double relative events.
        /// The systems are already indexed and may send meanwhile (the host accepts after our Loaded).
        /// </summary>
        internal static bool HoldWorldMessages => _holdWorld;

        /// <summary>Host: always. Client: the host's catch-up for the loaded world has started arriving.</summary>
        internal static bool CaughtUp => Net.IsHost || (WorldReady && !_holdWorld);

        // Client: the loaded world was received in THIS session (a Coop_Host world left over from an
        // earlier session is not the current host's world).
        private static bool _sessionWorld;

        internal static void Receive(ulong from, Msg type, BinaryReader r)
        {
            if (type == MsgChunk)
            {
                if (!Net.IsHost) ReceiveChunk(r);
                return;
            }
            if (type != MsgControl) return;

            var kind = (Ctl)r.ReadByte();
            if (Net.IsHost)
            {
                if (kind == Ctl.Loaded)
                {
                    int id = r.BaseStream.Position + 4 <= r.BaseStream.Length ? r.ReadInt32() : -1;
                    // Only the world it was sent last counts: a Loaded for an older transfer (restarted
                    // meanwhile) must not replay the journals of the newer save to it.
                    if (!Awaiting.TryGetValue(from, out int want) || want != id)
                    {
                        Plugin.Log.LogInfo($"Save transfer: ignoring Loaded {id} from {Net.NameOf(from)} (not its current transfer)");
                        return;
                    }
                    Awaiting.Remove(from);
                    ReadyPeers.Add(from);
                    Plugin.Instance.SetStatus(Net.NameOf(from) + " cargó el mundo");
                    // Everything below reaches the client after this marker, on the same ordered reliable
                    // channel; the client dropped the live world messages sent before it (Net.Handle).
                    SendControl(from, Ctl.CatchUp, null);
                    TerrainSync.ReplayJournal(from);  // digs made while the world was in transit
                    PurchaseSync.ReplayJournal(from); // purchases made while the world was in transit
                    WorldSync.ReplayJournal(from);    // switches / paint made while the world was in transit
                    ReplayWorldJournal(from);         // vehicle / bed / passenger / worker events
                    TutorialSync.SendFullState();     // bring the tutorial to the host's step
                    CashSync.SendCurrent();           // money, containers, world flags, forge, fuel
                    ObjectSync.SendCatchUp(from);     // every object's key and state (no need to wait for its request)
                }
                else if (kind == Ctl.Resend)
                {
                    int id = r.ReadInt32();
                    int ranges = r.ReadInt32();
                    Outgoing o = Sending.Find(x => x.Peer == from && x.Id == id);
                    if (o == null || ranges < 0 || ranges > MaxResendRanges) return;
                    // Still sending what it lacks (a slow link, not a lost chunk): resending now would only
                    // queue duplicates behind the originals.
                    if (Net.Transport.QueuedBytes(from) > ChunkSize)
                    {
                        Plugin.Log.LogInfo($"Save transfer: {Net.NameOf(from)} asked for chunks still on their way, not resending");
                        return;
                    }
                    int added = 0;
                    for (int i = 0; i < ranges; i++)
                    {
                        int first = r.ReadInt32(), n = r.ReadInt32();
                        if (first < 0 || n <= 0) continue;
                        // Only chunks that were already sent: the rest comes anyway.
                        int end = Math.Min(first + n, o.Next);
                        for (int c = first; c < end; c++)
                            if (o.RedoSet.Add(c)) { o.Redo.Enqueue(c); added++; }
                    }
                    if (added > 0)
                    {
                        Plugin.Log.LogWarning($"Save transfer: resending {added} missing chunks to {Net.NameOf(from)}");
                        o.DoneAt = 0f;
                    }
                }
                return;
            }

            // Client. Relayed client->host kinds (Loaded, Resend) can show up here; ignore them.
            if (kind == Ctl.CatchUp)
            {
                if (_holdWorld) Plugin.Log.LogInfo($"Save transfer: host catch-up started ({Time.unscaledTime - _holdSince:0.00} s after Loaded)");
                _holdWorld = false;
            }
            else if (kind == Ctl.Preparing)
            {
                _holdWorld = false;
                // A (new) world follows. Stop syncing the current one right away: it is the host's old
                // world (the host loaded another one) or this PC's own. The game keeps running meanwhile;
                // a load already under way finishes and is replaced by the new world.
                if (_clientPhase != ClientPhase.Waiting)
                {
                    _clientPhase = ClientPhase.Waiting;
                    _rxBuf = null;
                    _rxGot = null;
                    _rxWorld = null;
                    _phaseStarted = Time.unscaledTime;
                }
                Plugin.Instance.SetStatus(_hostWorldLoaded
                    ? "El host está cargando otra partida: espera, te la enviará..."
                    : "El host está guardando el mundo para enviártelo...");
            }
            else if (kind == Ctl.Failed)
            {
                string why = r.ReadString();
                Plugin.Instance.SetStatus("El host no pudo enviar el mundo: " + why + ". Desconectado.");
                EndClientSession("no recibió el mundo: " + why);
            }
        }

        private static void ReceiveChunk(BinaryReader r)
        {
            int id = r.ReadInt32();
            int index = r.ReadInt32();
            int count = r.ReadInt32();
            int total = r.ReadInt32();
            int offset = r.ReadInt32();
            int len = r.ReadInt32();
            byte[] data = r.ReadBytes(len); // copies: the transport reuses its receive buffer

            if (total <= 0 || total > MaxWorldBytes || count <= 0 || index < 0 || index >= count ||
                offset < 0 || len != data.Length || offset + len > total) return;

            // Host transfer ids only grow. Chunks of an older transfer (still queued when a rejoin restarted
            // it) or late resend duplicates of the transfer that already completed must not restart the
            // reception: that would throw the received world away, even in the middle of loading it.
            if (id < _rxId || (id == _rxId && _rxBuf == null)) return;
            if (id != _rxId || _rxBuf.Length != total || _rxGot.Length != count)
            {
                // A new transfer (also replaces one that was half way or already applied).
                _rxId = id;
                _rxBuf = new byte[total];
                _rxGot = new bool[count];
                _rxReceived = 0;
                _rxWorld = null;
                _rxLastChunk = Time.unscaledTime;
                _rxStarted = Time.unscaledTime;
                _rxInterval = 0f;
                _rxBytes = 0;
                _rxLoggedQuarter = -1;
                _clientPhase = ClientPhase.Receiving;
            }
            if (_clientPhase != ClientPhase.Receiving || _rxGot[index]) return;

            Buffer.BlockCopy(data, 0, _rxBuf, offset, len);
            _rxGot[index] = true;
            _rxReceived++;
            _rxBytes += len;
            float gap = Time.unscaledTime - _rxLastChunk;
            _rxInterval = _rxInterval <= 0f ? gap : _rxInterval * 0.8f + gap * 0.2f;
            _rxLastChunk = Time.unscaledTime;

            if (_rxReceived == count)
            {
                _rxWorld = _rxBuf;
                _rxBuf = null;
                _clientPhase = ClientPhase.WaitToLoad;
                _phaseStarted = Time.unscaledTime;
                Plugin.Instance.SetStatus("Mundo del host recibido. Cargando...");
            }
            else if (Time.unscaledTime > _nextProgress)
            {
                _nextProgress = Time.unscaledTime + 0.5f;
                float secs = Time.unscaledTime - _rxStarted;
                float rate = secs > 0.5f ? _rxBytes / secs : 0f;
                string speed = rate > 0f ? $" ({rate / 1024f:0} KB/s, ~{Mathf.CeilToInt((total - _rxBytes) / rate)} s)" : "";
                int pct = _rxReceived * 100 / count;
                // Shown twice a second, logged only at every quarter.
                bool log = pct / 25 != _rxLoggedQuarter;
                if (log) _rxLoggedQuarter = pct / 25;
                Plugin.Instance.SetStatus($"Recibiendo el mundo del host... {pct}%{speed}", log);
            }
        }

        private static void ClientTick()
        {
            switch (_clientPhase)
            {
                case ClientPhase.Receiving:
                    // Nothing new for much longer than the usual gap between chunks: ask for exactly the
                    // chunks still missing (the host ignores it while it is still sending them).
                    if (_rxGot != null && Time.unscaledTime - _rxLastChunk > Mathf.Max(8f, 4f * _rxInterval))
                    {
                        _rxLastChunk = Time.unscaledTime;
                        RequestMissing();
                    }
                    break;

                case ClientPhase.WaitToLoad:
                    TryStartLoad();
                    break;

                case ClientPhase.Loading:
                    WatchLoad();
                    break;

                case ClientPhase.Settling:
                    // Destroyed save objects disappear at the end of the frame and physics settles;
                    // wait a moment, then number the machines from scratch (same order as the host).
                    if (Time.unscaledTime - _phaseStarted > 1f)
                    {
                        if (!global::Singleton<Player>.IsInstanced() || SceneManager.GetActiveScene().buildIndex == 0)
                        {
                            LoadFailed("no se pudo cargar el mundo del host (ver el log)");
                            break;
                        }
                        MachineSync.ResetIndex();
                        ObjectSync.ResetIndex();
                        _clientPhase = ClientPhase.Idle;
                        _sessionWorld = true;
                        _coopSceneHandle = SceneManager.GetActiveScene().handle;
                        Plugin.Instance.SetStatus("Mundo del host cargado");
                        if (Net.Peers.Count > 0) SendControl(Net.Peers[0], Ctl.Loaded, null, _loadedId);
                        _holdWorld = true;
                        _holdSince = Time.unscaledTime;
                    }
                    break;

                case ClientPhase.Idle:
                    if (_holdWorld && Time.unscaledTime - _holdSince > HoldTimeout)
                    {
                        // The host never confirmed (it should within one round trip): play on with live data.
                        _holdWorld = false;
                        Plugin.Log.LogWarning("Save transfer: no catch-up from the host after Loaded, applying live data from now on");
                    }
                    break;
            }
        }

        /// <summary>Client: asks the host for the chunks it lacks (as ranges, at most MaxResendRanges).</summary>
        private static void RequestMissing()
        {
            if (Net.Peers.Count == 0) return;
            var ms = Net.Writer(MsgControl);
            var w = new BinaryWriter(ms);
            w.Write((byte)Ctl.Resend);
            w.Write(_rxId);
            long countPos = ms.Position;
            w.Write(0);
            int ranges = 0;
            for (int i = 0; i < _rxGot.Length && ranges < MaxResendRanges; i++)
            {
                if (_rxGot[i]) continue;
                int first = i;
                while (i + 1 < _rxGot.Length && !_rxGot[i + 1]) i++;
                w.Write(first);
                w.Write(i - first + 1);
                ranges++;
            }
            if (ranges == 0) return;
            w.Flush();
            byte[] data = ms.ToArray();
            Buffer.BlockCopy(BitConverter.GetBytes(ranges), 0, data, (int)countPos, 4);
            Plugin.Log.LogWarning($"Save transfer: no data for a while, asking the host for {ranges} missing range(s)");
            Net.Transport.Send(Net.Peers[0], data, true);
        }

        /// <summary>Writes the Coop_Host slot and starts the game's normal slot load once possible.</summary>
        private static void TryStartLoad()
        {
            CheckpointManager cpm = Checkpoints();
            if (cpm == null || cpm.GetLoadingState() != LoadingState.ReadyToPlay || SaveBusy()) return;

            try
            {
                // The game reads slots from local files only when UseCloud is off (LoadSaveData returns
                // nothing with cloud on). Force local files; this build never turns it on anyway.
                if (GameSaveManager.UseCloud)
                {
                    Plugin.Log.LogWarning("Save transfer: SaveManager.UseCloud was on, forcing local saves");
                    GameSaveManager.UseCloud = false;
                }
                WriteCoopSlot(_rxWorld);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Save transfer: could not write the Coop_Host slot: " + ex);
                Plugin.Instance.SetStatus("No se pudo guardar el mundo del host en tu PC (ver el log). Desconectado.");
                _clientPhase = ClientPhase.Idle;
                EndClientSession("no pudo guardar el mundo recibido");
                return;
            }
            _rxWorld = null;

            // Remember the player's own game (first host world of this session only), to give it back.
            if (!_hostWorldLoaded)
            {
                string cur = GameSaveManager.GetCurrentSlotName();
                if (cur != CoopSlot) _ownSlot = cur;
                _ownInWorld = cur != CoopSlot && SceneManager.GetActiveScene().buildIndex != 0 &&
                              global::Singleton<Player>.IsInstanced();
            }

            // Same steps as the load menu (MainMenuSave.SaveLoadToOldSlot).
            _prevCoop = _hostWorldLoaded;
            _hostWorldLoaded = true;
            _loadedId = _rxId;
            _rejectReason = null;
            _myLoadPending = true;
            GameSaveManager.SetCurrentSlot(CoopSlot);
            _ownLoad = true;
            try { cpm.RequestLoad(); }
            finally { _ownLoad = false; }
            try
            {
                if (global::Singleton<HUD>.IsInstanced() && global::Singleton<HUD>.Instance.MyVehicleSwitchingPanel != null)
                    global::Singleton<HUD>.Instance.MyVehicleSwitchingPanel.ClearVehiclesInPanel();
                CloseInGameMenu();
                if (SceneManager.GetActiveScene().buildIndex == 0)
                {
                    // From the main menu, like a menu load: gameplay input and hidden cursor.
                    InputManager.EnableMap("Walk");
                    if (global::Singleton<CursorManager>.IsInstanced())
                        global::Singleton<CursorManager>.Instance.SetCursorMenu(isInMenu: false);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Save transfer: menu cleanup failed: " + ex.Message);
            }

            _clientPhase = ClientPhase.Loading;
            _phaseStarted = Time.unscaledTime;
            _sawLoading = false;
            Plugin.Instance.SetStatus("Cargando el mundo del host...");
        }

        private static void WatchLoad()
        {
            if (_rejectReason != null)
            {
                // The game's own checks refused the save (OnLoadSaveData / ApplySaveDataToScene postfix).
                LoadFailed(_rejectReason);
                return;
            }
            CheckpointManager cpm = Checkpoints();
            if (cpm == null)
            {
                _sawLoading = true;
                // Between scenes. Never wait forever: a load that hangs here for 2 minutes failed.
                if (Time.unscaledTime - _phaseStarted > 120f) LoadFailed("la carga del mundo del host no terminó (ver el log)");
                return;
            }
            LoadingState state = cpm.GetLoadingState();
            if (state != LoadingState.ReadyToPlay)
            {
                _sawLoading = true;
                if (Time.unscaledTime - _phaseStarted > 300f) LoadFailed("la carga del mundo del host no terminó (ver el log)");
                return;
            }
            if (_sawLoading)
            {
                if (!global::Singleton<Player>.IsInstanced() || SceneManager.GetActiveScene().buildIndex == 0)
                {
                    // ApplySaveDataToScene failed and the game went back to the main menu.
                    LoadFailed("no se pudo cargar el mundo del host (ver el log)");
                    return;
                }
                _clientPhase = ClientPhase.Settling;
                _phaseStarted = Time.unscaledTime;
            }
            else if (Time.unscaledTime - _phaseStarted > 15f)
            {
                // RequestLoad rejected the save (revision, DLC, open beta...): it never left ReadyToPlay.
                LoadFailed("el juego rechazó el mundo del host (¿versión o DLC distintos?)");
            }
        }

        /// <summary>
        /// The host's world did not load: this PC still plays its own world (or none). The session
        /// ends with a message; the player's own game and slot are left as they are.
        /// </summary>
        private static void LoadFailed(string why)
        {
            _myLoadPending = false;
            _rejectReason = null;
            // Rejected before anything was loaded: the world that was there stays. Only if that was a
            // host world (a re-send) or a load got half way does the session end give the player its own
            // game back. A failed apply goes to the main menu by itself: nothing to give back there.
            _hostWorldLoaded = !_rejectToMenu && SceneManager.GetActiveScene().buildIndex != 0 && (_prevCoop || _sawLoading);
            _rejectToMenu = false;
            _clientPhase = ClientPhase.Idle;
            Plugin.Log.LogWarning("Save transfer: the host's world was not loaded: " + why);
            Plugin.Instance.SetStatus("No se pudo cargar el mundo del host: " + why + ". Desconectado.");
            EndClientSession("no pudo cargar el mundo: " + why);
        }

        /// <summary>Client: ends the session (cleanly, the host is told) after a fatal problem.</summary>
        private static void EndClientSession(string reason)
        {
            if (Net.Active && !Net.IsHost) Net.Shutdown(reason);
        }

        /// <summary>Unpacks the container into SavePath/Coop_Host, replacing what was there.</summary>
        private static void WriteCoopSlot(byte[] world)
        {
            string savePath = GameSaveManager.GetSavePath();
            if (string.IsNullOrEmpty(savePath)) savePath = Application.persistentDataPath + "/Saves";
            string target = Path.Combine(savePath, CoopSlot);
            string temp = target + ".incoming";
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);
            string tempRoot = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;

            var r = new BinaryReader(new MemoryStream(world), Encoding.UTF8);
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != Magic) throw new InvalidDataException("bad magic");
            int version = r.ReadInt32();
            if (version != Version) throw new InvalidDataException("unsupported version " + version);
            string hostSlot = r.ReadString();
            int files = r.ReadInt32();
            for (int i = 0; i < files; i++)
            {
                string rel = r.ReadString();
                int len = r.ReadInt32();
                byte[] data = r.ReadBytes(len);
                // Never write outside the Coop_Host folder.
                string full = Path.GetFullPath(Path.Combine(temp, rel));
                if (!full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || rel.Length == 0)
                    throw new InvalidDataException("bad file name " + rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllBytes(full, data);
            }

            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(temp, target);
            Plugin.Log.LogInfo($"Save transfer: wrote {files} files of host slot '{hostSlot}' to {target}");
        }

        // ---------- end of a client session: give the player its own game back ----------

        /// <summary>Called by Net.Shutdown (any reason: F7, host gone, timeout, rejected world, quit to menu).</summary>
        internal static void OnSessionEnded(bool wasHost)
        {
            if (!wasHost)
            {
                CoopSaves.OnClientSessionEnded(); // backup / host-lost prompt (holds the restore below)
                if (_hostWorldLoaded)
                {
                    // Still in (or loading) the host's world: leave it once the game can load.
                    _restore = true;
                    _restoreSince = Time.unscaledTime;
                    _restoreLoadAt = -1f;
                }
                else RestoreSlotName();
            }
            Reset();
        }

        /// <summary>The slot the player had before joining becomes the current one again (e.g. for "Continue").</summary>
        private static void RestoreSlotName()
        {
            if (_ownSlot != null && GameSaveManager.GetCurrentSlotName() == CoopSlot) GameSaveManager.SetCurrentSlot(_ownSlot);
        }

        private static bool SlotExists(string slot) =>
            !string.IsNullOrEmpty(slot) && File.Exists(Path.Combine(GameSaveManager.GetSlotSavePath(slot), "CheckpointData.sav"));

        /// <summary>
        /// After the session: the host's world (or what is left of it) is not this player's. Loads the
        /// slot it played before joining, or goes to the main menu when it joined from there.
        /// </summary>
        private static void RestoreTick()
        {
            if (CoopSaves.HoldRestore) return; // saving a backup, or the host-lost prompt is up
            float now = Time.unscaledTime;
            if (_restoreLoadAt >= 0f)
            {
                // The own slot was requested: done once the shared world is gone. If the game refused it
                // (still the same world after 15 s), fall back to the main menu.
                if (!_hostWorldLoaded) { _restoreLoadAt = -1f; _ownSlot = null; return; }
                if (GameState.IsLoading || now - _restoreLoadAt < 15f) return;
                _restoreLoadAt = -1f;
                Plugin.Log.LogWarning("Save transfer: own slot did not load, going to the main menu");
                GoToMainMenu();
                return;
            }
            if (!_restore) return;
            if (!_hostWorldLoaded) { _restore = false; RestoreSlotName(); _ownSlot = null; return; } // already left it
            CheckpointManager cpm = Checkpoints();
            bool canLoad = cpm != null && cpm.GetLoadingState() == LoadingState.ReadyToPlay && !SaveBusy() &&
                           SceneManager.GetActiveScene().buildIndex != 0;
            if (!canLoad)
            {
                if (now - _restoreSince > 120f)
                {
                    _restore = false;
                    Plugin.Log.LogWarning("Save transfer: could not leave the host's world after the session (still loading?)");
                }
                return;
            }
            _restore = false;
            if (_ownInWorld && SlotExists(_ownSlot))
            {
                Plugin.Instance.SetStatus("Sesión co-op terminada" + EndWhy() + ": cargando tu partida (" + _ownSlot + ")");
                GameSaveManager.SetCurrentSlot(_ownSlot);
                CloseInGameMenu();
                _coopSceneHandle = SceneManager.GetActiveScene().handle; // the world being left
                _ownLoad = true;
                try { cpm.RequestLoad(); }
                catch (Exception ex) { Plugin.Log.LogWarning("Save transfer: own slot load failed: " + ex.Message); }
                finally { _ownLoad = false; }
                _restoreLoadAt = now;
            }
            else
            {
                Plugin.Instance.SetStatus("Sesión co-op terminada" + EndWhy() + ": volviendo al menú principal");
                GoToMainMenu();
            }
        }

        /// <summary>" (why)" when the session did not end by the player's own choice, else "".</summary>
        private static string EndWhy() => string.IsNullOrEmpty(Net.EndReason) ? "" : " (" + Net.EndReason + ")";

        private static void GoToMainMenu()
        {
            if (_ownSlot != null) GameSaveManager.SetCurrentSlot(_ownSlot);
            _ownSlot = null;
            try
            {
                CloseInGameMenu();
                SceneManager.LoadScene(MainMenuScene); // same as InGameMenu.BackToMainMenu
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Save transfer: could not go to the main menu: " + ex.Message);
            }
        }

        private static void CloseInGameMenu()
        {
            if (!global::Singleton<MenuManager>.IsInstanced()) return;
            MenuManager menu = global::Singleton<MenuManager>.Instance;
            if (menu.InGameMenu && menu.IsShowed) menu.Close();
        }

        /// <summary>
        /// Notices that the host's world is no longer the loaded one (main menu, another slot) and what
        /// that means: in a session the client left it (session over); afterwards saves stop being
        /// redirected.
        /// </summary>
        private static void TrackCoopWorld()
        {
            if (!_hostWorldLoaded) return;
            Scene scene = SceneManager.GetActiveScene();
            bool loadingPhase = _clientPhase == ClientPhase.WaitToLoad || _clientPhase == ClientPhase.Loading ||
                                _clientPhase == ClientPhase.Settling;
            if (scene.buildIndex == 0 && !loadingPhase)
            {
                _hostWorldLoaded = false;
                if (Net.Active && !Net.IsHost)
                {
                    Plugin.Instance.SetStatus("Volviste al menú principal: saliste de la partida co-op");
                    Net.Shutdown(); // -> OnSessionEnded: restores the slot name
                }
                else RestoreSlotName();
                return;
            }
            // Not in a session: another world replaced the shared one (own slot loaded).
            if (!Net.Active && _coopSceneHandle != 0 && scene.handle != _coopSceneHandle && scene.buildIndex != 0 &&
                !GameState.IsLoading)
                _hostWorldLoaded = false;
        }

        // =====================================================================
        // Shared
        // =====================================================================

        internal static void Tick()
        {
            TrackCoopWorld();

            if (!Net.Active)
            {
                // _rxId too: the next session's host numbers its transfers from 1 again.
                if (_clientPhase != ClientPhase.Idle || _hostPhase != HostPhase.Idle || Sending.Count > 0 || _sessionWorld ||
                    PendingPeers.Count > 0 || _rxId != 0 || Awaiting.Count > 0 || ReadyPeers.Count > 0 ||
                    _hostSceneHandle != 0 || _holdWorld || WorldJournal.Count > 0) Reset();
                RestoreTick();
                return;
            }
            // A new session started: its world replaces whatever is loaded; nothing to give back now.
            _restore = false;
            _restoreLoadAt = -1f;
            if (Net.IsHost) HostTick();
            else ClientTick();
        }

        // ---------- API for CoopSaves (co-op saves, host loss) ----------

        /// <summary>Host: no world is being saved for, or sent to, a joiner right now.</summary>
        internal static bool HostTransferIdle => _hostPhase == HostPhase.Idle && PendingPeers.Count == 0 && Sending.Count == 0;

        /// <summary>
        /// The host is gone and the player chose to keep playing the shared world alone: it becomes the
        /// player's own world in <paramref name="slot"/> (saves go there, nothing loads the old slot back).
        /// </summary>
        internal static void KeepWorldAsOwn(string slot)
        {
            _restore = false;
            _restoreLoadAt = -1f;
            _hostWorldLoaded = false;
            _ownSlot = null;
            GameSaveManager.SetCurrentSlot(slot);
        }

        private static void Reset()
        {
            PendingPeers.Clear();
            Sending.Clear();
            Awaiting.Clear();
            ReadyPeers.Clear();
            _hostPhase = HostPhase.Idle;
            _hostAway = false;
            _hostSceneHandle = 0;
            _clientPhase = ClientPhase.Idle;
            _sessionWorld = false;
            _rxBuf = null;
            _rxGot = null;
            _rxWorld = null;
            _rxId = 0;
            _myLoadPending = false;
            _rejectReason = null;
            _rejectToMenu = false;
            _holdWorld = false;
            ClearWorldJournal();
        }

        private static void SendControl(ulong peer, Ctl kind, string text, int id = 0)
        {
            var ms = Net.Writer(MsgControl);
            var w = new BinaryWriter(ms);
            w.Write((byte)kind);
            if (text != null) w.Write(text);
            if (kind == Ctl.Loaded) w.Write(id);
            Net.Transport.Send(peer, ms.ToArray(), true);
        }

        private static CheckpointManager Checkpoints() =>
            SaveSystem.Singleton<CheckPointManagerBase>.IsInstanced()
                ? SaveSystem.Singleton<CheckPointManagerBase>.Instance as CheckpointManager
                : null;

        private static bool SaveBusy() =>
            SaveSystem.Singleton<GameSaveManager>.IsInstanced() && SaveSystem.Singleton<GameSaveManager>.Instance.CheckIfBusy();

        /// <summary>
        /// Which slot a save must use instead of the one the game picked, or null to leave it.
        /// While the host's world is the loaded one on this PC (in a session or after it): always
        /// Coop_Host, so the host's world never lands in one of the player's own slots.
        /// </summary>
        private static string RedirectSlot()
        {
            if (!_hostWorldLoaded || (Net.Active && Net.IsHost)) return null;
            return GameSaveManager.GetCurrentSlotName() == CoopSlot ? null : CoopSlot;
        }

        // ---------- Harmony: keep the client's saves inside Coop_Host ----------

        [HarmonyPatch(typeof(CheckpointManager), nameof(CheckpointManager.QuickSave))]
        private static class QuickSavePatch
        {
            private static bool Prefix()
            {
                // Host: another save into the slot that is being saved for a joiner (autosave, save key)
                // would replace the file with a newer world than the one the catch-up data matches.
                if (Net.Active && Net.IsHost && _hostPhase == HostPhase.Saving && !_ownSave &&
                    GameSaveManager.GetCurrentSlotName() == _savingSlot)
                {
                    Plugin.Instance.SetStatus("Guardado omitido: se está enviando esta partida a un jugador");
                    return false;
                }
                // The client's autosave in a session is useless (the host sends its world on every join)
                // and only costs a hitch: skip it. Autosave() already moved its next time on.
                if (ClientSession && GameSaveManager.GetCurrentSlotName() == "AutoSave")
                {
                    GameSaveManager.SetCurrentSlot(CoopSlot);
                    return false;
                }
                string slot = RedirectSlot();
                if (slot == null) return true;
                Plugin.Log.LogInfo($"Save transfer: save to '{GameSaveManager.GetCurrentSlotName()}' redirected to {slot}");
                GameSaveManager.SetCurrentSlot(slot);
                return true;
            }
        }

        [HarmonyPatch(typeof(CheckpointManager), nameof(CheckpointManager.RequestLoad))]
        private static class RequestLoadPatch
        {
            private static bool Prefix(CheckpointManager __instance)
            {
                if (_ownLoad || !ClientSession) return true;
                string slot = GameSaveManager.GetCurrentSlotName();
                if (slot == CoopSlot || slot == "QuickSave")
                {
                    // QuickLoad key / menu "Load" of the shared world: the copy on disk is stale (only the
                    // host's live world counts). Refused; F7 leaves the session.
                    GameSaveManager.SetCurrentSlot(CoopSlot);
                    Plugin.Instance.SetStatus("En co-op no se puede cargar otra partida (F7 para salir de la sesión)");
                    return false;
                }
                // The player picked one of its own slots: that means leaving the shared world. Only when
                // the game will really load it (same conditions as RequestLoad itself).
                if (__instance.GetLoadingState() != LoadingState.ReadyToPlay || SaveBusy()) return true;
                Plugin.Instance.SetStatus("Cargando tu partida '" + slot + "': saliste de la partida co-op");
                _coopSceneHandle = SceneManager.GetActiveScene().handle; // the world being left
                Net.Shutdown();      // OnSessionEnded schedules a restore...
                _restore = false;    // ...not needed: this load is the player's own game
                _ownSlot = null;
                return true;
            }
        }

        /// <summary>Why the game refused our load of the host's world, from the game's own checks.</summary>
        [HarmonyPatch(typeof(CheckpointManager), "OnLoadSaveData")]
        private static class OnLoadSaveDataPatch
        {
            private static void Postfix(CheckpointManager __instance, object __0, GameSaveManager.ERRORS __1)
            {
                if (!_myLoadPending) return;
                _myLoadPending = false;
                if (__instance.GetLoadingState() != LoadingState.ReadyToPlay) return; // accepted
                var xml = __0 as XmlData;
                if (__1 != GameSaveManager.ERRORS.OK || xml == null) _rejectReason = "el archivo recibido no se pudo leer";
                else if (xml.Revision > SaveSystem.SaveData.LastUpdateRevision())
                    _rejectReason = "tu juego es más antiguo que el del host (actualízalo)";
                else if (xml.IsOpenBeta && !SaveSystem.SaveData.IsOpenBeta())
                    _rejectReason = "el host juega la beta abierta y tú no";
                else _rejectReason = "te faltan DLC que usa la partida del host";
            }
        }

        [HarmonyPatch(typeof(CheckpointManager), "ApplySaveDataToScene")]
        private static class ApplySaveDataPatch
        {
            private static void Postfix(bool __result)
            {
                if (__result || !_hostWorldLoaded || _clientPhase != ClientPhase.Loading) return;
                _rejectReason = "el juego no pudo aplicar el mundo del host (¿versión distinta?)";
                _rejectToMenu = true; // CheckpointManager.ApplySaveDataToScene loads the main menu itself
            }
        }
    }
}
