"""The decisions, with no network: which days to book, what to ask, and what the approvals say.

Kept apart from Graph and the browser so every rule can be tested with plain data.

**The decision table**, for each day from today + notice_days for days_ahead days (weekdays only by default):

    work plan says office, entry asks for a slot, not yet requested  -> book it
    entry asks for a slot, work plan doesn't say office              -> ask, don't book
    work plan says office, an entry that doesn't ask, not requested   -> nothing (logged)
    already requested, work plan no longer says office                -> logged only, never raised

It never cancels anything: cancelling means contacting whoever runs the chargers, which is left to you.
With `work_plan = ""` the work plan isn't read, and every day whose entry asks is booked.
"""

from __future__ import annotations

import datetime as dt
import html
import re
from dataclasses import dataclass

from .config import Config


# ---- the calendar ---------------------------------------------------------------------------------

def is_entry(config: Config, subject: str | None) -> bool:
    """The day's travel block (by default, a subject starting "Travel"). A renamed one still counts:
    "Travel; EV – Charger 1 (Rear), 08:00–12:00" starts with Travel too."""
    return bool(config.entry.search(subject or ""))


def asks(config: Config, subject: str | None) -> bool:
    """The entry asks for a slot (by default "EV" as a word, any case: "Travel; EV", "Travel; Book EV charger")."""
    return bool(config.book.search(subject or ""))


def entry_days(config: Config, events: list) -> dict:
    """Each day with an entry -> whether any of that day's entries asks for a slot."""
    days: dict = {}
    for event in events:
        if not is_entry(config, event.get("subject")):
            continue
        day = dt.date.fromisoformat(event["start"]["dateTime"][:10])
        days[day] = days.get(day, False) or asks(config, event.get("subject"))
    return days


def work_plan_days(occurrences: list) -> dict:
    """Day -> the work plan's location types for it. A day can have several segments (office in the
    morning, a timeOff segment for a travel block marked out of office), and any office segment makes
    it an office day."""
    days: dict = {}
    for item in occurrences:
        day = dt.date.fromisoformat(item["start"]["dateTime"][:10])
        days.setdefault(day, set()).add(item.get("workLocationType", "unknown"))
    return days


def window(config: Config, today: dt.date) -> tuple[dt.date, dt.date]:
    """The days considered: [start, end)."""
    start = today + dt.timedelta(days=config.notice_days)
    return start, start + dt.timedelta(days=config.days_ahead)


@dataclass
class Decision:
    day: dt.date
    action: str    # book | ask | log
    text: str


def label(day: dt.date) -> str:
    return f"{day:%a %d %b}"


def decide(config: Config, today: dt.date, plan: dict, entries: dict, state: dict) -> list[Decision]:
    start, end = window(config, today)
    decisions = []
    day = start
    while day < end:
        if not config.weekdays_only or day.weekday() < 5:
            office = (config.work_plan.lower() in {p.lower() for p in plan.get(day, set())}
                      if config.work_plan else True)
            wants = entries.get(day, False)
            requested = day.isoformat() in state
            if office and wants and not requested:
                decisions.append(Decision(day, "book", f"would book {label(day)}"))
            elif wants and not office and not requested:
                where = ", ".join(sorted(plan.get(day, {"nothing set"})))
                decisions.append(Decision(day, "ask", f"{label(day)}: {config.label} says EV but work plan says "
                                                      f"{where}. Book EV?"))
            elif office and not requested and day in entries:
                decisions.append(Decision(day, "log", f"{label(day)}: {config.work_plan or 'a'} day, {config.label} "
                                                      "doesn't say EV: not booked"))
            elif requested and not office:
                # Never raised: cancelling means contacting whoever runs the chargers, which is left to you.
                decisions.append(Decision(day, "log", f"{label(day)}: EV requested, but work plan no longer says "
                                                      f"{config.work_plan} (not raised)"))
        day += dt.timedelta(days=1)
    return decisions


def next_working_day(today: dt.date) -> dt.date:
    day = today + dt.timedelta(days=1)
    while day.weekday() >= 5:
        day += dt.timedelta(days=1)
    return day


# ---- approvals -------------------------------------------------------------------------------------

def is_reply(config: Config, message: dict) -> bool:
    return config.approvals.subject.lower() in (message.get("subject") or "").lower()


def body_text(message: dict) -> str:
    text = re.sub(r"<[^>]+>", " ", (message.get("body") or {}).get("content", ""))
    return re.sub(r"\s+", " ", html.unescape(text))


def parse_reply(config: Config, message: dict) -> dict | None:
    """Date, start time, charge point and outcome from a reply about a request.

    The defaults read "Date - 2026-10-07 … Start Time - 08:00 … End Time - 10/30/2026 … You have been
    assigned; Charger 1 (Rear)". **Their End Time carries the approval date, month first**, a quirk of their
    process, so End Time is never read: the slot is slot_hours from the start."""
    rules = config.approvals
    text = body_text(message)
    day = re.search(rules.date, text, re.I)
    start = re.search(rules.start, text, re.I)
    if not day or not start:
        return None
    found = re.search(rules.point, text, re.I)
    point = found.group(1).strip() if found else None
    if not point:
        point = next((p for p in rules.charge_points if p.lower() in text.lower()), None)
    subject = message.get("subject") or ""
    outcome = ("approved" if re.search(rules.approved, subject, re.I) else
               "declined" if re.search(rules.declined, subject, re.I) else "other")
    return {"date": dt.date.fromisoformat(day.group(1)), "start": start.group(1), "point": point,
            "outcome": outcome, "received": message.get("receivedDateTime", "")}


def latest_replies(config: Config, messages: list, today: dt.date) -> dict:
    """The newest reply for each day from today on, in case a request was revised."""
    latest: dict = {}
    for message in sorted((m for m in messages if is_reply(config, m)),
                          key=lambda m: m.get("receivedDateTime", ""), reverse=True):
        reply = parse_reply(config, message)
        if reply and reply["date"] >= today and reply["date"] not in latest:
            latest[reply["date"]] = reply
    return latest


def renamed(config: Config, reply: dict) -> str:
    """The morning entry's new subject (by default "Travel; EV – Charger 1 (Rear), 08:00–12:00")."""
    start = dt.datetime.strptime(reply["start"], "%H:%M")
    end = start + dt.timedelta(hours=config.slot_hours)
    return config.approvals.rename.format(point=reply["point"], start=f"{start:%H:%M}", end=f"{end:%H:%M}",
                                          date=reply["date"])


def morning_entry(config: Config, events: list, day: dt.date) -> dict | None:
    """The earliest entry on that day starting before morning_before (an evening one is left alone)."""
    mornings = sorted((e for e in events if is_entry(config, e.get("subject"))
                       and e["start"]["dateTime"][:10] == day.isoformat()
                       and int(e["start"]["dateTime"][11:13]) < config.approvals.morning_before),
                      key=lambda e: e["start"]["dateTime"])
    return mornings[0] if mornings else None
