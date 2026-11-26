using System.ComponentModel;
using ModelContextProtocol.Server;
using ObsidianMcp.Server.Records;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Server.Tools;

/// <summary>Read-only Client, Matter, Work Product, docket, knowledge and administration tools.</summary>
[McpServerToolType]
public sealed class RecordReadTools(RecordService records, DocketService docket, ContextService context, VaultService vault)
{
    // ------------------------------------------------------------------ clients & matters

    [McpServerTool(Name = "list_clients", Title = "List clients", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists clients with their status, number of matters and open Work Products.")]
    public object ListClients([Description("Optional status filter, e.g. 'Active'.")] string status = "")
    {
        var set = records.Snapshot();
        var c = records.Schema.ClientCollection;
        var (open, _) = docket.Query(new DocketQuery { Limit = 1000 }, set);
        return set.All(RecordSchema.Client)
            .Where(r => status.Length == 0 || string.Equals(r.Get(c.StatusProperty), status, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Id)
            .Select(r => new
            {
                id = r.Id,
                name = r.Get(c.TitleProperty ?? "") ?? r.Note.Title,
                status = r.Get(c.StatusProperty),
                matters = set.Children(r, RecordSchema.Matter).Count(),
                open_work_products = open.Count(i => string.Equals(i.Client, r.Id, StringComparison.OrdinalIgnoreCase)),
                path = r.Path,
            });
    }

    [McpServerTool(Name = "get_client", Title = "Get client", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieves a client by client code or name: properties, drafting/reporting/prosecution preferences and guidelines, " +
                 "contacts, matters and open docket counts.")]
    public object GetClient([Description("Client code, name or note path.")] string client)
    {
        var set = records.Snapshot();
        return context.ClientSummary(set.Require(RecordSchema.Client, client), set, includePreferences: true);
    }

    [McpServerTool(Name = "list_matters", Title = "List matters", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists matters, optionally filtered by client, status or free text (docket number, title, application number).")]
    public object ListMatters(
        [Description("Client code or name.")] string client = "",
        [Description("Status filter, e.g. 'Active'.")] string status = "",
        [Description("Text contained in the docket number, title or application number.")] string text = "",
        [Description("Maximum results (default 100).")] int limit = 100)
    {
        var set = records.Snapshot();
        var mc = records.Schema.MatterCollection;
        var clientRecord = client.Length > 0 ? set.Require(RecordSchema.Client, client) : null;
        var (open, _) = docket.Query(new DocketQuery { Client = clientRecord?.Id, Limit = 1000 }, set);
        return set.All(RecordSchema.Matter)
            .Where(m => clientRecord is null || ReferenceEquals(set.Parent(m), clientRecord))
            .Where(m => status.Length == 0 || string.Equals(m.Get(mc.StatusProperty), status, StringComparison.OrdinalIgnoreCase))
            .Where(m => text.Length == 0 || new[] { m.Id, m.Get(mc.TitleProperty ?? ""), m.Get("application_number") }
                .Any(v => v?.Contains(text, StringComparison.OrdinalIgnoreCase) == true))
            .OrderBy(m => m.Id)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(m => new
            {
                id = m.Id,
                client = set.Parent(m)?.Id,
                title = m.Get(mc.TitleProperty ?? ""),
                status = m.Get(mc.StatusProperty),
                matter_type = m.Get("matter_type"),
                application_number = m.Get("application_number"),
                open_work_products = open.Count(i => string.Equals(i.Matter, m.Id, StringComparison.OrdinalIgnoreCase)),
                path = m.Path,
            });
    }

    [McpServerTool(Name = "get_matter_context", Title = "Matter context", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One-call consolidated context for a matter (by docket number, e.g. DEMO-101-A): matter properties and notes, the client's " +
                 "drafting/reporting/prosecution preferences, related and family matters, active Work Products, prior Work Product history " +
                 "(strategy/outcome summaries) and attachments. Use this before working on anything for a matter.")]
    public object GetMatterContext(
        [Description("Docket number, matter name or note path.")] string matter,
        [Description("Maximum prior Work Products to include (default 10).")] int history_limit = 10)
        => context.MatterContext(matter, history_limit);

    [McpServerTool(Name = "get_work_product_context", Title = "Work Product context", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One-call context for a Work Product: its docket data, properties, checklist and notes, the matter, the client's " +
                 "preferences, the applicable workflow, prior Work Products on the matter and attachments. Includes a 'version' for safe edits.")]
    public object GetWorkProductContext(
        [Description("Work Product id, name or note path.")] string work_product,
        [Description("Maximum prior Work Products to include (default 10).")] int history_limit = 10)
        => context.WorkProductContext(work_product, history_limit);

    // ------------------------------------------------------------------ docket

    [McpServerTool(Name = "get_docket", Title = "Docket", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Flexible docket query over Work Products; every dashboard view is a combination of these filters. Examples: upcoming deadlines " +
                 "(due_date_to=+30 days), overdue (only_overdue), Office Action responses (work_product_types=['Office Action']), awaiting others " +
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
            Client = NoteWriteTools.NullIfEmpty(client), Matter = NoteWriteTools.NullIfEmpty(matter),
            WorkProductTypes = work_product_types, Statuses = statuses, Priorities = priorities,
            DueDateFrom = Date(due_date_from), DueDateTo = Date(due_date_to),
            IncludeCompleted = include_completed, IncludeOverdue = include_overdue, OnlyOverdue = only_overdue,
            WaitingOn = NoteWriteTools.NullIfEmpty(waiting_on), FollowUpDueBy = Date(follow_up_due_by),
            OnlyReadyForFiling = only_ready_for_filing, StaleDays = stale_days,
            CompletedFrom = Date(completed_from), CompletedTo = Date(completed_to),
            Text = NoteWriteTools.NullIfEmpty(text), SortBy = sort_by, SortOrder = sort_order, Limit = limit,
        };
        var (items, total) = docket.Query(q);
        return new { as_of = DocketService.Today.ToString("yyyy-MM-dd"), total_matches = total, returned = items.Count, items };
    }

    [McpServerTool(Name = "get_docket_summary", Title = "Docket summary", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Aggregated docket statistics for daily/weekly reviews: active, overdue, due in 7/30 days, awaiting others, ready for filing, " +
                 "follow-ups due, stale, completed in the last 30 days, breakdowns by type/status/priority/client and the next deadlines.")]
    public object GetDocketSummary(
        [Description("Optional client code to scope the summary.")] string client = "",
        [Description("Days without changes after which active work counts as stale (default from schema).")] int stale_days = 0)
        => docket.Summary(NoteWriteTools.NullIfEmpty(client), stale_days > 0 ? stale_days : records.Schema.StaleAfterDays);

    [McpServerTool(Name = "get_docket_exceptions", Title = "Docket exceptions", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Data-integrity report: missing required properties, invalid values or dates, broken Client/Matter/Work Product relationships, " +
                 "duplicate identifiers, active work without due dates or next actions, inconsistent statuses and sync-conflict files.")]
    public object GetDocketExceptions(
        [Description("Optional client code to scope the check.")] string client = "",
        [Description("Minimum severity: error, warning or info (default warning).")] string min_severity = "warning",
        [Description("Maximum issues (default 200).")] int limit = 200)
    {
        var rank = new Dictionary<string, int> { ["error"] = 0, ["warning"] = 1, ["info"] = 2 };
        var max = rank.GetValueOrDefault(min_severity.ToLowerInvariant(), 1);
        var issues = docket.Exceptions(NoteWriteTools.NullIfEmpty(client)).Where(i => rank.GetValueOrDefault(i.Severity, 2) <= max).ToList();
        return new
        {
            total = issues.Count,
            by_code = issues.GroupBy(i => i.Code).ToDictionary(g => g.Key, g => g.Count()),
            issues = issues.Take(Math.Clamp(limit, 1, 2000)),
        };
    }

    // ------------------------------------------------------------------ knowledge & workflows

    [McpServerTool(Name = "search_knowledge", Title = "Search knowledge base", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the professional knowledge base: case law, legal authorities (statutes, regulations, MPEP), prosecution strategies, " +
                 "the argument library, firm procedures and research notes. Results include citation and verification properties. Stored summaries " +
                 "are NOT independently verified authority: check the source (source_url) before relying on them.")]
    public object SearchKnowledge(
        [Description("Search text, e.g. 'motivation to combine' or '\"112(f)\"'.")] string query,
        [Description("Optional category: case_law, legal_authorities, prosecution_strategies, arguments, firm_procedures, research.")] string category = "",
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
                var citation = records.Schema.CitationProperties
                    .Where(p => note.Properties.ContainsKey(p))
                    .ToDictionary(p => p, p => note.Properties[p]);
                hits.Add(new KnowledgeHit(cat, h.Path, h.Title, h.Score, h.Snippet, h.Tags, citation, note.Properties.ContainsKey("verified_date")));
            }
        }
        return new
        {
            verification_note = "Summaries stored in the vault are not independently verified legal authority. Confirm against the cited source before relying on them.",
            results = hits.OrderByDescending(h => h.Score).Take(Math.Clamp(limit, 1, 100)),
        };
    }

    [McpServerTool(Name = "get_workflow", Title = "Get workflow", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieves the professional workflow (instructions + checklist) by name or by Work Product type, e.g. 'Non-Final Office Action Response'. " +
                 "With no argument, lists the available workflows and the types they apply to.")]
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
    [Description("Returns the record schema: collections, folders, identifier and relationship properties, required properties, allowed values " +
                 "(statuses, Work Product types, priorities), protected properties and docket field names.")]
    public object GetSchema() => new { source = records.SchemaSource, schema = System.Text.Json.JsonDocument.Parse(records.Schema.ToJson()).RootElement };

    [McpServerTool(Name = "get_audit_log", Title = "Audit log", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Recent changes made through this server (newest first): operation, path, record, property before/after values, reason and versions.")]
    public IReadOnlyList<AuditEntry> GetAuditLog(
        [Description("Filter by note path (substring).")] string path = "",
        [Description("Filter by record id.")] string record_id = "",
        [Description("Only the last N days (0 = all).")] int days = 0,
        [Description("Maximum entries (default 50).")] int limit = 50)
    {
        var since = days > 0 ? DateTimeOffset.Now.AddDays(-days) : DateTimeOffset.MinValue;
        return vault.Audit.Read(e =>
            e.Timestamp >= since &&
            (path.Length == 0 || e.Path.Contains(path, StringComparison.OrdinalIgnoreCase) || e.FromPath?.Contains(path, StringComparison.OrdinalIgnoreCase) == true) &&
            (record_id.Length == 0 || string.Equals(e.RecordId, record_id, StringComparison.OrdinalIgnoreCase)),
            Math.Clamp(limit, 1, 1000));
    }

    internal static DateOnly? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return RecordSet.ParseDate(value) ?? throw new VaultException($"Invalid date '{value}'. Use YYYY-MM-DD.");
    }
}

public sealed record KnowledgeHit(string Category, string Path, string Title, double Score, string Snippet,
    IReadOnlyList<string> Tags, Dictionary<string, object?> Citation, bool Verified);
