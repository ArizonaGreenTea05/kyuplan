using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Domain;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Api.Spond;
using Kyuplan.Api.Statistics;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// The pieces that keep the database in line with Spond (sync, exams, the database itself) wired up without the web host, around a
/// <see cref="FakeSpondDataSource"/> the test controls and a clock pinned to 13 October 2026, 12:00 club time. Every call
/// gets a scope of its own, like a request, so nothing is remembered between two calls but what is in the database.
/// </summary>
internal sealed class SyncTestClub : IDisposable
{
    public const string Dojo = "g-dojo";

    public static readonly DateTimeOffset PinnedNow = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 10, 13);

    private readonly InMemoryDatabase _database = new();
    private readonly ClubClock _clock;

    public SyncTestClub()
    {
        Time = new FakeTimeProvider(PinnedNow);
        _clock = new ClubClock(Time, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(_clock);
        services.AddSingleton<IDataSource>(Spond);
        services.AddSingleton<IOptions<StatisticsOptions>>(Options.Create(new StatisticsOptions { ExamMinAverageStars = 0 })); // stars have tests of their own
        services.AddSingleton<IOptions<SpondOptions>>(Options.Create(SpondSettings));
        services.AddSingleton<StatisticsService>();
        services.AddSingleton<SyncGate>();
        services.AddSingleton<SyncStatusTracker>();
        services.AddDbContext<PlannerDbContext>(options => options.UseSqlite(_database.ConnectionString));
        services.AddScoped<SpondSync>();
        services.AddScoped<ExamService>();
        services.AddScoped<ExamPlanningService>();
        services.AddScoped<BeltService>();
        Services = services.BuildServiceProvider();

        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PlannerDbContext>().Database.Migrate();
    }

    public FakeTimeProvider Time { get; }

    /// <summary>The Spond options the sync reads; change them before syncing.</summary>
    public SpondOptions SpondSettings { get; } = new();

    public FakeSpondDataSource Spond { get; } = new();

    public ServiceProvider Services { get; }

    public void Dispose()
    {
        Services.Dispose();
        _database.Dispose();
    }

    public static MemberInfo Person(string id, string name) => new(id, name, []);

    public static MemberInfo Person(string id, string name, DateOnly? born, params string[] subGroupIds) => new(id, name, subGroupIds, born);

    public static GroupInfo Group(string id, params MemberInfo[] members) => new(id, $"Group {id}", [], members);

    public static GroupInfo Group(string id, SubGroupInfo[] subGroups, params MemberInfo[] members) => new(id, $"Group {id}", subGroups, members);

    /// <summary>The day <paramref name="daysFromToday"/> days from the pinned "today".</summary>
    public static DateOnly Day(int daysFromToday) => Today.AddDays(daysFromToday);

    /// <summary>A training from 18:00 to 19:30 (club time) on <paramref name="day"/>, to which exactly these members were invited.</summary>
    public EventInfo Event(string id, DateOnly day, params (string Member, Rsvp Rsvp)[] responses) => Event(id, "Training", day, responses);

    /// <summary>An event called <paramref name="name"/>, e.g. an exam, from 18:00 to 19:30 (club time) on <paramref name="day"/>, to which exactly these members were invited.</summary>
    public EventInfo Event(string id, string name, DateOnly day, params (string Member, Rsvp Rsvp)[] responses) => new(
        id, name, _clock.At(day, new TimeOnly(18, 0)), _clock.At(day, new TimeOnly(19, 30)), false,
        responses.ToDictionary(r => r.Member, r => r.Rsvp));

    public async Task RunAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    public async Task<T> QueryAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public Task SyncAsync() => RunAsync(sp => sp.GetRequiredService<SpondSync>().SyncAllAsync(CancellationToken.None));

    /// <summary>The members in the database, by name.</summary>
    public Task<List<Member>> MembersAsync() => QueryAsync(sp =>
        sp.GetRequiredService<PlannerDbContext>().Members.AsNoTracking().OrderBy(m => m.Name).ToListAsync());

    public Task<ExamDto> CreateExamAsync(string eventId, string groupId = Dojo, string[]? subGroups = null) => QueryAsync(sp =>
        sp.GetRequiredService<ExamService>().CreateAsync(new ExamInputDto(groupId, eventId, subGroups), CancellationToken.None));

    /// <summary>Who is recorded as invited to the exam (by Spond id), what they answered, and whether that counts as attending.</summary>
    public Task<List<(string Member, Rsvp Rsvp, bool Attended)>> ParticipantsAsync(int examId) => QueryAsync(async sp =>
    {
        var participants = await sp.GetRequiredService<PlannerDbContext>().ExamParticipants
            .AsNoTracking().Include(p => p.Member).Where(p => p.ExamId == examId).ToListAsync();
        return participants.OrderBy(p => p.Member.SpondId, StringComparer.Ordinal).Select(p => (p.Member.SpondId, p.Rsvp, p.Attended)).ToList();
    });

    public Task<int> AddBeltAsync(string name) => AddBeltAsync(name, []);

    /// <summary>A belt that is taken from <paramref name="from"/> (each previous belt with the age and the waiting time it asks for).</summary>
    public Task<int> AddBeltAsync(string name, params (int Previous, int MinAgeYears, int MinWaitMonths)[] from) => QueryAsync(async sp =>
    {
        var db = sp.GetRequiredService<PlannerDbContext>();
        var belt = new Belt
        {
            Name = name, PrimaryColor = "#FF8000",
            Prerequisites = [.. from.Select(f => new BeltPrerequisite { RequiredBeltId = f.Previous, MinAgeYears = f.MinAgeYears, MinWaitMonths = f.MinWaitMonths })],
        };
        db.Belts.Add(belt);
        await db.SaveChangesAsync();
        return belt.Id;
    });

    /// <summary>What a user does: sets what Spond does not know.</summary>
    public Task EditMemberAsync(string spondId, Action<Member> edit) => RunAsync(async sp =>
    {
        var db = sp.GetRequiredService<PlannerDbContext>();
        edit(await db.Members.SingleAsync(m => m.SpondId == spondId));
        await db.SaveChangesAsync();
    });

    /// <summary>
    /// Waits until <paramref name="condition"/> holds. For code that runs on its own thread (the worker): while it writes, a read
    /// of a shared in-memory SQLite database can fail with "table is locked", which only means "ask again".
    /// </summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition()) return;
            }
            catch (SqliteException)
            {
            }

            await Task.Delay(20);
        }

        Assert.Fail($"Timed out waiting until {what}.");
    }
}
