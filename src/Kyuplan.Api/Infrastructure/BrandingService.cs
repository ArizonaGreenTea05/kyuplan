using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Infrastructure;

/// <summary>An image to answer a request with: a file the settings name, or the bytes an administrator uploaded.</summary>
/// <param name="Path">The file, when it is one of the settings'; <c>null</c> when <paramref name="Bytes"/> are the image.</param>
/// <param name="Updated">When the uploaded image was saved; meaningless for a file, whose own time counts.</param>
public sealed record BrandingContent(string ContentType, string? Path, byte[]? Bytes, DateTimeOffset Updated);

/// <summary>
/// What the clients are told about the instance, and how administrators change it. The basis is what the API's <c>Branding</c>
/// settings say (<see cref="BrandingDefaults"/>); what administrators saved is put on top: the texts and the colour together, each
/// image slot on its own (an uploaded image, or "none" even where the settings name a file). Nothing is cached, so a change is in
/// every answer from the next request on. A client that has the old branding keeps it until it asks again.
/// </summary>
public sealed class BrandingService(PlannerDbContext db, BrandingDefaults defaults, TimeProvider time)
{
    /// <summary>
    /// Where the branding is served. Reading it (<c>GET</c> of the route itself and of the images) is open to everybody, see
    /// <see cref="ApiKeyMiddleware"/>; everything else below it is for administrators.
    /// </summary>
    public const string Route = "/api/branding";

    /// <summary>The administrators' view of the branding, below <see cref="Route"/>.</summary>
    public const string SettingsRoute = Route + "/settings";

    /// <summary>
    /// One slot's own image: a file of the settings (<see cref="Path"/>) or one in the database. <paramref name="Version"/> says which
    /// image it is: it changes whenever the image does (another upload, a replaced file), and the clients put it into the image's address.
    /// </summary>
    private sealed record Own(string ContentType, string? Path, string Version);

    /// <summary>What is in effect right now, read from the database once per request.</summary>
    private sealed record State(CustomBranding? Texts, bool IsCustomized, IReadOnlyDictionary<string, Own?> Slots);

    // --- Reading -------------------------------------------------------------------------------------------------

    /// <summary>What the clients are told.</summary>
    public async Task<BrandingDto> GetAsync(CancellationToken cancellationToken)
    {
        var state = await ReadAsync(cancellationToken);

        // The address of an image never changes, so the version of the image that is shown there is part of it (a query the route ignores):
        // a client that compares two answers, or a browser that caches by address, notices a swapped logo.
        string? Url(string name) => ServedFrom(state, name) is { } slot ? $"{ImageUrl(name)}?v={state.Slots[slot]!.Version}" : null;

        return new BrandingDto(
            ProductName(state), OrganizationName(state), AccentColor(state),
            Url(BrandingSlots.Logo), Url(BrandingSlots.LogoOnDark), Url(BrandingSlots.Icon),
            DefaultWebTheme(state), DefaultWindowsTheme(state));
    }

    /// <summary>What administrators edit: the same, with each slot's own image only.</summary>
    public async Task<BrandingSettingsDto> GetSettingsAsync(CancellationToken cancellationToken) =>
        ToSettings(await ReadAsync(cancellationToken));

    /// <summary>The image of that name (one of <see cref="BrandingSlots"/>) that is shown in its place, or <c>null</c> when there is none.</summary>
    public async Task<BrandingContent?> GetImageAsync(string name, CancellationToken cancellationToken)
    {
        var state = await ReadAsync(cancellationToken);
        if (ServedFrom(state, name) is not { } slot) return null;

        var own = state.Slots[slot]!;
        if (own.Path is not null) return new BrandingContent(own.ContentType, own.Path, null, default);

        var row = await db.CustomBrandingImages.AsNoTracking().SingleOrDefaultAsync(i => i.Slot == slot, cancellationToken);
        return row?.Content is { } bytes ? new BrandingContent(row.ContentType!, null, bytes, row.UpdatedAt) : null;
    }

    // --- Changing ------------------------------------------------------------------------------------------------

    /// <summary>Saves the texts and the colour. Input is expected to be validated by the endpoint (<see cref="BrandingRules"/>).</summary>
    public async Task<BrandingSettingsDto> SaveAsync(BrandingInputDto input, CancellationToken cancellationToken)
    {
        var row = await db.CustomBranding.SingleOrDefaultAsync(cancellationToken);
        if (row is null) db.CustomBranding.Add(row = new CustomBranding());

        row.ProductName = input.ProductName.Trim();
        row.OrganizationName = input.OrganizationName.Trim();
        row.AccentColor = input.AccentColor.Trim();
        if (input.DefaultWebTheme is not null) row.DefaultWebTheme = input.DefaultWebTheme;
        if (input.DefaultWindowsTheme is not null) row.DefaultWindowsTheme = input.DefaultWindowsTheme;
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        return await GetSettingsAsync(cancellationToken);
    }

    /// <summary>
    /// Stores an image for a slot. The bytes must be an image of a format the slot takes (<see cref="BrandingSlots.ContentTypes"/>) and
    /// at most <see cref="BrandingRules.MaxImageBytes"/>, else <see cref="InvalidInputException"/>.
    /// </summary>
    public async Task<BrandingSettingsDto> SetImageAsync(string slot, byte[] content, CancellationToken cancellationToken)
    {
        var allowed = BrandingSlots.ContentTypes(slot) ?? throw new ArgumentOutOfRangeException(nameof(slot), slot, "There is no such image slot.");
        if (content.Length > BrandingRules.MaxImageBytes)
            throw new InvalidInputException($"The image is larger than {BrandingRules.MaxImageBytes / 1024} KB.");

        var contentType = BrandingImageFormats.Detect(content);
        if (contentType is null || !allowed.Contains(contentType))
            throw new InvalidInputException($"The image for '{slot}' must be {string.Join(", ", allowed.Select(BrandingImageFormats.Name))}.");

        var row = await RowOfAsync(slot, cancellationToken);
        row.ContentType = contentType;
        row.Content = content;
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        return await GetSettingsAsync(cancellationToken);
    }

    /// <summary>Takes the slot's image away: the slot has none from now on, even if the settings name a file.</summary>
    public async Task<BrandingSettingsDto> RemoveImageAsync(string slot, CancellationToken cancellationToken)
    {
        if (BrandingSlots.ContentTypes(slot) is null) throw new ArgumentOutOfRangeException(nameof(slot), slot, "There is no such image slot.");

        if (defaults.FileOf(slot) is null)
        {
            // The settings have no image for it either, so "none" is what no row at all says.
            if (await db.CustomBrandingImages.SingleOrDefaultAsync(i => i.Slot == slot, cancellationToken) is { } row) db.CustomBrandingImages.Remove(row);
        }
        else
        {
            var row = await RowOfAsync(slot, cancellationToken);
            row.ContentType = null;
            row.Content = null;
            row.UpdatedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetSettingsAsync(cancellationToken);
    }

    /// <summary>Forgets everything administrators saved: the API's <c>Branding</c> settings apply again.</summary>
    public async Task<BrandingSettingsDto> ResetAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.CustomBrandingImages.ExecuteDeleteAsync(cancellationToken);
        await db.CustomBranding.ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetSettingsAsync(cancellationToken);
    }

    // --- What is in effect ---------------------------------------------------------------------------------------

    private async Task<CustomBrandingImage> RowOfAsync(string slot, CancellationToken cancellationToken)
    {
        var row = await db.CustomBrandingImages.SingleOrDefaultAsync(i => i.Slot == slot, cancellationToken);
        if (row is null) db.CustomBrandingImages.Add(row = new CustomBrandingImage { Slot = slot });
        return row;
    }

    private async Task<State> ReadAsync(CancellationToken cancellationToken)
    {
        var texts = await db.CustomBranding.AsNoTracking().SingleOrDefaultAsync(cancellationToken);

        // Not the images themselves: this runs for every request, and only the one that is served needs its bytes.
        var rows = await db.CustomBrandingImages.AsNoTracking()
            .Select(i => new { i.Slot, i.ContentType, i.UpdatedAt, Length = i.Content == null ? 0 : i.Content.Length })
            .ToListAsync(cancellationToken);

        var slots = new Dictionary<string, Own?>();
        foreach (var slot in BrandingSlots.All)
        {
            var row = rows.FirstOrDefault(r => r.Slot == slot);
            slots[slot] = row is not null
                ? row.ContentType is null ? null : new Own(row.ContentType, null, $"u{row.Length:x}-{row.UpdatedAt.UtcTicks:x}")
                : defaults.FileOf(slot) is { } file ? new Own(file.ContentType, file.Path, FileVersion(file.Path)) : null;
        }

        return new State(texts, texts is not null || rows.Count > 0, slots);
    }

    /// <summary>The file's size and time, so that a file replaced by one with an older time is noticed too (the same as its <c>ETag</c>).</summary>
    private static string FileVersion(string path) =>
        new FileInfo(path) is { Exists: true } file ? $"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}" : "missing";

    private string ProductName(State state) => state.Texts?.ProductName.Trim() is { Length: > 0 } name ? name : defaults.ProductName;

    private string OrganizationName(State state) => state.Texts is { } texts ? texts.OrganizationName.Trim() : defaults.OrganizationName;

    private string DefaultWebTheme(State state) =>
        state.Texts is { } texts && ThemeRules.IsValid(ThemeClients.Web, texts.DefaultWebTheme) ? texts.DefaultWebTheme : defaults.DefaultWebTheme;

    private string DefaultWindowsTheme(State state) =>
        state.Texts is { } texts && ThemeRules.IsValid(ThemeClients.Windows, texts.DefaultWindowsTheme) ? texts.DefaultWindowsTheme : defaults.DefaultWindowsTheme;

    private string AccentColor(State state) =>
        state.Texts is { } texts && BrandingRules.IsAccentColor(texts.AccentColor) ? texts.AccentColor.Trim() : defaults.AccentColor;

    /// <summary>
    /// Which slot's own image is shown for a name. A slot that has no image of its own shows the one of the slot it falls back to:
    /// the logo for dark surfaces falls back to the one for light surfaces.
    /// </summary>
    private static string? ServedFrom(State state, string name) => name switch
    {
        BrandingSlots.Logo => First(state, BrandingSlots.Logo),
        BrandingSlots.LogoOnDark => First(state, BrandingSlots.LogoOnDark, BrandingSlots.Logo),
        BrandingSlots.Icon => First(state, BrandingSlots.Icon),
        _ => null,
    };

    private static string? First(State state, params string[] slots) => slots.FirstOrDefault(slot => state.Slots[slot] is not null);

    private BrandingSettingsDto ToSettings(State state) => new(
        ProductName(state), OrganizationName(state), AccentColor(state), state.IsCustomized,
        [.. BrandingSlots.All.Select(slot => new BrandingImageDto(slot, state.Slots[slot] is null ? null : ImageUrl(slot)))],
        DefaultWebTheme(state), DefaultWindowsTheme(state));

    /// <summary>Relative to the API's address, like every other request a client makes.</summary>
    private static string ImageUrl(string name) => $"{Route.TrimStart('/')}/{name}";
}
