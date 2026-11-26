using System.Globalization;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Server.Records;

/// <summary>A note that belongs to a schema collection.</summary>
public sealed record Record(string Collection, string Id, Note Note)
{
    public string Path => Note.Path;
    public Dictionary<string, object?> Properties => Note.Properties;

    public string? Get(string property) =>
        Properties.TryGetValue(property, out var v) ? VaultService.PropertyValues(v).FirstOrDefault() : null;

    public DateOnly? GetDate(string property) => RecordSet.ParseDate(Get(property));
}

/// <summary>
/// A consistent snapshot of all records with relationship resolution. Built per request from the
/// (incrementally refreshed) note index, so it always reflects the files on disk.
/// </summary>
public sealed class RecordSet
{
    private readonly RecordSchema _schema;
    private readonly Dictionary<Record, Record?> _parents = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyCollection<Note> Notes { get; }
    public IReadOnlyDictionary<string, List<Record>> ByCollection { get; }

    public RecordSet(RecordSchema schema, IReadOnlyCollection<Note> notes)
    {
        _schema = schema;
        Notes = notes;
        var by = schema.Collections.Keys.ToDictionary(k => k, _ => new List<Record>(), StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes)
        {
            var r = ToRecord(schema, note);
            if (r is not null) by[r.Collection].Add(r);
        }
        ByCollection = by;
    }

    public static string? CollectionOf(RecordSchema schema, string path) =>
        schema.Collections
            .Where(c => c.Value.Folder.Length > 0 && path.StartsWith(c.Value.Folder.Trim('/') + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Value.Folder.Length)
            .Select(c => c.Key)
            .FirstOrDefault();

    public static Record? ToRecord(RecordSchema schema, Note note)
    {
        var collection = CollectionOf(schema, note.Path);
        if (collection is null) return null;
        var c = schema.Collections[collection];
        var id = note.Properties.TryGetValue(c.IdProperty, out var v) ? VaultService.PropertyValues(v).FirstOrDefault() : null;
        return new Record(collection, id ?? note.Title, note);
    }

    public IReadOnlyList<Record> All(string collection) => ByCollection[collection];

    /// <summary>Finds a record by identifier, note name, path or [[link]].</summary>
    public Record? Find(string collection, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var r = VaultService.StripLink(reference);
        var records = ByCollection[collection];
        var match = records.FirstOrDefault(x => x.Id.Equals(r, StringComparison.OrdinalIgnoreCase))
                    ?? records.FirstOrDefault(x => x.Note.Title.Equals(r, StringComparison.OrdinalIgnoreCase))
                    ?? records.FirstOrDefault(x => x.Path.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                                                   x.Path[..^3].Equals(r, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        var resolved = VaultService.ResolveLink(r, null, Notes);
        return resolved is null ? null : records.FirstOrDefault(x => x.Path.Equals(resolved, VaultService.PathComparison));
    }

    public Record Require(string collection, string? reference) =>
        Find(collection, reference) ?? throw new VaultException(
            $"{_schema.Collections[collection].Label} not found: '{reference}'.");

    public Record? Parent(Record record)
    {
        if (_parents.TryGetValue(record, out var cached)) return cached;
        var c = _schema.Collections[record.Collection];
        Record? parent = null;
        if (c.ParentCollection is not null && c.ParentProperty is not null)
        {
            var value = record.Get(c.ParentProperty);
            parent = value is null ? null : Find(c.ParentCollection, value);
        }
        _parents[record] = parent;
        return parent;
    }

    public IEnumerable<Record> Children(Record parent, string childCollection) =>
        ByCollection[childCollection].Where(r => Parent(r) is { } p && ReferenceEquals(p, parent));

    /// <summary>Walks up the parent chain to the ancestor of the given collection.</summary>
    public Record? Ancestor(Record record, string collection)
    {
        var current = record;
        for (var depth = 0; current is not null && depth < 5; depth++)
        {
            if (current.Collection.Equals(collection, StringComparison.OrdinalIgnoreCase)) return current;
            current = Parent(current);
        }
        return null;
    }

    public static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt)) return DateOnly.FromDateTime(dt);
        return null;
    }
}
