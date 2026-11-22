using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Finds LAN games without typing an IP. While a LAN host runs, a background thread broadcasts a
    /// small UDP beacon every second on <see cref="Port"/> (255.255.255.255, each network's own
    /// broadcast address, and 127.0.0.1 for a second copy on the same PC). A joining player (F10
    /// without a configured IP, or --coop-join auto) listens for it and connects to the first host
    /// that runs the same protocol. The launcher (GMS Co-op.exe) listens for the same beacon to list games.
    ///
    /// Beacon (UTF-8 text, '|' separated):
    ///   GMSCOOP1|protocol|mod version|host name|players|tcp port|joinable(1/0)
    /// The beacon is not part of the game protocol (Net.Protocol); the magic word versions its format.
    /// </summary>
    internal static class LanDiscovery
    {
        internal const int Port = 27778;
        internal const string Magic = "GMSCOOP1";
        private const float SearchTimeout = 30f;

        // ---------- host: broadcast ----------

        private static Thread _beaconThread;
        private static volatile bool _beaconRun;
        private static volatile string _beacon; // written by the main thread, read by the beacon thread
        private static float _nextBeaconUpdate;

        /// <summary>Called every frame from Plugin.Update: keeps the beacon running only while hosting a LAN game.</summary>
        internal static void Tick(int tcpPort)
        {
            bool hostingLan = Net.Active && Net.IsHost && Net.Transport != null && Net.Transport.Name == "LAN";
            if (hostingLan)
            {
                if (Time.unscaledTime >= _nextBeaconUpdate)
                {
                    _nextBeaconUpdate = Time.unscaledTime + 1f;
                    string name = Clean(Net.LocalName);
                    _beacon = string.Join("|", Magic, Net.Protocol.ToString(), Plugin.Version, name,
                                          (Net.Peers.Count + 1).ToString(), tcpPort.ToString(), "1");
                }
                if (_beaconThread == null) StartBeacon();
            }
            else if (_beaconThread != null)
            {
                StopBeacon();
            }

            if (_searching) SearchTick();
        }

        private static string Clean(string s) => (s ?? "Host").Replace('|', '/').Replace('\n', ' ');

        private static void StartBeacon()
        {
            _beaconRun = true;
            _beaconThread = new Thread(BeaconLoop) { IsBackground = true, Name = "GMSCoop LAN beacon" };
            _beaconThread.Start();
            Plugin.Log.LogInfo("LAN discovery: announcing this game on UDP port " + Port);
        }

        internal static void StopBeacon()
        {
            _beaconRun = false;
            _beaconThread = null; // the thread ends by itself within a second
        }

        private static void BeaconLoop()
        {
            UdpClient udp = null;
            int errors = 0;
            try
            {
                udp = new UdpClient { EnableBroadcast = true };
                while (_beaconRun)
                {
                    string text = _beacon;
                    if (text != null)
                    {
                        byte[] data = Encoding.UTF8.GetBytes(text);
                        foreach (IPAddress target in BroadcastTargets())
                        {
                            try { udp.Send(data, data.Length, new IPEndPoint(target, Port)); }
                            catch (Exception ex)
                            {
                                // A network without broadcast (VPN adapter, disconnected cable): skip it.
                                if (errors++ < 3) MainThread.Run(() => Plugin.Log.LogWarning($"LAN discovery: cannot announce on {target}: {ex.Message}"));
                            }
                        }
                    }
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                MainThread.Run(() => Plugin.Log.LogWarning("LAN discovery: announcing stopped: " + ex.Message +
                                                           " (players can still join with the host IP)"));
            }
            finally
            {
                try { udp?.Close(); } catch { }
            }
        }

        private static List<IPAddress> _targets;
        private static DateTime _targetsAt;

        /// <summary>255.255.255.255, 127.0.0.1 and the directed broadcast address of every IPv4 network that is up (cached 10 s).</summary>
        private static List<IPAddress> BroadcastTargets()
        {
            if (_targets != null && (DateTime.UtcNow - _targetsAt).TotalSeconds < 10) return _targets;
            var list = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        byte[] ip = ua.Address.GetAddressBytes();
                        byte[] mask = ua.IPv4Mask.GetAddressBytes();
                        if (mask.Length != 4 || (mask[0] == 0 && mask[1] == 0 && mask[2] == 0 && mask[3] == 0)) continue;
                        var b = new byte[4];
                        for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                        var addr = new IPAddress(b);
                        if (!list.Contains(addr)) list.Add(addr);
                    }
                }
            }
            catch { } // Mono without interface info: the global broadcast still works on most networks
            _targets = list;
            _targetsAt = DateTime.UtcNow;
            return list;
        }

        // ---------- client: search and join ----------

        private static volatile bool _searching;
        private static float _searchUntil;
        private static Thread _listenThread;
        private static Action<string, int, string> _onFound;
        private static readonly object FoundLock = new object();
        private static string _foundIp;
        private static int _foundPort;
        private static string _foundName;
        private static string _skipped; // last incompatible host seen (for the status line)

        internal static bool Searching => _searching;

        /// <summary>
        /// Listens for a LAN host for up to 30 s, then calls <paramref name="onFound"/>(ip, port, name) on the
        /// main thread. Cancel with <see cref="CancelSearch"/>.
        /// </summary>
        internal static void Search(Action<string, int, string> onFound)
        {
            CancelSearch();
            _onFound = onFound;
            lock (FoundLock) { _foundIp = null; _skipped = null; }
            _searching = true;
            _searchUntil = Time.unscaledTime + SearchTimeout;
            _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "GMSCoop LAN search" };
            _listenThread.Start();
            Plugin.Instance.SetStatus("Buscando partida en la red...");
        }

        internal static void CancelSearch()
        {
            _searching = false;
            _listenThread = null;
        }

        private static void SearchTick()
        {
            string ip, name;
            int port;
            lock (FoundLock) { ip = _foundIp; port = _foundPort; name = _foundName; }
            if (ip != null)
            {
                CancelSearch();
                Plugin.Log.LogInfo($"LAN discovery: found {name} at {ip}:{port}");
                _onFound?.Invoke(ip, port, name);
                return;
            }
            if (Net.Active) { CancelSearch(); return; } // a session started another way meanwhile
            if (Time.unscaledTime >= _searchUntil)
            {
                CancelSearch();
                string skipped;
                lock (FoundLock) skipped = _skipped;
                Plugin.Instance.SetStatus(skipped != null
                    ? "Solo encontré una partida con otra versión del mod (" + skipped + "). Todos necesitan la misma versión."
                    : "No encontré partidas en la red. ¿El host ya creó la partida (F9)? Revisa que estén en la misma red y que el firewall permita el juego.");
            }
        }

        private static void ListenLoop()
        {
            UdpClient udp = null;
            try
            {
                udp = new UdpClient();
                // Share the port with the launcher or a second copy of the game on this PC.
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                udp.Client.ReceiveTimeout = 500;
                while (_searching)
                {
                    IPEndPoint from = null;
                    byte[] data;
                    try { data = udp.Receive(ref from); }
                    catch (SocketException) { continue; } // timeout: check _searching again
                    if (!TryParse(data, out int protocol, out string version, out string name, out int port, out bool joinable)) continue;
                    lock (FoundLock)
                    {
                        if (protocol != Net.Protocol) { _skipped = name + ", " + version; continue; }
                        if (!joinable || _foundIp != null) continue;
                        _foundIp = from.Address.ToString();
                        _foundPort = port;
                        _foundName = name;
                    }
                }
            }
            catch (Exception ex)
            {
                MainThread.Run(() =>
                {
                    Plugin.Log.LogWarning("LAN discovery: cannot listen on UDP port " + Port + ": " + ex.Message);
                    if (_searching)
                    {
                        CancelSearch();
                        Plugin.Instance.SetStatus("No pude buscar partidas en la red (" + ex.Message + "). Únete con la IP del host.");
                    }
                });
            }
            finally
            {
                try { udp?.Close(); } catch { }
            }
        }

        internal static bool TryParse(byte[] data, out int protocol, out string version, out string name, out int port, out bool joinable)
        {
            protocol = 0; version = name = null; port = 0; joinable = false;
            if (data == null || data.Length < 8 || data.Length > 512) return false;
            string[] p;
            try { p = Encoding.UTF8.GetString(data).Split('|'); } catch { return false; }
            if (p.Length < 7 || p[0] != Magic) return false;
            if (!int.TryParse(p[1], out protocol) || !int.TryParse(p[5], out port) || port <= 0 || port > 65535) return false;
            version = p[2];
            name = p[3];
            joinable = p[6] == "1";
            return true;
        }
    }
}
