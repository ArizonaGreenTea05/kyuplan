using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>A repository as the tests have it: what the fetcher answers for "owner/repo".</summary>
internal sealed class FakeThemeFetcher : IThemeSourceFetcher
{
    public const string Cyberpunk = "ArizonaGreenTea05/kyuplan.themes.cyberpunk";

    public Dictionary<string, ThemeSourceFetch> Repositories { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, every read fails with it.</summary>
    public Exception? Failure { get; set; }

    public int Calls { get; private set; }

    public static ThemeSourceFile File(string name, ThemePackDto pack) => new($"{ThemeSourceRules.Folder}/{name}", Packs.Json(pack));

    public static ThemeSourceFetch Fetch(params ThemeSourceFile[] files) => new(files, []);

    /// <summary>The example theme pack of the issue: two themes.</summary>
    public static ThemeSourceFetch CyberpunkPack() => Fetch(
        File("cyberpunk-2077.json", Packs.Valid("cyberpunk-2077") with { Name = "Cyberpunk 2077", NameDe = null }),
        File("matrix.json", Packs.Valid("matrix") with { Name = "Matrix", NameDe = null }));

    public Task<ThemeSourceFetch> FetchAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        Calls++;
        if (Failure is not null) throw Failure;

        return Repositories.TryGetValue($"{owner}/{repo}", out var fetched)
            ? Task.FromResult(fetched)
            : throw new ThemeSourceException("The repository was not found. It has to be public.");
    }
}

/// <summary>Which addresses are theme packs.</summary>
public class ThemeSourceRulesTests
{
    [Theory]
    [InlineData("https://github.com/ArizonaGreenTea05/kyuplan.themes.cyberpunk", "ArizonaGreenTea05", "kyuplan.themes.cyberpunk")]
    [InlineData("  https://github.com/owner/repo  ", "owner", "repo")]
    [InlineData("https://github.com/owner/repo/", "owner", "repo")]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo")]
    [InlineData("https://github.com/owner/repo.git/", "owner", "repo")]
    [InlineData("https://www.github.com/owner/repo", "owner", "repo")]
    [InlineData("HTTPS://GITHUB.COM/owner/repo", "owner", "repo")]
    [InlineData("https://github.com/a/b_c-d.e", "a", "b_c-d.e")]
    [InlineData("https://github.com/o-w-n-e-r/1", "o-w-n-e-r", "1")]
    public void An_address_of_a_repository_is_taken_apart(string url, string owner, string repo)
    {
        Assert.Equal((owner, repo), ThemeSourceRules.Parse(url));
        Assert.True(ThemeSourceRules.IsValidUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("owner/repo")]
    [InlineData("github.com/owner/repo")]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("ftp://github.com/owner/repo")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://github.com.evil.example/owner/repo")]
    [InlineData("https://evil.example/github.com/owner/repo")]
    [InlineData("https://raw.githubusercontent.com/owner/repo")]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/owner")]
    [InlineData("https://github.com/owner/repo/tree/main")]
    [InlineData("https://github.com/owner/repo/themes")]
    [InlineData("https://github.com/owner/repo?ref=main")]
    [InlineData("https://github.com/owner/repo#readme")]
    [InlineData("https://user@github.com/owner/repo")]
    [InlineData("https://user:secret@github.com/owner/repo")]
    [InlineData("https://github.com:8443/owner/repo")]
    [InlineData("https://github.com/-owner/repo")]
    [InlineData("https://github.com/owner_x/repo")]
    [InlineData("https://github.com/owner/re po")]
    [InlineData("https://github.com/owner/repo%2Fother")]
    [InlineData("https://github.com/owner/.git")]
    [InlineData("https://github.com/owner/..")]
    [InlineData("https://github.com/owner/.")]
    public void Everything_else_is_no_theme_pack(string? url)
    {
        Assert.Null(ThemeSourceRules.Parse(url));
        Assert.False(ThemeSourceRules.IsValidUrl(url));
    }

    [Fact]
    public void The_lengths_of_the_names_are_those_of_github()
    {
        Assert.NotNull(ThemeSourceRules.Parse($"https://github.com/{new string('a', 39)}/{new string('b', 100)}"));
        Assert.Null(ThemeSourceRules.Parse($"https://github.com/{new string('a', 40)}/repo"));
        Assert.Null(ThemeSourceRules.Parse($"https://github.com/owner/{new string('b', 101)}"));
    }

    [Fact]
    public void An_address_has_one_spelling()
    {
        Assert.Equal("https://github.com/owner/repo", ThemeSourceRules.Url("owner", "repo"));
    }
}

/// <summary>Reads a repository over a stubbed connection: which requests it makes, and what it does with the answers.</summary>
public class GitHubThemeFetcherTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Tree(params (string Path, string Type, long Size)[] entries) =>
        JsonSerializer.Serialize(new { sha = "abc", tree = entries.Select(e => new { path = e.Path, type = e.Type, size = e.Size }), truncated = false });

    private static (GitHubThemeFetcher Fetcher, StubHandler Handler) Fetcher(string tree, Func<string, HttpResponseMessage>? raw = null)
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.Host == "api.github.com" ? Json(tree) : (raw ?? (_ => Json("{}")))(request.RequestUri.AbsolutePath));
        return (new GitHubThemeFetcher(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task Only_the_json_files_directly_in_the_themes_folder_are_read_in_order()
    {
        var (fetcher, handler) = Fetcher(Tree(
            ("README.md", "blob", 10), ("themes", "tree", 0), ("themes/zeta.json", "blob", 20), ("themes/alpha.JSON", "blob", 20),
            ("themes/notes.txt", "blob", 5), ("themes/sub/deep.json", "blob", 20), ("other/x.json", "blob", 20), ("themes.json", "blob", 20)),
            raw: path => Json($"text of {path}"));

        var fetched = await fetcher.FetchAsync("owner", "repo", default);

        Assert.Equal(["themes/alpha.JSON", "themes/zeta.json"], fetched.Files.Select(f => f.Path));
        Assert.Equal("text of /owner/repo/HEAD/themes/alpha.JSON", fetched.Files[0].Text);
        Assert.Empty(fetched.Problems);
        Assert.Equal(3, handler.Requests.Count); // the list and the two files, nothing else
    }

    [Fact]
    public async Task A_file_with_a_byte_order_mark_is_read_without_it_so_it_still_parses()
    {
        var json = Packs.Json(Packs.Valid("bom"));
        var (fetcher, _) = Fetcher(Tree(("themes/bom.json", "blob", 20)), raw: _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(json)]) });

        var fetched = await fetcher.FetchAsync("owner", "repo", default);

        var text = Assert.Single(fetched.Files).Text;
        Assert.DoesNotContain('﻿', text);
        Assert.Equal("abc123-bom", ThemeSourceSync.Build(new ThemeSourceDto("abc123", "https://github.com/o/r", "o/r", 0, null, null, null), fetched).Offered.Single().Id);
    }

    [Fact]
    public async Task The_requests_go_to_two_fixed_hosts_and_say_who_asks()
    {
        var (fetcher, handler) = Fetcher(Tree(("themes/a b.json", "blob", 20)));

        await fetcher.FetchAsync("Owner", "re.po", default);

        Assert.Equal("https://api.github.com/repos/Owner/re.po/git/trees/HEAD?recursive=1", handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Equal("https://raw.githubusercontent.com/Owner/re.po/HEAD/themes/a%20b.json", handler.Requests[1].RequestUri!.AbsoluteUri);
        Assert.All(handler.Requests, r => Assert.NotEmpty(r.Headers.UserAgent));
    }

    [Fact]
    public async Task A_file_that_is_too_large_is_not_downloaded_and_the_reason_is_kept()
    {
        var (fetcher, handler) = Fetcher(Tree(("themes/big.json", "blob", ThemePackRules.MaxBytes + 1), ("themes/ok.json", "blob", ThemePackRules.MaxBytes)));

        var fetched = await fetcher.FetchAsync("o", "r", default);

        Assert.Equal(["themes/ok.json"], fetched.Files.Select(f => f.Path));
        Assert.Contains("big.json: larger than 16 KB", Assert.Single(fetched.Problems));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_pack_offers_at_most_so_many_themes_and_the_rest_is_reported()
    {
        var entries = Enumerable.Range(0, ThemeSourceRules.MaxThemes + 3).Select(i => ($"themes/t{i:000}.json", "blob", 20L)).ToArray();
        var (fetcher, _) = Fetcher(Tree(entries));

        var fetched = await fetcher.FetchAsync("o", "r", default);

        Assert.Equal(ThemeSourceRules.MaxThemes, fetched.Files.Count);
        Assert.Equal(3, fetched.Problems.Count);
        Assert.Contains("t050.json: skipped", fetched.Problems[0]);
    }

    [Fact]
    public async Task A_file_that_vanished_meanwhile_is_a_problem_and_the_others_are_kept()
    {
        var (fetcher, _) = Fetcher(Tree(("themes/gone.json", "blob", 20), ("themes/here.json", "blob", 20)),
            raw: path => path.EndsWith("gone.json") ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json("{}"));

        var fetched = await fetcher.FetchAsync("o", "r", default);

        Assert.Equal(["themes/here.json"], fetched.Files.Select(f => f.Path));
        Assert.Equal(["gone.json: not found."], fetched.Problems);
    }

    [Fact]
    public async Task A_repository_without_a_themes_folder_offers_nothing()
    {
        var (fetcher, _) = Fetcher(Tree(("README.md", "blob", 3)));

        var fetched = await fetcher.FetchAsync("o", "r", default);

        Assert.Empty(fetched.Files);
        Assert.Empty(fetched.Problems);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    [InlineData(HttpStatusCode.Forbidden, "answered 403")] // no sign of a used-up quota: GitHub blocks the repository
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    [InlineData(HttpStatusCode.Conflict, "repository is empty")]
    [InlineData(HttpStatusCode.InternalServerError, "answered 500")]
    [InlineData(HttpStatusCode.BadGateway, "answered 502")]
    public async Task A_repository_that_cannot_be_read_says_why(HttpStatusCode status, string expected)
    {
        var fetcher = new GitHubThemeFetcher(new HttpClient(new StubHandler(_ => new HttpResponseMessage(status))));

        var ex = await Assert.ThrowsAsync<ThemeSourceException>(() => fetcher.FetchAsync("o", "r", default));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task A_forbidden_answer_with_no_requests_left_is_a_rate_limit()
    {
        var fetcher = new GitHubThemeFetcher(new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return response;
        })));

        var ex = await Assert.ThrowsAsync<ThemeSourceException>(() => fetcher.FetchAsync("o", "r", default));

        Assert.Contains("rate limit", ex.Message);
    }

    [Fact]
    public async Task A_forbidden_answer_that_asks_to_wait_is_a_rate_limit()
    {
        var fetcher = new GitHubThemeFetcher(new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return response;
        })));

        var ex = await Assert.ThrowsAsync<ThemeSourceException>(() => fetcher.FetchAsync("o", "r", default));

        Assert.Contains("rate limit", ex.Message);
    }

    [Fact]
    public async Task A_forbidden_answer_with_requests_left_is_no_rate_limit()
    {
        var fetcher = new GitHubThemeFetcher(new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "42");
            return response;
        })));

        var ex = await Assert.ThrowsAsync<ThemeSourceException>(() => fetcher.FetchAsync("o", "r", default));

        Assert.DoesNotContain("rate limit", ex.Message);
    }

    [Fact]
    public async Task A_list_that_github_cut_off_is_refused_instead_of_showing_a_wrong_picture()
    {
        var (fetcher, _) = Fetcher("""{"tree": [{"type": "blob", "path": "themes/a.json", "size": 20}], "truncated": true}""");

        var ex = await Assert.ThrowsAsync<ThemeSourceException>(() => fetcher.FetchAsync("o", "r", default));

        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public async Task A_list_that_is_no_json_is_refused()
    {
        var (fetcher, _) = Fetcher("<html>");

        var ex = await Assert.ThrowsAsync<ThemeSourceException>(() => fetcher.FetchAsync("o", "r", default));

        Assert.Contains("could not be read", ex.Message);
    }

    [Theory]
    [InlineData("""{"tree": [{"type": "blob"}]}""")]
    [InlineData("""{"tree": [{"type": 5, "path": "themes/a.json"}]}""")]
    [InlineData("""{"tree": [{"type": "blob", "path": "themes/a.json", "size": "big"}]}""")]
    public async Task A_list_of_an_unexpected_shape_is_refused(string tree)
    {
        var (fetcher, _) = Fetcher(tree);

        await Assert.ThrowsAsync<ThemeSourceException>(() => fetcher.FetchAsync("o", "r", default));
    }

    [Fact]
    public async Task A_list_without_entries_offers_nothing()
    {
        var (fetcher, _) = Fetcher("""{"sha": "abc"}""");

        Assert.Empty((await fetcher.FetchAsync("o", "r", default)).Files);
    }

    [Fact]
    public async Task A_file_that_turns_out_larger_than_announced_is_refused()
    {
        var (fetcher, _) = Fetcher(Tree(("themes/liar.json", "blob", 20)), raw: _ => Json(new string('x', ThemePackRules.MaxBytes + 1)));

        var ex = await Assert.ThrowsAsync<ThemeSourceException>(() => fetcher.FetchAsync("o", "r", default));

        Assert.Contains("larger than 16 KB", ex.Message);
    }

    [Fact]
    public async Task A_connection_that_fails_or_times_out_is_reported_a_cancelled_read_is_not()
    {
        var down = new GitHubThemeFetcher(new HttpClient(new FailingHandler(new HttpRequestException("no route"))));
        var ex = await Assert.ThrowsAsync<ThemeSourceException>(() => down.FetchAsync("o", "r", default));
        Assert.Contains("no route", ex.Message);

        var slow = new GitHubThemeFetcher(new HttpClient(new FailingHandler(new TaskCanceledException("timeout"))));
        ex = await Assert.ThrowsAsync<ThemeSourceException>(() => slow.FetchAsync("o", "r", default));
        Assert.Contains("did not answer in time", ex.Message);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.FetchAsync("o", "r", cancelled.Token));
    }

    private sealed class FailingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            cancellationToken.IsCancellationRequested ? Task.FromCanceled<HttpResponseMessage>(cancellationToken) : Task.FromException<HttpResponseMessage>(failure);
    }
}

/// <summary>How the files of a repository become themes: ids, names, and the rules a theme has to meet.</summary>
public class ThemeSourceBuildTests
{
    private static readonly ThemeSourceDto Source = new("abc123", "https://github.com/o/r", "o/r", 0, null, null, null);

    private static (List<ThemePackDto> Offered, List<string> Problems) Build(params ThemeSourceFile[] files) =>
        ThemeSourceSync.Build(Source, new ThemeSourceFetch(files, []));

    [Fact]
    public void The_ids_of_the_themes_start_with_the_key_of_the_pack_and_say_where_they_come_from()
    {
        var (offered, problems) = Build(FakeThemeFetcher.File("ocean.json", Packs.Valid()));

        var ocean = Assert.Single(offered);
        Assert.Empty(problems);
        Assert.Equal("abc123-ocean", ocean.Id);
        Assert.Equal("o/r", ocean.Source);
        Assert.False(ocean.BuiltIn);
        Assert.Null(ThemePackRules.Check(ocean));
    }

    [Fact]
    public void A_file_is_taken_for_what_it_says_whatever_it_is_called_and_the_source_of_a_file_is_not_believed()
    {
        var (offered, _) = Build(FakeThemeFetcher.File("whatever.json", Packs.Valid() with { BuiltIn = true, Source = "someone/else" }));

        Assert.Equal("abc123-ocean", offered.Single().Id);
        Assert.False(offered.Single().BuiltIn);
        Assert.Equal("o/r", offered.Single().Source);
    }

    [Fact]
    public void A_theme_may_have_the_id_of_a_theme_the_program_draws_itself_the_key_keeps_them_apart()
    {
        var (offered, problems) = Build(
            FakeThemeFetcher.File("dracula.json", Packs.Valid("dracula") with { Name = "Dracula 2", NameDe = null }),
            FakeThemeFetcher.File("nord.json", Packs.Valid(BuiltInThemePacks.HighContrastDark) with { Name = "HC", NameDe = null }));

        Assert.Empty(problems);
        Assert.Equal(["abc123-dracula", "abc123-" + BuiltInThemePacks.HighContrastDark], offered.Select(p => p.Id));
    }

    [Fact]
    public void The_names_of_the_themes_of_a_pack_must_be_unique_the_first_file_wins()
    {
        var (offered, problems) = Build(
            FakeThemeFetcher.File("b.json", Packs.Valid("second") with { Name = "OCEAN" }),
            FakeThemeFetcher.File("a.json", Packs.Valid("first")),
            FakeThemeFetcher.File("c.json", Packs.Valid("third") with { Name = "Other", NameDe = null }));

        Assert.Equal(["abc123-first", "abc123-third"], offered.Select(p => p.Id));
        var problem = Assert.Single(problems);
        Assert.StartsWith("b.json: ", problem);
        Assert.Contains("unique", problem);
        Assert.Contains("a.json", problem);
    }

    [Theory]
    [InlineData("Neon", null, "neon", null)]            // the same English name, any case
    [InlineData("Neon", "Licht", "Chrome", "licht")]    // the same German name, any case
    [InlineData("Neon", "Chrome", "Chrome", null)]      // no German name: the name stands for it
    [InlineData("Neon", null, "  Neon  ", null)]        // blanks around do not make a difference
    public void The_names_of_a_theme_must_differ_in_every_language(string name, string? nameDe, string otherName, string? otherNameDe)
    {
        var (offered, problems) = Build(
            FakeThemeFetcher.File("a.json", Packs.Valid("a") with { Name = name, NameDe = nameDe }),
            FakeThemeFetcher.File("b.json", Packs.Valid("b") with { Name = otherName, NameDe = otherNameDe }));

        Assert.Equal(["abc123-a"], offered.Select(p => p.Id));
        Assert.Contains("unique", Assert.Single(problems));
    }

    [Fact]
    public void Themes_that_differ_in_both_languages_are_both_offered()
    {
        var (offered, problems) = Build(
            FakeThemeFetcher.File("a.json", Packs.Valid("a") with { Name = "Neon", NameDe = "Neonlicht" }),
            FakeThemeFetcher.File("b.json", Packs.Valid("b") with { Name = "Neon Two", NameDe = "Neonlicht Zwei" }));

        Assert.Empty(problems);
        Assert.Equal(2, offered.Count);
    }

    [Fact]
    public void The_example_theme_pack_of_the_issue_is_accepted_as_it_is_in_the_repository()
    {
        const string cyberpunk = """
            {
              "id": "cyberpunk-2077", "name": "Cyberpunk 2077", "nameDe": "Cyberpunk 2077", "scheme": "dark", "highContrast": false, "accent": "#00f0ff",
              "colors": {
                "ground": "#0d0221", "sheet": "#160836", "sheetAlt": "#25104f", "sidebar": "#080117",
                "text": "#e8fbff", "muted": "#a99bff", "rule": "#3b1a7a", "ruleStrong": "#ff2bd6",
                "amber": "#fcee09", "amberWash": "#3d3a06", "red": "#ff2a6d", "redWash": "#46082a"
              }
            }
            """;
        const string matrix = """
            {
              "id": "matrix", "name": "Matrix", "nameDe": "Matrix", "scheme": "dark", "highContrast": false, "accent": "#00ff41",
              "colors": {
                "ground": "#000a03", "sheet": "#031a0a", "sheetAlt": "#062a12", "sidebar": "#000502",
                "text": "#c6ffd0", "muted": "#5fbf75", "rule": "#0b4a1d", "ruleStrong": "#00b82e",
                "amber": "#d6ff3d", "amberWash": "#2a3a05", "red": "#ff5f5f", "redWash": "#3a0b0b"
              }
            }
            """;

        var (offered, problems) = Build(new ThemeSourceFile("themes/cyberpunk-2077.json", cyberpunk), new ThemeSourceFile("themes/matrix.json", matrix));

        Assert.Empty(problems);
        Assert.Equal(["abc123-cyberpunk-2077", "abc123-matrix"], offered.Select(p => p.Id));
    }

    [Fact]
    public void Two_files_with_one_id_keep_the_first_one_even_when_the_names_differ()
    {
        var (offered, problems) = Build(
            FakeThemeFetcher.File("a.json", Packs.Valid("same")),
            FakeThemeFetcher.File("b.json", Packs.Valid("same") with { Name = "Another" }));

        Assert.Equal("Ocean", offered.Single().Name);
        Assert.Contains("the id 'same' is used by a.json", Assert.Single(problems));
    }

    [Fact]
    public void A_file_that_is_no_valid_theme_is_skipped_with_its_name_and_the_reason_the_rest_is_kept()
    {
        var (offered, problems) = Build(
            FakeThemeFetcher.File("1-no-json.json", Packs.Valid()) with { Text = "{ nope" },
            FakeThemeFetcher.File("2-null.json", Packs.Valid()) with { Text = "null" },
            FakeThemeFetcher.File("3-contrast.json", Packs.Valid("faint").With("text", "#444444")),
            FakeThemeFetcher.File("4-upper.json", Packs.Valid("Upper")),
            FakeThemeFetcher.File("5-no-colors.json", Packs.Valid()) with { Text = """{"id":"x","name":"X","scheme":"dark","highContrast":false}""" },
            FakeThemeFetcher.File("6-big.json", Packs.Valid()) with { Text = new string(' ', ThemePackRules.MaxBytes + 1) },
            FakeThemeFetcher.File("7-good.json", Packs.Valid("good") with { Name = "Good" }));

        Assert.Equal(["abc123-good"], offered.Select(p => p.Id));
        Assert.Equal(6, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("1-no-json.json: ") && p.Contains("JSON"));
        Assert.Contains(problems, p => p.StartsWith("3-contrast.json: ") && p.Contains("contrast"));
        Assert.Contains(problems, p => p.StartsWith("5-no-colors.json: ") && p.Contains("colors"));
        Assert.Contains(problems, p => p.StartsWith("6-big.json: ") && p.Contains("16 KB"));
    }

    [Fact]
    public void An_id_that_would_not_fit_with_the_key_in_front_is_refused_with_the_limit()
    {
        var longest = new string('a', ThemeRules.MaxLength - Source.Id.Length - 1);

        var (offered, _) = Build(FakeThemeFetcher.File("fits.json", Packs.Valid(longest)));
        Assert.Equal(ThemeRules.MaxLength, offered.Single().Id.Length);

        var (none, problems) = Build(FakeThemeFetcher.File("long.json", Packs.Valid(longest + "a")));
        Assert.Empty(none);
        Assert.Contains("longer than 25 characters", Assert.Single(problems));
    }

    [Fact]
    public void The_problems_of_the_fetcher_come_first_and_nothing_offered_is_no_error_here()
    {
        var (offered, problems) = ThemeSourceSync.Build(Source, new ThemeSourceFetch([], ["x.json: larger than 16 KB."]));

        Assert.Empty(offered);
        Assert.Equal(["x.json: larger than 16 KB."], problems);
    }
}

/// <summary>When the packs are read again: the store on its own, with a clock the test moves.</summary>
public sealed class ThemeSourceStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("pp-sources-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly ThemePackStore _packs;
    private readonly ThemeSourceStore _store;

    public ThemeSourceStoreTests()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ThemePackStore.DirectorySetting] = _folder }).Build();
        _packs = new ThemePackStore(new SettingsFile(Path.Combine(_folder, "settings.json")), configuration, NullLogger<ThemePackStore>.Instance);
        _store = new ThemeSourceStore(_packs, _time, NullLogger<ThemeSourceStore>.Instance);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string IndexFile => Path.Combine(_packs.Directory, ThemePackStore.SourcesFolder, ThemePackStore.SourcesIndex);

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{\"a\":1}")]
    public void A_corrupt_index_is_set_aside_and_the_store_starts_empty(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IndexFile)!);
        File.WriteAllText(IndexFile, content);

        Assert.Empty(_store.List());
        Assert.Empty(_store.Due());
        Assert.Null(_store.Find("abc123"));
        Assert.False(_store.Delete("abc123"));
        Assert.Equal(content, File.ReadAllText(IndexFile + ".bad"));

        var added = _store.Add("https://github.com/o/r"); // repaired: adding works again
        Assert.Equal([added.Id], _store.List().Select(s => s.Id));
    }

    [Fact]
    public void A_second_corrupt_index_replaces_the_earlier_bad_copy_and_record_of_a_missing_pack_does_not_throw()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IndexFile)!);
        File.WriteAllText(IndexFile + ".bad", "old");
        File.WriteAllText(IndexFile, "[[");

        _store.Record("abc123", [], true, null);

        Assert.Equal("[[", File.ReadAllText(IndexFile + ".bad"));
    }

    [Fact]
    public void A_new_pack_is_due_at_once_and_after_a_success_only_when_the_interval_is_over()
    {
        var added = _store.Add("https://github.com/o/r");
        Assert.Equal([added.Id], _store.Due().Select(s => s.Id));

        _store.Record(added.Id, [], true, null);
        Assert.Empty(_store.Due());

        _time.Advance(ThemeSourceRules.RefreshInterval - TimeSpan.FromSeconds(1));
        Assert.Empty(_store.Due());

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(_store.Due());
    }

    [Fact]
    public void After_a_failure_it_is_tried_again_sooner()
    {
        var added = _store.Add("https://github.com/o/r");
        _store.Record(added.Id, null, false, "down");

        _time.Advance(ThemeSourceRules.RetryInterval - TimeSpan.FromSeconds(1));
        Assert.Empty(_store.Due());

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(_store.Due());
        Assert.True(ThemeSourceRules.RetryInterval < ThemeSourceRules.RefreshInterval);
    }

    [Fact]
    public void A_failed_read_keeps_the_themes_and_a_read_of_nothing_new_leaves_the_catalog_alone()
    {
        var added = _store.Add("https://github.com/o/r");
        var theme = Packs.Valid(added.Id + "-ocean") with { Source = "o/r" };
        _store.Record(added.Id, [theme], true, null);
        var version = _packs.Catalog().Version;
        Assert.Contains(_packs.Catalog().Packs, p => p.Id == theme.Id);

        _time.Advance(TimeSpan.FromHours(7));
        _store.Record(added.Id, [theme], true, null); // the same again
        Assert.Equal(version, _packs.Catalog().Version);
        Assert.Equal(_time.GetUtcNow(), _store.Find(added.Id)!.LastSyncUtc); // but it is remembered that it was read

        _store.Record(added.Id, null, false, "down");
        Assert.Equal(version, _packs.Catalog().Version);
        Assert.Contains(_packs.Catalog().Packs, p => p.Id == theme.Id);
        var failed = _store.Find(added.Id)!;
        Assert.False(failed.LastSucceeded);
        Assert.Equal("down", failed.LastMessage);
        Assert.Equal(1, failed.ThemeCount);

        _store.Record(added.Id, [theme, Packs.Valid(added.Id + "-other") with { Name = "Other", Source = "o/r" }], true, "1 skipped");
        Assert.NotEqual(version, _packs.Catalog().Version);
        Assert.Equal(2, _store.Find(added.Id)!.ThemeCount);
    }

    [Fact]
    public void Recording_for_a_pack_that_was_removed_meanwhile_changes_nothing()
    {
        var added = _store.Add("https://github.com/o/r");
        _store.Delete(added.Id);

        _store.Record(added.Id, [Packs.Valid("ghost")], true, null);

        Assert.Empty(_store.List());
        Assert.DoesNotContain(_packs.Catalog().Packs, p => p.Id == "ghost");
        Assert.Equal(["index.json"], Directory.GetFiles(Path.Combine(_folder, ThemePackStore.SourcesFolder)).Select(Path.GetFileName)); // no file for the ghost
    }

    [Theory]
    [InlineData("https://github.com/Owner/Repo", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo", "https://github.com/OWNER/REPO.git")]
    public void A_repository_is_added_once_whatever_the_case(string first, string second)
    {
        _store.Add(first);

        var ex = Assert.Throws<InvalidInputException>(() => _store.Add(second));

        Assert.Contains("added already", ex.Message);
        Assert.Single(_store.List());
    }

    [Fact]
    public void The_key_is_stable_short_and_the_same_for_every_spelling_of_the_name()
    {
        var key = ThemeSourceStore.KeyOf("Owner", "Repo");

        Assert.Equal(6, key.Length);
        Assert.Matches("^[0-9a-f]{6}$", key);
        Assert.Equal(key, ThemeSourceStore.KeyOf("owner", "repo"));
        Assert.NotEqual(key, ThemeSourceStore.KeyOf("owner", "repo2"));
        Assert.Equal(key, _store.Add("https://github.com/Owner/Repo").Id);
    }

    [Fact]
    public void At_most_so_many_packs_can_be_added()
    {
        for (var i = 0; i < ThemeSourceRules.MaxSources; i++) _store.Add($"https://github.com/o/r{i}");

        var ex = Assert.Throws<InvalidInputException>(() => _store.Add("https://github.com/o/one-too-many"));

        Assert.Contains("At most 10", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.org/o/r")]
    public void An_address_that_is_none_is_refused(string? url)
    {
        Assert.Throws<InvalidInputException>(() => _store.Add(url));
        Assert.Empty(_store.List());
    }

    [Fact]
    public void A_pack_that_was_hand_corrupted_is_skipped_and_the_catalog_still_works()
    {
        var added = _store.Add("https://github.com/o/r");
        _store.Record(added.Id, [Packs.Valid(added.Id + "-ocean")], true, null);
        File.WriteAllText(Path.Combine(_folder, ThemePackStore.SourcesFolder, "dead00.json"), "{ nope");
        File.WriteAllText(Path.Combine(_folder, ThemePackStore.SourcesFolder, "beef00.json"), Packs.Json(Packs.Valid("not-a-list"))); // an object, not a list

        var catalog = _packs.Catalog();

        Assert.Contains(catalog.Packs, p => p.Id == added.Id + "-ocean");
        Assert.DoesNotContain(catalog.Packs, p => p.Id == "not-a-list");
    }

    [Fact]
    public void An_invalid_theme_in_a_pack_file_is_not_offered_and_an_id_that_is_taken_is_not_given_twice()
    {
        var added = _store.Add("https://github.com/o/r");
        var good = Packs.Valid(added.Id + "-ocean");
        File.WriteAllText(Path.Combine(_folder, added.Id + "-ocean.json"), Packs.Json(Packs.Valid(added.Id + "-ocean") with { Name = "Uploaded" }));
        File.WriteAllText(Path.Combine(_folder, ThemePackStore.SourcesFolder, added.Id + ".json"),
            JsonSerializer.Serialize(new[] { good, Packs.Valid("UPPER"), Packs.Valid(added.Id + "-faint").With("text", "#444444") }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var packs = _packs.Catalog().Packs;

        Assert.Equal("Uploaded", packs.Single(p => p.Id == added.Id + "-ocean").Name); // the uploaded one came first
        Assert.DoesNotContain(packs, p => p.Id is "UPPER");
        Assert.DoesNotContain(packs, p => p.Id.EndsWith("-faint"));
    }

    [Fact]
    public void Removing_a_pack_takes_its_themes_with_it()
    {
        var added = _store.Add("https://github.com/o/r");
        _store.Record(added.Id, [Packs.Valid(added.Id + "-ocean")], true, null);

        Assert.True(_store.Delete(added.Id));

        Assert.False(_store.Delete(added.Id));
        Assert.Empty(_store.List());
        Assert.DoesNotContain(_packs.Catalog().Packs, p => p.Id.StartsWith(added.Id));
        Assert.False(File.Exists(Path.Combine(_folder, ThemePackStore.SourcesFolder, added.Id + ".json")));
    }
}

/// <summary>Reading, adding and removing GitHub theme packs against the real API, with a repository the test controls.</summary>
public sealed class ThemeSourceApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string UserPassword = "a good password";

    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("pp-themes-").FullName;
    private readonly FakeThemeFetcher _fetcher = new() { Repositories = { [FakeThemeFetcher.Cyberpunk] = FakeThemeFetcher.CyberpunkPack() } };
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private WebApplicationFactory<Program> CreateHost() =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            builder.UseSetting("Themes:Directory", _folder);
            Configure(builder, _accounts);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IThemeSourceFetcher>(_fetcher);
                services.AddSingleton<TimeProvider>(_time);
            });
        });

    private static string Url(string repository = FakeThemeFetcher.Cyberpunk) => "https://github.com/" + repository;

    private static Task<HttpResponseMessage> AddAsync(HttpClient client, string url) => client.PostAsJsonAsync("/api/themes/sources", new ThemeSourceInputDto(url));

    private static async Task<ThemeCatalogDto> CatalogAsync(HttpClient client) => (await client.GetFromJsonAsync<ThemeCatalogDto>("/api/themes", Web))!;

    private static async Task<List<ThemeSourceDto>> SourcesAsync(HttpClient client) => (await client.GetFromJsonAsync<List<ThemeSourceDto>>("/api/themes/sources", Web))!;

    private static async Task<string> ProblemAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    private static string KeyOfCyberpunk => ThemeSourceStore.KeyOf("ArizonaGreenTea05", "kyuplan.themes.cyberpunk");

    [Fact]
    public async Task An_administrator_adds_a_theme_pack_everybody_sees_its_themes_and_they_survive_a_restart()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var before = await host.CreateClient().GetFromJsonAsync<BrandingDto>("/api/branding");

        var response = await AddAsync(admin, Url());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var source = (await response.Content.ReadFromJsonAsync<ThemeSourceDto>(Web))!;
        Assert.Equal(KeyOfCyberpunk, source.Id);
        Assert.Equal(FakeThemeFetcher.Cyberpunk, source.Name);
        Assert.Equal(Url(), source.Url);
        Assert.Equal(2, source.ThemeCount);
        Assert.True(source.LastSucceeded);
        Assert.Null(source.LastMessage);
        Assert.Equal(_time.GetUtcNow(), source.LastSyncUtc);

        var catalog = await CatalogAsync(host.CreateClient()); // no login needed
        var cyberpunk = catalog.Packs.Single(p => p.Id == $"{KeyOfCyberpunk}-cyberpunk-2077");
        Assert.Equal("Cyberpunk 2077", cyberpunk.Name);
        Assert.Equal(FakeThemeFetcher.Cyberpunk, cyberpunk.Source);
        Assert.False(cyberpunk.BuiltIn);
        Assert.Contains(catalog.Packs, p => p.Id == $"{KeyOfCyberpunk}-matrix");

        var after = await host.CreateClient().GetFromJsonAsync<BrandingDto>("/api/branding");
        Assert.NotEqual(before!.ThemesVersion, after!.ThemesVersion);
        Assert.Equal(catalog.Version, after.ThemesVersion);
        Assert.Equal([source], await SourcesAsync(admin));

        using var restarted = CreateHost();
        Assert.Contains((await CatalogAsync(restarted.CreateClient())).Packs, p => p.Id == $"{KeyOfCyberpunk}-matrix");
        Assert.Single(await SourcesAsync(restarted.CreateSignedInClient()));
    }

    [Fact]
    public async Task A_theme_of_a_pack_can_be_chosen_and_made_the_default_but_not_any_more_once_the_pack_is_removed()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await AddAsync(admin, Url());
        var id = $"{KeyOfCyberpunk}-matrix";

        var chosen = await admin.PutAsJsonAsync("/api/account/theme", new ThemePreferenceDto(ThemeClients.Web, id));
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/branding", new BrandingInputDto("PlanIt", "Club", "#1a5fb4", id))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/themes/sources/{KeyOfCyberpunk}")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/account/theme", new ThemePreferenceDto(ThemeClients.Web, id))).StatusCode); // not installed any more
    }

    [Fact]
    public async Task Two_theme_packs_can_offer_themes_of_the_same_id_and_name()
    {
        _fetcher.Repositories["someone/other-pack"] = FakeThemeFetcher.CyberpunkPack();
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        Assert.Equal(HttpStatusCode.OK, (await AddAsync(admin, Url())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(admin, Url("someone/other-pack"))).StatusCode);

        var themes = (await CatalogAsync(admin)).Packs.Where(p => p.Source is not null).ToList();
        Assert.Equal(4, themes.Count);
        Assert.Equal(4, themes.Select(p => p.Id).Distinct().Count());
        Assert.Equal(2, themes.Count(p => p.Name == "Matrix"));
        Assert.Equal(2, (await SourcesAsync(admin)).Count);
    }

    [Fact]
    public async Task The_names_of_the_themes_of_one_pack_must_be_unique_so_the_second_one_is_skipped_and_the_reason_is_shown()
    {
        _fetcher.Repositories[FakeThemeFetcher.Cyberpunk] = FakeThemeFetcher.Fetch(
            FakeThemeFetcher.File("a.json", Packs.Valid("a") with { Name = "Neon" }),
            FakeThemeFetcher.File("b.json", Packs.Valid("b") with { Name = "neon" }),
            FakeThemeFetcher.File("c.json", Packs.Valid("c") with { Name = "Chrome", NameDe = null }));
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var response = await AddAsync(admin, Url());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var source = (await response.Content.ReadFromJsonAsync<ThemeSourceDto>(Web))!;
        Assert.Equal(2, source.ThemeCount);
        Assert.True(source.LastSucceeded);
        Assert.Contains("b.json", source.LastMessage);
        Assert.Contains("unique", source.LastMessage);
        Assert.Equal(["Chrome", "Neon"], (await CatalogAsync(admin)).Packs.Where(p => p.Source is not null).Select(p => p.Name).Order());
    }

    [Fact]
    public async Task A_pack_without_a_usable_theme_is_refused_with_the_reason_and_nothing_is_kept()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        var catalog = await CatalogAsync(admin);

        _fetcher.Repositories["o/empty"] = FakeThemeFetcher.Fetch();
        var empty = await AddAsync(admin, Url("o/empty"));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("No themes found", await ProblemAsync(empty));

        _fetcher.Repositories["o/broken"] = FakeThemeFetcher.Fetch(FakeThemeFetcher.File("x.json", Packs.Valid().With("text", "#444444")));
        var broken = await AddAsync(admin, Url("o/broken"));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Contains("x.json", await ProblemAsync(broken));
        Assert.Contains("contrast", await ProblemAsync(broken));

        _fetcher.Repositories["o/problems-only"] = new ThemeSourceFetch([], ["big.json: larger than 16 KB."]);
        Assert.Contains("big.json", await ProblemAsync(await AddAsync(admin, Url("o/problems-only"))));

        Assert.Empty(await SourcesAsync(admin));
        Assert.Equal(catalog.Version, (await CatalogAsync(admin)).Version);
    }

    [Fact]
    public async Task A_repository_that_cannot_be_read_is_refused_with_what_github_said()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var missing = await AddAsync(admin, Url("nobody/nothing"));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("has to be public", await ProblemAsync(missing));

        _fetcher.Failure = new ThemeSourceException("GitHub did not answer in time.");
        Assert.Contains("did not answer in time", await ProblemAsync(await AddAsync(admin, Url())));

        _fetcher.Failure = new InvalidOperationException("boom");
        var unexpected = await AddAsync(admin, Url());
        Assert.Equal(HttpStatusCode.BadRequest, unexpected.StatusCode);
        Assert.Contains("boom", await ProblemAsync(unexpected));

        Assert.Empty(await SourcesAsync(admin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("https://gitlab.com/o/r")]
    [InlineData("https://github.com/o")]
    [InlineData("http://github.com/o/r")]
    public async Task An_address_that_is_no_github_repository_is_refused_before_anything_is_read(string url)
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();

        var response = await AddAsync(admin, url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("GitHub", await ProblemAsync(response));
        Assert.Equal(0, _fetcher.Calls);
    }

    [Fact]
    public async Task A_request_without_a_body_is_refused_too()
    {
        using var host = CreateHost();

        var response = await host.CreateSignedInClient().PostAsync("/api/themes/sources", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_pack_cannot_be_added_twice_and_there_is_a_limit()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await AddAsync(admin, Url());

        var twice = await AddAsync(admin, Url().ToUpperInvariant().Replace("HTTPS://GITHUB.COM", "https://github.com"));
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
        Assert.Contains("added already", await ProblemAsync(twice));

        for (var i = 1; i < ThemeSourceRules.MaxSources; i++)
        {
            _fetcher.Repositories[$"o/r{i}"] = FakeThemeFetcher.CyberpunkPack();
            Assert.Equal(HttpStatusCode.OK, (await AddAsync(admin, Url($"o/r{i}"))).StatusCode);
        }

        _fetcher.Repositories["o/last"] = FakeThemeFetcher.CyberpunkPack();
        var tooMany = await AddAsync(admin, Url("o/last"));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("At most 10", await ProblemAsync(tooMany));
    }

    [Fact]
    public async Task Only_those_with_themes_edit_manage_theme_packs()
    {
        using var host = CreateHost();
        Assert.Equal(HttpStatusCode.Created, (await host.CreateSignedInClient().PostAsJsonAsync("/api/users", Input("anna", UserPassword, false))).StatusCode);
        var anna = host.CreateSignedInClient("anna", UserPassword);
        await AddAsync(host.CreateSignedInClient(), Url());
        var anonymous = host.CreateClient();

        foreach (var client in new[] { anna, anonymous })
        {
            var expected = ReferenceEquals(client, anna) ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
            Assert.Equal(expected, (await client.GetAsync("/api/themes/sources")).StatusCode);
            Assert.Equal(expected, (await AddAsync(client, Url("o/x"))).StatusCode);
            Assert.Equal(expected, (await client.PostAsync($"/api/themes/sources/{KeyOfCyberpunk}/refresh", null)).StatusCode);
            Assert.Equal(expected, (await client.DeleteAsync($"/api/themes/sources/{KeyOfCyberpunk}")).StatusCode);
        }

        Assert.Single(await SourcesAsync(host.CreateSignedInClient()));
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync("/api/themes")).StatusCode); // the themes themselves are for everybody
    }

    [Fact]
    public async Task A_theme_of_a_pack_is_not_deleted_on_its_own_and_the_uploaded_limit_does_not_count_it()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await AddAsync(admin, Url());

        var response = await admin.DeleteAsync($"/api/themes/{KeyOfCyberpunk}-matrix");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("remove the theme pack", await ProblemAsync(response));
        Assert.Contains((await CatalogAsync(admin)).Packs, p => p.Id == $"{KeyOfCyberpunk}-matrix");
    }

    [Fact]
    public async Task Removing_a_pack_removes_its_themes_and_tells_the_clients()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await AddAsync(admin, Url());
        var versionWith = (await CatalogAsync(admin)).Version;

        var removed = await admin.DeleteAsync($"/api/themes/sources/{KeyOfCyberpunk}");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var catalog = await CatalogAsync(admin);
        Assert.DoesNotContain(catalog.Packs, p => p.Source is not null);
        Assert.NotEqual(versionWith, catalog.Version);
        Assert.Equal(catalog.Version, (await host.CreateClient().GetFromJsonAsync<BrandingDto>("/api/branding"))!.ThemesVersion);
        Assert.Empty(await SourcesAsync(admin));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/themes/sources/{KeyOfCyberpunk}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/themes/sources/..%2Fsettings")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/themes/sources/nothing/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Refreshing_picks_up_what_changed_in_the_repository_and_a_repository_that_did_not_change_leaves_the_catalog_alone()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await AddAsync(admin, Url());
        var version = (await CatalogAsync(admin)).Version;
        _time.Advance(TimeSpan.FromMinutes(5));

        var same = await admin.PostAsync($"/api/themes/sources/{KeyOfCyberpunk}/refresh", null);

        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        var read = (await same.Content.ReadFromJsonAsync<ThemeSourceDto>(Web))!;
        Assert.Equal(_time.GetUtcNow(), read.LastSyncUtc);
        Assert.Equal(version, (await CatalogAsync(admin)).Version);

        _fetcher.Repositories[FakeThemeFetcher.Cyberpunk] = FakeThemeFetcher.Fetch(
            FakeThemeFetcher.File("cyberpunk-2077.json", Packs.Valid("cyberpunk-2077") with { Name = "Cyberpunk 2077 (renamed)", NameDe = null }),
            FakeThemeFetcher.File("matrix.json", Packs.Valid("matrix") with { Name = "Matrix", NameDe = null }),
            FakeThemeFetcher.File("synthwave.json", Packs.Valid("synthwave") with { Name = "Synthwave", NameDe = null }));
        var changed = (await (await admin.PostAsync($"/api/themes/sources/{KeyOfCyberpunk}/refresh", null)).Content.ReadFromJsonAsync<ThemeSourceDto>(Web))!;

        Assert.Equal(3, changed.ThemeCount);
        var catalog = await CatalogAsync(admin);
        Assert.NotEqual(version, catalog.Version);
        Assert.Contains(catalog.Packs, p => p.Name == "Cyberpunk 2077 (renamed)");
        Assert.Contains(catalog.Packs, p => p.Id == $"{KeyOfCyberpunk}-synthwave");

        // a theme that is gone from the repository is gone here too
        _fetcher.Repositories[FakeThemeFetcher.Cyberpunk] = FakeThemeFetcher.Fetch(FakeThemeFetcher.File("matrix.json", Packs.Valid("matrix") with { Name = "Matrix" }));
        await admin.PostAsync($"/api/themes/sources/{KeyOfCyberpunk}/refresh", null);
        Assert.Equal([$"{KeyOfCyberpunk}-matrix"], (await CatalogAsync(admin)).Packs.Where(p => p.Source is not null).Select(p => p.Id));
    }

    [Fact]
    public async Task A_refresh_that_fails_keeps_the_themes_and_says_why_in_the_answer()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await AddAsync(admin, Url());
        var version = (await CatalogAsync(admin)).Version;

        _fetcher.Failure = new ThemeSourceException("GitHub answered 500 (Internal Server Error).");
        var failed = await admin.PostAsync($"/api/themes/sources/{KeyOfCyberpunk}/refresh", null);

        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        var read = (await failed.Content.ReadFromJsonAsync<ThemeSourceDto>(Web))!;
        Assert.False(read.LastSucceeded);
        Assert.Contains("500", read.LastMessage);
        Assert.Equal(2, read.ThemeCount);
        Assert.Equal(version, (await CatalogAsync(admin)).Version);
        Assert.Equal(2, (await CatalogAsync(admin)).Packs.Count(p => p.Source is not null));

        _fetcher.Failure = null;
        _fetcher.Repositories[FakeThemeFetcher.Cyberpunk] = FakeThemeFetcher.Fetch(); // the folder is gone: also a failure, the themes stay
        var empty = (await (await admin.PostAsync($"/api/themes/sources/{KeyOfCyberpunk}/refresh", null)).Content.ReadFromJsonAsync<ThemeSourceDto>(Web))!;
        Assert.False(empty.LastSucceeded);
        Assert.Contains("No themes found", empty.LastMessage);
        Assert.Equal(2, (await CatalogAsync(admin)).Packs.Count(p => p.Source is not null));

        _fetcher.Repositories[FakeThemeFetcher.Cyberpunk] = FakeThemeFetcher.CyberpunkPack();
        var healed = (await (await admin.PostAsync($"/api/themes/sources/{KeyOfCyberpunk}/refresh", null)).Content.ReadFromJsonAsync<ThemeSourceDto>(Web))!;
        Assert.True(healed.LastSucceeded);
        Assert.Null(healed.LastMessage);
    }

    [Fact]
    public async Task The_worker_reads_a_pack_again_when_its_time_is_over_and_sooner_after_a_failure()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await AddAsync(admin, Url());
        Assert.Equal(1, _fetcher.Calls);

        // not yet
        _time.Advance(ThemeSourceRules.RefreshInterval - TimeSpan.FromMinutes(5));
        await Task.Delay(300);
        Assert.Equal(1, _fetcher.Calls);

        // now: it fails
        _fetcher.Failure = new ThemeSourceException("down");
        _time.Advance(TimeSpan.FromMinutes(6));
        await WaitForAsync(() => _fetcher.Calls >= 2);
        await WaitForAsync(async () => (await SourcesAsync(admin)).Single().LastSucceeded == false);

        // the retry comes after half an hour, not after six
        _fetcher.Failure = null;
        var calls = _fetcher.Calls;
        _time.Advance(ThemeSourceRules.RetryInterval + TimeSpan.FromMinutes(1));
        await WaitForAsync(() => _fetcher.Calls > calls);
        await WaitForAsync(async () => (await SourcesAsync(admin)).Single().LastSucceeded == true);
    }

    private static async Task WaitForAsync(Func<bool> condition) => await WaitForAsync(() => Task.FromResult(condition()));

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        Assert.Fail("The worker did not do what was expected in time.");
    }

    [Fact]
    public async Task The_uploaded_themes_still_work_beside_the_packs_and_come_first()
    {
        using var host = CreateHost();
        var admin = host.CreateSignedInClient();
        await AddAsync(admin, Url());

        var uploaded = await admin.PutAsync("/api/themes", new StringContent(Packs.Json(Packs.Valid()), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);

        var packs = (await CatalogAsync(admin)).Packs;
        var ids = packs.Select(p => p.Id).ToList();
        Assert.True(ids.IndexOf("ocean") < ids.IndexOf($"{KeyOfCyberpunk}-matrix"));
        Assert.Null(packs.Single(p => p.Id == "ocean").Source);

        // an uploaded file cannot pose as a theme of a pack
        var posing = await admin.PutAsync("/api/themes", new StringContent(Packs.Json(Packs.Valid("posing") with { Source = "someone/else" }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, posing.StatusCode);
        Assert.Null((await CatalogAsync(admin)).Packs.Single(p => p.Id == "posing").Source);
    }
}

/// <summary>What the page does with the GitHub theme packs.</summary>
public sealed class ThemeSourceViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly LoginSession _session = new();
    private readonly ShellViewModel _shell;

    public ThemeSourceViewModelTests()
    {
        _language.Select("en");
        _api.Account = Dto(1, "admin", "admin", [], true);
        _api.NextSourceThemes = [Packs.Valid("matrix") with { Name = "Matrix" }, Packs.Valid("neon") with { Name = "Neon" }];
        _shell = new ShellViewModel(_api, _language, _session);
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private async Task<BrandingAdminViewModel> PageAsync()
    {
        var vm = new BrandingAdminViewModel(_shell, _api, Options.Create(new PlannerClientOptions { BaseAddress = "https://planner.example.org/" }));
        await vm.ActivateAsync();
        return vm;
    }

    private static async Task AddAsync(BrandingAdminViewModel vm, string url = "https://github.com/owner/repo")
    {
        vm.NewThemeSourceUrl = url;
        await vm.AddThemeSourceCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task The_page_lists_the_packs_with_their_state()
    {
        _api.ThemeSources.Add(new ThemeSourceDto("a1", "https://github.com/o/ok", "o/ok", 2, new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero), true, null));
        _api.ThemeSources.Add(new ThemeSourceDto("b2", "https://github.com/o/down", "o/down", 1, new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero), false, "GitHub did not answer in time."));
        _api.ThemeSources.Add(new ThemeSourceDto("c3", "https://github.com/o/skip", "o/skip", 1, new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero), true, "1 skipped: x.json: bad"));
        _api.ThemeSources.Add(new ThemeSourceDto("d4", "https://github.com/o/new", "o/new", 0, null, null, null));

        var vm = await PageAsync();

        Assert.Equal(["o/ok", "o/down", "o/skip", "o/new"], vm.ThemeSources.Select(r => r.Name));
        Assert.StartsWith("Themes: 2 · updated ", vm.ThemeSources[0].Details);
        Assert.Null(vm.ThemeSources[0].Problem);
        Assert.Equal("Update failed: GitHub did not answer in time.", vm.ThemeSources[1].Problem);
        Assert.Equal("Note: 1 skipped: x.json: bad", vm.ThemeSources[2].Problem);
        Assert.Equal("Themes: 0 · not updated yet", vm.ThemeSources[3].Details);
        Assert.Equal("https://github.com/o/ok", vm.ThemeSources[0].Url);
        Assert.Contains("themes", vm.ThemeSourcesLede);
        Assert.Contains("6 hours", vm.ThemeSourcesLede);
    }

    [Fact]
    public async Task An_administrator_adds_a_pack_by_its_address_and_its_themes_are_offered_at_once()
    {
        var vm = await PageAsync();
        var versionBefore = _shell.Brand.ThemesVersion;

        await AddAsync(vm);

        Assert.Equal(["add-source:https://github.com/owner/repo"], _api.ThemeCalls);
        Assert.Equal("owner/repo", Assert.Single(vm.ThemeSources).Name);
        Assert.Equal("", vm.NewThemeSourceUrl);
        Assert.Equal("Theme pack \"owner/repo\" added (2 themes).", vm.NoticeText);
        Assert.False(vm.HasError);
        Assert.NotEqual(versionBefore, _shell.Brand.ThemesVersion);

        var matrix = vm.ThemePacks.Single(r => r.Name == "Matrix");
        Assert.Equal("owner/repo", matrix.Source);
        Assert.Contains("owner/repo", matrix.Details);
        Assert.False(matrix.IsUploaded);
        Assert.False(vm.CanDeleteTheme(matrix)); // it goes with its pack
        Assert.Contains(vm.WebThemes, c => c.Name == "Matrix");
    }

    [Fact]
    public void A_name_that_two_packs_share_is_listed_with_the_repository_and_a_name_of_its_own_is_not()
    {
        var packs = new List<ThemePackDto>(BuiltInThemePacks.All)
        {
            Packs.Valid("aaa111-matrix") with { Name = "Matrix", Source = "one/pack" },
            Packs.Valid("bbb222-matrix") with { Name = "matrix", Source = "two/pack" },
            Packs.Valid("aaa111-neon") with { Name = "Neon", Source = "one/pack" },
            Packs.Valid("uploaded") with { Name = "Matrix" }, // not from a repository: stays as it is
        };

        var names = ThemeViewModel.ChoicesFor(ThemeClients.Web, packs).ToDictionary(c => c.Id, c => c.Name);

        Assert.Equal("Matrix (one/pack)", names["aaa111-matrix"]);
        Assert.Equal("matrix (two/pack)", names["bbb222-matrix"]);
        Assert.Equal("Matrix", names["uploaded"]);
        Assert.Equal("Neon", names["aaa111-neon"]);
    }

    [Fact]
    public async Task The_address_is_checked_on_the_page_and_a_wrong_one_cannot_be_sent()
    {
        var vm = await PageAsync();

        Assert.Null(vm.NewThemeSourceUrlProblem); // nothing typed yet
        Assert.False(vm.CanAddThemeSource);

        vm.NewThemeSourceUrl = "github.com/owner";
        Assert.Contains("no address of a GitHub repository", vm.NewThemeSourceUrlProblem);
        Assert.False(vm.CanAddThemeSource);
        Assert.False(vm.AddThemeSourceCommand.CanExecute(null));
        await vm.AddThemeSourceCommand.ExecuteAsync(null);
        Assert.Empty(_api.ThemeCalls);

        vm.NewThemeSourceUrl = "https://github.com/owner/repo";
        Assert.Null(vm.NewThemeSourceUrlProblem);
        Assert.True(vm.CanAddThemeSource);

        vm.NewThemeSourceUrl = "   ";
        Assert.Null(vm.NewThemeSourceUrlProblem);
        Assert.False(vm.CanAddThemeSource);
    }

    [Fact]
    public async Task A_refused_pack_shows_the_reason_and_keeps_the_address_for_a_correction()
    {
        _api.SourceFailure = "No themes found. The repository needs a folder 'themes' with one .json file for each theme.";
        var vm = await PageAsync();

        await AddAsync(vm);

        Assert.Contains("No themes found", vm.ErrorMessage);
        Assert.Equal("https://github.com/owner/repo", vm.NewThemeSourceUrl);
        Assert.Empty(vm.ThemeSources);
        Assert.False(vm.HasNotice);
        Assert.Empty(_api.ThemeCalls);

        vm.NewThemeSourceUrl = "https://github.com/owner/repo2"; // typing again clears the old complaint
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task A_pack_is_updated_by_hand_and_a_failure_is_shown_in_its_row_not_as_an_error()
    {
        var vm = await PageAsync();
        await AddAsync(vm);
        var row = vm.ThemeSources.Single();

        await vm.RefreshThemeSourceCommand.ExecuteAsync(row);

        Assert.Equal("Theme pack \"owner/repo\" updated.", vm.NoticeText);
        Assert.Contains("refresh-source:" + row.Id, _api.ThemeCalls);

        _api.SourceFailure = "GitHub does not answer more requests for now (rate limit). It is tried again later.";
        await vm.RefreshThemeSourceCommand.ExecuteAsync(vm.ThemeSources.Single());

        Assert.False(vm.HasError);
        Assert.False(vm.HasNotice);
        Assert.StartsWith("Update failed: GitHub does not answer", vm.ThemeSources.Single().Problem);
        Assert.Contains(vm.ThemePacks, r => r.Name == "Matrix"); // the themes stay
    }

    [Fact]
    public async Task Removing_a_pack_removes_its_themes_and_a_default_that_pointed_to_one_goes_back_to_the_plain_default()
    {
        var vm = await PageAsync();
        await AddAsync(vm);
        var row = vm.ThemeSources.Single();
        vm.FormDefaultWebTheme = vm.ThemePacks.Single(r => r.Name == "Matrix").Id;

        await vm.DeleteThemeSourceCommand.ExecuteAsync(row);

        Assert.Contains("delete-source:" + row.Id, _api.ThemeCalls);
        Assert.Empty(vm.ThemeSources);
        Assert.DoesNotContain(vm.ThemePacks, r => r.Source is not null);
        Assert.Equal(ThemeIds.Default, vm.FormDefaultWebTheme);
        Assert.Equal("Theme pack \"owner/repo\" removed.", vm.NoticeText);
    }

    [Fact]
    public async Task A_command_without_its_row_does_nothing()
    {
        var vm = await PageAsync();
        await AddAsync(vm);
        var row = vm.ThemeSources.Single();

        Assert.True(vm.CanChangeThemeSource(row));
        Assert.False(vm.CanChangeThemeSource(null));

        await vm.RefreshThemeSourceCommand.ExecuteAsync(null!); // a command is only guarded by its button
        await vm.DeleteThemeSourceCommand.ExecuteAsync(null!);
        Assert.Single(vm.ThemeSources);
    }

    [Fact]
    public async Task Somebody_without_themes_edit_does_not_even_ask_for_the_packs()
    {
        _api.ThemeSources.Add(new ThemeSourceDto("a1", "https://github.com/o/ok", "o/ok", 2, null, null, null));
        _api.Account = WithPermissions(2, "anna", Permission.BrandingView);
        _shell.Session.User = _api.Account;

        var vm = await PageAsync();

        Assert.False(vm.HasError); // the API would have said 403
        Assert.Empty(vm.ThemeSources);
        Assert.False(vm.CanManageThemes);
        vm.NewThemeSourceUrl = "https://github.com/owner/repo";
        Assert.False(vm.CanAddThemeSource);

        await vm.AddThemeSourceCommand.ExecuteAsync(null);

        Assert.Empty(_api.ThemeCalls);
    }

    [Fact]
    public async Task The_texts_follow_the_language()
    {
        _api.ThemeSources.Add(new ThemeSourceDto("a1", "https://github.com/o/down", "o/down", 1, null, false, "boom"));
        var vm = await PageAsync();

        _language.Select("de");

        Assert.Equal("Designs: 1 · noch nicht aktualisiert", vm.ThemeSources.Single().Details);
        Assert.Equal("Aktualisierung fehlgeschlagen: boom", vm.ThemeSources.Single().Problem);
        Assert.Contains("Stunden", vm.ThemeSourcesLede);
    }

    [Fact]
    public async Task The_page_forgets_the_packs_when_the_user_may_no_longer_see_it()
    {
        _api.ThemeSources.Add(new ThemeSourceDto("a1", "https://github.com/o/ok", "o/ok", 2, null, null, null));
        var vm = await PageAsync();
        vm.NewThemeSourceUrl = "https://github.com/owner/repo";
        Assert.Single(vm.ThemeSources);

        _api.Account = WithPermissions(2, "anna", Permission.ExamsView);
        _shell.Session.User = _api.Account;

        Assert.Empty(vm.ThemeSources);
        Assert.Equal("", vm.NewThemeSourceUrl);
    }
}
