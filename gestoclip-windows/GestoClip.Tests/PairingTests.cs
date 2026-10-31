using System.Net;
using GestoClip.Core.Discovery;
using GestoClip.Core.Pairing;
using GestoClip.Core.Protocol;
using GestoClip.Core.Transport;
using Xunit;

namespace GestoClip.Tests;

public class PairingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gestoclip-pair-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void QrInvite_UriRoundTrip_And_PngGeneration()
    {
        var inv = new QrPairInvite(1, "192.168.1.79", 8765, "abcdef0123456789", "482910", "pc1", "Mi Laptop", "WINDOWS", 1793470000000L);
        var uri = inv.ToUri();
        Assert.StartsWith("gestoclip://pair?", uri);

        var parsed = QrPairInvite.TryParse(uri);
        Assert.NotNull(parsed);
        Assert.Equal(inv.Ip, parsed!.Ip);
        Assert.Equal(inv.Port, parsed.Port);
        Assert.Equal(inv.Fp, parsed.Fp);
        Assert.Equal(inv.Token, parsed.Token);
        Assert.Equal(inv.DeviceId, parsed.DeviceId);
        Assert.Equal(inv.Name, parsed.Name);
        Assert.Equal(inv.Platform, parsed.Platform);

        var png = QrPairInvite.RenderPng(uri);
        Assert.True(png.Length > 200);
        // Firma PNG: 0x89 'P' 'N' 'G'
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);
    }

    [Fact]
    public void PairingService_OneTimeToken_ConsumedOnSuccess_AndRejectsReplay()
    {
        var store = new PairedStore(Path.Combine(_dir, "paired.json"));
        var svc = new PairingService(store, "pc1", "PC", "WINDOWS");
        var invite = svc.CreateInvite("127.0.0.1", 8765, "serverfp");

        var (ack1, peer1) = svc.TryAcceptPairRequest("clientfp12345678", new PairRequestMessage(invite.Token, "and1", "Pixel 8", "ANDROID"));
        Assert.True(ack1.Accepted);
        Assert.NotNull(peer1);
        Assert.True(store.IsPaired("clientfp12345678"));

        // Reuso del mismo token debe fallar porque es de un solo uso
        var (ack2, _) = svc.TryAcceptPairRequest("attackerfp123456", new PairRequestMessage(invite.Token, "att", "Evil", "ANDROID"));
        Assert.False(ack2.Accepted);
        Assert.False(store.IsPaired("attackerfp123456"));
    }

    [Fact]
    public void PairingService_ThreeFailedAttempts_InvalidatesInvite()
    {
        var store = new PairedStore(Path.Combine(_dir, "paired.json"));
        var svc = new PairingService(store, "pc1", "PC", "WINDOWS");
        var invite = svc.CreateInvite("127.0.0.1", 8765, "serverfp");

        for (var i = 0; i < 3; i++)
        {
            var (ack, _) = svc.TryAcceptPairRequest("fp1234567890", new PairRequestMessage("000000", "id", "n", "ANDROID"));
            Assert.False(ack.Accepted);
        }

        // Al 4to intento, incluso con el token real, ya fue invalidado
        var (ackValid, _) = svc.TryAcceptPairRequest("fp1234567890", new PairRequestMessage(invite.Token, "id", "n", "ANDROID"));
        Assert.False(ackValid.Accepted);
    }

    [Fact]
    public async Task EndToEnd_QrPairingOverTls_ThenBidirectionalTransfer()
    {
        var serverStore = new PairedStore(Path.Combine(_dir, "server-paired.json"));
        var clientStore = new PairedStore(Path.Combine(_dir, "client-paired.json"));

        var serverCert = DeviceCert.LoadOrCreate(Path.Combine(_dir, "server.pfx"));
        var clientCert = DeviceCert.LoadOrCreate(Path.Combine(_dir, "client.pfx"));

        var serverPairing = new PairingService(serverStore, "win-pc", "Laptop Windows", "WINDOWS");

        SecureChannel serverChannel = null!;
        SecureChannel clientChannel = null!;

        serverChannel = new SecureChannel(
            serverCert,
            () => new[] { new Peer("and-1", "Pixel 8", "ANDROID", IPAddress.Loopback, clientChannel.ListenPort, clientChannel.LocalFingerprint, DateTime.UtcNow) },
            serverStore.IsPaired,
            port: 0,
            pairing: serverPairing);
        serverChannel.StartListening();

        clientChannel = new SecureChannel(
            clientCert,
            () => new[] { new Peer("win-pc", "Laptop Windows", "WINDOWS", IPAddress.Loopback, serverChannel.ListenPort, serverChannel.LocalFingerprint, DateTime.UtcNow) },
            clientStore.IsPaired,
            port: 0);
        clientChannel.StartListening();

        using var _s = serverChannel;
        using var _c = clientChannel;

        // 1. El servidor genera invitación QR y la codifica en URI
        var inviteUri = serverPairing.CreateInvite("127.0.0.1", serverChannel.ListenPort, serverChannel.LocalFingerprint).ToUri();

        // 2. El cliente Android/PC escanea la URI del QR y ejecuta PairWithServerAsync
        var scannedInvite = QrPairInvite.TryParse(inviteUri)!;
        var ack = await clientChannel.PairWithServerAsync(scannedInvite, "and-1", "Pixel 8", "ANDROID", clientStore);

        Assert.True(ack.Accepted);
        Assert.True(serverStore.IsPaired(clientChannel.LocalFingerprint));
        Assert.True(clientStore.IsPaired(serverChannel.LocalFingerprint));

        // 3. Ahora ambos están emparejados: probamos transferencia Cliente -> Servidor
        var gotOnServer = new TaskCompletionSource<Payload>();
        serverChannel.Received += (p, _) => gotOnServer.TrySetResult(p);

        var sent1 = await clientChannel.SendToPeersAsync(new Payload(Kinds.Text, "clip.txt", "text/plain", "Hola desde Android"u8.ToArray()));
        Assert.Equal(1, sent1);
        var p1 = await gotOnServer.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Hola desde Android", System.Text.Encoding.UTF8.GetString(p1.Data));

        // 4. Y transferencia Servidor -> Cliente
        var gotOnClient = new TaskCompletionSource<Payload>();
        clientChannel.Received += (p, _) => gotOnClient.TrySetResult(p);

        var sent2 = await serverChannel.SendToPeersAsync(new Payload(Kinds.Text, "clip.txt", "text/plain", "Hola desde Windows"u8.ToArray()));
        Assert.Equal(1, sent2);
        var p2 = await gotOnClient.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Hola desde Windows", System.Text.Encoding.UTF8.GetString(p2.Data));
    }
}
