using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>One entry of the theme list. <see cref="Id"/> is a theme of <see cref="ThemeRules"/> or a theme pack, or empty for "whatever the server says".</summary>
public sealed record ThemeChoice(string Id, string Name);

/// <summary>
/// Which theme the client shows: the one the signed-in user chose for this kind of client (<see cref="PlannerClientOptions.ThemeClient"/>),
/// else the one the administrators set as the default (<see cref="BrandingDto.DefaultWebTheme"/> / <see cref="BrandingDto.DefaultWindowsTheme"/>).
/// The choice is kept with the account, so it follows the user to another computer. Besides the themes each client draws itself there are
/// theme packs (<see cref="ThemePackDto"/>: the built-in ones, e.g. the high contrast themes, and the ones administrators uploaded); the catalog is
/// asked for whenever the branding says it changed (<see cref="BrandingViewModel.ThemesVersion"/>). A chosen pack that is no longer installed
/// is as good as not chosen. This only decides <i>which</i> theme it is (<see cref="Effective"/>, <see cref="EffectivePack"/>, <see cref="Changed"/>);
/// drawing it is the client's job. Used by both clients.
/// </summary>
public sealed partial class ThemeViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly IPlannerApi _api;
    private string _announced = ""; // what Changed last told: nothing yet, so the first answer is always announced
    private string? _loadedVersion;
    private bool _catalogLoaded;
    private IReadOnlyList<ThemePackDto> _packs = BuiltInThemePacks.All;

    public ThemeViewModel(ShellViewModel shell, IPlannerApi api, IOptions<PlannerClientOptions>? options = null)
    {
        _shell = shell;
        _api = api;
        Client = ThemeClients.IsKnown(options?.Value.ThemeClient) ? options!.Value.ThemeClient : ThemeClients.Web;

        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ShellViewModel.CurrentUser)) return;

            Refresh();
            LoadCatalogIfChanged();
        };
        shell.Brand.Changed += (_, _) =>
        {
            Refresh();
            LoadCatalogIfChanged();
        };
        shell.Language.Changed += (_, _) => NotifyChoices();

        LoadCatalogIfChanged(); // the branding may have been answered before this was made (the login screen asks for it)
    }

    /// <summary>The set of packs is asked for when the branding says it is another than the one held (and not already while it is being asked for).</summary>
    private void LoadCatalogIfChanged()
    {
        var version = _shell.Brand.ThemesVersion;
        if (version.Length == 0 || version == _loadedVersion || version == _loading) return;

        _loading = version;
        _ = LoadCatalogAsync(version);
    }

    private string? _loading;

    /// <summary>One of <see cref="ThemeClients"/>: whose themes these are.</summary>
    public string Client { get; }

    /// <summary>The theme in effect (one of <see cref="ThemeRules.For"/>, or the id of an installed theme pack).</summary>
    public string Effective => Resolve();

    /// <summary>The installed theme pack that is in effect, or <c>null</c> when it is one of the themes the client draws itself.</summary>
    public ThemePackDto? EffectivePack => PackOf(Effective);

    /// <summary>"light" or "dark" when the theme in effect has only that colour scheme (so the light/dark switch is of no use), else <c>null</c>.</summary>
    public string? FixedScheme => EffectivePack?.Scheme ?? ThemeRules.FixedScheme(Effective);

    /// <summary>The installed theme packs, built-in first.</summary>
    public IReadOnlyList<ThemePackDto> Packs => _packs;

    /// <summary>The installed pack with this id, or <c>null</c>.</summary>
    public ThemePackDto? PackOf(string id) => _packs.FirstOrDefault(p => p.Id == id);

    /// <summary>The user's own choice, or empty while they follow the server's default.</summary>
    public string SelectedId => (Client == ThemeClients.Windows ? _shell.CurrentUser?.WindowsTheme : _shell.CurrentUser?.WebTheme) is { } own
        && IsAvailable(own) ? own : "";

    /// <summary>What the list shows as chosen: <see cref="SelectedId"/> as its <see cref="ThemeChoice"/>.</summary>
    public ThemeChoice Selected => Choices.First(c => c.Id == SelectedId);

    /// <summary>"Server default (…)" first, then the default look, the high contrast themes, the other themes of this kind of client and the other packs.</summary>
    public IReadOnlyList<ThemeChoice> Choices =>
    [
        new("", string.Format(Strings.Theme_ServerDefault, NameOf(ServerDefault))),
        .. ChoicesFor(Client, _packs),
    ];

    /// <summary>The themes a kind of client offers, named: the default look, the high contrast themes, the other themes the client draws itself, the other packs.</summary>
    public static IReadOnlyList<ThemeChoice> ChoicesFor(string client, IReadOnlyList<ThemePackDto> packs)
    {
        var own = ThemeRules.For(client);
        IEnumerable<string> ids =
        [
            .. own.Take(1),
            .. packs.Where(p => p.HighContrast).Select(p => p.Id),
            .. own.Skip(1),
            .. packs.Where(p => !p.HighContrast).Select(p => p.Id),
        ];
        var named = ids.Select(id => (Id: id, Name: NameOf(client, id, packs))).ToList();
        // The names of the themes of one GitHub theme pack are unique, those of two packs need not be: the repository tells them apart.
        return [.. named.Select(n => new ThemeChoice(n.Id,
            packs.FirstOrDefault(p => p.Id == n.Id) is { Source: { } source } && named.Count(o => string.Equals(o.Name, n.Name, StringComparison.OrdinalIgnoreCase)) > 1
                ? $"{n.Name} ({source})"
                : n.Name))];
    }

    /// <summary>What the last attempt to save a choice answered, or <c>null</c>.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Raised when <see cref="Effective"/> is another theme than the one last announced (or the pack in effect was replaced by one with other
    /// colours): at the first sign of life (a login, the branding), after a choice, after the server's default changed. A client that remembers a
    /// theme from last time paints it before the first announcement; so the first one is made even where it is the theme the client would start with.</summary>
    public event EventHandler? Changed;

    private string ServerDefault =>
        Client == ThemeClients.Windows ? _shell.Brand.DefaultWindowsTheme : _shell.Brand.DefaultWebTheme;

    /// <summary>A theme the client draws itself, or a pack that is installed.</summary>
    private bool IsAvailable(string? id) =>
        id is not null && ThemeRules.IsValid(Client, id) && (!ThemePackRules.IsPackId(id) || PackOf(id) is not null);

    private string Resolve()
    {
        var own = Client == ThemeClients.Windows ? _shell.CurrentUser?.WindowsTheme : _shell.CurrentUser?.WebTheme;
        return IsAvailable(own) ? own! : IsAvailable(ServerDefault) ? ServerDefault : ThemeIds.Default;
    }

    /// <summary>Saves the user's choice (<paramref name="id"/> empty: follow the server's default again). A choice that is the current one is not sent.</summary>
    public async Task ChooseAsync(string id)
    {
        if (id == SelectedId || (id.Length > 0 && !IsAvailable(id))) return;

        ErrorMessage = null;
        try
        {
            _shell.Session.User = await _api.SetThemeAsync(new ThemePreferenceDto(Client, id.Length == 0 ? null : id));
        }
        catch (PlannerApiException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        Refresh();
    }

    /// <summary>Asks the API for the installed packs and takes them over. Cosmetic like the branding: a failure keeps what there was.</summary>
    public async Task LoadCatalogAsync(string? version = null)
    {
        ThemeCatalogDto catalog;
        try
        {
            catalog = await _api.GetThemesAsync();
        }
        catch (PlannerApiException)
        {
            return;
        }
        finally
        {
            _loading = null;
        }

        _catalogLoaded = true;
        _loadedVersion = version ?? catalog.Version;
        _packs = catalog.Packs;
        OnPropertyChanged(nameof(Packs));
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(SelectedId));
        OnPropertyChanged(nameof(Selected));
        NotifyChoices();

        // A pack the catalog has not been asked about yet may well exist: stay with what is shown rather than flicker to the default and back.
        var wanted = Client == ThemeClients.Windows ? _shell.CurrentUser?.WindowsTheme : _shell.CurrentUser?.WebTheme;
        if (!_catalogLoaded && ThemePackRules.IsPackId(wanted ?? ServerDefault) && PackOf(wanted ?? ServerDefault) is null) return;

        var effective = Resolve();
        var key = effective + "|" + Describe(PackOf(effective));
        if (key == _announced) return;

        _announced = key;
        OnPropertyChanged(nameof(Effective));
        OnPropertyChanged(nameof(EffectivePack));
        OnPropertyChanged(nameof(FixedScheme));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Describe(ThemePackDto? pack) =>
        pack is null ? "" : $"{pack.Scheme}{pack.HighContrast}{pack.Accent}" + string.Concat(pack.Colors.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Value));

    private void NotifyChoices()
    {
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(Selected));
    }

    /// <summary>The name of a theme as listed, in the language of the client; an uninstalled pack is listed under its id.</summary>
    public string NameOf(string id) => NameOf(Client, id, _packs);

    /// <summary>The name a theme is listed under (also used by the branding page, which sets the defaults).</summary>
    public static string NameOf(string client, string id) => NameOf(client, id, BuiltInThemePacks.All);

    /// <summary>The name of a theme pack in the current language: the German one for German, else the English one.</summary>
    public static string PackName(ThemePackDto pack) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" && !string.IsNullOrWhiteSpace(pack.NameDe) ? pack.NameDe : pack.Name;

    public static string NameOf(string client, string id, IReadOnlyList<ThemePackDto> packs) => id switch
    {
        ThemeIds.Dracula => Strings.Theme_Dracula,
        ThemeIds.CatppuccinLatte => Strings.Theme_CatppuccinLatte,
        ThemeIds.CatppuccinMocha => Strings.Theme_CatppuccinMocha,
        ThemeIds.LiquidGlass => Strings.Theme_LiquidGlass,
        ThemeIds.Windows10 => Strings.Design_Windows10,
        ThemeIds.Aero => Strings.Design_Aero,
        ThemeIds.Native => Strings.Design_Native,
        ThemeIds.Default => client == ThemeClients.Windows ? Strings.Theme_DefaultWindows : Strings.Theme_Default,
        _ => packs.FirstOrDefault(p => p.Id == id) is { } pack ? PackName(pack) : id,
    };
}
