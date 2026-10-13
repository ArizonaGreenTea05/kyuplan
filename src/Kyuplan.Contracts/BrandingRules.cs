namespace Kyuplan.Contracts;

/// <summary>
/// The three images an instance can have (<see cref="BrandingDto"/>), by the name they are served under
/// (<c>api/branding/{name}</c>), and which formats an administrator may upload for each.
/// </summary>
public static class BrandingSlots
{
    /// <summary>Logo for light surfaces. One image for every client, so it is in a format all of them can draw: an SVG or a bitmap.</summary>
    public const string Logo = "logo";

    public const string LogoOnDark = "logo-on-dark";

    /// <summary>Icon of the browser tab.</summary>
    public const string Icon = "icon";

    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Bmp = "image/bmp";
    public const string Svg = "image/svg+xml";
    public const string Ico = "image/x-icon";

    /// <summary>All slots, in the order a page lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Logo, LogoOnDark, Icon];

    private static readonly string[] Logos = [Png, Jpeg, Gif, Bmp, Svg];
    private static readonly string[] Tab = [Png, Svg, Ico];

    /// <summary>The content types an upload for this slot may have, or <c>null</c> when there is no such slot.</summary>
    public static IReadOnlyList<string>? ContentTypes(string? slot) => slot switch
    {
        Logo or LogoOnDark => Logos,
        Icon => Tab,
        _ => null,
    };
}

/// <summary>
/// What an administrator may set of the branding. The API enforces it; the clients check the same rules so that they only offer to
/// save what the API will accept. Each check returns the problem in words, or <c>null</c> when it is fine.
/// </summary>
public static class BrandingRules
{
    public const int MaxProductNameLength = 60;
    public const int MaxOrganizationNameLength = 80;

    /// <summary>The largest image that is accepted: logos and icons are small, and they are kept in the database.</summary>
    public const int MaxImageBytes = 1024 * 1024;

    /// <summary>What an instance uses until somebody says otherwise.</summary>
    public const string DefaultAccentColor = "#2563eb";

    public static string? CheckProductName(string? productName)
    {
        var name = productName?.Trim();
        if (string.IsNullOrEmpty(name)) return "productName is required.";
        if (name.Length > MaxProductNameLength) return $"productName must be at most {MaxProductNameLength} characters long.";
        return name.Any(char.IsControl) ? "productName must not contain control characters." : null;
    }

    /// <summary>The organisation may be empty: an instance without a club name shows only the product name.</summary>
    public static string? CheckOrganizationName(string? organizationName)
    {
        var name = organizationName?.Trim() ?? "";
        if (name.Length > MaxOrganizationNameLength) return $"organizationName must be at most {MaxOrganizationNameLength} characters long.";
        return name.Any(char.IsControl) ? "organizationName must not contain control characters." : null;
    }

    public static string? CheckAccentColor(string? accentColor) =>
        IsAccentColor(accentColor) ? null : "accentColor must be a colour like #RGB or #RRGGBB.";

    /// <summary><c>#RGB</c> or <c>#RRGGBB</c>. The colour ends up in CSS and in brushes, so nothing but a hex colour passes.</summary>
    public static bool IsAccentColor(string? color)
    {
        var text = color?.Trim();
        return text is { Length: 4 or 7 } && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit);
    }
}
