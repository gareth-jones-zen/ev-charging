"""ev-charging: book an office EV charging slot when your calendar asks for one.

    ev-charging config                  create the settings file (if missing) and say where it is
    ev-charging sign-in                 sign in to Microsoft, then to the form in the tool's Edge window
    ev-charging book 2026-11-26         fill in the form and screenshot it; submits nothing
    ev-charging book 2026-11-26 --submit    and submit it (a request, approved by email)
    ev-charging run --dry-run           what the daily run would book, ask and rename; changes nothing
    ev-charging run                     book what the calendar asks for, apply approvals, list questions
    ev-charging run --hook              then pass the report to the settings' after_run command
    ev-charging approvals [--dry-run]   only apply the approval emails to the calendar
    ev-charging status                  requests from today on, and whether each is approved
    ev-charging autostart on            run daily at daily_at and at logon, and now (off, status)
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import sys

from m365_graph import SignInNeeded
from win_background import notify

from . import __version__, config as cfg, form, state as state_mod
from .outlook import client
from .run import CalendarError, locked_run, logger, summary


def main(argv: list | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):   # en dashes in subjects aren't in every console code page
        if stream and hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    parser = argparse.ArgumentParser(prog="ev-charging", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--config", help="settings file (default %%APPDATA%%\\ev-charging\\config.toml)")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("config", help="create the settings file if missing, and say where it is")
    p = sub.add_parser("sign-in", help="sign in to Microsoft, and to the form in the tool's Edge window")
    p.add_argument("--graph-only", action="store_true", help="skip the form's Edge window")
    p = sub.add_parser("book", help="fill in (and with --submit, submit) the form for one day")
    p.add_argument("date", help="yyyy-mm-dd")
    p.add_argument("--submit", action="store_true", help="submit it (default: fill in and screenshot only)")
    p.add_argument("--start", help="start time, such as 08:00 (default from the settings)")
    p.add_argument("--headless", action="store_true", help="no visible window")
    p.add_argument("--force", action="store_true", help="submit even if this day was already requested")
    for name, text in (("run", "the daily run: book, apply approvals, list questions"),
                       ("approvals", "only apply the approval emails to the calendar")):
        p = sub.add_parser(name, help=text)
        p.add_argument("--dry-run", action="store_true", help="say what it would do; change nothing")
        p.add_argument("--json", action="store_true", help="print the report as JSON")
        if name == "run":
            p.add_argument("--hook", action="store_true", help="then run the after_run command (not on a dry run)")
            p.add_argument("--scheduled", action="store_true",
                           help="as the daily task runs it: never open a browser to sign in, and notify")
    sub.add_parser("status", help="requests from today on, and whether each is approved")
    p = sub.add_parser("autostart", help="run daily and at logon (Task Scheduler)")
    p.add_argument("action", choices=["on", "off", "status"])
    args = parser.parse_args(argv)

    if args.command == "config":
        print(args.config or cfg.create_default())
        return 0
    if args.command == "autostart":
        return _autostart(args)
    try:
        config = cfg.load(args.config)
        if args.command == "sign-in":
            return _sign_in(config, args.graph_only)
        if args.command == "book":
            return _book(config, args)
        if args.command == "status":
            return _status(config)
        return _run(config, args)
    except (SignInNeeded, FileNotFoundError, ValueError, TimeoutError, form.FormError) as error:
        print(error, file=sys.stderr)
        return 2


def _run(config: cfg.Config, args) -> int:
    scheduled = getattr(args, "scheduled", False)
    log = logger(config, echo=not args.json and not scheduled)
    try:
        report = locked_run(config, dry_run=args.dry_run, hook=getattr(args, "hook", False),
                            interactive=False if scheduled else None, headless=True,
                            approvals_only=args.command == "approvals", log=log)
    except Exception as error:
        log(f"FAILED: {str(error)[:300]}")
        if scheduled:
            title = ("ev-charging needs a sign-in" if isinstance(error, SignInNeeded) else
                     "EV booking: couldn't read your calendar" if isinstance(error, CalendarError) else
                     "EV booking failed")
            notify(title, [str(error)[:200]])
        if isinstance(error, (SignInNeeded, CalendarError, TimeoutError)):
            print(error, file=sys.stderr)
            return 1
        raise
    log(f"done: {summary(report)}{' (dry run)' if args.dry_run else ''}")
    if scheduled and report["questions"] and not args.dry_run:
        notify(f"EV booking: {len(report['questions'])} to check", report["questions"])
    if args.json:
        print(json.dumps(report, indent=1))
    return 0


def _book(config: cfg.Config, args) -> int:
    day = dt.date.fromisoformat(args.date)
    if args.submit and day.isoformat() in state_mod.load(config.state_file) and not args.force:
        print(f"Already requested for {day:%a %d %b}; skipping (--force to request it again).")
        return 0
    start = args.start or form.start_time(config, form.car(config))
    outcome = form.book(config, day, submit=args.submit, headless=args.headless, start=start)
    if outcome.startswith("Submitted"):
        state_mod.record_submission(config.state_file, day, start)
        outcome += " Approval comes by email."
    print(outcome)
    return 0 if outcome.startswith(("Submitted", "Preview")) else 1


def _sign_in(config: cfg.Config, graph_only: bool) -> int:
    client(config, interactive=True).token()
    print("Signed in to Microsoft Graph.")
    if not graph_only:
        print("An Edge window opens on the form: sign in there if it asks. It closes once the form loads.")
        form.sign_in_window(config)
        print("The form loads, so bookings can be submitted.")
    return 0


def _status(config: cfg.Config) -> int:
    today = dt.date.today().isoformat()
    upcoming = {day: entry for day, entry in sorted(state_mod.load(config.state_file).items()) if day >= today}
    if not upcoming:
        print("No requests from today on.")
    for day, entry in upcoming.items():
        when = dt.date.fromisoformat(day)
        print(f"{when:%a %d %b}  {entry.get('start', '?'):5}  "
              + (f"approved: {entry['approved']}" if entry.get("approved") else "awaiting approval"))
    return 0


def _autostart(args) -> int:
    from . import autostart
    try:
        if args.action == "on":
            config = cfg.load(args.config)   # check the settings load before scheduling them
            command = autostart.enable(config.path, config.daily_at)
            print(f"Autostart is on: '{autostart.TASK}' runs daily at {config.daily_at} and at logon, and has "
                  f"started now.\n  {command}\n  log: {config.log_file}")
        elif args.action == "off":
            print("Autostart is off." if autostart.disable() else "Autostart was already off.")
        else:
            info = autostart.status()
            if info is None:
                print("Autostart is off.")
            else:
                print(f"Autostart is on ('{autostart.TASK}').")
                for item in ("state", "last_run", "last_result", "next_run", "command"):
                    print(f"  {item.replace('_', ' '):12} {info.get(item, '')}")
        return 0
    except (RuntimeError, FileNotFoundError, ValueError) as error:
        print(error, file=sys.stderr)
        return 2
