using System.Net;
using GestoClip.Core.Discovery;
using GestoClip.Core.Protocol;
using GestoClip.Core.Transport;
using Xunit;

namespace GestoClip.Tests;

public class SecureChannelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gestoclip-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (SecureChannel ch, string fp) Make(string name, Func<string, bool> paired, Func<IEnumerable<Peer>>? peers = null)
    {
        var cert = DeviceCert.LoadOrCreate(Path.Combine(_dir, name + ".pfx"));
        var ch = new SecureChannel(cert, peers ?? (() => Array.Empty<Peer>()), paired, port: 0);
        ch.StartListening();
        return (ch, DeviceCert.Fingerprint(cert));
    }

    private static Peer PeerOf(SecureChannel ch, string fp) =>
        new("id", "n", "WINDOWS", IPAddress.Loopback, ch.ListenPort, fp, DateTime.UtcNow);

    [Fact]
    public async Task PairedPeers_TransferPayload_OverTls()
    {
        string fpA = "", fpB = "";
        var receiver = Make("b", fp => fp == fpA);
        fpB = receiver.fp;
        var got = new TaskCompletionSource<Payload>();
        receiver.ch.Received += (p, _) => got.TrySetResult(p);

        var sender = Make("a", fp => fp == fpB, () => new[] { PeerOf(receiver.ch, fpB) });
        fpA = sender.fp;

        var data = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 253)).ToArray();
        var sent = await sender.ch.SendToPeersAsync(new Payload(Kinds.Image, "x.png", "image/png", data));

        Assert.Equal(1, sent);
        var p = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(data, p.Data);
        Assert.Equal("x.png", p.Name);
    }

    [Fact]
    public async Task UnpairedSender_IsRejected_AndNothingDelivered()
    {
        var receiver = Make("b2", _ => false); // no empareja a nadie
        var rejected = new TaskCompletionSource<string>();
        var received = false;
        receiver.ch.Rejected += fp => rejected.TrySetResult(fp);
        receiver.ch.Received += (_, _) => received = true;

        var sender = Make("a2", _ => true, () => new[] { PeerOf(receiver.ch, receiver.fp) });
        await sender.ch.SendToPeersAsync(new Payload(Kinds.Text, "t", "text/plain", "hola"u8.ToArray()));

        var fp = await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(sender.fp, fp);
        await Task.Delay(200);
        Assert.False(received);
    }

    [Fact]
    public async Task WrongPinnedFingerprint_SenderRefusesServer()
    {
        var receiver = Make("b3", _ => true);
        var sender = Make("a3", _ => true, () => new[] { PeerOf(receiver.ch, "deadbeef") }); // fingerprint falso
        var sent = await sender.ch.SendToPeersAsync(new Payload(Kinds.Text, "t", "text/plain", "x"u8.ToArray()));
        Assert.Equal(0, sent); // el emisor no confía en un servidor con otro certificado
    }
}
