using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GestoClip.App.Platform;
using GestoClip.Core.Actions;
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
    private readonly List<QueuedFileItem> _queuedFiles = new();
    private CancellationTokenSource? _sendCts;
    private QrPairInvite? _currentInvite;
    private bool _initializingSettingsUi;
    public bool AllowFullClose { get; set; }

    internal MainWindow(TrayContext controller)
    {
        InitializeComponent();
        _controller = controller;

        var ip = PairingService.GetPreferredLanIp();
        SidebarIpText.Text = $"IP: {ip}:{_controller.Channel.ListenPort}";
        SettingsIdentityText.Text =
            $"Equipo:         {Environment.MachineName}\n" +
            $"Device ID:      {_controller.DeviceId}\n" +
            $"Puerto TLS:     {_controller.Channel.ListenPort}  |  UDP Discovery: {UdpDiscovery.BroadcastPort}\n" +
            $"Atajo Cámara:   Ctrl + Alt + G\n" +
            $"Atajo Directo:  Ctrl + Alt + S (envía sin abrir cámara)\n" +
            $"SHA-256 FP:     {_controller.Channel.LocalFingerprint}";

        LoadSettingsIntoUi();
        RefreshPeersLists();
        RefreshActivityList();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => OnTick();
        _uiTimer.Start();

        Closing += (_, e) =>
        {
            if (!AllowFullClose)
            {
                e.Cancel = true;
                Hide();
                _controller.Toast("GestoClip sigue activo en la bandeja del sistema (Ctrl+Alt+G / Ctrl+Alt+S).");
            }
        };
    }

    private void LoadSettingsIntoUi()
    {
        _initializingSettingsUi = true;
        try
        {
            AutostartCheckBox.IsChecked = AutostartService.IsEnabled();
            AutoSaveCheckBox.IsChecked = _controller.Settings.AutoSaveReceivedFiles;
            ModeMultiFilesRadio.IsChecked = _controller.Settings.FistMode == GestureMode.ClipboardMultiFiles;
            ModeClipboardOnlyRadio.IsChecked = _controller.Settings.FistMode == GestureMode.ClipboardOrScreenshot;
            ModeFoldersRadio.IsChecked = _controller.Settings.FistMode == GestureMode.PreselectedFolders;

            PresetFoldersListBox.Items.Clear();
            if (_controller.Settings.PreselectedFolders is not null)
            {
                foreach (var f in _controller.Settings.PreselectedFolders)
                    PresetFoldersListBox.Items.Add(f);
            }
        }
        finally
        {
            _initializingSettingsUi = false;
        }
    }

    public void SelectTab(string tab)
    {
        if (tab == "pair") tab = "peers";
        ViewShare.Visibility = tab == "share" ? Visibility.Visible : Visibility.Collapsed;
        ViewGestures.Visibility = tab == "gestures" ? Visibility.Visible : Visibility.Collapsed;
        ViewPeers.Visibility = tab == "peers" ? Visibility.Visible : Visibility.Collapsed;
        ViewSettings.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;

        if (tab == "share")
        {
            RefreshPeersLists();
            RefreshActivityList();
        }
        if (tab == "peers")
        {
            if (_currentInvite is null || _controller.Pairing.ActiveInvite is null)
                RefreshQr();
            RefreshPeersLists();
        }
        if (tab == "settings")
            LoadSettingsIntoUi();
    }

    private void OnNavShare(object sender, RoutedEventArgs e) => SelectTab("share");
    private void OnNavGestures(object sender, RoutedEventArgs e) => SelectTab("gestures");
    private void OnNavPeers(object sender, RoutedEventArgs e) => SelectTab("peers");
    private void OnNavSettings(object sender, RoutedEventArgs e) => SelectTab("settings");

    // --- DRAG & DROP Y SELECCIÓN DE ARCHIVOS EN EL CENTRO PARA COMPARTIR ---

    private void OnWindowDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
        {
            SelectTab("share");
            AddFilesToQueue(paths);
        }
    }

    private void OnPickFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Elegir uno o varios archivos para compartir con GestoClip",
            Multiselect = true,
            Filter = "Todos los archivos (*.*)|*.*|Imágenes (*.png;*.jpg;*.jpeg;*.webp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.gif"
        };
        if (dlg.ShowDialog(this) == true)
            AddFilesToQueue(dlg.FileNames);
    }

    private void OnAddClipboardFiles(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Clipboard.ContainsFileDropList())
        {
            var list = System.Windows.Clipboard.GetFileDropList().Cast<string>().ToArray();
            AddFilesToQueue(list);
        }
        else
        {
            FooterStatusText.Text = "No hay archivos copiados en el portapapeles en este momento.";
        }
    }

    private void AddFilesToQueue(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
            var item = QueuedFileItem.FromFile(p);
            if (item.SizeBytes > Framing.MaxSize)
            {
                System.Windows.MessageBox.Show(this,
                    $"El archivo '{item.Name}' ({item.SizeFormatted}) supera el límite máximo permitido de 512 MiB.",
                    "Límite de tamaño excedido", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }
            if (_queuedFiles.All(x => !string.Equals(x.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase)))
            {
                _queuedFiles.Add(item);
                added++;
            }
        }
        RefreshQueueListUi();
        if (added > 0)
            FooterStatusText.Text = $"Se agregaron {added} archivo(s) a la cola de envío.";
    }

    private void OnClearQueue(object sender, RoutedEventArgs e)
    {
        _queuedFiles.Clear();
        RetrySendBtn.Visibility = Visibility.Collapsed;
        RefreshQueueListUi();
    }

    private void RefreshQueueListUi()
    {
        QueuedFilesListBox.Items.Clear();
        foreach (var item in _queuedFiles)
            QueuedFilesListBox.Items.Add($"📄 {item.Name}   ({item.SizeFormatted})   [{item.Kind}]");

        var totalBytes = _queuedFiles.Sum(x => x.SizeBytes);
        var totalStr = totalBytes >= 1024 * 1024 ? $"{totalBytes / (1024.0 * 1024.0):F2} MB" : $"{totalBytes / 1024} KB";
        QueueHeaderTitle.Text = $"1. Archivos seleccionados ({_queuedFiles.Count} — {totalStr})";
        TransferProgressText.Text = _queuedFiles.Count == 0
            ? "Arrastra uno o varios archivos aquí (máx. 512 MiB c/u)"
            : $"Listos {_queuedFiles.Count} archivo(s) ({totalStr})";
    }

    private async void OnSendQueuedFiles(object sender, RoutedEventArgs e)
    {
        if (_queuedFiles.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "Primero elige o arrastra al menos un archivo para enviar.", "GestoClip", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedPeers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sel in TargetPeersListBox.SelectedItems)
        {
            if (sel is TargetPeerItem tpi)
                selectedPeers.Add(tpi.Peer.Fingerprint);
        }

        _sendCts?.Cancel();
        _sendCts = new CancellationTokenSource();
        SendFilesBtn.IsEnabled = false;
        CancelSendBtn.Visibility = Visibility.Visible;
        RetrySendBtn.Visibility = Visibility.Collapsed;

        try
        {
            var snapshot = _queuedFiles.ToList();
            await _controller.SendQueuedFilesAsync(
                snapshot,
                selectedPeers.Count > 0 ? selectedPeers : null,
                _sendCts.Token,
                (idx, total, msg) => Dispatcher.BeginInvoke(() =>
                {
                    TransferProgressText.Text = msg;
                    FooterStatusText.Text = msg;
                }));

            _queuedFiles.Clear();
            RefreshQueueListUi();
            TransferProgressText.Text = "✅ Todos los archivos se enviaron con éxito.";
            FooterStatusText.Text = "✅ Transferencia completada.";
        }
        catch (OperationCanceledException)
        {
            TransferProgressText.Text = "🚫 Envío cancelado.";
            RetrySendBtn.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            TransferProgressText.Text = $"❌ Error: {ex.Message}";
            FooterStatusText.Text = $"Error en envío: {ex.Message}";
            RetrySendBtn.Visibility = Visibility.Visible;
        }
        finally
        {
            SendFilesBtn.IsEnabled = true;
            CancelSendBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelSend(object sender, RoutedEventArgs e) => _sendCts?.Cancel();

    public void RefreshActivityList()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ActivityListBox.Items.Clear();
            lock (_controller.ActivityHistory)
            {
                foreach (var entry in _controller.ActivityHistory)
                    ActivityListBox.Items.Add(new ActivityListItem(entry));
            }
        });
    }

    private void OnOpenSelectedActivityLocation(object sender, RoutedEventArgs e)
    {
        if (ActivityListBox.SelectedItem is ActivityListItem item && !string.IsNullOrWhiteSpace(item.Entry.SavedFilePath))
        {
            if (File.Exists(item.Entry.SavedFilePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Entry.SavedFilePath}\"") { UseShellExecute = true });
                return;
            }
            var dir = Path.GetDirectoryName(item.Entry.SavedFilePath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
                return;
            }
        }
        OnOpenDownloadsGestoClip(sender, e);
    }

    private void OnOpenPicturesGestoClip(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "GestoClip");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private void OnOpenDownloadsGestoClip(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GestoClip");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    // --- GESTOS Y CÁMARA ---

    private async void OnStartCameraGesture(object sender, RoutedEventArgs e) => await _controller.RunGestureAsync(false);
    private async void OnStartCameraDiag(object sender, RoutedEventArgs e) => await _controller.RunGestureAsync(true);
    private async void OnManualCopy(object sender, RoutedEventArgs e) => await _controller.ExecuteConfiguredFistActionAsync();
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

    // --- EMPAREJAMIENTO QR Y GESTIÓN DE DISPOSITIVOS ---

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
        QrDetailsText.Text = $"Dirección: {_currentInvite.Ip}:{_currentInvite.Port}  •  Equipo: {_currentInvite.Name}\nHuella TLS: {_currentInvite.Fp[..16]}…";
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
                RefreshPeersLists();
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
        if (ViewPeers.Visibility == Visibility.Visible)
        {
            UpdateQrCountdown();
            RefreshDiscoveredOnly();
        }
        else if (ViewShare.Visibility == Visibility.Visible)
        {
            RefreshDiscoveredOnly();
        }
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
            var activePeers = _controller.Discovery.Peers;
            PairedListBox.Items.Clear();
            TargetPeersListBox.Items.Clear();

            foreach (var p in _controller.Paired.List())
            {
                var online = activePeers.FirstOrDefault(d => string.Equals(d.PubKeyFp, p.Fingerprint, StringComparison.OrdinalIgnoreCase));
                PairedListBox.Items.Add(new PairedListItem(p, online));
                TargetPeersListBox.Items.Add(new TargetPeerItem(p, online));
            }
            RefreshDiscoveredOnly();
        });
    }

    private void RefreshDiscoveredOnly()
    {
        var activePeers = _controller.Discovery.Peers;
        DiscoveredListBox.Items.Clear();
        foreach (var d in activePeers)
        {
            var isPaired = _controller.Paired.IsPaired(d.PubKeyFp);
            var status = isPaired ? "✅ Emparejado y Reconocido" : "🔸 En red (Sin emparejar)";
            DiscoveredListBox.Items.Add($"{d.Name} [{d.Platform}] — {d.Address}:{d.Port} ({status})");
        }
        var count = _controller.Paired.List().Count;
        var onlineCount = activePeers.Count(d => _controller.Paired.IsPaired(d.PubKeyFp));
        SidebarStatusTitle.Text = count > 0
            ? $"● {onlineCount}/{count} par(es) en línea"
            : "● Escuchando en LAN";
    }

    private void OnRefreshPeersView(object sender, RoutedEventArgs e) => RefreshPeersLists();

    private void OnUnpairSelected(object sender, RoutedEventArgs e)
    {
        if (PairedListBox.SelectedItem is PairedListItem item)
        {
            _controller.Paired.Remove(item.Peer.Fingerprint);
            RefreshPeersLists();
            FooterStatusText.Text = $"Dispositivo eliminado: {item.Peer.Name}";
        }
    }

    private void OnUnpairAll(object sender, RoutedEventArgs e)
    {
        foreach (var p in _controller.Paired.List())
            _controller.Paired.Remove(p.Fingerprint);
        RefreshPeersLists();
        FooterStatusText.Text = "Todos los dispositivos emparejados fueron eliminados.";
    }

    // --- AJUSTES, MODO DE GESTO Y CARPETAS PRESELECCIONADAS ---

    private void OnGestureModeChanged(object sender, RoutedEventArgs e)
    {
        if (_initializingSettingsUi) return;
        var mode = ModeFoldersRadio.IsChecked == true
            ? GestureMode.PreselectedFolders
            : (ModeClipboardOnlyRadio.IsChecked == true ? GestureMode.ClipboardOrScreenshot : GestureMode.ClipboardMultiFiles);
        _controller.UpdateSettings(_controller.Settings with { FistMode = mode });
    }

    private void OnAddPresetFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Seleccionar carpeta para envío rápido por gesto"
        };
        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            var list = (_controller.Settings.PreselectedFolders ?? new List<string>()).ToList();
            if (!list.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(dlg.FolderName);
                _controller.UpdateSettings(_controller.Settings with { PreselectedFolders = list });
                LoadSettingsIntoUi();
            }
        }
    }

    private void OnRemovePresetFolder(object sender, RoutedEventArgs e)
    {
        if (PresetFoldersListBox.SelectedItem is string folder)
        {
            var list = (_controller.Settings.PreselectedFolders ?? new List<string>())
                .Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _controller.UpdateSettings(_controller.Settings with { PreselectedFolders = list });
            LoadSettingsIntoUi();
        }
    }

    private void OnAutoSaveToggled(object sender, RoutedEventArgs e)
    {
        if (_initializingSettingsUi) return;
        _controller.UpdateSettings(_controller.Settings with { AutoSaveReceivedFiles = AutoSaveCheckBox.IsChecked == true });
    }

    private void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        if (_initializingSettingsUi) return;
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

    private sealed record PairedListItem(PairedPeer Peer, Peer? OnlineInfo)
    {
        public override string ToString()
        {
            var fpShort = Peer.Fingerprint[..Math.Min(10, Peer.Fingerprint.Length)];
            var state = OnlineInfo is not null ? $"🟢 En línea ({OnlineInfo.Address})" : "⚪ Desconectado";
            return $"{Peer.Name} [{Peer.Platform}]  •  {state}  •  FP: {fpShort}…";
        }
    }

    private sealed record TargetPeerItem(PairedPeer Peer, Peer? OnlineInfo)
    {
        public override string ToString()
        {
            var state = OnlineInfo is not null ? $"🟢 Conectado ({OnlineInfo.Address})" : "⚪ Sin señal UDP";
            return $"☑️ {Peer.Name} [{Peer.Platform}] — {state}";
        }
    }

    private sealed record ActivityListItem(TransferActivityEntry Entry)
    {
        public override string ToString() => Entry.SummaryText;
    }
}
