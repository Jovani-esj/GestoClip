using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace GestoClip.Installer;

internal static class SetupProgram
{
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GestoClip");
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GestoClip";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            PerformUninstall();
            return 0;
        }

        if (args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)))
        {
            PerformInstall(desktopShortcut: true, startMenuShortcut: true, autostart: false, launchAfter: false, _ => { });
            return 0;
        }

        var app = new Application { ThemeMode = ThemeMode.Dark };
        app.Run(BuildInstallerWindow());
        return 0;
    }

    private static Window BuildInstallerWindow()
    {
        var win = new Window
        {
            Title = "Instalador de GestoClip — Windows 11",
            Width = 540,
            Height = 420,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)),
            Foreground = new SolidColorBrush(Color.FromRgb(244, 244, 245)),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI")
        };

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        var badge = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
            Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock { Text = "🖐️", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Instalar GestoClip v1.0", FontSize = 20, FontWeight = FontWeights.Bold });
        titles.Children.Add(new TextBlock { Text = "Portapapeles Universal por Gestos (Windows 11 ⇄ Android)", FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)) });
        header.Children.Add(badge);
        header.Children.Add(titles);
        root.Children.Add(header);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 38)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 16)
        };
        var opts = new StackPanel();
        opts.Children.Add(new TextBlock { Text = $"Ruta de instalación: {InstallDir}", FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)), Margin = new Thickness(0, 0, 0, 10) });

        var cbDesktop = new CheckBox { Content = "Crear acceso directo en el Escritorio", IsChecked = true, Margin = new Thickness(0, 4, 0, 4), FontSize = 13 };
        var cbStartMenu = new CheckBox { Content = "Agregar al Menú Inicio de Windows", IsChecked = true, Margin = new Thickness(0, 4, 0, 4), FontSize = 13 };
        var cbAutostart = new CheckBox { Content = "Iniciar automáticamente con Windows", IsChecked = true, Margin = new Thickness(0, 4, 0, 4), FontSize = 13 };
        var cbLaunch = new CheckBox { Content = "Abrir GestoClip al terminar la instalación", IsChecked = true, Margin = new Thickness(0, 4, 0, 4), FontSize = 13 };

        opts.Children.Add(cbDesktop);
        opts.Children.Add(cbStartMenu);
        opts.Children.Add(cbAutostart);
        opts.Children.Add(cbLaunch);
        card.Child = opts;
        root.Children.Add(card);

        var statusText = new TextBlock { Text = "Listo para instalar.", FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 216)), Margin = new Thickness(0, 0, 0, 14) };
        root.Children.Add(statusText);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var installBtn = new Button { Content = "Instalar ahora", Padding = new Thickness(22, 9, 22, 9), FontWeight = FontWeights.SemiBold, FontSize = 13.5, Margin = new Thickness(0, 0, 10, 0) };
        var cancelBtn = new Button { Content = "Cancelar", Padding = new Thickness(16, 9, 16, 9), FontSize = 13.5 };

        cancelBtn.Click += (_, _) => win.Close();
        installBtn.Click += async (_, _) =>
        {
            installBtn.IsEnabled = false;
            cancelBtn.IsEnabled = false;
            var d = cbDesktop.IsChecked == true;
            var s = cbStartMenu.IsChecked == true;
            var a = cbAutostart.IsChecked == true;
            var l = cbLaunch.IsChecked == true;
            try
            {
                await Task.Run(() => PerformInstall(d, s, a, l, msg => win.Dispatcher.BeginInvoke(() => statusText.Text = msg)));
                MessageBox.Show(win, "GestoClip se instaló correctamente en tu equipo.", "Instalación completada", MessageBoxButton.OK, MessageBoxImage.Information);
                win.Close();
            }
            catch (Exception ex)
            {
                statusText.Text = "Error: " + ex.Message;
                installBtn.IsEnabled = true;
                cancelBtn.IsEnabled = true;
            }
        };

        btnRow.Children.Add(installBtn);
        btnRow.Children.Add(cancelBtn);
        root.Children.Add(btnRow);

        win.Content = root;
        return win;
    }

    private static void PerformInstall(bool desktopShortcut, bool startMenuShortcut, bool autostart, bool launchAfter, Action<string> progress)
    {
        progress("Cerrando instancias previas de GestoClip...");
        foreach (var p in Process.GetProcessesByName("GestoClip.App"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        progress("Extrayendo archivos de la aplicación...");
        if (Directory.Exists(InstallDir))
        {
            try { Directory.Delete(InstallDir, true); } catch { }
        }
        Directory.CreateDirectory(InstallDir);

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                           ?? throw new InvalidOperationException("No se encontró el paquete incrustado payload.zip.");
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(InstallDir, overwriteFiles: true);
        }

        var exePath = Path.Combine(InstallDir, "GestoClip.App.exe");
        var uninstPath = Path.Combine(InstallDir, "Uninstall-GestoClip.exe");
        if (Environment.ProcessPath is { } selfPath && File.Exists(selfPath))
        {
            try { File.Copy(selfPath, uninstPath, overwrite: true); } catch { }
        }

        progress("Registrando accesos directos y desinstalador...");
        if (desktopShortcut)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateShortcut(Path.Combine(desktop, "GestoClip.lnk"), exePath, InstallDir, "GestoClip — Portapapeles Universal por Gestos");
        }
        if (startMenuShortcut)
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            CreateShortcut(Path.Combine(programs, "GestoClip.lnk"), exePath, InstallDir, "GestoClip — Portapapeles Universal por Gestos");
        }

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true))
        {
            key.SetValue("DisplayName", "GestoClip");
            key.SetValue("DisplayVersion", "1.0.0");
            key.SetValue("Publisher", "GestoClip");
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("DisplayIcon", exePath);
            key.SetValue("UninstallString", $"\"{uninstPath}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
        {
            if (autostart)
                run.SetValue("GestoClip", $"\"{exePath}\" --minimized");
            else
                run.DeleteValue("GestoClip", throwOnMissingValue: false);
        }

        progress("¡Instalación finalizada!");
        if (launchAfter && File.Exists(exePath))
        {
            Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = InstallDir, UseShellExecute = true });
        }
    }

    private static void PerformUninstall()
    {
        foreach (var p in Process.GetProcessesByName("GestoClip.App"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
        try
        {
            var desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "GestoClip.lnk");
            var startLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "GestoClip.lnk");
            if (File.Exists(desktopLnk)) File.Delete(desktopLnk);
            if (File.Exists(startLnk)) File.Delete(startLnk);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue("GestoClip", throwOnMissingValue: false);
        }
        catch { }

        // Borrado diferido de la carpeta de instalación tras salir del proceso actual
        if (Directory.Exists(InstallDir))
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/C timeout /t 2 /nobreak > NUL & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
    }

    private static void CreateShortcut(string lnkPath, string targetPath, string workDir, string description)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workDir;
            shortcut.Description = description;
            shortcut.IconLocation = $"{targetPath},0";
            shortcut.Save();
        }
        catch { }
    }
}
