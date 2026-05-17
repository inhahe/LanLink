using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LanLink;

/// <summary>
/// Handles the application-level logic of sending / receiving files,
/// directories, and text messages.  Uses <see cref="NetworkManager"/>
/// for routing and delivery.
/// </summary>
public sealed class TransferManager
{
    private readonly NetworkManager _network;
    private readonly AppSettings    _settings;

    private const int ChunkSize = 262_144;   // 256 KB

    // Active incoming transfers keyed by transfer-id.
    private readonly ConcurrentDictionary<string, IncomingTransfer> _incoming = new();

    public event Action<string>?         Log;
    public event Action<string, string>? TextReceived;   // (fromNodeId, text)
    public event Action<string, string>? FileReceived;   // (fromNodeId, savedPath)

    /// <summary>
    /// Live progress for a transfer.
    /// (transferId, displayText, isDone)
    /// The UI should create or update a log entry keyed by transferId.
    /// </summary>
    public event Action<string, string, bool>? ProgressUpdate;

    public TransferManager(NetworkManager network, AppSettings settings)
    {
        _network  = network;
        _settings = settings;
        _network.MessageReceived += OnMessage;
    }

    // ==================================================================
    //  Incoming message dispatch
    // ==================================================================

    private void OnMessage(string fromId, WireMessage msg, byte[] payload)
    {
        try
        {
            switch (msg.Type)
            {
                case MessageTypes.Text:      HandleText(fromId, msg);                break;
                case MessageTypes.FileStart: HandleFileStart(fromId, msg);           break;
                case MessageTypes.FileChunk: HandleFileChunk(fromId, msg, payload);  break;
                case MessageTypes.FileEnd:   HandleFileEnd(fromId, msg);             break;
                case MessageTypes.DirStart:  HandleDirStart(fromId, msg);            break;
                case MessageTypes.DirEnd:    HandleDirEnd(fromId, msg);              break;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Transfer error: {ex.Message}");
        }
    }

    // ==================================================================
    //  Send — single file
    // ==================================================================

    public async Task SendTextAsync(string targetId, string text)
    {
        await _network.SendToAsync(targetId, new WireMessage
        {
            Type = MessageTypes.Text,
            Text = text,
            From = _network.NodeId
        }).ConfigureAwait(false);
    }

    public async Task SendFileAsync(string targetId, string filePath)
    {
        var fi = new FileInfo(filePath);
        if (!fi.Exists) return;

        string tid = NewTransferId();
        var tracker = new ProgressTracker(tid, fi.Name, fi.Length);

        ProgressUpdate?.Invoke(tid, $"Sending {fi.Name}  0%", false);

        await _network.SendToAsync(targetId, new WireMessage
        {
            Type       = MessageTypes.FileStart,
            TransferId = tid,
            FileName   = fi.Name,
            FileSize   = fi.Length,
            From       = _network.NodeId
        }).ConfigureAwait(false);

        await SendChunksAsync(targetId, tid, fi.FullName, null, tracker)
              .ConfigureAwait(false);

        await _network.SendToAsync(targetId, new WireMessage
        {
            Type       = MessageTypes.FileEnd,
            TransferId = tid
        }).ConfigureAwait(false);

        ProgressUpdate?.Invoke(tid,
            $"Sent {fi.Name}  ({FormatSize(fi.Length)})", true);
    }

    // ==================================================================
    //  Send — directory
    // ==================================================================

    public async Task SendDirectoryAsync(string targetId, string dirPath)
    {
        var di = new DirectoryInfo(dirPath);
        if (!di.Exists) return;

        string tid = NewTransferId();

        var allFiles  = di.GetFiles("*", SearchOption.AllDirectories);
        long totalSize = allFiles.Sum(f => f.Length);
        var tracker   = new ProgressTracker(tid, di.Name, totalSize);

        ProgressUpdate?.Invoke(tid,
            $"Sending {di.Name}  0%  ({allFiles.Length} files, {FormatSize(totalSize)})",
            false);

        await _network.SendToAsync(targetId, new WireMessage
        {
            Type       = MessageTypes.DirStart,
            TransferId = tid,
            DirName    = di.Name,
            From       = _network.NodeId
        }).ConfigureAwait(false);

        foreach (var fi in allFiles)
        {
            string rel = Path.GetRelativePath(dirPath, fi.FullName);

            await _network.SendToAsync(targetId, new WireMessage
            {
                Type         = MessageTypes.FileStart,
                TransferId   = tid,
                FileName     = fi.Name,
                FileSize     = fi.Length,
                RelativePath = rel,
                From         = _network.NodeId
            }).ConfigureAwait(false);

            await SendChunksAsync(targetId, tid, fi.FullName, rel, tracker)
                  .ConfigureAwait(false);

            await _network.SendToAsync(targetId, new WireMessage
            {
                Type         = MessageTypes.FileEnd,
                TransferId   = tid,
                RelativePath = rel
            }).ConfigureAwait(false);
        }

        await _network.SendToAsync(targetId, new WireMessage
        {
            Type       = MessageTypes.DirEnd,
            TransferId = tid
        }).ConfigureAwait(false);

        ProgressUpdate?.Invoke(tid,
            $"Sent directory {di.Name}  ({FormatSize(totalSize)})", true);
    }

    // ==================================================================
    //  Chunk streaming with progress
    // ==================================================================

    private async Task SendChunksAsync(
        string targetId, string tid, string path, string? rel,
        ProgressTracker tracker)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[ChunkSize];
        int idx = 0;

        while (true)
        {
            int read = await fs.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) break;

            byte[] payload = (read == buffer.Length) ? buffer : buffer[..read];

            await _network.SendToAsync(targetId, new WireMessage
            {
                Type         = MessageTypes.FileChunk,
                TransferId   = tid,
                ChunkIndex   = idx++,
                RelativePath = rel
            }, payload).ConfigureAwait(false);

            tracker.BytesDone += read;
            ReportSendProgress(tracker);
        }
    }

    private void ReportSendProgress(ProgressTracker t)
    {
        var now = DateTime.UtcNow;
        if ((now - t.LastReport).TotalMilliseconds < 500) return;
        t.LastReport = now;

        double pct = t.TotalBytes > 0 ? (double)t.BytesDone / t.TotalBytes * 100 : 0;
        ProgressUpdate?.Invoke(t.TransferId,
            $"Sending {t.Label}  {pct:F0}%  ({FormatSize(t.BytesDone)} / {FormatSize(t.TotalBytes)})",
            false);
    }

    // ==================================================================
    //  Receive
    // ==================================================================

    private void HandleText(string fromId, WireMessage msg)
        => TextReceived?.Invoke(fromId, msg.Text ?? "");

    private void HandleFileStart(string fromId, WireMessage msg)
    {
        if (msg.TransferId is null || msg.FileName is null) return;

        Directory.CreateDirectory(_settings.DownloadFolder);

        var xfer = _incoming.GetOrAdd(msg.TransferId, _ => new IncomingTransfer
        {
            TransferId = msg.TransferId,
            FromNodeId = fromId
        });

        string key  = msg.RelativePath ?? msg.FileName;
        string save = (xfer.DirName is not null)
            ? Path.Combine(_settings.DownloadFolder, xfer.DirName, key)
            : Path.Combine(_settings.DownloadFolder, msg.FileName);

        Directory.CreateDirectory(Path.GetDirectoryName(save)!);
        save = UniquePath(save);

        xfer.FinalPaths[key] = save;
        xfer.OpenFiles[key]  = new FileStream(save, FileMode.Create, FileAccess.Write, FileShare.None);

        // Set up receive progress tracking for this file.
        xfer.CurrentFileName     = msg.FileName;
        xfer.CurrentFileSize     = msg.FileSize ?? 0;
        xfer.CurrentFileReceived = 0;

        string peer  = PeerName(fromId);
        string label = xfer.DirName is not null ? $"{xfer.DirName}/{msg.FileName}" : msg.FileName;
        ProgressUpdate?.Invoke(msg.TransferId,
            $"Receiving {label} from {peer}  0%", false);
    }

    private void HandleFileChunk(string fromId, WireMessage msg, byte[] payload)
    {
        if (msg.TransferId is null) return;
        if (!_incoming.TryGetValue(msg.TransferId, out var xfer)) return;

        string key = msg.RelativePath
                     ?? xfer.OpenFiles.Keys.FirstOrDefault()
                     ?? "";
        if (!xfer.OpenFiles.TryGetValue(key, out var fs)) return;

        fs.Write(payload, 0, payload.Length);
        xfer.TotalReceived       += payload.Length;
        xfer.CurrentFileReceived += payload.Length;

        // Rate-limited receive progress.
        var now = DateTime.UtcNow;
        if ((now - xfer.LastProgressReport).TotalMilliseconds >= 500)
        {
            xfer.LastProgressReport = now;
            string peer  = PeerName(fromId);
            string label = xfer.DirName is not null
                ? $"{xfer.DirName}/{xfer.CurrentFileName}"
                : xfer.CurrentFileName;
            double pct = xfer.CurrentFileSize > 0
                ? (double)xfer.CurrentFileReceived / xfer.CurrentFileSize * 100 : 0;
            ProgressUpdate?.Invoke(msg.TransferId,
                $"Receiving {label} from {peer}  {pct:F0}%  " +
                $"({FormatSize(xfer.CurrentFileReceived)} / {FormatSize(xfer.CurrentFileSize)})",
                false);
        }
    }

    private void HandleFileEnd(string fromId, WireMessage msg)
    {
        if (msg.TransferId is null) return;
        if (!_incoming.TryGetValue(msg.TransferId, out var xfer)) return;

        string key = msg.RelativePath
                     ?? xfer.OpenFiles.Keys.FirstOrDefault()
                     ?? "";

        if (xfer.OpenFiles.Remove(key, out var fs))
        {
            fs.Close();
            fs.Dispose();
        }

        if (xfer.FinalPaths.TryGetValue(key, out var path))
        {
            FileReceived?.Invoke(fromId, path);

            // For single-file transfers, mark the progress line done.
            // For directory transfers the DirEnd handler does that.
            if (xfer.DirName is null)
            {
                ProgressUpdate?.Invoke(msg.TransferId,
                    $"Saved {Path.GetFileName(path)} from {PeerName(fromId)}  \u2192  {path}",
                    true);
            }
        }

        // Single-file transfer? Clean up.
        if (xfer.DirName is null)
            _incoming.TryRemove(msg.TransferId, out _);
    }

    private void HandleDirStart(string fromId, WireMessage msg)
    {
        if (msg.TransferId is null || msg.DirName is null) return;

        _incoming[msg.TransferId] = new IncomingTransfer
        {
            TransferId = msg.TransferId,
            FromNodeId = fromId,
            DirName    = msg.DirName
        };

        ProgressUpdate?.Invoke(msg.TransferId,
            $"Receiving directory {msg.DirName} from {PeerName(fromId)}\u2026", false);
    }

    private void HandleDirEnd(string fromId, WireMessage msg)
    {
        if (msg.TransferId is null) return;
        if (!_incoming.TryRemove(msg.TransferId, out var xfer)) return;

        foreach (var fs in xfer.OpenFiles.Values)
        { try { fs.Close(); fs.Dispose(); } catch { } }

        ProgressUpdate?.Invoke(msg.TransferId,
            $"Received directory {xfer.DirName} from {PeerName(fromId)}  " +
            $"({FormatSize(xfer.TotalReceived)})", true);
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    private string PeerName(string nodeId)
        => _network.Peers.TryGetValue(nodeId, out var p) ? p.Name : nodeId[..Math.Min(8, nodeId.Length)];

    private static string NewTransferId()
        => Guid.NewGuid().ToString("N")[..8];

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir  = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext  = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)                return $"{bytes} B";
        if (bytes < 1024 * 1024)         return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

// ---------------------------------------------------------------------------
//  Progress tracker for a single send operation (file or directory).
// ---------------------------------------------------------------------------

internal sealed class ProgressTracker
{
    public readonly string TransferId;
    public readonly string Label;
    public readonly long   TotalBytes;
    public long     BytesDone;
    public DateTime LastReport;

    public ProgressTracker(string tid, string label, long totalBytes)
    {
        TransferId = tid;
        Label      = label;
        TotalBytes = totalBytes;
    }
}

// ---------------------------------------------------------------------------
//  State for an in-progress incoming transfer.
// ---------------------------------------------------------------------------

public sealed class IncomingTransfer
{
    public string  TransferId { get; init; } = "";
    public string  FromNodeId { get; init; } = "";
    public string? DirName    { get; set; }

    public Dictionary<string, FileStream> OpenFiles  { get; } = new();
    public Dictionary<string, string>     FinalPaths { get; } = new();
    public long TotalReceived { get; set; }

    // Per-file receive progress.
    public string   CurrentFileName     { get; set; } = "";
    public long     CurrentFileSize     { get; set; }
    public long     CurrentFileReceived { get; set; }
    public DateTime LastProgressReport  { get; set; }
}
