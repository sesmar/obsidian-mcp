# Functional requirements: implementation status

## MVP: the 25 exposed tools

All 25 tools in the MVP checklist are implemented with the specified names, and nothing else is exposed by default.

| # | Tool | Group | Read / write |
|---|---|---|---|
| 1 | `get_vault_info` | Vault and search | read |
| 2 | `search_notes` | Vault and search | read |
| 3 | `get_note` | Vault and search | read |
| 4 | `create_note` | Vault and search | write |
| 5 | `update_note` | Vault and search | write |
| 6 | `list_clients` | Client management | read |
| 7 | `get_client` | Client management | read |
| 8 | `get_client_matters` | Client management | read |
| 9 | `list_matters` | Matter management | read |
| 10 | `get_matter` | Matter management | read |
| 11 | `get_matter_context` | Matter management | read |
| 12 | `create_matter` | Matter management | write |
| 13 | `update_matter` | Matter management | write |
| 14 | `get_work_product` | Work Product management | read |
| 15 | `get_work_product_context` | Work Product management | read |
| 16 | `create_work_product` | Work Product management | write |
| 17 | `update_work_product` | Work Product management | write |
| 18 | `complete_work_product` | Work Product management | write |
| 19 | `get_docket` | Docket management | read |
| 20 | `get_docket_summary` | Docket management | read |
| 21 | `get_docket_exceptions` | Docket management | read |
| 22 | `get_template` | Knowledge and workflows | read |
| 23 | `get_workflow` | Knowledge and workflows | read |
| 24 | `get_schema` | Administration | read |
| 25 | `get_audit_log` | Administration | read |

**Rollout:** read-only first (`--read-only` or the read-only API key exposes tools 1–3, 6–11, 14–15 and 19–25), then the
7 controlled write tools. Schema validation, audit logging, relationship integrity and deadline protection are enforced
internally from the outset, whichever tools are exposed.

## Roadmap functions and where they live

Functions from the full requirements that are not separate MVP tools. Most are covered by MVP tool parameters or are
internal services; a few are available as opt-in tools; the rest are deferred.

**Covered by MVP tool parameters**

| Function(s) | Covered by |
|---|---|
| list_folders, list_notes | `get_vault_info` (top-level folders), `search_notes(folder=…)` |
| append_to_note, get_note_properties, update_note_properties | `update_note(append, set_properties, remove_properties)`, `get_note(properties_only)` |
| search_by_properties, search_by_tags, get_recently_modified_notes | `search_notes(properties, tags, modified_within_days)` |
| get_client_preferences, get_client_contacts, get_client_summary, get_client_guidelines | `get_client` |
| get_client_work_products, get_client_docket, list_work_products | `get_docket(client / matter / include_completed)` |
| get_matter_work_products, get_matter_active_work_products, get_matter_history, get_related_matters, get_matter_attachments, get_matter_notes | `get_matter`, `get_matter_context` |
| append_matter_note | `update_matter(append_note)` |
| update_work_product_status, update_work_product_due_date, update_work_product_target_date, set_next_action, set_waiting_on, set_follow_up_date, append_work_product_note, update_work_product_checklist, reopen_work_product | `update_work_product` parameters |
| get_work_product_attachments, get_work_product_checklist | `get_work_product` |
| get_work_product_history | `get_audit_log(record_id)` |
| get_upcoming_deadlines, get_overdue_work_products, get_work_products_by_type, get_waiting_work_products, get_follow_ups_due, get_ready_for_filing, get_stale_work_products, get_completed_work_products | `get_docket` filters |
| get_workload_summary | `get_docket_summary` |
| list_templates, create_from_template | `get_template()` lists templates; `create_note(template=…)`; record creation applies templates automatically |
| list_workflows, get_workflow_checklist, get_applicable_workflow | `get_workflow` (empty = list; by name or Work Product type; checklist parsed) |
| get_allowed_values | `get_schema` |
| validate_record, validate_relationships, find_duplicate_records | `get_docket_exceptions`; also enforced on every write |
| preview_changes, apply_changes | `dry_run=true` on every write tool |
| search_case_law, search_legal_authorities, search_prosecution_strategies, search_argument_library, get_firm_procedures | `search_notes(folder="Knowledge/…")` |

**Opt-in tools** (`--extended-tools`): `list_folder`, `get_note_links` (backlinks / outgoing links / related notes),
`get_attachment` (attachment metadata, SHA-256, PDF/DOCX/text extraction, images), `search_knowledge` (knowledge search
with citation and verification properties), `create_client`, `update_client`, `move_note` (rename/move with link
updates), `archive_note` (archive_note, archive_matter and archive_work_product). The ChatGPT `search` / `fetch`
pair is behind `--search-fetch`.

**Internal services already in place:** change snapshots for history and rollback (`.obsidian-mcp/history/`), audit
log, atomic writes, version checks, relationship resolution, schema validation.

**Deferred:** semantic_search, search_attachments / search_document_content (needs a content index), add_attachment
and link_attachment, get_document_versions, get_note_history and rollback_change as tools, validate_links,
get_workload_forecast, get_prompt / list_prompts, get_server_status / get_index_status / refresh_index.

## Architecture notes

* **Index.** The server keeps an in-memory index that re-parses only changed files on each request, so results always
  reflect the files on disk and nothing needs rebuilding. This is fast for vaults of a few thousand notes. SQLite FTS5
  (phase 3) remains the planned next step when attachment content search or larger vaults need it; it would be a
  rebuildable cache, not a source of truth.
* **Authority.** The Markdown files are authoritative. The audit log and snapshots live in `.obsidian-mcp/` inside the
  vault, so they sync and back up with Synology Drive. The firm's docketing system remains authoritative for legal deadlines.
