namespace ObsidianMcp.Server.Vault;

/// <summary>
/// Configuration for the vault. Bound from the "Vault" configuration section,
/// environment variables (OBSIDIAN_MCP_Vault__Path, ...) or command line switches.
/// </summary>
public sealed class VaultOptions
{
    public const string SectionName = "Vault";

    /// <summary>Absolute path to the root folder of the Obsidian vault.</summary>
    public string Path { get; set; } = "";

    /// <summary>When true, no tools that modify the vault are exposed.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Folders (vault-relative, matched by first path segment or any segment) that are never exposed.
    /// </summary>
    public List<string> ExcludedFolders { get; set; } = [".obsidian", ".trash", ".git"];

    /// <summary>Maximum number of results returned by search / list tools.</summary>
    public int MaxResults { get; set; } = 50;

    /// <summary>Maximum size (bytes) of a note the server will read.</summary>
    public long MaxNoteBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// Folder (inside the vault) for the server's own data: audit log, change snapshots and schema.json.
    /// Dot-folders are never exposed through the tools.
    /// </summary>
    public string DataFolder { get; set; } = ".obsidian-mcp";

    /// <summary>Path to the record schema (JSON). Defaults to {DataFolder}/schema.json; built-in defaults if missing.</summary>
    public string? SchemaPath { get; set; }

    /// <summary>Folder that archive_note moves ordinary notes into.</summary>
    public string ArchiveFolder { get; set; } = "Archive";

    /// <summary>
    /// Expose the generic "search" and "fetch" tools in the shape ChatGPT connectors / deep research expect.
    /// Null means: enabled for HTTP transport, disabled for stdio.
    /// </summary>
    public bool? EnableSearchFetchTools { get; set; }

    /// <summary>
    /// Name of the vault used in obsidian:// URLs. Defaults to the vault folder name.
    /// </summary>
    public string? VaultName { get; set; }
}
