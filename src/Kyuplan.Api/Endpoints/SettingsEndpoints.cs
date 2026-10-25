using Kyuplan.Api.Accounts;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>
/// The first start and the settings. <b>The setup is open to everybody, but only while the API has no user at all</b>: whoever gets there first
/// creates the first administrator (like any installer, put the API behind your network's walls until it is set up). Afterwards it answers 409.
/// <b>The settings need permissions</b>: <c>settings.view</c> to read, <c>settings.edit</c> to change.
/// </summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var setup = app.MapGroup("/api/setup").WithTags("Setup").AllowAnonymous();

        setup.MapGet("/", (SettingsService service) => service.GetStatus())
            .Produces<SetupStatusDto>()
            .WithSummary("Whether the API still has to be set up (it has no user yet) and, if so, the settings it would start with. Needs no login.");

        setup.MapPost("/", async (SetupInputDto input, SettingsService service, SetupState state, HttpContext context, CancellationToken ct) =>
            {
                if (!state.NeedsSetup) throw new SetupDoneException();
                if (Validate(input, service, IsLocal(context)) is { } errors) return Results.ValidationProblem(errors);

                AuditFilter.SetDetails(context, service.DescribeSetup(input));
                AuditFilter.SetUserName(context, input.AdminUserName.Trim());
                return Results.Ok(await service.SetupAsync(input, ct));
            })
            .Audited(AuditAreas.Settings, AuditActions.Setup, withBody: false)
            .Produces<SettingsSavedDto>()
            .WithSummary("Save the settings and create the first administrator. Only while the API has no user, 409 afterwards. Restarts the API when a setting changed that only a restart applies. Needs no login.");

        var settings = app.MapGroup("/api/settings").WithTags("Settings");

        settings.MapGet("/", (SettingsService service) => service.Get())
            .Produces<SettingsDto>()
            .WithSummary("The settings the API runs with. The Spond password is not given, only whether there is one.")
            .RequirePermission(Permission.SettingsView);

        settings.MapPut("/", async (SettingsInputDto input, SettingsService service, HttpContext context, CancellationToken ct) =>
            {
                if (service.Validate(input) is { } errors) return Results.ValidationProblem(errors);

                // The log gets the names of what changed, not the body: it holds the Spond logins, the metrics token and the script.
                if (service.DescribeChanges(input) is { } described) AuditFilter.SetDetails(context, described);
                else AuditFilter.Skip(context);
                return Results.Ok(await service.SaveAsync(input, ct));
            })
            .Audited(AuditAreas.Settings, AuditActions.Update, withBody: false)
            .Produces<SettingsSavedDto>()
            .WithSummary("Save the settings. They are kept in the settings file; when something changed, the API restarts itself to apply it and is back within seconds.")
            .RequirePermission(Permission.SettingsEdit);

        return app;
    }

    private static bool IsLocal(HttpContext context) =>
        !context.Request.Headers.ContainsKey("X-Forwarded-For") && !context.Request.Headers.ContainsKey("X-Original-For") &&
        (context.Connection.RemoteIpAddress is not { } address || System.Net.IPAddress.IsLoopback(address));

    private static Dictionary<string, string[]>? Validate(SetupInputDto? input, SettingsService service, bool isLocal)
    {
        if (input?.Settings is null) return new() { ["settings"] = ["settings is required."] };

        var errors = service.Validate(input.Settings) ?? [];
        if (service.ValidateSettingsFile(input.SettingsFilePath, isLocal) is { } fileProblem) errors["settingsFilePath"] = [fileProblem];
        if (AccountRules.CheckUserName(input.AdminUserName, "adminUserName") is { } nameProblem) errors["adminUserName"] = [nameProblem];
        if (AccountRules.CheckPassword(input.AdminPassword, "adminPassword") is { } passwordProblem) errors["adminPassword"] = [passwordProblem];
        return errors.Count == 0 ? null : errors;
    }
}
