using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>One entry of the theme list. <see cref="Id"/> is a theme of <see cref="ThemeRules"/>, or empty for "whatever the server says".</summary>
public sealed record ThemeChoice(string Id, string Name);

/// <summary>
/// Which theme the client shows: the one the signed-in user chose for this kind of client (<see cref="PlannerClientOptions.ThemeClient"/>),
/// else the one the administrators set as the default (<see cref="BrandingDto.DefaultWebTheme"/> / <see cref="BrandingDto.DefaultWindowsTheme"/>).
/// The choice is kept with the account, so it follows the user to another computer. This only decides <i>which</i> theme it is
/// (<see cref="Effective"/>, <see cref="Changed"/>); drawing it is the client's job. Used by both clients.
/// </summary>
public sealed partial class ThemeViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly IPlannerApi _api;
    private string _announced = ""; // what Changed last told: nothing yet, so the first answer is always announced

    public ThemeViewModel(ShellViewModel shell, IPlannerApi api, IOptions<PlannerClientOptions>? options = null)
    {
        _shell = shell;
        _api = api;
        Client = ThemeClients.IsKnown(options?.Value.ThemeClient) ? options!.Value.ThemeClient : ThemeClients.Web;

        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.CurrentUser)) Refresh();
        };
        shell.Brand.Changed += (_, _) => Refresh();
        shell.Language.Changed += (_, _) => NotifyChoices();
    }

    /// <summary>One of <see cref="ThemeClients"/>: whose themes these are.</summary>
    public string Client { get; }

    /// <summary>The theme in effect (one of <see cref="ThemeRules.For"/>).</summary>
    public string Effective => Resolve();

    /// <summary>The user's own choice, or empty while they follow the server's default.</summary>
    public string SelectedId => (Client == ThemeClients.Windows ? _shell.CurrentUser?.WindowsTheme : _shell.CurrentUser?.WebTheme) is { } own
        && ThemeRules.IsValid(Client, own) ? own : "";

    /// <summary>What the list shows as chosen: <see cref="SelectedId"/> as its <see cref="ThemeChoice"/>.</summary>
    public ThemeChoice Selected => Choices.First(c => c.Id == SelectedId);

    /// <summary>"Server default (…)" first, then the themes of this kind of client.</summary>
    public IReadOnlyList<ThemeChoice> Choices =>
    [
        new("", string.Format(Strings.Theme_ServerDefault, NameOf(Client, ServerDefault))),
        .. ThemeRules.For(Client).Select(id => new ThemeChoice(id, NameOf(Client, id))),
    ];

    /// <summary>What the last attempt to save a choice answered, or <c>null</c>.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Raised when <see cref="Effective"/> is another theme than the one last announced: at the first sign of life (a login, the
    /// branding), after a choice, after the server's default changed. A client that remembers a theme from last time paints it before
    /// the first announcement; so the first one is made even where it is the theme the client would start with.</summary>
    public event EventHandler? Changed;

    private string ServerDefault =>
        Client == ThemeClients.Windows ? _shell.Brand.DefaultWindowsTheme : _shell.Brand.DefaultWebTheme;

    private string Resolve() =>
        ThemeRules.Resolve(Client, Client == ThemeClients.Windows ? _shell.CurrentUser?.WindowsTheme : _shell.CurrentUser?.WebTheme, ServerDefault);

    /// <summary>Saves the user's choice (<paramref name="id"/> empty: follow the server's default again). A choice that is the current one is not sent.</summary>
    public async Task ChooseAsync(string id)
    {
        if (id == SelectedId || (id.Length > 0 && !ThemeRules.IsValid(Client, id))) return;

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

    private void Refresh()
    {
        OnPropertyChanged(nameof(SelectedId));
        OnPropertyChanged(nameof(Selected));
        NotifyChoices();

        var effective = Resolve();
        if (effective == _announced) return;

        _announced = effective;
        OnPropertyChanged(nameof(Effective));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyChoices()
    {
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(Selected));
    }

    /// <summary>The name a theme is listed under (also used by the branding page, which sets the defaults).</summary>
    public static string NameOf(string client, string id) => id switch
    {
        ThemeIds.Dracula => Strings.Theme_Dracula,
        ThemeIds.CatppuccinLatte => Strings.Theme_CatppuccinLatte,
        ThemeIds.CatppuccinMocha => Strings.Theme_CatppuccinMocha,
        ThemeIds.LiquidGlass => Strings.Theme_LiquidGlass,
        ThemeIds.Windows10 => Strings.Design_Windows10,
        ThemeIds.Aero => Strings.Design_Aero,
        ThemeIds.Native => Strings.Design_Native,
        _ => client == ThemeClients.Windows ? Strings.Theme_DefaultWindows : Strings.Theme_Default,
    };
}
