using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Backup;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Endpoints;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using Kyuplan.Api.Statistics;
using Scalar.AspNetCore;

// Settings that were saved in the web client only apply to an application that is built anew: it stops and is built and started again here,
// in the same process, with the same arguments.
do
{
    AppRestarter.Requested = false;
    await using var app = await BuildAsync(args);
    await app.RunAsync();
}
while (AppRestarter.Requested);

static async Task<WebApplication> BuildAsync(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    // What was set in the web client (setup and settings page) lives in a file of its own that is read on top of appsettings.json, so it survives
    // restarts. Saving it restarts the application (see AppRestarter), which builds this whole thing anew.
    var settingsFilePath = SettingsFile.Resolve(builder.Configuration, builder.Environment.ContentRootPath);
    var settingsFile = new SettingsFile(settingsFilePath, SettingsFile.CreateProtector(builder.Configuration, settingsFilePath));
    settingsFile.AddTo(builder.Configuration);
    builder.Services.AddSingleton(settingsFile);
    builder.Services.AddSingleton<IAppRestarter, AppRestarter>();
    builder.Services.AddSingleton<SetupState>();
    builder.Services.AddScoped<SettingsService>();

    // The log goes to text files as well as to the console (Logging:File:Path; empty = no files).
    builder.Services.Configure<FileLogOptions>(builder.Configuration.GetSection(FileLogOptions.Section));
    builder.Logging.AddConfiguredFile();

    builder.Services.Configure<SpondOptions>(builder.Configuration.GetSection(SpondOptions.Section));
    builder.Services.Configure<StatisticsOptions>(builder.Configuration.GetSection(StatisticsOptions.Section));
    builder.Services.Configure<ApiSecurityOptions>(builder.Configuration.GetSection(ApiSecurityOptions.Section));
    builder.Services.AddSingleton<LoginThrottle>();
    builder.Services.AddSingleton<VerifiedLogins>();
    // The host's switch ASPNETCORE_FORWARDEDHEADERS_ENABLED would believe every sender; only the proxies of Api:TrustedProxies are.
    builder.Services.PostConfigure<ForwardedHeadersOptions>(options => TrustedProxies.Apply(options, builder.Configuration.GetSection("Api:TrustedProxies").Get<string[]>()));

    // Nothing in this application takes a large body (the biggest is a 1 MB logo), so a client cannot make it buffer more than that.
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 2 * 1024 * 1024);

    // What the clients show of the club (name, accent colour, logos); they ask for it at /api/branding, before any login. The settings are
    // the basis; what administrators saved in the database is put on top (BrandingService).
    builder.Services.Configure<BrandingOptions>(builder.Configuration.GetSection(BrandingOptions.Section));
    builder.Services.AddSingleton<BrandingDefaults>();
    builder.Services.AddScoped<BrandingService>();
    builder.Services.AddSingleton<ThemePackStore>(); // the theme packs: built-in and uploaded (see ThemeEndpoints)

    // The installer of the Windows client lies next to the program in the container image (see ClientDownloadEndpoints).
    builder.Services.Configure<ClientDownloadOptions>(builder.Configuration.GetSection(ClientDownloadOptions.Section));
    builder.Services.Configure<DocumentationOptions>(builder.Configuration.GetSection(DocumentationOptions.Section));
    builder.Services.AddSingleton<DocumentationFiles>();
    builder.Services.AddSingleton<ClientInstaller>();

    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(sp => new ClubClock(
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<IOptions<StatisticsOptions>>().Value.ResolveTimeZone()));
    builder.Services.AddMemoryCache();

    // Decided when first resolved (not at registration) so that configuration overrides, e.g. in tests, are honoured.
    builder.Services.AddSingleton<ISpondDataSource>(sp =>
        sp.GetRequiredService<IOptions<SpondOptions>>().Value.UseDemoData
            ? ActivatorUtilities.CreateInstance<DemoDataSource>(sp)
            : ActivatorUtilities.CreateInstance<SpondApiDataSource>(sp));
    builder.Services.AddSingleton<StatisticsService>();

    // Exams, members, belts and comments live in the API's own database (SQLite by default, or PostgreSQL: Database:Provider). The connection string is resolved when the context
    // is first needed, for the same reason as the data source above.
    builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.Section));
    builder.Services.AddProviderDbContext<PlannerDbContext, PlannerPostgresDbContext>(
        sp => sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Resolve(sp.GetRequiredService<IHostEnvironment>().ContentRootPath));
    builder.Services.AddScoped<ExamService>();
    builder.Services.AddScoped<ExamPlanningService>();
    builder.Services.AddScoped<BeltService>();
    builder.Services.AddScoped<MemberService>();
    builder.Services.AddScoped<CommentService>();
    builder.Services.AddScoped<TagService>();
    builder.Services.AddScoped<RatingService>();
    builder.Services.AddScoped<CalendarService>();

    // A new database starts with the standard belts and roles (DefaultBelts, DefaultRoles); see DefaultDataOptions.
    builder.Services.Configure<DefaultDataOptions>(builder.Configuration.GetSection(DefaultDataOptions.Section));

    // Members and the invitations of exams are copies of what Spond says; this keeps them in line (see SpondSync).
    builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.Section));
    builder.Services.AddSingleton<SyncGate>();
    builder.Services.AddScoped<SpondSync>();
    builder.Services.AddHostedService<SpondSyncWorker>();

    // Backups: a file to download and upload, and backup databases that are kept up to date on a schedule (see BackupService).
    builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.Section));
    builder.Services.AddSingleton<BackupGate>();
    builder.Services.AddSingleton<BackupTargetStore>();
    builder.Services.AddSingleton<BackupRunner>();
    builder.Services.AddScoped<BackupService>();
    builder.Services.AddHostedService<BackupWorker>();

    // The user accounts live in a database of their own. Everything except the development docs requires a login (HTTP Basic,
    // see BasicAuthenticationHandler); the fallback policy makes that the default, so a new endpoint cannot be left open by accident.
    // What a user may do is checked per endpoint with RequirePermission, from the permissions the sign-in puts into the user's claims.
    builder.Services.Configure<AccountsOptions>(builder.Configuration.GetSection(AccountsOptions.Section));
    builder.Services.AddProviderDbContext<AccountsDbContext, AccountsPostgresDbContext>(
        sp => sp.GetRequiredService<IOptions<AccountsOptions>>().Value.Resolve(
            sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Provider, sp.GetRequiredService<IHostEnvironment>().ContentRootPath),
        DatabaseRegistration.AccountsHistoryTable);
    builder.Services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
    builder.Services.AddScoped<AccountService>();
    builder.Services.AddScoped<RoleService>();
    builder.Services.AddAuthentication(BasicAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(BasicAuthenticationHandler.SchemeName, null);
    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

    // A browser only sends the login it remembers to another origin (the Blazor client) when the API allows credentials.
    builder.Services.AddOptions<CorsOptions>().Configure<IOptions<ApiSecurityOptions>>((cors, api) =>
    {
        var origins = api.Value.AllowedOrigins;
        if (origins.Length == 0) return;
        cors.AddDefaultPolicy(policy => policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials());
    });
    builder.Services.AddCors();

    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<ApiExceptionHandler>();
    builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
    {
        // Lets the development docs (Scalar) ask for a user name and password and send them with "Try it".
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[BasicAuthenticationHandler.SchemeName] =
            new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "basic" };
        document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BasicAuthenticationHandler.SchemeName, document)] = [] }];
        return Task.CompletedTask;
    }));

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        // A database that is created now starts with the standard belts and roles; one that exists is only brought up to date.
        var seedDefaults = scope.ServiceProvider.GetRequiredService<IOptions<DefaultDataOptions>>().Value.Enabled;

        var planner = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
        var plannerIsNew = await planner.EnsureUpToDateAsync(CancellationToken.None);
        var accounts = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        var accountsIsNew = await accounts.EnsureUpToDateAsync(CancellationToken.None);

        // An instance that starts from nothing may start from a backup file instead (Backup:InitialDataFile); it then has what the club made of the belts and roles.
        if (plannerIsNew && accountsIsNew && await scope.ServiceProvider.GetRequiredService<BackupService>().LoadInitialDataAsync(CancellationToken.None))
        {
            app.Logger.LogInformation("Both databases are new: they start with the data of the backup file.");
        }
        else
        {
            if (plannerIsNew && seedDefaults)
            {
                await DefaultBelts.SeedAsync(planner, CancellationToken.None);
                app.Logger.LogInformation("The planner database is new: it starts with {Count} standard belts.", DefaultBelts.All.Count);
            }

            if (accountsIsNew && seedDefaults)
            {
                await DefaultRoles.SeedAsync(accounts, CancellationToken.None);
                app.Logger.LogInformation("The accounts database is new: it starts with the roles {Roles}.", string.Join(", ", DefaultRoles.All.Select(r => r.Name)));
            }
        }

        // Without a user nobody can sign in: the web client then asks for the settings and the first administrator (see SettingsEndpoints).
        var hasUsers = await scope.ServiceProvider.GetRequiredService<AccountService>().EnsureAdministratorAsync(CancellationToken.None);
        app.Services.GetRequiredService<SetupState>().Set(!hasUsers);
        if (!hasUsers) app.Logger.LogWarning("There is no user yet: open the web client in a browser to set the API up and create the first administrator.");
    }

    // Checks the branding settings now, so that one that cannot be used is in the log from the start and not from the first visitor.
    app.Services.GetRequiredService<BrandingDefaults>();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference().AllowAnonymous();
    }
    else
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    app.UseExceptionHandler();
    app.UseMiddleware<SecurityMiddleware>();

    // The web client (Blazor WebAssembly) is part of this application: its files are open to everybody (the login is asked for when the
    // page calls /api), and every address that is not the API's is a page of the client. The static files come before the
    // authorization, so the fallback policy does not apply to them.
    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();

    app.UseCors();
    app.UseMiddleware<ApiKeyMiddleware>();
    app.UseAuthentication();
    app.UseAuthorization();

    // For the container's health check, load balancers and uptime monitors: no login, no data, 503 while the database cannot be reached.
    app.MapGet("/healthz", async (PlannerDbContext db, CancellationToken cancellationToken) =>
            await db.Database.CanConnectAsync(cancellationToken) ? Results.Text("ok") : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
        .AllowAnonymous().ExcludeFromDescription();

    app.MapBrandingEndpoints();
    app.MapThemeEndpoints();
    app.MapBackupEndpoints();
    app.MapPlannerEndpoints();
    app.MapClientDownloadEndpoints();
    app.MapDocumentationEndpoints();
    app.MapExamEndpoints();
    app.MapBeltEndpoints();
    app.MapCommentEndpoints();
    app.MapCalendarEndpoints();
    app.MapRatingEndpoints();
    app.MapAccountEndpoints();
    app.MapSettingsEndpoints();

    // An address below /api that no endpoint knows is a 404, not the web client; every other address is a page of the client.
    // Both only for reading: another method that finds no endpoint must keep the answer it had (e.g. a body that is not JSON).
    var read = new HttpMethodMetadata(["GET", "HEAD"]);
    app.MapFallback("/api/{**path}", () => Results.NotFound()).WithMetadata(read).ExcludeFromDescription();
    app.MapFallbackToFile("index.html").WithMetadata(read).AllowAnonymous();

    return app;
}

/// <summary>Entry point marker for <c>WebApplicationFactory&lt;Program&gt;</c> in the integration tests.</summary>
public partial class Program;
