# kyuplan

Planner for the karate club's belt exams, built on the club's [Spond](https://spond.com) groups. Its heart is the list of **exams**:
for an exam that is still to come it **suggests who is fit for it** (from their belt, the day they got it, their age and their
attendance, measured with one of the filters you saved on the statistics page) and lets you **save the members you want to put forward**,
to be invited on Spond later; for an exam that took place it shows **who attended and who received which belt**. Underneath is the
attendance analysis the planner started as: **who attended at least a given percentage of the events they were invited to in a time
span**, plus the figures around it that show how engaged each member is.

A backend API reads Spond; a **Blazor WebAssembly** client and a **WPF** client show the same pages — the **Exams**, the
**Dashboard**, the **Attendance Statistics**, the **Members** (who holds which belt, and the **comments** users write about each), the **Belts** (what each belt requires) and the **Tags**
(topics for the comments, each connected to a Spond event or to none) —
using the same view models. Both clients speak **English and German**.

## Solution layout

| Project | What it is |
| --- | --- |
| `src/Kyuplan.Contracts` | DTOs shared by the API and the clients (the wire format). |
| `src/Kyuplan.Api` | ASP.NET Core minimal API. Reads Spond through the **Spond.API** package, runs the statistics, serves JSON, and keeps exams, saved filters, belts, the club's members, who is saved for an exam and who received which belt in a **SQLite** (or PostgreSQL) database (EF Core; the members and the invitations to exams are kept in line with Spond automatically) and the user accounts in a second one. Every call needs a login (HTTP Basic). |
| `src/Kyuplan.Client.Shared` | The typed API client **and all view models** (CommunityToolkit.Mvvm), plus **every text the clients show** (`Resources/Strings*.resx`). No UI framework code, so Blazor and WPF share it. |
| `src/Kyuplan.Client.Blazor` | Blazor WebAssembly UI (hand-written CSS, light/dark). Signs in through the browser's own login dialog. Has the **Exams** page (the start page), the **Members** (with their comments), **Belts** and **Tags** pages, the **Account** page and, for administrators, the **Users** and **Branding** pages. |
| `src/Kyuplan.Client.Wpf` | WPF UI in four designs the user picks (Windows 11 on the Fluent theme that ships with .NET, Windows 10, Windows Vista / 7 "Aero", native WPF; light/dark where the design has it). No control library. Windows only. Has a sign-in window, can remember the login (encrypted) and has the **Exams** page (the first one), the **Members** (with their comments), **Belts**, **Tags** and **Account** pages, and, for administrators, the **Users** and **Branding** pages. |
| `tests/Kyuplan.Tests` | xUnit: statistics engine, Spond mapping, API (in memory, with in-memory SQLite databases), accounts and logins, API client, view models. |
| `assets/branding` | Placeholder logos (SVG), tab icon and program icon (a generic crest) that stand in until an instance sets its own. The API serves the logos; see [Branding](#branding). |
| `installer`, `deploy`, `.github` | Release engineering: the WPF client's Inno Setup script, the Docker Compose files (plain Docker, with PostgreSQL, TrueNAS), and the release workflows (the Dockerfiles sit next to the API and the Blazor project). See [Releases and deployment](#releases-and-deployment). |
| `docs/` | **Documentation index**: [user guide](docs/user-guide.md), [admin guide](docs/admin-guide.md), [deployment guide](docs/deployment.md), [developer guide](docs/developer-guide.md). |
| `docs/attendance-statistics.md` | Definition of every figure of the statistics. |
| `docs/exam-planning.md` | The rules behind the Exams page: who is fit for an exam, what saving members and recording belts do. |
| `docs/comments-and-tags.md` | The rules behind the comments on members and the tags: replies, formatting, edit history, who may delete, tags and events. |

```
Spond ──► Api ──JSON──► Client.Shared (API client + view models) ──► Blazor
                                                                 └──► WPF
```

Pages and filtering logic live once, in `Client.Shared`. Each UI only contains markup: the Blazor pages and the WPF
views bind to the same view models (`ExamsViewModel`, `DashboardViewModel`, `AttendanceStatisticsViewModel`, `MembersViewModel`, `MemberCommentsViewModel`, `BeltsViewModel`, `TagsViewModel`, ...).

## Languages

English (the neutral language) and German. The clients follow the language of the browser or of Windows until the
user picks one on the **Account > Personal settings** page (`PreferencesViewModel`, together with the theme). The choice is kept with
the account (`UserAccount.Language`, `PUT /api/account/language`, codes in `LanguageRules`, which a test keeps equal to
`LanguageService.Supported`); `ShellViewModel` switches to it whenever the user becomes known (sign-in on another computer), and the
client also remembers it locally for the screens before sign-in. The language decides the texts **and** how numbers and dates are written (`en-GB`, `de-DE`).

- **Texts** live in `src/Kyuplan.Client.Shared/Resources/Strings.resx` (English) and `Strings.de.resx`. MSBuild generates
  the typed `Strings` class from them at build time, so a new key is available as `Strings.My_Key` right away.
  View models and `Display` use it; Razor uses `@Strings.X`, XAML uses `{loc:Loc X}`. Date patterns (`Format_Date`, ...)
  are resources too, because German writes `14. Okt. 2026`.
- **Adding a language**: add `Strings.<culture>.resx` with every key of `Strings.resx` and list the language in
  `LanguageService.Supported` (and in `wwwroot/js/lang.js` for the browser). `ResourceFileTests` fails when a key
  is missing, left over, or uses other `{0}` placeholders than the original.
- **Blazor** loads its translations when it starts, so switching the language reloads the page. **WPF** switches
  live: view models rebuild their texts from the data they already hold, without asking the API again.
- Names and events come from Spond and are shown as they are. Error messages that came from the API itself are in English.

## Run it

Requires the **.NET 10 SDK** (the Spond.API package targets .NET 10). The WPF client needs Windows.

The API starts in **demo mode** in the `Development` environment: it serves generated data for a fictional club, so
everything can be tried without Spond credentials. The UIs show a "Demo data" marker while this is the case.

```bash
dotnet run --project src/Kyuplan.Api --launch-profile http                 # http://localhost:5100: web client, API below /api, docs at /scalar
dotnet run --project src/Kyuplan.Client.Wpf                                # Windows desktop app
```

The API serves the Blazor web client itself, so the web client needs no address. (`dotnet run --project src/Kyuplan.Client.Blazor --launch-profile http` still starts it alone on http://localhost:5200 for hot reload; then set `PlannerApi:BaseAddress` in its `wwwroot/appsettings.json` to `http://localhost:5100/` and `Api:AllowedOrigins` of the API to `http://localhost:5200`.)

In Visual Studio, start the API, plus the WPF client if you want it.

The first start of the API creates its SQLite databases (`data/kyuplan.db` and `data/accounts.db` below the API's content root,
git-ignored) and brings their schema up to date; see [Exams and saved filters](#exams-and-saved-filters).

**A new database also starts with default data**, so that a club does not have to type in its grades first: the planner database gets
the **19 standard belts** (9th Kyu to 3rd Dan, with their colours and the age and waiting time for each step, see
`Persistence/DefaultBelts.cs`) and the accounts database the **roles** *Leading Trainer*, *Trainer* and *Passive User* (see
`Accounts/DefaultRoles.cs`). Nothing personal is part of it: no members, exams, comments, users or branding. It is added **once, when a
database is created**: a database that exists is never touched, and what is deleted later does not come back. All of it is ordinary
data that the Belts and Roles pages change. `DefaultData:Enabled` = `false` starts new databases empty.

**The API starts with no configuration at all, and the web client sets it up.** Everything needs a login, so while the API has no user
it says so in its log, and the web client, instead of the application, shows a **setup screen**: where the two databases are, the Spond
login (or demo data), the time zone, the exam keyword, the default thresholds and the sync, plus the **first administrator** (user name
and password). Finishing it creates that administrator, saves the settings and, if a setting changed that only a restart applies, restarts the
API by itself (the page waits for it and then loads the application; the browser asks for the login of the new administrator).
Whoever opens the page first creates the administrator, so do not expose a new installation before you have set it up; once there is
a user, the setup answers 409 for good.

**The settings stay editable**: administrators (permissions `settings.view` / `settings.edit`) change the same values later on the
**Settings** page. They are kept in a file of their own, `data/settings.json` (`Setup:SettingsFile`; JSON like `appsettings.json`, holding only
what was set in the client), which is read on top of `appsettings.json` and survives restarts and updates. A save that changes
something restarts the application inside its process (no service manager needed); a save that changes nothing does not. Values set by
environment variables or command line arguments win over the file (so a container that pins `Database__ConnectionString` keeps it, whatever
the page says). A new database path starts an empty database, nothing is moved. The Spond password is stored in that file in plain text,
as it would be in `appsettings.json`: keep `data/` private. A file that is not valid JSON is ignored (and said so on the console).

A headless installation can still be set up by configuration: set `Accounts:InitialAdminPassword` before the first start and that
administrator is created without the setup screen (it is only used while there is no user at all):

```bash
dotnet user-secrets set "Accounts:InitialAdminPassword" "…" --project src/Kyuplan.Api
```

The WPF client asks for the login when it starts, the browser asks when the Blazor client first calls the API; see
[Accounts and signing in](#accounts-and-signing-in).

### Connect it to Spond

Keep credentials out of source control: use user secrets (or environment variables such as `Spond__Password`).

```bash
dotnet user-secrets set "Spond:UseDemoData" "false" --project src/Kyuplan.Api
dotnet user-secrets set "Spond:Email"       "trainer@example.org" --project src/Kyuplan.Api
dotnet user-secrets set "Spond:Password"    "…"                   --project src/Kyuplan.Api
```

- Use `Spond:PhoneNumber` instead of `Spond:Email` if the account logs in by phone.
- **Several accounts**: Spond sometimes answers an account that sent too many requests with nothing at all. List more logins in
  `Spond:Accounts` (`Spond__Accounts__0__Email`, `Spond__Accounts__0__Password`, `Spond__Accounts__1__Email`, … as environment
  variables); `Spond:Email` / `Password` stays the first account. When an account yields no groups or no events, or its login is
  rejected, the next one is asked, and so on. An account that came back empty while another one delivered is asked last for
  `Spond:AccountCooldown` (10 min), so an account Spond ignores is not asked again and again. A month without events at all
  (every account empty) changes nothing. All accounts must see the same groups; the log tells which account was skipped.
- The account sees the groups it belongs to; use a dedicated account that is an admin of the groups to analyse.
- Administrators are members of their group like everybody else, so they show up in the statistics, the member list and the
  exam planning. Set `Spond:IgnoreAdministrators` to `true` to leave out the administrators: in Spond the members of a group
  with `Respondent` = true are the normal ones, those with `Respondent` = false are the administrators. Members who are
  already in the database are removed by the next sync, with their belt and their exam invitations.
- **Accounts with two-factor authentication are not supported** (the API runs unattended).
- Spond.API never refreshes its token and reports failed requests as empty lists, so the API logs in again every
  `Spond:LoginLifetime` (6 h), treats "no groups" as a failed login, and keeps empty results cached only briefly.
- Events are fetched month by month and cached for `Spond:CacheDuration` (15 min). The **Refresh** button in both
  clients clears that cache.

## Configuration

**API** (`appsettings.json`, user secrets, environment variables such as `Statistics__DashboardDays`)

| Key | Default | Meaning |
| --- | --- | --- |
| `Logging:File:Path` | `logs/kyuplan-.log` (in the Docker image `/data/logs/kyuplan-.log`) | Text files the log is written to, in addition to the console, with the levels of `Logging:LogLevel`. The date goes before the extension (`kyuplan-20261014.log`), a new file is started every day and the 31 newest are kept. A relative path is relative to the API's content root and its folder is created. Empty = no log files. |
| `Spond:UseDemoData` | `false` (`true` in Development) | Serve generated sample data. |
| `Spond:Email` / `PhoneNumber` / `Password` | – | Spond login (the first account). |
| `Spond:Accounts` | `[]` | More Spond logins (each with `Email` or `PhoneNumber`, and `Password`), asked in this order when the one before yields no groups or events. |
| `Spond:AccountCooldown` | `00:10:00` | How long an account that came back empty while another one delivered is asked after the others. |
| `Spond:IgnoreAdministrators` | `false` | Leave the administrators of the Spond groups (the members with `Respondent` = false, the login account usually among them) out of the statistics, the members and the exams. Not used with demo data. |
| `Spond:LoginLifetime` / `CacheDuration` | `06:00:00` / `00:15:00` | Re-login interval, event cache lifetime. |
| `Statistics:TimeZone` | `Europe/Berlin` | Time zone for days, weekdays and months (Spond delivers UTC). |
| `Statistics:DefaultMinAttendancePercent` | `75` | Pre-selected threshold. |
| `Statistics:DefaultMinInvitedEvents` | `3` | Pre-selected minimum number of invitations. |
| `Statistics:DashboardDays` / `UpcomingDays` | `90` / `14` | Dashboard look-back and look-ahead. |
| `Statistics:ExamKeyword` | `Prüfung` | Events whose name contains this word (case-insensitive) are exams. The statistics page opens with the span from the last exam to the next one. Empty, or no exam pair in the window = it opens with the events of the last six months instead. |
| `Statistics:EventSearchMonthsBack` | `12` | How many months back events are searched for exams and offered as the start or end of the span (1–24). |
| `Statistics:EventSearchMonthsAhead` | `6` | How many months ahead events are searched and offered, likewise (1–24). |
| `Database:Provider` | `Sqlite` | The database system of **both** databases: `Sqlite` (files, nothing to install) or `PostgreSql` (a server, version 13 or newer; needs the `citext` extension). See [docs/deployment.md](docs/deployment.md#choosing-the-database). |
| `Database:ConnectionString` | `Data Source=data/kyuplan.db` | Database of exams, saved filters, belts, members and comments. SQLite: a file; a relative name is resolved against the API's content root and its folder is created; keep the file on a persistent volume when the API runs in a container. PostgreSQL: an Npgsql connection string, e.g. `Host=db;Database=kyuplan;Username=kyuplan;Password=...`. |
| `DefaultData:Enabled` | `true` | Whether a database that is **created** by this start gets the standard belts and roles (see [Run it](#run-it)). Databases that exist are never filled, whatever the setting. |
| `Sync:Enabled` | `true` | Whether the API keeps the members and the exams' invitations in its database in line with Spond by itself (see [Belts, members and exam invitations](#belts-members-and-exam-invitations)). |
| `Sync:Interval` | `00:15:00` | How long to wait between two syncs; it runs once when the API starts, too. Spond data is cached for `Spond:CacheDuration`, so a much shorter wait reads the same data again. Zero or less = only at start. |
| `Accounts:ConnectionString` | `Data Source=data/accounts.db` | Database of the user accounts, of the system `Database:Provider` names, **separate** from the one above (with PostgreSQL it may be the same server database: the tables do not clash) and resolved the same way. Back it up and protect it on its own: it holds the password hashes. |
| `Accounts:InitialAdminUserName` | `admin` | User name of the administrator that is created when the accounts database has no user. |
| `Accounts:InitialAdminPassword` | empty | Its password (12 to 128 characters). Empty = no administrator is created: the web client's setup screen asks for one (see [Run it](#run-it)). Only used while there is no user; it can be removed from the configuration afterwards. |
| `Setup:SettingsFile` | `data/settings.json` | The file the web client's setup and settings page write to; read on top of `appsettings.json`, below environment variables and the command line. Relative to the API's content root; in the container it is `/data/settings.json`. |
| `Api:ApiKey` | empty | If set, `/api` requests need an `X-Api-Key` header **in addition to** the login. Optional; it keeps nobody out who can open the Blazor site, whose files contain it. |
| `Api:AllowInsecureHttp` | `false` | Take logins over plain HTTP from other machines too. Otherwise only HTTPS (or this machine) is served. |
| `Api:MaxLoginFailuresPerAddress`, `Api:MaxLoginFailuresPerUser`, `Api:LoginFailureWindow` | `10`, `30`, `00:15:00` | Lockout after failed logins (0 = no limit). |
| `AllowedHosts` | `*` (the first start proposes the address in use) | Host names the API answers to, separated by `;`. |
| `Setup:KeyDirectory` | `keys` next to the settings file | Where the key for the encrypted passwords in the settings file lives. |
| `Api:AllowedOrigins` | `[]` (dev: the Blazor dev URLs) | Browser origins allowed to call the API (CORS, with credentials, so that the browser may send its login). |
| `Branding:*` | placeholders (see [Branding](#branding)) | Name, accent colour, logos and icon of the instance, served to both clients. The basis: administrators can change them in the web client, and what they save applies instead. |

**Blazor client**: served by the API itself, so it talks to the address it was opened at and needs no setting. `wwwroot/appsettings.json` → `PlannerApi:BaseAddress` (empty = the page's own server) only matters when the client is hosted elsewhere, e.g. `dotnet run` of the Blazor project on its dev server; `PlannerApi:ApiKey` cannot be used with the served client.
**WPF client**: the address of the API is not configured but asked for by the client itself on its first start, and can be changed in the login window (**Change …** next to the server address). `appsettings.json` next to the executable only has `PlannerApi:ApiKey`.

Both clients also remember the **filter you used last** (see [The statistics](#the-statistics)): the Blazor client in the
browser's local storage (`pp-filter`), the WPF client in `%LocalAppData%\Kyuplan\last-filter.txt`, next to the saved theme and language.
The WPF client can also remember the **login**, encrypted (`login.dat` there, see [Accounts and signing in](#accounts-and-signing-in)); the Blazor client never stores one.

### API

| Endpoint | |
| --- | --- |
| *every `/api` endpoint* | needs a login: `Authorization: Basic …`. Without one, or with a wrong one, the answer is `401` with `WWW-Authenticate: Basic`. The one exception is *reading* the branding (next row). Apart from `/api/info`, `/api/refresh` and `/api/account` (every signed-in user) each endpoint also needs a **permission** of the user (`403` without it; the table below says which, see [Permissions and roles](#permissions-and-roles)). |
| `GET /api/branding` · `GET /api/branding/{logo\|logo-on-dark\|icon}` | **Open to everybody**: no login and not even the `X-Api-Key` (the clients show it before anybody has signed in, and an `<img>` can send neither). The first is `{ productName, organizationName, accentColor, logoUrl, logoOnDarkUrl, iconUrl, defaultWebTheme, defaultWindowsTheme }` (the urls are relative to the API's address, `null` = the instance has no such image); the others are the image files (`404` when there is none; `Cache-Control: no-cache` with an `ETag`; a `Content-Security-Policy` that lets an SVG opened on its own run nothing). See [Branding](#branding). |
| `GET /api/branding/settings` · `PUT /api/branding` · `PUT /api/branding/{slot}` · `DELETE /api/branding/{slot}` · `DELETE /api/branding` | **`branding.view`** for `GET …/settings`, **`branding.edit`** for the others (`403` without it; unlike reading, these need the `X-Api-Key` when one is set). The settings are the branding as it is edited: `{ productName, organizationName, accentColor, isCustomized, images: [{ slot, url }] }`, with each slot's *own* image only (`url` `null` = none of its own). `PUT /api/branding` saves `{ "productName", "organizationName", "accentColor", "defaultWebTheme", "defaultWindowsTheme" }` (`400` for a missing product name, a colour that is not `#RGB`/`#RRGGBB`, a name that is too long, or a default theme the client does not have; the organisation may be empty; a theme left out or `null` keeps the saved one). `PUT /api/branding/{slot}` takes the **image as the request body**: the bytes decide what it is, not the `Content-Type`; SVG, PNG, JPEG, GIF or BMP for `logo` / `logo-on-dark`, PNG, SVG or ICO for `icon`; at most 1 MB (`400` otherwise; `404` for an unknown slot). `DELETE /api/branding/{slot}` takes the slot's image away (it has none from then on, even if the settings name a file), `DELETE /api/branding` forgets everything saved and the settings of the API apply again. All of them answer with the settings. |
| `GET /api/info` | Version, demo flag, default filter values. |
| `GET /api/account` · `PUT /api/account` | The signed-in user (`{ id, userName, displayName, aliases, roles: [{ id, name }], permissions, effectivePermissions }`: `permissions` are the user's own, `effectivePermissions` everything they may do, with their roles'; a client uses it to check a login and to know what to offer); change **your own** names and/or password (`{ "userName", "currentPassword", "newPassword", "displayName", "aliases" }`; a new password needs the current one, `400` otherwise; `409` when a name is taken; `displayName` and `aliases` left out keep what there is, see [Accounts](#accounts-and-signing-in)). Nothing else can be changed here, in particular not the roles and permissions. |
| `GET /api/users` · `GET /api/users/{userId}` · `POST /api/users` · `PUT /api/users/{userId}` · `DELETE /api/users/{userId}` | **`users.view`** for the two reads, **`users.edit`** for create and change, **`users.delete`** for delete: list users; one user; create one (`{ "userName", "password", "displayName", "aliases", "roleIds", "permissions" }`); change one (same body; `password`, `displayName`, `aliases`, `roleIds` and `permissions` left out keep the current ones); delete one (`204`; their login, with its aliases, stops working at once). `roleIds` and `permissions` (the user's own) may only be sent by somebody with **`users.grant`**: without it a request that has them is refused with `403`, not silently ignored; a new user without them has no role and no permission. `400` for an unknown role or permission; `409` when a user name or alias is taken (by a user name or alias of anybody else, in any case), when the last user with the administrator role would lose it, or when somebody deletes **themselves** (another user with `users.delete` can). |
| `GET /api/roles` · `GET /api/roles/{roleId}` · `POST /api/roles` · `PUT /api/roles/{roleId}` · `DELETE /api/roles/{roleId}` | **`roles.view`** (or `users.grant`, who has to choose among them) for the reads, **`roles.edit`** for the rest. A role is `{ id, name, isSystem, permissions, userCount }`; create and replace take `{ "name", "permissions" }` (names are unique in any case, `409` otherwise; `400` for a permission that does not exist or is listed twice). Changing a role changes what everybody who has it may do, from their next request on; deleting one takes it from its users (their other roles and own permissions stay). The administrator role (`isSystem`) cannot be changed or deleted (`409`). |
| `GET /api/groups` | Groups with subgroups. |
| `GET /api/dashboard?groupId=&subGroupId=&days=` | Dashboard figures, upcoming events, top members, members needing attention. |
| `GET /api/events?groupId=` | The events of the last `Statistics:EventSearchMonthsBack` (12) and the next `Statistics:EventSearchMonthsAhead` (6) months (cancelled ones left out) and the span to preselect: the next upcoming exam and the latest one before it, or without such a pair the first and the latest event of the last six months (`suggestedFromEventId`, `suggestedToEventId`; `null` only when no event took place in the last six months). |
| `GET /api/statistics/attendance?groupId=&from=&to=&minAttendancePercent=&minInvitedEvents=&subGroupIds=&eventNameContains=&eventNameExcludes=` | Per-member and per-event attendance, summary, distribution, monthly trend. `subGroupIds` may be repeated (`&subGroupIds=a&subGroupIds=b`) to report the members of any of those subgroups; leave it out for everyone. Give the time span as `from`/`to` **or** as `fromEventId`/`toEventId` (ids from `/api/events`; an event stands for the day it starts on, both days included); the response's `filter` holds the days it resolved to. |
| `POST /api/refresh` | Drop cached Spond data. |
| `GET /api/exams` | All exams (Spond events that are exams), the next one first, each with its saved filters and how many members were invited, accepted, are saved for it and received a belt (`invitedCount`, `attendedCount`, `preSavedCount`, `beltsAwardedCount`). An exam's `name` and `date` are its event's and follow it on Spond. |
| `GET /api/exams/{examId}` · `POST /api/exams` · `PUT /api/exams/{examId}` · `DELETE /api/exams/{examId}` | One exam; make a Spond event an exam (`{ "groupId", "eventId", "subGroupIds" }`, an event from `/api/events`; `subGroupIds` are the subgroups the exam's default filter looks at, none or left out = everyone); point an exam at another event (its filters and subgroups stay, unless `subGroupIds` is given); delete it **together with its filters, the members saved for it and the belts recorded at it** (the event on Spond is not touched, nor are the belts members hold). `404` when the group or the event (it must be in the search window) is not found; `409` when the event already is an exam. |
| `GET /api/exams/{examId}/filters/{filterId}` · `POST …/filters` · `PUT …/filters/{filterId}` · `DELETE …/filters/{filterId}` | One saved filter of an exam; save one (`{ "name", "settings" }`, see `FilterSettingsDto`); replace it; delete it. `409` when the exam already has a filter of that name; `404` when the filter is not one of that exam. |
| `GET /api/belts` · `GET /api/belts/{beltId}` · `POST /api/belts` · `PUT /api/belts/{beltId}` · `DELETE /api/belts/{beltId}` | The belts, **in the order of the progression** (a belt comes after the belts it requires, then by name), each with its `prerequisites` (one entry `{ "beltId", "minAgeYears", "minWaitMonths" }` per previous belt: the age and the waiting time are demanded **for coming from that belt**, so Blue → Brown can ask for more than Blue-brown → Brown) and `memberCount`; one belt; create one (`{ "name", "primaryColor", "secondaryColor", "prerequisites" }`, colours `#RRGGBB`, `secondaryColor` `null` = no stripe, `prerequisites` may be empty); replace everything about one; delete one (`204`, its own requirements go with it). `400` for a rule that is broken (colour, name, a previous belt listed twice, age 0–99, waiting time 0–240 months, both per previous belt) and when a required belt does not exist or a belt would require itself, directly or through other belts; `409` when the name is taken (in any case) and when a delete is refused because members hold the belt, other belts require it or it was handed out at an exam. |
| `GET /api/members?groupId=` · `PUT /api/members/{memberId}` | **`members.view`** for the list, **`members.edit`** to set a member. The members the API holds (by name; `groupId` is optional), each with `beltId`, `beltSince`, `startedAt`, `birthDate` and `commentCount` (**0 unless the user has `comments.view`**: comments are not part of what the members page shows by itself); set them for one (`{ "beltId", "startedAt", "birthDate", "beltSince" }`, everything is replaced and `null` clears; `startedAt` and `beltSince` must be since 1950, `birthDate` since 1900, none in the future, else `400`; a member without a belt has no `beltSince`). Members are not added or removed here, Spond decides who is a member; a day set this way is kept by the sync. |
| `GET /api/members/{memberId}/comments` · `POST /api/members/{memberId}/comments` · `PUT /api/comments/{commentId}` · `DELETE /api/comments/{commentId}` | The comments on a member (oldest first, each with its tags and the history of its edits); write one or a reply (`{ "body", "tagIds", "parentId" }`, the body in the markup `**bold**` `*italic*` `__underlined__` `~~crossed out~~`); replace the body and tags of one (the edit is recorded with who made it and how, and what it said before); delete one **and the replies to it**. Reading needs **`comments.view`**, writing **`comments.write`**, replacing **`comments.edit`**, deleting **`comments.delete`** for the user's own comments or **`comments.deleteAny`** for all (`403` otherwise). See [docs/comments-and-tags.md](docs/comments-and-tags.md). |
| `GET /api/calendar?groupId=&view=day\|week\|month&date=` | **`calendar.view`**. The events of a group within the day, week (Monday to Sunday) or month of `date` (default: today), with how many accepted, declined or did not answer; cancelled events are marked. Events are those of the search window. |
| `GET /api/calendar/current?groupId=` | **`calendar.view`**. The events going on right now (the clients' banner asks this every minute). |
| `GET /api/calendar/events/{eventId}?groupId=` | **`calendar.view`**. One event with everybody invited (name, answer, planner member id when known); with `comments.view` also the comments on them that carry the event's tag. |
| `POST /api/calendar/events/{eventId}/members/{memberId}/comments?groupId=` | **`comments.write`**. A comment on a member, tagged with the event's tag (the oldest tag connected to the event; one named after the event and its day is created if there is none). |
| `GET /api/tags` · `GET /api/tags/{tagId}` · `POST /api/tags` · `PUT /api/tags/{tagId}` · `DELETE /api/tags/{tagId}` | **`tags.edit`** to create, replace and delete; reading needs `tags.view`, `comments.write` or `comments.edit` (whoever writes or changes comments chooses among the tags). The tags by name, each with the Spond event it is connected to (none or one; `groupId` + `eventId` on write, the event must be in the group's search window) and how many comments carry it; create, replace and delete one (a deleted tag is taken off the comments, which stay). `409` when the name is taken (in any case). |
| `GET /api/exams/{examId}/members` | Everybody the exam knows, by name: those invited on Spond (`invitation`: `Attended` = accepted, `Declined`, `Unanswered`, `Other`; `null` = not invited), those saved for it (`isPreSaved`) and those who received a belt at it (`receivedBeltId`). |
| `GET /api/exams/{examId}/suggestions?filterId=` | The members of the exam's group **judged for the exam**: the belt each would take and whether they meet what it asks (age and waiting time on the day of the exam, attendance) with the saved filter `filterId` (of this exam's group; `400` otherwise) or, without one, the default (previous exam to this exam). See [docs/exam-planning.md](docs/exam-planning.md). |
| `PUT /api/exams/{examId}/candidates` | Save members for the exam in the planner's own database, to be invited on Spond later (`{ "add": [memberIds], "remove": [memberIds] }`; whoever is not named stays as they are; `400` for a member that does not exist or is of another group). **Nobody is invited by this.** |
| `PUT /api/exams/{examId}/results/{memberId}` | Record the belt a member received at the exam (`{ "beltId" }`; `null` takes the result back). It also becomes the belt the member holds, with the exam day as the day they got it, unless they got a belt after this exam. `400` while the exam has not taken place. |

## The Exams page

The start page of both clients, and the reason the rest is there (the rules are in [docs/exam-planning.md](docs/exam-planning.md)):

- the **exams** of the group chosen at the top: the ones to come first, then the past ones; **Add exam** takes the Spond event that is the
  exam (a searchable list), **Delete exam** asks first;
- for an exam **to come**: *Who is fit for this exam*. Every member is judged for the next belt **on the day of the exam** (the belt after
  the one they hold, whose minimum age and waiting time are set per previous belt on the Belts page) and by their attendance, which is
  measured with a **filter you saved on the Attendance Statistics page** — you choose which one (*Measure attendance with*), or the
  default (previous exam to this exam, for the subgroups you ticked when you added the exam). Each member gets a verdict — **fit**, **not yet** (with the reasons), **needs data** (no belt,
  birth date or belt day) or **top belt** — and the page shows the fit ones first. **Tick the members to put forward and save the
  selection**: they are only kept in the planner's database (the Spond API this planner uses cannot invite anybody to an event); the page
  shows which of them are not invited on Spond yet, so that they can be invited there all at once;
- for an exam **that took place**: who attended (accepted) and, per attendee, the belt they **received**. Choosing one records it at once and
  makes it the belt the member holds, from the day of the exam (so the waiting time for the next belt counts from there).

Everything the suggestions need that Spond does not know is kept on the **Members** page (belt, the day they got it, birth date) and the
**Belts** page; the planner shows what is missing instead of guessing. (Belts are club-specific and are not generated, not even in demo
mode: create them on the Belts page and give them to the members first; until then everybody "needs data".)

## The statistics

The page answers the headline question with a **minimum attendance** slider and a **time span**; a member is
*qualified* when they accepted at least that share of the events **they were invited to** in the span. On top of that:

- filters: the time span is defined by **two events** — *From event* and *To event*, each picked in a searchable list
  that shows the date first — and opens with the previous exam and the next one (see `Statistics:ExamKeyword`; a group
  without such a pair opens with the events of the last six months).
  The other main filters are the subgroups (*Members*, tick any number; none ticked = everyone) and the minimum number of invitations (members with fewer get no
  verdict). **Advanced filters** hold the time span as dates with quick ranges (the last of dates and events you touched is in
  effect) and *only events containing* / *ignore events containing* (to leave out exams, tournaments, parties).
  A member search and a status filter work on the result;
- **filters are remembered and can be saved**: the filter you used last opens again with the page (and brings its group
  back), and a filter can be saved under a name for an exam — *Save as …*, for example "Filter for Gürtelprüfung on 26 Sep 2026" — and
  chosen again later from *Saved filters* (see [Exams and saved filters](#exams-and-saved-filters));
- per member: attendance, answer rate, current and longest streak, last attended, sessions per week, the outcome of the
  last 10 invitations, a rising/falling trend and a 0–100 engagement score. The member table shows the **name** in its
  own *Member* column and the member's **subgroups** in a *Subgroups* column of their own (both can be sorted by);
- overall: distribution of attendance, attendance by month, attendance per event.

**"Attended" means "accepted the invitation"** — Spond exposes RSVPs, not check-ins. See
[docs/attendance-statistics.md](docs/attendance-statistics.md) for the exact rules.

## Exams and saved filters

The API keeps one database (`Database:ConnectionString`; SQLite, or PostgreSQL with `Database:Provider`) with two tables and a **one exam to many filters**
relationship:

| Table | Columns | |
| --- | --- | --- |
| `Exams` | `Id`, `GroupId`, `EventId`, `Name`, `Date`, `SubGroupIds` (JSON array), `CreatedAt` | An exam, e.g. the belt exam of a day: **the Spond event `EventId` of group `GroupId`**. `EventId` is unique: an event is at most one exam. `Name` and `Date` are the event's (see below). `SubGroupIds` are the subgroups its default filter looks at (empty = everyone). |
| `SavedFilters` | `Id`, `ExamId` → `Exams.Id`, `Name`, `GroupId`, `From`, `To`, `FromEventId`, `ToEventId`, `SubGroupIds` (JSON array), `MinAttendancePercent`, `MinInvitedEvents`, `EventNameContains`, `EventNameExcludes`, `CreatedAt`, `UpdatedAt` | The settings of the filter card, stored under a name. A filter belongs to exactly one exam and is deleted with it; its name is unique within the exam. |

**An exam is a Spond event, not something typed in.** Creating one takes only the group and the event; the API looks the event
up on Spond (it must be within the search window, `Statistics:EventSearchMonthsBack` / `…Ahead`) and copies its name and its
day. Whenever exams are read, the copies are brought up to date from Spond, so an event that was renamed or moved is still the
same exam (and keeps its filters) under its new name and day. An exam whose event has left the search window, or whose group
cannot be read because Spond is unreachable, keeps the name and day it last saw: listing exams never needs Spond. Exams that
were created while they were still free-text (before the migration `ExamsAreSpondEvents`) had no event to link to and were
removed by it, together with their saved filters.

(The same database also holds the belts, the members and the invitations to exams, see
[Belts, members and exam invitations](#belts-members-and-exam-invitations).)

A saved filter keeps the time span both ways: as the two **events** (so that it follows them) and as the **days** they stood
for when it was saved. Spond events leave the server's search window after a while (`Statistics:EventSearchMonthsBack`); a
filter whose events are gone is applied with its two days instead.

In the clients, **Saved filters** sits below the filter fields:

- *Saved filter* lists the filters grouped by exam; choosing one puts it into the filter card (switching the group, if it was
  saved for another one) and reloads.
- *Save as …* saves the filter card as it is now. It proposes the exam the time span ends at: the exam that *is* the *To* event
  (when dates define the span: the exam on its last day), otherwise *New exam …* with that event preselected, and the name
  *Filter for {event} on {date}*. Both can be changed. For a new exam you choose the **event that is the exam** in the same
  searchable event list as for the time span; the exam is created on the fly, and an event that already is an exam is reused.
- *Reload* puts the chosen saved filter into the filter card again, taking back the changes made since it was chosen (nothing
  is saved or deleted by it).
- *Update* replaces the chosen saved filter with the filter card as it is now; *Delete* removes it. Updating or deleting a saved
  filter and deleting an exam (in the save form, with the exam chosen) all ask first; deleting an exam deletes its filters with it.

Changing the schema: edit `src/Kyuplan.Api/Persistence/Entities.cs` / `PlannerDbContext.cs` and add a migration
(the API applies pending migrations on start). PostgreSQL has migrations of its own, so a change needs **two**; a test fails when the second is missing:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --context PlannerDbContext --project src/Kyuplan.Api --output-dir Persistence/Migrations
dotnet build src/Kyuplan.Api
dotnet ef migrations add <Name> --no-build --context PlannerPostgresDbContext --project src/Kyuplan.Api --output-dir Persistence/PostgresMigrations -- --Database:Provider=PostgreSql --Database:ConnectionString="Host=x;Database=x" --Accounts:ConnectionString="Host=x;Database=x"
```

## Belts, members and exam invitations

The same database has six more tables. The belts and what a member holds can be edited on the HTTP API (`/api/belts`, `/api/members`)
and on the **Belts** and **Members** pages of both clients (see [Belts and members in the clients](#belts-and-members-in-the-clients));
the members and the invitations to exams are Spond's data and only kept in line with it; who is saved for an exam and who received which belt are the planner's own.

| Table | Columns | |
| --- | --- | --- |
| `Belts` | `Id`, `Name`, `PrimaryColor`, `SecondaryColor` | Every belt that can be held; a new database starts with the 19 standard ones (see [Run it](#run-it)). The name is unique regardless of case. Colours are written `#RRGGBB` (the database refuses anything else); the secondary colour (a stripe) is optional. What it takes to get the belt is not stored here but per previous belt, in `BeltPrerequisites`. |
| `BeltPrerequisites` | `BeltId` → `Belts.Id`, `RequiredBeltId` → `Belts.Id`, `MinAgeYears`, `MinWaitMonths` | The **requirements** for taking `BeltId`, one row per way to get there: a member must hold **one of** the belts listed for it (none for the first belt, which nobody has to earn first; several belts may have none, neither the database nor the API demands "at least one"), and the row says what is demanded when coming from that belt: a minimum age in full years and a minimum waiting time in months since the member got `RequiredBeltId` (`0` = no requirement; negative values are refused). So Blue → Brown can need a different age than Blue-brown → Brown. A belt cannot require itself, and the API does not let belts require each other in a circle. Deleting a belt deletes its own rows; a belt that another belt requires, or that a member holds, cannot be deleted. |
| `Members` | `Id`, `GroupId`, `SpondId`, `Name`, `BeltId` → `Belts.Id`, `BeltSince`, `StartedAt`, `BirthDate`, `CreatedAt` | A member of a Spond group, copied from Spond; `(GroupId, SpondId)` is unique. `BeltId` is **null until a user assigns a belt** (Spond knows nothing about belts); `BeltSince` is the day they got it (null when unknown or without a belt; recording a belt at an exam sets it). `StartedAt` is the day the member started in the club, null until known. `BirthDate` (null until known) is taken over from Spond when Spond has it; the minimum age of a belt is worked out from it. |
| `ExamParticipants` | `ExamId` → `Exams.Id`, `MemberId` → `Members.Id`, `Rsvp` | Who was **invited** to an exam's event (one row per invited member) and what they answered (`Accepted`, `Declined`, `Unanswered`, `Other`). Who **attended** is whoever accepted, as everywhere in the statistics. Deleted with the exam, and with the member. |
| `ExamCandidates` | `ExamId` → `Exams.Id`, `MemberId` → `Members.Id`, `CreatedAt` | The members a user **saved for an exam**, to be invited on Spond later. The planner's own list: the sync never touches it. Deleted with the exam, and with the member. |
| `ExamResults` | `ExamId` → `Exams.Id`, `MemberId` → `Members.Id`, `BeltId` → `Belts.Id` | The belt a member **received** at an exam (none for those who did not). Kept apart from `ExamParticipants` on purpose: the sync drops invitations that are gone from Spond, but never a result. Deleted with the exam, and with the member; a belt that is in here cannot be deleted. |

**Kept in line with Spond.** The API syncs when it starts and then every `Sync:Interval` (`SpondSyncWorker`), and an exam that is
created or pointed at another event is synced at once. A sync that cannot reach Spond is logged and skipped, and the data stays as it
is. What it does:

- **Members**: a member Spond has and the database has not is added; a renamed one is renamed; one Spond no longer lists is removed
  (and with it from the invitations of exams), as are all members of a group the account no longer sees. The belt and the start day
  are never touched, nor are the exams' saved members and results. The birth date is taken over from Spond (`DateOfBirth`) while it is not known, and never changed once it is there. Spond has an id per group, so a person who is in two groups is two members.
- **Start day**: while `StartedAt` is null, it is set to the day of the first event the member **attended**: not cancelled, already over,
  and accepted. Only the events of the search window are looked at (`Statistics:EventSearchMonthsBack`, 12 months by default, at most
  24), so a member who started earlier is given the first day they show up in it; raise the setting, or correct the day by hand.
  A day that is there is never changed, whoever set it.
- **Invitations**: the rows of an exam are made to match its event. Invited members are added, changed answers are updated, people who
  are not invited (any more) are removed. Somebody invited who is not a member of the group is not recorded. An exam whose event
  cannot be found (outside the search window) or cannot be read keeps what it has, like its name and day, which the sync keeps
  current as well.

### Belts and members in the clients

- **Belts** page (Blazor and WPF): the belts in the order of the progression with their colour (and stripe), one line per previous belt (one
  of them must be held before) with the minimum age and the waiting time for coming from that belt, and how many members hold each. *New belt* and *Edit* open one form: name, colour
  and stripe colour (typed as `#RRGGBB`, picked from the usual belt colours, or with the browser's colour picker in Blazor), the belts that
  can be ticked as previous belts (a belt that requires this one, directly or through others, is not offered), each ticked one with its own
  minimum age and waiting time (`0` = none). *Delete* asks first and is disabled, with the reason as its tooltip, while members hold the belt or other belts require it.
- **Members** page (Blazor and WPF): the members of the group chosen at the top, with their belt, the day they got it, the day they train
  since and their birth date. Search by name, or show only the members who hold no belt yet. *Edit* sets the belt (or none), the day they got
  it (only with a belt), the day they train since (or none; *Clear the day* lets the sync work it out again) and the birth date of one
  member. Names come from Spond and the list is as fresh as the last sync (`Sync:Interval`). What the suggestions on the Exams page need
  (belt, belt day, birth date) is set here.
- Both pages read their data again every time they are shown, so belts handed out on one page show up on the other (member counts, the belts a
  member can be given); a page whose form is open is left alone until the form is closed.
- As everywhere, a button is only enabled while pressing it would change something: *Save* needs a valid form that differs from what is
  stored, a colour button is off while that colour is the one in use, *New* is off while its form is open.
- Every signed-in user may use these pages (`403` is only for the Users page and `/api/users`). The invitations to exams
  (`ExamParticipants`) are not editable: what a member answered on Spond overwrites a hand edit at the next sync. (Who is saved for an exam
  and who received a belt are edited on the Exams page.)

## Accounts and signing in

Every `/api` call needs a login, and the API has a **separate database** (SQLite or PostgreSQL) for the accounts (`Accounts:ConnectionString`,
the `Users` table: `Id`, `UserName`, `DisplayName`, `PasswordHash`, `Permissions` (the user's own, a JSON list), `CreatedAt`; and `UserAliases`, `Roles` and `UserRoles`). Passwords are stored only
as salted, stretched hashes (ASP.NET Core's `PasswordHasher`). A password has 12–128 characters and may contain anything. Its schema is
changed like the other one, with `--context AccountsDbContext --output-dir Accounts/Migrations` (and `AccountsPostgresDbContext` / `Accounts/PostgresMigrations` for PostgreSQL).

A user has three kinds of names:

- **One user name**, the main login name.
- **Aliases** (at most 10): more names that sign in as the same user with the same password. Nobody else sees them.
- **A display name**: what **other users see** (today: as the author and editor of a comment). It is never empty: when it is left empty
  or not set it is the user name (existing users were given their user name by the migration). It is its own name: renaming the user
  name does not change it, and it does not have to be unique.

A user name and an alias are both **login names**: unique regardless of case across all users *and* aliases (no name signs in as two
users), at most 64 characters, no colon or control characters (HTTP Basic sends `user:password`). The user name and aliases of a user
differ from each other. For `PUT`, `displayName` and `aliases` left out (`null`) keep what the user has; an empty display name sets it
back to the user name and an empty list takes all aliases away. *Known limit:* the uniqueness between the user names and the aliases is
checked by the service, not by an index (an index cannot span two tables): two requests that race for the same name at the same instant
could both succeed, and the name then signs in as whichever of the two the database finds first (the password still has to fit).

### Permissions and roles

What a user may do is decided by **permissions**, not by a flag. A permission is one thing a user may do (`members.view`, `comments.write`, …) in a **domain** (members, comments, …). It is given to a user **directly**, or through a **role**: a named set of permissions that many users share. A user may do everything their own permissions and those of all their roles allow, put together. The API checks it on every request (`403` otherwise), so a change applies from the user's **next request** on; a client learns it when it starts, when its user is saved, and when *Refresh* is pressed.

Every page needs its `.view` permission, and every change a permission of its own. The clients follow this: a page is only in the navigation for somebody who may see it (and says so when it is opened by its address anyway), and a button that changes something is **hidden** for somebody who may not use it, not just disabled. Everybody who is signed in may look after their own account (`/api/account`, the **Account** page), read `/api/info` and press *Refresh*; the branding can still be read by anybody.

| Domain | Permissions |
| --- | --- |
| dashboard | `dashboard.view` |
| statistics | `statistics.view` |
| calendar | `calendar.view` (the Calendar page, the event detail, the banner of an event in progress; writing a comment there needs `comments.write`) |
| exams | `exams.view` (the Exams page, who is fit for an exam, the saved filters), `exams.edit` (make an event an exam, delete exams), `exams.filters` (save, replace and delete saved filters), `exams.candidates` (save members for an exam), `exams.results` (record the belts handed out at an exam) |
| members | `members.view`, `members.edit` (belt and days of a member) |
| belts | `belts.view` (members and exams need to read the belts, too), `belts.edit` |
| comments | `comments.view`, `comments.write` (comments and replies), `comments.edit` (change comments), `comments.delete` (one's own), `comments.deleteAny` |
| tags | `tags.view`, `tags.edit` |
| branding | `branding.view`, `branding.edit` |
| users | `users.view`, `users.edit` (create users, change names and passwords), `users.delete`, `users.grant` (give roles and permissions to users) |
| roles | `roles.view`, `roles.edit` |

So comments are no part of the members: somebody who may view members does not see the comments on them, nor how many there are, without `comments.view`; and somebody who may read comments writes none without `comments.write`.

- The **administrator role** is built in: it has *every* permission, also those added later, and it can neither be changed nor deleted. The last user who has it cannot lose it, and cannot be deleted. The first user, created on the first start, has it.
- Instances that had the administrator flag keep what everybody could do: the migration gives the administrator role to the administrators and a role **User** to everybody else, with what a user without the flag could do before (everything except the users, the roles and the branding). That role is an ordinary one: change it as you like. A **new instance** has no such users, so it starts with the default roles instead: **Leading Trainer** (the former "User" role: runs the exams, members, belts and tags, writes and deletes comments), **Trainer** (looks at everything, writes comments and tags) and **Passive User** (only looks); see [Run it](#run-it). A **new user has no role and no permission** (their own account only) until somebody with `users.grant` gives them some.
- `users.grant` and `roles.edit` are as good as every permission: who has one can give themselves the rest. Hand them out like the administrator role. (The API does not stop somebody from giving permissions they do not have themselves.)
- A permission stored for a user or role that the catalogue no longer has (an older version wrote it) is ignored.
- The **Roles** page (Blazor and WPF) lists the roles with how many permissions and users each has and creates, changes and deletes them; the same tick boxes, by domain, are in the **Users** form for a user's own permissions. Ticking a permission ticks the `.view` of its domain too, and unticking the `.view` unticks the rest of the domain: nobody is given something to change in a place they may not look at.

The login is **HTTP Basic**, checked on every request, so there are no sessions or tokens to manage. **Use HTTPS** (the API
redirects to it outside Development): Basic sends the password with every request. There is **no lockout or rate limiting**; a
failed login is written to the log (user name and address, never the password) and each check costs a password hash, but for
anything reachable from the internet put a rate limiter or a firewall in front.

**Screens.** Everybody has an **Account** page (Blazor and WPF) to change their own names (user name, display name and the aliases,
one per line) and their password, each part with its own button; a button is only enabled while pressing it would change something
the API accepts (valid names that differ from the current ones; the current password, a new one of 12–128 characters that differs from
it, typed twice). Whoever may see the users gets a **Users** page (Blazor and WPF; in the navigation only for them): the list of
users with their display name, aliases and roles, creating a user, editing the names, resetting the password (leave it empty to keep it)
and, with `users.grant`, choosing the roles and the own permissions, and deleting a user after a question. The page offers only what the
user may do and the API will accept: nobody deletes themselves, and the only user with the administrator role cannot give it up. Somebody who changes their own login there (or on the
Account page) stays signed in: the WPF client keeps signing in with the name it used as long as that is still the user name or an
alias (otherwise it takes the user name) and with the new password, and re-saves the login if it was remembered; the browser holds the
old login and asks again, which the page says whenever the user name, the password or an alias was changed or removed (changing only
the display name or adding an alias does not need it).
Everything is also available over HTTP (in development the API docs at `/scalar` offer the login):

```bash
curl -u admin:… -H "Content-Type: application/json" -X POST https://planner.example.org/api/users \
     -d '{ "userName": "anna", "password": "…", "displayName": "Anna K.", "aliases": ["annie"], "roleIds": [2] }'
```

- **WPF** first asks for the **address of the API** (a dialog on the first start; it is the user's own setting, in
  `%LocalAppData%\Kyuplan\api-address.txt`, not in `appsettings.json`), then shows a sign-in window before the main window.
  The sign-in window shows the address and has a **Change …** button that opens the address dialog again; the program restarts with
  the new address. *Remember me on this computer* keeps the login in
  `%LocalAppData%\Kyuplan\login.dat`, **encrypted with the Windows Data Protection API for the current Windows user**
  (another user, or another computer, cannot read it), together with the API address it belongs to: it is never sent to a different
  server. The next start signs in with it by itself; if the API no longer accepts it, it is forgotten and the window opens.
  *Sign out* (bottom left) forgets it and asks again.
- **Blazor** has no login page and **cannot remember a login**. The API answers `401` with `WWW-Authenticate: Basic`, so the
  browser shows **its own login dialog**, keeps the login for the browser session and sends it with every further call
  (the client asks the browser to include credentials; the API allows credentials for `Api:AllowedOrigins`). Cancelling the dialog
  leaves the "not signed in" message with *Try again*, which asks again. Each browser decides for itself whether it shows that dialog for a
  call to *another origin* (the Blazor dev server and the API are two origins); if yours does not, serve the Blazor client and the API from one origin (for example behind one reverse proxy).
  After changing your own user name or password (or taking away the alias you signed in by) the browser still holds the old login and asks again on the next call; the
  Account page says so. (A page cannot replace the login a browser keeps, and the web client deliberately never touches it.)

## Look

- **Blazor**: flat and ruled, like a printed score sheet. Stone-grey page, white sheets, 1px rules, small radii, no
  shadows. Titles and figures use an old-style serif (system fonts Palatino / Iowan Old Style /
  Noto Serif — nothing is downloaded), everything else the platform's own sans. The instance's own colour, the
  *accent*, is set in the API (see [Branding](#branding)) and is used for links, focus, selection **and** for what means
  *attended / meets the bar*; amber means *open / below*; red is kept for trouble.
- **The required attendance is one line.** In the member table a single vertical line runs through the whole attendance
  column at the required percentage and is named once in the header, so the table reads as *who has crossed the line*.
  Both clients draw it.
- **WPF** looks like a Windows 11 app on purpose: system title bar, Mica pane, WinUI-style cards and navigation, Segoe
  UI Variable and Segoe Fluent Icons, and **the instance's accent colour from the API** (not the one of Windows) for
  buttons, selection, focus, links, tick boxes and sliders, and for what means *attended / qualified* in the charts.
  Fluent has the Windows accent baked into about 70 brushes, so `ThemeService.UseAccent` replaces every one of them with a shade
  of the API's colour (`AccentShades`: darker for fills and text on a light theme, lighter on a dark theme, like Windows makes
  them from its own accent); the "attended" brushes are then taken from those. Until the API has answered (or when it cannot be
  reached) the app keeps the Windows accent. The other data colours (open, declined, trouble) are in
  `Themes/Palette.Light.xaml` / `Palette.Dark.xaml`; everything else comes from the Fluent theme.
  `Application.ThemeMode` is still flagged experimental in .NET 10, hence `<NoWarn>WPF0001</NoWarn>`.
- **Themes (both clients).** `ThemeRules` (Contracts) lists them per kind of client (`web`, `windows`): `default`, the general ones
  `dracula`, `catppuccin-latte`, `catppuccin-mocha`, `liquid-glass` in both clients, and `windows10`, `aero`, `native` in the Windows client
  only. The theme in effect is the user's own (`UserAccount.WebTheme` / `WindowsTheme`, set with `PUT /api/account/theme`, so it follows the
  user to another computer), else the administrators' default per kind of client (`BrandingDto.DefaultWebTheme` / `DefaultWindowsTheme`:
  `Branding:DefaultWebTheme` / `DefaultWindowsTheme` in appsettings, overridden by what the Branding page saves, public like the rest of
  the branding). `ThemeViewModel` (Client.Shared) decides which one it is and raises `Changed`; the clients only draw it. Web: `js/theme.js`
  sets `data-skin` on `<html>` (remembered in `localStorage` only as a copy, so the next visit paints in it at once), and `app.css` has one
  block of variables per skin (Dracula/Mocha dark only, Latte light only, Liquid Glass both, with a gradient field, translucent panels and
  `backdrop-filter`); the accent stays the instance's. WPF: see below.
- **WPF designs.** Every theme belongs to a design family: **default** is Windows 11 (or Windows 10 on older Windows), the Windows
  themes are their own families, Dracula and both Catppuccins are `Design.Modern.xaml` (flat classic controls, rounded) with one
  `Colors.*.xaml` each, Liquid Glass is `Design.Glass.xaml` with `Colors.Glass.Light/Dark.xaml` (see-through white over a gradient; WPF
  cannot blur what is behind). `ThemeService.Adopt(theme)` remembers the API's answer in `design.txt` (so the next start paints in it before
  it can ask) and swaps the colours live inside a family; another family needs a restart (at login silently, after the user's own choice
  after asking). The families: **Windows 11** (the Fluent theme, `Application.ThemeMode`),
  **Windows 10** (flat, square, 2 px frames, light/dark), **Windows Vista / 7 (Aero)** (gradients, 3 px corners, glass buttons,
  scroll bars with arrows, dark-blue headings, light only) and **native** (`ThemeMode.None`: WPF's own controls in system colours).
  The designs differ in shapes, fonts and control templates, not only colours, and the pages are the same XAML in all of them:
  the views ask for Fluent's resource names (`TextFillColorPrimaryBrush`, `AccentButtonStyle`, `BodyStrongTextBlockStyle`,
  `DefaultButtonStyle`, `ControlCornerRadius`, ...) and every `Themes/Design.*.xaml` provides the same names (`DesignContractTests`
  checks it) plus the app's own tokens (`AppNavPaneBrush`, `AppPillCornerRadius`, `AppTab*`, ...). Windows 10 and Aero share one set of
  templates, `Themes/Controls.Classic.xaml`, that reads shapes (`CtlBorderThickness`, `ControlCornerRadius`, ...) from the design file
  and brushes (`Ctl*`, solid or gradient) from `Colors.Win10.Light/Dark.xaml` / `Colors.Aero.xaml`; what depends on the accent colour is
  made by `ThemeService.MakeAccentBrushes` (flat on Windows 10, glossy on Aero). Because some pages base styles on the design's styles
  with `StaticResource`, the design is loaded once at start and changing it restarts the program; light/dark switches at once.
  `App.xaml` has no resources on purpose: `ThemeService.Initialize` loads the design, then `Styles.xaml`, then the colours.
  A new design = a `Design.<x>.xaml` with the whole contract, an entry in `ThemeService`, a string in both resx files.

## Branding

How an instance presents itself (its name, the club's name, an accent colour, logos and the tab icon) is **set in the API**, in
the `Branding` section of its settings, so one build of each client serves any club and a second instance needs no rebuild. The
clients ask for it at start-up (`GET /api/branding`, open to everybody: the login screen shows it before anybody has signed in)
and carry nothing of their own.

| Key | Default | Meaning |
| --- | --- | --- |
| `Branding:ProductName` | `kyuplan` | Name of the application: navigation, window and tab titles. |
| `Branding:OrganizationName` | empty | Name of the club, shown after the product name in titles ("kyuplan · Club") and as the logo's text. The demo data's main group is named after it, too. |
| `Branding:DefaultWebTheme` / `DefaultWindowsTheme` | `default` | Theme of the web / the Windows client for users who picked none (`default`, `dracula`, `catppuccin-latte`, `catppuccin-mocha`, `liquid-glass`; the Windows client also `windows10`, `aero`, `native`). Administrators change it on the Branding page. A value that is no theme of that client is logged and replaced by `default`. |
| `Branding:AccentColor` | `#2563eb` | Accent colour of **both** clients (`#RGB` or `#RRGGBB`): links, focus, selection, buttons, the loading bar, and what means *attended / meets the bar*. The darker tint used for text and the pale one used for backgrounds are derived from it. |
| `Branding:Logo` / `LogoOnDark` | none | Logo for light / for dark surfaces: a path to an image file, **the same one for both clients**. An **SVG** (sharp at any size) is the best choice; PNG, JPEG, GIF and BMP work, too. Without `LogoOnDark` the light logo is used on dark surfaces, too. (There used to be `WebLogo` / `WebLogoOnDark` for an SVG that only the browser could draw; they are gone, and a setting of that name is ignored: put the SVG in `Logo` / `LogoOnDark`.) |
| `Branding:Icon` | none | Icon of the browser tab (SVG, PNG or ICO). |

A relative path is relative to the folder of the API's program files (in the Docker image `/app`); give an absolute path for files
on a mounted volume. A setting that cannot be used (the file does not exist, is no image, the colour is none) is **logged when the API
starts and ignored**; it never stops the API. The image files are read again for every request, so replacing one needs no restart; the
settings themselves are read at start. An image the browser has shown is checked again with the API every time (`ETag`), and the
clients notice a replaced file by themselves (see below), so a changed logo shows up within half a minute.

### Changing the branding as an administrator

These settings are the **basis**. An administrator can change them without touching the server: both clients have a **Branding** page
(in the navigation, **only for those with `branding.view`**; `/branding` in the browser; changing needs `branding.edit`) with the name, the organisation, the accent colour and the
three images. What is saved there lives in the API's database (`kyuplan.db`, with the logos as they were uploaded: back it up
with the rest) and **applies instead of the settings**.

- **Texts and colour** are saved together with *Save*, which is only enabled while the form differs from what is saved and is valid.
  The accent colour can be typed (`#RGB` / `#RRGGBB`) or picked: the browser opens its colour picker, the desktop client offers a row of
  swatches next to the box (any other colour is typed).
- **The images** are saved with a *Save* of their own, like the texts: *Upload …* / *Replace …* picks a file and *Remove* takes the image
  away (only enabled while the slot shows one), and the slot shows how it will look, marked *Not saved yet*, with *Undo* to drop that
  change. Nothing reaches the API before *Save* (which is only enabled while some slot was changed); it sends the changed slots one
  after the other, and if one is refused the ones before it are saved and the rest stay picked. A file that is none of the formats the
  slot takes, or too large, is refused when it is picked. *Remove* means "none", also where the settings name a file; a slot without an image of its own falls back
  as described above (a missing dark logo shows the light one). The API looks at the bytes to see what an upload is, accepts only the
formats the slot is meant for (for the logos SVG, PNG, JPEG, GIF and BMP: what both clients draw) and at most 1 MB. The desktop
  client picks the file in a file dialog and shows the previews itself; for an image it cannot draw (an SVG that refers to files
  outside of itself, see below) it says "no preview here" instead.
- ***Back to the settings of the API*** forgets everything saved (after a confirmation: the uploaded images are gone) and the settings
  apply again. Until something is saved the page says so ("Nothing was saved here").
- **Clients follow a change by themselves.** Both ask the API for the branding every 30 seconds (a small public request, also while
  the login window is open) and re-dress when the answer differs: the name, the window and tab titles, the logos, the tab icon (in the
  desktop client the icon of its windows) and the accent colour (every colour of the browser page, every Fluent brush of the desktop
  client) change without a reload or a restart. The
  administrator's own client shows a change at once, without waiting for the next look. A client that cannot reach the API keeps what it
  shows and carries on when the API is back. (The image addresses carry a version, `api/branding/logo?v=...`, which changes with the
  image: that is how a swapped logo is noticed even though its address is the same. Not followed: the icon of the program
  file (in Explorer, on a shortcut) and the installer, and the title bar and window border of the desktop client, which are Windows'.)
- The demo data's group keeps being named after `Branding:OrganizationName` of the settings, not after the one saved on the page.

- **The settings that ship in `appsettings.json` are placeholders**: the name "My Club", a blue accent and a generic crest (the files in
  `assets/branding`, which the API project copies into its build and publish output). **Every real instance overrides
  them** with an `appsettings.Production.json`, user secrets or environment variables (`Branding__OrganizationName`, ...), and must
  override *every* key it does not want to inherit: an empty value (`Branding__Logo=`) switches an image off.
- The **web client** shows the logo for the current theme (light/dark), the name, the accent colour, the tab title and icon. Before it
  has loaded it fetches the branding by itself (`js/brand.js`) so that even the splash screen is dressed.
- The **WPF client** shows the same name, logos, window titles and accent colour (see [Look](#look); the title bar and window
  border are drawn by Windows and keep its accent colour where Windows is set to tint them). It draws the same logo files as the
  browser, **SVG included**: WPF cannot, so the desktop client renders an SVG itself with Skia (the engine of the browsers; the
  package `Svg.Skia`) to a bitmap of 1024 pixels on the longer side, which stays sharp at any size the window shows it in. An SVG
  that refers to anything outside of itself (`<image href="file:...">`, `url(https://...)`, `@import`) is **not drawn** there: a
  browser does not load such references for an image either, and an administrator's upload must not make every desktop fetch a
  file or a web address. Without a logo it shows just the name. A very light accent colour leaves the white text of a button little contrast, so pick a mid-tone or dark one.
  **The icon of its windows** (title bar, taskbar, Alt+Tab) is the tab icon of the web client. It is made at the sizes Windows asks
  for (16 to 256 pixels; an SVG from its vector art, any other image shrunk with averaging, an uploaded ICO as it is), so it stays
  sharp at 16 pixels. Without an icon, or while it is still downloading, the windows have the icon of the program file. Like the
  logos it is drawn as it is: an SVG that adapts to the browser's colour scheme with `prefers-color-scheme` (the placeholder
  `favicon.svg` does) shows its light-mode colours, so its dark ink is hard to see on a dark title bar or taskbar.
- **Fixed at build time**, not served: the icon of the program file and of the installer (`assets/branding/app.ico`, the placeholder crest) and the
  publisher name (`Authors` in `Directory.Build.props`, `AppPublisher` in `installer/Kyuplan.iss`). Change those when you
  build the WPF client for your own club.
- `tools/Render-Branding.ps1` (needs Microsoft Edge) renders the program icon `assets/branding/app.ico` from the tab icon
  `favicon.svg`; run it after changing the icon and commit the result. The logos need no rendering: both clients draw the SVGs.

## Releases and deployment

The whole framework has one version and is released by pushing one tag `v<version>`; the version of the tag (without the `v`) becomes the version of the API, both clients and the installer.

| Tag | Result |
| --- | --- |
| `v0.1` | Docker image `ghcr.io/<owner>/kyuplan` (`linux/amd64` and `linux/arm64`): the API, the web client **and the installers of the desktop clients** in one container, opened at the same address (settings as environment variables, e.g. `Spond__Email`; data in `/data`) + GitHub release with the installers attached |

```bash
git tag v0.1
git push origin v0.1
```

Deploy that one container and set up the rest in the web client. The web client offers the desktop app for the visitor's system for download (a button in the left panel), and an installed client offers an update button when the server carries a newer version. There is one installer per operating system (`Kyuplan-<platform>-<version>.<ext>`); today that is Windows.

**Where it runs**: anywhere a Linux container runs: plain Docker or Compose, TrueNAS, Azure, AWS, Google Cloud, Kubernetes, a Raspberry Pi, ... The **database is SQLite by default** (nothing to set up), or **PostgreSQL**, either next to the application in a second container (files in a local volume or folder) or an independent server (your own or a managed one). Ready-made Compose files are in [`deploy/`](deploy/). Every option, the extra steps each one needs (the `citext` extension, TLS, a reverse proxy for HTTPS, one replica only, no SQLite on network shares, ...), backups, updates and troubleshooting are described in [docs/deployment.md](docs/deployment.md#where-and-how-to-run-it-overview), together with the release workflows and the installer.

```bash
docker run -d -p 8080:8080 -v kyuplan-data:/data ghcr.io/<owner>/kyuplan:latest      # SQLite; open http://localhost:8080/
docker compose -f deploy/docker-compose.postgres.yml up -d                          # with its own PostgreSQL (set POSTGRES_PASSWORD in deploy/.env)
```

## Tests

```bash
dotnet test
```

The API tests run the real API in memory against the demo data source and SQLite databases that live in memory too
(`InMemoryDatabase`: one for the planner, one for the accounts), so the exam and user endpoints run against the real schema, unique
indexes and cascade (the belt tables, the belt and member endpoints, the exam planning and the sync with Spond are tested the same way, against a Spond the test controls; the rules of who is fit are tested without a database); the test clients sign in as a known administrator (`TestAccounts`). The WPF and Blazor views are not covered by
automated UI tests; the view models behind them are, including the German texts, the language switch and the sign-in. The WPF
client's encrypted login file (`DpapiLoginStore`) and its navigation (`MainViewModel`: a page is in it for somebody who may use it) are tested too: their source files are compiled into the test project, because the WPF project cannot be referenced
from it.

## Security and privacy

- The API returns **names and attendance of club members, including children**. Every call needs a login (see
  [Accounts and signing in](#accounts-and-signing-in)); serve it over **HTTPS only** and give every person their own account.
  Over plain HTTP the API refuses logins from other machines (`Api:AllowInsecureHttp` lifts that, on a network you trust only).
  Failed logins are counted: `Api:MaxLoginFailuresPerAddress` (10) from one address or `Api:MaxLoginFailuresPerUser` (30) as one
  user within `Api:LoginFailureWindow` (15 minutes) lock that address or name out for the rest of the window (answer 429; behind a
  reverse proxy this needs `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, else every visitor looks like the proxy). The Spond passwords in
  `settings.json` are encrypted; the key lives in `Setup:KeyDirectory` (default: `keys` next to the settings file, so keep that folder
  out of the data backups, e.g. a separate volume). Every response carries a content security policy and the usual browser protection
  headers. Requests for a host name other than `AllowedHosts` (asked on the first start, changeable on the settings page) are refused.
  Databases can only be moved next to the settings file on the settings page. `Api:ApiKey` is an extra layer at most: a key in the
  Blazor client's `wwwroot/appsettings.json` is visible to every visitor of that site.
- The first administrator's password is written to the log **once** when no password is configured: treat that log line as a
  secret, change the password, and do not ship such logs elsewhere. It is in the log **files** too (`Logging:File:Path`), which
  stay until they are rotated out after 31 days: delete the line, or the file, once you have changed the password.
- The API only **reads** from Spond. What it stores itself is the in-memory cache and, in its SQLite database, exams and
  saved filters (the exams' event ids, names and dates and the filter settings: group and subgroup ids, thresholds, search terms), the
  belts, and **the members: their names, their belt and the day they got it, the day they started, their birth date, for every exam who was
  invited to it and what they answered, who was saved for it and who received which belt** and **the comments users wrote about them, with their history** (the members include children; the birth
  date is taken over from Spond, which is what makes the age a belt asks for checkable). That database now holds personal data: protect and back it up like the accounts database, and do not
  copy it to places the club's Spond group is not meant to reach. What a user may do with that data is up to their permissions (see
  [Permissions and roles](#permissions-and-roles)): give them only what they need; a comment and the number of comments on a member are for those with `comments.view`.
- The accounts database holds user names, aliases, display names and password hashes, nothing else.
- The clients remember the last filter on the user's own machine (local storage / user profile); it holds the same settings. The WPF
  client can also remember the login there, encrypted for the Windows user; the Blazor client leaves the login to the browser.
- Keep the Spond password in user secrets or environment variables, never in a committed `appsettings` file.
