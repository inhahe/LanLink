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
    private readonly ILanLinkSettings _settings;

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

    public TransferManager(NetworkManager network, ILanLinkSettings settings)
    {
        _network  = network;
        _settings = settings;
        _network.MessageReceived += OnMessage;
        _network.PeerDisconnected += AbortIncomingFrom;
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
        if (!fi.Exists)
        {
            // Don't fail silently — the user picked a file and expects feedback.
            Log?.Invoke($"Can't send \u2014 file not found: {filePath}");
            return;
        }

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

    /// <param name="excludeName">
    /// Optional filter receiving each file/directory <i>name</i> (not path);
    /// return true to leave it out.  A predicate rather than a setting on
    /// <see cref="ILanLinkSettings"/> so platform-specific rules — Android's
    /// <c>.trashed-*</c> recycle bin, say — stay out of the shared contract.
    /// </param>
    public async Task SendDirectoryAsync(string targetId, string dirPath,
                                         Func<string, bool>? excludeName = null)
    {
        var di = new DirectoryInfo(dirPath);
        if (!di.Exists) return;

        string tid = NewTransferId();

        // Walking a big tree takes real time, and until it finishes there is
        // nothing at all on screen.  Claim the log line first so the user sees
        // the transfer exists; later updates reuse the same entry.
        ProgressUpdate?.Invoke(tid, $"Scanning {di.Name}…", false);

        // Walk the tree by hand rather than GetFiles(AllDirectories).  That
        // overload silently skips anything it cannot open (IgnoreInaccessible
        // defaults to true) and reports only files, so an unreadable folder
        // vanished without a word and an empty one could never be reproduced at
        // the far end.  Walking ourselves yields all three: files, the directory
        // structure, and the list of things we were refused.
        var (allFiles, relDirs, skipped) = ScanTree(di, excludeName);

        long totalSize = allFiles.Sum(f => f.Length);
        var tracker   = new ProgressTracker(tid, di.Name, totalSize)
        {
            FileCount = allFiles.Count
        };

        if (skipped.Count > 0)
        {
            Log?.Invoke($"Skipping {skipped.Count} unreadable item(s) in {di.Name}: "
                      + string.Join(", ", skipped.Take(5))
                      + (skipped.Count > 5 ? ", …" : ""));
        }

        string dirNote = relDirs.Count > 0 ? $", {relDirs.Count} folders" : "";
        ProgressUpdate?.Invoke(tid,
            $"Sending {di.Name}  0%  ({allFiles.Count} files{dirNote}, {FormatSize(totalSize)})",
            false);

        await _network.SendToAsync(targetId, new WireMessage
        {
            Type       = MessageTypes.DirStart,
            TransferId = tid,
            DirName    = di.Name,
            Dirs       = relDirs,
            From       = _network.NodeId
        }).ConfigureAwait(false);

        int index = 0;
        foreach (var fi in allFiles)
        {
            string rel = Path.GetRelativePath(dirPath, fi.FullName);

            tracker.FileIndex   = ++index;
            tracker.CurrentFile = rel;

            // Zero-byte files stream no chunks, so they would never trigger a
            // progress report; emit one here so a run of empty files can't look
            // like a stall.
            if (fi.Length == 0) EmitSendProgress(tracker);

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

    /// <summary>
    /// Depth-first walk returning every file, every sub-directory's relative
    /// path, and anything that could not be read.
    ///
    /// Deliberately not <c>GetFiles(SearchOption.AllDirectories)</c>: that
    /// swallows inaccessible directories (EnumerationOptions.IgnoreInaccessible
    /// is true by default) and never reports directories at all, which is how
    /// empty folders went missing with no error.
    /// </summary>
    private static (List<FileInfo> Files, List<string> Dirs, List<string> Skipped)
        ScanTree(DirectoryInfo root, Func<string, bool>? excludeName = null)
    {
        var files   = new List<FileInfo>();
        var dirs    = new List<string>();
        var skipped = new List<string>();

        void Walk(DirectoryInfo dir)
        {
            FileInfo[]      childFiles;
            DirectoryInfo[] childDirs;
            try
            {
                childFiles = dir.GetFiles();
                childDirs  = dir.GetDirectories();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skipped.Add(Path.GetRelativePath(root.FullName, dir.FullName));
                return;
            }

            foreach (var f in childFiles)
            {
                if (excludeName is not null && excludeName(f.Name)) continue;
                files.Add(f);
            }

            foreach (var child in childDirs)
            {
                if (excludeName is not null && excludeName(child.Name)) continue;

                // Record every directory, including empty ones — that is the
                // whole point of walking rather than globbing for files.
                dirs.Add(Path.GetRelativePath(root.FullName, child.FullName));
                Walk(child);
            }
        }

        Walk(root);
        return (files, dirs, skipped);
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
        EmitSendProgress(t);
    }

    private void EmitSendProgress(ProgressTracker t)
    {
        double pct = t.TotalBytes > 0 ? (double)t.BytesDone / t.TotalBytes * 100 : 0;

        // For a directory, the overall percentage alone looks identical to a
        // hung transfer for minutes at a time.  Naming the file currently on the
        // wire, and its position in the set, is what makes it obviously alive.
        string detail = t.FileCount > 1
            ? $"  —  file {t.FileIndex}/{t.FileCount}: {t.CurrentFile}"
            : "";

        ProgressUpdate?.Invoke(t.TransferId,
            $"Sending {t.Label}  {pct:F0}%  "
            + $"({FormatSize(t.BytesDone)} / {FormatSize(t.TotalBytes)}){detail}",
            false);
    }

    // ==================================================================
    //  Abandoned transfers
    // ==================================================================

    /// <summary>
    /// Release everything still in flight from a peer that just dropped.
    ///
    /// Each in-progress file is held open as a FileStream in
    /// <see cref="IncomingTransfer.OpenFiles"/> and closed on file_end/dir_end.
    /// If the sender vanishes those messages never arrive, so before this
    /// existed the handle stayed open for the life of the process: it leaked one
    /// descriptor per interrupted transfer, and — because a single open handle
    /// anywhere under a directory blocks renaming it — made the whole download
    /// folder impossible to move or delete, with "Access is denied" and no clue
    /// as to why.
    ///
    /// Part-written files are deleted rather than kept.  A truncated JPEG that
    /// looks like a complete photo is worse than no file: it is indistinguishable
    /// from a good one until you open it.  Files already finished in the same
    /// transfer are valid and are left alone.
    /// </summary>
    private void AbortIncomingFrom(string nodeId)
    {
        foreach (var kv in _incoming.ToArray())
        {
            if (kv.Value.FromNodeId != nodeId) continue;
            if (!_incoming.TryRemove(kv.Key, out var xfer)) continue;

            int discarded = 0;
            foreach (var (key, fs) in xfer.OpenFiles.ToArray())
            {
                try { fs.Close(); fs.Dispose(); } catch { }

                if (xfer.FinalPaths.TryGetValue(key, out var path))
                {
                    try
                    {
                        if (File.Exists(path)) { File.Delete(path); discarded++; }
                    }
                    catch { /* leave it rather than fail the cleanup */ }
                }
            }
            xfer.OpenFiles.Clear();

            string what = xfer.DirName is not null ? $"directory {xfer.DirName}" : "file transfer";
            Log?.Invoke($"{PeerName(nodeId)} disconnected during {what} — "
                      + $"discarded {discarded} part-written file(s); "
                      + "files already completed were kept.");

            ProgressUpdate?.Invoke(xfer.TransferId,
                $"Transfer from {PeerName(nodeId)} interrupted", true);
        }
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

        // Recreate the whole tree now, so folders containing no files still
        // arrive.  Previously a directory only ever came into existence as a side
        // effect of writing a file into it, so an empty one was silently dropped:
        // the sender reported success and the folder simply wasn't there.
        //
        // Guarded against path traversal — a peer could otherwise send "..\.."
        // and have us create directories outside the download folder.
        if (msg.Dirs is not null)
        {
            string root     = Path.Combine(_settings.DownloadFolder, msg.DirName);
            string rootFull = Path.GetFullPath(root);

            foreach (string rel in msg.Dirs)
            {
                try
                {
                    string full = Path.GetFullPath(Path.Combine(root, rel));
                    if (!full.StartsWith(rootFull, StringComparison.Ordinal)) continue;
                    Directory.CreateDirectory(full);
                }
                catch { /* one bad entry must not abort the transfer */ }
            }
        }

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

    /// <summary>Which file of a directory transfer is on the wire right now.</summary>
    public string CurrentFile = "";
    public int    FileIndex;
    public int    FileCount;

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
