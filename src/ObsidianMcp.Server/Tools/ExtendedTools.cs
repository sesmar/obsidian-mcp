using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ObsidianMcp.Server.Records;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Server.Tools;

/// <summary>
/// Read tools beyond the 25-tool MVP. Exposed only with --extended-tools, for when a demonstrated need arises.
/// </summary>
[McpServerToolType]
public sealed class ExtendedReadTools(VaultService vault, RecordService records, DocumentReader documents)
{
    [McpServerTool(Name = "list_folder", Title = "List folder", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists notes, attachments and sub-folders in a vault folder (vault-relative path with '/' separators).")]
    public IReadOnlyList<FolderEntry> ListFolder(
        [Description("Folder path; empty for the vault root.")] string path = "",
        [Description("Include everything below the folder.")] bool recursive = false,
        [Description("Only list folders.")] bool folders_only = false,
        [Description("Maximum entries (default 500).")] int limit = 500)
        => vault.ListFolder(path, recursive, limit, folders_only);

    [McpServerTool(Name = "get_note_links", Title = "Note links", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns a note's backlinks (including frontmatter relationships) and its outgoing links with resolved paths " +
                 "(resolved_path is null for links to notes that do not exist).")]
    public object GetNoteLinks([Description("Note path or name.")] string path) => new
    {
        backlinks = vault.GetBacklinks(path),
        outgoing = vault.GetOutgoingLinks(path),
    };

    [McpServerTool(Name = "get_attachment", Title = "Get attachment", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieves an attachment (PDF, DOCX, text or image): metadata, SHA-256 checksum and extracted text (paged with offset/max_chars). " +
                 "Images are returned as images. Accepts a path (as listed in a matter's attachments) or a file name.")]
    public CallToolResult GetAttachment(
        [Description("Attachment path or file name.")] string path,
        [Description("Character offset into the extracted text.")] int offset = 0,
        [Description("Maximum characters of text (default 40000).")] int max_chars = 40000,
        [Description("Only return metadata and checksum.")] bool metadata_only = false)
    {
        var rel = documents.Resolve(path);
        var info = documents.Info(rel);
        var result = new CallToolResult { Content = [] };
        if (!metadata_only && DocumentReader.IsImage(info.Extension))
        {
            if (info.Size > DocumentReader.MaxImageBytes) throw new VaultException($"Image is too large to return ({info.Size:N0} bytes).");
            result.Content.Add(new TextContentBlock { Text = JsonSerializer.Serialize(info, McpBuilderExtensions.ToolJson) });
            result.Content.Add(ImageContentBlock.FromBytes(documents.ReadBytes(rel), info.MimeType));
            return result;
        }
        var text = metadata_only ? null : documents.ExtractText(rel);
        var start = Math.Clamp(offset, 0, text?.Length ?? 0);
        var len = Math.Clamp(max_chars, 1000, 200_000);
        var slice = text?.Substring(start, Math.Min(len, text.Length - start));
        result.Content.Add(new TextContentBlock
        {
            Text = JsonSerializer.Serialize(new
            {
                info,
                content_notice = ContextService.ContentNotice,
                text_supported = metadata_only || text is not null,
                total_chars = text?.Length,
                offset = start,
                next_offset = text is not null && start + slice!.Length < text.Length ? start + slice.Length : (int?)null,
                text = slice,
            }, McpBuilderExtensions.ToolJson),
        });
        return result;
    }

    [McpServerTool(Name = "search_knowledge", Title = "Search knowledge base", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the knowledge base by category (case law, legal authorities, prosecution strategies, argument library, firm procedures, " +
                 "research) and returns citation and verification properties. Stored summaries are NOT verified authority.")]
    public object SearchKnowledge(
        [Description("Search text.")] string query,
        [Description("Optional category; see get_vault_info for the configured categories.")] string category = "",
        [Description("Optional required tags.")] string[]? tags = null,
        [Description("Maximum results (default 20).")] int limit = 20)
    {
        var folders = records.Schema.KnowledgeFolders;
        IEnumerable<KeyValuePair<string, string>> scope = category.Length == 0 ? folders
            : folders.TryGetValue(category, out var f) ? [new(category, f)]
            : throw new VaultException($"Unknown category '{category}'. Available: {string.Join(", ", folders.Keys)}.");
        var hits = new List<KnowledgeHit>();
        foreach (var (cat, folder) in scope)
        {
            IReadOnlyList<SearchHit> found;
            try { found = vault.Search(new VaultService.SearchQuery { Text = query, Folder = folder, Tags = tags, Limit = limit }); }
            catch (VaultException) { continue; } // folder does not exist yet
            foreach (var h in found)
            {
                var note = vault.GetNote(h.Path);
                var citation = records.Schema.CitationProperties.Where(note.Properties.ContainsKey).ToDictionary(p => p, p => note.Properties[p]);
                hits.Add(new KnowledgeHit(cat, h.Path, h.Title, h.Score, h.Snippet, h.Tags, citation, note.Properties.ContainsKey("verified_date")));
            }
        }
        return new
        {
            verification_note = "Summaries stored in the vault are not independently verified legal authority. Confirm against the cited source before relying on them.",
            results = hits.OrderByDescending(h => h.Score).Take(Math.Clamp(limit, 1, 100)),
        };
    }
}

/// <summary>Write tools beyond the 25-tool MVP. Exposed only with --extended-tools (and never in read-only mode).</summary>
[McpServerToolType]
public sealed class ExtendedWriteTools(VaultService vault, RecordService records)
{
    [McpServerTool(Name = "create_client", Title = "Create client", Destructive = false, OpenWorld = false)]
    [Description("Creates a client record from the client template. Requires client_code and client_name.")]
    public Task<ChangeResult> CreateClient(
        [Description("Properties, e.g. {\"client_code\": \"DEMO\", \"client_name\": \"Demo Robotics Inc.\"}.")] Dictionary<string, JsonElement> properties,
        [Description("Optional markdown body.")] string content = "",
        [Description("Preview without writing.")] bool dry_run = false)
        => records.CreateRecord(RecordSchema.Client, null, properties, ToolHelpers.NullIfEmpty(content), new WriteOptions { DryRun = dry_run });

    [McpServerTool(Name = "update_client", Title = "Update client", Destructive = true, OpenWorld = false)]
    [Description("Updates client properties (validated; the client code cannot change).")]
    public Task<ChangeResult> UpdateClient(
        [Description("Client code.")] string client,
        [Description("Properties to set.")] Dictionary<string, JsonElement>? set_properties = null,
        [Description("Property names to remove.")] string[]? remove_properties = null,
        [Description("Optional 'version' from a previous read.")] string expected_version = "",
        [Description("Preview without writing.")] bool dry_run = false)
        => records.UpdateRecord(RecordSchema.Client, client, (set_properties ?? []).ToDictionary(kv => kv.Key, kv => NoteParser.FromJson(kv.Value)),
            remove_properties, ToolHelpers.Opts(dry_run, expected_version), "update_client");

    [McpServerTool(Name = "move_note", Title = "Move or rename note", Destructive = true, OpenWorld = false)]
    [Description("Moves and/or renames a general note and updates links to it. Records cannot be moved.")]
    public async Task<object> MoveNote(
        [Description("Current path or name.")] string from,
        [Description("New path, or a folder ending in '/' to keep the name.")] string to,
        [Description("Preview without writing.")] bool dry_run = false)
    {
        var note = vault.GetNote(from);
        if (records.CollectionOf(note.Path) is not null || records.CollectionOf(vault.NormalizeNotePath(to.EndsWith('/') ? to + "x" : to)) is not null)
            throw new VaultException("Record notes are managed by the server and cannot be moved into or out of record folders.");
        var (newPath, changes) = await vault.MoveNote(note.Path, to, updateLinks: true, new WriteOptions { DryRun = dry_run });
        return new { new_path = newPath, dry_run, changes };
    }

    [McpServerTool(Name = "archive_note", Title = "Archive note", Destructive = false, OpenWorld = false)]
    [Description("Archives without deleting: records get the archived status; other notes move to the Archive folder (links updated).")]
    public async Task<object> ArchiveNote(
        [Description("Note path or name.")] string path,
        [Description("Why it is being archived (audit log).")] string reason = "",
        [Description("Preview without writing.")] bool dry_run = false)
    {
        var note = vault.GetNote(path);
        var opts = ToolHelpers.Opts(dry_run, null, reason);
        if (RecordSet.ToRecord(records.Schema, note) is { } record)
        {
            var c = records.Collection(record.Collection);
            if (c.ArchivedStatus is null) throw new VaultException($"No archived status is configured for {c.Label} records.");
            return await records.UpdateRecord(record.Collection, record.Id,
                new Dictionary<string, object?> { [c.StatusProperty] = c.ArchivedStatus }, null, opts, "archive");
        }
        var (newPath, changes) = await vault.MoveNote(note.Path, $"{vault.ArchiveFolder.Trim('/')}/{note.Path}", updateLinks: true, opts);
        return new { new_path = newPath, dry_run, changes };
    }
}

public sealed record KnowledgeHit(string Category, string Path, string Title, double Score, string Snippet,
    IReadOnlyList<string> Tags, Dictionary<string, object?> Citation, bool Verified);
