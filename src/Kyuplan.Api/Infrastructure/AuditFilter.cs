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
public sealed class AuditFilter(string area, string action) : IEndpointFilter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        if (result is IStatusCodeHttpResult { StatusCode: { } status } && (status < 200 || status > 299)) return result;

        var http = context.HttpContext;
        try
        {
            var user = http.User;
            var userId = int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (int?)null;
            await http.RequestServices.GetRequiredService<AuditService>().RecordAsync(
                userId, user.FindFirstValue(ClaimTypes.Name) ?? "", area, action, Target(http, result), Details(context), http.RequestAborted);
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
        var ids = http.Request.RouteValues.Where(v => v.Value is not null && v.Key.EndsWith("Id", StringComparison.Ordinal)).Select(v => $"{v.Key}={v.Value}").ToList();
        if (ids.Count > 0) return string.Join(", ", ids);

        var value = (result as IValueHttpResult)?.Value;
        var created = value?.GetType().GetProperty("Id")?.GetValue(value);
        return created is null ? null : $"id={created}";
    }

    /// <summary>The body of the request as JSON, with everything that looks like a password left out.</summary>
    private static string? Details(EndpointFilterInvocationContext context)
    {
        var body = context.Arguments.FirstOrDefault(a => a is not null && a.GetType().Namespace == typeof(AuditEntryDto).Namespace);
        if (body is null) return null;

        var node = JsonSerializer.SerializeToNode(body, body.GetType(), Json);
        Redact(node);
        return node?.ToJsonString();
    }

    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (key.Contains("password", StringComparison.OrdinalIgnoreCase)) obj.Remove(key);
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
    public static TBuilder Audited<TBuilder>(this TBuilder builder, string area, string action) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new AuditFilter(area, action));
}
