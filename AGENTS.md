# AGENTS.md

kyuplan: karate-club exam planner on Spond data. .NET 10 (SDK pinned in `global.json`), C# latest, nullable on.
Read this instead of exploring. Open `README.md` only for the section you need (`grep -n '^#' README.md`).

## Layout (`src/`)
- `Contracts`: wire DTOs and shared rules (`*Rules.cs`). Change here first when the API shape changes.
- `Api`: ASP.NET minimal API. `Endpoints/` routes, `Persistence/` services + EF (SQLite), `Accounts/` users, `Spond/` Spond access, `Statistics/` engine, `Domain/`.
- `Client.Shared`: typed API client, **all view models**, **all UI texts** (`Resources/Strings.resx` + `Strings.de.resx`). No UI-framework code.
- `Client.Blazor` (WASM) and `Client.Wpf` (Windows only): markup only; bind to the shared view models.
- `website/`: Docusaurus, one site per audience (user/admin/deployment, developer) built from `docs/*.md` (`scripts/audiences.mjs`: which file is a page of which site). The API serves the first three under `/docs/<audience>/`
  with a permission check per audience (`Contracts/Documentation.cs`, `Endpoints/DocumentationEndpoints.cs`); clients show `ShellViewModel.HelpLinks`. A new doc file = an entry in `audiences.mjs`. Node is not needed for `dotnet test`; the Dockerfile's `docs` stage and the release's `docs` job build it.
- `tests/Kyuplan.Tests`: single xUnit project.

Logic goes in `Client.Shared` or `Api`, never duplicated in the UIs.

## Commands
- Build/test (Linux-safe, skips WPF): `dotnet test tests/Kyuplan.Tests`. Narrow with `--filter "FullyQualifiedName~Name"`.
- `Client.Wpf` and the full `.sln` build only on Windows; on Linux skip them.
- A running Api locks its DLLs (MSB3021): build/test with `--artifacts-path <other dir>` or stop it first.
- Run API in demo mode (no Spond credentials): `dotnet run --project src/Kyuplan.Api --launch-profile http -- --Spond:UseDemoData=true` (port 5100, docs at `/scalar`). The launch profile alone does NOT enable demo data.
- Every API call needs HTTP Basic login; a new API has no user: the Blazor client then shows a setup screen (settings + first admin, `Endpoints/SettingsEndpoints.cs`), or set `Accounts:InitialAdminPassword`. Settings live in `data/settings.json` (read on top of appsettings, saving restarts the app inside `Program.cs`'s loop); tests set `Setup:SettingsFile` to a missing file. Authorization is permission/role based (see `Accounts/PermissionAuthorization.cs`), there is no admin flag.
- Before `dotnet ef`: `dotnet tool restore` (tool pinned in `.config/dotnet-tools.json`) and rebuild the Api project.

## Rules that bite
- **Texts**: add each new key to BOTH `Strings.resx` and `Strings.de.resx` with identical `{0}` placeholders (`ResourceFileTests` enforces it). The `Strings` class is generated at build time. Razor `@Strings.X`, XAML `{loc:Loc X}`. No hard-coded UI strings.
- **DB schema**: edit `Persistence/Entities.cs` / `PlannerDbContext.cs`, then add a migration (applied on API start):
  `dotnet ef migrations add <Name> --context PlannerDbContext --project src/Kyuplan.Api --output-dir Persistence/Migrations`
  Accounts DB: `--context AccountsDbContext --output-dir Accounts/Migrations`. Never hand-edit existing migrations.
  **Every schema change needs the PostgreSQL twin** (`PostgresMigrationTests` fails otherwise): same command with `--context PlannerPostgresDbContext --output-dir Persistence/PostgresMigrations` (accounts: `AccountsPostgresDbContext`, `Accounts/PostgresMigrations`), build first, and pass the provider to the app host: `-- --Database:Provider=PostgreSql --Database:ConnectionString="Host=x;Database=x" --Accounts:ConnectionString="Host=x;Database=x"` (no server is contacted). Provider differences of the model live in `Infrastructure/PostgresModel.cs` and `PlannerDbContext` (`IsColor`); catch unique/foreign-key violations with `DbUpdateException.IsUniqueViolation()/IsForeignKeyViolation()`, never with `SqliteException`.
- **Themes**: themes the clients draw themselves are `ThemeIds`; everything else is a **theme pack** (JSON of 12 colours, `Contracts/ThemePacks.cs`, built-ins in `BuiltInThemePacks`, uploads as files in `Themes:Directory` via `ThemePackStore`). New palettes/accessibility themes = a pack, not client code. Both clients derive everything from the 12 colours (`js/theme.js`, WPF `ThemePackColors`); a built-in pack must pass `ThemePackRules.Check` (`ThemePackRulesTests`).
- **Database/deployment**: `Database:Provider` (`Sqlite` default | `PostgreSql`) applies to both databases; contexts are chosen in `Infrastructure/DatabaseRegistration.cs`. One image for every Docker host (`src/Kyuplan.Api/Dockerfile`, amd64+arm64, `GET /healthz`), Compose files in `deploy/` (generic, PostgreSQL, TrueNAS); all options and extra steps are in `docs/deployment.md`: update it with any change there.
- **Releases**: one tag `v<version>` releases everything (`.github/workflows/release.yml`, see `docs/deployment.md`): the desktop installers (one per platform, `Kyuplan-<platform>-<version>.<ext>`, ids in `ClientPlatform`) are built first and baked into the one Docker image (`downloads/`, Dockerfile build context `installer`); the API offers them (`GET /api/client/<platform>`, `ServerInfoDto.ClientVersions`), the Blazor sidebar shows a download button for the visitor's system, and each desktop client updates itself via `ClientUpdateViewModel` + its `IClientUpdater`. A client for a new OS plugs in there (see `docs/deployment.md`), nothing platform-specific in the API/web client.
- **Backups** (`Api/Backup/`, `Endpoints/BackupEndpoints.cs`): a `.kyubackup` zip holds both databases as JSON rows per table (`DatabaseCopier` works on the EF model, so SQLite and PostgreSQL are interchangeable; a table with a shadow property makes `DatabaseCopier.Tables` throw, `BackupApiTests` catches it). Mirror databases (`BackupTargetStore`, JSON file, not in the DB) are refreshed by `BackupWorker`; a restore restarts the app. A new table needs nothing extra; a new schema needs the usual migrations. Page: `BackupViewModel` + `Backup.razor` + `BackupView.xaml`; options in `docs/admin-guide.md#backups`.
- Package versions live in `Directory.Packages.props` (central); csproj files carry no versions.
- **Exam prices/payments**: `ExamPrices` (general fee = row without belt, surcharge per belt) + `ExamPayments` (paid + due snapshot per exam/member), `ExamPaymentService`, `PaymentEndpoints`, `ExamPricesViewModel` (page) and `PaymentRow` in `ExamDetailViewModel`; rules in `docs/exam-planning.md#prices-and-payments`. Amounts are `decimal`, validated by `PaymentRules`.
- Domain rules are documented in `docs/` (`attendance-statistics.md`, `exam-planning.md`, `comments-and-tags.md`). Read the matching file before changing that logic, and update it with the code.
- Tests use in-memory SQLite (`InMemoryDatabase.cs`). Add tests next to the closest existing `*Tests.cs`. Test hosts must set `Sync:Enabled=false` and `DefaultData:Enabled=false`, and must not depend on shipped `appsettings.json` values (that once broke every CI workflow).
- Both UIs must keep working: a change to a view model or contract may need Blazor and WPF edits.

## Token savers
- Don't read `Migrations/*`, `*.Designer.cs`, `wwwroot/`, `assets/`, `installer/`, `deploy/`, `bin/`, `obj/`, `logs/`, `.github/` (read `.github/` and `deploy/` only when changing releases). Irrelevant to almost every task.
- Use `grep`/Glob to locate a symbol, then read only that range (offset/limit). Don't read whole files or the whole README.
- Don't re-run the full test suite after each edit; run the filtered test, then the full suite once at the end.
- Keep final reports to a few lines: what changed, where, test result.

## Git
The repo uses LF line endings; check with `git ls-files --eol` and keep edited files LF.
In a shallow clone (cloud agents), before pushing `git fetch origin <default-branch>` and rebase, so the push stays a thin pack.