using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Server.Records;

/// <summary>
/// Application service for Client / Matter / Work Product records: schema validation, relationship
/// integrity, deadline protection and controlled writes. All record writes go through here.
/// </summary>
public sealed partial class RecordService
{
    private readonly VaultService _vault;

    public RecordService(VaultService vault, IOptions<VaultOptions> options)
    {
        _vault = vault;
        var schemaPath = options.Value.SchemaPath is { Length: > 0 } p
            ? Path.GetFullPath(p, vault.RootPath)
            : Path.Combine(vault.DataFolder, "schema.json");
        Schema = RecordSchema.Load(schemaPath);
        SchemaSource = File.Exists(schemaPath) ? schemaPath : "built-in defaults";
    }

    public RecordSchema Schema { get; }
    public string SchemaSource { get; }
    public VaultService Vault => _vault;

    public RecordSet Snapshot() => new(Schema, _vault.GetAllNotes());

    public CollectionSchema Collection(string name) =>
        Schema.Collections.TryGetValue(name, out var c) ? c : throw new VaultException($"Unknown collection '{name}'.");

    public string? CollectionOf(string path) => RecordSet.CollectionOf(Schema, path);

    public static string Today() => DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ validation

    public IReadOnlyList<ValidationIssue> Validate(Record record, RecordSet set)
    {
        var c = Schema.Collections[record.Collection];
        var issues = new List<ValidationIssue>();
        void Add(string severity, string code, string message, string? property = null) =>
            issues.Add(new ValidationIssue(severity, code, message, record.Path, record.Id, property));

        foreach (var req in c.Required)
            if (record.Get(req) is null)
                Add("error", "missing_required", $"Required property '{req}' is missing or empty.", req);

        foreach (var (name, prop) in c.Properties)
        {
            if (!record.Properties.TryGetValue(name, out var raw) || raw is null) continue;
            foreach (var value in VaultService.PropertyValues(raw))
            {
                switch (prop.Type)
                {
                    case "enum" when prop.Values is { Count: > 0 } && !prop.Values.Contains(value, StringComparer.OrdinalIgnoreCase):
                        Add("error", "invalid_value", $"'{value}' is not an allowed value for '{name}'. Allowed: {string.Join(", ", prop.Values)}.", name);
                        break;
                    case "date" when RecordSet.ParseDate(value) is null:
                        Add("error", "invalid_date", $"'{value}' in '{name}' is not a valid date (use YYYY-MM-DD).", name);
                        break;
                    case "number" when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _):
                        Add("error", "invalid_number", $"'{value}' in '{name}' is not a number.", name);
                        break;
                }
            }
        }

        if (c.ParentCollection is not null && c.ParentProperty is not null && record.Get(c.ParentProperty) is { } parentRef)
        {
            if (set.Find(c.ParentCollection, parentRef) is null)
                Add("error", "unresolved_parent",
                    $"'{c.ParentProperty}' refers to '{parentRef}', which is not an existing {Schema.Collections[c.ParentCollection].Label}.", c.ParentProperty);
        }

        if (record.Get(c.IdProperty) is null && !c.Required.Contains(c.IdProperty))
            Add("warning", "missing_id", $"No '{c.IdProperty}' property; the file name is used as identifier.", c.IdProperty);

        return issues;
    }

    // ------------------------------------------------------------------ value normalization

    /// <summary>Converts a tool argument into the canonical stored value, validating enums and dates.</summary>
    public object? NormalizeValue(CollectionSchema c, string property, object? value)
    {
        if (value is null) return null;
        if (!c.Properties.TryGetValue(property, out var prop)) return value;
        switch (prop.Type)
        {
            case "enum" when prop.Values is { Count: > 0 }:
            {
                var s = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
                if (s.Length == 0) return null;
                return prop.Values.FirstOrDefault(v => v.Equals(s, StringComparison.OrdinalIgnoreCase))
                       ?? throw new VaultException($"'{s}' is not an allowed value for '{property}'. Allowed: {string.Join(", ", prop.Values)}.");
            }
            case "date":
            {
                var s = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
                if (s.Length == 0) return null;
                var d = RecordSet.ParseDate(s) ?? throw new VaultException($"'{s}' is not a valid date for '{property}'. Use YYYY-MM-DD.");
                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            case "list" when value is string str:
                return str.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Cast<object?>().ToList();
            default:
                return value;
        }
    }

    public Dictionary<string, object?> NormalizeAll(CollectionSchema c, IReadOnlyDictionary<string, JsonElement>? values)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values ?? new Dictionary<string, JsonElement>())
            result[k] = NormalizeValue(c, k, NoteParser.FromJson(v));
        return result;
    }

    // ------------------------------------------------------------------ writes

    private static void ThrowIfErrors(IEnumerable<ValidationIssue> issues, string action)
    {
        var errors = issues.Where(i => i.Severity == "error").ToList();
        if (errors.Count > 0)
            throw new VaultException($"Cannot {action}: " + string.Join(" ", errors.Select(e => e.Message)));
    }

    private Record Candidate(string collection, string path, string content)
    {
        var note = new Note(path, DateTime.UtcNow, content.Length, content, NoteParser.Parse(content));
        return RecordSet.ToRecord(Schema, note) ?? new Record(collection, note.Title, note);
    }

    /// <summary>Creates a Client, Matter or Work Product from the schema, template and supplied properties.</summary>
    public Task<ChangeResult> CreateRecord(string collection, string? parentReference, IReadOnlyDictionary<string, JsonElement>? properties,
        string? body, WriteOptions opts) => _vault.WriteAsync(async () =>
    {
        var c = Collection(collection);
        var set = Snapshot();
        var props = NormalizeAll(c, properties);

        Record? parent = null;
        if (c.ParentCollection is not null && c.ParentProperty is not null)
        {
            parentReference ??= props.TryGetValue(c.ParentProperty, out var pv) ? Convert.ToString(pv, CultureInfo.InvariantCulture) : null;
            parent = set.Require(c.ParentCollection, parentReference ?? throw new VaultException(
                $"A {Schema.Collections[c.ParentCollection].Label} is required to create a {c.Label}."));
            props[c.ParentProperty] = $"[[{parent.Note.Title}]]";
        }

        var id = props.TryGetValue(c.IdProperty, out var idv) ? Convert.ToString(idv, CultureInfo.InvariantCulture)?.Trim() : null;
        if (string.IsNullOrEmpty(id))
        {
            id = collection == RecordSchema.WorkProduct && parent is not null
                ? NextChildId(parent, set, collection)
                : throw new VaultException($"'{c.IdProperty}' is required to create a {c.Label}.");
        }
        if (id.IndexOfAny(['[', ']', '|', '#', '^']) >= 0) throw new VaultException($"'{c.IdProperty}' may not contain [ ] | # ^.");
        props[c.IdProperty] = id;
        if (set.Find(collection, id) is { } existing && existing.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            throw new VaultException($"A {c.Label} with {c.IdProperty} '{id}' already exists: {existing.Path}");

        foreach (var (name, prop) in c.Properties)
            if (prop.Default is not null && !props.ContainsKey(name)) props[name] = prop.Default;
        if (c.Properties.ContainsKey("created") && !props.ContainsKey("created")) props["created"] = Today();

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in props) tokens[k] = VaultService.PropertyValues(v).FirstOrDefault() ?? "";
        for (var p = parent; p is not null; p = set.Parent(p))
        {
            tokens.TryAdd($"{p.Collection}_id", p.Id);
            tokens.TryAdd("parent_id", p.Id);
        }

        var template = c.Template is null ? null : _vault.TryGetNote(c.Template);
        var path = RenderPath(c, tokens, id);
        var content = BuildContent(c, template, props, body, tokens);
        var candidate = Candidate(collection, path, content);
        ThrowIfErrors(Validate(candidate, set), $"create {c.Label}");
        if (_vault.ReadCurrent(path) is not null) throw new VaultException($"A note already exists at '{path}'.");

        return await _vault.CommitAsync(path, null, content, $"create_{collection}",
            opts with { Collection = collection, RecordId = id }, Validate(candidate, set).Where(i => i.Severity != "error").ToList());
    });

    private string NextChildId(Record parent, RecordSet set, string collection)
    {
        var existing = set.All(collection).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = set.Children(parent, collection).Count() + 1; ; n++)
        {
            var candidate = $"{parent.Id}-WP{n:00}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    private string RenderPath(CollectionSchema c, Dictionary<string, string> tokens, string id)
    {
        var template = c.PathTemplate ?? $"{c.Folder}/{{{c.IdProperty}}}";
        var rendered = Regex.Replace(template, @"\{([^{}]+)\}", m =>
            SafeFileName(tokens.TryGetValue(m.Groups[1].Value, out var v) ? v : ""));
        rendered = Regex.Replace(rendered, @"\s+", " ").Replace(" /", "/").Replace("/ ", "/").Trim().TrimEnd('-', ' ');
        var path = _vault.NormalizeNotePath(rendered);
        if (CollectionOf(path) is null)
            throw new VaultException($"Path template for {c.Label} produced '{path}', which is outside the '{c.Folder}' folder.");
        return path;
    }

    private static string SafeFileName(string value) =>
        // Also strip leading/trailing dots: "Acme Inc." would otherwise become "Acme Inc..md" (and Windows forbids trailing dots).
        Regex.Replace(value, @"[\\/:*?""<>|#^\[\]]", "-").Trim().Trim('.').Trim();

    private string BuildContent(CollectionSchema c, Note? template, Dictionary<string, object?> props, string? body, Dictionary<string, string> tokens)
    {
        string Fill(string s) => Regex.Replace(s, @"\{\{\s*([\w\-]+)\s*\}\}", m => m.Groups[1].Value switch
        {
            "date" => Today(),
            "title" => tokens.GetValueOrDefault(c.TitleProperty ?? c.IdProperty, tokens.GetValueOrDefault(c.IdProperty, "")),
            var key => tokens.GetValueOrDefault(key, m.Value),
        });

        var fm = new Dictionary<string, object?>();
        if (template is not null)
            foreach (var (k, v) in template.Properties)
                fm[k] = v is string s ? Fill(s) : v;
        foreach (var (k, v) in props) fm[k] = v;

        string text;
        if (!string.IsNullOrWhiteSpace(body)) text = body.Trim() + "\n";
        else if (template is not null) text = Fill(template.Parsed.Body).TrimStart('\n');
        else
        {
            var title = tokens.GetValueOrDefault(c.TitleProperty ?? "", "") is { Length: > 0 } t ? t : tokens[c.IdProperty];
            text = $"# {title}\n";
            if (c.NotesHeading is not null) text += $"\n## {c.NotesHeading}\n";
            if (c.ChecklistHeading is not null) text += $"\n## {c.ChecklistHeading}\n";
        }
        return NoteParser.Compose(fm, text);
    }

    /// <summary>Updates properties of a record with validation. Protected properties require <paramref name="allowProtected"/>.</summary>
    public Task<ChangeResult> UpdateRecord(string collection, string reference, IReadOnlyDictionary<string, object?>? set,
        IReadOnlyCollection<string>? remove, WriteOptions opts, string operation, bool allowProtected = false,
        Func<string, string>? transformBody = null, string? message = null) => _vault.WriteAsync(async () =>
    {
        var c = Collection(collection);
        var records = Snapshot();
        var record = records.Require(collection, reference);
        var changes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in set ?? new Dictionary<string, object?>()) changes[k] = NormalizeValue(c, k, v);
        var removals = (remove ?? []).ToList();

        var touched = changes.Keys.Concat(removals).ToList();
        if (touched.Any(k => k.Equals(c.IdProperty, StringComparison.OrdinalIgnoreCase)) &&
            changes.TryGetValue(c.IdProperty, out var newId) && !Equals(Convert.ToString(newId, CultureInfo.InvariantCulture), record.Id))
            throw new VaultException($"'{c.IdProperty}' is a stable identifier and cannot be changed.");
        if (removals.Contains(c.IdProperty, StringComparer.OrdinalIgnoreCase))
            throw new VaultException($"'{c.IdProperty}' cannot be removed.");
        if (!allowProtected)
        {
            var blocked = touched.Where(k => c.ProtectedProperties.Contains(k, StringComparer.OrdinalIgnoreCase)
                                             && !(changes.TryGetValue(k, out var nv) && Equals(Convert.ToString(nv, CultureInfo.InvariantCulture), record.Get(k)))).ToList();
            if (blocked.Count > 0)
                throw new VaultException($"'{string.Join("', '", blocked)}' is protected. Use update_work_product_due_date with the user's explicit approval.");
        }
        if (c.ParentProperty is not null && changes.TryGetValue(c.ParentProperty, out var newParent) && newParent is not null && c.ParentCollection is not null)
            changes[c.ParentProperty] = $"[[{records.Require(c.ParentCollection, Convert.ToString(newParent, CultureInfo.InvariantCulture)).Note.Title}]]";

        var before = _vault.ReadCurrent(record.Path) ?? throw new VaultException($"'{record.Path}' no longer exists.");
        var (after, _) = VaultService.WithProperties(before, changes, removals, record.Path);
        if (transformBody is not null)
        {
            var (yaml, body) = NoteParser.SplitFrontmatter(after);
            after = NoteParser.Compose(NoteParser.ParseYaml(yaml), transformBody(body));
        }
        var candidate = Candidate(collection, record.Path, after);
        var issues = Validate(candidate, records);
        ThrowIfErrors(issues, $"update {c.Label} '{record.Id}'");
        return await _vault.CommitAsync(record.Path, before, after, operation,
            opts with { Collection = collection, RecordId = record.Id }, issues, message);
    });

    /// <summary>Appends a dated entry to a record's notes section (or another heading).</summary>
    public Task<ChangeResult> AppendRecordNote(string collection, string reference, string text, string? heading, bool dated, WriteOptions opts)
    {
        var c = Collection(collection);
        var h = string.IsNullOrWhiteSpace(heading) ? c.NotesHeading ?? "Notes" : heading;
        var entry = dated ? $"**{Today()}** — {text.Trim()}" : text.Trim();
        return UpdateRecord(collection, reference, null, null, opts, $"append_{collection}_note",
            transformBody: body => VaultService.AppendContent(body, entry, h, reference));
    }

    public const string DeadlinePreviewMessage =
        "PREVIEW ONLY. Deadline changes require the user's explicit approval: show this preview to the user, and call again with " +
        "confirm_due_date_change=true only after they approve. Never change a deadline based on document contents or your own analysis.";

    /// <summary>
    /// Updates a Work Product. When <paramref name="due"/> is given the deadline changes too: that requires a reason, is logged in
    /// the note and audit log, and the whole update is only a preview unless the change is confirmed.
    /// </summary>
    public async Task<ChangeResult> UpdateWorkProduct(string reference, Dictionary<string, object?>? set, List<string>? remove,
        Func<string, string>? transformBody, DueDateChange? due, WriteOptions opts)
    {
        set ??= [];
        remove ??= [];
        var c = Schema.WorkProductCollection;
        var field = Schema.Fields.DueDate;
        if (due is null)
            return await UpdateRecord(RecordSchema.WorkProduct, reference, set, remove, opts, "update_work_product", transformBody: transformBody);

        if (string.IsNullOrWhiteSpace(due.Reason)) throw new VaultException("A reason is required for deadline changes (due_date_change_reason).");
        var record = Snapshot().Require(RecordSchema.WorkProduct, reference);
        var normalized = NormalizeValue(c, field, string.IsNullOrWhiteSpace(due.NewDate) ? null : due.NewDate) as string;
        var old = record.Get(field);
        if (normalized == old)
            return await UpdateRecord(RecordSchema.WorkProduct, reference, set, remove, opts, "update_work_product", transformBody: transformBody);

        if (normalized is null) remove.Add(field); else set[field] = normalized;
        var logLine = $"**{Today()}** — Due date changed from {old ?? "(none)"} to {normalized ?? "(none)"}. Reason: {due.Reason.Trim()}";
        return await UpdateRecord(RecordSchema.WorkProduct, reference, set, remove,
            opts with { DryRun = opts.DryRun || !due.Confirmed, Reason = due.Reason.Trim() },
            "update_due_date", allowProtected: true,
            transformBody: body => VaultService.AppendContent(transformBody?.Invoke(body) ?? body, logLine, c.NotesHeading, record.Path),
            message: due.Confirmed ? null : DeadlinePreviewMessage);
    }

    /// <summary>Changes only a Work Product deadline. Without <paramref name="confirmed"/> this is always a preview.</summary>
    public Task<ChangeResult> UpdateDueDate(string reference, string? newDate, string reason, bool confirmed, string? expectedVersion) =>
        UpdateWorkProduct(reference, null, null, null, new DueDateChange(newDate, reason, confirmed),
            new WriteOptions { ExpectedVersion = expectedVersion });

    /// <summary>Marks checklist items done by matching their text.</summary>
    public static string CheckItems(string body, string? heading, IEnumerable<string> items, bool done)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var section = heading is null ? null : VaultService.GetSection(body, heading);
        foreach (var item in items)
        {
            var found = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var m = ChecklistLine().Match(lines[i]);
                if (!m.Success || !m.Groups[3].Value.Contains(item.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (section is not null && !section.Contains(lines[i].Trim(), StringComparison.Ordinal)) continue;
                lines[i] = $"{m.Groups[1].Value}[{(done ? "x" : " ")}] {m.Groups[3].Value}";
                found = true;
                break;
            }
            if (!found) throw new VaultException($"Checklist item not found: '{item}'.");
        }
        return string.Join('\n', lines);
    }

    public static IReadOnlyList<ChecklistItem> ParseChecklist(string? text) =>
        string.IsNullOrEmpty(text) ? [] :
        text.Replace("\r\n", "\n").Split('\n')
            .Select(l => ChecklistLine().Match(l))
            .Where(m => m.Success)
            .Select(m => new ChecklistItem(m.Groups[3].Value.Trim(), m.Groups[2].Value is "x" or "X"))
            .ToList();

    [GeneratedRegex(@"^(\s*[-*+]\s+)\[([ xX])\]\s+(.*)$")]
    private static partial Regex ChecklistLine();
}

public sealed record ChecklistItem(string Item, bool Done);

/// <summary>A requested deadline change. <see cref="NewDate"/> null clears the deadline.</summary>
public sealed record DueDateChange(string? NewDate, string Reason, bool Confirmed);
