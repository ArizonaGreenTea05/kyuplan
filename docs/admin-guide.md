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
| **Data source** | Where groups, members and events come from: **Spond**, **Custom (script)**, **Manual** or **Demo data**; see [Data sources](#data-sources). |
| **Spond** | (Data source Spond.) Email (or phone number) and password of the Spond account. **Subgroup of members not registered yet** (`Spond:UnregisteredSubGroup`): name (or id, case-insensitive) of the Spond subgroup that holds the people who are in the group but not registered in the club yet; the Members page shows them as "not registered yet", and each sync keeps this in line with Spond. Empty, or no such subgroup in a group = everybody is registered. |
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

## Data sources

Everything kyuplan analyses (groups, subgroups, members, events and who answered what) comes from one **data source**, which you choose on the
Settings page (**Data source**; `DataSource:Kind`). Saving a change restarts the server, which takes a few seconds. All sources deliver the same
information, so every page works the same with each of them.

| Data source | Where the data comes from |
| --- | --- |
| **Spond** | A Spond account; see [Connecting Spond](#connecting-spond). |
| **Custom (script)** | A JavaScript you write that reads the data from any service; see [Custom data source](#custom-data-source). |
| **Manual** | Users with the permission *Enter groups, members and events by hand* (`manualdata.edit`) keep everything on the **Manual data** page; see [Manual data](#manual-data). |
| **Demo data** | Generated data of a fictional club, for training (`Spond:UseDemoData`, the older switch, also turns it on). |

Whatever the source, the members, belts, exams, comments and ratings kyuplan keeps itself stay in its own database. **Changing the source
changes the ids of groups, members and events**: members, exams and filters that belong to groups the new source does not know are removed by
the next sync. Switch the source once, at the beginning, not back and forth; take a [backup](#backups) before you do.

### Custom data source

With **Custom (script)** the Settings page shows a text box for a script. It defines two functions, which the server calls whenever it needs
the data (what they return is cached for 15 minutes; the **Refresh** button drops the cache):

```js
// All groups, with their subgroups and members.
function getGroups() {
  return [{
    id: "g1", name: "Dojo",                              // ids may be strings or numbers; they must stay the same from call to call
    subGroups: [{ id: "k", name: "Kids" }],
    members: [{ id: "m1", name: "Anna", subGroupIds: ["k"], birthDate: "2014-05-01" }]   // subGroupIds and birthDate are optional
  }];
}

// The events of one group that end between from and to (ISO 8601 texts). Return more if you like; the server filters again.
function getEvents(groupId, from, to) {
  return [{
    id: "e1", name: "Training",
    start: "2026-10-01T16:00:00Z", end: "2026-10-01T17:30:00Z",   // ISO 8601 (without an offset: UTC), a Date or epoch milliseconds
    cancelled: false,
    responses: { m1: "accepted", m2: "declined" }       // every invited member: accepted | declined | unanswered | other. Not listed = not invited
  }];
}
```

The button **Insert example script** puts a complete example into the box. Inside the script you have:

- `http.get(url, headers)`, `http.post(url, body, headers)`, `http.put(url, body, headers)` and `http.delete(url, headers)`. Each waits for the answer
  and returns `{ status, ok, text, json() }`. A `body` that is not text is sent as JSON. Only `http://` and `https://` addresses work, an answer may
  be at most 10 MB, and one request is given up after 30 seconds. A call of the script as a whole is stopped after 60 seconds, a request that is
  still waiting included.
- `console.log(...)`, which writes to the server's log.
- Everything of plain JavaScript (ECMAScript), and nothing else: no file system, no network but `http`, no .NET.

> **Who can reach what.** The requests are made **by the server**, from its network position, and follow redirects. A script can therefore reach addresses only the server
> can: other containers, the internal network and the like. Loopback (`localhost`) and link-local addresses (`169.254.x.x`, the cloud metadata service)
> are refused unless `DataSource:AllowLocalAddresses` is `true`; every other address is open. Only give the permission to change the settings to people you trust with that, and never paste a script you did not read.
> Credentials in the script are stored in `data/settings.json` and shown to everybody who may open the Settings page.

Rules to know:

- The script runs in a fresh, sandboxed engine for every call, one call at a time, and is stopped after `DataSource:ScriptTimeout` (60 seconds), at
  128 MB of memory or at 300 nested calls. A script that fails (an exception, a result that is not as described above) makes the pages show the
  error message and the server log the details; the rest of kyuplan keeps working with what it has.
- **Treat the script like code of the server.** Whoever may save the settings (`settings.edit`) can make the server send requests to any address it
  can reach, including addresses inside your network. Keep that permission to people you trust. Tokens in the script are stored in `settings.json`
  as plain text, so protect that file; the script is also part of backups of the settings file.
- The settings page checks the **syntax** when you save; whether the functions return the right things shows when the data is read the next time.
  Try a change on a copy first, e.g. with `DataSource__Script` in the environment of a test container.
- The members of a group are identified by the `id` you return. Keep ids stable, or members are seen as new people (belts and comments are lost
  with the old ones at the next sync).

### Manual data

With **Manual** nobody needs an outside service: the club keeps its groups on the **Manual data** page. The page is in the navigation of users who have the
permission `manualdata.edit`; give it to a role on the Roles page (administrators have it anyway).

1. **Group**: add the first group (a Spond "group" is a squad, a dojo, a team ...). Rename or delete it with the buttons next to the group
   picker. Deleting a group deletes its subgroups, members and events, and the planner forgets the belts, comments and exam data of those members.
2. **Subgroups** (tab): classes or teams inside the group, e.g. *Kids*, *Adults*. Statistics and exams narrow by them.
3. **Members** (tab): name, date of birth (the belt rules need it) and the subgroups they are in.
4. **Events** (tab): trainings, exams and other dates, with start and end in the club's time zone. In the form, invite everybody or nobody
   (then tick who is invited) and choose what each answered: *Accepted*, *Declined*, *No answer* or *Waiting list*. **All invited came**
   marks everybody invited as accepted, the quick way to record a training: *accepted* counts as attended (see
   [attendance statistics](attendance-statistics.md)).

Every change is saved at once and the members and exams are brought in line right away, so the other pages show it immediately; no waiting for
the next sync. The page tells you when the data source is not Manual (what you enter is then not used).

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
- **Demo mode**: the data source *Demo data* serves generated data for a fictional club (the clients show a "Demo data" marker). Good
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
| Stars an event counts without evaluation (`Statistics:DefaultEventStars`) | 2.5 | What an event counts, in the statistics and for exams, for a member who attended it and whom no user evaluated (0 to 5). |
| Minimum average evaluation for exams (`Statistics:ExamMinAverageStars`) | 3 | The average star evaluation a member needs to be suggested for an exam (0 to 5; 0 = not asked). With the defaults, members nobody has evaluated are not suggested. |
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
- The **Users** page shows each user's **status**: *Online* (a request with their login in the last five minutes; an open but idle client
  may therefore show as offline), *Last online* with the date and time, or *Never online*. The time is noted at most once a minute.
- A **new user has no role and no permission** (just their own account) until somebody with `users.grant` gives them some.

### The permissions

| Domain | Permissions |
| --- | --- |
| dashboard | `dashboard.view` |
| statistics | `statistics.view` |
| calendar | `calendar.view` (the Calendar page, the event detail, the banner of an event in progress; writing a comment there needs `comments.write`), `calendar.feeds` (define calendar feeds, see [Calendar feeds](#calendar-feeds)) |
| exams | `exams.view` (page, suggestions, saved filters), `exams.edit` (add/delete exams), `exams.filters` (save/replace/delete saved filters), `exams.candidates` (save members for an exam), `exams.results` (record belts handed out) |
| payments | `payments.view` (the Exam prices page, the payments of every exam), `payments.edit` (record what members paid), `prices.edit` (change the general fee and the stamp fee and belt price per belt) |
| members | `members.view`, `members.edit` (belt, belt day, start day, birth date) |
| belts | `belts.view`, `belts.edit` |
| comments | `comments.view`, `comments.viewOwn` (read the comments about the members one is oneself), `comments.write`, `comments.edit` (change any comment), `comments.delete` (own), `comments.deleteAny` |
| ratings | `ratings.view` (see every user's star evaluations of members), `ratings.viewOwn` (read the evaluations of the members one is oneself), `ratings.write` (evaluate members at an event, change and take back one's own) |
| tags | `tags.view`, `tags.edit` |
| branding | `branding.view`, `branding.edit` |
| themes | `themes.edit` (install and delete theme packs) |
| backups | `backups.view` (the page, the backup databases and their state), `backups.edit` (define, change, delete and run backup databases), `backups.download` (download a backup file: it holds every password hash), `backups.restore` (replace all data with a backup file) |
| users | `users.view`, `users.edit` (create users, names, passwords), `users.delete`, `users.grant` (give roles and permissions) |
| roles | `roles.view`, `roles.edit` |
| settings | `settings.view`, `settings.edit` |

`ratings.view` and `ratings.write` came with the star evaluations: the Leading Trainer and Trainer roles of a **new** installation have both, the Passive User only `ratings.view`; a role that already exists in your database has neither until you tick them on the Roles page. Until somebody may evaluate, no member has an evaluation and, with the standard minimum of 3 stars for exams, nobody is suggested for an exam: lower *Minimum average evaluation for exams* (0 switches it off) if that is not what you want yet.

`calendar.view` came with the calendar: the default roles of a **new** installation have it, but a role that already exists in your database does not until you tick it on the Roles page (administrators have every permission).

Ticking a permission in the forms also ticks the `.view` of its domain; unticking `.view` unticks the rest of the domain. The `.viewOwn`
permissions are the exception: they work without `.view` and are not touched by it.

### Users who are members

A user can also be a member of the club. On the **Users** page, edit the user and tick the **Member** (or members: a person in two Spond groups
is two members) they are; the Member column of the list shows who is whom. A member can be one user's only (409 otherwise), and a user who is
deleted stops being the member.

What is written **about a member** is then hidden from the user who is that member, whatever else they may read:

- **Comments** (members page, calendar event): `comments.view` shows the comments about everybody **except oneself**; to read the comments about
  oneself a user needs `comments.viewOwn`. The number of comments on a member in the member list follows the same rule, and a comment about
  oneself cannot be changed by somebody without `comments.viewOwn` (the answer would reveal what it said before).
- **Star evaluations** (calendar event, `GET /api/members/{id}/ratings`): the same with `ratings.view` and `ratings.viewOwn`.
- The two "own" permissions work **without** the general ones: a user with only `comments.viewOwn` reads the comments about themselves and
  nobody else's. Where they are shown: on the pages the user may open anyway (Members, Calendar) and on the **Account** page, section *About
  you*, which needs no page permission (it only shows what the "own" permissions allow, for the members the user is).
- Not affected: the averages of the statistics and the exam suggestions (they are totals, not what individual users wrote); writing comments
  and evaluations about oneself (`comments.write`, `ratings.write` are not restricted).

Existing roles do not have the new permissions until you tick them on the Roles page; users who are not linked to a member are not affected.

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

**Say who is a member**: edit the user and tick the member(s) they are, see *Users who are members* above. Needs `users.edit`.

**Reset a password**: edit the user and type a new password (empty = keep).

**Change what many people may do**: edit the **role** (Roles page). It applies to everybody who has it from their next request.

**Give one person extra rights**: tick *Own permissions* on their user. Prefer roles for anything several people share.

**Delete a user**: their login (and aliases) stops working at once. Their comments stay, authored by the display name they had. Nobody can
delete themselves; the last administrator cannot be deleted.

**Delete a role**: it is taken from its users; their other roles and own permissions stay.

### Rules to know

- User names and aliases are **login names**: unique across all users and aliases regardless of case, at most 64 characters, no colon
  or control characters. (A unique index in the database guarantees it, so even simultaneous requests cannot take one name twice.)
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
- **Currency**: the unit of all money (exam prices and payments), a symbol such as `€` or a code such as `CHF`, shown after every amount.
  The amounts themselves are not converted: changing it only changes the label.
- **Default themes**: the theme the web client and the Windows client show to users who have not picked one themselves (every user can
  pick their own in the navigation; *Server default* there follows this setting). Dracula, Catppuccin Latte/Mocha and Liquid Glass exist in
  both; Windows 10, Vista / 7 and native WPF only in the Windows client. Saved with the texts and the colour. The API's settings
  `Branding__DefaultWebTheme` / `Branding__DefaultWindowsTheme` are the basis until this is saved.
- **Theme packs** (below the images; anybody who may see the page sees the list, `themes.edit` installs and deletes): see
  [Theme packs](#theme-packs). The installed packs are also offered as default themes in the lists above.
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

### Theme packs

More themes come as **theme packs**: a small JSON file of colours that adds a theme to **both** clients. Every installed pack is offered to
all users (in their personal settings) and can be made the default theme above. Users who had chosen a pack that is deleted later see the
default theme again; nothing else has to be cleaned up.

The program ships with packs of its own (they show the format and cannot be deleted or replaced): three **high contrast** themes for people
who see less (*High contrast (dark)*, *High contrast (light)*, *High contrast (yellow on black)*: text with at least 7:1 contrast, strong
borders, thick focus rings, underlined links) and some popular palettes (Nord, Solarized dark/light, Gruvbox dark/light, Tokyo Night).

To add one, use **Install theme pack ...** on the Branding page (permission `themes.edit`) and pick the file; a pack with the id of an
installed one replaces it. Or put `<id>.json` into the `themes` folder next to `settings.json` (in the container: `/data/themes`; the
folder can be moved with `Themes__Directory`): it is picked up at once, without a restart, and a file that is no valid pack is skipped
(and named in the log). The file:

```json
{
  "id": "ocean",
  "name": "Ocean",
  "nameDe": "Ozean",
  "scheme": "dark",
  "highContrast": false,
  "accent": null,
  "colors": {
    "ground": "#0b1d2a", "sheet": "#10293b", "sheetAlt": "#17364d", "sidebar": "#08151f",
    "text": "#e6f1f8", "muted": "#9db8ca", "rule": "#1f4560", "ruleStrong": "#3d6a8a",
    "amber": "#f2c14e", "amberWash": "#3a3015", "red": "#ff8f85", "redWash": "#3b1c1c"
  }
}
```

- `id`: lower-case letters, digits and single hyphens, at most 32 characters; not the id of a built-in theme (`default`, `dracula`, `nord`, ...).
  It is also the file name.
- `name`, `nameDe` (optional): what the lists show, at most 40 characters; users of the German interface see `nameDe` if there is one.
- `scheme`: `light` or `dark`. A pack has one scheme, so the light/dark button is off while it is in use.
- `highContrast`: `true` adds strong borders, thick focus rings and underlined links, and demands text contrast of 7:1 instead of 4.5:1.
- `accent` (optional, `#RRGGBB`): the colour of links, focus, selection and buttons. Without it the accent of your instance is used. High
  contrast themes carry their own, since the colour has to suit their surfaces.
- `colors` (all twelve, `#RRGGBB`): `ground` (behind everything), `sheet` (pages, cards, boxes), `sheetAlt` (alternate rows, hovered
  things), `sidebar`, `text`, `muted` (secondary text), `rule` (thin lines), `ruleStrong` (borders of boxes and buttons), `amber` /
  `amberWash` (*below the bar / open*: text and background tint), `red` / `redWash` (trouble).

The server refuses a pack whose text is hard to read, and says why: `text` needs a contrast of at least 4.5:1 on `ground`, `sheet` and
`sidebar` (7:1 for a high contrast pack), `muted` at least 3:1 on `sheet` (4.5:1), an `accent` at least 3:1 on `sheet`. A file is at most 16 KB, and
at most 50 packs can be installed. The desktop app draws a pack with its flat controls (the same colours, the rounded look of Dracula), so
installing one never needs a new version of either client.

## Calendar feeds

A calendar feed is a calendar of your own that calendar apps (Google Calendar, Apple Calendar, Outlook, Thunderbird, ...) subscribe to as an *internet calendar*. Open **Administration → Calendar feeds** (needs `calendar.feeds`; a role that exists already must get it ticked on the Roles page) to make, change and delete them, copy the address and give a feed a new one. The same is available through the API (`/api/calendar-feeds`, see `/scalar` in development). A feed has a name and one or more **sources**; its entries are all the sources' entries together.

| Source `kind` | Entries |
| --- | --- |
| `events` | The events of a Spond group (its search window, cancelled ones only with `includeCancelled`). |
| `birthdays` | Every member's birthday as an all-day entry titled `🎂 Name (age)`. |
| `clubAnniversaries` | The day a member started in the club, titled `🏅 Name (years)`. |
| `beltAnniversaries` | The day a member got the belt they hold, titled `🥋 Name: Belt (years)`. |

Every source names its `groupId` and can be narrowed to **people**: `subGroupIds` (Spond subgroups of the group) plus `memberIds` (ids from the members list) are put together; with neither, everybody of the group counts. For `events`, `invitation` says how the selected people must be invited: `any` (at least one, the default), `all` (all of them: "every event that invites a, b and c") or `only` (nobody else: "the events of this subgroup alone"). Events can also be narrowed with `nameContains` / `nameExcludes` (words separated by commas, like the statistics filter) and `onlyExams` (events that are exams in the planner). `label` puts a word in front of each title (`Kids: Training`). `subGroupLabels` (subgroup id → word) does the same per selected subgroup: an entry gets the word of every subgroup it concerns (an event that invites someone of it, a member's birthday), after `label`: `Kids / Teens: Training`.

Example: kids' trainings and the kids' birthdays in one calendar.

```json
{
  "name": "Kids",
  "sources": [
    { "kind": "events", "groupId": "…", "subGroupIds": ["…"], "invitation": "only", "label": "Kids" },
    { "kind": "birthdays", "groupId": "…", "subGroupIds": ["…"] }
  ]
}
```

The answer carries `url`, e.g. `https://planner.example.org/calendar-feeds/<token>.ics`: that is the address to paste into the calendar app. **It needs no login**, because calendar apps cannot send one: the secret token in the address is the key, so treat the address like a password. `POST /api/calendar-feeds/{id}/regenerate` gives a feed a new token (the old address stops working), deleting the feed ends it. Calendar apps ask again every few hours (the file suggests one); the entries are made from Spond and the member data at each request. Birthdays and anniversaries are written for last year, this year and next year, so the age in the title is right; a member without the date is skipped. Mind who may see a feed: birthdays name members, and with `https` only the address is secret.

## Backups

Open **Administration → Backup**. It has two parts; each has its own permission (see the table above), because a backup file holds **every user's password hash** and the personal data of the members.

### Backup file: download and restore

*Download backup* (`backups.download`) gives you one file (`kyuplan-backup-<date>.kyubackup`, a zip) with everything in **both databases**: members, exams, comments, evaluations, tags, belts, the branding as saved on the Branding page, and all users with their roles, aliases and password hashes. It does **not** contain the settings file (`settings.json`, which says where the databases are and holds the Spond logins), the `keys` folder or uploaded theme packs: those are files, not data; copy them as described in the [deployment guide](deployment.md#backup-restore-and-updates).

The file is independent of the database system: a backup of a SQLite server restores into a PostgreSQL one and back.

**Encryption (optional).** Type a *passphrase* (at least 8 characters) above the buttons before you download, and the file is encrypted (AES-256-GCM, key derived with PBKDF2): without the passphrase it cannot be read, and a wrong passphrase or a damaged file is refused. Restoring an encrypted file needs the same passphrase in the same field. **A lost passphrase cannot be recovered.** For a new server (`Backup:InitialDataFile`) set `Backup:InitialDataPassphrase` as well. The mirror databases below are not encrypted (they are normal databases: protect them like the live one).

*Restore from file…* (`backups.restore`) **replaces all data on the running server** with the content of a backup file. The page asks you to confirm first; nothing happens before that. Then:

- the file is checked completely before anything is changed: a file that is no backup, is damaged, or was made by a **newer** version of the program than this server is refused and nothing changes;
- both databases are replaced, each in one transaction (if writing fails, that database keeps its old data);
- the server restarts by itself (a few seconds) so that it starts from the new data; the page waits for it to come back.

Everything that is not in the file is **lost**, including users created since: after a restore you can only sign in with the users the backup had. **Download a backup before you restore one.**

**A new server from a backup.** A new server has no data to replace, so it cannot be restored through the page (nobody can sign in yet). Put the file on the server and set `Backup:InitialDataFile` (in a container: `Backup__InitialDataFile=/data/start.kyubackup`). When both databases are **created** by that start, their data is loaded from the file instead of the default belts and roles, and the setup screen does not appear because the users come with the file. Once the databases exist the setting is ignored, so it can stay.

### Backup databases

A *backup database* is a **second database** that the server keeps a complete copy of all data in, by itself, once every 15 minutes, hour, 6 hours, 12 hours, day or week (your choice per backup database; you can define up to 20). Every update **replaces** what the backup database held; it is a mirror, not a history. Use the *Download* button (and your own file backups) if you need older states.

| Kind | Location | What it holds |
| --- | --- | --- |
| SQLite files in a folder | a folder on the server (relative to the program folder, or absolute; created if missing) | `planner.db` and `accounts.db` |
| PostgreSQL database | a connection string, e.g. `Host=backup-db;Database=kyuplan_backup;Username=kyuplan;Password=...` | the tables of both databases (they do not clash) |

- **Never choose the live database.** The server refuses a folder that holds its own SQLite files or its own PostgreSQL database. Pick a different disk, share or server so that a failure of the first does not take the copy with it.
- The kinds are independent of the live database's system: a SQLite server can mirror into PostgreSQL and the other way round.
- **Restore test.** After every update (scheduled or *Update now*) the server reads the whole backup database back, as a restore would, and compares it table by table with what it wrote. A mismatch or an unreadable database makes the run **fail** with the reason, so a mirror that could not be restored shows up in the list instead of on the day you need it.
- **Update now** runs one at once. The list shows when each one was last updated and, for a failed run, the reason (database unreachable, wrong password, folder not writable). A failed run is tried again at the next interval; a switched-off backup database is not updated.
- A backup database is a normal database of this program: to switch to it, point `Database:ConnectionString` / `Accounts:ConnectionString` at it (it carries the current schema, and the server brings it up to date when it starts).
- The definitions are kept in the file `backup-targets.json` next to the settings file (`Backup:TargetsFile` moves it); the locations are stored encrypted, because a connection string holds a password. They are not part of a backup file.
- `Backup:Enabled=false` stops the automatic updates (downloads, uploads and *Update now* keep working).

The backup databases and files protect against losing the server's disk or a wrong change. They are **not** a replacement for testing your restore: restore a backup file on a spare instance once.

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

The Backup page (see [Backups](#backups)) adds a downloadable backup file of both databases and mirror databases that are updated by themselves; the file does not replace copying `settings.json` and `keys`.

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
