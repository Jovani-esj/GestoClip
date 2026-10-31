using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GestoClip.Core.Actions;
using GestoClip.Core.Protocol;

namespace GestoClip.App.Platform;

internal static class Native
{
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi; // fija el tamaño correcto de la unión
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    public const uint KEYEVENTF_KEYUP = 0x2;
    public const ushort VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10, VK_C = 0x43, VK_V = 0x56;
}

public sealed class WinInputInjector : IInputInjector
{
    public void CtrlC() => Combo(Native.VK_C);
    public void CtrlV() => Combo(Native.VK_V);

    private static void Combo(ushort key)
    {
        // Suelta Alt/Shift físicos (p. ej. si venimos de un hotkey Ctrl+Alt+G) para que no contaminen el atajo.
        var list = new List<Native.INPUT>();
        foreach (var m in new[] { Native.VK_MENU, Native.VK_SHIFT })
            if ((Native.GetAsyncKeyState(m) & 0x8000) != 0) list.Add(Key(m, true));
        list.Add(Key(Native.VK_CONTROL, false));
        list.Add(Key(key, false));
        list.Add(Key(key, true));
        list.Add(Key(Native.VK_CONTROL, true));
        Native.SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<Native.INPUT>());
    }

    private static Native.INPUT Key(ushort vk, bool up) => new()
    {
        type = 1,
        u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 } }
    };
}

public sealed class WinClipboardService : IClipboardService
{
    public uint SequenceNumber() => Native.GetClipboardSequenceNumber();

    public Payload? ReadAny()
    {
        var all = ReadAll();
        return all.Count > 0 ? all[0] : null;
    }

    public IReadOnlyList<Payload> ReadAll() => OnSta<IReadOnlyList<Payload>>(() =>
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (System.Windows.Forms.Clipboard.ContainsFileDropList())
                {
                    var files = System.Windows.Forms.Clipboard.GetFileDropList();
                    var list = new List<Payload>();
                    foreach (var f in files)
                    {
                        if (string.IsNullOrWhiteSpace(f) || !File.Exists(f)) continue;
                        var item = QueuedFileItem.FromFile(f);
                        if (item.SizeBytes <= GestoClip.Core.Transport.Framing.MaxSize)
                            list.Add(new Payload(item.Kind, item.Name, item.Mime, File.ReadAllBytes(item.FullPath)));
                    }
                    if (list.Count > 0) return list;
                }
                if (System.Windows.Forms.Clipboard.ContainsImage())
                {
                    using var img = System.Windows.Forms.Clipboard.GetImage()!;
                    using var ms = new MemoryStream();
                    img.Save(ms, ImageFormat.Png);
                    return new[] { new Payload(Kinds.Image, $"portapapeles_{DateTime.Now:HHmmss}.png", "image/png", ms.ToArray()) };
                }
                if (System.Windows.Forms.Clipboard.ContainsText())
                    return new[] { new Payload(Kinds.Text, "clip.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes(System.Windows.Forms.Clipboard.GetText())) };
                return Array.Empty<Payload>();
            }
            catch (ExternalException) { Thread.Sleep(40); }
        }
        return Array.Empty<Payload>();
    });

    public void Write(Payload p) => OnSta<object?>(() =>
    {
        switch (p.Kind)
        {
            case Kinds.Text:
                System.Windows.Forms.Clipboard.SetText(System.Text.Encoding.UTF8.GetString(p.Data));
                break;
            case Kinds.Image:
            case Kinds.Screenshot:
                using (var ms = new MemoryStream(p.Data)) System.Windows.Forms.Clipboard.SetImage(Image.FromStream(ms));
                break;
            default:
                var dir = Path.Combine(Path.GetTempPath(), "GestoClip");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, Path.GetFileName(p.Name));
                File.WriteAllBytes(path, p.Data);
                var coll = new System.Collections.Specialized.StringCollection { path };
                System.Windows.Forms.Clipboard.SetFileDropList(coll);
                break;
        }
        return null;
    });

    private static T OnSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (error is not null) throw error;
        return result;
    }
}

public sealed class WinScreenCapture : IScreenCapture
{
    public Payload CaptureActiveMonitor()
    {
        var screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var b = screen.Bounds;
        using var bmp = new Bitmap(b.Width, b.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, System.Drawing.Point.Empty, b.Size);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return new Payload(Kinds.Screenshot, $"captura_{DateTime.Now:yyyyMMdd_HHmmss}.png", "image/png", ms.ToArray());
    }
}
