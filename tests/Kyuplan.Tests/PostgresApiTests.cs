using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// The API on a real PostgreSQL server, for what differs from SQLite: the migrations, case-insensitive names (citext instead of NOCASE),
/// and <c>decimal</c> amounts. Needs <see cref="PostgresDatabase.Variable"/>; see <see cref="PostgresFactAttribute"/>.
/// </summary>
public sealed class PostgresApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private PostgresDatabase? _database;

    public void Dispose() => _database?.Dispose();

    private PlannerApiClient Start()
    {
        _database = new PostgresDatabase();
        var http = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false");
            builder.UseSetting("Database:Provider", "PostgreSql");
            builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
            builder.UseSetting("Accounts:ConnectionString", _database.ConnectionString); // one database, a migration history table per context
            builder.UseSetting("Accounts:InitialAdminUserName", TestAccounts.AdminName);
            builder.UseSetting("Accounts:InitialAdminPassword", TestAccounts.AdminPassword);
        }).CreateSignedInClient();
        return new PlannerApiClient(http);
    }

    private static async Task<HttpStatusCode> StatusOfAsync(Func<Task> call) =>
        (await Assert.ThrowsAsync<PlannerApiException>(call)).StatusCode ?? throw new InvalidOperationException("no status");

    [PostgresFact]
    public async Task The_migrations_run_and_the_sign_in_works()
    {
        var api = Start();

        Assert.Empty(await api.GetBeltsAsync());
    }

    [PostgresFact]
    public async Task Names_are_unique_regardless_of_case()
    {
        var api = Start();
        await api.CreateBeltAsync(new BeltInputDto("White", "#FFFFFF", null, []));

        var status = await StatusOfAsync(() => api.CreateBeltAsync(new BeltInputDto("wHITE", "#EEEEEE", null, [])));

        Assert.Equal(HttpStatusCode.Conflict, status);
    }

    [PostgresFact]
    public async Task Prices_keep_their_cents()
    {
        var api = Start();
        var white = await api.CreateBeltAsync(new BeltInputDto("White", "#FFFFFF", null, []));
        var yellow = await api.CreateBeltAsync(new BeltInputDto("Yellow", "#FFD800", null, [new BeltPrerequisiteDto(white.Id, 0, 0)]));

        await api.SetExamPricesAsync(new ExamPricesDto(12.34m, [new BeltPriceDto(yellow.Id, 0.01m, 999999.99m)]));

        var prices = await api.GetExamPricesAsync();
        Assert.Equal(12.34m, prices.GeneralFee);
        Assert.Equal([(yellow.Id, 0.01m, 999999.99m)], prices.BeltPrices.Select(p => (p.BeltId, p.StampFee, p.BeltPrice)));
    }
}
