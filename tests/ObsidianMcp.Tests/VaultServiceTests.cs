using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Tests;

public sealed class VaultServiceTests : IDisposable
{
    private readonly TestVault _tv = new();
    private readonly VaultService _vault;

    public VaultServiceTests() => _vault = _tv.Service();
    public void Dispose() => _tv.Dispose();

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("Projects/../../outside.md")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData(".obsidian/app.json")]
    [InlineData("Projects/.git/config")]
    public void Rejects_paths_outside_or_excluded(string path)
    {
        Assert.Throws<VaultException>(() =>
        {
            var rel = _vault.Normalize(path);
            _vault.ToFullPath(rel);
        });
    }

    [Fact]
    public void Leading_slash_means_vault_root()
    {
        var full = _vault.ToFullPath(_vault.Normalize("/etc/passwd"));
        Assert.StartsWith(_vault.RootPath, full);
    }

    [Fact]
    public void Rejects_symlink_escaping_vault()
    {
        var outside = Path.Combine(Path.GetTempPath(), "obsidian-mcp-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.md"), "secret");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_tv.Root, "Linked"), outside);
            Assert.Throws<VaultException>(() => _vault.GetNote("Linked/secret.md"));
            Assert.DoesNotContain(_vault.GetAllNotes(), n => n.Path.StartsWith("Linked"));
        }
        finally { Directory.Delete(outside, true); }
    }

    [Fact]
    public void Indexes_notes_but_not_hidden_folders()
    {
        var paths = _vault.GetAllNotes().Select(n => n.Path).Order().ToList();
        Assert.Equal(["Home.md", "Ideas/Brainstorm.md", "Projects/Project Alpha.md"], paths);
    }

    [Fact]
    public void Parses_frontmatter_tags_and_ignores_code()
    {
        var home = _vault.GetNote("Home");
        Assert.Contains("index", home.Parsed.Tags);
        Assert.DoesNotContain("notatag", home.Parsed.Tags);
        Assert.DoesNotContain(home.Parsed.Links, l => l.Target == "Not A Link");
        Assert.Equal("Home", home.Parsed.Headings[0].Text);

        var alpha = _vault.GetNote("Projects/Project Alpha");
        Assert.Equal("active", alpha.Parsed.Frontmatter["status"]);
        Assert.Contains("project/alpha", alpha.Parsed.Tags);
        Assert.Contains("todo", alpha.Parsed.Tags);
    }

    [Fact]
    public void Resolves_links_by_name_and_path()
    {
        Assert.Equal("Projects/Project Alpha.md", _vault.ResolveLink("Project Alpha", "Home.md"));
        Assert.Equal("Ideas/Brainstorm.md", _vault.ResolveLink("Ideas/Brainstorm", "Home.md"));
        Assert.Null(_vault.ResolveLink("Nope", "Home.md"));
    }

    [Fact]
    public void Finds_backlinks_from_wiki_and_markdown_links()
    {
        var back = _vault.GetBacklinks("Project Alpha");
        Assert.Equal(["Home.md", "Ideas/Brainstorm.md"], back.Select(b => b.Path).Order());
        Assert.Equal(2, back.Single(b => b.Path == "Home.md").Count);
    }

    [Fact]
    public void Search_ranks_and_filters()
    {
        var hits = _vault.Search("budget");
        Assert.Equal("Projects/Project Alpha.md", hits[0].Path);
        Assert.Contains("budget", hits[0].Snippet, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(_vault.Search("budget", folder: "Ideas"));
        Assert.Single(_vault.Search("", tag: "project"));
        Assert.Empty(_vault.Search("budget nonexistentword"));
    }

    [Fact]
    public async Task Append_under_heading_inserts_at_end_of_section()
    {
        await _vault.AppendToNote("Project Alpha", "- [ ] Review budget", createIfMissing: false, heading: "Tasks");
        var text = _tv.Read("Projects/Project Alpha.md");
        var tasks = text.IndexOf("## Tasks");
        var inserted = text.IndexOf("- [ ] Review budget");
        var notes = text.IndexOf("## Notes");
        Assert.True(tasks < inserted && inserted < notes, text);
    }

    [Fact]
    public async Task Replace_requires_unique_match()
    {
        _tv.Write("Dup.md", "a b a");
        await Assert.ThrowsAsync<VaultException>(() => _vault.ReplaceInNote("Dup", "a", "x", replaceAll: false));
        var (_, n) = await _vault.ReplaceInNote("Dup", "a", "x", replaceAll: true);
        Assert.Equal(2, n);
        Assert.Equal("x b x", _tv.Read("Dup.md"));
    }

    [Fact]
    public async Task Move_updates_links_in_other_notes()
    {
        var (newPath, updated) = await _vault.MoveNote("Project Alpha", "Archive/Alpha Old.md", updateLinks: true);
        Assert.Equal("Archive/Alpha Old.md", newPath);
        Assert.False(_tv.Exists("Projects/Project Alpha.md"));
        Assert.Contains("Home.md", updated);

        var home = _tv.Read("Home.md");
        Assert.Contains("[[Alpha Old]]", home);
        Assert.Contains("(Archive/Alpha%20Old.md)", home);
        Assert.Contains("[[Not A Link]]", home); // code block untouched
        Assert.Contains("[[Alpha Old#Tasks]]", _tv.Read("Ideas/Brainstorm.md"));
        // The moved note's own wikilink to Home still resolves.
        Assert.Contains("[[Home]]", _tv.Read("Archive/Alpha Old.md"));
    }

    [Fact]
    public async Task Update_frontmatter_preserves_body()
    {
        var set = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["status"] = System.Text.Json.JsonDocument.Parse("\"done\"").RootElement,
            ["priority"] = System.Text.Json.JsonDocument.Parse("2").RootElement,
        };
        await _vault.UpdateFrontmatter("Project Alpha", set, ["tags"]);
        var note = _tv.Service().GetNote("Project Alpha");
        Assert.Equal("done", note.Parsed.Frontmatter["status"]);
        Assert.False(note.Parsed.Frontmatter.ContainsKey("tags"));
        Assert.Contains("## Tasks", note.Content);
        Assert.StartsWith("---\n", note.Content);
    }

    [Fact]
    public async Task Delete_moves_to_trash()
    {
        await _vault.DeleteNote("Ideas/Brainstorm", permanent: false);
        Assert.False(_tv.Exists("Ideas/Brainstorm.md"));
        Assert.True(_tv.Exists(".trash/Brainstorm.md"));
    }

    [Fact]
    public async Task Read_only_vault_rejects_writes()
    {
        var ro = _tv.Service(readOnly: true);
        await Assert.ThrowsAsync<VaultException>(() => ro.CreateNote("New", "x", false));
    }

    [Fact]
    public void Daily_note_path_uses_obsidian_settings()
    {
        Assert.Equal("Daily/2026-11-26.md", _vault.DailyNotePath(new DateOnly(2026, 11, 26)));
    }

    [Theory]
    [InlineData("YYYY-MM-DD", "2026-03-05")]
    [InlineData("YYYY/MM/DD", "2026/03/05")]
    [InlineData("dddd, MMMM Do YYYY", "Thursday, March 5 2026")]
    [InlineData("[Journal] YYYY-MM-DD", "Journal 2026-03-05")]
    public void Converts_moment_formats(string moment, string expected)
    {
        var fmt = NoteParser.MomentToDotNet(moment);
        Assert.Equal(expected, new DateOnly(2026, 3, 5).ToString(fmt, System.Globalization.CultureInfo.InvariantCulture));
    }
}
