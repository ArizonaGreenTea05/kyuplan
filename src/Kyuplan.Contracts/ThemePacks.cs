namespace Kyuplan.Contracts;

/// <summary>
/// A theme pack: the "plugin" that adds a theme to both clients. It is a small JSON file of colours (<c>GET /api/themes</c> lists the packs,
/// administrators upload and delete them, see <see cref="ThemePackRules"/>). The same colours dress the web client (as CSS variables) and the
/// Windows client (every brush of its flat design is derived from them), so one file is one theme everywhere.
/// </summary>
/// <param name="Id">Lower-case letters, digits and single hyphens, at most <see cref="ThemeRules.MaxLength"/> characters; no id of a built-in theme (<see cref="ThemePackRules.IsPackId"/>).</param>
/// <param name="Name">What the theme is called in lists.</param>
/// <param name="NameDe">The German name; <c>null</c> = <paramref name="Name"/> in every language.</param>
/// <param name="Scheme"><c>light</c> or <c>dark</c>: a pack has one colour scheme, so the light/dark switch does nothing while it is on.</param>
/// <param name="HighContrast">Draws stronger borders, thick focus rings and underlined links (for people who see less).</param>
/// <param name="Accent">The colour of links, focus and selection, <c>#RRGGBB</c>; <c>null</c> = the accent colour of the instance. High contrast themes carry their own, since the colour has to fit their surfaces.</param>
/// <param name="Colors">Every key of <see cref="ThemePackRules.ColorKeys"/> as <c>#RRGGBB</c>.</param>
/// <param name="BuiltIn">Ships with the program and cannot be deleted. Ignored in an upload.</param>
public sealed record ThemePackDto(
    string Id,
    string Name,
    string? NameDe,
    string Scheme,
    bool HighContrast,
    string? Accent,
    IReadOnlyDictionary<string, string> Colors,
    bool BuiltIn = false)
{
    public string Color(string key) => Colors.TryGetValue(key, out var value) ? value : "#808080";
}

/// <summary>All installed theme packs. <paramref name="Version"/> changes whenever a pack is added, replaced or deleted (it is also in <see cref="BrandingDto.ThemesVersion"/>).</summary>
public sealed record ThemeCatalogDto(string Version, IReadOnlyList<ThemePackDto> Packs);

/// <summary>The colours of a pack, the rules for them, and the contrast the packs have to keep.</summary>
public static class ThemePackRules
{
    // The keys of ThemePackDto.Colors.
    public const string Ground = "ground";           // behind everything
    public const string Sheet = "sheet";             // pages, cards, boxes
    public const string SheetAlt = "sheetAlt";       // zebra rows, hovered things, quiet fills
    public const string Sidebar = "sidebar";         // navigation
    public const string Text = "text";
    public const string Muted = "muted";             // secondary text
    public const string Rule = "rule";               // thin lines
    public const string RuleStrong = "ruleStrong";   // borders of boxes and buttons
    public const string Amber = "amber";             // "below the bar / open"
    public const string AmberWash = "amberWash";
    public const string Red = "red";                 // trouble
    public const string RedWash = "redWash";

    public static IReadOnlyList<string> ColorKeys { get; } =
        [Ground, Sheet, SheetAlt, Sidebar, Text, Muted, Rule, RuleStrong, Amber, AmberWash, Red, RedWash];

    public const int MaxNameLength = 40;

    /// <summary>A pack is a few hundred bytes; anything larger is no theme.</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>The smallest contrast (WCAG) the text has to have on the page, on a card and on the navigation.</summary>
    public const double MinTextContrast = 4.5;

    /// <summary>The same for secondary text, which is smaller and quieter but must stay readable.</summary>
    public const double MinMutedContrast = 3.0;

    /// <summary>The smallest contrast of a high contrast theme's text (WCAG AAA).</summary>
    public const double HighContrastTextContrast = 7.0;

    /// <summary>The ids of the themes the clients draw themselves; a pack cannot take one.</summary>
    private static readonly HashSet<string> Reserved =
    [
        ThemeIds.Default, ThemeIds.Dracula, ThemeIds.CatppuccinLatte, ThemeIds.CatppuccinMocha, ThemeIds.LiquidGlass,
        ThemeIds.Windows10, ThemeIds.Aero, ThemeIds.Native,
    ];

    /// <summary>The id is spelled like a pack's and is no theme of the clients' own.</summary>
    public static bool IsPackId(string? id) =>
        id is { Length: > 0 and <= ThemeRules.MaxLength } && !Reserved.Contains(id) && IsSlug(id);

    private static bool IsSlug(string id)
    {
        var previousHyphen = true; // no leading hyphen
        foreach (var c in id)
        {
            var hyphen = c == '-';
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' || hyphen) || (hyphen && previousHyphen)) return false;
            previousHyphen = hyphen;
        }

        return !previousHyphen;
    }

    /// <summary><c>#RRGGBB</c> (any case) as its three channels.</summary>
    public static bool TryParseHex(string? value, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (value is not { Length: 7 } || value[0] != '#') return false;
        if (!byte.TryParse(value.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(value.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(value.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return false;

        rgb = (r, g, b);
        return true;
    }

    /// <summary>The WCAG contrast ratio of two colours, 1 (equal) to 21 (black on white); 1 for something that is no colour.</summary>
    public static double Contrast(string? a, string? b)
    {
        if (!TryParseHex(a, out var x) || !TryParseHex(b, out var y)) return 1;
        double la = Luminance(x), lb = Luminance(y);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance((byte R, byte G, byte B) c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>Black or white, whichever reads better on this colour (the text of a button in the accent colour).</summary>
    public static string OnColor(string? background) => Contrast(background, "#000000") >= Contrast(background, "#ffffff") ? "#000000" : "#ffffff";

    /// <summary>A problem with a pack, or <c>null</c> when it can be installed.</summary>
    public static string? Check(ThemePackDto? pack)
    {
        if (pack is null) return "The theme pack is empty.";
        if (!IsPackId(pack.Id))
            return $"id must be lower-case letters, digits and single hyphens (at most {ThemeRules.MaxLength} characters) and not the id of a built-in theme.";
        if (string.IsNullOrWhiteSpace(pack.Name) || pack.Name.Length > MaxNameLength || pack.Name.Any(char.IsControl))
            return $"name is required and at most {MaxNameLength} characters.";
        if (pack.NameDe is not null && (pack.NameDe.Length > MaxNameLength || pack.NameDe.Any(char.IsControl)))
            return $"nameDe is at most {MaxNameLength} characters.";
        if (pack.Scheme is not ("light" or "dark")) return "scheme must be light or dark.";
        if (pack.Accent is not null && !TryParseHex(pack.Accent, out _)) return "accent must be #RRGGBB.";

        foreach (var key in ColorKeys)
        {
            if (!pack.Colors.TryGetValue(key, out var value)) return $"colors.{key} is missing.";
            if (!TryParseHex(value, out _)) return $"colors.{key} must be #RRGGBB.";
        }

        if (pack.Colors.Keys.FirstOrDefault(k => !ColorKeys.Contains(k)) is { } unknown) return $"colors.{unknown} is not a colour of a theme.";

        var wanted = pack.HighContrast ? HighContrastTextContrast : MinTextContrast;
        foreach (var surface in new[] { Ground, Sheet, Sidebar })
        {
            if (Contrast(pack.Color(Text), pack.Color(surface)) < wanted)
                return $"The text needs a contrast of at least {wanted:0.#}:1 on {surface} (it has {Contrast(pack.Color(Text), pack.Color(surface)):0.0}:1).";
        }

        if (Contrast(pack.Color(Muted), pack.Color(Sheet)) < (pack.HighContrast ? MinTextContrast : MinMutedContrast))
            return "muted is too faint on sheet.";
        if (pack.Accent is not null && Contrast(pack.Accent, pack.Color(Sheet)) < MinMutedContrast)
            return "accent is too faint on sheet.";

        return null;
    }
}
