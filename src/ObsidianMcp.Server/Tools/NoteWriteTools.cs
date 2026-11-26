using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using ObsidianMcp.Server.Records;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Server.Tools;

/// <summary>
/// Generic note writes. Notes that are Client / Matter / Work Product records are routed through the
/// record service so these tools can never bypass schema validation or deadline protection.
/// </summary>
[McpServerToolType]
public sealed class NoteWriteTools(VaultService vault, RecordService records)
{
    private const string VersionDesc = "Optional 'version' from get_note; the write fails if the note changed since (e.g. edited in Obsidian or synced).";
    private const string DryRunDesc = "Preview the change (diff) without writing.";

    private Record? RecordAt(string rel) =>
        records.CollectionOf(rel) is null ? null : records.Snapshot().ByCollection.Values.SelectMany(r => r).FirstOrDefault(r => r.Path == rel);

    [McpServerTool(Name = "create_note", Title = "Create note", Destructive = false, OpenWorld = false)]
    [Description("Creates a general markdown note (not a Client/Matter/Work Product record: use the create_* record tools for those). " +
                 "Optionally starts from a note in the Templates folder.")]
    public async Task<ChangeResult> CreateNote(
        [Description("Vault-relative path, e.g. 'Knowledge/Research/Means-plus-function.md'.")] string path,
        [Description("Markdown content (optionally with YAML frontmatter). Appended after the template body when a template is used.")] string content = "",
        [Description("Optional template name or path in the Templates folder.")] string template = "",
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var rel = vault.NormalizeNotePath(path);
        if (records.CollectionOf(rel) is { } c)
            throw new VaultException($"'{rel}' is in the {records.Collection(c).Label} collection. Use create_{c} so the record is validated.");
        var text = content;
        if (!string.IsNullOrWhiteSpace(template))
        {
            var t = vault.TryGetNote(template) ?? vault.TryGetNote($"{records.Schema.TemplatesFolder}/{template}")
                    ?? throw new VaultException($"Template not found: '{template}'.");
            var filled = t.Content.Replace("{{title}}", Path.GetFileNameWithoutExtension(rel)).Replace("{{date}}", RecordService.Today());
            text = string.IsNullOrWhiteSpace(content) ? filled : filled.TrimEnd() + "\n\n" + content;
        }
        return await vault.CreateNote(rel, text, new WriteOptions { DryRun = dry_run });
    }

    [McpServerTool(Name = "append_to_note", Title = "Append to note", Destructive = false, OpenWorld = false)]
    [Description("Appends markdown to the end of a note, or to the end of a heading's section (the heading is created if missing). " +
                 "Never alters existing content or properties. Creates general notes that do not exist yet.")]
    public async Task<ChangeResult> AppendToNote(
        [Description("Note path or name.")] string path,
        [Description("Markdown to append.")] string content,
        [Description("Optional heading (without #).")] string heading = "",
        [Description(VersionDesc)] string expected_version = "",
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var rel = vault.ResolveExistingOrNew(path);
        if (records.CollectionOf(rel) is { } c && vault.TryGetNote(rel) is null)
            throw new VaultException($"'{rel}' would create a new {records.Collection(c).Label} record. Use create_{c} instead.");
        return await vault.AppendToNote(rel, content, createIfMissing: true, heading,
            new WriteOptions { DryRun = dry_run, ExpectedVersion = NullIfEmpty(expected_version) });
    }

    [McpServerTool(Name = "update_note", Title = "Update note text", Destructive = true, OpenWorld = false)]
    [Description("Edits a note by replacing an exact piece of text (copy it from get_note). old_text must be unique unless replace_all=true; " +
                 "empty new_text deletes it. For record notes the properties block cannot be edited here; use the record tools or update_note_properties.")]
    public Task<ChangeResult> UpdateNote(
        [Description("Note path or name.")] string path,
        [Description("Exact existing text.")] string old_text,
        [Description("Replacement text.")] string new_text,
        [Description("Replace every occurrence.")] bool replace_all = false,
        [Description(VersionDesc)] string expected_version = "",
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var note = vault.GetNote(path);
        var opts = new WriteOptions { DryRun = dry_run, ExpectedVersion = NullIfEmpty(expected_version) };
        if (RecordAt(note.Path) is { } record)
        {
            return records.UpdateRecord(record.Collection, record.Id, null, null, opts, "update_note", transformBody: body =>
            {
                var updated = VaultService.ReplaceText(body, old_text, new_text, replace_all, note.Path);
                if (NoteParser.SplitFrontmatter(updated).Yaml is not null)
                    throw new VaultException("Record properties cannot be edited with update_note.");
                return updated;
            });
        }
        return vault.WriteAsync(async () =>
        {
            var before = vault.ReadCurrent(note.Path) ?? throw new VaultException($"Note not found: '{note.Path}'");
            return await vault.CommitAsync(note.Path, before, VaultService.ReplaceText(before, old_text, new_text, replace_all, note.Path), "update_note", opts);
        });
    }

    [McpServerTool(Name = "update_note_properties", Title = "Update note properties", Destructive = true, OpenWorld = false)]
    [Description("Sets and/or removes properties (YAML frontmatter) without touching the body. For Client/Matter/Work Product records the " +
                 "values are validated against the schema, identifiers cannot change, and protected properties (deadlines) are refused.")]
    public Task<ChangeResult> UpdateNoteProperties(
        [Description("Note path or name.")] string path,
        [Description("Properties to set, e.g. {\"status\": \"In Progress\", \"tags\": [\"oa\", \"urgent\"]}.")] Dictionary<string, JsonElement>? set = null,
        [Description("Property names to remove.")] string[]? remove = null,
        [Description(VersionDesc)] string expected_version = "",
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var note = vault.GetNote(path);
        var opts = new WriteOptions { DryRun = dry_run, ExpectedVersion = NullIfEmpty(expected_version) };
        var values = (set ?? []).ToDictionary(kv => kv.Key, kv => NoteParser.FromJson(kv.Value));
        if (RecordAt(note.Path) is { } record)
            return records.UpdateRecord(record.Collection, record.Id, values, remove, opts, "update_note_properties");
        return vault.WriteAsync(async () =>
        {
            var before = vault.ReadCurrent(note.Path) ?? throw new VaultException($"Note not found: '{note.Path}'");
            var (after, _) = VaultService.WithProperties(before, values, remove, note.Path);
            return await vault.CommitAsync(note.Path, before, after, "update_note_properties", opts);
        });
    }

    [McpServerTool(Name = "move_note", Title = "Move or rename note", Destructive = true, OpenWorld = false)]
    [Description("Moves and/or renames a general note and updates links to it in other notes. Records (clients, matters, work products) cannot be moved.")]
    public async Task<object> MoveNote(
        [Description("Current path or name.")] string from,
        [Description("New path, or a folder ending in '/' to keep the name.")] string to,
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var note = vault.GetNote(from);
        if (records.CollectionOf(note.Path) is not null || records.CollectionOf(vault.NormalizeNotePath(to.EndsWith('/') ? to + "x" : to)) is not null)
            throw new VaultException("Record notes are managed by the server and cannot be moved into or out of record folders.");
        var (newPath, changes) = await vault.MoveNote(note.Path, to, updateLinks: true, new WriteOptions { DryRun = dry_run });
        return new { new_path = newPath, dry_run, changes };
    }

    [McpServerTool(Name = "archive_note", Title = "Archive note", Destructive = false, OpenWorld = false)]
    [Description("Archives without deleting. Records get their status set to the archived status; other notes are moved to the Archive folder " +
                 "(links are updated). Nothing is ever permanently deleted by this server.")]
    public async Task<object> ArchiveNote(
        [Description("Note path or name.")] string path,
        [Description("Why it is being archived (recorded in the audit log).")] string reason = "",
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var note = vault.GetNote(path);
        var opts = new WriteOptions { DryRun = dry_run, Reason = NullIfEmpty(reason) };
        if (RecordAt(note.Path) is { } record)
        {
            var c = records.Collection(record.Collection);
            if (c.ArchivedStatus is null) throw new VaultException($"No archived status is configured for {c.Label} records.");
            return await records.UpdateRecord(record.Collection, record.Id,
                new Dictionary<string, object?> { [c.StatusProperty] = c.ArchivedStatus }, null, opts, "archive");
        }
        var dest = $"{vault.ArchiveFolder.Trim('/')}/{note.Path}";
        var (newPath, changes) = await vault.MoveNote(note.Path, dest, updateLinks: true, opts);
        return new { new_path = newPath, dry_run, changes };
    }

    internal static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
