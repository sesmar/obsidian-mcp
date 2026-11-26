using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ObsidianMcp.Server.Vault;

/// <summary>A note held in the in-memory index.</summary>
public sealed record Note(string Path, DateTime Modified, long Size, string Content, ParsedNote Parsed)
{
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
}

public sealed record FolderEntry(string Path, string Type, long? Size, DateTime? Modified);
public sealed record SearchHit(string Path, string Title, double Score, string Snippet, IReadOnlyList<string> Tags);
public sealed record LinkInfo(string Target, string? Subpath, string? ResolvedPath, bool IsEmbed);
public sealed record Backlink(string Path, int Count, IReadOnlyList<string> Context);

/// <summary>
/// All file-system access to the vault goes through this class. It guarantees that every path stays
/// inside the vault root (no "..", absolute paths, or symlink escapes) and that excluded folders
/// such as .obsidian are never touched.
/// </summary>
public sealed class VaultService
{
    private readonly VaultOptions _options;
    private readonly string _root;       // full path with trailing separator
    private readonly string _realRoot;   // root with symlinks resolved, trailing separator
    private readonly ConcurrentDictionary<string, Note> _index;
    private readonly Lock _refreshLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    private static readonly StringComparer PathComparer =
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
    }

    public string RootPath => _root;
    public string VaultName { get; }
    public bool IsReadOnly => _options.ReadOnly;
    public int MaxResults => _options.MaxResults;

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
        if (!System.IO.Path.HasExtension(p) || !IsNoteExtension(System.IO.Path.GetExtension(p)))
            p += ".md";
        return p;
    }

    private static bool IsNoteExtension(string ext) => ext.Equals(".md", StringComparison.OrdinalIgnoreCase);

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
        while (current.Length >= _root.Length - 1 && IsUnderRoot(current))
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

    public bool IsExcluded(string relative)
    {
        if (relative.Length == 0) return false;
        var segments = relative.Split('/');
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
                if (IsExcluded(rel) || rel.Split('/').Any(s => s.StartsWith('.'))) continue;
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

    /// <summary>
    /// For write targets: the exact path if that file exists, otherwise an existing note the name resolves to
    /// (e.g. "Plan" -> "Projects/Plan.md"), otherwise the normalized path for a new note.
    /// </summary>
    private string ResolveExistingOrNew(string path)
    {
        var rel = NormalizeNotePath(path);
        if (File.Exists(ToFullPath(rel))) return rel;
        return ResolveLink(Normalize(path), null) ?? rel;
    }

    // ------------------------------------------------------------------ browsing

    public IReadOnlyList<FolderEntry> ListFolder(string? folder, bool recursive, int? limit = null)
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
        return new DirectoryInfo(full).EnumerateFileSystemInfos("*", opts)
            .Select(i => (Info: i, Rel: ToRelative(i.FullName)))
            .Where(x => !IsExcluded(x.Rel) && !x.Rel.Split('/').Any(s => s.StartsWith('.')))
            .OrderBy(x => x.Info is FileInfo).ThenBy(x => x.Rel, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Info is FileInfo f
                ? new FolderEntry(x.Rel, IsNoteExtension(f.Extension) ? "note" : "file", f.Length, f.LastWriteTimeUtc)
                : new FolderEntry(x.Rel + "/", "folder", null, null))
            .ToList();
    }

    public IReadOnlyList<Note> RecentNotes(int limit) =>
        GetAllNotes().OrderByDescending(n => n.Modified).Take(Math.Clamp(limit, 1, _options.MaxResults)).ToList();

    // ------------------------------------------------------------------ search

    public IReadOnlyList<SearchHit> Search(string query, string? folder = null, string? tag = null, int? limit = null)
    {
        var terms = Tokenize(query);
        var folderRel = string.IsNullOrWhiteSpace(folder) ? "" : Normalize(folder);
        var tagFilter = tag?.Trim().TrimStart('#');
        if (terms.Count == 0 && string.IsNullOrEmpty(tagFilter) && folderRel.Length == 0)
            throw new VaultException("Provide a query, a tag, or a folder to search.");

        var hits = new List<SearchHit>();
        foreach (var note in GetAllNotes())
        {
            if (folderRel.Length > 0 && !note.Path.StartsWith(folderRel + "/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(tagFilter) && !HasTag(note, tagFilter)) continue;

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
            if (terms.Count > 1 && note.Content.Contains(query.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase)) score += 15;
            hits.Add(new SearchHit(note.Path, note.Title, score, Snippet(note, terms), note.Parsed.Tags));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => _index.TryGetValue(h.Path, out var n) ? n.Modified : DateTime.MinValue)
            .Take(Math.Clamp(limit ?? _options.MaxResults, 1, _options.MaxResults))
            .ToList();
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

    private static string? ResolveLink(string target, string? sourcePath, IReadOnlyCollection<Note> notes)
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
            .Select(l => new LinkInfo(l.Target, l.Subpath, ResolveLink(l.Target, note.Path, notes), l.IsEmbed))
            .ToList();
    }

    public IReadOnlyList<Backlink> GetBacklinks(string path)
    {
        var target = GetNote(path).Path;
        var notes = GetAllNotes();
        var result = new List<Backlink>();
        foreach (var n in notes)
        {
            if (n.Path.Equals(target, PathComparison)) continue;
            var count = n.Parsed.Links.Count(l => ResolveLink(l.Target, n.Path, notes) is { } r && r.Equals(target, PathComparison));
            if (count == 0) continue;
            var name = System.IO.Path.GetFileNameWithoutExtension(target);
            var context = n.Content.Split('\n')
                .Where(line => line.Contains(name, StringComparison.OrdinalIgnoreCase) && (line.Contains("[[") || line.Contains("](")))
                .Select(line => line.Trim())
                .Take(3)
                .ToList();
            result.Add(new Backlink(n.Path, count, context));
        }
        return result.OrderByDescending(b => b.Count).ThenBy(b => b.Path).ToList();
    }

    // ------------------------------------------------------------------ writes

    private void EnsureWritable()
    {
        if (_options.ReadOnly) throw new VaultException("The vault is mounted read-only.");
    }

    private async Task<T> WithWriteLock<T>(Func<Task<T>> action)
    {
        EnsureWritable();
        await _writeLock.WaitAsync();
        try { return await action(); }
        finally { _writeLock.Release(); }
    }

    public Task<string> CreateNote(string path, string content, bool overwrite) => WithWriteLock(async () =>
    {
        var rel = NormalizeNotePath(path);
        var full = ToFullPath(rel);
        if (File.Exists(full) && !overwrite)
            throw new VaultException($"Note already exists: '{rel}'. Use overwrite=true or a different tool to edit it.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
        return rel;
    });

    public Task<string> AppendToNote(string path, string content, bool createIfMissing, string? heading = null) => WithWriteLock(async () =>
    {
        var rel = ResolveExistingOrNew(path);
        var full = ToFullPath(rel);
        if (!File.Exists(full))
        {
            if (!createIfMissing) throw new VaultException($"Note not found: '{rel}'");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            var initial = string.IsNullOrWhiteSpace(heading) ? content : $"## {heading.Trim().TrimStart('#').Trim()}\n\n{content}";
            await File.WriteAllTextAsync(full, EnsureEndsWithNewline(initial));
            return rel;
        }

        var existing = await File.ReadAllTextAsync(full);
        string updated;
        if (string.IsNullOrWhiteSpace(heading))
        {
            updated = existing.Length == 0 ? content
                : existing.TrimEnd('\r', '\n') + "\n\n" + content;
        }
        else
        {
            updated = InsertUnderHeading(existing, heading, content, rel);
        }
        await File.WriteAllTextAsync(full, EnsureEndsWithNewline(updated));
        return rel;
    });

    private static string EnsureEndsWithNewline(string s) => s.EndsWith('\n') ? s : s + "\n";

    private static string InsertUnderHeading(string content, string heading, string insert, string rel)
    {
        var wanted = heading.Trim().TrimStart('#').Trim();
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
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
                if (m.Groups[2].Value.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    start = i; level = m.Groups[1].Length;
                }
            }
            else if (m.Groups[1].Length <= level)
            {
                return Splice(lines, start, i, insert);
            }
        }
        if (start < 0) throw new VaultException($"Heading '{wanted}' not found in '{rel}'.");
        return Splice(lines, start, lines.Count, insert);
    }

    private static string Splice(List<string> lines, int headingLine, int sectionEnd, string insert)
    {
        // Insert after the last non-blank line of the section.
        var at = sectionEnd;
        while (at - 1 > headingLine && string.IsNullOrWhiteSpace(lines[at - 1])) at--;
        var block = new List<string>();
        if (at - 1 == headingLine) block.Add("");
        block.AddRange(insert.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));
        if (at < lines.Count) block.Add("");
        lines.InsertRange(at, block);
        return string.Join('\n', lines);
    }

    public Task<(string Path, int Replacements)> ReplaceInNote(string path, string oldText, string newText, bool replaceAll) => WithWriteLock(async () =>
    {
        if (string.IsNullOrEmpty(oldText)) throw new VaultException("old_text must not be empty.");
        var note = GetNote(path);
        var full = ToFullPath(note.Path);
        var content = await File.ReadAllTextAsync(full);
        var count = CountOrdinal(content, oldText);
        if (count == 0) throw new VaultException($"Text not found in '{note.Path}'. Read the note first and copy the text exactly.");
        if (count > 1 && !replaceAll)
            throw new VaultException($"Text occurs {count} times in '{note.Path}'. Include more surrounding text or set replace_all=true.");
        var updated = replaceAll ? content.Replace(oldText, newText, StringComparison.Ordinal) : ReplaceFirst(content, oldText, newText);
        await File.WriteAllTextAsync(full, updated);
        return (note.Path, replaceAll ? count : 1);
    });

    private static int CountOrdinal(string text, string term)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(term, i, StringComparison.Ordinal)) >= 0) { count++; i += term.Length; }
        return count;
    }

    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        var i = text.IndexOf(oldText, StringComparison.Ordinal);
        return text[..i] + newText + text[(i + oldText.Length)..];
    }

    public Task<Dictionary<string, object?>> UpdateFrontmatter(string path, Dictionary<string, JsonElement>? set, IEnumerable<string>? remove) => WithWriteLock(async () =>
    {
        var note = GetNote(path);
        var full = ToFullPath(note.Path);
        var content = await File.ReadAllTextAsync(full);
        var (yaml, body) = NoteParser.SplitFrontmatter(content);
        var fm = NoteParser.ParseYaml(yaml);
        if (fm.ContainsKey("_error")) throw new VaultException($"'{note.Path}' has invalid YAML frontmatter; fix it with replace_in_note instead.");
        foreach (var (k, v) in set ?? []) fm[k] = NoteParser.FromJson(v);
        foreach (var k in remove ?? []) fm.Remove(k);
        if (fm.Count > 0 && !body.StartsWith('\n') && body.Length > 0) body = body.TrimStart('\r');
        await File.WriteAllTextAsync(full, NoteParser.Compose(fm, body));
        return fm;
    });

    public Task<(string NewPath, IReadOnlyList<string> UpdatedNotes)> MoveNote(string from, string to, bool updateLinks) => WithWriteLock(async () =>
    {
        var source = GetNote(from);
        var destRel = NormalizeNotePath(to.EndsWith('/') ? to + System.IO.Path.GetFileName(source.Path) : to);
        var srcFull = ToFullPath(source.Path);
        var destFull = ToFullPath(destRel);
        if (File.Exists(destFull)) throw new VaultException($"A note already exists at '{destRel}'.");

        var notes = GetAllNotes();
        var edits = new List<(string Path, string Content)>();
        if (updateLinks)
        {
            var newName = System.IO.Path.GetFileNameWithoutExtension(destRel);
            var nameIsUnique = !notes.Any(n => !n.Path.Equals(source.Path, PathComparison) &&
                System.IO.Path.GetFileNameWithoutExtension(n.Path).Equals(newName, StringComparison.OrdinalIgnoreCase));
            var wikiTarget = nameIsUnique ? newName : destRel[..^3];

            foreach (var n in notes)
            {
                var isSelf = n.Path.Equals(source.Path, PathComparison);
                if (!isSelf && !n.Parsed.Links.Any(l => ResolveLink(l.Target, n.Path, notes) is { } r && r.Equals(source.Path, PathComparison)))
                    continue;
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
                if (!string.Equals(updated, n.Content, StringComparison.Ordinal)) edits.Add((notePathAfter, updated));
            }
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destFull)!);
        File.Move(srcFull, destFull);
        foreach (var (p, c) in edits) await File.WriteAllTextAsync(ToFullPath(p), c);
        return (destRel, (IReadOnlyList<string>)edits.Select(e => e.Path).Where(p => !p.Equals(destRel, PathComparison)).ToList());
    });

    private static string RelativeLink(string fromNote, string toNote)
    {
        var fromDir = System.IO.Path.GetDirectoryName(fromNote)?.Replace('\\', '/') ?? "";
        return fromDir.Length == 0 ? toNote : System.IO.Path.GetRelativePath("/" + fromDir, "/" + toNote).Replace('\\', '/');
    }

    public Task<string> DeleteNote(string path, bool permanent) => WithWriteLock(() =>
    {
        var note = GetNote(path);
        var full = ToFullPath(note.Path);
        if (permanent)
        {
            File.Delete(full);
            _index.TryRemove(note.Path, out _);
            return Task.FromResult($"Permanently deleted '{note.Path}'.");
        }
        // Same behaviour as Obsidian's "Move to Obsidian trash" (.trash folder in the vault root).
        var trash = System.IO.Path.Combine(_root, ".trash");
        Directory.CreateDirectory(trash);
        var name = System.IO.Path.GetFileName(full);
        var dest = System.IO.Path.Combine(trash, name);
        if (File.Exists(dest))
            dest = System.IO.Path.Combine(trash, $"{System.IO.Path.GetFileNameWithoutExtension(name)} {DateTime.Now:yyyyMMddHHmmss}.md");
        File.Move(full, dest);
        _index.TryRemove(note.Path, out _);
        return Task.FromResult($"Moved '{note.Path}' to the vault's .trash folder.");
    });

    // ------------------------------------------------------------------ daily notes

    public string DailyNotePath(DateOnly date)
    {
        string? folder = _options.DailyNotesFolder, format = _options.DailyNotesFormat;
        if (folder is null || format is null)
        {
            try
            {
                var cfg = System.IO.Path.Combine(_root, ".obsidian", "daily-notes.json");
                if (File.Exists(cfg))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                    if (folder is null && doc.RootElement.TryGetProperty("folder", out var f)) folder = f.GetString();
                    if (format is null && doc.RootElement.TryGetProperty("format", out var fmt) && !string.IsNullOrWhiteSpace(fmt.GetString()))
                        format = NoteParser.MomentToDotNet(fmt.GetString()!);
                }
            }
            catch (Exception e) when (e is IOException or JsonException) { /* fall back to defaults */ }
        }
        format ??= "yyyy-MM-dd";
        var name = date.ToString(format, CultureInfo.InvariantCulture);
        var rel = string.IsNullOrWhiteSpace(folder) ? name : $"{folder.Trim('/')}/{name}";
        return NormalizeNotePath(rel);
    }

    public string ObsidianUrl(string path) =>
        $"obsidian://open?vault={Uri.EscapeDataString(VaultName)}&file={Uri.EscapeDataString(path)}";

    private static string EnsureTrailingSeparator(string p) =>
        p.EndsWith(System.IO.Path.DirectorySeparatorChar) ? p : p + System.IO.Path.DirectorySeparatorChar;

    private static string ExpandHome(string p) =>
        p.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..] : p;
}
