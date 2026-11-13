# ev-charging

Books an office EV charging slot for you when your Outlook calendar asks for one, and puts the charge point you're given into your calendar.

It's for workplaces where charging slots are requested through a **Microsoft Form**, and approved by someone who emails back. Once a day it:

1. **Looks ahead** (14 days by default) for calendar entries that ask for a slot. The usual habit is a "Travel" block on each office day, with "EV" in it when you want the charger: `Travel; EV`.
2. **Checks Outlook's "My work plan"** says you're in the office that day (optional).
3. **Submits the form** for each such day, once, at your usual start time.
4. **Reads the approval emails** and renames that morning's entry with the charge point you were given: `Travel; EV – Charger 1 (Rear), 08:00–12:00`, with the charger as the location.
5. **Asks you** about anything it can't decide (below), with a Windows notification.

**It never cancels anything.** Cancelling means contacting whoever runs the chargers, so that's left to you. A day you've requested that's no longer an office day is logged, not raised.

## What it decides

For each day from tomorrow (weekdays only by default):

| Work plan | Calendar entry | Already requested? | What it does |
|---|---|---|---|
| Office | Asks for a slot | No | **Books it** |
| Not office | Asks for a slot | No | **Asks you**, and doesn't book |
| Office | Doesn't ask (a plain "Travel"), or none | No | Nothing (logged) |
| Not office | Anything | Yes | Logged only: never raised, never cancelled |

With `work_plan = ""` the work plan isn't read, and every day whose entry asks is booked.

**It also asks you when:** a booking fails; a request is declined; or the next working day's request still isn't approved.

**A submission is a request, not a booking.** Someone approves it, and the approval email is what puts the charge point in your calendar.

## Install

Needs Windows, Python 3.11+ and Microsoft Edge.

```
pipx install --preinstall git+https://github.com/gareth-jones-zen/m365-graph --preinstall git+https://github.com/gareth-jones-zen/win-background git+https://github.com/gareth-jones-zen/ev-charging
```

[m365-graph](https://github.com/gareth-jones-zen/m365-graph) does the Microsoft sign-in and Graph requests. [win-background](https://github.com/gareth-jones-zen/win-background) does the scheduled task, notifications, locks and the log. Neither is on PyPI, so both are installed first, from their repos. Playwright drives your installed Edge, so there's no browser to download.

## Getting started

```
ev-charging config     # create the settings file and say where it is
```

**1. Fill in the settings file** (below). The parts specific to your workplace are the form's address and questions, and who sends the approvals. If a colleague already uses it, copy their `[form]` and `[approvals]` sections.

**2. Put your car's details in a file of their own**, outside any repo, at `car_file` (default `%LOCALAPPDATA%\ev-charging\car.json`):

```json
{"car_make": "Make", "car_model": "Model", "car_registration": "YOUR REG", "additional_information": ""}
```

Any key in it can be used in the form's fields. `start_time` in it is used if the settings file doesn't set one. It must be one object of names and values, as above: anything else (a list, say) fails the booking with an error naming the file.

**3. Sign in**, once:

```
ev-charging sign-in
```

A browser opens to sign in to Microsoft (for your calendar and mail), then an Edge window opens on the form: sign in there if it asks. It closes once the form loads. That Edge profile is the tool's own (`edge_profile`), never your everyday browser, and its sign-in persists.

**4. Try it:**

```
ev-charging book 2026-11-26          # fill in the form and screenshot it; submits nothing
ev-charging run --dry-run            # what the daily run would book, ask and rename
```

Check the screenshot (in `screenshots`) shows the right answers. Then:

```
ev-charging autostart on             # daily at daily_at, and at logon
```

## Commands

```
ev-charging config                       create the settings file (if missing) and say where it is
ev-charging sign-in [--graph-only]       sign in to Microsoft, then to the form in the tool's Edge window
ev-charging book <date> [--submit]       fill in the form for a day; --submit sends it
                [--start 09:00] [--headless] [--force]
ev-charging run [--dry-run] [--hook] [--json]   the daily run
ev-charging approvals [--dry-run]        only apply the approval emails to the calendar
ev-charging status                       requests from today on, and whether each is approved
ev-charging autostart on|off|status      the daily task
```

`--config <file>` picks a settings file; otherwise `EV_CHARGING_CONFIG`, then `%APPDATA%\ev-charging\config.toml`. `book --submit` won't request a day twice unless you add `--force`; use it for a one-off day, or to book a day the run asked you about. `book` waits for a run in progress (the same lock), so the two can't request the same day at once.

## Settings

```toml
login_hint = "you@example.com"          # your work sign-in
tenant = "organizations"                # optional: your tenant id pins sign-in to one organisation
msal_cache = "..."                      # optional: share another tool's Microsoft sign-in
timezone = "GMT Standard Time"          # Windows name of your time zone, for calendar times
state_file = "..."                      # optional: what's been requested (default under %LOCALAPPDATA%\ev-charging)
log_file = "..."                        # optional
car_file = "..."                        # optional: your car's details, outside any repo
edge_profile = "..."                    # optional: the tool's Edge profile
screenshots = "..."                     # optional: where form screenshots go
after_run = ["python", "hook.py"]       # optional: run after each run, with a JSON report on stdin
daily_at = "09:00"                      # when autostart's daily run happens

[booking]
days_ahead = 14                         # how far ahead to look
start_time = "08:00"                    # the slot you ask for
slot_hours = 4                          # how long a slot lasts (for the renamed entry)
notice_days = 1                         # the first day considered is today + this
weekdays_only = true

[trigger]
entry = '^\s*travel'                    # regex: which calendar entries are the day's travel block
book = '\bev\b'                         # regex: the entry asks for a slot ("EV" as a word, any case)
label = "Travel block"                  # what to call it in questions
work_plan = "office"                    # the work plan must say this to book; "" to ignore it

[form]
url = "https://forms.office.com/Pages/ResponsePage.aspx?id=YOUR-FORM-ID"
questions = 6                           # how many questions it has: any other number stops the booking
confirmation = "response was submitted|Your response was|Thanks"   # regex: the page after Submit

[[form.field]]                          # one per question to fill
question = 1                            # its position on the form, from 1
type = "date"                           # date, choice (a dropdown) or text
value = "{date:%d/%m/%Y}"               # {date:...}, {start}, or any key in car_file; empty is skipped

[approvals]                             # optional: without it, nothing reads the replies
sender = "facilities@example.com"       # who emails the outcome
subject = "electric car charging request"   # text in the subject of every reply
approved = "APPROVED"                   # regex on the subject
declined = "DECLIN|REJECT"              # regex on the subject
date = 'Date\s*-\s*(\d{4}-\d{2}-\d{2})' # regex on the body: the day (yyyy-mm-dd)
start = 'Start Time\s*-\s*(\d{1,2}:\d{2})'  # regex on the body: the start time
point = 'assigned;\s*(.+?\))'           # regex on the body: the charge point
charge_points = ["Charger 1 (Rear)"]    # optional: names to look for if `point` finds nothing
look_back_days = 60                     # how far back to read replies
rename = "Travel; EV – {point}, {start}–{end}"  # the morning entry's new subject ({point}, {start}, {end}, {date:...})
morning_before = 12                     # the entry renamed starts before this hour; later ones are left alone
set_location = true                     # put the charge point in the entry's location too
chase_next_working_day = true           # ask if the next working day's request isn't approved yet
```

Paths may use `~` and `%VARIABLES%`; relative paths are relative to the settings file.

**Keep the renamed subject matching `entry`**, and `book` too, so a renamed entry still counts as the day's travel block. The default `Travel; EV – …` does both.

**Only the latest reply for each day counts**, in case a request is revised. Replies are read from every mail folder, so an Outlook rule filing them away makes no difference.

## Quirks worth knowing

**Microsoft Forms:**

- **Forms keeps an unsubmitted draft for each user**, so after a preview the fields come back filled in, and a dropdown shows its last answer instead of "Select your answer". The dropdown is therefore found by its role, never its placeholder text.
- **The date picker swallows the next click** unless it's closed first, so the tool presses Escape after the date.
- **Nothing else can submit a Forms response**: not Graph, not Power Automate. That's why it drives Edge.
- **If the form changes** (a question added or moved), the question count stops the booking rather than filling the wrong boxes. Update `questions` and the fields.

**Approval emails:**

- **Don't read the end time from an approval.** In the process this was built for, the email's "End Time" carries the approval date, month first, not the end of the slot. The tool never reads it: the slot is `slot_hours` from the start.

**Notice:** most schemes ask for at least a day's notice, and spaces can fill up. The run looks from tomorrow (`notice_days`); book further ahead by hand with `book --submit` if you need to.

## The after-run hook

After every run that isn't a dry run (`run --hook`, which the daily task uses), `after_run` gets the report on stdin, from the settings file's folder:

```json
{"config": "C:\\...\\config.toml", "questions": ["Thu 26 Nov: Travel block says EV but work plan says remote. Book EV?"],
 "booked": ["2026-11-26"], "failed": [], "summary": "1 booked, 0 failed, 1 question(s)"}
```

It runs even when there are no questions: each run's questions replace the last run's, so an empty list means the earlier ones are settled. Use it to put the questions somewhere you'll see them.

## Notifications

A scheduled run notifies only when something needs you: questions, a sign-in needed, or the calendar couldn't be read. If the approval emails can't all be read, the approvals read before the problem are still saved, and the problem is a question. A run that books and renames without trouble is silent; `status` and the log say what it did.

## Where things are

- **State** (`state_file`): each requested day, when it was submitted and the charge point once approved. It's what stops a day being requested twice.
  - **A day is recorded just before its request is submitted**, and taken off again if the submission fails. So a state file that can't be written after a submission (a scanner or OneDrive holding it) can't lead to the day being requested again; if it can't be written beforehand, nothing is submitted and the run asks you. Writes are retried for a couple of seconds first.
  - If a failed submission can't be taken off again, the day stays marked as requested and the question says so: book it with `book --submit --force`.
- **Log** (`log_file`): every decision, booking and rename.
- **Screenshots** (`screenshots`): the form as filled in, for each preview and submission, and after a Submit that didn't confirm.

## Tests

```
python -m unittest discover -s tests
```

No network or browser: the calendar, mail and form are fakes.
