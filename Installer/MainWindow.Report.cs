using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace GMSCoopInstaller
{
    /// <summary>Last lines printed by cloudflared (for "Enviar reporte").</summary>
    internal static class CloudflareLog
    {
        private static readonly StringBuilder Tail = new StringBuilder();

        internal static void Add(string line)
        {
            if (line == null) return;
            lock (Tail)
            {
                Tail.AppendLine(DateTime.Now.ToString("HH:mm:ss ") + line);
                if (Tail.Length > 20000) Tail.Remove(0, Tail.Length - 20000);
            }
        }

        internal static string Text { get { lock (Tail) return Tail.ToString(); } }
    }

    /// <summary>Player name (first-run prompt, Ajustes) and the "Enviar reporte" zip.</summary>
    internal sealed partial class MainWindow
    {
        // =====================================================================================
        // Player name
        // =====================================================================================

        internal static string PlayerName => CleanName(Settings.Get("name"));

        /// <summary>Same rules as the mod (--coop-name): no control chars, no quotes or rich-text brackets, max 20.</summary>
        private static string CleanName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var sb = new StringBuilder();
            foreach (char c in s.Trim())
                if (!char.IsControl(c) && c != '"' && c != '<' && c != '>') sb.Append(c);
            string n = sb.ToString().Trim();
            if (n.Length > 20) n = n.Substring(0, 20).Trim();
            return n.Length == 0 ? null : n;
        }

        /// <summary>Name suggestion: the Steam account name of this PC, else the Windows user name.</summary>
        private static string DefaultName()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string n = k?.GetValue("LastGameNameUsed") as string;
                    if (string.IsNullOrWhiteSpace(n)) n = k?.GetValue("AutoLoginUser") as string;
                    if (!string.IsNullOrWhiteSpace(n)) return CleanName(n);
                }
            }
            catch { }
            return CleanName(Environment.UserName) ?? "Minero";
        }

        /// <summary>Launch argument with the chosen name ("" when none).</summary>
        private static string NameArg => PlayerName != null ? " --coop-name \"" + PlayerName + "\"" : "";

        /// <summary>First run: a friendly "¿Cómo te llamas?" with a ready default, so Enter is enough.</summary>
        private void AskNameOnce()
        {
            if (Preview || PlayerName != null) return;
            string name = PromptName(DefaultName());
            Settings.Set("name", name ?? DefaultName());
        }

        private string PromptName(string suggestion)
        {
            var box = Ui.Input("Tu nombre", 20);
            box.Text = suggestion ?? "";
            box.MaxLength = 20;
            var dlg = new Window
            {
                Owner = this, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = Ui.Body,
            };
            string result = null;
            var stack = new StackPanel { Width = 420 };
            stack.Children.Add(new TextBlock { Text = "¿Cómo te llamas?", FontFamily = Ui.Heading, FontSize = 24, Foreground = Ui.Gold });
            stack.Children.Add(new TextBlock { Text = "Así te verán tus amigos en el juego (arriba de tu muñeco y en los marcadores).", Foreground = Ui.Muted, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14) });
            stack.Children.Add(Ui.InputFrame(box));
            Action ok = () => { result = CleanName(box.Text); dlg.Close(); };
            var btn = Ui.Button("Listo", Ui.IconCheck, Ui.GoldColor, ok, 48, 16);
            btn.Margin = new Thickness(0, 14, 0, 0);
            stack.Children.Add(btn);
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) ok(); else if (e.Key == Key.Escape) dlg.Close(); };
            var card = Ui.CardBox(stack, 24);
            card.BorderBrush = Ui.Gold;
            card.BorderThickness = new Thickness(1.5);
            dlg.Content = card;
            dlg.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            dlg.ShowDialog();
            return result;
        }

        /// <summary>The "Tu nombre" card in Ajustes.</summary>
        private UIElement NameCard()
        {
            var box = Ui.Input("Tu nombre", 18);
            box.Text = PlayerName ?? "";
            box.MaxLength = 20;
            box.LostKeyboardFocus += (s, e) =>
            {
                string n = CleanName(box.Text);
                Settings.Set("name", n ?? "");
                Status(n != null ? "Nombre guardado: " + n + " (se usa al abrir el juego)." : "Sin nombre: se usará el de Steam.");
            };
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
            var stack = new StackPanel();
            stack.Children.Add(Ui.InputFrame(box));
            stack.Children.Add(new TextBlock { Text = "Se ve arriba de tu muñeco y en tus marcadores (clic de la rueda del mouse en el juego).", Foreground = Ui.Muted, FontSize = 12.5, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
            return stack;
        }

        // =====================================================================================
        // Report
        // =====================================================================================

        private bool _reporting;

        /// <summary>
        /// One zip on the Desktop with everything needed to debug: the game's BepInEx logs and config, the
        /// logs of every player (when this PC hosts a running session, the mod collects them first), the
        /// launcher settings, versions and the cloudflared output. Opens Explorer with the zip selected.
        /// </summary>
        private void SendReport()
        {
            if (_reporting) return;
            if (_game == null) { Status("No encontré el juego: ve a Ajustes y pulsa Buscar.", Tone.Error); return; }
            _reporting = true;
            Status("Preparando el reporte...", Tone.Busy);
            string game = _game;
            bool running = GameInstall.GameRunning();
            Task.Run(() => BuildReport(game, running, msg => Status(msg, Tone.Busy))).ContinueWith(t =>
            {
                _reporting = false;
                if (t.IsFaulted)
                {
                    Status("No se pudo hacer el reporte: " + (t.Exception?.GetBaseException().Message ?? "error"), Tone.Error);
                    return;
                }
                Status("Reporte listo en el Escritorio: " + Path.GetFileName(t.Result) + ". Mándamelo por WhatsApp o Drive.");
                try { Process.Start("explorer.exe", "/select,\"" + t.Result + "\""); } catch { }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static string BuildReport(string game, bool gameRunning, Action<string> progress)
        {
            string bep = Path.Combine(game, "BepInEx");
            string collected = null, collectNote = "el juego no estaba abierto";

            // Ask the running game for everyone's logs (only a host collects; others answer at once).
            if (gameRunning && Directory.Exists(bep))
            {
                string req = Path.Combine(bep, "gmscoop_report_request.txt");
                string done = Path.Combine(bep, "gmscoop_report_done.txt");
                try { if (File.Exists(done)) File.Delete(done); File.WriteAllText(req, DateTime.Now.ToString("o")); } catch { }
                progress("Pidiendo los logs de los jugadores al juego...");
                DateTime until = DateTime.Now.AddSeconds(25);
                collectNote = "el juego no respondió (¿versión vieja del mod?)";
                while (DateTime.Now < until)
                {
                    System.Threading.Thread.Sleep(500);
                    if (!File.Exists(done)) continue;
                    try
                    {
                        string[] lines = File.ReadAllLines(done);
                        collected = lines.Length > 0 && lines[0].Length > 0 ? lines[0] : null;
                        collectNote = "logs de jugadores: " + (lines.Length > 1 ? lines[1] : "?");
                        File.Delete(done);
                    }
                    catch { }
                    break;
                }
                try { if (File.Exists(req)) File.Delete(req); } catch { }
            }

            progress("Juntando archivos...");
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string zipPath = Path.Combine(desktop, "GMS Co-op-reporte-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm") + ".zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (string name in new[] { "LogOutput.log", "LogOutput.log.1", "LogOutput.log.2" })
                    AddText(zip, Path.Combine(bep, name), "BepInEx/" + name);
                string cfg = Path.Combine(bep, "config");
                if (Directory.Exists(cfg))
                    foreach (string f in Directory.GetFiles(cfg, "*.cfg")) AddText(zip, f, "BepInEx/config/" + Path.GetFileName(f));

                // Players' logs: the folder just collected, plus any collected in the last 2 hours.
                string reports = Path.Combine(bep, "reports");
                if (Directory.Exists(reports))
                    foreach (string dir in Directory.GetDirectories(reports))
                    {
                        bool recent = Directory.GetLastWriteTime(dir) > DateTime.Now.AddHours(-2);
                        bool isCollected = collected != null && string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(collected).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                        if (!recent && !isCollected) continue;
                        foreach (string f in Directory.GetFiles(dir, "*.log"))
                            AddText(zip, f, "jugadores/" + Path.GetFileName(dir) + "/" + Path.GetFileName(f));
                    }

                AddText(zip, Path.Combine(Cloudflare.Folder, "launcher.ini"), "launcher/launcher.ini");
                AddString(zip, "launcher/cloudflared.txt", CloudflareLog.Text);

                var info = new StringBuilder();
                info.AppendLine("Reporte de GMS Co-op " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                info.AppendLine("Launcher / mod: " + GameInstall.VersionText);
                info.AppendLine("Juego: " + GameVersion(game));
                info.AppendLine("Carpeta del juego: " + game);
                info.AppendLine("Windows: " + Environment.OSVersion + (Environment.Is64BitOperatingSystem ? " (64 bits)" : ""));
                info.AppendLine("Juego abierto al hacer el reporte: " + (gameRunning ? "sí" : "no") + " (" + collectNote + ")");
                info.AppendLine("Steam abierto: " + (GameInstall.SteamRunning() ? "sí" : "no"));
                AddString(zip, "info.txt", info.ToString());
            }
            return zipPath;
        }

        private static string GameVersion(string game)
        {
            try
            {
                string exe = Path.Combine(game, GameInstall.GameExe);
                var v = FileVersionInfo.GetVersionInfo(exe);
                string appInfo = Path.Combine(game, "GoldMiningSimulator_Data", "app.info");
                string extra = File.Exists(appInfo) ? " / " + string.Join(" ", File.ReadAllLines(appInfo)) : "";
                return (v.ProductVersion ?? v.FileVersion ?? "?") + extra;
            }
            catch { return "?"; }
        }

        /// <summary>Adds a text file read with sharing (the game may be writing it), Windows user name masked.</summary>
        private static void AddText(ZipArchive zip, string path, string entry)
        {
            try
            {
                if (!File.Exists(path)) return;
                string text;
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(f, Encoding.UTF8, true))
                    text = r.ReadToEnd();
                AddString(zip, entry, text);
            }
            catch { }
        }

        private static void AddString(ZipArchive zip, string entry, string text)
        {
            string user = Environment.UserName;
            if (!string.IsNullOrEmpty(user) && user.Length > 1)
                text = text.Replace("\\Users\\" + user, "\\Users\\<user>").Replace("/Users/" + user, "/Users/<user>");
            ZipArchiveEntry e = zip.CreateEntry(entry, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(text ?? "");
        }
    }
}
