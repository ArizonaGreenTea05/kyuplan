namespace Kyuplan.Contracts;

/// <summary>The kinds of client that have a theme of their own (<see cref="ThemeRules"/>).</summary>
public static class ThemeClients
{
    public const string Web = "web";
    public const string Windows = "windows";

    public static bool IsKnown(string? client) => client is Web or Windows;
}

/// <summary>The ids of the themes. <see cref="Default"/> and the general themes exist in every client, the others in the Windows client only.</summary>
public static class ThemeIds
{
    /// <summary>The look the client has without any theme: the browser's own (light or dark like the system), or the design that fits the installed Windows.</summary>
    public const string Default = "default";

    // General: the web client and the Windows client.
    public const string Dracula = "dracula";
    public const string CatppuccinLatte = "catppuccin-latte";
    public const string CatppuccinMocha = "catppuccin-mocha";
    public const string LiquidGlass = "liquid-glass";

    // Windows client only.
    public const string Windows10 = "windows10";
    public const string Aero = "aero";
    public const string Native = "native";
}

/// <summary>
/// Which themes a client offers and how the one in effect is found. Every user may pick a theme per kind of client (it is kept with
/// the account, so it follows them to another computer); where they have not, the theme an administrator set for that kind of client
/// applies (<see cref="BrandingDto.DefaultWebTheme"/>, <see cref="BrandingDto.DefaultWindowsTheme"/>).
/// </summary>
public static class ThemeRules
{
    /// <summary>Longer than every id; the size of the database columns.</summary>
    public const int MaxLength = 32;

    /// <summary>The themes of both clients, in the order they are offered after <see cref="ThemeIds.Default"/>.</summary>
    public static IReadOnlyList<string> General { get; } =
        [ThemeIds.Dracula, ThemeIds.CatppuccinLatte, ThemeIds.CatppuccinMocha, ThemeIds.LiquidGlass];

    private static IReadOnlyList<string> Web { get; } = [ThemeIds.Default, .. General];

    private static IReadOnlyList<string> Windows { get; } =
        [ThemeIds.Default, ThemeIds.Windows10, ThemeIds.Aero, ThemeIds.Native, .. General];

    /// <summary>The themes a kind of client (<see cref="ThemeClients"/>) offers, <see cref="ThemeIds.Default"/> first.</summary>
    public static IReadOnlyList<string> For(string? client) => client == ThemeClients.Windows ? Windows : Web;

    /// <summary>"light" or "dark" for a theme that has only that one (Dracula is dark, Catppuccin Latte is light, ...); <c>null</c> for a theme
    /// that has both and so follows the user's light/dark switch (or the system).</summary>
    public static string? FixedScheme(string? theme) => theme switch
    {
        ThemeIds.Dracula or ThemeIds.CatppuccinMocha => "dark",
        ThemeIds.CatppuccinLatte or ThemeIds.Aero => "light",
        _ => null,
    };

    public static bool IsValid(string? client, string? theme) => theme is not null && ThemeClients.IsKnown(client) && For(client).Contains(theme);

    /// <summary>A problem with a theme that is to be saved (the account's choice, or an instance's default), or <c>null</c>.</summary>
    public static string? Check(string? client, string? theme, string field) =>
        IsValid(client, theme) ? null : $"{field} must be one of: {string.Join(", ", For(client))}.";

    /// <summary>The theme in effect: the user's own, else the instance's default; <see cref="ThemeIds.Default"/> when neither is a theme this kind of client has.</summary>
    public static string Resolve(string? client, string? userTheme, string? instanceDefault) =>
        IsValid(client, userTheme) ? userTheme! : IsValid(client, instanceDefault) ? instanceDefault! : ThemeIds.Default;
}

/// <summary>What a user sends to choose their theme for a kind of client (<c>PUT /api/account/theme</c>).</summary>
/// <param name="Client">One of <see cref="ThemeClients"/>.</param>
/// <param name="Theme">A theme of that client (<see cref="ThemeRules.For"/>), or <c>null</c> to follow the instance's default again.</param>
public sealed record ThemePreferenceDto(string Client, string? Theme);
