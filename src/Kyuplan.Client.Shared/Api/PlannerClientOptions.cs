namespace Kyuplan.Client.Api;

public sealed class PlannerClientOptions
{
    public const string Section = "PlannerApi";

    /// <summary>Base address of the Kyuplan API, e.g. <c>https://planner.example.org/</c>.</summary>
    public string BaseAddress { get; set; } = "http://localhost:5100/";

    /// <summary><see cref="BaseAddress"/> as an address that relative ones (<c>api/branding/logo</c>) can be resolved against.</summary>
    public Uri BaseUri => new(BaseAddress.EndsWith('/') ? BaseAddress : BaseAddress + "/");

    /// <summary>Which kind of client this is, for the themes: <see cref="Kyuplan.Contracts.ThemeClients"/>.</summary>
    public string ThemeClient { get; set; } = Kyuplan.Contracts.ThemeClients.Web;

    /// <summary>Sent as <c>X-Api-Key</c> when the API is configured to require one.</summary>
    public string? ApiKey { get; set; }
}
