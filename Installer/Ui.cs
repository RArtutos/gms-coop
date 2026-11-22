using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace GMSCoopInstaller
{
    /// <summary>Theme, vector icons and animated controls for the launcher (built in code, no XAML).</summary>
    internal static class Ui
    {
        internal static readonly Color BgColor = Color.FromRgb(0x14, 0x11, 0x0D);
        internal static readonly Color CardColor = Color.FromRgb(0x22, 0x1C, 0x15);
        internal static readonly Color CardHover = Color.FromRgb(0x2D, 0x25, 0x1B);
        internal static readonly Color GoldColor = Color.FromRgb(0xF2, 0xB6, 0x3D);
        internal static readonly Color GoldDark = Color.FromRgb(0xB9, 0x82, 0x1E);

        internal static readonly Brush Bg = Freeze(new SolidColorBrush(BgColor));
        internal static readonly Brush Card = Freeze(new SolidColorBrush(CardColor));
        internal static readonly Brush Gold = Freeze(new SolidColorBrush(GoldColor));
        internal static readonly Brush Text = Freeze(new SolidColorBrush(Color.FromRgb(0xF3, 0xED, 0xE2)));
        internal static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0xA8, 0x9A, 0x86)));
        internal static readonly Brush Line = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xF2, 0xB6, 0x3D)));
        internal static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0x6B)));
        internal static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x4F)));
        internal static readonly Brush Blue = Freeze(new SolidColorBrush(Color.FromRgb(0x3E, 0x8E, 0xDE)));
        internal static readonly Brush InputBg = Freeze(new SolidColorBrush(Color.FromRgb(0x0E, 0x0C, 0x09)));

        internal static readonly FontFamily Heading = new FontFamily("Bahnschrift SemiBold, Segoe UI Semibold");
        internal static readonly FontFamily Body = new FontFamily("Segoe UI");
        internal static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");

        private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        // Vector icons (24x24 paths, original).
        internal const string IconPick = "M14.8 2.2c2.9.4 5.6 1.9 7 4.1-2.1-.9-4.4-1.2-6.4-.8l-1.4 1.4 2 2-1.4 1.4-2-2-9.2 9.2a1.4 1.4 0 0 1-2-2l9.2-9.2-2-2 1.4-1.4 2 2 1.4-1.4c.4-2-.1-4.3-.6-5.3z";
        internal const string IconUsers = "M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8zm7.5 0a3.2 3.2 0 1 0 0-6.4 3.2 3.2 0 0 0 0 6.4zM2 20c0-3.3 3.1-6 7-6s7 2.7 7 6v1H2zm15.4 1v-1c0-2-.8-3.8-2.2-5.1.4-.1.9-.1 1.3-.1 3 0 5.5 2.1 5.5 4.8V21z";
        internal const string IconCloud = "M7 19a5 5 0 0 1-.6-10A6.5 6.5 0 0 1 19 8.5 4.8 4.8 0 0 1 18.2 19z";
        internal const string IconSteam = "M12 2a10 10 0 0 0-10 9.4l5.4 2.2a2.8 2.8 0 0 1 1.7-.5l2.4-3.5a3.9 3.9 0 1 1 3.9 3.9l-3.4 2.5a2.9 2.9 0 0 1-5.7.6L2.4 15A10 10 0 1 0 12 2zm3.4 4.4a2.6 2.6 0 1 0 0 5.2 2.6 2.6 0 0 0 0-5.2z";
        internal const string IconWifi = "M12 18.5a1.8 1.8 0 1 1 0 3.6 1.8 1.8 0 0 1 0-3.6zM5.6 14.6a9 9 0 0 1 12.8 0l-1.7 1.7a6.6 6.6 0 0 0-9.4 0zM2.2 11.2a13.8 13.8 0 0 1 19.6 0l-1.7 1.7a11.4 11.4 0 0 0-16.2 0z";
        internal const string IconGear = "M12 8.5a3.5 3.5 0 1 0 0 7 3.5 3.5 0 0 0 0-7zm8.9 5.1-1.6-.4-.6 1.5.9 1.4-1.9 1.9-1.4-.9-1.5.6-.4 1.6h-2.8l-.4-1.6-1.5-.6-1.4.9-1.9-1.9.9-1.4-.6-1.5-1.6-.4v-2.8l1.6-.4.6-1.5-.9-1.4 1.9-1.9 1.4.9 1.5-.6.4-1.6h2.8l.4 1.6 1.5.6 1.4-.9 1.9 1.9-.9 1.4.6 1.5 1.6.4z";
        internal const string IconHome = "M12 3 2 11.5h3V21h5.5v-6h3v6H19v-9.5h3z";
        internal const string IconCopy = "M8 7V3h13v13h-4v4H4V7zm2 0h7v7h2V5h-9zM6 9v9h9V9z";
        internal const string IconPlay = "M7 4v16l13-8z";
        internal const string IconLink = "M10.6 13.4a1 1 0 0 1 0-1.4l3.5-3.5a1 1 0 1 1 1.4 1.4L12 13.4a1 1 0 0 1-1.4 0zM8.5 19.5a4.2 4.2 0 0 1-3-7.2l2.1-2.1 1.4 1.4-2.1 2.1a2.2 2.2 0 0 0 3.1 3.1l2.1-2.1 1.4 1.4-2.1 2.1a4.2 4.2 0 0 1-2.9 1.3zm7.9-5.7-1.4-1.4 2.1-2.1a2.2 2.2 0 0 0-3.1-3.1l-2.1 2.1-1.4-1.4 2.1-2.1a4.2 4.2 0 0 1 5.9 5.9z";
        internal const string IconClose = "M6.4 5 12 10.6 17.6 5 19 6.4 13.4 12l5.6 5.6-1.4 1.4-5.6-5.6L6.4 19 5 17.6l5.6-5.6L5 6.4z";
        internal const string IconMin = "M5 11h14v2H5z";
        internal const string IconFolder = "M3 5h7l2 2h9v12H3z";
        internal const string IconCheck = "M9.5 16.2 5.3 12l-1.4 1.4 5.6 5.6L20.1 8.4 18.7 7z";
        internal const string IconWarn = "M12 2 1 21h22zm1 15h-2v2h2zm0-8h-2v6h2z";

        internal static Path Icon(string data, double size, Brush brush)
        {
            return new Path
            {
                Data = Geometry.Parse(data),
                Fill = brush,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
            };
        }

        internal static TextBlock Label(string text, double size, Brush brush, FontFamily font = null, FontWeight? weight = null)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = brush,
                FontFamily = font ?? Body,
                FontWeight = weight ?? FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
            };
        }

        internal static Border CardBox(UIElement child, double padding = 18)
        {
            return new Border
            {
                Background = Card,
                CornerRadius = new CornerRadius(14),
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(padding),
                Child = child,
            };
        }

        /// <summary>Makes <paramref name="b"/> react to the mouse: grows a little on hover, shrinks on press, runs <paramref name="click"/>.</summary>
        internal static void Interactive(Border b, Action click, Color normal, Color hover) =>
            Interactive(b, click, () => normal, () => hover);

        /// <summary>As above, with colors read at each hover (a selected tab changes its normal color).</summary>
        internal static void Interactive(Border b, Action click, Func<Color> normalOf, Func<Color> hoverOf)
        {
            Color normal = normalOf();
            var bg = new SolidColorBrush(normal);
            b.Background = bg;
            var scale = new ScaleTransform(1, 1);
            b.RenderTransform = scale;
            b.RenderTransformOrigin = new Point(0.5, 0.5);
            b.Cursor = Cursors.Hand;
            b.MouseEnter += (s, e) =>
            {
                if (!b.IsEnabled) return;
                Animate(scale, 1.025, 140);
                bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(hoverOf(), TimeSpan.FromMilliseconds(140)));
            };
            b.MouseLeave += (s, e) =>
            {
                Animate(scale, 1.0, 180);
                bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(normalOf(), TimeSpan.FromMilliseconds(180)));
            };
            b.MouseLeftButtonDown += (s, e) => { if (b.IsEnabled) Animate(scale, 0.97, 80); e.Handled = true; };
            b.MouseLeftButtonUp += (s, e) =>
            {
                if (!b.IsEnabled) return;
                Animate(scale, b.IsMouseOver ? 1.025 : 1.0, 120);
                if (b.IsMouseOver) click?.Invoke();
                e.Handled = true;
            };
            b.IsEnabledChanged += (s, e) => b.Opacity = b.IsEnabled ? 1.0 : 0.45;
        }

        private static void Animate(ScaleTransform t, double to, int ms)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            t.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            t.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        /// <summary>A big rounded button with an icon.</summary>
        internal static Border Button(string text, string icon, Color color, Action click, double height = 52, double fontSize = 17, Brush fore = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            fore = fore ?? (color == GoldColor ? (Brush)new SolidColorBrush(Color.FromRgb(0x1E, 0x15, 0x05)) : Brushes.White);
            if (icon != null) row.Children.Add(new Border { Child = Icon(icon, fontSize + 3, fore), Margin = new Thickness(0, 0, 10, 0) });
            row.Children.Add(new TextBlock { Text = text, FontSize = fontSize, FontFamily = Heading, Foreground = fore, VerticalAlignment = VerticalAlignment.Center });
            var b = new Border { CornerRadius = new CornerRadius(12), Height = height, Padding = new Thickness(22, 0, 22, 0), Child = row };
            Interactive(b, click, color, Lighten(color, 0.12));
            b.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black };
            return b;
        }

        internal static Color Lighten(Color c, double f) =>
            Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));

        internal static TextBox Input(string placeholder, double fontSize = 18)
        {
            var tb = new TextBox
            {
                FontSize = fontSize,
                FontFamily = Mono,
                Foreground = Text,
                Background = InputBg,
                CaretBrush = Gold,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 12, 14, 12),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            tb.Tag = placeholder;
            return tb;
        }

        /// <summary>Wraps a TextBox in a rounded frame with a placeholder that hides while typing.</summary>
        internal static Border InputFrame(TextBox tb)
        {
            var hint = new TextBlock
            {
                Text = (string)tb.Tag,
                Foreground = Muted,
                FontSize = tb.FontSize - 2,
                FontFamily = Body,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            tb.TextChanged += (s, e) => hint.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            hint.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; // text set before the frame
            var g = new Grid();
            g.Children.Add(tb);
            g.Children.Add(hint);
            var frame = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = InputBg,
                BorderThickness = new Thickness(1.5),
                BorderBrush = Line,
                Child = g,
            };
            tb.GotKeyboardFocus += (s, e) => frame.BorderBrush = Gold;
            tb.LostKeyboardFocus += (s, e) => frame.BorderBrush = Line;
            return frame;
        }

        internal static Border Badge(string text, Brush color)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0x33, ((SolidColorBrush)color).Color.R, ((SolidColorBrush)color).Color.G, ((SolidColorBrush)color).Color.B)),
                BorderBrush = color,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 2, 8, 2),
                Child = new TextBlock { Text = text, Foreground = color, FontSize = 12, FontFamily = Heading },
            };
        }

        internal static void FadeIn(UIElement e, int ms = 260, double fromY = 14)
        {
            var t = new TranslateTransform(0, fromY);
            e.RenderTransform = t;
            e.Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        }

        /// <summary>Loads an image file without locking it; null if missing or broken.</summary>
        internal static BitmapImage LoadImage(string path, int decodeWidth = 0)
        {
            try
            {
                if (path == null || !File.Exists(path)) return null;
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) img.DecodePixelWidth = decodeWidth;
                img.UriSource = new Uri(path);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        /// <summary>Original fallback banner: dusk gradient, layered mountains with a gold rim, film grain.</summary>
        internal static BitmapSource ProceduralHero(int w = 1280, int h = 400)
        {
            var dv = new DrawingVisual();
            var rnd = new Random(451340);
            using (DrawingContext dc = dv.RenderOpen())
            {
                var sky = new LinearGradientBrush(Color.FromRgb(0x3A, 0x22, 0x10), Color.FromRgb(0x0D, 0x0B, 0x10), 90);
                dc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
                dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(0x90, 0xF2, 0xB6, 0x3D), Color.FromArgb(0, 0xF2, 0xB6, 0x3D)),
                               null, new Point(w * 0.72, h * 0.55), w * 0.35, h * 0.6);
                Color[] layers = { Color.FromRgb(0x2A, 0x1C, 0x12), Color.FromRgb(0x1D, 0x15, 0x0E), Color.FromRgb(0x12, 0x0E, 0x0A) };
                for (int l = 0; l < layers.Length; l++)
                {
                    var geo = new StreamGeometry();
                    using (StreamGeometryContext g = geo.Open())
                    {
                        double baseY = h * (0.55 + l * 0.13);
                        g.BeginFigure(new Point(0, h), true, true);
                        double x = 0;
                        g.LineTo(new Point(0, baseY), true, false);
                        while (x < w)
                        {
                            double step = 60 + rnd.NextDouble() * 120;
                            double peak = baseY - (40 + rnd.NextDouble() * (110 - l * 25));
                            g.LineTo(new Point(x + step * 0.5, peak), true, false);
                            g.LineTo(new Point(x + step, baseY - rnd.NextDouble() * 30), true, false);
                            x += step;
                        }
                        g.LineTo(new Point(w, h), true, false);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(new SolidColorBrush(layers[l]), new Pen(new SolidColorBrush(Color.FromArgb((byte)(90 - l * 25), 0xF2, 0xB6, 0x3D)), 1.5), geo);
                }
                for (int i = 0; i < 9000; i++)
                {
                    byte a = (byte)rnd.Next(6, 22);
                    var c = rnd.Next(2) == 0 ? Color.FromArgb(a, 255, 255, 255) : Color.FromArgb(a, 0, 0, 0);
                    dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(rnd.Next(w), rnd.Next(h), 1.5, 1.5));
                }
            }
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>Window icon drawn from the pickaxe path (no icon file in the exe).</summary>
        internal static ImageSource AppIcon()
        {
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(BgColor), null, new RectangleGeometry(new Rect(0, 0, 24, 24), 5, 5)));
            g.Children.Add(new GeometryDrawing(Gold, null, Geometry.Parse(IconPick)));
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }
    }
}
