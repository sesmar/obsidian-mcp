using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using ObsidianMcp.Server.Records;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Server.Tools;

/// <summary>Controlled, validated and audited writes to Client / Matter / Work Product records.</summary>
[McpServerToolType]
public sealed class RecordWriteTools(RecordService records)
{
    private const string VersionDesc = "Optional 'version' from a previous read; fails if the record changed since.";
    private const string DryRunDesc = "Preview the change without writing.";

    [McpServerTool(Name = "create_client", Title = "Create client", Destructive = false, OpenWorld = false)]
    [Description("Creates a client record from the client template. Requires client_code and client_name. See get_schema for properties.")]
    public Task<ChangeResult> CreateClient(
        [Description("Properties, e.g. {\"client_code\": \"DEMO\", \"client_name\": \"Demo Robotics Inc.\", \"status\": \"Active\"}.")] Dictionary<string, JsonElement> properties,
        [Description("Optional markdown body (otherwise the template body is used).")] string content = "",
        [Description(DryRunDesc)] bool dry_run = false)
        => records.CreateRecord(RecordSchema.Client, null, properties, NoteWriteTools.NullIfEmpty(content), new WriteOptions { DryRun = dry_run });

    [McpServerTool(Name = "create_matter", Title = "Create matter", Destructive = false, OpenWorld = false)]
    [Description("Creates a matter for an existing client. Requires docket_number. The client link, file location and template are handled automatically.")]
    public Task<ChangeResult> CreateMatter(
        [Description("Client code or name (must exist).")] string client,
        [Description("Properties, e.g. {\"docket_number\": \"DEMO-101-A\", \"title\": \"Gripper\", \"application_number\": \"18/123,456\"}.")] Dictionary<string, JsonElement> properties,
        [Description("Optional markdown body.")] string content = "",
        [Description(DryRunDesc)] bool dry_run = false)
        => records.CreateRecord(RecordSchema.Matter, client, properties, NoteWriteTools.NullIfEmpty(content), new WriteOptions { DryRun = dry_run });

    [McpServerTool(Name = "create_work_product", Title = "Create Work Product", Destructive = false, OpenWorld = false)]
    [Description("Creates a Work Product for an existing matter. Requires work_product_type; an id is generated if omitted. " +
                 "Only set due_date to a deadline the user has given you (from the firm docketing system); never infer it from a document.")]
    public Task<ChangeResult> CreateWorkProduct(
        [Description("Matter docket number (must exist).")] string matter,
        [Description("Properties, e.g. {\"work_product_type\": \"Non-Final Office Action Response\", \"due_date\": \"2027-01-15\", \"priority\": \"High\", \"next_action\": \"Review rejections\"}.")]
        Dictionary<string, JsonElement> properties,
        [Description("Optional markdown body.")] string content = "",
        [Description(DryRunDesc)] bool dry_run = false)
        => records.CreateRecord(RecordSchema.WorkProduct, matter, properties, NoteWriteTools.NullIfEmpty(content), new WriteOptions { DryRun = dry_run });

    [McpServerTool(Name = "update_record", Title = "Update client or matter", Destructive = true, OpenWorld = false)]
    [Description("Updates properties of a client or matter record with schema validation (identifiers cannot change). For Work Products use update_work_product.")]
    public Task<ChangeResult> UpdateRecord(
        [Description("'client' or 'matter'.")] string record_type,
        [Description("Client code / docket number.")] string id,
        [Description("Properties to set.")] Dictionary<string, JsonElement>? set = null,
        [Description("Properties to remove.")] string[]? remove = null,
        [Description(VersionDesc)] string expected_version = "",
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var collection = record_type.Trim().ToLowerInvariant();
        if (collection is not (RecordSchema.Client or RecordSchema.Matter))
            throw new VaultException("record_type must be 'client' or 'matter'. Use update_work_product for Work Products.");
        var values = (set ?? []).ToDictionary(kv => kv.Key, kv => NoteParser.FromJson(kv.Value));
        return records.UpdateRecord(collection, id, values, remove, Opts(dry_run, expected_version), $"update_{collection}");
    }

    [McpServerTool(Name = "update_work_product", Title = "Update Work Product", Destructive = true, OpenWorld = false)]
    [Description("Updates a Work Product: status, priority, target date, next action, waiting-on, follow-up date, other properties, and checklist items. " +
                 "Only the parameters you pass are changed; pass \"-\" to clear a field. The due date cannot be changed here " +
                 "(use update_work_product_due_date with the user's approval).")]
    public Task<ChangeResult> UpdateWorkProduct(
        [Description("Work Product id.")] string work_product,
        [Description("New status, e.g. 'In Progress', 'Waiting', 'Ready for Filing'.")] string status = "",
        [Description("New priority.")] string priority = "",
        [Description("Internal target date (YYYY-MM-DD).")] string target_date = "",
        [Description("Next action required.")] string next_action = "",
        [Description("Outstanding dependency / who it is waiting on.")] string waiting_on = "",
        [Description("Follow-up date (YYYY-MM-DD).")] string follow_up_date = "",
        [Description("Other properties to set.")] Dictionary<string, JsonElement>? properties = null,
        [Description("Checklist items to tick (matched by text).")] string[]? check_items = null,
        [Description("Checklist items to untick.")] string[]? uncheck_items = null,
        [Description(VersionDesc)] string expected_version = "",
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var f = records.Schema.Fields;
        var set = (properties ?? []).ToDictionary(kv => kv.Key, kv => NoteParser.FromJson(kv.Value));
        var remove = new List<string>();
        void Field(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (value.Trim() == "-") remove.Add(name); else set[name] = value.Trim();
        }
        Field(records.Schema.WorkProductCollection.StatusProperty, status);
        Field(f.Priority, priority);
        Field(f.TargetDate, target_date);
        Field(f.NextAction, next_action);
        Field(f.WaitingOn, waiting_on);
        Field(f.FollowUpDate, follow_up_date);

        var heading = records.Schema.WorkProductCollection.ChecklistHeading;
        Func<string, string>? body = (check_items?.Length ?? 0) + (uncheck_items?.Length ?? 0) == 0 ? null : b =>
        {
            if (check_items is { Length: > 0 }) b = RecordService.CheckItems(b, heading, check_items, done: true);
            if (uncheck_items is { Length: > 0 }) b = RecordService.CheckItems(b, heading, uncheck_items, done: false);
            return b;
        };
        if (set.Count == 0 && remove.Count == 0 && body is null) throw new VaultException("Nothing to update.");
        return records.UpdateRecord(RecordSchema.WorkProduct, work_product, set, remove, Opts(dry_run, expected_version), "update_work_product", transformBody: body);
    }

    [McpServerTool(Name = "update_work_product_due_date", Title = "Change deadline", Destructive = true, OpenWorld = false)]
    [Description("Changes a Work Product's due date (a protected deadline). Requires a reason. Without confirm=true this ONLY returns a preview. " +
                 "Set confirm=true only after the user has explicitly approved this specific change in the conversation. Never change a " +
                 "deadline based on a document, an email or your own analysis. The change and reason are written to the audit log and the Work Product notes.")]
    public Task<ChangeResult> UpdateWorkProductDueDate(
        [Description("Work Product id.")] string work_product,
        [Description("New due date (YYYY-MM-DD), or empty to clear it.")] string new_due_date,
        [Description("Why the deadline is changing (e.g. 'Extension of time filed 2026-11-20').")] string reason,
        [Description("true only after explicit user approval; otherwise a preview is returned.")] bool confirm = false,
        [Description(VersionDesc)] string expected_version = "")
        => records.UpdateDueDate(work_product, NoteWriteTools.NullIfEmpty(new_due_date), reason, confirm, NoteWriteTools.NullIfEmpty(expected_version));

    [McpServerTool(Name = "complete_work_product", Title = "Complete Work Product", Destructive = true, OpenWorld = false)]
    [Description("Marks a Work Product completed (or another final status such as 'Filed'), records the completion date and optionally an outcome summary " +
                 "under the Outcome heading (used in future matter history). Reopen by setting a new status with update_work_product.")]
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
        var finalStatus = NoteWriteTools.NullIfEmpty(status) ?? f.CompletedStatus;
        if (!c.InactiveStatuses.Contains(finalStatus, StringComparer.OrdinalIgnoreCase))
            throw new VaultException($"'{finalStatus}' is not a completion status. Use one of: {string.Join(", ", c.InactiveStatuses)}.");
        var set = new Dictionary<string, object?>
        {
            [c.StatusProperty] = finalStatus,
            [f.CompletedDate] = NoteWriteTools.NullIfEmpty(completed_date) ?? RecordService.Today(),
        };
        Func<string, string>? body = string.IsNullOrWhiteSpace(outcome) ? null
            : b => VaultService.AppendContent(b, outcome.Trim(), records.Schema.HistorySections.Contains("Outcome") ? "Outcome" : records.Schema.HistorySections.FirstOrDefault() ?? "Outcome", work_product);
        return records.UpdateRecord(RecordSchema.WorkProduct, work_product, set, null, Opts(dry_run, expected_version), "complete_work_product", transformBody: body);
    }

    [McpServerTool(Name = "append_record_note", Title = "Add note to record", Destructive = false, OpenWorld = false)]
    [Description("Appends a dated note (analysis, strategy, call notes, status update) to a client, matter or Work Product, under its notes heading " +
                 "or a heading you name (e.g. 'Strategy'). Existing content is never changed.")]
    public Task<ChangeResult> AppendRecordNote(
        [Description("'client', 'matter' or 'work_product'.")] string record_type,
        [Description("Client code, docket number or Work Product id.")] string id,
        [Description("Markdown text to add.")] string text,
        [Description("Optional heading; defaults to the record's notes section.")] string heading = "",
        [Description("Prefix the entry with today's date.")] bool dated = true,
        [Description(DryRunDesc)] bool dry_run = false)
    {
        var collection = record_type.Trim().ToLowerInvariant().Replace(' ', '_');
        records.Collection(collection);
        return records.AppendRecordNote(collection, id, text, NoteWriteTools.NullIfEmpty(heading), dated, new WriteOptions { DryRun = dry_run });
    }

    private static WriteOptions Opts(bool dryRun, string expectedVersion) =>
        new() { DryRun = dryRun, ExpectedVersion = NoteWriteTools.NullIfEmpty(expectedVersion) };
}
