"""The Python ev-charging as an oracle for the .NET port.

Run against this clone's copy of the Python (src/ev_charging, v0.1.1), never the live checkout, with
PYTHONDONTWRITEBYTECODE=1 so nothing is written beside the source:

    python tests/parity/oracle.py scenarios <count> <seed> <out.json>   # generated scenarios and what Python did
    python tests/parity/oracle.py misc <out.json>                       # settings, templates, URLs, text, hook, CLI
    python tests/parity/oracle.py replay <recording.json> <out.json>    # a recorded live read, replayed

No network and no browser: Outlook is a fake (or the real Outlook class over a fake Graph serving a recording), the
booker is a fake, and datetime.now is frozen. Every file is written under a temporary folder.
"""

from __future__ import annotations

import contextlib
import datetime as dt
import io
import json
import pathlib
import random
import sys
import tempfile
import tomllib

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))

from ev_charging import cli, config as cfg, form, outlook as outlook_mod, planner, run, state  # noqa: E402
from m365_graph import SignInNeeded  # noqa: E402

# ---- fakes ----------------------------------------------------------------------------------------------------


class Failure(OSError):
    """What a fake raises: str() is the message, as an OSError's is."""


class FakeOutlook:
    """The tests' FakeOutlook, with failures to order and every call recorded."""

    def __init__(self, scenario: dict):
        self.plan, self.calendar, self.messages = scenario["plan"], scenario["events"], scenario["messages"]
        self.fail = scenario.get("fail") or {}
        self.calls: list = []
        self.renamed: list = []
        self.event_calls = 0

    def signed_out(self):
        """fail["sign_in"]: every Graph read finds no usable sign-in, as a lapsed sign-in would."""
        if "sign_in" in self.fail:
            raise SignInNeeded(self.fail["sign_in"])

    def work_plan(self, start, end):
        self.calls.append(["work_plan", start.isoformat(), end.isoformat()])
        self.signed_out()
        if "work_plan" in self.fail:
            raise Failure(self.fail["work_plan"])
        return [s for s in self.plan if start.isoformat() <= s["start"]["dateTime"][:10] < end.isoformat()]

    def events(self, start, end):
        self.calls.append(["events", start.isoformat(), end.isoformat()])
        self.event_calls += 1
        self.signed_out()
        if "events" in self.fail or ("events_later" in self.fail and self.event_calls > 1):
            raise Failure(self.fail.get("events") or self.fail["events_later"])
        return [e for e in self.calendar if start.isoformat() <= e["start"]["dateTime"][:10] < end.isoformat()]

    def messages_from(self, sender, since):
        self.calls.append(["messages_from", sender, since.isoformat()])
        self.signed_out()
        if "messages" in self.fail:
            raise Failure(self.fail["messages"])
        return self.messages

    def rename(self, event_id, subject, location):
        self.calls.append(["rename", event_id])
        if "rename" in self.fail:
            raise Failure(self.fail["rename"])
        self.renamed.append([event_id, subject, location])


def booker_for(scenario: dict, calls: list):
    outcomes = scenario.get("booker") or {}

    def booker(config, day):
        calls.append(day.isoformat())
        outcome = outcomes.get(day.isoformat(), {"kind": "return", "text": f"Submitted: {day:%a %d %b %Y}, 08:00."})
        if outcome["kind"] == "raise":
            raise {"FormError": form.FormError, "TimeoutError": TimeoutError}[outcome["type"]](outcome["text"])
        return outcome["text"]

    return booker


@contextlib.contextmanager
def frozen_now(when: str):
    """state.record_submission's datetime.now(), frozen."""
    fixed = dt.datetime.fromisoformat(when)

    class Frozen(dt.datetime):
        @classmethod
        def now(cls, tz=None):
            return fixed

    class Shim:
        datetime = Frozen
        date = dt.date
        timedelta = dt.timedelta

    saved = state.dt
    state.dt = Shim
    try:
        yield
    finally:
        state.dt = saved


def configure(scenario: dict, folder: pathlib.Path) -> cfg.Config:
    path = folder / "config.toml"
    path.write_text(scenario["settings"], encoding="utf-8")
    config = cfg.parse(tomllib.loads(scenario["settings"]), path)
    config.state_file = folder / "state.json"
    config.log_file = folder / "log.txt"
    config.car_file = folder / "car.json"
    config.after_run = []
    if scenario.get("state") is not None:
        config.state_file.write_bytes(scenario["state"].encode("utf-8"))
    if scenario.get("car") is not None:
        config.car_file.write_bytes(scenario["car"].encode("utf-8"))
    return config


def outcome(config: cfg.Config, scenario: dict, outlook, booked: list, lines: list, report=None, error=None) -> dict:
    saved = config.state_file.read_bytes().decode("utf-8") if config.state_file.exists() else None
    return {"report": report, "error": error, "log": lines, "renamed": getattr(outlook, "renamed", []),
            "calls": getattr(outlook, "calls", []), "booked_calls": booked, "state": saved,
            "summary": run.summary(report) if report and "booked" in report else None}


def run_scenario(scenario: dict, outlook_factory=None) -> dict:
    with tempfile.TemporaryDirectory() as tmp:
        folder = pathlib.Path(tmp)
        config = configure(scenario, folder)
        outlook = outlook_factory(config) if outlook_factory else FakeOutlook(scenario)
        lines, booked = [], []
        today = dt.date.fromisoformat(scenario["today"])
        with frozen_now(scenario["now"]):
            try:
                if scenario.get("approvals_call"):
                    report = {"questions": run.apply_approvals(config, outlook, today, scenario["dry_run"], lines.append)}
                else:
                    report = run.run(config, outlook, today=today, dry_run=scenario["dry_run"],
                                     approvals_only=scenario["approvals_only"], log=lines.append,
                                     booker=booker_for(scenario, booked))
            except run.CalendarError as error:
                return outcome(config, scenario, outlook, booked, lines, error={"type": "CalendarError", "text": str(error)})
            except Exception as error:   # anything else escaping run() is a parity point too
                return outcome(config, scenario, outlook, booked, lines,
                               error={"type": type(error).__name__, "text": str(error)})
        return outcome(config, scenario, outlook, booked, lines, report=report)


# ---- scenario generation -------------------------------------------------------------------------------------

SUBJECTS = ["Travel", "Travel; EV", "travel ev", "  Travel; Book EV charger", "Travel; Bevan's leaving do", "Travel; EVENT",
            "Travel – EV Charging 1 (Rear CP), 08:00–12:00", "Travel; EV – Charger 1 (Rear), 08:00–12:00", "Standup",
            "Time to travel", "TRAVEL; ev", "Travel;EV", "Travel/EV", "Travel_EV", "Travel EVs", "Trävel EV", "Travel; ÉV",
            None, "", "Travel 🚗 EV", "\tTravel\u00a0EV", "Office, charge car", "Travel ev\u0301", "In the office; EV",
            "Travel\u2028EV", "Holiday (annual leave)"]
KINDS = ["office", "remote", "timeOff", "unknown", "Office", "OFFICE", None, "remote"]
POINTS = ["Charger 1 (Rear)", "Charger 2 (Front)", "EV Charging 1 (Rear CP)", "EV Charging 2 (Front CP)", "  Spaced (Bay 3)  ",
          "Ünïcode (Bay)", "No brackets"]
RENAMES = ["Travel; EV – {point}, {start}–{end}", "Travel – {point}, {start}–{end}", "Travel ({point}) {date:%d/%m} {start}-{end}",
           "{date} {point}", "{date:%a %d %b %Y} – {point}", "Travel {{EV}} {point}", "{point}"]


def iso(day: dt.date) -> str:
    return day.isoformat()


def toml_str(text: str) -> str:
    return json.dumps(text, ensure_ascii=False)   # a TOML basic string is JSON-compatible for these


def settings_text(r: random.Random) -> str:
    lines = ['login_hint = "you@example.com"']
    booking = [f"days_ahead = {r.choice([1, 3, 7, 14, 14, 14, 21])}",
               f"notice_days = {r.choice([0, 1, 1, 1, 2, 3])}",
               f"slot_hours = {r.choice(['4', '4.0', '3.5', '2.25', '1', '0.5', '6'])}",
               f"weekdays_only = {r.choice(['true', 'true', 'false'])}"]
    if r.random() < 0.8:
        booking.append(f'start_time = "{r.choice(["08:00", "07:30", "09:15"])}"')
    trigger = []
    if r.random() < 0.2:
        trigger.append(r.choice(["entry = 'office'", "entry = '^\\s*(travel|office)'"]))
    if r.random() < 0.2:
        trigger.append(r.choice(["book = 'charge'", "book = 'charge|\\bev\\b'"]))
    if r.random() < 0.3:
        trigger.append(f'label = {toml_str(r.choice(["Day block", "Travel block", "Trajet"]))}')
    trigger.append(f'work_plan = {toml_str(r.choice(["office", "office", "office", "", "Office", "remote", " office "]))}')
    text = "\n".join(lines) + "\n\n[booking]\n" + "\n".join(booking) + "\n\n[trigger]\n" + "\n".join(trigger) + "\n"
    text += ('\n[form]\nurl = "file:///mock"\nquestions = 6\n\n[[form.field]]\nquestion = 1\ntype = "date"\n'
             'value = "{date:%d/%m/%Y}"\n')
    if r.random() < 0.85:
        approvals = ['sender = "facilities@example.com"', 'subject = "electric car charging request"',
                     f"rename = {toml_str(r.choice(RENAMES))}",
                     f"morning_before = {r.choice([9, 10, 12, 12, 12, 14])}",
                     f"set_location = {r.choice(['true', 'true', 'false'])}",
                     f"chase_next_working_day = {r.choice(['true', 'true', 'false'])}",
                     f"look_back_days = {r.choice([30, 60])}"]
        if r.random() < 0.4:
            approvals.append(f"charge_points = [{', '.join(toml_str(p) for p in r.sample(POINTS, 2))}]")
        if r.random() < 0.15:
            approvals.append("point = 'nomatch(x)'")
        if r.random() < 0.1:
            approvals.append("declined = 'NOPE'")
        text += "\n[approvals]\n" + "\n".join(approvals) + "\n"
    return text


def reply(r: random.Random, day: dt.date, today: dt.date) -> dict:
    subject = r.choice(["Your Electric Car Charging Request - APPROVED", "Your Electric Car Charging Request - DECLINED",
                        "Your Electric Car Charging Request - REJECTED", "RE: Your electric car charging request",
                        "your electric car charging request - approved", "Something else", None,
                        "Your Electric Car Charging Request - APPROVED", "Your Electric Car Charging Request - APPROVED"])
    start = r.choice(["08:00", "8:00", "09:15", "07:30", "08:00"])
    point = r.choice(POINTS)
    sep = r.choice([" - ", "-", " -  ", "&nbsp;- "])
    body = r.choice([
        f"<p>Date{sep}{iso(day)}</p><p>Start Time - {start}</p><p>End Time - {today.month}/{today.day}/{today.year} 10:00</p>"
        f"<p>You have been assigned; {point}</p>",
        f"<html><body><div>Date - {iso(day)}<br>Start Time - {start}<br>\n\nYou have been assigned;\n {point}"
        f" &amp; more &#8211; text</div></body></html>",
        f"Date - {iso(day)} Start Time - {start} assigned; {point}",
        f"Date - {iso(day)} no start here",
        "nothing at all",
        f"<p>DATE - {iso(day)}</p><p>start time - {start}</p><p>Assigned; {point}</p><p>Also {r.choice(POINTS)}</p>",
    ])
    received = (dt.datetime.combine(today, dt.time(9)) - dt.timedelta(hours=r.randint(0, 24 * 20))).strftime("%Y-%m-%dT%H:%M:%SZ")
    message = {"subject": subject, "receivedDateTime": r.choice([received, received, "2026-11-10T10:00:00Z"]),
               "body": {"content": body}}
    if r.random() < 0.03:
        del message["receivedDateTime"]
    if r.random() < 0.03:
        message["body"] = None
    return message


def scenario(r: random.Random, index: int) -> dict:
    today = dt.date(2026, 1, 1) + dt.timedelta(days=r.randint(0, 730))
    days = [today + dt.timedelta(days=n) for n in range(-3, 25)]
    plan, events = [], []
    for day in days:
        if r.random() < 0.6:
            for _ in range(r.choice([1, 1, 2])):
                segment = {"start": {"dateTime": f"{iso(day)}T{r.choice(['09', '13', '00'])}:00:00.0000000"}}
                kind = r.choice(KINDS)
                if kind is not None:
                    segment["workLocationType"] = kind
                plan.append(segment)
        for _ in range(r.choice([0, 0, 1, 1, 2, 3])):
            event = {"id": f"e{len(events)}", "start": {"dateTime": f"{iso(day)}T{r.randint(5, 20):02d}:{r.choice([0, 30]):02d}:00.0000000"}}
            subject = r.choice(SUBJECTS)
            if subject is not None:
                event["subject"] = subject
            events.append(event)
    messages = [reply(r, r.choice(days + [today - dt.timedelta(days=5)]), today) for _ in range(r.choice([0, 1, 2, 4, 6]))]
    requested = {}
    for day in r.sample(days, r.choice([0, 0, 1, 2, 4])):
        entry = {"start": "08:00", "submitted": f"{iso(today)}T08:00:0{r.randint(0, 9)}"}
        if r.random() < 0.4:
            entry["approved"] = r.choice(POINTS)
        if r.random() < 0.1:
            entry = {}
        requested[iso(day)] = entry
    state_text = None
    if requested or r.random() < 0.3:
        state_text = json.dumps(requested, indent=r.choice([None, 2]), sort_keys=r.choice([True, False]),
                                ensure_ascii=r.choice([True, False]))
    car = r.choice([None, json.dumps({"car_make": "Make", "start_time": "09:00"}), json.dumps({"car_make": "Make"}),
                    "not json", json.dumps({"start_time": "07:45"})])
    booker = {}
    for day in days:
        roll = r.random()
        if roll < 0.08:
            booker[iso(day)] = {"kind": "return", "text": "Clicked Submit but didn't see a confirmation. Check C:\\shots\\x.png"}
        elif roll < 0.12:
            booker[iso(day)] = {"kind": "raise", "type": "FormError", "text": "Expected 6 questions, found 7. The form has changed; not filling it."}
        elif roll < 0.14:
            booker[iso(day)] = {"kind": "raise", "type": "TimeoutError", "text": "x" * 150}
        elif roll < 0.16:
            booker[iso(day)] = {"kind": "return", "text": "Preview only, nothing submitted: x.png"}
    fail = {}
    roll = r.random()
    if roll < 0.03:
        fail["work_plan"] = "network down"
    elif roll < 0.06:
        fail["events"] = "events down"
    elif roll < 0.09:
        fail["events_later"] = "later events down " + "é" * 130
    elif roll < 0.12:
        fail["messages"] = "mail down"
    elif roll < 0.15:
        fail["rename"] = "rename refused"
    mode = r.random()
    return {"name": f"s{index}", "today": iso(today), "now": f"{iso(today)}T08:0{r.randint(0, 9)}:3{r.randint(0, 9)}",
            "settings": settings_text(r), "plan": plan, "events": events, "messages": messages, "state": state_text,
            "car": car, "booker": booker, "fail": fail, "dry_run": mode < 0.25, "approvals_only": 0.25 <= mode < 0.35,
            "approvals_call": 0.35 <= mode < 0.42}


def extend(s: dict, x: random.Random) -> None:
    """Cases added with the 0.1.1 fixes, from their own random stream so every other scenario stays as it was: a
    sign-in needed (every Graph read), and a car file that's a JSON list (which crashed 0.1.0 when the settings set
    no start_time)."""
    if x.random() < 0.04:
        s["fail"] = {"sign_in": "No usable sign-in for these scopes. At a console, run: ev-charging sign-in"}
    if x.random() < 0.06:
        s["car"] = x.choice(["[1, 2]", '"x"', "null", "3"])


def scenarios(count: int, seed: int) -> list:
    r = random.Random(seed)
    found = []
    for index in range(count):
        s = scenario(r, index)
        extend(s, random.Random(f"0.1.1-{seed}-{index}"))
        s["expected"] = run_scenario(s)
        found.append(s)
    return found


# ---- misc goldens --------------------------------------------------------------------------------------------

BAD_SETTINGS = [
    "[booking]\nstart_time = '8am'\n", "[trigger]\nbook = '('\n", "[approvals]\nsender = 'x@example.com'\n",
    "daily_at = '24:00'\n", "daily_at = '9:00'\n", "[booking]\ndays_ahead = 0\n", "[booking]\nnotice_days = -1\n",
    "[[form.field]]\nquestion = 1\ntype = 'bogus'\nvalue = 'x'\n", "[[form.field]]\ntype = 'text'\nvalue = 'x'\n",
    "[[form.field]]\nquestion = 1\ntype = 'text'\n", "[form]\nquestions = 2\n[[form.field]]\nquestion = 3\nvalue = 'x'\n",
    "[form]\nconfirmation = '['\n", "[approvals]\nsender = 'a'\nsubject = 'b'\ndate = '('\n",
    "[[form.field]]\nquestion = 1\nvalue = 'x'\ntype = 'text'\nextra = [1, 2.5, true, 'é']\n",
    "[[form.field]]\nquestion = 0\nvalue = 'x'\n", "[booking]\nstart_time = '23:59'\nslot_hours = 2\n",
    "[booking]\nstart_time = ''\n", "", "login_hint = 'me'\ntenant = 'abc'\ntimezone = 'UTC'\nafter_run = ['a', 'b']\n",
    "[trigger]\nwork_plan = '  Office  '\nlabel = 'L'\n", "[approvals]\nsender = 'a'\nsubject = 'b'\ncharge_points = ['x']\n",
    "state_file = 'sub/state.json'\nlog_file = '%LOCALAPPDATA%/x/log.txt'\ncar_file = '~/car.json'\n",
    "[[form.field]]\nquestion = 2\ntype = 'choice'\nvalue = 5\n",
]


def config_summary(config: cfg.Config, folder: pathlib.Path) -> dict:
    def p(path):
        text = str(path)
        return text.replace(str(folder), "<DIR>")
    a = config.approvals
    return {"path": p(config.path), "login_hint": config.login_hint, "tenant": config.tenant, "timezone": config.timezone,
            "state_file": p(config.state_file), "log_file": p(config.log_file), "car_file": p(config.car_file),
            "after_run": config.after_run, "daily_at": config.daily_at, "days_ahead": config.days_ahead,
            "start_time": config.start_time, "slot_hours": repr(config.slot_hours), "notice_days": config.notice_days,
            "weekdays_only": config.weekdays_only, "entry": config.entry.pattern, "book": config.book.pattern,
            "label": config.label, "work_plan": config.work_plan, "form_url": config.form.url,
            "questions": config.form.questions, "confirmation": config.form.confirmation,
            "fields": [[f.question, f.type, f.value] for f in config.form.fields],
            "approvals": [a.enabled, a.sender, a.subject, a.approved, a.declined, a.date, a.start, a.point, a.charge_points,
                          a.look_back_days, a.rename, a.morning_before, a.set_location, a.chase_next_working_day]}


def misc() -> dict:
    out: dict = {}
    with tempfile.TemporaryDirectory() as tmp:
        folder = pathlib.Path(tmp)
        path = folder / "x.toml"
        settings = []
        for text in BAD_SETTINGS + [cfg.TEMPLATE]:
            try:
                config = cfg.parse(tomllib.loads(text), path)
                settings.append({"toml": text, "ok": config_summary(config, folder)})
            except ValueError as error:
                settings.append({"toml": text, "error": str(error).replace(str(folder), "<DIR>")})
        out["settings"] = settings

        config = cfg.parse(tomllib.loads(cfg.TEMPLATE), path)
        r = random.Random(7)
        templates = []
        details_list = [{"car_make": "Make", "car_model": "Mödel", "car_registration": "AB12 CDE", "additional_information": ""},
                        {"car_make": "Make"}, {"car_make": 5, "car_model": 2.5, "car_registration": True, "additional_information": None},
                        {"car_make": " "}]
        values = ["{date:%d/%m/%Y}", "{start}", "{car_make}", "{car_model}", "{car_registration}", "{additional_information}",
                  "{date}", "{date:%a %d %b %Y}", "{{literal}} {start}", "{date:%A %B %y %H:%M:%S %I %p %%}", "plain", "",
                  "{car_make} and {car_model}"]
        for _ in range(200):
            fields = [cfg.Field(question=n + 1, type=r.choice(["date", "choice", "text"]), value=r.choice(values))
                      for n in range(r.randint(1, 6))]
            config.form.fields = fields
            day = dt.date(2026, 1, 1) + dt.timedelta(days=r.randint(0, 900))
            start = r.choice(["08:00", "9:15", ""])
            details = r.choice(details_list)
            try:
                result = [list(v) for v in form.values(config, day, start, details)]
                templates.append({"fields": [[f.question, f.type, f.value] for f in fields], "day": iso(day), "start": start,
                                  "details": details, "ok": result})
            except form.FormError as error:
                templates.append({"fields": [[f.question, f.type, f.value] for f in fields], "day": iso(day), "start": start,
                                  "details": details, "error": str(error)})
        out["templates"] = templates

        starts = []
        for start_setting in ("", "09:30"):
            for details in ({"start_time": "07:00"}, {}, {"start_time": ""}):
                config.start_time = start_setting
                starts.append({"setting": start_setting, "details": details, "start": form.start_time(config, details)})
        out["start_time"] = starts

        renames = []
        for template in RENAMES + ["{point:>5}", "{nope}", "{date:%d/%m} {start}"]:
            for hours in (4, 3.5, 0.5, 2.25, 13, 1 / 3):
                for start in ("08:00", "8:00", "23:30", "00:00"):
                    config.approvals.rename = template
                    config.slot_hours = hours
                    rep = {"start": start, "point": "Charger 1 (Rear)", "date": dt.date(2026, 11, 24)}
                    try:
                        renames.append({"template": template, "hours": repr(hours), "start": start, "ok": planner.renamed(config, rep)})
                    except Exception as error:
                        renames.append({"template": template, "hours": repr(hours), "start": start,
                                        "error": f"{type(error).__name__}: {error}"})
        out["renames"] = renames

        bodies = ["<p>a&nbsp;b</p>", "&amp;&lt;&gt;&#8211;&#x2013;&notanentity;&amp", "x<br/>y\n\n\tz", "<a href='x'>link</a> text",
                  "a <b", "\u00a0\u2028\u3000 spaced \x1c\x1f", "", "<!-- c -->d", "&nbsp&nbsp;e"]
        out["body_text"] = [{"in": b, "out": planner.body_text({"body": {"content": b}})} for b in bodies]

        labels = []
        for n in range(0, 800, 3):
            day = dt.date(2025, 12, 25) + dt.timedelta(days=n)
            labels.append({"day": iso(day), "label": planner.label(day), "next": iso(planner.next_working_day(day)),
                           "status": f"{day:%a %d %b}", "submitted": f"{day:%a %d %b %Y}"})
        out["labels"] = labels

        class Graph:
            def __init__(self):
                self.calls = []

            def request(self, url, method="GET", body=None, headers=None):
                self.calls.append({"url": url, "method": method, "body": body, "headers": headers})
                return {"value": []}

        urls = []
        senders = ["facilities@example.com", "o'brien@example.com", "a+b@example.com", "space y@example.com", "ünï@example.com",
                   "x&y=z#%/?@example.com"]
        for n in range(30):
            config.timezone = r.choice(["GMT Standard Time", "UTC", "Pacific Standard Time"])
            graph = Graph()
            o = outlook_mod.Outlook(config, graph)
            start = dt.date(2026, 1, 1) + dt.timedelta(days=r.randint(0, 700))
            end = start + dt.timedelta(days=r.randint(1, 20))
            o.work_plan(start, end)
            o.events(start, end)
            o.messages_from(r.choice(senders), start)
            o.rename(f"AAMk{n}=/+", "Travel – EV Charging 1 (Rear CP), 08:00–12:00", r.choice(["EV Charging 1 (Rear CP)", None, ""]))
            urls.append({"timezone": config.timezone, "start": iso(start), "end": iso(end), "calls": graph.calls})
        out["urls"] = urls

        hooks = []
        for report in ({"questions": [], "booked": [], "failed": []},
                       {"questions": ["Thu 26 Nov: Travel block says EV but work plan says remote. Book EV?", "é – 🚗"],
                        "booked": ["2026-11-26"], "failed": ["2026-11-27"]}):
            config.path = pathlib.Path("C:\\Users\\someone\\settings\\ev é.toml")
            hooks.append({"report": report, "payload": json.dumps({"config": str(config.path), "questions": report["questions"],
                                                                  "booked": report["booked"], "failed": report["failed"],
                                                                  "summary": run.summary(report)}),
                          "summary": run.summary(report)})
        out["hooks"] = hooks

        statuses = []
        for state_obj in ({}, {"2026-11-10": {"start": "08:00"}, "2026-11-12": {"start": "08:00", "approved": "Charger 1 (Rear)"},
                               "2026-11-13": {}, "2026-11-20": {"start": "7:00", "submitted": "x"}, "2026-12-01": {"approved": ""}}):
            state_path = folder / "status-state.json"
            state.save(state_path, state_obj)
            config.state_file = state_path

            class Day(dt.date):
                @classmethod
                def today(cls):
                    return dt.date(2026, 11, 12)

            class Shim:
                date = Day
                datetime = dt.datetime

            saved = cli.dt
            cli.dt = Shim
            buffer = io.StringIO()
            try:
                with contextlib.redirect_stdout(buffer):
                    code = cli._status(config)
            finally:
                cli.dt = saved
            statuses.append({"state": state_obj, "out": buffer.getvalue(), "code": code,
                             "bytes": state_path.read_bytes().decode("utf-8")})
        out["status"] = statuses
    return out


# ---- replaying a live recording ------------------------------------------------------------------------------


class RecordedGraph:
    """m365_graph's Client.request, served from a recording by URL. A URL not in it is an error, so a URL that
    Python and .NET build differently shows up."""

    def __init__(self, calls: dict):
        self.calls = calls
        self.seen: list = []

    def request(self, url, method="GET", body=None, headers=None):
        if method != "GET":
            raise RuntimeError(f"replay: {method} isn't a read")
        self.seen.append(url)
        if url not in self.calls:
            raise RuntimeError("replay: a URL the recording doesn't have")
        return self.calls[url]


class JournalOutlook(outlook_mod.Outlook):
    """The real Outlook class over the recording, with rename journaled rather than sent."""

    def __init__(self, config, graph):
        super().__init__(config, graph)
        self.renamed: list = []
        self.calls: list = []

    def rename(self, event_id, subject, location):
        self.renamed.append([event_id, subject, location])


def replay(recording: dict) -> dict:
    results = {}
    for name, dry in (("dry", True), ("live", False)):
        scenario = {"settings": recording["settings"], "today": recording["today"], "now": recording["now"],
                    "state": recording.get("state"), "car": None, "booker": {}, "dry_run": dry, "approvals_only": False}
        graph = RecordedGraph(recording["calls"])
        results[name] = run_scenario(scenario, lambda config: JournalOutlook(config, graph))
        results[name]["urls"] = graph.seen
    return results


def main(argv: list) -> int:
    if argv[0] == "scenarios":
        data = scenarios(int(argv[1]), int(argv[2]))
        target = argv[3]
    elif argv[0] == "misc":
        data = misc()
        target = argv[1]
    elif argv[0] == "replay":
        data = replay(json.loads(pathlib.Path(argv[1]).read_text(encoding="utf-8")))
        target = argv[2]
    else:
        print(__doc__)
        return 2
    pathlib.Path(target).write_text(json.dumps(data, indent=1, ensure_ascii=False), encoding="utf-8", newline="\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
