using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Infrastructure;

/// <summary>
/// Records a change in the audit log once it went through: the endpoint ran without an exception and answered with a success status.
/// A refused or failed request changed nothing, so it leaves nothing behind. Recording is part of the request, but a log that cannot be
/// written must not turn a change that is already made into an error for the user: it is logged instead.
/// </summary>
public sealed class AuditFilter(string area, string action, bool withBody = true) : IEndpointFilter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string DetailsItem = "Kyuplan.AuditDetails";
    private const string SkipItem = "Kyuplan.AuditSkip";
    private const string UserNameItem = "Kyuplan.AuditUserName";

    /// <summary>
    /// Words in a JSON key (and "script" at its end) that mark a secret: the value is never written to the log, wherever the key is nested. A secret that is sent under a name
    /// without one of them would end up in clear text (and in every backup), so a new endpoint with such a field adds its word here.
    /// </summary>
    internal static readonly string[] SecretWords =
        ["password", "passphrase", "token", "secret", "connection", "location", "credential", "apikey", "authorization", "cookie"];

    /// <summary>Tells the filter what to write as the details of this request instead of its body (for an endpoint whose body is not what changed, or is a file).</summary>
    public static void SetDetails(HttpContext http, string? json) => http.Items[DetailsItem] = json;

    /// <summary>The request went through but changed nothing (a settings save without a difference): it leaves no entry, like a refused one.</summary>
    public static void Skip(HttpContext http) => http.Items[SkipItem] = true;

    /// <summary>Who made the change when the request is not signed in (the setup, which creates the first user).</summary>
    public static void SetUserName(HttpContext http, string userName) => http.Items[UserNameItem] = userName;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        if (result is IStatusCodeHttpResult { StatusCode: { } status } && (status < 200 || status > 299)) return result;
        if (context.HttpContext.Items.ContainsKey(SkipItem)) return result;

        var http = context.HttpContext;
        try
        {
            var user = http.User;
            var userId = int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (int?)null;
            await http.RequestServices.GetRequiredService<AuditService>().RecordAsync(
                userId, http.Items[UserNameItem] as string ?? user.FindFirstValue(ClaimTypes.Name) ?? "", area, action, Target(http, result), http.Items.TryGetValue(DetailsItem, out var given) ? given as string : withBody ? Details(context) : null, http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            http.RequestServices.GetRequiredService<ILogger<AuditFilter>>().LogError(ex, "The {Area} {Action} could not be written to the audit log.", area, action);
        }

        return result;
    }

    /// <summary>The ids in the route, or for a new record the id it got.</summary>
    private static string? Target(HttpContext http, object? result)
    {
        var ids = http.Request.RouteValues.Where(v => v.Value is not null && (v.Key.EndsWith("Id", StringComparison.Ordinal) || v.Key == "id")).Select(v => $"{v.Key}={v.Value}").ToList();
        if (ids.Count > 0) return string.Join(", ", ids);

        var value = (result as IValueHttpResult)?.Value;
        var created = value?.GetType().GetProperty("Id")?.GetValue(value);
        return created is null ? null : $"id={created}";
    }

    /// <summary>The body of the request as JSON, with everything that looks like a secret left out (see <see cref="SecretWords"/>).</summary>
    private static string? Details(EndpointFilterInvocationContext context)
    {
        var body = context.Arguments.FirstOrDefault(a => a is not null && a.GetType().Namespace == typeof(AuditEntryDto).Namespace);
        if (body is null) return null;

        var node = JsonSerializer.SerializeToNode(body, body.GetType(), Json);
        Redact(node);
        return node?.ToJsonString();
    }

    /// <summary>A key that ends in "script" (<c>customScript</c>) is code, which often holds tokens; "description" and the like are not meant.</summary>
    internal static bool IsSecret(string key) =>
        key.EndsWith("script", StringComparison.OrdinalIgnoreCase) || SecretWords.Any(word => key.Contains(word, StringComparison.OrdinalIgnoreCase));

    internal static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (IsSecret(key)) obj.Remove(key);
                    else Redact(obj[key]);
                }
                break;
            case JsonArray array:
                foreach (var item in array) Redact(item);
                break;
        }
    }
}

public static class AuditFilterExtensions
{
    /// <summary>Write the change this endpoint makes into the audit log (see <see cref="AuditFilter"/>).</summary>
    /// <param name="withBody">Whether the request's body is written down (without secrets); <c>false</c> for a body that is text somebody wrote or a file.</param>
    public static TBuilder Audited<TBuilder>(this TBuilder builder, string area, string action, bool withBody = true) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new AuditFilter(area, action, withBody));
}
