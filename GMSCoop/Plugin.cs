using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GMSCoop
{
    [BepInPlugin(Guid, "GMS Co-op", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "gmscoop.multiplayer";
        public const string Version = "0.14.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        /// <summary>Player name chosen in the launcher (--coop-name), or null for the Steam / PC name.</summary>
        internal static string CustomName;

        private static string CleanName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var sb = new System.Text.StringBuilder();
            foreach (char c in s.Trim())
                if (!char.IsControl(c) && c != '<' && c != '>') sb.Append(c); // '<' '>' would be rich-text tags in the overlay
            string n = sb.ToString().Trim();
            if (n.Length > 20) n = n.Substring(0, 20).Trim();
            return n.Length == 0 ? null : n;
        }

        private Harmony _harmony;
        private const string KeysHelp = "F8: crear partida por Steam | F9: crear partida por LAN | F10: unirse por LAN";
        private string _status = KeysHelp;

        private ConfigEntry<string> _lanHostIp;
        private ConfigEntry<int> _lanPort;

        // Command line (the launcher, GMS Co-op.exe, passes these):
        //   --coop-host              host a LAN game
        //   --coop-steam-host        host a Steam game (as F8) once Steam is ready
        //   --coop-join <ip[:port]>  join a LAN / internet host by address
        //   --coop-join auto         join the first LAN game found (LanDiscovery)
        //   --coop-load <slot>       load that save slot from the main menu (TestProbe.TryAutoLoad)
        //   --coop-lowgfx            lowest graphics (second copy on one PC)
        // Joining a Steam game by code is the game's own "+connect_lobby <id>" (SteamLobby).
        private string _autoMode;
        private string _autoJoinIp;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            HybridTransport.Log = s => Log.LogInfo(s);

            _lanHostIp = Config.Bind("LAN", "HostIp", "127.0.0.1", "IP of the LAN host to join with F10.");
            _lanPort = Config.Bind("LAN", "Port", 27777, "TCP port for LAN games.");
            CoopSaves.Init(Config);

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--coop-host") _autoMode = "host";
                if (args[i] == "--coop-steam-host") _autoMode = "steamhost";
                if (args[i] == "--coop-join" && i + 1 < args.Length) { _autoMode = "join"; _autoJoinIp = args[i + 1]; }
                if (args[i] == "--coop-lowgfx") _lowGfx = true;
                if (args[i] == "--coop-name" && i + 1 < args.Length) CustomName = CleanName(args[i + 1]);
            }
            TestProbe.ParseArgs(args);
            AvatarModels.ParseArgs(args);

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            FocusInput.Apply(_harmony);
            Log.LogInfo("GMS Co-op loaded" + (_autoMode != null ? " (auto " + _autoMode + ")" : ""));
        }

        private bool _steamReady;
        private float _nextSteamCheck;

        private void Update()
        {
            MainThread.Drain();
            CheckSteam();
            try { TestProbe.Tick(); } catch (Exception ex) { Log.LogWarning("Test probe: " + ex.Message); }
            try { LanDiscovery.Tick(_lanPort.Value); } catch (Exception ex) { Log.LogWarning("LAN discovery: " + ex.Message); }
            FocusInput.Tick();
            CoopUi.Tick(); // co-op saves/backups, host-lost prompt, kick list (Ctrl+F6), newer-world offer
            try { Social.Tick(); } catch (Exception ex) { Log.LogWarning("Markers/report: " + ex.Message); }

            // Ctrl+F12, not F12 alone: F12 is Steam's screenshot key, and each dump adds a large block to the log.
            if (Input.GetKeyDown(KeyCode.F12) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                DebugDump.Run();

            if (!Net.Active)
            {
                if (_autoMode == "steamhost")
                {
                    // Wait for the game's SteamManager; if Steam never comes up, say so once and stop waiting.
                    if (_steamReady && Time.unscaledTime > 5f)
                    {
                        SteamLobby.Host();
                        SetStatus("Creando la partida de Steam...");
                        _autoMode = null;
                    }
                    else if (Time.unscaledTime > 60f)
                    {
                        SetStatus("Steam no respondió: no se pudo crear la partida de Steam. Abre Steam o usa LAN (F9).");
                        _autoMode = null;
                    }
                }
                else if (_autoMode != null && Time.unscaledTime > 5f)
                {
                    if (_autoMode == "host") HostLan();
                    else JoinAddress(_autoJoinIp);
                    _autoMode = null;
                }
                else if (Input.GetKeyDown(KeyCode.F8))
                {
                    if (_steamReady) { SteamLobby.Host(); SetStatus("Creando la partida de Steam..."); }
                    else SetStatus("Steam no está listo todavía: prueba en unos segundos o usa LAN (F9 / F10)");
                }
                else if (Input.GetKeyDown(KeyCode.F9)) HostLan();
                else if (Input.GetKeyDown(KeyCode.F10))
                {
                    // No IP configured (the default): find the host on the network instead of asking for one.
                    string ip = (_lanHostIp.Value ?? "").Trim();
                    JoinAddress(ip.Length == 0 || ip == "127.0.0.1" ? "auto" : ip);
                }
            }
            else if (Input.GetKeyDown(KeyCode.F7))
            {
                LanDiscovery.CancelSearch();
                CoopSaves.BeforeHostLeaves(); // host: last save of the shared world
                Net.Shutdown();
                SetStatus("Saliste de la sesión co-op. " + KeysHelp);
            }

            // The game turns runInBackground off by default (OPTIONS_RUN_IN_BACKGROUND), which
            // freezes an unfocused window and stops all networking. Force it on during a session.
            if (Net.Active && !Application.runInBackground) Application.runInBackground = true;

            LimitGpuLoad();

            RunSystem(0, NetPoll);
            for (int i = 0; i < UpdateSystems.Length; i++) RunSystem(i + 1, UpdateSystems[i]);
        }

        // ---------- system tick order ----------
        //
        // Update, every frame, in this order (each one also runs outside a session to clean up after it):
        //  0 Net.Poll           receive + dispatch every packet, keep-alive / timeouts
        //  1 SaveTransfer       world transfer; on a client's load: re-index MachineSync / ObjectSync, Loaded
        //  2 PlayerSync         own pose out, avatars
        //  3 TerrainSync        terrain / voxel diffs (journal for joiners)
        //  4 GoldSync           ledger, clock, workers + SessionLifecycle, ContainerSync, DirtSync, WorldSync/FuelSync
        //  5 MachineSync        vehicles + BedSync (must run before MachinePartsSync / PassengerSync, which use its ids)
        //  6 MachinePartsSync   arms, wheels, engine, light trailer
        //  7 ObjectSync         loose objects, holders, ropes (needs MachineSync's drivers for towed loads)
        //  8 PassengerSync      passenger seats (needs MachineSync's drivers)
        //  9 TutorialSync
        // 10 PurchaseSync       must run after ObjectSync: its queued messages wait for ObjectSync.KeysReady
        // FixedUpdate: MachineSync (bodies) before ObjectSync (loads riding on / tied to them).
        // Each system runs isolated: an exception in one is logged (rate limited) and the others still run.

        private static readonly Action NetPoll = Net.Poll;
        private static readonly Action[] UpdateSystems =
        {
            SaveTransfer.Tick, PlayerSync.Tick, TerrainSync.Tick, GoldSync.Tick, MachineSync.Tick,
            MachinePartsSync.Tick, ObjectSync.Tick, PassengerSync.Tick, TutorialSync.Tick, PurchaseSync.Tick,
        };
        private static readonly string[] SystemNames =
        {
            "Net.Poll", "SaveTransfer", "PlayerSync", "TerrainSync", "GoldSync", "MachineSync",
            "MachinePartsSync", "ObjectSync", "PassengerSync", "TutorialSync", "PurchaseSync",
            "MachineSync.FixedTick", "ObjectSync.FixedTick",
        };
        private static readonly Action[] FixedSystems = { MachineSync.FixedTick, ObjectSync.FixedTick };
        private static readonly float[] NextErrorLog = new float[13];
        private static readonly int[] ErrorCount = new int[13];

        private static void RunSystem(int slot, Action tick)
        {
            try { tick(); }
            catch (Exception ex)
            {
                ErrorCount[slot]++;
                float now = Time.unscaledTime;
                if (now < NextErrorLog[slot]) return;
                NextErrorLog[slot] = now + 10f;
                Log.LogError($"{SystemNames[slot]} failed ({ErrorCount[slot]} time(s) so far): {ex}");
            }
        }

        // ---------- GPU load (two instances on one laptop GPU caused a VIDEO_TDR_FAILURE blue screen) ----------

        private bool _lowGfx;
        private float _nextGfxCheck;

        /// <summary>
        /// --coop-lowgfx: lowest quality preset and 30 FPS for this instance (meant for the second
        /// window when testing two instances on one PC). In any co-op session, an unfocused window
        /// is capped at 15 FPS: networking keeps running, the GPU gets a rest.
        /// </summary>
        private void LimitGpuLoad()
        {
            if (Time.unscaledTime < _nextGfxCheck) return;
            _nextGfxCheck = Time.unscaledTime + 1f;

            if (_lowGfx)
            {
                if (QualitySettings.GetQualityLevel() != 0) QualitySettings.SetQualityLevel(0, true);
                QualitySettings.vSyncCount = 0;
                QualitySettings.shadows = ShadowQuality.Disable;
            }

            bool focused = Application.isFocused;
            int wanted = !focused && Net.Active ? 15 : (_lowGfx ? 30 : -1);
            if (Application.targetFrameRate != wanted) Application.targetFrameRate = wanted;
        }

        private void FixedUpdate()
        {
            for (int i = 0; i < FixedSystems.Length; i++) RunSystem(UpdateSystems.Length + 1 + i, FixedSystems[i]);
        }

        private void CheckSteam()
        {
            // Wait for the game's own SteamManager to init SteamAPI. Do not touch
            // SteamManager.Initialized: its getter creates a second SteamManager if none exists.
            if (_steamReady || Time.unscaledTime < _nextSteamCheck) return;
            _nextSteamCheck = Time.unscaledTime + 1f;
            if (FindObjectOfType<SteamManager>() == null) return;
            try
            {
                SteamLobby.Init();
                _steamReady = true;
                Log.LogInfo("Steam ready");
            }
            catch (Exception ex)
            {
                Log.LogWarning("Steam not available, LAN only: " + ex.Message);
                _steamReady = false;
                _nextSteamCheck = float.MaxValue;
            }
        }

        private void HostLan()
        {
            try
            {
                Net.StartLanHost(_lanPort.Value);
                SetStatus($"Partida LAN creada (puerto {_lanPort.Value}). Esperando jugadores... (F7: salir)");
            }
            catch (Exception ex)
            {
                SetStatus("No se pudo abrir el puerto " + _lanPort.Value + " (¿lo usa otro programa?): " + ex.Message);
            }
        }

        private void JoinLan(string ip, int port)
        {
            SetStatus($"Conectando a {ip}:{port}...");
            Net.StartLanClient(ip, port);
        }

        /// <summary>"auto" = find a LAN game; otherwise "ip", "name", "ip:port" or "[ipv6]:port".</summary>
        private void JoinAddress(string address)
        {
            address = (address ?? "").Trim();
            if (address.Length == 0 || address.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                LanDiscovery.Search((ip, port, name) =>
                {
                    if (Net.Active) return;
                    SetStatus($"Partida encontrada: {name}. Conectando...");
                    Net.StartLanClient(ip, port);
                });
                return;
            }
            int port = _lanPort.Value;
            string host = address;
            int colon = address.LastIndexOf(':');
            // host:port, but not a bare IPv6 address (several ':' without brackets).
            if (colon > 0 && (address.IndexOf(':') == colon || address.StartsWith("[")) &&
                int.TryParse(address.Substring(colon + 1), out int p) && p > 0 && p <= 65535)
            {
                port = p;
                host = address.Substring(0, colon).Trim('[', ']');
            }
            JoinLan(host, port);
        }

        /// <summary>
        /// Shows <paramref name="s"/> in the overlay. Logged unless <paramref name="log"/> is false (progress
        /// lines) or it repeats the current status (periodic reminders must not fill the log).
        /// </summary>
        internal void SetStatus(string s, bool log = true)
        {
            if (s == null || s == _status) return;
            _status = s;
            if (log) Log.LogInfo(s);
        }

        private float _nextOverlay;

        private void LateUpdate()
        {
            if (Time.unscaledTime < _nextOverlay) return;
            _nextOverlay = Time.unscaledTime + 0.25f;

            var sb = new System.Text.StringBuilder();
            sb.Append("<b>[Co-op]</b> ").Append(_status);
            if (Net.Active)
            {
                if (Net.IsHost && SteamLobby.Lobby.IsValid() && Net.Transport?.Name == "Steam")
                    sb.Append($"\nCódigo de partida: {SteamLobby.Lobby.m_SteamID} (para unirse sin invitación: GMS Co-op.exe → Unirse)");
                // Player list: green = packet in the last 2 s, yellow = up to 10 s, red = silent longer.
                // A client only talks to the host (star topology): it lists the host, not the other clients.
                int n = Net.Peers.Count;
                if (Net.IsHost) sb.Append($"\nTú: host ({Net.Transport?.Name}) | {n} {(n == 1 ? "jugador conectado" : "jugadores conectados")}");
                else sb.Append($"\nTú: cliente ({Net.Transport?.Name})");
                foreach (var p in Net.Peers)
                {
                    string state, color;
                    if (Net.LastSeen.TryGetValue(p, out float t))
                    {
                        float ago = Time.unscaledTime - t;
                        state = ago >= 2f ? $"sin datos hace {ago:0} s"
                              : Net.IsHost && !SaveTransfer.PeerReady(p) ? "recibiendo el mundo" : "jugando";
                        color = ago < 2f ? "#4CFF4C" : ago < 10f ? "#FFE14C" : "#FF4C4C";
                    }
                    else
                    {
                        state = "conectando...";
                        color = "#FFE14C";
                    }
                    string who = Net.IsHost ? Net.NameOf(p) : Net.NameOf(p) + " (host)";
                    // Dot = link state; name = that player's color (same as its name tag and markers).
                    string pc = PlayerColors.Hex(PlayerColors.For(Net.LocalIdOf(p)));
                    sb.Append($"\n  <color={color}>●</color> <color={pc}>{who}</color><color={color}>: {state}</color>");
                    // Link quality: round trip (± variation) and packet loss, from the keep-alive pings.
                    // Path to this peer: "Directo" (hole-punched UDP), "Cloudflare", "LAN" or "Internet".
                    string path = Net.LinkLabel(p);
                    if (path != null) sb.Append(path == "Directo" ? "  <color=#4CFF4C>· Directo</color>" : "  · " + path);
                    if (Net.TryGetQuality(p, out float rtt, out float jit, out float loss))
                    {
                        string q = rtt < 120f && jit < 40f && (loss < 0f || loss < 2f) ? "#4CFF4C"
                                 : rtt < 250f && jit < 100f && (loss < 0f || loss < 8f) ? "#FFE14C" : "#FF4C4C";
                        sb.Append($"  <color={q}>{rtt:0} ms ±{jit:0}");
                        sb.Append(loss < 0f ? ", pérdida ...</color>" : $", pérdida {loss:0.#}%</color>");
                    }
                }
            }
            CoopUi.AppendOverlay(sb);
            string hint = Social.HintText;
            if (hint != null) sb.Append("\n<color=#FFD24C>").Append(hint).Append("</color>");
            Overlay.SetText(sb.ToString());
        }

        private void OnDestroy()
        {
            Overlay.Destroy();
            LanDiscovery.StopBeacon();
            LanDiscovery.CancelSearch();
            Net.Shutdown();
            _harmony?.UnpatchSelf();
        }
    }
}
