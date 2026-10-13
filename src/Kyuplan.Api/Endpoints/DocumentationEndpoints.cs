using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Configuration;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>Where the built documentation lies (see <see cref="DocumentationOptions"/>).</summary>
public sealed class DocumentationFiles(IOptions<DocumentationOptions> options, IHostEnvironment environment)
{
    private string Folder => string.IsNullOrWhiteSpace(options.Value.Directory)
        ? Path.Combine(AppContext.BaseDirectory, "docs")
        : Path.GetFullPath(options.Value.Directory, environment.ContentRootPath);

    /// <summary>Whether the server carries the documentation at all (the user guide is always part of it).</summary>
    public bool Exists => Directory.Exists(Path.Combine(Folder, Documentation.User));

    /// <summary>
    /// The file of an address inside an audience's site, <c>null</c> when there is none. A folder is its <c>index.html</c> (the sites are built with
    /// trailing slashes); an address that tries to leave the audience's folder finds nothing.
    /// </summary>
    public string? Resolve(string audience, string path, out bool isFolder)
    {
        isFolder = false;
        var root = Path.GetFullPath(Path.Combine(Folder, audience)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

        if (Directory.Exists(full))
        {
            isFolder = true;
            full = Path.Combine(full, "index.html");
        }

        return File.Exists(full) ? full : null;
    }
}

/// <summary>
/// The documentation, one Docusaurus site per audience (<see cref="Documentation"/>). Everything needs a login, and an audience's files only go to the
/// users who may do what that part of the documentation is about: a user without the right gets 403, so the administrator guide is not a click away for everybody.
/// </summary>
public static class DocumentationEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static IEndpointRouteBuilder MapDocumentationEndpoints(this IEndpointRouteBuilder app)
    {
        // The start of the help: the user guide, which everybody may read.
        app.MapGet("/docs", () => Results.Redirect("/" + Documentation.PathFor(Documentation.User)))
            .WithTags("Documentation").WithSummary("Redirects to the user guide.");

        app.MapMethods("/docs/{audience}/{**path}", ["GET", "HEAD"], (string audience, string? path, HttpContext context, DocumentationFiles files) =>
            {
                if (Documentation.Find(audience) is not { } part) return Results.NotFound();
                if (!Documentation.MayRead(part, context.User.Has)) return Results.Forbid();

                if (files.Resolve(audience, path ?? "", out var isFolder) is not { } file) return Results.NotFound();

                // A folder asked for without the closing slash would resolve the page's relative links wrongly.
                if (isFolder && context.Request.Path.Value is { } asked && !asked.EndsWith('/')) return Results.Redirect(asked + "/");

                ContentTypes.TryGetContentType(file, out var contentType);
                return Results.File(file, contentType ?? "application/octet-stream");
            })
            .WithTags("Documentation")
            .WithSummary("A file of the documentation for an audience (user, admin, deployment); 403 without the permissions that audience needs.");

        return app;
    }
}
