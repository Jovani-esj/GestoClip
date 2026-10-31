using GestoClip.Core.Discovery;
using GestoClip.Core.Pairing;
using GestoClip.Core.Protocol;
using GestoClip.Core.Transport;

namespace GestoClip.Core.Actions;

public enum GestureMode
{
    ClipboardOrScreenshot = 0,
    ClipboardMultiFiles = 1,
    PreselectedFolders = 2
}

public record UserSettings(
    GestureMode FistMode = GestureMode.ClipboardMultiFiles,
    List<string>? PreselectedFolders = null,
    bool AutoSaveReceivedFiles = true,
    string HotkeyModifiers = "Ctrl+Alt",
    string HotkeyKey = "G"
)
{
    public static UserSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return Json.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new UserSettings();
        }
        catch { }
        return new UserSettings(GestureMode.ClipboardMultiFiles, new List<string>(), true, "Ctrl+Alt", "G");
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Json.Serialize(this));
    }
}

public record QueuedFileItem(
    string FullPath,
    string Name,
    long SizeBytes,
    string Kind,
    string Mime
)
{
    public string SizeFormatted => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / (1024.0 * 1024.0):F2} MB"
        : $"{Math.Max(1, SizeBytes / 1024)} KB";

    public static QueuedFileItem FromFile(string path)
    {
        var fi = new FileInfo(path);
        var ext = fi.Extension.ToLowerInvariant();
        var isImg = ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";
        var mime = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            ".mp4" => "video/mp4",
            ".txt" or ".md" => "text/plain",
            _ => "application/octet-stream"
        };
        return new QueuedFileItem(fi.FullName, fi.Name, fi.Exists ? fi.Length : 0L, isImg ? Kinds.Image : Kinds.File, mime);
    }
}

public enum TransferDirection { Sent, Received }
public enum TransferStatus { InProgress, Completed, Failed, Cancelled }

public record TransferActivityEntry(
    string Id,
    string FileName,
    string Kind,
    long SizeBytes,
    TransferDirection Direction,
    string PeerName,
    TransferStatus Status,
    string? SavedFilePath,
    string? ErrorMessage,
    DateTime Timestamp
)
{
    public string SummaryText
    {
        get
        {
            var arrow = Direction == TransferDirection.Sent ? "📤 Enviado" : "📥 Recibido";
            var kb = SizeBytes >= 1024 * 1024 ? $"{SizeBytes / (1024.0 * 1024.0):F1} MB" : $"{Math.Max(1, SizeBytes / 1024)} KB";
            var state = Status switch
            {
                TransferStatus.Completed => "✅ Completado",
                TransferStatus.InProgress => "⏳ En progreso...",
                TransferStatus.Cancelled => "🚫 Cancelado",
                _ => $"❌ Error: {ErrorMessage}"
            };
            return $"{arrow}: {FileName} ({kb})  •  {PeerName}  •  {state}  [{Timestamp:HH:mm:ss}]";
        }
    }
}

/// <summary>
/// Guardado atómico libre de colisiones de nombres en Imágenes/GestoClip o Descargas/GestoClip.
/// Primero escribe un archivo temporal (.part), verifica la longitud de bytes y lo renombra al destino final.
/// </summary>
public static class ReceivedFileStore
{
    public static string SaveAtomically(Payload payload, string? customPicturesRoot = null, string? customDownloadsRoot = null)
    {
        var isImage = payload.Kind is Kinds.Image or Kinds.Screenshot
                      || payload.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        var baseRoot = isImage
            ? (customPicturesRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))
            : (customDownloadsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

        var targetDir = Path.Combine(baseRoot, "GestoClip");
        Directory.CreateDirectory(targetDir);

        var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(payload.Name)
            ? (isImage ? $"imagen_{DateTime.Now:yyyyMMdd_HHmmss}.png" : $"archivo_{DateTime.Now:yyyyMMdd_HHmmss}.bin")
            : payload.Name);

        var finalPath = GetNonCollidingPath(targetDir, safeName);
        var tempPath = finalPath + $".{Guid.NewGuid():N}.part";

        try
        {
            File.WriteAllBytes(tempPath, payload.Data);
            var writtenInfo = new FileInfo(tempPath);
            if (!writtenInfo.Exists || writtenInfo.Length != payload.Data.LongLength)
                throw new IOException("Verificación de integridad fallida: el tamaño en disco no coincide con el paquete recibido.");

            File.Move(tempPath, finalPath, overwrite: false);
            return finalPath;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public static string GetNonCollidingPath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 10_000; i++)
        {
            var next = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(next)) return next;
        }
        return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    private static string SanitizeFileName(string name)
    {
        var justName = Path.GetFileName(name.Trim());
        foreach (var c in Path.GetInvalidFileNameChars())
            justName = justName.Replace(c, '_');
        return string.IsNullOrWhiteSpace(justName) ? "archivo_gestoclip" : justName;
    }
}
