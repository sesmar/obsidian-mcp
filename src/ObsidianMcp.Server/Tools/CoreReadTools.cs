using System.ComponentModel;
using ModelContextProtocol.Server;
using ObsidianMcp.Server.Records;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Server.Tools;

/// <summary>The 18 read-only tools of the 25-tool MVP.</summary>
[McpServerToolType]
public sealed class CoreReadTools(VaultService vault, RecordService records, DocketService docket, ContextService context)
{
    // ------------------------------------------------------------------ vault and search

    [McpServerTool(Name = "get_vault_info", Title = "Vault info", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Overview of the vault: record counts per collection (clients, matters, work products), knowledge-base folders, " +
                 "templates and workflows folders, top-level folders, top tags and whether writes are allowed. Call this first.")]
    public object GetVaultInfo()
    {
        var set = records.Snapshot();
        return new
        {
            name = vault.VaultName,
            read_only = vault.IsReadOnly,
            note_count = set.Notes.Count,
            collections = records.Schema.Collections.ToDictionary(c => c.Key, c => new { folder = c.Value.Folder, count = set.All(c.Key).Count }),
            knowledge_folders = records.Schema.KnowledgeFolders,
            workflows_folder = records.Schema.WorkflowsFolder,
            templates_folder = records.Schema.TemplatesFolder,
            top_level = vault.ListFolder("", recursive: false, limit: 200, foldersOnly: true).Select(e => e.Path),
            top_tags = vault.GetTags().OrderByDescending(t => t.Value).Take(25).ToDictionary(t => t.Key, t => t.Value),
            schema_source = records.SchemaSource,
        };
    }

    [McpServerTool(Name = "search_notes", Title = "Search notes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches note content and metadata. All words must match (use \"quotes\" for phrases). Filters: folder (e.g. a knowledge-base " +
                 "folder such as 'Knowledge/Case Law'), tags (all must match, nested included), properties (exact match or list membership; " +
                 "'*' = has a value, '!' = missing) and modified_within_days. The query may be empty when a filter is given. Knowledge-base " +
                 "summaries are not verified authority: check the stored source before relying on them.")]
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

    [McpServerTool(Name = "get_note", Title = "Get note", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieves a note: properties (YAML frontmatter), tags, headings, links and markdown content, plus a 'version' to pass as " +
                 "expected_version when editing. Accepts a path or just a note name. properties_only=true skips the body.")]
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

    // ------------------------------------------------------------------ clients

    [McpServerTool(Name = "list_clients", Title = "List clients", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists clients with their status, number of matters and open / overdue Work Products.")]
    public object ListClients(
        [Description("Optional status filter, e.g. 'Active'.")] string status = "",
        [Description("Optional text in the client code or name.")] string text = "")
    {
        var set = records.Snapshot();
        var c = records.Schema.ClientCollection;
        var (open, _) = docket.Query(new DocketQuery { Limit = 1000 }, set);
        return set.All(RecordSchema.Client)
            .Where(r => status.Length == 0 || string.Equals(r.Get(c.StatusProperty), status, StringComparison.OrdinalIgnoreCase))
            .Where(r => text.Length == 0 || r.Id.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                        r.Get(c.TitleProperty ?? "")?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(r => r.Id)
            .Select(r => new
            {
                id = r.Id,
                name = r.Get(c.TitleProperty ?? "") ?? r.Note.Title,
                status = r.Get(c.StatusProperty),
                matters = set.Children(r, RecordSchema.Matter).Count(),
                open_work_products = open.Count(i => string.Equals(i.Client, r.Id, StringComparison.OrdinalIgnoreCase)),
                overdue = open.Count(i => i.Overdue && string.Equals(i.Client, r.Id, StringComparison.OrdinalIgnoreCase)),
                path = r.Path,
            });
    }

    [McpServerTool(Name = "get_client", Title = "Get client", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieves a client by client code or name: properties, drafting/reporting/prosecution preferences and guidelines, contacts, " +
                 "matters and open docket counts.")]
    public object GetClient([Description("Client code, name or note path.")] string client)
    {
        var set = records.Snapshot();
        return context.ClientSummary(set.Require(RecordSchema.Client, client), set, includePreferences: true);
    }

    [McpServerTool(Name = "get_client_matters", Title = "Client matters", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieves the matters of a client, with status, application number and open / overdue Work Product counts.")]
    public IEnumerable<object> GetClientMatters(
        [Description("Client code or name.")] string client,
        [Description("Optional status filter, e.g. 'Active'.")] string status = "")
        => context.Matters(client, status, null, 1000);

    // ------------------------------------------------------------------ matters

    [McpServerTool(Name = "list_matters", Title = "List matters", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists matters, optionally filtered by client, status or text (docket number, title, application number).")]
    public IEnumerable<object> ListMatters(
        [Description("Client code or name.")] string client = "",
        [Description("Status filter, e.g. 'Active'.")] string status = "",
        [Description("Text in the docket number, title or application number.")] string text = "",
        [Description("Maximum results (default 100).")] int limit = 100)
        => context.Matters(client, status, text, limit);

    [McpServerTool(Name = "get_matter", Title = "Get matter", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieves a matter by docket number: properties, matter notes, its Work Products (brief), attachments and a 'version' for edits. " +
                 "Use get_matter_context for the full prosecution context.")]
    public object GetMatter([Description("Docket number, e.g. DEMO-101-A.")] string matter) => context.Matter(matter);

    [McpServerTool(Name = "get_matter_context", Title = "Matter context", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One-call consolidated context for a matter: matter properties and notes, the client's drafting/reporting/prosecution " +
                 "preferences, related and family matters, active Work Products, prior Work Product history (strategy/outcome summaries) " +
                 "and attachments. Use this before working on anything for a matter.")]
    public object GetMatterContext(
        [Description("Docket number, e.g. DEMO-101-A.")] string matter,
        [Description("Maximum prior Work Products to include (default 10).")] int history_limit = 10)
        => context.MatterContext(matter, history_limit);

    // ------------------------------------------------------------------ work products

    [McpServerTool(Name = "get_work_product", Title = "Get Work Product", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieves a Work Product: docket data (due date, days until due, status, next action, waiting on), properties, checklist, " +
                 "notes, attachments and a 'version' for edits.")]
    public object GetWorkProduct([Description("Work Product id, e.g. DEMO-101-A-WP02.")] string work_product) => context.WorkProduct(work_product);

    [McpServerTool(Name = "get_work_product_context", Title = "Work Product context", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One-call context for a Work Product: the Work Product itself, its matter, the client's preferences, the applicable workflow, " +
                 "prior Work Products on the matter and attachments. Use this before working on an assignment.")]
    public object GetWorkProductContext(
        [Description("Work Product id.")] string work_product,
        [Description("Maximum prior Work Products to include (default 10).")] int history_limit = 10)
        => context.WorkProductContext(work_product, history_limit);

    // ------------------------------------------------------------------ docket

    [McpServerTool(Name = "get_docket", Title = "Docket", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Flexible docket query over Work Products; every dashboard view is a combination of these filters. Examples: upcoming deadlines " +
                 "(due_date_to), overdue (only_overdue), Office Action responses (work_product_types=['Office Action']), awaiting others " +
                 "(waiting_on='*'), follow-ups due (follow_up_due_by=today), ready for filing, stale work (stale_days=14), completed in a period " +
                 "(completed_from/completed_to). Dates are YYYY-MM-DD. Completed items are excluded unless include_completed=true.")]
    public object GetDocket(
        [Description("Client code or name.")] string client = "",
        [Description("Matter docket number.")] string matter = "",
        [Description("Work Product types (substring match, e.g. 'Office Action').")] string[]? work_product_types = null,
        [Description("Exact statuses.")] string[]? statuses = null,
        [Description("Priorities.")] string[]? priorities = null,
        [Description("Due on or after (YYYY-MM-DD).")] string due_date_from = "",
        [Description("Due on or before (YYYY-MM-DD).")] string due_date_to = "",
        [Description("Include completed / inactive Work Products.")] bool include_completed = false,
        [Description("Include overdue items.")] bool include_overdue = true,
        [Description("Only overdue items.")] bool only_overdue = false,
        [Description("'*' for anything awaiting another party, or text matched against waiting_on.")] string waiting_on = "",
        [Description("Follow-up date on or before (YYYY-MM-DD).")] string follow_up_due_by = "",
        [Description("Only Work Products ready for filing.")] bool only_ready_for_filing = false,
        [Description("Only active items not modified for this many days.")] int stale_days = 0,
        [Description("Completed on or after (YYYY-MM-DD).")] string completed_from = "",
        [Description("Completed on or before (YYYY-MM-DD).")] string completed_to = "",
        [Description("Text in id, title or next action.")] string text = "",
        [Description("due_date, target_date, follow_up_date, completed_date, priority, client, matter, status, type, modified or id.")] string sort_by = "due_date",
        [Description("ascending or descending.")] string sort_order = "ascending",
        [Description("Maximum results (default 100).")] int limit = 100)
    {
        var q = new DocketQuery
        {
            Client = ToolHelpers.NullIfEmpty(client), Matter = ToolHelpers.NullIfEmpty(matter),
            WorkProductTypes = work_product_types, Statuses = statuses, Priorities = priorities,
            DueDateFrom = ToolHelpers.Date(due_date_from), DueDateTo = ToolHelpers.Date(due_date_to),
            IncludeCompleted = include_completed, IncludeOverdue = include_overdue, OnlyOverdue = only_overdue,
            WaitingOn = ToolHelpers.NullIfEmpty(waiting_on), FollowUpDueBy = ToolHelpers.Date(follow_up_due_by),
            OnlyReadyForFiling = only_ready_for_filing, StaleDays = stale_days,
            CompletedFrom = ToolHelpers.Date(completed_from), CompletedTo = ToolHelpers.Date(completed_to),
            Text = ToolHelpers.NullIfEmpty(text), SortBy = sort_by, SortOrder = sort_order, Limit = limit,
        };
        var (items, total) = docket.Query(q);
        return new { as_of = DocketService.Today.ToString("yyyy-MM-dd"), total_matches = total, returned = items.Count, items };
    }

    [McpServerTool(Name = "get_docket_summary", Title = "Docket summary", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Aggregated workload and deadline statistics for daily/weekly reviews: active, overdue, due in 7/30 days, awaiting others, " +
                 "ready for filing, follow-ups due, stale, completed in the last 30 days, breakdowns by type/status/priority/client and the next deadlines.")]
    public object GetDocketSummary(
        [Description("Optional client code to scope the summary.")] string client = "",
        [Description("Days without changes after which active work counts as stale (default from schema).")] int stale_days = 0)
        => docket.Summary(ToolHelpers.NullIfEmpty(client), stale_days > 0 ? stale_days : records.Schema.StaleAfterDays);

    [McpServerTool(Name = "get_docket_exceptions", Title = "Docket exceptions", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Data-integrity report: missing deadlines and required properties, invalid values or dates, broken Client/Matter/Work Product " +
                 "relationships, duplicate identifiers, inconsistent statuses and sync-conflict files.")]
    public object GetDocketExceptions(
        [Description("Optional client code to scope the check.")] string client = "",
        [Description("Minimum severity: error, warning or info (default warning).")] string min_severity = "warning",
        [Description("Maximum issues (default 200).")] int limit = 200)
    {
        var rank = new Dictionary<string, int> { ["error"] = 0, ["warning"] = 1, ["info"] = 2 };
        var max = rank.GetValueOrDefault(min_severity.ToLowerInvariant(), 1);
        var issues = docket.Exceptions(ToolHelpers.NullIfEmpty(client)).Where(i => rank.GetValueOrDefault(i.Severity, 2) <= max).ToList();
        return new
        {
            total = issues.Count,
            by_code = issues.GroupBy(i => i.Code).ToDictionary(g => g.Key, g => g.Count()),
            issues = issues.Take(Math.Clamp(limit, 1, 2000)),
        };
    }

    // ------------------------------------------------------------------ knowledge and workflows

    [McpServerTool(Name = "get_template", Title = "Get template", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieves a standardized note or document template from the Templates folder, with the {{placeholders}} it uses. " +
                 "With no argument, lists the available templates.")]
    public object GetTemplate([Description("Template name or path. Empty to list templates.")] string name = "")
    {
        var folder = records.Schema.TemplatesFolder.Trim('/') + "/";
        var templates = vault.GetAllNotes().Where(n => n.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Path).ToList();
        if (string.IsNullOrWhiteSpace(name))
            return new { templates = templates.Select(t => new { name = t.Title, path = t.Path }) };
        var key = name.Trim();
        var template = templates.FirstOrDefault(t => t.Title.Equals(key, StringComparison.OrdinalIgnoreCase) || t.Path.Equals(key, StringComparison.OrdinalIgnoreCase))
                       ?? templates.FirstOrDefault(t => t.Title.Contains(key, StringComparison.OrdinalIgnoreCase))
                       ?? throw new VaultException($"Template not found: '{name}'. Call get_template with no name to list templates.");
        var usedBy = records.Schema.Collections.Where(c => string.Equals(c.Value.Template, template.Path, StringComparison.OrdinalIgnoreCase)).Select(c => c.Key);
        return new
        {
            name = template.Title,
            path = template.Path,
            used_for_records = usedBy,
            placeholders = System.Text.RegularExpressions.Regex.Matches(template.Content, @"\{\{\s*([\w\-]+)\s*\}\}")
                .Select(m => m.Groups[1].Value).Distinct(),
            properties = template.Properties,
            content = template.Content,
        };
    }

    [McpServerTool(Name = "get_workflow", Title = "Get workflow", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieves the instructions and checklist for a professional workflow, by name or by Work Product type (e.g. " +
                 "'Non-Final Office Action Response'). With no argument, lists the workflows and the types they apply to.")]
    public object GetWorkflow([Description("Workflow name or Work Product type. Empty to list workflows.")] string name_or_type = "")
    {
        if (string.IsNullOrWhiteSpace(name_or_type))
            return new
            {
                workflows = context.Workflows().Select(w => new
                {
                    title = w.Title,
                    path = w.Path,
                    applies_to = w.Properties.TryGetValue(records.Schema.WorkflowAppliesToProperty, out var v) ? VaultService.PropertyValues(v) : [],
                }),
            };
        var wf = context.FindWorkflow(name_or_type) ?? throw new VaultException($"No workflow found for '{name_or_type}'.");
        return context.WorkflowBlock(wf, full: true);
    }

    // ------------------------------------------------------------------ administration

    [McpServerTool(Name = "get_schema", Title = "Get schema", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the collection schemas: folders, identifier and relationship properties, required properties, permitted values " +
                 "(statuses, Work Product types, priorities), protected properties and docket field names.")]
    public object GetSchema() => new { source = records.SchemaSource, schema = System.Text.Json.JsonDocument.Parse(records.Schema.ToJson()).RootElement };

    [McpServerTool(Name = "get_audit_log", Title = "Audit log", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Recorded changes made through this server (newest first): operation, path, record, property before/after values, reason and versions.")]
    public IReadOnlyList<AuditEntry> GetAuditLog(
        [Description("Filter by note path (substring).")] string path = "",
        [Description("Filter by record id (client code, docket number or Work Product id).")] string record_id = "",
        [Description("Filter by operation, e.g. update_due_date.")] string operation = "",
        [Description("Only the last N days (0 = all).")] int days = 0,
        [Description("Maximum entries (default 50).")] int limit = 50)
    {
        var since = days > 0 ? DateTimeOffset.Now.AddDays(-days) : DateTimeOffset.MinValue;
        return vault.Audit.Read(e =>
            e.Timestamp >= since &&
            (path.Length == 0 || e.Path.Contains(path, StringComparison.OrdinalIgnoreCase) || e.FromPath?.Contains(path, StringComparison.OrdinalIgnoreCase) == true) &&
            (record_id.Length == 0 || string.Equals(e.RecordId, record_id, StringComparison.OrdinalIgnoreCase)) &&
            (operation.Length == 0 || string.Equals(e.Operation, operation, StringComparison.OrdinalIgnoreCase)),
            Math.Clamp(limit, 1, 1000));
    }
}

internal static class ToolHelpers
{
    public static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static DateOnly? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return RecordSet.ParseDate(value) ?? throw new VaultException($"Invalid date '{value}'. Use YYYY-MM-DD.");
    }

    public static WriteOptions Opts(bool dryRun, string? expectedVersion, string? reason = null) =>
        new() { DryRun = dryRun, ExpectedVersion = NullIfEmpty(expectedVersion), Reason = NullIfEmpty(reason) };
}
