namespace Kyuplan.Contracts;

/// <summary>One part of the documentation (a Docusaurus site of its own) and who may read it.</summary>
/// <param name="Id">Folder and address segment: the site lives at <c>docs/&lt;id&gt;/</c>.</param>
/// <param name="AnyOf">The permissions of which a user needs one; empty = every signed-in user.</param>
public sealed record DocumentationAudience(string Id, IReadOnlyList<string> AnyOf);

/// <summary>
/// The documentation is built once per audience, so that a user is only ever offered (and served) what applies to them: the user guide to
/// everybody, the administrator guide to those who may change the settings, users, roles or the branding, the deployment guide to those who
/// may change the settings (the operator of the server). The developer guide is not part of the server, it is only in the release's documentation archive.
/// The server checks these rules for every file; the clients use them to show only the links that will work.
/// </summary>
public static class Documentation
{
    public const string User = "user";
    public const string Admin = "admin";
    public const string Deployment = "deployment";

    /// <summary>In the order the clients list them.</summary>
    public static readonly IReadOnlyList<DocumentationAudience> Audiences =
    [
        new(User, []),
        new(Admin, [Permission.SettingsView, Permission.UsersView, Permission.RolesView, Permission.BrandingEdit]),
        new(Deployment, [Permission.SettingsEdit]),
    ];

    /// <summary>Address of an audience's site, relative to the API.</summary>
    public static string PathFor(string audience) => $"docs/{Uri.EscapeDataString(audience)}/";

    public static DocumentationAudience? Find(string audience) => Audiences.FirstOrDefault(a => a.Id == audience);

    /// <summary>Whether somebody who may do what <paramref name="can"/> says may read this audience's documentation.</summary>
    public static bool MayRead(DocumentationAudience audience, Func<string, bool> can) => audience.AnyOf.Count == 0 || audience.AnyOf.Any(can);

    /// <summary>The audiences somebody may read, in the order of <see cref="Audiences"/>.</summary>
    public static IEnumerable<DocumentationAudience> For(Func<string, bool> can) => Audiences.Where(a => MayRead(a, can));
}
