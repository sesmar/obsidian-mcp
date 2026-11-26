# obsidian-mcp

An [MCP](https://modelcontextprotocol.io) server, written in C# / .NET 10, that exposes an Obsidian vault on your
computer to Claude, ChatGPT, Gemini and any other MCP client.

It works directly on the vault's markdown files, so **Obsidian does not need to be running** and no Obsidian plugin is required.
It understands Obsidian conventions: `[[wikilinks]]` (with `#heading` / `|alias`), `![[embeds]]`, `#nested/tags`,
YAML frontmatter properties, the `.trash` folder and Daily Notes settings.

Built on the official [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).

## Tools

| Tool | What it does |
|---|---|
| `get_vault_info` | Overview: note count, top folders, top tags, recent notes |
| `list_folder` | List notes, files and sub-folders |
| `read_note` | Full content + parsed frontmatter, tags, headings, links. Accepts a path or just a note name |
| `search_notes` | Ranked full-text search with snippets; `"quoted phrases"`, folder and tag filters |
| `list_recent_notes` | Most recently modified notes |
| `list_tags` / `find_notes_by_tag` | Tag index (frontmatter + inline), nested tags included |
| `get_backlinks` / `get_outgoing_links` | Link graph, resolved the way Obsidian resolves links |
| `read_daily_note` | Daily note for a date (uses `.obsidian/daily-notes.json`) |
| `create_note` | Create a note (folders created as needed) |
| `append_to_note` | Append to the end of a note or of a heading's section |
| `replace_in_note` | Exact find-and-replace edit (unique match required) |
| `update_frontmatter` | Set/remove properties without touching the body |
| `move_note` | Move/rename and update links in other notes |
| `delete_note` | Move to `.trash` (default) or delete permanently |
| `append_to_daily_note` | Append to today's (or any date's) daily note |
| `search` / `fetch` | The generic pair ChatGPT connectors and deep research expect (on by default in HTTP mode) |

Write tools are not registered at all with `--read-only`.

## Safety

* Every path is confined to the vault: `..`, absolute paths and symlinks that point outside the vault are rejected.
* `.obsidian`, `.trash`, `.git` and other dot-folders are never exposed (add more with `--exclude <folder>`).
* Deletes go to the vault's `.trash` folder unless `permanent=true`.
* HTTP mode refuses to start without an API key (unless you pass `--no-auth`), uses constant-time key comparison,
  and rejects browser requests from non-local `Origin`s (DNS-rebinding protection). It listens on `127.0.0.1` by default.

## Build & install

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet test                                   # 26 unit + end-to-end tests
dotnet pack src/ObsidianMcp.Server -c Release -o artifacts
dotnet tool install -g --add-source ./artifacts obsidian-mcp
obsidian-mcp --help
```

This puts an `obsidian-mcp` command on your PATH (`~/.dotnet/tools`). Alternatively build a single
self-contained executable that doesn't need .NET installed:

```bash
dotnet publish src/ObsidianMcp.Server -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
# use osx-arm64, osx-x64, linux-x64 ... as appropriate
```

## Usage

```
obsidian-mcp --vault <path> [--read-only] [--transport stdio|http] [--urls http://127.0.0.1:5199] [--api-key <key>]
```

* **stdio** (default): the client launches the server as a subprocess. Use for desktop/CLI apps.
* **http**: Streamable HTTP endpoint at `/mcp`. Use for clients that connect to a URL (ChatGPT, claude.ai web).

Every setting can also come from environment variables (`OBSIDIAN_VAULT_PATH`, `OBSIDIAN_MCP_API_KEY`,
`OBSIDIAN_MCP_Vault__MaxResults=100`, …) or `appsettings.json` next to the executable.
See `src/ObsidianMcp.Server/Vault/VaultOptions.cs` for all options.

## Connecting clients

> On Windows, escape backslashes in JSON: `"C:\\Users\\me\\Documents\\MyVault"`.

### Claude Desktop (stdio)

Settings → Developer → Edit Config (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "obsidian": {
      "command": "obsidian-mcp",
      "args": ["--vault", "/Users/me/Documents/MyVault"]
    }
  }
}
```

If `obsidian-mcp` isn't found, use the full path (`~/.dotnet/tools/obsidian-mcp`, or
`%USERPROFILE%\\.dotnet\\tools\\obsidian-mcp.exe`), or `"command": "dotnet", "args": ["path/to/obsidian-mcp.dll", "--vault", "..."]`.
Restart Claude Desktop afterwards.

### Claude Code

```bash
claude mcp add obsidian -- obsidian-mcp --vault "/Users/me/Documents/MyVault"
```

### Gemini CLI

`~/.gemini/settings.json`:

```json
{
  "mcpServers": {
    "obsidian": {
      "command": "obsidian-mcp",
      "args": ["--vault", "/Users/me/Documents/MyVault"]
    }
  }
}
```

### ChatGPT (and claude.ai custom connectors)

These run in the provider's cloud, so they need a **public HTTPS URL** that reaches your computer.

1. Start the server in HTTP mode with a long random key:
   ```bash
   obsidian-mcp --vault ~/Documents/MyVault --transport http --api-key "$(openssl rand -hex 32)"
   ```
2. Expose it with a tunnel, for example
   `cloudflared tunnel --url http://127.0.0.1:5199` or `ngrok http 5199`.
3. **ChatGPT:** Settings → Apps & Connectors → Advanced → enable *Developer mode*, then create a connector with
   URL `https://<tunnel-host>/mcp?key=<your-key>` and authentication *No authentication*
   (the key in the URL is the credential, because ChatGPT can't send custom headers).
   **claude.ai:** Settings → Connectors → Add custom connector, same URL.

Clients that can send headers should use `Authorization: Bearer <key>` or `X-API-Key: <key>` instead of `?key=`.

> ⚠️ Anyone with the URL and key can read (and, unless `--read-only`, modify) your vault. Use `--read-only`
> unless you need writes, keep the key secret, and stop the tunnel when you're done. For long-term remote use
> put the server behind a proper OAuth-capable gateway.

## Project layout

```
src/ObsidianMcp.Server/
  Program.cs              transports (stdio / HTTP), auth, CLI parsing
  Vault/VaultService.cs   safe file access, index, search, links, edits
  Vault/NoteParser.cs     frontmatter, tags, links, headings, link rewriting
  Tools/*.cs              MCP tool definitions
tests/ObsidianMcp.Tests/  unit tests + end-to-end test over stdio using the MCP client SDK
```
