using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LanLink;

/// <summary>
/// Persists chat messages, known peers, and pending (unsent) messages to disk.
/// Stored in %LOCALAPPDATA%\LanLink\store.json.
/// </summary>
public sealed class MessageStore
{
    public List<SavedMessage>  Messages { get; set; } = new();
    public List<SavedPeer>     KnownPeers { get; set; } = new();
    public List<PendingMessage> PendingMessages { get; set; } = new();

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true
    };

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LanLink", "store.json");

    public static MessageStore Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var json = File.ReadAllText(StorePath);
                var store = JsonSerializer.Deserialize<MessageStore>(json, _jsonOpts);
                if (store is not null) return store;
            }
        }
        catch { /* corrupt or missing — start fresh */ }
        return new MessageStore();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(StorePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this, _jsonOpts));
        }
        catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ messages

    /// <summary>Keep at most this many messages in history.</summary>
    private const int MaxMessages = 500;

    public void AddMessage(SavedMessage msg)
    {
        Messages.Add(msg);
        while (Messages.Count > MaxMessages)
            Messages.RemoveAt(0);
    }

    // ------------------------------------------------------------------ pending

    public void AddPending(PendingMessage pm)
    {
        PendingMessages.Add(pm);
    }

    public void RemovePending(string id)
    {
        PendingMessages.RemoveAll(p => p.Id == id);
    }

    public List<PendingMessage> GetPendingFor(string nodeId)
    {
        return PendingMessages.FindAll(p =>
            p.TargetNodeId.Equals(nodeId, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ known peers

    public void UpsertPeer(SavedPeer peer)
    {
        var existing = KnownPeers.Find(p =>
            p.NodeId.Equals(peer.NodeId, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.Name = peer.Name;
            existing.Endpoint = peer.Endpoint;
        }
        else
        {
            KnownPeers.Add(peer);
        }
    }
}

// ---------------------------------------------------------------------------
//  Data models for persistence
// ---------------------------------------------------------------------------

public sealed class SavedMessage
{
    public DateTime Time       { get; set; }
    public string   PeerNodeId { get; set; } = "";
    public string   PeerName   { get; set; } = "";
    public string   Text       { get; set; } = "";
    public bool     IsSent     { get; set; }  // true = outgoing, false = incoming
}

public sealed class SavedPeer
{
    public string  NodeId   { get; set; } = "";
    public string  Name     { get; set; } = "";
    public string? Endpoint { get; set; }
}

public sealed class PendingMessage
{
    public string   Id           { get; set; } = "";
    public DateTime Time         { get; set; }
    public string   TargetNodeId { get; set; } = "";
    public string   TargetName   { get; set; } = "";
    public string   Text         { get; set; } = "";
}
