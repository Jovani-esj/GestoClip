using System.Drawing;
using System.IO;
using System.Windows.Forms;
using GestoClip.App.Platform;
using GestoClip.Core.Actions;
using GestoClip.Core.Discovery;
using GestoClip.Core.Pairing;
using GestoClip.Core.Protocol;
using GestoClip.Core.Transport;
using GestoClip.Core.Vision;
using GestoClip.Vision;

namespace GestoClip.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (SelfInstaller.TryHandleInstallerMode(args))
            return;

        using var mutex = new Mutex(true, "GestoClip.SingleInstance", out var first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        var wpfApp = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
        };

        var startMinimized = args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        System.Windows.Forms.Application.Run(new TrayContext(wpfApp, startMinimized));
    }
}

/// <summary>Controlador principal: coordina el Centro para Compartir, Gestos, Bandeja, Red mTLS y Configuración.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly System.Windows.Application _wpfApp;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkey;
    private readonly GestureActions _actions;
    private readonly ToolStripMenuItem _pairedSubMenu;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly MainWindow _mainWindow;
    private readonly string _settingsPath;

    public UdpDiscovery Discovery { get; }
    public SecureChannel Channel { get; }
    public PairedStore Paired { get; }
    public PairingService Pairing { get; }
    public string DeviceId { get; }
    public UserSettings Settings { get; private set; }
    public List<TransferActivityEntry> ActivityHistory { get; } = new();

    public TrayContext(System.Windows.Application wpfApp, bool startMinimized)
    {
        _wpfApp = wpfApp;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GestoClip");
        Directory.CreateDirectory(dir);

        _settingsPath = Path.Combine(dir, "settings.json");
        Settings = UserSettings.Load(_settingsPath);

        var idPath = Path.Combine(dir, "device.id");
        DeviceId = File.Exists(idPath) ? File.ReadAllText(idPath).Trim() : Guid.NewGuid().ToString("N")[..12];
        File.WriteAllText(idPath, DeviceId);

        Paired = new PairedStore(Path.Combine(dir, "paired.json"));
        Pairing = new PairingService(Paired, DeviceId, Environment.MachineName, "WINDOWS");
        var cert = DeviceCert.LoadOrCreate(Path.Combine(dir, "device.pfx"));

        UdpDiscovery? disc = null;
        Channel = new SecureChannel(cert, () => disc!.Peers, Paired.IsPaired, SecureChannel.Port, Pairing);
        var beacon = new Beacon(1, DeviceId, Environment.MachineName, "WINDOWS",
            SecureChannel.Port, Channel.LocalFingerprint, 0);
        disc = new UdpDiscovery(beacon);
        Discovery = disc;

        _actions = new GestureActions(new WinClipboardService(), new WinInputInjector(), new WinScreenCapture(), Channel);

        Discovery.PeerSeen += peer =>
        {
            // Reconocimiento automático si el dispositivo emparejado vuelve a conectarse a la red
            Paired.ReconcileDiscoveredPeer(peer.PubKeyFp, peer.DeviceId, peer.Name, peer.Platform);
        };

        Channel.Received += (p, remoteFp) =>
        {
            _actions.Receive(p);
            var peerName = Paired.List().FirstOrDefault(x => string.Equals(x.Fingerprint, remoteFp, StringComparison.OrdinalIgnoreCase))?.Name
                           ?? remoteFp[..Math.Min(8, remoteFp.Length)];

            string? savedPath = null;
            if (p.Kind != Kinds.Text && Settings.AutoSaveReceivedFiles)
            {
                try
                {
                    savedPath = ReceivedFileStore.SaveAtomically(p);
                }
                catch (Exception ex)
                {
                    RecordActivity(new TransferActivityEntry(
                        Guid.NewGuid().ToString("N")[..8], p.Name, p.Kind, p.Data.LongLength,
                        TransferDirection.Received, peerName, TransferStatus.Failed, null, ex.Message, DateTime.Now));
                    Toast($"Error al guardar {p.Name}: {ex.Message}");
                    return;
                }
            }

            RecordActivity(new TransferActivityEntry(
                Guid.NewGuid().ToString("N")[..8], p.Name, p.Kind, p.Data.LongLength,
                TransferDirection.Received, peerName, TransferStatus.Completed, savedPath, null, DateTime.Now));

            var kb = Math.Max(1, p.Data.Length / 1024);
            var locationInfo = savedPath is not null ? $"Guardado en {Path.GetFileName(Path.GetDirectoryName(savedPath))}/GestoClip" : "Listo para pegar con palma abierta (🖐️)";
            _mainWindow?.NotifyTransferEvent($"📥 Recibido: {p.Name} ({kb} KB)", locationInfo);
            Toast($"Recibido de {peerName}: {p.Name}");
        };

        Channel.Rejected += fp =>
        {
            var shortFp = fp[..Math.Min(8, fp.Length)];
            _mainWindow?.NotifyTransferEvent("⚠️ Conexión rechazada", $"Certificado no emparejado: {shortFp}…");
            Toast($"Conexión rechazada (no emparejado): {shortFp}…");
        };

        Pairing.DevicePaired += peer =>
        {
            _mainWindow?.NotifyTransferEvent($"🔒 Emparejado con {peer.Name}", $"Plataforma: {peer.Platform}");
            _mainWindow?.RefreshPeersLists();
            Toast($"Dispositivo emparejado: {peer.Name} ({peer.Platform})");
        };

        Discovery.Start();
        Channel.StartListening();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir Centro para Compartir (GestoClip)", null, (_, _) => ShowMainWindow("share"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Activar Gesto por Cámara (Ctrl+Alt+G)", null, async (_, _) => await RunGestureAsync(false));
        menu.Items.Add("Acción Directa: Enviar según Modo de Gesto (Ctrl+Alt+S)", null, async (_, _) => await ExecuteConfiguredFistActionAsync());
        menu.Items.Add("Acción Directa: Pegar contenido recibido", null, (_, _) => DoPaste());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Dispositivos y Emparejamiento QR...", null, (_, _) => ShowMainWindow("peers"));

        _pairedSubMenu = new ToolStripMenuItem("Dispositivos emparejados");
        _pairedSubMenu.DropDownOpening += (_, _) => RebuildPairedSubMenu();
        menu.Items.Add(_pairedSubMenu);

        _autostartItem = new ToolStripMenuItem("Iniciar con Windows")
        {
            Checked = AutostartService.IsEnabled()
        };
        _autostartItem.Click += (_, _) => SetAutostart(!_autostartItem.Checked);
        menu.Items.Add(_autostartItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir por completo", null, (_, _) => ExitApplication());

        Icon trayIcon = SystemIcons.Application;
        try
        {
            var icoFile = Path.Combine(AppContext.BaseDirectory, "Assets", "gestoclip.ico");
            if (File.Exists(icoFile)) trayIcon = new Icon(icoFile);
        }
        catch { }

        _tray = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "GestoClip — Centro para Compartir y Gestos",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow("share");

        _hotkey = new HotkeyWindow();
        _hotkey.HotkeyPressed += async id =>
        {
            if (id == 1) await RunGestureAsync(false);
            else if (id == 2) await ExecuteConfiguredFistActionAsync();
        };
        _hotkey.Register(1, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.G);
        _hotkey.Register(2, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.S);

        _mainWindow = new MainWindow(this);
        if (!startMinimized)
            _mainWindow.Show();
    }

    public void UpdateSettings(UserSettings newSettings)
    {
        Settings = newSettings;
        Settings.Save(_settingsPath);
    }

    public void RecordActivity(TransferActivityEntry entry)
    {
        lock (ActivityHistory)
        {
            var idx = ActivityHistory.FindIndex(x => x.Id == entry.Id);
            if (idx >= 0) ActivityHistory[idx] = entry;
            else ActivityHistory.Insert(0, entry);
            if (ActivityHistory.Count > 100) ActivityHistory.RemoveAt(ActivityHistory.Count - 1);
        }
        _mainWindow?.RefreshActivityList();
    }

    public void ShowMainWindow(string tab)
    {
        if (_mainWindow.WindowState == System.Windows.WindowState.Minimized)
            _mainWindow.WindowState = System.Windows.WindowState.Normal;
        _mainWindow.Show();
        _mainWindow.Activate();
        _mainWindow.SelectTab(tab);
    }

    private void RebuildPairedSubMenu()
    {
        _pairedSubMenu.DropDownItems.Clear();
        var list = Paired.List();
        if (list.Count == 0)
        {
            _pairedSubMenu.DropDownItems.Add(new ToolStripMenuItem("(Ninguno)") { Enabled = false });
            return;
        }
        foreach (var p in list)
        {
            var shortFp = p.Fingerprint[..Math.Min(8, p.Fingerprint.Length)];
            var item = new ToolStripMenuItem($"{p.Name} [{p.Platform}] ({shortFp}…) — clic para desvincular");
            item.Click += (_, _) =>
            {
                Paired.Remove(p.Fingerprint);
                _mainWindow.RefreshPeersLists();
                Toast($"Desvinculado: {p.Name}");
            };
            _pairedSubMenu.DropDownItems.Add(item);
        }
    }

    public void SetAutostart(bool enable)
    {
        try
        {
            AutostartService.SetEnabled(enable);
            _autostartItem.Checked = AutostartService.IsEnabled();
            Toast(_autostartItem.Checked ? "GestoClip iniciará con Windows." : "Inicio con Windows desactivado.");
        }
        catch (Exception e)
        {
            Toast("No se pudo cambiar el inicio automático: " + e.Message);
        }
    }

    private HandTracker? _tracker;
    private bool _running;

    public async Task RunGestureAsync(bool debug)
    {
        if (_running) return;
        _running = true;
        try
        {
            _tracker ??= new HandTracker(Path.Combine(AppContext.BaseDirectory, "Assets"));
            var session = new CameraSession(_tracker);
            session.FramePreview += (jpg, pose, n) => _mainWindow.UpdateCameraFrame(jpg, pose, n);

            StreamWriter? log = null;
            if (debug)
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GestoClip", "camera-debug.log");
                log = new StreamWriter(path, false) { AutoFlush = true };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                session.Frame += (pose, n) => log.WriteLine($"{sw.ElapsedMilliseconds,6} ms  frame {n,4}  {pose}");
                Toast("Diagnóstico: muestra la mano a la cámara 10 s (abierta, luego cerrada).");
            }
            else Toast("Cámara activa 6 s: palma→puño envía según tu modo, puño→palma pega.");

            var ev = await session.RunAsync(TimeSpan.FromSeconds(debug ? 10 : 6));
            log?.Dispose();
            if (debug)
            {
                var msg = "Diagnóstico terminado. Log guardado en %APPDATA%\\GestoClip\\camera-debug.log";
                _mainWindow.NotifyCameraStopped(msg);
                Toast(msg);
                return;
            }
            switch (ev)
            {
                case GestureEvent.Copy:
                    _mainWindow.NotifyCameraStopped("Gesto detectado: ✊ Enviar / Capturar");
                    await ExecuteConfiguredFistActionAsync();
                    break;
                case GestureEvent.Paste:
                    _mainWindow.NotifyCameraStopped("Gesto detectado: 🖐️ Pegar");
                    DoPaste();
                    break;
                default:
                    _mainWindow.NotifyCameraStopped("Tiempo agotado sin detectar transición de gesto.");
                    Toast("No se detectó ningún gesto.");
                    break;
            }
        }
        catch (Exception e)
        {
            _mainWindow.NotifyCameraStopped("Error de cámara: " + e.Message);
            Toast("Cámara: " + e.Message);
        }
        finally { _running = false; }
    }

    /// <summary>Ejecuta la acción del puño cerrado según la preferencia del usuario (Portapapeles/Múltiples archivos o Carpetas Preseleccionadas).</summary>
    public async Task ExecuteConfiguredFistActionAsync()
    {
        try
        {
            if (Settings.FistMode == GestureMode.PreselectedFolders && Settings.PreselectedFolders is { Count: > 0 })
            {
                var filesToSend = new List<QueuedFileItem>();
                foreach (var folder in Settings.PreselectedFolders)
                {
                    if (!Directory.Exists(folder)) continue;
                    var latest = new DirectoryInfo(folder)
                        .GetFiles()
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .Take(5)
                        .Select(f => QueuedFileItem.FromFile(f.FullName));
                    filesToSend.AddRange(latest);
                }

                if (filesToSend.Count > 0)
                {
                    await SendQueuedFilesAsync(filesToSend, null, CancellationToken.None);
                    return;
                }
            }

            var payloads = await _actions.OnFistAllAsync();
            foreach (var p in payloads)
            {
                RecordActivity(new TransferActivityEntry(
                    Guid.NewGuid().ToString("N")[..8], p.Name, p.Kind, p.Data.LongLength,
                    TransferDirection.Sent, "Equipos emparejados", TransferStatus.Completed, null, null, DateTime.Now));
            }
            var totalKb = Math.Max(1, payloads.Sum(x => x.Data.LongLength) / 1024);
            _mainWindow.NotifyTransferEvent($"📤 Enviados {payloads.Count} elemento(s)", $"{totalKb} KB enviados a los equipos emparejados");
            Toast($"Enviados {payloads.Count} elemento(s)");
        }
        catch (Exception e)
        {
            Toast("Error al enviar: " + e.Message);
        }
    }

    public async Task SendQueuedFilesAsync(
        IReadOnlyList<QueuedFileItem> items,
        ISet<string>? targetFingerprints,
        CancellationToken ct,
        Action<int, int, string>? onProgress = null)
    {
        var total = items.Count;
        var index = 0;
        foreach (var item in items)
        {
            index++;
            var actId = Guid.NewGuid().ToString("N")[..8];
            onProgress?.Invoke(index, total, $"Enviando ({index}/{total}): {item.Name}...");
            RecordActivity(new TransferActivityEntry(
                actId, item.Name, item.Kind, item.SizeBytes,
                TransferDirection.Sent, "Equipos seleccionados", TransferStatus.InProgress, item.FullPath, null, DateTime.Now));

            try
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(item.FullPath))
                    throw new FileNotFoundException($"No se encontró el archivo '{item.Name}'.");
                if (item.SizeBytes > Framing.MaxSize)
                    throw new InvalidOperationException($"El archivo '{item.Name}' ({item.SizeFormatted}) supera el límite máximo de 512 MiB.");

                var bytes = await File.ReadAllBytesAsync(item.FullPath, ct);
                var payload = new Payload(item.Kind, item.Name, item.Mime, bytes);
                var sentCount = await Channel.SendToSelectedPeersAsync(payload, targetFingerprints, ct);
                if (sentCount == 0)
                    throw new IOException("Ningún equipo emparejado seleccionado respondió en la red local.");

                RecordActivity(new TransferActivityEntry(
                    actId, item.Name, item.Kind, item.SizeBytes,
                    TransferDirection.Sent, $"{sentCount} equipo(s)", TransferStatus.Completed, item.FullPath, null, DateTime.Now));
            }
            catch (OperationCanceledException)
            {
                RecordActivity(new TransferActivityEntry(
                    actId, item.Name, item.Kind, item.SizeBytes,
                    TransferDirection.Sent, "Cancelado", TransferStatus.Cancelled, item.FullPath, "Cancelado por el usuario", DateTime.Now));
                throw;
            }
            catch (Exception ex)
            {
                RecordActivity(new TransferActivityEntry(
                    actId, item.Name, item.Kind, item.SizeBytes,
                    TransferDirection.Sent, "Error", TransferStatus.Failed, item.FullPath, ex.Message, DateTime.Now));
                throw;
            }
        }
    }

    public void DoPaste()
    {
        if (_actions.OnPalm())
            _mainWindow.NotifyTransferEvent("📋 Contenido pegado", "Se inyectó en el portapapeles y se envió Ctrl+V.");
        else
            Toast("No hay nada pendiente por pegar.");
    }

    public void Toast(string msg) => _tray.ShowBalloonTip(2500, "GestoClip", msg, ToolTipIcon.Info);

    public void ExitApplication() => ExitThread();

    protected override void ExitThreadCore()
    {
        _mainWindow.AllowFullClose = true;
        _mainWindow.Close();
        _wpfApp.Shutdown();
        _tray.Visible = false;
        _hotkey.Dispose();
        Discovery.Dispose();
        Channel.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public const uint MOD_ALT = 1, MOD_CONTROL = 2;
    private const int WM_HOTKEY = 0x312;
    public event Action<int>? HotkeyPressed;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);

    public HotkeyWindow() => CreateHandle(new CreateParams());
    public void Register(int id, uint mod, uint vk) => RegisterHotKey(Handle, id, mod, vk);
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY) HotkeyPressed?.Invoke((int)m.WParam);
        base.WndProc(ref m);
    }
    public void Dispose()
    {
        UnregisterHotKey(Handle, 1);
        UnregisterHotKey(Handle, 2);
        DestroyHandle();
    }
}
