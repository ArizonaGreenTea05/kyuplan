using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kyuplan.Client.Localization;

/// <summary>A language the clients are translated into. <see cref="Culture"/> also decides number and date formats.</summary>
public sealed record LanguageOption(string Code, string Name, CultureInfo Culture)
{
    /// <summary>The name in its own language. Lists and screen readers show this instead of the record's fields.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The language of the user interface: which <c>Strings.&lt;culture&gt;.resx</c> is used and how numbers and dates
/// are written. Both clients follow the operating system or browser language until the user picks one; remembering
/// that pick is up to the client (a file in WPF, local storage in the browser).
/// </summary>
public sealed class LanguageService : ObservableObject
{
    /// <summary>
    /// The languages that have a translation. The first one is the fallback and matches the neutral
    /// <c>Strings.resx</c>. British English because the club writes dates day-first and the clock in 24 hours.
    /// </summary>
    public static IReadOnlyList<LanguageOption> Supported { get; } =
    [
        new("en", "English", CultureInfo.GetCultureInfo("en-GB")),
        new("de", "Deutsch", CultureInfo.GetCultureInfo("de-DE")),
    ];

    private LanguageOption? _applied;

    public LanguageService() => Current = Resolve(CultureInfo.CurrentUICulture);

    public LanguageOption Current { get; private set; }

    /// <summary>Raised after the language changed, so that texts built from it (key figures, labels) can be rebuilt.</summary>
    public event EventHandler? Changed;

    /// <summary>The supported language that fits <paramref name="culture"/> best: same language, otherwise the fallback.</summary>
    public static LanguageOption Resolve(CultureInfo culture) =>
        Supported.FirstOrDefault(l => l.Culture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName) ?? Supported[0];

    /// <summary>Like <see cref="Resolve(CultureInfo)"/> for a stored or typed code such as "de" or "de-AT". Unknown codes give the fallback.</summary>
    public static LanguageOption Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Supported[0];

        try
        {
            return Resolve(CultureInfo.GetCultureInfo(code.Trim()));
        }
        catch (CultureNotFoundException)
        {
            return Supported[0];
        }
    }

    /// <summary>
    /// Applies the culture of <paramref name="language"/> to the calling thread and to every thread started later.
    /// <see cref="CultureInfo.CurrentCulture"/> flows with the execution context, so a culture set inside an async
    /// continuation or a dispatcher callback is undone when that returns: call this where the thread starts (for
    /// WPF the constructor of <c>App</c>), not from <c>OnStartup</c>.
    /// </summary>
    public static void Apply(LanguageOption language)
    {
        CultureInfo.DefaultThreadCurrentCulture = language.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = language.Culture;
        CultureInfo.CurrentCulture = language.Culture;
        CultureInfo.CurrentUICulture = language.Culture;
    }

    /// <summary>
    /// Makes <paramref name="language"/> the current one, applies its culture (see <see cref="Apply"/>) and tells
    /// the listeners. Call it again whenever the user picks another language.
    /// </summary>
    public void Select(LanguageOption language)
    {
        var changed = Current != language;
        Current = language;

        if (_applied != language)
        {
            _applied = language;
            Apply(language);
        }

        if (!changed) return;
        OnPropertyChanged(nameof(Current));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Select(string? code) => Select(Resolve(code));
}
