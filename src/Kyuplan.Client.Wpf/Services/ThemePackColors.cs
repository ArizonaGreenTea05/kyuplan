using System.Windows;
using System.Windows.Media;
using Kyuplan.Contracts;

namespace Kyuplan.Client.WpfApp.Services;

/// <summary>
/// Makes the colour set of a theme pack (<see cref="ThemePackDto"/>: twelve colours) for the flat designs: every brush the hand-made
/// <c>Colors.*.xaml</c> files define (same keys), derived from the pack's surfaces, text and lines, so one small file dresses all the
/// controls. The accent-coloured parts are not here; <see cref="ThemeService"/> makes them from the accent colour as it does for every
/// theme. A high contrast pack gets the strong border colour on buttons, boxes and ticks (not the thin line colour).
/// </summary>
public static class ThemePackColors
{
    public static ResourceDictionary Build(ThemePackDto pack)
    {
        var s = Parse(pack, ThemePackRules.Sheet);
        var a = Parse(pack, ThemePackRules.SheetAlt);
        var g = Parse(pack, ThemePackRules.Ground);
        var n = Parse(pack, ThemePackRules.Sidebar);
        var t = Parse(pack, ThemePackRules.Text);
        var m = Parse(pack, ThemePackRules.Muted);
        var r = Parse(pack, ThemePackRules.Rule);
        var rs = Parse(pack, ThemePackRules.RuleStrong);

        var dark = pack.Scheme == "dark";
        var strong = pack.HighContrast ? rs : r; // the border of things that are pressed, typed into or ticked
        var none = Colors.Transparent;
        var field = dark ? g : s; // a box is drawn deeper than the page in a dark theme and as a white sheet in a light one
        var quiet = Mix(s, a, 0.5);

        var d = new ResourceDictionary();
        void Set(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            d[key] = brush;
        }

        // Text
        Set("TextFillColorPrimaryBrush", t);
        Set("TextFillColorSecondaryBrush", pack.HighContrast ? t : Mix(t, m, 0.4));
        Set("TextFillColorTertiaryBrush", m);
        Set("TextFillColorDisabledBrush", Mix(m, s, 0.45));

        // Surfaces
        Set("AppWindowBrush", s);
        Set("AppNavPaneBrush", n);
        Set("AppNavSelectedBorderBrush", none);
        Set("SolidBackgroundFillColorBaseBrush", quiet);
        Set("LayerFillColorDefaultBrush", g);
        Set("CardBackgroundFillColorDefaultBrush", a);
        Set("CardStrokeColorDefaultBrush", pack.HighContrast ? rs : r);
        Set("DividerStrokeColorDefaultBrush", r);
        Set("ControlStrongStrokeColorDefaultBrush", rs);
        Set("SubtleFillColorSecondaryBrush", a);
        Set("ControlFillColorSecondaryBrush", Toward(a, t, 0.12));
        Set("ComboBoxDropDownBackground", quiet);
        Set("ComboBoxDropDownBorderBrush", rs);

        // Buttons
        Set("CtlButtonBackground", a);
        Set("CtlButtonBorder", strong);
        Set("CtlButtonInnerBorder", none);
        Set("CtlButtonHoverBackground", Toward(a, t, 0.10));
        Set("CtlButtonHoverBorder", rs);
        Set("CtlButtonPressedBackground", Toward(a, t, 0.18));
        Set("CtlButtonPressedBorder", strong);
        Set("CtlButtonFocusBorder", t);
        Set("CtlButtonDisabledBackground", quiet);
        Set("CtlButtonDisabledBorder", none);

        // Boxes
        Set("CtlInputBackground", field);
        Set("CtlInputFocusBackground", field);
        Set("CtlInputDisabledBackground", quiet);
        Set("CtlInputBorder", pack.HighContrast ? rs : Mix(rs, r, 0.3));
        Set("CtlInputHoverBorder", t);

        // Tick boxes, radio buttons
        Set("CtlGlyphBackground", none);
        Set("CtlGlyphHoverBackground", none);
        Set("CtlGlyphBorder", pack.HighContrast ? t : Mix(t, s, 0.2));
        Set("CtlGlyphHoverBorder", t);

        // Lists
        Set("CtlListHoverBackground", a);
        Set("CtlListHoverBorder", none);

        // Progress, scroll bars, slider, table
        Set("CtlProgressTrack", Toward(a, t, 0.12));
        Set("CtlProgressBorder", none);
        Set("CtlScrollTrack", none);
        Set("CtlScrollThumb", Mix(m, s, 0.55));
        Set("CtlScrollThumbBorder", none);
        Set("CtlScrollThumbHover", Mix(m, s, 0.3));
        Set("CtlScrollThumbPressed", m);
        Set("CtlScrollArrowBackground", none);
        Set("CtlSliderTrack", rs);
        Set("CtlSliderTrackBorder", none);
        Set("CtlSliderThumb", t);
        Set("CtlSliderThumbBorder", none);
        Set("CtlSliderThumbHover", t);
        Set("CtlSliderThumbPressed", Mix(t, s, 0.2));
        Set("CtlHeaderBackground", none);
        Set("CtlHeaderBorder", pack.HighContrast ? rs : r);

        // Tabs
        Set("AppTabSelectedBrush", none);
        Set("AppTabHoverBrush", none);
        return d;
    }

    private static Color Parse(ThemePackDto pack, string key) =>
        ThemePackRules.TryParseHex(pack.Color(key), out var c) ? Color.FromRgb(c.R, c.G, c.B) : Colors.Gray;

    /// <summary>The colour that is <paramref name="amount"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private static Color Toward(Color from, Color to, double amount) => Mix(from, to, amount);
}
