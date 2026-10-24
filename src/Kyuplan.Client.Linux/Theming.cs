using Kyuplan.Client.LinuxApp.Services;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp;

/// <summary>
/// Dresses the program in the theme in effect (<see cref="ThemeViewModel"/>): light or dark like the system unless the theme has only one
/// scheme, the twelve colours of a theme pack, and the accent colour of the instance. Drawn live: unlike the Windows client, no design needs a restart.
/// </summary>
internal sealed class Theming
{
    private readonly ThemeViewModel _themes;
    private readonly BrandingViewModel _brand;
    private Gtk.CssProvider? _provider;

    public Theming(ThemeViewModel themes, BrandingViewModel brand)
    {
        _themes = themes;
        _brand = brand;
        themes.Changed += (_, _) => Apply();
        brand.Changed += (_, _) => Apply();
        Adw.StyleManager.GetDefault().OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == "dark") Apply(updateScheme: false);
        };
    }

    public void Apply(bool updateScheme = true)
    {
        var id = _themes.Effective;
        var pack = _themes.EffectivePack ?? ThemePalette.BuiltIn(id);
        var styles = Adw.StyleManager.GetDefault();

        if (updateScheme)
        {
            // A theme with one colour scheme forces it; the others follow the system (the light/dark switch of the desktop).
            styles.ColorScheme = (pack?.Scheme ?? ThemeRules.FixedScheme(id)) switch
            {
                "dark" => Adw.ColorScheme.ForceDark,
                "light" => Adw.ColorScheme.ForceLight,
                _ => Adw.ColorScheme.Default,
            };
        }

        var css = ThemePalette.Css(pack, _brand.AccentColor, styles.Dark);
        var display = Gdk.Display.GetDefault();
        if (display is null) return;

        if (_provider is not null) Gtk.StyleContext.RemoveProviderForDisplay(display, _provider);
        _provider = Gtk.CssProvider.New();
        _provider.LoadFromString(css);
        Gtk.StyleContext.AddProviderForDisplay(display, _provider, Gtk.Constants.STYLE_PROVIDER_PRIORITY_APPLICATION);
    }
}
