using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GestoClip.Core.Protocol;
using QRCoder;

namespace GestoClip.Core.Pairing;

public record PairedPeer(
    string Fingerprint,
    string DeviceId,
    string Name,
    string Platform,
    DateTimeOffset PairedAt
);

public record QrPairInvite(
    int V,
    string Ip,
    int Port,
    string Fp,
    string Token,
    string DeviceId,
    string Name,
    string Platform,
    long ExpiresAtUnixMs
)
{
    public const string Scheme = "gestoclip://pair";

    public string ToUri()
    {
        var q = new StringBuilder(Scheme).Append('?');
        q.Append("v=").Append(V);
        q.Append("&ip=").Append(Uri.EscapeDataString(Ip));
        q.Append("&port=").Append(Port);
        q.Append("&fp=").Append(Uri.EscapeDataString(Fp));
        q.Append("&token=").Append(Uri.EscapeDataString(Token));
        q.Append("&id=").Append(Uri.EscapeDataString(DeviceId));
        q.Append("&name=").Append(Uri.EscapeDataString(Name));
        q.Append("&platform=").Append(Uri.EscapeDataString(Platform));
        q.Append("&exp=").Append(ExpiresAtUnixMs);
        return q.ToString();
    }

    public static QrPairInvite? TryParse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.StartsWith("{", StringComparison.Ordinal))
        {
            try { return Json.Deserialize<QrPairInvite>(raw); } catch { return null; }
        }
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, "gestoclip", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "pair", StringComparison.OrdinalIgnoreCase))
            return null;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2)
                map[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
        }

        if (!map.TryGetValue("ip", out var ip) || string.IsNullOrWhiteSpace(ip)) return null;
        if (!map.TryGetValue("fp", out var fp) || string.IsNullOrWhiteSpace(fp)) return null;
        if (!map.TryGetValue("token", out var token) || string.IsNullOrWhiteSpace(token)) return null;

        var v = map.TryGetValue("v", out var vs) && int.TryParse(vs, out var vi) ? vi : 1;
        var port = map.TryGetValue("port", out var ps) && int.TryParse(ps, out var pi) ? pi : 8765;
        var id = map.TryGetValue("id", out var ids) ? ids : "peer";
        var name = map.TryGetValue("name", out var ns) ? ns : "Dispositivo";
        var platform = map.TryGetValue("platform", out var pls) ? pls : "UNKNOWN";
        var exp = map.TryGetValue("exp", out var exs) && long.TryParse(exs, out var exl) ? exl : 0L;

        return new QrPairInvite(v, ip, port, fp.ToLowerInvariant(), token, id, name, platform, exp);
    }

    public static byte[] RenderPng(string content, int pixelsPerModule = 8)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }
}

/// <summary>Almacén persistente de pares autorizados por fingerprint SHA-256 de su certificado TLS.</summary>
public sealed class PairedStore
{
    private readonly string _path;
    private readonly Dictionary<string, PairedPeer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public PairedStore(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            _peers.Clear();
            if (!File.Exists(_path)) return;
            try
            {
                var txt = File.ReadAllText(_path).Trim();
                if (txt.StartsWith("[", StringComparison.Ordinal))
                {
                    var list = Json.Deserialize<List<PairedPeer>>(txt);
                    if (list is not null)
                        foreach (var p in list)
                            if (!string.IsNullOrWhiteSpace(p.Fingerprint))
                                _peers[p.Fingerprint.ToLowerInvariant()] = p;
                }
                else
                {
                    foreach (var line in txt.Split('\n', '\r'))
                    {
                        var fp = line.Trim().ToLowerInvariant();
                        if (fp.Length > 0)
                            _peers[fp] = new PairedPeer(fp, fp[..Math.Min(8, fp.Length)], "Dispositivo", "UNKNOWN", DateTimeOffset.UtcNow);
                    }
                }
            }
            catch { }
        }
    }

    private void SaveNoLock()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, Json.Serialize(_peers.Values.ToList()));
    }

    public bool IsPaired(string fp)
    {
        if (string.IsNullOrWhiteSpace(fp)) return false;
        lock (_lock) return _peers.ContainsKey(fp);
    }

    public IReadOnlyList<PairedPeer> List()
    {
        lock (_lock) return _peers.Values.OrderByDescending(p => p.PairedAt).ToList();
    }

    public PairedPeer AddOrUpdate(string fp, string deviceId, string name, string platform)
    {
        var norm = fp.Trim().ToLowerInvariant();
        var peer = new PairedPeer(norm, deviceId, name, platform, DateTimeOffset.UtcNow);
        lock (_lock)
        {
            _peers[norm] = peer;
            SaveNoLock();
        }
        return peer;
    }

    public bool Remove(string fp)
    {
        lock (_lock)
        {
            if (!_peers.Remove(fp.Trim().ToLowerInvariant())) return false;
            SaveNoLock();
            return true;
        }
    }
}

/// <summary>
/// Gestiona sesiones efímeras de emparejamiento por QR con token de un solo uso (6 dígitos),
/// expiración corta y límite de intentos para evitar fuerza bruta en la red local.
/// </summary>
public sealed class PairingService
{
    public const int MaxFailedAttempts = 3;
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(120);

    private readonly PairedStore _store;
    private readonly string _localDeviceId;
    private readonly string _localName;
    private readonly string _localPlatform;
    private readonly object _lock = new();

    private QrPairInvite? _activeInvite;
    private int _failedAttempts;

    public event Action<PairedPeer>? DevicePaired;

    public PairingService(PairedStore store, string localDeviceId, string localName, string localPlatform = "WINDOWS")
        => (_store, _localDeviceId, _localName, _localPlatform) = (store, localDeviceId, localName, localPlatform);

    public QrPairInvite CreateInvite(string localIp, int port, string localFingerprint, TimeSpan? ttl = null)
    {
        var duration = ttl ?? DefaultTtl;
        var token = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
        var exp = DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds();
        var invite = new QrPairInvite(1, localIp, port, localFingerprint.ToLowerInvariant(), token, _localDeviceId, _localName, _localPlatform, exp);
        lock (_lock)
        {
            _activeInvite = invite;
            _failedAttempts = 0;
        }
        return invite;
    }

    public void CancelInvite()
    {
        lock (_lock)
        {
            _activeInvite = null;
            _failedAttempts = 0;
        }
    }

    public QrPairInvite? ActiveInvite
    {
        get
        {
            lock (_lock)
            {
                if (_activeInvite is null) return null;
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > _activeInvite.ExpiresAtUnixMs)
                {
                    _activeInvite = null;
                    return null;
                }
                return _activeInvite;
            }
        }
    }

    public (PairAckMessage Ack, PairedPeer? Peer) TryAcceptPairRequest(string remoteFp, PairRequestMessage req)
    {
        PairedPeer? paired = null;
        PairAckMessage ack;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_activeInvite is null || now > _activeInvite.ExpiresAtUnixMs)
            {
                _activeInvite = null;
                return (new PairAckMessage(false, _localDeviceId, _localName, _localPlatform, "No hay sesión de emparejamiento activa o expiró"), null);
            }

            var expectedBytes = Encoding.UTF8.GetBytes(_activeInvite.Token);
            var actualBytes = Encoding.UTF8.GetBytes(req.Token ?? "");
            var match = expectedBytes.Length == actualBytes.Length &&
                        CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);

            if (!match)
            {
                _failedAttempts++;
                if (_failedAttempts >= MaxFailedAttempts)
                    _activeInvite = null;
                return (new PairAckMessage(false, _localDeviceId, _localName, _localPlatform, "Token inválido"), null);
            }

            // Éxito: consumir token de un solo uso inmediatamente
            _activeInvite = null;
            _failedAttempts = 0;
            paired = _store.AddOrUpdate(
                remoteFp,
                string.IsNullOrWhiteSpace(req.DeviceId) ? remoteFp[..8] : req.DeviceId,
                string.IsNullOrWhiteSpace(req.Name) ? "Dispositivo" : req.Name,
                string.IsNullOrWhiteSpace(req.Platform) ? "ANDROID" : req.Platform
            );
            ack = new PairAckMessage(true, _localDeviceId, _localName, _localPlatform);
        }

        if (paired is not null)
            DevicePaired?.Invoke(paired);
        return (ack, paired);
    }

    public static string GetPreferredLanIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var name = ni.Name.ToLowerInvariant();
                var desc = ni.Description.ToLowerInvariant();
                if (name.Contains("vethernet") || name.Contains("vmware") || name.Contains("virtualbox") ||
                    desc.Contains("vmware") || desc.Contains("virtual")) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    {
                        var s = ua.Address.ToString();
                        if (!s.StartsWith("169.254.", StringComparison.Ordinal))
                            return s;
                    }
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }
}
