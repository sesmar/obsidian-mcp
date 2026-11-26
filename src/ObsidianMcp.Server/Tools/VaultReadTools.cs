using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Server.Tools;

/// <summary>Read-only tools. Always exposed.</summary>
[McpServerToolType]
public sealed class VaultReadTools(VaultService vault)
{
    [McpServerTool(Name = "get_vault_info", Title = "Vault info", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns an overview of the Obsidian vault: name, note count, top-level folders, most-used tags and whether it is writable. Call this first to orient yourself.")]
    public object GetVaultInfo()
    {
        var notes = vault.GetAllNotes();
        return new
        {
            name = vault.VaultName,
            noteCount = notes.Count,
            readOnly = vault.IsReadOnly,
            topLevel = vault.ListFolder("", recursive: false, limit: 200).Select(e => e.Path),
            topTags = vault.GetTags().OrderByDescending(t => t.Value).Take(30).Select(t => new { tag = t.Key, count = t.Value }),
            recentlyModified = notes.OrderByDescending(n => n.Modified).Take(10).Select(n => n.Path),
        };
    }

    [McpServerTool(Name = "list_folder", Title = "List folder", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists notes, files and sub-folders in a vault folder. Paths are relative to the vault root using '/' separators.")]
    public IReadOnlyList<FolderEntry> ListFolder(
        [Description("Vault-relative folder path. Empty string for the vault root.")] string path = "",
        [Description("Include everything below the folder, not just direct children.")] bool recursive = false,
        [Description("Maximum entries to return (default 500).")] int limit = 500)
        => vault.ListFolder(path, recursive, limit);

    [McpServerTool(Name = "read_note", Title = "Read note", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Reads a note's full markdown content plus its parsed frontmatter properties, tags, headings and links. " +
                 "Accepts a vault-relative path ('Projects/Plan.md'), a path without '.md', or just a note name ('Plan') which is resolved like an Obsidian [[wikilink]].")]
    public object ReadNote(
        [Description("Path or name of the note.")] string path)
    {
        var note = vault.GetNote(path);
        return new
        {
            path = note.Path,
            modified = note.Modified,
            frontmatter = note.Parsed.Frontmatter,
            tags = note.Parsed.Tags,
            headings = note.Parsed.Headings.Select(h => new string('#', h.Level) + " " + h.Text),
            links = note.Parsed.Links.Select(l => l.Target).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase),
            content = note.Content,
        };
    }

    [McpServerTool(Name = "search_notes", Title = "Search notes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Full-text search across all notes (case-insensitive). Every word must appear in the note's path or content; " +
                 "wrap a phrase in double quotes to match it exactly. Optionally restrict to a folder and/or tag. Results are ranked and include a snippet.")]
    public IReadOnlyList<SearchHit> SearchNotes(
        [Description("Search text, e.g. 'quarterly budget' or '\"exact phrase\" other'. May be empty if folder or tag is given.")] string query,
        [Description("Optional vault-relative folder to search within.")] string folder = "",
        [Description("Optional tag filter without '#'. Nested tags (tag/sub) are included.")] string tag = "",
        [Description("Maximum results (default 20).")] int limit = 20)
        => vault.Search(query, folder, tag, limit);

    [McpServerTool(Name = "list_recent_notes", Title = "Recent notes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the most recently modified notes.")]
    public object ListRecentNotes([Description("How many notes to return (default 20).")] int limit = 20)
        => vault.RecentNotes(limit).Select(n => new { path = n.Path, modified = n.Modified, tags = n.Parsed.Tags });

    [McpServerTool(Name = "list_tags", Title = "List tags", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists every tag used in the vault (frontmatter and inline #tags) with the number of notes using it.")]
    public IReadOnlyDictionary<string, int> ListTags() => vault.GetTags();

    [McpServerTool(Name = "find_notes_by_tag", Title = "Find notes by tag", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns all notes carrying a tag. Nested tags are included (searching 'project' also matches 'project/alpha').")]
    public object FindNotesByTag(
        [Description("Tag name, with or without the leading '#'.")] string tag)
    {
        var t = tag.Trim().TrimStart('#');
        return vault.GetAllNotes()
            .Where(n => VaultService.HasTag(n, t))
            .OrderBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .Take(vault.MaxResults * 4)
            .Select(n => new { path = n.Path, tags = n.Parsed.Tags, modified = n.Modified });
    }

    [McpServerTool(Name = "get_backlinks", Title = "Backlinks", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds all notes that link to the given note (via [[wikilinks]] or markdown links), with the linking lines for context.")]
    public IReadOnlyList<Backlink> GetBacklinks([Description("Path or name of the note.")] string path)
        => vault.GetBacklinks(path);

    [McpServerTool(Name = "get_outgoing_links", Title = "Outgoing links", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the links in a note and the vault path each one resolves to (resolvedPath is null for unresolved links to notes that do not exist yet).")]
    public IReadOnlyList<LinkInfo> GetOutgoingLinks([Description("Path or name of the note.")] string path)
        => vault.GetOutgoingLinks(path);

    [McpServerTool(Name = "read_daily_note", Title = "Read daily note", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Reads the daily note for a date (default: today), using the vault's Daily Notes folder and date format settings.")]
    public object ReadDailyNote(
        [Description("Date as YYYY-MM-DD. Defaults to today.")] string date = "")
    {
        var path = vault.DailyNotePath(ParseDate(date));
        try
        {
            var note = vault.GetNote(path);
            return new { path = note.Path, exists = true, content = note.Content };
        }
        catch (VaultException)
        {
            return new { path, exists = false, content = "" };
        }
    }

    internal static DateOnly ParseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return DateOnly.FromDateTime(DateTime.Now);
        if (DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        throw new VaultException($"Invalid date '{date}'. Use YYYY-MM-DD.");
    }
}
