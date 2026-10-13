using Kyuplan.Api.Configuration;
using Spond.API.Models;
using Spond.API.Services;
using static Spond.API.Enums;

namespace Kyuplan.Api.Spond;

/// <summary>
/// The calls <see cref="SpondApiDataSource"/> makes for one Spond account. Like Spond.API itself they report a failed request as an
/// empty list. It is an interface so that the switching between accounts can be tested without Spond.
/// </summary>
internal interface ISpondAccountClient
{
    Task<bool> LoginAsync();

    Task<List<SpondGroup>> GetGroupsAsync();

    /// <summary>The events of a group that end within [<paramref name="minEnd"/>, <paramref name="maxEnd"/>]; both <see cref="DateTimeKind.Utc"/>.</summary>
    Task<List<SpondEvent>> GetEventsAsync(string groupId, DateTime minEnd, DateTime maxEnd);
}

/// <summary>The Spond.API client of one account, which is not thread-safe and holds that account's token.</summary>
internal sealed class SpondAccountClient(SpondAccount account, SpondClient client) : ISpondAccountClient
{
    /// <summary>A month of one group has far fewer events than this; the cap only guards against server defaults.</summary>
    private const int MaxEventsPerMonth = 500;

    public Task<bool> LoginAsync() => string.IsNullOrWhiteSpace(account.Email)
        ? client.LoginWithPhoneNumber(account.PhoneNumber!, account.Password!)
        : client.LoginWithEmail(account.Email, account.Password!);

    public Task<List<SpondGroup>> GetGroupsAsync() => client.GetGroups();

    public Task<List<SpondEvent>> GetEventsAsync(string groupId, DateTime minEnd, DateTime maxEnd) => client.GetEvents(
        groupId, minEnd, maxEnd,
        max: MaxEventsPerMonth,
        order: Order.Ascending,
        scheduled: true,
        includeHidden: false,
        includeComments: false,
        addProfileInfo: false);
}
