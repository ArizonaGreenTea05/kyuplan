using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestData;

namespace Kyuplan.Tests;

/// <summary>Strings.resx is English, Strings.de.resx German. These tests keep the two in step.</summary>
public class ResourceFileTests
{
    private static Dictionary<string, string> Load(CultureInfo culture)
    {
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"No resources for '{culture.Name}'.");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static readonly Dictionary<string, string> English = Load(CultureInfo.InvariantCulture);
    private static readonly Dictionary<string, string> German = Load(new CultureInfo("de"));

    private static string[] Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Distinct().Order().ToArray();

    [Fact]
    public void German_has_exactly_the_keys_of_the_neutral_language()
    {
        Assert.Empty(English.Keys.Except(German.Keys));   // untranslated
        Assert.Empty(German.Keys.Except(English.Keys));   // left over after a rename
    }

    [Fact]
    public void No_text_is_empty()
    {
        Assert.All(English, e => Assert.False(string.IsNullOrWhiteSpace(e.Value), $"{e.Key} (en) is empty"));
        Assert.All(German, e => Assert.False(string.IsNullOrWhiteSpace(e.Value), $"{e.Key} (de) is empty"));
    }

    [Fact]
    public void A_translation_uses_the_same_placeholders_as_the_original()
    {
        foreach (var (key, english) in English)
            Assert.True(Placeholders(english).SequenceEqual(Placeholders(German[key])),
                $"{key}: English has {string.Join(" ", Placeholders(english))}, German has {string.Join(" ", Placeholders(German[key]))}");
    }

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repository root not found."), "src");
    }

    [Fact]
    public void Every_key_is_used_somewhere_in_the_sources()
    {
        var files = Directory.EnumerateFiles(SourceRoot(), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".razor") || f.EndsWith(".xaml"))
            .Where(f => !Regex.IsMatch(f, @"[\\/](obj|bin|Migrations|PostgresMigrations)[\\/]"))
            .Where(f => !f.EndsWith(".g.cs") && !f.EndsWith(".Designer.cs"));
        var text = string.Join('\n', files.Select(File.ReadAllText));

        var words = Regex.Matches(text, @"\w+").Select(m => m.Value).ToHashSet();
        // Keys built at runtime ("PermissionGroup_" + name) are covered by their literal prefix.
        var prefixes = Regex.Matches(text, "\"([A-Za-z]+_)\"").Select(m => m.Groups[1].Value).ToHashSet();

        var unused = English.Keys
            .Where(k => !words.Contains(k) && !prefixes.Any(k.StartsWith))
            .Order().ToArray();

        Assert.True(unused.Length == 0, "Unused keys, remove them from Strings.resx and Strings.de.resx: " + string.Join(", ", unused));
    }

    [Theory]
    [InlineData("Strings.resx")]
    [InlineData("Strings.de.resx")]
    public void Keys_are_grouped_by_their_feature_prefix(string file)
    {
        var xml = XDocument.Load(Path.Combine(SourceRoot(), "Kyuplan.Client.Shared", "Resources", file));
        var prefixes = xml.Root!.Elements("data").Select(d => ((string)d.Attribute("name")!).Split('_')[0]).ToList();

        // A prefix is scattered when it starts a new run after another prefix came in between.
        var scattered = prefixes.Where((p, i) => i > 0 && p != prefixes[i - 1]).GroupBy(p => p).Where(g => g.Count() > 1 || g.Key == prefixes[0])
            .Select(g => g.Key).ToArray();

        Assert.True(scattered.Length == 0, $"{file}: keys of these prefixes are not next to each other: " + string.Join(", ", scattered));
    }

    [Fact]
    public void Every_text_is_actually_translated_apart_from_a_few_that_read_the_same()
    {
        var same = English.Where(e => e.Value == German[e.Key]).Select(e => e.Key).Order().ToArray();

        // Names, formats and loan words. A new entry here must be a conscious decision, not a forgotten translation.
        Assert.Equal(["AuditArea_tags", "Col_Engagement", "Col_Form", "Col_Status", "Design_Aero", "Design_Windows10", "Format_Month", "Format_MonthLong", "Manual_ColName", "Manual_Name", "Settings_DataSource_spond", "Settings_ScriptSecretName", "Setup_AdminTitle", "Shell_ApiVersion", "Shell_BrandAria", "Theme_CatppuccinLatte", "Theme_CatppuccinMocha", "Theme_Dracula", "Theme_LiquidGlass", "Users_StatusOnline"], same);
    }
}

public sealed class LanguageServiceTests : IDisposable
{
    public LanguageServiceTests() => CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-GB");

    public void Dispose()
    {
        // Select() applies the culture process-wide; do not leak it into other tests.
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("DE-ch", "de")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("fr", "en")]       // no translation: fall back to English
    [InlineData("", "en")]
    [InlineData(null, "en")]
    [InlineData("not a culture!", "en")]
    public void A_code_resolves_to_the_best_supported_language(string? code, string expected) =>
        Assert.Equal(expected, LanguageService.Resolve(code).Code);

    [Fact]
    public void A_service_starts_in_the_language_of_the_system()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

        Assert.Equal("de", new LanguageService().Current.Code);
    }

    [Fact]
    public void Selecting_a_language_switches_texts_and_formats_and_tells_listeners()
    {
        var service = new LanguageService();
        var changes = 0;
        service.Changed += (_, _) => changes++;

        service.Select("de");

        Assert.Equal(1, changes);
        Assert.Equal("Aktualisieren", Strings.Shell_Refresh);
        Assert.Equal("66,7 %", Display.Percent(66.7));
        Assert.Equal("de-DE", CultureInfo.CurrentCulture.Name); // not the process-wide default: parallel tests reset that

        service.Select("de"); // nothing changed
        Assert.Equal(1, changes);

        service.Select("en");
        Assert.Equal("Refresh", Strings.Shell_Refresh);
        Assert.Equal("66.7 %", Display.Percent(66.7));
    }
}

public sealed class LocalizedViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;

    public LocalizedViewModelTests()
    {
        _language.Select("en");
        _shell = new ShellViewModel(_api, _language);
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition was not met in time");
    }

    [Fact]
    public async Task The_dashboard_rebuilds_its_texts_when_the_language_changes_without_asking_the_api_again()
    {
        var vm = new DashboardViewModel(_shell, _api);
        await vm.ActivateAsync();
        await Eventually(() => !vm.IsBusy && vm.HasData);
        Assert.Equal("Events held", vm.Kpis[0].Label);
        Assert.Equal("Last 90 days", vm.PeriodOptions.Single(p => p.Days == 90).Label);
        Assert.Equal("66.6 %", vm.Kpis.Single(k => k.Label == "Attendance").Value);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var periodList = vm.PeriodOptions;
        var period = periodList.Single(p => p.Days == 90);
        var labelChanged = false;
        period.PropertyChanged += (_, e) => labelChanged |= e.PropertyName == nameof(PeriodOption.Label);

        _language.Select("de");

        Assert.Equal("Stattgefundene Termine", vm.Kpis[0].Label);
        Assert.Equal("66,6 %", vm.Kpis.Single(k => k.Label == "Anwesenheit").Value);
        Assert.Equal("4 von 9", vm.Kpis.Single(k => k.Label == "75 % erreicht").Value);
        Assert.Equal("Letzte 90 Tage", vm.PeriodOptions.Single(p => p.Days == 90).Label);
        Assert.Equal("Übersicht", vm.Title);
        Assert.Equal("6 von 10 kommen", vm.Upcoming.Single().AcceptedText);
        Assert.Equal("2 offen", vm.Upcoming.Single().OpenText);
        Assert.Contains(nameof(DashboardViewModel.Title), changed);

        // The list itself stays: a replaced list loses its selection in a WPF combo box. Its entries re-read their text.
        Assert.Same(periodList, vm.PeriodOptions);
        Assert.True(labelChanged);
        Assert.Single(_api.DashboardRequests);
    }

    [Fact]
    public async Task The_statistics_page_rebuilds_filter_summary_options_and_figures()
    {
        var vm = new AttendanceStatisticsViewModel(_shell, _api, TimeProvider.System);
        await vm.ActivateAsync();
        await Eventually(() => !vm.IsBusy && vm.HasResults);
        Assert.StartsWith("Who attended at least 70 %", vm.FilterSummary);
        var summaryChanged = false;
        vm.PropertyChanged += (_, e) => summaryChanged |= e.PropertyName == nameof(vm.SubGroupSummary);
        Assert.Equal("Everyone", vm.SubGroupSummary);
        Assert.Equal("Meeting 70 %", vm.Result.Kpis[0].Label);
        Assert.Equal("1 of 3", vm.Result.Kpis[0].Value);
        Assert.Contains("between \"Gürtelprüfung\"", vm.FilterSummary);
        var event1 = vm.EventOptions[1];
        Assert.StartsWith("Gürtelprüfung · Sat, 28 Mar", event1.Label);
        Assert.Equal("Choose an event …", vm.EventOptions[0].Label);

        _language.Select("de");

        Assert.Contains("Wer war bei mindestens 70 %", vm.FilterSummary);
        Assert.StartsWith("Zwischen „Gürtelprüfung“ (28. Mär", vm.FilterSummary);
        Assert.Same(event1, vm.EventOptions[1]); // the entries stay, so a combo box keeps its selection
        Assert.StartsWith("Gürtelprüfung · Sa", event1.Label); // the weekday's abbreviation differs between ICU versions
        Assert.Contains("28. Mär", event1.Label);
        Assert.Equal("Termin wählen …", vm.EventOptions[0].Label);
        Assert.StartsWith("Der Zeitraum ergibt sich aus den oben gewählten Terminen", vm.SpanHint);
        Assert.True(summaryChanged);
        Assert.Equal("Alle", vm.SubGroupSummary);
        Assert.Equal(["Kinder"], vm.SubGroupOptions.Select(o => o.Name)); // group data is not translated
        Assert.Equal("70 % erreicht", vm.Result.Kpis[0].Label);
        Assert.Equal("1 von 3", vm.Result.Kpis[0].Value);
        Assert.Equal("Anwesenheitsstatistik", vm.Title);
        Assert.Equal("4 Mitglieder", vm.Table.MembersCaption);
        Assert.Single(_api.AttendanceRequests);
    }

    [Fact]
    public async Task A_filter_error_is_shown_in_the_new_language()
    {
        var vm = new AttendanceStatisticsViewModel(_shell, _api, TimeProvider.System);
        await vm.ActivateAsync();
        await Eventually(() => !vm.IsBusy && vm.HasResults);
        vm.From = new DateTime(2026, 6, 30);
        vm.To = new DateTime(2026, 1, 1);
        await Eventually(() => vm.FilterError is not null);
        Assert.Equal("The start date must not be after the end date.", vm.FilterError);

        _language.Select("de");

        Assert.Equal("Das Startdatum darf nicht nach dem Enddatum liegen.", vm.FilterError);
    }

    [Fact]
    public async Task An_event_order_error_is_shown_in_the_new_language()
    {
        var vm = new AttendanceStatisticsViewModel(_shell, _api, TimeProvider.System);
        await vm.ActivateAsync();
        await Eventually(() => !vm.IsBusy && vm.HasResults);
        vm.FromEventId = "exam-2";
        vm.ToEventId = "exam-1";
        await Eventually(() => vm.FilterError is not null);
        Assert.Equal("The \"From\" event must not be after the \"To\" event.", vm.FilterError);

        _language.Select("de");

        Assert.Equal("Der Termin „Von“ darf nicht nach dem Termin „Bis“ liegen.", vm.FilterError);
    }

    [Fact]
    public void Dates_numbers_and_statuses_follow_the_language()
    {
        var date = new DateOnly(2026, 10, 14);

        Assert.Equal("14 Oct 2026", Display.Date(date));
        Assert.Equal("Qualified", Display.Status(QualificationStatus.Qualified));

        _language.Select("de");

        Assert.StartsWith("14. Okt", Display.Date(date));
        Assert.EndsWith("2026", Display.Date(date));
        Assert.Equal("Qualifiziert", Display.Status(QualificationStatus.Qualified));
        Assert.Equal("▲ +18,5 Pkt. gegenüber früher", Display.TrendText(AttendanceTrend.Rising, 18.5));
    }

    [Fact]
    public async Task Chart_tooltips_use_the_singular_or_plural_form_by_count()
    {
        var one = await LoadDistribution(members: 1);
        var many = await LoadDistribution(members: 3);

        Assert.Equal("1 member attended 70–79 % of their invitations", one.Result.Distribution.Single().Tooltip);
        Assert.Equal("3 members attended 70–79 % of their invitations", many.Result.Distribution.Single().Tooltip);

        _language.Select("de");

        Assert.Equal("1 Mitglied war bei 70–79 % der Einladungen dabei", one.Result.Distribution.Single().Tooltip);
        Assert.Equal("3 Mitglieder waren bei 70–79 % der Einladungen dabei", many.Result.Distribution.Single().Tooltip);
    }

    private async Task<AttendanceStatisticsViewModel> LoadDistribution(int members)
    {
        _api.AttendanceHandler = (filter, _) => Task.FromResult(FakePlannerApi.Statistics(filter) with
        {
            Distribution = [new DistributionBucketDto(70, 80, members)],
        });

        var vm = new AttendanceStatisticsViewModel(_shell, _api, TimeProvider.System);
        await vm.ActivateAsync();
        await Eventually(() => !vm.IsBusy && vm.Result.Distribution.Count == 1);
        return vm;
    }
}
