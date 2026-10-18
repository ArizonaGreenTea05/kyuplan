using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>One member the signed-in user is, to choose from when they are several (a person in two Spond groups is two members).</summary>
public sealed class OwnMemberRow(MemberChoiceDto member, string? groupName)
{
    public int Id => member.Id;

    public string GroupId => member.GroupId;

    public string Name => member.Name;

    /// <summary>The name, with the group when it is known which one.</summary>
    public string Label => groupName is null ? member.Name : $"{member.Name} ({groupName})";
}

/// <summary>
/// What the users wrote and gave about the signed-in user themselves, shown on the account page: the comments and the evaluations of the
/// members they are (an administrator says which). It is the one place that needs no page permission, only <c>comments.viewOwn</c> and
/// <c>ratings.viewOwn</c>, each for its own part and independent of <c>comments.view</c> / <c>ratings.view</c> (which never show a user
/// what is written about themselves). Used by both clients.
/// </summary>
public sealed partial class AboutMeViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly IPlannerApi _api;

    public AboutMeViewModel(ShellViewModel shell, IPlannerApi api)
    {
        _shell = shell;
        _api = api;
        Comments = new MemberCommentsViewModel(shell, api);

        shell.PermissionsChanged += (_, _) =>
        {
            // What the user may no longer read is dropped at once, not left on show until the page is read again.
            if (!CanViewRatings) Ratings = [];
            if (!CanViewComments) Comments.Close();
            NotifyStateChanged();
        };
    }

    /// <summary>The comments about the member that is chosen: the same thread view as on the members page.</summary>
    public MemberCommentsViewModel Comments { get; }

    /// <summary>The user may read the comments about themselves.</summary>
    public bool CanViewComments => _shell.Can(Permission.CommentsViewOwn);

    /// <summary>The user may read the evaluations of themselves.</summary>
    public bool CanViewRatings => _shell.Can(Permission.RatingsViewOwn);

    /// <summary>The members the user is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(HasSeveralMembers))]
    private IReadOnlyList<OwnMemberRow> _members = [];

    /// <summary>The member whose comments and evaluations are shown (the first one until the user picks another).</summary>
    [ObservableProperty]
    private OwnMemberRow? _selected;

    /// <summary>The evaluations of the chosen member, the latest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRatings), nameof(RatingSummary))]
    private IReadOnlyList<RatingRow> _ratings = [];

    /// <summary>The section is shown: the user may read something about themselves, and is a member.</summary>
    public bool IsVisible => (CanViewComments || CanViewRatings) && Members.Count > 0;

    /// <summary>The user is more than one member: they choose which one to look at.</summary>
    public bool HasSeveralMembers => Members.Count > 1;

    public bool HasRatings => Ratings.Count > 0;

    /// <summary>"Average 3.5 of 5 from 2 evaluations", or that nobody evaluated yet.</summary>
    public string RatingSummary => !HasRatings
        ? Strings.Rating_None
        : string.Format(Ratings.Count == 1 ? Strings.Rating_AverageOne : Strings.Rating_Average, Display.Stars(Ratings.Average(r => r.Stars)), Ratings.Count);

    /// <summary>Reads the members the user is and what is written about the chosen one; nothing at all without the "own" permissions.</summary>
    public async Task ReloadAsync()
    {
        if (!CanViewComments && !CanViewRatings)
        {
            Clear();
            return;
        }

        await LoadAsync(async ct =>
        {
            var own = await _api.GetOwnMembersAsync(ct);
            Members = [.. own.Select(m => new OwnMemberRow(m, _shell.Groups.FirstOrDefault(g => g.Id == m.GroupId)?.Name))];
            Selected = Members.FirstOrDefault(m => m.Id == Selected?.Id) ?? Members.FirstOrDefault();
            await ShowSelectedAsync(ct);
        });
    }

    [RelayCommand]
    private async Task SelectAsync(OwnMemberRow member)
    {
        if (ReferenceEquals(member, Selected) || !Members.Contains(member)) return;

        Selected = member;
        await LoadAsync(ShowSelectedAsync);
    }

    private async Task ShowSelectedAsync(CancellationToken cancellationToken)
    {
        Comments.Close();
        Ratings = [];
        if (Selected is not { } member) return;

        if (CanViewRatings)
        {
            var me = _shell.CurrentUser?.Id;
            var ratings = await _api.GetMemberRatingsAsync(member.Id, cancellationToken);
            Ratings = [.. ratings.Select(r => new RatingRow(r, r.UserId == me))];
        }

        if (CanViewComments)
            await Comments.ShowAsync(new MemberRow(new MemberDto(member.Id, member.GroupId, member.Name, null, null), new BeltOption()));
    }

    /// <summary>Nothing of what was read is kept (the user may no longer read it, or somebody else signed in).</summary>
    private void Clear()
    {
        Members = [];
        Selected = null;
        Ratings = [];
        Comments.Close();
        ErrorMessage = null;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanViewComments));
        OnPropertyChanged(nameof(CanViewRatings));
        OnPropertyChanged(nameof(IsVisible));
    }

    /// <summary>The language changed: the texts that were worded from it are read again.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(RatingSummary));
        foreach (var rating in Ratings) rating.Refresh();
    }
}
