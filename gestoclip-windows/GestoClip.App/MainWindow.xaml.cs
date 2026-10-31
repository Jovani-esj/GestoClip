using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GestoClip.App.Platform;
using GestoClip.Core.Discovery;
using GestoClip.Core.Pairing;
using GestoClip.Core.Protocol;
using GestoClip.Core.Transport;
using GestoClip.Core.Vision;

namespace GestoClip.App;

public partial class MainWindow : Window
{
    private readonly TrayContext _controller;
    private readonly DispatcherTimer _uiTimer;
    private QrPairInvite? _currentInvite;
    public bool AllowFullClose { get; set; }

    internal MainWindow(TrayContext controller)
    {
        InitializeComponent();
        _controller = controller;

        var ip = PairingService.GetPreferredLanIp();
        SidebarIpText.Text = $"IP: {ip}:{_controller.Channel.ListenPort}";
        SettingsIdentityText.Text =
            $"Equipo:       {Environment.MachineName}\n" +
            $"Device ID:    {_controller.DeviceId}\n" +
            $"Puerto TLS:   {_controller.Channel.ListenPort}  |  UDP Discovery: {UdpDiscovery.BroadcastPort}\n" +
            $"SHA-256 FP:   {_controller.Channel.LocalFingerprint}";

        AutostartCheckBox.IsChecked = AutostartService.IsEnabled();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => OnTick();
        _uiTimer.Start();

        Closing += (_, e) =>
        {
            if (!AllowFullClose)
            {
                e.Cancel = true;
                Hide();
                _controller.Toast("GestoClip sigue activo en la bandeja del sistema (Ctrl+Alt+G).");
            }
        };
    }

    public void SelectTab(string tab)
    {
        ViewGestures.Visibility = tab == "gestures" ? Visibility.Visible : Visibility.Collapsed;
        ViewPair.Visibility = tab == "pair" ? Visibility.Visible : Visibility.Collapsed;
        ViewPeers.Visibility = tab == "peers" ? Visibility.Visible : Visibility.Collapsed;
        ViewSettings.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;

        if (tab == "pair") RefreshQr();
        if (tab == "peers") RefreshPeersLists();
        if (tab == "settings") AutostartCheckBox.IsChecked = AutostartService.IsEnabled();
    }

    private void OnNavGestures(object sender, RoutedEventArgs e) => SelectTab("gestures");
    private void OnNavPair(object sender, RoutedEventArgs e) => SelectTab("pair");
    private void OnNavPeers(object sender, RoutedEventArgs e) => SelectTab("peers");
    private void OnNavSettings(object sender, RoutedEventArgs e) => SelectTab("settings");

    private async void OnStartCameraGesture(object sender, RoutedEventArgs e) => await _controller.RunGestureAsync(false);
    private async void OnStartCameraDiag(object sender, RoutedEventArgs e) => await _controller.RunGestureAsync(true);
    private async void OnManualCopy(object sender, RoutedEventArgs e) => await _controller.DoCopyAsync();
    private void OnManualPaste(object sender, RoutedEventArgs e) => _controller.DoPaste();

    public void UpdateCameraFrame(byte[] jpgBytes, HandPose pose, int frameIndex)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                using var ms = new MemoryStream(jpgBytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                CameraPreviewImage.Source = bmp;
                CameraPlaceholder.Visibility = Visibility.Collapsed;

                switch (pose)
                {
                    case HandPose.Palm:
                        PoseBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74));
                        PoseBadgeText.Text = $"🖐️ PALMA ABIERTA (#{frameIndex})";
                        break;
                    case HandPose.Fist:
                        PoseBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(234, 88, 12));
                        PoseBadgeText.Text = $"✊ PUÑO CERRADO (#{frameIndex})";
                        break;
                    default:
                        PoseBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70));
                        PoseBadgeText.Text = $"Buscando mano... (#{frameIndex})";
                        break;
                }
            }
            catch { }
        });
    }

    public void NotifyCameraStopped(string statusMessage)
    {
        Dispatcher.BeginInvoke(() =>
        {
            PoseBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
            PoseBadgeText.Text = "Cámara apagada";
            FooterStatusText.Text = statusMessage;
        });
    }

    public void NotifyTransferEvent(string title, string detail)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LastTransferTitle.Text = title;
            LastTransferDetail.Text = detail;
            FooterStatusText.Text = $"{title} — {detail}";
        });
    }

    private void RefreshQr()
    {
        var ip = PairingService.GetPreferredLanIp();
        _currentInvite = _controller.Pairing.CreateInvite(ip, _controller.Channel.ListenPort, _controller.Channel.LocalFingerprint);
        var png = QrPairInvite.RenderPng(_currentInvite.ToUri(), pixelsPerModule: 8);
        using var ms = new MemoryStream(png);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();

        QrCodeImage.Source = bmp;
        QrPinLabel.Text = $"TOKEN: {_currentInvite.Token}";
        QrDetailsText.Text = $"Dirección: {_currentInvite.Ip}:{_currentInvite.Port}\nEquipo: {_currentInvite.Name}\nHuella TLS: {_currentInvite.Fp[..16]}…";
        UpdateQrCountdown();
    }

    private void OnRefreshQr(object sender, RoutedEventArgs e) => RefreshQr();

    private void OnCopyQrUri(object sender, RoutedEventArgs e)
    {
        if (_currentInvite is null) return;
        System.Windows.Clipboard.SetText(_currentInvite.ToUri());
        FooterStatusText.Text = "Enlace gestoclip://pair copiado al portapapeles.";
    }

    private async void OnConnectByUri(object sender, RoutedEventArgs e)
    {
        var invite = QrPairInvite.TryParse(PairUriInputBox.Text);
        if (invite is null)
        {
            System.Windows.MessageBox.Show(this, "El enlace no tiene formato gestoclip://pair válido.", "GestoClip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var ack = await _controller.Channel.PairWithServerAsync(invite, _controller.DeviceId, Environment.MachineName, "WINDOWS", _controller.Paired);
            if (ack.Accepted)
            {
                NotifyTransferEvent($"Emparejado con {ack.ServerName}", $"Plataforma: {ack.ServerPlatform}");
                SelectTab("peers");
            }
            else
            {
                System.Windows.MessageBox.Show(this, $"Rechazado por el servidor: {ack.Reason}", "GestoClip", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Error al conectar: {ex.Message}", "GestoClip", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnTick()
    {
        if (ViewPair.Visibility == Visibility.Visible)
            UpdateQrCountdown();
        if (ViewPeers.Visibility == Visibility.Visible)
            RefreshDiscoveredOnly();
    }

    private void UpdateQrCountdown()
    {
        var active = _controller.Pairing.ActiveInvite;
        if (active is null)
        {
            QrTimerLabel.Text = "QR expirado o ya utilizado. Pulsa 'Regenerar QR'.";
            return;
        }
        var rem = TimeSpan.FromMilliseconds(Math.Max(0, active.ExpiresAtUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        QrTimerLabel.Text = $"Expira en {rem.Minutes:D2}:{rem.Seconds:D2} (token de un solo uso)";
    }

    public void RefreshPeersLists()
    {
        Dispatcher.BeginInvoke(() =>
        {
            PairedListBox.Items.Clear();
            foreach (var p in _controller.Paired.List())
            {
                PairedListBox.Items.Add(new PairedListItem(p));
            }
            RefreshDiscoveredOnly();
        });
    }

    private void RefreshDiscoveredOnly()
    {
        DiscoveredListBox.Items.Clear();
        foreach (var d in _controller.Discovery.Peers)
        {
            var isPaired = _controller.Paired.IsPaired(d.PubKeyFp);
            var status = isPaired ? "✅ Emparejado" : "🔸 No emparejado";
            DiscoveredListBox.Items.Add($"{d.Name} [{d.Platform}] — {d.Address}:{d.Port} ({status})");
        }
        var count = _controller.Paired.List().Count;
        SidebarStatusTitle.Text = count > 0 ? $"● Activo ({count} emparejado{(count == 1 ? "" : "s")})" : "● Escuchando en LAN";
    }

    private void OnRefreshPeersView(object sender, RoutedEventArgs e) => RefreshPeersLists();

    private void OnUnpairSelected(object sender, RoutedEventArgs e)
    {
        if (PairedListBox.SelectedItem is PairedListItem item)
        {
            _controller.Paired.Remove(item.Peer.Fingerprint);
            RefreshPeersLists();
        }
    }

    private void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        var want = AutostartCheckBox.IsChecked == true;
        _controller.SetAutostart(want);
        AutostartCheckBox.IsChecked = AutostartService.IsEnabled();
    }

    private void OnOpenAppDataFolder(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GestoClip");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private void OnQuitAppCompletely(object sender, RoutedEventArgs e) => _controller.ExitApplication();

    private sealed record PairedListItem(PairedPeer Peer)
    {
        public override string ToString()
        {
            var fpShort = Peer.Fingerprint[..Math.Min(12, Peer.Fingerprint.Length)];
            return $"{Peer.Name} [{Peer.Platform}]  —  FP: {fpShort}…  ({Peer.PairedAt.LocalDateTime:g})";
        }
    }
}
