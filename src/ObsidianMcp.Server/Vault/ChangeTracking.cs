using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObsidianMcp.Server.Vault;

public sealed record PropertyChange(string Property, object? Before, object? After);

/// <summary>Outcome of a (possibly simulated) write.</summary>
public sealed record ChangeResult(
    string Path,
    string Operation,
    bool DryRun,
    bool Created,
    string? VersionBefore,
    string? VersionAfter,
    IReadOnlyList<PropertyChange> PropertyChanges,
    IReadOnlyList<string> BodyDiff,
    string? AuditId,
    IReadOnlyList<ValidationIssue>? Warnings = null,
    string? Message = null);

/// <summary>Options shared by every write.</summary>
public sealed record WriteOptions
{
    public bool DryRun { get; init; }
    /// <summary>Version (from a previous read) the caller expects; the write fails if the file changed since.</summary>
    public string? ExpectedVersion { get; init; }
    public string? Reason { get; init; }
    public string? Collection { get; init; }
    public string? RecordId { get; init; }
    public static readonly WriteOptions Default = new();
}

public sealed record ValidationIssue(string Severity, string Code, string Message, string? Path = null, string? RecordId = null, string? Property = null);

public sealed record AuditEntry
{
    public string Id { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public string Operation { get; init; } = "";
    public string Path { get; init; } = "";
    public string? FromPath { get; init; }
    public string? Collection { get; init; }
    public string? RecordId { get; init; }
    public string? VersionBefore { get; init; }
    public string? VersionAfter { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<PropertyChange>? PropertyChanges { get; init; }
    public bool BodyChanged { get; init; }
}

/// <summary>
/// Append-only JSON-lines audit log plus content snapshots (for history / rollback), stored in the
/// server's data folder inside the vault so it is synchronised and backed up with the vault.
/// </summary>
public sealed class AuditLog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _logFile;
    private readonly string _historyDir;
    private readonly Lock _lock = new();

    public AuditLog(string dataFolder)
    {
        _logFile = System.IO.Path.Combine(dataFolder, "audit.jsonl");
        _historyDir = System.IO.Path.Combine(dataFolder, "history");
    }

    public static string Version(string? content) => content is null
        ? "none"
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];

    public void SaveSnapshot(string content)
    {
        var file = System.IO.Path.Combine(_historyDir, Version(content) + ".md");
        if (File.Exists(file)) return;
        Directory.CreateDirectory(_historyDir);
        File.WriteAllText(file, content);
    }

    public string? LoadSnapshot(string version)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, "^[0-9a-f]{16}$")) return null;
        var file = System.IO.Path.Combine(_historyDir, version + ".md");
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    public AuditEntry Append(AuditEntry entry)
    {
        var e = entry with { Id = Guid.NewGuid().ToString("N")[..12], Timestamp = DateTimeOffset.Now };
        lock (_lock)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_logFile)!);
            File.AppendAllText(_logFile, JsonSerializer.Serialize(e, Json) + "\n");
        }
        return e;
    }

    public IReadOnlyList<AuditEntry> Read(Func<AuditEntry, bool>? filter, int limit)
    {
        if (!File.Exists(_logFile)) return [];
        var result = new List<AuditEntry>();
        foreach (var line in File.ReadLines(_logFile).Reverse())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            AuditEntry? e;
            try { e = JsonSerializer.Deserialize<AuditEntry>(line, Json); }
            catch (JsonException) { continue; }
            if (e is null || (filter is not null && !filter(e))) continue;
            result.Add(e);
            if (result.Count >= limit) break;
        }
        return result;
    }
}

/// <summary>Minimal line diff (LCS) used for change previews.</summary>
public static class TextDiff
{
    public static IReadOnlyList<string> Lines(string? before, string? after, int maxLines = 200)
    {
        var a = (before ?? "").Replace("\r\n", "\n").Split('\n');
        var b = (after ?? "").Replace("\r\n", "\n").Split('\n');
        if (a.Length * (long)b.Length > 4_000_000)
            return [$"(diff too large: {a.Length} -> {b.Length} lines)"];

        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var output = new List<string>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (output.Count >= maxLines) { output.Add("… (diff truncated)"); break; }
            if (x < a.Length && y < b.Length && a[x] == b[y]) { x++; y++; }
            else if (y < b.Length && (x == a.Length || lcs[x, y + 1] >= lcs[x + 1, y])) output.Add("+ " + b[y++]);
            else output.Add("- " + a[x++]);
        }
        return output;
    }
}
