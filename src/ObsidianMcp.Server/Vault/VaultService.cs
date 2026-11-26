using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ObsidianMcp.Server.Vault;

/// <summary>A note held in the in-memory index.</summary>
public sealed record Note(string Path, DateTime Modified, long Size, string Content, ParsedNote Parsed)
{
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Version => AuditLog.Version(Content);
    public Dictionary<string, object?> Properties => Parsed.Frontmatter;
}

public sealed record FolderEntry(string Path, string Type, long? Size, DateTime? Modified);
public sealed record SearchHit(string Path, string Title, double Score, string Snippet, IReadOnlyList<string> Tags, DateTime Modified);
public sealed record LinkInfo(string Target, string? Subpath, string? ResolvedPath, bool IsEmbed);
public sealed record Backlink(string Path, int Count, IReadOnlyList<string> Context);

/// <summary>
/// All file-system access to the vault goes through this class. It guarantees that every path stays
/// inside the vault root (no "..", absolute paths, dot-folders or symlink escapes), and that every
/// modification is atomic, version-checked, previewable and audited.
/// </summary>
public sealed class VaultService
{
    private readonly VaultOptions _options;
    private readonly string _root;       // full path with trailing separator
    private readonly string _realRoot;   // root with symlinks resolved, trailing separator
    private readonly ConcurrentDictionary<string, Note> _index;
    private readonly Lock _refreshLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    internal static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    internal static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public VaultService(IOptions<VaultOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.Path))
            throw new InvalidOperationException("No vault path configured. Pass --vault <path> or set OBSIDIAN_VAULT_PATH.");

        var full = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(ExpandHome(_options.Path)));
        if (!Directory.Exists(full))
            throw new InvalidOperationException($"Vault folder does not exist: {full}");

        _root = EnsureTrailingSeparator(full);
        var rootInfo = new DirectoryInfo(full);
        var real = rootInfo.LinkTarget is not null ? rootInfo.ResolveLinkTarget(true)?.FullName ?? full : full;
        _realRoot = EnsureTrailingSeparator(real);
        _index = new ConcurrentDictionary<string, Note>(PathComparer);
        VaultName = _options.VaultName ?? rootInfo.Name;
        DataFolder = System.IO.Path.Combine(_root, _options.DataFolder);
        Audit = new AuditLog(DataFolder);
    }

    public string RootPath => _root;
    public string VaultName { get; }
    public string DataFolder { get; }
    public AuditLog Audit { get; }
    public bool IsReadOnly => _options.ReadOnly;
    public int MaxResults => _options.MaxResults;
    public string ArchiveFolder => _options.ArchiveFolder;

    // ------------------------------------------------------------------ paths

    /// <summary>Normalizes a user-supplied vault-relative path to "folder/sub/file.ext" form.</summary>
    public string Normalize(string? path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        p = p.Trim('/');
        if (p.Length > 0 && (System.IO.Path.IsPathRooted(p) || p.Contains(':')))
            throw new VaultException($"Path must be relative to the vault root: '{path}'");
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or ".."))
            throw new VaultException($"Path may not contain '.' or '..' segments: '{path}'");
        if (segments.Any(s => s.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0))
            throw new VaultException($"Path contains invalid characters: '{path}'");
        return string.Join('/', segments);
    }

    /// <summary>Normalizes a note path, adding ".md" when no extension is given.</summary>
    public string NormalizeNotePath(string? path)
    {
        var p = Normalize(path);
        if (p.Length == 0) throw new VaultException("A note path is required.");
        if (!IsNoteExtension(System.IO.Path.GetExtension(p))) p += ".md";
        return p;
    }

    public static bool IsNoteExtension(string ext) => ext.Equals(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps a normalized vault-relative path to a full path, enforcing all safety checks.</summary>
    public string ToFullPath(string relative)
    {
        if (IsExcluded(relative))
            throw new VaultException($"Access to '{relative}' is not allowed.");

        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, relative));
        if (!IsUnderRoot(full) && !EnsureTrailingSeparator(full).Equals(_root, PathComparison))
            throw new VaultException($"Path escapes the vault: '{relative}'");

        // Refuse anything that is (or lives under) a symlink pointing outside the vault.
        var current = full;
        while (IsUnderRoot(current))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(true)?.FullName;
                if (target is null || !(IsUnderRoot(target) || target.StartsWith(_realRoot, PathComparison)))
                    throw new VaultException($"Path resolves outside the vault: '{relative}'");
            }
            current = System.IO.Path.GetDirectoryName(current) ?? "";
        }
        return full;
    }

    public string ToRelative(string full) =>
        System.IO.Path.GetRelativePath(_root, full).Replace('\\', '/');

    private bool IsUnderRoot(string full) => full.StartsWith(_root, PathComparison);

    /// <summary>True for dot-folders/files (.obsidian, .trash, the server data folder…) and configured exclusions.</summary>
    public bool IsExcluded(string relative)
    {
        if (relative.Length == 0) return false;
        var segments = relative.Split('/');
        if (segments.Any(s => s.StartsWith('.'))) return true;
        foreach (var raw in _options.ExcludedFolders)
        {
            var ex = raw.Replace('\\', '/').Trim('/');
            if (ex.Length == 0) continue;
            if (ex.Contains('/'))
            {
                if (relative.Equals(ex, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(ex + "/", StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (segments.Any(s => s.Equals(ex, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ index

    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Skipping reparse points means symlinked folders/files are never followed.
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
    };

    /// <summary>Returns all notes, re-reading only files that changed since the last call.</summary>
    public IReadOnlyCollection<Note> GetAllNotes()
    {
        lock (_refreshLock)
        {
            var seen = new HashSet<string>(PathComparer);
            foreach (var file in Directory.EnumerateFiles(_root, "*.md", WalkOptions))
            {
                var rel = ToRelative(file);
                if (IsExcluded(rel)) continue;
                seen.Add(rel);
                var fi = new FileInfo(file);
                if (_index.TryGetValue(rel, out var cached) && cached.Modified == fi.LastWriteTimeUtc && cached.Size == fi.Length)
                    continue;
                var note = LoadNote(rel, fi);
                if (note is not null) _index[rel] = note;
            }
            foreach (var key in _index.Keys)
                if (!seen.Contains(key)) _index.TryRemove(key, out _);
            return _index.Values.ToList();
        }
    }

    /// <summary>All non-excluded files (any type), vault-relative.</summary>
    public IEnumerable<string> EnumerateFiles(string folder = "")
    {
        var full = ToFullPath(Normalize(folder));
        if (!Directory.Exists(full)) yield break;
        foreach (var f in Directory.EnumerateFiles(full, "*", WalkOptions))
        {
            var rel = ToRelative(f);
            if (!IsExcluded(rel)) yield return rel;
        }
    }

    private Note? LoadNote(string rel, FileInfo fi)
    {
        if (fi.Length > _options.MaxNoteBytes) return null;
        try
        {
            var content = File.ReadAllText(fi.FullName);
            return new Note(rel, fi.LastWriteTimeUtc, fi.Length, content, NoteParser.Parse(content));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public Note GetNote(string path)
    {
        var rel = NormalizeNotePath(path);
        var full = ToFullPath(rel);
        var fi = new FileInfo(full);
        if (!fi.Exists)
        {
            // Allow "Note Name" to find "some/folder/Note Name.md" like Obsidian's quick switcher.
            var resolved = ResolveLink(Normalize(path), null);
            if (resolved is null) throw new VaultException($"Note not found: '{rel}'");
            rel = resolved;
            fi = new FileInfo(ToFullPath(rel));
        }
        if (fi.Length > _options.MaxNoteBytes)
            throw new VaultException($"Note '{rel}' is too large ({fi.Length:N0} bytes).");
        if (_index.TryGetValue(rel, out var cached) && cached.Modified == fi.LastWriteTimeUtc && cached.Size == fi.Length)
            return cached;
        var note = LoadNote(rel, fi) ?? throw new VaultException($"Could not read note '{rel}'.");
        _index[rel] = note;
        return note;
    }

    public Note? TryGetNote(string path)
    {
        try { return GetNote(path); }
        catch (VaultException) { return null; }
    }

    /// <summary>
    /// For write targets: the exact path if that file exists, otherwise an existing note the name resolves to
    /// (e.g. "Plan" -> "Projects/Plan.md"), otherwise the normalized path for a new note.
    /// </summary>
    public string ResolveExistingOrNew(string path)
    {
        var rel = NormalizeNotePath(path);
        if (File.Exists(ToFullPath(rel))) return rel;
        return ResolveLink(Normalize(path), null) ?? rel;
    }

    // ------------------------------------------------------------------ browsing

    public IReadOnlyList<FolderEntry> ListFolder(string? folder, bool recursive, int? limit = null, bool foldersOnly = false)
    {
        var rel = Normalize(folder);
        var full = ToFullPath(rel);
        if (!Directory.Exists(full)) throw new VaultException($"Folder not found: '{rel}'");

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = WalkOptions.AttributesToSkip,
        };
        var max = Math.Clamp(limit ?? 500, 1, 5000);
        var dir = new DirectoryInfo(full);
        IEnumerable<FileSystemInfo> items = foldersOnly ? dir.EnumerateDirectories("*", opts) : dir.EnumerateFileSystemInfos("*", opts);
        return items
            .Select(i => (Info: i, Rel: ToRelative(i.FullName)))
            .Where(x => !IsExcluded(x.Rel))
            .OrderBy(x => x.Info is FileInfo).ThenBy(x => x.Rel, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Info is FileInfo f
                ? new FolderEntry(x.Rel, IsNoteExtension(f.Extension) ? "note" : "file", f.Length, f.LastWriteTimeUtc)
                : new FolderEntry(x.Rel + "/", "folder", null, null))
            .ToList();
    }

    // ------------------------------------------------------------------ search

    public sealed record SearchQuery
    {
        public string? Text { get; init; }
        public string? Folder { get; init; }
        public IReadOnlyList<string>? Tags { get; init; }
        /// <summary>Property filters: exact (case-insensitive) match, list membership, "*" = present, "!" = missing/empty.</summary>
        public IReadOnlyDictionary<string, string>? Properties { get; init; }
        public int? ModifiedWithinDays { get; init; }
        public int? Limit { get; init; }
    }

    public IReadOnlyList<SearchHit> Search(string query, string? folder = null, string? tag = null, int? limit = null) =>
        Search(new SearchQuery
        {
            Text = query, Folder = folder, Limit = limit,
            Tags = string.IsNullOrWhiteSpace(tag) ? null : [tag],
        });

    public IReadOnlyList<SearchHit> Search(SearchQuery q)
    {
        var terms = Tokenize(q.Text);
        var folderRel = string.IsNullOrWhiteSpace(q.Folder) ? "" : Normalize(q.Folder);
        var tags = (q.Tags ?? []).Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0).ToList();
        var props = q.Properties ?? new Dictionary<string, string>();
        var since = q.ModifiedWithinDays is > 0 ? DateTime.UtcNow.AddDays(-q.ModifiedWithinDays.Value) : (DateTime?)null;
        if (terms.Count == 0 && tags.Count == 0 && props.Count == 0 && folderRel.Length == 0 && since is null)
            throw new VaultException("Provide a query or at least one filter (folder, tags, properties, modified_within_days).");

        var hits = new List<SearchHit>();
        foreach (var note in GetAllNotes())
        {
            if (folderRel.Length > 0 && !note.Path.StartsWith(folderRel + "/", StringComparison.OrdinalIgnoreCase)) continue;
            if (since is not null && note.Modified < since) continue;
            if (tags.Any(t => !HasTag(note, t))) continue;
            if (props.Any(p => !MatchesProperty(note.Properties, p.Key, p.Value))) continue;

            double score = 0;
            var allMatch = true;
            foreach (var term in terms)
            {
                var inTitle = note.Path.Contains(term, StringComparison.OrdinalIgnoreCase);
                var count = CountOccurrences(note.Content, term);
                if (!inTitle && count == 0) { allMatch = false; break; }
                score += (inTitle ? 10 : 0) + Math.Min(count, 20) + (note.Parsed.Tags.Any(t => t.Equals(term.TrimStart('#'), StringComparison.OrdinalIgnoreCase)) ? 5 : 0);
            }
            if (!allMatch) continue;
            if (terms.Count > 1 && note.Content.Contains(q.Text!.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase)) score += 15;
            hits.Add(new SearchHit(note.Path, note.Title, score, Snippet(note, terms), note.Parsed.Tags, note.Modified));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Modified)
            .Take(Math.Clamp(q.Limit ?? _options.MaxResults, 1, _options.MaxResults))
            .ToList();
    }

    public static bool MatchesProperty(Dictionary<string, object?> fm, string key, string expected)
    {
        fm.TryGetValue(key, out var value);
        var values = PropertyValues(value);
        return expected switch
        {
            "*" => values.Count > 0,
            "!" => values.Count == 0,
            _ => values.Any(v => v.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                                 StripLink(v).Equals(StripLink(expected), StringComparison.OrdinalIgnoreCase)),
        };
    }

    /// <summary>Flattens a frontmatter value into non-empty strings.</summary>
    public static List<string> PropertyValues(object? value) => value switch
    {
        null => [],
        string s => string.IsNullOrWhiteSpace(s) ? [] : [s.Trim()],
        IEnumerable<object?> list => list.SelectMany(PropertyValues).ToList(),
        _ => [Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""],
    };

    /// <summary>"[[Target|Alias]]" -> "Target".</summary>
    public static string StripLink(string value)
    {
        var v = value.Trim();
        if (v.StartsWith("[[") && v.EndsWith("]]")) v = v[2..^2];
        var bar = v.IndexOf('|');
        if (bar >= 0) v = v[..bar];
        var hash = v.IndexOf('#');
        if (hash > 0) v = v[..hash];
        return v.Trim();
    }

    private static List<string> Tokenize(string? query)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return result;
        foreach (Match m in Regex.Matches(query, "\"([^\"]+)\"|(\\S+)"))
            result.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return result;
    }

    private static int CountOccurrences(string text, string term)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(term, i, StringComparison.OrdinalIgnoreCase)) >= 0) { count++; i += term.Length; }
        return count;
    }

    private static string Snippet(Note note, IReadOnlyList<string> terms, int radius = 100)
    {
        var body = note.Parsed.Body;
        var idx = -1;
        foreach (var t in terms)
        {
            idx = body.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) break;
        }
        if (idx < 0) idx = 0;
        var start = Math.Max(0, idx - radius);
        var end = Math.Min(body.Length, idx + radius);
        var s = Regex.Replace(body[start..end], @"\s+", " ").Trim();
        return (start > 0 ? "…" : "") + s + (end < body.Length ? "…" : "");
    }

    // ------------------------------------------------------------------ tags

    public static bool HasTag(Note note, string tag, bool includeNested = true) =>
        note.Parsed.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase) ||
                                  (includeNested && t.StartsWith(tag + "/", StringComparison.OrdinalIgnoreCase)));

    public IReadOnlyDictionary<string, int> GetTags()
    {
        var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in GetAllNotes())
            foreach (var t in note.Parsed.Tags)
                counts[t] = counts.TryGetValue(t, out var c) ? c + 1 : 1;
        return counts;
    }

    // ------------------------------------------------------------------ links

    /// <summary>Resolves a link target to a vault-relative note path using Obsidian's rules (roughly).</summary>
    public string? ResolveLink(string target, string? sourcePath) => ResolveLink(target, sourcePath, GetAllNotes());

    internal static string? ResolveLink(string target, string? sourcePath, IReadOnlyCollection<Note> notes)
    {
        var t = target.Trim().Replace('\\', '/').TrimStart('/');
        if (t.Length == 0) return sourcePath; // [[#heading]] links to self
        if (t.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) t = t[..^3];

        var sourceDir = sourcePath is null ? "" : (System.IO.Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? "");

        // 1. Exact vault-relative path, 2. relative to the source note's folder.
        var candidates = new[] { t, sourceDir.Length > 0 ? CombineRelative(sourceDir, t) : null };
        foreach (var c in candidates)
        {
            if (c is null) continue;
            var match = notes.FirstOrDefault(n => n.Path[..^3].Equals(c, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Path;
        }

        // 3. Shortest-path match on the trailing segment(s): [[Note]] or [[folder/Note]].
        var byName = notes
            .Where(n => n.Path[..^3].Equals(t, StringComparison.OrdinalIgnoreCase) ||
                        n.Path[..^3].EndsWith("/" + t, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byName.Count == 0) return null;
        return byName
            .OrderByDescending(n => sourceDir.Length > 0 && n.Path.StartsWith(sourceDir + "/", StringComparison.OrdinalIgnoreCase))
            .ThenBy(n => n.Path.Count(ch => ch == '/'))
            .ThenBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .First().Path;
    }

    /// <summary>Resolves a link to a non-note file (attachment) by path or file name.</summary>
    public string? ResolveAttachment(string target, string? sourcePath)
    {
        var t = StripLink(target).Replace('\\', '/').TrimStart('/');
        if (t.Length == 0) return null;
        try
        {
            var direct = Normalize(t);
            if (File.Exists(ToFullPath(direct))) return direct;
            var sourceDir = sourcePath is null ? "" : System.IO.Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? "";
            if (sourceDir.Length > 0 && CombineRelative(sourceDir, t) is { } rel && !IsExcluded(rel) && File.Exists(ToFullPath(rel))) return rel;
        }
        catch (VaultException) { return null; }
        var name = System.IO.Path.GetFileName(t);
        return EnumerateFiles()
            .Where(f => System.IO.Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Count(c => c == '/'))
            .FirstOrDefault();
    }

    private static string? CombineRelative(string dir, string rel)
    {
        var parts = new List<string>(dir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var seg in rel.Split('/'))
        {
            if (seg == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else if (seg != ".") parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    public IReadOnlyList<LinkInfo> GetOutgoingLinks(string path)
    {
        var note = GetNote(path);
        var notes = GetAllNotes();
        return note.Parsed.Links
            .Select(l =>
            {
                var ext = System.IO.Path.GetExtension(l.Target);
                var resolved = ext.Length > 0 && !IsNoteExtension(ext)
                    ? ResolveAttachment(l.Target, note.Path)
                    : ResolveLink(l.Target, note.Path, notes);
                return new LinkInfo(l.Target, l.Subpath, resolved, l.IsEmbed);
            })
            .ToList();
    }

    public IReadOnlyList<Backlink> GetBacklinks(string path)
    {
        var target = GetNote(path).Path;
        var notes = GetAllNotes();
        var name = System.IO.Path.GetFileNameWithoutExtension(target);
        var result = new List<Backlink>();
        foreach (var n in notes)
        {
            if (n.Path.Equals(target, PathComparison)) continue;
            var count = n.Parsed.Links.Count(l => ResolveLink(l.Target, n.Path, notes) is { } r && r.Equals(target, PathComparison));
            // Frontmatter relationships such as `matter: "[[DEMO-101-A]]"` also count as links.
            count += n.Properties.Values.SelectMany(PropertyValues)
                .Count(v => v.StartsWith("[[") && ResolveLink(StripLink(v), n.Path, notes) is { } r && r.Equals(target, PathComparison));
            if (count == 0) continue;
            var context = n.Content.Split('\n')
                .Where(line => line.Contains(name, StringComparison.OrdinalIgnoreCase) && (line.Contains("[[") || line.Contains("](")))
                .Select(line => line.Trim())
                .Take(3)
                .ToList();
            result.Add(new Backlink(n.Path, count, context));
        }
        return result.OrderByDescending(b => b.Count).ThenBy(b => b.Path).ToList();
    }

    // ------------------------------------------------------------------ change management

    /// <summary>Runs a write operation under the vault-wide write lock.</summary>
    public async Task<T> WriteAsync<T>(Func<Task<T>> action)
    {
        if (_options.ReadOnly) throw new VaultException("The vault is mounted read-only.");
        await _writeLock.WaitAsync();
        try { return await action(); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Current file content, or null if it does not exist. Call inside <see cref="WriteAsync{T}"/>.</summary>
    public string? ReadCurrent(string rel)
    {
        var full = ToFullPath(rel);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    /// <summary>
    /// Commits new content for a file (call inside <see cref="WriteAsync{T}"/>). Verifies the expected version,
    /// computes a preview, and unless dry-run writes atomically, snapshots the previous content and audits.
    /// </summary>
    public async Task<ChangeResult> CommitAsync(string rel, string? before, string after, string operation, WriteOptions opts,
        IReadOnlyList<ValidationIssue>? warnings = null, string? message = null)
    {
        var versionBefore = before is null ? null : AuditLog.Version(before);
        if (opts.ExpectedVersion is { Length: > 0 } expected && !expected.Equals(versionBefore ?? "none", StringComparison.OrdinalIgnoreCase))
            throw new VaultException(
                $"'{rel}' was modified since it was read (expected version {expected}, current {versionBefore ?? "none"}). " +
                "Re-read it and reapply the change.");

        var propertyChanges = DiffProperties(before, after);
        var (_, bodyBefore) = NoteParser.SplitFrontmatter(before ?? "");
        var (_, bodyAfter) = NoteParser.SplitFrontmatter(after);
        var bodyDiff = bodyBefore == bodyAfter ? [] : TextDiff.Lines(bodyBefore, bodyAfter);
        var versionAfter = AuditLog.Version(after);

        if (opts.DryRun)
            return new ChangeResult(rel, operation, true, before is null, versionBefore, versionAfter, propertyChanges, bodyDiff, null, warnings,
                message ?? "Preview only. Nothing was written. Repeat the call with dry_run=false to apply.");

        if (before is not null && before == after)
            return new ChangeResult(rel, operation, false, false, versionBefore, versionBefore, [], [], null, warnings, "No changes.");

        if (before is not null) Audit.SaveSnapshot(before);
        await WriteAtomicAsync(rel, after);
        var entry = Audit.Append(new AuditEntry
        {
            Operation = operation, Path = rel, Collection = opts.Collection, RecordId = opts.RecordId,
            VersionBefore = versionBefore, VersionAfter = versionAfter, Reason = opts.Reason,
            PropertyChanges = propertyChanges, BodyChanged = bodyDiff.Count > 0,
        });
        return new ChangeResult(rel, operation, false, before is null, versionBefore, versionAfter, propertyChanges, bodyDiff, entry.Id, warnings, message);
    }

    private async Task WriteAtomicAsync(string rel, string content)
    {
        var full = ToFullPath(rel);
        var dir = System.IO.Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        // Hidden temp file in the same folder, then an atomic rename over the target.
        var tmp = System.IO.Path.Combine(dir, $".{System.IO.Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tmp, content, Utf8NoBom);
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        _index.TryRemove(rel, out _);
    }

    private static IReadOnlyList<PropertyChange> DiffProperties(string? before, string after)
    {
        var a = NoteParser.ParseYaml(NoteParser.SplitFrontmatter(before ?? "").Yaml);
        var b = NoteParser.ParseYaml(NoteParser.SplitFrontmatter(after).Yaml);
        var changes = new List<PropertyChange>();
        foreach (var key in a.Keys.Union(b.Keys))
        {
            a.TryGetValue(key, out var va);
            b.TryGetValue(key, out var vb);
            if (JsonSerializer.Serialize(va) != JsonSerializer.Serialize(vb)) changes.Add(new PropertyChange(key, va, vb));
        }
        return changes;
    }

    /// <summary>Moves a file (call inside <see cref="WriteAsync{T}"/>), audited.</summary>
    public ChangeResult MoveFile(string fromRel, string toRel, string operation, WriteOptions opts)
    {
        var src = ToFullPath(fromRel);
        var dest = ToFullPath(toRel);
        if (!File.Exists(src)) throw new VaultException($"Not found: '{fromRel}'");
        if (File.Exists(dest)) throw new VaultException($"A file already exists at '{toRel}'.");
        var content = File.ReadAllText(src);
        var version = AuditLog.Version(content);
        if (opts.ExpectedVersion is { Length: > 0 } expected && !expected.Equals(version, StringComparison.OrdinalIgnoreCase))
            throw new VaultException($"'{fromRel}' was modified since it was read. Re-read it and try again.");
        if (opts.DryRun)
            return new ChangeResult(toRel, operation, true, false, version, version, [], [$"moved from {fromRel}"], null,
                Message: "Preview only. Nothing was moved.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
        File.Move(src, dest);
        _index.TryRemove(fromRel, out _);
        var entry = Audit.Append(new AuditEntry
        {
            Operation = operation, Path = toRel, FromPath = fromRel, Collection = opts.Collection, RecordId = opts.RecordId,
            VersionBefore = version, VersionAfter = version, Reason = opts.Reason,
        });
        return new ChangeResult(toRel, operation, false, false, version, version, [], [$"moved from {fromRel}"], entry.Id);
    }

    // ------------------------------------------------------------------ generic note writes

    public Task<ChangeResult> CreateNote(string path, string content, WriteOptions opts) => WriteAsync(async () =>
    {
        var rel = NormalizeNotePath(path);
        if (ReadCurrent(rel) is not null)
            throw new VaultException($"Note already exists: '{rel}'. Use update_note or append_to_note to change it.");
        return await CommitAsync(rel, null, content, "create_note", opts);
    });

    public Task<ChangeResult> AppendToNote(string path, string content, bool createIfMissing, string? heading, WriteOptions opts) => WriteAsync(async () =>
    {
        var rel = ResolveExistingOrNew(path);
        return await CommitAsync(rel, ReadCurrent(rel) is { } before ? before : NotFoundOr(rel, createIfMissing),
            AppendContent(ReadCurrent(rel), content, heading, rel), "append_to_note", opts);
    });

    private static string? NotFoundOr(string rel, bool create) =>
        create ? null : throw new VaultException($"Note not found: '{rel}'");

    /// <summary>Appends at the end of the note or of a heading's section (creating the heading if needed).</summary>
    public static string AppendContent(string? existing, string content, string? heading, string rel, bool createHeading = true)
    {
        if (existing is null)
        {
            var initial = string.IsNullOrWhiteSpace(heading) ? content : $"## {CleanHeading(heading)}\n\n{content}";
            return EnsureEndsWithNewline(initial);
        }
        if (string.IsNullOrWhiteSpace(heading))
            return EnsureEndsWithNewline(existing.Length == 0 ? content : existing.TrimEnd('\r', '\n') + "\n\n" + content);
        var inserted = TryInsertUnderHeading(existing, heading, content);
        if (inserted is not null) return EnsureEndsWithNewline(inserted);
        if (!createHeading) throw new VaultException($"Heading '{CleanHeading(heading)}' not found in '{rel}'.");
        return EnsureEndsWithNewline(existing.TrimEnd('\r', '\n') + $"\n\n## {CleanHeading(heading)}\n\n{content}");
    }

    private static string CleanHeading(string h) => h.Trim().TrimStart('#').Trim();
    private static string EnsureEndsWithNewline(string s) => s.EndsWith('\n') ? s : s + "\n";

    /// <summary>Returns the text of a heading's section (without the heading line), or null.</summary>
    public static string? GetSection(string content, string heading)
    {
        var (start, end, _) = FindSection(content.Replace("\r\n", "\n").Split('\n').ToList(), heading);
        if (start < 0) return null;
        var lines = content.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', lines[(start + 1)..end]).Trim();
    }

    private static (int Start, int End, int Level) FindSection(List<string> lines, string heading)
    {
        var wanted = CleanHeading(heading);
        int start = -1, level = 0;
        var inFence = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~")) { inFence = !inFence; continue; }
            if (inFence) continue;
            var m = Regex.Match(line, @"^(#{1,6})[ \t]+(.+?)[ \t#]*$");
            if (!m.Success) continue;
            if (start < 0)
            {
                if (m.Groups[2].Value.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase)) { start = i; level = m.Groups[1].Length; }
            }
            else if (m.Groups[1].Length <= level) return (start, i, level);
        }
        return (start, lines.Count, level);
    }

    private static string? TryInsertUnderHeading(string content, string heading, string insert)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        var (start, end, _) = FindSection(lines, heading);
        if (start < 0) return null;
        // Insert after the last non-blank line of the section.
        var at = end;
        while (at - 1 > start && string.IsNullOrWhiteSpace(lines[at - 1])) at--;
        var block = new List<string>();
        if (at - 1 == start) block.Add("");
        block.AddRange(insert.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));
        if (lines.Skip(at).Any(l => !string.IsNullOrWhiteSpace(l))) block.Add("");
        lines.InsertRange(at, block);
        return string.Join('\n', lines);
    }

    /// <summary>Exact text replacement. Returns the new content.</summary>
    public static string ReplaceText(string content, string oldText, string newText, bool replaceAll, string rel)
    {
        if (string.IsNullOrEmpty(oldText)) throw new VaultException("old_text must not be empty.");
        int count = 0, i = 0;
        while ((i = content.IndexOf(oldText, i, StringComparison.Ordinal)) >= 0) { count++; i += oldText.Length; }
        if (count == 0) throw new VaultException($"Text not found in '{rel}'. Read the note first and copy the text exactly.");
        if (count > 1 && !replaceAll)
            throw new VaultException($"Text occurs {count} times in '{rel}'. Include more surrounding text or set replace_all=true.");
        if (replaceAll) return content.Replace(oldText, newText, StringComparison.Ordinal);
        var at = content.IndexOf(oldText, StringComparison.Ordinal);
        return content[..at] + newText + content[(at + oldText.Length)..];
    }

    /// <summary>Applies property changes to a note's content, keeping the body intact.</summary>
    public static (string Content, Dictionary<string, object?> Properties) WithProperties(
        string content, IReadOnlyDictionary<string, object?>? set, IEnumerable<string>? remove, string rel)
    {
        var (yaml, body) = NoteParser.SplitFrontmatter(content);
        var fm = NoteParser.ParseYaml(yaml);
        if (fm.ContainsKey("_error")) throw new VaultException($"'{rel}' has invalid YAML frontmatter; fix it with update_note first.");
        foreach (var (k, v) in set ?? new Dictionary<string, object?>()) fm[k] = v;
        foreach (var k in remove ?? []) fm.Remove(k);
        return (NoteParser.Compose(fm, body), fm);
    }

    public Task<(string NewPath, IReadOnlyList<ChangeResult> Changes)> MoveNote(string from, string to, bool updateLinks, WriteOptions opts) => WriteAsync(async () =>
    {
        var source = GetNote(from);
        var destRel = NormalizeNotePath(to.EndsWith('/') ? to + System.IO.Path.GetFileName(source.Path) : to);
        if (File.Exists(ToFullPath(destRel))) throw new VaultException($"A note already exists at '{destRel}'.");

        var notes = GetAllNotes();
        var edits = new List<(string Path, string Before, string After)>();
        if (updateLinks)
        {
            var newName = System.IO.Path.GetFileNameWithoutExtension(destRel);
            var nameIsUnique = !notes.Any(n => !n.Path.Equals(source.Path, PathComparison) &&
                System.IO.Path.GetFileNameWithoutExtension(n.Path).Equals(newName, StringComparison.OrdinalIgnoreCase));
            var wikiTarget = nameIsUnique ? newName : destRel[..^3];

            foreach (var n in notes)
            {
                var isSelf = n.Path.Equals(source.Path, PathComparison);
                var notePathAfter = isSelf ? destRel : n.Path;
                var updated = NoteParser.RewriteLinks(n.Content, (link, isWiki) =>
                {
                    if (link.Target.Length == 0) return null;
                    var resolved = ResolveLink(link.Target, n.Path, notes);
                    if (resolved is null) return null;
                    if (resolved.Equals(source.Path, PathComparison))
                        return isWiki ? wikiTarget : RelativeLink(notePathAfter, destRel);
                    // Relative markdown links inside the moved note must be re-based.
                    if (isSelf && !isWiki) return RelativeLink(destRel, resolved);
                    return null;
                });
                if (!string.Equals(updated, n.Content, StringComparison.Ordinal)) edits.Add((notePathAfter, n.Content, updated));
            }
        }

        var changes = new List<ChangeResult> { MoveFile(source.Path, destRel, "move_note", opts) };
        var editOpts = opts with { ExpectedVersion = null };
        foreach (var (p, before, after) in edits)
        {
            // In dry-run mode the moved note still lives at its old path.
            var current = opts.DryRun ? before : ReadCurrent(p) ?? before;
            changes.Add(await CommitAsync(p, current, after, "update_links", editOpts));
        }
        return (destRel, (IReadOnlyList<ChangeResult>)changes);
    });

    private static string RelativeLink(string fromNote, string toNote)
    {
        var fromDir = System.IO.Path.GetDirectoryName(fromNote)?.Replace('\\', '/') ?? "";
        return fromDir.Length == 0 ? toNote : System.IO.Path.GetRelativePath("/" + fromDir, "/" + toNote).Replace('\\', '/');
    }

    public string ObsidianUrl(string path) =>
        $"obsidian://open?vault={Uri.EscapeDataString(VaultName)}&file={Uri.EscapeDataString(path)}";

    private static string EnsureTrailingSeparator(string p) =>
        p.EndsWith(System.IO.Path.DirectorySeparatorChar) ? p : p + System.IO.Path.DirectorySeparatorChar;

    private static string ExpandHome(string p) =>
        p.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..] : p;
}
