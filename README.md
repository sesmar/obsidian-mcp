# obsidian-mcp

An [MCP](https://modelcontextprotocol.io) server, in C# / .NET 10, that turns an Obsidian vault into a structured
**Client → Matter → Work Product** practice and docket API for ChatGPT, Claude, Gemini and other MCP clients.

The Markdown files stay the authoritative records. The server adds what a plain file interface can't:
relationship resolution, schema validation, docket queries, consolidated matter context, deadline protection,
change previews, version checks and an audit log. Obsidian does not need to be running.

Built on the official [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).
See [docs/REQUIREMENTS-STATUS.md](docs/REQUIREMENTS-STATUS.md) for how the functional requirements map to tools.

## Tools (32, plus `search`/`fetch` for ChatGPT)

| Group | Tools |
|---|---|
| **Docket** | `get_docket` (flexible query: client, matter, types, statuses, priorities, due-date range, overdue, waiting, follow-ups, ready for filing, stale, completed range, sorting), `get_docket_summary`, `get_docket_exceptions` |
| **Context** | `get_matter_context`, `get_work_product_context`: one call returns the matter, client preferences, related/family matters, active and prior Work Products, workflow, checklist and attachments |
| **Clients & matters** | `list_clients`, `get_client`, `list_matters`, `create_client`, `create_matter`, `update_record` |
| **Work Products** | `create_work_product`, `update_work_product` (status, priority, target date, next action, waiting on, follow-up, checklist), `update_work_product_due_date` (protected), `complete_work_product`, `append_record_note` |
| **Knowledge & workflows** | `search_knowledge` (case law, authorities, strategies, argument library, procedures, research; returns citation/verification properties), `get_workflow` |
| **Vault** | `get_vault_info`, `list_folder`, `get_note`, `search_notes` (text + folder/tag/property/recency filters), `get_note_links`, `get_attachment` (PDF/DOCX/text extraction, images, SHA-256) |
| **General writes** | `create_note` (optionally from a template), `append_to_note`, `update_note`, `update_note_properties`, `move_note`, `archive_note` |
| **Admin** | `get_schema`, `get_audit_log` |

With `--read-only`, or for callers using the read-only API key, only the 18 read tools (plus `search`/`fetch`) are available.

## Data model

Records are ordinary notes with YAML properties, identified by the folder they live in:

```
Clients/DEMO - Demo Robotics Inc.md                     client_code: DEMO
Matters/DEMO/DEMO-101-A.md                              docket_number: DEMO-101-A, client: "[[DEMO - Demo Robotics Inc]]"
Matters/DEMO/DEMO-101-A/Office Action 2026-10-01.pdf    attachment (folder named after the matter note)
Work Products/DEMO-101-A/DEMO-101-A-WP02 Final Office Action Response.md
                                                        work_product_id, matter: "[[DEMO-101-A]]", work_product_type,
                                                        status, priority, due_date, target_date, next_action, waiting_on, follow_up_date
Workflows/Office Action Response.md                     applies_to: [Non-Final Office Action Response, ...]
Templates/Work Product.md                               used by create_work_product
Knowledge/Case Law/KSR v Teleflex.md                    citation, pinpoint, source_url, verified_date
```

Relationships are `[[wikilinks]]` in properties, so they also show up in Obsidian's graph and backlinks.
Client preferences are headings in the client note (`## Drafting Preferences`, `## Reporting Requirements`,
`## Prosecution Guidelines`, …) and are pulled into every matter and Work Product context.

**Everything is configurable.** Folders, property names, required fields, statuses, Work Product types, priorities,
protected properties, path templates and context headings come from `.obsidian-mcp/schema.json` in the vault.
Generate the defaults and edit them to match your vault:

```bash
obsidian-mcp --print-schema > "/path/to/vault/.obsidian-mcp/schema.json"
```

`samples/demo-vault` is a complete example (fictional client DEMO, matter DEMO-101-A). Open it in Obsidian or point the server at it.

## Data integrity and safety

| Requirement | How |
|---|---|
| Vault isolation / path validation | Every path is confined to the vault; `..`, absolute paths and symlinks pointing outside are rejected; dot-folders (`.obsidian`, `.trash`, `.obsidian-mcp`) are never exposed |
| Record validation | Required properties, allowed values, dates and parent relationships are checked on every record write; invalid writes are refused |
| No bypass | Generic note tools route record notes through the same validation; they cannot create records, move them, or change identifiers or protected properties |
| Stable identifiers | `client_code`, `docket_number`, `work_product_id` cannot be changed; duplicates are refused; Work Product ids are generated |
| Deadline protection | `due_date` changes only via `update_work_product_due_date`, which needs a reason and returns a preview unless `confirm=true` (to be set only after explicit user approval); the change is logged in the note and audit log |
| Change previews | Every write tool accepts `dry_run=true` and returns property before/after values and a body diff |
| Concurrency | Reads return a `version`; writes accept `expected_version` and fail if the file changed (Obsidian edit, Synology sync) |
| Atomic writes | Write to a hidden temp file, then atomic rename |
| Audit log | `.obsidian-mcp/audit.jsonl` records every change (operation, record, property before/after, reason, versions); prior content is snapshotted in `.obsidian-mcp/history/` |
| Deletion protection | No delete tool. `archive_note` sets an archived status on records or moves other notes to `Archive/` |
| Sync conflicts | `get_docket_exceptions` flags Synology/Dropbox/OneDrive conflict copies |
| Authentication | HTTP mode requires an API key; an optional second **read-only key** only sees read tools (enforced server-side) |
| Prompt injection | Context responses label note content as data; server instructions tell the model not to follow instructions found in notes |
| Data minimisation | Context responses include configured sections only, truncated, with `get_note` for the full text |

The firm's docketing system remains authoritative for legal deadlines; the server says so in docket summaries.

## Build & install

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet test                                    # 43 unit + end-to-end tests
dotnet pack src/ObsidianMcp.Server -c Release -o artifacts
dotnet tool install -g --add-source ./artifacts obsidian-mcp
obsidian-mcp --help
```

Or build a self-contained executable that doesn't need .NET installed:

```bash
dotnet publish src/ObsidianMcp.Server -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## Usage

```
obsidian-mcp --vault <path> [--read-only] [--transport stdio|http] [--urls http://127.0.0.1:5199]
             [--api-key <key>] [--read-only-api-key <key>] [--schema <file>]
```

* **stdio** (default): the client launches the server (Claude Desktop, Claude Code, Gemini CLI).
* **http**: Streamable HTTP endpoint at `/mcp` (ChatGPT, claude.ai). Keys can also come from
  `OBSIDIAN_MCP_API_KEY` / `OBSIDIAN_MCP_READ_API_KEY`.

## Connecting clients

> On Windows, escape backslashes in JSON: `"C:\\Users\\me\\Vault"`.

**Claude Desktop / Gemini CLI**: in `claude_desktop_config.json` or `~/.gemini/settings.json`:

```json
{ "mcpServers": { "obsidian": { "command": "obsidian-mcp", "args": ["--vault", "C:\\Users\\me\\Vault"] } } }
```

**Claude Code**: `claude mcp add obsidian -- obsidian-mcp --vault "/path/to/vault"`

**ChatGPT** (and claude.ai custom connectors) run in the provider's cloud and need a public HTTPS URL:

1. `obsidian-mcp --vault <path> --transport http --api-key "$(openssl rand -hex 32)" --read-only-api-key "$(openssl rand -hex 32)"`
2. Expose it with a tunnel, e.g. `cloudflared tunnel --url http://127.0.0.1:5199`.
3. In ChatGPT (developer mode / workspace connectors) add `https://<tunnel-host>/mcp?key=<key>` with no authentication.
   Start with the **read-only key**; use the read-write key only when you want ChatGPT to update records.

> ⚠️ Anyone with the URL and key can use the vault. Keep keys secret, stop the tunnel when not in use, and check
> what your firm's ChatGPT workspace allows before connecting professional data. For long-term use, put the server
> behind an OAuth-capable gateway instead of a key in the URL.

## Project layout

```
src/ObsidianMcp.Server/
  Program.cs                  transports, API keys, read-only/read-write access control, CLI
  Vault/VaultService.cs       safe file access, index, search, links, atomic/versioned/audited writes
  Vault/ChangeTracking.cs     audit log, snapshots, diffs
  Vault/NoteParser.cs         frontmatter, tags, links, headings
  Vault/DocumentReader.cs     PDF / DOCX / text extraction, checksums
  Records/RecordSchema.cs     configurable schema + defaults
  Records/RecordService.cs    validation, relationship integrity, record writes, deadline protection
  Records/DocketService.cs    docket query engine, summary, exceptions
  Records/ContextService.cs   matter / Work Product context assembly, workflows
  Tools/*.cs                  MCP tool definitions
samples/demo-vault/           example vault + schema.json
tests/ObsidianMcp.Tests/      unit tests + end-to-end tests over stdio with the MCP client SDK
```
