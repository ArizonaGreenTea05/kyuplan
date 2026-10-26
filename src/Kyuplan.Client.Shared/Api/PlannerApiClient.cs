using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.Api;

public sealed class PlannerApiClient(HttpClient http) : IPlannerApi
{
    public Task<ServerInfoDto> GetInfoAsync(CancellationToken cancellationToken = default) =>
        GetAsync("api/info", PlannerJsonContext.Default.ServerInfoDto, cancellationToken);

    public async Task DownloadClientAsync(string platform, Stream destination, CancellationToken cancellationToken = default)
    {
        // Headers only: the installer is large and is copied on, not buffered (and the client's timeout then only covers the wait for the answer).
        using var response = await SendAsync(() => http.GetAsync(ClientDownload.PathFor(platform),HttpCompletionOption.ResponseHeadersRead, cancellationToken));
        await EnsureSuccessAsync(response, cancellationToken);
        await response.Content.CopyToAsync(destination, cancellationToken);
    }

    public async Task<bool> DownloadClientSignatureAsync(string platform, Stream destination, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => http.GetAsync(ClientDownload.SignaturePathFor(platform), cancellationToken));
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        await EnsureSuccessAsync(response, cancellationToken);
        await response.Content.CopyToAsync(destination, cancellationToken);
        return true;
    }

    public Task<BrandingDto> GetBrandingAsync(CancellationToken cancellationToken = default) =>
        GetAsync("api/branding", PlannerJsonContext.Default.BrandingDto, cancellationToken);

    public Task<BrandingSettingsDto> GetBrandingSettingsAsync(CancellationToken cancellationToken = default) =>
        GetAsync("api/branding/settings", PlannerJsonContext.Default.BrandingSettingsDto, cancellationToken);

    public Task<BrandingSettingsDto> SaveBrandingAsync(BrandingInputDto branding, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync("api/branding", Json(branding, PlannerJsonContext.Default.BrandingInputDto), cancellationToken),
            PlannerJsonContext.Default.BrandingSettingsDto, cancellationToken);

    // The bytes are the body, as they are; the API finds out what they are by looking at them.
    public Task<BrandingSettingsDto> UploadBrandingImageAsync(string slot, Stream image, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() =>
            {
                var content = new StreamContent(image);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                return http.PutAsync($"api/branding/{Uri.EscapeDataString(slot)}", content, cancellationToken);
            },
            PlannerJsonContext.Default.BrandingSettingsDto, cancellationToken);

    public Task<BrandingSettingsDto> RemoveBrandingImageAsync(string slot, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.DeleteAsync($"api/branding/{Uri.EscapeDataString(slot)}", cancellationToken),
            PlannerJsonContext.Default.BrandingSettingsDto, cancellationToken);

    public Task<SetupStatusDto> GetSetupAsync(CancellationToken cancellationToken = default) =>
        GetAsync("api/setup", PlannerJsonContext.Default.SetupStatusDto, cancellationToken);

    public Task<SettingsSavedDto> SetupAsync(SetupInputDto setup, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync("api/setup", Json(setup, PlannerJsonContext.Default.SetupInputDto), cancellationToken),
            PlannerJsonContext.Default.SettingsSavedDto, cancellationToken);

    public Task<SettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        GetAsync("api/settings", PlannerJsonContext.Default.SettingsDto, cancellationToken);

    public Task<SettingsSavedDto> SaveSettingsAsync(SettingsInputDto settings, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync("api/settings", Json(settings, PlannerJsonContext.Default.SettingsInputDto), cancellationToken),
            PlannerJsonContext.Default.SettingsSavedDto, cancellationToken);

    public Task<BrandingSettingsDto> ResetBrandingAsync(CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.DeleteAsync("api/branding", cancellationToken), PlannerJsonContext.Default.BrandingSettingsDto, cancellationToken);

    public Task<UserDto> GetAccountAsync(CancellationToken cancellationToken = default) =>
        GetAsync("api/account", PlannerJsonContext.Default.UserDto, cancellationToken);

    public Task<PluginCatalogDto> GetPluginsAsync(CancellationToken cancellationToken = default) =>
        GetAsync("api/plugins", PlannerJsonContext.Default.PluginCatalogDto, cancellationToken);

    public async Task<IReadOnlyList<PluginAdminDto>> GetInstalledPluginsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/plugin-admin", PlannerJsonContext.Default.ListPluginAdminDto, cancellationToken);

    public Task<PluginChangedDto> InstallPluginAsync(string url, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync("api/plugin-admin", Json(new PluginInstallInputDto(url), PlannerJsonContext.Default.PluginInstallInputDto), cancellationToken),
            PlannerJsonContext.Default.PluginChangedDto, cancellationToken);

    public Task<PluginChangedDto> UpdatePluginAsync(string id, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync($"api/plugin-admin/{Uri.EscapeDataString(id)}/update", null, cancellationToken),
            PlannerJsonContext.Default.PluginChangedDto, cancellationToken);

    public Task<PluginChangedDto> RemovePluginAsync(string id, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.DeleteAsync($"api/plugin-admin/{Uri.EscapeDataString(id)}", cancellationToken),
            PlannerJsonContext.Default.PluginChangedDto, cancellationToken);

    public Task<System.Text.Json.JsonElement> GetPluginDataAsync(string pluginId, string path, CancellationToken cancellationToken = default) =>
        SendPluginAsync(() => http.GetAsync(PluginUrl(pluginId, path), cancellationToken), cancellationToken);

    public Task<System.Text.Json.JsonElement> SendPluginAsync(string pluginId, string method, string path, System.Text.Json.JsonElement? body, CancellationToken cancellationToken = default) =>
        SendPluginAsync(() =>
        {
            var request = new HttpRequestMessage(new HttpMethod(method), PluginUrl(pluginId, path));
            if (body is { } value) request.Content = JsonContent.Create(value, PlannerJsonContext.Default.JsonElement);
            return http.SendAsync(request, cancellationToken);
        }, cancellationToken);

    // The path comes from the layout of a plugin, which the API has checked (no way out of the plugin's own addresses); it is escaped by part all the same.
    private static string PluginUrl(string pluginId, string path) =>
        $"api/plugins/{Uri.EscapeDataString(pluginId)}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";

    private async Task<System.Text.Json.JsonElement> SendPluginAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(send);
        await EnsureSuccessAsync(response, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0) return default;

        try
        {
            return await response.Content.ReadFromJsonAsync(PlannerJsonContext.Default.JsonElement, cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return default; // an answer that is no JSON (text) is a success all the same
        }
    }

    public Task<ThemeCatalogDto> GetThemesAsync(CancellationToken cancellationToken = default) =>
        GetAsync("api/themes", PlannerJsonContext.Default.ThemeCatalogDto, cancellationToken);

    public Task<ThemeCatalogDto> InstallThemeAsync(string json, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync("api/themes", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), cancellationToken),
            PlannerJsonContext.Default.ThemeCatalogDto, cancellationToken);

    public Task<ThemeCatalogDto> DeleteThemeAsync(string id, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.DeleteAsync($"api/themes/{Uri.EscapeDataString(id)}", cancellationToken),
            PlannerJsonContext.Default.ThemeCatalogDto, cancellationToken);

    public async Task<IReadOnlyList<ThemeSourceDto>> GetThemeSourcesAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/themes/sources", PlannerJsonContext.Default.ListThemeSourceDto, cancellationToken);

    public Task<ThemeSourceDto> AddThemeSourceAsync(string url, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync("api/themes/sources", Json(new ThemeSourceInputDto(url), PlannerJsonContext.Default.ThemeSourceInputDto), cancellationToken),
            PlannerJsonContext.Default.ThemeSourceDto, cancellationToken);

    public Task<ThemeSourceDto> RefreshThemeSourceAsync(string id, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync($"api/themes/sources/{Uri.EscapeDataString(id)}/refresh", null, cancellationToken),
            PlannerJsonContext.Default.ThemeSourceDto, cancellationToken);

    public Task DeleteThemeSourceAsync(string id, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/themes/sources/{Uri.EscapeDataString(id)}", cancellationToken), cancellationToken);

    public async Task<SyncStatusDto> GetSyncStatusAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/sync/status", PlannerJsonContext.Default.SyncStatusDto, cancellationToken);

    public Task RunSyncAsync(bool confirmRemoval = false, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.PostAsync(confirmRemoval ? "api/sync/run?confirmRemoval=true" : "api/sync/run", null, cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<BackupTargetDto>> GetBackupTargetsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/backup/targets", PlannerJsonContext.Default.ListBackupTargetDto, cancellationToken);

    public Task<BackupTargetDto> CreateBackupTargetAsync(BackupTargetInputDto target, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync("api/backup/targets", Json(target, PlannerJsonContext.Default.BackupTargetInputDto), cancellationToken),
            PlannerJsonContext.Default.BackupTargetDto, cancellationToken);

    public Task<BackupTargetDto> UpdateBackupTargetAsync(int id, BackupTargetInputDto target, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/backup/targets/{id}", Json(target, PlannerJsonContext.Default.BackupTargetInputDto), cancellationToken),
            PlannerJsonContext.Default.BackupTargetDto, cancellationToken);

    public Task DeleteBackupTargetAsync(int id, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/backup/targets/{id}", cancellationToken), cancellationToken);

    public Task<BackupTargetDto> RunBackupTargetAsync(int id, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync($"api/backup/targets/{id}/run", null, cancellationToken), PlannerJsonContext.Default.BackupTargetDto, cancellationToken);

    public async Task<BackupFile> DownloadExamExportAsync(int examId, ExamExportKind kind, bool excel, CancellationToken cancellationToken = default)
    {
        var what = kind == ExamExportKind.Payments ? "payments" : "results";
        var format = excel ? "xlsx" : "csv";
        using var response = await SendAsync(() => http.GetAsync($"api/exams/{examId}/{what}/export?format={format}", cancellationToken));
        await EnsureSuccessAsync(response, cancellationToken);

        var name = response.Content.Headers.ContentDisposition?.FileName?.Trim('"') is { Length: > 0 } given ? given : $"exam-{examId}-{what}.{format}";
        return new BackupFile(name, await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    public async Task<BackupFile> DownloadBackupAsync(string? passphrase = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => http.SendAsync(WithPassphrase(new HttpRequestMessage(HttpMethod.Get, "api/backup/download"), passphrase), cancellationToken));
        await EnsureSuccessAsync(response, cancellationToken);

        var name = response.Content.Headers.ContentDisposition?.FileName?.Trim('"') is { Length: > 0 } given ? given : $"kyuplan-backup{BackupRules.Extension}";
        return new BackupFile(name, await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    public Task<BackupRestoredDto> RestoreBackupAsync(Stream file, string? passphrase = null, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.SendAsync(WithPassphrase(new HttpRequestMessage(HttpMethod.Post, "api/backup/restore") { Content = new StreamContent(file) }, passphrase), cancellationToken),
            PlannerJsonContext.Default.BackupRestoredDto, cancellationToken);

    private static HttpRequestMessage WithPassphrase(HttpRequestMessage request, string? passphrase)
    {
        if (!string.IsNullOrEmpty(passphrase)) request.Headers.Add(BackupRules.PassphraseHeader, BackupRules.EncodePassphrase(passphrase));
        return request;
    }

    public Task<UserDto> SetThemeAsync(ThemePreferenceDto preference, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync("api/account/theme", Json(preference, PlannerJsonContext.Default.ThemePreferenceDto), cancellationToken),
            PlannerJsonContext.Default.UserDto, cancellationToken);

    public Task<UserDto> SetLanguageAsync(LanguagePreferenceDto preference, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync("api/account/language", Json(preference, PlannerJsonContext.Default.LanguagePreferenceDto), cancellationToken),
            PlannerJsonContext.Default.UserDto, cancellationToken);

    public Task<UserDto> UpdateAccountAsync(AccountUpdateDto account, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync("api/account", Json(account, PlannerJsonContext.Default.AccountUpdateDto), cancellationToken),
            PlannerJsonContext.Default.UserDto, cancellationToken);

    public async Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/users", PlannerJsonContext.Default.ListUserDto, cancellationToken);

    public Task<UserDto> CreateUserAsync(UserInputDto user, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync("api/users", Json(user, PlannerJsonContext.Default.UserInputDto), cancellationToken),
            PlannerJsonContext.Default.UserDto, cancellationToken);

    public Task<UserDto> UpdateUserAsync(int userId, UserInputDto user, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/users/{userId}", Json(user, PlannerJsonContext.Default.UserInputDto), cancellationToken),
            PlannerJsonContext.Default.UserDto, cancellationToken);

    public async Task<IReadOnlyList<MemberChoiceDto>> GetOwnMembersAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/account/members", PlannerJsonContext.Default.ListMemberChoiceDto, cancellationToken);

    public Task<UserDto> SetUserMembersAsync(int userId, IReadOnlyList<int> memberIds, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/users/{userId}/members", Json(new UserMembersDto(memberIds), PlannerJsonContext.Default.UserMembersDto), cancellationToken),
            PlannerJsonContext.Default.UserDto, cancellationToken);

    public async Task<IReadOnlyList<MemberChoiceDto>> GetMemberChoicesAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/members/choices", PlannerJsonContext.Default.ListMemberChoiceDto, cancellationToken);

    public Task DeleteUserAsync(int userId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/users/{userId}", cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/roles", PlannerJsonContext.Default.ListRoleDto, cancellationToken);

    public Task<RoleDto> CreateRoleAsync(RoleInputDto role, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync("api/roles", Json(role, PlannerJsonContext.Default.RoleInputDto), cancellationToken),
            PlannerJsonContext.Default.RoleDto, cancellationToken);

    public Task<RoleDto> UpdateRoleAsync(int roleId, RoleInputDto role, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/roles/{roleId}", Json(role, PlannerJsonContext.Default.RoleInputDto), cancellationToken),
            PlannerJsonContext.Default.RoleDto, cancellationToken);

    public Task DeleteRoleAsync(int roleId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/roles/{roleId}", cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<GroupDto>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/groups", PlannerJsonContext.Default.ListGroupDto, cancellationToken);

    public Task<DashboardDto> GetDashboardAsync(
        string groupId, string? subGroupId = null, int? days = null, CancellationToken cancellationToken = default)
    {
        var query = $"groupId={Uri.EscapeDataString(groupId)}";
        if (!string.IsNullOrEmpty(subGroupId)) query += $"&subGroupId={Uri.EscapeDataString(subGroupId)}";
        if (days is { } d) query += $"&days={d}";
        return GetAsync($"api/dashboard?{query}", PlannerJsonContext.Default.DashboardDto, cancellationToken);
    }

    public Task<EventChoicesDto> GetEventChoicesAsync(string groupId, CancellationToken cancellationToken = default) =>
        GetAsync($"api/events?groupId={Uri.EscapeDataString(groupId)}", PlannerJsonContext.Default.EventChoicesDto, cancellationToken);

    public Task<AttendanceStatisticsDto> GetAttendanceAsync(AttendanceQuery query, CancellationToken cancellationToken = default) =>
        GetAsync($"api/statistics/attendance?{query.ToQueryString()}", PlannerJsonContext.Default.AttendanceStatisticsDto, cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.PostAsync("api/refresh", content: null, cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<ExamDto>> GetExamsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/exams", PlannerJsonContext.Default.ListExamDto, cancellationToken);

    public Task<ExamDto> CreateExamAsync(ExamInputDto exam, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync("api/exams", Json(exam, PlannerJsonContext.Default.ExamInputDto), cancellationToken),
            PlannerJsonContext.Default.ExamDto, cancellationToken);

    public Task<ExamDto> UpdateExamAsync(int examId, ExamInputDto exam, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/exams/{examId}", Json(exam, PlannerJsonContext.Default.ExamInputDto), cancellationToken),
            PlannerJsonContext.Default.ExamDto, cancellationToken);

    public Task DeleteExamAsync(int examId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/exams/{examId}", cancellationToken), cancellationToken);

    public Task<SavedFilterDto> CreateFilterAsync(int examId, SavedFilterInputDto filter, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync($"api/exams/{examId}/filters", Json(filter, PlannerJsonContext.Default.SavedFilterInputDto), cancellationToken),
            PlannerJsonContext.Default.SavedFilterDto, cancellationToken);

    public Task<SavedFilterDto> UpdateFilterAsync(int examId, int filterId, SavedFilterInputDto filter, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/exams/{examId}/filters/{filterId}", Json(filter, PlannerJsonContext.Default.SavedFilterInputDto), cancellationToken),
            PlannerJsonContext.Default.SavedFilterDto, cancellationToken);

    public Task DeleteFilterAsync(int examId, int filterId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/exams/{examId}/filters/{filterId}", cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<ExamMemberDto>> GetExamMembersAsync(int examId, CancellationToken cancellationToken = default) =>
        await GetAsync($"api/exams/{examId}/members", PlannerJsonContext.Default.ListExamMemberDto, cancellationToken);

    public Task<ExamSuggestionsDto> GetExamSuggestionsAsync(int examId, int? filterId = null, CancellationToken cancellationToken = default) =>
        GetAsync($"api/exams/{examId}/suggestions{(filterId is { } id ? $"?filterId={id}" : "")}",
            PlannerJsonContext.Default.ExamSuggestionsDto, cancellationToken);

    public async Task<IReadOnlyList<ExamMemberDto>> SetExamCandidatesAsync(int examId, ExamCandidatesInputDto candidates, CancellationToken cancellationToken = default) =>
        await SendJsonAsync(() => http.PutAsync($"api/exams/{examId}/candidates", Json(candidates, PlannerJsonContext.Default.ExamCandidatesInputDto), cancellationToken),
            PlannerJsonContext.Default.ListExamMemberDto, cancellationToken);

    public Task<ExamMemberDto> SetExamResultAsync(int examId, int memberId, ExamResultInputDto result, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/exams/{examId}/results/{memberId}", Json(result, PlannerJsonContext.Default.ExamResultInputDto), cancellationToken),
            PlannerJsonContext.Default.ExamMemberDto, cancellationToken);

    public async Task<ExamPricesDto> GetExamPricesAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/exam-prices", PlannerJsonContext.Default.ExamPricesDto, cancellationToken);

    public Task<ExamPricesDto> SetExamPricesAsync(ExamPricesDto prices, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync("api/exam-prices", Json(prices, PlannerJsonContext.Default.ExamPricesDto), cancellationToken),
            PlannerJsonContext.Default.ExamPricesDto, cancellationToken);

    public async Task<ExamPaymentsDto> GetExamPaymentsAsync(int examId, CancellationToken cancellationToken = default) =>
        await GetAsync($"api/exams/{examId}/payments", PlannerJsonContext.Default.ExamPaymentsDto, cancellationToken);

    public Task<ExamPaymentDto> SetExamPaymentAsync(int examId, int memberId, ExamPaymentInputDto payment, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/exams/{examId}/payments/{memberId}", Json(payment, PlannerJsonContext.Default.ExamPaymentInputDto), cancellationToken),
            PlannerJsonContext.Default.ExamPaymentDto, cancellationToken);

    public async Task<IReadOnlyList<BeltDto>> GetBeltsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/belts", PlannerJsonContext.Default.ListBeltDto, cancellationToken);

    public Task<BeltDto> CreateBeltAsync(BeltInputDto belt, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync("api/belts", Json(belt, PlannerJsonContext.Default.BeltInputDto), cancellationToken),
            PlannerJsonContext.Default.BeltDto, cancellationToken);

    public Task<BeltDto> UpdateBeltAsync(int beltId, BeltInputDto belt, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/belts/{beltId}", Json(belt, PlannerJsonContext.Default.BeltInputDto), cancellationToken),
            PlannerJsonContext.Default.BeltDto, cancellationToken);

    public Task DeleteBeltAsync(int beltId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/belts/{beltId}", cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<MemberDto>> GetMembersAsync(string groupId, CancellationToken cancellationToken = default) =>
        await GetAsync($"api/members?groupId={Uri.EscapeDataString(groupId)}", PlannerJsonContext.Default.ListMemberDto, cancellationToken);

    public Task<MemberDto> UpdateMemberAsync(int memberId, MemberInputDto member, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/members/{memberId}", Json(member, PlannerJsonContext.Default.MemberInputDto), cancellationToken),
            PlannerJsonContext.Default.MemberDto, cancellationToken);

    public async Task<IReadOnlyList<MemberDto>> GetHiddenMembersAsync(string groupId, CancellationToken cancellationToken = default) =>
        await GetAsync($"api/members/hidden?groupId={Uri.EscapeDataString(groupId)}", PlannerJsonContext.Default.ListMemberDto, cancellationToken);

    public Task HideMemberAsync(int memberId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.PostAsync($"api/members/{memberId}/hide", null, cancellationToken), cancellationToken);

    public Task<MemberDto> UnhideMemberAsync(int memberId, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync($"api/members/{memberId}/unhide", null, cancellationToken), PlannerJsonContext.Default.MemberDto, cancellationToken);

    public async Task<IReadOnlyList<RatingDto>> GetMemberRatingsAsync(int memberId, CancellationToken cancellationToken = default) =>
        await GetAsync($"api/members/{memberId}/ratings", PlannerJsonContext.Default.ListRatingDto, cancellationToken);

    public async Task<IReadOnlyList<CommentDto>> GetCommentsAsync(int memberId, CancellationToken cancellationToken = default) =>
        await GetAsync($"api/members/{memberId}/comments", PlannerJsonContext.Default.ListCommentDto, cancellationToken);

    public Task<CommentDto> CreateCommentAsync(int memberId, CommentInputDto comment, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync($"api/members/{memberId}/comments", Json(comment, PlannerJsonContext.Default.CommentInputDto), cancellationToken),
            PlannerJsonContext.Default.CommentDto, cancellationToken);

    public Task<CalendarDto> GetCalendarAsync(string groupId, string view, DateOnly? date = null, CancellationToken cancellationToken = default) =>
        GetAsync($"api/calendar?groupId={Uri.EscapeDataString(groupId)}&view={view}" + (date is { } d ? $"&date={d:yyyy-MM-dd}" : ""),
            PlannerJsonContext.Default.CalendarDto, cancellationToken);

    public async Task<IReadOnlyList<CalendarEventDto>> GetCurrentEventsAsync(string groupId, CancellationToken cancellationToken = default) =>
        await GetAsync($"api/calendar/current?groupId={Uri.EscapeDataString(groupId)}", PlannerJsonContext.Default.ListCalendarEventDto, cancellationToken);

    public Task<CalendarEventDetailDto> GetCalendarEventAsync(string groupId, string eventId, CancellationToken cancellationToken = default) =>
        GetAsync($"api/calendar/events/{Uri.EscapeDataString(eventId)}?groupId={Uri.EscapeDataString(groupId)}",
            PlannerJsonContext.Default.CalendarEventDetailDto, cancellationToken);

    public Task<CommentDto> CreateEventCommentAsync(string groupId, string eventId, int memberId, CalendarCommentInputDto comment, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync(
                $"api/calendar/events/{Uri.EscapeDataString(eventId)}/members/{memberId}/comments?groupId={Uri.EscapeDataString(groupId)}",
                Json(comment, PlannerJsonContext.Default.CalendarCommentInputDto), cancellationToken),
            PlannerJsonContext.Default.CommentDto, cancellationToken);

    public Task<RatingDto> RateMemberAsync(string groupId, string eventId, int memberId, int stars, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync(
                $"api/calendar/events/{Uri.EscapeDataString(eventId)}/members/{memberId}/rating?groupId={Uri.EscapeDataString(groupId)}",
                Json(new RatingInputDto(stars), PlannerJsonContext.Default.RatingInputDto), cancellationToken),
            PlannerJsonContext.Default.RatingDto, cancellationToken);

    public Task UnrateMemberAsync(string groupId, string eventId, int memberId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync(
            $"api/calendar/events/{Uri.EscapeDataString(eventId)}/members/{memberId}/rating?groupId={Uri.EscapeDataString(groupId)}", cancellationToken), cancellationToken);

    public Task<CommentDto> UpdateCommentAsync(int commentId, CommentUpdateDto comment, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/comments/{commentId}", Json(comment, PlannerJsonContext.Default.CommentUpdateDto), cancellationToken),
            PlannerJsonContext.Default.CommentDto, cancellationToken);

    public Task DeleteCommentAsync(int commentId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/comments/{commentId}", cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/tags", PlannerJsonContext.Default.ListTagDto, cancellationToken);

    public Task<TagDto> CreateTagAsync(TagInputDto tag, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync("api/tags", Json(tag, PlannerJsonContext.Default.TagInputDto), cancellationToken),
            PlannerJsonContext.Default.TagDto, cancellationToken);

    public Task<TagDto> UpdateTagAsync(int tagId, TagInputDto tag, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/tags/{tagId}", Json(tag, PlannerJsonContext.Default.TagInputDto), cancellationToken),
            PlannerJsonContext.Default.TagDto, cancellationToken);

    public Task DeleteTagAsync(int tagId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/tags/{tagId}", cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<CalendarFeedDto>> GetCalendarFeedsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/calendar-feeds", PlannerJsonContext.Default.ListCalendarFeedDto, cancellationToken);

    public Task<CalendarFeedDto> CreateCalendarFeedAsync(CalendarFeedInputDto feed, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync("api/calendar-feeds", Json(feed, PlannerJsonContext.Default.CalendarFeedInputDto), cancellationToken),
            PlannerJsonContext.Default.CalendarFeedDto, cancellationToken);

    public Task<CalendarFeedDto> UpdateCalendarFeedAsync(int feedId, CalendarFeedInputDto feed, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/calendar-feeds/{feedId}", Json(feed, PlannerJsonContext.Default.CalendarFeedInputDto), cancellationToken),
            PlannerJsonContext.Default.CalendarFeedDto, cancellationToken);

    public Task<CalendarFeedDto> RegenerateCalendarFeedAsync(int feedId, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync($"api/calendar-feeds/{feedId}/regenerate", null, cancellationToken),
            PlannerJsonContext.Default.CalendarFeedDto, cancellationToken);

    public Task DeleteCalendarFeedAsync(int feedId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/calendar-feeds/{feedId}", cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<ManualGroupDto>> GetManualGroupsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("api/manual/groups", PlannerJsonContext.Default.ListManualGroupDto, cancellationToken);

    public Task<ManualGroupDto> CreateManualGroupAsync(ManualNameInputDto group, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync("api/manual/groups", Json(group, PlannerJsonContext.Default.ManualNameInputDto), cancellationToken),
            PlannerJsonContext.Default.ManualGroupDto, cancellationToken);

    public Task<ManualGroupDto> RenameManualGroupAsync(string groupId, ManualNameInputDto group, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/manual/groups/{Uri.EscapeDataString(groupId)}", Json(group, PlannerJsonContext.Default.ManualNameInputDto), cancellationToken),
            PlannerJsonContext.Default.ManualGroupDto, cancellationToken);

    public Task DeleteManualGroupAsync(string groupId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/manual/groups/{Uri.EscapeDataString(groupId)}", cancellationToken), cancellationToken);

    public Task<SubGroupDto> CreateManualSubGroupAsync(string groupId, ManualNameInputDto subGroup, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync($"api/manual/groups/{Uri.EscapeDataString(groupId)}/subgroups", Json(subGroup, PlannerJsonContext.Default.ManualNameInputDto), cancellationToken),
            PlannerJsonContext.Default.SubGroupDto, cancellationToken);

    public Task<SubGroupDto> RenameManualSubGroupAsync(string subGroupId, ManualNameInputDto subGroup, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/manual/subgroups/{Uri.EscapeDataString(subGroupId)}", Json(subGroup, PlannerJsonContext.Default.ManualNameInputDto), cancellationToken),
            PlannerJsonContext.Default.SubGroupDto, cancellationToken);

    public Task DeleteManualSubGroupAsync(string subGroupId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/manual/subgroups/{Uri.EscapeDataString(subGroupId)}", cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<ManualMemberDto>> GetManualMembersAsync(string groupId, CancellationToken cancellationToken = default) =>
        await GetAsync($"api/manual/groups/{Uri.EscapeDataString(groupId)}/members", PlannerJsonContext.Default.ListManualMemberDto, cancellationToken);

    public Task<ManualMemberDto> CreateManualMemberAsync(string groupId, ManualMemberInputDto member, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync($"api/manual/groups/{Uri.EscapeDataString(groupId)}/members", Json(member, PlannerJsonContext.Default.ManualMemberInputDto), cancellationToken),
            PlannerJsonContext.Default.ManualMemberDto, cancellationToken);

    public Task<ManualMemberDto> UpdateManualMemberAsync(string memberId, ManualMemberInputDto member, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/manual/members/{Uri.EscapeDataString(memberId)}", Json(member, PlannerJsonContext.Default.ManualMemberInputDto), cancellationToken),
            PlannerJsonContext.Default.ManualMemberDto, cancellationToken);

    public Task DeleteManualMemberAsync(string memberId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/manual/members/{Uri.EscapeDataString(memberId)}", cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<ManualEventDto>> GetManualEventsAsync(string groupId, CancellationToken cancellationToken = default) =>
        await GetAsync($"api/manual/groups/{Uri.EscapeDataString(groupId)}/events", PlannerJsonContext.Default.ListManualEventDto, cancellationToken);

    public Task<ManualEventDto> CreateManualEventAsync(string groupId, ManualEventInputDto manualEvent, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PostAsync($"api/manual/groups/{Uri.EscapeDataString(groupId)}/events", Json(manualEvent, PlannerJsonContext.Default.ManualEventInputDto), cancellationToken),
            PlannerJsonContext.Default.ManualEventDto, cancellationToken);

    public Task<ManualEventDto> UpdateManualEventAsync(string eventId, ManualEventInputDto manualEvent, CancellationToken cancellationToken = default) =>
        SendJsonAsync(() => http.PutAsync($"api/manual/events/{Uri.EscapeDataString(eventId)}", Json(manualEvent, PlannerJsonContext.Default.ManualEventInputDto), cancellationToken),
            PlannerJsonContext.Default.ManualEventDto, cancellationToken);

    public Task DeleteManualEventAsync(string eventId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(() => http.DeleteAsync($"api/manual/events/{Uri.EscapeDataString(eventId)}", cancellationToken), cancellationToken);

    private static JsonContent Json<T>(T value, JsonTypeInfo<T> typeInfo) => JsonContent.Create(value, typeInfo);

    private Task<T> GetAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken) =>
        SendJsonAsync(() => http.GetAsync(url, cancellationToken), typeInfo, cancellationToken);

    private async Task SendNoContentAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(send);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Sends a request and reads the JSON answer; failures surface as <see cref="PlannerApiException"/>.</summary>
    private async Task<T> SendJsonAsync<T>(Func<Task<HttpResponseMessage>> send, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(send);
        await EnsureSuccessAsync(response, cancellationToken);

        try
        {
            return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken)
                   ?? throw new PlannerApiException(Strings.Api_Empty, response.StatusCode);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new PlannerApiException(Strings.Api_Unreadable, response.StatusCode, ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException ex)
        {
            throw new PlannerApiException(string.Format(Strings.Api_Unreachable, http.BaseAddress), null, ex);
        }
        catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
        {
            throw new PlannerApiException(Strings.Api_Timeout, HttpStatusCode.RequestTimeout, ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        ProblemDetailsPayload? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync(PlannerJsonContext.Default.ProblemDetailsPayload, cancellationToken);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            // Not a problem+json body (e.g. a proxy error page): fall back to the status code below.
        }

        var detail = problem?.Errors is { Count: > 0 } errors
            ? string.Join(" ", errors.Values.SelectMany(v => v))
            : problem?.Detail ?? problem?.Title;

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => Strings.Api_Unauthorized,
            // The authorization middleware says nothing more than the status: "administrators only". When the API names a reason (a
            // comment that only its author or an administrator may delete), that reason is the better message.
            HttpStatusCode.Forbidden when detail is null => Strings.Api_Forbidden,
            _ when detail is not null => detail,
            _ => string.Format(Strings.Api_Status, (int)response.StatusCode, response.ReasonPhrase),
        };

        throw new PlannerApiException(message, response.StatusCode);
    }
}
