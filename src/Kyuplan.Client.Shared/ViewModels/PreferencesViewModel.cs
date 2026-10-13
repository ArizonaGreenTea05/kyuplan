using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// The page of the user's own settings, a sub page of the account: the theme (<see cref="Themes"/>) and the language of the clients.
/// Both are kept with the account, so they follow the user to another computer. Choosing a language saves it; the shell then switches
/// to it (<see cref="ShellViewModel"/> follows the language of the signed-in user, also at sign-in).
/// </summary>
public sealed class PreferencesViewModel : PageViewModel
{
    private readonly IPlannerApi _api;

    public PreferencesViewModel(ShellViewModel shell, IPlannerApi api, ThemeViewModel themes) : base(shell)
    {
        _api = api;
        Themes = themes;
        shell.Language.Changed += (_, _) => OnPropertyChanged(nameof(SelectedLanguage));
    }

    public override string Title => Strings.Nav_Preferences;

    /// <summary>Nothing here depends on the selected group or the Spond data.</summary>
    protected override bool ReloadsWithContext => false;

    /// <summary>Every signed-in user looks after their own settings, so the page needs no permission.</summary>
    protected override string? ViewPermission => null;

    /// <summary>The theme choice (design, and which theme is in effect).</summary>
    public ThemeViewModel Themes { get; }

    public IReadOnlyList<LanguageOption> Languages => LanguageService.Supported;

    /// <summary>The language in use.</summary>
    public LanguageOption SelectedLanguage => Shell.Language.Current;

    /// <summary>Saves <paramref name="language"/> as the user's own; a choice that is the current one is not sent.</summary>
    public async Task ChooseLanguageAsync(LanguageOption language)
    {
        if (language.Code == Shell.Language.Current.Code && Shell.CurrentUser?.Language == language.Code) return;

        await LoadAsync(async ct => Shell.Session.User = await _api.SetLanguageAsync(new LanguagePreferenceDto(language.Code), ct));
        OnPropertyChanged(nameof(SelectedLanguage)); // a failed save leaves the list on the language that is still in use
    }

    protected override Task ReloadAsync() => Task.CompletedTask;
}
