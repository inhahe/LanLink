using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LanLink;

// ---------------------------------------------------------------------------
//  Wire message — flat structure with nullable fields for each message type.
//  Keeps serialization simple: one class covers every message kind.
// ---------------------------------------------------------------------------

public sealed class WireMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    // ---- hello ----
    [JsonPropertyName("nodeId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NodeId { get; set; }

    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("port"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Port { get; set; }

    // ---- text ----
    [JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("from"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; set; }

    // ---- file / dir transfer ----
    [JsonPropertyName("transferId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransferId { get; set; }

    [JsonPropertyName("fileName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileName { get; set; }

    [JsonPropertyName("fileSize"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FileSize { get; set; }

    [JsonPropertyName("relativePath"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RelativePath { get; set; }

    [JsonPropertyName("chunkIndex"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ChunkIndex { get; set; }

    [JsonPropertyName("dirName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DirName { get; set; }

    // ---- peer list ----
    [JsonPropertyName("peers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PeerInfo>? Peers { get; set; }

    // ---- relay ----
    [JsonPropertyName("to"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? To { get; set; }

    [JsonPropertyName("hops"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Hops { get; set; }

    [JsonPropertyName("inner"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WireMessage? Inner { get; set; }
}

// Subset of peer data shared in peer_list messages.
public sealed class PeerInfo
{
    [JsonPropertyName("nodeId")]  public string NodeId { get; set; } = "";
    [JsonPropertyName("name")]    public string Name   { get; set; } = "";
    [JsonPropertyName("hops")]    public int    Hops   { get; set; }
}

// String constants for message types.
public static class MessageTypes
{
    public const string Hello     = "hello";
    public const string PeerList  = "peer_list";
    public const string Text      = "text";
    public const string FileStart = "file_start";
    public const string FileChunk = "file_chunk";
    public const string FileEnd   = "file_end";
    public const string DirStart  = "dir_start";
    public const string DirEnd    = "dir_end";
    public const string Relay     = "relay";
    public const string Ping      = "ping";
    public const string Pong      = "pong";
}

// ---------------------------------------------------------------------------
//  Binary frame codec.
//
//  Frame layout (all lengths big-endian uint32):
//    [4 B  header length  H]
//    [4 B  payload length P]
//    [H B  UTF-8 JSON header]
//    [P B  raw binary payload — file chunk data, or empty]
// ---------------------------------------------------------------------------

public static class FrameCodec
{
    private const int MaxHeaderSize  = 1_048_576;   // 1 MB
    private const int MaxPayloadSize = 1_048_576;   // 1 MB

    public static async Task WriteFrameAsync(
        Stream stream, WireMessage message, byte[]? payload = null, CancellationToken ct = default)
    {
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(message);
        int payloadLen  = payload?.Length ?? 0;

        var prefix = new byte[8];
        WriteBE32(prefix, 0, headerBytes.Length);
        WriteBE32(prefix, 4, payloadLen);

        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        if (payload is { Length: > 0 })
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<(WireMessage message, byte[] payload)?> ReadFrameAsync(
        Stream stream, CancellationToken ct = default)
    {
        var prefix = new byte[8];
        if (!await ReadExactAsync(stream, prefix, ct).ConfigureAwait(false))
            return null;

        int headerLen  = ReadBE32(prefix, 0);
        int payloadLen = ReadBE32(prefix, 4);

        if (headerLen  <= 0 || headerLen  > MaxHeaderSize)  return null;
        if (payloadLen <  0 || payloadLen > MaxPayloadSize) return null;

        var headerBuf = new byte[headerLen];
        if (!await ReadExactAsync(stream, headerBuf, ct).ConfigureAwait(false))
            return null;

        byte[] payload = Array.Empty<byte>();
        if (payloadLen > 0)
        {
            payload = new byte[payloadLen];
            if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
                return null;
        }

        var msg = JsonSerializer.Deserialize<WireMessage>(headerBuf);
        return msg is null ? null : (msg, payload);
    }

    // ---- helpers ----

    private static void WriteBE32(byte[] buf, int off, int val)
    {
        buf[off]     = (byte)(val >> 24);
        buf[off + 1] = (byte)(val >> 16);
        buf[off + 2] = (byte)(val >> 8);
        buf[off + 3] = (byte) val;
    }

    private static int ReadBE32(byte[] buf, int off)
        => (buf[off] << 24) | (buf[off + 1] << 16) | (buf[off + 2] << 8) | buf[off + 3];

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct)
                                .ConfigureAwait(false);
            if (n == 0) return false;          // stream closed
            offset += n;
        }
        return true;
    }
}
