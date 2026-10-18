using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// An entry of the belt lists: a belt, or the empty entry (<see cref="IsNone"/>, "No belt") that comes first. The entries are not
/// replaced when the language changes (a replaced list loses its selection in WPF): they re-read their text after <see cref="Refresh"/>.
/// </summary>
public sealed class BeltOption(BeltDto? belt = null) : ObservableObject
{
    public int? Id => belt?.Id;

    public bool IsNone => belt is null;

    public string Label => belt?.Name ?? Strings.MemberPage_NoBelt;

    /// <summary>The belt's colour as <c>#RRGGBB</c>; empty for the empty entry.</summary>
    public string Color => belt?.PrimaryColor ?? "";

    /// <summary>The colour of the stripe as <c>#RRGGBB</c>; empty when there is none.</summary>
    public string SecondaryColor => belt?.SecondaryColor ?? "";

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One line of the member list: the member plus ready-to-show texts, which follow the language after <see cref="Refresh"/>.</summary>
public sealed class MemberRow(MemberDto member, BeltOption belt) : ObservableObject
{
    public MemberDto Member => member;

    public int Id => member.Id;

    public string Name => member.Name;

    public BeltOption Belt => belt;

    public bool HasBelt => !belt.IsNone;

    public int CommentCount => member.CommentCount;

    /// <summary>"Comments", or "Comments (3)" once there are some: what the button that opens the comments says.</summary>
    public string CommentsText => member.CommentCount > 0 ? string.Format(Strings.MemberPage_CommentsCount, member.CommentCount) : Strings.MemberPage_Comments;

    public bool Registered => member.Registered;

    public string RegisteredText => member.Registered ? Strings.MemberPage_Registered : Strings.MemberPage_NotRegistered;

    /// <summary>Sort key: members who are not registered yet come first.</summary>
    public int RegisteredSort => member.Registered ? 1 : 0;

    public string StartedText => member.StartedAt is { } day ? Display.Date(day) : Strings.MemberPage_NotKnown;

    /// <summary>Sort key: members whose start day is not known come first.</summary>
    public DateOnly StartedSort => member.StartedAt ?? DateOnly.MinValue;

    public string BirthText => member.BirthDate is { } day ? Display.Date(day) : Strings.MemberPage_NotKnown;

    public DateOnly BirthSort => member.BirthDate ?? DateOnly.MinValue;

    /// <summary>The day the member got the belt they hold; "not known" for a member with a belt and no day, nothing for a member without a belt.</summary>
    public string BeltSinceText => !HasBelt ? Display.None : member.BeltSince is { } day ? Display.Date(day) : Strings.MemberPage_NotKnown;

    public DateOnly BeltSinceSort => member.BeltSince ?? DateOnly.MinValue;

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// The members page: the members of the selected group (a copy of Spond's, kept up to date by the API) with what Spond does not know:
/// the belt each holds and the day they got it, the day each started and, when Spond has none, the birth date (the age a belt asks for
/// is worked out from it). All of it can be set one member at a time. Used by both clients.
/// </summary>
public sealed partial class MembersViewModel : PageViewModel
{
    private readonly IPlannerApi _api;
    private readonly TimeProvider _time;
    private readonly BeltOption _noBelt = new();
    private Func<string>? _notice;

    public MembersViewModel(ShellViewModel shell, IPlannerApi api, TimeProvider time) : base(shell)
    {
        _api = api;
        _time = time;
        _beltOptions = [_noBelt];
        _formBelt = _noBelt;

        Comments = new MemberCommentsViewModel(shell, api);
        Comments.CountChanged += (_, e) => ShowCommentCount(e.MemberId, e.Count);
        Comments.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MemberCommentsViewModel.IsOpen)) return;

            OnPropertyChanged(nameof(IsShowingComments));
            NotifyStateChanged();
        };

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyStateChanged();
        };
    }

    public override string Title => Strings.Nav_Members;

    protected override string? ViewPermission => Permission.MembersView;

    /// <summary>Whether the user may set a member's belt and days; without it the page only shows them.</summary>
    public bool CanEdit => Shell.Can(Permission.MembersEdit);

    /// <summary>
    /// Whether the user may read the comments on members at all (the column of buttons is shown). Comments are no part of what the members page
    /// shows by itself; which members' comments, see <see cref="MayViewComments"/>.
    /// </summary>
    public bool CanViewComments => Shell.Can(Permission.CommentsView) || Shell.Can(Permission.CommentsViewOwn);

    /// <summary>
    /// Whether the user may read the comments on this member: <c>comments.view</c> for everybody they are not, <c>comments.viewOwn</c> for the
    /// members they are themselves, each without the other.
    /// </summary>
    public bool MayViewComments(MemberRow? member) =>
        member is not null && Shell.CurrentUser?.MayRead(member.Id, Permission.CommentsView, Permission.CommentsViewOwn) == true;

    protected override void OnPermissionsChanged()
    {
        if (IsShowingComments && !MayViewComments(Comments.Member)) Comments.Close(); // the user may no longer read what is open

        base.OnPermissionsChanged();
        NotifyStateChanged();
    }

    /// <summary>The comments on one member, which the page shows in place of the list while they are open.</summary>
    public MemberCommentsViewModel Comments { get; }

    /// <summary>
    /// Belts are created and deleted on the belts page, and members come and go with Spond: read both again whenever the page is shown,
    /// unless the user is in the middle of something (a form). The comments of a member that are open are read again too (tags may have been
    /// changed on the tags page meanwhile), which leaves a comment that is being written as it is.
    /// </summary>
    protected override bool ReloadsOnActivation => !IsFormOpen;

    /// <summary>
    /// The members of another group are another list: the old one goes away, and with it a form for one of its members, so that a
    /// list that fails to load does not leave the previous group's members on show.
    /// </summary>
    protected override void OnContextChanged()
    {
        CloseForm();

        // The data was refreshed (the same group): the comments stay open, a draft included, and are read again with the list.
        if (Comments.Member is { } open && open.Member.GroupId != Shell.SelectedGroupId) Comments.Close();
        Members = [];
        HasLoaded = false;
    }

    /// <summary>Every member of the selected group, by name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleMembers), nameof(CountText), nameof(IsNobodyHere), nameof(NoneMatch))]
    private IReadOnlyList<MemberRow> _members = [];

    /// <summary>The belts, led by the empty entry: what a member's belt can be set to. One list, so that a selection of it stays valid.</summary>
    [ObservableProperty]
    private IReadOnlyList<BeltOption> _beltOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNobodyHere), nameof(NoneMatch))]
    private bool _hasLoaded;

    // --- Finding members -------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleMembers), nameof(CountText), nameof(NoneMatch))]
    private string _searchText = "";

    /// <summary>Only members who hold no belt yet: what is left to do when belts are handed out one by one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleMembers), nameof(CountText), nameof(NoneMatch))]
    private bool _onlyWithoutBelt;

    /// <summary>The members that fit the search and the belt filter, by name.</summary>
    public IReadOnlyList<MemberRow> VisibleMembers => [.. Members.Where(Fits)];

    private bool Fits(MemberRow member)
    {
        if (OnlyWithoutBelt && member.HasBelt) return false;

        // Every word has to occur in the name; case and accents do not matter.
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        return SearchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(word =>
            compare.IndexOf(member.Name, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
    }

    /// <summary>"12 members", or "3 of 12 members" while a filter hides some.</summary>
    public string CountText => VisibleMembers.Count == Members.Count
        ? string.Format(Strings.Stats_MembersCount, Members.Count)
        : string.Format(Strings.Stats_MembersCountOf, VisibleMembers.Count, Members.Count);

    /// <summary>The group has no members (as far as the API knows).</summary>
    public bool IsNobodyHere => HasLoaded && Members.Count == 0;

    /// <summary>There are members, but none fits the search and the filter.</summary>
    public bool NoneMatch => HasLoaded && Members.Count > 0 && VisibleMembers.Count == 0;

    // --- Result of the last action ---------------------------------------------------------------------------

    /// <summary>What the last save achieved, e.g. <c>Anna saved.</c>. Reads its text again when the language changes.</summary>
    public string NoticeText => _notice?.Invoke() ?? "";

    public bool HasNotice => _notice is not null;

    private void SetNotice(Func<string>? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    // --- The form: one member --------------------------------------------------------------------------------

    /// <summary>The member the form changes; <c>null</c> while the form is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormOpen), nameof(FormTitle))]
    private MemberRow? _editing;

    [ObservableProperty]
    private BeltOption _formBelt;

    /// <summary>The day the member started; <c>null</c> = not known. A date picker holds a <see cref="DateTime"/>, so this does too.</summary>
    [ObservableProperty]
    private DateTime? _formStartedAt;

    /// <summary>The day the member was born; <c>null</c> = not known.</summary>
    [ObservableProperty]
    private DateTime? _formBirthDate;

    /// <summary>The day the member got the belt chosen; <c>null</c> = not known. There is none without a belt.</summary>
    [ObservableProperty]
    private DateTime? _formBeltSince;

    public bool IsFormOpen => Editing is not null;

    public string FormTitle => Editing is { } member ? string.Format(Strings.MemberPage_FormEdit, member.Name) : "";

    /// <summary>The entry chosen in the form; the empty one if the combo box lost its selection (it does when its list is replaced).</summary>
    private BeltOption SelectedBelt => FormBelt ?? _noBelt;

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().Date);

    private DateOnly? FormStartedDay => FormStartedAt is { } day ? DateOnly.FromDateTime(day) : null;

    private DateOnly? FormBirthDay => FormBirthDate is { } day ? DateOnly.FromDateTime(day) : null;

    /// <summary>The day the belt was received as it would be saved: none without a belt.</summary>
    private DateOnly? FormBeltSinceDay => SelectedBelt.IsNone || FormBeltSince is not { } day ? null : DateOnly.FromDateTime(day);

    /// <summary>A belt is chosen, so there can be a day it was received: the field is only for members with a belt.</summary>
    public bool CanSetBeltSince => IsFormOpen && !SelectedBelt.IsNone;

    /// <summary>The start day typed is one the API refuses (before 1950, or in the future).</summary>
    public string? StartedAtProblem =>
        IsFormOpen && MemberRules.CheckStartedAt(FormStartedDay, Today) is not null
            ? string.Format(Strings.MemberPage_StartedInvalid, Display.Date(MemberRules.EarliestStart))
            : null;

    /// <summary>The birth date typed is one the API refuses (before 1900, or in the future).</summary>
    public string? BirthDateProblem =>
        IsFormOpen && MemberRules.CheckBirthDate(FormBirthDay, Today) is not null
            ? string.Format(Strings.MemberPage_BirthInvalid, Display.Date(MemberRules.EarliestBirth))
            : null;

    /// <summary>The day the belt was received is one the API refuses (before 1950, or in the future).</summary>
    public string? BeltSinceProblem =>
        IsFormOpen && MemberRules.CheckBeltSince(FormBeltSinceDay, Today) is not null
            ? string.Format(Strings.MemberPage_StartedInvalid, Display.Date(MemberRules.EarliestStart))
            : null;

    /// <summary>The form holds days the API accepts, and something differs from what the member has.</summary>
    public bool CanSave =>
        CanEdit && Editing is { } member && !IsBusy && StartedAtProblem is null && BirthDateProblem is null && BeltSinceProblem is null
        && (SelectedBelt.Id != member.Member.BeltId
            || FormStartedDay != member.Member.StartedAt
            || FormBirthDay != member.Member.BirthDate
            || FormBeltSinceDay != (SelectedBelt.IsNone ? null : member.Member.BeltSince));

    private bool CanBeginEdit(MemberRow? member) => CanEdit && member is not null && !ReferenceEquals(member, Editing) && !IsShowingComments;

    [RelayCommand(CanExecute = nameof(CanBeginEdit))]
    private void BeginEdit(MemberRow member)
    {
        if (!CanBeginEdit(member)) return; // Execute does not ask CanExecute: what the user may not do is not done

        ErrorMessage = null;
        SetNotice(null);

        Editing = member;
        FormBelt = member.Belt;
        FormStartedAt = member.Member.StartedAt?.ToDateTime(TimeOnly.MinValue);
        FormBirthDate = member.Member.BirthDate?.ToDateTime(TimeOnly.MinValue);
        FormBeltSince = member.Member.BeltSince?.ToDateTime(TimeOnly.MinValue);
        NotifyStateChanged();
    }

    [RelayCommand]
    private void CancelForm()
    {
        ErrorMessage = null;
        CloseForm();
    }

    private void CloseForm()
    {
        Editing = null;
        NotifyStateChanged();
    }

    private bool CanClearStartedAt() => FormStartedAt is not null;

    [RelayCommand(CanExecute = nameof(CanClearStartedAt))]
    private void ClearStartedAt() => FormStartedAt = null;

    private bool CanClearBirthDate() => FormBirthDate is not null;

    [RelayCommand(CanExecute = nameof(CanClearBirthDate))]
    private void ClearBirthDate() => FormBirthDate = null;

    private bool CanClearBeltSince() => CanSetBeltSince && FormBeltSince is not null;

    [RelayCommand(CanExecute = nameof(CanClearBeltSince))]
    private void ClearBeltSince() => FormBeltSince = null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave || Editing is not { } editing) return; // a command is only guarded by its button; a form can also be submitted with Enter

        var input = new MemberInputDto(SelectedBelt.Id, FormStartedDay, FormBirthDay, FormBeltSinceDay);
        MemberDto? saved = null;
        await LoadAsync(async ct => saved = await _api.UpdateMemberAsync(editing.Id, input, ct));

        if (saved is null) return; // the reason is in ErrorMessage; the form stays open so the user can correct it

        var updated = new MemberRow(saved, BeltOf(saved.BeltId));
        Members = [.. Members.Select(m => ReferenceEquals(m, editing) ? updated : m)];
        CloseForm();

        var name = saved.Name;
        SetNotice(() => string.Format(Strings.MemberPage_Saved, name));
    }

    partial void OnFormBeltChanged(BeltOption value)
    {
        // Without a belt there is no day it was received: what was typed is not kept for the case the belt comes back.
        if (value is { IsNone: true }) FormBeltSince = null;
        FormEdited();
    }

    partial void OnFormStartedAtChanged(DateTime? value) => FormEdited();

    partial void OnFormBirthDateChanged(DateTime? value) => FormEdited();

    partial void OnFormBeltSinceChanged(DateTime? value) => FormEdited();

    /// <summary>The user is correcting the form: what the API said about the last attempt is old news.</summary>
    private void FormEdited()
    {
        if (IsFormOpen) ErrorMessage = null;
        NotifyStateChanged();
    }

    // --- The comments on a member ----------------------------------------------------------------------------

    /// <summary>The page shows the comments on one member, not the list of members.</summary>
    public bool IsShowingComments => Comments.IsOpen;

    private bool CanOpenComments(MemberRow? member) => MayViewComments(member) && !IsBusy && !IsShowingComments;

    [RelayCommand(CanExecute = nameof(CanOpenComments))]
    private async Task OpenCommentsAsync(MemberRow member)
    {
        ErrorMessage = null;
        SetNotice(null);
        CloseForm();

        await Comments.ShowAsync(member);
    }

    private bool CanCloseComments() => IsShowingComments;

    [RelayCommand(CanExecute = nameof(CanCloseComments))]
    private void CloseComments() => Comments.Close();

    /// <summary>The member's row shows how many comments there are, also after one was written or deleted.</summary>
    private void ShowCommentCount(int memberId, int count)
    {
        if (Members.All(m => m.Id != memberId || m.CommentCount == count)) return;

        Members = [.. Members.Select(m => m.Id == memberId ? new MemberRow(m.Member with { CommentCount = count }, m.Belt) : m)];
    }

    // --- Loading ---------------------------------------------------------------------------------------------

    /// <summary>Asks the API again, e.g. after a failed load.</summary>
    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    protected override async Task ReloadAsync()
    {
        var groupId = Shell.SelectedGroupId;
        if (groupId.Length == 0) return; // the shell has no group (yet): there is nothing to ask for

        await LoadAsync(async ct =>
        {
            var belts = await _api.GetBeltsAsync(ct);
            var members = await _api.GetMembersAsync(groupId, ct);

            BeltOptions = [_noBelt, .. belts.Select(b => new BeltOption(b))];
            Members = [.. members.Select(m => new MemberRow(m, BeltOf(m.BeltId)))];
            HasLoaded = true;
        });

        if (IsShowingComments) await Comments.ReloadAsync();
    }

    /// <summary>The entry of a belt; the empty one for no belt, and for a belt this client does not know (it was just deleted).</summary>
    private BeltOption BeltOf(int? beltId) => BeltOptions.FirstOrDefault(o => o.Id is not null && o.Id == beltId) ?? _noBelt;

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(StartedAtProblem));
        OnPropertyChanged(nameof(BirthDateProblem));
        OnPropertyChanged(nameof(BeltSinceProblem));
        OnPropertyChanged(nameof(CanSetBeltSince));
        SaveCommand.NotifyCanExecuteChanged();
        BeginEditCommand.NotifyCanExecuteChanged();
        OpenCommentsCommand.NotifyCanExecuteChanged();
        CloseCommentsCommand.NotifyCanExecuteChanged();
        ClearStartedAtCommand.NotifyCanExecuteChanged();
        ClearBirthDateCommand.NotifyCanExecuteChanged();
        ClearBeltSinceCommand.NotifyCanExecuteChanged();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var belt in BeltOptions) belt.Refresh();
        foreach (var member in Members) member.Refresh();
        Comments.Refresh();
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(NoticeText));
        NotifyStateChanged(); // the problem is worded in the language
    }
}
