using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GMSCoopInstaller
{
    /// <summary>Self-update banner: "Hay versión nueva X — Actualizar" over the page when GitHub has a newer launcher.</summary>
    internal sealed partial class MainWindow
    {
        private Border _updateBanner;
        private Updater.Release _pendingUpdate;
        private bool _updateChecked;

        private void AddUpdateBanner(Grid grid)
        {
            _updateBanner = new Border
            {
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 8, 8, 8),
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x22, 0x10)),
                BorderBrush = Ui.Gold,
                BorderThickness = new Thickness(1.5),
            };
            Grid.SetRow(_updateBanner, 1);
            Panel.SetZIndex(_updateBanner, 50);
            grid.Children.Add(_updateBanner);
        }

        /// <summary>Asks GitHub for a newer launcher once per start (and again before joining a host with another version).</summary>
        private void CheckForUpdate()
        {
            if (Preview || _updateChecked) return;
            _updateChecked = true;
            Updater.CleanupOld();
            Updater.CheckAsync().ContinueWith(t =>
            {
                if (t.Result == null) return;
                _pendingUpdate = t.Result;
                ShowUpdateBanner();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>A host on the network runs another mod version: look for an update right away.</summary>
        internal void CheckForUpdateNow(string otherVersion)
        {
            if (_pendingUpdate != null) { ShowUpdateBanner(); return; }
            _updateChecked = false;
            Status("Esa partida usa la versión " + otherVersion + ": buscando actualización...", Tone.Busy);
            Updater.CheckAsync().ContinueWith(t =>
            {
                if (t.Result == null)
                {
                    Status("Esa partida usa otra versión del mod (v" + otherVersion + "). Pídele al host que actualice GMS Co-op.", Tone.Error);
                    return;
                }
                _pendingUpdate = t.Result;
                ShowUpdateBanner();
                Status("Hay versión nueva (" + t.Result.Tag + "): pulsa Actualizar arriba.", Tone.Warn);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ShowUpdateBanner()
        {
            if (_updateBanner == null || _pendingUpdate == null) return;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Ui.Icon(Ui.IconCloud, 18, Ui.Gold));
            row.Children.Add(new TextBlock
            {
                Text = "Hay versión nueva " + _pendingUpdate.Tag + " (tienes v" + Updater.Current + ")",
                Foreground = Ui.Text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 14, 0),
            });
            var btn = Ui.Button("Actualizar", Ui.IconPlay, Ui.GoldColor, ApplyUpdate, 36, 14, new SolidColorBrush(Color.FromRgb(0x1E, 0x15, 0x05)));
            row.Children.Add(btn);
            _updateBanner.Child = row;
            _updateBanner.Visibility = Visibility.Visible;
            Ui.FadeIn(_updateBanner, 300, -8);
        }

        /// <summary>Small muted note under a card's main button.</summary>
        private static TextBlock CoopNote(string text) =>
            new TextBlock { Text = text, Foreground = Ui.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 0, 0) };

        /// <summary>
        /// Before hosting a co-op world: if another local slot holds a newer copy of the same world (higher
        /// revision, for example a backup made after the host was lost), offer to host that one instead.
        /// False = the player cancelled.
        /// </summary>
        private bool OfferNewerCopy()
        {
            if (string.IsNullOrEmpty(_slot)) return true;
            var all = SaveSlots.List();
            SaveSlot chosen = all.Find(x => x.Name == _slot);
            SaveSlot newer = SaveSlots.NewerCopyOf(chosen, all);
            if (newer == null) return true;
            MessageBoxResult r = MessageBox.Show(this,
                "Hay una versión más nueva de este mundo co-op:\n\n" +
                "  '" + newer.Name + "'  ·  rev " + newer.Revision + (string.IsNullOrEmpty(newer.LastSavedBy) ? "" : "  ·  último: " + newer.LastSavedBy) + "\n" +
                "  '" + chosen.Name + "'  ·  rev " + chosen.Revision + "  (la que elegiste)\n\n" +
                "¿Abrir la más nueva?  (Sí = la más nueva, No = la que elegiste)",
                "GMS Co-op", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes)
            {
                _slot = newer.Name;
                Settings.Set("slot", _slot);
                if (_view == "create" || _view == "home") Show(_view);
            }
            return true;
        }

        private void ApplyUpdate()
        {
            if (_pendingUpdate == null || _busy) return;
            if (_gameProcess != null && !_gameProcess.HasExited)
            {
                Status("Cierra el juego antes de actualizar GMS Co-op.", Tone.Warn);
                return;
            }
            _busy = true;
            Status("Descargando " + _pendingUpdate.Tag + "...", Tone.Busy);
            Updater.Release r = _pendingUpdate;
            Task.Run(() => Updater.Apply(r)).ContinueWith(t =>
            {
                _busy = false;
                if (t.IsFaulted)
                {
                    Status("No se pudo actualizar: " + (t.Exception?.GetBaseException().Message ?? "error") + ". Sigues con esta versión.", Tone.Error);
                    return;
                }
                StopBridges();
                Application.Current.Shutdown();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
