using System.Security.Cryptography;
using System.Text;
using GestoClip.Core.Protocol;

namespace GestoClip.Core.Transport;

/// <summary>
/// Formato en el cable: una línea JSON (ClipMessage) terminada en \n, seguida de Size bytes binarios.
/// </summary>
public static class Framing
{
    public const int Version = 1;
    public const long MaxSize = 512L * 1024 * 1024;

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static async Task WriteAsync(Stream s, Payload p, CancellationToken ct = default)
    {
        var header = new ClipMessage(Version, p.Kind, p.Name, p.Mime, p.Data.LongLength, Sha256Hex(p.Data));
        var line = Encoding.UTF8.GetBytes(Json.Serialize(header) + "\n");
        await s.WriteAsync(line, ct);
        await s.WriteAsync(p.Data, ct);
        await s.FlushAsync(ct);
    }

    public static async Task<Payload> ReadAsync(Stream s, CancellationToken ct = default, long maxSize = MaxSize)
    {
        var line = await ReadLineAsync(s, ct) ?? throw new InvalidDataException("Conexión cerrada");
        var h = Json.Deserialize<ClipMessage>(line) ?? throw new InvalidDataException("Encabezado inválido");
        if (h.Size < 0 || h.Size > maxSize) throw new InvalidDataException("Tamaño fuera de rango");
        var buf = new byte[h.Size];
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException("Transferencia incompleta");
            read += n;
        }
        if (!string.Equals(Sha256Hex(buf), h.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 no coincide");
        return new Payload(h.Kind, h.Name, h.Mime, buf);
    }

    private static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one, ct);
            if (n == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
            if (bytes.Count > 8192) throw new InvalidDataException("Encabezado demasiado largo");
        }
    }
}
