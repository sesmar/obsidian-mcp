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
        await _vault.AppendToNote("Project Alpha", "- [ ] Review budget", createIfMissing: false, heading: "Tasks", WriteOptions.Default);
        var text = _tv.Read("Projects/Project Alpha.md");
        var tasks = text.IndexOf("## Tasks");
        var inserted = text.IndexOf("- [ ] Review budget");
        var notes = text.IndexOf("## Notes");
        Assert.True(tasks < inserted && inserted < notes, text);
    }

    [Fact]
    public void Replace_requires_unique_match()
    {
        Assert.Throws<VaultException>(() => VaultService.ReplaceText("a b a", "a", "x", replaceAll: false, "Dup.md"));
        Assert.Equal("x b x", VaultService.ReplaceText("a b a", "a", "x", replaceAll: true, "Dup.md"));
    }

    [Fact]
    public async Task Move_updates_links_in_other_notes()
    {
        var (newPath, changes) = await _vault.MoveNote("Project Alpha", "Archive/Alpha Old.md", updateLinks: true, WriteOptions.Default);
        Assert.Equal("Archive/Alpha Old.md", newPath);
        Assert.False(_tv.Exists("Projects/Project Alpha.md"));
        Assert.Contains(changes, c => c.Path == "Home.md");

        var home = _tv.Read("Home.md");
        Assert.Contains("[[Alpha Old]]", home);
        Assert.Contains("(Archive/Alpha%20Old.md)", home);
        Assert.Contains("[[Not A Link]]", home); // code block untouched
        Assert.Contains("[[Alpha Old#Tasks]]", _tv.Read("Ideas/Brainstorm.md"));
        Assert.Contains("[[Home]]", _tv.Read("Archive/Alpha Old.md"));
    }

    [Fact]
    public async Task Move_dry_run_changes_nothing()
    {
        var (_, changes) = await _vault.MoveNote("Project Alpha", "Archive/Alpha Old.md", updateLinks: true, new WriteOptions { DryRun = true });
        Assert.All(changes, c => Assert.True(c.DryRun));
        Assert.True(_tv.Exists("Projects/Project Alpha.md"));
        Assert.Contains("[[Project Alpha]]", _tv.Read("Home.md"));
    }

    [Fact]
    public async Task Writes_are_audited_and_snapshotted()
    {
        var before = _vault.GetNote("Home").Content;
        var result = await _vault.AppendToNote("Home", "more", createIfMissing: false, null, new WriteOptions { Reason = "test" });
        Assert.NotNull(result.AuditId);
        var entry = Assert.Single(_vault.Audit.Read(null, 10));
        Assert.Equal("Home.md", entry.Path);
        Assert.Equal("test", entry.Reason);
        Assert.Equal(before, _vault.Audit.LoadSnapshot(entry.VersionBefore!));
    }

    [Fact]
    public async Task Stale_expected_version_is_rejected()
    {
        var version = _vault.GetNote("Home").Version;
        _tv.Write("Home.md", "changed in Obsidian");
        await Assert.ThrowsAsync<VaultException>(() =>
            _vault.AppendToNote("Home", "x", false, null, new WriteOptions { ExpectedVersion = version }));
        Assert.Equal("changed in Obsidian", _tv.Read("Home.md"));
    }

    [Fact]
    public async Task Dry_run_returns_diff_without_writing()
    {
        var result = await _vault.AppendToNote("Home", "new line", false, null, new WriteOptions { DryRun = true });
        Assert.True(result.DryRun);
        Assert.Contains("+ new line", result.BodyDiff);
        Assert.DoesNotContain("new line", _tv.Read("Home.md"));
    }

    [Fact]
    public void Property_changes_keep_body()
    {
        var (content, fm) = VaultService.WithProperties(_tv.Read("Projects/Project Alpha.md"),
            new Dictionary<string, object?> { ["status"] = "done", ["priority"] = 2L }, ["tags"], "x.md");
        Assert.Equal("done", fm["status"]);
        Assert.False(fm.ContainsKey("tags"));
        Assert.Contains("## Tasks", content);
        Assert.StartsWith("---\n", content);
    }

    [Fact]
    public async Task Read_only_vault_rejects_writes()
    {
        var ro = _tv.Service(readOnly: true);
        await Assert.ThrowsAsync<VaultException>(() => ro.CreateNote("New", "x", WriteOptions.Default));
    }

    [Fact]
    public void Hidden_and_data_folders_are_not_accessible()
    {
        Assert.Throws<VaultException>(() => _vault.ToFullPath(".obsidian-mcp/audit.jsonl"));
        Assert.Throws<VaultException>(() => _vault.ToFullPath("Notes/.secret.md"));
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
