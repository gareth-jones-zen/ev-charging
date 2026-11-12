"""The settings file: where the form is, what in your calendar asks for a slot, and how approvals read.

Everything specific to an organisation or a person lives here, never in the code. See TEMPLATE below
(also what `ev-charging config` writes) and the README for what each setting does.

Paths may use ~ and %VARIABLES%; relative paths are relative to the settings file.
"""

from __future__ import annotations

import os
import pathlib
import re
import tomllib
from dataclasses import dataclass, field

APPDATA = pathlib.Path(os.environ.get("APPDATA", pathlib.Path.home()))
LOCAL = pathlib.Path(os.environ.get("LOCALAPPDATA", pathlib.Path.home())) / "ev-charging"
DEFAULT_CONFIG = APPDATA / "ev-charging" / "config.toml"

TEMPLATE = """\
# ev-charging settings. See the README for what each one does.

login_hint = "you@example.com"         # your work sign-in, for Microsoft Graph
# tenant = "organizations"             # or your organisation's tenant id
# timezone = "GMT Standard Time"       # Windows name of your time zone, for calendar times
# car_file = "%LOCALAPPDATA%/ev-charging/car.json"   # your car's details: keep it out of any repo
# daily_at = "09:00"                   # when autostart's daily run happens (and at logon)

[booking]
days_ahead = 14          # how far ahead to look
start_time = "08:00"     # the slot you ask for
slot_hours = 4           # how long a slot lasts
notice_days = 1          # the first day considered is today + this
weekdays_only = true

[trigger]
# A calendar entry whose subject matches `entry` is the day's travel block; it asks for a slot when its
# subject also matches `book`. Both are case-insensitive regular expressions.
entry = '^\\s*travel'
book = '\\bev\\b'
label = "Travel block"   # what to call it in questions
work_plan = "office"     # the Outlook work plan must say this to book; "" to ignore the work plan

[form]
url = "https://forms.office.com/Pages/ResponsePage.aspx?id=YOUR-FORM-ID"
questions = 6            # how many questions the form has: anything else stops the booking
confirmation = "response was submitted|Your response was|Thanks"

# One [[form.field]] per question to fill, by its position on the form (1 is the first).
# type: date, choice (a dropdown) or text. value: {date:...}, {start}, or any key in car_file.
[[form.field]]
question = 1
type = "date"
value = "{date:%d/%m/%Y}"

[[form.field]]
question = 2
type = "choice"
value = "{start}"

[[form.field]]
question = 3
type = "text"
value = "{car_make}"

[approvals]
sender = "facilities@example.com"
subject = "electric car charging request"
approved = "APPROVED"
declined = "DECLIN|REJECT"
date = 'Date\\s*-\\s*(\\d{4}-\\d{2}-\\d{2})'
start = 'Start Time\\s*-\\s*(\\d{1,2}:\\d{2})'
point = 'assigned;\\s*(.+?\\))'
# charge_points = ["Charger 1"]   # optional: names to look for if `point` finds nothing
rename = "Travel; EV – {point}, {start}–{end}"
"""


@dataclass
class Field:
    question: int          # 1-based position on the form
    type: str              # date | choice | text
    value: str             # a str.format template: {date:...}, {start}, or a car_file key


@dataclass
class Form:
    url: str = ""
    questions: int = 0     # 0: don't check
    confirmation: str = "response was submitted|Your response was|Thanks"
    fields: list[Field] = field(default_factory=list)


@dataclass
class Approvals:
    enabled: bool = False
    sender: str = ""
    subject: str = ""
    approved: str = "APPROVED"
    declined: str = "DECLIN|REJECT"
    date: str = r"Date\s*-\s*(\d{4}-\d{2}-\d{2})"
    start: str = r"Start Time\s*-\s*(\d{1,2}:\d{2})"
    point: str = r"assigned;\s*(.+?\))"
    charge_points: list[str] = field(default_factory=list)
    look_back_days: int = 60
    rename: str = "Travel; EV – {point}, {start}–{end}"
    morning_before: int = 12   # the entry to rename starts before this hour
    set_location: bool = True
    chase_next_working_day: bool = True


@dataclass
class Config:
    path: pathlib.Path
    login_hint: str | None = None
    tenant: str = "organizations"
    msal_cache: pathlib.Path | None = None
    timezone: str = "GMT Standard Time"
    state_file: pathlib.Path = LOCAL / "state.json"
    log_file: pathlib.Path = LOCAL / "ev-charging.log"
    car_file: pathlib.Path = LOCAL / "car.json"
    edge_profile: pathlib.Path = LOCAL / "edge-profile"
    screenshots: pathlib.Path = LOCAL / "screenshots"
    after_run: list = field(default_factory=list)
    daily_at: str = "09:00"
    days_ahead: int = 14
    start_time: str = ""
    slot_hours: float = 4
    notice_days: int = 1
    weekdays_only: bool = True
    entry: re.Pattern = re.compile(r"^\s*travel", re.I)
    book: re.Pattern = re.compile(r"\bev\b", re.I)
    label: str = "Travel block"
    work_plan: str = "office"
    form: Form = field(default_factory=Form)
    approvals: Approvals = field(default_factory=Approvals)


def _path(value: str, base: pathlib.Path) -> pathlib.Path:
    path = pathlib.Path(os.path.expandvars(os.path.expanduser(value)))
    return path if path.is_absolute() else (base / path).resolve()


def _regex(value: str, where: str) -> re.Pattern:
    try:
        return re.compile(value, re.I)
    except re.error as error:
        raise ValueError(f"{where} isn't a valid regular expression: {error}") from None


def _time(value: str, where: str) -> str:
    if not re.fullmatch(r"([01]\d|2[0-3]):[0-5]\d", value):
        raise ValueError(f"{where} should be a time such as \"08:00\", not {value!r}.")
    return value


def parse(raw: dict, path: pathlib.Path) -> Config:
    base = path.parent
    paths = {key: _path(raw[key], base) for key in
             ("msal_cache", "state_file", "log_file", "car_file", "edge_profile", "screenshots") if raw.get(key)}
    booking, trigger = raw.get("booking", {}), raw.get("trigger", {})
    config = Config(path=path, login_hint=raw.get("login_hint"), tenant=raw.get("tenant", "organizations"),
                    timezone=raw.get("timezone", "GMT Standard Time"), after_run=list(raw.get("after_run", [])),
                    daily_at=_time(str(raw.get("daily_at", "09:00")), "daily_at"), **paths)
    config.days_ahead = int(booking.get("days_ahead", 14))
    config.start_time = _time(str(booking["start_time"]), "[booking] start_time") if booking.get("start_time") else ""
    config.slot_hours = float(booking.get("slot_hours", 4))
    config.notice_days = int(booking.get("notice_days", 1))
    config.weekdays_only = bool(booking.get("weekdays_only", True))
    if config.days_ahead < 1 or config.notice_days < 0:
        raise ValueError(f"[booking] in {path}: days_ahead must be at least 1 and notice_days at least 0.")
    config.entry = _regex(trigger.get("entry", r"^\s*travel"), "[trigger] entry")
    config.book = _regex(trigger.get("book", r"\bev\b"), "[trigger] book")
    config.label = trigger.get("label", "Travel block")
    config.work_plan = str(trigger.get("work_plan", "office")).strip()

    form = raw.get("form", {})
    config.form = Form(url=form.get("url", ""), questions=int(form.get("questions", 0)),
                       confirmation=form.get("confirmation", Form.confirmation))
    _regex(config.form.confirmation, "[form] confirmation")
    for item in form.get("field", []):
        kind = item.get("type", "text")
        if kind not in ("date", "choice", "text") or not item.get("question") or "value" not in item:
            raise ValueError(f"A [[form.field]] in {path} needs question (a number), type (date, choice or text) "
                             f"and value: {item}")
        config.form.fields.append(Field(question=int(item["question"]), type=kind, value=str(item["value"])))
    if config.form.questions and any(f.question > config.form.questions for f in config.form.fields):
        raise ValueError(f"A [[form.field]] in {path} names a question beyond [form] questions = {config.form.questions}.")

    approvals = raw.get("approvals")
    if approvals:
        if not approvals.get("sender") or not approvals.get("subject"):
            raise ValueError(f"[approvals] in {path} needs sender and subject.")
        config.approvals = Approvals(enabled=True, **{k: approvals[k] for k in (
            "sender", "subject", "approved", "declined", "date", "start", "point", "charge_points",
            "look_back_days", "rename", "morning_before", "set_location", "chase_next_working_day") if k in approvals})
        for key in ("approved", "declined", "date", "start", "point"):
            _regex(getattr(config.approvals, key), f"[approvals] {key}")
    return config


def load(path: os.PathLike | str | None = None) -> Config:
    path = pathlib.Path(path or os.environ.get("EV_CHARGING_CONFIG") or DEFAULT_CONFIG).resolve()
    if not path.exists():
        raise FileNotFoundError(f"No settings file at {path}. Run `ev-charging config` to create one.")
    return parse(tomllib.loads(path.read_text(encoding="utf-8")), path)


def create_default() -> pathlib.Path:
    if not DEFAULT_CONFIG.exists():
        DEFAULT_CONFIG.parent.mkdir(parents=True, exist_ok=True)
        DEFAULT_CONFIG.write_text(TEMPLATE, encoding="utf-8")
    return DEFAULT_CONFIG
