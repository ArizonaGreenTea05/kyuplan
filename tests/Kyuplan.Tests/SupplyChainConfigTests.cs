using System.Text.RegularExpressions;

namespace Kyuplan.Tests;

/// <summary>
/// The dependency and supply-chain checks live in files nothing compiles (.github/dependabot.yml, the workflows): a new package.json or
/// Dockerfile that Dependabot does not know, or a release that lost its scan, would go unnoticed. These tests read the files as text.
/// </summary>
public partial class SupplyChainConfigTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("global.json not found above the test binaries.");
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    private static string Relative(string path) => "/" + Path.GetRelativePath(Root, path).Replace('\\', '/');

    private static string Folder(string path) => Relative(Path.GetDirectoryName(path)!) is var folder && folder == "/." ? "/" : folder;

    private static IEnumerable<string> Files(string name) =>
        Directory.EnumerateFiles(Root, name, SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "node_modules" or "bin" or "obj" or ".git" or "build"));

    /// <summary>The ecosystem of every update entry with the folders it watches (<c>directory</c> and the items of <c>directories</c>).</summary>
    private static List<(string Ecosystem, List<string> Directories)> Updates()
    {
        var result = new List<(string, List<string>)>();
        foreach (var block in Regex.Split(Read(".github/dependabot.yml"), @"^\s*- package-ecosystem:", RegexOptions.Multiline).Skip(1))
        {
            var lines = block.Split('\n');
            var directories = new List<string>();
            var inList = false;
            foreach (var raw in lines.Skip(1))
            {
                var line = raw.Trim();
                if (line.StartsWith("directory:")) directories.Add(line["directory:".Length..].Trim());
                else if (line.StartsWith("directories:")) inList = true;
                else if (inList && line.StartsWith("- ")) directories.Add(line[2..].Trim());
                else if (line.Length > 0) inList = false;
            }
            result.Add((lines[0].Trim(), directories));
        }
        return result;
    }

    private static List<string> Watched(string ecosystem) => [.. Updates().Where(u => u.Ecosystem == ecosystem).SelectMany(u => u.Directories)];

    [Fact]
    public void Dependabot_watches_the_nuget_packages_the_npm_packages_the_images_and_the_actions()
    {
        var ecosystems = Updates().Select(u => u.Ecosystem).ToList();
        Assert.Equal(["docker", "docker-compose", "github-actions", "npm", "npm", "nuget"], ecosystems.Order());
    }

    [Fact]
    public void Dependabot_has_a_weekly_schedule_for_every_entry()
    {
        var text = Read(".github/dependabot.yml");
        Assert.Equal(Updates().Count, Regex.Matches(text, @"interval:\s*weekly").Count);
    }

    [Fact]
    public void Every_package_json_is_watched_by_dependabot()
    {
        var folders = Files("package.json").Select(Folder).ToList();
        Assert.NotEmpty(folders);
        Assert.All(folders, folder => Assert.Contains(folder, Watched("npm")));
    }

    [Fact]
    public void Dependabot_does_not_watch_an_npm_folder_that_does_not_exist()
    {
        var folders = Files("package.json").Select(Folder).ToList();
        Assert.All(Watched("npm"), folder => Assert.Contains(folder, folders));
    }

    [Fact]
    public void Central_package_versions_sit_in_the_folder_dependabot_watches_for_nuget()
    {
        Assert.Contains("/", Watched("nuget"));
        Assert.True(File.Exists(Path.Combine(Root, "Directory.Packages.props")));
    }

    [Fact]
    public void Every_dockerfile_is_watched_by_dependabot()
    {
        var folders = Files("Dockerfile").Select(Folder).ToList();
        Assert.NotEmpty(folders);
        Assert.All(folders, folder => Assert.Contains(folder, Watched("docker")));
    }

    [Fact]
    public void Every_compose_file_is_in_a_folder_dependabot_watches()
    {
        var folders = Files("docker-compose*.yml").Select(Folder).ToList();
        Assert.NotEmpty(folders);
        Assert.All(folders, folder => Assert.Contains(folder, Watched("docker-compose")));
    }

    [Fact]
    public void Dependabot_watches_the_workflows_and_every_composite_action()
    {
        var watched = Watched("github-actions");
        Assert.Contains("/", watched);
        Assert.Contains("/.github/actions/*", watched);
        // the glob matches the folders that have an action.yml
        Assert.All(Files("action.yml"), action => Assert.Equal("/.github/actions", Path.GetDirectoryName(Folder(action))!.Replace('\\', '/')));
    }

    [Fact]
    public void The_audit_action_checks_nuget_including_the_wpf_client_and_both_npm_projects()
    {
        var action = Read(".github/actions/audit-dependencies/action.yml");
        Assert.Contains("package --vulnerable --include-transitive", action);
        Assert.Contains("tests/Kyuplan.Tests src/Kyuplan.Client.Wpf", action);
        // the Windows-only WPF client only restores on Linux with this
        Assert.Contains("EnableWindowsTargeting: 'true'", action);
        Assert.Contains("has the following vulnerable packages", action);
        Assert.Contains("NU1900", action);
        Assert.Contains("working-directory: website", action);
        Assert.Contains("working-directory: tests/ui", action);
        Assert.Equal(2, Regex.Matches(action, @"npm audit --audit-level").Count);
    }

    [Theory]
    [InlineData(".github/workflows/dependency-audit.yml")]
    [InlineData(".github/workflows/release.yml")]
    public void The_audit_runs_in_the_workflow(string workflow) => Assert.Contains("uses: ./.github/actions/audit-dependencies", Read(workflow));

    [Fact]
    public void The_audit_runs_on_pull_requests_and_weekly()
    {
        var workflow = Read(".github/workflows/dependency-audit.yml");
        Assert.Contains("pull_request:", workflow);
        Assert.Matches(@"cron: '[^']+ \* \* [0-7]'", workflow);
    }

    [Fact]
    public void Every_job_of_the_release_waits_for_the_audit()
    {
        var release = Read(".github/workflows/release.yml");
        var jobs = Regex.Split(release[release.IndexOf("\njobs:", StringComparison.Ordinal)..], @"^  (?=[a-z-]+:\s*$)", RegexOptions.Multiline).Skip(1).ToList();
        Assert.Equal("audit", jobs[0][..jobs[0].IndexOf(':')]);
        Assert.Equal("image", jobs[^1][..jobs[^1].IndexOf(':')]);
        var builders = jobs[1..^1];
        Assert.NotEmpty(builders);
        foreach (var job in builders) Assert.Matches(@"needs:\s*audit\b", job);
        // the image waits for every job in between (a new client job has to be added to its needs)
        var imageNeeds = Regex.Match(jobs[^1], @"needs:\s*\[([^\]]*)\]").Groups[1].Value.Split(',').Select(n => n.Trim());
        Assert.Equal(builders.Select(j => j[..j.IndexOf(':')]).Order(), imageNeeds.Order());
    }

    [Fact]
    public void The_audit_job_of_the_release_only_reads_the_repository()
    {
        // the workflow grants contents: write and packages: write to publish; the audit job needs neither
        var release = Read(".github/workflows/release.yml");
        var audit = release[release.IndexOf("\n  audit:", StringComparison.Ordinal)..release.IndexOf("\n  windows:", StringComparison.Ordinal)];
        Assert.Matches(@"permissions:\s*\n\s+contents: read\b", audit);
        Assert.DoesNotContain("write", audit);
    }

    [Fact]
    public void The_audit_report_is_written_outside_the_checkout()
    {
        var action = Read(".github/actions/audit-dependencies/action.yml");
        Assert.Contains("$RUNNER_TEMP/nuget-audit.txt", action);
        Assert.DoesNotContain(" nuget-audit.txt", action);
    }

    [Fact]
    public void The_release_scans_the_image_before_it_pushes_it_and_attaches_the_sbom()
    {
        var release = Read(".github/workflows/release.yml");
        var scan = release.IndexOf("aquasecurity/trivy-action", StringComparison.Ordinal);
        var push = release.IndexOf("push: true", StringComparison.Ordinal);
        Assert.InRange(scan, 1, push - 1);
        Assert.Contains("exit-code: '1'", release);
        Assert.Contains("severity: HIGH,CRITICAL", release);
        Assert.Contains("ignore-unfixed: true", release);
        // pinned to a commit, not to a tag that can be moved
        Assert.Matches(@"aquasecurity/trivy-action@[0-9a-f]{40}\b", release);
        Assert.DoesNotMatch(@"trivy-action@v", release);
        Assert.Contains("format: cyclonedx", release);
        Assert.Contains("artifacts/sbom/*", release);
    }

    [Fact]
    public void The_trivy_ignore_file_the_release_names_exists_and_lists_only_cve_ids()
    {
        Assert.Contains("trivyignores: .trivyignore", Read(".github/workflows/release.yml"));
        var entries = Read(".trivyignore").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));
        Assert.All(entries, entry => Assert.Matches(@"^(CVE-\d{4}-\d{4,}|GHSA-[\w-]+|DS-?\d+|AVD-[\w-]+)$", entry));
    }
}
