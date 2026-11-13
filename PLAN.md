# Plan: ev-charging ported to .NET 11 Native AOT, with Edge over DevTools

*13 Nov 2026, written before the port, overnight and unattended (branch `dotnet`; the Python is tagged `v0.1.0`).
Phases 1 to 4 are done overnight; 5 to 7 wait for GJ.*

## Does this change the earlier recommendation?

The browser-tools assessment (12 Nov) said **don't port ev-charging**: the form needs a browser, Playwright for .NET
brings Node and can't be AOT-compiled, so a port would be 110 to 180 MB and gain nothing. Since then the Edge
DevTools route was proven (EdgeDevTools and PeopleFirstClient in background-tools-lib, a 6 MB AOT exe, byte-identical
People First reads). Filling a form is more than a sign-in, but every step the Python takes (go to a page, wait for a
selector, click, type, press Escape, pick an option by role, screenshot, wait for text) is a handful of DevTools
commands. So:

- **Size: about 11 MB instead of 110 to 180 MB**, one exe, no Python, no Node. The MSAL sign-in is most of it.
- **The risk the assessment named hasn't gone away:** the real form can't be tested without changing GJ's Forms draft
  or sending Facilities a request. Overnight it is proven only against a local mock of a Microsoft Form.
- **So the recommendation changes from "don't port" to "port, but switch only after a supervised check on the real
  form"** (phase 5). The case for it is colleagues (a single exe, no Python) and one fewer Python tool; for GJ alone
  there's no urgency, as the Python works.

## The goal

**The same decisions, questions, log lines, calendar renames and state file as the Python version, byte for byte**,
from the same settings file, state file, car file, log, Edge profile, task name (`ev-charging run`) and run lock
(`ev-charging-run-<key>`, with Python's lower-casing), so either version can replace the other with nothing moved.
The form is filled through background-tools-lib's EdgeDevTools (Edge over its DevTools pipe), not Playwright.

## Names

| | Python | .NET |
|---|---|---|
| Command | `python -m ev_charging` (pythonw for the task) | `ev-charging.exe`; `ev-charging-background.exe` (no window) for the task |
| Installed to | pip, editable from `~/source/repos/ev-charging` | unzip to `%LOCALAPPDATA%\Programs\ev-charging\` |
| Settings, state, car file, log, screenshots, Edge profile, task, run lock | as set in `tools/ev-charging.toml` | unchanged: the same files and names |

## Module map

| Python | .NET | Notes |
|---|---|---|
| `config.py` | `Settings.cs` | Tomlyn; paths through PyCompat's `PyPath`; the same TEMPLATE text; Python's error messages (dict `repr` included) |
| `planner.py` | `Planner.cs` | `str.lower()`, code-point sorts, stable sort by `receivedDateTime`, `html.unescape`, Python's `\s` |
| (inside `planner.py`, `form.py`) | `PyFormat.cs` | the `str.format` subset the templates use: `{name}`, `{name:strftime}` for dates, `{{`/`}}` |
| `state.py` | `State.cs` | `json.dumps(indent=2, sort_keys=True)` with CRLF (text mode), temp file then replace |
| `outlook.py` | `Outlook.cs`, `Graph.cs` | the same URLs (`urlencode`'s `quote_plus` exactly) and `Prefer` header; reads through a **read-only fence** |
| (the PATCH in `outlook.py`) | `WriteDoor.cs` | **the calendar write door**: Live, Journal or Refuse; dry runs and every check use Refuse or Journal |
| `form.py` | `Form.cs`, `FormPage.cs` | EdgeDevTools: navigate, poll for the questions, click, `Input.insertText`, Escape, role queries, `Page.captureScreenshot` |
| (the Submit click) | `SubmitDoor.cs` | **the form door, Refuse by default**: Live only for `run` (not a dry run) and `book --submit`; tests and checks use Refuse, Journal, or LocalOnly (clicks only on a `file://` page) |
| `run.py` | `Run.cs` | the decision loop, approvals, the hook (`Shell.Run`, stdin UTF-8), `Locks.Exclusive` |
| `cli.py`, `autostart.py` | `Cli.cs`, `Autostart.cs` | argparse's texts and exit codes (help captured from Python); the task through WinBackground |

## How parity is checked

1. **The 28 Python tests, by name**, plus new ones (door, fence, templates, URLs, mock form).
2. **The Python as an oracle** (`tests/parity/oracle.py`, run with `PYTHONDONTWRITEBYTECODE=1` against this clone's
   copy of the Python, never the live checkout): `run.run()` and `apply_approvals()` with the clock frozen and fakes
   for Outlook and the booker, over generated scenarios (work plan by Travel blocks by requested by approvals, the
   14-day window, weekends, notice, clock changes, renames, declines, awkward HTML and text, failing bookings,
   calendar failures, dry runs, approvals only). Compared: report, decisions, questions, log lines, rename payloads,
   booker calls, state bytes. Plus misc goldens: settings errors, templates, URLs, the hook payload, CLI texts.
3. **Live, read-only, on a copy of the token cache:** the work plan, calendar and Facilities-reply GETs recorded once
   through the .NET adapter behind the fence; then both planners replay the same recording (fakes for the booker and
   rename, a copy of the state file) and the outputs are compared by SHA-256 and counts. No names or dates printed.
4. **The form, against a local mock only:** a static HTML page shaped like a Microsoft Form (question items, a date
   box whose picker swallows the next click, a dropdown found by role that shows a saved draft's answer, text boxes,
   inputs that only update on real input events, a Submit button and a confirmation), in a throwaway Edge profile.
   **The real Forms URL is never opened** (even a preview overwrites GJ's draft).
5. **Native AOT:** both exes publish with no warnings; the AOT exe is used for the live check.

## Phases

1. **Set-up (done):** fresh clone at `~/source/repos/ev-charging-dotnet`, tag `v0.1.0`, branch `dotnet`.
2. **Oracle:** scenarios and misc goldens from the Python.
3. **Port:** module by module against the goldens; CI builds, tests and publishes both exes.
4. **Checks (read-only):** above.
5. **Supervised form check (GJ, at the console):**
   1. `ev-charging.exe --config <a copy of the settings> book <a day> --start 08:00` (no `--submit`): it fills GJ's
      real form in a visible window and screenshots it. This overwrites his Forms draft, which is harmless;
   2. compare the screenshot with the Python one for the same day;
   3. then, for a day GJ actually wants, `book <day> --submit` from the .NET exe, and check Facilities' approval
      arrives and the next run renames the Travel block.
6. **Shadow (GJ):** a week of `ev-charging.exe run --dry-run --json` beside the Python task, on its own copy of the
   settings pointing at a copy of the state file, comparing with the Python log each morning.
7. **Switch (GJ):**
   1. `! python -m ev_charging --config tools/ev-charging.toml autostart off`;
   2. unzip the build to `%LOCALAPPDATA%\Programs\ev-charging\`;
   3. `! "%LOCALAPPDATA%\Programs\ev-charging\ev-charging.exe" --config tools/ev-charging.toml autostart on`;
   4. `after_run` stays `["python", "ev_charging_hook.py"]` (the EA hook is Python either way);
   5. one EA commit: CLAUDE.md's "EV charging bookings" section and the toml's comments;
   6. **rollback:** `ev-charging autostart off`, then the Python `autostart on`. Nothing else moves.

## Decisions taken overnight (GJ's to overrule)

| # | Decision | Why |
|---|---|---|
| 1 | EdgeDevTools, not Playwright | The point of the port: 11 MB and AOT instead of 110 to 180 MB |
| 2 | Typing, keys, role queries and screenshots live in the tool (`FormPage.cs`), not the library | EdgeDevTools says it never types into a page, and another agent may be changing the library tonight. A candidate to move into the library as an opt-in later |
| 3 | Two doors: the calendar write door over a read-only Graph fence, and a Submit door that defaults to Refuse | Nothing in a test or a check can rename an entry or send a request |
| 4 | Headless, the form stops at once on a Microsoft page that needs a person (password, code, Hello) | Python waited 5 minutes for a person who isn't there; a visible window still waits 5 minutes |
| 5 | **One named lock for the Edge profile, `edge-profile-<key>`**, the key computed as `EdgeProfile.Key` (win-background's `key()` of the normalised path), held only while Edge is open. It waits up to 10 minutes, then the booking fails with "profile busy"; Edge's own lock file (`EdgeProfile.IsOpen`) is a second guard. A test proves the two spellings in the tomls (`%LOCALAPPDATA%/claude-ea/m365-edge-profile` here, `~/AppData/Local/claude-ea/m365-edge-profile` in teams-transcripts) give one key | The live finding: the 08:00 run and a transcript fetch can collide on `m365-edge-profile`, and Python fails at once. Agreed with the teams-transcripts port (coordinator, 13 Nov). **The live Python tools still take no such lock: fixing them is GJ's decision** |
| 6 | Templates: the `str.format` subset ported exactly; a strftime directive PyCompat doesn't support, or a format spec on a text value, is refused when the settings load | Rather than mean something different; GJ's settings use `%d/%m/%Y` only |
| 7 | `[approvals]` values type-checked at load | Python takes them untyped and fails mid-run |
| 8 | `book --submit` doesn't take the run lock, as in Python | Parity; noted for GJ |
| 9 | Help text is Python's, captured at 80 columns | argparse output is static |
| 10 | Sibling-checkout references to background-tools-lib; x64 only; the Python stays on `main` at `v0.1.0` | As the other ports |

## Risks

- **The real form** is the fragile part, as it is in Python: Microsoft can change its markup. The mock proves the
  mechanics (typing that a React-style form accepts, the picker quirk, the dropdown by role, the strict matches),
  not Microsoft's current page.
- **A real submission is a request to Facilities**: it is never made overnight, and phase 5 is GJ's.
- **Python drift:** the goldens record v0.1.0; regenerate them if the Python changes.

## Status (13 Nov 2026, morning): phases 1 to 4 done, live check pending

Resumed at 07:25 after a Windows Update restart killed the first agent at 02:27 (the port was uncommitted then).

- **Built:** `ev-charging.exe` and `ev-charging-background.exe`, Native AOT, about 11 MB each, no AOT warnings.
  CI (`.github/workflows/build.yml`) builds, runs the tests (the mock-form tests in a real headless Edge), publishes
  both exes and runs the AOT parity runner on the goldens.
- **Tests (45):** the 28 Python tests by name; the 400 oracle scenarios; 876 settings, template, rename, URL, body
  text, label, hook and status goldens; argparse's 24 texts and exit codes; door and lock tests; six tests filling the
  local mock form over DevTools (every field kind, the picker quirk, trusted input only, preview, a refusing door, a
  changed form, a missing option, a busy profile).
- **Parity through the AOT runner (`tests/parity/Program.cs`):** the goldens, and generated scenarios from the oracle
  (see the commit messages and the report for the counts).
- **The form:** proven against the local mock only. **The real Forms URL has never been opened** and nothing was
  submitted. Phase 5 (GJ at the console) is the first time the real form is filled by the .NET exe.

### Deliberate differences

| # | Python | .NET |
|---|---|---|
| 1 | An invalid pattern is refused with sre's wording | The same refusal, with PyRegex's wording after "isn't a valid regular expression:" |
| 2 | Headless, a Microsoft page needing a person waits 5 minutes | Stops at once with a FormError (decision 4) |
| 3 | No profile lock: a run and a transcript fetch can collide | `edge-profile-{key}`, waits up to 10 minutes (decision 5) |
| 4 | An option not in the list: Playwright's TimeoutError after 30 s (a traceback from `book`) | A FormError naming the option; two matches is a FormError (Playwright's strict mode) |
| 5 | A car file that isn't a JSON object fails later (AttributeError, escaping the run) | A FormError when it's read, so the booking fails and is raised as a question |
| 6 | A car file's non-text `start_time` is stored as it is | Stored as Python prints it (text) |
| 7 | `book` takes any `date.fromisoformat` form | `YYYY-MM-DD` only |
| 8 | Anything unexpected: a traceback, exit 1 | `Type: message`, exit 1 |
| 9 | Templates and `[approvals]` types fail mid-run | Refused when the settings load (decisions 6 and 7) |
| 10 | The state file's swap (`os.replace`) fails at once on a transient access denied | Retried up to 5 times; the bytes written are the same |

### Python bugs found (carried for parity, GJ's to fix)

- **A sign-in failure during planning is reported as "couldn't read your calendar"**, never "needs a sign-in":
  `plan()` wraps every exception in CalendarError, so `_run`'s SignInNeeded branch is unreachable for it.
- **`record_submission` is outside `_book`'s try.** If the state file can't be replaced after a request was
  submitted (seen as a transient PermissionError from `os.replace`, 2 in 10,000 oracle runs, most likely antivirus
  on the temp file), the run fails and the day isn't recorded, so the next run requests it again.
- **`apply_approvals` returns early on any error**, so approvals already read in that run aren't saved (renames
  already made stay).
- **A car file that's a JSON list** crashes the whole run (AttributeError in `start_time`, outside the try).
