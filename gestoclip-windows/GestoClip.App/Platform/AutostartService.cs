using Microsoft.Win32;

namespace GestoClip.App.Platform;

public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GestoClip";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var val = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled, string? executablePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            var exe = executablePath ?? Environment.ProcessPath ?? AppContext.BaseDirectory;
            key.SetValue(ValueName, $"\"{exe}\" --minimized");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
