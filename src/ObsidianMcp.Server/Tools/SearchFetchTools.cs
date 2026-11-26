using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Server.Tools;

/// <summary>
/// The "search" + "fetch" pair in the exact shape OpenAI requires for ChatGPT connectors used in
/// deep research / company knowledge: each returns a single text content item holding a JSON object.
/// </summary>
[McpServerToolType]
public sealed class SearchFetchTools(VaultService vault)
{
    [McpServerTool(Name = "search", Title = "Search vault", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the user's Obsidian notes and returns matching note ids. Use 'fetch' to read a note.")]
    public CallToolResult Search([Description("Search query.")] string query)
    {
        var results = string.IsNullOrWhiteSpace(query)
            ? vault.RecentNotes(20).Select(n => new { id = n.Path, title = n.Title, url = vault.ObsidianUrl(n.Path) })
            : vault.Search(query, limit: 20).Select(h => new { id = h.Path, title = h.Title, url = vault.ObsidianUrl(h.Path) });
        return Json(new { results });
    }

    [McpServerTool(Name = "fetch", Title = "Fetch note", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Retrieves the full content of a note by the id returned from 'search'.")]
    public CallToolResult Fetch([Description("Note id (vault-relative path).")] string id)
    {
        var note = vault.GetNote(id);
        return Json(new
        {
            id = note.Path,
            title = note.Title,
            text = note.Content,
            url = vault.ObsidianUrl(note.Path),
            metadata = new { tags = string.Join(", ", note.Parsed.Tags), modified = note.Modified.ToString("O") },
        });
    }

    private static CallToolResult Json(object value) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value) }],
    };
}
