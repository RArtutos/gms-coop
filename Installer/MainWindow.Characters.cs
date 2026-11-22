using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GMSCoopInstaller
{
    /// <summary>"Tu personaje" gallery (passed to the game as --coop-character) and the asset credits.</summary>
    internal sealed partial class MainWindow
    {
        private sealed class CharacterInfo
        {
            public string Id, Name, Credit;
            public CharacterInfo(string id, string name, string credit) { Id = id; Name = name; Credit = credit; }
        }

        // Same ids as the mod (GMSCoop\AvatarModels.cs). "worker" is the game's own worker model.
        private static readonly CharacterInfo[] Characters =
        {
            new CharacterInfo("worker", "Trabajador del juego", "Modelo del propio juego"),
            new CharacterInfo("q_mannequin", "Maniquí", "Quaternius (CC0)"),
            new CharacterInfo("kk_barbarian", "Bárbaro", "KayKit · Kay Lousberg (CC0)"),
            new CharacterInfo("kk_knight", "Caballero", "KayKit · Kay Lousberg (CC0)"),
            new CharacterInfo("kk_mage", "Mago", "KayKit · Kay Lousberg (CC0)"),
            new CharacterInfo("kk_rogue", "Pícaro", "KayKit · Kay Lousberg (CC0)"),
            new CharacterInfo("kk_rogue_hooded", "Pícaro encapuchado", "KayKit · Kay Lousberg (CC0)"),
        };

        private static string CharacterId
        {
            get
            {
                string id = Settings.Get("character", "worker");
                foreach (CharacterInfo c in Characters) if (c.Id == id) return id;
                return "worker";
            }
        }

        /// <summary>Launch argument with the chosen character ("" for the default game worker).</summary>
        private static string CharacterArg => CharacterId == "worker" ? "" : " --coop-character " + CharacterId;

        private readonly List<Border> _charCards = new List<Border>();

        /// <summary>The gallery card in Ajustes: click a character to pick it.</summary>
        private UIElement CharacterCard()
        {
            _charCards.Clear();
            var wrap = new WrapPanel();
            foreach (CharacterInfo c in Characters) wrap.Children.Add(CharacterTile(c, onPick: null));
            RefreshCharCards();
            var stack = new StackPanel();
            stack.Children.Add(wrap);
            stack.Children.Add(new TextBlock
            {
                Text = "Así te ven tus amigos. Todos tienen tus herramientas en la mano y la pose de cargar. Si a un amigo le falta el personaje, te ve como el trabajador del juego.",
                Foreground = Ui.Muted, FontSize = 12.5, Margin = new Thickness(2, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
            return stack;
        }

        private Border CharacterTile(CharacterInfo c, Action onPick)
        {
            var stack = new StackPanel { Width = 112 };
            var thumb = new Border { Width = 112, Height = 140, CornerRadius = new CornerRadius(10), ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x0D, 0x0A)) };
            BitmapImage img = Thumb(c.Id);
            if (img != null) thumb.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            else thumb.Child = Ui.Icon(Ui.IconUsers, 44, Ui.Gold);
            stack.Children.Add(thumb);
            stack.Children.Add(new TextBlock { Text = c.Name, FontFamily = Ui.Heading, FontSize = 13.5, Foreground = Ui.Text, Margin = new Thickness(2, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center });
            stack.Children.Add(new TextBlock { Text = c.Credit, FontSize = 10.5, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center });
            var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(6), Margin = new Thickness(0, 0, 10, 10), BorderThickness = new Thickness(2), Child = stack, Tag = c.Id, ToolTip = c.Name + " — " + c.Credit };
            Ui.Interactive(card, () =>
            {
                Settings.Set("character", c.Id);
                RefreshCharCards();
                Status("Personaje: " + c.Name + " (se usa al abrir el juego).");
                onPick?.Invoke();
            }, Ui.CardColor, Ui.CardHover);
            _charCards.Add(card);
            return card;
        }

        private void RefreshCharCards()
        {
            string id = CharacterId;
            foreach (Border b in _charCards) b.BorderBrush = (string)b.Tag == id ? Ui.Gold : Brushes.Transparent;
        }

        private static readonly Dictionary<string, BitmapImage> ThumbCache = new Dictionary<string, BitmapImage>();

        private static BitmapImage Thumb(string id)
        {
            if (ThumbCache.TryGetValue(id, out BitmapImage cached)) return cached;
            BitmapImage img = null;
            try
            {
                Stream s = typeof(MainWindow).Assembly.GetManifestResourceStream("thumbs." + id + ".png");
                if (s != null)
                    using (s)
                    {
                        img = new BitmapImage();
                        img.BeginInit();
                        img.CacheOption = BitmapCacheOption.OnLoad;
                        img.StreamSource = s;
                        img.EndInit();
                        img.Freeze();
                    }
            }
            catch { img = null; }
            ThumbCache[id] = img;
            return img;
        }

        /// <summary>First run, right after the name: a quick character pick (the default is fine: just close).</summary>
        private void AskCharacterOnce()
        {
            if (Preview || Settings.Get("character", null) != null) return;
            var dlg = new Window
            {
                Owner = this, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = Ui.Body,
            };
            var stack = new StackPanel { Width = 520 };
            stack.Children.Add(new TextBlock { Text = "Elige tu personaje", FontFamily = Ui.Heading, FontSize = 24, Foreground = Ui.Gold });
            stack.Children.Add(new TextBlock { Text = "Así te verán tus amigos. Lo puedes cambiar después en Ajustes.", Foreground = Ui.Muted, FontSize = 13.5, Margin = new Thickness(0, 6, 0, 12), TextWrapping = TextWrapping.Wrap });
            _charCards.Clear();
            var wrap = new WrapPanel();
            foreach (CharacterInfo c in Characters) wrap.Children.Add(CharacterTile(c, onPick: null));
            stack.Children.Add(wrap);
            RefreshCharCards();
            var btn = Ui.Button("Listo", Ui.IconCheck, Ui.GoldColor, () => dlg.Close(), 48, 16);
            btn.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(btn);
            var card = Ui.CardBox(stack, 24);
            card.BorderBrush = Ui.Gold;
            card.BorderThickness = new Thickness(1.5);
            dlg.Content = card;
            dlg.KeyDown += (s, e) => { if (e.Key == Key.Enter || e.Key == Key.Escape) dlg.Close(); };
            dlg.ShowDialog();
            if (Settings.Get("character", null) == null) Settings.Set("character", "worker");
        }

        // ---------- credits ----------

        private static readonly string[][] Credits =
        {
            new[] { "KayKit Character Pack: Adventurers", "Kay Lousberg (kaylousberg.com)", "CC0 1.0", "https://github.com/KayKit-Game-Assets/KayKit-Character-Pack-Adventures-1.0" },
            new[] { "Universal Animation Library (maniquí y animaciones)", "Quaternius (quaternius.com)", "CC0 1.0", "https://opengameart.org/content/universal-animation-library" },
            new[] { "BepInEx (cargador de mods)", "BepInEx team", "LGPL-2.1", "https://github.com/BepInEx/BepInEx" },
            new[] { "HarmonyX (parches)", "BepInEx team / Andreas Pardeike (Harmony)", "MIT", "https://github.com/BepInEx/HarmonyX" },
            new[] { "cloudflared (túnel)", "Cloudflare", "Apache-2.0", "https://github.com/cloudflare/cloudflared" },
        };

        /// <summary>The "Créditos" card in Ajustes: every third-party asset and tool, with its author, license and link.</summary>
        private UIElement CreditsCard()
        {
            var stack = new StackPanel();
            foreach (string[] c in Credits)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                row.Children.Add(new TextBlock { Text = c[0], FontFamily = Ui.Heading, FontSize = 13.5, Foreground = Ui.Text });
                var link = new TextBlock { Text = c[1] + "  ·  " + c[2] + "  ·  " + c[3], FontSize = 12, Foreground = Ui.Muted, Cursor = Cursors.Hand, TextWrapping = TextWrapping.Wrap };
                string url = c[3];
                link.MouseLeftButtonUp += (s, e) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
                row.Children.Add(link);
                stack.Children.Add(row);
            }
            stack.Children.Add(new TextBlock { Text = "Gold Mining Simulator y su modelo de trabajador pertenecen a sus creadores. Este mod no está afiliado con ellos.", FontSize = 11.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            return Ui.CardBox(stack, 16);
        }
    }
}
