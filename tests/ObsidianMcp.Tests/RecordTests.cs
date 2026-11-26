using System.Text.Json;
using ObsidianMcp.Server.Records;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Tests;

/// <summary>Client / Matter / Work Product behaviour against a copy of samples/demo-vault.</summary>
public sealed class RecordTests : IDisposable
{
    private readonly TestVault _tv = new(TestVault.DemoVaultPath);
    private readonly VaultService _vault;
    private readonly RecordService _records;
    private readonly DocketService _docket;
    private readonly ContextService _context;
    private static string Day(int offset) => DocketService.Today.AddDays(offset).ToString("yyyy-MM-dd");

    public RecordTests()
    {
        var options = _tv.OptionsFor();
        _vault = new VaultService(options);
        _records = new RecordService(_vault, options);
        _docket = new DocketService(_records);
        _context = new ContextService(_records, _docket);
    }

    public void Dispose() => _tv.Dispose();

    private static Dictionary<string, JsonElement> Props(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

    private Task<ChangeResult> NewWorkProduct(string matter, object props) =>
        _records.CreateRecord(RecordSchema.WorkProduct, matter, Props(props), null, WriteOptions.Default);

    [Fact]
    public void Loads_schema_and_classifies_records()
    {
        Assert.EndsWith("schema.json", _records.SchemaSource);
        var set = _records.Snapshot();
        Assert.Single(set.All(RecordSchema.Client));
        Assert.Equal(3, set.All(RecordSchema.Matter).Count);
        Assert.Equal(3, set.All(RecordSchema.WorkProduct).Count);

        var wp = set.Require(RecordSchema.WorkProduct, "DEMO-101-A-WP02");
        Assert.Equal("DEMO-101-A", set.Parent(wp)!.Id);
        Assert.Equal("DEMO", set.Ancestor(wp, RecordSchema.Client)!.Id);
    }

    [Fact]
    public void Demo_vault_has_no_errors()
    {
        Assert.DoesNotContain(_docket.Exceptions(), i => i.Severity == "error");
    }

    [Fact]
    public async Task Creates_work_product_with_generated_id_path_and_template()
    {
        var result = await NewWorkProduct("DEMO-102", new
        {
            work_product_type = "inventor interview", // case-insensitive enum, stored canonically
            due_date = Day(10),
            next_action = "Send questions",
        });
        Assert.Equal("Work Products/DEMO-102/DEMO-102-WP02 Inventor Interview.md", result.Path);
        var note = _vault.GetNote(result.Path);
        Assert.Equal("Inventor Interview", note.Properties["work_product_type"]);
        Assert.Equal("[[DEMO-102]]", note.Properties["matter"]);
        Assert.Equal("Not Started", note.Properties["status"]);
        Assert.Contains("## Working Notes", note.Content); // from Templates/Work Product.md
        Assert.Contains("# Inventor Interview — DEMO-102", note.Content);
    }

    [Fact]
    public async Task Rejects_invalid_records()
    {
        await Assert.ThrowsAsync<VaultException>(() => NewWorkProduct("NOPE-1", new { work_product_type = "Appeal" }));
        await Assert.ThrowsAsync<VaultException>(() => NewWorkProduct("DEMO-102", new { work_product_type = "Not A Type" }));
        await Assert.ThrowsAsync<VaultException>(() => NewWorkProduct("DEMO-102", new { work_product_type = "Appeal", due_date = "someday" }));
        await Assert.ThrowsAsync<VaultException>(() => _records.CreateRecord(RecordSchema.Matter, "DEMO",
            Props(new { docket_number = "DEMO-101-A" }), null, WriteOptions.Default)); // duplicate id
    }

    [Fact]
    public async Task Due_date_is_protected_from_generic_updates()
    {
        var ex = await Assert.ThrowsAsync<VaultException>(() => _records.UpdateRecord(RecordSchema.WorkProduct, "DEMO-101-A-WP02",
            new Dictionary<string, object?> { ["due_date"] = "2027-02-01" }, null, WriteOptions.Default, "update_note_properties"));
        Assert.Contains("protected", ex.Message);
        await Assert.ThrowsAsync<VaultException>(() => _records.UpdateRecord(RecordSchema.WorkProduct, "DEMO-101-A-WP02",
            new Dictionary<string, object?> { ["work_product_id"] = "OTHER" }, null, WriteOptions.Default, "x"));
    }

    [Fact]
    public async Task Due_date_change_requires_confirmation_and_is_audited()
    {
        const string path = "Work Products/DEMO-101-A/DEMO-101-A-WP02 Final Office Action Response.md";
        var preview = await _records.UpdateDueDate("DEMO-101-A-WP02", "2027-02-01", "Extension of time filed", confirmed: false, null);
        Assert.True(preview.DryRun);
        Assert.Contains("approval", preview.Message);
        Assert.Equal("2027-01-01", _vault.GetNote(path).Properties["due_date"]);

        await Assert.ThrowsAsync<VaultException>(() => _records.UpdateDueDate("DEMO-101-A-WP02", "2027-02-01", "", confirmed: true, null));

        var applied = await _records.UpdateDueDate("DEMO-101-A-WP02", "2027-02-01", "Extension of time filed", confirmed: true, null);
        Assert.False(applied.DryRun);
        var change = Assert.Single(applied.PropertyChanges);
        Assert.Equal("due_date", change.Property);
        var note = _vault.GetNote(path);
        Assert.Equal("2027-02-01", note.Properties["due_date"]);
        Assert.Contains("Due date changed from 2027-01-01 to 2027-02-01", note.Content);
        var audit = _vault.Audit.Read(e => e.Operation == "update_due_date", 5);
        Assert.Equal("Extension of time filed", Assert.Single(audit).Reason);
    }

    [Fact]
    public async Task Docket_queries_filter_and_sort()
    {
        await NewWorkProduct("DEMO-102", new { work_product_type = "Appeal", due_date = Day(-2) });       // overdue
        await NewWorkProduct("DEMO-102", new { work_product_type = "Client Report", due_date = Day(5) }); // due soon
        await NewWorkProduct("DEMO-101-B", new { work_product_type = "Information Disclosure Statement", status = "Ready for Filing", due_date = Day(20) });

        var (overdue, _) = _docket.Query(new DocketQuery { OnlyOverdue = true });
        Assert.Equal("Appeal", Assert.Single(overdue).Type);

        var (week, _) = _docket.Query(new DocketQuery { DueDateFrom = DocketService.Today, DueDateTo = DocketService.Today.AddDays(7) });
        Assert.Equal("Client Report", Assert.Single(week).Type);

        var (oa, _) = _docket.Query(new DocketQuery { WorkProductTypes = ["Office Action"] });
        Assert.Equal("DEMO-101-A-WP02", Assert.Single(oa).Id); // the filed one is excluded

        var (oaAll, _) = _docket.Query(new DocketQuery { WorkProductTypes = ["Office Action"], IncludeCompleted = true });
        Assert.Equal(2, oaAll.Count);

        var (waiting, _) = _docket.Query(new DocketQuery { WaitingOn = "*" });
        Assert.Equal("DEMO-102-WP01", Assert.Single(waiting).Id);

        var (ready, _) = _docket.Query(new DocketQuery { OnlyReadyForFiling = true });
        Assert.Single(ready);

        var (byClient, _) = _docket.Query(new DocketQuery { Client = "DEMO" });
        Assert.Equal(byClient.Select(i => i.DueDate).Order(StringComparer.Ordinal), byClient.Select(i => i.DueDate));

        var (matter, _) = _docket.Query(new DocketQuery { Matter = "DEMO-101-B" });
        Assert.Single(matter);
    }

    [Fact]
    public async Task Summary_counts_docket()
    {
        await NewWorkProduct("DEMO-102", new { work_product_type = "Appeal", due_date = Day(-1) });
        var summary = JsonSerializer.SerializeToElement(_docket.Summary(null, 14));
        Assert.Equal(3, summary.GetProperty("active").GetInt32());
        Assert.Equal(1, summary.GetProperty("overdue").GetInt32());
        Assert.Equal(1, summary.GetProperty("awaiting_others").GetInt32());
    }

    [Fact]
    public void Matter_context_assembles_everything()
    {
        var ctx = JsonSerializer.SerializeToElement(_context.MatterContext("DEMO-101-A", 10));
        Assert.Equal("DEMO-101-A", ctx.GetProperty("matter").GetProperty("id").GetString());
        var prefs = ctx.GetProperty("client").GetProperty("preferences");
        Assert.Contains("robotic end effector", prefs.GetProperty("Drafting Preferences").GetString());
        Assert.Contains("5 business days", prefs.GetProperty("Reporting Requirements").GetString());

        var related = ctx.GetProperty("related_matters").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Equal(["DEMO-101-B"], related);

        Assert.Equal("DEMO-101-A-WP02", Assert.Single(ctx.GetProperty("active_work_products").EnumerateArray()).GetProperty("Id").GetString());
        var history = Assert.Single(ctx.GetProperty("history").EnumerateArray());
        Assert.Contains("teaches away", history.GetProperty("summary").GetProperty("Strategy").GetString());
        Assert.Contains("Matters/DEMO/DEMO-101-A/Office Action 2026-10-01.pdf",
            ctx.GetProperty("attachments").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public void Work_product_context_includes_workflow_and_checklist()
    {
        var ctx = JsonSerializer.SerializeToElement(_context.WorkProductContext("DEMO-101-A-WP02", 10));
        var checklist = ctx.GetProperty("work_product").GetProperty("checklist").EnumerateArray().ToList();
        Assert.Equal(5, checklist.Count);
        Assert.True(checklist[0].GetProperty("Done").GetBoolean());
        Assert.Equal("Workflows/Office Action Response.md", ctx.GetProperty("workflow").GetProperty("path").GetString());
        Assert.Equal("DEMO-101-A-WP01", Assert.Single(ctx.GetProperty("prior_work_products").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Complete_and_checklist_updates()
    {
        var c = _records.Schema.WorkProductCollection;
        await _records.UpdateRecord(RecordSchema.WorkProduct, "DEMO-101-A-WP02", null, null, WriteOptions.Default, "update_work_product",
            transformBody: b => RecordService.CheckItems(b, c.ChecklistHeading, ["examiner interview"], done: true));
        Assert.Contains("- [x] Schedule examiner interview", _vault.GetNote("DEMO-101-A-WP02 Final Office Action Response").Content);

        await _records.UpdateRecord(RecordSchema.WorkProduct, "DEMO-101-A-WP02",
            new Dictionary<string, object?> { ["status"] = "completed", ["completed_date"] = Day(0) }, null, WriteOptions.Default, "complete_work_product");
        var (active, _) = _docket.Query(new DocketQuery { Matter = "DEMO-101-A" });
        Assert.Empty(active);
    }

    [Fact]
    public async Task Exceptions_find_data_problems()
    {
        _tv.Write("Work Products/DEMO-102/Orphan.md", "---\nwork_product_id: ORPHAN-1\nmatter: \"[[NOPE]]\"\nwork_product_type: Appeal\nstatus: Bogus\n---\n");
        _tv.Write("Matters/DEMO/DEMO-102_DiskStation_Conflict.md", "x");
        await NewWorkProduct("DEMO-102", new { work_product_type = "Appeal", status = "Waiting" });
        var codes = _docket.Exceptions().Select(i => i.Code).ToHashSet();
        Assert.Contains("unresolved_parent", codes);
        Assert.Contains("invalid_value", codes);
        Assert.Contains("sync_conflict", codes);
        Assert.Contains("missing_due_date", codes);
        Assert.Contains("waiting_without_dependency", codes);
    }

    [Fact]
    public async Task Append_record_note_adds_dated_entry()
    {
        await _records.AppendRecordNote(RecordSchema.Matter, "DEMO-101-A", "Called examiner; interview set.", null, dated: true, WriteOptions.Default);
        var content = _vault.GetNote("DEMO-101-A").Content;
        Assert.Contains($"**{Day(0)}** — Called examiner; interview set.", content);
        Assert.True(content.IndexOf("Called examiner") > content.IndexOf("## Matter Notes"));
    }
}
