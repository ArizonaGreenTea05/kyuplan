# User guide

This guide is for the people who **use** kyuplan every day: trainers, exam organisers and anybody who looks things up. It explains
what each page shows and how to do the common jobs. Everything an administrator sets up (users, roles, data source, branding) is in the
[admin guide](admin-guide.md).

- [What kyuplan is](#what-kyuplan-is)
- [Getting started](#getting-started)
- [Finding your way around](#finding-your-way-around)
- [Dashboard](#dashboard)
- [Attendance Statistics](#attendance-statistics)
- [Exams](#exams) (the start page)
- [Members](#members) and [comments](#comments)
- [Belts](#belts)
- [Tags](#tags)
- [Your account](#your-account)
- [Language, theme and other settings](#language-theme-and-other-settings)
- [Frequently asked questions](#frequently-asked-questions)

## What kyuplan is

kyuplan is the planner for the club's belt exams. It reads the club's groups, members and events from a data source (for example [Spond](https://spond.com)) and answers three questions:

1. **Who is fit for the next exam?** From a member's belt, the day they got it, their age and their attendance, kyuplan tells who may
   take the next belt, and who is missing what.
2. **Who attended an exam and who received which belt?** You record the results, and the belt each member holds is kept up to date.
3. **Who trains regularly?** The attendance analysis shows how many of the events they were invited to each member accepted, plus
   figures that show how engaged each member is.

It is used in a **web browser** and, on Windows, as an **installed desktop app**. Both show the same pages and work the same way.

> **What "attended" means.** The data source records *answers to invitations*, not who actually stood in the hall. kyuplan counts a member as
> having **attended** an event when they **accepted** the invitation. Somebody who accepted and stayed at home counts as present;
> somebody who came without answering does not. Keep this in mind when you read the figures.

## Getting started

### You need

- the **address** of your club's kyuplan server (for example `https://kyuplan.example.org`),
- a **user name and password** from your administrator.

You can only see and do what your administrator gave you permission for. If a page below is missing from your menu, you do not have
the permission for it; ask your administrator.

### In the browser

1. Open the address. The browser shows its own **sign-in dialog**: enter your user name and password.
2. The planner opens on the **Exams** page.

kyuplan has no login page of its own and does not remember your login: the browser keeps it until you close the browser. If you
cancel the dialog, the page says that you are not signed in and offers **Try again**.

### In the desktop app (Windows)

1. Install it. Either use the **Download for Windows** button at the bottom left of the web page, or the installer your administrator
   gives you (`Kyuplan-windows-<version>.exe`). It installs for you alone and needs no administrator rights. Because the installer is not
   code-signed, Windows may warn: choose *More info* → *Run anyway*.
2. At the first start the app asks for the **address of the server** (once; it is remembered for your Windows account).
3. The sign-in window follows. Enter your user name and password. Tick **Remember me on this computer** to be signed in automatically
   next time: the login is stored encrypted and can only be read by your own Windows account, on this computer.
4. To use another server, sign out and press **Change …** next to the server address in the sign-in window.
5. **Updates**: when the server has a newer version, an **Update to x.y.z** button appears at the bottom left. A click downloads and
   installs it, and the app restarts by itself.

### Where the data comes from

Your administrator decides where the groups, members and events come from: from Spond, from another service, or from entries made in kyuplan itself.
If your role has the permission to enter data by hand, the **Manual data** page appears in the navigation; see the
[administrator guide](admin-guide.md#manual-data) for how it works (pick or add a group, then keep its members, subgroups and events; events
record who was invited and who came).

### Demo data

If the page shows a **Demo data** marker, the server serves generated sample data for a fictional club, not your real groups.
Your administrator uses this to try things out.

## Finding your way around

- **Navigation** (left): the pages you may use. The pages are: Exams, Dashboard, Attendance Statistics, Calendar, Members, Belts, Tags, and
  your Account. Administrators additionally see Users, Roles, Settings and Branding.
- **Group**: the club's group whose data you look at. Choosing another group reloads the page. (Most clubs have one group, with
  subgroups such as children and adults.)
- **Refresh**: kyuplan keeps the data for about 15 minutes. **Refresh** throws that away and asks the data source again. Use it after
  you changed an event or a member there and want to see it now.
- **Buttons are only active when they would change something.** *Save* is grey until you changed something valid, *Select the fit* is
  grey when everybody fit is already ticked, and so on. If a button is grey, it has nothing to do (the tooltip often says why).
- **Dangerous actions ask first.** Deleting an exam, a filter, a belt, a tag or a comment always asks for confirmation.
- Buttons for things you may not do are hidden.
- **Event banner**: while an event of the selected group is going on (it has started and is not over, cancelled events do not count), a banner
  at the top of **every** page says so. Choosing it opens the event on the Calendar page. It checks every minute.
- **Keyboard and screen readers.** Everything can be used without a mouse. In the browser, the first press of **Tab** shows *Skip to main
  content*, which jumps over the navigation to the page. *Administration* and *Account* open when they have the focus (or on a click or
  pointing at them); **Escape** closes the menu again, **Arrow down** (or **Enter**) opens it and moves into it. The page you are on is
  announced as the current page. Every field, icon button and progress bar has a name that a screen reader reads out; the desktop app
  offers the same names through Windows' UI Automation. Colours: *Account → Preferences* has high-contrast themes.
- **Help** (navigation, in the desktop app at the bottom): opens this guide in the browser. You only see the guides that apply to you: administrators additionally get
  *Help for administrators* (and, if they may change the settings, *Help for installing*). The browser asks for your login if it does not know it yet.

## Calendar

The **Calendar** page shows the events of the selected group as in any calendar app: **Day**, **Week** (Monday to Sunday) or **Month**, with
*Previous*, *Next* and *Today*. Cancelled events are struck through. The **trainers** of an event are in the tooltip of the event (and in the list of the web client's day and week view). The calendar only knows the events kyuplan reads from the data source (about the
last year and the next six months).

Choosing an event opens its **detail**: when it takes place, who its **trainers** (hosts) are, how many accepted, declined or did not answer, and **everybody invited** with their
answer. Below each member you see the comments written about them **for this event** and, if you may write comments, a box to add one. A
comment written there is **tagged automatically** with the event: kyuplan uses the tag that is connected to the event, and creates one
(named after the event and its day) if there is none yet. The comment is an ordinary comment on the member; you find it on the Members page, and
the tag on the Tags page. A member who kyuplan does not know yet (they were invited since the last synchronisation with the data source) cannot be
commented on until the next synchronisation.

### Evaluating members

If you may evaluate members, each member in the detail of an event that has started has the buttons **0 1 2 3 4 5**: the stars you give that member
for **this event**. You have one evaluation per member and event: choose other stars to change it, **Take back** to remove it. **0 stars is an
evaluation** (a poor one, shown in red); a member nobody has evaluated says *Not evaluated*. Everybody who may read evaluations sees the
evaluations of all users for the member, each with the user's name, and their average.

The stars count in the statistics (a *Stars* column) and for exams: a member needs an average of at least 3 stars (the club can change this) to be
suggested. An event counts **once** for a member, however many users evaluated them, and an attended event nobody evaluated counts 2.5 stars (the
club can change this, too). See [Star evaluations](attendance-statistics.md#star-evaluations).

## Dashboard

A quick picture of how the group is doing over the last **30 / 90 / 180 / 365 days** (90 by default; you choose at the top):

- **Headline figures**: how many members meet the required attendance, the overall attendance and response rate, the average number of
  attendees per event.
- **Upcoming events** of the next two weeks, with how many members are coming, declined or have not answered yet.
- **Most engaged**: the five members with the highest engagement score.
- **Needs attention**: members at risk of dropping out or whose attendance is falling, lowest score first.

The exact definition of each figure is in [attendance-statistics.md](attendance-statistics.md).

## Attendance Statistics

This page answers one question: **who attended at least a given share of the events they were invited to, in a given time span?**

### The filters

| Filter | What it does |
| --- | --- |
| **Minimum attendance** (slider) | The share of invitations a member must have accepted to count as *qualified*. A single line runs through the whole attendance column at this value, so you can see at a glance who is over it. |
| **From event / To event** | The time span, given as two events picked from a searchable list (it shows the date first). The page opens with the **previous exam and the next exam**; a group without such a pair opens with the last six months. Both end days are included, so the previous exam itself counts. |
| **Members** | Tick any number of subgroups. Nothing ticked = everybody. |
| **Invited to at least** | Members invited to fewer events than this get no verdict ("too few events"). |
| **Advanced filters** | The time span as **dates** (with quick ranges such as "last 3 months") instead of events, **Only events containing** (e.g. `Training`) and **Ignore events containing** (e.g. `Prüfung, Turnier`, to leave exams, tournaments and parties out of a "training attendance" question). Several terms are separated by `,` or `;`. |
| **Find a member**, **Show** | Search by name and filter by verdict on the result. These are not part of a saved filter. |

Tips for searching events: type a part of the name (`prufung` finds *Gürtelprüfung*) or the date however you write it: `26.9.`,
`2026-09`, `sat`, `march`. Arrow keys and Enter pick an entry.

### What you see

For each member: **attendance**, **answer rate** (how often they replied at all), **current and longest streak**, **last attended**,
**sessions per week**, the **outcome of the last 10 invitations**, a **trend** (rising / falling) and an **engagement score** from
0 to 100. The verdict is:

- **Qualified**: at least the required share of their invitations accepted.
- **Below**: enough events, but under the required share.
- **Too few events**: invited to fewer events than the minimum; no verdict.

Further down: how attendance is spread over the members, attendance by month, and attendance per event.

> A member is only judged against events they were **invited to**. Somebody who joined three months ago is not punished for the year
> before, and a child is not measured against the adults' training.

### Saved filters

kyuplan **remembers the filter you used last** and opens the page with it. You can also **save** a filter under a name and attach it to
an exam. This is how you tell the Exams page *how to measure attendance* for that exam.

1. Set up the filter the way you want it (for example from the previous exam to the next exam, 75 %, at least 3 invitations).
2. Open **Saved filters** below the filter fields and press **Save as …**. kyuplan proposes the exam your time span ends at and a name
   like *Filter for Gürtelprüfung on 26 Sep 2026*. Both can be changed. For a new exam you pick the event that is the exam.
3. Later, choose the filter from **Saved filter** to load it. **Reload** drops the changes you made since; **Update** overwrites the saved
   filter with what is on screen; **Delete** removes it.

## Exams

The **start page**, and the reason the rest exists. It lists the exams of the selected group: those **to come** first (the next one at
the top and selected when you open the page), then the past ones.

### Adding an exam

An exam is an **event of the data source**, not something you type in. Press **Add exam** and choose the event (type part of its name or date to
find it). If the group has subgroups you are also asked *for which subgroups the suggestions are* (nothing ticked = everybody); this can
only be chosen now. The exam's name and date follow the event if it is renamed or moved there. **Delete exam** removes the exam, its
saved filters, the members saved for it and the belts recorded at it. Nothing changes in the data source, and the belts members hold stay as they
are.

### An exam that is still to come: who is fit?

Every member is judged for **the belt after the one they hold**, **on the day of the exam** (somebody who turns 12 before the exam is 12
for it). Three things are checked:

| Check | Met when |
| --- | --- |
| **Attendance** | The statistics call the member *qualified* with the filter you chose (see below). |
| **Age** | The age in full years on the exam day is at least the minimum age of the next belt. |
| **Waiting time** | The full months from the day the belt was received to the exam day are at least the minimum waiting time. |

Each member gets a verdict:

| Verdict | Meaning |
| --- | --- |
| **Fit** | Everything is met. |
| **Not yet** | Something is missing; the row says what ("Waited 1 months by the exam day, 3 needed."). |
| **Needs data** | Nothing is missed, but a check cannot be made: no belt, no birth date, or no idea when the belt was received. The row says what is missing, and you fix it on the [Members](#members) page. |
| **Top belt** | Nothing left to take. |

Fit members are listed first, then those who need data, then those who are not fit yet.

**Measure attendance with** chooses how attendance is counted:

- one of your **saved filters** (see above), or
- the **default**: from the previous exam to this exam, with the default minimum attendance, for the subgroups chosen when the exam was
  added. The page says in one sentence what was used. The link *Define a filter on the Attendance Statistics page* takes you there.

**Putting members forward.** Tick the members you want to propose (or press **Select the fit**), then **Save selection**. The selection
is stored **in kyuplan only**: **kyuplan cannot invite anybody.** Invite the saved members in the data source yourself. After that, the
*Answer* column shows their answer and the page counts how many of your saved members are **not invited yet**, so you can invite all
of them in one go. *Discard changes* drops the ticks you have not saved; *Clear selection* unticks everybody.

### A past exam: who attended, who received which belt

The page lists those who **accepted** the invitation (plus anybody who was given a belt anyway). Next to each attendee, a list holds the
belt they **received**. Choosing one is saved at once, and **it also becomes the belt the member holds**, with the exam day as the day
they got it. That is what makes the waiting time for the next belt count from the right day. Choose *No belt* to take a result back
(the belt the member holds stays as it is; correct it on the Members page).

Good to know:

- You can record belts from the **day of the exam** on, not before.
- If a member received a belt *after* this exam, typing in this older result keeps it as history and does not turn their current belt
  back. When you enter old exams for members who already hold a belt, set their belt day on the Members page first, or enter the newest
  exam first.

The exact rules are in [exam-planning.md](exam-planning.md).

## Members

The members of the selected group, with their **belt**, the **day they got it**, the **day they started training** and their **birth
date**. You can search by name and show only members who hold no belt yet.

Who is a member is decided by **the data source**: names come from there and members cannot be added or removed in kyuplan. The list is as fresh
as the last automatic sync (every 15 minutes by default). A member who leaves the data source is removed, together with their belt
data, comments and exam invitations.

**Hide** takes a member out of the statistics and out of every list and page (after a question; the member stays in the data source and
keeps their belt, comments and results). Hidden members can only be seen again with **View hidden members** at the top of the page, which
needs its own permission (`members.hidden`): there you can make each of them visible again with **Make visible**, and **Back to members**
returns to the list.

What the data source does not know, you enter here, because the Exams page needs it:

- **Edit** sets the belt (or none), the day they got it (only possible with a belt), the day they train since, and the birth date.
- The **birth date** is taken from the data source when it has one. A value that is there is never overwritten.
- The **day they train since** is worked out automatically as the day of the first event the member attended; press **Clear the day**
  to let kyuplan work it out again. A day you set yourself is never changed by the sync.
- Members with no belt, no belt day or no birth date will show up as **Needs data** on the Exams page.

Dates must lie in the past: the day they started and the day they got the belt since 1950, the birth date since 1900.

### Comments

Every member row has a **Comments (n)** button (if you may read comments). It opens the comments on that member; **Back to the members**
returns to the list.

- **Write** a comment, or **reply** to one (replies can be replied to, up to four steps deep).
- **Format** with the buttons **bold**, *italic*, underline and ~~strikethrough~~: select text and press the button, or type the markers
  yourself: `**bold**`, `*italic*`, `__underlined__`, `~~crossed out~~`. A live preview shows the result. A comment is at most 4000
  characters.
- Add **tags** (up to 20) to say what the comment is about; you can create a tag right there with **New tag**.
- **Edit history**: every change is recorded: who edited it, when, whether the text or the formatting changed, which tags were added or
  removed, and what it said before. The comment shows *Edited by …*; click it to see the history.
- **Delete**: you can delete your own comments if you have that permission; deleting other people's needs a further permission. A
  comment is deleted **together with its replies**; the question tells you how many.
- Comments are visible to everybody who may read comments, and your **display name** (not your login name) is shown as the author.

Comments are the club's notes about people, some of them children. Write what you would be comfortable having read by the person's
parents, and keep it factual. The rules are in [comments-and-tags.md](comments-and-tags.md).

## Belts

The belts of the club **in the order of progression**, with their colour (and stripe) and, for each way of getting there, what it
takes:

- the **previous belt** a member must hold,
- the **minimum age** in years and the **minimum waiting time** in months, both counted **for coming from that previous belt** (so
  *Blue → Brown* can demand more than *Blue-brown → Brown*), and
- how many members hold the belt.

**New belt** and **Edit** open a form: name, colour and stripe colour (type `#RRGGBB`, or pick one), and the previous belts to tick, each
with its minimum age and waiting time (`0` = none). **Delete** is disabled, with the reason as tooltip, while members hold the belt, other
belts require it, or it was handed out at an exam. A new installation starts with the 19 standard belts (9th Kyu to 3rd Dan); changing
them is ordinary editing.

## Tags

Tags are topics that comments can be about, for example *Belt exam September*. On the **Tags** page you create, rename and delete them.
A tag can be connected to **one event or to none** (one event can have many tags). Names are unique regardless of case.
Deleting a tag asks first and says on how many comments it is: it is taken off them, **the comments stay**.

## Your account

The **Account** page changes your own details. Everybody who is signed in has it.

- **User name**: the name you sign in with.
- **Display name**: what *other users* see, for example next to your comments. If you leave it empty, your user name is shown.
- **Aliases**: up to ten more names that also sign you in, with the same password (one per line). Nobody else sees them.
- **Password**: needs the current password, then the new one twice. 12 to 128 characters, anything goes (spaces, umlauts, colons).

If an administrator linked you to the club member(s) you are, and gave you the permissions `comments.viewOwn` and/or `ratings.viewOwn`, the page also
shows **About you**: the comments and star evaluations the others wrote about you. Without these permissions nobody sees what is written about
themselves, however much else they may read.

Every part has its own button, which only becomes active when you changed something the server will accept. **Your roles and
permissions cannot be changed here**; only an administrator does that. After changing your user name or password in the browser, the
browser still holds the old login and asks for the new one at the next click: that is normal. The desktop app keeps you signed in.

## Language, theme and other settings

Your own settings are on the page **Account > Personal settings**. They are kept with your account, so they are the same on every computer.

- **Language**: English or German. Until you pick one, the apps follow the language of your browser or Windows. The choice also decides
  how numbers and dates are written. The web client reloads when you switch. Names and events come from the data source and are shown as they are.
- **Theme**: light or dark; the app starts in your system's theme. The button is off in the themes that have only one (Dracula,
  Catppuccin, Windows Vista / 7, native WPF and the theme packs).
- **Appearance**: the look of the program. Both apps offer **Dracula**, **Catppuccin Latte** (light),
  **Catppuccin Mocha** (dark) and **Liquid Glass** (frosted, translucent panels). The desktop app also has the Windows looks:
  **Windows (automatic)** (Windows 11, or Windows 10 on older Windows), **Windows 10** (flat and square), **Vista / 7 (Aero)** (glossy
  buttons, a pale blue navigation pane) and **Native WPF** (the plain controls in your Windows colours). The first entry, *Server
  default*, follows what your administrators chose. The choice is kept with **your account** (separately for the web and the desktop app),
  so it is the same on every computer. In the desktop app a theme of another family than the one in use restarts the program (after
  asking). The accent colour of your instance is used in every theme (the high contrast themes bring their own).
- **High contrast and more themes**: if text is hard to read for you, choose one of the three **High contrast** themes (white on black,
  black on white, or yellow on black): the strongest text contrast, clearly drawn borders, thick focus rings and underlined links. Your
  administrators can add more themes (the list also holds Nord, Solarized, Gruvbox and Tokyo Night). Windows' own high contrast mode is
  respected by the web client as well.
- The apps remember your last filter on your own computer (browser storage, or `%LocalAppData%\Kyuplan`).

## Frequently asked questions

**A member is missing from the list.** Members come from the data source and are synced every 15 minutes. Check that they are in the group there,
then press **Refresh** and wait for the next sync. A member of two groups appears once per group.

**Everybody says "Needs data" on the Exams page.** Belts are club-specific: create the belts (Belts page), then give members their belt,
the day they got it and their birth date (Members page).

**Why is somebody "Not yet" although they trained a lot?** Open the row: it names the check that failed. Attendance counts only events they were
**invited to** and **accepted**, within the filter's time span.

**Why is an attendance number different from what I count by hand?** Cancelled events, events that are not over yet, events outside the time span,
and events excluded by the name filters do not count; and "attended" means "accepted". See [attendance-statistics.md](attendance-statistics.md).

**I saved members for an exam. Why did nobody get an invitation?** kyuplan cannot invite anybody. Saved members are the planner's own list; invite them in the data source.

**The data is old.** Press **Refresh**. If the whole server lost its connection to the data source, ask your administrator to look at the log.

**I can't see a page or a button.** You don't have the permission. Ask your administrator which role fits your job.

**I get "Your user is not allowed to use this page".** The same: you opened the address of a page you may not use.

**The login dialog keeps coming back.** Wrong user name or password, or your account was locked for 15 minutes after too many failed
attempts; wait and try again.
