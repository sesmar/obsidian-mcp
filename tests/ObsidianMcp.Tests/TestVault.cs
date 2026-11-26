using Microsoft.Extensions.Options;
using ObsidianMcp.Server.Vault;

namespace ObsidianMcp.Tests;

/// <summary>Creates a throwaway vault on disk with a few interlinked notes.</summary>
public sealed class TestVault : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "obsidian-mcp-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Copies an existing folder (e.g. the demo vault) instead of creating the default notes.</summary>
    public TestVault(string copyFrom)
    {
        foreach (var file in Directory.EnumerateFiles(copyFrom, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(Root, Path.GetRelativePath(copyFrom, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }

    public static string DemoVaultPath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "demo-vault"))) dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("samples/demo-vault"), "samples", "demo-vault");
        }
    }

    public TestVault()
    {
        Write("Home.md", """
            ---
            tags: [index]
            aliases: [Start]
            ---
            # Home

            Welcome. See [[Project Alpha]] and [[Ideas/Brainstorm|my ideas]].
            Also [the plan](Projects/Project%20Alpha.md).

            ```
            [[Not A Link]] #notatag
            ```
            """);
        Write("Projects/Project Alpha.md", """
            ---
            status: active
            tags:
              - project/alpha
            ---
            # Project Alpha

            ## Tasks
            - [ ] Write budget #todo

            ## Notes
            Quarterly budget is due. Links back to [[Home]].
            """);
        Write("Ideas/Brainstorm.md", "Random thoughts about #ideas and [[Project Alpha#Tasks]].\n");
        Write(".obsidian/app.json", "{}");
        Write(".obsidian/daily-notes.json", """{"folder":"Daily","format":"YYYY-MM-DD"}""");
    }

    public void Write(string rel, string content)
    {
        var full = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Read(string rel) => File.ReadAllText(Path.Combine(Root, rel));
    public bool Exists(string rel) => File.Exists(Path.Combine(Root, rel));

    public VaultService Service(bool readOnly = false) => new(OptionsFor(readOnly));

    public IOptions<VaultOptions> OptionsFor(bool readOnly = false) =>
        Options.Create(new VaultOptions { Path = Root, ReadOnly = readOnly });

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
