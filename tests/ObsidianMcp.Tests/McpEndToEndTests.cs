using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ObsidianMcp.Tests;

/// <summary>Launches the real server over stdio against a copy of the demo vault and talks to it with the MCP client SDK.</summary>
public sealed class McpEndToEndTests : IAsyncLifetime
{
    private readonly TestVault _tv = new(TestVault.DemoVaultPath);
    private McpClient _client = null!;

    public async Task InitializeAsync() => _client = await Connect();

    private async Task<McpClient> Connect(params string[] extraArgs)
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "obsidian-mcp.dll");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "obsidian",
            Command = "dotnet",
            Arguments = [serverDll, "--vault", _tv.Root, .. extraArgs],
        });
        return await McpClient.CreateAsync(transport);
    }

    public static readonly string[] MvpTools =
    [
        "get_vault_info", "search_notes", "get_note", "create_note", "update_note",
        "list_clients", "get_client", "get_client_matters",
        "list_matters", "get_matter", "get_matter_context", "create_matter", "update_matter",
        "get_work_product", "get_work_product_context", "create_work_product", "update_work_product", "complete_work_product",
        "get_docket", "get_docket_summary", "get_docket_exceptions",
        "get_template", "get_workflow",
        "get_schema", "get_audit_log",
    ];

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        _tv.Dispose();
    }

    private async Task<JsonElement> Call(string tool, Dictionary<string, object?> args)
    {
        var result = await _client.CallToolAsync(tool, args);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.True(result.IsError != true, $"{tool} failed: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task Exposes_exactly_the_25_mvp_tools()
    {
        var tools = (await _client.ListToolsAsync()).Select(t => t.Name).Order().ToList();
        Assert.Equal(MvpTools.Order(), tools);
        Assert.Equal("obsidian-vault", _client.ServerInfo?.Name);
    }

    [Fact]
    public async Task Read_only_mode_exposes_18_tools()
    {
        await using var ro = await Connect("--read-only");
        var tools = await ro.ListToolsAsync();
        Assert.Equal(18, tools.Count);
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
    }

    [Fact]
    public async Task Extended_and_search_fetch_tools_are_opt_in()
    {
        await using var ext = await Connect("--extended-tools", "--search-fetch");
        var tools = (await ext.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        foreach (var name in new[] { "get_attachment", "search_knowledge", "list_folder", "create_client", "archive_note", "search", "fetch" })
            Assert.Contains(name, tools);
        Assert.DoesNotContain("delete_note", tools); // deletion protection

        var ctx = JsonDocument.Parse(string.Concat((await ext.CallToolAsync("get_matter_context", new Dictionary<string, object?> { ["matter"] = "DEMO-101-A" }))
            .Content.OfType<TextContentBlock>().Select(c => c.Text))).RootElement;
        var attachment = ctx.GetProperty("attachments")[0].GetString();
        var doc = JsonDocument.Parse(string.Concat((await ext.CallToolAsync("get_attachment", new Dictionary<string, object?> { ["path"] = attachment }))
            .Content.OfType<TextContentBlock>().Select(c => c.Text))).RootElement;
        Assert.Contains("FINAL OFFICE ACTION", doc.GetProperty("text").GetString());
        Assert.Equal(64, doc.GetProperty("info").GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public async Task Docket_workflow_end_to_end()
    {
        var summary = await Call("get_docket_summary", []);
        Assert.Equal(2, summary.GetProperty("active").GetInt32());

        var created = await Call("create_work_product", new()
        {
            ["matter"] = "DEMO-101-B",
            ["properties"] = new Dictionary<string, object?> { ["work_product_type"] = "Client Report", ["priority"] = "High" },
        });
        var id = "DEMO-101-B-WP01";
        Assert.Contains(id, created.GetProperty("path").GetString());

        var docket = await Call("get_docket", new() { ["client"] = "DEMO", ["priorities"] = new[] { "High" } });
        Assert.Equal(2, docket.GetProperty("total_matches").GetInt32());

        var ctx = await Call("get_work_product_context", new() { ["work_product"] = id });
        var version = ctx.GetProperty("work_product").GetProperty("version").GetString();

        await Call("update_work_product", new() { ["work_product"] = id, ["next_action"] = "Send report", ["expected_version"] = version });

        // A stale version is rejected.
        var stale = await _client.CallToolAsync("update_work_product", new Dictionary<string, object?>
            { ["work_product"] = id, ["status"] = "In Progress", ["expected_version"] = version });
        Assert.True(stale.IsError);

        // Generic tools cannot bypass deadline protection.
        var bypass = await _client.CallToolAsync("update_note", new Dictionary<string, object?>
            { ["path"] = id, ["set_properties"] = new Dictionary<string, object?> { ["due_date"] = "2030-01-01" } });
        Assert.True(bypass.IsError);

        // Deadline change through update_work_product: preview until confirmed.
        var preview = await Call("update_work_product", new()
            { ["work_product"] = id, ["due_date"] = "2027-03-01", ["due_date_change_reason"] = "Client instruction", ["next_action"] = "Draft" });
        Assert.True(preview.GetProperty("dry_run").GetBoolean());
        var wp = await Call("get_work_product", new() { ["work_product"] = id });
        Assert.False(wp.GetProperty("docket").TryGetProperty("due_date", out var unchanged) && unchanged.ValueKind != JsonValueKind.Null);
        Assert.Equal("Send report", wp.GetProperty("docket").GetProperty("next_action").GetString()); // the whole call was a preview

        var noReason = await _client.CallToolAsync("update_work_product", new Dictionary<string, object?>
            { ["work_product"] = id, ["due_date"] = "2027-03-01", ["confirm_due_date_change"] = true });
        Assert.True(noReason.IsError);

        await Call("update_work_product", new()
        {
            ["work_product"] = id, ["due_date"] = "2027-03-01", ["due_date_change_reason"] = "Client instruction",
            ["confirm_due_date_change"] = true, ["append_note"] = "Report outline agreed.",
        });
        wp = await Call("get_work_product", new() { ["work_product"] = id });
        Assert.Equal("2027-03-01", wp.GetProperty("docket").GetProperty("due_date").GetString());
        Assert.Contains("Report outline agreed.", wp.GetProperty("content").GetString());
        var audit = await Call("get_audit_log", new() { ["record_id"] = id, ["operation"] = "update_due_date" });
        Assert.Equal("Client instruction", audit[0].GetProperty("reason").GetString());

        // get_matter, get_client_matters, update_matter, get_template.
        var matter = await Call("get_matter", new() { ["matter"] = "DEMO-101-B" });
        Assert.Contains(matter.GetProperty("work_products").EnumerateArray(), w => w.GetProperty("id").GetString() == id);
        var matters = await Call("get_client_matters", new() { ["client"] = "DEMO" });
        Assert.Equal(3, matters.GetArrayLength());
        await Call("update_matter", new() { ["matter"] = "DEMO-101-B", ["set_properties"] = new Dictionary<string, object?> { ["application_number"] = "18/000,002" }, ["append_note"] = "Filed." });
        Assert.Equal("18/000,002", (await Call("get_matter", new() { ["matter"] = "DEMO-101-B" })).GetProperty("properties").GetProperty("application_number").GetString());
        var template = await Call("get_template", new() { ["name"] = "Work Product" });
        Assert.Contains("matter_id", template.GetProperty("placeholders").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Errors_are_reported_to_the_model()
    {
        var result = await _client.CallToolAsync("get_note", new Dictionary<string, object?> { ["path"] = "../../etc/passwd" });
        Assert.True(result.IsError);
        Assert.Contains("..", string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
    }
}
