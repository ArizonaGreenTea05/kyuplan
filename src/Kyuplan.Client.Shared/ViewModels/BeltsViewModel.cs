using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// One line of the belt list: the belt plus ready-to-show texts. The texts follow the language, so the row re-reads them after
/// <see cref="Refresh"/>.
/// </summary>
/// <param name="prerequisites">The belts a member may come from, by name, with what applies when coming from each.</param>
public sealed class BeltRow(
    BeltDto belt, IReadOnlyList<(string Name, BeltPrerequisiteDto Requirement)> prerequisites, IReadOnlyList<string> requiredBy) : ObservableObject
{
    public BeltDto Belt => belt;

    public int Id => belt.Id;

    public string Name => belt.Name;

    public string PrimaryColor => belt.PrimaryColor;

    public string? SecondaryColor => belt.SecondaryColor;

    public bool HasSecondaryColor => belt.SecondaryColor is not null;

    public int MemberCount => belt.MemberCount;

    /// <summary>
    /// One line per belt of which a member must hold one, with what applies when coming from it: "White: min. age 8 y, waiting time
    /// 3 mo", or just "White" when nothing else is required from there.
    /// </summary>
    public string PrerequisitesText => prerequisites.Count == 0
        ? Strings.Belts_NoPrerequisite
        : string.Join("\n", prerequisites.Select(p => Describe(p.Name, p.Requirement)));

    private static string Describe(string name, BeltPrerequisiteDto requirement)
    {
        List<string> parts = [];
        if (requirement.MinAgeYears > 0) parts.Add(string.Format(Strings.Belts_RequirementAge, requirement.MinAgeYears));
        if (requirement.MinWaitMonths > 0) parts.Add(string.Format(Strings.Belts_RequirementWait, requirement.MinWaitMonths));
        return parts.Count == 0 ? name : $"{name}: {string.Join(", ", parts)}";
    }

    /// <summary>Why the belt cannot be deleted, or <c>null</c> when it can: the API refuses a belt that members hold or that others require.</summary>
    public string? DeleteBlockedText =>
        MemberCount > 0 ? string.Format(Strings.Belts_HeldBy, MemberCount)
        : requiredBy.Count > 0 ? string.Format(Strings.Belts_RequiredBy, string.Join(", ", requiredBy))
        : null;

    public bool CanDelete => DeleteBlockedText is null;

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// A belt that can be ticked as the previous belt of the belt being edited, with what applies when coming from it. The age and the
/// waiting time only count while the belt is ticked.
/// </summary>
public sealed partial class BeltPrerequisiteOption(int id, string name, string color) : ObservableObject
{
    public int Id { get; } = id;

    public string Name { get; } = name;

    public string Color { get; } = color;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The age in full years a member coming from this belt must have. 0 = no minimum.</summary>
    [ObservableProperty]
    private int _minAgeYears;

    /// <summary>The months that must have passed since the member got this belt. 0 = no waiting time.</summary>
    [ObservableProperty]
    private int _minWaitMonths;

    public string? MinAgeProblem =>
        BeltRules.CheckMinAgeYears(MinAgeYears) is not null ? string.Format(Strings.Belts_NumberInvalid, BeltRules.MaxMinAgeYears) : null;

    public string? MinWaitProblem =>
        BeltRules.CheckMinWaitMonths(MinWaitMonths) is not null ? string.Format(Strings.Belts_NumberInvalid, BeltRules.MaxMinWaitMonths) : null;

    /// <summary>Nothing is wrong with what is entered; what is not ticked is not looked at.</summary>
    public bool IsValid => !IsSelected || (MinAgeProblem is null && MinWaitProblem is null);

    partial void OnMinAgeYearsChanged(int value) => OnPropertyChanged(nameof(MinAgeProblem));

    partial void OnMinWaitMonthsChanged(int value) => OnPropertyChanged(nameof(MinWaitProblem));

    /// <summary>The problems are worded in the language: read them again after it changed.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(MinAgeProblem));
        OnPropertyChanged(nameof(MinWaitProblem));
    }
}

/// <summary>
/// The belts page: all belts with what each requires (the belt a member comes from and, depending on it, a minimum age and a
/// waiting time), with creating, editing and deleting them. A belt that members hold or that other belts require cannot be deleted,
/// so its delete button is off and says why. Used by both clients.
/// </summary>
public sealed partial class BeltsViewModel : PageViewModel
{
    /// <summary>Colours to start from; any other can be written as <c>#RRGGBB</c>.</summary>
    public static IReadOnlyList<string> ColorPresets { get; } =
        ["#FFFFFF", "#FFD800", "#FF8C00", "#00A650", "#1E64C8", "#7D3C98", "#7B4A1E", "#000000", "#D32F2F"];

    private readonly IPlannerApi _api;
    private Func<string>? _notice;

    public BeltsViewModel(ShellViewModel shell, IPlannerApi api) : base(shell)
    {
        _api = api;

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyStateChanged();
        };
    }

    public override string Title => Strings.Nav_Belts;

    protected override string? ViewPermission => Permission.BeltsView;

    /// <summary>Whether the user may create, change and delete belts; without it the page only shows them.</summary>
    public bool CanEdit => Shell.Can(Permission.BeltsEdit);

    protected override void OnPermissionsChanged()
    {
        base.OnPermissionsChanged();
        NotifyStateChanged();
    }

    /// <summary>The belts do not depend on the selected group or on Spond.</summary>
    protected override bool ReloadsWithContext => false;

    /// <summary>The member counts change when belts are handed out on the members page, so the list is read again whenever the page is shown.</summary>
    protected override bool ReloadsOnActivation => !IsFormOpen && !IsConfirmingDelete;

    /// <summary>All belts in the order of the progression: a belt comes after the belts it requires.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private IReadOnlyList<BeltRow> _belts = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasLoaded;

    /// <summary>The list was loaded and holds no belt: the page invites to create the first one.</summary>
    public bool IsEmpty => HasLoaded && Belts.Count == 0;

    /// <summary>What the page says when there is no belt: it invites to create the first one only somebody who may.</summary>
    public string EmptyText => CanEdit ? Strings.Belts_Empty : Strings.Belts_EmptyView;

    // --- Result of the last action ---------------------------------------------------------------------------

    /// <summary>What the last action achieved, e.g. <c>Belt "Orange" created.</c>. Reads its text again when the language changes.</summary>
    public string NoticeText => _notice?.Invoke() ?? "";

    public bool HasNotice => _notice is not null;

    private void SetNotice(Func<string>? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    // --- The form: a new belt, or an existing one ------------------------------------------------------------

    /// <summary>The form is open, for a new belt (<see cref="Editing"/> is <c>null</c>) or for <see cref="Editing"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreating), nameof(FormTitle))]
    private bool _isFormOpen;

    /// <summary>The belt the form changes; <c>null</c> when it creates one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreating), nameof(FormTitle))]
    private BeltRow? _editing;

    [ObservableProperty]
    private string _formName = "";

    /// <summary>The belt's colour as <c>#RRGGBB</c>.</summary>
    [ObservableProperty]
    private string _formPrimaryColor = "";

    /// <summary>The colour of the stripe as <c>#RRGGBB</c>; empty = no stripe.</summary>
    [ObservableProperty]
    private string _formSecondaryColor = "";

    /// <summary>
    /// The other belts, ticked where a member may come from them, each with the minimum age and waiting time that apply then. Belts
    /// that require the edited belt are not offered: that would be a circle.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<BeltPrerequisiteOption> _prerequisiteOptions = [];

    public bool IsCreating => IsFormOpen && Editing is null;

    public string FormTitle => Editing is { } belt ? string.Format(Strings.Belts_FormEdit, belt.Name) : Strings.Belts_FormNew;

    public string? NameProblem
    {
        get
        {
            if (!IsFormOpen) return null;

            var name = FormName.Trim();
            if (BeltRules.CheckName(name) is not null)
                return FormName.Length > 0 || Editing is not null ? string.Format(Strings.Belts_NameInvalid, BeltRules.MaxNameLength) : null;

            return Belts.Any(b => b.Id != Editing?.Id && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                ? Strings.Belts_NameTaken
                : null;
        }
    }

    public string? PrimaryColorProblem =>
        IsFormOpen && !BeltRules.IsColor(FormPrimaryColor.Trim()) ? Strings.Belts_ColorInvalid : null;

    public string? SecondaryColorProblem =>
        IsFormOpen && BeltRules.CheckSecondaryColor(FormSecondaryColor) is not null ? Strings.Belts_ColorInvalid : null;

    /// <summary>The colour of the belt as typed, ready for a swatch: <c>#RRGGBB</c>, or empty while what is typed is not a colour.</summary>
    public string PrimaryPreview => BeltRules.IsColor(FormPrimaryColor.Trim()) ? FormPrimaryColor.Trim() : "";

    /// <summary>The colour of the stripe as typed, ready for a swatch; empty when there is none or it is not a colour yet.</summary>
    public string SecondaryPreview => BeltRules.IsColor(FormSecondaryColor.Trim()) ? FormSecondaryColor.Trim() : "";

    /// <summary>The form holds a belt the API accepts and, for an existing belt, one that differs from what is there.</summary>
    public bool CanSave =>
        CanEdit && IsFormOpen && !IsBusy && ValidInput() is { } input && (Editing is null || !Same(Editing.Belt, input));

    private BeltInputDto? ValidInput() =>
        NameProblem is null && BeltRules.CheckName(FormName) is null
        && PrimaryColorProblem is null && SecondaryColorProblem is null && PrerequisiteOptions.All(o => o.IsValid)
            ? new BeltInputDto(
                FormName.Trim(), FormPrimaryColor.Trim().ToUpperInvariant(),
                string.IsNullOrWhiteSpace(FormSecondaryColor) ? null : FormSecondaryColor.Trim().ToUpperInvariant(),
                [.. PrerequisiteOptions.Where(o => o.IsSelected).Select(o => new BeltPrerequisiteDto(o.Id, o.MinAgeYears, o.MinWaitMonths)).OrderBy(p => p.BeltId)])
            : null;

    private static bool Same(BeltDto belt, BeltInputDto input) =>
        belt.Name == input.Name && belt.PrimaryColor == input.PrimaryColor && belt.SecondaryColor == input.SecondaryColor
        && belt.Prerequisites.OrderBy(p => p.BeltId).SequenceEqual(input.Prerequisites);

    private bool CanBeginCreate() => CanEdit && !IsCreating;

    [RelayCommand(CanExecute = nameof(CanBeginCreate))]
    private void BeginCreate()
    {
        if (CanBeginCreate()) OpenForm(null); // Execute does not ask CanExecute: what the user may not do is not done
    }

    private bool CanBeginEdit(BeltRow? belt) => CanEdit && belt is not null && !ReferenceEquals(belt, Editing);

    [RelayCommand(CanExecute = nameof(CanBeginEdit))]
    private void BeginEdit(BeltRow belt)
    {
        if (CanBeginEdit(belt)) OpenForm(belt);
    }

    private void OpenForm(BeltRow? belt)
    {
        ErrorMessage = null;
        SetNotice(null);
        PendingDelete = null;

        // A belt that (through others) requires this one cannot be required by it.
        var dependents = belt is null ? [] : Dependents(belt.Id);
        var options = Belts
            .Where(b => b.Id != belt?.Id && !dependents.Contains(b.Id))
            .Select(b =>
            {
                var current = belt?.Belt.Prerequisites.FirstOrDefault(p => p.BeltId == b.Id);
                return new BeltPrerequisiteOption(b.Id, b.Name, b.PrimaryColor)
                {
                    IsSelected = current is not null,
                    MinAgeYears = current?.MinAgeYears ?? 0,
                    MinWaitMonths = current?.MinWaitMonths ?? 0,
                };
            })
            .ToList();
        foreach (var option in options)
        {
            option.PropertyChanged += (_, e) =>
            {
                // Only what the user changes is an edit; the problems being worded again after a language change is not.
                if (e.PropertyName is nameof(BeltPrerequisiteOption.IsSelected) or nameof(BeltPrerequisiteOption.MinAgeYears) or nameof(BeltPrerequisiteOption.MinWaitMonths))
                    FormEdited();
            };
        }

        Editing = belt;
        FormName = belt?.Name ?? "";
        FormPrimaryColor = belt?.PrimaryColor ?? ColorPresets[0];
        FormSecondaryColor = belt?.SecondaryColor ?? "";
        PrerequisiteOptions = options;
        IsFormOpen = true;
        NotifyStateChanged();
    }

    /// <summary>The ids of the belts that require <paramref name="beltId"/>, directly or through other belts.</summary>
    private HashSet<int> Dependents(int beltId)
    {
        var found = new HashSet<int>();
        var pending = new Queue<int>([beltId]);
        while (pending.TryDequeue(out var id))
        {
            foreach (var belt in Belts.Where(b => b.Belt.Prerequisites.Any(p => p.BeltId == id) && found.Add(b.Id)))
                pending.Enqueue(belt.Id);
        }

        return found;
    }

    [RelayCommand]
    private void CancelForm()
    {
        IsFormOpen = false;
        Editing = null;
        ErrorMessage = null;
        NotifyStateChanged();
    }

    // --- The colours -----------------------------------------------------------------------------------------

    private bool CanPickPrimary(string? color) => !Matches(FormPrimaryColor, color);

    [RelayCommand(CanExecute = nameof(CanPickPrimary))]
    private void PickPrimaryColor(string color) => FormPrimaryColor = color;

    private bool CanPickSecondary(string? color) => !Matches(FormSecondaryColor, color);

    [RelayCommand(CanExecute = nameof(CanPickSecondary))]
    private void PickSecondaryColor(string color) => FormSecondaryColor = color;

    private bool CanClearSecondary() => FormSecondaryColor.Length > 0;

    [RelayCommand(CanExecute = nameof(CanClearSecondary))]
    private void ClearSecondaryColor() => FormSecondaryColor = "";

    /// <summary>Whether the colour typed is this one (in any case); for marking the preset that is in use.</summary>
    public static bool Matches(string typed, string? color) => string.Equals(typed.Trim(), color, StringComparison.OrdinalIgnoreCase);

    // --- Saving ----------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave || ValidInput() is not { } input) return; // a command is only guarded by its button; a form can also be submitted with Enter

        var editing = Editing;
        BeltDto? saved = null;
        await LoadAsync(async ct =>
        {
            try
            {
                saved = editing is null ? await _api.CreateBeltAsync(input, ct) : await _api.UpdateBeltAsync(editing.Id, input, ct);
            }
            catch (PlannerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                throw new PlannerApiException(Strings.Belts_NameTaken, ex.StatusCode, ex);
            }

            Belts = Rows(await _api.GetBeltsAsync(ct));
        });

        if (saved is null) return; // the reason is in ErrorMessage; the form stays open so the user can correct it

        // It is saved, even if showing the new list failed: the form must not be left open to save it a second time.
        IsFormOpen = false;
        Editing = null;

        var name = saved.Name;
        var wasCreated = editing is null;
        SetNotice(() => string.Format(wasCreated ? Strings.Belts_Created : Strings.Belts_Saved, name));
        NotifyStateChanged();
    }

    partial void OnFormNameChanged(string value) => FormEdited();

    partial void OnFormPrimaryColorChanged(string value) => FormEdited();

    partial void OnFormSecondaryColorChanged(string value) => FormEdited();

    /// <summary>The user is correcting the form: what the API said about the last attempt is old news.</summary>
    private void FormEdited()
    {
        if (IsFormOpen) ErrorMessage = null;
        NotifyStateChanged();
    }

    // --- Deleting --------------------------------------------------------------------------------------------

    /// <summary>The belt waiting for the confirmation that deletes it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingDelete), nameof(ConfirmText))]
    private BeltRow? _pendingDelete;

    public bool IsConfirmingDelete => PendingDelete is not null;

    public string ConfirmText => PendingDelete is { } belt ? string.Format(Strings.Belts_ConfirmDelete, belt.Name) : "";

    /// <summary>A belt can be deleted unless members hold it or other belts require it (see <see cref="BeltRow.DeleteBlockedText"/>).</summary>
    public bool CanDelete(BeltRow? belt) => CanEdit && belt is { CanDelete: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void AskDelete(BeltRow belt)
    {
        if (!CanDelete(belt)) return;

        ErrorMessage = null;
        SetNotice(null);
        PendingDelete = belt;
    }

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } belt) return;

        PendingDelete = null;
        var deleted = false;
        await LoadAsync(async ct =>
        {
            await _api.DeleteBeltAsync(belt.Id, ct);
            deleted = true;
            Belts = Rows(await _api.GetBeltsAsync(ct));
        });

        // A form for the deleted belt has nothing left to change.
        if (deleted && Editing?.Id == belt.Id) CancelForm();
        if (deleted) SetNotice(() => string.Format(Strings.Belts_Deleted, belt.Name));
    }

    // --- Loading ---------------------------------------------------------------------------------------------

    /// <summary>Asks the API again, e.g. after a failed load.</summary>
    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    protected override async Task ReloadAsync()
    {
        await LoadAsync(async ct =>
        {
            Belts = Rows(await _api.GetBeltsAsync(ct));
            HasLoaded = true;
        });
    }

    private static IReadOnlyList<BeltRow> Rows(IReadOnlyList<BeltDto> belts) =>
        [.. belts.Select(b => new BeltRow(
            b,
            [.. belts.Join(b.Prerequisites, o => o.Id, p => p.BeltId, (o, p) => (o.Name, Requirement: p))], // in the order of the list
            [.. belts.Where(o => o.Prerequisites.Any(p => p.BeltId == b.Id)).Select(o => o.Name)]))];

    partial void OnBeltsChanged(IReadOnlyList<BeltRow> value) => NotifyStateChanged();

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(NameProblem));
        OnPropertyChanged(nameof(PrimaryColorProblem));
        OnPropertyChanged(nameof(SecondaryColorProblem));
        OnPropertyChanged(nameof(PrimaryPreview));
        OnPropertyChanged(nameof(SecondaryPreview));
        SaveCommand.NotifyCanExecuteChanged();
        BeginCreateCommand.NotifyCanExecuteChanged();
        BeginEditCommand.NotifyCanExecuteChanged();
        AskDeleteCommand.NotifyCanExecuteChanged();
        PickPrimaryColorCommand.NotifyCanExecuteChanged();
        PickSecondaryColorCommand.NotifyCanExecuteChanged();
        ClearSecondaryColorCommand.NotifyCanExecuteChanged();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var belt in Belts) belt.Refresh();
        foreach (var option in PrerequisiteOptions) option.Refresh();
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(EmptyText));
        NotifyStateChanged(); // the problems are worded in the language
    }
}
