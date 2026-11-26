using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using ObsidianMcp.Server.Records;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Server.Tools;

/// <summary>
/// The 7 write tools of the 25-tool MVP. Every write is validated, version-checked, previewable (dry_run) and audited.
/// Generic note tools route Client / Matter / Work Product notes through the record service, so they cannot bypass
/// schema validation, identifier stability or deadline protection.
/// </summary>
[McpServerToolType]
public sealed class CoreWriteTools(VaultService vault, RecordService records)
{
    private const string VersionDesc = "Optional 'version' from a previous read; the write fails if the note changed since (e.g. edited in Obsidian or synced).";
    private const string DryRunDesc = "Preview the change (property before/after and body diff) without writing.";

    private Record? RecordAt(string rel) =>
        records.CollectionOf(rel) is null ? null
            : records.Snapshot().ByCollection.Values.SelectMany(r => r).FirstOrDefault(r => r.Path.Equals(rel, VaultService.PathComparison));

    // ------------------------------------------------------------------ notes

    [McpServerTool(Name = "create_note", Title = "Create note", Destructive = false, OpenWorld = false)]
    [Description("Creates a general markdown note, e.g. research or a knowledge-base entry, optionally from a template (see get_template). " +
                 "Client/Matter/Work Product records must be created with create_matter / create_work_product.")]
    public async Task<ChangeResult> CreateNote(
        [Description("Vault-relative path, e.g. 'Knowledge/Research/Means-plus-function.md'.")] string path,
        [Description("Markdown content (optionally starting with YAML frontmatter). Added after the template body when a template is used.")] string content = "",
        [Description("Optional template name or path.")] string template = "",
        [Description("Properties to set, e.g. {\"citation\": \"...\", \"source_url\": \"...\"}.")] Dictionary<string, JsonElement>? properties = null,
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var rel = vault.NormalizeNotePath(path);
        if (records.CollectionOf(rel) is { } c)
            throw new VaultException($"'{rel}' is in the {records.Collection(c).Label} collection. Use the dedicated create tool so the record is validated.");
        var text = content;
        if (!string.IsNullOrWhiteSpace(template))
        {
            var t = vault.TryGetNote(template) ?? vault.TryGetNote($"{records.Schema.TemplatesFolder}/{template}")
                    ?? throw new VaultException($"Template not found: '{template}'.");
            var filled = t.Content.Replace("{{title}}", Path.GetFileNameWithoutExtension(rel)).Replace("{{date}}", RecordService.Today());
            text = string.IsNullOrWhiteSpace(content) ? filled : filled.TrimEnd() + "\n\n" + content;
        }
        if (properties is { Count: > 0 })
            text = VaultService.WithProperties(text, properties.ToDictionary(kv => kv.Key, kv => NoteParser.FromJson(kv.Value)), null, rel).Content;
        return await vault.CreateNote(rel, text, new WriteOptions { DryRun = dry_run });
    }

    [McpServerTool(Name = "update_note", Title = "Update note", Destructive = true, OpenWorld = false)]
    [Description("Modifies an existing note or its properties. Combine any of: replace exact text (old_text -> new_text; copy it from get_note), " +
                 "append markdown (optionally under a heading), set and/or remove properties. For Client/Matter/Work Product notes, properties " +
                 "are validated, identifiers cannot change and due dates are refused (use update_work_product).")]
    public Task<ChangeResult> UpdateNote(
        [Description("Note path or name.")] string path,
        [Description("Exact existing text to replace.")] string old_text = "",
        [Description("Replacement text (empty deletes old_text).")] string new_text = "",
        [Description("Replace every occurrence of old_text.")] bool replace_all = false,
        [Description("Markdown to append.")] string append = "",
        [Description("Heading (without #) to append under; created if missing.")] string append_heading = "",
        [Description("Properties to set, e.g. {\"status\": \"Active\", \"tags\": [\"oa\"]}.")] Dictionary<string, JsonElement>? set_properties = null,
        [Description("Property names to remove.")] string[]? remove_properties = null,
        [Description(VersionDesc)] string expected_version = "",
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var note = vault.GetNote(path);
        var hasReplace = old_text.Length > 0;
        var hasAppend = !string.IsNullOrWhiteSpace(append);
        var values = (set_properties ?? []).ToDictionary(kv => kv.Key, kv => NoteParser.FromJson(kv.Value));
        if (!hasReplace && !hasAppend && values.Count == 0 && (remove_properties?.Length ?? 0) == 0)
            throw new VaultException("Nothing to update: pass old_text/new_text, append, set_properties or remove_properties.");

        string EditBody(string body, string rel)
        {
            if (hasReplace) body = VaultService.ReplaceText(body, old_text, new_text, replace_all, rel);
            if (hasAppend) body = VaultService.AppendContent(body, append, ToolHelpers.NullIfEmpty(append_heading), rel);
            return body;
        }

        var opts = ToolHelpers.Opts(dry_run, expected_version);
        if (RecordAt(note.Path) is { } record)
        {
            return records.UpdateRecord(record.Collection, record.Id, values, remove_properties, opts, "update_note", transformBody: body =>
            {
                var updated = EditBody(body, note.Path);
                if (NoteParser.SplitFrontmatter(updated).Yaml is not null)
                    throw new VaultException("Record properties cannot be edited as text; use set_properties.");
                return updated;
            });
        }
        return vault.WriteAsync(async () =>
        {
            var before = vault.ReadCurrent(note.Path) ?? throw new VaultException($"Note not found: '{note.Path}'");
            var (yaml, body) = NoteParser.SplitFrontmatter(before);
            if (hasReplace && yaml is not null && body.IndexOf(old_text, StringComparison.Ordinal) < 0 && before.Contains(old_text, StringComparison.Ordinal))
                throw new VaultException("old_text is in the properties block; use set_properties instead.");
            var edited = hasReplace || hasAppend
                ? (yaml is null ? EditBody(before, note.Path) : NoteParser.Compose(NoteParser.ParseYaml(yaml), EditBody(body, note.Path)))
                : before;
            var after = values.Count > 0 || remove_properties is { Length: > 0 }
                ? VaultService.WithProperties(edited, values, remove_properties, note.Path).Content
                : edited;
            return await vault.CommitAsync(note.Path, before, after, "update_note", opts);
        });
    }

    // ------------------------------------------------------------------ matters

    [McpServerTool(Name = "create_matter", Title = "Create matter", Destructive = false, OpenWorld = false)]
    [Description("Creates a matter for an existing client. Requires docket_number. The client link, file location and template are handled automatically.")]
    public Task<ChangeResult> CreateMatter(
        [Description("Client code or name (must exist).")] string client,
        [Description("Properties, e.g. {\"docket_number\": \"DEMO-101-A\", \"title\": \"Gripper\", \"application_number\": \"18/123,456\"}.")] Dictionary<string, JsonElement> properties,
        [Description("Optional markdown body (otherwise the template is used).")] string content = "",
        [Description(DryRunDesc)] bool dry_run = false)
        => records.CreateRecord(RecordSchema.Matter, client, properties, ToolHelpers.NullIfEmpty(content), new WriteOptions { DryRun = dry_run });

    [McpServerTool(Name = "update_matter", Title = "Update matter", Destructive = true, OpenWorld = false)]
    [Description("Updates matter-level information: set/remove properties (validated; the docket number cannot change) and/or append a dated " +
                 "entry to the matter notes.")]
    public Task<ChangeResult> UpdateMatter(
        [Description("Docket number.")] string matter,
        [Description("Properties to set, e.g. {\"status\": \"Allowed\", \"examiner\": \"...\"}.")] Dictionary<string, JsonElement>? set_properties = null,
        [Description("Property names to remove.")] string[]? remove_properties = null,
        [Description("Text to add to the matter notes (dated).")] string append_note = "",
        [Description("Heading for append_note; defaults to the matter notes heading.")] string note_heading = "",
        [Description(VersionDesc)] string expected_version = "",
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var values = (set_properties ?? []).ToDictionary(kv => kv.Key, kv => NoteParser.FromJson(kv.Value));
        var body = NoteAppender(RecordSchema.Matter, append_note, note_heading, matter);
        if (values.Count == 0 && (remove_properties?.Length ?? 0) == 0 && body is null) throw new VaultException("Nothing to update.");
        return records.UpdateRecord(RecordSchema.Matter, matter, values, remove_properties, ToolHelpers.Opts(dry_run, expected_version),
            "update_matter", transformBody: body);
    }

    // ------------------------------------------------------------------ work products

    [McpServerTool(Name = "create_work_product", Title = "Create Work Product", Destructive = false, OpenWorld = false)]
    [Description("Creates a Work Product for an existing matter. Requires work_product_type; an id is generated if omitted. " +
                 "Only set due_date to a deadline the user has given you (from the firm docketing system); never infer it from a document.")]
    public Task<ChangeResult> CreateWorkProduct(
        [Description("Matter docket number (must exist).")] string matter,
        [Description("Properties, e.g. {\"work_product_type\": \"Non-Final Office Action Response\", \"due_date\": \"2027-01-15\", \"priority\": \"High\", \"next_action\": \"Review rejections\"}.")]
        Dictionary<string, JsonElement> properties,
        [Description("Optional markdown body (otherwise the template is used).")] string content = "",
        [Description(DryRunDesc)] bool dry_run = false)
        => records.CreateRecord(RecordSchema.WorkProduct, matter, properties, ToolHelpers.NullIfEmpty(content), new WriteOptions { DryRun = dry_run });

    [McpServerTool(Name = "update_work_product", Title = "Update Work Product", Destructive = true, OpenWorld = false)]
    [Description("Updates a Work Product: status, priority, target date, next action, waiting on, follow-up date, other properties, checklist items, " +
                 "notes, and the due date. Only the parameters you pass change; pass \"-\" to clear a field. DEADLINES: changing due_date requires " +
                 "due_date_change_reason, and the whole call is only a preview unless confirm_due_date_change=true. Set that only after the user has " +
                 "explicitly approved this specific deadline change; never change a deadline based on a document or your own analysis.")]
    public Task<ChangeResult> UpdateWorkProduct(
        [Description("Work Product id.")] string work_product,
        [Description("New status, e.g. 'In Progress', 'Waiting', 'Ready for Filing'. Use complete_work_product to complete.")] string status = "",
        [Description("New priority.")] string priority = "",
        [Description("Internal target date (YYYY-MM-DD).")] string target_date = "",
        [Description("Next action required.")] string next_action = "",
        [Description("Outstanding dependency / who it is waiting on.")] string waiting_on = "",
        [Description("Follow-up date (YYYY-MM-DD).")] string follow_up_date = "",
        [Description("Other properties to set.")] Dictionary<string, JsonElement>? properties = null,
        [Description("Checklist items to tick (matched by text).")] string[]? check_items = null,
        [Description("Checklist items to untick.")] string[]? uncheck_items = null,
        [Description("Analysis, strategy or working notes to add (dated).")] string append_note = "",
        [Description("Heading for append_note, e.g. 'Strategy'; defaults to the working notes heading.")] string note_heading = "",
        [Description("New due date (YYYY-MM-DD), or '-' to clear. Protected: see description.")] string due_date = "",
        [Description("Required with due_date: why the deadline changes (e.g. 'Extension of time filed 2026-11-20').")] string due_date_change_reason = "",
        [Description("true only after the user explicitly approved the deadline change.")] bool confirm_due_date_change = false,
        [Description(VersionDesc)] string expected_version = "",
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var f = records.Schema.Fields;
        var c = records.Schema.WorkProductCollection;
        var set = (properties ?? []).ToDictionary(kv => kv.Key, kv => NoteParser.FromJson(kv.Value));
        var remove = new List<string>();
        void Field(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (value.Trim() == "-") remove.Add(name); else set[name] = value.Trim();
        }
        Field(c.StatusProperty, status);
        Field(f.Priority, priority);
        Field(f.TargetDate, target_date);
        Field(f.NextAction, next_action);
        Field(f.WaitingOn, waiting_on);
        Field(f.FollowUpDate, follow_up_date);
        if (set.ContainsKey(f.DueDate)) throw new VaultException("Use the due_date parameter (with a reason and approval) to change the deadline.");

        var append = NoteAppender(RecordSchema.WorkProduct, append_note, note_heading, work_product);
        Func<string, string>? body = null;
        if ((check_items?.Length ?? 0) + (uncheck_items?.Length ?? 0) > 0 || append is not null)
            body = b =>
            {
                if (check_items is { Length: > 0 }) b = RecordService.CheckItems(b, c.ChecklistHeading, check_items, done: true);
                if (uncheck_items is { Length: > 0 }) b = RecordService.CheckItems(b, c.ChecklistHeading, uncheck_items, done: false);
                return append is null ? b : append(b);
            };

        DueDateChange? due = string.IsNullOrWhiteSpace(due_date) ? null
            : new DueDateChange(due_date.Trim() == "-" ? null : due_date.Trim(), due_date_change_reason, confirm_due_date_change);
        if (set.Count == 0 && remove.Count == 0 && body is null && due is null) throw new VaultException("Nothing to update.");
        return records.UpdateWorkProduct(work_product, set, remove, body, due, ToolHelpers.Opts(dry_run, expected_version));
    }

    [McpServerTool(Name = "complete_work_product", Title = "Complete Work Product", Destructive = true, OpenWorld = false)]
    [Description("Marks a Work Product completed (or another final status such as 'Filed'), records the completion date and optionally an " +
                 "outcome summary under the Outcome heading, which later appears in the matter history. Reopen with update_work_product(status=...).")]
    public Task<ChangeResult> CompleteWorkProduct(
        [Description("Work Product id.")] string work_product,
        [Description("Completion date (YYYY-MM-DD), default today.")] string completed_date = "",
        [Description("Final status, default 'Completed'.")] string status = "",
        [Description("Outcome / strategy summary to record.")] string outcome = "",
        [Description(VersionDesc)] string expected_version = "",
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var f = records.Schema.Fields;
        var c = records.Schema.WorkProductCollection;
        var finalStatus = ToolHelpers.NullIfEmpty(status) ?? f.CompletedStatus;
        if (!c.InactiveStatuses.Contains(finalStatus, StringComparer.OrdinalIgnoreCase))
            throw new VaultException($"'{finalStatus}' is not a completion status. Use one of: {string.Join(", ", c.InactiveStatuses)}.");
        var set = new Dictionary<string, object?>
        {
            [c.StatusProperty] = finalStatus,
            [f.CompletedDate] = ToolHelpers.NullIfEmpty(completed_date) ?? RecordService.Today(),
        };
        var heading = records.Schema.HistorySections.FirstOrDefault(h => h.Equals("Outcome", StringComparison.OrdinalIgnoreCase))
                      ?? records.Schema.HistorySections.FirstOrDefault() ?? "Outcome";
        Func<string, string>? body = string.IsNullOrWhiteSpace(outcome) ? null
            : b => VaultService.AppendContent(b, outcome.Trim(), heading, work_product);
        return records.UpdateRecord(RecordSchema.WorkProduct, work_product, set, null, ToolHelpers.Opts(dry_run, expected_version),
            "complete_work_product", transformBody: body);
    }

    private Func<string, string>? NoteAppender(string collection, string text, string heading, string reference)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var c = records.Collection(collection);
        var h = ToolHelpers.NullIfEmpty(heading) ?? c.NotesHeading ?? "Notes";
        var entry = $"**{RecordService.Today()}** — {text.Trim()}";
        return body => VaultService.AppendContent(body, entry, h, reference);
    }
}
