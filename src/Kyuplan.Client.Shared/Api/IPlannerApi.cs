using Kyuplan.Contracts;

namespace Kyuplan.Client.Api;

/// <summary>Everything the clients can ask the Kyuplan API. Failures surface as <see cref="PlannerApiException"/>.</summary>
public interface IPlannerApi
{
    Task<ServerInfoDto> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the installer of the desktop client for a platform (<see cref="ClientPlatform"/>) that the server carries into <paramref name="destination"/>. 404 when it has none (<see cref="ServerInfoDto.ClientVersion"/> is <c>null</c>).</summary>
    Task DownloadClientAsync(string platform, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>Writes the signature of that installer into <paramref name="destination"/>; <c>false</c> (nothing written) when the release was not signed.</summary>
    Task<bool> DownloadClientSignatureAsync(string platform, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>How the instance presents itself: name, accent colour, logos. Open to everybody, so it works before any login.</summary>
    Task<BrandingDto> GetBrandingAsync(CancellationToken cancellationToken = default);

    /// <summary>The branding as it is edited: the texts and the colour in effect, each image slot's own image, and whether anything was saved. Needs <c>branding.view</c> (403 without it).</summary>
    Task<BrandingSettingsDto> GetBrandingSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the product name, the organisation and the accent colour; they apply instead of the API's settings from then on. Answers with the settings.</summary>
    Task<BrandingSettingsDto> SaveBrandingAsync(BrandingInputDto branding, CancellationToken cancellationToken = default);

    /// <summary>Uploads the image of a slot (one of <see cref="BrandingSlots"/>). 400 when it is not an image the slot takes or is larger than <see cref="BrandingRules.MaxImageBytes"/>. Answers with the settings.</summary>
    Task<BrandingSettingsDto> UploadBrandingImageAsync(string slot, Stream image, CancellationToken cancellationToken = default);

    /// <summary>Takes the image of a slot away: it has none from now on. Answers with the settings.</summary>
    Task<BrandingSettingsDto> RemoveBrandingImageAsync(string slot, CancellationToken cancellationToken = default);

    /// <summary>Forgets everything saved: the API's own settings apply again. Answers with the settings.</summary>
    Task<BrandingSettingsDto> ResetBrandingAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the API still has to be set up (it has no user yet), and what it would start with. Open to everybody, so it works before any login.</summary>
    Task<SetupStatusDto> GetSetupAsync(CancellationToken cancellationToken = default);

    /// <summary>The first start: saves the settings and creates the first administrator. 409 once the API has a user. The API may restart itself afterwards.</summary>
    Task<SettingsSavedDto> SetupAsync(SetupInputDto setup, CancellationToken cancellationToken = default);

    /// <summary>The settings the API runs with. Needs <c>settings.view</c> (403 without it).</summary>
    Task<SettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the settings; the API restarts itself when something changed. Needs <c>settings.edit</c>.</summary>
    Task<SettingsSavedDto> SaveSettingsAsync(SettingsInputDto settings, CancellationToken cancellationToken = default);

    /// <summary>The user the request was made as, with their roles and what they may do. Fails with 401 when the login is wrong, so it is how a client checks one.</summary>
    Task<UserDto> GetAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the signed-in user's own user name and/or password. A new password needs the current one (else 400).</summary>
    Task<UserDto> UpdateAccountAsync(AccountUpdateDto account, CancellationToken cancellationToken = default);

    /// <summary>The installed theme packs (built-in and uploaded). Needs no login.</summary>
    Task<ThemeCatalogDto> GetThemesAsync(CancellationToken cancellationToken = default);

    /// <summary>Installs a theme pack (its JSON text) or replaces the one with the same id. Answers with the catalog; the API says what is wrong with a pack it refuses.</summary>
    Task<ThemeCatalogDto> InstallThemeAsync(string json, CancellationToken cancellationToken = default);

    /// <summary>Deletes an uploaded theme pack. Answers with the catalog.</summary>
    Task<ThemeCatalogDto> DeleteThemeAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>The GitHub theme packs and how reading each one went. Needs <c>themes.edit</c>.</summary>
    Task<IReadOnlyList<ThemeSourceDto>> GetThemeSourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a GitHub theme pack (the address of its repository) and reads it. The API says what is wrong with one it refuses (400).</summary>
    Task<ThemeSourceDto> AddThemeSourceAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>Reads a GitHub theme pack again now. A failure is in the answer's <see cref="ThemeSourceDto.LastMessage"/>.</summary>
    Task<ThemeSourceDto> RefreshThemeSourceAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Removes a GitHub theme pack and its themes.</summary>
    Task DeleteThemeSourceAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>What the plugins that run add: their pages the user may open, their permissions, their data sources (the forms only for those who may see the settings).</summary>
    Task<PluginCatalogDto> GetPluginsAsync(CancellationToken cancellationToken = default);

    /// <summary>The installed plugins and whether they run. Needs <c>plugins.view</c>.</summary>
    Task<IReadOnlyList<PluginAdminDto>> GetInstalledPluginsAsync(CancellationToken cancellationToken = default);

    /// <summary>Installs the plugin of a public GitHub repository (its latest release). The API restarts itself when something changed; it says what is wrong with a plugin it refuses (400). Needs <c>plugins.edit</c>.</summary>
    Task<PluginChangedDto> InstallPluginAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>Fetches the latest release of the repository the plugin came from.</summary>
    Task<PluginChangedDto> UpdatePluginAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Removes a plugin; the API restarts itself.</summary>
    Task<PluginChangedDto> RemovePluginAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Reads an endpoint of a plugin (<c>api/plugins/&lt;id&gt;/&lt;path&gt;</c>) as JSON.</summary>
    Task<System.Text.Json.JsonElement> GetPluginDataAsync(string pluginId, string path, CancellationToken cancellationToken = default);

    /// <summary>Calls an endpoint of a plugin (<c>POST</c>, <c>PUT</c> or <c>DELETE</c>) with an optional JSON body. The answer is the JSON the endpoint returned; a default <see cref="System.Text.Json.JsonElement"/> when it returned none.</summary>
    Task<System.Text.Json.JsonElement> SendPluginAsync(string pluginId, string method, string path, System.Text.Json.JsonElement? body, CancellationToken cancellationToken = default);

    /// <summary>How the sync with the data source is doing. Needs <c>sync.view</c>.</summary>
    Task<SyncStatusDto> GetSyncStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the sync with the data source now. With <paramref name="confirmRemoval"/> a run that the API refused because it would remove too many
    /// members is applied anyway. Needs <c>settings.edit</c>; a refused run is an <see cref="ApiException"/> (409).
    /// </summary>
    Task RunSyncAsync(bool confirmRemoval = false, CancellationToken cancellationToken = default);

    /// <summary>The backup databases and how their last run went. Needs <c>backups.view</c>.</summary>
    Task<IReadOnlyList<BackupTargetDto>> GetBackupTargetsAsync(CancellationToken cancellationToken = default);

    /// <summary>Defines a backup database. Needs <c>backups.edit</c>; the API says what is wrong with one it refuses.</summary>
    Task<BackupTargetDto> CreateBackupTargetAsync(BackupTargetInputDto target, CancellationToken cancellationToken = default);

    /// <summary>Replaces the definition of a backup database. Needs <c>backups.edit</c>.</summary>
    Task<BackupTargetDto> UpdateBackupTargetAsync(int id, BackupTargetInputDto target, CancellationToken cancellationToken = default);

    /// <summary>Forgets a backup database. Needs <c>backups.edit</c>.</summary>
    Task DeleteBackupTargetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Updates a backup database now. A run that failed is answered like one that worked: its reason is the target's <see cref="BackupTargetDto.LastMessage"/>. Needs <c>backups.edit</c>.</summary>
    Task<BackupTargetDto> RunBackupTargetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Downloads a backup file with all data, encrypted with <paramref name="passphrase"/> when it is not empty. Needs <c>backups.download</c>.</summary>
    /// <summary>The payment list or the results of an exam as a CSV (<paramref name="excel"/> false) or Excel file. Needs <c>payments.view</c> or <c>exams.view</c>.</summary>
    Task<BackupFile> DownloadExamExportAsync(int examId, ExamExportKind kind, bool excel, CancellationToken cancellationToken = default);

    Task<BackupFile> DownloadBackupAsync(string? passphrase = null, CancellationToken cancellationToken = default);

    /// <summary>Replaces ALL data with a backup file; the API restarts afterwards. Needs <c>backups.restore</c>; the API says what is wrong with a file it refuses.</summary>
    /// <param name="passphrase">Needed when the file is encrypted.</param>
    Task<BackupRestoredDto> RestoreBackupAsync(Stream file, string? passphrase = null, CancellationToken cancellationToken = default);

    /// <summary>Chooses the signed-in user's own theme for a kind of client (<c>null</c>: follow the instance's default). Answers with the user.</summary>
    Task<UserDto> SetThemeAsync(ThemePreferenceDto preference, CancellationToken cancellationToken = default);

    /// <summary>Chooses the signed-in user's own language (<c>null</c>: follow the client's system language). Answers with the user.</summary>
    Task<UserDto> SetLanguageAsync(LanguagePreferenceDto preference, CancellationToken cancellationToken = default);

    /// <summary>All users, by user name. Needs <c>users.view</c> (403 without it).</summary>
    Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a user. Needs <c>users.edit</c>, and <c>users.grant</c> to give roles or permissions.</summary>
    Task<UserDto> CreateUserAsync(UserInputDto user, CancellationToken cancellationToken = default);

    /// <summary>Changes a user's name and, when the input has a password, the password; with <c>users.grant</c> also the roles and permissions.</summary>
    Task<UserDto> UpdateUserAsync(int userId, UserInputDto user, CancellationToken cancellationToken = default);

    /// <summary>The members the signed-in user is (an administrator links them); every user may ask.</summary>
    Task<IReadOnlyList<MemberChoiceDto>> GetOwnMembersAsync(CancellationToken cancellationToken = default);

    /// <summary>Says which members a user is (all of them; the others are released). Needs <c>users.edit</c>; a member can be one user's only (409). Answers with the user.</summary>
    Task<UserDto> SetUserMembersAsync(int userId, IReadOnlyList<int> memberIds, CancellationToken cancellationToken = default);

    /// <summary>Every member by name with the user who already is them, for choosing the members of a user. Needs <c>members.view</c> or <c>users.edit</c>.</summary>
    Task<IReadOnlyList<MemberChoiceDto>> GetMemberChoicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes a user other than the one signed in. Needs <c>users.delete</c>.</summary>
    Task DeleteUserAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>All roles by name, the administrator role first, each with its permissions and how many users have it. Needs <c>roles.view</c> or <c>users.grant</c>.</summary>
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a role. 409 when the name is taken (in any case). Needs <c>roles.edit</c>.</summary>
    Task<RoleDto> CreateRoleAsync(RoleInputDto role, CancellationToken cancellationToken = default);

    /// <summary>Replaces the name and the permissions of a role. 409 when the name is taken or it is the administrator role.</summary>
    Task<RoleDto> UpdateRoleAsync(int roleId, RoleInputDto role, CancellationToken cancellationToken = default);

    /// <summary>Deletes a role and takes it from its users. 409 for the administrator role.</summary>
    Task DeleteRoleAsync(int roleId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GroupDto>> GetGroupsAsync(CancellationToken cancellationToken = default);

    Task<DashboardDto> GetDashboardAsync(
        string groupId, string? subGroupId = null, int? days = null, CancellationToken cancellationToken = default);

    /// <summary>The events around today to pick a time span from, and the exam-to-exam span the server suggests.</summary>
    Task<EventChoicesDto> GetEventChoicesAsync(string groupId, CancellationToken cancellationToken = default);

    Task<AttendanceStatisticsDto> GetAttendanceAsync(AttendanceQuery query, CancellationToken cancellationToken = default);

    /// <summary>Ask the server to drop its cached Spond data.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>All exams, the next one first, with the filters saved for each.</summary>
    Task<IReadOnlyList<ExamDto>> GetExamsAsync(CancellationToken cancellationToken = default);

    Task<ExamDto> CreateExamAsync(ExamInputDto exam, CancellationToken cancellationToken = default);

    Task<ExamDto> UpdateExamAsync(int examId, ExamInputDto exam, CancellationToken cancellationToken = default);

    /// <summary>Deletes the exam and the filters saved for it.</summary>
    Task DeleteExamAsync(int examId, CancellationToken cancellationToken = default);

    Task<SavedFilterDto> CreateFilterAsync(int examId, SavedFilterInputDto filter, CancellationToken cancellationToken = default);

    Task<SavedFilterDto> UpdateFilterAsync(int examId, int filterId, SavedFilterInputDto filter, CancellationToken cancellationToken = default);

    Task DeleteFilterAsync(int examId, int filterId, CancellationToken cancellationToken = default);

    /// <summary>Everybody an exam knows: those invited on Spond with their answer, those saved for it, those who received a belt at it. By name.</summary>
    Task<IReadOnlyList<ExamMemberDto>> GetExamMembersAsync(int examId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The members of the exam's group, judged for the exam with a saved filter of the group (<c>null</c> = the default: previous exam to this
    /// one): the belt each would take, and whether they meet what it asks on the day of the exam.
    /// </summary>
    Task<ExamSuggestionsDto> GetExamSuggestionsAsync(int examId, int? filterId = null, CancellationToken cancellationToken = default);

    /// <summary>Saves these members for the exam, replacing the ones saved before. Nobody is invited on Spond by it.</summary>
    Task<IReadOnlyList<ExamMemberDto>> SetExamCandidatesAsync(int examId, ExamCandidatesInputDto candidates, CancellationToken cancellationToken = default);

    /// <summary>Records the belt a member received at an exam (<c>null</c> takes the result back). 400 while the exam has not taken place.</summary>
    Task<ExamMemberDto> SetExamResultAsync(int examId, int memberId, ExamResultInputDto result, CancellationToken cancellationToken = default);

    /// <summary>The price list of the exams: the general fee and the stamp fee and belt price per belt. Needs <c>payments.view</c> or <c>prices.edit</c>.</summary>
    Task<ExamPricesDto> GetExamPricesAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the price list. Needs <c>prices.edit</c>.</summary>
    Task<ExamPricesDto> SetExamPricesAsync(ExamPricesDto prices, CancellationToken cancellationToken = default);

    /// <summary>Who pays for an exam, what each owes and what they paid. Needs <c>payments.view</c>.</summary>
    Task<ExamPaymentsDto> GetExamPaymentsAsync(int examId, CancellationToken cancellationToken = default);

    /// <summary>Records the total a member paid for an exam. Needs <c>payments.edit</c>.</summary>
    Task<ExamPaymentDto> SetExamPaymentAsync(int examId, int memberId, ExamPaymentInputDto payment, CancellationToken cancellationToken = default);

    /// <summary>All belts in the order of the progression, each with its requirements and how many members hold it.</summary>
    Task<IReadOnlyList<BeltDto>> GetBeltsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a belt. 409 when the name is taken (in any case), 400 when a required belt does not exist.</summary>
    Task<BeltDto> CreateBeltAsync(BeltInputDto belt, CancellationToken cancellationToken = default);

    /// <summary>Replaces everything about a belt. 409 when the name is taken, 400 when it would require itself.</summary>
    Task<BeltDto> UpdateBeltAsync(int beltId, BeltInputDto belt, CancellationToken cancellationToken = default);

    /// <summary>Deletes a belt. 409 while a member holds it or another belt requires it.</summary>
    Task DeleteBeltAsync(int beltId, CancellationToken cancellationToken = default);

    /// <summary>The members of a Spond group the API holds, by name, with the belt they hold and the day they started.</summary>
    Task<IReadOnlyList<MemberDto>> GetMembersAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>Sets the belt, the day it was received, the start day and the birth date of a member (<c>null</c> clears any).</summary>
    Task<MemberDto> UpdateMemberAsync(int memberId, MemberInputDto member, CancellationToken cancellationToken = default);

    /// <summary>The members of a Spond group a user hid, by name (<c>members.hidden</c>); no other call returns them.</summary>
    Task<IReadOnlyList<MemberDto>> GetHiddenMembersAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>Hides a member: no statistic counts them and nothing lists them but <see cref="GetHiddenMembersAsync"/> (<c>members.edit</c>).</summary>
    Task HideMemberAsync(int memberId, CancellationToken cancellationToken = default);

    /// <summary>Makes a hidden member visible again (<c>members.hidden</c>).</summary>
    Task<MemberDto> UnhideMemberAsync(int memberId, CancellationToken cancellationToken = default);

    /// <summary>The comments on a member, oldest first (a reply comes after the comment it replies to), with their tags and the history of their edits.</summary>
    Task<IReadOnlyList<RatingDto>> GetMemberRatingsAsync(int memberId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommentDto>> GetCommentsAsync(int memberId, CancellationToken cancellationToken = default);

    /// <summary>Writes a comment on a member as the signed-in user, or a reply to one of the member's comments.</summary>
    Task<CommentDto> CreateCommentAsync(int memberId, CommentInputDto comment, CancellationToken cancellationToken = default);

    /// <summary>Replaces the body and the tags of a comment; the change is recorded with who made it and how.</summary>
    Task<CommentDto> UpdateCommentAsync(int commentId, CommentUpdateDto comment, CancellationToken cancellationToken = default);

    /// <summary>Deletes a comment and the replies to it. 403 unless the user may delete their own comments (and wrote it) or those of others.</summary>
    Task DeleteCommentAsync(int commentId, CancellationToken cancellationToken = default);

    /// <summary>The events of a group within the day, week or month (<see cref="CalendarViews"/>) of a date (<c>null</c> = today).</summary>
    Task<CalendarDto> GetCalendarAsync(string groupId, string view, DateOnly? date = null, CancellationToken cancellationToken = default);

    /// <summary>The events of a group that are going on right now.</summary>
    Task<IReadOnlyList<CalendarEventDto>> GetCurrentEventsAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>One event with everybody invited to it, what they answered and the comments on them that carry the event's tag.</summary>
    Task<CalendarEventDetailDto> GetCalendarEventAsync(string groupId, string eventId, CancellationToken cancellationToken = default);

    /// <summary>Writes a comment on a member of an event; the API tags it with the event's tag.</summary>
    Task<CommentDto> CreateEventCommentAsync(string groupId, string eventId, int memberId, CalendarCommentInputDto comment, CancellationToken cancellationToken = default);

    /// <summary>Evaluates a member at an event with 0 to 5 stars as the signed-in user; replaces the user's earlier evaluation there.</summary>
    Task<RatingDto> RateMemberAsync(string groupId, string eventId, int memberId, int stars, CancellationToken cancellationToken = default);

    /// <summary>Takes the signed-in user's evaluation of a member at an event back.</summary>
    Task UnrateMemberAsync(string groupId, string eventId, int memberId, CancellationToken cancellationToken = default);

    /// <summary>All tags by name, with the event each is connected to and how many comments carry it.</summary>
    Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a tag, optionally connected to a Spond event. 409 when the name is taken (in any case), 404 when the event is not found.</summary>
    Task<TagDto> CreateTagAsync(TagInputDto tag, CancellationToken cancellationToken = default);

    /// <summary>Replaces the name and the event of a tag (no event = disconnected). 409 when the name is taken.</summary>
    Task<TagDto> UpdateTagAsync(int tagId, TagInputDto tag, CancellationToken cancellationToken = default);

    /// <summary>Deletes a tag and takes it off the comments that carry it.</summary>
    Task DeleteTagAsync(int tagId, CancellationToken cancellationToken = default);

    /// <summary>All calendar feeds (ICS addresses for calendar apps) with their sources; needs calendar.feeds.</summary>
    Task<IReadOnlyList<CalendarFeedDto>> GetCalendarFeedsAsync(CancellationToken cancellationToken = default);

    /// <summary>Makes a calendar feed. 409 when the name is taken, 404 when a group is not found, 400 when a subgroup is not one of its group.</summary>
    Task<CalendarFeedDto> CreateCalendarFeedAsync(CalendarFeedInputDto feed, CancellationToken cancellationToken = default);

    /// <summary>Replaces the name and sources of a feed; its address stays.</summary>
    Task<CalendarFeedDto> UpdateCalendarFeedAsync(int feedId, CalendarFeedInputDto feed, CancellationToken cancellationToken = default);

    /// <summary>Gives a feed a new secret address; the old one stops working.</summary>
    Task<CalendarFeedDto> RegenerateCalendarFeedAsync(int feedId, CancellationToken cancellationToken = default);

    Task DeleteCalendarFeedAsync(int feedId, CancellationToken cancellationToken = default);

    /// <summary>The groups kept by hand (data source "manual") with their subgroups; needs manualdata.edit.</summary>
    Task<IReadOnlyList<ManualGroupDto>> GetManualGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a group. 409 when the name is taken.</summary>
    Task<ManualGroupDto> CreateManualGroupAsync(ManualNameInputDto group, CancellationToken cancellationToken = default);

    Task<ManualGroupDto> RenameManualGroupAsync(string groupId, ManualNameInputDto group, CancellationToken cancellationToken = default);

    /// <summary>Deletes a group with its subgroups, members and events.</summary>
    Task DeleteManualGroupAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>Adds a subgroup to a group. 409 when the name is taken in the group.</summary>
    Task<SubGroupDto> CreateManualSubGroupAsync(string groupId, ManualNameInputDto subGroup, CancellationToken cancellationToken = default);

    Task<SubGroupDto> RenameManualSubGroupAsync(string subGroupId, ManualNameInputDto subGroup, CancellationToken cancellationToken = default);

    /// <summary>Deletes a subgroup; its members stay in the group.</summary>
    Task DeleteManualSubGroupAsync(string subGroupId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManualMemberDto>> GetManualMembersAsync(string groupId, CancellationToken cancellationToken = default);

    Task<ManualMemberDto> CreateManualMemberAsync(string groupId, ManualMemberInputDto member, CancellationToken cancellationToken = default);

    /// <summary>Replaces the name, birth date and subgroups of a member.</summary>
    Task<ManualMemberDto> UpdateManualMemberAsync(string memberId, ManualMemberInputDto member, CancellationToken cancellationToken = default);

    Task DeleteManualMemberAsync(string memberId, CancellationToken cancellationToken = default);

    /// <summary>The events of a group, the latest first.</summary>
    Task<IReadOnlyList<ManualEventDto>> GetManualEventsAsync(string groupId, CancellationToken cancellationToken = default);

    Task<ManualEventDto> CreateManualEventAsync(string groupId, ManualEventInputDto manualEvent, CancellationToken cancellationToken = default);

    /// <summary>Replaces an event, its invitations and answers included.</summary>
    Task<ManualEventDto> UpdateManualEventAsync(string eventId, ManualEventInputDto manualEvent, CancellationToken cancellationToken = default);

    Task DeleteManualEventAsync(string eventId, CancellationToken cancellationToken = default);
}
