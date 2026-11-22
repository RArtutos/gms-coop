using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GMSCoopInstaller
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Settings.Load();
            string[] args = Environment.GetCommandLineArgs();
            // --preview <out.png> [home|create|join|settings]: render a view to a PNG without showing a window
            // (design checks; no network, no install).
            int pi = Array.IndexOf(args, "--preview");
            if (pi >= 0 && pi + 1 < args.Length)
            {
                MainWindow.Preview = true;
                var w = new MainWindow();
                w.RenderTo(args[pi + 1], pi + 2 < args.Length ? args[pi + 2] : "home");
                return;
            }
            // --selftest-cloudflare <out.txt>: tunnel round trip through a local echo server (no game, no window).
            int ti = Array.IndexOf(args, "--selftest-cloudflare");
            if (ti >= 0 && ti + 1 < args.Length) { File.WriteAllText(args[ti + 1], SelfTest.Cloudflare()); return; }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show("Error inesperado: " + e.Exception.Message, "GMS Co-op", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            app.Run(new MainWindow());
        }
    }

    /// <summary>
    /// GMS Co-op launcher: installs/updates the mod by itself, creates or joins games (Steam, Cloudflare
    /// tunnel, LAN with auto-discovery, or an IP for VPN users) and starts the game with the right flags.
    /// Borderless WPF window built in code; game art comes from the player's own Steam cache.
    /// </summary>
    internal sealed partial class MainWindow : Window
    {
        private enum HostMode { Steam, Cloudflare, Lan }

        private string _game;
        private readonly ContentControl _content = new ContentControl();
        private readonly Dictionary<string, Border> _nav = new Dictionary<string, Border>();
        private readonly TextBlock _statusText = new TextBlock();
        private readonly ProgressBar _statusBar = new ProgressBar();
        private readonly Border _statusIcon = new Border();
        internal static bool Preview;
        private readonly LanBrowser _lan = Preview ? null : new LanBrowser();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private bool _busy;

        // Running session
        private Process _gameProcess;
        private HostBridge _hostBridge;
        private ClientBridge _clientBridge;
        private string _inviteCode;

        // Create view state
        private string _slot;        // null = choose in game
        private HostMode _mode = HostMode.Steam;
        private bool LowGfx => Settings.Get("lowgfx") == "1";

        public MainWindow()
        {
            Title = "GMS Co-op";
            Icon = Ui.AppIcon();
            Width = 1000;
            // Fit small screens / high display scaling: never taller than the work area. The home cards
            // adapt (fewer save rows), so a shorter window still shows everything important.
            Height = Math.Max(600, Math.Min(770, SystemParameters.WorkArea.Height - 16));
            MinWidth = 1000;
            MinHeight = Height;
            if (Environment.GetEnvironmentVariable("GMSCOOP_PREVIEW_HEIGHT") is string ph && double.TryParse(ph, out double phv)) { Height = phv; MinHeight = phv; }
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Ui.Body;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            string saved = Settings.Get("game");
            _game = GameInstall.IsGameDir(saved) ? saved : SafeFindGame();
            _slot = Settings.Get("slot", null);
            if (string.IsNullOrEmpty(_slot)) _slot = null; // "" = choose in game
            if (Enum.TryParse(Settings.Get("mode", "Steam"), out HostMode m)) _mode = m;

            Content = BuildShell();
            Show("home");

            if (Preview) return;
            _timer.Tick += (s, e) => OnTimer();
            _timer.Start();
            Loaded += (s, e) => AutoInstall();
            Loaded += (s, e) => CheckForUpdate(); // MainWindow.Update.cs
            Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(() => { AskNameOnce(); AskCharacterOnce(); }), System.Windows.Threading.DispatcherPriority.ApplicationIdle); // MainWindow.Report.cs, MainWindow.Characters.cs
            Closing += OnClosing;
        }

        internal void RenderTo(string file, string view)
        {
            // "home-code" / "create-code": the same view with a sample invite code (design check).
            if (view.EndsWith("-code")) { _inviteCode = "CF-leaves-looked-instructions-gentle"; _mode = HostMode.Cloudflare; view = view.Substring(0, view.Length - 5); }
            Show(view);
            var root = (FrameworkElement)Content;
            root.Measure(new Size(Width, Height));
            root.Arrange(new Rect(0, 0, Width, Height));
            root.UpdateLayout();
            // Skip the fade-in: render the final state.
            ClearFades(_content.Content as DependencyObject);
            root.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(new Border { Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)) });
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(file)) enc.Save(f);
        }

        private static void ClearFades(DependencyObject d)
        {
            if (d == null) return;
            if (d is UIElement fe) { fe.BeginAnimation(UIElement.OpacityProperty, null); fe.Opacity = 1; if (fe.RenderTransform is TranslateTransform) fe.RenderTransform = null; }
            foreach (object c in LogicalTreeHelper.GetChildren(d)) ClearFades(c as DependencyObject);
        }

        /// <summary>The banner gets shorter on short windows (small screens / high scaling), down to 170 px.</summary>
        private double HeroHeight => Math.Max(170, Math.Min(230, Height - 540));

        private static string SafeFindGame()
        {
            try { return GameInstall.FindGame(); } catch { return null; }
        }

        // =====================================================================================
        // Shell: hero banner, navigation, content, status strip
        // =====================================================================================

        private UIElement BuildShell()
        {
            var root = new Border
            {
                Margin = new Thickness(14),
                CornerRadius = new CornerRadius(18),
                Background = Ui.Bg,
                BorderBrush = Ui.Line,
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.7 },
            };
            var grid = new Grid { ClipToBounds = true };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeroHeight) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            root.Child = new Border { CornerRadius = new CornerRadius(18), ClipToBounds = true, Child = grid };
            // Rounded clip for the hero image.
            grid.Loaded += (s, e) => grid.Clip = new RectangleGeometry(new Rect(0, 0, grid.ActualWidth, grid.ActualHeight), 17, 17);
            grid.SizeChanged += (s, e) => grid.Clip = new RectangleGeometry(new Rect(0, 0, grid.ActualWidth, grid.ActualHeight), 17, 17);

            grid.Children.Add(BuildHero());

            _content.Margin = new Thickness(28, 18, 28, 8);
            Grid.SetRow(_content, 1);
            grid.Children.Add(_content);

            var status = BuildStatus();
            Grid.SetRow(status, 2);
            grid.Children.Add(status);
            AddUpdateBanner(grid); // MainWindow.Update.cs
            return root;
        }

        private UIElement BuildHero()
        {
            var hero = new Grid();
            BitmapSource art = Ui.LoadImage(SteamArt.Hero, 1400) ?? Ui.LoadImage(SteamArt.Header, 1400);
            hero.Children.Add(new Image { Source = art ?? Ui.ProceduralHero(), Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center });
            // Darken toward the bottom so text and tabs stay readable, and blend into the page.
            hero.Children.Add(new Border
            {
                Background = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x55, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(0x20, 0, 0, 0), 0.35),
                    new GradientStop(Color.FromArgb(0xF0, Ui.BgColor.R, Ui.BgColor.G, Ui.BgColor.B), 0.92),
                    new GradientStop(Ui.BgColor, 1),
                }, 90),
            });

            // Title: the game's logo from the Steam cache, else text.
            var title = new StackPanel { Margin = new Thickness(30, HeroHeight >= 220 ? 30 : 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            BitmapImage logo = Ui.LoadImage(SteamArt.Logo, 520);
            if (logo != null)
                title.Children.Add(new Image { Source = logo, Height = HeroHeight >= 220 ? 92 : 64, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform,
                                               Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.8 } });
            else
                title.Children.Add(new TextBlock { Text = "GOLD MINING SIMULATOR", FontFamily = Ui.Heading, FontSize = 34, Foreground = Ui.Text,
                                                   Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.9 } });
            var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 10, 0, 0) };
            sub.Children.Add(new Border
            {
                Background = Ui.Gold,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 3, 10, 3),
                Child = new TextBlock { Text = "CO-OP", FontFamily = Ui.Heading, FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x15, 0x05)) },
            });
            sub.Children.Add(new TextBlock { Text = "  multijugador para jugar con tus amigos  ·  v" + GameInstall.VersionText,
                                             Foreground = Ui.Text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                                             Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.9 } });
            title.Children.Add(sub);
            hero.Children.Add(title);

            // Drag area + window buttons.
            var drag = new Border { Background = Brushes.Transparent, Height = 44, VerticalAlignment = VerticalAlignment.Top };
            drag.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 1) DragMove(); };
            hero.Children.Add(drag);
            var winButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 12, 0) };
            winButtons.Children.Add(WindowButton(Ui.IconMin, () => WindowState = WindowState.Minimized, Color.FromArgb(0x60, 0, 0, 0), Color.FromArgb(0xA0, 0x40, 0x40, 0x40)));
            winButtons.Children.Add(WindowButton(Ui.IconClose, Close, Color.FromArgb(0x60, 0, 0, 0), Color.FromRgb(0xC0, 0x3A, 0x30)));
            hero.Children.Add(winButtons);

            // Navigation tabs.
            var nav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(26, 0, 0, 10) };
            nav.Children.Add(NavTab("home", "Inicio", Ui.IconHome));
            nav.Children.Add(NavTab("create", "Crear partida", Ui.IconPick));
            nav.Children.Add(NavTab("join", "Unirse", Ui.IconUsers));
            nav.Children.Add(NavTab("settings", "Ajustes", Ui.IconGear));
            hero.Children.Add(nav);
            return hero;
        }

        private static Border WindowButton(string icon, Action click, Color normal, Color hover)
        {
            var b = new Border { Width = 34, Height = 30, CornerRadius = new CornerRadius(8), Margin = new Thickness(4, 0, 0, 0),
                                 Child = Ui.Icon(icon, 13, Brushes.White) };
            Ui.Interactive(b, click, normal, hover);
            return b;
        }

        private Border NavTab(string key, string text, string icon)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = Ui.Icon(icon, 16, Ui.Text);
            var tx = new TextBlock { Text = text, FontFamily = Ui.Heading, FontSize = 15, Foreground = Ui.Text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(ic);
            row.Children.Add(tx);
            var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0), Child = row, Tag = new object[] { ic, tx } };
            Ui.Interactive(b, () => Show(key),
                           () => _view == key ? Ui.GoldColor : Color.FromArgb(0x70, 0, 0, 0),
                           () => _view == key ? Ui.Lighten(Ui.GoldColor, 0.15) : Color.FromArgb(0xB0, 0x30, 0x26, 0x18));
            _nav[key] = b;
            return b;
        }

        private void SelectTab(string key)
        {
            foreach (var kv in _nav)
            {
                bool on = kv.Key == key;
                var parts = (object[])kv.Value.Tag;
                Brush fg = on ? new SolidColorBrush(Color.FromRgb(0x1E, 0x15, 0x05)) : Ui.Text;
                ((System.Windows.Shapes.Path)parts[0]).Fill = fg;
                ((TextBlock)parts[1]).Foreground = fg;
                var bg = (SolidColorBrush)kv.Value.Background;
                bg.BeginAnimation(SolidColorBrush.ColorProperty, null);
                bg.Color = on ? Ui.GoldColor : Color.FromArgb(0x70, 0, 0, 0);
            }
        }

        private UIElement BuildStatus()
        {
            var g = new Grid { Margin = new Thickness(28, 0, 28, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            _statusIcon.Child = Ui.Icon(Ui.IconCheck, 16, Ui.Green);
            _statusIcon.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(_statusIcon);
            _statusText.Foreground = Ui.Muted;
            _statusText.FontSize = 13.5;
            _statusText.VerticalAlignment = VerticalAlignment.Center;
            _statusText.Margin = new Thickness(10, 0, 10, 0);
            _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
            // On an error, a click on the message makes the report (MainWindow.Report.cs).
            _statusText.MouseLeftButtonUp += (s, e) => { if (_statusText.Foreground == Ui.Red) SendReport(); };
            Grid.SetColumn(_statusText, 1);
            g.Children.Add(_statusText);
            _statusBar.Height = 5;
            _statusBar.IsIndeterminate = true;
            _statusBar.Foreground = Ui.Gold;
            _statusBar.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xF2, 0xB6, 0x3D));
            _statusBar.BorderThickness = new Thickness(0);
            _statusBar.VerticalAlignment = VerticalAlignment.Center;
            _statusBar.Visibility = Visibility.Hidden;
            Grid.SetColumn(_statusBar, 2);
            g.Children.Add(_statusBar);
            return g;
        }

        private enum Tone { Ok, Busy, Warn, Error }

        private void Status(string text, Tone tone = Tone.Ok)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => Status(text, tone))); return; }
            _statusText.Text = text;
            _statusText.ToolTip = tone == Tone.Error ? text + "\n(Clic aquí para enviar un reporte)" : text;
            _statusText.Cursor = tone == Tone.Error ? Cursors.Hand : null;
            _statusText.Foreground = tone == Tone.Error ? Ui.Red : tone == Tone.Warn ? Ui.Gold : Ui.Muted;
            _statusBar.Visibility = tone == Tone.Busy ? Visibility.Visible : Visibility.Hidden;
            string icon = tone == Tone.Ok ? Ui.IconCheck : tone == Tone.Busy ? Ui.IconCloud : Ui.IconWarn;
            Brush color = tone == Tone.Ok ? Ui.Green : tone == Tone.Busy ? Ui.Gold : tone == Tone.Warn ? Ui.Gold : Ui.Red;
            _statusIcon.Child = Ui.Icon(icon, 16, color);
        }

        private string _view;

        private void Show(string key)
        {
            _view = key;
            SelectTab(key);
            UIElement v;
            switch (key)
            {
                case "create": v = CreateView(); break;
                case "join": v = JoinView(); break;
                case "settings": v = SettingsView(); break;
                default: v = HomeView(); break;
            }
            _content.Content = v;
            Ui.FadeIn(v);
        }

        // =====================================================================================
        // Views
        // =====================================================================================

        private UIElement HomeView()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ----- Create card: pick a recent save + how friends connect, then one click -----
            // A grid sized by the window: the save list takes the space that is left and simply shows
            // fewer rows when it does not fit (FitStackPanel), so nothing is cut and no scroll bar appears.
            var create = new Grid();
            for (int r = 0; r < 7; r++)
                create.RowDefinitions.Add(new RowDefinition { Height = r == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            void AddRow(UIElement e, int row) { Grid.SetRow(e, row); create.Children.Add(e); }
            AddRow(CardHead(Ui.IconPick, "Crear partida"), 0);
            AddRow(new TextBlock { Text = "Elige la partida y cómo entran tus amigos.", Foreground = Ui.Muted, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) }, 1);

            _homeHasCode = _inviteCode != null;
            var slots = SaveSlots.List().OrderByDescending(x => x.When).ToList();
            if (_slot != null && !slots.Any(x => x.Name == _slot)) _slot = null;
            _homeSlotRows.Clear();
            if (!_homeHasCode)
            {
                // The selected choice first (never hidden), then the most recent saves, then "choose in game".
                var slotList = new FitStackPanel();
                SaveSlot selected = _slot != null ? slots.First(x => x.Name == _slot) : null;
                slotList.Children.Add(SlotRow(_slot, selected));
                foreach (SaveSlot sl in slots.Where(x => x.Name != _slot).Take(4)) slotList.Children.Add(SlotRow(sl.Name, sl));
                if (_slot != null) slotList.Children.Add(SlotRow(null, null));
                AddRow(slotList, 2);
                RefreshSlotRows();
                if (slots.Count > 1)
                    AddRow(LinkText("Ver todas mis partidas (" + slots.Count + ")", () => Show("create")), 3);

                var chips = new UniformGrid { Columns = 3, Margin = new Thickness(0, 10, 0, 12) };
                _modeChips.Clear();
                chips.Children.Add(ModeChip(HostMode.Steam, Ui.IconSteam, "Steam"));
                chips.Children.Add(ModeChip(HostMode.Cloudflare, Ui.IconCloud, "Cloudflare"));
                chips.Children.Add(ModeChip(HostMode.Lan, Ui.IconWifi, "Red local"));
                AddRow(chips, 4);
                RefreshModeChips();
            }
            else
            {
                AddRow(new TextBlock { Text = "Partida en curso: " + (_slot ?? "elegida en el juego") + "  ·  " + ModeName(_mode), Foreground = Ui.Text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), VerticalAlignment = VerticalAlignment.Top }, 2);
            }

            var createBtn = Ui.Button("Crear partida", Ui.IconPlay, Ui.GoldColor, Host, 54, 18);
            createBtn.ToolTip = "El avance de todos se guarda en la partida del host. Durante la partida: F7 sale, Ctrl+F6 saca a un jugador.";
            AddRow(createBtn, 5);
            _invitePanel = new Border { Margin = new Thickness(0, 10, 0, 0) };
            AddRow(_invitePanel, 6);
            UpdateInvitePanel();
            g.Children.Add(Ui.CardBox(create, 22));

            // ----- Join card: code box with paste, and the games found on the network -----
            var join = new StackPanel();
            join.Children.Add(CardHead(Ui.IconUsers, "Unirse"));
            join.Children.Add(new TextBlock { Text = "Pega el código que te pasó tu amigo. En la misma red, déjalo vacío y se encuentra solo.", Foreground = Ui.Muted, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14) });
            var code = Ui.Input("Código de partida o IP", 16);
            code.Text = Settings.Get("code");
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Ui.InputFrame(code));
            var paste = Ui.Button("Pegar", Ui.IconCopy, Color.FromRgb(0x3A, 0x30, 0x22), () =>
            {
                string t = null;
                try { if (Clipboard.ContainsText()) t = Clipboard.GetText().Trim(); } catch { }
                if (string.IsNullOrEmpty(t)) { Status("El portapapeles está vacío: copia primero el código que te mandaron.", Tone.Warn); return; }
                code.Text = t;
                code.Focus();
                code.CaretIndex = t.Length;
            }, 48, 14);
            paste.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(paste, 1);
            row.Children.Add(paste);
            code.KeyDown += (s, e) => { if (e.Key == Key.Enter) Join(code.Text); };
            join.Children.Add(row);
            var joinBtn = Ui.Button("Unirse", Ui.IconLink, Color.FromRgb(0x3E, 0x8E, 0xDE), () => Join(code.Text), 54, 18);
            joinBtn.Margin = new Thickness(0, 12, 0, 0);
            join.Children.Add(joinBtn);
            join.Children.Add(CoopNote("Juegas en el mundo del host: tu avance se guarda en su partida, y en tu PC queda una copia de respaldo (Coop_Respaldo_...)."));

            join.Children.Add(new TextBlock { Text = "Partidas en tu red", FontFamily = Ui.Heading, FontSize = 15.5, Foreground = Ui.Gold, Margin = new Thickness(2, 20, 0, 8) });
            _homeLanLine = new TextBlock { Foreground = Ui.Muted, FontSize = 13, Margin = new Thickness(2, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
            join.Children.Add(_homeLanLine);
            _homeLanList = new StackPanel();
            join.Children.Add(_homeLanList);
            _homeLanKeys = null;
            UpdateHomeLan();
            var c2 = Ui.CardBox(new ScrollViewer { Content = join, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }, 22);
            Grid.SetColumn(c2, 2);
            g.Children.Add(c2);
            return g;
        }

        private static StackPanel CardHead(string icon, string title)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Ui.Icon(icon, 28, Ui.Gold));
            head.Children.Add(new TextBlock { Text = title, FontFamily = Ui.Heading, FontSize = 25, Foreground = Ui.Text, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return head;
        }

        private static TextBlock LinkText(string text, Action click)
        {
            var t = new TextBlock { Text = text + "  →", Foreground = Ui.Gold, FontSize = 13, Margin = new Thickness(4, 6, 0, 0), Cursor = Cursors.Hand };
            t.MouseLeftButtonUp += (s, e) => click();
            t.MouseEnter += (s, e) => t.TextDecorations = TextDecorations.Underline;
            t.MouseLeave += (s, e) => t.TextDecorations = null;
            return t;
        }

        private readonly List<Border> _homeSlotRows = new List<Border>();

        /// <summary>One compact save row on the home card (thumbnail, name, date); null = choose in the game.</summary>
        private Border SlotRow(string name, SaveSlot s)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var thumb = new Border { Width = 60, Height = 34, CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x0D, 0x0A)) };
            BitmapImage img = s != null ? Ui.LoadImage(s.Screenshot, 160) : null;
            if (img != null) thumb.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            else thumb.Child = Ui.Icon(s == null ? Ui.IconPlay : Ui.IconPick, 18, Ui.Muted);
            grid.Children.Add(thumb);
            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = s == null ? "Elegir en el juego" : s.Name, FontFamily = Ui.Heading, FontSize = 14.5, Foreground = Ui.Text, TextTrimming = TextTrimming.CharacterEllipsis });
            string info = s == null ? "Cargas o empiezas una partida dentro del juego"
                        : FriendlyDate(s.When) + (s.Days != null ? "  ·  día " + s.Days : "") + (s.Cash != null ? "  ·  $" + s.Cash : "");
            text.Children.Add(new TextBlock { Text = info, FontSize = 12, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
            if (s?.CoopLine != null) text.Children.Add(new TextBlock { Text = s.CoopLine, FontSize = 11.5, Foreground = Ui.Gold, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 4), BorderThickness = new Thickness(2), Child = grid, Tag = name };
            Ui.Interactive(row, () => { _slot = name; Settings.Set("slot", name ?? ""); RefreshSlotRows(); }, Ui.CardColor, Ui.CardHover);
            _homeSlotRows.Add(row);
            return row;
        }

        private void RefreshSlotRows()
        {
            foreach (Border r in _homeSlotRows) r.BorderBrush = (string)r.Tag == _slot ? Ui.Gold : Brushes.Transparent;
        }

        private static string FriendlyDate(DateTime when)
        {
            DateTime today = DateTime.Today;
            if (when.Date == today) return "hoy " + when.ToString("HH:mm");
            if (when.Date == today.AddDays(-1)) return "ayer " + when.ToString("HH:mm");
            return when.ToString("dd/MM/yyyy");
        }

        private readonly List<Border> _modeChips = new List<Border>();

        private Border ModeChip(HostMode mode, string icon, string title)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(Ui.Icon(icon, 16, Ui.Gold));
            sp.Children.Add(new TextBlock { Text = title, FontSize = 13.5, Foreground = Ui.Text, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var chip = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(8), Margin = new Thickness(0, 0, 6, 0), BorderThickness = new Thickness(2), Child = sp, Tag = mode };
            chip.ToolTip = mode == HostMode.Steam ? "Amigos con su propia cuenta de Steam (invitación o código)."
                         : mode == HostMode.Cloudflare ? "Cualquier internet, sin abrir puertos: te da un código CF-..."
                         : "Misma casa o misma VPN: te encuentran solos.";
            Ui.Interactive(chip, () => { _mode = mode; Settings.Set("mode", mode.ToString()); RefreshModeChips(); UpdateInvitePanel(); }, Ui.CardColor, Ui.CardHover);
            _modeChips.Add(chip);
            return chip;
        }

        private void RefreshModeChips()
        {
            foreach (Border c in _modeChips) c.BorderBrush = (HostMode)c.Tag == _mode ? Ui.Gold : Brushes.Transparent;
        }

        private TextBlock _homeLanLine;
        private StackPanel _homeLanList;
        private string _homeLanKeys;

        private void UpdateHomeLan()
        {
            if (_homeLanLine == null || _homeLanList == null) return;
            List<LanHost> hosts = _lan?.Snapshot() ?? new List<LanHost>();
            string keys = string.Join(",", hosts.Select(h => h.Key + "/" + h.Players + "/" + h.PingMs));
            if (keys == _homeLanKeys) return;
            _homeLanKeys = keys;
            _homeLanLine.Text = "Buscando... (el host crea la partida en modo \"Red local\")";
            _homeLanLine.Visibility = hosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _homeLanList.Children.Clear();
            foreach (LanHost h in hosts.Take(4))
            {
                bool sameVersion = h.Version == GameInstall.VersionText;
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = Ui.Icon(Ui.IconWifi, 18, sameVersion ? Ui.Green : Ui.Red);
                icon.VerticalAlignment = VerticalAlignment.Center;
                grid.Children.Add(icon);
                var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
                text.Children.Add(new TextBlock { Text = h.Name, FontFamily = Ui.Heading, FontSize = 14.5, Foreground = Ui.Text, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = h.Players + (h.Players == 1 ? " jugador" : " jugadores") + "  ·  " + (h.PingMs >= 0 ? h.PingMs + " ms" : "— ms"), FontSize = 12, Foreground = Ui.Muted });
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);
                var badge = Ui.Badge(sameVersion ? "Entrar" : "otra versión", sameVersion ? Ui.Green : Ui.Red);
                badge.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(badge, 2);
                grid.Children.Add(badge);
                var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6), Child = grid, BorderBrush = Ui.Line, BorderThickness = new Thickness(1) };
                LanHost target = h;
                Ui.Interactive(card, () =>
                {
                    if (!sameVersion) { CheckForUpdateNow(target.Version); return; } // MainWindow.Update.cs
                    if (!Prepare(out string err)) { if (err != null) Status(err, Tone.Error); return; }
                    StopBridges();
                    Launch("--coop-join " + target.Ip + ":" + target.Port, "Entrando a la partida de " + target.Name + "...");
                }, Ui.CardColor, Ui.CardHover);
                _homeLanList.Children.Add(card);
                Ui.FadeIn(card, 200, 6);
            }
        }

        private static string ModeName(HostMode m) =>
            m == HostMode.Steam ? "por Steam" : m == HostMode.Cloudflare ? "por Cloudflare" : "red local";

        // ---------- Create ----------

        private readonly List<Border> _slotCards = new List<Border>();
        private readonly List<Border> _modeCards = new List<Border>();
        private Border _invitePanel;

        private UIElement CreateView()
        {
            var page = new StackPanel();
            page.Children.Add(Section("1. Elige la partida"));

            var slots = SaveSlots.List();
            if (_slot != null && !slots.Any(x => x.Name == _slot)) _slot = null;
            var strip = new StackPanel { Orientation = Orientation.Horizontal };
            _slotCards.Clear();
            strip.Children.Add(SlotCard(null, null));
            foreach (SaveSlot s in slots) strip.Children.Add(SlotCard(s.Name, s));
            var scroll = new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 4, 0, 8) };
            scroll.PreviewMouseWheel += (s, e) => { scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta); e.Handled = true; };
            page.Children.Add(scroll);
            RefreshSlotCards();

            page.Children.Add(Section("2. ¿Cómo se conectan tus amigos?"));
            var modes = new UniformGrid { Columns = 3 };
            _modeCards.Clear();
            modes.Children.Add(ModeCard(HostMode.Steam, Ui.IconSteam, "Steam", "Amigos con su propia cuenta de Steam. Invítalos con Shift+Tab o pásales el código."));
            modes.Children.Add(ModeCard(HostMode.Cloudflare, Ui.IconCloud, "Cloudflare", "Funciona con cualquier internet, sin abrir puertos. Te da un código para pasar."));
            modes.Children.Add(ModeCard(HostMode.Lan, Ui.IconWifi, "Red local / VPN", "Misma casa o misma VPN (Tailscale, ZeroTier, Radmin). Te encuentran solos."));
            page.Children.Add(modes);
            RefreshModeCards();

            var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var go = Ui.Button("Crear partida", Ui.IconPlay, Ui.GoldColor, Host, 52, 18);
            go.MinWidth = 260;
            bottom.Children.Add(go);
            _invitePanel = new Border { Margin = new Thickness(18, 0, 0, 0) };
            Grid.SetColumn(_invitePanel, 1);
            bottom.Children.Add(_invitePanel);
            page.Children.Add(bottom);
            UpdateInvitePanel();

            // Short windows: the whole page shrinks uniformly to fit (no scroll bar, nothing cut).
            page.Width = 944;
            return new Viewbox { Child = page, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        }

        private static TextBlock Section(string text) =>
            new TextBlock { Text = text, FontFamily = Ui.Heading, FontSize = 17, Foreground = Ui.Gold, Margin = new Thickness(2, 6, 0, 8) };

        private Border SlotCard(string name, SaveSlot s)
        {
            var stack = new StackPanel();
            var thumb = new Border { Width = 150, Height = 84, CornerRadius = new CornerRadius(10), ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x0D, 0x0A)) };
            BitmapImage img = s != null ? Ui.LoadImage(s.Screenshot, 380) : null;
            if (img != null)
                thumb.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            else
                thumb.Child = Ui.Icon(s == null ? Ui.IconPlay : Ui.IconPick, 34, Ui.Muted);
            stack.Children.Add(thumb);
            stack.Children.Add(new TextBlock { Text = s == null ? "Elegir en el juego" : s.Name, FontFamily = Ui.Heading, FontSize = 15, Foreground = Ui.Text, Margin = new Thickness(2, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 });
            string info = s == null ? "Cargas o empiezas una partida tú"
                        : s.When.ToString("dd/MM/yyyy HH:mm") + (s.Days != null ? "  ·  día " + s.Days : "") + (s.Cash != null ? "  ·  $" + s.Cash : "");
            stack.Children.Add(new TextBlock { Text = info, FontSize = 12, Foreground = Ui.Muted, Margin = new Thickness(2, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 });
            if (s?.CoopLine != null) stack.Children.Add(new TextBlock { Text = s.CoopLine, FontSize = 11.5, Foreground = Ui.Gold, Margin = new Thickness(2, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 176, ToolTip = s.CoopLine });
            var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(8), Margin = new Thickness(0, 0, 12, 0), BorderThickness = new Thickness(2), Child = stack, Tag = name };
            Ui.Interactive(card, () => { _slot = name; Settings.Set("slot", name ?? ""); RefreshSlotCards(); }, Ui.CardColor, Ui.CardHover);
            _slotCards.Add(card);
            return card;
        }

        private void RefreshSlotCards()
        {
            foreach (Border c in _slotCards)
                c.BorderBrush = (string)c.Tag == _slot ? Ui.Gold : Brushes.Transparent;
        }

        private Border ModeCard(HostMode mode, string icon, string title, string desc)
        {
            var stack = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Ui.Icon(icon, 22, Ui.Gold));
            head.Children.Add(new TextBlock { Text = title, FontFamily = Ui.Heading, FontSize = 17, Foreground = Ui.Text, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(head);
            stack.Children.Add(new TextBlock { Text = desc, FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 12, 0), BorderThickness = new Thickness(2), Child = stack, Tag = mode };
            Ui.Interactive(card, () => { _mode = mode; Settings.Set("mode", mode.ToString()); RefreshModeCards(); }, Ui.CardColor, Ui.CardHover);
            _modeCards.Add(card);
            return card;
        }

        private void RefreshModeCards()
        {
            foreach (Border c in _modeCards)
                c.BorderBrush = (HostMode)c.Tag == _mode ? Ui.Gold : Brushes.Transparent;
        }

        private bool _homeHasCode;

        private void UpdateInvitePanel()
        {
            // Home shows a compact card while a game is running: rebuild it when the code appears or goes.
            if (_view == "home" && _homeHasCode != (_inviteCode != null)) { Show("home"); return; }
            if (_invitePanel == null) return;
            if (_inviteCode == null)
            {
                _invitePanel.Child = new TextBlock
                {
                    Text = _mode == HostMode.Steam ? "El código de Steam sale en el juego y se copia solo."
                         : _mode == HostMode.Cloudflare ? "Aquí verás el código para tus amigos."
                         : "En tu red te encuentran solos; por VPN, ponen tu IP.",
                    Foreground = Ui.Muted, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                };
                return;
            }
            // Vertical so it fits any card width: the code shrinks to fit (never a scroll bar or a cut).
            var stack = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = "Código para tus amigos (ya está copiado)", Foreground = Ui.Muted, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            var copy = Ui.Button("Copiar", Ui.IconCopy, Color.FromRgb(0x3A, 0x30, 0x22), () => { CopyCode(); Status("Código copiado: pégalo en el chat con tus amigos."); }, 36, 13.5);
            copy.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(copy, 1);
            head.Children.Add(copy);
            stack.Children.Add(head);
            var code = new TextBlock { Text = _inviteCode, FontFamily = Ui.Mono, FontSize = 24, Foreground = Ui.Gold, Margin = new Thickness(0, 8, 0, 0) };
            stack.Children.Add(new Viewbox { Child = code, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
            var box = Ui.CardBox(stack, 12);
            box.BorderBrush = Ui.Gold;
            box.ToolTip = _inviteCode;
            _invitePanel.Child = box;
            Ui.FadeIn(box);
        }

        private void CopyCode()
        {
            if (_inviteCode == null) return;
            try { Clipboard.SetText(_inviteCode); } catch { }
        }

        // ---------- Join ----------

        private WrapPanel _lanCards;
        private TextBlock _lanEmpty;
        private string _lanKeys = "";

        private UIElement JoinView()
        {
            var page = new StackPanel();
            page.Children.Add(Section("Código de partida o IP"));
            var code = Ui.Input("Pega aquí el código de tu amigo (Steam, CF-... o IP)", 20);
            code.Text = Settings.Get("code");
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Ui.InputFrame(code));
            var joinBtn = Ui.Button("Unirse", Ui.IconLink, Color.FromRgb(0x3E, 0x8E, 0xDE), () => Join(code.Text), 54, 18);
            joinBtn.Margin = new Thickness(12, 0, 0, 0);
            joinBtn.MinWidth = 170;
            Grid.SetColumn(joinBtn, 1);
            row.Children.Add(joinBtn);
            code.KeyDown += (s, e) => { if (e.Key == Key.Enter) Join(code.Text); };
            page.Children.Add(row);
            page.Children.Add(new TextBlock
            {
                Text = "Sirve el número de Steam, el código CF-... de Cloudflare o una IP (IP:puerto) de VPN. Si tu amigo te invitó por Steam, acepta la invitación en Steam (Shift+Tab) y listo.",
                Foreground = Ui.Muted, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0),
            });

            page.Children.Add(Section("Partidas en tu red"));
            _lanCards = new WrapPanel();
            _lanEmpty = new TextBlock { Text = "Buscando partidas en tu red... (el host crea la partida en modo \"Red local / VPN\")", Foreground = Ui.Muted, FontSize = 13.5, Margin = new Thickness(2, 0, 0, 0) };
            page.Children.Add(_lanEmpty);
            page.Children.Add(_lanCards);
            _lanKeys = null;
            RefreshLanCards();
            return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private void RefreshLanCards()
        {
            if (_lanCards == null) return;
            List<LanHost> hosts = _lan?.Snapshot() ?? new List<LanHost>();
            string keys = string.Join(",", hosts.Select(h => h.Key + "/" + h.Players + "/" + h.PingMs));
            if (keys == _lanKeys) return;
            bool firstFill = _lanKeys == null || _lanKeys.Length == 0;
            _lanKeys = keys;
            _lanEmpty.Visibility = hosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _lanCards.Children.Clear();
            foreach (LanHost h in hosts)
            {
                bool sameVersion = h.Version == GameInstall.VersionText;
                var stack = new StackPanel { Width = 250 };
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(Ui.Icon(Ui.IconWifi, 20, Ui.Gold));
                head.Children.Add(new TextBlock { Text = h.Name, FontFamily = Ui.Heading, FontSize = 16, Foreground = Ui.Text, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200, VerticalAlignment = VerticalAlignment.Center });
                stack.Children.Add(head);
                var info = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
                info.Children.Add(new TextBlock { Text = h.Players + (h.Players == 1 ? " jugador" : " jugadores") + "   ·   " + (h.PingMs >= 0 ? h.PingMs + " ms" : "— ms") + "   ", Foreground = Ui.Muted, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
                info.Children.Add(Ui.Badge(sameVersion ? "v" + h.Version + " ✓" : "otra versión (v" + h.Version + ")", sameVersion ? Ui.Green : Ui.Red));
                stack.Children.Add(info);
                stack.Children.Add(new TextBlock { Text = h.Ip + ":" + h.Port, Foreground = Ui.Muted, FontSize = 11.5, FontFamily = Ui.Mono, Margin = new Thickness(0, 6, 0, 0) });
                var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16), Margin = new Thickness(0, 0, 12, 12), Child = stack, BorderBrush = Ui.Line, BorderThickness = new Thickness(1) };
                LanHost target = h;
                Ui.Interactive(card, () =>
                {
                    if (!sameVersion) { CheckForUpdateNow(target.Version); return; } // MainWindow.Update.cs
                    Launch("--coop-join " + target.Ip + ":" + target.Port, "Entrando a la partida de " + target.Name + "...");
                }, Ui.CardColor, Ui.CardHover);
                _lanCards.Children.Add(card);
                if (firstFill) Ui.FadeIn(card);
            }
        }

        // ---------- Settings ----------

        private UIElement SettingsView()
        {
            var page = new StackPanel();
            page.Children.Add(Section("Juego"));
            var gameRow = new Grid();
            gameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var gameInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            gameInfo.Children.Add(new TextBlock { Text = _game ?? "No encontré el juego: pulsa Buscar y elige GoldMiningSimulator.exe.", Foreground = _game == null ? Ui.Red : Ui.Text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
            gameInfo.Children.Add(new TextBlock { Text = ModState(), Foreground = Ui.Muted, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) });
            gameRow.Children.Add(gameInfo);
            var browse = Ui.Button("Buscar...", Ui.IconFolder, Color.FromRgb(0x3A, 0x30, 0x22), Browse, 42, 14);
            Grid.SetColumn(browse, 1);
            gameRow.Children.Add(browse);
            page.Children.Add(Ui.CardBox(gameRow, 16));

            var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            Border reinstall = Ui.Button("Reinstalar el mod", Ui.IconCheck, Color.FromRgb(0x2F, 0x7A, 0x45), () => RunInstall(force: true), 42, 14);
            reinstall.Margin = new Thickness(0, 0, 10, 10);
            buttons.Children.Add(reinstall);
            Border uninstall = Ui.Button("Desinstalar", Ui.IconClose, Color.FromRgb(0x8A, 0x35, 0x2D), Uninstall, 42, 14);
            uninstall.Margin = new Thickness(0, 0, 10, 10);
            buttons.Children.Add(uninstall);
            Border logs = Ui.Button("Abrir registros", Ui.IconFolder, Color.FromRgb(0x3A, 0x30, 0x22), OpenLogs, 42, 14);
            logs.Margin = new Thickness(0, 0, 10, 10);
            buttons.Children.Add(logs);
            page.Children.Add(buttons);

            page.Children.Add(Section("Tu nombre"));
            page.Children.Add(NameCard()); // MainWindow.Report.cs

            page.Children.Add(Section("Tu personaje"));
            page.Children.Add(CharacterCard()); // MainWindow.Characters.cs

            page.Children.Add(Section("¿Algo falló?"));
            var report = Ui.Button("Enviar reporte", Ui.IconFolder, Color.FromRgb(0x3E, 0x8E, 0xDE), SendReport, 50, 16);
            report.HorizontalAlignment = HorizontalAlignment.Left;
            report.MinWidth = 260;
            page.Children.Add(report);
            page.Children.Add(new TextBlock { Text = "Hace un .zip en tu Escritorio con los registros. Si creaste la partida y está abierta, junta también los de tus amigos.", Foreground = Ui.Muted, FontSize = 12.5, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap });

            page.Children.Add(Section("Opciones"));
            var low = new CheckBox
            {
                Content = new TextBlock { Text = "Gráficos bajos (para una PC lenta, o para abrir dos copias en la misma PC)", Foreground = Ui.Text, FontSize = 13.5 },
                IsChecked = LowGfx, VerticalContentAlignment = VerticalAlignment.Center,
            };
            low.Checked += (s, e) => Settings.Set("lowgfx", "1");
            low.Unchecked += (s, e) => Settings.Set("lowgfx", "0");
            page.Children.Add(low);

            page.Children.Add(Section("Bueno saber"));
            page.Children.Add(new TextBlock
            {
                Text = "• Dos PCs con la misma cuenta de Steam no se conectan por Steam: usen \"Red local\" (o Cloudflare) y pon Steam en modo sin conexión en una de ellas.\n" +
                       "• Cloudflare usa el servicio gratis de túneles rápidos de Cloudflare (sin cuenta). Es para pruebas: puede fallar a veces. cloudflared se baja solo la primera vez a %APPDATA%\\GMSCoop.\n" +
                       "• Todos necesitan la misma versión de GMS Co-op (esta es la " + GameInstall.VersionText + ").\n" +
                       "• Teclas en el juego: F8 Steam, F9 LAN, F10 unirse en tu red, F7 salir, Ctrl+F12 diagnóstico.",
                Foreground = Ui.Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 21,
            });
            page.Children.Add(Section("Créditos"));
            page.Children.Add(CreditsCard()); // MainWindow.Characters.cs
            return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private string ModState()
        {
            if (_game == null) return "";
            try
            {
                if (!GameInstall.IsInstalled(_game)) return "El mod no está instalado (se instala solo al jugar).";
                return GameInstall.IsUpToDate(_game) ? "Mod " + GameInstall.VersionText + " instalado y al día." : "Hay una versión distinta del mod: se actualiza sola al jugar.";
            }
            catch { return ""; }
        }

        private void Browse()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Gold Mining Simulator|" + GameInstall.GameExe, Title = "Busca " + GameInstall.GameExe };
            if (dlg.ShowDialog(this) != true) return;
            string dir = Path.GetDirectoryName(dlg.FileName);
            if (!GameInstall.IsGameDir(dir)) { Status("Esa carpeta no tiene " + GameInstall.GameExe + ".", Tone.Error); return; }
            _game = dir;
            Settings.Set("game", dir);
            Show("settings");
            AutoInstall();
        }

        private void OpenLogs()
        {
            if (_game == null) return;
            string dir = Path.Combine(_game, "BepInEx");
            try { Process.Start("explorer.exe", "\"" + (Directory.Exists(dir) ? dir : _game) + "\""); } catch { }
        }

        // =====================================================================================
        // Install / update
        // =====================================================================================

        private void AutoInstall()
        {
            if (_game == null) { Status("No encontré el juego. Ve a Ajustes y pulsa Buscar.", Tone.Warn); return; }
            bool upToDate;
            try { upToDate = GameInstall.IsUpToDate(_game); } catch { upToDate = false; }
            if (upToDate) { GameInstall.EnsureSteamAppId(_game); Status("Listo para jugar · mod " + GameInstall.VersionText + " instalado"); return; }
            if (GameInstall.GameRunning()) { Status("El mod se instalará cuando cierres el juego.", Tone.Warn); return; }
            RunInstall(force: false);
        }

        private void RunInstall(bool force)
        {
            if (_game == null || _busy) return;
            if (GameInstall.GameRunning()) { Status("Cierra el juego para instalar el mod.", Tone.Error); return; }
            _busy = true;
            Status("Instalando el mod " + GameInstall.VersionText + "...", Tone.Busy);
            string game = _game;
            Task.Run(() =>
            {
                try { return (ok: true, msg: GameInstall.Install(game)); }
                catch (UnauthorizedAccessException) { return (ok: false, msg: "Windows no deja escribir en la carpeta del juego. Cierra el juego o abre GMS Co-op como administrador."); }
                catch (Exception ex) { return (ok: false, msg: ex.Message); }
            }).ContinueWith(t =>
            {
                _busy = false;
                Status(t.Result.msg, t.Result.ok ? Tone.Ok : Tone.Error);
                if (_view == "settings") Show("settings");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void Uninstall()
        {
            if (_game == null || _busy) return;
            if (GameInstall.GameRunning()) { Status("Cierra el juego para desinstalar.", Tone.Error); return; }
            if (MessageBox.Show(this, "¿Quitar el mod GMS Co-op del juego? Tus partidas no se tocan.", "GMS Co-op", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { Status(GameInstall.Uninstall(_game)); }
            catch (Exception ex) { Status("No se pudo desinstalar: " + ex.Message, Tone.Error); }
            Show("settings");
        }

        /// <summary>Checks everything before starting the game; installs or updates the mod if needed. False = cannot start.</summary>
        private bool Prepare(out string error)
        {
            error = null;
            if (_game == null) { error = "No encontré el juego. Ve a Ajustes y pulsa Buscar."; return false; }
            if (_busy) { error = "Espera a que termine lo que está en curso."; return false; }
            if (_gameProcess != null && !_gameProcess.HasExited) { error = "El juego ya está abierto desde GMS Co-op. Ciérralo para empezar otra partida."; return false; }
            if (GameInstall.GameRunning() &&
                MessageBox.Show(this, "El juego ya está abierto. ¿Abrir otra copia? (solo para probar dos copias en la misma PC; activa \"Gráficos bajos\" en Ajustes)",
                                "GMS Co-op", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return false;
            bool upToDate;
            try { upToDate = GameInstall.IsUpToDate(_game); } catch { upToDate = false; }
            if (!upToDate)
            {
                if (GameInstall.GameRunning()) { error = "El mod necesita actualizarse: cierra el juego primero."; return false; }
                try { Status(GameInstall.Install(_game)); }
                catch (Exception ex) { error = "No se pudo instalar el mod: " + ex.Message; return false; }
            }
            GameInstall.EnsureSteamAppId(_game);
            return true;
        }

        // =====================================================================================
        // Host / join / launch
        // =====================================================================================

        private string LoadArg => !string.IsNullOrEmpty(_slot) ? " --coop-load \"" + _slot + "\"" : "";

        private void Host()
        {
            if (!OfferNewerCopy()) return; // MainWindow.Update.cs: a newer copy of this co-op world exists
            if (!Prepare(out string err)) { if (err != null) Status(err, Tone.Error); return; }
            StopBridges();
            _inviteCode = null;
            switch (_mode)
            {
                case HostMode.Steam:
                    if (!GameInstall.SteamRunning()) { Status("Abre Steam primero: la partida por Steam lo necesita.", Tone.Error); return; }
                    Launch("--coop-steam-host" + LoadArg, "Juego abierto. El código de Steam aparece en el juego y se copia solo.");
                    break;
                case HostMode.Lan:
                    Launch("--coop-host" + LoadArg, "Juego abierto. Tus amigos en la misma red te encuentran en \"Unirse\".");
                    break;
                case HostMode.Cloudflare:
                    HostCloudflare();
                    break;
            }
            UpdateInvitePanel();
        }

        private void HostCloudflare()
        {
            _busy = true;
            Status("Preparando Cloudflare...", Tone.Busy);
            Task.Run(() =>
            {
                Cloudflare.EnsureCloudflared(msg => Status(msg, Tone.Busy));
                Status("Abriendo el túnel de Cloudflare...", Tone.Busy);
                var bridge = new HostBridge(27777, msg => Status(msg));
                bridge.Start(TimeSpan.FromSeconds(30));
                return bridge;
            }).ContinueWith(t =>
            {
                _busy = false;
                if (t.IsFaulted)
                {
                    string msg = t.Exception?.GetBaseException().Message ?? "error desconocido";
                    Status("Cloudflare: " + msg.Split('\n')[0], Tone.Error);
                    MessageBox.Show(this, msg, "GMS Co-op: Cloudflare", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _hostBridge = t.Result;
                _inviteCode = Cloudflare.CodePrefix + _hostBridge.Subdomain;
                CopyCode();
                UpdateInvitePanel();
                string edge = _hostBridge.Location != null ? " (Cloudflare " + HostBridge.EdgeName(_hostBridge.Location) + ", " + (_hostBridge.Protocol ?? "").ToUpperInvariant() + ")" : "";
                Launch("--coop-host" + LoadArg, "Túnel listo" + edge + ": pásales el código " + _inviteCode + " (ya está copiado). No cierres GMS Co-op mientras juegan.");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void Join(string text)
        {
            Settings.Set("code", (text ?? "").Trim());
            JoinTarget target = JoinTarget.Parse(text);
            if (target.Type == JoinTarget.Kind.Invalid) { Status("Ese código no se entiende. Pega el número de Steam, el código CF-... o una IP.", Tone.Error); return; }
            if (!Prepare(out string err)) { if (err != null) Status(err, Tone.Error); return; }
            StopBridges();
            switch (target.Type)
            {
                case JoinTarget.Kind.Auto:
                    Launch("--coop-join auto", "Juego abierto: buscando la partida en tu red...");
                    break;
                case JoinTarget.Kind.SteamLobby:
                    if (!GameInstall.SteamRunning()) { Status("Abre Steam primero: el código de Steam lo necesita.", Tone.Error); return; }
                    Launch("+connect_lobby " + target.Value, "Juego abierto: entrando a la partida de Steam...");
                    break;
                case JoinTarget.Kind.Address:
                    Launch("--coop-join " + target.Value, "Juego abierto: conectando a " + target.Value + "...");
                    break;
                case JoinTarget.Kind.Cloudflare:
                    JoinCloudflare(target.Value);
                    break;
            }
        }

        private void JoinCloudflare(string sub)
        {
            _busy = true;
            Status("Conectando con la partida de Cloudflare...", Tone.Busy);
            Task.Run(() =>
            {
                var bridge = new ClientBridge(sub, msg => Status(msg));
                bridge.Probe(TimeSpan.FromSeconds(25));
                bridge.Start();
                return bridge;
            }).ContinueWith(t =>
            {
                _busy = false;
                if (t.IsFaulted)
                {
                    string msg = t.Exception?.GetBaseException().Message ?? "error desconocido";
                    Status(msg.Split('\n')[0], Tone.Error);
                    MessageBox.Show(this, msg, "GMS Co-op: Cloudflare", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _clientBridge = t.Result;
                string rtt = _clientBridge.RttMs >= 0 ? $" ({_clientBridge.RttMs:0} ms)" : "";
                Launch("--coop-join 127.0.0.1:" + _clientBridge.Port, "Conectado por Cloudflare" + rtt + ". El juego intentará además una conexión directa. No cierres GMS Co-op mientras juegas.");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void Launch(string args, string doneText)
        {
            if (LowGfx) args += " --coop-lowgfx";
            args += NameArg; // MainWindow.Report.cs
            args += CharacterArg; // MainWindow.Characters.cs
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(_game, GameInstall.GameExe), args) { WorkingDirectory = _game, UseShellExecute = false };
                _gameProcess = Process.Start(psi);
                Status(doneText);
                if (!GameInstall.SteamRunning()) Status(doneText + " (Steam no está abierto: el juego puede pedirlo.)", Tone.Warn);
            }
            catch (Exception ex)
            {
                Status("No se pudo abrir el juego: " + ex.Message, Tone.Error);
                StopBridges();
            }
        }

        private void StopBridges()
        {
            _hostBridge?.Dispose();
            _hostBridge = null;
            _clientBridge?.Dispose();
            _clientBridge = null;
            if (_inviteCode != null) { _inviteCode = null; UpdateInvitePanel(); }
        }

        private void OnTimer()
        {
            if (_view == "join") RefreshLanCards();
            if (_view == "home") UpdateHomeLan();
            if (_gameProcess != null && _gameProcess.HasExited)
            {
                _gameProcess = null;
                bool hadBridge = _hostBridge != null || _clientBridge != null;
                StopBridges();
                Status(hadBridge ? "El juego se cerró: túnel de Cloudflare cerrado." : "El juego se cerró.");
            }
            else if (_hostBridge != null && !_hostBridge.TunnelAlive)
            {
                Status("El túnel de Cloudflare se cayó. Cierra el juego y crea la partida otra vez.", Tone.Error);
            }
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            bool tunnel = (_hostBridge != null || _clientBridge != null) && _gameProcess != null && !_gameProcess.HasExited;
            if (tunnel && MessageBox.Show(this, "Si cierras GMS Co-op se corta la conexión de Cloudflare del juego. ¿Cerrar de todos modos?",
                                          "GMS Co-op", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            StopBridges();
            _lan?.Dispose();
        }
    }
}
