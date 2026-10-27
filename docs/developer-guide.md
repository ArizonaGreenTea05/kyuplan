# Developer guide

For people who want to **change or extend kyuplan**: set up the workspace, find your way around the code, and follow the conventions that
keep the two clients, the API and the databases consistent. The project is MIT licensed; contributions are welcome.

- [Overview](#overview)
- [Getting started](#getting-started)
- [Repository map](#repository-map)
- [Architecture](#architecture)
- [The rules that bite](#the-rules-that-bite)
- [Recipes](#recipes)
- [Testing](#testing)
- [Releases and CI](#releases-and-ci)
- [Contribution workflow](#contribution-workflow)
- [Where to read more](#where-to-read-more)

## Overview

kyuplan is a planner for a karate club's belt exams built on the club's Spond groups.

| Layer | Technology |
| --- | --- |
| Runtime / language | **.NET 10** (SDK pinned in `global.json`), C# `latest`, nullable reference types on |
| API | ASP.NET Core minimal API, EF Core (**SQLite** by default, **PostgreSQL** optional), Basic authentication, plugins loaded from GitHub releases (Spond is one: the `Spond.API` package is the plugin's, not the API's) |
| Web client | Blazor WebAssembly, hand-written CSS, served by the API itself |
| Desktop client | WPF, four designs (the Fluent theme that ships with .NET, Windows 10, Aero, native; Windows only), no control library, `Svg.Skia` for SVG logos |
| Linux client | GTK 4 + libadwaita through the Gir.Core bindings (`GirCore.Gtk-4.0`, `GirCore.Adw-1`), widgets built in code, no web view; built for Arch Linux (`deploy/arch/PKGBUILD`) |
| Phone client | .NET for Android (`net10.0-android`), native views drawn in code, no UI library; a bottom tab bar over the shared view models |
| Shared client code | typed API client, **all view models** (CommunityToolkit.Mvvm) and **all UI texts** (`.resx`) |
| Tests | xUnit, a single project, in-memory SQLite |
| Packaging | one Docker image (API + web client + the Windows installer), Inno Setup installer, GitHub Actions |

```
Spond ──► Api ──JSON──► Client.Shared (API client + view models) ──► Blazor (WASM)
                                                                 ├──► WPF (Windows)
                                                                 ├──► GTK 4 (Linux)
                                                                 └──► Android (APK)
```

The guiding principle: **logic lives once**, in `Client.Shared` (what the UI does) or `Api` (what the server does). The two UI projects
contain **markup only** and bind to the same view models. If you find yourself writing the same logic in Razor and XAML, move it.

## Getting started

### Prerequisites

- **.NET 10 SDK** (`dotnet --version`; `global.json` pins `10.0.100` and rolls forward to newer feature bands).
- **Windows** if you want to build or run the WPF client or the full solution. On Linux/macOS, test the shared and API projects only.
- Optional: Docker (to build the image), PostgreSQL (to run against it), Inno Setup 6 (to build the installer), Microsoft Edge (for
  `tools/Render-Branding.ps1`).
- Visual Studio 2022+ / Rider / VS Code all work; `Kyuplan.slnLaunch.user` starts the API and the WPF client together in Visual Studio.

### Build and test

```bash
dotnet test tests/Kyuplan.Tests              # builds the dependencies and runs everything; Linux-safe, skips the WPF project
dotnet test tests/Kyuplan.Tests --filter "FullyQualifiedName~ExamSuggestion"   # a narrow run
dotnet build Kyuplan.sln                     # the whole solution, Windows only (includes WPF and, with the Android workload + JDK/SDK, the Android client)
```

### Run it locally (demo mode)

No Spond account is needed: demo mode serves generated data for a fictional club.

```bash
dotnet run --project src/Kyuplan.Api --launch-profile http -- --Spond:UseDemoData=true
```

- Web client and API: <http://localhost:5100/>, API documentation (Scalar): <http://localhost:5100/scalar>.
- The launch profile alone does **not** enable demo data: pass `--Spond:UseDemoData=true`. (In the `Development` environment demo mode may
  also be the default; the explicit flag always works.)
- A new API has no user. The web client shows a **setup screen** (Spond login, settings and first administrator). For a quick start, set the
  initial administrator by configuration instead:

  ```bash
  dotnet user-secrets set "Accounts:InitialAdminPassword" "a-long-dev-password" --project src/Kyuplan.Api
  ```
  The user name is `admin`. Every call needs HTTP Basic credentials.
- Databases and settings land in the `data/` folder of `src/Kyuplan.Api` (`kyuplan.db`, `accounts.db`, `settings.json`; git-ignored).
- The WPF client: `dotnet run --project src/Kyuplan.Client.Wpf` (Windows). It asks for the API address on first start (`http://localhost:5100/`).
- Hot reload for the web client alone: `dotnet run --project src/Kyuplan.Client.Blazor --launch-profile http` serves it on
  <http://localhost:5200>; then set `PlannerApi:BaseAddress` in its `wwwroot/appsettings.json` to `http://localhost:5100/` and
  `Api:AllowedOrigins` of the API to `http://localhost:5200`. Normally just use the API-hosted client.
- **A running API locks its DLLs** (error `MSB3021`). Stop it, or build/test with another output folder:
  `dotnet test tests/Kyuplan.Tests --artifacts-path <other dir>`.

### Real Spond data

```bash
dotnet user-secrets set "Spond:UseDemoData" "false" --project src/Kyuplan.Api
dotnet user-secrets set "Spond:Email" "trainer@example.org" --project src/Kyuplan.Api
dotnet user-secrets set "Spond:Password" "…" --project src/Kyuplan.Api
```

Never commit credentials. Use user secrets or environment variables (`Spond__Password`). Accounts with two-factor authentication do not work.

## Repository map

```
src/
  Kyuplan.Contracts      DTOs (wire format) and shared rules (*Rules.cs); referenced by API and clients
  Kyuplan.Api            The server
    Endpoints/             Route groups: Planner, Exam, Belt, Comment, Account, Branding, Settings, ClientDownload, Documentation
    Persistence/           EF context, Entities.cs, services (Exam, ExamPlanning, Member, Belt, Comment, Tag), SpondSync(+Worker)
    Accounts/              Users, roles, Basic auth handler, permission authorization, login throttle, default roles
    Spond/                 DemoDataSource (the generated sample data; the Spond data source is a plugin)
    Plugins/               Plugin store, installer, GitHub fetcher, host, catalog
    Sources/               The other IDataSource implementations: CustomScriptDataSource (Jint), ManualDataSource (database)
    Statistics/            AttendanceCalculator, ExamSuggestionCalculator, span finder, ...
    Infrastructure/        Middleware (security headers, API key), settings service, restart loop, database registration, branding, logging
    Domain/                Plain records the services work on
    Configuration/         Strongly typed options (Options.cs)
  Kyuplan.Client.Shared  IPlannerApi + PlannerApiClient, ViewModels/, Localization/, Resources/Strings*.resx, Storage/
  Kyuplan.Client.Blazor  Pages/, Components/, Layout/, wwwroot/ (markup only)
  Kyuplan.Client.Wpf     Views/, Controls/, Themes/, Services/, ViewModels/ (shell glue only), Localization/
  Kyuplan.Client.Linux   Pages/ (one per page, `PageRegistry`), Pages/Parts/, Stages/ (address, setup, login, shell), Kit/ (widgets, bindings), Services/
  Kyuplan.Client.Android Screens/ (one per page), Kit/ (widgets, `Screen` base), Services/ (login store, updater), Resources/
tests/Kyuplan.Tests      One xUnit project for everything
tests/linux/             Dockerfile and script that run the Linux client in a virtual X server on Arch Linux and take screenshots
docs/                    This documentation and the domain rules (Markdown, written for GitHub and for the Docusaurus site)
website/                 Docusaurus project: one site per audience built from docs/ (scripts/audiences.mjs), served by the API under /docs
deploy/                  Docker Compose files (generic, PostgreSQL, TrueNAS), .env.example, and arch/ (PKGBUILD, desktop entry of the Linux client)
installer/               Inno Setup script of the WPF client, and the public update key (also built into the Android and Linux clients)
assets/branding/         Placeholder logos and icons
tools/                   Render-Branding.ps1 (program icon from the tab icon)
.github/                 Release workflow and its composite actions
```

Files you will rarely need and that are noisy to read: `Migrations/*`, `*.Designer.cs`, `wwwroot/`, `bin/`, `obj/`, `logs/`.

## Architecture

### Request pipeline (API)

`Program.cs` builds the host inside a loop that **restarts the application in-process** when the Settings page saves something that needs it.
In order: exception handler → security middleware (CSP and headers, host check) → static files (the Blazor client) → CORS → API key
middleware (optional) → authentication (HTTP Basic against the accounts database, with login lockout) → authorization (permission policies) →
endpoints. `GET /healthz` is open; `GET /api/branding*` is open; everything else under `/api` needs a login; unknown `/api/*` is `404`, any
other GET falls back to the Blazor `index.html`.

### Data

Two databases, chosen by `Database:Provider` (`Sqlite` or `PostgreSql`, **both** databases):

- **Planner database** (`PlannerDbContext`): `Exams`, `SavedFilters`, `Belts`, `BeltPrerequisites`, `Members`, `ExamParticipants`
  (invitations, owned by the Spond sync), `ExamCandidates` (members a user saved for an exam), `ExamResults` (belts handed out), `Comments`,
  `CommentEdits`, `Tags`, `CommentTags`, `CustomBranding(+Images)`. Entities are in `Persistence/Entities.cs`.
- **Accounts database** (`AccountsDbContext`): `Users`, `UserAliases`, `Roles`, `UserRoles`. Kept separate because it holds password hashes
  and is protected and backed up on its own.

Both are migrated automatically at start (`Database setup` in `Infrastructure/DatabaseSetup.cs`). A **new** database is filled with default belts
and roles once (`DefaultData:Enabled`). SQLite and PostgreSQL have **separate migration sets** (see below). `Infrastructure/DatabaseRegistration.cs`
picks the contexts; `Infrastructure/PostgresModel.cs` holds the provider differences (for example `citext` for case-insensitive unique names).

### Plugins

`src/Kyuplan.Plugins.Abstractions` is the interface of plugins (`IKyuplanPlugin`, `IPluginBuilder`, the `IDataSource` every data source implements and the model it delivers); `Api/Plugins/` loads them (`PluginStore` keeps the installed ones, `PluginInstaller`/`GitHubPluginFetcher` get them from GitHub, `PluginHost` loads the assemblies in their own load contexts and collects what they register, `PluginCatalogService` tells the clients what they add) and `Endpoints/PluginEndpoints.cs` serves it. A plugin's pages and settings forms are JSON (`PluginUiLayout` in Contracts) that every client draws with the shared `Plugin*ViewModel`s: Blazor `Components/PluginItems.razor`, WPF `Controls/PluginItemsView.xaml`, Linux `Kit/PluginView.cs`. Spond is a plugin in its own repository (`ArizonaGreenTea05/kyuplan.plugins.spond`); `ArizonaGreenTea05/kyuplan.plugins.example` is the template that shows every option (keep it in step when `Plugins.Abstractions`, the manifest or the layout rules gain one). Writing one: [Plugin development](plugin-development.md).

### Spond access and sync

`IDataSource` (in `Kyuplan.Plugins.Abstractions`) hides where groups, members and events come from (the Spond plugin for Spond.API with caching, re-login and optional
backup accounts; `DemoDataSource` for generated data; `CustomScriptDataSource`, the administrator's JavaScript run by Jint; `ManualDataSource`,
rows entered by users in the `Manual*` tables). Which one runs is decided once, in `Program.cs`, from `DataSourceOptions.Resolve`
(`DataSource:Kind`, `Spond:UseDemoData` wins); the settings page changes it and restarts the API. The API **only reads** from the source. `SpondSyncWorker` (a hosted service) runs `SpondSync`
at start and every `Sync:Interval`: members, start days, birth dates and the invitations of exams are kept in line; what users entered (belts,
belt days, comments, saved members, results) is never touched.

### Authentication and permissions

HTTP Basic, checked on every request (`BasicAuthenticationHandler`), no sessions or tokens. The permission catalogue is `Contracts/Permission.cs`.
`PermissionAuthorization.cs` turns every permission into a policy, computed from the user's own permissions plus the permissions of their
roles **per request**. Endpoints require policies; the administrator role has all permissions, always including future ones. A user's roles
and permissions are returned by `GET /api/account` so clients can hide what the user cannot use.

### Domain logic

| Concern | Where | Rules document |
| --- | --- | --- |
| Attendance figures, engagement, trends | `Api/Statistics/AttendanceCalculator.cs` | [attendance-statistics.md](attendance-statistics.md) |
| Who is fit for an exam | `Api/Statistics/ExamSuggestionCalculator.cs`, `Persistence/ExamPlanningService.cs` | [exam-planning.md](exam-planning.md) |
| Comments, tags, markup | `Persistence/CommentService.cs`, `TagService.cs`, `Contracts/CommentFormat.cs` | [comments-and-tags.md](comments-and-tags.md) |
| Validation rules shared with the clients | `Contracts/*Rules.cs` | – |

**Read the matching rules document before changing such logic, and update it in the same change.**

### Clients

- `Kyuplan.Client.Shared` registers the API client and all view models as **singletons** (`AddPlannerClient`), so filters and results survive
  navigation. `IPlannerApi`/`PlannerApiClient` is the single place that knows the HTTP routes. `LoginSession` holds the login (WPF); the browser
  adds its own.
- View models derive from `ViewModelBase`/`PageViewModel`, use CommunityToolkit.Mvvm, expose `Display` helpers for texts and rebuild their texts
  when the language changes (WPF switches live; Blazor reloads).
- **Blazor**: `Pages/*.razor` bind to the view models through `ViewModelComponent`; `PageGate` hides pages the user has no permission for.
  Login is the in-page login screen (`LoginScreen`, `LoginViewModel`); the login stays in `LoginSession` in memory only (`SessionOnlyLoginStore` remembers nothing, `BrowserNoCredentialsHandler` keeps the browser dialog out), sign out reloads the page.
- **WPF**: `Views/*.xaml` with `{loc:Loc Key}` bindings; `ThemeService` loads the design the user picked (`Themes/Design.*.xaml`, see
  the WPF part of [Look](reference.md#look) in the reference) and applies the accent colour from the API to it;
  `DpapiLoginStore` keeps the remembered login encrypted for the Windows user; `IClientUpdater` implementation runs the installer for self-update.
- **Linux** (`src/Kyuplan.Client.Linux`, part of `Kyuplan.sln`; it builds on every system because Gir.Core is managed code, but only runs where GTK 4.10+
  and libadwaita 1.4+ are installed): one `Adw.ApplicationWindow` shows, one after the other, the address question, the setup of a new API, the sign-in
  and then the application (`KyuplanApp`, `Stages/`). `NavigationViewModel` is the sidebar (a page is in it while the user may use it, as in WPF, plus the
  settings page the web client has); `PageRegistry` picks the page that draws a page view model (view-model-first, like the WPF data templates).
  GTK has no data binding, so the widgets are built in code with the helpers of `Kit/`: `Bind.Watch` subscribes to a view model's
  `PropertyChanged` (and ends with the widget), `Ui` has the factories (`Ui.Entry(vm, nameof(vm.Name))` is a two-way bound field, `Ui.Row`, `Ui.Choice`, `Ui.Repeat`,
  `Ui.BoxedList`), `Widgets` the shared pieces (page frame, key figures, bars, charts), `Table` a grid for the big tables. Texts come from the shared
  `Strings` like everywhere; they are read when a screen is built and the screen is built again when the language changes. Themes are
  colour sets (`Services/ThemePalette.cs`) that replace libadwaita's named colours through a CSS provider (`Theming`); light or dark follows the desktop
  (`Adw.StyleManager`). `SecretServiceLoginStore` keeps the login in the keyring through `secret-tool` (never on disk), `UserPreferences` the small
  settings in `$XDG_CONFIG_HOME/kyuplan`, `LinuxClientUpdater` installs the signed pacman package with `pkexec pacman -U`. A new page: a file in `Pages/`, an entry in
  `PageRegistry` and in `NavigationViewModel`. The files of the project that need no GTK (`Services/`, `NavigationViewModel`) are compiled into the test
  project and tested there; the widgets have no unit tests (they need a display): keep logic in the view models and look at them with
  [the screenshots](#linux-screenshots).
- **Android** (`src/Kyuplan.Client.Android`, part of `Kyuplan.sln`; building it needs the Android workload and a JDK/SDK): one
  `MainActivity` shows the address question, the sign-in and then `MainScreen`. `MobileShellViewModel` (Client.Shared, tested) is the tab bar: the
  pages of everyday use only, each tab present while the user may use it. A `Screen` (`Kit/Screen.cs`) draws a view model with plain Android
  views (`Ui` has the widgets) and draws again, once per burst, when a view model it observes changes; texts come from the shared `Strings`
  like everywhere (no layout files, nothing hard-coded). `KeystoreLoginStore` keeps the login encrypted with the Android Keystore,
  `AndroidClientUpdater` + `UpdateFileProvider` hand the downloaded APK to the package installer. A new page: a `Screen`, an entry in
  `MobileShellViewModel` and `MainScreen.CreatePage`. The screens have no automated tests (they need a device); keep logic in the view models.

## The rules that bite

1. **Texts.** Every UI string is a resource. Add each new key to **both** `Strings.resx` and `Strings.de.resx` with identical `{0}`
   placeholders (`ResourceFileTests` enforces it). The `Strings` class is generated at build time. Razor: `@Strings.Key`; XAML:
   `{loc:Loc Key}`; view models: `Strings.Key`. **No hard-coded UI strings**, including tooltips. Dates use format resources
   (`Format_Date`) because German writes `14. Okt. 2026`. Error messages coming from the API are English.
2. **Database schema.** Edit `Persistence/Entities.cs` / `PlannerDbContext.cs`, then add a migration for **both providers**:

   ```bash
   dotnet tool restore                       # once; the dotnet-ef tool is pinned in .config/dotnet-tools.json
   dotnet ef migrations add <Name> --context PlannerDbContext --project src/Kyuplan.Api --output-dir Persistence/Migrations
   dotnet build src/Kyuplan.Api
   dotnet ef migrations add <Name> --no-build --context PlannerPostgresDbContext --project src/Kyuplan.Api \
       --output-dir Persistence/PostgresMigrations \
       -- --Database:Provider=PostgreSql --Database:ConnectionString="Host=x;Database=x" --Accounts:ConnectionString="Host=x;Database=x"
   ```

   Accounts database: `--context AccountsDbContext --output-dir Accounts/Migrations`, and `AccountsPostgresDbContext` /
   `Accounts/PostgresMigrations`. **Never hand-edit an existing migration.** `PostgresMigrationTests` fails when the PostgreSQL twin is
   missing. No database server is contacted. Rebuild before every `dotnet ef` call. Catch unique/foreign-key violations with
   `DbUpdateException.IsUniqueViolation()` / `IsForeignKeyViolation()`, never with `SqliteException`.
   Tests that must run on a real server (`PostgresApiTests`, marked `[PostgresFact]`) read its connection string from the environment
   variable `KYUPLAN_TEST_POSTGRES` (a user who may create databases; each test creates and drops a database of its own) and are skipped
   without it. CI runs them in the `postgres` job against a PostgreSQL container.
3. **Both UIs must be accessible.** Every control a user can focus has a name for assistive technology: in WPF `AutomationProperties.Name`
   (same `{loc:Loc Key}` as the visible label; icon-only buttons, inputs, combo and date boxes, progress bars, `RevealPasswordBox`,
   `NumberBox`, `PermissionPicker`), in Blazor a `<label>`, `aria-label` or visible text (an icon-only `<button>` needs `aria-label`;
   `<Icon>` is `aria-hidden`). Nothing is taken out of the tab order (`tabindex="-1"` on a control is refused), the layout keeps the skip
   link and the `<main id="main">` landmark, and the fly-out menus are groups of links, not ARIA menus (no `role="menu"`). Keyboard
   details that markup cannot give live in `wwwroot/js/a11y.js` (skip link target, `aria-current`, Escape in the fly-outs, the error bar).
   `AccessibilityMarkupTests` checks these rules on the markup; the browser smoke tests check them in a browser.
4. **Both UIs must keep working.** A change to a view model or a contract may need edits in the Blazor page *and* the WPF view.
5. **Contracts first.** When the API shape changes, change `Kyuplan.Contracts` first, then the API, `IPlannerApi`/`PlannerApiClient`, the view
   model, then both UIs. The JSON context (`PlannerJsonContext`) needs new DTOs registered.
6. **Buttons are only enabled when pressing them would change something** (`CanExecute` against the real state), and a button for something a
   user may not do is hidden, not just disabled. Add a test.
7. **Permissions.** A new feature gets its own permission (`Contracts/Permission.cs`, with the `.view` pairing logic), policies in
   `PermissionAuthorization`, strings for it, and an entry in the docs table. Endpoints without a policy are open: check twice.
8. **Package versions** live in `Directory.Packages.props` (central package management); csproj files carry none.
9. **Line endings are LF.** Check with `git ls-files --eol` and keep edited files LF (the `.gitattributes`/editor must not convert).
10. **Tests must not depend on shipped `appsettings.json` values** (that once broke every workflow). Test hosts set `Sync:Enabled=false`,
    `DefaultData:Enabled=false` and `Setup:SettingsFile` to a missing file.
11. **Deployment docs.** Anything that changes how the container is configured or deployed must be reflected in [deployment.md](deployment.md).
12. **Logging and secrets.** Never log passwords. Spond credentials only via secrets/environment/settings (encrypted in the file).
13. **One replica.** The sync worker, login throttle and Spond cache are in memory; do not design features that need several instances
    without addressing that.

## Recipes

### Add a setting

1. Add the property to the options class (`Api/Configuration/Options.cs`) with a default; document it in the configuration table of the [reference](reference.md#configuration).
2. If it should be editable on the Settings page: add it to the settings DTOs (`Contracts/SettingsDtos.cs`), `Infrastructure/SettingsService.cs`,
   `SettingsForm`/`SettingsViewModel`, and the shared Blazor component `SettingsFields.razor` plus the WPF settings fields; add texts.
3. If it needs a restart to apply, make sure `SettingsService` reports it (the loop in `Program.cs` restarts the host).
4. Tests: `SetupApiTests`, `SetupViewModelTests`.

### Add an API endpoint

1. DTOs in `Contracts` (plus validation in a `*Rules.cs` if clients should validate too, and register in `PlannerJsonContext`).
2. Logic in a service under `Persistence/` (or `Statistics/`); register it in `Program.cs`.
3. A route in the matching `Endpoints/*.cs`, with the permission policy.
4. `IPlannerApi` + `PlannerApiClient` + `FakePlannerApi` (tests).
5. Tests next to the closest existing `*ApiTests.cs`, including a `403` test in `PermissionApiTests`.
6. Update the endpoint table in [the reference](reference.md#api).

### Add a page

1. A view model in `Client.Shared/ViewModels` (derive from `PageViewModel`), registered in `ServiceCollectionExtensions`.
2. Texts for both languages (including the `Nav_*` entry).
3. A Razor page in `Client.Blazor/Pages` (wrap in `PageGate`, add a navigation entry in `Layout`), and a view in
   `Client.Wpf/Views` (add a navigation entry in the shell's `MainViewModel`).
4. A permission (see rule 7) and `MainViewModelTests`/view model tests.

### Add a language

Add `Strings.<culture>.resx` with all keys of `Strings.resx`, list the culture in `LanguageService.Supported` and in
`wwwroot/js/lang.js`. `ResourceFileTests` checks for missing, left-over and mismatched keys.

### Add a Spond feature

Everything Spond-related passes through `IDataSource`. Extend the interface, implement it for every source (demo, custom script,
manual, and the Spond plugin in its own repository), and use `FakeSpondDataSource` in tests. A new kind of source that ships with the program: add the id to `DataSourceKinds`, the class, one line in `Program.cs`,
and an option in the settings page (`SettingsForm.DataSources`, the `Settings_DataSource_*` texts); any other source is a plugin ([Plugin development](plugin-development.md)). Remember: Spond.API never refreshes its token and reports failures as empty
lists; see the handling in the Spond plugin's data source.

### Add a client for another operating system

Give it an `IClientUpdater` (platform, installer extension, version, how to run the installer), register `ClientUpdateViewModel`, show its
button, then add a job to `.github/workflows/release.yml` that builds the installer as artifact `installer-<platform>` named
`Kyuplan-<platform>-<version>.<ext>` and add it to the `needs` of the `image` job. The platform ids are in `ClientPlatform`. Nothing changes in the
API or the web client. See [deployment.md](deployment.md).

### Branding your own desktop build

The icon of the program file and the installer (`assets/branding/app.ico`) and the publisher (`Authors` in `Directory.Build.props`,
`AppPublisher` in `installer/Kyuplan.iss`) are fixed at build time. `tools/Render-Branding.ps1` renders `app.ico` from the tab icon
`favicon.svg` (needs Microsoft Edge); commit the result. Logos, name, colour and tab icon are runtime settings (see the admin guide).

### Debug against the real client

- `/scalar` shows the OpenAPI document and lets you try calls with a login.
- Use `--Spond:UseDemoData=true` for reproducible data.
- To drive the Blazor UI in a browser against an auth-protected API, run the API and a small reverse proxy that injects the Basic header.
- For WPF runtime checks without the real `App` (it opens the main window), create the views offscreen in a small harness.

## Testing

```bash
dotnet test tests/Kyuplan.Tests
```

- One xUnit project covers the statistics engine, Spond mapping, the real API **in memory** against in-memory SQLite databases (`InMemoryDatabase`:
  one planner, one accounts), the Spond sync (against a Spond the test controls: `FakeSpondDataSource`, `SyncTestClub`), the migrations
  (including the PostgreSQL twin check), accounts and permissions, branding, the typed API client, and the **view models** (including the German texts).
- Test clients sign in as a known administrator (`TestAccounts`).
- There are no unit tests for Razor and XAML: keep views thin and test the view models. The web client also has [browser smoke tests](#browser-smoke-tests); WPF has [screenshots of every page](#wpf-screenshots). The WPF-only classes under test
  (`DpapiLoginStore`, `MainViewModel`) are compiled into the test project from their source files.
- Conventions: put a test next to the closest existing `*Tests.cs`; name tests after the rule they pin; keep the rules documents in sync with
  the tests that cite them.
- Run the narrow filter while you work and the **full suite once at the end**. Tests that start a host must not depend on shipped appsettings.

### Browser smoke tests

`tests/ui` holds Playwright tests of the web client (Node 22, not needed for `dotnet test`). They start the API in demo mode (fresh databases in the
temp folder, administrator `admin`), open **every page** as that administrator in a light and a dark desktop browser (and some pages on a phone),
and fail on an unhandled script error, the Blazor error bar, the "not allowed" text or a login that is not accepted. They compare nothing:
the point is the screenshot of each page, so that a UI change can be looked at.

```bash
cd tests/ui
npm install
npx playwright install chromium   # once
npx playwright test               # starts the API itself (dotnet run -c Release, port 5199)
npx playwright show-report artifacts/report
```

- Screenshots: `tests/ui/artifacts/screenshots/<desktop-light|desktop-dark|mobile>/<page>.png`. CI uploads them as the artifact `ui-screenshots`
  (job `ui-smoke`); a failed run also uploads the report and the traces (`ui-report`).
- `KYUPLAN_UI_BASE_URL` (+ `KYUPLAN_UI_PASSWORD`) tests an API that already runs; `KYUPLAN_UI_STRICT_CONSOLE=1` also fails on errors in the browser console.
- A new page of the web client = an entry in the list at the top of `smoke.spec.ts`.

### Linux screenshots

`tests/linux` (not part of `dotnet test`) is the Linux counterpart of the browser smoke tests: a Dockerfile of an Arch Linux with GTK 4, libadwaita, the .NET SDK and
a virtual X server (Xvfb), and `shots.sh`, which builds the API and the client from the checkout, starts the API in demo mode, starts the client, signs in as the
administrator and saves a PNG of the login screen and of every page of the sidebar. It fails when the build or the start fails or the client logs an exception.
Any machine with Docker will do, also Windows and macOS:

```bash
docker build -t kyuplan-arch tests/linux
docker run --rm -v "$PWD":/repo:ro -v "$PWD/linux-screenshots":/out kyuplan-arch bash /repo/tests/linux/shots.sh
```

- The pages are reached by clicking the rows of the sidebar at fixed places (the administrator of the demo data sees all of them); a new page of the
  sidebar moves the ones below it, so adjust the table in `shots.sh`.
- The administrator's password is made up by the script for that one run of a throw-away API.
- The window is drawn by the software renderer (`GSK_RENDERER=cairo`); the fonts and the theme are those of the container, not of your desktop.

### WPF screenshots

`tests/Kyuplan.WpfShots` (Windows only, not part of `dotnet test`) opens the main window of the WPF client offscreen, signs in as the
administrator of a **running** API and saves a PNG of every page. It is not a test: only a page that cannot be shown (error message of the page,
sign-in refused) fails it.

```bash
dotnet run --project src/Kyuplan.Api --launch-profile http -- --Spond:UseDemoData=true   # note the administrator password it is started with
dotnet run --project tests/Kyuplan.WpfShots -- http://localhost:5100 <user> <password> wpf-screenshots
```

- It follows the theme and language preferences of the Windows user (`%LocalAppData%\Kyuplan`) but never signs in with, or saves, a login.
- The API needs `Sync:Enabled=true` (the default): members and exams of the demo data reach the database through the sync. Both screenshot jobs do the same.
- CI: job `wpf-screenshots`, artifact `wpf-screenshots`.

## Releases and CI

- One tag `v<version>` releases everything (`.github/workflows/release.yml`): it tests on Windows, publishes the WPF client self-contained,
  builds the Inno Setup installer, builds the Linux package (`makepkg` in an Arch Linux container) and the Android app, builds and pushes the multi-arch Docker image (`ghcr.io/<owner>/kyuplan`, with the installer baked into
  `downloads/`), builds the documentation (the image carries the guides for users, `Kyuplan-docs-<version>.zip` carries all of them), and publishes a GitHub release with the installer and the archive attached.
- The tag's version becomes `-p:Version` for every build: assembly versions, `GET /api/info`, the installer and the self-update comparison.
- Locally built versions use `<Version>` of `Directory.Build.props`.
- Dependencies: Dependabot (`.github/dependabot.yml`) updates NuGet, npm, the Dockerfile, the Compose files and the actions weekly; `dependency-audit.yml` (every PR and weekly) and the start of the release fail on known vulnerabilities; the release scans the image with Trivy before pushing and attaches an SBOM. A new `package.json`, Dockerfile, Compose folder or composite action needs a Dependabot entry (`SupplyChainConfigTests`). Details in [deployment.md](deployment.md#dependency-and-supply-chain-checks).
- Details, tag formats, prerelease behaviour and building the pieces by hand are in [deployment.md](deployment.md#releasing).

```bash
git tag v0.1 && git push origin v0.1
```

## Contribution workflow

1. **Discuss first** for anything larger than a fix: open an issue describing the problem and the idea.
2. Branch from `main`. Keep a change focused: one topic per pull request.
3. Build and run the relevant tests; run the **full suite** before pushing. Update both UIs, texts in both languages, the PostgreSQL migration,
   and the docs for the rule you changed.
4. Write a clear commit message (what and why). Commits are small and describe one change.
5. In the pull request: what changed, how you tested it, screenshots for UI changes (both light and dark if relevant), and any follow-up.
6. Review checklist for contributors and reviewers:
   - [ ] Logic is in `Client.Shared` or `Api`, not duplicated in the UIs
   - [ ] New texts exist in English **and** German
   - [ ] Schema change has both migrations and the PostgreSQL twin test passes
   - [ ] New permission guarded on the server *and* hidden in the clients
   - [ ] Buttons enabled only when they would change something
   - [ ] Tests added; full suite green
   - [ ] LF line endings; no secrets; no personal data in fixtures
   - [ ] Docs updated (rules document, reference tables, deployment guide)

Security issues: do not open a public issue with details; contact the maintainer privately.

## Where to read more

| Topic | Document |
| --- | --- |
| Every setting, endpoint and table | [Reference](reference.md) |
| What end users do | [user-guide.md](user-guide.md) |
| Running and managing an instance | [admin-guide.md](admin-guide.md), [deployment.md](deployment.md) |
| Attendance rules | [attendance-statistics.md](attendance-statistics.md) |
| Exam rules | [exam-planning.md](exam-planning.md) |
| Comment and tag rules | [comments-and-tags.md](comments-and-tags.md) |
| Notes for coding agents | [AGENTS.md](../AGENTS.md) |
