using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace ObsidianMcp.Server.Vault;

/// <summary>A link found in a note. Target is the raw link target without heading/block/alias.</summary>
public sealed record NoteLink(string Target, string? Subpath, string? Alias, bool IsEmbed);

/// <summary>A heading found in a note.</summary>
public sealed record NoteHeading(int Level, string Text, int Line);

/// <summary>Result of parsing a markdown note.</summary>
public sealed record ParsedNote(
    Dictionary<string, object?> Frontmatter,
    string Body,
    IReadOnlyList<string> Tags,
    IReadOnlyList<NoteLink> Links,
    IReadOnlyList<NoteHeading> Headings);

/// <summary>Stateless parsing helpers for Obsidian-flavoured markdown.</summary>
public static partial class NoteParser
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();
    private static readonly ISerializer YamlOut = new SerializerBuilder()
        .WithIndentedSequences()
        .Build();

    [GeneratedRegex(@"\A---[ \t]*\r?\n(.*?)(?:\r?\n)?^(?:---|\.\.\.)[ \t]*(?:\r?\n|\z)", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex FrontmatterRegex();

    // ```lang ... ``` or ~~~ ... ~~~ fenced blocks
    [GeneratedRegex(@"^(```|~~~).*?^\1[ \t]*$", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex(@"`[^`\r\n]+`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"%%.*?%%", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    // #tag, #nested/tag, #tag-with-dash. Must contain at least one non-digit. Not preceded by a word char or '&' (html entities).
    [GeneratedRegex(@"(?<![\w&#/])#([\p{L}\p{N}_\-/]*[\p{L}_\-/][\p{L}\p{N}_\-/]*)")]
    private static partial Regex InlineTagRegex();

    // [[target#heading|alias]] and ![[embed]]
    [GeneratedRegex(@"(!?)\[\[([^\[\]\|#\^\r\n]*)((?:#|\^)[^\[\]\|\r\n]*)?(?:\|([^\[\]\r\n]*))?\]\]")]
    private static partial Regex WikiLinkRegex();

    // [text](relative/path.md#heading) – only local markdown links
    [GeneratedRegex(@"(!?)\[([^\]\r\n]*)\]\((?!https?:|mailto:|obsidian:)<?([^)\s>]+?\.md)(#[^)\s>]*)?>?(?:\s+""[^""]*"")?\)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"^(#{1,6})[ \t]+(.+?)[ \t#]*$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    public static Regex WikiLinkPattern => WikiLinkRegex();

    public static ParsedNote Parse(string content)
    {
        var (yaml, body) = SplitFrontmatter(content);
        var frontmatter = ParseYaml(yaml);
        var searchable = StripNonContent(body);

        var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in FrontmatterTags(frontmatter)) tags.Add(t);
        foreach (Match m in InlineTagRegex().Matches(searchable))
        {
            var tag = m.Groups[1].Value.Trim('/');
            if (tag.Length > 0) tags.Add(tag);
        }

        var links = new List<NoteLink>();
        foreach (Match m in WikiLinkRegex().Matches(searchable))
        {
            var target = m.Groups[2].Value.Trim();
            var sub = m.Groups[3].Success ? m.Groups[3].Value : null;
            var alias = m.Groups[4].Success ? m.Groups[4].Value : null;
            if (target.Length == 0 && sub is null) continue;
            links.Add(new NoteLink(target, sub, alias, m.Groups[1].Value == "!"));
        }
        foreach (Match m in MarkdownLinkRegex().Matches(searchable))
        {
            var target = Uri.UnescapeDataString(m.Groups[3].Value.Trim());
            var sub = m.Groups[4].Success ? Uri.UnescapeDataString(m.Groups[4].Value) : null;
            links.Add(new NoteLink(target, sub, m.Groups[2].Value, m.Groups[1].Value == "!"));
        }

        var headings = new List<NoteHeading>();
        var bodyNoCode = FencedCodeRegex().Replace(body, m => new string('\n', CountLines(m.Value) - 1));
        foreach (Match m in HeadingRegex().Matches(bodyNoCode))
        {
            headings.Add(new NoteHeading(m.Groups[1].Length, m.Groups[2].Value.Trim(), CountLines(bodyNoCode[..m.Index])));
        }

        return new ParsedNote(frontmatter, body, tags.ToList(), links, headings);
    }

    /// <summary>Splits YAML frontmatter from the body. Returns (null, content) if there is none.</summary>
    public static (string? Yaml, string Body) SplitFrontmatter(string content)
    {
        var text = content.StartsWith('﻿') ? content[1..] : content;
        var m = FrontmatterRegex().Match(text);
        if (!m.Success) return (null, text);
        return (m.Groups[1].Value, text[m.Length..]);
    }

    public static Dictionary<string, object?> ParseYaml(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return new();
        try
        {
            var obj = Yaml.Deserialize<object?>(yaml);
            return Normalize(obj) as Dictionary<string, object?> ?? new();
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return new() { ["_error"] = "Invalid YAML frontmatter" };
        }
    }

    /// <summary>Rebuilds a note from a frontmatter dictionary and body.</summary>
    public static string Compose(Dictionary<string, object?> frontmatter, string body)
    {
        if (frontmatter.Count == 0) return body;
        var yaml = YamlOut.Serialize(frontmatter).TrimEnd();
        return $"---\n{yaml}\n---\n{body}";
    }

    /// <summary>Converts a JSON value (from a tool argument) into plain .NET objects suitable for YAML.</summary>
    public static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value)),
        _ => null,
    };

    private static object? Normalize(object? o) => o switch
    {
        IDictionary<object, object?> d => d.ToDictionary(kv => kv.Key?.ToString() ?? "", kv => Normalize(kv.Value)),
        IList<object?> l => l.Select(Normalize).ToList(),
        _ => o,
    };

    private static IEnumerable<string> FrontmatterTags(Dictionary<string, object?> fm)
    {
        foreach (var key in new[] { "tags", "tag" })
        {
            if (!fm.TryGetValue(key, out var v) || v is null) continue;
            IEnumerable<object?> items = v switch
            {
                string s => s.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries),
                IEnumerable<object?> list => list,
                _ => [v.ToString()],
            };
            foreach (var item in items)
            {
                var t = item?.ToString()?.Trim().TrimStart('#').Trim('/');
                if (!string.IsNullOrEmpty(t)) yield return t;
            }
        }
    }

    /// <summary>Removes code blocks, inline code and comments (keeps line structure roughly).</summary>
    public static string StripNonContent(string body)
    {
        var s = FencedCodeRegex().Replace(body, " ");
        s = InlineCodeRegex().Replace(s, " ");
        s = CommentRegex().Replace(s, " ");
        return s;
    }

    private static int CountLines(string s)
    {
        var n = 1;
        foreach (var c in s) if (c == '\n') n++;
        return n;
    }
}

public static partial class NoteParser
{
    /// <summary>
    /// Rewrites wiki links and local markdown links. <paramref name="rewrite"/> returns the new target
    /// (without subpath/alias) or null to leave the link untouched. Links inside code are never touched.
    /// </summary>
    public static string RewriteLinks(string content, Func<NoteLink, bool, string?> rewrite)
    {
        var protectedRanges = new List<(int Start, int End)>();
        foreach (Match m in FencedCodeRegex().Matches(content)) protectedRanges.Add((m.Index, m.Index + m.Length));
        foreach (Match m in InlineCodeRegex().Matches(content)) protectedRanges.Add((m.Index, m.Index + m.Length));
        bool IsProtected(Match m) => protectedRanges.Any(r => m.Index < r.End && m.Index + m.Length > r.Start);

        var result = WikiLinkRegex().Replace(content, m =>
        {
            if (IsProtected(m)) return m.Value;
            var link = new NoteLink(m.Groups[2].Value.Trim(), m.Groups[3].Success ? m.Groups[3].Value : null,
                m.Groups[4].Success ? m.Groups[4].Value : null, m.Groups[1].Value == "!");
            if (link.Target.Length == 0) return m.Value;
            var target = rewrite(link, true);
            if (target is null) return m.Value;
            return $"{m.Groups[1].Value}[[{target}{link.Subpath}{(link.Alias is null ? "" : "|" + link.Alias)}]]";
        });

        protectedRanges.Clear();
        foreach (Match m in FencedCodeRegex().Matches(result)) protectedRanges.Add((m.Index, m.Index + m.Length));
        foreach (Match m in InlineCodeRegex().Matches(result)) protectedRanges.Add((m.Index, m.Index + m.Length));

        return MarkdownLinkRegex().Replace(result, m =>
        {
            if (IsProtected(m)) return m.Value;
            var link = new NoteLink(Uri.UnescapeDataString(m.Groups[3].Value), m.Groups[4].Success ? m.Groups[4].Value : null,
                m.Groups[2].Value, m.Groups[1].Value == "!");
            var target = rewrite(link, false);
            if (target is null) return m.Value;
            var encoded = string.Join('/', target.Split('/').Select(Uri.EscapeDataString));
            return $"{m.Groups[1].Value}[{m.Groups[2].Value}]({encoded}{link.Subpath})";
        });
    }

    /// <summary>Converts a Moment.js date format (as used by Obsidian) to a .NET format string.</summary>
    public static string MomentToDotNet(string moment)
    {
        var sb = new System.Text.StringBuilder();
        var i = 0;
        (string Moment, string Net)[] tokens =
        [
            ("YYYY", "yyyy"), ("YY", "yy"), ("MMMM", "MMMM"), ("MMM", "MMM"), ("MM", "MM"), ("M", "%M"),
            ("DDDD", "%d"), ("Do", "%d"), ("DD", "dd"), ("D", "%d"), ("dddd", "dddd"), ("ddd", "ddd"),
            ("HH", "HH"), ("H", "%H"), ("mm", "mm"), ("ss", "ss"),
        ];
        while (i < moment.Length)
        {
            if (moment[i] == '[')
            {
                var end = moment.IndexOf(']', i);
                if (end < 0) end = moment.Length;
                var literal = moment[(i + 1)..Math.Min(end, moment.Length)];
                sb.Append('\'').Append(literal.Replace("'", "")).Append('\'');
                i = end + 1;
                continue;
            }
            var tok = tokens.FirstOrDefault(t => string.CompareOrdinal(moment, i, t.Moment, 0, t.Moment.Length) == 0);
            if (tok.Moment is not null)
            {
                // "%x" is only needed when the single specifier is the whole format string.
                sb.Append(tok.Net.StartsWith('%') && moment.Length > tok.Moment.Length ? tok.Net[1..] : tok.Net);
                i += tok.Moment.Length;
            }
            else
            {
                var c = moment[i];
                sb.Append(char.IsLetter(c) ? $"'{c}'" : c is '/' or ':' or '\\' or '%' ? $"\\{c}" : c.ToString());
                i++;
            }
        }
        return sb.ToString();
    }
}
