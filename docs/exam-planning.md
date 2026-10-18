# Exam planning: definitions

This page defines what the **Exams** page shows, so that a verdict on screen can always be traced back to a rule. The rules are
implemented by [`ExamSuggestionCalculator`](../src/Kyuplan.Api/Statistics/ExamSuggestionCalculator.cs) (who is fit) and
[`ExamPlanningService`](../src/Kyuplan.Api/Persistence/ExamPlanningService.cs) (what is read and written around it); the tests in
`tests/Kyuplan.Tests/ExamSuggestionCalculatorTests.cs` and `ExamPlanningTests.cs` pin each rule below.

## Exams

An exam is a Spond event that somebody made an exam (see the README). The page lists the exams of the selected group: the ones **to
come** first (the next one at the top, and chosen when the page opens), then the ones that **took place**, the latest first. An exam
is *to come* from the day it is on (inclusive) and *past* the day after. The exam **of today** counts as both: it can be planned and
belts can already be recorded for it.

- **Add exam** takes the Spond event that is the exam. The events offered are those of the search window
  (`Statistics:EventSearchMonthsBack` / `…Ahead`) that are not exams yet; type a part of the name or the date to find one. If the group
  has subgroups, the form also asks **which subgroups the suggestions are for** (tick any number; none ticked = everyone). They become the
  subgroups of the exam's *default* filter (see below). They can only be chosen when the exam is added.
- **Delete exam** asks first. It deletes the exam's saved filters, the members saved for it and the belts recorded at it. The event on
  Spond is not touched, and neither are the belts the members hold: a belt that was handed out stays handed out.

## A past exam: who attended and who received which belt

- **Attended** means **accepted the invitation**, as everywhere (Spond records RSVPs, not check-ins). The page lists those who accepted,
  and anybody who was given a belt at the exam even though they did not accept.
- Next to every attendee a select holds the belt they **received**. Choosing one records it at once; there is no save button.
  Choosing "No belt" takes the result back.
- Recording a belt for a member **also makes it the belt they hold**, and the day of the exam the day they got it (`Members.BeltSince`),
  so that the waiting time for the next belt counts from there. This is not done when it would turn the clock back: when the member
  got a belt **after** this exam (a result at a later exam, or a belt day after the exam day), the result stays on record as history and
  the member keeps what they hold. This is what makes typing in the results of earlier exams afterwards safe. (A member whose belt day is
  **not known** cannot be protected this way: a result recorded for them is applied. When typing in old history for members who already
  hold a belt, set their belt day on the Members page first, or enter the newest exam first.)
- **Taking a result back leaves the belt the member holds as it is.** The belt of a member is corrected on the Members page.
- A belt can only be recorded once the exam has taken place (the day of the exam is enough); the API answers `400` before that.
- A belt that was handed out at an exam cannot be deleted (`409`), like a belt a member holds.
- The counts at the top (invited, attended, declined, no answer, belts handed out) are those of the exam's Spond invitations and results.

Results are kept apart from the invitations: the sync with Spond drops invitations that are gone from Spond, but never a result.

## An exam to come: who is fit

Every member of the group is judged for **the belt after the one they hold**, **on the day of the exam** (not today): somebody who turns
12 before the exam is 12 for it.

### The next belt

The belt a member would take is a belt that lists the belt they hold among its previous belts (see *Belts* in the README), together with
what that belt asks for **coming from the belt they hold**: a minimum age and a minimum waiting time. When several belts ask for the belt
held, the **highest** one in the order of the belts that the member is fit for is taken (somebody who is fit for both blue-brown and
the 1st brown is put forward for brown), otherwise the nearest one, i.e. the first in that order. A member whose belt no
other belt asks for holds the highest belt and has nothing to take.

### The checks

| Check | Met when | Not known when |
| --- | --- | --- |
| **Attendance** | the attendance statistics (with the filter chosen, see below) call the member *Qualified*: at least the required share of the events they were invited to, and invited to at least the minimum number | – (a member who was not invited to any event of the period, or to too few, is *not met*: there is no attendance to speak of) |
| **Evaluation** | the member's **average evaluation** (0 to 5 stars, see [attendance-statistics.md](attendance-statistics.md#star-evaluations)) over the same time span is at least `Statistics:ExamMinAverageStars` (3 by default; 0 = not asked) | – (a member with no event to average over has *not met* it) |
| **Age** | the age in full years **on the exam day** is at least the minimum age of the next belt (or there is none) | there is no birth date, and a minimum age is asked |
| **Waiting time** | the full months from the day the belt was received to the exam day are at least the minimum waiting time (or there is none) | neither the day the belt was received nor an earlier exam is known, and a waiting time is asked |

- **Months and years are counted in full**: a month that is shorter than the day it started on ends on its last day (from 31 January,
  a month has passed on 28 February); somebody born on 29 February has their birthday on 28 February in a year without a 29th.
- **The waiting time counts from** the day the member got their belt (`Members.BeltSince`: set by hand or by recording a belt at an
  exam). While that day is not known it counts from **the member's last exam**: the latest exam before this one, not in the future, that
  they accepted or received a belt at. The belt cannot have been received later than that exam, so the waiting time is never
  overstated by it. The row's tooltip says which of the two it is.
- A member with **no belt assigned** cannot be judged: they need the belt set on the Members page.

### The verdict

| Verdict | Meaning |
| --- | --- |
| **Fit** | every check is met. |
| **Not yet** | at least one check is *not met* (what is missed wins over what is not known). The row says which: "Waited 1 months by the exam day, 3 needed." or "Average evaluation 2.5 of 5, 3.0 needed." |
| **Needs data** | nothing is missed, but a check cannot be made: no belt, no birth date, or no idea when the belt was received. The row says what is missing. |
| **Top belt** | the member holds the highest belt: there is nothing to take an exam for. |

An event nobody evaluated counts 2.5 stars (`Statistics:DefaultEventStars`) for a member who attended it, so with the standard values a member who was
never evaluated is *not yet*; the table shows the average in its own column (*Stars*), amber when it falls short.

The list is ordered fit first (the best attendance first), then those who need data, those who are not fit yet and those with nothing to take.
The page shows the fit ones first and counts every kind; the other kinds are one click away. The search finds members by the words of their name.

### Which filter measures the attendance

The attendance is the one of the **Attendance Statistics** page, with a filter you choose (*Measure attendance with*):

- a **saved filter** of the exam's group, whichever exam it was saved for (the filters saved for this exam are offered first, the newest
  first, and the newest of them is chosen when the page opens: it is the one you saved for this exam; an exam without a filter of its
  own, such as a new one, starts with the default, whatever other exams have). It brings its time span (the two
  events, or the two days they stood for once the events are gone), the subgroups, the required share, the minimum number of
  invitations and the event name terms. A filter for some subgroups judges only the members of those subgroups.
- the **default**: from the previous exam of the group to this exam (or, without one, the last six months, and never more than twelve),
  the configured `Statistics:DefaultMinAttendancePercent` / `DefaultMinInvitedEvents`, and the **subgroups chosen for the exam when it was
  added** (everyone when none were). Like a saved filter for some subgroups, it judges only the members of those subgroups; the summary
  under the choice says so (*Only members of …*). A saved filter brings its own subgroups, whatever the exam was made for.

Below the choice a link, *Define a filter on the Attendance Statistics page*, opens that page (in the desktop app it switches to it): filters
are defined and saved there.

The page says in a sentence what the attendance was counted with. Only events that are already over count, so for an exam in the
future the span ends with the events that have taken place.

## Saving members for an exam

The ticks in the list are the user's: tick the members to put forward (or *Select the fit*), then **Save selection**. The members are saved **in
the planner's own database only**: nobody is invited on Spond. Once somebody invites them there (the Spond API this planner uses has no
way to add recipients to an event), the sync shows their answer in the *On Spond* column, and the page counts the saved members who are
**not invited yet**: those are the ones left to invite, all at once.

- Saving sends what **changed** (who was ticked and is not saved, who was saved and is not ticked); members the list does not show
  (a filter for some subgroups leaves the others out) are never touched, nor what somebody else saved meanwhile.
- The sync with Spond never changes the saved members. A member who leaves Spond is removed from the list with their invitations.
- Buttons are only enabled while they would change something: *Select the fit* while somebody who is fit is not ticked, *Clear selection*
  while somebody is ticked, *Save selection* and *Discard changes* while the ticks differ from what is saved.

## Prices and payments

Members pay for exams. The **Exam prices** page holds the price list of the club: one **general fee** that every member pays for an exam, and
an **additional price per belt** (0 = none). What a member owes for an exam is `general fee + additional price of the belt they receive`.

- **The belt they receive** is the belt recorded for them at the exam; until one is recorded, the first belt (in the order of the progression)
  that asks for the belt they hold. Without either (no belt held, or no belt comes next) only the general fee is due.
- **Who pays** (the *Payments* section of the exam on the Exams page): everybody who accepted the exam on Spond, everybody saved for it who did
  not decline, everybody who received a belt at it, and everybody an administrator already recorded something for.
- An administrator records **the total a member paid** and, if wanted, a note (*Mark as paid* fills in the amount due). The day of the payment
  is today unless given. The **status** follows: *not paid* (nothing paid), *partly paid* (less than is due) or *paid* (the amount due or more;
  an exam that costs nothing counts as paid). Entering 0 and no note takes the record back.
- **Prices change.** A member with a record keeps the amount due they were saved with (re-worked from the current prices every time the record
  is saved), so changing the prices does not turn settled exams into open ones. Members without a record always follow the current prices.
- Amounts are whole cents between 0 and 100,000. The currency is the one of the user's language settings; the planner does not convert.
- Permissions: `payments.view` (the price page, the payments of every exam), `payments.edit` (record payments), `prices.edit` (change the price
  list). The built-in roles do not carry them (money is not the trainers' business); give them through a role.

## What the Members page adds

Besides the belt and the day they started, a member has **the day they got their belt** and a **birth date**. The birth date is taken over
from Spond when Spond has one (and a day set by hand, or taken earlier, is never changed); the age a belt asks for is worked out from it.
The day the belt was received belongs to a belt: without one there is none.
