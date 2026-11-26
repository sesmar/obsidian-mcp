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
    /// Folder for daily notes. If null, it is read from .obsidian/daily-notes.json, falling back to the vault root.
    /// </summary>
    public string? DailyNotesFolder { get; set; }

    /// <summary>
    /// .NET date format for daily note file names. If null, it is read from .obsidian/daily-notes.json
    /// (Moment.js format, converted), falling back to yyyy-MM-dd.
    /// </summary>
    public string? DailyNotesFormat { get; set; }

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
