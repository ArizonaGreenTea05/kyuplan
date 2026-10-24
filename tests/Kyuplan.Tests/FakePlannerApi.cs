using System.Net;
using Kyuplan.Client.Api;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestData;

namespace Kyuplan.Tests;

/// <summary>Scriptable stand-in for the API, recording every request the view models make.</summary>
internal sealed class FakePlannerApi : IPlannerApi
{
    public ServerInfoDto Info { get; set; } = new("kyuplan", "1.0", false, 70, 4, "Europe/Berlin");

    public List<GroupDto> Groups { get; set; } =
    [
        new("g1", "Dojo", 3, [new SubGroupDto("sg1", "Kinder")]),
        new("g2", "Team", 2, []),
    ];

    /// <summary>When set, every call fails with this exception.</summary>
    public Exception? FailWith { get; set; }

    public Func<AttendanceQuery, CancellationToken, Task<AttendanceStatisticsDto>> AttendanceHandler { get; set; }

    public List<AttendanceQuery> AttendanceRequests { get; } = [];

    /// <summary>What the server offers and suggests as time span, for every group.</summary>
    public EventChoicesDto EventChoices { get; set; } = SampleEventChoices();

    /// <summary>The group of every event-choices request.</summary>
    public List<string> EventChoiceRequests { get; } = [];

    public List<(string GroupId, int? Days)> DashboardRequests { get; } = [];

    public int RefreshCalls { get; private set; }

    public DashboardDto Dashboard { get; set; } = SampleDashboard();

    public FakePlannerApi()
    {
        AttendanceHandler = (filter, _) => Task.FromResult(Statistics(filter));
        BrandingSettings = BrandingDefaults;
    }

    public Task<ServerInfoDto> GetInfoAsync(CancellationToken cancellationToken = default) =>
        FailWith is { } ex ? Task.FromException<ServerInfoDto>(ex) : Task.FromResult(Info);

    /// <summary>The bytes the fake serves as an installer.</summary>
    public byte[] ClientInstaller { get; set; } = [1, 2, 3];

    /// <summary>The platform of every download.</summary>
    public List<string> ClientDownloads { get; } = [];

    public async Task DownloadClientAsync(string platform, Stream destination, CancellationToken cancellationToken = default)
    {
        if (FailWith is { } ex) throw ex;
        ClientDownloads.Add(platform);
        await destination.WriteAsync(ClientInstaller, cancellationToken);
    }

    /// <summary>The signature the fake serves for the installer; <c>null</c> = the release is unsigned.</summary>
    public byte[]? ClientSignature { get; set; }

    public async Task<bool> DownloadClientSignatureAsync(string platform, Stream destination, CancellationToken cancellationToken = default)
    {
        if (FailWith is { } ex) throw ex;
        if (ClientSignature is null) return false;
        await destination.WriteAsync(ClientSignature, cancellationToken);
        return true;
    }

    /// <summary>
    /// What the server says about how the instance presents itself. It fails with <see cref="BrandingFailWith"/>, not with
    /// <see cref="FailWith"/>: the branding is a call of its own that the shell makes on top of its data calls.
    /// </summary>
    public BrandingDto Branding { get; set; } = new("kyuplan", "", "#2563eb", null, null, null);

    public Exception? BrandingFailWith { get; set; }

    public int BrandingRequests { get; private set; }

    /// <summary>When set, answers the next requests for the branding instead of <see cref="Branding"/>: a request that is still on its way.</summary>
    public Func<Task<BrandingDto>>? BrandingResponder { get; set; }

    public Task<BrandingDto> GetBrandingAsync(CancellationToken cancellationToken = default)
    {
        BrandingRequests++;
        if (BrandingResponder is { } responder) return responder();
        return BrandingFailWith is { } ex ? Task.FromException<BrandingDto>(ex) : Task.FromResult(Branding);
    }

    // --- Branding as administrators edit it: a server that keeps it, and lets administrators only see and change it ----

    /// <summary>What the API's own settings say: the branding after a reset, and the scene to start from.</summary>
    public BrandingSettingsDto BrandingDefaults { get; set; } = new(
        "kyuplan", "My Club", "#2563eb", false,
        [.. BrandingSlots.All.Select(slot => new BrandingImageDto(slot, slot is BrandingSlots.Logo or BrandingSlots.Icon ? $"api/branding/{slot}" : null))]);

    /// <summary>What the server holds. The calls below change it like the real API would.</summary>
    public BrandingSettingsDto BrandingSettings { get; set; }

    /// <summary>Every call that changed the branding, e.g. <c>save-branding</c>, <c>upload-image:logo</c>, <c>remove-image:logo</c>, <c>reset-branding</c>.</summary>
    public List<string> BrandingCalls { get; } = [];

    public List<BrandingInputDto> BrandingInputs { get; } = [];

    /// <summary>The bytes of every uploaded image, in the order they came.</summary>
    public List<byte[]> BrandingUploads { get; } = [];

    public int BrandingSettingsRequests { get; private set; }

    /// <summary>When set, uploading or removing the image of this slot is refused (and nothing else is).</summary>
    public string? BrandingImageRefusedSlot { get; set; }

    /// <summary>When set, only reading the branding settings fails.</summary>
    public Exception? BrandingSettingsFailure { get; set; }

    public Task<BrandingSettingsDto> GetBrandingSettingsAsync(CancellationToken cancellationToken = default)
    {
        BrandingSettingsRequests++;
        return Run(() =>
        {
            if (BrandingSettingsFailure is { } failure) throw failure;
            if (!Account.Can(Permission.BrandingView)) throw Forbidden();
            return BrandingSettings;
        });
    }

    public Task<BrandingSettingsDto> SaveBrandingAsync(BrandingInputDto branding, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            BrandingCalls.Add("save-branding");
            BrandingInputs.Add(branding);
            if (!Account.Can(Permission.BrandingEdit)) throw Forbidden();
            if (BrandingRules.CheckAccentColor(branding.AccentColor) is { } problem) throw BadRequest(problem);

            return Publish(BrandingSettings = BrandingSettings with
            {
                ProductName = branding.ProductName.Trim(), OrganizationName = branding.OrganizationName.Trim(),
                AccentColor = branding.AccentColor.Trim(), IsCustomized = true,
                DefaultWebTheme = branding.DefaultWebTheme ?? BrandingSettings.DefaultWebTheme,
                DefaultWindowsTheme = branding.DefaultWindowsTheme ?? BrandingSettings.DefaultWindowsTheme,
                Currency = branding.Currency?.Trim() ?? BrandingSettings.Currency,
            });
        });

    public Task<BrandingSettingsDto> UploadBrandingImageAsync(string slot, Stream image, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            BrandingCalls.Add($"upload-image:{slot}");
            if (!Account.Can(Permission.BrandingEdit)) throw Forbidden();
            if (slot == BrandingImageRefusedSlot) throw BadRequest("The API refused this image.");

            using var bytes = new MemoryStream();
            image.CopyTo(bytes);
            BrandingUploads.Add(bytes.ToArray());
            if (bytes.Length > BrandingRules.MaxImageBytes) throw BadRequest("The image is too large.");

            return WithImage(slot, $"api/branding/{slot}");
        });

    public Task<BrandingSettingsDto> RemoveBrandingImageAsync(string slot, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            BrandingCalls.Add($"remove-image:{slot}");
            if (!Account.Can(Permission.BrandingEdit)) throw Forbidden();
            if (slot == BrandingImageRefusedSlot) throw BadRequest("The API refused this image.");

            return WithImage(slot, null);
        });

    public Task<BrandingSettingsDto> ResetBrandingAsync(CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            BrandingCalls.Add("reset-branding");
            if (!Account.Can(Permission.BrandingEdit)) throw Forbidden();

            return Publish(BrandingSettings = BrandingDefaults);
        });

    private BrandingSettingsDto WithImage(string slot, string? url) =>
        Publish(BrandingSettings = BrandingSettings with
        {
            IsCustomized = true,
            Images = [.. BrandingSettings.Images.Select(i => i.Slot == slot ? i with { Url = url } : i)],
        });

    private int _published;

    /// <summary>
    /// What every client is told from now on, as the real API does it after a change: the public view of the saved settings (a missing
    /// dark logo is the light one) with an address for each image that is new every time.
    /// </summary>
    private BrandingSettingsDto Publish(BrandingSettingsDto settings)
    {
        _published++;
        string? Served(string name, params string[] slots) =>
            slots.Any(slot => settings.Images.Single(i => i.Slot == slot).Url is not null) ? $"api/branding/{name}?v={_published}" : null;

        Branding = new BrandingDto(
            settings.ProductName, settings.OrganizationName, settings.AccentColor,
            Served(BrandingSlots.Logo, BrandingSlots.Logo),
            Served(BrandingSlots.LogoOnDark, BrandingSlots.LogoOnDark, BrandingSlots.Logo),
            Served(BrandingSlots.Icon, BrandingSlots.Icon),
            settings.DefaultWebTheme, settings.DefaultWindowsTheme, Currency: settings.Currency);
        return settings;
    }

    /// <summary>Who the server says is signed in. Set <see cref="FailWith"/> to a 401 to play a wrong login.</summary>
    public UserDto Account { get; set; } = TestAccounts.Dto(1, "anna", "anna", [], isAdmin: false);

    public int AccountRequests { get; private set; }

    /// <summary>When set, the answer to <see cref="GetAccountAsync"/> waits until it is completed: a sign-in that takes its time.</summary>
    public TaskCompletionSource? AccountGate { get; set; }

    public async Task<UserDto> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        AccountRequests++;
        if (AccountGate is { } gate) await gate.Task;
        return FailWith is { } ex ? throw ex : Account;
    }

    // --- Users: a server that keeps them in a list, and lets only those with the permissions see and change them ---

    /// <summary>The password <see cref="UpdateAccountAsync"/> expects as the current one.</summary>
    public string AccountPassword { get; set; } = "current password";

    /// <summary>What the server holds, for the administrators' calls. The calls below change it like the real API would.</summary>
    public List<UserDto> Users { get; } = [];

    /// <summary>Every call that changed users or the own account, e.g. <c>create-user:Anna</c>, <c>update-user:2</c>, <c>delete-user:2</c>, <c>update-account:anna</c>.</summary>
    public List<string> UserCalls { get; } = [];

    public List<UserInputDto> UserInputs { get; } = [];

    public List<AccountUpdateDto> AccountUpdates { get; } = [];

    public int UserListRequests { get; private set; }

    /// <summary>When set, only the list of users fails: everything else keeps working.</summary>
    public Exception? UserListFailure { get; set; }

    private int _nextUserId = 100;

    // --- Theme packs: a server that keeps the uploaded ones next to the built-in ones, for those with themes.edit ---

    /// <summary>The uploaded packs the server holds.</summary>
    public List<ThemePackDto> UploadedPacks { get; } = [];

    /// <summary>Every call that changed the packs: <c>install-theme:id</c>, <c>delete-theme:id</c>.</summary>
    public List<string> ThemeCalls { get; } = [];

    public int ThemeCatalogRequests { get; private set; }

    private int _themeRevision;

    private ThemeCatalogDto ThemeCatalog => new($"t{_themeRevision}", [.. BuiltInThemePacks.All, .. UploadedPacks]);

    private ThemeCatalogDto PublishThemes()
    {
        _themeRevision++;
        Branding = Branding with { ThemesVersion = ThemeCatalog.Version };
        return ThemeCatalog;
    }

    public Task<ThemeCatalogDto> GetThemesAsync(CancellationToken cancellationToken = default)
    {
        ThemeCatalogRequests++;
        return Run(() => ThemeCatalog);
    }

    public Task<ThemeCatalogDto> InstallThemeAsync(string json, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            if (!Account.Can(Permission.ThemesEdit)) throw Forbidden();
            var pack = System.Text.Json.JsonSerializer.Deserialize<ThemePackDto>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (ThemePackRules.Check(pack) is { } problem) throw BadRequest(problem);
            if (BuiltInThemePacks.Contains(pack!.Id)) throw BadRequest("built in");

            ThemeCalls.Add("install-theme:" + pack.Id);
            UploadedPacks.RemoveAll(p => p.Id == pack.Id);
            UploadedPacks.Add(pack with { BuiltIn = false });
            return PublishThemes();
        });

    public Task<ThemeCatalogDto> DeleteThemeAsync(string id, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            if (!Account.Can(Permission.ThemesEdit)) throw Forbidden();
            ThemeCalls.Add("delete-theme:" + id);
            if (UploadedPacks.RemoveAll(p => p.Id == id) == 0) throw BadRequest("no such pack");
            return PublishThemes();
        });

    // --- Backups ---------------------------------------------------------------------------------------------

    public List<BackupTargetDto> BackupTargets { get; } = [];

    /// <summary>What the fake did, in order: <c>create:Name</c>, <c>update:1</c>, <c>delete:1</c>, <c>run:1</c>, <c>download</c>, <c>restore:bytes</c>.</summary>
    public List<string> BackupCalls { get; } = [];

    /// <summary>What a run of a backup database reports; <c>null</c> = it worked.</summary>
    public string? BackupRunFails { get; set; }

    public BackupRestoredDto BackupRestored { get; set; } = new(DateTimeOffset.Parse("2026-10-18T10:00:00Z"), 42);

    // --- Prices and payments -----------------------------------------------------------------------------------

    /// <summary>What <see cref="GetExamPricesAsync"/> answers; <see cref="SetExamPricesAsync"/> replaces it.</summary>
    public ExamPricesDto ExamPrices { get; set; } = new(0, []);

    /// <summary>What <see cref="GetExamPaymentsAsync"/> answers per exam id; <see cref="SetExamPaymentAsync"/> changes the rows like the real API would (the amount due stays).</summary>
    public Dictionary<int, List<ExamPaymentDto>> ExamPayments { get; } = [];

    /// <summary>Every call that changed prices or payments, e.g. <c>set-prices</c>, <c>set-payment:3:7</c>.</summary>
    public List<string> PaymentCalls { get; } = [];

    public List<ExamPaymentInputDto> PaymentInputs { get; } = [];

    public Task<ExamPricesDto> GetExamPricesAsync(CancellationToken cancellationToken = default) => Run(() => ExamPrices);

    public Task<ExamPricesDto> SetExamPricesAsync(ExamPricesDto prices, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            PaymentCalls.Add("set-prices");
            if (!PaymentRules.IsAmount(prices.GeneralFee)) throw BadRequest("The fee is no amount.");
            return ExamPrices = prices with { BeltPrices = [.. prices.BeltPrices.Where(p => p.StampFee > 0 || p.BeltPrice > 0)] };
        });

    public Task<ExamPaymentsDto> GetExamPaymentsAsync(int examId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            var rows = ExamPayments.GetValueOrDefault(examId)?.ToList() ?? [];
            return new ExamPaymentsDto(examId, rows, rows.Sum(r => r.Due), rows.Sum(r => r.Paid));
        });

    public Task<ExamPaymentDto> SetExamPaymentAsync(int examId, int memberId, ExamPaymentInputDto payment, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            PaymentCalls.Add($"set-payment:{examId}:{memberId}");
            PaymentInputs.Add(payment);
            if (!PaymentRules.IsAmount(payment.Paid)) throw BadRequest("The amount is no money.");

            var rows = ExamPayments.GetValueOrDefault(examId) ?? throw NotFound();
            var index = rows.FindIndex(r => r.MemberId == memberId);
            if (index < 0) throw BadRequest("This member does not pay for the exam.");

            var row = rows[index];
            return rows[index] = row with
            {
                OwnBelt = payment.OwnBelt,
                BeltPrice = payment.OwnBelt ? 0 : row.BeltPrice,
                Paid = payment.Paid,
                PaidOn = payment.Paid > 0 ? payment.PaidOn ?? new DateOnly(2026, 10, 13) : null,
                Note = string.IsNullOrWhiteSpace(payment.Note) ? null : payment.Note.Trim(),
                Status = PaymentRules.StatusOf(row.GeneralFee + row.StampFee + (payment.OwnBelt ? 0 : row.BeltPrice), payment.Paid),
            };
        });

    public SyncStatusDto SyncStatus { get; set; } = new(true, 15, false, 0, null, null, []);

    public Task<SyncStatusDto> GetSyncStatusAsync(CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            if (!Account.Can(Permission.SyncView)) throw Forbidden();
            return SyncStatus;
        });

    /// <summary>What running the sync fails with, e.g. the API refusing it (409).</summary>
    public Exception? SyncRunError { get; set; }

    /// <summary>The confirmation values the sync was run with, in order.</summary>
    public List<bool> SyncRuns { get; } = [];

    public Task RunSyncAsync(bool confirmRemoval = false, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            if (!Account.Can(Permission.SettingsEdit)) throw Forbidden();
            if (SyncRunError is { } error) throw error;
            SyncRuns.Add(confirmRemoval);
            if (confirmRemoval) SyncStatus = SyncStatus with { RemovalRefused = false, ConsecutiveFailures = 0 };
            return 0;
        });

    public Task<IReadOnlyList<BackupTargetDto>> GetBackupTargetsAsync(CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<BackupTargetDto>>(() =>
        {
            if (!Account.Can(Permission.BackupsView)) throw Forbidden();
            return [.. BackupTargets];
        });

    public Task<BackupTargetDto> CreateBackupTargetAsync(BackupTargetInputDto target, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            if (!Account.Can(Permission.BackupsEdit)) throw Forbidden();
            if (BackupRules.Check(target) is { } problem) throw BadRequest(problem);

            BackupCalls.Add("create:" + target.Name);
            var created = new BackupTargetDto(BackupTargets.Select(t => t.Id).DefaultIfEmpty(0).Max() + 1, target.Name, target.Kind, target.Location,
                target.IntervalMinutes, target.Enabled, null, null, null, null);
            BackupTargets.Add(created);
            return created;
        });

    public Task<BackupTargetDto> UpdateBackupTargetAsync(int id, BackupTargetInputDto target, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            if (!Account.Can(Permission.BackupsEdit)) throw Forbidden();
            if (BackupRules.Check(target) is { } problem) throw BadRequest(problem);

            BackupCalls.Add("update:" + id);
            var index = BackupTargets.FindIndex(t => t.Id == id);
            if (index < 0) throw BadRequest("no such backup database");
            return BackupTargets[index] = BackupTargets[index] with
            {
                Name = target.Name, Kind = target.Kind, Location = target.Location, IntervalMinutes = target.IntervalMinutes, Enabled = target.Enabled,
            };
        });

    public Task DeleteBackupTargetAsync(int id, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            if (!Account.Can(Permission.BackupsEdit)) throw Forbidden();
            BackupCalls.Add("delete:" + id);
            if (BackupTargets.RemoveAll(t => t.Id == id) == 0) throw BadRequest("no such backup database");
            return 0;
        });

    public Task<BackupTargetDto> RunBackupTargetAsync(int id, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            if (!Account.Can(Permission.BackupsEdit)) throw Forbidden();
            BackupCalls.Add("run:" + id);
            var index = BackupTargets.FindIndex(t => t.Id == id);
            if (index < 0) throw BadRequest("no such backup database");
            var now = DateTimeOffset.Parse("2026-10-18T12:00:00Z");
            return BackupTargets[index] = BackupTargets[index] with
            {
                LastRunUtc = now, LastSucceeded = BackupRunFails is null, LastMessage = BackupRunFails, NextRunUtc = now.AddMinutes(BackupTargets[index].IntervalMinutes),
            };
        });

    /// <summary>Every export asked for, e.g. <c>payments:7:csv</c>.</summary>
    public List<string> ExportCalls { get; } = [];

    public Task<BackupFile> DownloadExamExportAsync(int examId, ExamExportKind kind, bool excel, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            if (!Account.Can(kind == ExamExportKind.Payments ? Permission.PaymentsView : Permission.ExamsView)) throw Forbidden();
            var call = $"{kind.ToString().ToLowerInvariant()}:{examId}:{(excel ? "xlsx" : "csv")}";
            ExportCalls.Add(call);
            return new BackupFile(call.Replace(':', '-'), [1, 2, 3]);
        });

    public Task<BackupFile> DownloadBackupAsync(string? passphrase = null, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            if (!Account.Can(Permission.BackupsDownload)) throw Forbidden();
            BackupCalls.Add(string.IsNullOrEmpty(passphrase) ? "download" : "download:" + passphrase);
            return new BackupFile("kyuplan-backup-test.kyubackup", [1, 2, 3]);
        });

    public async Task<BackupRestoredDto> RestoreBackupAsync(Stream file, string? passphrase = null, CancellationToken cancellationToken = default)
    {
        using var copy = new MemoryStream();
        await file.CopyToAsync(copy, cancellationToken);
        return await Run(() =>
        {
            if (!Account.Can(Permission.BackupsRestore)) throw Forbidden();
            if (copy.Length == 0) throw BadRequest("This is not a backup file.");

            BackupCalls.Add("restore:" + copy.Length + (string.IsNullOrEmpty(passphrase) ? "" : ":" + passphrase));
            return BackupRestored;
        });
    }

    public List<ThemePreferenceDto> ThemeChoices { get; } = [];

    public Task<UserDto> SetThemeAsync(ThemePreferenceDto preference, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ThemeChoices.Add(preference);
            return Account = preference.Client == ThemeClients.Windows
                ? Account with { WindowsTheme = preference.Theme }
                : Account with { WebTheme = preference.Theme };
        });

    public List<LanguagePreferenceDto> LanguageChoices { get; } = [];

    public Task<UserDto> SetLanguageAsync(LanguagePreferenceDto preference, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            LanguageChoices.Add(preference);
            return Account = Account with { Language = preference.Language };
        });

    public Task<UserDto> UpdateAccountAsync(AccountUpdateDto account, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            UserCalls.Add($"update-account:{account.UserName}");
            AccountUpdates.Add(account);

            if (account.NewPassword is not null && account.CurrentPassword != AccountPassword)
                throw new PlannerApiException("The current password is wrong.", HttpStatusCode.BadRequest);
            if (TakenName(account.UserName, account.Aliases, Account.Id) is { } taken) throw Conflict(TakenMessage(taken));

            if (account.NewPassword is not null) AccountPassword = account.NewPassword;
            return Rename(Account.Id, account.UserName, account.DisplayName, account.Aliases, Account.Roles, Account.Permissions);
        });

    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        UserListRequests++;
        return Run<IReadOnlyList<UserDto>>(() =>
        {
            if (UserListFailure is { } failure) throw failure;
            if (!Account.Can(Permission.UsersView)) throw Forbidden();
            return Users.OrderBy(u => u.UserName, StringComparer.OrdinalIgnoreCase).ToList();
        });
    }

    public Task<UserDto> CreateUserAsync(UserInputDto user, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            UserCalls.Add($"create-user:{user.UserName}");
            UserInputs.Add(user);
            if (!Account.Can(Permission.UsersEdit) || MayNotGrant(user)) throw Forbidden();
            if (TakenName(user.UserName, user.Aliases, except: null) is { } taken) throw Conflict(TakenMessage(taken));

            var created = Build(_nextUserId++, user.UserName, user.DisplayName, user.Aliases, RolesOf(user.RoleIds ?? []), user.Permissions ?? []);
            Users.Add(created);
            return created;
        });

    public Task<UserDto> UpdateUserAsync(int userId, UserInputDto user, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            UserCalls.Add($"update-user:{userId}");
            UserInputs.Add(user);
            if (!Account.Can(Permission.UsersEdit) || MayNotGrant(user)) throw Forbidden();

            var existing = Users.FirstOrDefault(u => u.Id == userId) ?? throw NotFound();
            if (TakenName(user.UserName, user.Aliases, userId) is { } taken) throw Conflict(TakenMessage(taken));

            var roles = user.RoleIds is { } roleIds ? RolesOf(roleIds) : existing.Roles;
            if (existing.HasAdministratorRole() && roles.All(r => r.Name != RoleRules.AdministratorName)
                && Users.All(u => u.Id == userId || !u.HasAdministratorRole()))
                throw Conflict("There must be at least one administrator.");

            return Rename(userId, user.UserName, user.DisplayName, user.Aliases, roles, user.Permissions ?? existing.Permissions);
        });

    /// <summary>The user each linked member is (member id to user id), like the members' UserId in the API.</summary>
    public Dictionary<int, int> MemberLinks { get; } = [];

    public Task<UserDto> SetUserMembersAsync(int userId, IReadOnlyList<int> memberIds, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            UserCalls.Add($"set-user-members:{userId}:{string.Join(",", memberIds.Order())}");
            if (!Account.Can(Permission.UsersEdit)) throw Forbidden();

            var user = Users.FirstOrDefault(u => u.Id == userId) ?? (userId == Account.Id ? Account : null) ?? throw NotFound();
            foreach (var id in memberIds)
            {
                if (Members.All(m => m.Id != id)) throw NotFound();
                if (MemberLinks.TryGetValue(id, out var other) && other != userId) throw Conflict("That member is already linked to another user.");
            }

            foreach (var released in MemberLinks.Where(l => l.Value == userId).Select(l => l.Key).ToList()) MemberLinks.Remove(released);
            foreach (var id in memberIds) MemberLinks[id] = userId;

            return Rename(userId, user.UserName, user.DisplayName, user.Aliases, user.Roles, user.Permissions);
        });

    public Task<IReadOnlyList<MemberChoiceDto>> GetMemberChoicesAsync(CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<MemberChoiceDto>>(() =>
        {
            if (!Account.Can(Permission.UsersEdit) && !Account.Can(Permission.MembersView)) throw Forbidden();
            return [.. Members.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => new MemberChoiceDto(m.Id, m.GroupId, m.Name, MemberLinks.TryGetValue(m.Id, out var user) ? user : null))];
        });

    public Task DeleteUserAsync(int userId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            UserCalls.Add($"delete-user:{userId}");
            if (!Account.Can(Permission.UsersDelete)) throw Forbidden();
            if (userId == Account.Id) throw Conflict("You cannot delete your own user.");

            Users.Remove(Users.FirstOrDefault(u => u.Id == userId) ?? throw NotFound());
            foreach (var released in MemberLinks.Where(l => l.Value == userId).Select(l => l.Key).ToList()) MemberLinks.Remove(released);
            return 0;
        });

    /// <summary>The first of the names that is a user name or alias of somebody else, like the API's check.</summary>
    private string? TakenName(string userName, IReadOnlyList<string>? aliases, int? except) =>
        new[] { userName }.Concat(aliases ?? []).Select(n => n.Trim()).FirstOrDefault(name => Users.Any(u =>
            u.Id != except && u.Aliases.Append(u.UserName).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))));

    private static string TakenMessage(string name) => $"The user name or alias '{name}' is already taken.";

    /// <summary>Roles or permissions asked for by somebody who may not give them: refused, not ignored.</summary>
    private bool MayNotGrant(UserInputDto user) =>
        (user.RoleIds is not null || user.Permissions is not null) && !Account.Can(Permission.UsersGrant);

    private IReadOnlyList<RoleRefDto> RolesOf(IReadOnlyList<int> roleIds) =>
        [.. roleIds.Distinct().Select(id => Roles.FirstOrDefault(r => r.Id == id)?.Ref() ?? throw BadRequest($"There is no role with id {id}."))];

    /// <summary>What the API makes of a name input: <c>null</c> keeps the display name and aliases, an empty display name is the user name.</summary>
    private UserDto Build(int userId, string userName, string? displayName, IReadOnlyList<string>? aliases, IReadOnlyList<RoleRefDto> roles, IReadOnlyList<string> permissions)
    {
        var name = userName.Trim();
        var current = Users.FirstOrDefault(u => u.Id == userId) ?? (userId == Account.Id ? Account : null);
        var shown = displayName is null ? current?.DisplayName ?? name : displayName.Trim().Length > 0 ? displayName.Trim() : name;
        var own = Permission.InCatalogueOrder(permissions);
        var effective = roles.Any(r => r.Name == RoleRules.AdministratorName)
            ? Permission.All
            : Permission.InCatalogueOrder(own.Concat(roles.SelectMany(r => Roles.First(role => role.Id == r.Id).Permissions)));
        return new UserDto(userId, name, shown, [.. (aliases ?? current?.Aliases ?? []).Select(a => a.Trim())], roles, own, effective,
            MemberIds: [.. MemberLinks.Where(l => l.Value == userId).Select(l => l.Key).Order()]);
    }

    /// <summary>Changes a user in the list (if they are in it) and, when it is the signed-in one, the account.</summary>
    private UserDto Rename(int userId, string userName, string? displayName, IReadOnlyList<string>? aliases, IReadOnlyList<RoleRefDto> roles, IReadOnlyList<string> permissions)
    {
        var renamed = Build(userId, userName, displayName, aliases, roles, permissions);
        var index = Users.FindIndex(u => u.Id == userId);
        if (index >= 0) Users[index] = renamed;
        if (userId == Account.Id) Account = renamed;
        return renamed;
    }

    // --- Roles: a server that keeps them in a list, and lets only those with the permissions see and change them ---

    /// <summary>What the server holds: the administrator role and the role "User", as the accounts migration makes them.</summary>
    public List<RoleDto> Roles { get; } =
    [
        new(TestAccounts.AdministratorRoleId, RoleRules.AdministratorName, true, [], 1),
        new(TestAccounts.UserRoleId, TestAccounts.UserRoleName, false, TestAccounts.UserRolePermissions, 0),
    ];

    /// <summary>Every call that changed roles, e.g. <c>create-role:Trainer</c>, <c>update-role:3</c>, <c>delete-role:3</c>.</summary>
    public List<string> RoleCalls { get; } = [];

    public List<RoleInputDto> RoleInputs { get; } = [];

    public int RoleListRequests { get; private set; }

    /// <summary>When set, only the list of roles fails: everything else keeps working.</summary>
    public Exception? RoleListFailure { get; set; }

    private int _nextRoleId = 100;

    public Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken cancellationToken = default)
    {
        RoleListRequests++;
        return Run<IReadOnlyList<RoleDto>>(() =>
        {
            if (RoleListFailure is { } failure) throw failure;
            if (!Account.Can(Permission.RolesView) && !Account.Can(Permission.UsersGrant)) throw Forbidden();
            return [.. Roles.Select(r => r with { UserCount = Users.Count(u => u.Roles.Any(x => x.Id == r.Id)) })
                .OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        });
    }

    public Task<RoleDto> CreateRoleAsync(RoleInputDto role, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            RoleCalls.Add($"create-role:{role.Name}");
            RoleInputs.Add(role);
            if (!Account.Can(Permission.RolesEdit)) throw Forbidden();
            if (Roles.Any(r => string.Equals(r.Name, role.Name.Trim(), StringComparison.OrdinalIgnoreCase))) throw Conflict("A role with that name already exists.");

            var created = new RoleDto(_nextRoleId++, role.Name.Trim(), false, Permission.InCatalogueOrder(role.Permissions), 0);
            Roles.Add(created);
            return created;
        });

    public Task<RoleDto> UpdateRoleAsync(int roleId, RoleInputDto role, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            RoleCalls.Add($"update-role:{roleId}");
            RoleInputs.Add(role);
            if (!Account.Can(Permission.RolesEdit)) throw Forbidden();

            var existing = Roles.FirstOrDefault(r => r.Id == roleId) ?? throw NotFound();
            if (existing.IsSystem) throw Conflict("The administrator role cannot be changed or deleted.");
            if (Roles.Any(r => r.Id != roleId && string.Equals(r.Name, role.Name.Trim(), StringComparison.OrdinalIgnoreCase))) throw Conflict("A role with that name already exists.");

            var updated = existing with { Name = role.Name.Trim(), Permissions = Permission.InCatalogueOrder(role.Permissions) };
            Roles[Roles.IndexOf(existing)] = updated;
            return updated;
        });

    public Task DeleteRoleAsync(int roleId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            RoleCalls.Add($"delete-role:{roleId}");
            if (!Account.Can(Permission.RolesEdit)) throw Forbidden();

            var existing = Roles.FirstOrDefault(r => r.Id == roleId) ?? throw NotFound();
            if (existing.IsSystem) throw Conflict("The administrator role cannot be changed or deleted.");

            Roles.Remove(existing);
            return 0;
        });

    public Task<IReadOnlyList<GroupDto>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
        FailWith is { } ex ? Task.FromException<IReadOnlyList<GroupDto>>(ex) : Task.FromResult<IReadOnlyList<GroupDto>>(Groups);

    public Task<DashboardDto> GetDashboardAsync(
        string groupId, string? subGroupId = null, int? days = null, CancellationToken cancellationToken = default)
    {
        DashboardRequests.Add((groupId, days));
        return FailWith is { } ex ? Task.FromException<DashboardDto>(ex) : Task.FromResult(Dashboard);
    }

    public Task<EventChoicesDto> GetEventChoicesAsync(string groupId, CancellationToken cancellationToken = default)
    {
        EventChoiceRequests.Add(groupId);
        return FailWith is { } ex ? Task.FromException<EventChoicesDto>(ex) : Task.FromResult(EventChoices);
    }

    public Task<AttendanceStatisticsDto> GetAttendanceAsync(AttendanceQuery query, CancellationToken cancellationToken = default)
    {
        AttendanceRequests.Add(query);
        return FailWith is { } ex ? Task.FromException<AttendanceStatisticsDto>(ex) : AttendanceHandler(query, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCalls++;
        return FailWith is { } ex ? Task.FromException(ex) : Task.CompletedTask;
    }

    // --- Exams and saved filters: a server that keeps them in a list ---------------------------------------

    /// <summary>What the server holds. Change it to set the scene; the calls below change it like the real API would.</summary>
    public List<ExamDto> Exams { get; } = [];

    /// <summary>Every call that changed exams or filters, e.g. <c>create-exam:exam-2</c> (by event id), <c>create-filter:1:Spring</c>.</summary>
    public List<string> ExamCalls { get; } = [];

    public int ExamListRequests { get; private set; }

    /// <summary>When set, only the list of exams fails: the statistics keep working.</summary>
    public Exception? ExamListFailure { get; set; }

    /// <summary>The next attempt to save a filter fails with this exception; the one after it works again.</summary>
    public Exception? FailNextCreateFilter { get; set; }

    private int _nextId = 1;

    /// <summary>Sets the scene: an event of <see cref="SampleEvents"/> that is an exam, with filters, as if they had been saved before.</summary>
    public ExamDto AddExam(string eventId, params (string Name, FilterSettingsDto Settings)[] filters)
    {
        var id = _nextId++;
        var exam = ExamOf(id, eventId) with
        {
            Filters = filters.Select(f => new SavedFilterDto(_nextId++, id, f.Name, f.Settings, Now)).ToList(),
        };
        Exams.Add(exam);
        return exam;
    }

    /// <summary>An exam is its event: the name and the day are the event's.</summary>
    private static ExamDto ExamOf(int id, string eventId)
    {
        var e = SampleEvents.FirstOrDefault(e => e.Id == eventId) ?? throw NotFound();
        return new ExamDto(id, "g1", e.Id, e.Name, DateOnly.FromDateTime(e.Start.Date), []);
    }

    public Task<IReadOnlyList<ExamDto>> GetExamsAsync(CancellationToken cancellationToken = default)
    {
        ExamListRequests++;
        return (ExamListFailure ?? FailWith) is { } ex
            ? Task.FromException<IReadOnlyList<ExamDto>>(ex)
            : Task.FromResult<IReadOnlyList<ExamDto>>(Exams.ToList());
    }

    public Task<ExamDto> CreateExamAsync(ExamInputDto exam, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ExamCalls.Add($"create-exam:{exam.EventId}");
            var created = ExamOf(_nextId, exam.EventId);
            if (Exams.Any(e => e.EventId == exam.EventId))
                throw Conflict($"The event '{created.Name}' on {created.Date:yyyy-MM-dd} is already an exam.");

            _nextId++;
            created = created with { SubGroupIds = exam.SubGroupIds ?? [] };
            Exams.Add(created);
            return created;
        });

    public Task<ExamDto> UpdateExamAsync(int examId, ExamInputDto exam, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ExamCalls.Add($"update-exam:{examId}");
            var existing = Find(examId);
            var updated = ExamOf(examId, exam.EventId) with { Filters = existing.Filters, SubGroupIds = exam.SubGroupIds ?? existing.SubGroupIds };
            Exams[Exams.IndexOf(existing)] = updated;
            return updated;
        });

    public Task DeleteExamAsync(int examId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ExamCalls.Add($"delete-exam:{examId}");
            Find(examId);
            Exams.RemoveAll(e => e.Id == examId);
            return 0;
        });

    public Task<SavedFilterDto> CreateFilterAsync(int examId, SavedFilterInputDto filter, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ExamCalls.Add($"create-filter:{examId}:{filter.Name}");
            if (FailNextCreateFilter is { } failure)
            {
                FailNextCreateFilter = null;
                throw failure;
            }

            var exam = Find(examId);
            if (exam.Filters.Any(f => Same(f.Name, filter.Name)))
                throw Conflict($"This exam already has a filter named '{filter.Name}'.");

            var created = new SavedFilterDto(_nextId++, examId, filter.Name.Trim(), filter.Settings, Now);
            Exams[Exams.IndexOf(exam)] = exam with { Filters = [.. exam.Filters, created] };
            return created;
        });

    public Task<SavedFilterDto> UpdateFilterAsync(int examId, int filterId, SavedFilterInputDto filter, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ExamCalls.Add($"update-filter:{examId}:{filterId}");
            var exam = Find(examId);
            var existing = exam.Filters.FirstOrDefault(f => f.Id == filterId) ?? throw NotFound();
            var updated = existing with { Name = filter.Name.Trim(), Settings = filter.Settings, UpdatedAt = Now };
            Exams[Exams.IndexOf(exam)] = exam with { Filters = exam.Filters.Select(f => f.Id == filterId ? updated : f).ToList() };
            return updated;
        });

    public Task DeleteFilterAsync(int examId, int filterId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ExamCalls.Add($"delete-filter:{examId}:{filterId}");
            var exam = Find(examId);
            if (exam.Filters.All(f => f.Id != filterId)) throw NotFound();
            Exams[Exams.IndexOf(exam)] = exam with { Filters = exam.Filters.Where(f => f.Id != filterId).ToList() };
            return 0;
        });

    // --- Around an exam: who is in it, who is fit, who is saved, who received a belt -----------------------

    /// <summary>What <see cref="GetExamMembersAsync"/> answers per exam id. The calls below change it like the real API would.</summary>
    public Dictionary<int, List<ExamMemberDto>> ExamMembers { get; } = [];

    /// <summary>What <see cref="GetExamSuggestionsAsync"/> answers per exam id; an exam without an entry has nobody to suggest.</summary>
    public Dictionary<int, ExamSuggestionsDto> ExamSuggestions { get; } = [];

    /// <summary>The exam and the filter of every suggestions request.</summary>
    public List<(int ExamId, int? FilterId)> SuggestionRequests { get; } = [];

    /// <summary>Every call that saved members or recorded belts, e.g. <c>set-candidates:3</c>, <c>set-result:3:7</c>.</summary>
    public List<string> PlanningCalls { get; } = [];

    public List<ExamCandidatesInputDto> CandidateInputs { get; } = [];

    public List<ExamResultInputDto> ResultInputs { get; } = [];

    public int ExamMemberRequests { get; private set; }

    /// <summary>When set, only the suggestions fail.</summary>
    public Exception? SuggestionFailure { get; set; }

    /// <summary>When set, only the list of an exam's members fails.</summary>
    public Exception? ExamMembersFailure { get; set; }

    public Task<IReadOnlyList<ExamMemberDto>> GetExamMembersAsync(int examId, CancellationToken cancellationToken = default)
    {
        ExamMemberRequests++;
        return ExamMembersFailure is { } failure
            ? Task.FromException<IReadOnlyList<ExamMemberDto>>(failure)
            : Run<IReadOnlyList<ExamMemberDto>>(() => ExamMembers.GetValueOrDefault(examId)?.ToList() ?? []);
    }

    public Task<ExamSuggestionsDto> GetExamSuggestionsAsync(int examId, int? filterId = null, CancellationToken cancellationToken = default)
    {
        SuggestionRequests.Add((examId, filterId));
        return SuggestionFailure is { } failure
            ? Task.FromException<ExamSuggestionsDto>(failure)
            : Run(() =>
            {
                var exam = Find(examId);
                var filter = filterId is { } id ? exam.Filters.Concat(Exams.SelectMany(e => e.Filters)).FirstOrDefault(f => f.Id == id) : null;
                return ExamSuggestions.TryGetValue(examId, out var suggestions)
                    ? suggestions with { FilterId = filter?.Id, FilterName = filter?.Name }
                    : new ExamSuggestionsDto(
                        examId, exam.Name, exam.Date, filter?.Id, filter?.Name,
                        new AttendanceFilter(exam.GroupId, exam.Date.AddMonths(-6), exam.Date), []);
            });
    }

    public Task<IReadOnlyList<ExamMemberDto>> SetExamCandidatesAsync(int examId, ExamCandidatesInputDto candidates, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<ExamMemberDto>>(() =>
        {
            PlanningCalls.Add($"set-candidates:{examId}");
            CandidateInputs.Add(candidates);
            Find(examId);
            if (candidates.Add.FirstOrDefault(id => Members.All(m => m.Id != id)) is var unknown and not 0)
                throw BadRequest($"There is no member with id {unknown}.");

            var members = ExamMembers.GetValueOrDefault(examId)?.ToList() ?? [];
            members = members
                .Select(m => m with { IsPreSaved = candidates.Add.Contains(m.MemberId) || (m.IsPreSaved && !candidates.Remove.Contains(m.MemberId)) })
                .Where(m => m.IsPreSaved || m.Invitation is not null || m.ReceivedBeltId is not null).ToList();
            foreach (var id in candidates.Add.Where(id => members.All(m => m.MemberId != id)))
            {
                var member = Members.Single(m => m.Id == id);
                members.Add(new ExamMemberDto(id, member.Name, member.BeltId, null, true, null));
            }

            ExamMembers[examId] = members;
            return members.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        });

    public Task<ExamMemberDto> SetExamResultAsync(int examId, int memberId, ExamResultInputDto result, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            PlanningCalls.Add($"set-result:{examId}:{memberId}");
            ResultInputs.Add(result);
            var exam = Find(examId);
            if (exam.Date > DateOnly.FromDateTime(Now.Date)) throw BadRequest("The exam has not taken place yet.");

            var member = Members.FirstOrDefault(m => m.Id == memberId) ?? throw NotFound();
            if (result.BeltId is { } beltId)
            {
                if (Belts.All(b => b.Id != beltId)) throw BadRequest("There is no such belt.");
                Members[Members.IndexOf(member)] = member = member with { BeltId = beltId, BeltSince = exam.Date };
            }

            var members = ExamMembers.GetValueOrDefault(examId)?.ToList() ?? [];
            var row = members.FirstOrDefault(m => m.MemberId == memberId) ?? new ExamMemberDto(memberId, member.Name, member.BeltId, null, false, null);
            row = row with { BeltId = member.BeltId, ReceivedBeltId = result.BeltId };
            members.RemoveAll(m => m.MemberId == memberId);
            members.Add(row);
            ExamMembers[examId] = members;
            return row;
        });

    // --- Belts and members: a server that keeps them in lists ----------------------------------------------

    /// <summary>The belts the server holds, in the order it answers with. <see cref="BeltDto.MemberCount"/> is worked out from <see cref="Members"/>.</summary>
    public List<BeltDto> Belts { get; } = [];

    /// <summary>The members the server holds, of every group.</summary>
    public List<MemberDto> Members { get; } = [];

    /// <summary>Every call that changed belts or members, e.g. <c>create-belt:Orange</c>, <c>update-belt:3</c>, <c>delete-belt:3</c>, <c>update-member:7</c>.</summary>
    public List<string> BeltCalls { get; } = [];

    public List<BeltInputDto> BeltInputs { get; } = [];

    public List<MemberInputDto> MemberInputs { get; } = [];

    public int BeltListRequests { get; private set; }

    /// <summary>The group of every members request.</summary>
    public List<string> MemberListRequests { get; } = [];

    /// <summary>When set, only the list of belts fails.</summary>
    public Exception? BeltListFailure { get; set; }

    /// <summary>When set, only the list of members fails.</summary>
    public Exception? MemberListFailure { get; set; }

    /// <summary>Sets the scene: a belt, as if it had been created before. Prerequisites are given by the ids of belts that exist.</summary>
    public BeltDto AddBelt(string name, string color = "#FFFFFF", string? stripe = null, params BeltPrerequisiteDto[] requires)
    {
        var belt = new BeltDto(_nextId++, name, color, stripe, requires, 0);
        Belts.Add(belt);
        return belt;
    }

    /// <summary>Sets the scene: a member of a group, as if the sync with Spond had created them.</summary>
    public MemberDto AddMember(
        string name, string groupId = "g1", int? beltId = null, DateOnly? startedAt = null, DateOnly? birthDate = null, DateOnly? beltSince = null)
    {
        var member = new MemberDto(_nextId++, groupId, name, beltId, startedAt, birthDate, beltSince);
        Members.Add(member);
        return member;
    }

    private BeltDto WithCount(BeltDto belt) => belt with { MemberCount = Members.Count(m => m.BeltId == belt.Id) };

    public Task<IReadOnlyList<BeltDto>> GetBeltsAsync(CancellationToken cancellationToken = default)
    {
        BeltListRequests++;
        return BeltListFailure is { } failure
            ? Task.FromException<IReadOnlyList<BeltDto>>(failure)
            : Run<IReadOnlyList<BeltDto>>(() => Belts.Select(WithCount).ToList());
    }

    public Task<BeltDto> CreateBeltAsync(BeltInputDto belt, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            BeltCalls.Add($"create-belt:{belt.Name}");
            BeltInputs.Add(belt);
            if (Belts.Any(b => Same(b.Name, belt.Name))) throw Conflict($"There already is a belt named '{belt.Name}'.");
            if (belt.Prerequisites.Any(p => Belts.All(b => b.Id != p.BeltId))) throw BadRequest("There is no belt to require.");

            var created = new BeltDto(_nextId++, belt.Name.Trim(), belt.PrimaryColor, belt.SecondaryColor, belt.Prerequisites.ToList(), 0);
            Belts.Add(created);
            return created;
        });

    public Task<BeltDto> UpdateBeltAsync(int beltId, BeltInputDto belt, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            BeltCalls.Add($"update-belt:{beltId}");
            BeltInputs.Add(belt);
            var existing = Belts.FirstOrDefault(b => b.Id == beltId) ?? throw NotFound();
            if (Belts.Any(b => b.Id != beltId && Same(b.Name, belt.Name))) throw Conflict($"There already is a belt named '{belt.Name}'.");

            var updated = existing with
            {
                Name = belt.Name.Trim(), PrimaryColor = belt.PrimaryColor, SecondaryColor = belt.SecondaryColor,
                Prerequisites = belt.Prerequisites.ToList(),
            };
            Belts[Belts.IndexOf(existing)] = updated;
            return WithCount(updated);
        });

    public Task DeleteBeltAsync(int beltId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            BeltCalls.Add($"delete-belt:{beltId}");
            var existing = Belts.FirstOrDefault(b => b.Id == beltId) ?? throw NotFound();
            if (Members.Any(m => m.BeltId == beltId) || Belts.Any(b => b.Prerequisites.Any(p => p.BeltId == beltId)))
                throw Conflict($"The belt '{existing.Name}' is in use.");

            Belts.Remove(existing);
            return 0;
        });

    public Task<IReadOnlyList<MemberDto>> GetMembersAsync(string groupId, CancellationToken cancellationToken = default)
    {
        MemberListRequests.Add(groupId);
        return MemberListFailure is { } failure
            ? Task.FromException<IReadOnlyList<MemberDto>>(failure)
            : Run<IReadOnlyList<MemberDto>>(() =>
                Members.Where(m => m.GroupId == groupId).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(WithComments).ToList());
    }

    private MemberDto WithComments(MemberDto member) => member with { CommentCount = Comments.Count(c => c.MemberId == member.Id) };

    public Task<MemberDto> UpdateMemberAsync(int memberId, MemberInputDto member, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            BeltCalls.Add($"update-member:{memberId}");
            MemberInputs.Add(member);
            var existing = Members.FirstOrDefault(m => m.Id == memberId) ?? throw NotFound();
            if (member.BeltId is { } beltId && Belts.All(b => b.Id != beltId)) throw BadRequest("There is no such belt.");

            var updated = existing with
            {
                BeltId = member.BeltId, StartedAt = member.StartedAt, BirthDate = member.BirthDate,
                BeltSince = member.BeltId is null ? null : member.BeltSince,
            };
            Members[Members.IndexOf(existing)] = updated;
            return updated;
        });

    /// <summary>The members a user hid: <see cref="Members"/> are the visible ones, like the server's list.</summary>
    public List<MemberDto> HiddenMembers { get; } = [];

    /// <summary>When set, the hidden members cannot be listed.</summary>
    public Exception? HiddenListFailure { get; set; }

    public List<string> HiddenMemberListRequests { get; } = [];

    public Task<IReadOnlyList<MemberDto>> GetHiddenMembersAsync(string groupId, CancellationToken cancellationToken = default)
    {
        HiddenMemberListRequests.Add(groupId);
        return HiddenListFailure is { } failure
            ? Task.FromException<IReadOnlyList<MemberDto>>(failure)
            : Run<IReadOnlyList<MemberDto>>(() =>
                HiddenMembers.Where(m => m.GroupId == groupId).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task HideMemberAsync(int memberId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            BeltCalls.Add($"hide-member:{memberId}");
            var existing = Members.FirstOrDefault(m => m.Id == memberId) ?? throw NotFound();
            Members.Remove(existing);
            HiddenMembers.Add(existing);
            return 0;
        });

    public Task<MemberDto> UnhideMemberAsync(int memberId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            BeltCalls.Add($"unhide-member:{memberId}");
            var existing = HiddenMembers.FirstOrDefault(m => m.Id == memberId) ?? throw NotFound();
            HiddenMembers.Remove(existing);
            Members.Add(existing);
            return WithComments(existing);
        });

    // --- Comments and tags: a server that keeps them in lists ----------------------------------------------

    /// <summary>The tags the server holds; <see cref="TagDto.CommentCount"/> is worked out from <see cref="Comments"/>.</summary>
    public List<TagDto> Tags { get; } = [];

    /// <summary>The comments of every member, in the order they were written.</summary>
    public List<CommentDto> Comments { get; } = [];

    /// <summary>Every call that changed comments or tags, e.g. <c>create-comment:7</c> (by member id), <c>update-comment:3</c>, <c>delete-comment:3</c>, <c>create-tag:Exam</c>, <c>update-tag:2</c>, <c>delete-tag:2</c>.</summary>
    public List<string> CommentCalls { get; } = [];

    public List<CommentInputDto> CommentInputs { get; } = [];

    public List<CommentUpdateDto> CommentUpdates { get; } = [];

    public List<TagInputDto> TagInputs { get; } = [];

    public List<int> CommentListRequests { get; } = [];

    public int TagListRequests { get; private set; }

    /// <summary>When set, only the list of a member's comments fails.</summary>
    public Exception? CommentListFailure { get; set; }

    /// <summary>When set, only the list of tags fails.</summary>
    public Exception? TagListFailure { get; set; }

    private int _tick;

    /// <summary>Each comment and each edit comes a minute after the one before: the clock of the server.</summary>
    private DateTimeOffset NextMoment() => Now.AddMinutes(_tick++);

    /// <summary>Sets the scene: a tag, as if it had been created before. <paramref name="eventId"/> is an event of <see cref="SampleEvents"/>.</summary>
    public TagDto AddTag(string name, string? eventId = null)
    {
        var tag = new TagDto(_nextId++, name, eventId is null ? null : EventOfTag(eventId), 0);
        Tags.Add(tag);
        return tag;
    }

    /// <summary>
    /// Sets the scene: a comment as if somebody had written it before. The author is a user who needs not be in <see cref="Users"/>; a
    /// reply names its parent by id.
    /// </summary>
    public CommentDto AddComment(
        int memberId, string body, int authorId = 1, string authorName = "anna", int? parentId = null, params TagDto[] tags)
    {
        var comment = new CommentDto(
            _nextId++, memberId, parentId, body, authorId, authorName, NextMoment(),
            [.. tags.Select(t => new CommentTagDto(t.Id, t.Name, t.Event))], []);
        Comments.Add(comment);
        return comment;
    }

    private static TagEventDto EventOfTag(string eventId)
    {
        var e = SampleEvents.FirstOrDefault(e => e.Id == eventId) ?? throw NotFound();
        return new TagEventDto("g1", e.Id, e.Name, e.Start);
    }

    private TagDto WithCount(TagDto tag) => tag with { CommentCount = Comments.Count(c => c.Tags.Any(t => t.Id == tag.Id)) };

    /// <summary>The evaluations <see cref="GetMemberRatingsAsync"/> answers with.</summary>
    public List<RatingDto> MemberRatings { get; } = [];

    public Task<IReadOnlyList<RatingDto>> GetMemberRatingsAsync(int memberId, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<RatingDto>>(() =>
        {
            if (!MayRead(memberId, Permission.RatingsView, Permission.RatingsViewOwn)) throw Forbidden();
            return [.. MemberRatings.Where(r => r.MemberId == memberId)];
        });

    /// <summary>The permission that applies to what is written about a member: the "own" one when the signed-in user is that member.</summary>
    private bool MayRead(int memberId, string view, string viewOwn) =>
        Account.Can(MemberLinks.TryGetValue(memberId, out var user) && user == Account.Id ? viewOwn : view);

    public Task<IReadOnlyList<MemberChoiceDto>> GetOwnMembersAsync(CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<MemberChoiceDto>>(() =>
            [.. Members.Where(m => MemberLinks.TryGetValue(m.Id, out var user) && user == Account.Id).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => new MemberChoiceDto(m.Id, m.GroupId, m.Name, Account.Id))]);

    public Task<IReadOnlyList<CommentDto>> GetCommentsAsync(int memberId, CancellationToken cancellationToken = default)
    {
        CommentListRequests.Add(memberId);
        return CommentListFailure is { } failure
            ? Task.FromException<IReadOnlyList<CommentDto>>(failure)
            : Run<IReadOnlyList<CommentDto>>(() =>
            {
                if (Members.All(m => m.Id != memberId)) throw NotFound();
                if (!MayRead(memberId, Permission.CommentsView, Permission.CommentsViewOwn)) throw Forbidden();
                return Comments.Where(c => c.MemberId == memberId).OrderBy(c => c.Id).ToList();
            });
    }

    public Task<CommentDto> CreateCommentAsync(int memberId, CommentInputDto comment, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            CommentCalls.Add($"create-comment:{memberId}");
            CommentInputs.Add(comment);
            if (Members.All(m => m.Id != memberId)) throw NotFound();
            if (comment.ParentId is { } parentId && Comments.All(c => c.Id != parentId || c.MemberId != memberId))
                throw BadRequest($"There is no comment with id {parentId} on this member to reply to.");

            var created = new CommentDto(
                _nextId++, memberId, comment.ParentId, CommentFormat.Clean(comment.Body), Account.Id, Account.UserName, NextMoment(),
                TagsOf(comment.TagIds), []);
            Comments.Add(created);
            return created;
        });

    // --- Calendar ---------------------------------------------------------------------------------------------

    /// <summary>The events the server holds; the calendar answers with those that start within the asked days.</summary>
    public List<CalendarEventDto> CalendarEvents { get; } = [];

    /// <summary>Who is invited to which event (by event id); an event without an entry has nobody.</summary>
    public Dictionary<string, List<CalendarAttendeeDto>> Attendees { get; } = [];

    /// <summary>Today as the server says it.</summary>
    public DateOnly CalendarToday { get; set; } = new(2026, 10, 13);

    /// <summary>The events going on now.</summary>
    public List<CalendarEventDto> CurrentEvents { get; } = [];

    /// <summary>Every <c>calendar:view:date</c> and <c>current:group</c> asked for, in order.</summary>
    public List<string> CalendarCalls { get; } = [];

    public List<(string EventId, int MemberId, string Body)> EventComments { get; } = [];

    public Task<CalendarDto> GetCalendarAsync(string groupId, string view, DateOnly? date = null, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            var day = date ?? CalendarToday;
            CalendarCalls.Add($"calendar:{view}:{day:yyyy-MM-dd}");
            var (from, to) = CalendarViews.Range(view, day);
            return new CalendarDto(view, day, from, to, CalendarToday,
                [.. CalendarEvents.Where(e => e.GroupId == groupId && DateOnly.FromDateTime(e.Start.Date) >= from && DateOnly.FromDateTime(e.Start.Date) <= to).OrderBy(e => e.Start)]);
        });

    public Task<IReadOnlyList<CalendarEventDto>> GetCurrentEventsAsync(string groupId, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<CalendarEventDto>>(() =>
        {
            CalendarCalls.Add($"current:{groupId}");
            return [.. CurrentEvents.Where(e => e.GroupId == groupId)];
        });

    public Task<CalendarEventDetailDto> GetCalendarEventAsync(string groupId, string eventId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            CalendarCalls.Add($"event:{eventId}");
            var e = CalendarEvents.FirstOrDefault(e => e.GroupId == groupId && e.Id == eventId) ?? throw NotFound();
            return new CalendarEventDetailDto(e, Attendees.GetValueOrDefault(eventId) ?? []);
        });

    public Task<CommentDto> CreateEventCommentAsync(string groupId, string eventId, int memberId, CalendarCommentInputDto comment, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            EventComments.Add((eventId, memberId, comment.Body));
            var attendees = Attendees.GetValueOrDefault(eventId) ?? throw NotFound();
            var index = attendees.FindIndex(a => a.MemberId == memberId);
            if (index < 0) throw NotFound();

            var tag = new CommentTagDto(1000, $"Event {eventId}", null);
            if (comment.ParentId is { } parent && attendees[index].Comments.All(c => c.Id != parent)) throw BadRequest("No such comment to reply to.");
            var created = new CommentDto(_nextId++, memberId, comment.ParentId, CommentFormat.Clean(comment.Body), Account.Id, Account.UserName, NextMoment(), [tag], []);
            attendees[index] = attendees[index] with { Comments = [.. attendees[index].Comments, created] };
            return created;
        });

    /// <summary>Every <c>rate:event:member:stars</c> and <c>unrate:event:member</c> asked for, in order.</summary>
    public List<string> RatingCalls { get; } = [];

    public Task<RatingDto> RateMemberAsync(string groupId, string eventId, int memberId, int stars, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            RatingCalls.Add($"rate:{eventId}:{memberId}:{stars}");
            if (!RatingRules.IsStars(stars)) throw BadRequest("stars");
            var attendees = Attendees.GetValueOrDefault(eventId) ?? throw NotFound();
            var index = attendees.FindIndex(a => a.MemberId == memberId);
            if (index < 0) throw NotFound();

            var rating = new RatingDto(memberId, eventId, Account.Id, Account.UserName, stars, NextMoment());
            attendees[index] = attendees[index] with { Ratings = [.. (attendees[index].Ratings ?? []).Where(r => r.UserId != Account.Id), rating] };
            return rating;
        });

    public Task UnrateMemberAsync(string groupId, string eventId, int memberId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            RatingCalls.Add($"unrate:{eventId}:{memberId}");
            var attendees = Attendees.GetValueOrDefault(eventId) ?? throw NotFound();
            var index = attendees.FindIndex(a => a.MemberId == memberId);
            if (index < 0) throw NotFound();

            attendees[index] = attendees[index] with { Ratings = [.. (attendees[index].Ratings ?? []).Where(r => r.UserId != Account.Id)] };
            return 0;
        });

    public Task<CommentDto> UpdateCommentAsync(int commentId, CommentUpdateDto comment, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            CommentCalls.Add($"update-comment:{commentId}");
            CommentUpdates.Add(comment);
            var existing = Comments.FirstOrDefault(c => c.Id == commentId) ?? throw NotFound();

            var body = CommentFormat.Clean(comment.Body);
            var tags = TagsOf(comment.TagIds);
            var added = tags.Where(t => existing.Tags.All(h => h.Id != t.Id)).Select(t => t.Name).Order().ToList();
            var removed = existing.Tags.Where(h => tags.All(t => t.Id != h.Id)).Select(t => t.Name).Order().ToList();
            var (textChanged, formattingChanged) = CommentFormat.Compare(existing.Body, body);
            if (!textChanged && !formattingChanged && added.Count + removed.Count == 0) return existing;

            var edit = new CommentEditDto(
                _nextId++, Account.Id, Account.UserName, NextMoment(), textChanged, formattingChanged, added, removed, existing.Body);
            var updated = existing with { Body = body, Tags = tags, Edits = [.. existing.Edits, edit] };
            Comments[Comments.IndexOf(existing)] = updated;
            return updated;
        });

    public Task DeleteCommentAsync(int commentId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            CommentCalls.Add($"delete-comment:{commentId}");
            var existing = Comments.FirstOrDefault(c => c.Id == commentId) ?? throw NotFound();
            if (!(existing.AuthorId == Account.Id ? Account.Can(Permission.CommentsDelete) || Account.Can(Permission.CommentsDeleteAny) : Account.Can(Permission.CommentsDeleteAny)))
                throw new PlannerApiException("Not allowed to delete this comment.", HttpStatusCode.Forbidden);

            // The replies go with it, and theirs.
            var gone = new HashSet<int> { commentId };
            while (Comments.Where(c => c.ParentId is { } p && gone.Contains(p) && !gone.Contains(c.Id)).Select(c => c.Id).ToList() is { Count: > 0 } more)
                gone.UnionWith(more);
            Comments.RemoveAll(c => gone.Contains(c.Id));
            return 0;
        });

    private List<CommentTagDto> TagsOf(IReadOnlyList<int> tagIds) =>
        [.. tagIds.Distinct().Select(id => Tags.FirstOrDefault(t => t.Id == id) ?? throw BadRequest($"There is no tag with id {id}."))
            .Select(t => new CommentTagDto(t.Id, t.Name, t.Event))];

    public Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        TagListRequests++;
        return TagListFailure is { } failure
            ? Task.FromException<IReadOnlyList<TagDto>>(failure)
            : Run<IReadOnlyList<TagDto>>(() => Tags.Select(WithCount).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task<TagDto> CreateTagAsync(TagInputDto tag, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            CommentCalls.Add($"create-tag:{tag.Name}");
            TagInputs.Add(tag);
            if (Tags.Any(t => Same(t.Name, tag.Name))) throw Conflict($"There already is a tag named '{tag.Name}'.");

            var created = new TagDto(_nextId++, tag.Name.Trim(), tag.EventId is null ? null : EventOfTag(tag.EventId), 0);
            Tags.Add(created);
            return created;
        });

    public Task<TagDto> UpdateTagAsync(int tagId, TagInputDto tag, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            CommentCalls.Add($"update-tag:{tagId}");
            TagInputs.Add(tag);
            var existing = Tags.FirstOrDefault(t => t.Id == tagId) ?? throw NotFound();
            if (Tags.Any(t => t.Id != tagId && Same(t.Name, tag.Name))) throw Conflict($"There already is a tag named '{tag.Name}'.");

            var updated = existing with { Name = tag.Name.Trim(), Event = tag.EventId is null ? null : EventOfTag(tag.EventId) };
            Tags[Tags.IndexOf(existing)] = updated;

            // The comments that carry it show the new name and event.
            for (var i = 0; i < Comments.Count; i++)
            {
                if (Comments[i].Tags.Any(t => t.Id == tagId))
                    Comments[i] = Comments[i] with { Tags = [.. Comments[i].Tags.Select(t => t.Id == tagId ? new CommentTagDto(tagId, updated.Name, updated.Event) : t)] };
            }

            return WithCount(updated);
        });

    public Task DeleteTagAsync(int tagId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            CommentCalls.Add($"delete-tag:{tagId}");
            Tags.Remove(Tags.FirstOrDefault(t => t.Id == tagId) ?? throw NotFound());
            for (var i = 0; i < Comments.Count; i++)
                Comments[i] = Comments[i] with { Tags = [.. Comments[i].Tags.Where(t => t.Id != tagId)] };
            return 0;
        });

    /// <summary>The calendar feeds the server holds.</summary>
    public List<CalendarFeedDto> CalendarFeeds { get; } = [];

    /// <summary>Every call that changed feeds, e.g. <c>create-feed:Kids</c>, <c>update-feed:3</c>, <c>regenerate-feed:3</c>, <c>delete-feed:3</c>.</summary>
    public List<string> FeedCalls { get; } = [];

    public List<CalendarFeedInputDto> FeedInputs { get; } = [];

    public Task<IReadOnlyList<CalendarFeedDto>> GetCalendarFeedsAsync(CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<CalendarFeedDto>>(() => [.. CalendarFeeds]);

    public Task<CalendarFeedDto> CreateCalendarFeedAsync(CalendarFeedInputDto feed, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            FeedCalls.Add($"create-feed:{feed.Name}");
            FeedInputs.Add(feed);
            var id = _nextId++;
            var created = new CalendarFeedDto(id, feed.Name, $"token{id}", $"https://x/calendar-feeds/token{id}.ics", feed.Sources, Now);
            CalendarFeeds.Add(created);
            return created;
        });

    public Task<CalendarFeedDto> UpdateCalendarFeedAsync(int feedId, CalendarFeedInputDto feed, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            FeedCalls.Add($"update-feed:{feedId}");
            FeedInputs.Add(feed);
            var index = CalendarFeeds.FindIndex(f => f.Id == feedId);
            if (index < 0) throw NotFound();
            return CalendarFeeds[index] = CalendarFeeds[index] with { Name = feed.Name, Sources = feed.Sources };
        });

    public Task<CalendarFeedDto> RegenerateCalendarFeedAsync(int feedId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            FeedCalls.Add($"regenerate-feed:{feedId}");
            var index = CalendarFeeds.FindIndex(f => f.Id == feedId);
            if (index < 0) throw NotFound();
            var token = $"new{_nextId++}";
            return CalendarFeeds[index] = CalendarFeeds[index] with { Token = token, Url = $"https://x/calendar-feeds/{token}.ics" };
        });

    public Task DeleteCalendarFeedAsync(int feedId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            FeedCalls.Add($"delete-feed:{feedId}");
            CalendarFeeds.Remove(CalendarFeeds.FirstOrDefault(f => f.Id == feedId) ?? throw NotFound());
            return 0;
        });

    /// <summary>The groups, members and events kept by hand (data source "manual").</summary>
    public List<ManualGroupDto> ManualGroups { get; } = [];

    public List<ManualMemberDto> ManualMembers { get; } = [];

    public List<ManualEventDto> ManualEvents { get; } = [];

    /// <summary>Every call that changed the manual data, e.g. <c>create-group:Dojo</c>, <c>update-member:m1</c>, <c>delete-event:e1</c>.</summary>
    public List<string> ManualCalls { get; } = [];

    public ManualEventInputDto? LastManualEventInput { get; private set; }

    public ManualMemberInputDto? LastManualMemberInput { get; private set; }

    public Task<IReadOnlyList<ManualGroupDto>> GetManualGroupsAsync(CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<ManualGroupDto>>(() => [.. ManualGroups.Select(g => g with { MemberCount = ManualMembers.Count(m => m.GroupId == g.Id) })]);

    public Task<ManualGroupDto> CreateManualGroupAsync(ManualNameInputDto group, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ManualCalls.Add($"create-group:{group.Name}");
            var created = new ManualGroupDto($"g{_nextId++}", group.Name, [], 0);
            ManualGroups.Add(created);
            return created;
        });

    public Task<ManualGroupDto> RenameManualGroupAsync(string groupId, ManualNameInputDto group, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ManualCalls.Add($"rename-group:{groupId}:{group.Name}");
            var index = ManualGroups.FindIndex(g => g.Id == groupId);
            if (index < 0) throw NotFound();
            return ManualGroups[index] = ManualGroups[index] with { Name = group.Name };
        });

    public Task DeleteManualGroupAsync(string groupId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ManualCalls.Add($"delete-group:{groupId}");
            ManualGroups.Remove(ManualGroups.FirstOrDefault(g => g.Id == groupId) ?? throw NotFound());
            ManualMembers.RemoveAll(m => m.GroupId == groupId);
            ManualEvents.RemoveAll(e => e.GroupId == groupId);
            return 0;
        });

    public Task<SubGroupDto> CreateManualSubGroupAsync(string groupId, ManualNameInputDto subGroup, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ManualCalls.Add($"create-subgroup:{subGroup.Name}");
            var index = ManualGroups.FindIndex(g => g.Id == groupId);
            if (index < 0) throw NotFound();
            var created = new SubGroupDto($"s{_nextId++}", subGroup.Name);
            ManualGroups[index] = ManualGroups[index] with { SubGroups = [.. ManualGroups[index].SubGroups, created] };
            return created;
        });

    public Task<SubGroupDto> RenameManualSubGroupAsync(string subGroupId, ManualNameInputDto subGroup, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ManualCalls.Add($"rename-subgroup:{subGroupId}:{subGroup.Name}");
            var index = ManualGroups.FindIndex(g => g.SubGroups.Any(s => s.Id == subGroupId));
            if (index < 0) throw NotFound();
            var renamed = new SubGroupDto(subGroupId, subGroup.Name);
            ManualGroups[index] = ManualGroups[index] with { SubGroups = [.. ManualGroups[index].SubGroups.Select(s => s.Id == subGroupId ? renamed : s)] };
            return renamed;
        });

    public Task DeleteManualSubGroupAsync(string subGroupId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ManualCalls.Add($"delete-subgroup:{subGroupId}");
            var index = ManualGroups.FindIndex(g => g.SubGroups.Any(s => s.Id == subGroupId));
            if (index < 0) throw NotFound();
            ManualGroups[index] = ManualGroups[index] with { SubGroups = [.. ManualGroups[index].SubGroups.Where(s => s.Id != subGroupId)] };
            return 0;
        });

    public Task<IReadOnlyList<ManualMemberDto>> GetManualMembersAsync(string groupId, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<ManualMemberDto>>(() => [.. ManualMembers.Where(m => m.GroupId == groupId)]);

    public Task<ManualMemberDto> CreateManualMemberAsync(string groupId, ManualMemberInputDto member, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ManualCalls.Add($"create-member:{member.Name}");
            LastManualMemberInput = member;
            var created = new ManualMemberDto($"m{_nextId++}", groupId, member.Name, member.BirthDate, member.SubGroupIds ?? []);
            ManualMembers.Add(created);
            return created;
        });

    public Task<ManualMemberDto> UpdateManualMemberAsync(string memberId, ManualMemberInputDto member, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ManualCalls.Add($"update-member:{memberId}");
            LastManualMemberInput = member;
            var index = ManualMembers.FindIndex(m => m.Id == memberId);
            if (index < 0) throw NotFound();
            return ManualMembers[index] = ManualMembers[index] with { Name = member.Name, BirthDate = member.BirthDate, SubGroupIds = member.SubGroupIds ?? [] };
        });

    public Task DeleteManualMemberAsync(string memberId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ManualCalls.Add($"delete-member:{memberId}");
            ManualMembers.Remove(ManualMembers.FirstOrDefault(m => m.Id == memberId) ?? throw NotFound());
            return 0;
        });

    public Task<IReadOnlyList<ManualEventDto>> GetManualEventsAsync(string groupId, CancellationToken cancellationToken = default) =>
        Run<IReadOnlyList<ManualEventDto>>(() => [.. ManualEvents.Where(e => e.GroupId == groupId).OrderByDescending(e => e.Start)]);

    public Task<ManualEventDto> CreateManualEventAsync(string groupId, ManualEventInputDto manualEvent, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ManualCalls.Add($"create-event:{manualEvent.Name}");
            LastManualEventInput = manualEvent;
            var created = new ManualEventDto($"e{_nextId++}", groupId, manualEvent.Name, manualEvent.Start, manualEvent.End, manualEvent.Cancelled, manualEvent.Invitations ?? [], manualEvent.TrainerIds ?? []);
            ManualEvents.Add(created);
            return created;
        });

    public Task<ManualEventDto> UpdateManualEventAsync(string eventId, ManualEventInputDto manualEvent, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ManualCalls.Add($"update-event:{eventId}");
            LastManualEventInput = manualEvent;
            var index = ManualEvents.FindIndex(e => e.Id == eventId);
            if (index < 0) throw NotFound();
            return ManualEvents[index] = ManualEvents[index] with
            {
                Name = manualEvent.Name, Start = manualEvent.Start, End = manualEvent.End, Cancelled = manualEvent.Cancelled, Invitations = manualEvent.Invitations ?? [], TrainerIds = manualEvent.TrainerIds ?? [],
            };
        });

    public Task DeleteManualEventAsync(string eventId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            ManualCalls.Add($"delete-event:{eventId}");
            ManualEvents.Remove(ManualEvents.FirstOrDefault(e => e.Id == eventId) ?? throw NotFound());
            return 0;
        });

    private Task<T> Run<T>(Func<T> action)
    {
        if (FailWith is { } ex) return Task.FromException<T>(ex);

        try
        {
            return Task.FromResult(action());
        }
        catch (PlannerApiException failure)
        {
            return Task.FromException<T>(failure);
        }
    }

    // --- Setup and settings ----------------------------------------------------------------------------------

    public static readonly SettingsDto DefaultSettings = new(
        "data/kyuplan.db", "data/accounts.db", false, null, null, false, "Europe/Berlin", "Prüfung", 60, 3, true, 30);

    /// <summary>What the server says of whether it needs the setup; the fake never needs it unless a test says so.</summary>
    public bool NeedsSetup { get; set; }

    public SettingsDto Settings { get; set; } = DefaultSettings;

    public List<SetupInputDto> SetupCalls { get; } = [];

    public List<SettingsInputDto> SettingsSaves { get; } = [];

    /// <summary>What saving answers: whether the server restarts.</summary>
    public bool SaveRestarts { get; set; } = true;

    /// <summary>How many of the next questions whether the API needs a setup fail before it answers: a server that restarts.</summary>
    public int SetupQuestionsFailing { get; set; }

    public int SetupQuestions { get; private set; }

    public Task<SetupStatusDto> GetSetupAsync(CancellationToken cancellationToken = default)
    {
        SetupQuestions++;
        if (SetupQuestionsFailing > 0)
        {
            SetupQuestionsFailing--;
            return Task.FromException<SetupStatusDto>(new PlannerApiException("Not reachable.", null));
        }

        return Run(() => new SetupStatusDto(NeedsSetup, NeedsSetup ? Settings : null));
    }

    public Task<SettingsSavedDto> SetupAsync(SetupInputDto setup, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            SetupCalls.Add(setup);
            if (!NeedsSetup) throw Conflict("The API is set up already.");

            NeedsSetup = false;
            return new SettingsSavedDto(SaveRestarts);
        });

    public Task<SettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        Run(() => Account.Can(Permission.SettingsView) ? Settings : throw Forbidden());

    public Task<SettingsSavedDto> SaveSettingsAsync(SettingsInputDto settings, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            SettingsSaves.Add(settings);
            if (!Account.Can(Permission.SettingsEdit)) throw Forbidden();

            Settings = Settings with
            {
                DatabasePath = settings.DatabasePath, AccountsDatabasePath = settings.AccountsDatabasePath, UseDemoData = settings.UseDemoData,
                SpondEmail = settings.SpondEmail, SpondPhoneNumber = settings.SpondPhoneNumber,
                HasSpondPassword = Settings.HasSpondPassword || !string.IsNullOrEmpty(settings.SpondPassword),
                TimeZone = settings.TimeZone, ExamKeyword = settings.ExamKeyword,
                DefaultMinAttendancePercent = settings.DefaultMinAttendancePercent, DefaultMinInvitedEvents = settings.DefaultMinInvitedEvents,
                SyncEnabled = settings.SyncEnabled, SyncIntervalMinutes = settings.SyncIntervalMinutes,
                DefaultEventStars = settings.DefaultEventStars ?? Settings.DefaultEventStars,
                ExamMinAverageStars = settings.ExamMinAverageStars ?? Settings.ExamMinAverageStars,
                UnregisteredSubGroup = settings.UnregisteredSubGroup ?? Settings.UnregisteredSubGroup,
            };
            return new SettingsSavedDto(SaveRestarts);
        });

    private ExamDto Find(int examId) => Exams.FirstOrDefault(e => e.Id == examId) ?? throw NotFound();

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static PlannerApiException NotFound() => new("Not found.", HttpStatusCode.NotFound);

    private static PlannerApiException Forbidden() => new("Not allowed.", HttpStatusCode.Forbidden);

    private static PlannerApiException Conflict(string message) => new(message, HttpStatusCode.Conflict);

    private static PlannerApiException BadRequest(string message) => new(message, HttpStatusCode.BadRequest);

    /// <summary>Two exams around the test clock's "now" (30 June 2026) and two ordinary trainings.</summary>
    public static readonly IReadOnlyList<EventChoiceDto> SampleEvents =
    [
        new("exam-1", "Gürtelprüfung", At(3, 28, 10)),
        new("train-1", "Training", At(6, 20)),
        new("train-2", "Training", At(7, 2)),
        new("exam-2", "Gürtelprüfung", At(9, 26, 10)),
    ];

    public static EventChoicesDto SampleEventChoices() => new(SampleEvents, "exam-1", "exam-2");

    /// <summary>What the server would answer: the query with its time span resolved to days.</summary>
    public static AttendanceFilter Resolve(AttendanceQuery query)
    {
        return new AttendanceFilter(
            query.GroupId, Day(query.From, query.FromEventId), Day(query.To, query.ToEventId), query.SubGroupIds,
            query.MinAttendancePercent, query.MinInvitedEvents, query.EventNameContains, query.EventNameExcludes);

        static DateOnly Day(DateOnly? date, string? eventId) =>
            date ?? DateOnly.FromDateTime(SampleEvents.Single(e => e.Id == eventId).Start.Date);
    }

    public static AttendanceStatisticsDto Statistics(AttendanceQuery query, params MemberAttendanceDto[] members)
    {
        if (members.Length == 0)
        {
            members =
            [
                MemberStats("Anna", invited: 10, attended: 8),
                MemberStats("Ben", invited: 10, attended: 5, status: QualificationStatus.BelowThreshold, level: EngagementLevel.Irregular, score: 50),
                MemberStats("Clara", invited: 2, attended: 2, status: QualificationStatus.InsufficientData, score: 90),
                MemberStats("Dora", invited: 10, attended: 6, status: QualificationStatus.BelowThreshold, score: 62),
            ];
        }

        var summary = new AttendanceSummaryDto(
            EventCount: 12, MemberCount: members.Length,
            QualifiedCount: members.Count(m => m.Status == QualificationStatus.Qualified),
            BelowThresholdCount: members.Count(m => m.Status == QualificationStatus.BelowThreshold),
            InsufficientDataCount: members.Count(m => m.Status == QualificationStatus.InsufficientData),
            AtRiskCount: 0, OverallAttendanceRate: 65.4, AverageResponseRate: 91, AverageAttendeesPerEvent: 6.5);

        var buckets = Enumerable.Range(0, 10).Select(i => new DistributionBucketDto(i * 10, i == 9 ? 100 : i * 10 + 10, i == 5 ? 2 : 1)).ToList();
        var months = new[] { new MonthlyTrendPointDto(new DateOnly(2026, 5, 1), 8, 6.0, 60), new MonthlyTrendPointDto(new DateOnly(2026, 6, 1), 4, 7.5, 75) };
        var events = new[] { new EventAttendanceDto("e1", "Training", At(6, 20), 10, 7, 2, 1, 70) };

        return new AttendanceStatisticsDto(Resolve(query), "Dojo", Now, summary, members, events, buckets, months);
    }

    public static DashboardDto SampleDashboard() => new(
        "g1", "Dojo", new DateOnly(2026, 4, 2), new DateOnly(2026, 6, 30), 75,
        new AttendanceSummaryDto(20, 10, 4, 5, 1, 2, 66.6, 90, 6.4),
        [new UpcomingEventDto("u1", "Training", At(7, 2), 10, 6, 2, 2)],
        [MemberStats("Anna")],
        [MemberStats("Ben", level: EngagementLevel.AtRisk, score: 20)],
        [new MonthlyTrendPointDto(new DateOnly(2026, 6, 1), 4, 7.5, 75)]);
}
