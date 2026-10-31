using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using GestoClip.Core.Protocol;

namespace GestoClip.Core.Discovery;

public record Peer(string DeviceId, string Name, string Platform, IPAddress Address, int Port, string PubKeyFp, DateTime LastSeen);

/// <summary>Baliza UDP broadcast cada 2 s (puerto 8766), mismo esquema probado en habitos_kmp.</summary>
public sealed class UdpDiscovery : IDisposable
{
    public const int BroadcastPort = 8766;
    private readonly Beacon _local;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Peer> _peers = new();
    private readonly object _lock = new();

    public event Action<Peer>? PeerSeen;

    public UdpDiscovery(Beacon local) => _local = local;

    public IReadOnlyList<Peer> Peers
    {
        get { lock (_lock) return _peers.Values.Where(p => DateTime.UtcNow - p.LastSeen < TimeSpan.FromSeconds(10)).ToList(); }
    }

    public void Start()
    {
        _ = Task.Run(() => ListenAsync(_cts.Token));
        _ = Task.Run(() => BroadcastAsync(_cts.Token));
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, BroadcastPort));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct);
                var b = Json.Deserialize<Beacon>(Encoding.UTF8.GetString(r.Buffer));
                if (b is null || b.DeviceId == _local.DeviceId) continue;
                var peer = new Peer(b.DeviceId, b.Name, b.Platform, r.RemoteEndPoint.Address, b.Port, b.PubKeyFp, DateTime.UtcNow);
                lock (_lock) _peers[b.DeviceId] = peer;
                PeerSeen?.Invoke(peer);
            }
            catch (OperationCanceledException) { break; }
            catch { /* paquete inválido: se ignora */ }
        }
    }

    private async Task BroadcastAsync(CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var data = Encoding.UTF8.GetBytes(Json.Serialize(_local with { SentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
                foreach (var target in BroadcastTargets())
                    await udp.SendAsync(data, new IPEndPoint(target, BroadcastPort), ct);
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(2000, ct); }
        }
    }

    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        yield return IPAddress.Broadcast;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var b = new byte[4];
                for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(b);
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
