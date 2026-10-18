using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Infrastructure;

/// <summary>An image file named by the settings and what to tell the client it is.</summary>
public sealed record BrandingFile(string Path, string ContentType);

/// <summary>
/// What the API's <c>Branding</c> settings say, checked once when the API starts: the branding of an instance nobody has changed in
/// the administrators' page (<see cref="BrandingService"/> puts what they changed on top). A setting that cannot be used is logged and
/// left out; branding is never a reason for the API not to start. The image files themselves are read again for every request, so
/// replacing one needs no restart.
/// </summary>
public sealed class BrandingDefaults
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly Dictionary<string, BrandingFile> _files = [];

    public BrandingDefaults(IOptions<BrandingOptions> options, ILogger<BrandingDefaults> log)
    {
        var settings = options.Value;

        Add(BrandingSlots.Logo, Resolve(log, nameof(settings.Logo), settings.Logo));
        Add(BrandingSlots.LogoOnDark, Resolve(log, nameof(settings.LogoOnDark), settings.LogoOnDark));
        Add(BrandingSlots.Icon, Resolve(log, nameof(settings.Icon), settings.Icon));

        ProductName = settings.ProductName.Trim();
        if (ProductName.Length == 0) ProductName = BrandingOptions.DefaultProductName;

        OrganizationName = settings.OrganizationName.Trim();

        DefaultWebTheme = Theme(log, ThemeClients.Web, settings.DefaultWebTheme);
        DefaultWindowsTheme = Theme(log, ThemeClients.Windows, settings.DefaultWindowsTheme);

        Currency = BrandingRules.CheckCurrency(settings.Currency) is null ? settings.Currency.Trim() : BrandingRules.DefaultCurrency;

        AccentColor = settings.AccentColor.Trim();
        if (!BrandingRules.IsAccentColor(AccentColor))
        {
            log.LogWarning("Branding:AccentColor is '{Color}', which is neither #RGB nor #RRGGBB. {Default} is used instead.", settings.AccentColor, BrandingOptions.DefaultAccentColor);
            AccentColor = BrandingOptions.DefaultAccentColor;
        }
    }

    public string DefaultWebTheme { get; }

    public string DefaultWindowsTheme { get; }

    public string ProductName { get; }

    public string OrganizationName { get; }

    public string AccentColor { get; }

    public string Currency { get; }

    /// <summary>The file the settings name for this slot (one of <see cref="BrandingSlots"/>), if it is a usable one.</summary>
    public BrandingFile? FileOf(string slot) => _files.GetValueOrDefault(slot);

    private static string Theme(ILogger log, string client, string? theme)
    {
        if (ThemeRules.IsValid(client, theme?.Trim())) return theme!.Trim();
        if (!string.IsNullOrWhiteSpace(theme)) log.LogWarning("Branding:Default{Client}Theme is '{Theme}', which is no theme of that client. '{Default}' is used instead.", client, theme, ThemeIds.Default);
        return ThemeIds.Default;
    }

    private void Add(string slot, BrandingFile? file)
    {
        if (file is not null) _files[slot] = file;
    }

    private static BrandingFile? Resolve(ILogger log, string setting, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var full = Path.GetFullPath(path.Trim(), AppContext.BaseDirectory);
        if (!File.Exists(full))
        {
            log.LogWarning("Branding:{Setting} is '{Path}', but there is no file {Full}. It is ignored.", setting, path, full);
            return null;
        }

        if (!ContentTypes.TryGetContentType(full, out var contentType) || !contentType.StartsWith("image/", StringComparison.Ordinal))
        {
            log.LogWarning("Branding:{Setting} is '{Path}', which is not an image file (png, jpg, gif, bmp, svg, ico). It is ignored.", setting, path);
            return null;
        }

        return new BrandingFile(full, contentType);
    }
}
