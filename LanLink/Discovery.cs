using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LanLink;

/// <summary>
/// UDP broadcast discovery.  Every few seconds each node announces itself
/// on the LAN.  Listeners on the same port pick up the announcement and
/// fire <see cref="PeerDiscovered"/>.
///
/// Broadcasts are sent on the subnet-specific broadcast address of every
/// active IPv4 interface (not just 255.255.255.255) for reliability across
/// multi-NIC setups and networks that drop the limited broadcast.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private readonly string _nodeId;
    private readonly string _name;
    private readonly int    _port;      // UDP port (same as TCP port)
    private UdpClient?      _udp;
    private CancellationTokenSource? _cts;

    /// <summary>(nodeId, name, remoteEndpoint, tcpPort)</summary>
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

        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        _udp.EnableBroadcast = true;

        _ = BroadcastLoopAsync(_cts.Token);
        _ = ListenLoopAsync(_cts.Token);
    }

    // ---- broadcast our presence every 3 s ----

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
                // Send to the subnet broadcast address of every active IPv4
                // interface so discovery works across Ethernet, Wi-Fi, etc.
                foreach (var dest in GetBroadcastEndpoints())
                {
                    try { await _udp!.SendAsync(bytes, dest, ct).ConfigureAwait(false); }
                    catch (SocketException) { }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* transient — try again next tick */ }

            try  { await Task.Delay(3_000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Enumerate every active, non-loopback IPv4 interface and compute its
    /// directed broadcast address  (ip | ~mask).
    /// </summary>
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

        // Fallback: always include the limited broadcast too.
        result.Add(new IPEndPoint(IPAddress.Broadcast, _port));
        return result;
    }

    // ---- listen for announcements ----

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
                // Transient network error (WiFi blip, adapter toggle, sleep/wake).
                // Wait briefly and retry rather than permanently killing discovery.
                try { await Task.Delay(1_000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch { /* malformed packet — ignore */ }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp?.Close();
        _cts?.Dispose();
    }
}
