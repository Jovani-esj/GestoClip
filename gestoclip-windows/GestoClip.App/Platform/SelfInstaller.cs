using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WpfApp = System.Windows.Application;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace GestoClip.App.Platform;

/// <summary>
/// Permite que el mismo ejecutable autocontenido funcione como instalador gráfico (GestoClip-Setup.exe)
/// cuando se ejecuta con nombre "Setup" o con --install / --uninstall.
/// </summary>
internal static class SelfInstaller
{
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GestoClip");
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GestoClip";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool TryHandleInstallerMode(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            PerformUninstall();
            return true;
        }

        if (args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)))
        {
            PerformInstall(desktopShortcut: true, startMenuShortcut: true, autostart: false, launchAfter: false, _ => { });
            return true;
        }

        var selfPath = Environment.ProcessPath ?? string.Empty;
        var fileName = Path.GetFileName(selfPath);
        var isExplicitSetup = args.Any(a => string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase))
                              || fileName.Contains("Setup", StringComparison.OrdinalIgnoreCase);

        if (!isExplicitSetup)
            return false;

        var app = new WpfApp { ThemeMode = ThemeMode.Dark };
        app.Run(BuildInstallerWindow());
        return true;
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
            Background = new SolidColorBrush(WpfColor.FromRgb(24, 24, 27)),
            Foreground = new SolidColorBrush(WpfColor.FromRgb(244, 244, 245)),
            FontFamily = new WpfFontFamily("Segoe UI Variable Display, Segoe UI")
        };

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

        var header = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        var badge = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(WpfColor.FromRgb(59, 130, 246)),
            Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock { Text = "🖐️", FontSize = 22, HorizontalAlignment = WpfHAlign.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Instalar GestoClip v1.0", FontSize = 20, FontWeight = FontWeights.Bold });
        titles.Children.Add(new TextBlock { Text = "Portapapeles Universal por Gestos (Windows 11 ⇄ Android)", FontSize = 12.5, Foreground = new SolidColorBrush(WpfColor.FromRgb(161, 161, 170)) });
        header.Children.Add(badge);
        header.Children.Add(titles);
        root.Children.Add(header);

        var card = new Border
        {
            Background = new SolidColorBrush(WpfColor.FromRgb(32, 32, 38)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 16)
        };
        var opts = new StackPanel();
        opts.Children.Add(new TextBlock { Text = $"Ruta de instalación: {InstallDir}", FontSize = 11.5, Foreground = new SolidColorBrush(WpfColor.FromRgb(161, 161, 170)), Margin = new Thickness(0, 0, 0, 10) });

        var cbDesktop = new WpfCheckBox { Content = "Crear acceso directo en el Escritorio", IsChecked = true, Margin = new Thickness(0, 4, 0, 4), FontSize = 13 };
        var cbStartMenu = new WpfCheckBox { Content = "Agregar al Menú Inicio de Windows", IsChecked = true, Margin = new Thickness(0, 4, 0, 4), FontSize = 13 };
        var cbAutostart = new WpfCheckBox { Content = "Iniciar automáticamente con Windows", IsChecked = true, Margin = new Thickness(0, 4, 0, 4), FontSize = 13 };
        var cbLaunch = new WpfCheckBox { Content = "Abrir GestoClip al terminar la instalación", IsChecked = true, Margin = new Thickness(0, 4, 0, 4), FontSize = 13 };

        opts.Children.Add(cbDesktop);
        opts.Children.Add(cbStartMenu);
        opts.Children.Add(cbAutostart);
        opts.Children.Add(cbLaunch);
        card.Child = opts;
        root.Children.Add(card);

        var statusText = new TextBlock { Text = "Listo para instalar.", FontSize = 12.5, Foreground = new SolidColorBrush(WpfColor.FromRgb(212, 212, 216)), Margin = new Thickness(0, 0, 0, 14) };
        root.Children.Add(statusText);

        var btnRow = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = WpfHAlign.Right };
        var installBtn = new WpfButton { Content = "Instalar ahora", Padding = new Thickness(22, 9, 22, 9), FontWeight = FontWeights.SemiBold, FontSize = 13.5, Margin = new Thickness(0, 0, 10, 0) };
        var cancelBtn = new WpfButton { Content = "Cancelar", Padding = new Thickness(16, 9, 16, 9), FontSize = 13.5 };

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
                WpfMessageBox.Show(win, "GestoClip se instaló correctamente en tu equipo.", "Instalación completada", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var currentPid = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("GestoClip.App"))
        {
            if (p.Id == currentPid) continue;
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        progress("Copiando aplicación y modelos ONNX...");
        Directory.CreateDirectory(InstallDir);

        var selfPath = Environment.ProcessPath ?? throw new InvalidOperationException("No se pudo determinar la ruta del ejecutable actual.");
        var exePath = Path.Combine(InstallDir, "GestoClip.App.exe");
        File.Copy(selfPath, exePath, overwrite: true);

        var srcAssets = Path.Combine(AppContext.BaseDirectory, "Assets");
        var dstAssets = Path.Combine(InstallDir, "Assets");
        Directory.CreateDirectory(dstAssets);
        if (Directory.Exists(srcAssets))
        {
            foreach (var file in Directory.GetFiles(srcAssets))
            {
                File.Copy(file, Path.Combine(dstAssets, Path.GetFileName(file)), overwrite: true);
            }
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
            key.SetValue("UninstallString", $"\"{exePath}\" --uninstall");
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
        var currentPid = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("GestoClip.App"))
        {
            if (p.Id == currentPid) continue;
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
