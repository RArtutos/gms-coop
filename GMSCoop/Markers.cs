using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>Stable per-player color (name tag, markers, overlay list), from the player's LocalId.</summary>
    internal static class PlayerColors
    {
        private static readonly Color[] Palette =
        {
            new Color(1.00f, 0.76f, 0.20f), // gold
            new Color(0.30f, 0.80f, 1.00f), // sky blue
            new Color(0.45f, 0.95f, 0.40f), // green
            new Color(1.00f, 0.45f, 0.35f), // coral
            new Color(0.80f, 0.55f, 1.00f), // violet
            new Color(1.00f, 0.55f, 0.85f), // pink
            new Color(0.35f, 1.00f, 0.85f), // aqua
            new Color(1.00f, 0.95f, 0.45f), // lemon
        };

        internal static Color For(ulong localId)
        {
            if (localId == 0) return Color.white;
            ulong h = localId * 0x9E3779B97F4A7C15UL;
            return Palette[(int)((h >> 32) % (ulong)Palette.Length)];
        }

        internal static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);
    }

    /// <summary>
    /// Social features over message 18 (never a world message: no world gate, the host relays by hand):
    ///  sub 0 Marker      aim + middle mouse: every player sees a marker with the pinger's name, color and distance.
    ///  sub 1 LogRequest  host -> clients: send me your log tail (for the launcher's "Enviar reporte").
    ///  sub 2 LogChunk    client -> host: compressed log tail in chunks; the host writes BepInEx\reports\&lt;fecha&gt;\&lt;player&gt;.log.
    /// </summary>
    internal static class Social
    {
        internal const Msg MsgId = (Msg)18;
        private const byte SubMarker = 0, SubLogRequest = 1, SubLogChunk = 2;

        // ---------------- markers ----------------

        private sealed class Marker
        {
            public ulong Owner;
            public string Name;
            public Vector3 Pos;
            public float Born;
            public Color Color;
            public GameObject Beam;
        }

        private const float MarkerLife = 12f;
        private const int MaxPerPlayer = 3;
        private const float MaxRange = 300f;
        private static readonly List<Marker> Markers = new List<Marker>();
        private static float _nextLocalMarker;
        private static bool _hintShown;
        private static float _hintUntil;

        internal static void Tick()
        {
            HudBehaviour.Ensure();
            PollReportRequest();
            PumpLogUpload();
            ExpireMarkers();
            if (!Net.Active) { if (Markers.Count > 0) ClearMarkers(); return; }

            if (!_hintShown && Net.WorldLive && !GameState.IsLoading)
            {
                _hintShown = true;
                _hintUntil = Time.unscaledTime + 10f;
            }

            // Middle mouse, only in the focused window, in game (cursor locked), not while loading.
            if (Application.isFocused && Input.GetMouseButtonDown(2) && Cursor.lockState == CursorLockMode.Locked &&
                !GameState.IsLoading && Net.WorldLive && Time.unscaledTime >= _nextLocalMarker)
            {
                Camera cam = Camera.main;
                if (cam != null && Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, MaxRange, ~0, QueryTriggerInteraction.Ignore))
                {
                    _nextLocalMarker = Time.unscaledTime + 0.4f;
                    string name = Net.LocalName;
                    AddMarker(Net.LocalId, name, hit.point);
                    var ms = Net.Writer(MsgId);
                    ms.WriteByte(SubMarker);
                    var w = new BinaryWriter(ms);
                    w.Write(Net.LocalId);
                    w.Write(name ?? "");
                    w.Write(hit.point.x); w.Write(hit.point.y); w.Write(hit.point.z);
                    Net.Broadcast(ms.ToArray(), reliable: true);
                }
            }
        }

        internal static string HintText =>
            Time.unscaledTime < _hintUntil ? "Rueda del mouse (clic): marcar un punto para todos" : null;

        private static void AddMarker(ulong owner, string name, Vector3 pos)
        {
            // At most MaxPerPlayer alive per player: the oldest goes.
            int count = 0;
            Marker oldest = null;
            foreach (Marker m in Markers)
                if (m.Owner == owner) { count++; if (oldest == null || m.Born < oldest.Born) oldest = m; }
            if (count >= MaxPerPlayer && oldest != null) RemoveMarker(oldest);

            var mk = new Marker { Owner = owner, Name = string.IsNullOrEmpty(name) ? "?" : name, Pos = pos, Born = Time.unscaledTime, Color = PlayerColors.For(owner) };
            mk.Beam = MakeBeam(pos, mk.Color);
            Markers.Add(mk);
            Beep.Play();
        }

        private static Material _beamMat;

        private static GameObject MakeBeam(Vector3 pos, Color color)
        {
            try
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                UnityEngine.Object.Destroy(go.GetComponent<Collider>());
                go.name = "CoopMarkerBeam";
                go.transform.position = pos + Vector3.up * 15f;
                go.transform.localScale = new Vector3(0.18f, 15f, 0.18f);
                var r = go.GetComponent<Renderer>();
                if (_beamMat == null)
                {
                    Shader sh = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
                    _beamMat = new Material(sh);
                }
                var mat = new Material(_beamMat) { color = color };
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                return go;
            }
            catch { return null; }
        }

        private static void RemoveMarker(Marker m)
        {
            if (m.Beam != null) UnityEngine.Object.Destroy(m.Beam);
            Markers.Remove(m);
        }

        private static void ExpireMarkers()
        {
            float now = Time.unscaledTime;
            for (int i = Markers.Count - 1; i >= 0; i--)
            {
                Marker m = Markers[i];
                float age = now - m.Born;
                if (age > MarkerLife) { RemoveMarker(m); continue; }
                if (m.Beam != null && age > MarkerLife - 2f)
                {
                    float k = Mathf.Clamp01((MarkerLife - age) / 2f);
                    m.Beam.transform.localScale = new Vector3(0.18f * k, 15f, 0.18f * k);
                }
            }
        }

        private static void ClearMarkers()
        {
            for (int i = Markers.Count - 1; i >= 0; i--) RemoveMarker(Markers[i]);
        }

        /// <summary>Drawn by HudBehaviour.OnGUI: diamond + name + distance, clamped to the screen edge with an arrow.</summary>
        internal static void DrawMarkers()
        {
            if (Markers.Count == 0) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            EnsureStyles();
            float now = Time.unscaledTime;
            float sw = Screen.width, sh = Screen.height;
            float scale = Mathf.Max(1f, sh / 1080f);
            foreach (Marker m in Markers)
            {
                float age = now - m.Born;
                float alpha = age > MarkerLife - 2f ? Mathf.Clamp01((MarkerLife - age) / 2f) : 1f;
                Vector3 sp = cam.WorldToScreenPoint(m.Pos + Vector3.up * 0.6f);
                bool behind = sp.z < 0f;
                float x = sp.x, y = sh - sp.y;
                if (behind) { x = sw - x; y = sh - y; }
                float margin = 40f * scale;
                bool off = behind || x < margin || x > sw - margin || y < margin || y > sh - margin;
                float dist = Vector3.Distance(cam.transform.position, m.Pos);
                Color c = m.Color;
                c.a = alpha;

                if (off)
                {
                    // Clamp to the screen edge along the direction from the center.
                    Vector2 center = new Vector2(sw / 2f, sh / 2f);
                    Vector2 dir = new Vector2(x, y) - center;
                    if (dir.sqrMagnitude < 1f) dir = Vector2.up;
                    dir.Normalize();
                    float kx = dir.x != 0 ? (sw / 2f - margin) / Mathf.Abs(dir.x) : float.MaxValue;
                    float ky = dir.y != 0 ? (sh / 2f - margin) / Mathf.Abs(dir.y) : float.MaxValue;
                    Vector2 p = center + dir * Mathf.Min(kx, ky);
                    x = p.x; y = p.y;
                    float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg + 90f;
                    Matrix4x4 saved = GUI.matrix;
                    GUIUtility.RotateAroundPivot(angle, new Vector2(x, y));
                    Label(new Rect(x - 20 * scale, y - 20 * scale, 40 * scale, 40 * scale), "▲", _arrow, c, scale);
                    GUI.matrix = saved;
                }
                else
                {
                    Label(new Rect(x - 20 * scale, y - 26 * scale, 40 * scale, 40 * scale), "◆", _diamond, c, scale);
                }
                string text = m.Name + "  " + (dist < 1000f ? dist.ToString("0", CultureInfo.InvariantCulture) + " m" : (dist / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km");
                float ty = off ? y + (y > sh / 2f ? -46 : 22) * scale : y + 12 * scale;
                Label(new Rect(x - 150 * scale, ty, 300 * scale, 26 * scale), text, _text, c, scale);
            }
        }

        private static GUIStyle _diamond, _arrow, _text;

        private static void EnsureStyles()
        {
            if (_text != null) return;
            _diamond = new GUIStyle { alignment = TextAnchor.MiddleCenter, fontSize = 30, fontStyle = FontStyle.Bold };
            _arrow = new GUIStyle(_diamond) { fontSize = 26 };
            _text = new GUIStyle { alignment = TextAnchor.UpperCenter, fontSize = 17, fontStyle = FontStyle.Bold };
        }

        /// <summary>Text with a dark outline (readable at night, in fog and on snow).</summary>
        private static void Label(Rect r, string s, GUIStyle style, Color c, float scale)
        {
            int baseSize = style.fontSize;
            style.fontSize = Mathf.RoundToInt(baseSize * scale);
            var shadow = new Color(0f, 0f, 0f, 0.85f * c.a);
            style.normal.textColor = shadow;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0) GUI.Label(new Rect(r.x + dx * 2, r.y + dy * 2, r.width, r.height), s, style);
            style.normal.textColor = c;
            GUI.Label(r, s, style);
            style.fontSize = baseSize;
        }

        // ---------------- receive ----------------

        /// <summary>Message 18. The host relays markers to the other clients; log chunks stay on the host.</summary>
        internal static void Receive(ulong from, BinaryReader r, byte[] raw)
        {
            byte sub = r.ReadByte();
            switch (sub)
            {
                case SubMarker:
                    {
                        ulong owner = r.ReadUInt64();
                        string name = r.ReadString();
                        var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        if (owner == Net.LocalId || !IsFinite(pos)) return;
                        if (name.Length > 40) name = name.Substring(0, 40);
                        if (Net.IsHost) Net.Broadcast(raw, reliable: true, except: from);
                        if (Net.WorldLive && !GameState.IsLoading) AddMarker(owner, name, pos);
                        return;
                    }
                case SubLogRequest:
                    if (!Net.IsHost) StartLogUpload(r.ReadInt32());
                    return;
                case SubLogChunk:
                    if (Net.IsHost) ReceiveLogChunk(from, r);
                    return;
                case RemoteSounds.SubPlay:
                case RemoteSounds.SubStop:
                case RemoteSounds.SubParam:
                    RemoteSounds.Receive(sub, from, r, raw);
                    return;
                case DogSync.SubDog:
                    DogSync.Receive(from, r, raw);
                    return;
            }
        }

        private static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
            !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        // ---------------- report: host collects the clients' logs ----------------
        //
        // The launcher's "Enviar reporte" writes BepInEx\gmscoop_report_request.txt. The mod (any role) answers
        // with BepInEx\gmscoop_report_done.txt: a host first asks every client for its log tail and waits for
        // them (max 20 s). A client or a solo game answers at once. Ctrl+Shift+F12 on the host does the same.

        private static string BepDir => Path.Combine(BepInEx.Paths.BepInExRootPath ?? ".", "");
        private static string RequestFile => Path.Combine(BepDir, "gmscoop_report_request.txt");
        private static string DoneFile => Path.Combine(BepDir, "gmscoop_report_done.txt");

        private static float _nextPoll;
        private static int _reqId;
        private static float _collectUntil = -1f;
        private static string _reportDir;
        private static readonly HashSet<ulong> Waiting = new HashSet<ulong>();
        private static readonly Dictionary<ulong, byte[][]> Incoming = new Dictionary<ulong, byte[][]>();

        private static void PollReportRequest()
        {
            bool key = Input.GetKeyDown(KeyCode.F12) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
                       (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            bool file = false;
            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + 1f;
                try { file = File.Exists(RequestFile); if (file) File.Delete(RequestFile); } catch { file = false; }
            }
            if (key || file) StartCollect();

            if (_collectUntil > 0f && (Waiting.Count == 0 || Time.unscaledTime > _collectUntil)) FinishCollect();
        }

        private static void StartCollect()
        {
            _reportDir = Path.Combine(BepDir, "reports", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));
            Waiting.Clear();
            Incoming.Clear();
            if (Net.Active && Net.IsHost && Net.Peers.Count > 0)
            {
                try { Directory.CreateDirectory(_reportDir); } catch { }
                _reqId++;
                foreach (ulong p in Net.Peers) Waiting.Add(p);
                var ms = Net.Writer(MsgId);
                ms.WriteByte(SubLogRequest);
                new BinaryWriter(ms).Write(_reqId);
                Net.Broadcast(ms.ToArray(), reliable: true);
                _collectUntil = Time.unscaledTime + 20f;
                Plugin.Instance?.SetStatus("Reporte: juntando los logs de los jugadores...");
            }
            else
            {
                _collectUntil = Time.unscaledTime; // nothing to collect: answer now
            }
        }

        private static void FinishCollect()
        {
            int got = 0;
            foreach (var kv in Incoming)
            {
                byte[][] parts = kv.Value;
                bool complete = true;
                foreach (byte[] b in parts) if (b == null) { complete = false; break; }
                if (!complete) continue;
                try
                {
                    var all = new MemoryStream();
                    foreach (byte[] b in parts) all.Write(b, 0, b.Length);
                    all.Position = 0;
                    string name = Sanitize(Net.NameOf(kv.Key));
                    using (var z = new DeflateStream(all, CompressionMode.Decompress))
                    using (var f = File.Create(Path.Combine(_reportDir, name + ".log")))
                        z.CopyTo(f);
                    got++;
                }
                catch (Exception ex) { Plugin.Log.LogWarning("Report: could not save a log: " + ex.Message); }
            }
            int asked = Incoming.Count + Waiting.Count;
            try { File.WriteAllText(DoneFile, (got > 0 ? _reportDir : "") + "\n" + got + "/" + (Net.Active && Net.IsHost ? Net.Peers.Count : 0)); } catch { }
            if (Net.Active && Net.IsHost && Net.Peers.Count > 0)
                Plugin.Instance?.SetStatus($"Reporte: {got} de {Net.Peers.Count} logs recibidos");
            Plugin.Log.LogInfo($"Report: collected {got} client log(s) into {_reportDir} (asked {asked})");
            _collectUntil = -1f;
            Waiting.Clear();
        }

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        private static void ReceiveLogChunk(ulong from, BinaryReader r)
        {
            int req = r.ReadInt32();
            int index = r.ReadInt32();
            int count = r.ReadInt32();
            int len = r.ReadInt32();
            byte[] data = r.ReadBytes(len);
            if (req != _reqId || _collectUntil < 0f || count <= 0 || count > 200 || index < 0 || index >= count) return;
            if (!Incoming.TryGetValue(from, out byte[][] parts) || parts.Length != count) Incoming[from] = parts = new byte[count][];
            parts[index] = data;
            foreach (byte[] b in parts) if (b == null) return;
            Waiting.Remove(from);
        }

        // client side
        private static readonly Queue<byte[]> Upload = new Queue<byte[]>();

        private static void StartLogUpload(int req)
        {
            Upload.Clear();
            try
            {
                string log = Path.Combine(BepDir, "LogOutput.log");
                byte[] tail;
                using (var f = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long max = 2L * 1024 * 1024;
                    long start = Math.Max(0, f.Length - max);
                    f.Position = start;
                    tail = new byte[f.Length - start];
                    int got = 0;
                    while (got < tail.Length) { int n = f.Read(tail, got, tail.Length - got); if (n <= 0) break; got += n; }
                }
                var packed = new MemoryStream();
                using (var z = new DeflateStream(packed, CompressionMode.Compress, leaveOpen: true)) z.Write(tail, 0, tail.Length);
                byte[] all = packed.ToArray();
                const int chunk = 48 * 1024;
                int count = Math.Max(1, (all.Length + chunk - 1) / chunk);
                for (int i = 0; i < count; i++)
                {
                    int off = i * chunk, len = Math.Min(chunk, all.Length - off);
                    var ms = Net.Writer(MsgId);
                    ms.WriteByte(SubLogChunk);
                    var w = new BinaryWriter(ms);
                    w.Write(req); w.Write(i); w.Write(count); w.Write(len);
                    w.Write(all, off, len);
                    Upload.Enqueue(ms.ToArray());
                }
                Plugin.Log.LogInfo($"Report: the host asked for my log, sending {all.Length / 1024} KB in {count} part(s)");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Report: could not read my log: " + ex.Message); }
        }

        private static void PumpLogUpload()
        {
            if (Upload.Count == 0) return;
            if (!Net.Active) { Upload.Clear(); return; }
            for (int i = 0; i < 3 && Upload.Count > 0; i++) Net.Broadcast(Upload.Dequeue(), reliable: true);
        }

        // ---------------- HUD host ----------------

        /// <summary>IMGUI host for the markers (OnGUI needs a MonoBehaviour).</summary>
        private sealed class HudBehaviour : MonoBehaviour
        {
            private static HudBehaviour _instance;

            internal static void Ensure()
            {
                if (_instance != null) return;
                var go = new GameObject("GMSCoopHud");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<HudBehaviour>();
            }

            private void OnGUI()
            {
                if (Event.current.type != EventType.Repaint) return;
                try { DrawMarkers(); } catch { }
            }
        }

        /// <summary>A short two-tone ping, generated (no game asset needed).</summary>
        private static class Beep
        {
            private static AudioSource _src;
            private static AudioClip _clip;

            internal static void Play()
            {
                try
                {
                    if (_src == null)
                    {
                        var go = new GameObject("GMSCoopBeep");
                        UnityEngine.Object.DontDestroyOnLoad(go);
                        _src = go.AddComponent<AudioSource>();
                        _src.spatialBlend = 0f;
                        _src.volume = 0.35f;
                        const int rate = 44100;
                        int n = rate / 5;
                        var data = new float[n];
                        for (int i = 0; i < n; i++)
                        {
                            float t = (float)i / rate;
                            float f = i < n / 2 ? 880f : 1320f;
                            float env = Mathf.Clamp01(1f - (float)i / n) * Mathf.Clamp01(i / 200f);
                            data[i] = Mathf.Sin(2f * Mathf.PI * f * t) * env * 0.6f;
                        }
                        _clip = AudioClip.Create("coop_ping", n, 1, rate, false);
                        _clip.SetData(data, 0);
                    }
                    _src.PlayOneShot(_clip);
                }
                catch { }
            }
        }
    }
}
