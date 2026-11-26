using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ObsidianMcp.Tests;

/// <summary>Launches the real server over stdio against a copy of the demo vault and talks to it with the MCP client SDK.</summary>
public sealed class McpEndToEndTests : IAsyncLifetime
{
    private readonly TestVault _tv = new(TestVault.DemoVaultPath);
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "obsidian-mcp.dll");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "obsidian",
            Command = "dotnet",
            Arguments = [serverDll, "--vault", _tv.Root],
        });
        _client = await McpClient.CreateAsync(transport);
    }

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
    public async Task Lists_expected_tools()
    {
        var tools = (await _client.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        foreach (var name in new[] { "get_docket", "get_docket_summary", "get_matter_context", "get_work_product_context",
                     "create_work_product", "update_work_product_due_date", "get_attachment", "search_knowledge" })
            Assert.Contains(name, tools);
        Assert.DoesNotContain("delete_note", tools); // deletion protection
        Assert.DoesNotContain("fetch", tools);       // search/fetch are http-only by default
        Assert.Equal("obsidian-vault", _client.ServerInfo?.Name);
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
        var bypass = await _client.CallToolAsync("update_note_properties", new Dictionary<string, object?>
            { ["path"] = id, ["set"] = new Dictionary<string, object?> { ["due_date"] = "2030-01-01" } });
        Assert.True(bypass.IsError);
    }

    [Fact]
    public async Task Matter_context_and_attachment_text()
    {
        var ctx = await Call("get_matter_context", new() { ["matter"] = "DEMO-101-A" });
        Assert.Equal("DEMO-101-A", ctx.GetProperty("matter").GetProperty("id").GetString());
        var attachment = ctx.GetProperty("attachments")[0].GetString();

        var doc = await Call("get_attachment", new() { ["path"] = attachment });
        Assert.Contains("FINAL OFFICE ACTION", doc.GetProperty("text").GetString());
        Assert.Equal(64, doc.GetProperty("info").GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public async Task Errors_are_reported_to_the_model()
    {
        var result = await _client.CallToolAsync("get_note", new Dictionary<string, object?> { ["path"] = "../../etc/passwd" });
        Assert.True(result.IsError);
        Assert.Contains("..", string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
    }
}
