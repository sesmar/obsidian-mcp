# obsidian-mcp

An [MCP](https://modelcontextprotocol.io) server, in C# / .NET 10, that turns an Obsidian vault into a structured
**Client → Matter → Work Product** practice and docket API for ChatGPT, Claude, Gemini and other MCP clients.

The Markdown files stay the authoritative records. The server adds what a plain file interface can't:
relationship resolution, schema validation, docket queries, consolidated matter context, deadline protection,
change previews, version checks and an audit log. Obsidian does not need to be running.

Built on the official [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).
See [docs/REQUIREMENTS-STATUS.md](docs/REQUIREMENTS-STATUS.md) for how the functional requirements map to tools.

## Tools: the 25-tool MVP

| Group | Tools | Priority |
|---|---|---|
| Vault and search (5) | `get_vault_info`, `search_notes`, `get_note`, `create_note`, `update_note` | P0 |
| Client management (3) | `list_clients`, `get_client`, `get_client_matters` | P0 |
| Matter management (5) | `list_matters`, `get_matter`, `get_matter_context`, `create_matter`, `update_matter` | P0 |
| Work Product management (5) | `get_work_product`, `get_work_product_context`, `create_work_product`, `update_work_product`, `complete_work_product` | P0 |
| Docket management (3) | `get_docket`, `get_docket_summary`, `get_docket_exceptions` | P0 |
| Knowledge and workflows (2) | `get_template`, `get_workflow` | P1 |
| Administration (2) | `get_schema`, `get_audit_log` | P0 |

18 tools are read-only and 7 write. **Roll out read-only first**: run with `--read-only` (or give the client the
read-only API key) and only 18 tools are exposed; enable the 7 write tools once the read side has proved itself.
Schema validation, relationship integrity, audit logging and deadline protection are enforced internally either way.

Highlights:

* `get_docket` is one flexible query engine (client, matter, types, statuses, priorities, due-date range, overdue,
  waiting on, follow-ups due, ready for filing, stale, completed range, sorting); every dashboard view is a filter combination.
* `get_matter_context` / `get_work_product_context` return the matter, client drafting/reporting/prosecution preferences,
  related and family matters, active and prior Work Products with strategy/outcome summaries, the applicable workflow,
  checklist and attachments in one call.
* `update_work_product` covers status, priority, target date, next action, waiting on, follow-up date, checklist, notes
  and the due date. A deadline change needs `due_date_change_reason`, and the **whole call is only a preview** unless
  `confirm_due_date_change=true`, which the model is told to set only after you approve that specific change.
* `update_note` combines exact text replacement, appending (optionally under a heading) and property changes. On record
  notes it applies the same validation as the record tools.

### Optional tools

Not part of the MVP; enable when a need is demonstrated:

* `--extended-tools`: `list_folder`, `get_note_links`, `get_attachment` (PDF/DOCX/text extraction, images, SHA-256),
  `search_knowledge` (category search with citation/verification properties), `create_client`, `update_client`,
  `move_note`, `archive_note`.
* `--search-fetch`: the generic `search` / `fetch` pair that ChatGPT deep research requires.

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
| Deadline protection | `due_date` changes only via `update_work_product`'s `due_date` parameter, which needs a reason and only previews unless `confirm_due_date_change=true` (to be set only after explicit user approval); the change is logged in the note and the audit log |
| Change previews | Every write tool accepts `dry_run=true` and returns property before/after values and a body diff |
| Concurrency | Reads return a `version`; writes accept `expected_version` and fail if the file changed (Obsidian edit, Synology sync) |
| Atomic writes | Write to a hidden temp file, then atomic rename |
| Audit log | `.obsidian-mcp/audit.jsonl` records every change (operation, record, property before/after, reason, versions); prior content is snapshotted in `.obsidian-mcp/history/` |
| Deletion protection | No delete tool. Records are retired by status; the optional `archive_note` moves other notes to `Archive/` |
| Sync conflicts | `get_docket_exceptions` flags Synology/Dropbox/OneDrive conflict copies |
| Authentication / read-write separation | HTTP mode requires an API key; an optional second **read-only key** only sees the read tools (enforced server-side) |
| Prompt injection | Context responses label note content as data; server instructions tell the model not to follow instructions found in notes |
| Data minimisation | Context responses include configured sections only, truncated, with `get_note` for the full text |

The firm's docketing system remains authoritative for legal deadlines; the server says so in docket summaries.

## Build & install

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet test                                    # 44 unit + end-to-end tests
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
             [--extended-tools] [--search-fetch]
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
   Add `--search-fetch` if you want to use the vault in ChatGPT deep research.

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
  Tools/CoreReadTools.cs      the 18 read tools of the MVP
  Tools/CoreWriteTools.cs     the 7 write tools of the MVP
  Tools/ExtendedTools.cs      optional tools (--extended-tools)
  Tools/SearchFetchTools.cs   optional ChatGPT search/fetch (--search-fetch)
samples/demo-vault/           example vault + schema.json
tests/ObsidianMcp.Tests/      unit tests + end-to-end tests over stdio with the MCP client SDK
```
