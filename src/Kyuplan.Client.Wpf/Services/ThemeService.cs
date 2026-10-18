using System.Collections;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;
using Microsoft.Win32;

namespace Kyuplan.Client.WpfApp.Services;

/// <summary>
/// The look of the program: a theme (<see cref="ThemeIds"/>: the ones of the Windows client, i.e. Windows 11 or 10 as <c>default</c>, Windows 10,
/// Windows Vista / 7 "Aero", the native WPF controls, and the general ones Dracula, Catppuccin and Liquid Glass) and, for the themes
/// that have both, light or dark mode. Which theme it is, is the API's business (the user's own choice, else the administrators' default,
/// see <c>ThemeViewModel</c>); this class draws it and keeps a copy of the last one for the next start.
/// <para>
/// Every theme belongs to a design family, which is not only colours but shapes, fonts, control templates: Windows 11 is the Fluent theme that ships with WPF
/// (<see cref="Application.ThemeMode"/>: Mica, rounded corners), Windows 10 and Aero are the one set of templates in
/// Themes/Controls.Classic.xaml with the shapes and colours of their era, "native" is no theme at all (WPF's own controls in the
/// colours of the Windows colour scheme). The windows are written against names of Fluent's resources (<c>TextFillColorPrimaryBrush</c>,
/// <c>AccentButtonStyle</c>, ...); every Themes/Design.*.xaml provides the same names, so the XAML of the pages is the same in every design.
/// Dracula and Catppuccin are one family (flat classic controls with rounded corners, "Modern"; only the colours differ), Liquid Glass is
/// the same controls with large radii over see-through white. Because the pages refer to some names with StaticResource (a style another
/// one is based on), the family is loaded once, at start, and changing to a theme of another family restarts the program; a theme of the
/// same family (Dracula to Catppuccin) and light/dark are switched at any time.
/// </para>
/// <para>
/// Windows 11 follows Windows until the user picks a mode with the toggle; the choice is then remembered. Fluent draws selection,
/// focus, buttons and links in the accent colour of Windows; once <see cref="UseAccent"/> has been told the accent colour of the instance
/// (the API's <c>Branding:AccentColor</c>), every brush that carries the Windows accent is replaced by one in that colour, so until then
/// (or when the API cannot be reached) the app follows Windows. The other designs have no accent of their own, so ThemeService makes the
/// accent brushes for them (the instance's colour, otherwise the accent of Windows for Windows 10, the blue of Windows 7 for Aero, the
/// selection colour of the desktop for native). What no design knows are the data colours (attended, declined, ...), which live in
/// Palette.Light/Dark.xaml and are swapped along with the mode. "Attended" is drawn in the accent colour: those three brushes are taken
/// from the accent brushes whenever the mode or the accent changes, so nothing is a fixed green.
/// </para>
/// </summary>
public sealed partial class ThemeService(UserPreferences preferences) : ObservableObject
{
    // The design families.
    public const string Windows11 = "windows11";
    public const string Windows10 = "windows10";
    public const string Aero = "aero";
    public const string Native = "native";
    public const string Modern = "modern";
    public const string Glass = "glass";

    private const string Preference = "theme";

    /// <summary>The theme of the last run (a copy of what the API said), so the program starts in it before it can ask. The file is called "design".</summary>
    private const string ThemePreference = "design";

    /// <summary>The colours of the last theme when it was a theme pack (JSON), so that the program starts in it before it can ask.</summary>
    private const string PackPreference = "design-pack";

    private ResourceDictionary? _palette;
    private ResourceDictionary? _colors;
    private Rgb? _accent;
    private readonly HashSet<object> _accentOverrides = [];

    /// <summary>The theme in use (one of <see cref="ThemeRules.For"/> for the Windows client).</summary>
    public string Theme => _theme;

    private static string DefaultFamily => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? Windows11 : Windows10;

    /// <summary>A theme this client has; anything else (a theme of a newer version, nothing saved yet) is <see cref="ThemeIds.Default"/>.</summary>
    private static string Normalize(string? theme) => ThemeRules.IsValid(ThemeClients.Windows, theme) ? theme! : ThemeIds.Default;

    /// <summary>The theme the last run ended with: a theme pack only together with its colours (<see cref="PackPreference"/>), which are all that is known of it now.</summary>
    private static string StartTheme(UserPreferences preferences)
    {
        var theme = Normalize(preferences.Get(ThemePreference));
        return ThemePackRules.IsPackId(theme) && ReadPack(preferences)?.Id != theme ? ThemeIds.Default : theme;
    }

    private static ThemePackDto? ReadPack(UserPreferences preferences)
    {
        try
        {
            return preferences.Get(PackPreference) is { } json && JsonSerializer.Deserialize<ThemePackDto>(json, PackJson) is { } pack && ThemePackRules.Check(pack) is null ? pack : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions PackJson = new(JsonSerializerDefaults.Web);

    private static string FamilyOf(string theme) => theme switch
    {
        ThemeIds.Windows10 => Windows10,
        ThemeIds.Aero => Aero,
        ThemeIds.Native => Native,
        ThemeIds.Dracula or ThemeIds.CatppuccinLatte or ThemeIds.CatppuccinMocha => Modern,
        ThemeIds.LiquidGlass => Glass,
        _ when ThemePackRules.IsPackId(theme) => Modern, // a pack is colours only: the flat controls with rounded corners
        _ => DefaultFamily,
    };

    /// <summary>"light" or "dark" when the theme has only that colour scheme (a pack has one), else <c>null</c>.</summary>
    private string? FixedScheme => _pack?.Scheme ?? ThemeRules.FixedScheme(_theme);

    /// <summary>Whether the theme has a dark mode to switch to: Windows 11, Windows 10 and Liquid Glass do; Aero and the native controls do
    /// not, and Dracula, Catppuccin Latte and Mocha and the theme packs are one colour scheme only (so the toggle is of no use there).</summary>
    public bool SupportsDark => FixedScheme is null && _design is Windows11 or Windows10 or Glass;

    [ObservableProperty]
    private bool _isDark;

    private string _theme = StartTheme(preferences);

    /// <summary>The colours of <see cref="Theme"/> when it is a theme pack.</summary>
    private ThemePackDto? _pack = ThemePackRules.IsPackId(StartTheme(preferences)) ? ReadPack(preferences) : null;

    /// <summary>The design family this run was started with; it cannot change without a restart.</summary>
    private readonly string _design = FamilyOf(StartTheme(preferences));

    public void Initialize()
    {
        var app = Application.Current;
        var mode = preferences.Get(Preference);

        // The order matters: Styles.xaml bases its styles on names the design provides, and StaticResource looks them up while it loads.
        app.ThemeMode = _design == Windows11 ? ThemeModeOf(mode) : ThemeMode.None;
        var merged = app.Resources.MergedDictionaries;
        merged.Add(Load($"Themes/{DesignFile}"));
        merged.Add(Load("Themes/Styles.xaml"));

        Apply(mode);

        // Every window, as it opens: the designs drawn by this program give it their background and font (an implicit style would not
        // reach the classes derived from Window) and tell Windows to draw the caption bar dark when they are.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => ApplyWindow((Window)s)));

        // General also fires when the Windows accent colour changes; wait until Fluent has taken the new one over.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (preferences.Get(Preference) is null) Apply(null);
                else ApplyAccent();
            }, DispatcherPriority.Background);
        };
    }

    public void Toggle()
    {
        if (!SupportsDark) return;

        var mode = IsDark ? "light" : "dark";
        preferences.Set(Preference, mode);
        Apply(mode);
    }

    /// <summary>
    /// Takes over the theme the API says is in effect: remembers it for the next start and, where it is of the design family in use, draws it
    /// now. <c>true</c> when it is of another family: that is loaded once, at start, so the program has to start again (the caller does that,
    /// after asking where there is something to lose); the theme is remembered, so the new run starts in it.
    /// </summary>
    public bool Adopt(string theme, ThemePackDto? pack = null)
    {
        theme = Normalize(theme);
        if (ThemePackRules.IsPackId(theme) && (pack is null || pack.Id != theme)) theme = ThemeIds.Default; // a pack that is not installed any more
        if (!ThemePackRules.IsPackId(theme)) pack = null;

        preferences.Set(ThemePreference, theme);
        preferences.Set(PackPreference, pack is null ? "" : JsonSerializer.Serialize(pack, PackJson));

        var samePack = _pack is null && pack is null || _pack is not null && pack is not null && JsonSerializer.Serialize(_pack, PackJson) == JsonSerializer.Serialize(pack, PackJson);
        if (theme == _theme && samePack) return false;
        if (FamilyOf(theme) != _design) return true;

        _theme = theme;
        _pack = pack;
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(SupportsDark));
        Apply(preferences.Get(Preference));
        return false;
    }

    private string DesignFile => _design switch
    {
        Windows10 => "Design.Win10.xaml",
        Aero => "Design.Aero.xaml",
        Native => "Design.Native.xaml",
        Modern => "Design.Modern.xaml",
        Glass => "Design.Glass.xaml",
        _ => "Design.Fluent.xaml",
    };

    /// <summary>The colour set of the theme, if its design family has none of its own (Windows 11 and the native controls draw themselves).</summary>
    private string? ColorsFile => _theme switch
    {
        ThemeIds.Dracula => "Colors.Dracula.xaml",
        ThemeIds.CatppuccinMocha => "Colors.CatppuccinMocha.xaml",
        ThemeIds.CatppuccinLatte => "Colors.CatppuccinLatte.xaml",
        ThemeIds.LiquidGlass => $"Colors.Glass.{(IsDark ? "Dark" : "Light")}.xaml",
        _ => _design switch
        {
            Windows10 => $"Colors.Win10.{(IsDark ? "Dark" : "Light")}.xaml",
            Aero => "Colors.Aero.xaml",
            _ => null,
        },
    };

    private static ThemeMode ThemeModeOf(string? mode) => mode switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    // With the assembly in the address, so that it also resolves where the entry assembly is another one (the tests of the views).
    private static ResourceDictionary Load(string path) =>
        new() { Source = new Uri($"pack://application:,,,/{typeof(ThemeService).Assembly.GetName().Name};component/{path}", UriKind.Absolute) };

    /// <param name="mode">"light", "dark", or null to follow Windows.</param>
    private void Apply(string? mode)
    {
        var app = Application.Current;
        if (_design == Windows11) app.ThemeMode = ThemeModeOf(mode);

        IsDark = FixedScheme is "dark" || (SupportsDark && (mode is "dark" || (mode is null && WindowsUsesDarkTheme())));

        var merged = app.Resources.MergedDictionaries;
        if (_palette is not null) merged.Remove(_palette);
        _palette = Load($"Themes/Palette.{(IsDark ? "Dark" : "Light")}.xaml");
        merged.Add(_palette);

        if (_colors is not null) merged.Remove(_colors);
        _colors = _pack is not null ? ThemePackColors.Build(_pack) : ColorsFile is { } colors ? Load($"Themes/{colors}") : null;
        if (_colors is not null) merged.Add(_colors);

        ApplyAccent();

        foreach (Window window in app.Windows) ApplyWindow(window);
    }

    /// <summary>Draws the app in this accent colour (<c>#RGB</c> or <c>#RRGGBB</c>) instead of the one of Windows. Anything else, or
    /// <c>null</c>, goes back to the accent colour of Windows. Call it on the UI thread; it can be called again whenever the instance's
    /// accent colour changes, and every brush in the windows follows (they refer to the brushes dynamically).</summary>
    public void UseAccent(string? hex)
    {
        Rgb? accent = Rgb.TryParse(hex, out var rgb) ? rgb : null;
        if (accent == _accent) return; // e.g. only the name changed: some 70 brushes are no reason to be replaced again

        _accent = accent;
        ApplyAccent();
    }

    /// <summary>The accent brushes of the design in the colour of the instance, then "attended" in that accent: fill and text from the
    /// accent brushes (they already differ for light and dark), plus a see-through wash of the fill. Set on the application itself,
    /// so they win over Fluent's brushes and over the palette's fallback.</summary>
    private void ApplyAccent()
    {
        var app = Application.Current;

        foreach (var key in _accentOverrides) app.Resources.Remove(key);
        _accentOverrides.Clear();
        if (_design == Windows11)
        {
            if (_accent is { } accent) OverrideFluentAccent(app, accent);
        }
        else
        {
            MakeAccentBrushes(app);
        }

        if (app.TryFindResource("AccentFillColorDefaultBrush") is not SolidColorBrush fill
            || app.TryFindResource("AccentTextFillColorPrimaryBrush") is not SolidColorBrush text)
            return;

        app.Resources["DataYesBrush"] = Frozen(fill.Color);
        app.Resources["DataYesTextBrush"] = Frozen(text.Color);
        app.Resources["DataYesWashBrush"] = Frozen(Color.FromArgb(IsDark ? (byte)0x33 : (byte)0x26, fill.Color.R, fill.Color.G, fill.Color.B));

        // The line under the selected tab, in every design that draws it in the accent colour (Aero's tab is a raised one).
        if (_design != Aero) app.Resources["AppTabSelectedBorderBrush"] = Frozen(fill.Color);
    }

    /// <summary>
    /// The designs without Fluent have no accent of their own: the brushes Fluent would have are made here, from the instance's accent
    /// colour, otherwise from the colour of the design's era (the Windows accent for Windows 10, the blue of Windows 7 for Aero, the
    /// selection colour of the desktop for native). On top of them come the accent-coloured parts of the classic controls (the main
    /// button, selected rows, ticks, the progress bar), which look the way their era drew them: solid and flat on Windows 10, glossy on Aero.
    /// </summary>
    private void MakeAccentBrushes(Application app)
    {
        // A theme pack may carry its own accent (high contrast themes need one that fits their surfaces); it wins over the instance's.
        var accent = _pack?.Accent is { } packAccent && Rgb.TryParse(packAccent, out var fromPack) ? fromPack : _accent ?? DefaultAccent();
        var shades = AccentShades.Of(accent, IsDark);
        var onAccent = Luminance(shades.Fill) > 0.62 ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255);

        Set(app, "AccentFillColorSelectedTextBackgroundBrush", shades.Selection);
        Set(app, "AccentFillColorDefaultBrush", shades.Fill);
        Set(app, "AccentTextFillColorPrimaryBrush", shades.Text);
        Set(app, "AccentTextFillColorSecondaryBrush", shades.TextSecondary);
        Set(app, "TextOnAccentFillColorPrimaryBrush", onAccent);

        if (_design == Native) return; // the desktop's own controls ask the system, not these

        Set(app, "CtlInputFocusBorder", shades.Fill);
        var selection = shades.Selection;

        if (_design != Aero)
        {
            // Flat (Windows 10 and the general themes): the accent colour itself, a shade darker when the pointer is on it.
            Set(app, "CtlAccentButtonBackground", shades.Fill);
            Set(app, "CtlAccentButtonHoverBackground", shades.Fill.Darker(0.9));
            Set(app, "CtlAccentButtonPressedBackground", shades.Fill.Darker(0.78));
            Set(app, "CtlAccentButtonBorder", Color.FromArgb(0, 0, 0, 0));
            Set(app, "CtlAccentButtonHoverBorder", shades.Fill.Darker(0.7));
            Set(app, "CtlAccentButtonPressedBorder", Color.FromArgb(0, 0, 0, 0));
            Set(app, "CtlAccentButtonInnerBorder", Color.FromArgb(0, 0, 0, 0));

            Set(app, "CtlToggleCheckedBackground", shades.Fill);
            Set(app, "CtlToggleCheckedBorder", Color.FromArgb(0, 0, 0, 0));
            Set(app, "CtlToggleCheckedForeground", onAccent);
            Set(app, "CtlCheckedBackground", shades.Fill);
            Set(app, "CtlCheckedBorder", shades.Fill);
            Set(app, "CtlCheckMarkBrush", onAccent);
            Set(app, "CtlRadioDotBrush", shades.Fill);
            Set(app, "CtlListSelectedBackground", Color.FromArgb(IsDark ? (byte)0x66 : (byte)0x40, selection.R, selection.G, selection.B));
            Set(app, "CtlListSelectedBorder", Color.FromArgb(0, 0, 0, 0));
            Set(app, "CtlProgressFill", shades.Fill);
            Set(app, "CtlSliderFill", shades.Fill);
        }
        else
        {
            // Aero: glass. Gradients made from the accent colour the way Windows 7 makes its blue ones.
            var fill = shades.Fill;
            Set(app, "CtlAccentButtonBackground", Gradient((0, fill.Lighter(0.45)), (0.5, fill.Lighter(0.18)), (0.5, fill.Darker(0.92)), (1, fill.Lighter(0.12))));
            Set(app, "CtlAccentButtonHoverBackground", Gradient((0, fill.Lighter(0.6)), (0.5, fill.Lighter(0.32)), (0.5, fill.Lighter(0.08)), (1, fill.Lighter(0.3))));
            Set(app, "CtlAccentButtonPressedBackground", Gradient((0, fill.Darker(0.8)), (0.5, fill.Darker(0.88)), (0.5, fill.Darker(0.7)), (1, fill.Darker(0.85))));
            Set(app, "CtlAccentButtonBorder", fill.Darker(0.62));
            Set(app, "CtlAccentButtonHoverBorder", fill.Darker(0.55));
            Set(app, "CtlAccentButtonPressedBorder", fill.Darker(0.45));
            Set(app, "CtlAccentButtonInnerBorder", Color.FromArgb(0x66, 255, 255, 255));

            Set(app, "CtlToggleCheckedBackground", Gradient((0, selection.Lighter(0.82)), (0.5, selection.Lighter(0.62)), (0.5, selection.Lighter(0.42)), (1, selection.Lighter(0.55))));
            Set(app, "CtlToggleCheckedBorder", selection.Darker(0.7));
            Set(app, "CtlToggleCheckedForeground", new Rgb(0, 0, 0));
            Set(app, "CtlListSelectedBackground", Gradient((0, selection.Lighter(0.9)), (1, selection.Lighter(0.72))));
            Set(app, "CtlListSelectedBorder", selection.Darker(0.85));
        }
    }

    private Rgb DefaultAccent() => _design switch
    {
        Aero => new Rgb(0x33, 0x99, 0xFF), // the selection blue of Windows 7
        Native => new Rgb(SystemColors.HighlightColor.R, SystemColors.HighlightColor.G, SystemColors.HighlightColor.B),
        _ => new Rgb(SystemColors.AccentColor.R, SystemColors.AccentColor.G, SystemColors.AccentColor.B),
    };

    /// <summary>Perceived brightness, 0 (black) to 1 (white): decides between a white and a black label on the accent colour.</summary>
    private static double Luminance(Rgb c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    private static void Set(Application app, string key, Rgb colour) => app.Resources[key] = Frozen(Color.FromRgb(colour.R, colour.G, colour.B));

    private static void Set(Application app, string key, Color colour) => app.Resources[key] = Frozen(colour);

    private static void Set(Application app, string key, Brush brush)
    {
        brush.Freeze();
        app.Resources[key] = brush;
    }

    private static LinearGradientBrush Gradient(params (double Offset, Rgb Colour)[] stops) =>
        new(new GradientStopCollection(stops.Select(s => new GradientStop(Color.FromRgb(s.Colour.R, s.Colour.G, s.Colour.B), s.Offset))), 90);

    /// <summary>
    /// Fluent keeps the accent colour of Windows in some 70 brushes (buttons, tick boxes, switches, focus lines, links, selection, ...)
    /// that carry the colour itself, not a reference to it, so there is no single key to change. They are recognised by their colour:
    /// the four shades Fluent has for this mode are read from its own named accent brushes, and every brush of the theme that has
    /// one of them (the focus line of a text box is a gradient, the rest are plain) is replaced, with the same transparency, by the
    /// matching shade of the instance's accent.
    /// </summary>
    private void OverrideFluentAccent(Application app, Rgb accent)
    {
        var fluent = app.Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Fluent", StringComparison.Ordinal) == true);
        if (fluent is null
            || RgbOf(fluent, "AccentFillColorSelectedTextBackgroundBrush") is not { } selection
            || RgbOf(fluent, "AccentFillColorDefaultBrush") is not { } fill
            || RgbOf(fluent, "AccentTextFillColorPrimaryBrush") is not { } text
            || RgbOf(fluent, "AccentTextFillColorSecondaryBrush") is not { } textSecondary)
            return; // not the theme this was written for: better the accent of Windows than a half-changed one

        var mine = AccentShades.Of(accent, IsDark);
        var replacements = new Dictionary<Rgb, Rgb>();
        replacements.TryAdd(selection, mine.Selection);
        replacements.TryAdd(fill, mine.Fill);
        replacements.TryAdd(text, mine.Text);
        replacements.TryAdd(textSecondary, mine.TextSecondary);

        Color? Recolour(Color c) => replacements.TryGetValue(new Rgb(c.R, c.G, c.B), out var to) ? Color.FromArgb(c.A, to.R, to.G, to.B) : null;

        foreach (var entry in Entries(fluent))
        {
            Brush? replacement = null;
            if (entry.Value is SolidColorBrush solid && Recolour(solid.Color) is { } colour)
            {
                replacement = new SolidColorBrush(colour) { Opacity = solid.Opacity };
            }
            else if (entry.Value is GradientBrush gradient && gradient.GradientStops.Any(s => Recolour(s.Color) is not null))
            {
                var copy = gradient.Clone();
                foreach (var stop in copy.GradientStops) stop.Color = Recolour(stop.Color) ?? stop.Color;
                replacement = copy;
            }

            if (replacement is null) continue;
            replacement.Freeze();
            app.Resources[entry.Key] = replacement;
            _accentOverrides.Add(entry.Key);
        }
    }

    /// <summary>The entries of a dictionary and of the ones merged into it, each key once, as a lookup would find it. With an explicit
    /// light or dark mode Fluent is one flat dictionary, but while following Windows (<see cref="ThemeMode.System"/>) the brushes are in a
    /// dictionary merged into the Fluent one, which a plain <c>foreach</c> over it does not visit.</summary>
    private static IEnumerable<DictionaryEntry> Entries(ResourceDictionary dictionary, HashSet<object>? seen = null)
    {
        seen ??= [];
        foreach (DictionaryEntry entry in dictionary)
            if (seen.Add(entry.Key)) yield return entry;

        // The last merged dictionary wins a lookup, so it comes first here.
        foreach (var merged in dictionary.MergedDictionaries.Reverse())
            foreach (var entry in Entries(merged, seen))
                yield return entry;
    }

    private static Rgb? RgbOf(ResourceDictionary dictionary, string key) =>
        dictionary[key] is SolidColorBrush { Color: var c } ? new Rgb(c.R, c.G, c.B) : null;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static bool WindowsUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }

    /// <summary>Windows 11's Fluent theme draws windows by itself (Mica, dark caption bar). For the other designs the window gets the
    /// background of the design, and (but for native, which keeps the desktop's font) its font, and the caption bar is asked to be dark.</summary>
    private void ApplyWindow(Window window)
    {
        if (_design == Windows11) return;

        window.SetResourceReference(Control.BackgroundProperty, "AppWindowBrush");
        if (_design != Native)
        {
            window.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
            window.SetResourceReference(Control.FontFamilyProperty, "AppFontFamily");
            window.SetResourceReference(Control.FontSizeProperty, "AppFontSize");

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var dark = IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref dark, sizeof(int)); // fails on Windows before 10 20H1: the bar stays light
        }
    }

    private const int ImmersiveDarkMode = 20; // DWMWA_USE_IMMERSIVE_DARK_MODE

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
