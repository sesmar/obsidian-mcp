using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ObsidianMcp.Tests;

/// <summary>Launches the real server over stdio and talks to it with the MCP client SDK.</summary>
public sealed class McpEndToEndTests : IAsyncLifetime
{
    private readonly TestVault _tv = new();
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

    [Fact]
    public async Task Lists_expected_tools()
    {
        var tools = (await _client.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Assert.Contains("search_notes", tools);
        Assert.Contains("read_note", tools);
        Assert.Contains("append_to_note", tools);
        Assert.DoesNotContain("fetch", tools); // search/fetch are http-only by default
        Assert.Equal("obsidian-vault", _client.ServerInfo?.Name);
    }

    [Fact]
    public async Task Search_then_read_then_append()
    {
        var search = await _client.CallToolAsync("search_notes", new Dictionary<string, object?> { ["query"] = "budget" });
        Assert.NotEqual(true, search.IsError);
        Assert.Contains("Project Alpha", Text(search));

        var read = await _client.CallToolAsync("read_note", new Dictionary<string, object?> { ["path"] = "Project Alpha" });
        using var doc = JsonDocument.Parse(Text(read));
        Assert.Equal("Projects/Project Alpha.md", doc.RootElement.GetProperty("path").GetString());

        var append = await _client.CallToolAsync("append_to_note",
            new Dictionary<string, object?> { ["path"] = "Inbox", ["content"] = "hello from MCP" });
        Assert.NotEqual(true, append.IsError);
        Assert.Contains("hello from MCP", _tv.Read("Inbox.md"));
    }

    [Fact]
    public async Task Errors_are_reported_to_the_model()
    {
        var result = await _client.CallToolAsync("read_note", new Dictionary<string, object?> { ["path"] = "../../etc/passwd" });
        Assert.True(result.IsError);
        Assert.Contains("..", Text(result));
    }

    private static string Text(CallToolResult r) => string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
