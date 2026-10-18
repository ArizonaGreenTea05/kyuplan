using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>One image slot as the branding page lists it.</summary>
/// <param name="Slot">One of <see cref="BrandingSlots"/>.</param>
/// <param name="PreviewUrl">
/// What the slot will show: its saved image (absolute, changes with every change so that a browser shows the new one), a <c>data:</c> address
/// for an image that was picked but is not saved yet, or <c>null</c> when it has none (or is to lose it).
/// </param>
/// <param name="OnDark">The slot is for dark surfaces: its preview is shown on a dark background.</param>
/// <param name="IsPending">The slot was changed (an image picked or taken away) and the change is not saved yet.</param>
public sealed record BrandingImageRow(string Slot, string Title, string Hint, string EmptyText, string? PreviewUrl, bool OnDark, string Accept, bool IsPending = false)
{
    public bool HasImage => PreviewUrl is not null;
}

/// <summary>One theme pack as the branding page lists it.</summary>
/// <param name="Details">"dark · high contrast · built in".</param>
public sealed record ThemePackRow(string Id, string Name, string Details, bool BuiltIn);

/// <summary>
/// The administrators' page for the branding: the name, the organisation, the accent colour and the five images that both clients show.
/// The texts and the colour are saved together; the images are picked or taken away first (the page shows how they will look) and
/// saved together with a button of their own; one more button goes
/// back to what the API's <c>Branding</c> settings say. The API only answers administrators, so the page does not even ask anybody
/// else. Every change is shown by this client at once (it asks <see cref="BrandingViewModel"/> again); the other clients follow
/// within <see cref="BrandingViewModel.DefaultWatchInterval"/>. Used by both clients.
/// </summary>
public sealed partial class BrandingAdminViewModel : PageViewModel
{
    /// <summary>Colours to start from; any other can be written as <c>#RRGGBB</c>. (The browser has a colour picker of its own and does not use them.)</summary>
    public static IReadOnlyList<string> AccentPresets { get; } =
        ["#2563EB", "#0EA5E9", "#14B8A6", "#16A34A", "#84CC16", "#EAB308", "#F97316", "#DC2626", "#DB2777", "#9333EA", "#4F46E5", "#475569"];

    private readonly IPlannerApi _api;
    private readonly Uri? _baseAddress;
    private Func<string>? _notice;

    /// <summary>The branding as the API last said it. What the form is compared with.</summary>
    private BrandingSettingsDto? _settings;

    /// <summary>Counts what was loaded or saved, to give every preview a new address: the image of a slot keeps its address when it changes.</summary>
    private int _revision;

    /// <summary>What was done with an image slot and is not saved yet.</summary>
    /// <param name="Content">The picked file; <c>null</c> when the image is to be taken away.</param>
    /// <param name="PreviewUrl">The picked file as a <c>data:</c> address, for the slot to show it; <c>null</c> with <paramref name="Content"/>.</param>
    private sealed record StagedImage(byte[]? Content, string? PreviewUrl);

    /// <summary>The slots that were changed and not saved yet, with what was done to them. Dropped when the page loads, resets or loses its administrator.</summary>
    private readonly Dictionary<string, StagedImage> _staged = [];

    /// <param name="options">Holds the API's address; the urls of the previews are made absolute against it. Without it they stay relative.</param>
    public BrandingAdminViewModel(ShellViewModel shell, IPlannerApi api, IOptions<PlannerClientOptions>? options = null) : base(shell)
    {
        _api = api;
        _baseAddress = options?.Value.BaseUri;

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyStateChanged();
        };
    }

    protected override string? ViewPermission => Permission.BrandingView;

    /// <summary>
    /// The signed-in user may no longer see the page (or somebody else signed in): nothing of what was loaded or typed is kept. A user who
    /// may see it reads it again (the base class takes care of that).
    /// </summary>
    protected override void OnPermissionsChanged()
    {
        if (!CanView) Reset();

        base.OnPermissionsChanged();
        NotifyStateChanged();
    }

    public override string Title => Strings.Nav_Branding;

    /// <summary>The branding does not depend on the selected group or the Spond data, and what was typed must survive a refresh.</summary>
    protected override bool ReloadsWithContext => false;

    /// <summary>Whether the signed-in user may change the branding; without it the page only shows it.</summary>
    public bool CanEdit => Shell.Can(Permission.BrandingEdit);

    /// <summary>The API has answered: there is something to edit.</summary>
    public bool IsLoaded => _settings is not null;

    /// <summary>An administrator saved something; everything else is what the API's settings say.</summary>
    public bool IsCustomized => _settings?.IsCustomized == true;

    // --- Result of the last action ---------------------------------------------------------------------------

    /// <summary>What the last action achieved, e.g. <c>Saved.</c>. Reads its text again when the language changes.</summary>
    public string NoticeText => _notice?.Invoke() ?? "";

    public bool HasNotice => _notice is not null;

    private void SetNotice(Func<string>? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    // --- The texts and the colour ----------------------------------------------------------------------------

    [ObservableProperty]
    private string _formProductName = "";

    [ObservableProperty]
    private string _formOrganizationName = "";

    [ObservableProperty]
    private string _formAccentColor = "";

    /// <summary>The theme the web client shows to users who picked none (a <see cref="ThemeRules"/> id).</summary>
    [ObservableProperty]
    private string _formDefaultWebTheme = ThemeIds.Default;

    /// <summary>The same for the Windows client.</summary>
    [ObservableProperty]
    private string _formDefaultWindowsTheme = ThemeIds.Default;

    /// <summary>The themes an administrator can make the default of the web client (the installed theme packs included).</summary>
    public IReadOnlyList<ThemeChoice> WebThemes => ThemeViewModel.ChoicesFor(ThemeClients.Web, _packs);

    /// <summary>The themes an administrator can make the default of the Windows client.</summary>
    public IReadOnlyList<ThemeChoice> WindowsThemes => ThemeViewModel.ChoicesFor(ThemeClients.Windows, _packs);

    // --- Theme packs ---------------------------------------------------------------------------------------

    /// <summary>The installed theme packs as the API last said (the built-in ones until it has).</summary>
    private IReadOnlyList<ThemePackDto> _packs = BuiltInThemePacks.All;

    /// <summary>The installed packs, for the list: built-in first (as the API sends them), then the uploaded ones.</summary>
    public IReadOnlyList<ThemePackRow> ThemePacks => [.. _packs.Select(p => new ThemePackRow(p.Id, ThemeViewModel.PackName(p), Describe(p), p.BuiltIn))];

    /// <summary>Whether the signed-in user may install and delete theme packs.</summary>
    public bool CanManageThemes => Shell.Can(Permission.ThemesEdit);

    /// <summary>Packs can be installed once the page is loaded, and while nothing else is being saved.</summary>
    public bool CanInstallTheme => CanManageThemes && !IsBusy && IsLoaded;

    /// <summary>An uploaded pack can be deleted; a built-in one cannot, so there the button would change nothing.</summary>
    public bool CanDeleteTheme(ThemePackRow? row) => CanManageThemes && !IsBusy && row is { BuiltIn: false };

    private static string Describe(ThemePackDto pack)
    {
        var parts = new List<string> { pack.Scheme == "dark" ? Strings.Themes_Dark : Strings.Themes_Light };
        if (pack.HighContrast) parts.Add(Strings.Themes_HighContrast);
        if (pack.BuiltIn) parts.Add(Strings.Themes_BuiltIn);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Installs the theme pack in a file the user picked (its text). A file that is too large is refused here without being parsed; for the rest the
    /// API is the judge and says what is wrong (a missing colour, too little contrast, ...).
    /// </summary>
    /// <param name="read">Reads the file's text; called only when the file is not too large.</param>
    public async Task InstallThemeAsync(long size, Func<Task<string>> read)
    {
        if (!CanInstallTheme) return;

        ErrorMessage = null;
        SetNotice(null);
        if (size > ThemePackRules.MaxBytes)
        {
            ErrorMessage = string.Format(Strings.Themes_TooLarge, ThemePackRules.MaxBytes / 1024);
            return;
        }

        string json;
        try
        {
            json = await read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = string.Format(Strings.Error_Unexpected, ex.Message);
            return;
        }

        if (json.Length > ThemePackRules.MaxBytes) // the size a file announced is not believed
        {
            ErrorMessage = string.Format(Strings.Themes_TooLarge, ThemePackRules.MaxBytes / 1024);
            return;
        }

        var before = _packs.Select(p => p.Id).ToHashSet();
        ThemeCatalogDto? catalog = null;
        await LoadAsync(async ct => catalog = await _api.InstallThemeAsync(json, ct));
        if (catalog is null) return; // the reason is in ErrorMessage

        var installed = catalog.Packs.FirstOrDefault(p => !before.Contains(p.Id)) ?? catalog.Packs.LastOrDefault(p => !p.BuiltIn);
        AdoptPacks(catalog);
        SetNotice(() => string.Format(Strings.Themes_Installed, installed is null ? "" : ThemeViewModel.PackName(installed)));
        await ShowChangeAsync();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteTheme))]
    private async Task DeleteThemeAsync(ThemePackRow row)
    {
        if (!CanDeleteTheme(row)) return;

        ErrorMessage = null;
        SetNotice(null);
        ThemeCatalogDto? catalog = null;
        await LoadAsync(async ct => catalog = await _api.DeleteThemeAsync(row.Id, ct));
        if (catalog is null) return;

        AdoptPacks(catalog);
        SetNotice(() => string.Format(Strings.Themes_Deleted, row.Name));
        await ShowChangeAsync();
    }

    private void AdoptPacks(ThemeCatalogDto catalog)
    {
        _packs = catalog.Packs;

        // A default that was an uploaded pack and is gone must not stay selected in the form: the list no longer has it.
        if (!WebThemes.Any(c => c.Id == FormDefaultWebTheme)) FormDefaultWebTheme = ThemeIds.Default;
        if (!WindowsThemes.Any(c => c.Id == FormDefaultWindowsTheme)) FormDefaultWindowsTheme = ThemeIds.Default;

        OnPropertyChanged(nameof(ThemePacks));
        OnPropertyChanged(nameof(WebThemes));
        OnPropertyChanged(nameof(WindowsThemes));
        OnPropertyChanged(nameof(SelectedWebTheme));
        OnPropertyChanged(nameof(SelectedWindowsTheme));
        NotifyStateChanged();
    }

    /// <summary>The list entries as bound by a control that picks an object (the desktop client).</summary>
    public ThemeChoice? SelectedWebTheme
    {
        get => WebThemes.FirstOrDefault(c => c.Id == FormDefaultWebTheme);
        set { if (value is not null) FormDefaultWebTheme = value.Id; }
    }

    public ThemeChoice? SelectedWindowsTheme
    {
        get => WindowsThemes.FirstOrDefault(c => c.Id == FormDefaultWindowsTheme);
        set { if (value is not null) FormDefaultWindowsTheme = value.Id; }
    }

    public string? ProductNameProblem =>
        IsLoaded && BrandingRules.CheckProductName(FormProductName) is not null
            ? string.Format(Strings.Branding_ProductNameInvalid, BrandingRules.MaxProductNameLength)
            : null;

    public string? OrganizationNameProblem =>
        BrandingRules.CheckOrganizationName(FormOrganizationName) is not null
            ? string.Format(Strings.Branding_OrganizationNameInvalid, BrandingRules.MaxOrganizationNameLength)
            : null;

    public string? AccentColorProblem =>
        IsLoaded && !BrandingRules.IsAccentColor(FormAccentColor) ? Strings.Branding_AccentColorInvalid : null;

    /// <summary>
    /// The value for a colour picker, which only knows <c>#rrggbb</c>: the typed colour spelled out, or the saved one while what is typed is no colour.
    /// </summary>
    public string AccentPickerValue
    {
        get
        {
            var color = BrandingRules.IsAccentColor(FormAccentColor) ? FormAccentColor.Trim() : _settings?.AccentColor ?? BrandingRules.DefaultAccentColor;
            if (color.Length == 4) color = $"#{color[1]}{color[1]}{color[2]}{color[2]}{color[3]}{color[3]}";
            return color.ToLowerInvariant();
        }
    }

    /// <summary>Takes a colour of <see cref="AccentPresets"/> (or any other) into the form. Off while the form already holds it.</summary>
    private bool CanPickAccentColor(string? color) =>
        CanEdit && !string.Equals(FormAccentColor.Trim(), color?.Trim(), StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanPickAccentColor))]
    private void PickAccentColor(string color) => FormAccentColor = color;

    /// <summary>The form holds something the API accepts and that differs from what is saved.</summary>
    public bool CanSave =>
        CanEdit && !IsBusy && _settings is { } saved
        && ProductNameProblem is null && OrganizationNameProblem is null && AccentColorProblem is null
        && (FormProductName.Trim() != saved.ProductName
            || FormOrganizationName.Trim() != saved.OrganizationName
            || !string.Equals(FormAccentColor.Trim(), saved.AccentColor, StringComparison.OrdinalIgnoreCase)
            || FormDefaultWebTheme != saved.DefaultWebTheme
            || FormDefaultWindowsTheme != saved.DefaultWindowsTheme);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return; // a command is only guarded by its button; a form can also be submitted with Enter

        var input = new BrandingInputDto(FormProductName.Trim(), FormOrganizationName.Trim(), FormAccentColor.Trim(), FormDefaultWebTheme, FormDefaultWindowsTheme);
        BrandingSettingsDto? saved = null;
        await LoadAsync(async ct => saved = await _api.SaveBrandingAsync(input, ct));

        if (saved is null) return; // the reason is in ErrorMessage; what was typed stays

        Adopt(saved, refillForm: true);
        SetNotice(() => Strings.Branding_Saved);
        await ShowChangeAsync();
    }

    partial void OnFormProductNameChanged(string value) => FormEdited();

    partial void OnFormOrganizationNameChanged(string value) => FormEdited();

    partial void OnFormAccentColorChanged(string value) => FormEdited();

    partial void OnFormDefaultWebThemeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedWebTheme));
        FormEdited();
    }

    partial void OnFormDefaultWindowsThemeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedWindowsTheme));
        FormEdited();
    }

    /// <summary>The user is correcting the form: what the API said about the last attempt is old news.</summary>
    private void FormEdited()
    {
        if (IsLoaded) ErrorMessage = null;
        NotifyStateChanged();
    }

    // --- The images ----------------------------------------------------------------------------------------

    /// <summary>The five slots, in the order of <see cref="BrandingSlots.All"/>.</summary>
    [ObservableProperty]
    private IReadOnlyList<BrandingImageRow> _images = [];

    /// <summary>"Up to 1024 KB each; … Nothing changes until the images are saved."</summary>
    public string ImagesLede => string.Format(Strings.Branding_ImagesLede, BrandingRules.MaxImageBytes / 1024);

    /// <summary>Images can be picked once the branding is loaded, and while nothing else is being saved.</summary>
    public bool CanUpload => CanEdit && !IsBusy && IsLoaded;

    /// <summary>A slot's image can be taken away while the slot shows one: otherwise pressing the button would change nothing.</summary>
    public bool CanRemove(BrandingImageRow? row) => CanEdit && !IsBusy && row is { HasImage: true };

    /// <summary>A slot's change can be dropped while it has one: the slot shows its saved image again.</summary>
    public bool CanUndo(BrandingImageRow? row) => CanEdit && !IsBusy && row is { IsPending: true };

    /// <summary>Some slot was changed and the change is not saved yet.</summary>
    public bool CanSaveImages => CanEdit && !IsBusy && IsLoaded && _staged.Count > 0;

    /// <summary>
    /// Takes the file a user picked for a slot; <see cref="SaveImagesCommand"/> sends it to the API. A file that is too large is refused here,
    /// without reading it, and so is one that is none of the formats the slot takes (the API would refuse it later, with nothing to say
    /// which of the slots it was).
    /// </summary>
    /// <param name="open">Opens the file's content; called only when the file is not too large.</param>
    public async Task PickImageAsync(string slot, long size, Func<Stream> open)
    {
        if (IsBusy || !IsLoaded) return;

        ErrorMessage = null;
        SetNotice(null);
        if (size > BrandingRules.MaxImageBytes)
        {
            ErrorMessage = string.Format(Strings.Branding_ImageTooLarge, BrandingRules.MaxImageBytes / 1024);
            return;
        }

        byte[] content;
        try
        {
            await using var stream = open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            content = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = string.Format(Strings.Error_Unexpected, ex.Message);
            return;
        }

        if (content.Length > BrandingRules.MaxImageBytes) // the size a file announced is not believed
        {
            ErrorMessage = string.Format(Strings.Branding_ImageTooLarge, BrandingRules.MaxImageBytes / 1024);
            return;
        }

        var contentType = BrandingImageFormats.Detect(content);
        var accepted = BrandingSlots.ContentTypes(slot) ?? [];
        if (contentType is null || !accepted.Contains(contentType))
        {
            ErrorMessage = string.Format(Strings.Branding_ImageNotAccepted, SlotTitle(slot), string.Join(", ", accepted.Select(BrandingImageFormats.Name)));
            return;
        }

        _staged[slot] = new StagedImage(content, $"data:{contentType};base64,{Convert.ToBase64String(content)}");
        StagedChanged();
    }

    /// <summary>Takes the slot's image away: where nothing is saved there is nothing to take, so a picked file is only dropped.</summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveImage(BrandingImageRow row)
    {
        if (!CanRemove(row)) return;

        ErrorMessage = null;
        SetNotice(null);
        if (_settings?.Images.Any(image => image.Slot == row.Slot && image.Url is not null) == true) _staged[row.Slot] = new StagedImage(null, null);
        else _staged.Remove(row.Slot);
        StagedChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void UndoImage(BrandingImageRow row)
    {
        if (!CanUndo(row)) return;

        ErrorMessage = null;
        SetNotice(null);
        _staged.Remove(row.Slot);
        StagedChanged();
    }

    private void StagedChanged()
    {
        Images = BuildRows();
        NotifyStateChanged();
    }

    /// <summary>
    /// Sends the picked images and the removals to the API, slot by slot. When one fails the ones before it are saved (and shown as
    /// such), it and the ones after it stay as they are, and the error says why.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveImages))]
    private async Task SaveImagesAsync()
    {
        if (!CanSaveImages) return;

        SetNotice(null);
        BrandingSettingsDto? saved = null;
        await LoadAsync(async ct =>
        {
            foreach (var slot in BrandingSlots.All.Where(_staged.ContainsKey).ToList())
            {
                if (_staged[slot].Content is { } content)
                {
                    await using var stream = new MemoryStream(content);
                    saved = await _api.UploadBrandingImageAsync(slot, stream, ct);
                }
                else
                {
                    saved = await _api.RemoveBrandingImageAsync(slot, ct);
                }

                _staged.Remove(slot);
            }
        });

        if (saved is null) return; // the first one failed: the reason is in ErrorMessage, and everything stays picked

        Adopt(saved, refillForm: false);
        if (_staged.Count == 0) SetNotice(() => Strings.Branding_ImagesSaved);
        await ShowChangeAsync();
    }

    // --- Back to the API's settings ------------------------------------------------------------------------

    /// <summary>There is something saved to forget.</summary>
    public bool CanReset => CanEdit && !IsBusy && IsCustomized;

    /// <summary>Waiting for the confirmation that forgets everything saved: the uploaded images cannot be got back.</summary>
    [ObservableProperty]
    private bool _isConfirmingReset;

    /// <summary>There is something to forget, and the question is not already open.</summary>
    private bool CanAskReset => CanReset && !IsConfirmingReset;

    partial void OnIsConfirmingResetChanged(bool value) => AskResetCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanAskReset))]
    private void AskReset()
    {
        if (!CanReset) return;

        ErrorMessage = null;
        SetNotice(null);
        IsConfirmingReset = true;
    }

    [RelayCommand]
    private void CancelReset() => IsConfirmingReset = false;

    [RelayCommand]
    private async Task ConfirmResetAsync()
    {
        if (!IsConfirmingReset) return;

        IsConfirmingReset = false;
        BrandingSettingsDto? saved = null;
        await LoadAsync(async ct => saved = await _api.ResetBrandingAsync(ct));

        if (saved is null) return;

        _staged.Clear(); // the page shows what the API says now, the pictures that were only picked included
        Adopt(saved, refillForm: true);
        SetNotice(() => Strings.Branding_ResetDone);
        await ShowChangeAsync();
    }

    /// <summary>
    /// The branding this client shows is the one of the API, not of this page: ask for it again now that it changed, instead of waiting
    /// for the next regular look (<see cref="BrandingViewModel.Watch"/>). Never fails; the change is saved either way.
    /// </summary>
    private Task ShowChangeAsync() => Shell.Brand.RefreshAsync();

    // --- Loading -------------------------------------------------------------------------------------------

    /// <summary>Asks the API again, e.g. after a failed load.</summary>
    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    protected override async Task ReloadAsync()
    {
        await LoadAsync(async ct =>
        {
            var settings = await _api.GetBrandingSettingsAsync(ct);
            _packs = (await _api.GetThemesAsync(ct)).Packs; // before the form is refilled: the defaults it holds may be packs
            _staged.Clear(); // like the form, the pictures picked meanwhile give way to what the API says
            Adopt(settings, refillForm: true);
            OnPropertyChanged(nameof(ThemePacks));
            OnPropertyChanged(nameof(WebThemes));
            OnPropertyChanged(nameof(WindowsThemes));
            OnPropertyChanged(nameof(SelectedWebTheme));
            OnPropertyChanged(nameof(SelectedWindowsTheme));
        });
    }

    /// <summary>
    /// Takes over what the API says. The form is refilled when the answer is about what it holds (the texts were saved, the branding was
    /// reset, the page was loaded); an image that was uploaded does not discard what was typed meanwhile.
    /// </summary>
    private void Adopt(BrandingSettingsDto settings, bool refillForm)
    {
        _settings = settings;
        _revision++;
        Images = BuildRows();

        if (refillForm)
        {
            FormProductName = settings.ProductName;
            FormOrganizationName = settings.OrganizationName;
            FormAccentColor = settings.AccentColor;
            FormDefaultWebTheme = settings.DefaultWebTheme;
            FormDefaultWindowsTheme = settings.DefaultWindowsTheme;
        }

        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsCustomized));
        NotifyStateChanged();
    }

    private IReadOnlyList<BrandingImageRow> BuildRows() =>
        _settings is null
            ? []
            : [.. _settings.Images.Select(image =>
            {
                var staged = _staged.GetValueOrDefault(image.Slot);
                var url = staged is not null ? staged.PreviewUrl
                    : image.Url is null ? null
                    : $"{Absolute(image.Url)}?v={_revision}";
                return new BrandingImageRow(
                    image.Slot, SlotTitle(image.Slot), SlotHint(image.Slot), SlotEmpty(image.Slot), url,
                    OnDark: image.Slot is BrandingSlots.LogoOnDark,
                    Accept: string.Join(',', BrandingSlots.ContentTypes(image.Slot) ?? []),
                    IsPending: staged is not null);
            })];

    private string Absolute(string url) => _baseAddress is null ? url : new Uri(_baseAddress, url).AbsoluteUri;

    /// <summary>Nothing of a branding the signed-in user may no longer see is kept.</summary>
    private void Reset()
    {
        _settings = null;
        _staged.Clear();
        Images = [];
        FormProductName = FormOrganizationName = FormAccentColor = "";
        FormDefaultWebTheme = FormDefaultWindowsTheme = ThemeIds.Default;
        IsConfirmingReset = false;
        SetNotice(null);
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsCustomized));
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanReset));
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(CanSaveImages));
        OnPropertyChanged(nameof(CanManageThemes));
        OnPropertyChanged(nameof(CanInstallTheme));
        DeleteThemeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ProductNameProblem));
        OnPropertyChanged(nameof(OrganizationNameProblem));
        OnPropertyChanged(nameof(AccentColorProblem));
        OnPropertyChanged(nameof(AccentPickerValue));
        SaveCommand.NotifyCanExecuteChanged();
        SaveImagesCommand.NotifyCanExecuteChanged();
        PickAccentColorCommand.NotifyCanExecuteChanged();
        AskResetCommand.NotifyCanExecuteChanged();
        RemoveImageCommand.NotifyCanExecuteChanged();
        UndoImageCommand.NotifyCanExecuteChanged();
    }

    // --- Texts of the slots --------------------------------------------------------------------------------

    private static string SlotTitle(string slot) => slot switch
    {
        BrandingSlots.Logo => Strings.Branding_SlotLogo,
        BrandingSlots.LogoOnDark => Strings.Branding_SlotLogoOnDark,
        _ => Strings.Branding_SlotIcon,
    };

    private static string SlotHint(string slot) => slot switch
    {
        BrandingSlots.Logo => Strings.Branding_HintLogo,
        BrandingSlots.LogoOnDark => Strings.Branding_HintLogoOnDark,
        _ => Strings.Branding_HintIcon,
    };

    /// <summary>What the clients show when the slot has no image of its own.</summary>
    private static string SlotEmpty(string slot) => slot switch
    {
        BrandingSlots.Logo => Strings.Branding_EmptyLogo,
        BrandingSlots.LogoOnDark => Strings.Branding_EmptyLogoOnDark,
        _ => Strings.Branding_EmptyIcon,
    };

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Images = BuildRows(); // the texts of the slots
        OnPropertyChanged(nameof(ImagesLede));
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(WebThemes)); // the names of the themes
        OnPropertyChanged(nameof(WindowsThemes));
        OnPropertyChanged(nameof(SelectedWebTheme));
        OnPropertyChanged(nameof(SelectedWindowsTheme));
        NotifyStateChanged(); // the problems
    }
}
