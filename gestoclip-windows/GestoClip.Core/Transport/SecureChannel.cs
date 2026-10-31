using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GestoClip.Core.Actions;
using GestoClip.Core.Discovery;
using GestoClip.Core.Pairing;
using GestoClip.Core.Protocol;

namespace GestoClip.Core.Transport;

public static class DeviceCert
{
    /// <summary>Certificado autofirmado persistente (PFX en disco, protegido por ACL del perfil de usuario).</summary>
    public static X509Certificate2 LoadOrCreate(string pfxPath)
    {
        const string pass = "gestoclip";
        if (File.Exists(pfxPath))
            return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, pass, X509KeyStorageFlags.Exportable);

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=GestoClip", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
        File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, pass));
        return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, pass, X509KeyStorageFlags.Exportable);
    }

    public static string Fingerprint(X509Certificate cert) =>
        Convert.ToHexString(SHA256.HashData(new X509Certificate2(cert).RawData)).ToLowerInvariant();
}

/// <summary>Servidor y cliente TLS. El par se acepta solo si su fingerprint está en la lista de emparejados (o envía un PAIR_REQUEST con token válido).</summary>
public sealed class SecureChannel : ISender, IDisposable
{
    public const int Port = 8765;
    private const long MaxUnpairedBytes = 4096;
    private const byte DeliveryAckByte = 0x06;

    private readonly X509Certificate2 _cert;
    private readonly Func<IEnumerable<Peer>> _peers;
    private readonly Func<string, bool> _isPaired;
    private readonly PairingService? _pairing;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public event Action<Payload, string>? Received; // payload, fingerprint remoto
    public event Action<string>? Rejected;          // fingerprint no emparejado

    public string LocalFingerprint => DeviceCert.Fingerprint(_cert);

    public SecureChannel(
        X509Certificate2 cert,
        Func<IEnumerable<Peer>> peers,
        Func<string, bool> isPaired,
        int port = Port,
        PairingService? pairing = null)
        => (_cert, _peers, _isPaired, ListenPort, _pairing) = (cert, peers, isPaired, port, pairing);

    public int ListenPort { get; private set; }

    public void StartListening()
    {
        _listener = new TcpListener(IPAddress.Any, ListenPort);
        _listener.Start();
        ListenPort = ((IPEndPoint)_listener.LocalEndpoint).Port; // soporta puerto 0 (aleatorio) en pruebas
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleAsync(client));
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                string remoteFp = "";
                using var ssl = new SslStream(client.GetStream(), false, (_, c, _, _) =>
                {
                    if (c is null) return false;
                    remoteFp = DeviceCert.Fingerprint(c);
                    return true; // la decisión real se toma abajo, con el fingerprint
                });
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _cert,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, _cts.Token);

                if (!_isPaired(remoteFp))
                {
                    if (_pairing is null || _pairing.ActiveInvite is null)
                    {
                        Rejected?.Invoke(remoteFp);
                        return;
                    }
                    // Si hay una invitación QR activa, permitimos únicamente un PAIR_REQUEST pequeño
                    Payload pairPayload;
                    try
                    {
                        pairPayload = await Framing.ReadAsync(ssl, _cts.Token, maxSize: MaxUnpairedBytes);
                    }
                    catch
                    {
                        Rejected?.Invoke(remoteFp);
                        return;
                    }

                    if (pairPayload.Kind != Kinds.PairRequest)
                    {
                        Rejected?.Invoke(remoteFp);
                        return;
                    }

                    var req = Json.Deserialize<PairRequestMessage>(Encoding.UTF8.GetString(pairPayload.Data));
                    if (req is null)
                    {
                        Rejected?.Invoke(remoteFp);
                        return;
                    }

                    var (ack, _) = _pairing.TryAcceptPairRequest(remoteFp, req);
                    var ackBytes = Encoding.UTF8.GetBytes(Json.Serialize(ack));
                    await Framing.WriteAsync(ssl, new Payload(Kinds.PairAck, "pair.json", "application/json", ackBytes), _cts.Token);
                    if (!ack.Accepted)
                        Rejected?.Invoke(remoteFp);
                    return;
                }

                var payload = await Framing.ReadAsync(ssl, _cts.Token);
                if (payload.Kind == Kinds.PairRequest && _pairing is not null)
                {
                    var req = Json.Deserialize<PairRequestMessage>(Encoding.UTF8.GetString(payload.Data));
                    if (req is not null)
                    {
                        var (ack, _) = _pairing.TryAcceptPairRequest(remoteFp, req);
                        var ackBytes = Encoding.UTF8.GetBytes(Json.Serialize(ack));
                        await Framing.WriteAsync(ssl, new Payload(Kinds.PairAck, "pair.json", "application/json", ackBytes), _cts.Token);
                    }
                    return;
                }

                Received?.Invoke(payload, remoteFp);
                await ssl.WriteAsync(new[] { DeliveryAckByte }, _cts.Token);
                await ssl.FlushAsync(_cts.Token);
            }
            catch { /* conexión inválida o cortada */ }
        }
    }

    /// <summary>Ejecuta el handshake de emparejamiento como cliente usando los datos del QR escaneado.</summary>
    public async Task<PairAckMessage> PairWithServerAsync(
        QrPairInvite invite,
        string clientDeviceId,
        string clientName,
        string clientPlatform = "WINDOWS",
        PairedStore? clientStore = null,
        CancellationToken ct = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(invite.Ip, invite.Port, ct);
        using var ssl = new SslStream(
            client.GetStream(),
            false,
            (_, c, _, _) => c is not null && string.Equals(DeviceCert.Fingerprint(c), invite.Fp, StringComparison.OrdinalIgnoreCase));

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "GestoClip",
            ClientCertificates = new X509CertificateCollection { _cert },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, ct);

        var req = new PairRequestMessage(invite.Token, clientDeviceId, clientName, clientPlatform);
        var reqBytes = Encoding.UTF8.GetBytes(Json.Serialize(req));
        await Framing.WriteAsync(ssl, new Payload(Kinds.PairRequest, "pair.json", "application/json", reqBytes), ct);

        var resp = await Framing.ReadAsync(ssl, ct, maxSize: MaxUnpairedBytes);
        if (resp.Kind != Kinds.PairAck)
            throw new InvalidDataException("Respuesta inesperada del servidor al emparejar");

        var ack = Json.Deserialize<PairAckMessage>(Encoding.UTF8.GetString(resp.Data))
                  ?? throw new InvalidDataException("Respuesta de emparejamiento inválida");

        if (ack.Accepted && clientStore is not null)
            clientStore.AddOrUpdate(invite.Fp, ack.ServerDeviceId, ack.ServerName, ack.ServerPlatform);

        return ack;
    }

    public async Task<int> SendToPeersAsync(Payload p, CancellationToken ct = default)
        => await SendToSelectedPeersAsync(p, null, ct);

    public async Task<int> SendToSelectedPeersAsync(
        Payload p,
        ISet<string>? targetFingerprints = null,
        CancellationToken ct = default)
    {
        if (p.Data.LongLength > Framing.MaxSize)
            throw new InvalidOperationException($"El archivo '{p.Name}' supera el límite permitido de 512 MiB.");

        var sent = 0;
        var candidates = _peers().Where(x =>
            _isPaired(x.PubKeyFp) &&
            (targetFingerprints is null || targetFingerprints.Count == 0 || targetFingerprints.Contains(x.PubKeyFp)));

        foreach (var peer in candidates)
        {
            try
            {
                await SendAsync(peer, p, ct);
                sent++;
            }
            catch
            {
                if (targetFingerprints is not null && targetFingerprints.Count == 1)
                    throw;
            }
        }
        return sent;
    }

    private async Task SendAsync(Peer peer, Payload p, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(peer.Address, peer.Port, ct);
        using var ssl = new SslStream(client.GetStream(), false,
            (_, c, _, _) => c is not null && string.Equals(DeviceCert.Fingerprint(c), peer.PubKeyFp, StringComparison.OrdinalIgnoreCase));
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "GestoClip",
            ClientCertificates = new X509CertificateCollection { _cert },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, ct);
        await Framing.WriteAsync(ssl, p, ct);
        var ack = new byte[1];
        var n = await ssl.ReadAsync(ack, ct);
        if (n != 1 || ack[0] != DeliveryAckByte)
            throw new IOException("El par no confirmó la recepción del paquete.");
    }

    public void Dispose() { _cts.Cancel(); _listener?.Stop(); }
}
