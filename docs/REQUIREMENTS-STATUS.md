# Functional requirements: implementation status

How each function in the requirements maps to the MCP tools. To keep tool selection reliable, overlapping
functions were merged into one tool with parameters, or into an internal service, as the requirements recommend.

Legend: **Tool** = exposed MCP tool · **Merged** = covered by the named tool · **Internal** = application service,
not exposed · **Deferred** = later phase.

## 1. Vault management
| Function | Status |
|---|---|
| get_vault_info | Tool |
| list_folders / list_notes | Merged → `list_folder` (`folders_only`, `recursive`) |
| get_note | Tool (returns `version` for safe edits) |
| create_note | Tool (optional template; refuses record folders) |
| update_note | Tool (exact text replacement; record properties protected) |
| append_to_note | Tool |
| rename_note / move_note | Merged → `move_note` (updates links) |
| archive_note | Tool (records: archived status; notes: moved to `Archive/`) |
| get_note_properties | Merged → `get_note(properties_only=true)` |
| update_note_properties | Tool (records validated, protected properties refused) |
| get_note_history | Internal: snapshots in `.obsidian-mcp/history/` + `get_audit_log`. Rollback tool deferred |

## 2. Search and retrieval
| Function | Status |
|---|---|
| search_notes | Tool |
| search_by_properties / search_by_tags | Merged → `search_notes(properties, tags)` |
| get_recently_modified_notes | Merged → `search_notes(modified_within_days)` |
| get_backlinks / get_outgoing_links | Merged → `get_note_links` |
| get_related_notes | Merged → `get_note_links`; related matters in `get_matter_context` |
| get_attachment | Tool |
| search_attachments | Deferred (needs a content index; see SQLite below) |
| semantic_search | Deferred (phase 9) |

## 3. Client management
| Function | Status |
|---|---|
| list_clients, get_client | Tools |
| create_client | Tool |
| update_client | Merged → `update_record(record_type="client")` |
| get_client_matters, get_client_preferences, get_client_contacts, get_client_summary | Merged → `get_client` |
| get_client_work_products, get_client_docket | Merged → `get_docket(client=…)` |

## 4. Matter management
| Function | Status |
|---|---|
| list_matters | Tool |
| get_matter, get_matter_context, get_matter_work_products, get_matter_active_work_products, get_matter_history, get_related_matters, get_matter_attachments, get_matter_notes | Merged → `get_matter_context` |
| create_matter | Tool |
| update_matter | Merged → `update_record(record_type="matter")` |
| append_matter_note | Merged → `append_record_note` |
| archive_matter | Merged → `archive_note` |

## 5. Work Product management
| Function | Status |
|---|---|
| list_work_products | Merged → `get_docket(include_completed=…)` |
| get_work_product, get_work_product_context, get_work_product_attachments, get_work_product_checklist | Merged → `get_work_product_context` |
| create_work_product | Tool |
| update_work_product, update_work_product_status, update_work_product_target_date, set_next_action, set_waiting_on, set_follow_up_date, update_work_product_checklist, reopen_work_product | Merged → `update_work_product` |
| update_work_product_due_date | Tool (reason required; preview unless `confirm=true`) |
| complete_work_product | Tool |
| append_work_product_note | Merged → `append_record_note` |
| get_work_product_history | Merged → `get_audit_log(record_id=…)` |
| archive_work_product | Merged → `archive_note` |

## 6. Docket and workload
| Function | Status |
|---|---|
| get_docket | Tool (common query engine) |
| get_docket_summary | Tool |
| get_upcoming_deadlines, get_overdue_work_products, get_work_products_by_type, get_waiting_work_products, get_follow_ups_due, get_ready_for_filing, get_stale_work_products, get_completed_work_products | Merged → `get_docket` filters |
| get_workload_summary | Merged → `get_docket_summary` breakdowns |
| get_docket_exceptions | Tool |
| get_workload_forecast | Deferred (can be derived from `get_docket` due-date ranges today) |

## 7. Professional knowledge base
| Function | Status |
|---|---|
| search_case_law, search_legal_authorities, search_prosecution_strategies, search_argument_library, get_firm_procedures, get_research_context | Merged → `search_knowledge(category=…)` |
| get_case_law | Merged → `get_note` |
| get_client_guidelines | Merged → `get_client` / context tools |
| create_case_law, create_research_note, update_case_law | Merged → `create_note` (with a template), `update_note_properties` |
| get_related_cases | Merged → `get_note_links` |

Citation, pinpoint, source URL and verification date are returned with knowledge results, along with a reminder that stored summaries are not verified authority.

## 8. Templates and workflows
| Function | Status |
|---|---|
| list_workflows, get_workflow, get_workflow_checklist, get_applicable_workflow | Merged → `get_workflow` (by name or Work Product type; parses checklist) |
| list_templates, get_template | Merged → `list_folder("Templates")`, `get_note` |
| create_from_template | Merged → `create_note(template=…)`; record creation uses templates automatically |
| get_prompt, list_prompts | Deferred (could be exposed as MCP prompts) |

## 9. Attachments
| Function | Status |
|---|---|
| list_attachments | Merged → context tools (`attachments`) |
| get_attachment, get_attachment_metadata, get_document_text, get_document_hash | Merged → `get_attachment` (metadata, SHA-256, paged text, images) |
| add_attachment, link_attachment | Deferred (link existing files with `update_note_properties` / `append_record_note` meanwhile) |
| search_document_content, get_document_versions | Deferred |

## 10. Integrity, security, administration
| Function | Status |
|---|---|
| get_schema, get_audit_log | Tools |
| validate_record, validate_relationships, find_duplicate_records | Merged → `get_docket_exceptions`; also enforced on every write |
| get_allowed_values | Merged → `get_schema` |
| preview_changes / apply_changes | Merged → `dry_run=true` on every write tool |
| validate_links | Deferred (unresolved links visible via `get_note_links`) |
| rollback_change | Deferred (snapshots are already recorded) |
| get_server_status, get_index_status, refresh_index | Not needed yet: the index refreshes incrementally on every call |

## Architecture notes

* **Index.** The server keeps an in-memory index that re-parses only changed files on each request, so results always
  reflect the files on disk and nothing needs rebuilding. This is fast for vaults of a few thousand notes. SQLite FTS5
  (phase 3) remains the planned next step when attachment content search or larger vaults need it; it would be a
  rebuildable cache beside the vault, not a source of truth.
* **Authority.** The Markdown files are authoritative. The audit log and snapshots live in `.obsidian-mcp/` inside the
  vault, so they sync and back up with Synology Drive. The firm's docketing system remains authoritative for legal deadlines.
