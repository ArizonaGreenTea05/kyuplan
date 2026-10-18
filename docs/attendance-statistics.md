# Attendance statistics: definitions

This page defines every figure the **Attendance Statistics** page and the **Dashboard** show, so that a number on
screen can always be traced back to a rule. The implementation is
[`AttendanceCalculator`](../src/Kyuplan.Api/Statistics/AttendanceCalculator.cs); the unit tests in
`tests/Kyuplan.Tests/AttendanceCalculatorTests.cs` pin each rule below.

## What "attended" means

Spond records **RSVPs**, not check-ins. A member counts as having *attended* an event when they **accepted** the
invitation. Declining, not answering, and being on the waiting list all count as *not attended*.

This is the best signal the Spond API (via the Spond.API package) exposes, but it is not proof of presence: someone who
accepted and stayed home is counted as present, and someone who showed up without answering is not.

## The time span

The span is two days, both included, evaluated in the club's time zone (`Europe/Berlin` by default). It is given as
two **dates** or as two **events**, in which case each event stands for the day it starts on. The statistics page
defines it by events by default and preselects the **previous exam** and the **next exam**:

1. Take the events that start within `Statistics:EventSearchMonthsBack` (12) months before and `Statistics:EventSearchMonthsAhead` (6) months after today, cancelled ones excluded.
2. Exams are those whose name contains `Statistics:ExamKeyword` (`Prüfung`, case-insensitive), so *Gürtelprüfung* counts.
3. *To* is the first exam that starts after the current moment; an exam that has already started today counts as past.
4. *From* is the latest exam that starts before *To*.

Without such a pair (no exam in the window, no exam keyword, or only a *To* or only a *From*) the page still
preselects two events, so the choice is never empty: the **first event of the last six months** and the **latest event
that takes place today or earlier**.

Both events are picked in a searchable list. Each entry leads with its date (a calendar page, and the full date with
the year below the name), and the search box takes the name or the date however it is written: `prüfung`, `26.9.`,
`2026-09`, `sat`, `march`, `26 sep`. Name words match anywhere in the name (also without accents: `prufung`); date words
match the start of a date part, so `26` finds the 26th and not every day of 2026. All words must fit. Arrow keys and
Enter pick an entry; the list opens at the event that is chosen.

Because the span includes both end days, the previous exam itself is part of the analysis (and so is the next one,
once it is over); leave exams out with **Ignore events containing** if they should not count.

## Which events count

An event is part of the analysis when all of these hold:

- it is **not cancelled**,
- it is **already over** (its end time is in the past),
- it **starts on a day inside the selected time span** (see above),
- its name matches **Only events containing** (if set) and does **not** match **Ignore events containing**
  (terms separated by `,` or `;`, case-insensitive).

Use the name filters to keep exams, tournaments and parties out of a "training attendance" question.

## Who was "invited"

A member was invited to an event when Spond lists them in that event's responses (accepted, declined, unanswered,
waiting list or unconfirmed). Members are only judged against events they were invited to, so someone who joined
three months ago is not penalised for the year before, and a member of the *Kinder* subgroup is not measured against
the adults' training.

The **Members** filter restricts the report to the members of one or more subgroups: tick any number of them and
everyone in at least one of them is reported (a member of two ticked subgroups appears once). With none ticked, the
whole group is reported. Events are then counted over those members only.

## Remembered and saved filters

The filters above (time span, subgroups, minimum attendance, minimum invitations, event name terms) are what a **remembered**
or **saved** filter holds; the member search, the status buttons and the sorting are not part of it.

- The client remembers the filter that was in effect at the last load and opens the page with it, in its group. A group that
  no longer exists drops the filter; so does a file that cannot be read.
- A filter saved on the server belongs to one exam and is stored with the two events **and** the two days of its time span.
  It is applied with the events as long as both are still offered (see *The time span*), otherwise with the days.
- The **Exams** page uses a saved filter (of any exam of the group) to measure the attendance of the members it suggests for an exam;
  see [exam-planning.md](exam-planning.md).

## Per member

| Figure | Definition |
| --- | --- |
| **Invited** | Counted events the member was invited to. Members with none are left out. |
| **Attended / Declined / No answer** | How they answered those invitations. |
| **Attendance** | Attended ÷ Invited. |
| **Status** | *Qualified*: Attendance is at least the required percentage. *Below*: enough events, but under it. *Too few events*: invited to fewer than the minimum, no verdict. The comparison is exact (`attended × 100 ≥ required × invited`), so 3 of 4 qualifies for 75 %. |
| **Answered** | Share of invitations that got an answer (accepted or declined). Low values mean the member is hard to plan with. |
| **Streak** | Consecutive accepted events counting back from the most recent one, and the longest such run in the span. |
| **Last attended** | Start date of the most recent accepted event in the span, and how many days ago that was. |
| **Per week** | Accepted events per week, from the member's first invitation in the span to the end of the span (at least one week). Training frequency, independent of how many sessions were offered. |
| **Form** | Outcome of the last 10 invitations, oldest first. |
| **Trend** | Attendance over the last 8 invitations compared with attendance before that, in percentage points. Rising / falling at ±15 points; needs at least 12 invitations, otherwise "not enough events". |
| **Stars** | The average evaluation, 0 to 5 stars (see [Star evaluations](#star-evaluations)). Empty without an event to average over. The tooltip says how many of those events somebody evaluated. |
| **Engagement** | A 0–100 score: **60 %** attendance + **25 %** recent form (attendance over the last 8 invitations, or overall attendance with fewer than 8) + **15 %** answered. |

Engagement bands: **Excellent** ≥ 80 · **Good** ≥ 60 · **Irregular** ≥ 40 · **At risk** < 40.

The stars are not part of the engagement score.

The weights are a judgement call, not a standard; they are constants at the top of `AttendanceCalculator` if your
club weighs things differently.

## Summary figures

- **Meeting X %**: members with status *Qualified*, out of members that have a verdict.
- **Attendance**: all accepted invitations ÷ all invitations, over every member and event in the analysis.
- **Response rate**: all answered invitations ÷ all invitations.
- **Ø attendees**: average number of accepted invitations per event.
- **At risk**: members in the *At risk* band that have a verdict.
- **How attendance is spread**: members per 10-point attendance band. Members with too few events are not included.
- **Attendance by month**: accepted ÷ invited over the events of each month. Months without events are omitted rather than shown as 0 %.
- **Average evaluation**: the stars of every counted member and event (see below) averaged; the events table has the average of the members of each event.

## Star evaluations

Besides comments, users **evaluate a member at an event with 0 to 5 stars**, to follow how members perform. The implementation is
[`RatingService`](../src/Kyuplan.Api/Persistence/RatingService.cs) (what is stored) and [`RatingBook`](../src/Kyuplan.Api/Statistics/RatingBook.cs)
(how the statistics read it); the tests in `tests/Kyuplan.Tests/RatingTests.cs` and `RatingApiTests.cs` pin each rule below.

- **Where.** On the **Calendar** page, in the detail of an event: next to each invited member the buttons **0 1 2 3 4 5**. Choosing one gives the
  evaluation; choosing another replaces it; **Take back** removes it. Members can be evaluated once the event has started.
- **One per user, member and event.** A user has at most one evaluation of a member at an event (the API replaces it); several users may each
  have theirs.
- **0 is not "none".** A member nobody evaluated shows *Not evaluated*. An evaluation of 0 stars shows `☆☆☆☆☆ 0/5` in red, and the button 0 is
  highlighted in red when it is the user's.
- **Everybody sees everybody's, except their own.** Whoever may read evaluations (`ratings.view`) sees all evaluations of the member at the event
  (not those of the members they are themselves: that needs `ratings.viewOwn`, which works without `ratings.view`), each with the
  user (their display name) and, in the tooltip, when it was given, and the average of them. Whoever may give them (`ratings.write`) gets the
  buttons; nobody can change or take back another user's evaluation.
- **An event counts once.** In every figure an event is **one** instance for a member, however many users evaluated them: it counts the
  **average of its evaluations**, and, when nobody evaluated the member, the **default of 2.5 stars** (setting *Stars an event counts without
  evaluation*, `Statistics:DefaultEventStars`).
- **Which events count.** The events the member **attended** (accepted) and those somebody evaluated them at, even if they did not accept.
  Events they stayed away from, without an evaluation, count nothing. Without any such event the member has no average.
- **Average evaluation** = the mean of what those events count, over the time span and filter of the page; rounded to two decimals, shown with
  one. *Stars*, *the events' average* and the *Average evaluation* figure follow the same filter as everything else on the page.
- **Exams.** A member needs at least the **minimum average** (setting *Minimum average evaluation for exams*, `Statistics:ExamMinAverageStars`,
  3 by default; 0 = not asked) over the filter's time span to be suggested for an exam; see [exam-planning.md](exam-planning.md). The filter card
  has a *Minimum evaluation* slider that starts at the setting; it is saved with a saved filter and then replaces the setting for suggestions made
  with that filter (it does not change the statistics shown). With the
  standard values nobody who was never evaluated is suggested (every event counts 2.5, below 3): evaluations are what puts members forward.

### API

| Call | |
| --- | --- |
| `PUT /api/calendar/events/{eventId}/members/{memberId}/rating?groupId=` | `ratings.write`. `{ "stars": 0–5 }` as the signed-in user, replacing their earlier evaluation. `400` for stars outside 0–5 or an event that has not started, `404` for an unknown group, event or member. |
| `DELETE /api/calendar/events/{eventId}/members/{memberId}/rating?groupId=` | `ratings.write`. Takes back the signed-in user's own evaluation (`204`, also when there is none). |
| `GET /api/calendar/events/{eventId}?groupId=` | With `ratings.view` each attendee carries `ratings` (everybody's evaluations of them at this event). |
| `GET /api/members/{memberId}/ratings` | `ratings.view`, or `ratings.viewOwn` for a member the user is themselves (403 otherwise). Everything the member got, at all events, the latest first. |

The statistics endpoint adds `averageStars`, `starEvents` and `evaluatedEvents` per member, `averageStars` per event and in the summary.

## Dashboard

The dashboard uses the same rules over the last 30 / 90 / 180 / 365 days (default 90, `Statistics:DashboardDays`),
with the default threshold from the server configuration, and adds:

- **Upcoming events** in the next two weeks, with how many members are coming, declined or have not answered yet.
- **Most engaged**: the five highest engagement scores among members with a verdict.
- **Needs attention**: members in the *At risk* band, or with a *falling* trend (unless *Excellent*), lowest score first.
