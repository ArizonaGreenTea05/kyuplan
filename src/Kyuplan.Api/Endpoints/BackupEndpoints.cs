using System.Text.Json.Nodes;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Backup;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Kyuplan.Api.Endpoints;

/// <summary>
/// Backups: the backup databases that are kept up to date on a schedule (<see cref="BackupTargetDto"/>), the download of a backup file and the
/// replacement of all data with an uploaded one. Each part has its own permission; the file holds every password hash, which is why downloading is not part of viewing.
/// </summary>
public static class BackupEndpoints
{
    public const string Route = "/api/backup";

    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).WithTags("Backup");

        group.MapGet("/targets", (BackupTargetStore store) => store.List().Select(BackupTargetStore.Mask).ToList())
            .Produces<IReadOnlyList<BackupTargetDto>>()
            .WithSummary("The backup databases and how their last run went.")
            .RequirePermission(Permission.BackupsView);

        group.MapPost("/targets", (BackupTargetInputDto input, BackupTargetStore store) => Results.Ok(BackupTargetStore.Mask(store.Add(input))))
            .Audited(AuditAreas.Backup, AuditActions.Create)
            .Produces<BackupTargetDto>()
            .WithSummary("Define a backup database: a folder for two SQLite files or a PostgreSQL connection string, and how often (minutes) it is updated. 400 with the reason when it cannot be used.")
            .RequirePermission(Permission.BackupsEdit);

        group.MapPut("/targets/{id:int}", (int id, BackupTargetInputDto input, BackupTargetStore store) =>
                store.Update(id, input) is { } target ? Results.Ok(BackupTargetStore.Mask(target)) : Results.NotFound())
            .Audited(AuditAreas.Backup, AuditActions.Update)
            .Produces<BackupTargetDto>()
            .WithSummary("Replace the definition of a backup database.")
            .RequirePermission(Permission.BackupsEdit);

        group.MapDelete("/targets/{id:int}", (int id, BackupTargetStore store) => store.Delete(id) ? Results.NoContent() : Results.NotFound())
            .Audited(AuditAreas.Backup, AuditActions.Delete)
            .WithSummary("Forget a backup database (the data in it stays where it is).")
            .RequirePermission(Permission.BackupsEdit);

        group.MapPost("/targets/{id:int}/run", async (int id, BackupRunner runner, CancellationToken ct) =>
                await runner.RunAsync(id, ct) is { } target ? Results.Ok(BackupTargetStore.Mask(target)) : Results.NotFound())
            .Audited(AuditAreas.Backup, AuditActions.Run)
            .Produces<BackupTargetDto>()
            .WithSummary("Update the backup database now. A failed run is still answered with 200; its reason is the target's lastMessage.")
            .RequirePermission(Permission.BackupsEdit);

        group.MapGet("/download", (BackupService service, HttpContext http) =>
            {
                // A file that is saved and sent on must not be cached.
                http.Response.Headers.CacheControl = "no-store";
                var passphrase = BackupRules.DecodePassphrase(http.Request.Headers[BackupRules.PassphraseHeader]);
                if (BackupRules.CheckPassphrase(passphrase) is { } problem) return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);

                // The file holds every password hash: who took it, and whether it is encrypted, belongs in the log (written when the download starts).
                AuditFilter.SetDetails(http, new JsonObject { ["encrypted"] = !string.IsNullOrEmpty(passphrase) }.ToJsonString());
                return Results.Stream(stream => service.WriteAsync(stream, passphrase, http.RequestAborted), BackupRules.ContentType, service.FileName());
            })
            .Audited(AuditAreas.Backup, AuditActions.Download, withBody: false)
            .WithSummary("Download a backup file (zip) with all data of both databases; encrypted with a passphrase (header X-Backup-Passphrase, base64 of UTF-8, at least 8 characters) when one is sent. It can be uploaded to restore, or be the InitialDataFile of a new instance.")
            .RequirePermission(Permission.BackupsDownload);

        group.MapPost("/restore", async (HttpRequest request, BackupService service, IAppRestarter restarter, IOptions<BackupOptions> options, CancellationToken ct) =>
            {
                var limit = options.Value.MaxUploadMegabytes * 1024L * 1024L;
                if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = limit;
                if (request.ContentLength > limit) return Results.Problem($"The file is larger than {options.Value.MaxUploadMegabytes} MB.", statusCode: StatusCodes.Status413PayloadTooLarge);

                // The zip format needs to seek: the body goes to a file first.
                var temporary = Path.GetTempFileName();
                try
                {
                    await using (var file = File.Create(temporary)) await request.Body.CopyToAsync(file, ct);
                    await using var stream = File.OpenRead(temporary);
                    var passphrase = BackupRules.DecodePassphrase(request.Headers[BackupRules.PassphraseHeader]);
                    var restored = await service.RestoreAsync(stream, passphrase, ct);
                    // Written by the audit filter once the restore went through (the restart is requested below and waits for the answer).
                    AuditFilter.SetDetails(request.HttpContext, new JsonObject
                    {
                        ["backupCreated"] = restored.CreatedUtc,
                        ["rows"] = restored.Rows,
                        ["encrypted"] = !string.IsNullOrEmpty(passphrase),
                    }.ToJsonString());
                    restarter.Request(); // everything that was read from the old data (logins, caches) is read again
                    return Results.Ok(restored);
                }
                finally
                {
                    File.Delete(temporary);
                }
            })
            .Audited(AuditAreas.Backup, AuditActions.Restore, withBody: false)
            .Accepts<Stream>(BackupRules.ContentType)
            .Produces<BackupRestoredDto>()
            .WithSummary("Replace ALL data (both databases) with an uploaded backup file (the body; header X-Backup-Passphrase when it is encrypted). 400 with the reason, and nothing changed, when it is no backup this program can read. The API restarts afterwards.")
            .RequirePermission(Permission.BackupsRestore);

        return app;
    }
}
