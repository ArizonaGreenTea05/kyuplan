using System.Globalization;
using System.Text;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp.Services;

/// <summary>
/// Turns a theme into the stylesheet GTK draws the program with. Every theme that is not the system's own look is a colour set of twelve
/// colours (<see cref="ThemePackDto"/>): the installed theme packs, and the themes the clients draw themselves (Dracula, Catppuccin), which
/// are packs here too. The colours replace the named colours libadwaita derives all its widgets from (<c>window_bg_color</c> and friends), so
/// one small set dresses every control; the accent colour (of the instance, or of a high contrast pack) is applied on top of any theme.
/// </summary>
public static class ThemePalette
{
    /// <summary>The themes of <see cref="ThemeRules.General"/> as colour sets; <c>null</c> for the ones that have none (the system's look, Liquid Glass, which is the system's look with the accent).</summary>
    public static ThemePackDto? BuiltIn(string id) => id switch
    {
        ThemeIds.Dracula => Pack(id, "Dracula", "dark",
            ground: "#1E1F29", sheet: "#282A36", sheetAlt: "#343746", sidebar: "#21222C", text: "#F8F8F2", muted: "#C5C8D9",
            rule: "#44475A", ruleStrong: "#6272A4", amber: "#FFB86C", amberWash: "#3A3226", red: "#FF5555", redWash: "#3D2530"),
        ThemeIds.CatppuccinMocha => Pack(id, "Catppuccin Mocha", "dark",
            ground: "#181825", sheet: "#1E1E2E", sheetAlt: "#313244", sidebar: "#11111B", text: "#CDD6F4", muted: "#A6ADC8",
            rule: "#45475A", ruleStrong: "#6C7086", amber: "#F9E2AF", amberWash: "#3A3528", red: "#F38BA8", redWash: "#3B2833"),
        ThemeIds.CatppuccinLatte => Pack(id, "Catppuccin Latte", "light",
            ground: "#E6E9EF", sheet: "#EFF1F5", sheetAlt: "#DCE0E8", sidebar: "#DCE0E8", text: "#4C4F69", muted: "#5C5F77",
            rule: "#CCD0DA", ruleStrong: "#9CA0B0", amber: "#B86E00", amberWash: "#F6E9D2", red: "#D20F39", redWash: "#F6D5DB"),
        _ => null,
    };

    private static ThemePackDto Pack(string id, string name, string scheme, string ground, string sheet, string sheetAlt, string sidebar, string text,
        string muted, string rule, string ruleStrong, string amber, string amberWash, string red, string redWash) =>
        new(id, name, null, scheme, false, null, new Dictionary<string, string>
        {
            [ThemePackRules.Ground] = ground, [ThemePackRules.Sheet] = sheet, [ThemePackRules.SheetAlt] = sheetAlt, [ThemePackRules.Sidebar] = sidebar,
            [ThemePackRules.Text] = text, [ThemePackRules.Muted] = muted, [ThemePackRules.Rule] = rule, [ThemePackRules.RuleStrong] = ruleStrong,
            [ThemePackRules.Amber] = amber, [ThemePackRules.AmberWash] = amberWash, [ThemePackRules.Red] = red, [ThemePackRules.RedWash] = redWash,
        }, BuiltIn: true);

    /// <summary>
    /// The stylesheet: the status colours and chips every theme needs, the accent, and (for a <paramref name="pack"/>) the twelve colours.
    /// <paramref name="fallbackAmber"/>/<paramref name="fallbackRed"/> are the status colours of the system look.
    /// </summary>
    public static string Css(ThemePackDto? pack, string accent, bool dark)
    {
        var css = new StringBuilder();
        var effectiveAccent = pack?.Accent is { } own && ThemePackRules.TryParseHex(own, out _) ? own : ThemePackRules.TryParseHex(accent, out _) ? accent : "#2563eb";
        var onAccent = Contrast(effectiveAccent, "#FFFFFF") >= Contrast(effectiveAccent, "#000000") ? "#FFFFFF" : "#000000";

        // The accent: links, focus rings, the selected row and every "suggested" button.
        css.AppendLine($"@define-color accent_bg_color {effectiveAccent};");
        css.AppendLine($"@define-color accent_fg_color {onAccent};");
        css.AppendLine($"@define-color accent_color {Readable(effectiveAccent, pack?.Color(ThemePackRules.Sheet) ?? (dark ? "#242424" : "#FFFFFF"))};");

        string amber, amberWash, red, redWash, muted, rule;
        if (pack is null)
        {
            amber = dark ? "#F5C211" : "#9C6500";
            amberWash = dark ? "#3A3220" : "#FCF1D5";
            red = dark ? "#FF7B63" : "#C01C28";
            redWash = dark ? "#3D2622" : "#F9DADA";
            muted = dark ? "#C0BFBC" : "#5E5C64";
            rule = dark ? "#454545" : "#D5D5D5";
        }
        else
        {
            amber = pack.Color(ThemePackRules.Amber);
            amberWash = pack.Color(ThemePackRules.AmberWash);
            red = pack.Color(ThemePackRules.Red);
            redWash = pack.Color(ThemePackRules.RedWash);
            muted = pack.Color(ThemePackRules.Muted);
            rule = pack.Color(ThemePackRules.Rule);

            var ground = pack.Color(ThemePackRules.Ground);
            var sheet = pack.Color(ThemePackRules.Sheet);
            var alt = pack.Color(ThemePackRules.SheetAlt);
            var sidebar = pack.Color(ThemePackRules.Sidebar);
            var text = pack.Color(ThemePackRules.Text);
            var strong = pack.Color(ThemePackRules.RuleStrong);

            css.AppendLine($"@define-color window_bg_color {ground};");
            css.AppendLine($"@define-color window_fg_color {text};");
            css.AppendLine($"@define-color view_bg_color {sheet};");
            css.AppendLine($"@define-color view_fg_color {text};");
            css.AppendLine($"@define-color card_bg_color {sheet};");
            css.AppendLine($"@define-color card_fg_color {text};");
            css.AppendLine($"@define-color popover_bg_color {sheet};");
            css.AppendLine($"@define-color popover_fg_color {text};");
            css.AppendLine($"@define-color dialog_bg_color {sheet};");
            css.AppendLine($"@define-color dialog_fg_color {text};");
            css.AppendLine($"@define-color headerbar_bg_color {ground};");
            css.AppendLine($"@define-color headerbar_fg_color {text};");
            css.AppendLine($"@define-color headerbar_border_color {rule};");
            css.AppendLine($"@define-color sidebar_bg_color {sidebar};");
            css.AppendLine($"@define-color sidebar_fg_color {text};");
            css.AppendLine($"@define-color sidebar_backdrop_color {sidebar};");
            css.AppendLine($"@define-color sidebar_border_color {rule};");
            css.AppendLine($"@define-color warning_bg_color {amber};");
            css.AppendLine($"@define-color warning_color {amber};");
            css.AppendLine($"@define-color destructive_bg_color {red};");
            css.AppendLine($"@define-color destructive_color {red};");
            css.AppendLine($"@define-color error_bg_color {red};");
            css.AppendLine($"@define-color error_color {red};");
            css.AppendLine($"@define-color shade_color alpha({text}, 0.07);");
            css.AppendLine($"@define-color borders {rule};");
            // Buttons, entries and other controls are drawn from the window colour and a veil of the text colour; the alt sheet colour tints the veil.
            css.AppendLine($"button, entry, spinbutton, dropdown > button, .boxed-list {{ border-color: {strong}; }}");
            css.AppendLine($"button:not(.suggested-action):not(.destructive-action):not(.flat) {{ background-color: {alt}; color: {text}; }}");
            css.AppendLine($"row:hover, .navigation-sidebar row:hover {{ background-color: {alt}; }}");
            css.AppendLine($".dim-label, .caption-heading {{ color: {muted}; opacity: 1; }}");
            if (pack.HighContrast)
            {
                css.AppendLine($"button, entry, .card, .boxed-list, row:focus {{ border: 2px solid {strong}; }}");
                css.AppendLine($"*:focus-visible {{ outline: 3px solid {text}; outline-offset: 2px; }}");
                css.AppendLine("label a, button.link { text-decoration: underline; }");
            }
        }

        foreach (var pad in new[] { 6, 8, 10, 12, 14, 16 }) css.AppendLine($".pad-{pad} {{ padding: {pad}px; }}");

        // What the program itself draws: tags and statuses ("below the bar", "trouble"), the bars of the statistics, the marker of a demo instance.
        css.AppendLine($".chip {{ padding: 1px 8px; border-radius: 99px; background: alpha({muted}, 0.16); }}");
        css.AppendLine($".chip-warn {{ background: {amberWash}; color: {amber}; }}");
        css.AppendLine($".chip-bad {{ background: {redWash}; color: {red}; }}");
        css.AppendLine($".chip-ok {{ background: alpha(@accent_bg_color, 0.18); color: @accent_color; }}");
        css.AppendLine(".good-text { color: @accent_color; }");
        css.AppendLine($".warn-text {{ color: {amber}; }}");
        css.AppendLine($".bad-text {{ color: {red}; }}");
        css.AppendLine($".bar-track {{ background: alpha({muted}, 0.22); border-radius: 4px; min-height: 8px; }}");
        css.AppendLine(".legend-swatch { border-radius: 2px; }");
        css.AppendLine($".legend-swatch.bar-yes-swatch {{ background: @accent_bg_color; }}");
        css.AppendLine($".legend-swatch.bar-no-swatch {{ background: {red}; }}");
        css.AppendLine($".legend-swatch.bar-open-swatch {{ background: alpha({muted}, 0.4); }}");
        css.AppendLine(".bar-fill { border-radius: 4px 4px 0 0; min-width: 14px; }");
        css.AppendLine(".bar-fill.highlight { background: @accent_bg_color; }");
        css.AppendLine(".bar-fill.plain { background: alpha(@accent_bg_color, 0.55); }");
        css.AppendLine("progressbar.rate-bar trough, progressbar.stacked-bar trough { min-height: 8px; }");
        css.AppendLine("progressbar.rate-bar progress, progressbar.stacked-bar progress { min-height: 8px; background: @accent_bg_color; }");
        css.AppendLine($"progressbar.rate-bar.warn progress {{ background: {amber}; }}");
        css.AppendLine($"progressbar.rate-bar.bad progress {{ background: {red}; }}");
        css.AppendLine("progressbar.stacked-bar.bar-open trough { background: alpha(currentColor, 0.18); }");
        css.AppendLine("progressbar.stacked-bar.bar-open progress { background: transparent; }");
        css.AppendLine("progressbar.stacked-bar.bar-no trough, progressbar.stacked-bar.bar-yes trough { background: transparent; }");
        css.AppendLine($"progressbar.stacked-bar.bar-no progress {{ background: {red}; }}");
        css.AppendLine("progressbar.stacked-bar.bar-yes progress { background: @accent_bg_color; }");
        css.AppendLine($".bar-fill.warn {{ background: {amber}; }}");
        css.AppendLine($".bar-fill.bad {{ background: {red}; }}");
        css.AppendLine($".demo-chip {{ background: {amberWash}; color: {amber}; border-radius: 99px; padding: 2px 10px; font-weight: bold; }}");
        css.AppendLine($".rule-line {{ border-top: 1px solid {rule}; }}");
        css.AppendLine($".banner-event {{ background: @accent_bg_color; color: @accent_fg_color; padding: 8px 18px; }}");
        css.AppendLine($".banner-event label, .banner-event button {{ color: @accent_fg_color; }}");
        css.AppendLine($".banner-problem {{ background: {redWash}; color: {red}; padding: 8px 18px; }}");
        css.AppendLine(".table-head { font-weight: bold; }");
        css.AppendLine($".table-row {{ border-bottom: 1px solid {rule}; padding: 6px 4px; }}");
        css.AppendLine($".calendar-cell {{ border: 1px solid {rule}; border-radius: 6px; padding: 4px; }}");
        css.AppendLine($".calendar-cell.today {{ border-color: @accent_bg_color; border-width: 2px; }}");
        css.AppendLine(".calendar-cell.other-month { opacity: 0.5; }");
        return css.ToString();
    }

    /// <summary>The accent as text on <paramref name="background"/>: the accent itself when it is readable there, else a lighter or darker one.</summary>
    public static string Readable(string accent, string background)
    {
        if (!ThemePackRules.TryParseHex(accent, out var c)) return accent;

        var (r, g, b) = (c.R, c.G, c.B);
        for (var i = 0; i < 12 && ThemePackRules.Contrast(Hex(r, g, b), background) < 4.5; i++)
        {
            var towardsWhite = Luminance(background) < 0.5;
            var target = towardsWhite ? 255 : 0;
            r = (byte)(r + (target - r) / 5);
            g = (byte)(g + (target - g) / 5);
            b = (byte)(b + (target - b) / 5);
        }

        return Hex(r, g, b);
    }

    private static string Hex(byte r, byte g, byte b) => string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");

    private static double Luminance(string hex) => ThemePackRules.Contrast(hex, "#000000") > 10 ? 1 : 0;

    private static double Contrast(string a, string b) => ThemePackRules.Contrast(a, b);
}
