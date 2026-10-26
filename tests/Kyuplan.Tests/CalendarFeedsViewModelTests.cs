using System.Globalization;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>The calendar feeds page and its form: sources with the subgroups and members of their group, a new address, deleting.</summary>
public sealed class CalendarFeedsViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly CalendarFeedsViewModel _vm;

    public CalendarFeedsViewModelTests()
    {
        _language.Select("en");
        _api.Account = TestAccounts.Dto(1, "boss", "boss", [], isAdmin: true);
        _vm = new CalendarFeedsViewModel(new ShellViewModel(_api, _language), _api);
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
    public async Task The_page_lists_the_feeds_with_what_they_contain_and_without_an_address()
    {
        _api.CalendarFeeds.Add(new CalendarFeedDto(1, "Kids", null,
            [new CalendarSourceDto(CalendarSourceKinds.Events, "g1"), new CalendarSourceDto(CalendarSourceKinds.Birthdays, "g1")], DateTimeOffset.UnixEpoch));

        await _vm.ActivateAsync();

        var row = Assert.Single(_vm.Feeds);
        Assert.Equal("Events (Dojo), Birthdays (Dojo)", row.SourcesText);
        Assert.False(_vm.IsEmpty);
        Assert.False(_vm.HasAddress);
        Assert.Equal("", _vm.Address);
        Assert.Equal("", _vm.AddressText);
    }

    [Fact]
    public async Task A_source_whose_group_is_gone_is_marked_in_the_list_in_both_languages()
    {
        _api.CalendarFeeds.Add(new CalendarFeedDto(1, "Kids", null,
            [new CalendarSourceDto(CalendarSourceKinds.Events, "g1"), new CalendarSourceDto(CalendarSourceKinds.Birthdays, "gone")],
            DateTimeOffset.UnixEpoch, [1]));
        await _vm.ActivateAsync();
        var row = Assert.Single(_vm.Feeds);

        Assert.Equal("Events (Dojo), ⚠ Birthdays (gone): group no longer exists – source is left out of the calendar", row.SourcesText);

        _language.Select("de");
        Assert.EndsWith(": Gruppe existiert nicht mehr – Quelle wird im Kalender ausgelassen", row.SourcesText);
        Assert.Contains("⚠", row.SourcesText);
    }

    [Fact]
    public async Task Sources_are_not_marked_when_no_position_is_reported_or_it_is_out_of_range()
    {
        _api.CalendarFeeds.Add(new CalendarFeedDto(1, "A", null, [new CalendarSourceDto(CalendarSourceKinds.Events, "g1")], DateTimeOffset.UnixEpoch, null));
        _api.CalendarFeeds.Add(new CalendarFeedDto(2, "B", null, [new CalendarSourceDto(CalendarSourceKinds.Events, "g1")], DateTimeOffset.UnixEpoch, [5, -1]));

        await _vm.ActivateAsync();

        Assert.All(_vm.Feeds, f => Assert.Equal("Events (Dojo)", f.SourcesText));
    }

    [Fact]
    public async Task A_feed_is_made_from_sources_with_the_ticked_subgroups_and_members()
    {
        var anna = _api.AddMember("Anna");
        _api.AddMember("Ben");
        await _vm.ActivateAsync();

        _vm.BeginCreateCommand.Execute(null);
        var source = Assert.Single(_vm.Editor.Sources);
        await Eventually(() => source.Members.Count == 2);
        Assert.False(_vm.Editor.CanSave); // no name yet

        _vm.Editor.Name = "Kids";
        source.SubGroups.Single().IsSelected = true;
        source.Members.Single(m => m.Name == "Anna").IsSelected = true;
        source.Invitation = EventInvitations.Only;
        source.NameContains = " Training ";
        source.Label = "Kids";
        await _vm.Editor.SaveCommand.ExecuteAsync(null);

        var sent = Assert.Single(_api.FeedInputs);
        var sentSource = Assert.Single(sent.Sources);
        Assert.Equal(["sg1"], sentSource.SubGroupIds);
        Assert.Equal([anna.Id], sentSource.MemberIds);
        Assert.Equal(sentSource with { SubGroupIds = null, MemberIds = null }, new CalendarSourceDto(CalendarSourceKinds.Events, "g1", null, null, EventInvitations.Only, "Training", null, false, false, "Kids"));
        Assert.False(_vm.Editor.IsOpen);
        Assert.Equal("Calendar “Kids” made.", _vm.NoticeText);
        Assert.Single(_vm.Feeds);
    }

    [Fact]
    public async Task The_address_of_a_new_feed_is_shown_once_and_can_be_dismissed()
    {
        await _vm.ActivateAsync();
        _vm.BeginCreateCommand.Execute(null);
        _vm.Editor.Name = "Kids";

        await _vm.Editor.SaveCommand.ExecuteAsync(null);

        Assert.True(_vm.HasAddress);
        Assert.StartsWith("https://x/calendar-feeds/token", _vm.Address);
        Assert.Equal("Address of “Kids”. Copy it now: it is not shown again.", _vm.AddressText);
        _language.Select("de");
        Assert.Equal("Adresse von „Kids“. Kopiere sie jetzt: Sie wird nicht noch einmal angezeigt.", _vm.AddressText);

        _vm.DismissAddressCommand.Execute(null);
        Assert.False(_vm.HasAddress);
        Assert.Equal("", _vm.Address);
        Assert.Equal("", _vm.AddressText);
        Assert.Null(_vm.Feeds.Single().Feed.Url); // the list does not have it: the server keeps only a hash
    }

    [Fact]
    public async Task Saving_a_changed_feed_shows_no_new_address_and_editing_does_not_lose_the_one_shown()
    {
        await _vm.ActivateAsync();
        _vm.BeginCreateCommand.Execute(null);
        _vm.Editor.Name = "Kids";
        await _vm.Editor.SaveCommand.ExecuteAsync(null);
        var address = _vm.Address;
        Assert.True(_vm.HasAddress);

        // The address is told only once: starting something else must not throw it away before it was copied.
        _vm.BeginEditCommand.Execute(_vm.Feeds.Single());
        Assert.True(_vm.HasAddress);

        _vm.Editor.Name = "Kids 2";
        await _vm.Editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(address, _vm.Address);
        Assert.Equal("Kids 2", _vm.Feeds.Single().Name);

        _vm.BeginCreateCommand.Execute(null);
        Assert.Equal(address, _vm.Address);
    }

    [Fact]
    public async Task A_deleted_feed_takes_its_address_away_but_the_address_of_another_stays()
    {
        _api.CalendarFeeds.Add(new CalendarFeedDto(1, "Kids", null, [new CalendarSourceDto(CalendarSourceKinds.Events, "g1")], DateTimeOffset.UnixEpoch));
        _api.CalendarFeeds.Add(new CalendarFeedDto(2, "Adults", null, [new CalendarSourceDto(CalendarSourceKinds.Events, "g1")], DateTimeOffset.UnixEpoch));
        await _vm.ActivateAsync();

        _vm.AskRegenerateCommand.Execute(_vm.Feeds.Single(f => f.Id == 1));
        await _vm.ConfirmCommand.ExecuteAsync(null);
        Assert.True(_vm.HasAddress);
        Assert.Contains("Kids", _vm.AddressText);

        _vm.AskDeleteCommand.Execute(_vm.Feeds.Single(f => f.Id == 2));
        await _vm.ConfirmCommand.ExecuteAsync(null);
        Assert.True(_vm.HasAddress);

        _vm.AskDeleteCommand.Execute(_vm.Feeds.Single(f => f.Id == 1));
        await _vm.ConfirmCommand.ExecuteAsync(null);
        Assert.False(_vm.HasAddress);
    }

    [Fact]
    public async Task A_failed_new_address_shows_no_address()
    {
        _api.CalendarFeeds.Add(new CalendarFeedDto(1, "Kids", null, [new CalendarSourceDto(CalendarSourceKinds.Events, "g1")], DateTimeOffset.UnixEpoch));
        await _vm.ActivateAsync();
        _vm.AskRegenerateCommand.Execute(_vm.Feeds.Single());
        _api.CalendarFeeds.Clear(); // gone meanwhile: the API answers 404

        await _vm.ConfirmCommand.ExecuteAsync(null);

        Assert.False(_vm.HasAddress);
    }

    [Fact]
    public async Task A_source_of_birthdays_sends_none_of_the_event_fields()
    {
        await _vm.ActivateAsync();
        _vm.BeginCreateCommand.Execute(null);
        var source = _vm.Editor.Sources[0];

        _vm.Editor.Name = "Birthdays";
        source.Kind = CalendarSourceKinds.Birthdays;
        source.Invitation = EventInvitations.All;
        source.NameContains = "x";
        source.OnlyExams = true;
        await _vm.Editor.SaveCommand.ExecuteAsync(null);

        var sent = _api.FeedInputs.Single().Sources.Single();
        Assert.Empty(sent.SubGroupIds!);
        Assert.Empty(sent.MemberIds!);
        Assert.Equal(sent with { SubGroupIds = null, MemberIds = null }, new CalendarSourceDto(CalendarSourceKinds.Birthdays, "g1"));
    }

    [Fact]
    public async Task A_name_cannot_be_used_twice_and_a_form_without_a_source_cannot_be_saved()
    {
        _api.CalendarFeeds.Add(new CalendarFeedDto(1, "Kids", null, [new CalendarSourceDto(CalendarSourceKinds.Events, "g1")], DateTimeOffset.UnixEpoch));
        await _vm.ActivateAsync();

        _vm.BeginCreateCommand.Execute(null);
        _vm.Editor.Name = "kids";
        Assert.NotNull(_vm.Editor.NameProblem);
        Assert.False(_vm.Editor.CanSave);

        _vm.Editor.Name = "Adults";
        Assert.True(_vm.Editor.CanSave);
        _vm.Editor.RemoveSourceCommand.Execute(_vm.Editor.Sources[0]);
        Assert.False(_vm.Editor.CanSave);
    }

    [Fact]
    public async Task A_new_address_and_deleting_ask_first()
    {
        _api.CalendarFeeds.Add(new CalendarFeedDto(1, "Kids", null, [new CalendarSourceDto(CalendarSourceKinds.Events, "g1")], DateTimeOffset.UnixEpoch));
        await _vm.ActivateAsync();
        var row = _vm.Feeds.Single();

        _vm.AskRegenerateCommand.Execute(row);
        Assert.True(_vm.IsConfirming);
        Assert.Empty(_api.FeedCalls);
        await _vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(["regenerate-feed:1"], _api.FeedCalls);
        Assert.StartsWith("https://x/calendar-feeds/new", _vm.Address);
        Assert.True(_vm.HasAddress);

        _vm.AskDeleteCommand.Execute(_vm.Feeds.Single());
        _vm.CancelConfirmCommand.Execute(null);
        Assert.Single(_api.CalendarFeeds);

        _vm.AskDeleteCommand.Execute(_vm.Feeds.Single());
        await _vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(_api.CalendarFeeds);
        Assert.True(_vm.IsEmpty);
    }
}
