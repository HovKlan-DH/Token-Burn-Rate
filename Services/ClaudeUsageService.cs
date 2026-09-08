using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TokenBurnRate.Models;

namespace TokenBurnRate.Services;

/// <summary>
/// Reads Claude Code's local JSONL transcripts and aggregates token usage.
///
/// Two properties of the transcript format drive this implementation:
///  1. A single assistant message is appended repeatedly as it streams (observed up to 4x),
///     so records MUST be de-duplicated by message id or totals roughly double.
///  2. The transcript set is large (~280MB) but nearly static: only ~0.1% of bytes change
///     per day. Files are therefore parsed incrementally from a byte offset and cached.
/// </summary>
public sealed class ClaudeUsageService
{
    private readonly string _projectsRoot;
    private readonly ConcurrentDictionary<string, FileCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed class FileCacheEntry
    {
        public long Offset;
        public long Length;
        public List<UsageRecord> Records = new();
    }

    public ClaudeUsageService(string? projectsRoot = null)
        => _projectsRoot = projectsRoot ?? DefaultProjectsRoot();

    /// <summary>~/.claude/projects on every platform; CLAUDE_CONFIG_DIR overrides it.</summary>
    public static string DefaultProjectsRoot()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
            return Path.Combine(configDir, "projects");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", "projects");
    }

    public bool DataDirectoryExists => Directory.Exists(_projectsRoot);
    public string ProjectsRoot => _projectsRoot;

    /// <summary>Parses any new transcript bytes and returns every known usage record.</summary>
    public async Task<IReadOnlyList<UsageRecord>> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_projectsRoot))
                return Array.Empty<UsageRecord>();

            var files = Directory.EnumerateFiles(_projectsRoot, "*.jsonl", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await RefreshFileAsync(file, ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Claude Code may be mid-write; keep whatever we already cached.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            // De-duplicate across the whole corpus: the same message id can appear in more
            // than one file when a session is resumed or forked.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var all = new List<UsageRecord>();
            foreach (var entry in _cache.Values)
            {
                foreach (var rec in entry.Records)
                {
                    if (rec.MessageId.Length > 0 && !seen.Add(rec.MessageId))
                        continue;
                    all.Add(rec);
                }
            }
            return all;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshFileAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return;

        var entry = _cache.GetOrAdd(path, _ => new FileCacheEntry());

        // Truncated or rotated file: start over.
        if (info.Length < entry.Length)
        {
            entry.Offset = 0;
            entry.Length = 0;
            entry.Records = new List<UsageRecord>();
        }

        if (info.Length == entry.Length) return; // unchanged

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        fs.Seek(entry.Offset, SeekOrigin.Begin);

        using var reader = new StreamReader(fs);
        long consumed = entry.Offset;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;

            // Only advance the offset past lines we know are complete. A trailing partial
            // line (no newline yet) is re-read on the next pass.
            consumed += System.Text.Encoding.UTF8.GetByteCount(line) + 1;

            if (line.Length == 0) continue;
            if (TryParseLine(line, out var rec))
                entry.Records.Add(rec);
        }

        entry.Offset = Math.Min(consumed, info.Length);
        entry.Length = info.Length;
    }

    private static bool TryParseLine(string line, out UsageRecord record)
    {
        record = default;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "assistant")
                return false;
            if (!root.TryGetProperty("message", out var msg)) return false;
            if (!msg.TryGetProperty("usage", out var usage)) return false;

            var model = msg.TryGetProperty("model", out var mo) ? mo.GetString() ?? "" : "";
            // Synthetic messages are locally generated and never billed.
            if (model is "" or "<synthetic>") return false;

            if (!root.TryGetProperty("timestamp", out var tsEl)) return false;
            if (!DateTimeOffset.TryParse(tsEl.GetString(), out var ts)) return false;

            record = new UsageRecord(
                MessageId(msg) ?? "",
                ts.ToUniversalTime(),
                model,
                Num(usage, "input_tokens"),
                Num(usage, "output_tokens"),
                Num(usage, "cache_creation_input_tokens"),
                Num(usage, "cache_read_input_tokens"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long Num(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    /// <summary>Message id used for de-duplication, read straight from the streamed message.</summary>
    private static string? MessageId(JsonElement msg)
        => msg.TryGetProperty("id", out var id) ? id.GetString() : null;
}
