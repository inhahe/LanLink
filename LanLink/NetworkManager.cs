using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LanLink;

/// <summary>
/// Central networking hub.  Manages:
///   - LAN UDP discovery
///   - TCP listener for incoming connections
///   - Outgoing TCP connections (LAN auto-connect + manual remote)
///   - Routing table &amp; multi-hop relay for bridging LANs
///   - Peer list exchange for bridge propagation
/// </summary>
public sealed class NetworkManager : IDisposable
{
    private readonly AppSettings      _settings;
    private readonly DiscoveryService _discovery;
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;

    // Active TCP connections keyed by remote node-id.
    private readonly ConcurrentDictionary<string, PeerConnection> _connections = new();

    // All known peers (LAN, direct, relayed).
    private readonly ConcurrentDictionary<string, Peer> _peers = new();

    // Routing table: target node-id  ->  next-hop node-id
    //   (for direct peers, next-hop == target)
    private readonly ConcurrentDictionary<string, string> _routes = new();

    // Peers we learned about from each connection (for cleanup on disconnect).
    private readonly ConcurrentDictionary<string, HashSet<string>> _learnedFrom = new();

    // ---- public surface ----

    public string NodeId   => _settings.NodeId;
    public string NodeName => _settings.DisplayName;
    public IReadOnlyDictionary<string, Peer> Peers => _peers;

    public event Action<Peer>?   PeerAdded;
    public event Action<Peer>?   PeerUpdated;
    public event Action<string>? PeerRemoved;   // node-id
    /// <summary>Application-level message received (text / file_start / …).</summary>
    public event Action<string, WireMessage, byte[]>? MessageReceived;
    public event Action<string>? Log;

    // ---- ctor ----

    public NetworkManager(AppSettings settings)
    {
        _settings  = settings;
        _discovery = new DiscoveryService(settings.NodeId, settings.DisplayName, settings.Port);
        _discovery.PeerDiscovered += OnLanPeerDiscovered;
    }

    // ==================================================================
    //  Start / stop
    // ==================================================================

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _settings.Port);
        _listener.Start();
        _ = AcceptLoopAsync();
        _discovery.Start();
        _ = StaleCleanupLoopAsync();
        Log?.Invoke($"Listening on port {_settings.Port}  (TCP + UDP discovery)");
    }

    // ==================================================================
    //  TCP accept loop
    // ==================================================================

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(_cts.Token)
                                             .ConfigureAwait(false);
                var conn = new PeerConnection(client, isOutgoing: false);
                WireUpConnection(conn);
                conn.StartReading();
                await SendHelloAsync(conn).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log?.Invoke($"Accept error: {ex.Message}"); }
        }
    }

    // ==================================================================
    //  Outgoing connection  (LAN auto-connect or manual remote)
    // ==================================================================

    public async Task<bool> ConnectToAsync(string host, int port)
    {
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(host, port).ConfigureAwait(false);
            var conn = new PeerConnection(client, isOutgoing: true);
            WireUpConnection(conn);
            conn.StartReading();
            await SendHelloAsync(conn).ConfigureAwait(false);
            Log?.Invoke($"Connected to {host}:{port}");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Connection to {host}:{port} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Wait 10 s, then connect if the other side hasn't connected to us yet.
    /// Handles asymmetric UDP discovery (e.g. virtual NICs eating broadcasts).
    /// </summary>
    private async Task FallbackConnectAsync(string nodeId, string host, int port)
    {
        try { await Task.Delay(10_000, _cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        if (!_connections.ContainsKey(nodeId))
            await ConnectToAsync(host, port).ConfigureAwait(false);
    }

    // ==================================================================
    //  LAN discovery handler
    // ==================================================================

    private void OnLanPeerDiscovered(string nodeId, string name, IPEndPoint ep, int tcpPort)
    {
        if (nodeId == _settings.NodeId) return;

        var created = new Peer { NodeId = nodeId };
        bool isNew  = _peers.TryAdd(nodeId, created);
        var  peer   = isNew ? created : _peers[nodeId];

        peer.Name      = name;
        peer.RouteType = RouteType.Lan;
        peer.Endpoint  = $"{ep.Address}:{tcpPort}";
        peer.Hops      = 0;
        peer.LastSeen  = DateTime.UtcNow;
        peer.NextHopId = null;
        _routes[nodeId] = nodeId;

        if (isNew)
        {
            PeerAdded?.Invoke(peer);
            Log?.Invoke($"Discovered {name} on LAN");
        }
        else
        {
            PeerUpdated?.Invoke(peer);
        }

        // Connect (or reconnect) if we don't have an active TCP session.
        // The lower-id node connects immediately; the higher-id node waits
        // 10 s then connects as a fallback (handles asymmetric discovery
        // caused by multiple virtual NICs eating broadcasts).
        if (!_connections.ContainsKey(nodeId))
        {
            if (string.Compare(_settings.NodeId, nodeId, StringComparison.Ordinal) < 0)
                _ = ConnectToAsync(ep.Address.ToString(), tcpPort);
            else
                _ = FallbackConnectAsync(nodeId, ep.Address.ToString(), tcpPort);
        }
    }

    // ==================================================================
    //  Connection wiring
    // ==================================================================

    private void WireUpConnection(PeerConnection conn)
    {
        conn.MessageReceived += OnConnectionMessage;
        conn.Disconnected    += OnConnectionDisconnected;
    }

    private async Task SendHelloAsync(PeerConnection conn)
    {
        try
        {
            await conn.SendAsync(new WireMessage
            {
                Type   = MessageTypes.Hello,
                NodeId = _settings.NodeId,
                Name   = _settings.DisplayName,
                Port   = _settings.Port
            }).ConfigureAwait(false);
        }
        catch { /* connection may have dropped already */ }
    }

    // ==================================================================
    //  Incoming message dispatch
    // ==================================================================

    private void OnConnectionMessage(PeerConnection conn, WireMessage msg, byte[] payload)
    {
        switch (msg.Type)
        {
            case MessageTypes.Hello:    HandleHello(conn, msg);              break;
            case MessageTypes.PeerList: HandlePeerList(conn, msg);           break;
            case MessageTypes.Relay:    HandleRelay(conn, msg, payload);     break;
            case MessageTypes.Ping:     _ = conn.SendAsync(new WireMessage { Type = MessageTypes.Pong }); break;
            default:
                // Application messages → forward to TransferManager / UI.
                MessageReceived?.Invoke(conn.RemoteNodeId ?? msg.From ?? "?", msg, payload);
                break;
        }
    }

    // ---- hello ----

    private void HandleHello(PeerConnection conn, WireMessage msg)
    {
        if (msg.NodeId is null || msg.Name is null) return;

        // Deduplicate: first connection wins.
        if (_connections.ContainsKey(msg.NodeId))
        {
            conn.Dispose();
            return;
        }

        conn.RemoteNodeId = msg.NodeId;
        conn.RemoteName   = msg.Name;
        _connections[msg.NodeId] = conn;

        var created = new Peer { NodeId = msg.NodeId };
        bool isNew  = _peers.TryAdd(msg.NodeId, created);
        var  peer   = isNew ? created : _peers[msg.NodeId];

        peer.Name        = msg.Name;
        peer.IsConnected = true;
        peer.Hops        = 0;
        peer.LastSeen    = DateTime.UtcNow;
        if (peer.RouteType != RouteType.Lan)
            peer.RouteType = RouteType.Direct;
        _routes[msg.NodeId] = msg.NodeId;

        if (isNew) PeerAdded?.Invoke(peer);
        else       PeerUpdated?.Invoke(peer);

        Log?.Invoke($"TCP connected to {msg.Name}  ({conn.RemoteEndpoint})");

        _ = SendPeerListAsync(conn);
    }

    // ---- peer list exchange (bridging) ----

    private async Task SendPeerListAsync(PeerConnection conn)
    {
        try
        {
            var list = _peers.Values
                .Where(p => p.NodeId != conn.RemoteNodeId)
                .Select(p => new PeerInfo { NodeId = p.NodeId, Name = p.Name, Hops = p.Hops })
                .ToList();

            await conn.SendAsync(new WireMessage
            {
                Type  = MessageTypes.PeerList,
                Peers = list
            }).ConfigureAwait(false);
        }
        catch { /* connection dropped */ }
    }

    private void HandlePeerList(PeerConnection conn, WireMessage msg)
    {
        if (msg.Peers is null || conn.RemoteNodeId is null) return;

        string fromId = conn.RemoteNodeId;
        var learned = new HashSet<string>();

        foreach (var info in msg.Peers)
        {
            if (info.NodeId == _settings.NodeId)      continue; // skip self
            if (_connections.ContainsKey(info.NodeId)) continue; // already direct

            int newHops = info.Hops + 1;
            if (newHops > 4) continue;                           // max relay depth

            var created = new Peer { NodeId = info.NodeId };
            bool isNew  = _peers.TryAdd(info.NodeId, created);
            var  peer   = isNew ? created : _peers[info.NodeId];

            if (isNew || peer.Hops > newHops)
            {
                peer.Name        = info.Name;
                peer.RouteType   = RouteType.Relayed;
                peer.Hops        = newHops;
                peer.NextHopId   = fromId;
                peer.IsConnected = true;
                peer.LastSeen    = DateTime.UtcNow;
                _routes[info.NodeId] = fromId;

                learned.Add(info.NodeId);

                if (isNew) PeerAdded?.Invoke(peer);
                else       PeerUpdated?.Invoke(peer);
            }
        }

        _learnedFrom[fromId] = learned;

        // Propagate updated peer list to all other connections (bridging).
        foreach (var (otherId, otherConn) in _connections)
        {
            if (otherId != fromId)
                _ = SendPeerListAsync(otherConn);
        }
    }

    // ---- relay ----

    private void HandleRelay(PeerConnection conn, WireMessage msg, byte[] payload)
    {
        if (msg.To is null || msg.Inner is null) return;

        if (msg.To == _settings.NodeId)
        {
            // Addressed to us — unwrap and process the inner message.
            string from = msg.From ?? conn.RemoteNodeId ?? "?";
            MessageReceived?.Invoke(from, msg.Inner, payload);
        }
        else
        {
            _ = ForwardRelayAsync(msg, payload);
        }
    }

    private async Task ForwardRelayAsync(WireMessage relay, byte[] payload)
    {
        if (relay.To is null) return;

        if (_routes.TryGetValue(relay.To, out var nextHop) &&
            _connections.TryGetValue(nextHop, out var conn))
        {
            relay.Hops ??= new List<string>();
            if (relay.Hops.Contains(_settings.NodeId))
            {
                Log?.Invoke($"Relay loop detected for {relay.To} — dropping");
                return;
            }
            relay.Hops.Add(_settings.NodeId);

            try { await conn.SendAsync(relay, payload).ConfigureAwait(false); }
            catch { Log?.Invoke($"Relay forward to {relay.To} failed"); }
        }
        else
        {
            Log?.Invoke($"No route to {relay.To} for relay");
        }
    }

    // ==================================================================
    //  Send (public API used by TransferManager)
    // ==================================================================

    public async Task SendToAsync(string targetNodeId, WireMessage msg, byte[]? payload = null)
    {
        if (!_routes.TryGetValue(targetNodeId, out var nextHop))
        {
            Log?.Invoke($"No route to peer {targetNodeId}");
            return;
        }
        if (!_connections.TryGetValue(nextHop, out var conn))
        {
            Log?.Invoke($"No active connection to next-hop {nextHop}");
            return;
        }

        if (nextHop == targetNodeId)
        {
            // Direct — send as-is.
            await conn.SendAsync(msg, payload).ConfigureAwait(false);
        }
        else
        {
            // Wrap in a relay envelope.
            var relay = new WireMessage
            {
                Type  = MessageTypes.Relay,
                To    = targetNodeId,
                From  = _settings.NodeId,
                Hops  = new List<string> { _settings.NodeId },
                Inner = msg
            };
            await conn.SendAsync(relay, payload).ConfigureAwait(false);
        }
    }

    // ==================================================================
    //  Disconnection handling
    // ==================================================================

    private void OnConnectionDisconnected(PeerConnection conn)
    {
        if (conn.RemoteNodeId is null) return;

        _connections.TryRemove(conn.RemoteNodeId, out _);

        // Mark peer as not connected.
        if (_peers.TryGetValue(conn.RemoteNodeId, out var peer))
        {
            peer.IsConnected = false;
            PeerUpdated?.Invoke(peer);
        }

        // Remove relayed peers that were learned through this connection.
        if (_learnedFrom.TryRemove(conn.RemoteNodeId, out var learned))
        {
            foreach (string id in learned)
            {
                if (_routes.TryGetValue(id, out var nh) && nh == conn.RemoteNodeId)
                {
                    _routes.TryRemove(id, out _);
                    if (_peers.TryRemove(id, out _))
                        PeerRemoved?.Invoke(id);
                }
            }
        }

        Log?.Invoke($"Disconnected from {conn.RemoteName ?? conn.RemoteNodeId}");

        try { conn.Dispose(); } catch { }

        // Propagate updated peer list to remaining connections.
        foreach (var (_, other) in _connections)
            _ = SendPeerListAsync(other);
    }

    // ==================================================================
    //  Stale peer cleanup (runs every 30 s)
    // ==================================================================

    private async Task StaleCleanupLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(30_000, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var now   = DateTime.UtcNow;
            var stale = _peers.Values
                .Where(p => !p.IsConnected && (now - p.LastSeen).TotalSeconds > 15)
                .ToList();

            foreach (var p in stale)
            {
                _routes.TryRemove(p.NodeId, out _);
                if (_peers.TryRemove(p.NodeId, out _))
                    PeerRemoved?.Invoke(p.NodeId);
            }
        }
    }

    // ==================================================================
    //  Dispose
    // ==================================================================

    public void Dispose()
    {
        _cts.Cancel();
        _discovery.Dispose();
        try { _listener?.Stop(); } catch { }
        foreach (var c in _connections.Values)
        { try { c.Dispose(); } catch { } }
        _connections.Clear();
    }
}
