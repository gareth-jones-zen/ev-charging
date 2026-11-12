"""One run: read the calendar, book what's asked for, apply the approvals, and report the questions.

**Questions** are the things only you can decide: an entry asking for a slot on a day the work plan
doesn't say office, a booking that failed, a request declined, or the next working day's request still
unapproved. A scheduled run notifies them (one Windows notification) and passes them to the after_run
hook, so another system can queue them too:

    {"config": "...", "questions": ["Thu 26 Nov: Travel block says EV but work plan says remote. Book EV?"],
     "booked": ["2026-11-26"], "failed": [], "summary": "1 booked, 1 question"}

The hook runs after every run that isn't a dry run, questions or not: each run's questions replace the
last run's, so an empty list means the earlier ones are settled. It runs from the settings file's folder.

Nothing is ever cancelled.
"""

from __future__ import annotations

import datetime as dt
import json
import subprocess

from win_background import NO_WINDOW, exclusive, key
from win_background import logger as background_logger

from . import form, planner, state as state_mod
from .config import LOCAL, Config
from .outlook import Outlook, client


class CalendarError(RuntimeError):
    """The work plan or calendar couldn't be read, so nothing was decided."""


def logger(config: Config, echo: bool = True):
    return background_logger(config.log_file, echo=echo)


def summary(report: dict) -> str:
    return (f"{len(report['booked'])} booked, {len(report['failed'])} failed, "
            f"{len(report['questions'])} question(s)")


def apply_approvals(config: Config, outlook, today: dt.date, dry_run: bool, log=print) -> list:
    """Write each approved charge point into that day's morning entry, and return the questions."""
    rules = config.approvals
    if not rules.enabled:
        return []
    state = state_mod.load(config.state_file)   # re-read: a booking just now adds to it
    questions = []
    try:
        messages = outlook.messages_from(rules.sender, today - dt.timedelta(days=rules.look_back_days))
        for day, reply in sorted(planner.latest_replies(config, messages, today).items()):
            label = planner.label(day)
            if reply["outcome"] == "declined":
                questions.append(f"{label}: EV request DECLINED.")
                continue
            if reply["outcome"] != "approved" or not reply["point"]:
                continue
            subject = planner.renamed(config, reply)
            state.setdefault(day.isoformat(), {})["approved"] = reply["point"]
            event = planner.morning_entry(config, outlook.events(day, day + dt.timedelta(days=1)), day)
            if event is None:
                # Usually a day you're no longer going in. Cancelling is left to you, so it isn't raised.
                log(f"{label}: EV approved ({reply['point']}) but no morning {config.label} to update (not raised)")
                continue
            if event.get("subject") == subject:
                continue
            if dry_run:
                log(f"would update {label} {config.label} -> {subject}")
                continue
            outlook.rename(event["id"], subject, reply["point"] if rules.set_location else None)
            log(f"updated {label} {config.label} -> {subject}")
    except Exception as error:
        log(f"FAILED applying approvals: {error}")
        return [f"Couldn't read the approval emails: {str(error)[:120]}"]
    if rules.chase_next_working_day:
        nearest = planner.next_working_day(today)
        entry = state.get(nearest.isoformat())
        if entry and not entry.get("approved"):
            questions.append(f"{planner.label(nearest)}: EV requested but not approved yet.")
    if not dry_run:
        state_mod.save(config.state_file, state)
    return questions


def plan(config: Config, outlook, today: dt.date) -> list:
    start, end = planner.window(config, today)
    try:
        work_plan = planner.work_plan_days(outlook.work_plan(start, end)) if config.work_plan else {}
        entries = planner.entry_days(config, outlook.events(start, end))
    except Exception as error:
        raise CalendarError(f"Couldn't read the work plan or calendar: {error}") from error
    return planner.decide(config, today, work_plan, entries, state_mod.load(config.state_file))


def run(config: Config, outlook=None, today: dt.date | None = None, dry_run: bool = False, headless: bool = True,
        approvals_only: bool = False, log=print, booker=None) -> dict:
    """booker(config, day) -> outcome text; the form by default. Tests pass a fake."""
    today = today or dt.date.today()
    report = {"booked": [], "failed": [], "questions": [], "decisions": []}
    if not approvals_only:
        for decision in plan(config, outlook, today):
            report["decisions"].append({"date": decision.day.isoformat(), "action": decision.action})
            if decision.action == "ask":
                report["questions"].append(decision.text)
            elif decision.action == "log":
                log(decision.text)
            elif dry_run:
                log(decision.text)
            else:
                _book(config, decision.day, report, headless, log, booker)
    report["questions"] += apply_approvals(config, outlook, today, dry_run, log)
    for question in report["questions"]:
        log(f"ask: {question}")
    return report


def _book(config: Config, day: dt.date, report: dict, headless: bool, log, booker) -> None:
    start = form.start_time(config, _car(config))
    try:
        outcome = (booker or (lambda c, d: form.book(c, d, submit=True, headless=headless, start=start, log=log)))(
            config, day)
    except Exception as error:
        outcome = f"{type(error).__name__}: {error}"
    log(f"book {planner.label(day)}: {outcome}")
    if outcome.startswith("Submitted"):
        state_mod.record_submission(config.state_file, day, start)
        report["booked"].append(day.isoformat())
    else:
        report["failed"].append(day.isoformat())
        report["questions"].append(f"{planner.label(day)}: booking FAILED. {outcome[:120]}")


def _car(config: Config) -> dict:
    try:
        return form.car(config)
    except Exception:
        return {}


def after_run(config: Config, report: dict, log=print) -> None:
    payload = json.dumps({"config": str(config.path), "questions": report["questions"], "booked": report["booked"],
                          "failed": report["failed"], "summary": summary(report)})
    log(f"after-run hook: {' '.join(config.after_run)}")
    try:
        done = subprocess.run(config.after_run, input=payload, cwd=config.path.parent, capture_output=True,
                              text=True, encoding="utf-8", errors="replace", timeout=300, creationflags=NO_WINDOW)
        for line in (done.stdout + done.stderr).splitlines():
            if line.strip():
                log(f"  hook: {line.strip()}")
        if done.returncode:
            log(f"after-run hook exited {done.returncode}")
    except Exception as error:
        log(f"after-run hook failed: {error}")


def locked_run(config: Config, dry_run: bool = False, hook: bool = False, interactive: bool | None = None,
               headless: bool = True, approvals_only: bool = False, log=print, outlook=None, booker=None,
               today: dt.date | None = None) -> dict:
    """One run at a time per settings file: two at once could request the same day twice."""
    with exclusive(f"ev-charging-run-{key(config.path)}", 900, LOCAL, what=f"run for {config.path}"):
        report = run(config, outlook or Outlook(config, client(config, interactive)), today=today, dry_run=dry_run,
                     headless=headless, approvals_only=approvals_only, log=log, booker=booker)
    if hook and not dry_run and not approvals_only and config.after_run:
        after_run(config, report, log)
    return report
