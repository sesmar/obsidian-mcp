using System.Text.RegularExpressions;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Server.Records;

public sealed record DocketItem(
    string Id, string Title, string Path, string? Client, string? Matter, string? Type, string? Status, string? Priority,
    string? DueDate, int? DaysUntilDue, bool Overdue, string? TargetDate, string? FollowUpDate, bool FollowUpDue,
    string? NextAction, string? WaitingOn, string? CompletedDate, bool Active, DateTime Modified, int DaysSinceModified);

/// <summary>Flexible docket filter. Every dashboard view is a combination of these fields.</summary>
public sealed record DocketQuery
{
    public string? Client { get; init; }
    public string? Matter { get; init; }
    public IReadOnlyList<string>? WorkProductTypes { get; init; }
    public IReadOnlyList<string>? Statuses { get; init; }
    public IReadOnlyList<string>? Priorities { get; init; }
    public DateOnly? DueDateFrom { get; init; }
    public DateOnly? DueDateTo { get; init; }
    public bool IncludeCompleted { get; init; }
    public bool IncludeOverdue { get; init; } = true;
    public bool OnlyOverdue { get; init; }
    /// <summary>"*" = anything waiting; otherwise case-insensitive substring of waiting_on.</summary>
    public string? WaitingOn { get; init; }
    public DateOnly? FollowUpDueBy { get; init; }
    public bool OnlyReadyForFiling { get; init; }
    public int? StaleDays { get; init; }
    public DateOnly? CompletedFrom { get; init; }
    public DateOnly? CompletedTo { get; init; }
    public string? Text { get; init; }
    public string SortBy { get; init; } = "due_date";
    public string SortOrder { get; init; } = "ascending";
    public int Limit { get; init; } = 100;
}

/// <summary>Docket queries, workload reporting and data-quality checks over Work Product records.</summary>
public sealed class DocketService(RecordService records)
{
    private RecordSchema Schema => records.Schema;
    private WorkProductFields F => Schema.Fields;

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public DocketItem ToItem(Record wp, RecordSet set)
    {
        var c = Schema.WorkProductCollection;
        var matter = set.Parent(wp);
        var client = matter is null ? null : set.Parent(matter);
        var status = wp.Get(c.StatusProperty);
        var active = !IsInactive(status);
        var due = wp.GetDate(F.DueDate);
        var followUp = wp.GetDate(F.FollowUpDate);
        int? days = due is null ? null : due.Value.DayNumber - Today.DayNumber;
        return new DocketItem(
            wp.Id,
            wp.Get(c.TitleProperty ?? "") ?? wp.Note.Title,
            wp.Path,
            client?.Id,
            matter?.Id ?? (c.ParentProperty is null ? null : wp.Get(c.ParentProperty) is { } m ? VaultService.StripLink(m) : null),
            wp.Get(F.Type),
            status,
            wp.Get(F.Priority),
            due?.ToString("yyyy-MM-dd"),
            days,
            active && days < 0,
            wp.GetDate(F.TargetDate)?.ToString("yyyy-MM-dd"),
            followUp?.ToString("yyyy-MM-dd"),
            active && followUp is not null && followUp <= Today,
            wp.Get(F.NextAction),
            wp.Get(F.WaitingOn),
            wp.GetDate(F.CompletedDate)?.ToString("yyyy-MM-dd"),
            active,
            wp.Note.Modified,
            (int)(DateTime.UtcNow - wp.Note.Modified).TotalDays);
    }

    public bool IsInactive(string? status) =>
        status is not null && Schema.WorkProductCollection.InactiveStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);

    public (IReadOnlyList<DocketItem> Items, int TotalMatches) Query(DocketQuery q, RecordSet? snapshot = null)
    {
        var set = snapshot ?? records.Snapshot();
        Record? client = q.Client is { Length: > 0 } ? set.Require(RecordSchema.Client, q.Client) : null;
        Record? matter = q.Matter is { Length: > 0 } ? set.Require(RecordSchema.Matter, q.Matter) : null;
        var includeCompleted = q.IncludeCompleted || q.CompletedFrom is not null || q.CompletedTo is not null;

        var items = new List<DocketItem>();
        foreach (var wp in set.All(RecordSchema.WorkProduct))
        {
            if (matter is not null && !ReferenceEquals(set.Parent(wp), matter)) continue;
            if (client is not null && !ReferenceEquals(set.Ancestor(wp, RecordSchema.Client), client)) continue;
            var it = ToItem(wp, set);

            if (!includeCompleted && !it.Active) continue;
            if (!q.IncludeOverdue && it.Overdue) continue;
            if (q.OnlyOverdue && !it.Overdue) continue;
            if (q.WorkProductTypes is { Count: > 0 } types &&
                !types.Any(t => it.Type?.Contains(t, StringComparison.OrdinalIgnoreCase) == true)) continue;
            if (q.Statuses is { Count: > 0 } statuses && !statuses.Contains(it.Status ?? "", StringComparer.OrdinalIgnoreCase)) continue;
            if (q.Priorities is { Count: > 0 } prios && !prios.Contains(it.Priority ?? "", StringComparer.OrdinalIgnoreCase)) continue;
            if (q.DueDateFrom is not null || q.DueDateTo is not null)
            {
                var due = RecordSet.ParseDate(it.DueDate);
                if (due is null || due < q.DueDateFrom || due > q.DueDateTo) continue;
            }
            if (q.WaitingOn is { Length: > 0 } w)
            {
                var waiting = it.WaitingOn is { Length: > 0 } || string.Equals(it.Status, F.WaitingStatus, StringComparison.OrdinalIgnoreCase);
                if (!waiting || (w != "*" && it.WaitingOn?.Contains(w, StringComparison.OrdinalIgnoreCase) != true)) continue;
            }
            if (q.FollowUpDueBy is not null && !(RecordSet.ParseDate(it.FollowUpDate) is { } fu && fu <= q.FollowUpDueBy)) continue;
            if (q.OnlyReadyForFiling && !string.Equals(it.Status, F.ReadyForFilingStatus, StringComparison.OrdinalIgnoreCase)) continue;
            if (q.StaleDays is > 0 && !(it.Active && it.DaysSinceModified >= q.StaleDays)) continue;
            if (q.CompletedFrom is not null || q.CompletedTo is not null)
            {
                var done = RecordSet.ParseDate(it.CompletedDate);
                if (done is null || done < q.CompletedFrom || done > q.CompletedTo) continue;
            }
            if (q.Text is { Length: > 0 } text &&
                !(it.Id.Contains(text, StringComparison.OrdinalIgnoreCase) || it.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                  it.NextAction?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)) continue;
            items.Add(it);
        }

        var sorted = Sort(items, q.SortBy, q.SortOrder.StartsWith("desc", StringComparison.OrdinalIgnoreCase));
        return (sorted.Take(Math.Clamp(q.Limit, 1, 1000)).ToList(), items.Count);
    }

    private IEnumerable<DocketItem> Sort(List<DocketItem> items, string sortBy, bool descending)
    {
        var priorities = Schema.WorkProductCollection.Properties.TryGetValue(F.Priority, out var p) ? p.Values ?? [] : [];
        int PriorityRank(string? v) { var i = v is null ? -1 : priorities.FindIndex(x => x.Equals(v, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; }

        // Items without a value always sort last.
        Func<DocketItem, IComparable?> key = sortBy.ToLowerInvariant() switch
        {
            "target_date" => i => i.TargetDate,
            "follow_up_date" => i => i.FollowUpDate,
            "completed_date" => i => i.CompletedDate,
            "priority" => i => PriorityRank(i.Priority) is var r && r == int.MaxValue ? null : r,
            "client" => i => i.Client,
            "matter" => i => i.Matter,
            "status" => i => i.Status,
            "type" or "work_product_type" => i => i.Type,
            "modified" => i => i.Modified,
            "id" => i => i.Id,
            _ => i => i.DueDate,
        };
        var withValue = items.Where(i => key(i) is not null);
        var ordered = descending ? withValue.OrderByDescending(key) : withValue.OrderBy(key);
        return ordered.ThenBy(i => i.DueDate ?? "9999").ThenBy(i => i.Id).Concat(items.Where(i => key(i) is null).OrderBy(i => i.DueDate ?? "9999"));
    }

    public object Summary(string? client, int staleDays)
    {
        var set = records.Snapshot();
        var (active, _) = Query(new DocketQuery { Client = client, Limit = 1000 }, set);
        var today = Today;
        int Due(int days) => active.Count(i => i.DaysUntilDue is { } d && d >= 0 && d <= days);
        static IEnumerable<object> Group(IEnumerable<DocketItem> items, Func<DocketItem, string?> by) =>
            items.GroupBy(i => by(i) ?? "(none)").OrderByDescending(g => g.Count()).Select(g => new { key = g.Key, count = g.Count() });

        var (completed30, _) = Query(new DocketQuery { Client = client, CompletedFrom = today.AddDays(-30), CompletedTo = today, Limit = 1000 }, set);
        return new
        {
            as_of = today.ToString("yyyy-MM-dd"),
            client,
            active = active.Count,
            overdue = active.Count(i => i.Overdue),
            due_today = Due(0),
            due_in_7_days = Due(7),
            due_in_30_days = Due(30),
            no_due_date = active.Count(i => i.DueDate is null),
            awaiting_others = active.Count(i => i.WaitingOn is { Length: > 0 } || string.Equals(i.Status, F.WaitingStatus, StringComparison.OrdinalIgnoreCase)),
            ready_for_filing = active.Count(i => string.Equals(i.Status, F.ReadyForFilingStatus, StringComparison.OrdinalIgnoreCase)),
            follow_ups_due = active.Count(i => i.FollowUpDue),
            stale = active.Count(i => i.DaysSinceModified >= staleDays),
            completed_last_30_days = completed30.Count,
            by_type = Group(active, i => i.Type),
            by_status = Group(active, i => i.Status),
            by_priority = Group(active, i => i.Priority),
            by_client = Group(active, i => i.Client),
            next_deadlines = active.Where(i => i.DueDate is not null).OrderBy(i => i.DueDate).Take(10)
                .Select(i => new { i.Id, i.Matter, i.Type, i.DueDate, i.DaysUntilDue, i.Status }),
            note = "Deadlines come from the Obsidian records. The firm's official docketing system remains authoritative for legal deadlines.",
        };
    }

    /// <summary>Data-quality problems: schema violations, broken relationships, duplicates, inconsistent docket data, sync conflicts.</summary>
    public IReadOnlyList<ValidationIssue> Exceptions(string? client = null)
    {
        var set = records.Snapshot();
        var issues = new List<ValidationIssue>();
        Record? clientFilter = client is { Length: > 0 } ? set.Require(RecordSchema.Client, client) : null;
        bool InScope(Record r) => clientFilter is null || ReferenceEquals(set.Ancestor(r, RecordSchema.Client), clientFilter);

        foreach (var (collection, list) in set.ByCollection)
        {
            foreach (var r in list.Where(InScope)) issues.AddRange(records.Validate(r, set));
            foreach (var dup in list.Where(InScope).GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                issues.Add(new ValidationIssue("error", "duplicate_id",
                    $"{dup.Count()} {Schema.Collections[collection].Label} records share the identifier '{dup.Key}': {string.Join(", ", dup.Select(d => d.Path))}",
                    dup.First().Path, dup.Key));
        }

        foreach (var wp in set.All(RecordSchema.WorkProduct).Where(InScope))
        {
            var it = ToItem(wp, set);
            void Add(string severity, string code, string msg, string? prop = null) =>
                issues.Add(new ValidationIssue(severity, code, msg, wp.Path, wp.Id, prop));
            if (it.Active && it.DueDate is null) Add("warning", "missing_due_date", "Active Work Product has no due date.", F.DueDate);
            if (it.Active && it.NextAction is null) Add("info", "missing_next_action", "Active Work Product has no next action.", F.NextAction);
            if (string.Equals(it.Status, F.WaitingStatus, StringComparison.OrdinalIgnoreCase) && it.WaitingOn is null)
                Add("warning", "waiting_without_dependency", $"Status is '{F.WaitingStatus}' but '{F.WaitingOn}' is empty.", F.WaitingOn);
            if (!it.Active && it.CompletedDate is null && !string.Equals(it.Status, Schema.WorkProductCollection.ArchivedStatus, StringComparison.OrdinalIgnoreCase))
                Add("warning", "missing_completed_date", "Inactive Work Product has no completed date.", F.CompletedDate);
            if (it.Active && it.TargetDate is not null && it.DueDate is not null && string.CompareOrdinal(it.TargetDate, it.DueDate) > 0)
                Add("warning", "target_after_due", $"Target date {it.TargetDate} is after due date {it.DueDate}.", F.TargetDate);
            if (it.Active && set.Parent(wp) is { } m &&
                Schema.MatterCollection.InactiveStatuses.Contains(m.Get(Schema.MatterCollection.StatusProperty) ?? "", StringComparer.OrdinalIgnoreCase))
                Add("warning", "active_work_on_inactive_matter", $"Matter '{m.Id}' is '{m.Get(Schema.MatterCollection.StatusProperty)}' but this Work Product is active.");
        }

        // File-sync conflict copies (Synology Drive, Dropbox, OneDrive, iCloud…).
        foreach (var file in records.Vault.EnumerateFiles())
            if (SyncConflict().IsMatch(System.IO.Path.GetFileName(file)))
                issues.Add(new ValidationIssue("warning", "sync_conflict", "File looks like a sync-conflict copy; reconcile it with the original.", file));

        var order = new Dictionary<string, int> { ["error"] = 0, ["warning"] = 1, ["info"] = 2 };
        return issues.OrderBy(i => order.GetValueOrDefault(i.Severity, 3)).ThenBy(i => i.Code).ThenBy(i => i.Path).ToList();
    }

    private static Regex SyncConflict() => new(@"(_Conflict|conflicted copy|\(Conflict|-conflict-|Case Conflict)", RegexOptions.IgnoreCase);
}
