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

/// <summary>Controlador principal: coordina la ventana Windows 11, el icono de bandeja, la red TLS y la cámara.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly System.Windows.Application _wpfApp;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkey;
    private readonly GestureActions _actions;
    private readonly ToolStripMenuItem _pairedSubMenu;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly MainWindow _mainWindow;

    public UdpDiscovery Discovery { get; }
    public SecureChannel Channel { get; }
    public PairedStore Paired { get; }
    public PairingService Pairing { get; }
    public string DeviceId { get; }

    public TrayContext(System.Windows.Application wpfApp, bool startMinimized)
    {
        _wpfApp = wpfApp;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GestoClip");
        Directory.CreateDirectory(dir);

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

        Channel.Received += (p, _) =>
        {
            _actions.Receive(p);
            var kb = Math.Max(1, p.Data.Length / 1024);
            _mainWindow?.NotifyTransferEvent($"📥 Recibido: {p.Name} ({p.Kind})", $"{kb} KB listos para pegar con palma abierta (🖐️)");
            Toast($"Recibido: {p.Name}. Abre la palma para pegar.");
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
            Toast($"Dispositivo emparejado por QR: {peer.Name} ({peer.Platform})");
        };

        Discovery.Start();
        Channel.StartListening();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir ventana principal de GestoClip", null, (_, _) => ShowMainWindow("gestures"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Gestos por cámara (Ctrl+Alt+G)", null, async (_, _) => await RunGestureAsync(false));
        menu.Items.Add("Diagnóstico de cámara (10 s, log)", null, async (_, _) => await RunGestureAsync(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Enviar ahora (puño)", null, async (_, _) => await DoCopyAsync());
        menu.Items.Add("Pegar ahora (palma)", null, (_, _) => DoPaste());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Emparejar dispositivo (QR)...", null, (_, _) => ShowMainWindow("pair"));

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

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "GestoClip — Portapapeles por Gestos",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow("gestures");

        _hotkey = new HotkeyWindow();
        _hotkey.Pressed += async () => await RunGestureAsync(false);
        _hotkey.Register(HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.G);

        _mainWindow = new MainWindow(this);
        if (!startMinimized)
            _mainWindow.Show();
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
            else Toast("Cámara activa 6 s: palma→puño copia, puño→palma pega.");

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
                    _mainWindow.NotifyCameraStopped("Gesto detectado: ✊ Copiar / Capturar");
                    await DoCopyAsync();
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

    public async Task DoCopyAsync()
    {
        try
        {
            var p = await _actions.OnFistAsync();
            var kb = Math.Max(1, p.Data.Length / 1024);
            _mainWindow.NotifyTransferEvent($"📤 Enviado: {p.Name} ({p.Kind})", $"{kb} KB enviados a los pares emparejados");
            Toast($"Enviado: {p.Name}");
        }
        catch (Exception e) { Toast("Error al enviar: " + e.Message); }
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
    public event Action? Pressed;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);

    public HotkeyWindow() => CreateHandle(new CreateParams());
    public void Register(uint mod, uint vk) => RegisterHotKey(Handle, 1, mod, vk);
    protected override void WndProc(ref Message m) { if (m.Msg == WM_HOTKEY) Pressed?.Invoke(); base.WndProc(ref m); }
    public void Dispose() { UnregisterHotKey(Handle, 1); DestroyHandle(); }
}
