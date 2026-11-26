using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ObsidianMcp.Server.Records;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Server.Tools;

/// <summary>General read-only vault tools.</summary>
[McpServerToolType]
public sealed class VaultTools(VaultService vault, RecordService records, DocumentReader documents)
{
    [McpServerTool(Name = "get_vault_info", Title = "Vault info", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Overview of the vault: record counts per collection (clients, matters, work products), top-level folders, " +
                 "knowledge-base categories, top tags and whether writes are allowed. Call this first.")]
    public object GetVaultInfo()
    {
        var set = records.Snapshot();
        return new
        {
            name = vault.VaultName,
            read_only = vault.IsReadOnly,
            note_count = set.Notes.Count,
            collections = records.Schema.Collections.ToDictionary(c => c.Key, c => new { folder = c.Value.Folder, count = set.All(c.Key).Count }),
            knowledge_categories = records.Schema.KnowledgeFolders,
            workflows_folder = records.Schema.WorkflowsFolder,
            templates_folder = records.Schema.TemplatesFolder,
            top_level = vault.ListFolder("", recursive: false, limit: 200).Select(e => e.Path),
            top_tags = vault.GetTags().OrderByDescending(t => t.Value).Take(25).ToDictionary(t => t.Key, t => t.Value),
            schema_source = records.SchemaSource,
        };
    }

    [McpServerTool(Name = "list_folder", Title = "List folder", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists notes, attachments and sub-folders in a vault folder (vault-relative path with '/' separators).")]
    public IReadOnlyList<FolderEntry> ListFolder(
        [Description("Folder path; empty for the vault root.")] string path = "",
        [Description("Include everything below the folder.")] bool recursive = false,
        [Description("Only list folders.")] bool folders_only = false,
        [Description("Maximum entries (default 500).")] int limit = 500)
        => vault.ListFolder(path, recursive, limit, folders_only);

    [McpServerTool(Name = "get_note", Title = "Get note", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieves a note: properties (YAML frontmatter), tags, headings, links and markdown content, plus a 'version' to pass as " +
                 "expected_version when editing. Accepts a path or just a note name. Set properties_only=true to skip the body.")]
    public object GetNote(
        [Description("Note path or name.")] string path,
        [Description("Return only metadata, not the content.")] bool properties_only = false)
    {
        var note = vault.GetNote(path);
        var record = RecordSet.ToRecord(records.Schema, note);
        return new
        {
            path = note.Path,
            version = note.Version,
            modified = note.Modified,
            record = record is null ? null : new { collection = record.Collection, id = record.Id },
            properties = note.Properties,
            tags = note.Parsed.Tags,
            headings = properties_only ? null : note.Parsed.Headings.Select(h => new string('#', h.Level) + " " + h.Text),
            links = properties_only ? null : note.Parsed.Links.Select(l => l.Target).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase),
            content = properties_only ? null : note.Content,
        };
    }

    [McpServerTool(Name = "search_notes", Title = "Search notes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Full-text search with optional filters. All words must match (use \"quotes\" for phrases). Filters: folder, tags (all must match, " +
                 "nested tags included), properties (exact match or list membership; '*' = has a value, '!' = missing) and modified_within_days. " +
                 "The query may be empty when a filter is given; results are then the most recently modified matches.")]
    public IReadOnlyList<SearchHit> SearchNotes(
        [Description("Search text.")] string query = "",
        [Description("Restrict to this folder.")] string folder = "",
        [Description("Required tags, without '#'.")] string[]? tags = null,
        [Description("Property filters, e.g. {\"status\": \"Active\", \"examiner\": \"*\"}.")] Dictionary<string, string>? properties = null,
        [Description("Only notes modified in the last N days.")] int modified_within_days = 0,
        [Description("Maximum results (default 20).")] int limit = 20)
        => vault.Search(new VaultService.SearchQuery
        {
            Text = query, Folder = folder, Tags = tags, Properties = properties,
            ModifiedWithinDays = modified_within_days, Limit = limit,
        });

    [McpServerTool(Name = "get_note_links", Title = "Note links", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns a note's backlinks (notes linking to it, including frontmatter relationships) and its outgoing links with resolved paths " +
                 "(resolved_path is null for links to notes that do not exist).")]
    public object GetNoteLinks([Description("Note path or name.")] string path) => new
    {
        backlinks = vault.GetBacklinks(path),
        outgoing = vault.GetOutgoingLinks(path),
    };

    [McpServerTool(Name = "get_attachment", Title = "Get attachment", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieves an attachment (PDF, DOCX, text or image) stored in the vault: metadata, SHA-256 checksum and extracted text " +
                 "(paged with offset/max_chars). Images are returned as images. Accepts a path or a file name.")]
    public CallToolResult GetAttachment(
        [Description("Attachment path or file name, e.g. 'Matters/DEMO/DEMO-101-A/Office Action 2026-10-01.pdf'.")] string path,
        [Description("Character offset into the extracted text.")] int offset = 0,
        [Description("Maximum characters of text to return (default 40000).")] int max_chars = 40000,
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

        string? text = metadata_only ? null : documents.ExtractText(rel);
        var start = Math.Clamp(offset, 0, text?.Length ?? 0);
        var len = Math.Clamp(max_chars, 1000, 200_000);
        var slice = text is null ? null : text.Substring(start, Math.Min(len, text.Length - start));
        result.Content.Add(new TextContentBlock
        {
            Text = JsonSerializer.Serialize(new
            {
                info,
                content_notice = ContextService.ContentNotice,
                text_supported = metadata_only || text is not null,
                total_chars = text?.Length,
                offset = start,
                next_offset = text is not null && start + (slice?.Length ?? 0) < text.Length ? start + slice!.Length : (int?)null,
                text = slice,
            }, McpBuilderExtensions.ToolJson),
        });
        return result;
    }
}
