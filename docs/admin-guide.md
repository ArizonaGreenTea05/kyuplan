# Administrator guide

This guide is for the people who **run** a kyuplan instance after it has been deployed: first setup, Spond connection, users and
roles, the club's data, branding, day-to-day operations and problem solving. How to get the server running is in the
[deployment guide](deployment.md); how the pages are used is in the [user guide](user-guide.md).

- [Responsibilities at a glance](#responsibilities-at-a-glance)
- [First setup](#first-setup)
- [Connecting Spond](#connecting-spond)
- [Settings page and configuration files](#settings-page-and-configuration-files)
- [Users, roles and permissions](#users-roles-and-permissions)
- [Preparing the club's data](#preparing-the-clubs-data)
- [Branding](#branding)
- [Desktop clients and updates](#desktop-clients-and-updates)
- [Day-to-day operation](#day-to-day-operation)
- [Security and privacy](#security-and-privacy)
- [Troubleshooting](#troubleshooting)
- [Checklists](#checklists)

## Responsibilities at a glance

| Task | Where |
| --- | --- |
| Create the first administrator, connect Spond | Setup screen (first visit), later the **Settings** page |
| Create users and give them roles | **Users** and **Roles** pages |
| Define the belts and their requirements | **Belts** page |
| Give the club its name, colour and logo | **Branding** page |
| Back up, update, watch the log | the server (see [Day-to-day operation](#day-to-day-operation)) |

Administrators are ordinary users with the built-in **administrator role**. There is no separate "admin flag": every page and every
change is guarded by a **permission**, and the administrator role has all of them.

## First setup

A brand-new installation has no user, and every call needs a login. Therefore the web client shows a **setup screen** the first time it
is opened. **Whoever opens the page first creates the administrator**, so do the setup right after deploying and do not expose a new
installation to the internet before it is done. Once a user exists, the setup answers `409` for good.

The setup screen asks for:

| Field | Meaning |
| --- | --- |
| **Settings file** | Where the settings are kept (`data/settings.json`; fixed, and shown read-only, when the deployment pins it). |
| **Planner / Accounts database** | The two SQLite files (only with SQLite; with PostgreSQL the connection strings are environment settings). Keep them as two different files. |
| **Spond** | Email (or phone number) and password of the Spond account, or **Use demo data** to try kyuplan out. |
| **Time zone**, **Exam keyword**, **default minimum attendance**, **default minimum invited events** | See [Statistics settings](#statistics-and-exams). |
| **Synchronisation** | Whether to sync with Spond automatically and how often. |
| **Allowed host names** | The proposal is the address you opened. Requests to other host names are refused. |
| **Administrator** | User name and password (12 to 128 characters, typed twice). |

Press **Finish the setup**. kyuplan creates the administrator, saves the settings and, when a setting needs a restart, restarts itself
inside its own process (the page waits and then loads the application; the browser asks for the new administrator's login).

**Headless alternative**: set `Accounts__InitialAdminPassword` (and optionally `Accounts__InitialAdminUserName`, default `admin`)
before the first start. The administrator is then created without the setup screen. The setting is only used while there is no user at
all and can be removed afterwards. Without it, the setup screen creates the administrator.

**What a new database contains.** To save typing, a *new* database starts with default data (once; existing databases are never
touched): the **19 standard belts** (9th Kyu to 3rd Dan, with colours, minimum ages and waiting times) and three roles. Nothing personal:
no members, exams, comments, users or branding. Set `DefaultData__Enabled=false` to start empty.

After the setup, sign in and do, in this order:

1. [Change the administrator password if you do not like it](#users-roles-and-permissions) (Account page) and create a **personal account
   for yourself** if the first one was a shared one.
2. Check that Spond works: the **Members** page should fill within a minute; the **Exams** page lists exams after you add them.
3. [Create roles and users](#users-roles-and-permissions).
4. [Prepare the belts and members' data](#preparing-the-clubs-data).
5. [Set the branding](#branding).

## Connecting Spond

kyuplan only **reads** from Spond (groups, members, events and answers). Use a **dedicated Spond account that is an administrator of
the groups to analyse**; the account sees the groups it belongs to.

- **Two-factor authentication is not supported** for that account: the server logs in unattended.
- Use `Email`, or `PhoneNumber` if the account logs in by phone.
- **Backup accounts.** Spond sometimes answers an account that sent too many requests with nothing at all. On the Settings page you can
  add *Backup Spond accounts*; when the first yields no groups or events (or its login is rejected), the next is asked. An account that
  came back empty while another delivered is asked last for 10 minutes (`Spond:AccountCooldown`). All accounts must see the same groups;
  the log says which account was skipped.
- **Ignore group administrators.** Group administrators (the members with `Respondent = false` in Spond, usually the login account) are
  members like everybody else and appear in statistics, the member list and exam planning. Tick *Ignore group administrators* to leave
  them out; those already in the database are removed by the next sync together with their belt and exam data.
- **Caching.** Events are fetched month by month and cached for 15 minutes (`Spond:CacheDuration`). The **Refresh** button in the clients clears the
  cache. The server logs in again every 6 hours (`Spond:LoginLifetime`).
- The Spond password is stored **encrypted** in `settings.json`; the key is in the `keys` folder next to it. Keep that folder safe and, if
  you share backups, keep the key apart from the settings file (`Setup__KeyDirectory`).
- **Demo mode**: *Use demo data instead of Spond* serves generated data for a fictional club (the clients show a "Demo data" marker). Good
  for training users, never for real use.

### The automatic sync

The server keeps its copy in line with Spond by itself: when it starts and then every 15 minutes (`Sync:Interval`; `0` = only at start;
`Sync:Enabled=false` turns it off). It

- **adds** new Spond members, **renames** renamed ones and **removes** members that left (with their belt, comments, saved-for-exam
  entries and results),
- sets a member's **start day** to the day of the first event they attended, while none is set,
- takes over a member's **birth date** from Spond while none is set,
- keeps the **invitations** of every exam equal to the Spond event,
- **never** touches belts, belt days, comments, who was saved for an exam, or recorded results.

A sync that cannot reach Spond is logged and skipped; the data stays as it is. Because Spond has an id per group, somebody who is in two
groups is two members. Only events within the search window (12 months back, 6 ahead by default) are looked at.

## Settings page and configuration files

Administrators with `settings.view` / `settings.edit` change the same values later on the **Settings** page. They are written to
`data/settings.json` (`/data/settings.json` in the container), which is read **on top of** `appsettings.json`.

Precedence, highest first: **command line → environment variables → settings.json → appsettings.json**. A value pinned by an
environment variable cannot be changed on the page (a container that pins `Database__ConnectionString` keeps it).

A save that changes something **restarts the application inside its process** (no service manager needed; the container keeps
running); a save that changes nothing does not. A file that is not valid JSON is ignored (and said so on the console).

### Statistics and exams

| Setting (Settings page / key) | Default | Meaning |
| --- | --- | --- |
| Time zone (`Statistics:TimeZone`) | `Europe/Berlin` | Spond delivers UTC; days, weekdays and months are read in this zone. Use an IANA name. |
| Exam keyword (`Statistics:ExamKeyword`) | `Prüfung` | Events whose name contains this word (case-insensitive) count as exams in the statistics' preselection. Empty = the statistics open with the last six months. |
| Minimum attendance (`Statistics:DefaultMinAttendancePercent`) | 75 | Preselected threshold and default for exam suggestions. |
| Minimum invited events (`Statistics:DefaultMinInvitedEvents`) | 3 | Preselected minimum number of invitations. |
| `Statistics:DashboardDays` / `UpcomingDays` | 90 / 14 | Dashboard look-back and look-ahead. |
| `Statistics:EventSearchMonthsBack` / `…Ahead` | 12 / 6 | The window (1 to 24 months) in which events are searched and offered, for exams, tags and the sync. Widen it if members started earlier or exams are far out. |

### Security settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `AllowedHosts` | `*` (setup proposes your address) | Host names the server answers to, separated by `;`. **Change it when the address changes**, otherwise the site answers `400 Invalid Hostname`. |
| `Api:AllowInsecureHttp` | `false` | Accept logins over plain HTTP from other machines. Only on a network you trust completely. |
| `Api:MaxLoginFailuresPerAddress` / `PerUser` / `LoginFailureWindow` | 10 / 30 / 15 min | Lockout after failed logins (`429` for the rest of the window; `0` = no limit). Behind a reverse proxy this needs `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. |
| `Api:ApiKey` | empty | Optional extra header `X-Api-Key` on all `/api` calls. Only an extra layer: anyone who can open the web client can read it from there. |
| `Api:AllowedOrigins` | `[]` | Only for a web client hosted at another origin (development). Not needed with the integrated one. |

The complete list of keys is in the [README's configuration table](../README.md#configuration).

## Users, roles and permissions

### The model

- A **user** has one **user name** (the main login), up to ten **aliases** (more names that sign in as the same user with the same
  password) and a **display name** that other users see (for example on comments; it is the user name when left empty).
- A **permission** is one thing a user may do. A **role** is a named set of permissions. A user may do everything their own permissions
  **and** all their roles' permissions allow, put together.
- Every page needs its `.view` permission, and every change a permission of its own. Buttons for things a user may not do are hidden;
  the server enforces it with `403` on every request, so a change applies from the user's **next request** on.
- Everybody who is signed in can use their own **Account** page, read the server info and press *Refresh*.
- A **new user has no role and no permission** (just their own account) until somebody with `users.grant` gives them some.

### The permissions

| Domain | Permissions |
| --- | --- |
| dashboard | `dashboard.view` |
| statistics | `statistics.view` |
| calendar | `calendar.view` (the Calendar page, the event detail, the banner of an event in progress; writing a comment there needs `comments.write`) |
| exams | `exams.view` (page, suggestions, saved filters), `exams.edit` (add/delete exams), `exams.filters` (save/replace/delete saved filters), `exams.candidates` (save members for an exam), `exams.results` (record belts handed out) |
| members | `members.view`, `members.edit` (belt, belt day, start day, birth date) |
| belts | `belts.view`, `belts.edit` |
| comments | `comments.view`, `comments.write`, `comments.edit` (change any comment), `comments.delete` (own), `comments.deleteAny` |
| tags | `tags.view`, `tags.edit` |
| branding | `branding.view`, `branding.edit` |
| users | `users.view`, `users.edit` (create users, names, passwords), `users.delete`, `users.grant` (give roles and permissions) |
| roles | `roles.view`, `roles.edit` |
| settings | `settings.view`, `settings.edit` |

`calendar.view` came with the calendar: the default roles of a **new** installation have it, but a role that already exists in your database does not until you tick it on the Roles page (administrators have every permission).

Ticking a permission in the forms also ticks the `.view` of its domain; unticking `.view` unticks the rest of the domain.

Comments are deliberately **separate from members**: somebody who may view members does not see comments, nor how many exist, without
`comments.view`.

### Built-in and default roles

- **Administrator** (built in): every permission, also those added in later versions. It cannot be changed or deleted. The **last user who
  has it can neither lose it nor be deleted.** The first user created on the first start has it.
- On a new database three ordinary, editable roles are seeded:

  | Role | May |
  | --- | --- |
  | **Leading Trainer** | Dashboard, statistics, calendar, run exams (add, filters, candidates, results), edit members and belts, write/edit/delete own comments, manage tags. Not: delete others' comments, users, roles, branding, settings. |
  | **Trainer** | Look at everything (dashboard, statistics, calendar, exams, members, belts), write and edit comments, delete own, manage tags. Changes no exam, member or belt. |
  | **Passive User** | Look only: dashboard, statistics, calendar, exams, members, belts, comments and tags. |

  They are a starting point: change them like any other role. Instances upgraded from the time when an "admin flag" existed keep
  working: administrators got the administrator role, everybody else a role *User* with what they could do before.

### Everyday tasks

**Create a user** (Users page → **New user**): user name, initial password (12 to 128 characters), display name, aliases, and, with
`users.grant`, roles and own permissions. Tell the user their password through a safe channel; they can change it on their Account page.

**Reset a password**: edit the user and type a new password (empty = keep).

**Change what many people may do**: edit the **role** (Roles page). It applies to everybody who has it from their next request.

**Give one person extra rights**: tick *Own permissions* on their user. Prefer roles for anything several people share.

**Delete a user**: their login (and aliases) stops working at once. Their comments stay, authored by the display name they had. Nobody can
delete themselves; the last administrator cannot be deleted.

**Delete a role**: it is taken from its users; their other roles and own permissions stay.

### Rules to know

- User names and aliases are **login names**: unique across all users and aliases regardless of case, at most 64 characters, no colon
  or control characters. (The uniqueness between names and aliases is checked in code, not by an index; two requests racing for the same
  name at the same instant could both succeed. Create users one at a time.)
- **Nobody can give or touch more than they have.** Roles and permissions given to a user, and permissions added to a role, must all be ones the
  giver has themselves. Changing a user's names or password, or deleting a user, needs at least the permissions that user has (so somebody
  with `users.edit` cannot reset an administrator's password). Only an administrator (or somebody with every permission) can do all of it.
  Still hand `users.grant` and `roles.edit` out with care: they let a user hand out everything they have.
- A permission that no longer exists in the catalogue (written by an older version) is ignored.
- Changing your **own** login in the desktop client keeps you signed in. In the browser you will be asked to sign in again.

### Using the API with a script

Everything the pages do is available over HTTP with Basic authentication. In development the API documentation is at `/scalar`.

```bash
curl -u admin:… -H "Content-Type: application/json" -X POST https://kyuplan.example.org/api/users \
     -d '{ "userName": "anna", "password": "…", "displayName": "Anna K.", "aliases": ["annie"], "roleIds": [2] }'
```

## Preparing the club's data

The Exams page can only judge members for whom the data exists. An administrator (or a leading trainer) prepares it once:

1. **Belts** page: check the standard belts against the club's exam rules. For each belt, the **previous belts** (one of them must be
   held) with the **minimum age (years)** and **minimum waiting time (months)** for coming from that previous belt. There is no rule that
   a belt needs a previous one; the first belt has none. Belts must not require each other in a circle. A belt cannot be deleted while
   members hold it, other belts require it, or it was handed out at an exam. Colours are `#RRGGBB`; the stripe is optional.
2. **Members** page: for each member set the **belt**, the **day they got it** and (if Spond has none) the **birth date**. The *start
   day* is worked out by the sync. Members without these show up as *Needs data* in exam suggestions.
3. **Exams** page: add the Spond events that are belt exams. For past exams, record who received which belt, **newest exam first**, or set
   the members' belt days first (see [exam-planning.md](exam-planning.md#a-past-exam-who-attended-and-who-received-which-belt)).
4. **Attendance Statistics** page: define and save the filters you want to use for exam suggestions.
5. **Tags**: create a few topics for comments, if useful.

**Naming exams** so that statistics find them: with `Statistics:ExamKeyword=Prüfung`, an event is an exam for the *statistics' preselection*
when its name contains the keyword. Exams *on the Exams page* are the events someone added there, whatever their name.

## Branding

The name, the club name, the accent colour and the logos of the instance come from the **Branding** page (permission `branding.view`
to look, `branding.edit` to change). It affects both clients.

- **Texts and colour**: product name (navigation, window and tab titles), organisation name, accent colour (`#RGB` or `#RRGGBB`: type it
  or pick it; used for links, focus, selection, buttons and for what means *attended / meets the bar*). Choose a mid-tone or dark colour:
  a very light accent leaves white button text with little contrast. **Save** is only active while the form differs and is valid.
- **Default themes**: the theme the web client and the Windows client show to users who have not picked one themselves (every user can
  pick their own in the navigation; *Server default* there follows this setting). Dracula, Catppuccin Latte/Mocha and Liquid Glass exist in
  both; Windows 10, Vista / 7 and native WPF only in the Windows client. Saved with the texts and the colour. The API's settings
  `Branding__DefaultWebTheme` / `Branding__DefaultWindowsTheme` are the basis until this is saved.
- **Images**: *Logo* (on light surfaces), *Logo on dark* (optional; the light one is used if missing) and *Icon* (browser tab and, in the
  desktop app, the window icon). Prefer **SVG**; PNG, JPEG, GIF and BMP work for logos, PNG/SVG/ICO for the icon; at most **1 MB** each.
  The server looks at the file's bytes, not its name. The desktop app does not draw SVGs that refer to outside files or web addresses;
  it shows "no preview" instead. Pick files with *Upload …*, check the preview, then **Save**; *Undo* drops a pending change; *Remove*
  takes an image away.
- **Back to the settings of the API** forgets everything saved on the page (the uploaded images are gone) and the deployment's
  `Branding__*` settings apply again.
- What is saved here lives in the database (back it up with it) and **applies instead of** the settings (`appsettings` / environment
  variables `Branding__ProductName`, …). If a setting seems to have no effect, look at this page first.
- Open clients pick up a change within **30 seconds**, without reload or restart.
- **Not changeable at runtime**: the icon of the program file and the installer, and the publisher name. They are built in; see the
  [developer guide](developer-guide.md#branding-your-own-desktop-build).

## Desktop clients and updates

The server image carries the Windows installer. The web client shows a **Download for Windows** button for the visitor's system, and
installed desktop apps check the server every hour and offer an **Update to x.y.z** button when the server's version is newer. So
**updating the server updates the clients** (users click the button). Nothing has to be distributed by hand.

Silent installation for managed PCs: `Kyuplan-windows-1.2.3.exe /VERYSILENT /SUPPRESSMSGBOXES`. The installer does not know the server
address; each user enters it at first start (it is kept in `%LocalAppData%\Kyuplan\api-address.txt`). The installer is not code-signed, so SmartScreen
warns on download. Users' local data (address, language, theme, last filter, remembered login) stays after uninstalling.

## Documentation for your users

The documentation is part of the server. **Help** in both clients opens the [user guide](user-guide.md) for everybody; this administrator guide is offered to users who have one of
`settings.view`, `users.view`, `roles.view` or `branding.edit`, and the deployment guide to users with `settings.edit`. Giving or taking away those permissions therefore also decides who
sees what; there is nothing to configure. Details and the archive attached to every release: [deployment guide](deployment.md#the-documentation).

## Day-to-day operation

**Backups.** What matters is the data folder (`/data` in the container):

- SQLite: both databases (`kyuplan.db` and `accounts.db`, with their `-wal` and `-shm` files; simplest is to stop the container while
  copying), the settings file and the `keys` folder.
- PostgreSQL: `pg_dump` of the database(s), plus `settings.json` and `keys`.
- The planner database holds **personal data of members including children, and the comments**; the accounts database holds the password
  hashes. Treat the backups accordingly (encrypted, access controlled) and keep them within the club's data-protection rules.

Details and commands are in the [deployment guide](deployment.md#backup-restore-and-updates). **Test a restore** at least once.

**Updates.** Pull the new image tag and recreate the container. The database migrations run automatically when the server starts, so
**back up first**, and never start a database with an *older* version than the one that last migrated it. Releases pin
`1.2`/`1.2.3`; `latest` moves with every release. Read the GitHub release notes.

**Logs.** The server writes to the console (`docker logs kyuplan`) and to daily files `logs/kyuplan-<date>.log` (31 are kept). Failed
logins are logged with user name and address, never the password. The log also tells which Spond account was skipped and why a sync failed.

**Health.** `GET /healthz` answers `200` without a login while the database is reachable (`503` otherwise). The Docker image uses it as
its health check. Only **one** instance may run.

**Time zone.** The container runs in UTC; the club's zone is `Statistics:TimeZone`.

**Reset to a clean start.** Stop the container and delete (or move) the database files (with SQLite; with PostgreSQL use an empty
database); the next start creates a new database with the default data and, with no user, shows the setup screen again. This destroys
data: back up first.

**Moving between SQLite and PostgreSQL** is not automated: switching starts with an empty database (members come back from Spond;
belts of members, exams, comments and users must be entered again).

## Security and privacy

- The server shows **names and attendance of members, including children**, and stores their belts, birth dates and comments. Give every
  person their own account and only the permissions their job needs; `comments.view` is the permission that reveals the notes.
- **Use HTTPS.** The login is HTTP Basic: the password travels with every request. Terminate TLS at a reverse proxy and set
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. Over plain HTTP the server refuses logins from other machines unless
  `Api:AllowInsecureHttp` is set (trusted networks only).
- **Lockout**: 10 failed logins from one address, or 30 for one user name, within 15 minutes block that address or name for the rest of the
  window.
- Responses carry a content security policy and the usual protection headers; requests for host names not in `AllowedHosts` are refused.
- Passwords are stored as salted, stretched hashes; the Spond password in `settings.json` is encrypted with a key in `keys/`.
- Comments are never rendered as HTML, so nothing a user types can inject anything.
- kyuplan only reads from Spond and never writes to it.
- **The club's data protection duties** (informing members/parents, a legal basis for storing birth dates and notes, deletion on request)
  are for the club to settle; kyuplan removes members who leave Spond, together with their data, at the next sync. Members who leave Spond
  take their comments with them. There is no export of one person's data other than reading the database.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| Setup screen does not appear | A user already exists (or `Accounts__InitialAdminPassword` created one). Sign in. |
| No members / no events | Spond login wrong or two-factor on; look at the log for the Spond lines; add a backup account; the account must belong to the groups. Press **Refresh**, then wait for a sync. |
| Group is empty after some days | Spond sometimes answers nothing when it was asked too often: add *backup Spond accounts*. |
| Only some events appear / an exam cannot be added | The event is outside the search window (default 12 months back / 6 ahead): widen `EventSearchMonthsBack`/`…Ahead`. |
| Wrong day for events | `Statistics:TimeZone` is not the club's zone. |
| `400 Invalid Hostname` | `AllowedHosts` does not contain the address in use. Fix it (environment variable, or the Settings page if you can still reach it). |
| `400 HTTPS required` | Plain HTTP from another machine. Use HTTPS via a proxy (and the forwarded-headers variable), or `Api__AllowInsecureHttp=true` on a trusted network. |
| `429` on login | Lockout after failed logins; wait out the window. Behind a proxy without forwarded headers *every* visitor counts as one address. |
| A user sees no pages | They have no role. Give them one (Users page, needs `users.grant`). |
| A setting "does not stick" | It is pinned by an environment variable or the command line; the page cannot override it. |
| Branding looks different from the settings | The branding saved on the Branding page applies instead; use *Back to the settings of the API*. |
| A belt cannot be deleted | Members hold it, other belts require it, or it was handed out at an exam. The tooltip says which. |
| Exam suggestions say *Needs data* for everybody | No belts/belt days/birth dates yet; see [Preparing the club's data](#preparing-the-clubs-data). |
| Container `unhealthy`, database errors, `Permission denied` on `/data` | See the [deployment troubleshooting table](deployment.md#troubleshooting). |
| Works twice / syncs twice | More than one instance is running. Scale to one. |

## Checklists

**After installing**

- [ ] Setup finished, administrator can sign in
- [ ] HTTPS works from outside; `AllowedHosts` is the real address
- [ ] Spond data appears (Members page); demo data is *off*
- [ ] Backup running; a restore was tested
- [ ] Personal accounts and roles created; the initial admin password changed
- [ ] Belts checked; members' belt, belt day and birth date entered
- [ ] Branding set
- [ ] Users told the address and how to get the desktop app

**Before each update**

- [ ] Read the release notes
- [ ] Backup (database(s), `settings.json`, `keys`)
- [ ] Pull the new tag, recreate the container, check `/healthz` and the log
- [ ] Sign in, open Exams and Members, press *Refresh*

**When somebody leaves the club's staff**

- [ ] Delete their user (or take their roles away)
- [ ] If they had `users.grant` / `roles.edit` / administrator, review the other users and roles
- [ ] If they knew a shared password (Spond account, initial admin), change it
