using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Infrastructure;

namespace Kyuplan.Tests;

/// <summary>
/// The documentation is Markdown nothing compiles, so it drifts from the code: a configuration key that was renamed, a file that moved, a
/// link whose heading changed. These tests read the Markdown as text and fail when what it claims no longer exists. (The reference tables
/// themselves are docs/reference.md; the README is only an overview.)
/// </summary>
public partial class DocsDriftTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("global.json not found above the test binaries.");
    }

    /// <summary>README, AGENTS.md and every Markdown file of docs/, relative to the repository root with forward slashes.</summary>
    public static IEnumerable<string> MarkdownFiles() =>
        new[] { "README.md", "AGENTS.md" }
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "docs"), "*.md", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/')))
            .OrderBy(f => f, StringComparer.Ordinal);

    public static TheoryData<string> MarkdownFileData() => new(MarkdownFiles());

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    /// <summary>The Markdown without fenced code blocks (their content is examples, not claims).</summary>
    private static string Prose(string relativePath) => FencePattern().Replace(Read(relativePath).Replace("\r\n", "\n"), "");

    [GeneratedRegex(@"^```.*?^```[ \t]*$", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FencePattern();

    // ---- configuration keys ----

    /// <summary>Sections the configuration knows, from the options classes (<c>Section</c> constant) and appsettings.json.</summary>
    private static readonly HashSet<string> Sections = new(StringComparer.Ordinal);

    /// <summary>Every key (<c>Section:Property</c>), also every parent of a nested key, that the code or appsettings.json defines.</summary>
    private static readonly HashSet<string> Keys = BuildKeys();

    private static HashSet<string> BuildKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        void Add(string key)
        {
            for (var at = key.IndexOf(':'); at >= 0; at = key.IndexOf(':', at + 1)) keys.Add(key[..at]);
            keys.Add(key);
        }

        foreach (var type in typeof(SpondOptions).Assembly.GetTypes())
        {
            if (type.GetField("Section", BindingFlags.Public | BindingFlags.Static) is not { IsLiteral: true } field) continue;
            var section = (string)field.GetRawConstantValue()!;
            Add(section);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) Add($"{section}:{property.Name}");
        }

        Add(SettingsFile.PathSetting);
        Add(SettingsFile.KeyDirectorySetting);
        Add(ThemePackStore.DirectorySetting);

        void Walk(JsonElement element, string prefix)
        {
            foreach (var property in element.EnumerateObject())
            {
                var key = prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}";
                Add(key);
                if (property.Value.ValueKind == JsonValueKind.Object) Walk(property.Value, key);
            }
        }
        // The API's settings and the clients' own (PlannerApi:BaseAddress, ...), which the docs name as well.
        foreach (var file in new[] { "src/Kyuplan.Api/appsettings.json", "src/Kyuplan.Client.Blazor/wwwroot/appsettings.json", "src/Kyuplan.Client.Wpf/appsettings.json" })
        {
            using var appsettings = JsonDocument.Parse(Read(file));
            Walk(appsettings.RootElement, "");
        }

        foreach (var key in keys) Sections.Add(key.Split(':')[0]);
        return keys;
    }

    /// <summary>A configuration key in code font, <c>Section:Key</c> (possibly deeper). <c>Spond:Email</c> / <c>PhoneNumber</c> shorthands are not keys of their own.</summary>
    [GeneratedRegex(@"`([A-Z][A-Za-z]*(?::[A-Za-z]+)+)`")]
    private static partial Regex ConfigKeyPattern();

    [Theory]
    [MemberData(nameof(MarkdownFileData))]
    public void Configuration_keys_in_the_docs_exist(string file)
    {
        _ = Keys;
        var unknown = ConfigKeyPattern().Matches(Prose(file))
            .Select(m => m.Groups[1].Value)
            .Where(key => key != "Section:Key" && !Keys.Contains(key))
            .Distinct()
            .ToList();

        Assert.True(unknown.Count == 0,
            $"{file} names configuration keys that neither an options class nor appsettings.json has: {string.Join(", ", unknown)}");
    }

    [Fact]
    public void The_key_catalogue_knows_the_documented_sections()
    {
        _ = Keys;
        foreach (var section in new[] { "Spond", "Statistics", "Database", "Accounts", "Api", "Branding", "Sync", "DefaultData", "Setup", "Themes", "Logging" })
            Assert.Contains(section, Sections);
        Assert.Contains("Spond:AccountCooldown", Keys);
        Assert.Contains("Logging:File:Path", Keys);
        Assert.DoesNotContain("Spond:NoSuchKey", Keys);
    }

    [Fact]
    public void The_reference_documents_every_core_option_section()
    {
        // The sections a deployment has to know: each is mentioned in the reference by at least one of its keys.
        var reference = Prose("docs/reference.md");
        foreach (var section in new[] { "Spond", "Statistics", "Database", "Accounts", "Api", "Branding", "Sync", "DefaultData", "Setup" })
            Assert.True(reference.Contains($"`{section}:", StringComparison.Ordinal), $"docs/reference.md mentions no key of {section}.");
    }

    // ---- links and paths ----

    [GeneratedRegex(@"(?<!\!)\[[^\]]*\]\(([^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex LinkPattern();

    /// <summary>The anchors GitHub and Docusaurus make from the headings of a Markdown file.</summary>
    private static HashSet<string> Anchors(string relativePath)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match heading in HeadingPattern().Matches(Prose(relativePath)))
        {
            var text = LinkText().Replace(heading.Groups[1].Value.Replace("`", ""), "$1").Trim().ToLowerInvariant();
            var slug = Regex.Replace(Regex.Replace(text, @"[^\p{L}\p{N}\s_-]", ""), @"\s", "-");
            var count = seen.GetValueOrDefault(slug);
            seen[slug] = count + 1;
            anchors.Add(count == 0 ? slug : $"{slug}-{count}");
        }
        return anchors;
    }

    [GeneratedRegex(@"^#{1,6}[ \t]+(.+?)[ \t]*#*[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkText();

    [Theory]
    [MemberData(nameof(MarkdownFileData))]
    public void Relative_links_in_the_docs_resolve(string file)
    {
        var folder = Path.GetDirectoryName(file)!;
        var broken = new List<string>();
        foreach (Match link in LinkPattern().Matches(Prose(file)))
        {
            var target = link.Groups[1].Value;
            if (Regex.IsMatch(target, @"^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase) || target.StartsWith('/')) continue;

            var hash = target.IndexOf('#');
            var path = hash < 0 ? target : target[..hash];
            var anchor = hash < 0 ? "" : target[(hash + 1)..];

            var resolved = path.Length == 0 ? file : Path.GetRelativePath(Root, Path.GetFullPath(Path.Combine(Root, folder, Uri.UnescapeDataString(path)))).Replace('\\', '/');
            var full = Path.Combine(Root, resolved);
            if (!File.Exists(full) && !Directory.Exists(full)) { broken.Add($"{target} (no such file)"); continue; }
            if (anchor.Length > 0 && resolved.EndsWith(".md", StringComparison.Ordinal) && !Anchors(resolved).Contains(anchor))
                broken.Add($"{target} (no such heading)");
        }

        Assert.True(broken.Count == 0, $"{file} has links that lead nowhere: {string.Join("; ", broken)}");
    }

    /// <summary>A path in code font that starts at the repository root, such as <c>src/Kyuplan.Api/Dockerfile</c> or <c>docs/reference.md#run-it</c>.</summary>
    [GeneratedRegex(@"`((?:src|tests|docs|website|deploy|installer|assets|tools|\.github)/[^`\s*<>{}]+)`")]
    private static partial Regex RootedPathPattern();

    [Theory]
    [MemberData(nameof(MarkdownFileData))]
    public void Repository_paths_in_the_docs_exist(string file)
    {
        var missing = RootedPathPattern().Matches(Prose(file))
            .Select(m => m.Groups[1].Value.Split('#')[0].TrimEnd('/', '.', ','))
            .Where(path => path.Length > 0 && !File.Exists(Path.Combine(Root, path)) && !Directory.Exists(Path.Combine(Root, path)))
            .Distinct()
            .ToList();

        Assert.True(missing.Count == 0, $"{file} names paths that do not exist: {string.Join(", ", missing)}");
    }

    // ---- structure ----

    [Fact]
    public void The_readme_stays_a_short_overview()
    {
        // The detail lives in docs/; a README that grows back into a second reference is the drift this test is here to stop.
        var lines = Read("README.md").Replace("\r\n", "\n").Split('\n').Length;
        Assert.True(lines <= 80, $"README.md has {lines} lines: move the detail to docs/ and link to it.");
    }

    [Fact]
    public void Every_doc_file_is_a_page_of_a_site_or_the_index()
    {
        var audiences = Read("website/scripts/audiences.mjs");
        foreach (var file in MarkdownFiles().Where(f => f.StartsWith("docs/", StringComparison.Ordinal) && f != "docs/README.md" && !f.StartsWith("docs/examples/", StringComparison.Ordinal)))
            Assert.True(audiences.Contains($"'{file}'", StringComparison.Ordinal), $"{file} is not listed in website/scripts/audiences.mjs.");
    }

    [Fact]
    public void Every_page_of_the_sites_exists()
    {
        foreach (Match source in Regex.Matches(Read("website/scripts/audiences.mjs"), @"source:\s*'([^']+)'"))
            Assert.True(File.Exists(Path.Combine(Root, source.Groups[1].Value)), $"audiences.mjs lists {source.Groups[1].Value}, which does not exist.");
    }
}
