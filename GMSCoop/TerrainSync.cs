using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Every dig path in the game (GoldTerrainDigger, TerrainDeformerBase incl. wheel ruts, Kopanie, ...)
    /// ends in UnityEngine.TerrainData.SetHeights / SetHeightsDelayLOD / SetAlphamaps.
    /// Patching those three Unity methods catches all terrain edits in one place.
    ///
    /// Coalescing: a game write only marks a dirty rectangle per terrain (heights and splat maps
    /// separately). Every 0.1 s the dirty rectangle is read back, compared with a "shadow" (the
    /// values every peer is believed to have: captured lazily in 64x64 blocks right before the
    /// first local write touches a block, and updated on every send/receive), and only the cells
    /// that really changed are sent: their bounding box, a change mask and the absolute values
    /// (delta-predicted, byte planes, deflated). Absolute values keep the result correct even if
    /// two PCs differ slightly elsewhere; unchanged cells in the box keep the receiver's own value.
    /// Messages stay reliable (ordered).
    ///
    /// Old scheme for comparison: every game call sent its whole region uncompressed (2 bytes per
    /// height cell, 1 byte per cell and layer for splats). A truck on soft ground deforms per wheel
    /// per physics step (TerrainDeformerBase.CalculateNewHeightsForWheel, 50 Hz x 4..6 wheels,
    /// ~10x10..20x20 cells): roughly 40..160 KB/s of reliable traffic plus a SetAlphamaps per shovel
    /// dig. Now: at most 10 messages/s per terrain and map, usually a few hundred bytes each.
    ///
    /// Receiver: SetHeightsDelayLOD, then a single TerrainData.SyncHeightmap (= ApplyDelayedHeightmapModification) per
    /// terrain per frame (the game's own dig code does the same).
    ///
    /// Terrain identity: the rounded world position (x, z) of the Terrain tile plus its heightmap
    /// resolution. Streamed areas (AreaStreamer) load their own terrain tiles, so the set of loaded
    /// tiles differs between PCs; edits (and parcel resets / drill points) for a tile that is not loaded
    /// here wait in one ordered queue per tile, compacted to the newest value per cell so that hours of
    /// digging elsewhere stay bounded by one tile's size, and are applied as soon as that tile is loaded
    /// (see "late edits"). The host also sends its queues to a joining player (they are not in its save).
    ///
    /// Join journal (host only): edits made after the host saved the world for a joining player are
    /// not in that save. The host keeps every terrain message (own and relayed) since its last
    /// save-for-transfer and replays them to the player once it reports the world as loaded
    /// (SaveTransfer calls ReplayJournal). Messages carry absolute values, so replaying in order is exact.
    /// Recorded only while a peer is joining (between its save and its Loaded): no cost otherwise.
    ///
    /// Message (Msg.TerrainHeights / Msg.TerrainAlphas): version u8 (=3), origin u64 (sender LocalId),
    /// seq u32, key x i32, key z i32, resolution i32, x i32, y i32, w i32, h i32, layers u8 (alphas
    /// only), codec u8 (0 raw, 1 deflate), raw length i32, payload length i32, payload.
    /// Payload: allChanged u8, [mask bits w*h if not all changed], then for the changed cells
    /// (row-major) the values minus the previous changed cell's value, low byte plane then high byte
    /// plane (heights, u16) / one plane per layer (alphas, u8).
    /// Also carried in Msg.TerrainHeights: host acknowledgements (AckKind) and gold/resource voxel
    /// edits (VoxKind); parcel resets travel as (Msg)14 sub 4 and are journaled here too.
    ///
    /// Concurrent edits are host-ordered (see "host ordering"): every PC ends with the same values.
    /// Edits for a tile that is not loaded apply as soon as it loads; if the tile then restores its
    /// saved state they are applied again. FreezeDirt (season frost texture, computed on every PC from
    /// the shared clock) is never sent; load/restore full-map writes are local by nature.
    /// </summary>
    internal static class TerrainSync
    {
        private const byte Version = 3;
        private const byte AckKind = 0xAC;             // host -> all: [type][AckKind][target u64][seq u32]
        private const byte VoxKind = 0x56;             // gold / resource voxel edit, see voxels section
        private const float FlushInterval = 0.1f;
        private const int MaxCells = 256 * 256;        // larger single writes are load/restore, not synced
        private const int MaxDirtyCells = 192 * 192;   // a sparse coalesced rect larger than this is flushed early
        private const int Block = 64;                  // shadow block size (cells)
        private const int MaxShadowBlocks = 4096;
        private const float RecentWindow = 15f;        // edits to a freshly loaded tile are re-applied if its saved state is restored
        private const long RecentMaxBytes = 8L * 1024 * 1024;
        private const long PendingMaxBytes = 64L * 1024 * 1024; // all queues; one compacted tile is ~1..4 MB

        // Set while applying remote data so the patches do not echo it back.
        private static bool _applying;
        // > 0 while a local-only write runs (GoldTerrainDigger.FreezeDirt).
        private static int _suppress;
        private static bool _loggedFirstHeights, _loggedVersion;

        // ---------- per terrain state ----------

        private sealed class Map
        {
            public readonly Dictionary<int, byte[]> Shadow = new Dictionary<int, byte[]>(); // heights: u16 LE pairs
            public int Layers;                 // alphas: layer count the shadow was built for
            public bool Dirty;
            public int X0, Y0, X1, Y1;         // dirty rect, exclusive max
            // Client: cells this PC sent that the host has not ordered yet (cell index -> seq).
            public readonly Dictionary<int, uint> Unacked = new Dictionary<int, uint>();
        }

        private sealed class Info
        {
            public Terrain T;
            public TerrainData D;
            public int Kx, Kz;
            public float FirstSeen;
            public readonly Map H = new Map();
            public readonly Map A = new Map();
            // Messages applied/sent while the tile was freshly loaded (re-applied after a restore).
            public readonly List<byte[]> Recent = new List<byte[]>();
            public long RecentBytes;
            public bool ReapplyH, ReapplyA, ReapplyV;
            public Vox V;
        }

        private static readonly Dictionary<TerrainData, Info> ByData = new Dictionary<TerrainData, Info>();
        private static readonly Dictionary<long, Info> ByKey = new Dictionary<long, Info>();
        private static readonly HashSet<Info> LodPending = new HashSet<Info>();
        // World area (x/z and new height range) of the remote height writes since the last SyncHeightmap, per tile:
        // loose objects there are lifted / woken when the collider changes (ObjectSync.TerrainEditBegin/End).
        private static readonly Dictionary<Info, Bounds> EditArea = new Dictionary<Info, Bounds>();
        private static float _builtAt = -10f;
        private static float _nextFlush, _nextHousekeeping, _nextPendingRetry;
        private static int _lastTickFrame = -1;

        private static long Key(int kx, int kz) => ((long)kx << 32) | (uint)kz;

        private static void Rebuild()
        {
            _builtAt = Time.unscaledTime;
            float now = Time.unscaledTime;
            foreach (Terrain t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                if (ByData.TryGetValue(t.terrainData, out Info info) && info.T == t) continue;
                Vector3 p = t.transform.position;
                info = new Info
                {
                    T = t, D = t.terrainData, FirstSeen = now,
                    Kx = Mathf.RoundToInt(p.x), Kz = Mathf.RoundToInt(p.z),
                };
                ByData[info.D] = info;
                ByKey[Key(info.Kx, info.Kz)] = info;
            }
            // Forget destroyed tiles (streamed area unloaded).
            List<TerrainData> dead = null;
            foreach (var kv in ByData)
                if (kv.Value.T == null || kv.Key == null) (dead ??= new List<TerrainData>()).Add(kv.Key);
            if (dead == null) return;
            foreach (var d in dead)
            {
                Info info = ByData[d];
                ByData.Remove(d);
                if (ByKey.TryGetValue(Key(info.Kx, info.Kz), out Info k) && k == info) ByKey.Remove(Key(info.Kx, info.Kz));
                LodPending.Remove(info);
                EditArea.Remove(info);
            }
        }

        private static readonly List<BoxCollider> _noDig = new List<BoxCollider>();
        private static readonly Collider[] _noDigHits = new Collider[32];
        private static int _noDigMask = -2;

        /// <summary>
        /// The machines' / trailers' no-dig volumes (CantDigUnderMachine, layer NoColisions, as GoldTerrainDigger
        /// .AddVolumeDirt finds them) over heightmap cells x..x+w, y..y+h of the tile. False if there are none.
        /// </summary>
        private static bool CollectNoDigZones(Info info, int x, int y, int w, int h)
        {
            _noDig.Clear();
            if (_noDigMask == -2) _noDigMask = LayerMask.GetMask("NoColisions");
            if (_noDigMask == 0) return false;
            TerrainData d = info.D;
            Vector3 o = info.T.GetPosition(), size = d.size;
            int res = d.heightmapResolution;
            float sx = size.x / (res - 1), sz = size.z / (res - 1);
            var min = new Vector3(o.x + x * sx, o.y - 10f, o.z + y * sz);
            var max = new Vector3(o.x + (x + w - 1) * sx, o.y + size.y + 10f, o.z + (y + h - 1) * sz);
            Vector3 half = (max - min) * 0.5f + new Vector3(1f, 0f, 1f);
            int n = Physics.OverlapBoxNonAlloc((min + max) * 0.5f, half, _noDigHits, Quaternion.identity, _noDigMask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                Collider col = _noDigHits[i];
                _noDigHits[i] = null;
                if (col == null || !col.enabled) continue;
                CantDigUnderMachine cd = col.GetComponent<CantDigUnderMachine>();
                if (cd == null || !cd.isActiveAndEnabled || cd.BlockingVolume == null) continue;
                if (!_noDig.Contains(cd.BlockingVolume)) _noDig.Add(cd.BlockingVolume);
            }
            return _noDig.Count > 0;
        }

        /// <summary>World x/z inside one of the collected no-dig volumes (their footprint, height ignored).</summary>
        private static bool InNoDigZone(float wx, float wz)
        {
            foreach (BoxCollider b in _noDig)
            {
                if (b == null) continue;
                Transform t = b.transform;
                Vector3 l = t.InverseTransformPoint(new Vector3(wx, t.TransformPoint(b.center).y, wz)) - b.center;
                // Footprint in the box's own x/z (as the game's rectangle: right / forward axes of the volume).
                if (Mathf.Abs(l.x) <= b.size.x * 0.5f && Mathf.Abs(l.z) <= b.size.z * 0.5f) return true;
            }
            return false;
        }

        /// <summary>Adds a remote height write (heightmap cells x..x+w, y..y+h, new values) to the tile's edited world area.</summary>
        private static void AddEditArea(Info info, int x, int y, int w, int h, float[,] arr)
        {
            TerrainData d = info.D;
            Vector3 o = info.T.GetPosition(), size = d.size;
            int res = d.heightmapResolution;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++)
                {
                    float v = arr[i, j];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
            float sx = size.x / (res - 1), sz = size.z / (res - 1);
            var min = new Vector3(o.x + x * sx, o.y + lo * size.y, o.z + y * sz);
            var max = new Vector3(o.x + (x + w - 1) * sx, o.y + hi * size.y, o.z + (y + h - 1) * sz);
            var b = new Bounds();
            b.SetMinMax(min, max);
            if (EditArea.TryGetValue(info, out Bounds old)) b.Encapsulate(old);
            EditArea[info] = b;
        }

        private static Info Get(TerrainData d)
        {
            if (d == null) return null;
            if (ByData.TryGetValue(d, out Info info) && info.T != null) return info;
            if (Time.unscaledTime - _builtAt > 0.25f) Rebuild();
            return ByData.TryGetValue(d, out info) && info.T != null ? info : null;
        }

        private static Info Get(int kx, int kz)
        {
            if (ByKey.TryGetValue(Key(kx, kz), out Info info) && info.T != null) return info;
            if (Time.unscaledTime - _builtAt > 0.25f) Rebuild();
            return ByKey.TryGetValue(Key(kx, kz), out info) && info.T != null ? info : null;
        }

        private static void ResetAll()
        {
            ClearWorld();
            _seq = 0;
            _world = -1;
        }

        private static int _world = -1;
        private static readonly SubsystemGuard GTick = new SubsystemGuard("Terrain sync"),
            GRecv = new SubsystemGuard("Terrain sync: receive"),
            GHook = new SubsystemGuard("Terrain sync: write hook"); // terrain writes run up to 50 Hz per wheel: rate-limited

        /// <summary>
        /// Everything tied to the world this PC played: tile infos (shadows, unacked cells, recent edits),
        /// edits and parcel resets waiting for a tile to load, the client's unacknowledged sends. Called when
        /// that world is replaced (GameState.WorldVersion: load, host reload, scene change): an edit of the
        /// old world must never be applied to a tile of the new one when it loads.
        /// </summary>
        private static void ClearWorld()
        {
            ByData.Clear();
            ByKey.Clear();
            LodPending.Clear();
            EditArea.Clear();
            Pending.Clear();
            _pendingBytes = 0;
            _builtAt = -10f;
            Acks.Clear();
        }

        /// <summary>Drops the old world's state once a new world is detected (cheap: once per frame).</summary>
        private static void CheckWorld()
        {
            int world = GameState.WorldVersion;
            if (world == _world) return;
            bool had = _world >= 0 && Pending.Count > 0;
            _world = world;
            ClearWorld();
            if (had) Plugin.Log.LogInfo("Terrain sync: world replaced, edits waiting for tiles of the old world dropped");
        }

        // ---------- host ordering ----------
        //
        // Every edit message carries its sender (LocalId) and a per-sender sequence number. The host is
        // the sequencer: the order in which it applies messages is the order every PC ends up with.
        //  - The host applies every message at once and immediately sends an acknowledgement
        //    (sender, seq) to everybody, before it relays the message itself.
        //  - A client remembers which cells it sent and the host has not acknowledged yet. An incoming
        //    message (host order) does not overwrite those cells: the host applies our edit after it,
        //    so our value is the final one there. Once acknowledged, incoming values apply again.
        //  - Every PC (host too) keeps cells it changed but has not sent yet (current value differs
        //    from the shadow): its own message follows in host order and wins.
        // Two players editing the same cells therefore end with identical values on every PC.

        // Pure part (SyncCore.AckTracker, tested offline with jittery host ordering).
        private static readonly AckTracker Acks = new AckTracker();
        private static uint _seq;

        private static uint NextSeq() => ++_seq;

        private static void TrackUnacked(Dictionary<int, uint> map, uint seq, List<int> cells)
        {
            if (Net.IsHost) return;
            Acks.Track(map, seq, cells);
        }

        private static void OnAck(uint seq) => Acks.OnAck(seq);

        private static void SendAck(ulong target, uint seq)
        {
            var ms = Net.Writer(Msg.TerrainHeights);
            var w = new BinaryWriter(ms);
            w.Write(AckKind);
            w.Write(target);
            w.Write(seq);
            TrafficC.Broadcast(TrafficC.Cat.Terrain, ms.ToArray(), true);
        }

        /// <summary>This PC plays the shared world and is not loading: edits are real player edits.</summary>
        private static bool Synced() => Net.WorldLive;

        // ---------- join journal ----------

        private const long JournalMaxBytes = 64L * 1024 * 1024;
        private static readonly List<byte[]> Journal = new List<byte[]>();
        private static long _journalBytes;
        private static bool _journalFullLogged; // once per journal (a slow join would log it for every edit)

        /// <summary>Host: the world was just saved for a joining player; start a fresh journal.</summary>
        internal static void ClearJournal()
        {
            // Edits still waiting in a dirty rect are already in the terrain the save just captured;
            // sending them now is harmless (absolute values) and keeps the shadows exact.
            try { FlushAll(true); }
            catch (Exception ex) { GTick.Fail(ex); } // the journal starts fresh in any case (called by SaveTransfer)
            Journal.Clear();
            _journalBytes = 0;
            _journalFullLogged = false;
        }

        /// <summary>Host: a peer is between its world save and its Loaded (only then is the journal replayed).</summary>
        private static bool JoinPending()
        {
            for (int i = 0; i < Net.Peers.Count; i++)
                if (!SaveTransfer.PeerReady(Net.Peers[i])) return true;
            return false;
        }

        private static void Record(byte[] msg)
        {
            // Only while somebody is joining: nobody else ever needs the journal (no cost the rest of the time).
            if (!Net.Active || !Net.IsHost || !JoinPending()) return;
            if (_journalBytes + msg.Length > JournalMaxBytes)
            {
                if (!_journalFullLogged)
                {
                    _journalFullLogged = true;
                    Plugin.Log.LogWarning("Terrain sync: journal full (64 MB), a joining player may miss some digs");
                }
                return;
            }
            Journal.Add(msg);
            _journalBytes += msg.Length;
        }

        /// <summary>Host: send every edit made since the transfer save to a player that just loaded.</summary>
        internal static void ReplayJournal(ulong peer)
        {
            // Called by SaveTransfer before the tutorial / economy catch-up: never throws into it.
            int pend;
            try
            {
                FlushAll(true); // local edits of the last 0.1 s go into the journal first, keeping the order
                // Edits for tiles the host has not loaded are not in its save: they go first (older than the
                // journal; a queued edit made after the save is in the journal too and is applied again there,
                // in its place, so the order of the newest values is kept).
                pend = SendPendingTo(peer);
                foreach (var msg in Journal)
                {
                    Net.Transport.Send(peer, msg, true);
                    TrafficC.Sent(TrafficC.Cat.Terrain, msg.Length);
                }
            }
            catch (Exception ex) { GTick.Fail(ex); return; }
            Plugin.Log.LogInfo($"Terrain sync: replayed {Journal.Count} edits ({_journalBytes / 1024} KB)" +
                (pend > 0 ? $" and {pend} queued edits of tiles not loaded on the host" : "") + $" to {Net.NameOf(peer)}");
        }

        // ---------- late edits for tiles that are not loaded here ----------
        //
        // Streamed areas load their own terrain tiles, so a tile another player digs in may not be loaded on
        // this PC for hours. Everything for such a tile (heights, splats, voxels, parcel resets, drill
        // points) waits in ONE ordered queue per tile and is applied, in order, the moment the tile loads.
        // A long session must neither grow these queues without bound nor drop the oldest edits (that
        // would leave the tile wrong when this PC finally goes there), so a queue is COMPACTED whenever it
        // grew by CompactEvery: all heights messages since the last parcel reset become one message with
        // the newest value of every cell, likewise splats and voxels (values are absolute, the newest
        // wins, so the result is exactly the same terrain). A queue is then bounded by the size of one
        // tile, whatever the session length. A parcel reset drops the heights / splats queued before it
        // (it rewrites the whole tile).
        // Host: its own queues are edits that are NOT in its save (the tile was not loaded); a joining
        // player gets them right before the journal (ReplayJournal).

        private sealed class PendTile
        {
            public int Kx, Kz;
            public readonly List<byte[]> Msgs = new List<byte[]>();
            public long Bytes, BytesAtCompact;
        }

        private const long CompactEvery = 512L * 1024;
        private static readonly Dictionary<long, PendTile> Pending = new Dictionary<long, PendTile>();
        private static readonly List<long> _pendKeys = new List<long>();
        private static long _pendingBytes;
        private static bool _loggedPendingFull;

        private static bool IsTileCommand(byte[] raw) =>
            raw.Length >= 10 && raw[0] == (byte)GoldSync.MsgId && (raw[1] == GoldSync.SubParcelReset || raw[1] == GoldSync.SubDrill);

        /// <summary>Tile key of a terrain message (key at byte 14) or a reset / drill command (key at byte 2).</summary>
        private static bool TileOf(byte[] raw, out int kx, out int kz)
        {
            kx = kz = 0;
            int at = IsTileCommand(raw) ? 2 : KeyOffset;
            if (raw.Length < at + 8) return false;
            kx = BitConverter.ToInt32(raw, at);
            kz = BitConverter.ToInt32(raw, at + 4);
            return true;
        }

        private static bool HasPendingFor(int kx, int kz) => Pending.Count > 0 && Pending.ContainsKey(Key(kx, kz));

        /// <summary>
        /// True if another player's edits still wait for a loaded tile that covers <paramref name="p"/> (x/z): its
        /// heights are not final yet (a freshly loaded area shows its saved surface until RetryPending ran).
        /// </summary>
        internal static bool HasPendingAt(Vector3 p)
        {
            if (Pending.Count == 0) return false;
            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains == null) return false;
            foreach (Terrain t in terrains)
            {
                if (t == null || t.terrainData == null) continue;
                Vector3 o = t.GetPosition(), size = t.terrainData.size;
                if (p.x < o.x || p.z < o.z || p.x > o.x + size.x || p.z > o.z + size.z) continue;
                if (Pending.ContainsKey(Key(Mathf.RoundToInt(o.x), Mathf.RoundToInt(o.z)))) return true;
            }
            return false;
        }

        private static void Pend(byte[] raw)
        {
            if (!TileOf(raw, out int kx, out int kz)) return;
            long key = Key(kx, kz);
            if (!Pending.TryGetValue(key, out PendTile t)) Pending[key] = t = new PendTile { Kx = kx, Kz = kz };
            if (IsTileCommand(raw) && raw[1] == GoldSync.SubParcelReset)
            {
                // The reset rewrites the tile's heights and splats: what was queued for them before is moot.
                int kept = 0;
                for (int i = 0; i < t.Msgs.Count; i++)
                {
                    byte[] m = t.Msgs[i];
                    bool hOrA = !IsTileCommand(m) && m[1] != VoxKind;
                    if (hOrA) { t.Bytes -= m.Length; _pendingBytes -= m.Length; continue; }
                    t.Msgs[kept++] = m;
                }
                t.Msgs.RemoveRange(kept, t.Msgs.Count - kept);
                t.BytesAtCompact = Math.Min(t.BytesAtCompact, t.Bytes);
            }
            t.Msgs.Add(raw);
            t.Bytes += raw.Length;
            _pendingBytes += raw.Length;
            if (t.Bytes - t.BytesAtCompact > CompactEvery) Compact(t);
            if (_pendingBytes > PendingMaxBytes) DropLargestPending();
        }

        /// <summary>Last resort (dozens of distinct tiles, each at its compacted maximum): drop the largest queue.</summary>
        private static void DropLargestPending()
        {
            while (_pendingBytes > PendingMaxBytes && Pending.Count > 0)
            {
                long worst = 0;
                PendTile wt = null;
                foreach (var kv in Pending) if (wt == null || kv.Value.Bytes > wt.Bytes) { worst = kv.Key; wt = kv.Value; }
                Pending.Remove(worst);
                _pendingBytes -= wt.Bytes;
                if (!_loggedPendingFull)
                {
                    _loggedPendingFull = true;
                    Plugin.Log.LogWarning($"Terrain sync: too many edits for terrain tiles not loaded here, tile ({wt.Kx},{wt.Kz}) dropped");
                }
            }
        }

        private static void RetryPending()
        {
            if (Pending.Count == 0 || GameState.IsLoading) return;
            _pendKeys.Clear();
            foreach (var kv in Pending) if (Get(kv.Value.Kx, kv.Value.Kz) != null) _pendKeys.Add(kv.Key);
            foreach (long key in _pendKeys)
            {
                PendTile t = Pending[key];
                int done = 0;
                for (; done < t.Msgs.Count; done++)
                {
                    bool waiting;
                    try { waiting = !ApplyQueued(t.Msgs[done]); }
                    catch (Exception ex) { GRecv.Fail(ex); waiting = false; } // a bad entry is dropped
                    if (waiting) break; // the tile went away again: the rest waits, in order
                }
                for (int i = 0; i < done; i++) { t.Bytes -= t.Msgs[i].Length; _pendingBytes -= t.Msgs[i].Length; }
                t.Msgs.RemoveRange(0, done);
                t.BytesAtCompact = Math.Min(t.BytesAtCompact, t.Bytes);
                if (t.Msgs.Count == 0) Pending.Remove(key);
            }
            if (Pending.Count == 0) _pendingBytes = 0;
        }

        /// <summary>Applies one queued entry. False = its tile is not loaded (keep it).</summary>
        private static bool ApplyQueued(byte[] raw) => ApplyCommandOrEdit(raw, true);

        /// <summary>
        /// A reset / drill command or a terrain message. remember = keep it for a re-apply if the (freshly
        /// loaded) tile restores its saved state afterwards: that restore also brings back the old heights,
        /// splats, gold and drill points, so a queued parcel reset or drill point applied right after the tile
        /// loaded must run again, like the edits (see ReapplyRestored).
        /// </summary>
        private static bool ApplyCommandOrEdit(byte[] raw, bool remember)
        {
            if (IsTileCommand(raw))
            {
                var r = new BinaryReader(new MemoryStream(raw, 2, raw.Length - 2));
                int kx = r.ReadInt32(), kz = r.ReadInt32();
                if (raw[1] == GoldSync.SubParcelReset) return ApplyReset(kx, kz, r.ReadInt32(), remember ? raw : null);
                var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                return ApplyDrill(kx, kz, p, r.ReadSingle(), remember ? raw : null);
            }
            if (!remember) return TryApply(raw, true, out _);
            return TryApply(raw, false, out bool waiting) || !waiting;
        }

        // ---- compaction ----

        private sealed class Merge
        {
            public int Kx, Kz;
            // heights: sparse (only cells that changed), newest value per cell
            public int HRes = -1;
            public Dictionary<int, ushort> HV;
            public int HX0 = int.MaxValue, HY0 = int.MaxValue, HX1 = -1, HY1 = -1;
            // splats: sparse, newest layer bytes per cell
            public int AResX = -1, ALayers = -1;
            public Dictionary<int, byte[]> AV;
            public int AX0 = int.MaxValue, AY0 = int.MaxValue, AX1 = -1, AY1 = -1;
            // voxels: per array id, newest value per index
            public readonly Dictionary<byte, KeyValuePair<int, Dictionary<int, float>>> V = new Dictionary<byte, KeyValuePair<int, Dictionary<int, float>>>();
            public bool Any;
        }

        /// <summary>Folds one terrain message into the merge. False = it does not fit (other resolution / layers): keep it as it is.</summary>
        private static bool MergeIn(Merge mg, byte[] raw)
        {
            var r = new BinaryReader(new MemoryStream(raw, 2, raw.Length - 2));
            r.ReadUInt64(); r.ReadUInt32(); // origin, seq
            int kx = r.ReadInt32(), kz = r.ReadInt32();
            if (kx != mg.Kx || kz != mg.Kz) return false;
            if (raw[1] == VoxKind)
            {
                int arrays = r.ReadByte();
                var parsed = new List<KeyValuePair<byte, KeyValuePair<int, KeyValuePair<int[], float[]>>>>();
                for (int a = 0; a < arrays; a++)
                {
                    byte id = r.ReadByte();
                    int len = r.ReadInt32();
                    byte[] body = ReadPayload(r);
                    if (mg.V.TryGetValue(id, out var have) && have.Key != len) return false;
                    var ms = new MemoryStream(body);
                    var br = new BinaryReader(ms);
                    int count = br.ReadInt32();
                    if (count < 0 || count > len) return false;
                    var idx = new int[count];
                    int prev = 0;
                    for (int c = 0; c < count; c++) { prev += ReadVarint(ms); idx[c] = prev; }
                    var val = new float[count];
                    for (int c = 0; c < count; c++) val[c] = br.ReadSingle();
                    parsed.Add(new KeyValuePair<byte, KeyValuePair<int, KeyValuePair<int[], float[]>>>(id,
                        new KeyValuePair<int, KeyValuePair<int[], float[]>>(len, new KeyValuePair<int[], float[]>(idx, val))));
                }
                foreach (var p in parsed)
                {
                    if (!mg.V.TryGetValue(p.Key, out var slot))
                        mg.V[p.Key] = slot = new KeyValuePair<int, Dictionary<int, float>>(p.Value.Key, new Dictionary<int, float>());
                    int[] idx = p.Value.Value.Key;
                    float[] val = p.Value.Value.Value;
                    for (int c = 0; c < idx.Length; c++)
                        if (idx[c] >= 0 && idx[c] < p.Value.Key) slot.Value[idx[c]] = val[c];
                }
                mg.Any = true;
                return true;
            }
            bool heights = (Msg)raw[0] == Msg.TerrainHeights;
            int res = r.ReadInt32();
            int x = r.ReadInt32(), y = r.ReadInt32(), w = r.ReadInt32(), h = r.ReadInt32();
            int n = heights ? 1 : r.ReadByte();
            if (w <= 0 || h <= 0 || x < 0 || y < 0 || res <= 0 || res > 16385 || x + w > res) return false;
            byte[] payload = ReadPayload(r);
            var pr = new BinaryReader(new MemoryStream(payload));
            bool[] mask = ReadMask(pr, w * h);
            int count2 = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i]) count2++;
            if (heights)
            {
                if (y + h > res) return false;
                if (mg.HRes < 0) { mg.HRes = res; mg.HV = new Dictionary<int, ushort>(); }
                else if (mg.HRes != res) return false;
                byte[] lo = pr.ReadBytes(count2), hi = pr.ReadBytes(count2);
                if (hi.Length != count2) return false;
                ushort prev = 0;
                int c = 0;
                for (int i = 0; i < h; i++)
                    for (int j = 0; j < w; j++)
                    {
                        if (!mask[i * w + j]) continue;
                        prev = (ushort)(prev + (lo[c] | hi[c] << 8));
                        c++;
                        mg.HV[(y + i) * res + x + j] = prev;
                    }
                mg.HX0 = Math.Min(mg.HX0, x); mg.HY0 = Math.Min(mg.HY0, y);
                mg.HX1 = Math.Max(mg.HX1, x + w); mg.HY1 = Math.Max(mg.HY1, y + h);
            }
            else
            {
                if (n <= 0) return false;
                if (mg.AResX < 0) { mg.AResX = res; mg.ALayers = n; mg.AV = new Dictionary<int, byte[]>(); }
                else if (mg.AResX != res || mg.ALayers != n) return false;
                var planes = new byte[n][];
                for (int k = 0; k < n; k++) { planes[k] = pr.ReadBytes(count2); if (planes[k].Length != count2) return false; }
                var prev = new byte[n];
                int c = 0;
                for (int i = 0; i < h; i++)
                    for (int j = 0; j < w; j++)
                    {
                        if (!mask[i * w + j]) continue;
                        var v = new byte[n];
                        for (int k = 0; k < n; k++) { prev[k] = (byte)(prev[k] + planes[k][c]); v[k] = prev[k]; }
                        c++;
                        mg.AV[(y + i) * res + x + j] = v;
                    }
                mg.AX0 = Math.Min(mg.AX0, x); mg.AY0 = Math.Min(mg.AY0, y);
                mg.AX1 = Math.Max(mg.AX1, x + w); mg.AY1 = Math.Max(mg.AY1, y + h);
            }
            mg.Any = true;
            return true;
        }

        // A compacted queue can hold hours of digging on one tile; Steam drops reliable packets over 1 MB
        // (Transport.MaxReliable), so the merged state is split into messages of bounded size (row bands for
        // heights / splats, index runs for voxels). Values are absolute: the split result is the same terrain.
        private const int MergeChunkBytes = 256 * 1024; // raw payload per message, before deflate

        /// <summary>The merged state as messages in the live format (origin 0): heights, splats, voxels, each split to stay well below 1 MB.</summary>
        private static void EmitMerge(Merge mg, List<byte[]> into)
        {
            if (!mg.Any) return;
            if (mg.HRes > 0 && mg.HV.Count > 0)
                foreach (var band in Bands(mg.HV.Keys, mg.HRes, mg.HY0, mg.HY1, MergeChunkBytes / 2))
                    EmitHeights(mg, band.Key, band.Value, into);
            if (mg.AResX > 0 && mg.AV.Count > 0)
                foreach (var band in Bands(mg.AV.Keys, mg.AResX, mg.AY0, mg.AY1, Math.Max(1, MergeChunkBytes / Math.Max(1, mg.ALayers))))
                    EmitAlphas(mg, band.Key, band.Value, into);
            foreach (var kv in mg.V)
            {
                var idx = new List<int>(kv.Value.Value.Keys);
                idx.Sort();
                const int perMsg = MergeChunkBytes / 8; // f32 value + varint index (<= 4 bytes for these sizes)
                for (int start = 0; start < idx.Count; start += perMsg)
                {
                    int end = Math.Min(idx.Count, start + perMsg);
                    var body = new MemoryStream();
                    var bw = new BinaryWriter(body);
                    bw.Write(end - start);
                    int prev = 0;
                    for (int c = start; c < end; c++) { WriteVarint(body, idx[c] - prev); prev = idx[c]; }
                    for (int c = start; c < end; c++) bw.Write(kv.Value.Value[idx[c]]);
                    var ms = Net.Writer(Msg.TerrainHeights);
                    var w = new BinaryWriter(ms);
                    w.Write(VoxKind); w.Write(0UL); w.Write(0u);
                    w.Write(mg.Kx); w.Write(mg.Kz);
                    w.Write((byte)1);
                    w.Write(kv.Key);
                    w.Write(kv.Value.Key);
                    WritePayload(w, body);
                    into.Add(ms.ToArray());
                }
            }
        }

        /// <summary>Row bands [y0, y1) of a sparse cell set (key = y * res + x) holding at most maxCells cells each (a single row may exceed it).</summary>
        private static List<KeyValuePair<int, int>> Bands(IEnumerable<int> keys, int res, int y0, int y1, int maxCells)
        {
            var perRow = new int[Math.Max(0, y1 - y0)];
            foreach (int k in keys) { int row = k / res - y0; if (row >= 0 && row < perRow.Length) perRow[row]++; }
            var bands = new List<KeyValuePair<int, int>>();
            int start = y0, cells = 0;
            for (int row = y0; row < y1; row++)
            {
                int n = perRow[row - y0];
                if (cells > 0 && cells + n > maxCells) { bands.Add(new KeyValuePair<int, int>(start, row)); start = row; cells = 0; }
                cells += n;
            }
            if (cells > 0) bands.Add(new KeyValuePair<int, int>(start, y1));
            return bands;
        }

        private static void EmitHeights(Merge mg, int y0, int y1, List<byte[]> into)
        {
            int w = mg.HX1 - mg.HX0, h = y1 - y0;
            var mask = new bool[w * h];
            var vals = new List<ushort>();
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++)
                {
                    if (!mg.HV.TryGetValue((y0 + i) * mg.HRes + mg.HX0 + j, out ushort v)) continue;
                    mask[i * w + j] = true;
                    vals.Add(v);
                }
            if (vals.Count == 0) return;
            var body = new MemoryStream();
            WriteMask(body, mask, vals.Count);
            var lo = new byte[vals.Count];
            var hi = new byte[vals.Count];
            ushort prev = 0;
            for (int c = 0; c < vals.Count; c++)
            {
                ushort d = (ushort)(vals[c] - prev);
                prev = vals[c];
                lo[c] = (byte)d;
                hi[c] = (byte)(d >> 8);
            }
            body.Write(lo, 0, lo.Length);
            body.Write(hi, 0, hi.Length);
            var ms = Net.Writer(Msg.TerrainHeights);
            var bw = new BinaryWriter(ms);
            bw.Write(Version); bw.Write(0UL); bw.Write(0u);
            bw.Write(mg.Kx); bw.Write(mg.Kz); bw.Write(mg.HRes);
            bw.Write(mg.HX0); bw.Write(y0); bw.Write(w); bw.Write(h);
            WritePayload(bw, body);
            into.Add(ms.ToArray());
        }

        private static void EmitAlphas(Merge mg, int y0, int y1, List<byte[]> into)
        {
            int n = mg.ALayers, w = mg.AX1 - mg.AX0, h = y1 - y0;
            var mask = new bool[w * h];
            var cells = new List<byte[]>();
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++)
                    if (mg.AV.TryGetValue((y0 + i) * mg.AResX + mg.AX0 + j, out byte[] v)) { mask[i * w + j] = true; cells.Add(v); }
            if (cells.Count == 0) return;
            var body = new MemoryStream();
            WriteMask(body, mask, cells.Count);
            var plane = new byte[cells.Count];
            for (int k = 0; k < n; k++)
            {
                byte prev = 0;
                for (int c = 0; c < cells.Count; c++) { plane[c] = (byte)(cells[c][k] - prev); prev = cells[c][k]; }
                body.Write(plane, 0, plane.Length);
            }
            var ms = Net.Writer(Msg.TerrainAlphas);
            var bw = new BinaryWriter(ms);
            bw.Write(Version); bw.Write(0UL); bw.Write(0u);
            bw.Write(mg.Kx); bw.Write(mg.Kz); bw.Write(mg.AResX);
            bw.Write(mg.AX0); bw.Write(y0); bw.Write(w); bw.Write(h);
            bw.Write((byte)n);
            WritePayload(bw, body);
            into.Add(ms.ToArray());
        }

        /// <summary>
        /// Rewrites a tile's queue: between two parcel resets, all heights / splats / voxel messages become one
        /// message each with the newest value per cell (drill points and resets stay, in order). A message that
        /// does not fit the merge (other resolution / layer count / bad data) ends the run and stays as it is.
        /// </summary>
        private static void Compact(PendTile t)
        {
            int before = t.Msgs.Count;
            long bytesBefore = t.Bytes;
            var result = new List<byte[]>();
            try
            {
                var mg = new Merge { Kx = t.Kx, Kz = t.Kz };
                foreach (byte[] raw in t.Msgs)
                {
                    if (IsTileCommand(raw))
                    {
                        if (raw[1] == GoldSync.SubParcelReset) { EmitMerge(mg, result); mg = new Merge { Kx = t.Kx, Kz = t.Kz }; }
                        result.Add(raw);
                        continue;
                    }
                    bool fits;
                    try { fits = MergeIn(mg, raw); }
                    catch (Exception) { fits = false; }
                    if (fits) continue;
                    EmitMerge(mg, result);
                    mg = new Merge { Kx = t.Kx, Kz = t.Kz };
                    result.Add(raw);
                }
                EmitMerge(mg, result);
            }
            catch (Exception ex)
            {
                GRecv.Fail(ex); // keep the queue as it was
                t.BytesAtCompact = t.Bytes;
                return;
            }
            long bytes = 0;
            foreach (byte[] m in result) bytes += m.Length;
            t.Msgs.Clear();
            t.Msgs.AddRange(result);
            _pendingBytes += bytes - t.Bytes;
            t.Bytes = bytes;
            t.BytesAtCompact = bytes;
            CompactedTiles++;
            if (CompactedTiles <= 3 || CompactedTiles % 50 == 0)
                Plugin.Log.LogInfo($"Terrain sync: edits for tile ({t.Kx},{t.Kz}) (not loaded here) compacted: {before} -> {t.Msgs.Count} messages, {bytesBefore / 1024} -> {bytes / 1024} KB (compaction #{CompactedTiles})");
        }

        internal static int CompactedTiles; // stats (log lines)

        /// <summary>Host: its queued edits (tiles it has not loaded: not in its save) go to a joining player before the journal.</summary>
        private static int SendPendingTo(ulong peer)
        {
            int n = 0;
            foreach (PendTile t in Pending.Values)
            {
                if (t.Bytes - t.BytesAtCompact > 0 && t.Msgs.Count > 1) Compact(t);
                foreach (byte[] m in t.Msgs)
                {
                    Net.Transport.Send(peer, m, true);
                    TrafficC.Sent(TrafficC.Cat.Terrain, m.Length);
                    n++;
                }
            }
            return n;
        }

        // ---------- tick (called every frame) ----------

        internal static void Tick()
        {
            if (_lastTickFrame == Time.frameCount) return;
            _lastTickFrame = Time.frameCount;

            if (!Net.Active)
            {
                if (ByData.Count > 0 || Pending.Count > 0 || _world >= 0) ResetAll();
                return;
            }

            float now = Time.unscaledTime;
            try
            {
                CheckWorld();
                // Remote heights were written with SetHeightsDelayLOD: rebuild LOD/collision once per frame.
                if (LodPending.Count > 0)
                {
                    // The collider still has the old surface until SyncHeightmap: loose objects in the edited
                    // area are measured on it first, then lifted by what the ground rose under them (or woken
                    // where it dropped) before the next physics step. Otherwise another player's dig / dirt
                    // dump buried resting bodies, which then sank or fell through the terrain on this PC.
                    foreach (var kv in EditArea)
                        if (kv.Key.T != null && LodPending.Contains(kv.Key)) ObjectSync.TerrainEditBegin(kv.Value);
                    EditArea.Clear();
                    foreach (Info info in LodPending)
                        if (info.T != null && info.D != null) info.D.SyncHeightmap(); // = Terrain.ApplyDelayedHeightmapModification (obsolete name)
                    LodPending.Clear();
                    ObjectSync.TerrainEditEnd();
                }
                ReapplyRestored();
                if (now >= _nextFlush)
                {
                    _nextFlush = now + FlushInterval;
                    FlushAll();
                    // Edits for a tile that was not loaded apply as soon as it is (no fixed wait: a
                    // later restore of its saved state re-applies them, see ReapplyRestored).
                    if (Pending.Count > 0 && now >= _nextPendingRetry)
                    {
                        _nextPendingRetry = now + 0.25f;
                        Rebuild();
                        RetryPending();
                    }
                }
                if (now >= _nextHousekeeping)
                {
                    _nextHousekeeping = now + 1f;
                    Rebuild();
                }
            }
            catch (Exception ex)
            {
                LodPending.Clear();
                EditArea.Clear();
                ObjectSync.TerrainEditEnd();
                GTick.Fail(ex);
            }
        }

        private static void FlushAll(bool force = false)
        {
            if (ByData.Count == 0) return;
            bool synced = Synced();
            foreach (Info info in ByData.Values)
            {
                if (info.T == null) continue;
                if (!synced)
                {
                    info.H.Dirty = info.A.Dirty = false;
                    info.V = null; // a load rewrites the voxels: start over
                    continue;
                }
                try
                {
                    if (info.H.Dirty) FlushHeights(info);
                    if (info.A.Dirty) FlushAlphas(info);
                    if (info.V != null && info.V.Dirty && (force || Time.unscaledTime >= info.V.Next)) FlushVoxels(info);
                }
                catch (Exception ex)
                {
                    info.H.Dirty = info.A.Dirty = false;
                    GTick.Fail(ex);
                }
            }
        }

        /// <summary>
        /// A freshly loaded streamed tile got its saved heights/splats/voxels restored (a full-size
        /// write) after edits were applied to it: apply those edits again, in their original order.
        /// </summary>
        private static void ReapplyRestored()
        {
            if (ByData.Count == 0 || !Synced()) return;
            List<Info> todo = null;
            foreach (Info info in ByData.Values)
                if (info.ReapplyH || info.ReapplyA || info.ReapplyV) (todo ??= new List<Info>()).Add(info);
            if (todo == null) return;
            foreach (Info info in todo)
            {
                bool h = info.ReapplyH, a = info.ReapplyA, v = info.ReapplyV;
                info.ReapplyH = info.ReapplyA = info.ReapplyV = false;
                if (info.T == null || info.Recent.Count == 0) continue;
                int n = 0;
                bool rerunReset = false; // a parcel reset ran again: it cleared Recent, keep what follows it
                foreach (byte[] raw in info.Recent.ToArray())
                {
                    bool cmd = IsTileCommand(raw);
                    bool isReset = cmd && raw[1] == GoldSync.SubParcelReset;
                    bool isVox = !cmd && raw[1] == VoxKind;
                    bool isH = !cmd && !isVox && (Msg)raw[0] == Msg.TerrainHeights;
                    // reset: heights + splats + gold; drill point: same save entry as the voxels
                    bool due = isReset ? (h || a) : cmd || isVox ? v : isH ? h : a;
                    if (!due && !rerunReset) continue;
                    try
                    {
                        // after a re-run reset, edits are applied forced and kept again here (not inside TryApply)
                        if (ApplyCommandOrEdit(raw, isReset || (rerunReset && cmd))) n++;
                        if (isReset) rerunReset = true;
                        else if (rerunReset && !cmd) Remember(info, raw);
                    }
                    catch (Exception ex) { GRecv.Fail(ex); }
                }
                if (n > 0) Plugin.Log.LogInfo($"Terrain sync: tile ({info.Kx},{info.Kz}) restored its saved state, re-applied {n} edits");
            }
        }


        // ---------- shadows ----------

        private static int BlockKey(int bx, int by) => by * 4096 + bx;

        /// <summary>Captures the current (pre-write) values of every block the rect touches that has no shadow yet.</summary>
        private static void EnsureShadow(Info info, bool heights, int x, int y, int w, int h)
        {
            Map m = heights ? info.H : info.A;
            int resX = heights ? info.D.heightmapResolution : info.D.alphamapWidth;
            int resY = heights ? info.D.heightmapResolution : info.D.alphamapHeight;
            if (!heights && m.Layers != info.D.alphamapLayers)
            {
                m.Shadow.Clear();
                m.Layers = info.D.alphamapLayers;
            }
            if (!Clip(ref x, ref y, ref w, ref h, resX, resY)) return;
            int bx0 = x / Block, by0 = y / Block, bx1 = (x + w - 1) / Block, by1 = (y + h - 1) / Block;
            int need = 0;
            for (int by = by0; by <= by1; by++)
                for (int bx = bx0; bx <= bx1; bx++)
                    if (!m.Shadow.ContainsKey(BlockKey(bx, by))) need++;
            if (need == 0) return;
            if (info.H.Shadow.Count + info.A.Shadow.Count + need > MaxShadowBlocks)
            {
                // Bound memory: send what is pending, then start over (blocks are re-captured on demand).
                FlushAll();
                info.H.Shadow.Clear();
                info.A.Shadow.Clear();
            }
            for (int by = by0; by <= by1; by++)
                for (int bx = bx0; bx <= bx1; bx++)
                {
                    int key = BlockKey(bx, by);
                    if (m.Shadow.ContainsKey(key)) continue;
                    m.Shadow[key] = Capture(info, heights, bx, by, resX, resY);
                }
        }

        private static byte[] Capture(Info info, bool heights, int bx, int by, int resX, int resY)
        {
            int cx = bx * Block, cy = by * Block;
            int cw = Mathf.Min(Block, resX - cx), ch = Mathf.Min(Block, resY - cy);
            if (heights)
            {
                var block = new byte[Block * Block * 2];
                float[,] v = info.D.GetHeights(cx, cy, cw, ch);
                for (int i = 0; i < ch; i++)
                    for (int j = 0; j < cw; j++)
                        PutU16(block, i * Block + j, QH(v[i, j]));
                return block;
            }
            else
            {
                int n = info.A.Layers;
                var block = new byte[Block * Block * n];
                float[,,] v = info.D.GetAlphamaps(cx, cy, cw, ch);
                for (int i = 0; i < ch; i++)
                    for (int j = 0; j < cw; j++)
                        for (int k = 0; k < n; k++)
                            block[(i * Block + j) * n + k] = QA(v[i, j, k]);
                return block;
            }
        }

        /// <summary>A local write that is not sent: keep existing shadow blocks equal to what was written.</summary>
        private static void RefreshHeightShadow(Info info, int x, int y, float[,] v)
        {
            int h = v.GetLength(0), w = v.GetLength(1);
            foreach (var kv in info.H.Shadow)
            {
                int bx = kv.Key % 4096, by = kv.Key / 4096;
                int cx = bx * Block, cy = by * Block;
                int i0 = Mathf.Max(cy, y), i1 = Mathf.Min(cy + Block, y + h);
                int j0 = Mathf.Max(cx, x), j1 = Mathf.Min(cx + Block, x + w);
                for (int i = i0; i < i1; i++)
                    for (int j = j0; j < j1; j++)
                        PutU16(kv.Value, (i - cy) * Block + (j - cx), QH(v[i - y, j - x]));
            }
        }

        private static void RefreshAlphaShadow(Info info, int x, int y, float[,,] v)
        {
            int h = v.GetLength(0), w = v.GetLength(1), n = v.GetLength(2);
            if (n != info.A.Layers) { info.A.Shadow.Clear(); return; }
            foreach (var kv in info.A.Shadow)
            {
                int bx = kv.Key % 4096, by = kv.Key / 4096;
                int cx = bx * Block, cy = by * Block;
                int i0 = Mathf.Max(cy, y), i1 = Mathf.Min(cy + Block, y + h);
                int j0 = Mathf.Max(cx, x), j1 = Mathf.Min(cx + Block, x + w);
                for (int i = i0; i < i1; i++)
                    for (int j = j0; j < j1; j++)
                        for (int k = 0; k < n; k++)
                            kv.Value[((i - cy) * Block + (j - cx)) * n + k] = QA(v[i - y, j - x, k]);
            }
        }

        private static void DropShadow(Map m, int x, int y, int w, int h)
        {
            if (m.Shadow.Count == 0) return;
            List<int> dead = null;
            foreach (int key in m.Shadow.Keys)
            {
                int cx = key % 4096 * Block, cy = key / 4096 * Block;
                if (cx < x + w && cx + Block > x && cy < y + h && cy + Block > y) (dead ??= new List<int>()).Add(key);
            }
            if (dead != null) foreach (int key in dead) m.Shadow.Remove(key);
        }

        private static void MarkDirty(Info info, Map m, bool heights, int x, int y, int w, int h)
        {
            if (m.Dirty)
            {
                int ux0 = Mathf.Min(m.X0, x), uy0 = Mathf.Min(m.Y0, y);
                int ux1 = Mathf.Max(m.X1, x + w), uy1 = Mathf.Max(m.Y1, y + h);
                long union = (long)(ux1 - ux0) * (uy1 - uy0);
                long parts = (long)(m.X1 - m.X0) * (m.Y1 - m.Y0) + (long)w * h;
                if (union <= MaxDirtyCells || union <= 2 * parts)
                {
                    m.X0 = ux0; m.Y0 = uy0; m.X1 = ux1; m.Y1 = uy1;
                    return;
                }
                // Two far apart edits: send the old one now instead of reading a huge mostly-unchanged box.
                if (heights) FlushHeights(info); else FlushAlphas(info);
            }
            m.Dirty = true;
            m.X0 = x; m.Y0 = y; m.X1 = x + w; m.Y1 = y + h;
        }

        // ---------- send ----------

        private static void FlushHeights(Info info)
        {
            Map m = info.H;
            m.Dirty = false;
            int res = info.D.heightmapResolution;
            int x = m.X0, y = m.Y0, w = m.X1 - m.X0, h = m.Y1 - m.Y0;
            if (!Clip(ref x, ref y, ref w, ref h, res, res)) return;
            // Shadow blocks normally exist (captured right before the write). A missing one (memory
            // limit reset) counts as all changed and is captured after sending.
            HashSet<int> missing = null;

            float[,] cur = info.D.GetHeights(x, y, w, h);
            var sentCells = new List<int>();
            var q = new ushort[w * h];
            var changed = new bool[w * h];
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            byte[] block = null;
            int blockKey = -1;
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++)
                {
                    int gx = x + j, gy = y + i;
                    int key = BlockKey(gx / Block, gy / Block);
                    if (key != blockKey)
                    {
                        blockKey = key;
                        if (!m.Shadow.TryGetValue(key, out block)) (missing ??= new HashSet<int>()).Add(key);
                    }
                    ushort v = QH(cur[i, j]);
                    int idx = i * w + j;
                    q[idx] = v;
                    int si = (gy % Block) * Block + gx % Block;
                    if (block != null && GetU16(block, si) == v) continue;
                    changed[idx] = true;
                    sentCells.Add(gy * res + gx);
                    if (block != null) PutU16(block, si, v);
                    if (j < minX) minX = j;
                    if (j > maxX) maxX = j;
                    if (i < minY) minY = i;
                    if (i > maxY) maxY = i;
                }
            if (maxX < 0) return;

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            var cells = new List<ushort>(bw * bh);
            var mask = new bool[bw * bh];
            for (int i = 0; i < bh; i++)
                for (int j = 0; j < bw; j++)
                {
                    int idx = (i + minY) * w + j + minX;
                    if (!changed[idx]) continue;
                    mask[i * bw + j] = true;
                    cells.Add(q[idx]);
                }

            var body = new MemoryStream();
            WriteMask(body, mask, cells.Count);
            ushort prev = 0;
            var lo = new byte[cells.Count];
            var hi = new byte[cells.Count];
            for (int c = 0; c < cells.Count; c++)
            {
                ushort d = (ushort)(cells[c] - prev);
                prev = cells[c];
                lo[c] = (byte)d;
                hi[c] = (byte)(d >> 8);
            }
            body.Write(lo, 0, lo.Length);
            body.Write(hi, 0, hi.Length);

            uint seq = NextSeq();
            TrackUnacked(m.Unacked, seq, sentCells);
            var ms = Net.Writer(Msg.TerrainHeights);
            var bwr = new BinaryWriter(ms);
            bwr.Write(Version);
            bwr.Write(Net.LocalId); bwr.Write(seq);
            bwr.Write(info.Kx); bwr.Write(info.Kz); bwr.Write(res);
            bwr.Write(x + minX); bwr.Write(y + minY); bwr.Write(bw); bwr.Write(bh);
            WritePayload(bwr, body);
            Send(ms.ToArray(), cells.Count, info);
            CaptureMissing(info, true, missing, res, res);
        }

        private static void FlushAlphas(Info info)
        {
            Map m = info.A;
            m.Dirty = false;
            int resX = info.D.alphamapWidth, resY = info.D.alphamapHeight;
            int x = m.X0, y = m.Y0, w = m.X1 - m.X0, h = m.Y1 - m.Y0;
            if (!Clip(ref x, ref y, ref w, ref h, resX, resY)) return;
            if (m.Layers != info.D.alphamapLayers)
            {
                m.Shadow.Clear();
                m.Layers = info.D.alphamapLayers;
            }
            HashSet<int> missing = null;
            int n = m.Layers;
            if (n <= 0 || n > 255) return;

            float[,,] cur = info.D.GetAlphamaps(x, y, w, h);
            var sentCells = new List<int>();
            var q = new byte[w * h * n];
            var changed = new bool[w * h];
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            byte[] block = null;
            int blockKey = -1;
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++)
                {
                    int gx = x + j, gy = y + i;
                    int key = BlockKey(gx / Block, gy / Block);
                    if (key != blockKey)
                    {
                        blockKey = key;
                        if (!m.Shadow.TryGetValue(key, out block)) (missing ??= new HashSet<int>()).Add(key);
                    }
                    int idx = i * w + j;
                    int si = ((gy % Block) * Block + gx % Block) * n;
                    bool diff = block == null;
                    for (int k = 0; k < n; k++)
                    {
                        byte v = QA(cur[i, j, k]);
                        q[idx * n + k] = v;
                        if (block != null && block[si + k] != v) { diff = true; block[si + k] = v; }
                    }
                    if (!diff) continue;
                    changed[idx] = true;
                    sentCells.Add(gy * resX + gx);
                    if (j < minX) minX = j;
                    if (j > maxX) maxX = j;
                    if (i < minY) minY = i;
                    if (i > maxY) maxY = i;
                }
            if (maxX < 0) return;

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            var mask = new bool[bw * bh];
            var idxs = new List<int>(bw * bh);
            for (int i = 0; i < bh; i++)
                for (int j = 0; j < bw; j++)
                {
                    int idx = (i + minY) * w + j + minX;
                    if (!changed[idx]) continue;
                    mask[i * bw + j] = true;
                    idxs.Add(idx);
                }

            var body = new MemoryStream();
            WriteMask(body, mask, idxs.Count);
            var plane = new byte[idxs.Count];
            for (int k = 0; k < n; k++)
            {
                byte prev = 0;
                for (int c = 0; c < idxs.Count; c++)
                {
                    byte v = q[idxs[c] * n + k];
                    plane[c] = (byte)(v - prev);
                    prev = v;
                }
                body.Write(plane, 0, plane.Length);
            }

            uint seq = NextSeq();
            TrackUnacked(m.Unacked, seq, sentCells);
            var ms = Net.Writer(Msg.TerrainAlphas);
            var bwr = new BinaryWriter(ms);
            bwr.Write(Version);
            bwr.Write(Net.LocalId); bwr.Write(seq);
            bwr.Write(info.Kx); bwr.Write(info.Kz); bwr.Write(resX);
            bwr.Write(x + minX); bwr.Write(y + minY); bwr.Write(bw); bwr.Write(bh);
            bwr.Write((byte)n);
            WritePayload(bwr, body);
            Send(ms.ToArray(), idxs.Count, info);
            CaptureMissing(info, false, missing, resX, resY);
        }

        private static void CaptureMissing(Info info, bool heights, HashSet<int> missing, int resX, int resY)
        {
            if (missing == null || info.H.Shadow.Count + info.A.Shadow.Count + missing.Count > MaxShadowBlocks) return;
            Map m = heights ? info.H : info.A;
            foreach (int key in missing) m.Shadow[key] = Capture(info, heights, key % 4096, key / 4096, resX, resY);
        }

        private static void Send(byte[] msg, int cells, Info info)
        {
            TrafficC.TerrainCells += cells; // stats: one line per 5 min for all player & world traffic (TrafficC)
            Record(msg);
            Remember(info, msg);
            TrafficC.Broadcast(TrafficC.Cat.Terrain, msg, true);
        }

        /// <summary>Keeps edits of a freshly loaded tile: a restore of its saved state would wipe them.</summary>
        private static void Remember(Info info, byte[] msg)
        {
            if (info == null || Time.unscaledTime - info.FirstSeen > RecentWindow)
            {
                if (info != null && info.Recent.Count > 0) { info.Recent.Clear(); info.RecentBytes = 0; }
                return;
            }
            if (info.RecentBytes + msg.Length > RecentMaxBytes) return;
            info.Recent.Add(msg);
            info.RecentBytes += msg.Length;
        }

        private static void WriteMask(MemoryStream body, bool[] mask, int count)
        {
            if (count == mask.Length)
            {
                body.WriteByte(1);
                return;
            }
            body.WriteByte(0);
            var bits = new byte[(mask.Length + 7) / 8];
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) bits[i >> 3] |= (byte)(1 << (i & 7));
            body.Write(bits, 0, bits.Length);
        }

        private static bool[] ReadMask(BinaryReader r, int cells)
        {
            var mask = new bool[cells];
            if (r.ReadByte() == 1)
            {
                for (int i = 0; i < cells; i++) mask[i] = true;
                return mask;
            }
            byte[] bits = r.ReadBytes((cells + 7) / 8);
            for (int i = 0; i < cells; i++) mask[i] = (bits[i >> 3] & (1 << (i & 7))) != 0;
            return mask;
        }

        // ---------- compression ----------

        private static bool? _deflateOk;

        private static void WritePayload(BinaryWriter w, MemoryStream body)
        {
            byte[] raw = body.ToArray();
            byte[] packed = raw.Length >= 64 && DeflateWorks() ? Deflate(raw) : null;
            bool useDeflate = packed != null && packed.Length < raw.Length;
            w.Write((byte)(useDeflate ? 1 : 0));
            w.Write(raw.Length);
            byte[] data = useDeflate ? packed : raw;
            w.Write(data.Length);
            w.Write(data);
        }

        private static byte[] ReadPayload(BinaryReader r)
        {
            byte codec = r.ReadByte();
            int rawLen = r.ReadInt32();
            int len = r.ReadInt32();
            if (rawLen < 0 || len < 0 || rawLen > 64 * 1024 * 1024) throw new InvalidDataException("bad terrain payload size");
            byte[] data = r.ReadBytes(len);
            if (codec == 0) return data;
            if (codec != 1) throw new InvalidDataException("unknown terrain codec " + codec);
            var outBuf = new byte[rawLen];
            using (var ds = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress))
            {
                int got = 0;
                while (got < rawLen)
                {
                    int n = ds.Read(outBuf, got, rawLen - got);
                    if (n <= 0) throw new EndOfStreamException("truncated terrain payload");
                    got += n;
                }
            }
            return outBuf;
        }

        private static byte[] Deflate(byte[] raw)
        {
            try
            {
                var ms = new MemoryStream();
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true)) ds.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                _deflateOk = false;
                Plugin.Log.LogWarning("Terrain sync: deflate failed, sending uncompressed: " + ex.Message);
                return null;
            }
        }

        /// <summary>DeflateStream needs MonoPosixHelper (zlib) in Unity's Mono; test it once.</summary>
        private static bool DeflateWorks()
        {
            if (_deflateOk.HasValue) return _deflateOk.Value;
            try
            {
                var probe = new byte[256];
                for (int i = 0; i < probe.Length; i++) probe[i] = (byte)(i % 7);
                var ms = new MemoryStream();
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true)) ds.Write(probe, 0, probe.Length);
                var back = new byte[probe.Length];
                using (var ds = new DeflateStream(new MemoryStream(ms.ToArray()), CompressionMode.Decompress))
                {
                    int got = 0;
                    while (got < back.Length) { int n = ds.Read(back, got, back.Length - got); if (n <= 0) break; got += n; }
                }
                for (int i = 0; i < probe.Length; i++)
                    if (back[i] != probe[i]) throw new InvalidDataException("round trip mismatch");
                _deflateOk = true;
            }
            catch (Exception ex)
            {
                _deflateOk = false;
                Plugin.Log.LogWarning("Terrain sync: deflate not available, sending uncompressed: " + ex.Message);
            }
            return _deflateOk.Value;
        }

        // ---------- receive ----------

        /// <param name="raw">Whole message (type byte included), recorded in the host journal.</param>
        internal static void ReceiveHeights(BinaryReader r, byte[] raw)
        {
            TrafficC.Recv(TrafficC.Cat.Terrain, raw.Length);
            SafeReceive(raw);
        }

        internal static void ReceiveAlphas(BinaryReader r, byte[] raw)
        {
            TrafficC.Recv(TrafficC.Cat.Terrain, raw.Length);
            SafeReceive(raw);
        }

        /// <summary>A bad message is logged and dropped here: the host's Net layer still relays it.</summary>
        private static void SafeReceive(byte[] raw)
        {
            try
            {
                if (Net.Active) CheckWorld(); // a message of this frame must not land in the old world's queues
                Receive(raw);
            }
            catch (Exception ex) { GRecv.Fail(ex); }
        }

        private static void Receive(byte[] raw)
        {
            if (raw.Length >= 2 && raw[1] == AckKind)
            {
                if (raw.Length < 14 || Net.IsHost) return;
                if (BitConverter.ToUInt64(raw, 2) == Net.LocalId) OnAck(BitConverter.ToUInt32(raw, 10));
                return;
            }
            if (raw.Length < 2 || (raw[1] != Version && raw[1] != VoxKind))
            {
                if (!_loggedVersion)
                {
                    _loggedVersion = true;
                    Plugin.Log.LogWarning("Terrain sync: ignoring terrain data from a different mod version");
                }
                return;
            }
            if (raw.Length < KeyOffset + 8) return;
            // Host = sequencer: acknowledge right away (Net relays a client's message to the others before this
            // handler runs; the ack goes to everybody, only the sender uses it), so the sender knows which
            // of its cells the host has ordered (see "host ordering").
            ulong origin = BitConverter.ToUInt64(raw, 2);
            if (Net.IsHost && origin != Net.LocalId) SendAck(origin, BitConverter.ToUInt32(raw, 10));
            Record(raw);
            // A client still loading the host world ignores live edits: the host replays its journal
            // (which contains them) once the world is loaded.
            if (!SaveTransfer.WorldReady) return;
            // Keep order per tile: while older edits for this tile wait, newer ones wait behind them.
            if (TileOf(raw, out int pkx, out int pkz) && HasPendingFor(pkx, pkz)) { Pend(raw); return; }
            if (!TryApply(raw, false, out bool waiting) && waiting) Pend(raw);
        }

        // Offset of the tile key (x i32, z i32) in every message: type, kind/version, origin, seq.
        private const int KeyOffset = 14;

        /// <summary>
        /// Applies a terrain message. waiting = the target tile is not loaded here (yet).
        /// force = re-apply after a restore of the tile's saved state (no "own edit wins" checks).
        /// </summary>
        private static bool TryApply(byte[] raw, bool force, out bool waiting)
        {
            waiting = false;
            if (raw[1] == VoxKind) return TryApplyVoxels(raw, force, out waiting);
            var r = new BinaryReader(new MemoryStream(raw, 2, raw.Length - 2));
            bool heights = (Msg)raw[0] == Msg.TerrainHeights;
            ulong origin = r.ReadUInt64();
            r.ReadUInt32(); // seq
            int kx = r.ReadInt32(), kz = r.ReadInt32(), res = r.ReadInt32();
            int x = r.ReadInt32(), y = r.ReadInt32(), w = r.ReadInt32(), h = r.ReadInt32();
            int n = heights ? 1 : r.ReadByte();

            Info info = Get(kx, kz);
            if (info == null || GameState.IsLoading)
            {
                waiting = true;
                return false;
            }

            TerrainData d = info.D;
            int resX = heights ? d.heightmapResolution : d.alphamapWidth;
            int resY = heights ? d.heightmapResolution : d.alphamapHeight;
            if (res != resX || w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > resX || y + h > resY) return false;
            if (!heights && n != d.alphamapLayers) return false;

            byte[] body = ReadPayload(r);
            var br = new BinaryReader(new MemoryStream(body));
            bool[] mask = ReadMask(br, w * h);
            int count = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i]) count++;
            bool own = origin == Net.LocalId; // journal / restore re-apply of our own edit
            bool check = !force && !own;
            Map m = heights ? info.H : info.A;
            int skipped = 0;

            _applying = true;
            try
            {
                if (heights)
                {
                    byte[] lo = br.ReadBytes(count), hi = br.ReadBytes(count);
                    if (hi.Length != count) return false;
                    float[,] arr = d.GetHeights(x, y, w, h);
                    bool zones = check && CollectNoDigZones(info, x, y, w, h);
                    Vector3 to = info.T.GetPosition();
                    float csx = d.size.x / (resX - 1), csz = d.size.z / (resX - 1);
                    ushort prev = 0;
                    int c = 0;
                    for (int i = 0; i < h; i++)
                        for (int j = 0; j < w; j++)
                        {
                            if (!mask[i * w + j]) continue;
                            prev = (ushort)(prev + (lo[c] | hi[c] << 8));
                            c++;
                            if (check && KeepLocal(m, true, 1, x + j, y + i, resX, arr, null, i, j))
                            {
                                mask[i * w + j] = false;
                                skipped++;
                                continue;
                            }
                            float nv = prev / 65535f;
                            // Never raise the ground under a machine / trailer here (the game's own rule for local
                            // dumps, CantDigUnderMachine): another player's dump, made where that machine stood a bit
                            // elsewhere on their PC (or with a tool that ignores the rule), buried parked machines.
                            if (zones && nv > arr[i, j] + 1e-5f && InNoDigZone(to.x + (x + j) * csx, to.z + (y + i) * csz))
                            {
                                mask[i * w + j] = false;
                                skipped++;
                                continue;
                            }
                            arr[i, j] = nv;
                        }
                    _noDig.Clear();
                    if (skipped < count)
                    {
                        d.SetHeightsDelayLOD(x, y, arr);
                        LodPending.Add(info);
                        AddEditArea(info, x, y, w, h, arr);
                        UpdateShadow(info.H, true, 1, x, y, w, h, mask, arr, null);
                    }
                    if (!_loggedFirstHeights)
                    {
                        _loggedFirstHeights = true;
                        Plugin.Log.LogInfo($"Terrain sync: first remote dig applied on terrain ({kx},{kz}) at ({x},{y}) size {w}x{h}, {count} cells");
                    }
                }
                else
                {
                    var planes = new byte[n][];
                    for (int k = 0; k < n; k++)
                    {
                        planes[k] = br.ReadBytes(count);
                        if (planes[k].Length != count) return false;
                    }
                    float[,,] arr = d.GetAlphamaps(x, y, w, h);
                    var prev = new byte[n];
                    int c = 0;
                    for (int i = 0; i < h; i++)
                        for (int j = 0; j < w; j++)
                        {
                            if (!mask[i * w + j]) continue;
                            for (int k = 0; k < n; k++) prev[k] = (byte)(prev[k] + planes[k][c]);
                            c++;
                            if (check && KeepLocal(m, false, n, x + j, y + i, resX, null, arr, i, j))
                            {
                                mask[i * w + j] = false;
                                skipped++;
                                continue;
                            }
                            for (int k = 0; k < n; k++) arr[i, j, k] = prev[k] / 255f;
                        }
                    if (skipped < count)
                    {
                        d.SetAlphamaps(x, y, arr);
                        if (info.A.Layers == n) UpdateShadow(info.A, false, n, x, y, w, h, mask, null, arr);
                    }
                }
            }
            finally { _applying = false; }
            if (!force) Remember(info, raw);
            return true;
        }

        /// <summary>
        /// Host ordering rule: keep this PC's value for a cell it changed and has not sent yet (current
        /// differs from the shadow) or sent and the host has not acknowledged yet (clients only).
        /// </summary>
        private static bool KeepLocal(Map m, bool heights, int n, int gx, int gy, int resX, float[,] hv, float[,,] av, int i, int j)
        {
            if (m.Unacked.Count > 0 && m.Unacked.ContainsKey(gy * resX + gx)) return true;
            if (m.Shadow.Count == 0 || !m.Shadow.TryGetValue(BlockKey(gx / Block, gy / Block), out byte[] block)) return false;
            int si = (gy % Block) * Block + gx % Block;
            if (heights) return AckTracker.KeepLocal(false, true, GetU16(block, si), QH(hv[i, j]));
            if (m.Layers != n) return false;
            for (int k = 0; k < n; k++) if (block[si * n + k] != QA(av[i, j, k])) return true;
            return false;
        }

        /// <summary>Received cells are now what everybody has: write them into existing shadow blocks.</summary>
        private static void UpdateShadow(Map m, bool heights, int n, int x, int y, int w, int h, bool[] mask, float[,] hv, float[,,] av)
        {
            if (m.Shadow.Count == 0) return;
            byte[] block = null;
            int blockKey = -1;
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++)
                {
                    if (!mask[i * w + j]) continue;
                    int gx = x + j, gy = y + i;
                    int key = BlockKey(gx / Block, gy / Block);
                    if (key != blockKey) { blockKey = key; m.Shadow.TryGetValue(key, out block); }
                    if (block == null) continue;
                    int si = (gy % Block) * Block + gx % Block;
                    if (heights) PutU16(block, si, QH(hv[i, j]));
                    else for (int k = 0; k < n; k++) block[si * n + k] = QA(av[i, j, k]);
                }
        }

        // ---------- helpers ----------

        private static ushort QH(float v) => (ushort)Mathf.Clamp(Mathf.RoundToInt(v * 65535f), 0, 65535);
        private static byte QA(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
        private static void PutU16(byte[] b, int i, ushort v) { b[i * 2] = (byte)v; b[i * 2 + 1] = (byte)(v >> 8); }
        private static ushort GetU16(byte[] b, int i) => (ushort)(b[i * 2] | b[i * 2 + 1] << 8);

        private static bool Clip(ref int x, ref int y, ref int w, ref int h, int resX, int resY)
        {
            if (x < 0) { w += x; x = 0; }
            if (y < 0) { h += y; y = 0; }
            if (x + w > resX) w = resX - x;
            if (y + h > resY) h = resY - y;
            return w > 0 && h > 0;
        }

        // ---------- gold / resource voxels ----------
        //
        // GoldTerrainDigger keeps the gold under the ground as a voxel volume (GeologicalGoldData,
        // 100 x 40 x 100 per tile) plus one volume per other resource (_resources[type].Volume). Digging
        // takes gold out of it, dumped dirt puts gold back. Each PC used to do this on its own copy:
        // the same spot gave its gold once on every PC. Now every voxel write is synced like heights:
        // a Harmony prefix on every GoldTerrainDigger method that changes voxels captures a shadow copy
        // of the volumes (once, lazily) and marks the tile dirty; every 0.1 s the volumes are compared
        // with the shadow and the changed voxels (index, absolute value) are sent, deflated, in a
        // TerrainHeights message with kind VoxKind. Same host ordering, journal and restore re-apply as
        // heights. A machine this PC does not drive does not dig at all (ContainerSync), so a dig's gold
        // is taken by the PC whose player dug, exactly once.
        //
        // Message: type 3, VoxKind, origin u64, seq u32, key x i32, key z i32, arrays u8, then per
        // array: id u8 (0 = GeologicalGoldData, 1 + ResourceType = resource volume, 0xFF = soft-dirt floor
        // float[,] of the heightmap), length i32,
        // payload (codec as heights) = count i32, count x index delta (7-bit varint), count x f32.

        private sealed class Vox
        {
            public float[][] Shadow;
            public Array[] Src;
            public byte[] Ids;
            public Dictionary<int, uint>[] Unacked;
            public bool Dirty;
            public float Next; // voxels are compared at most every VoxInterval (a full compare is ~6 MB)
        }

        private const float VoxInterval = 0.5f;

        private static readonly AccessTools.FieldRef<GoldTerrainDigger, float[,,]> GoldRef =
            TryRef<GoldTerrainDigger, float[,,]>("GeologicalGoldData");
        private static readonly AccessTools.FieldRef<GoldTerrainDigger, Dictionary<ResourceType, ResourceField>> ResRef =
            TryRef<GoldTerrainDigger, Dictionary<ResourceType, ResourceField>>("_resources");
        private static readonly AccessTools.FieldRef<GoldTerrainDigger, bool> RefreshMeshRef =
            TryRef<GoldTerrainDigger, bool>("_refreshGoldMesh");
        // Deepest dug level per heightmap cell (UpdateSoftDirt in ComputeNewHeights2 / TryPush /
        // ShovelAddHeights): machines with DigOnlySoft can dig refilled dirt down to it. Saved with the tile
        // ("SoftDirt"), so it is synced like a voxel volume: a hole dug and refilled by one player must be
        // diggable by the others' soft-only machines, and the host's save must contain the clients' digs.
        private const byte SoftDirtId = 0xFF;
        private static readonly AccessTools.FieldRef<GoldTerrainDigger, float[,]> SoftDirtRef =
            TryRef<GoldTerrainDigger, float[,]>("_softDirt");

        private static AccessTools.FieldRef<T, F> TryRef<T, F>(string field)
        {
            try { return AccessTools.FieldRefAccess<T, F>(field); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Terrain sync: field {typeof(T).Name}.{field} not found ({ex.Message})");
                return null;
            }
        }

        private static void VoxArrays(GoldTerrainDigger g, List<byte> ids, List<Array> arrays)
        {
            if (GoldRef != null && GoldRef(g) is float[,,] gold) { ids.Add(0); arrays.Add(gold); }
            if (SoftDirtRef != null && SoftDirtRef(g) is float[,] soft) { ids.Add(SoftDirtId); arrays.Add(soft); }
            if (ResRef == null) return;
            Dictionary<ResourceType, ResourceField> res = ResRef(g);
            if (res == null) return;
            foreach (ResourceType t in Enum.GetValues(typeof(ResourceType)))
                if (res.TryGetValue(t, out ResourceField f) && f != null && f.Volume != null)
                {
                    ids.Add((byte)(1 + (int)t));
                    arrays.Add(f.Volume);
                }
        }

        private static Array VoxArray(GoldTerrainDigger g, byte id)
        {
            if (id == 0) return GoldRef != null ? GoldRef(g) : null;
            if (id == SoftDirtId) return SoftDirtRef != null ? SoftDirtRef(g) : null;
            if (ResRef == null) return null;
            Dictionary<ResourceType, ResourceField> res = ResRef(g);
            return res != null && res.TryGetValue((ResourceType)(id - 1), out ResourceField f) && f != null ? f.Volume : null;
        }

        private static Info InfoOf(GoldTerrainDigger g)
        {
            if (g == null) return null;
            Terrain t = g.GetComponent<Terrain>();
            return t != null ? Get(t.terrainData) : null;
        }

        /// <summary>Prefix of every voxel-writing method: shadow before the first local change, mark dirty.</summary>
        private static void VoxWrite(GoldTerrainDigger g)
        {
            if (_applying || _parcelReset > 0 || _suppress > 0 || !Synced()) return;
            Info info = InfoOf(g);
            if (info == null) return;
            if (info.V != null)
            {
                // Common path (every dig frame): no allocation.
                bool same = true;
                for (int i = 0; i < info.V.Src.Length && same; i++) same = ReferenceEquals(info.V.Src[i], VoxArray(g, info.V.Ids[i]));
                if (same) { info.V.Dirty = true; return; }
                FlushVoxels(info); // arrays were replaced (load): send what is pending first
            }
            var ids = new List<byte>();
            var arrays = new List<Array>();
            VoxArrays(g, ids, arrays);
            if (arrays.Count == 0) return;
            var v = new Vox
            {
                Ids = ids.ToArray(),
                Src = arrays.ToArray(),
                Shadow = new float[arrays.Count][],
                Unacked = new Dictionary<int, uint>[arrays.Count],
                Dirty = true,
            };
            for (int i = 0; i < arrays.Count; i++)
            {
                v.Shadow[i] = new float[arrays[i].Length];
                Buffer.BlockCopy(arrays[i], 0, v.Shadow[i], 0, arrays[i].Length * 4);
                v.Unacked[i] = new Dictionary<int, uint>();
            }
            info.V = v;
        }

        private static readonly List<int> VoxIdx = new List<int>();

        /// <summary>
        /// Changed voxels (indices) of a float array against its shadow; updates the shadow. Compares the
        /// game's array in place (pinned): no 6 MB copy per tile every 0.5 s.
        /// </summary>
        private static unsafe List<int> CompareVoxels(Array src, float[] sh, int len)
        {
            VoxIdx.Clear();
            GCHandle h = GCHandle.Alloc(src, GCHandleType.Pinned);
            try
            {
                float* p = (float*)h.AddrOfPinnedObject();
                fixed (float* q = sh)
                {
                    for (int i = 0; i < len; i++)
                    {
                        float x = p[i];
                        if (x == q[i]) continue;
                        q[i] = x;
                        VoxIdx.Add(i);
                    }
                }
            }
            finally { h.Free(); }
            return VoxIdx;
        }

        private static void FlushVoxels(Info info)
        {
            Vox v = info.V;
            if (v == null) return;
            v.Dirty = false;
            v.Next = Time.unscaledTime + VoxInterval;
            GoldTerrainDigger g = info.T != null ? info.T.GetComponent<GoldTerrainDigger>() : null;
            if (g == null) { info.V = null; return; }
            var ms = Net.Writer(Msg.TerrainHeights);
            var w = new BinaryWriter(ms);
            uint seq = NextSeq();
            w.Write(VoxKind);
            w.Write(Net.LocalId); w.Write(seq);
            w.Write(info.Kx); w.Write(info.Kz);
            long countPos = ms.Position;
            w.Write((byte)0);
            int arraysSent = 0, total = 0;
            for (int a = 0; a < v.Src.Length; a++)
            {
                Array src = VoxArray(g, v.Ids[a]);
                if (!ReferenceEquals(src, v.Src[a]))
                {
                    info.V = null; // replaced by a load: the shadow is meaningless now
                    return;
                }
                int len = src.Length;
                float[] sh = v.Shadow[a];
                if (sh.Length != len) { info.V = null; return; }
                List<int> idx = CompareVoxels(src, sh, len); // the shadow now holds the new values
                if (idx.Count == 0) continue;
                var body = new MemoryStream();
                var bw = new BinaryWriter(body);
                bw.Write(idx.Count);
                int prev = 0;
                foreach (int i in idx) { WriteVarint(body, i - prev); prev = i; }
                foreach (int i in idx) bw.Write(sh[i]);
                w.Write(v.Ids[a]);
                w.Write(len);
                WritePayload(w, body);
                TrackUnacked(v.Unacked[a], seq, idx);
                arraysSent++;
                total += idx.Count;
            }
            if (arraysSent == 0) return;
            byte[] msg = ms.ToArray();
            msg[countPos] = (byte)arraysSent;
            Send(msg, total, info);
        }

        private static bool TryApplyVoxels(byte[] raw, bool force, out bool waiting)
        {
            waiting = false;
            var r = new BinaryReader(new MemoryStream(raw, 2, raw.Length - 2));
            ulong origin = r.ReadUInt64();
            r.ReadUInt32();
            int kx = r.ReadInt32(), kz = r.ReadInt32();
            int arrays = r.ReadByte();
            Info info = Get(kx, kz);
            if (info == null || GameState.IsLoading) { waiting = true; return false; }
            GoldTerrainDigger g = info.T.GetComponent<GoldTerrainDigger>();
            if (g == null) return false;
            bool check = !force && origin != Net.LocalId;
            Vox v = info.V;
            var one = new float[1];
            for (int a = 0; a < arrays; a++)
            {
                byte id = r.ReadByte();
                int len = r.ReadInt32();
                byte[] body = ReadPayload(r);
                Array dst = VoxArray(g, id);
                if (dst == null || dst.Length != len) continue;
                int slot = -1;
                if (v != null)
                    for (int i = 0; i < v.Ids.Length; i++)
                        if (v.Ids[i] == id && ReferenceEquals(v.Src[i], dst)) { slot = i; break; }
                var ms = new MemoryStream(body);
                var br = new BinaryReader(ms);
                int count = br.ReadInt32();
                if (count < 0 || count > len) continue;
                var idx = new int[count];
                int prev = 0;
                for (int c = 0; c < count; c++) { prev += ReadVarint(ms); idx[c] = prev; }
                for (int c = 0; c < count; c++)
                {
                    float val = br.ReadSingle();
                    int i = idx[c];
                    if (i < 0 || i >= len) continue;
                    if (slot >= 0)
                    {
                        if (check)
                        {
                            if (v.Unacked[slot].Count > 0 && v.Unacked[slot].ContainsKey(i)) continue;
                            Buffer.BlockCopy(dst, i * 4, one, 0, 4);
                            if (one[0] != v.Shadow[slot][i]) continue; // changed here, not sent yet
                        }
                        v.Shadow[slot][i] = val;
                    }
                    one[0] = val;
                    Buffer.BlockCopy(one, 0, dst, i * 4, 4);
                }
            }
            if (RefreshMeshRef != null) RefreshMeshRef(g) = true; // gold areas display
            if (!force) Remember(info, raw);
            return true;
        }

        private static void WriteVarint(Stream s, int v)
        {
            uint u = (uint)v;
            while (u >= 0x80) { s.WriteByte((byte)(u | 0x80)); u >>= 7; }
            s.WriteByte((byte)u);
        }

        private static int ReadVarint(Stream s)
        {
            uint result = 0;
            int shift = 0;
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) throw new EndOfStreamException("truncated voxel payload");
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return (int)result;
                shift += 7;
                if (shift > 28) throw new InvalidDataException("bad varint");
            }
        }

        [HarmonyPatch]
        private static class PatchVoxelWrites
        {
            private static readonly string[] Names =
            {
                "DigWithBox", "TryMove", "DigGold", "DigGoldUp", "AddGold", "AddDensity", "AddVolumeDirt",
                "ComputeNewHeights2", "TryPush", "TakeGold", "AddGoldSave", "DigResource",
            };

            private static IEnumerable<MethodBase> TargetMethods()
            {
                var list = new List<MethodBase>();
                foreach (MethodInfo m in typeof(GoldTerrainDigger).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (Array.IndexOf(Names, m.Name) >= 0 && !m.IsAbstract && !m.IsGenericMethodDefinition) list.Add(m);
                return list;
            }

            private static bool Prepare()
            {
                foreach (var _ in TargetMethods()) return true;
                Plugin.Log.LogWarning("Terrain sync: no GoldTerrainDigger voxel methods found, gold voxels not synced");
                return false;
            }

            private static void Prefix(GoldTerrainDigger __instance)
            {
                // DigWithBox / TryMove of a machine another PC simulates are skipped by ContainerSync
                // (PatchTerrainDig, same methods): nothing is written, so nothing to capture or compare.
                if (ContainerSync.BlockDig > 0) return;
                try { VoxWrite(__instance); }
                catch (Exception ex) { GHook.Fail(ex); }
            }
        }

        // ---------- drill points ----------
        //
        // GoldTerrainDigger.CheckGold (drill rig / drill script) records a drill point the first time a
        // spot is drilled (isNew), and a new point can give a gold nugget. Each PC kept its own list, so
        // the same spot gave a nugget once per PC. A new point is sent ((Msg)14 sub 9: key x, key z, hit
        // point, max depth) and every PC records it with the same call, so the spot counts once.

        private static bool _applyingDrill;

        internal static void ReceiveDrill(BinaryReader r)
        {
            int kx = r.ReadInt32(), kz = r.ReadInt32();
            var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float maxDepth = r.ReadSingle();
            var ms = GoldSync.Begin(GoldSync.SubDrill, out BinaryWriter w);
            w.Write(kx); w.Write(kz); w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Write(maxDepth);
            byte[] msg = ms.ToArray();
            Record(msg);
            if (!SaveTransfer.WorldReady) return; // in the journal for this client
            CheckWorld();
            // Tile not loaded here (or loading, or older edits of it still queued): queued with its edits,
            // so the spot also counts as drilled here once this PC goes there.
            if (GameState.IsLoading || HasPendingFor(kx, kz) || !ApplyDrill(kx, kz, p, maxDepth, msg)) Pend(msg);
        }

        /// <summary>Records a drill point on a loaded tile. False = the tile is not loaded here.</summary>
        private static bool ApplyDrill(int kx, int kz, Vector3 p, float maxDepth, byte[] remember = null)
        {
            Info info = Get(kx, kz);
            if (info == null || GameState.IsLoading) return false;
            GoldTerrainDigger g = info.T.GetComponent<GoldTerrainDigger>();
            if (g == null) return true;
            _applyingDrill = true;
            try { g.CheckGold(p, maxDepth, out _, out _, out _); } // idempotent: a known point is not new
            catch (Exception ex) { GRecv.Fail(ex); }
            finally { _applyingDrill = false; }
            if (remember != null) Remember(info, remember);
            return true;
        }

        [HarmonyPatch]
        private static class PatchCheckGold
        {
            private static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(GoldTerrainDigger), nameof(GoldTerrainDigger.CheckGold),
                    new[] { typeof(Vector3), typeof(float), typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(bool).MakeByRefType() });

            private static bool Prepare() => TargetMethod() != null;

            private static void Postfix(GoldTerrainDigger __instance, Vector3 hitPoint, float maxDepth, ref bool isNew)
            {
                if (!isNew || _applyingDrill || !Synced()) return;
                try
                {
                    Info info = InfoOf(__instance);
                    if (info == null) return;
                    var ms = GoldSync.Begin(GoldSync.SubDrill, out BinaryWriter w);
                    w.Write(info.Kx); w.Write(info.Kz);
                    w.Write(hitPoint.x); w.Write(hitPoint.y); w.Write(hitPoint.z);
                    w.Write(maxDepth);
                    byte[] msg = ms.ToArray();
                    Record(msg);
                    TrafficC.Broadcast(TrafficC.Cat.Terrain, msg, true);
                }
                catch (Exception ex) { GHook.Fail(ex); }
            }
        }

        // ---------- parcel reset ----------
        //
        // BankParcelGUI.ResetConfirm -> GoldTerrainDigger.ResetParcell restores the claim's original
        // heights and splats and rolls new gold areas. Outside leaderboard mode the gold areas come from
        // the unseeded UnityEngine.Random, so every PC would roll a different gold map. The command is sent
        // ((Msg)14 sub 4: key x, key z, seed i32) and every PC runs ResetParcell on that tile with
        // UnityEngine.Random seeded the same way (the global random state is restored afterwards).

        private static int _parcelReset;
        private static bool _applyingReset;
        private static int _resetSeed;
        private static bool _hasResetSeed;

        private static void DropAll(Info info)
        {
            info.H.Shadow.Clear(); info.A.Shadow.Clear();
            info.H.Unacked.Clear(); info.A.Unacked.Clear();
            info.H.Dirty = info.A.Dirty = false;
            info.V = null;
            info.Recent.Clear();
            info.RecentBytes = 0;
        }

        internal static void ReceiveParcelReset(BinaryReader r)
        {
            int kx = r.ReadInt32(), kz = r.ReadInt32(), seed = r.ReadInt32();
            var ms = GoldSync.Begin(GoldSync.SubParcelReset, out BinaryWriter w);
            w.Write(kx); w.Write(kz); w.Write(seed);
            byte[] msg = ms.ToArray();
            Record(msg);
            if (!SaveTransfer.WorldReady) return; // the host's journal brings it after the load
            CheckWorld();
            // Queued in order with the tile's edits when it is not loaded (or older edits of it still wait).
            if (HasPendingFor(kx, kz) || !ApplyReset(kx, kz, seed, msg)) Pend(msg);
        }

        private static bool ApplyReset(int kx, int kz, int seed, byte[] remember = null)
        {
            Info info = Get(kx, kz);
            if (info == null || GameState.IsLoading) return false;
            GoldTerrainDigger g = info.T.GetComponent<GoldTerrainDigger>();
            if (g == null) return true;
            _applyingReset = true;
            _resetSeed = seed;
            _hasResetSeed = true;
            try { g.ResetParcell(); }
            catch (Exception ex) { GRecv.Fail(ex); }
            finally { _applyingReset = false; _hasResetSeed = false; }
            // After the reset (its finalizer cleared the tile's recent edits): a later restore of the tile's
            // saved state runs it again.
            if (remember != null) Remember(info, remember);
            Plugin.Log.LogInfo($"Terrain sync: parcel ({kx},{kz}) reset by another player");
            return true;
        }

        [HarmonyPatch]
        private static class PatchResetParcell
        {
            private static MethodBase TargetMethod() =>
                GoldSync.Need(typeof(GoldTerrainDigger), nameof(GoldTerrainDigger.ResetParcell), "shared parcel reset", Type.EmptyTypes);

            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(GoldTerrainDigger __instance, out UnityEngine.Random.State? __state)
            {
                __state = null;
                try
                {
                    Info info = InfoOf(__instance);
                    if (_applyingReset && _hasResetSeed)
                    {
                        __state = UnityEngine.Random.state;
                        UnityEngine.Random.InitState(_resetSeed);
                    }
                    else if (!_applyingReset && info != null && Synced())
                    {
                        FlushAll(true); // edits made before the reset go out first (same reliable channel)
                        int seed = Guid.NewGuid().GetHashCode();
                        var ms = GoldSync.Begin(GoldSync.SubParcelReset, out BinaryWriter w);
                        w.Write(info.Kx); w.Write(info.Kz); w.Write(seed);
                        byte[] msg = ms.ToArray();
                        Record(msg);
                        TrafficC.Broadcast(TrafficC.Cat.Terrain, msg, true);
                        __state = UnityEngine.Random.state;
                        UnityEngine.Random.InitState(seed); // same gold map on every PC
                    }
                }
                catch (Exception ex) { GHook.Fail(ex); }
                _parcelReset++;
            }

            private static Exception Finalizer(GoldTerrainDigger __instance, Exception __exception, UnityEngine.Random.State? __state)
            {
                if (_parcelReset > 0) _parcelReset--;
                if (__state.HasValue) UnityEngine.Random.state = __state.Value;
                try
                {
                    Info info = InfoOf(__instance);
                    if (info != null) DropAll(info); // everything known about this tile is stale now
                }
                catch (Exception ex) { GHook.Fail(ex); }
                return __exception;
            }
        }

        // ---------- Harmony ----------

        /// <summary>A full-size write hit a tile (saved state restored / parcel reset).</summary>
        private static void Restored(Info info, bool heights, bool alphas)
        {
            if (_parcelReset > 0)
            {
                // Parcel reset: the old edits are meant to disappear.
                info.Recent.Clear();
                info.RecentBytes = 0;
                return;
            }
            if (info.Recent.Count == 0 || Time.unscaledTime - info.FirstSeen > RecentWindow) return;
            if (heights) info.ReapplyH = true;
            if (alphas) info.ReapplyA = true;
            info.ReapplyV = true; // the voxels come from the same save entry
        }

        private static void BeforeHeights(TerrainData data, int x, int y, float[,] heights)
        {
            try { BeforeHeightsCore(data, x, y, heights); }
            catch (Exception ex) { GHook.Fail(ex); }
        }

        private static void AfterHeights(TerrainData data, int x, int y, float[,] heights)
        {
            try { AfterHeightsCore(data, x, y, heights); }
            catch (Exception ex) { GHook.Fail(ex); }
        }

        private static void BeforeHeightsCore(TerrainData data, int x, int y, float[,] heights)
        {
            if (_applying || heights == null || !Synced()) return;
            int h = heights.GetLength(0), w = heights.GetLength(1);
            if (w * h == 0) return;
            Info info = Get(data);
            if (info == null) return;
            // A local-only write is about to overwrite cells: send real edits waiting in the dirty rect
            // first, they would look unchanged after the shadow refresh.
            if (_suppress > 0) { if (info.H.Dirty) FlushHeights(info); return; }
            if (w * h <= MaxCells) EnsureShadow(info, true, x, y, w, h);
        }

        private static void AfterHeightsCore(TerrainData data, int x, int y, float[,] heights)
        {
            if (_applying || heights == null || !Net.Active) return;
            int h = heights.GetLength(0), w = heights.GetLength(1);
            if (w * h == 0) return;
            Info info = Get(data);
            if (info == null) return;
            if (_suppress > 0) { RefreshHeightShadow(info, x, y, heights); return; }
            if (!Synced() || w * h > MaxCells)
            {
                // Load/restore or pre-join write: what we knew about this area is stale now.
                DropShadow(info.H, x, y, w, h);
                if (w * h > MaxCells) Restored(info, true, false);
                return;
            }
            MarkDirty(info, info.H, true, x, y, w, h);
        }

        [HarmonyPatch(typeof(TerrainData), nameof(TerrainData.SetHeights), typeof(int), typeof(int), typeof(float[,]))]
        private static class PatchSetHeights
        {
            private static void Prefix(TerrainData __instance, int xBase, int yBase, float[,] heights) =>
                BeforeHeights(__instance, xBase, yBase, heights);

            private static void Postfix(TerrainData __instance, int xBase, int yBase, float[,] heights) =>
                AfterHeights(__instance, xBase, yBase, heights);
        }

        [HarmonyPatch(typeof(TerrainData), nameof(TerrainData.SetHeightsDelayLOD), typeof(int), typeof(int), typeof(float[,]))]
        private static class PatchSetHeightsDelayLOD
        {
            private static void Prefix(TerrainData __instance, int xBase, int yBase, float[,] heights) =>
                BeforeHeights(__instance, xBase, yBase, heights);

            private static void Postfix(TerrainData __instance, int xBase, int yBase, float[,] heights) =>
                AfterHeights(__instance, xBase, yBase, heights);
        }

        [HarmonyPatch(typeof(TerrainData), nameof(TerrainData.SetAlphamaps))]
        private static class PatchSetAlphamaps
        {
            private static void Prefix(TerrainData __instance, int x, int y, float[,,] map)
            {
                try { Before(__instance, x, y, map); }
                catch (Exception ex) { GHook.Fail(ex); }
            }

            private static void Postfix(TerrainData __instance, int x, int y, float[,,] map)
            {
                try { After(__instance, x, y, map); }
                catch (Exception ex) { GHook.Fail(ex); }
            }

            private static void Before(TerrainData __instance, int x, int y, float[,,] map)
            {
                if (_applying || map == null || !Synced()) return;
                int h = map.GetLength(0), w = map.GetLength(1);
                if (w * h == 0) return;
                Info info = Get(__instance);
                if (info == null) return;
                if (_suppress > 0) { if (info.A.Dirty) FlushAlphas(info); return; }
                if (w * h <= MaxCells) EnsureShadow(info, false, x, y, w, h);
            }

            private static void After(TerrainData __instance, int x, int y, float[,,] map)
            {
                if (_applying || map == null || !Net.Active) return;
                int h = map.GetLength(0), w = map.GetLength(1);
                if (w * h == 0) return;
                Info info = Get(__instance);
                if (info == null) return;
                if (_suppress > 0) { RefreshAlphaShadow(info, x, y, map); return; }
                if (!Synced() || w * h > MaxCells)
                {
                    DropShadow(info.A, x, y, w, h);
                    if (w * h > MaxCells) Restored(info, false, true);
                    return;
                }
                MarkDirty(info, info.A, false, x, y, w, h);
            }
        }

        /// <summary>
        /// Seasonal frost texture: every PC computes it from its own clock/temperature over the whole
        /// splat map. Never sent (it used to be skipped only because it is larger than MaxCells).
        /// </summary>
        [HarmonyPatch]
        private static class PatchFreezeDirt
        {
            private static MethodBase TargetMethod() =>
                GoldSync.Need(typeof(GoldTerrainDigger), "FreezeDirt", "local-only frost texture");

            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix() => _suppress++;

            private static Exception Finalizer(Exception __exception)
            {
                if (_suppress > 0) _suppress--;
                return __exception;
            }
        }
    }
}
