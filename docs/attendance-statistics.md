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
| **Engagement** | A 0–100 score: **60 %** attendance + **25 %** recent form (attendance over the last 8 invitations, or overall attendance with fewer than 8) + **15 %** answered. |

Engagement bands: **Excellent** ≥ 80 · **Good** ≥ 60 · **Irregular** ≥ 40 · **At risk** < 40.

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

## Dashboard

The dashboard uses the same rules over the last 30 / 90 / 180 / 365 days (default 90, `Statistics:DashboardDays`),
with the default threshold from the server configuration, and adds:

- **Upcoming events** in the next two weeks, with how many members are coming, declined or have not answered yet.
- **Most engaged**: the five highest engagement scores among members with a verdict.
- **Needs attention**: members in the *At risk* band, or with a *falling* trend (unless *Excellent*), lowest score first.
