using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

#if ANDROID
using Android.Content;
using Android.Net.Wifi;
#endif

namespace LanLink;

/// <summary>
/// UDP broadcast discovery (mobile).
/// On Android, acquires a WifiManager.MulticastLock so the device actually
/// delivers broadcast packets to user-space (most Android devices filter
/// multicast/broadcast by default to save battery).
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private readonly string _nodeId;
    private readonly string _name;
    private readonly int    _port;
    private UdpClient?      _udp;
    private CancellationTokenSource? _cts;

#if ANDROID
    private WifiManager.MulticastLock? _multicastLock;
#endif

    public event Action<string, string, IPEndPoint, int>? PeerDiscovered;

    public DiscoveryService(string nodeId, string name, int port)
    {
        _nodeId = nodeId;
        _name   = name;
        _port   = port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

#if ANDROID
        // Android drops broadcast/multicast packets unless a MulticastLock is held.
        var wifiManager = (WifiManager?)Android.App.Application.Context
            .GetSystemService(Context.WifiService);
        if (wifiManager is not null)
        {
            _multicastLock = wifiManager.CreateMulticastLock("LanLink.Discovery");
            _multicastLock.Acquire();
        }
#endif

        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        _udp.EnableBroadcast = true;

        _ = BroadcastLoopAsync(_cts.Token);
        _ = ListenLoopAsync(_cts.Token);
    }

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        var msg = new WireMessage
        {
            Type   = "announce",
            NodeId = _nodeId,
            Name   = _name,
            Port   = _port
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(msg);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var dest in GetBroadcastEndpoints())
                {
                    try { await _udp!.SendAsync(bytes, dest, ct).ConfigureAwait(false); }
                    catch (SocketException) { }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }

            try  { await Task.Delay(3_000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private List<IPEndPoint> GetBroadcastEndpoints()
    {
        var result = new List<IPEndPoint>();
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var uni in iface.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    byte[] ip   = uni.Address.GetAddressBytes();
                    byte[] mask = uni.IPv4Mask.GetAddressBytes();
                    byte[] bcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                        bcast[i] = (byte)(ip[i] | ~mask[i]);

                    result.Add(new IPEndPoint(new IPAddress(bcast), _port));
                }
            }
        }
        catch { }

        result.Add(new IPEndPoint(IPAddress.Broadcast, _port));
        return result;
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
                var msg = JsonSerializer.Deserialize<WireMessage>(result.Buffer);
                if (msg is { Type: "announce", NodeId: not null, Name: not null, Port: not null }
                    && msg.NodeId != _nodeId)
                {
                    var ep = new IPEndPoint(result.RemoteEndPoint.Address, msg.Port.Value);
                    PeerDiscovered?.Invoke(msg.NodeId, msg.Name, ep, msg.Port.Value);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException)
            {
                // Transient network error (WiFi blip, sleep/wake).
                // Wait briefly and retry rather than permanently killing discovery.
                try { await Task.Delay(1_000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch { }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp?.Close();
        _cts?.Dispose();

#if ANDROID
        if (_multicastLock is { IsHeld: true })
        {
            _multicastLock.Release();
            _multicastLock = null;
        }
#endif
    }
}
