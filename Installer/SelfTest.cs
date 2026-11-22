using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace GMSCoopInstaller
{
    /// <summary>Developer check of the Cloudflare path without the game: echo server ← HostBridge ← tunnel ← ClientBridge ← test client.</summary>
    internal static class SelfTest
    {
        internal static string Cloudflare()
        {
            var log = new StringBuilder();
            Action<string> L = s => { lock (log) log.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); };
            HostBridge host = null;
            ClientBridge client = null;
            var echo = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                echo.Start();
                int echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
                new Thread(() =>
                {
                    while (true)
                    {
                        TcpClient c;
                        try { c = echo.AcceptTcpClient(); } catch { return; }
                        new Thread(() =>
                        {
                            try
                            {
                                var s = c.GetStream();
                                var b = new byte[65536];
                                int n;
                                while ((n = s.Read(b, 0, b.Length)) > 0) s.Write(b, 0, n);
                            }
                            catch { }
                        }) { IsBackground = true }.Start();
                    }
                }) { IsBackground = true }.Start();

                GMSCoopInstaller.Cloudflare.EnsureCloudflared(L);
                L("cloudflared ok");
                host = new HostBridge(echoPort, L);
                string sub = host.Start(TimeSpan.FromSeconds(40));
                L("tunnel: " + sub);
                client = new ClientBridge(sub, L);
                var sw = Stopwatch.StartNew();
                client.Probe(TimeSpan.FromSeconds(40));
                L("probe ok after " + sw.ElapsedMilliseconds + " ms");
                client.Start();
                using (var tc = new TcpClient { NoDelay = true })
                {
                    tc.Connect(IPAddress.Loopback, client.Port);
                    var s = tc.GetStream();
                    s.ReadTimeout = 15000;
                    var rnd = new Random(1);
                    // Small messages: round-trip time. Then 1 MB: throughput and byte integrity.
                    for (int i = 0; i < 5; i++)
                    {
                        byte[] msg = Encoding.ASCII.GetBytes("ping " + i);
                        var t = Stopwatch.StartNew();
                        s.Write(msg, 0, msg.Length);
                        var back = new byte[msg.Length];
                        int got = 0;
                        while (got < back.Length) got += s.Read(back, got, back.Length - got);
                        L($"rtt {i}: {t.ElapsedMilliseconds} ms ({Encoding.ASCII.GetString(back)})");
                    }
                    var big = new byte[1024 * 1024];
                    rnd.NextBytes(big);
                    var t2 = Stopwatch.StartNew();
                    var writer = new Thread(() => s.Write(big, 0, big.Length)) { IsBackground = true };
                    writer.Start();
                    var recv = new byte[big.Length];
                    int r = 0;
                    while (r < recv.Length) { int n = s.Read(recv, r, recv.Length - r); if (n <= 0) break; r += n; }
                    bool same = r == big.Length;
                    for (int i = 0; same && i < big.Length; i++) if (recv[i] != big[i]) same = false;
                    L($"1 MB echo: {r} bytes in {t2.ElapsedMilliseconds} ms, identical: {same}");
                    L(same ? "RESULT PASS" : "RESULT FAIL");
                }
            }
            catch (Exception ex)
            {
                L("RESULT FAIL: " + ex.GetBaseException().Message);
            }
            finally
            {
                client?.Dispose();
                host?.Dispose();
                try { echo.Stop(); } catch { }
            }
            return log.ToString();
        }
    }
}
