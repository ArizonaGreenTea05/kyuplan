using System.Globalization;
using System.Net;
using System.Text;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

public class PlannerApiClientTests
{
    // The error messages are localized; these tests read the English ones.
    public PlannerApiClientTests() => CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-GB");

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static (PlannerApiClient Api, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new PlannerApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://planner.test/") }), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task The_branding_is_read_from_api_branding_and_may_lack_images()
    {
        var (api, handler) = Create(_ => Json(HttpStatusCode.OK, """
            {"productName":"PlanIt","organizationName":"Club","accentColor":"#1a5fb4","logoUrl":"api/branding/logo",
             "logoOnDarkUrl":null,"iconUrl":"api/branding/icon"}
            """));

        var branding = await api.GetBrandingAsync();

        Assert.Equal("/api/branding", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Equal(new BrandingDto("PlanIt", "Club", "#1a5fb4", "api/branding/logo", null, "api/branding/icon"), branding);
    }

    private const string SettingsJson = """
        {"productName":"PlanIt","organizationName":"Club","accentColor":"#1a5fb4","isCustomized":true,
         "images":[{"slot":"logo","url":"api/branding/logo"},{"slot":"logo-on-dark","url":null}]}
        """;

    [Fact]
    public async Task The_branding_as_administrators_edit_it_is_read_from_api_branding_settings()
    {
        var (api, handler) = Create(_ => Json(HttpStatusCode.OK, SettingsJson));

        var settings = await api.GetBrandingSettingsAsync();

        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
        Assert.Equal("/api/branding/settings", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Equal("PlanIt", settings.ProductName);
        Assert.True(settings.IsCustomized);
        Assert.Equal([new BrandingImageDto("logo", "api/branding/logo"), new BrandingImageDto("logo-on-dark", null)], settings.Images);
    }

    [Fact]
    public async Task Saving_the_branding_puts_the_texts_and_the_colour_as_json()
    {
        string? body = null;
        var (api, handler) = Create(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return Json(HttpStatusCode.OK, SettingsJson);
        });

        var settings = await api.SaveBrandingAsync(new BrandingInputDto("PlanIt", "Club", "#1a5fb4"));

        Assert.Equal(HttpMethod.Put, handler.Requests.Single().Method);
        Assert.Equal("/api/branding", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Equal("""{"productName":"PlanIt","organizationName":"Club","accentColor":"#1a5fb4","defaultWebTheme":null,"defaultWindowsTheme":null,"currency":null}""", body);
        Assert.True(settings.IsCustomized);
    }

    [Fact]
    public async Task An_image_is_put_to_its_slot_as_the_plain_bytes()
    {
        byte[]? body = null;
        string? contentType = null;
        var (api, handler) = Create(request =>
        {
            body = request.Content!.ReadAsByteArrayAsync().Result;
            contentType = request.Content.Headers.ContentType?.MediaType;
            return Json(HttpStatusCode.OK, SettingsJson);
        });

        await api.UploadBrandingImageAsync("logo-on-dark", new MemoryStream([1, 2, 3, 4]));

        Assert.Equal(HttpMethod.Put, handler.Requests.Single().Method);
        Assert.Equal("/api/branding/logo-on-dark", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Equal([1, 2, 3, 4], body);
        Assert.Equal("application/octet-stream", contentType); // the API looks at the bytes, not at what is claimed
    }

    [Fact]
    public async Task Removing_an_image_and_going_back_are_deletes_that_answer_with_the_settings()
    {
        var (api, handler) = Create(_ => Json(HttpStatusCode.OK, SettingsJson));

        await api.RemoveBrandingImageAsync("logo-on-dark");
        await api.ResetBrandingAsync();

        Assert.Equal([HttpMethod.Delete, HttpMethod.Delete], handler.Requests.Select(r => r.Method));
        Assert.Equal(["/api/branding/logo-on-dark", "/api/branding"], handler.Requests.Select(r => r.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task A_refused_image_surfaces_the_reason_the_api_gives()
    {
        var (api, _) = Create(_ => Json(HttpStatusCode.BadRequest,
            """{"title":"Invalid input","status":400,"detail":"The image for 'logo' must be PNG, JPEG, GIF, BMP."}"""));

        var failure = await Assert.ThrowsAsync<PlannerApiException>(() => api.UploadBrandingImageAsync("logo", new MemoryStream([1])));

        Assert.Equal("The image for 'logo' must be PNG, JPEG, GIF, BMP.", failure.Message);
        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
    }

    [Fact]
    public async Task A_query_with_dates_is_sent_as_an_escaped_query_string()
    {
        var (api, handler) = Create(_ => Json(HttpStatusCode.OK, "{}"));
        var query = new AttendanceQuery(
            "group 1", new DateOnly(2026, 1, 5), new DateOnly(2026, 6, 30), null, null, ["sg/kids", "sg2"], 80, 4, "Training; Prüfung", null);

        await api.GetAttendanceAsync(query); // the body is irrelevant here, only the request is under test

        var uri = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/statistics/attendance", uri.AbsolutePath);
        Assert.Equal(
            "groupId=group%201&from=2026-01-05&to=2026-06-30&minAttendancePercent=80&minInvitedEvents=4" +
            "&subGroupIds=sg%2Fkids&subGroupIds=sg2&eventNameContains=Training%3B%20Pr%C3%BCfung",
            uri.Query.TrimStart('?'));
    }

    [Fact]
    public async Task A_query_with_events_sends_their_ids_and_no_dates()
    {
        var (api, handler) = Create(_ => Json(HttpStatusCode.OK, "{}"));
        var query = new AttendanceQuery("g1", FromEventId: "e 1", ToEventId: "e/2", MinAttendancePercent: 60, MinInvitedEvents: 2);

        await api.GetAttendanceAsync(query);

        Assert.Equal(
            "groupId=g1&fromEventId=e%201&toEventId=e%2F2&minAttendancePercent=60&minInvitedEvents=2",
            handler.Requests.Single().RequestUri!.Query.TrimStart('?'));
    }

    [Fact]
    public async Task Event_choices_are_requested_for_a_group()
    {
        var (api, handler) = Create(_ => Json(HttpStatusCode.OK,
            """{"events":[{"id":"e1","name":"Gürtelprüfung","start":"2026-09-26T10:00:00+02:00"}],"suggestedFromEventId":"e0","suggestedToEventId":"e1"}"""));

        var choices = await api.GetEventChoicesAsync("group 1");

        var uri = handler.Requests.Single().RequestUri!;
        Assert.Equal("/api/events", uri.AbsolutePath);
        Assert.Equal("groupId=group%201", uri.Query.TrimStart('?'));
        Assert.Equal("Gürtelprüfung", Assert.Single(choices.Events).Name);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.FromHours(2)), choices.Events[0].Start);
        Assert.Equal("e0", choices.SuggestedFromEventId);
        Assert.Equal("e1", choices.SuggestedToEventId);
    }

    [Fact]
    public async Task Dashboard_request_includes_only_the_given_options()
    {
        var (api, handler) = Create(_ => Json(HttpStatusCode.OK, "{}"));

        await api.GetDashboardAsync("g1", days: 30);

        Assert.Equal("groupId=g1&days=30", handler.Requests.Single().RequestUri!.Query.TrimStart('?'));
    }

    [Fact]
    public async Task Refresh_posts_to_the_refresh_endpoint()
    {
        var (api, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await api.RefreshAsync();

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/refresh", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Server_problem_details_become_the_exception_message()
    {
        var (api, _) = Create(_ => Json(HttpStatusCode.ServiceUnavailable,
            """{"title":"Spond is unavailable","status":503,"detail":"Spond rejected the login."}"""));

        var ex = await Assert.ThrowsAsync<PlannerApiException>(() => api.GetGroupsAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal("Spond rejected the login.", ex.Message);
    }

    [Theory]
    [InlineData(false, "/api/sync/run")]
    [InlineData(true, "/api/sync/run?confirmRemoval=true")]
    public async Task Running_the_sync_posts_to_api_sync_run_and_says_whether_the_removal_is_confirmed(bool confirm, string expected)
    {
        var (api, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await api.RunSyncAsync(confirm);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(expected, request.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task A_refused_sync_run_surfaces_the_reason_the_api_gives()
    {
        var (api, _) = Create(_ => Json(HttpStatusCode.Conflict,
            """{"title":"Sync refused","status":409,"detail":"The data source no longer lists 2 of the 2 stored members of all groups."}"""));

        var ex = await Assert.ThrowsAsync<PlannerApiException>(() => api.RunSyncAsync());

        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.Contains("2 of the 2", ex.Message);
    }

    [Fact]
    public async Task A_refusal_for_a_user_without_the_rights_is_explained_even_though_the_api_sends_no_body()
    {
        var (api, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<PlannerApiException>(() => api.GetUsersAsync());

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal("The API refused the request: your user is not allowed to do this.", ex.Message);
    }

    [Fact]
    public async Task Users_are_deleted_with_a_delete_request_to_the_user()
    {
        var (api, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await api.DeleteUserAsync(7);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/api/users/7", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_non_json_error_page_falls_back_to_the_status_code()
    {
        var (api, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>Bad gateway</html>", Encoding.UTF8, "text/html"),
        });

        var ex = await Assert.ThrowsAsync<PlannerApiException>(() => api.GetInfoAsync());

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.Contains("502", ex.Message);
    }

    [Fact]
    public async Task An_unreachable_server_is_reported_without_a_status_code()
    {
        var api = new PlannerApiClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("connection refused")))
        {
            BaseAddress = new Uri("http://planner.test/"),
        });

        var ex = await Assert.ThrowsAsync<PlannerApiException>(() => api.GetInfoAsync());

        Assert.Null(ex.StatusCode);
        Assert.Contains("planner.test", ex.Message);
    }

    [Fact]
    public async Task Unreadable_success_bodies_are_reported_as_a_version_mismatch()
    {
        var (api, _) = Create(_ => Json(HttpStatusCode.OK, """{"name": 42}"""));

        var ex = await Assert.ThrowsAsync<PlannerApiException>(() => api.GetInfoAsync());

        Assert.Contains("cannot read", ex.Message);
    }
}
