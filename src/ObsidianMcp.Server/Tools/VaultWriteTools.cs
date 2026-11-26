using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Server.Tools;

/// <summary>Tools that modify the vault. Not registered when the server runs with --read-only.</summary>
[McpServerToolType]
public sealed class VaultWriteTools(VaultService vault)
{
    [McpServerTool(Name = "create_note", Title = "Create note", Destructive = false, OpenWorld = false)]
    [Description("Creates a new markdown note. Folders are created as needed. Fails if the note exists unless overwrite=true. " +
                 "Use [[Note Name]] for links and #tags or YAML frontmatter for tags, as in Obsidian.")]
    public async Task<string> CreateNote(
        [Description("Vault-relative path, e.g. 'Projects/New Idea.md' ('.md' is added if missing).")] string path,
        [Description("Full markdown content, optionally starting with YAML frontmatter.")] string content,
        [Description("Replace the note if it already exists.")] bool overwrite = false)
    {
        var p = await vault.CreateNote(path, content, overwrite);
        return $"Created '{p}'.";
    }

    [McpServerTool(Name = "append_to_note", Title = "Append to note", Destructive = false, OpenWorld = false)]
    [Description("Appends markdown to the end of a note, or to the end of a specific heading's section if 'heading' is given. Creates the note if it does not exist (unless create_if_missing=false).")]
    public async Task<string> AppendToNote(
        [Description("Path or name of the note.")] string path,
        [Description("Markdown to append.")] string content,
        [Description("Optional heading text (without #). Content is inserted at the end of that section.")] string heading = "",
        [Description("Create the note when it does not exist.")] bool create_if_missing = true)
    {
        var p = await vault.AppendToNote(path, content, create_if_missing, heading);
        return $"Appended to '{p}'.";
    }

    [McpServerTool(Name = "replace_in_note", Title = "Edit note text", Destructive = true, OpenWorld = false)]
    [Description("Edits a note by replacing an exact piece of text. old_text must match exactly (including whitespace) and be unique unless replace_all=true. " +
                 "Read the note first. To delete text, pass an empty new_text.")]
    public async Task<string> ReplaceInNote(
        [Description("Path or name of the note.")] string path,
        [Description("Exact existing text to replace.")] string old_text,
        [Description("Replacement text.")] string new_text,
        [Description("Replace every occurrence instead of requiring a unique match.")] bool replace_all = false)
    {
        var (p, n) = await vault.ReplaceInNote(path, old_text, new_text, replace_all);
        return $"Made {n} replacement(s) in '{p}'.";
    }

    [McpServerTool(Name = "update_frontmatter", Title = "Update properties", Destructive = true, OpenWorld = false)]
    [Description("Sets and/or removes YAML frontmatter properties (Obsidian 'Properties') on a note without touching the body. " +
                 "Values may be strings, numbers, booleans or lists, e.g. {\"status\": \"done\", \"tags\": [\"project\", \"q3\"]}.")]
    public async Task<object> UpdateFrontmatter(
        [Description("Path or name of the note.")] string path,
        [Description("Properties to add or overwrite.")] Dictionary<string, JsonElement>? set = null,
        [Description("Property names to remove.")] string[]? remove = null)
    {
        var fm = await vault.UpdateFrontmatter(path, set, remove);
        return new { path, frontmatter = fm };
    }

    [McpServerTool(Name = "move_note", Title = "Move or rename note", Destructive = true, OpenWorld = false)]
    [Description("Moves and/or renames a note. By default, links to it in other notes are updated, like Obsidian does.")]
    public async Task<object> MoveNote(
        [Description("Current path or name of the note.")] string from,
        [Description("New vault-relative path (e.g. 'Archive/Old Plan.md'), or a folder ending in '/' to keep the file name.")] string to,
        [Description("Rewrite [[links]] in other notes to point at the new location.")] bool update_links = true)
    {
        var (newPath, updated) = await vault.MoveNote(from, to, update_links);
        return new { newPath, updatedNotes = updated };
    }

    [McpServerTool(Name = "delete_note", Title = "Delete note", Destructive = true, OpenWorld = false)]
    [Description("Deletes a note. By default it is moved to the vault's .trash folder (recoverable); permanent=true removes it for good.")]
    public Task<string> DeleteNote(
        [Description("Path or name of the note.")] string path,
        [Description("Permanently delete instead of moving to .trash.")] bool permanent = false)
        => vault.DeleteNote(path, permanent);

    [McpServerTool(Name = "append_to_daily_note", Title = "Append to daily note", Destructive = false, OpenWorld = false)]
    [Description("Appends markdown to the daily note for a date (default: today), creating it if needed. Optionally under a heading.")]
    public async Task<string> AppendToDailyNote(
        [Description("Markdown to append.")] string content,
        [Description("Date as YYYY-MM-DD. Defaults to today.")] string date = "",
        [Description("Optional heading (without #) to append under.")] string heading = "")
    {
        var path = vault.DailyNotePath(VaultReadTools.ParseDate(date));
        var p = await vault.AppendToNote(path, content, createIfMissing: true, heading);
        return $"Appended to '{p}'.";
    }
}
