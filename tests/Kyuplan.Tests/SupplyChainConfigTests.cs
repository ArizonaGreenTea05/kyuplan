using System.Text.RegularExpressions;
using Kyuplan.Contracts;

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
        Assert.Equal(["docker", "docker", "docker-compose", "github-actions", "npm", "npm", "nuget"], ecosystems.Order());
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
        // both npm projects go through the script that applies the accept-list
        Assert.Equal(2, Regex.Matches(action, @"npm-audit\.mjs""? ""\$NPM_LEVEL"" \.npm-audit-ignore").Count);
        Assert.DoesNotContain("npm audit --audit-level", action);
    }

    [Fact]
    public void The_npm_audit_script_fails_on_an_unreadable_report_and_on_advisories_outside_the_accept_list()
    {
        var script = Read(".github/actions/audit-dependencies/npm-audit.mjs");
        Assert.Contains("'audit', '--json'", script);
        Assert.Contains("npm audit gave no readable report", script);
        Assert.Contains("process.exit(1)", script);
        Assert.Contains("!accepted.has(id)", script);
    }

    [Fact]
    public void Every_accepted_npm_advisory_has_a_reason_above_it()
    {
        var lines = Read("website/.npm-audit-ignore").Split('\n').Select(l => l.Trim()).ToList();
        var ids = lines.Select((line, index) => (line, index)).Where(x => Regex.IsMatch(x.line, @"^GHSA(-[a-z0-9]{4}){3}$")).ToList();
        Assert.Equal(ids.Count, ids.Select(x => x.line).Distinct().Count());
        // the line above an id is a comment (the reason); nothing but ids and comments is allowed
        Assert.All(ids, x => Assert.StartsWith("#", lines[x.index - 1]));
        Assert.All(lines.Where(l => l.Length > 0 && !l.StartsWith('#')), l => Assert.Matches(@"^GHSA(-[a-z0-9]{4}){3}$", l));
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

    /// <summary>The jobs of release.yml by name, in file order, each with its text (from its name to the next job).</summary>
    private static List<(string Name, string Text)> ReleaseJobs()
    {
        var release = Read(".github/workflows/release.yml");
        return [.. Regex.Split(release[release.IndexOf("\njobs:", StringComparison.Ordinal)..], @"^  (?=[a-z-]+:\s*$)", RegexOptions.Multiline)
            .Skip(1).Select(job => (job[..job.IndexOf(':')], job))];
    }

    private static string ReleaseJob(string name) => ReleaseJobs().Single(j => j.Name == name).Text;

    [Fact]
    public void Every_job_of_the_release_waits_for_the_audit()
    {
        var jobs = ReleaseJobs();
        Assert.Equal("audit", jobs[0].Name);
        // the image collects every build, the release is published last
        Assert.Equal(["image", "publish"], jobs.TakeLast(2).Select(j => j.Name));
        var builders = jobs[1..^2];
        Assert.NotEmpty(builders);
        foreach (var job in builders) Assert.Matches(@"needs:\s*audit\b", job.Text);
        // the image waits for every build in between (a new client job has to be added to its needs)
        var imageNeeds = Regex.Match(jobs[^2].Text, @"needs:\s*\[([^\]]*)\]").Groups[1].Value.Split(',').Select(n => n.Trim());
        Assert.Equal(builders.Select(j => j.Name).Order(), imageNeeds.Order());
        Assert.Matches(@"needs:\s*image\b", jobs[^1].Text);
    }

    [Fact]
    public void The_audit_job_of_the_release_only_reads_the_repository()
    {
        var audit = ReleaseJob("audit");
        Assert.Matches(@"permissions:\s*\n\s+contents: read\b", audit);
        Assert.DoesNotContain("write", audit);
    }

    [Fact]
    public void The_release_token_is_read_only_except_for_the_image_push_and_the_publish_job()
    {
        var release = Read(".github/workflows/release.yml");
        // the workflow-level default (before the jobs) is read-only
        var header = release[..release.IndexOf("\njobs:", StringComparison.Ordinal)];
        Assert.Matches(@"(?m)^permissions:\s*\n  contents: read\s*$", header);
        Assert.DoesNotMatch(@"(?m)^\s+[a-z-]+: write", header);

        // only the image job may write packages and only the publish job contents; the jobs with the signing secrets write nothing
        foreach (var (name, text) in ReleaseJobs())
        {
            var permissions = Regex.Match(text, @"(?m)^    permissions:\s*\n((?:      \S.*\n)+)").Groups[1].Value;
            var writes = Regex.Matches(permissions, @"^\s+([a-z-]+): write", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
            var expected = name switch { "image" => new[] { "packages" }, "publish" => ["contents"], _ => [] };
            Assert.True(expected.SequenceEqual(writes), $"job {name} writes [{string.Join(",", writes)}], expected [{string.Join(",", expected)}]");
        }

        // the signing secrets sit in jobs that cannot write, never in the two that can
        foreach (var name in new[] { "image", "publish" })
        {
            var text = ReleaseJob(name);
            Assert.DoesNotContain("UPDATE_SIGNING_KEY", text);
            Assert.DoesNotContain("WINDOWS_CERT", text);
            Assert.DoesNotContain("ANDROID_KEY", text);
        }
        foreach (var name in new[] { "windows", "android", "linux" })
            Assert.DoesNotContain("write", Regex.Match(ReleaseJob(name), @"(?m)^    permissions:\s*\n((?:      \S.*\n)+)").Groups[1].Value);

        // the release is created from artifacts by the publish job; the image job hands over the SBOM and the image name
        var image = ReleaseJob("image");
        Assert.Contains("name: sbom", image);
        Assert.Contains("name: ${{ steps.image.outputs.name }}", image);
        Assert.DoesNotContain("uses: ./.github/actions/publish-release", image);
        var publish = ReleaseJob("publish");
        Assert.Contains("uses: ./.github/actions/publish-release", publish);
        Assert.Contains("name: sbom", publish);
        Assert.Contains("needs.image.outputs.name", publish);
        Assert.DoesNotContain("steps.image.", publish);
        Assert.DoesNotContain("push: true", publish);
    }

    [Fact]
    public void A_release_without_the_update_key_or_the_android_keystore_fails_before_anything_is_built_unless_unsigned_is_allowed()
    {
        var audit = ReleaseJob("audit");
        var start = audit.IndexOf("Check that the release can be signed", StringComparison.Ordinal);
        Assert.True(start > 0);
        var gate = audit[start..audit.IndexOf("actions/checkout@", StringComparison.Ordinal)];
        // only whether a secret exists is passed on, never its value
        Assert.Contains("HAS_UPDATE_KEY: ${{ secrets.UPDATE_SIGNING_KEY != '' }}", gate);
        Assert.Contains("HAS_ANDROID_KEYSTORE: ${{ secrets.ANDROID_KEYSTORE != '' }}", gate);
        Assert.DoesNotContain("WINDOWS_CERT", gate);
        Assert.Contains("ALLOW_UNSIGNED: ${{ vars.ALLOW_UNSIGNED_RELEASE }}", gate);
        Assert.Contains("[ \"$ALLOW_UNSIGNED\" = true ]", gate);
        Assert.Contains("exit 1", gate);
        // the first step of the first job: nothing else runs before it
        Assert.DoesNotContain("uses:", audit[..start]);
        Assert.Contains("ALLOW_UNSIGNED_RELEASE", Read("docs/deployment.md"));
    }

    private static IEnumerable<string> WorkflowAndActionFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root, ".github"), "*.yml", SearchOption.AllDirectories).Where(f => !f.EndsWith("dependabot.yml"));

    [Fact]
    public void Every_action_of_the_workflows_and_composite_actions_is_pinned_to_a_commit_with_its_version_in_a_comment()
    {
        var external = new List<string>();
        foreach (var file in WorkflowAndActionFiles())
            foreach (var line in File.ReadAllLines(file).Where(l => Regex.IsMatch(l, @"^\s*(- )?uses:")))
            {
                var action = Regex.Match(line, @"uses:\s*(\S+)").Groups[1].Value;
                if (action.StartsWith("./")) continue; // an action or workflow of this repository
                external.Add(action);
                Assert.Matches(@"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$", action);
                Assert.Matches(@"@[0-9a-f]{40} # v\d+(\.\d+)*\s*$", line);
            }
        // guards the test itself against matching nothing
        Assert.Contains(external, a => a.StartsWith("actions/checkout@"));
        Assert.Contains(external, a => a.StartsWith("docker/login-action@"));
        Assert.Contains(external, a => a.StartsWith("anthropics/claude-code-action@"));
        Assert.Contains(external, a => a.StartsWith("aquasecurity/trivy-action@"));
    }

    [Fact]
    public void The_documentation_stage_builds_with_the_node_lts_line_of_the_workflows_and_dependabot_leaves_the_major_alone()
    {
        var image = int.Parse(Regex.Match(Read("src/Kyuplan.Api/Dockerfile"), @"^FROM .*\bnode:(\d+)-", RegexOptions.Multiline).Groups[1].Value);
        Assert.Equal(0, image % 2); // odd majors are never LTS
        var versions = WorkflowAndActionFiles()
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"node-version:\s*(\d+)").Select(m => int.Parse(m.Groups[1].Value))).ToList();
        Assert.NotEmpty(versions);
        Assert.All(versions, v => Assert.Equal(image, v));
        Assert.Contains($"Node {image}", Read("docs/developer-guide.md"));

        Assert.Matches(@"dependency-name: node\s*\n\s*update-types: \['version-update:semver-major'\]", Read(".github/dependabot.yml"));
    }

    [Fact]
    public void The_base_images_of_the_dockerfile_are_pinned_to_a_digest()
    {
        var stages = new List<string> { "scratch" };
        var froms = Regex.Matches(Read("src/Kyuplan.Api/Dockerfile"), @"^FROM (?:--platform=\S+ )?(\S+)(?: AS (\S+))?", RegexOptions.Multiline);
        Assert.Equal(4, froms.Count);
        foreach (var from in froms.Cast<Match>())
        {
            var image = from.Groups[1].Value;
            // a stage that continues an earlier one has no image of its own
            if (!stages.Contains(image)) Assert.Matches(@"^[\w./-]+:[\w.-]+@sha256:[0-9a-f]{64}$", image);
            if (from.Groups[2].Success) stages.Add(from.Groups[2].Value);
        }
    }

    [Fact]
    public void The_audit_report_is_written_outside_the_checkout()
    {
        var action = Read(".github/actions/audit-dependencies/action.yml");
        Assert.Contains("$RUNNER_TEMP/nuget-audit.txt", action);
        Assert.DoesNotContain(" nuget-audit.txt", action);
    }

    [Fact]
    public void The_release_version_action_strips_the_carriage_returns_of_jq_on_the_windows_runner()
    {
        // jq.exe ends lines with CRLF: "true\r" != "true" failed the release with "a word appears twice"
        var lines = Read(".github/actions/release-version/action.yml").Split('\n').Where(l => l.Contains("jq -r")).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Contains("| tr -d '\\r')", l));
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
    public void The_pr_build_builds_the_three_artifacts_replaces_the_old_ones_and_never_touches_the_release_secrets()
    {
        var workflow = Read(".github/workflows/pr-build.yml");
        // WPF installer and Android APK (shared with the preview), and the image, which carries both
        var clients = Read(".github/workflows/build-clients.yml");
        Assert.Contains("src/Kyuplan.Client.Wpf", clients);
        Assert.Contains("src/Kyuplan.Client.Android", clients);
        Assert.Contains("uses: ./.github/workflows/build-clients.yml", workflow);
        Assert.Contains("build-contexts: installer=artifacts/installer", workflow);
        Assert.Matches(@"needs:\s*clients\b", workflow);
        // a new commit replaces the release (with its files and tag), the image tag and the comment; closing removes them
        Assert.Contains("gh release delete \"$TAG\" --cleanup-tag", workflow);
        Assert.Contains("<!-- pr-build -->", workflow);
        Assert.Contains("types: [opened, reopened, synchronize, ready_for_review, closed]", workflow);
        Assert.Contains("cancel-in-progress: true", workflow);
        // forks get no token that can write
        Assert.Contains("github.event.pull_request.head.repo.full_name == github.repository", workflow);
        // only the built-in token: the signing keys of the release stay out of builds of unreviewed code
        foreach (var file in new[] { workflow, clients, Read(".github/workflows/preview.yml") })
        {
            Assert.DoesNotContain("WINDOWS_CERT", file);
            Assert.DoesNotContain("UPDATE_SIGNING_KEY", file);
            Assert.DoesNotContain("ANDROID_KEY", file);
        }

        // a name the installer parser accepts, so the server offers the installers inside the image
        Assert.NotNull(ClientDownload.Parse("Kyuplan-windows-0.0.0-pr.12.3.exe"));
        Assert.NotNull(ClientDownload.Parse("Kyuplan-android-0.0.0-pr.12.3.apk"));
        Assert.Contains("0.0.0-pr.${{ github.event.pull_request.number }}.${{ github.run_number }}", workflow);
    }

    [Fact]
    public void The_preview_publishes_every_commit_of_main_as_its_own_package_apart_from_the_releases()
    {
        var preview = Read(".github/workflows/preview.yml");
        Assert.Contains("branches: [main]", preview);
        Assert.Contains("uses: ./.github/workflows/build-clients.yml", preview);
        Assert.Contains("kyuplan-preview", preview);
        Assert.Contains("sha-${GITHUB_SHA::7}", preview);
        // every commit gets its package: a newer run must not cancel one that is still publishing
        Assert.Contains("cancel-in-progress: false", preview);
        Assert.Contains("push: true", preview);
        // the release's image has another name, so the preview cannot move its tags (latest, 1.2, ...)
        Assert.DoesNotContain("/kyuplan\"", preview);
        Assert.DoesNotContain("/kyuplan:", preview);
        Assert.Contains("ghcr.io/${GITHUB_REPOSITORY_OWNER,,}/kyuplan\"", Read(".github/workflows/release.yml"));
        Assert.NotNull(ClientDownload.Parse("Kyuplan-windows-0.0.0-preview.34.exe"));
        Assert.NotNull(ClientDownload.Parse("Kyuplan-android-0.0.0-preview.34.apk"));
        Assert.Contains("0.0.0-preview.${{ github.run_number }}", preview);
    }

    [Fact]
    public void The_trivy_ignore_file_the_release_names_exists_and_lists_only_cve_ids()
    {
        Assert.Contains("trivyignores: .trivyignore", Read(".github/workflows/release.yml"));
        var entries = Read(".trivyignore").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));
        Assert.All(entries, entry => Assert.Matches(@"^(CVE-\d{4}-\d{4,}|GHSA-[\w-]+|DS-?\d+|AVD-[\w-]+)$", entry));
    }
}
