namespace Kyuplan.Contracts;

/// <summary>
/// How this instance presents itself: what the API's <c>Branding</c> settings say. Public, so that a client can show it before
/// anybody has signed in. Every url is relative to the API's address (<c>api/branding/logo?v=1a2b</c>) and is <c>null</c> when the
/// instance has no such image. The <c>v</c> changes whenever the image behind the address does, so a client that asks again
/// (<c>BrandingViewModel</c> does, regularly) finds out that anything changed by comparing this record with the one it has.
/// </summary>
/// <param name="ProductName">Name of the application, shown in the navigation and in the title of windows and tabs.</param>
/// <param name="OrganizationName">Name of the club or organisation; empty when there is none.</param>
/// <param name="AccentColor">Accent colour of the web client as <c>#RGB</c> or <c>#RRGGBB</c>.</param>
/// <param name="LogoUrl">Logo for light surfaces: an SVG, or a bitmap (PNG, JPEG, GIF, BMP). Both clients draw all of them.</param>
/// <param name="LogoOnDarkUrl">Logo for dark surfaces; the light one when the instance has no other.</param>
/// <param name="IconUrl">Icon of the browser tab.</param>
/// <param name="DefaultWebTheme">The theme (<see cref="ThemeRules"/>) the web client shows to users who picked none themselves.</param>
/// <param name="DefaultWindowsTheme">The same for the Windows client.</param>
/// <param name="ThemesVersion">Changes whenever a theme pack is installed, replaced or deleted (<see cref="ThemeCatalogDto.Version"/>): a client that
/// sees it differ asks for the catalog again.</param>
public sealed record BrandingDto(
    string ProductName,
    string OrganizationName,
    string AccentColor,
    string? LogoUrl,
    string? LogoOnDarkUrl,
    string? IconUrl,
    string DefaultWebTheme = ThemeIds.Default,
    string DefaultWindowsTheme = ThemeIds.Default,
    string ThemesVersion = "");

/// <summary>What an administrator saves of the branding's texts and colour. The images are uploaded one by one.</summary>
/// <param name="ProductName">Required.</param>
/// <param name="OrganizationName">May be empty.</param>
/// <param name="AccentColor"><c>#RGB</c> or <c>#RRGGBB</c>.</param>
/// <param name="DefaultWebTheme">A theme of the web client; <c>null</c> keeps the saved one.</param>
/// <param name="DefaultWindowsTheme">A theme of the Windows client; <c>null</c> keeps the saved one.</param>
public sealed record BrandingInputDto(
    string ProductName, string OrganizationName, string AccentColor,
    string? DefaultWebTheme = null, string? DefaultWindowsTheme = null);

/// <summary>One image slot as the administrators' page shows it.</summary>
/// <param name="Slot">The name of the slot, one of <see cref="BrandingSlots"/>.</param>
/// <param name="Url">The slot's own image (relative to the API's address), or <c>null</c> when it has none: the clients may then show the
/// image of another slot in its place (a missing dark logo is the light one), see <see cref="BrandingDto"/>.</param>
public sealed record BrandingImageDto(string Slot, string? Url);

/// <summary>
/// The branding as administrators edit it: what the clients are told (<see cref="BrandingDto"/>), but with each slot's own image only,
/// and whether anything was changed here at all.
/// </summary>
/// <param name="IsCustomized">An administrator saved something here. The texts and the colour are then the saved ones (they are saved
/// together), and each image is the uploaded one, none, or, where nobody touched it, what the API's <c>Branding</c> settings say.
/// While this is <c>false</c> everything is what those settings say.</param>
public sealed record BrandingSettingsDto(
    string ProductName,
    string OrganizationName,
    string AccentColor,
    bool IsCustomized,
    IReadOnlyList<BrandingImageDto> Images,
    string DefaultWebTheme = ThemeIds.Default,
    string DefaultWindowsTheme = ThemeIds.Default);
