using System;
using System.Collections.Generic;
using System.IO;
using SaveSystem;
using UnityEngine;
using GameSaveManager = SaveSystem.SaveManager;

namespace GMSCoop
{
    /// <summary>
    /// Session message (Msg)19, point-to-point between a client and the host, never relayed.
    ///
    /// Identity: after every save the host sends the world's identity to every client (and to a client as
    /// soon as it plays the world); the client writes it into Coop_Host\gmscoop_world.json, so its copy
    /// always knows the host's current revision. The first identity a client receives in a session also
    /// triggers the "newer copy" check:
    ///   - a solo copy of this world (the client kept playing after losing the host) whose BaseRevision is
    ///     at least the revision the host loaded: it continues from a state at least as new as the host's;
    ///   - or any copy with a higher revision than the host's current one.
    /// The host is then asked "&lt;nombre&gt; tiene una versión más nueva de este mundo ... [U] Usar la suya /
    /// [N] Seguir con la mía" (game input is paused while it is shown). U: the client uploads its slot.
    /// The host unpacks it next to its slot, then, once the save system is idle, no world transfer runs and
    /// the game is ReadyToPlay, keeps its own slot as "&lt;slot&gt;_antes_&lt;fecha&gt;", moves the upload in and
    /// loads it (saves are blocked meanwhile). If the game refuses the load, the old slot comes back.
    /// SaveTransfer sends the loaded world to every player, as for any world the host loads.
    ///
    /// Wire: byte sub, then
    ///   1 Offer    (client->host): string worldId, int revision, bool solo, int baseRevision, long savedUtcTicks
    ///   2 Request  (host->client): string worldId
    ///   3 Chunk    (client->host): int id, int index, int count, int total, int len, bytes
    ///   4 Failed   (client->host): string why
    ///   5 Identity (host->client): string worldId, int revision, int loadedRevision, long savedUtcTicks, string by, double minutes
    /// Container: int files, {string name, int len, bytes}*
    /// </summary>
    internal static class CoopSession
    {
        internal const Msg MsgSession = (Msg)19;
        private const byte SubOffer = 1, SubRequest = 2, SubChunk = 3, SubFailed = 4, SubIdentity = 5;
        private const int ChunkSize = 64 * 1024;
        private const int MaxQueued = 512 * 1024;
        private const int MaxBytes = 512 * 1024 * 1024;

        // ---- client ----
        private static bool _checked;
        private static string _offerSlot;
        private static byte[] _upData;
        private static int _upId, _upNext, _upCount;
        private static ulong _upPeer;

        // ---- host ----
        private static readonly HashSet<ulong> IdentitySent = new HashSet<ulong>();
        private static ulong _offerPeer;
        private static string _offerName;
        private static int _offerRev, _ownRev;
        private static DateTime _offerSaved;
        private static bool _prompt, _receiving;
        private static byte[] _rx;
        private static bool[] _rxGot;
        private static int _rxId, _rxCount, _rxHave;
        private static float _rxLast;
        // Install queue: unpacked upload waiting for a safe moment.
        private static string _installTmp, _installSlot;
        private static float _installSince;
        private static bool _loadCheck;               // RequestLoad was called: confirm it started
        private static string _restoreDir, _restoreKeep;

        /// <summary>An uploaded world is about to replace the host's slot: saves are blocked (CoopSaves).</summary>
        internal static bool InstallPending => _installTmp != null || _loadCheck;

        /// <summary>A prompt that reads plain keys is shown: game input is paused (FocusInput).</summary>
        internal static bool PromptOpen => _prompt;

        internal static void Tick()
        {
            if (_installTmp != null || _loadCheck) InstallTick();
            if (!Net.Active) { ResetSession(); return; }
            if (!Net.IsHost) ClientTick();
            else HostTick();
        }

        private static void ResetSession()
        {
            _checked = false; _offerSlot = null; _upData = null;
            _prompt = false; _receiving = false; _rx = null; _rxGot = null;
            IdentitySent.Clear();
        }

        // ================================================================= identity

        /// <summary>Host: after a save, every client learns the world's current identity.</summary>
        internal static void BroadcastIdentity(WorldIdentity id, int loadedRevision)
        {
            if (id == null || !Net.Active || !Net.IsHost || Net.Transport == null) return;
            byte[] data = IdentityPacket(id, loadedRevision);
            foreach (ulong p in Net.Peers)
                if (SaveTransfer.PeerReady(p)) { Net.Transport.Send(p, data, true); IdentitySent.Add(p); }
        }

        private static byte[] IdentityPacket(WorldIdentity id, int loadedRevision)
        {
            var ms = Begin(SubIdentity, out var w);
            w.Write(id.WorldId ?? "");
            w.Write(id.Revision);
            w.Write(loadedRevision);
            w.Write(id.LastSavedUtc.Ticks);
            w.Write(id.LastSavedBy ?? "");
            w.Write(id.MinutesPlayed);
            return ms.ToArray();
        }

        private static void OnIdentity(BinaryReader r)
        {
            var id = new WorldIdentity
            {
                WorldId = r.ReadString(),
                Revision = r.ReadInt32(),
            };
            int loadedRev = r.ReadInt32();
            id.LastSavedUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            id.LastSavedBy = r.ReadString();
            id.MinutesPlayed = r.ReadDouble();
            if (string.IsNullOrEmpty(id.WorldId)) return;
            CoopSaves.OnHostIdentity(id);
            if (_checked || !SaveTransfer.ClientSession) return;
            _checked = true;
            try { CheckNewerCopy(id, loadedRev); } catch (Exception ex) { Plugin.Log.LogWarning("World check: " + ex.Message); }
        }

        // ================================================================= client

        private static void ClientTick()
        {
            if (!SaveTransfer.ClientSession) { _checked = false; return; }
            if (_upData != null) PumpUpload();
        }

        private static void CheckNewerCopy(WorldIdentity host, int hostLoadedRev)
        {
            if (Net.Peers.Count == 0) return;
            foreach (var copy in WorldIdentity.FindCopies(host.WorldId))
            {
                if (copy.Key == SaveTransfer.CoopSlot) continue;
                WorldIdentity c = copy.Value;
                bool newer = (c.Solo && c.BaseRevision >= hostLoadedRev) || c.Revision > host.Revision;
                if (!newer) continue;
                _offerSlot = copy.Key;
                var ms = Begin(SubOffer, out var w);
                w.Write(host.WorldId);
                w.Write(c.Revision);
                w.Write(c.Solo);
                w.Write(c.BaseRevision);
                w.Write(c.LastSavedUtc.Ticks);
                Net.Transport.Send(Net.Peers[0], ms.ToArray(), true);
                Plugin.Instance.SetStatus($"Tienes una versión más nueva de este mundo ('{copy.Key}', rev {c.Revision}): el host decide cuál usar");
                return;
            }
        }

        private static void StartUpload(ulong host, string worldId)
        {
            try
            {
                if (_offerSlot == null) throw new InvalidOperationException("no hay copia que enviar");
                WorldIdentity id = WorldIdentity.Read(_offerSlot);
                if (id == null || id.WorldId != worldId) throw new InvalidOperationException("la copia ya no coincide");
                string dir = WorldIdentity.SlotDir(_offerSlot);
                var buf = new MemoryStream();
                var w = new BinaryWriter(buf);
                string[] files = Directory.GetFiles(dir);
                w.Write(files.Length);
                foreach (string f in files)
                {
                    byte[] b = File.ReadAllBytes(f);
                    w.Write(Path.GetFileName(f));
                    w.Write(b.Length);
                    w.Write(b);
                }
                _upData = buf.ToArray();
                if (_upData.Length > MaxBytes) throw new InvalidOperationException("la partida es demasiado grande");
                _upId = Environment.TickCount;
                _upNext = 0;
                _upCount = Math.Max(1, (_upData.Length + ChunkSize - 1) / ChunkSize);
                _upPeer = host;
                Plugin.Instance.SetStatus($"Enviando tu versión del mundo al host ({_upData.Length / 1024} KB)...");
            }
            catch (Exception ex)
            {
                _upData = null;
                var ms = Begin(SubFailed, out var fw);
                fw.Write(ex.Message);
                Net.Transport.Send(host, ms.ToArray(), true);
                Plugin.Instance.SetStatus("No se pudo enviar tu versión del mundo: " + ex.Message);
            }
        }

        private static void PumpUpload()
        {
            if (!Net.Peers.Contains(_upPeer)) { _upData = null; return; }
            for (int n = 0; n < 8 && _upNext < _upCount; n++)
            {
                if (Net.Transport.QueuedBytes(_upPeer) > MaxQueued) return;
                int off = _upNext * ChunkSize;
                int len = Math.Min(ChunkSize, _upData.Length - off);
                var ms = Begin(SubChunk, out var w);
                w.Write(_upId); w.Write(_upNext); w.Write(_upCount); w.Write(_upData.Length); w.Write(len);
                w.Write(_upData, off, len);
                Net.Transport.Send(_upPeer, ms.ToArray(), true);
                _upNext++;
            }
            if (_upNext >= _upCount)
            {
                Plugin.Instance.SetStatus("Tu versión del mundo llegó al host: el host la carga y te la vuelve a mandar");
                _upData = null;
            }
        }

        // ================================================================= host

        private static void HostTick()
        {
            // A client that just started playing the world gets the identity (and runs its check).
            WorldIdentity own = CoopSaves.HostIdentity;
            if (own != null)
                foreach (ulong p in Net.Peers)
                    if (!IdentitySent.Contains(p) && SaveTransfer.PeerReady(p))
                    {
                        Net.Transport.Send(p, IdentityPacket(own, CoopSaves.HostLoadedRevision), true);
                        IdentitySent.Add(p);
                    }
            IdentitySent.RemoveWhere(p => !Net.Peers.Contains(p) || !SaveTransfer.PeerReady(p));

            if (_prompt)
            {
                // Game input is paused while the prompt is up (FocusInput), so U / N reach only this prompt.
                if (!Net.Peers.Contains(_offerPeer)) { _prompt = false; return; }
                if (Input.GetKeyDown(KeyCode.U))
                {
                    _prompt = false;
                    var ms = Begin(SubRequest, out var w);
                    w.Write(own?.WorldId ?? "");
                    Net.Transport.Send(_offerPeer, ms.ToArray(), true);
                    _receiving = true;
                    _rx = null;
                    _rxLast = Time.unscaledTime;
                    Plugin.Instance.SetStatus("Recibiendo la versión de " + _offerName + "...");
                }
                else if (Input.GetKeyDown(KeyCode.N))
                {
                    _prompt = false;
                    Plugin.Instance.SetStatus("Sigues con tu versión del mundo (la de " + _offerName + " queda en su PC)");
                }
            }
            if (_receiving && Time.unscaledTime - _rxLast > 60f)
            {
                _receiving = false; _rx = null;
                Plugin.Instance.SetStatus("No llegó la versión de " + _offerName + ": sigues con la tuya");
            }
        }

        internal static void Receive(ulong from, BinaryReader r)
        {
            byte sub = r.ReadByte();
            try
            {
                if (Net.IsHost)
                {
                    if (sub == SubOffer) OnOffer(from, r);
                    else if (sub == SubChunk) OnChunk(from, r);
                    else if (sub == SubFailed && from == _offerPeer)
                    {
                        _receiving = false; _rx = null;
                        Plugin.Instance.SetStatus(Net.NameOf(from) + " no pudo enviar su versión: " + r.ReadString());
                    }
                }
                else if (sub == SubRequest) StartUpload(from, r.ReadString());
                else if (sub == SubIdentity) OnIdentity(r);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Session message " + sub + ": " + ex.Message); }
        }

        private static void OnOffer(ulong from, BinaryReader r)
        {
            string worldId = r.ReadString();
            int rev = r.ReadInt32();
            bool solo = r.ReadBoolean();
            int baseRev = r.ReadInt32();
            var saved = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            WorldIdentity own = CoopSaves.HostIdentity;
            if (own == null || own.WorldId != worldId || _receiving || InstallPending) return;
            bool newer = (solo && baseRev >= CoopSaves.HostLoadedRevision) || rev > own.Revision;
            if (!newer) return;
            _offerPeer = from;
            _offerName = Net.NameOf(from);
            _offerRev = rev;
            _ownRev = own.Revision;
            _offerSaved = saved;
            _prompt = true;
            Plugin.Instance.SetStatus($"{_offerName} tiene una versión más nueva de este mundo: [U] usar la suya / [N] seguir con la mía");
        }

        private static void OnChunk(ulong from, BinaryReader r)
        {
            if (!_receiving || from != _offerPeer) return;
            int id = r.ReadInt32(), index = r.ReadInt32(), count = r.ReadInt32(), total = r.ReadInt32(), len = r.ReadInt32();
            if (total <= 0 || total > MaxBytes || count <= 0 || index < 0 || index >= count || len < 0 || len > ChunkSize) return;
            if (_rx == null || _rxId != id)
            {
                _rx = new byte[total]; _rxGot = new bool[count]; _rxId = id; _rxCount = count; _rxHave = 0;
            }
            int off = index * ChunkSize;
            if (off + len > _rx.Length || _rxGot[index]) return;
            byte[] b = r.ReadBytes(len);
            Buffer.BlockCopy(b, 0, _rx, off, b.Length);
            _rxGot[index] = true;
            _rxHave++;
            _rxLast = Time.unscaledTime;
            if (_rxHave % 16 == 0) Plugin.Instance.SetStatus($"Recibiendo la versión de {_offerName}... {_rxHave * 100 / _rxCount}%", log: false);
            if (_rxHave == _rxCount) Unpack();
        }

        /// <summary>Unpacks the upload next to the host's slot (the slot itself is untouched until InstallTick).</summary>
        private static void Unpack()
        {
            _receiving = false;
            byte[] data = _rx;
            _rx = null;
            try
            {
                string slot = GameSaveManager.GetCurrentSlotName();
                if (string.IsNullOrEmpty(slot) || slot == SaveTransfer.CoopSlot) throw new InvalidOperationException("no sé en qué partida guardar");
                string tmp = WorldIdentity.SlotDir(slot) + ".incoming";
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);
                using (var br = new BinaryReader(new MemoryStream(data)))
                {
                    int files = br.ReadInt32();
                    if (files <= 0 || files > 64) throw new InvalidDataException("archivo dañado");
                    for (int i = 0; i < files; i++)
                    {
                        string name = Path.GetFileName(br.ReadString());
                        int len = br.ReadInt32();
                        if (string.IsNullOrEmpty(name) || len < 0) throw new InvalidDataException("archivo dañado");
                        File.WriteAllBytes(Path.Combine(tmp, name), br.ReadBytes(len));
                    }
                }
                if (!File.Exists(Path.Combine(tmp, "CheckpointData.sav"))) throw new InvalidDataException("falta CheckpointData.sav");
                _installTmp = tmp;
                _installSlot = slot;
                _installSince = Time.unscaledTime;
                Plugin.Instance.SetStatus("Versión de " + _offerName + " recibida: se carga en cuanto el juego esté libre...");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("World upload unpack: " + ex);
                Plugin.Instance.SetStatus("No se pudo usar la versión de " + _offerName + ": " + ex.Message + " (sigues con la tuya)");
            }
        }

        /// <summary>Moves the upload into the host's slot and loads it, only at a safe moment; confirms the load started.</summary>
        private static void InstallTick()
        {
            float now = Time.unscaledTime;
            CheckpointManager cpm = CoopSaves.Checkpoints();
            if (_loadCheck)
            {
                // Started = the game left ReadyToPlay (loading). Still ReadyToPlay after 5 s = refused.
                if (GameState.IsLoading || (cpm != null && cpm.GetLoadingState() != LoadingState.ReadyToPlay))
                {
                    _loadCheck = false;
                    Plugin.Log.LogInfo("World upload: load started");
                    return;
                }
                if (now - _installSince < 5f) return;
                _loadCheck = false;
                RestoreOld("el juego no aceptó cargar la partida");
                return;
            }
            if (now - _installSince > 120f)
            {
                try { Directory.Delete(_installTmp, true); } catch { }
                _installTmp = null;
                Plugin.Instance.SetStatus("No se pudo cargar la versión de " + _offerName + " (el juego siguió ocupado): sigues con la tuya");
                return;
            }
            bool safe = cpm != null && !GameState.IsLoading && !CoopSaves.SaveBusy() && SaveTransfer.HostTransferIdle &&
                        cpm.GetLoadingState() == LoadingState.ReadyToPlay;
            if (!safe) return;
            string slot = _installSlot, tmp = _installTmp;
            _installTmp = null;
            string dir = WorldIdentity.SlotDir(slot);
            string keep = slot + "_antes_" + DateTime.Now.ToString("yyyyMMdd_HHmm");
            string keepDir = WorldIdentity.SlotDir(keep);
            try
            {
                if (Directory.Exists(keepDir)) Directory.Delete(keepDir, true);
                if (Directory.Exists(dir)) Directory.Move(dir, keepDir);
                Directory.Move(tmp, dir);
                _restoreDir = dir;
                _restoreKeep = keepDir;
                Plugin.Instance.SetStatus($"Usando la versión de {_offerName} (tu versión anterior quedó en '{keep}'). Cargando...");
                GameSaveManager.SetCurrentSlot(slot);
                _loadCheck = true;
                _installSince = now;
                cpm.RequestLoad(); // SaveTransfer sends the loaded world to every player
            }
            catch (Exception ex)
            {
                _loadCheck = false;
                Plugin.Log.LogWarning("World upload install: " + ex);
                RestoreOld(ex.Message);
            }
        }

        private static void RestoreOld(string why)
        {
            try
            {
                if (_restoreKeep != null && Directory.Exists(_restoreKeep))
                {
                    string failed = _restoreDir + ".rechazada";
                    if (Directory.Exists(failed)) Directory.Delete(failed, true);
                    if (Directory.Exists(_restoreDir)) Directory.Move(_restoreDir, failed);
                    Directory.Move(_restoreKeep, _restoreDir);
                }
                Plugin.Instance.SetStatus("No se pudo usar la versión de " + _offerName + ": " + why + ". Tu partida quedó como estaba.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("World upload restore failed: " + ex);
                Plugin.Instance.SetStatus("No se pudo usar la versión de " + _offerName + " y no se pudo restaurar la tuya automáticamente: está en '" + Path.GetFileName(_restoreKeep) + "'");
            }
            _restoreDir = _restoreKeep = null;
        }

        internal static void AppendOverlay(System.Text.StringBuilder sb)
        {
            if (!_prompt) return;
            sb.Append("\n<color=#FFE14C><b>").Append(_offerName).Append("</b> tiene una versión más nueva de este mundo (guardada ")
              .Append(WorldIdentity.Friendly(_offerSaved)).Append(", rev ").Append(_offerRev).Append(" vs ").Append(_ownRev)
              .Append(").  [U] Usar la suya   [N] Seguir con la mía  (el juego espera tu elección)</color>");
        }

        private static MemoryStream Begin(byte sub, out BinaryWriter w)
        {
            var ms = Net.Writer(MsgSession);
            w = new BinaryWriter(ms);
            w.Write(sub);
            return ms;
        }
    }
}
