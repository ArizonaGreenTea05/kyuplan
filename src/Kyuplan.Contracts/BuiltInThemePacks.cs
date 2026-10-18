namespace Kyuplan.Contracts;

/// <summary>
/// The theme packs that ship with the program (they cannot be deleted; an upload cannot take their ids). They are packs like any
/// uploaded one, so they show how a pack is made: the three high contrast themes for people who see less (WCAG AAA text contrast, strong
/// borders, thick focus rings, underlined links), and some popular palettes.
/// </summary>
public static class BuiltInThemePacks
{
    public const string HighContrastDark = "high-contrast-dark";
    public const string HighContrastLight = "high-contrast-light";
    public const string HighContrastYellow = "high-contrast-yellow";

    public static IReadOnlyList<ThemePackDto> All { get; } =
    [
        // Accessibility first: they are offered above the other packs.
        Pack(HighContrastDark, "High contrast (dark)", "Hoher Kontrast (dunkel)", "dark", true, "#5cc8ff",
            "#000000", "#000000", "#1a1a1a", "#000000", "#ffffff", "#d4d4d4", "#8c8c8c", "#ffffff", "#ffd400", "#3b3000", "#ff8a8a", "#4a0000"),
        Pack(HighContrastLight, "High contrast (light)", "Hoher Kontrast (hell)", "light", true, "#0033cc",
            "#ffffff", "#ffffff", "#eeeeee", "#ffffff", "#000000", "#333333", "#595959", "#000000", "#6b4500", "#ffe7a3", "#a60000", "#ffd6d6"),
        Pack(HighContrastYellow, "High contrast (yellow on black)", "Hoher Kontrast (gelb auf schwarz)", "dark", true, "#00ffff",
            "#000000", "#000000", "#1f1f00", "#000000", "#ffff00", "#e6e64d", "#a6a600", "#ffff00", "#ff9d00", "#332000", "#ff7a7a", "#400000"),

        Pack("nord", "Nord", null, "dark", false, null,
            "#2e3440", "#3b4252", "#434c5e", "#2b303b", "#eceff4", "#aeb7c8", "#4c566a", "#6b7790", "#ebcb8b", "#4a4332", "#ff9ba3", "#4f3a40"),
        Pack("solarized-dark", "Solarized dark", "Solarized dunkel", "dark", false, null,
            "#002b36", "#073642", "#0b4251", "#00212b", "#eee8d5", "#93a1a1", "#174a58", "#586e75", "#d9a521", "#3a3a14", "#f06a67", "#3d2a2b"),
        Pack("solarized-light", "Solarized light", "Solarized hell", "light", false, null,
            "#eee8d5", "#fdf6e3", "#f5eed9", "#eee8d5", "#073642", "#586e75", "#ddd6c1", "#b5ad98", "#8a6500", "#f3e3b3", "#c0211f", "#f8dcd3"),
        Pack("gruvbox-dark", "Gruvbox dark", "Gruvbox dunkel", "dark", false, null,
            "#1d2021", "#282828", "#32302f", "#1b1b1b", "#ebdbb2", "#a89984", "#3c3836", "#665c54", "#fabd2f", "#3d3318", "#fb6a58", "#3f2220"),
        Pack("gruvbox-light", "Gruvbox light", "Gruvbox hell", "light", false, null,
            "#ebdbb2", "#fbf1c7", "#f2e5bc", "#ebdbb2", "#3c3836", "#665c54", "#d5c4a1", "#a89984", "#8f5a00", "#f4dd9a", "#9d0006", "#f5cdb8"),
        Pack("tokyo-night", "Tokyo Night", null, "dark", false, null,
            "#16161e", "#1a1b26", "#24283b", "#13131a", "#c0caf5", "#9aa5ce", "#2f334d", "#565f89", "#e0af68", "#38301f", "#f7768e", "#3b2330"),
    ];

    public static bool Contains(string? id) => All.Any(p => p.Id == id);

    private static ThemePackDto Pack(
        string id, string name, string? nameDe, string scheme, bool highContrast, string? accent,
        string ground, string sheet, string sheetAlt, string sidebar, string text, string muted,
        string rule, string ruleStrong, string amber, string amberWash, string red, string redWash) =>
        new(id, name, nameDe, scheme, highContrast, accent,
            new Dictionary<string, string>
            {
                [ThemePackRules.Ground] = ground, [ThemePackRules.Sheet] = sheet, [ThemePackRules.SheetAlt] = sheetAlt,
                [ThemePackRules.Sidebar] = sidebar, [ThemePackRules.Text] = text, [ThemePackRules.Muted] = muted,
                [ThemePackRules.Rule] = rule, [ThemePackRules.RuleStrong] = ruleStrong,
                [ThemePackRules.Amber] = amber, [ThemePackRules.AmberWash] = amberWash,
                [ThemePackRules.Red] = red, [ThemePackRules.RedWash] = redWash,
            },
            BuiltIn: true);
}
