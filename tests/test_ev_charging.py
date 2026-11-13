"""Run with: python -m unittest discover -s tests

No network and no browser: Outlook is a fake with the same methods, and the form is a fake booker."""

import argparse
import contextlib
import datetime as dt
import io
import json
import pathlib
import sys
import tempfile
import threading
import tomllib
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "src"))

from ev_charging import cli, config as cfg, form, planner, run, state  # noqa: E402
from m365_graph import SignInNeeded  # noqa: E402
from win_background import Lock, key  # noqa: E402

TODAY = dt.date(2026, 11, 12)   # a Thursday


def settings(folder: pathlib.Path, extra: str = "") -> cfg.Config:
    text = cfg.TEMPLATE + extra
    path = folder / "config.toml"
    path.write_text(text, encoding="utf-8")
    config = cfg.parse(tomllib.loads(text), path)
    config.state_file = folder / "state.json"
    config.log_file = folder / "log.txt"
    config.car_file = folder / "car.json"
    config.car_file.write_text(json.dumps({"car_make": "Make", "car_model": "Model", "car_registration": "YOUR REG"}),
                               encoding="utf-8")
    return config


def event(day: str, subject: str, hour: int = 7, eid: str | None = None) -> dict:
    return {"id": eid or f"{day}-{hour}", "subject": subject, "start": {"dateTime": f"{day}T{hour:02d}:30:00.0000000"}}


def segment(day: str, kind: str) -> dict:
    return {"start": {"dateTime": f"{day}T09:00:00.0000000"}, "workLocationType": kind}


def reply(day: str, subject: str = "Your Electric Car Charging Request - APPROVED", received: str = "2026-11-10T10:00:00Z",
          point: str = "Charger 1 (Rear)", start: str = "08:00") -> dict:
    body = (f"<p>Date - {day}</p><p>Start Time - {start}</p><p>End Time - 11/10/2026 10:00</p>"
            f"<p>You have been assigned; {point}</p>")
    return {"subject": subject, "receivedDateTime": received, "body": {"content": body}}


class FakeOutlook:
    def __init__(self, plan=(), events=(), messages=()):
        self.plan, self.calendar, self.messages = list(plan), list(events), list(messages)
        self.renamed = []

    def work_plan(self, start, end):
        return [s for s in self.plan if start.isoformat() <= s["start"]["dateTime"][:10] < end.isoformat()]

    def events(self, start, end):
        return [e for e in self.calendar if start.isoformat() <= e["start"]["dateTime"][:10] < end.isoformat()]

    def messages_from(self, sender, since):
        return self.messages

    def rename(self, event_id, subject, location):
        self.renamed.append((event_id, subject, location))


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.folder = pathlib.Path(self.tmp.name)
        self.config = settings(self.folder)
        self.lines = []

    def tearDown(self):
        self.tmp.cleanup()

    def log(self, text):
        self.lines.append(text)


class TriggerTests(Base):
    def test_entry_and_ev_word(self):
        c = self.config
        self.assertTrue(planner.is_entry(c, "Travel"))
        self.assertTrue(planner.is_entry(c, "  travel; EV"))
        self.assertTrue(planner.is_entry(c, "Travel; EV – Charger 1 (Rear), 08:00–12:00"))
        self.assertFalse(planner.is_entry(c, "Time to travel"))
        for subject in ("Travel; EV", "Travel; Book EV charger", "Travel ev", "Travel; EV – Charger 1 (Rear)"):
            self.assertTrue(planner.asks(c, subject), subject)
        for subject in ("Travel", "Travel; Bevan's leaving do", "Travel; EVENT", "Travel; Leeds"):
            self.assertFalse(planner.asks(c, subject), subject)

    def test_entry_days_any_entry_asks(self):
        days = planner.entry_days(self.config, [event("2026-11-16", "Travel"), event("2026-11-16", "Travel; EV", 17),
                                                event("2026-11-17", "Travel"), event("2026-11-18", "Standup")])
        self.assertEqual(days, {dt.date(2026, 11, 16): True, dt.date(2026, 11, 17): False})

    def test_custom_trigger(self):
        text = cfg.TEMPLATE.replace("entry = '^\\s*travel'", "entry = 'office'").replace(
            "book = '\\bev\\b'", "book = 'charge'")
        self.assertNotEqual(text.count("'office'"), 0)
        self.assertIn("book = 'charge'", text)
        custom = cfg.parse(tomllib.loads(text), self.folder / "config.toml")
        self.assertTrue(planner.is_entry(custom, "In the office"))
        self.assertTrue(planner.asks(custom, "Office, charge car"))
        self.assertFalse(planner.asks(custom, "Office; EV"))


class DecisionTests(Base):
    def decide(self, plan, entries, booked=()):
        work_plan = {dt.date.fromisoformat(d): set(k) for d, k in plan.items()}
        entry_days = {dt.date.fromisoformat(d): v for d, v in entries.items()}
        return {d.day.isoformat(): d for d in planner.decide(self.config, TODAY, work_plan, entry_days,
                                                             {b: {"start": "08:00"} for b in booked})}

    def test_table(self):
        found = self.decide(
            plan={"2026-11-16": {"office"}, "2026-11-17": {"remote"}, "2026-11-18": {"office"},
                  "2026-11-19": {"office", "timeOff"}, "2026-11-20": {"remote"}, "2026-11-23": {"office"},
                  "2026-11-24": {"office"}},
            entries={"2026-11-16": True, "2026-11-17": True, "2026-11-18": False, "2026-11-19": True,
                     "2026-11-24": True},
            booked=["2026-11-19", "2026-11-20"])
        self.assertEqual(found["2026-11-16"].action, "book")              # office + EV
        self.assertEqual(found["2026-11-17"].action, "ask")               # EV but remote
        self.assertIn("work plan says remote", found["2026-11-17"].text)
        self.assertEqual(found["2026-11-18"].action, "log")               # office, plain Travel
        self.assertNotIn("2026-11-19", found)                             # requested already: nothing
        self.assertEqual(found["2026-11-20"].action, "log")               # requested, no longer office: never raised
        self.assertNotIn("2026-11-23", found)                             # office, no Travel block: nothing
        self.assertEqual(found["2026-11-24"].action, "book")

    def test_no_work_plan_set_says_nothing_set(self):
        found = self.decide(plan={}, entries={"2026-11-16": True})
        self.assertIn("work plan says nothing set", found["2026-11-16"].text)

    def test_window_weekdays_and_notice(self):
        entries = {(TODAY + dt.timedelta(days=n)).isoformat(): True for n in range(0, 20)}
        plan = {d: {"office"} for d in entries}
        found = self.decide(plan, entries)
        days = sorted(found)
        self.assertEqual(days[0], "2026-11-13")                           # tomorrow: notice_days = 1
        self.assertEqual(days[-1], "2026-11-26")                          # 14 days from tomorrow, exclusive
        self.assertFalse(any(dt.date.fromisoformat(d).weekday() >= 5 for d in days))

    def test_work_plan_ignored(self):
        self.config.work_plan = ""
        found = self.decide(plan={}, entries={"2026-11-16": True, "2026-11-17": False})
        self.assertEqual(found["2026-11-16"].action, "book")
        self.assertEqual(found["2026-11-17"].action, "log")

    def test_next_working_day(self):
        self.assertEqual(planner.next_working_day(dt.date(2026, 11, 13)), dt.date(2026, 11, 16))   # Fri -> Mon
        self.assertEqual(planner.next_working_day(TODAY), dt.date(2026, 11, 13))


class ApprovalTests(Base):
    def test_parse_ignores_month_first_end_time(self):
        parsed = planner.parse_reply(self.config, reply("2026-11-24"))
        self.assertEqual(parsed["date"], dt.date(2026, 11, 24))
        self.assertEqual(parsed["start"], "08:00")
        self.assertEqual(parsed["point"], "Charger 1 (Rear)")
        self.assertEqual(parsed["outcome"], "approved")
        # End Time said 11/10/2026 10:00 (the approval date, month first): the slot is still 4 hours from 08:00.
        self.assertEqual(planner.renamed(self.config, parsed), "Travel; EV – Charger 1 (Rear), 08:00–12:00")

    def test_parse_declined_and_unparseable(self):
        self.assertEqual(planner.parse_reply(self.config, reply("2026-11-24", subject="Your Electric Car Charging Request - DECLINED"))["outcome"],
                         "declined")
        self.assertIsNone(planner.parse_reply(self.config, {"subject": "x", "body": {"content": "nothing"}}))

    def test_charge_point_fallback(self):
        self.config.approvals.point = r"nomatch(x)"
        self.config.approvals.charge_points = ["Charger 1 (Rear)"]
        self.assertEqual(planner.parse_reply(self.config, reply("2026-11-24"))["point"], "Charger 1 (Rear)")

    def test_latest_reply_wins_and_past_ignored(self):
        messages = [reply("2026-11-24", point="Charger 1 (Rear)", received="2026-11-10T10:00:00Z"),
                    reply("2026-11-24", point="Charger 2 (Front)", received="2026-11-11T10:00:00Z"),
                    reply("2026-11-05"),
                    {"subject": "Something else", "receivedDateTime": "2026-11-11T00:00:00Z",
                     "body": {"content": "Date - 2026-11-30 Start Time - 08:00"}}]
        latest = planner.latest_replies(self.config, messages, TODAY)
        self.assertEqual(list(latest), [dt.date(2026, 11, 24)])
        self.assertEqual(latest[dt.date(2026, 11, 24)]["point"], "Charger 2 (Front)")

    def test_morning_entry_only(self):
        events = [event("2026-11-24", "Travel", 17, "evening"), event("2026-11-24", "Travel; EV", 7, "morning"),
                  event("2026-11-24", "Standup", 9, "standup")]
        self.assertEqual(planner.morning_entry(self.config, events, dt.date(2026, 11, 24))["id"], "morning")
        self.assertIsNone(planner.morning_entry(self.config, events[:1], dt.date(2026, 11, 24)))

    def test_rename_template_and_slot_length(self):
        self.config.approvals.rename = "Travel ({point}) {date:%d/%m} {start}-{end}"
        self.config.slot_hours = 3.5
        parsed = planner.parse_reply(self.config, reply("2026-11-24", start="09:15"))
        self.assertEqual(planner.renamed(self.config, parsed), "Travel (Charger 1 (Rear)) 24/11 09:15-12:45")


class RunTests(Base):
    def outlook(self, **extra):
        defaults = dict(
            plan=[segment("2026-11-16", "office"), segment("2026-11-17", "remote"), segment("2026-11-24", "office")],
            events=[event("2026-11-16", "Travel; EV"), event("2026-11-17", "Travel; EV"),
                    event("2026-11-24", "Travel; EV", 7, "tue-morning"), event("2026-11-24", "Travel", 17, "tue-evening")],
            messages=[reply("2026-11-24")])
        defaults.update(extra)
        return FakeOutlook(**defaults)

    def test_dry_run_changes_nothing(self):
        fake, booked = self.outlook(), []
        report = run.run(self.config, fake, today=TODAY, dry_run=True, log=self.log,
                         booker=lambda c, d: booked.append(d) or "Submitted")
        self.assertEqual(booked, [])
        self.assertEqual(fake.renamed, [])
        self.assertFalse(self.config.state_file.exists())
        self.assertIn("would book Mon 16 Nov", self.lines)
        self.assertIn("would update Tue 24 Nov Travel block -> Travel; EV – Charger 1 (Rear), 08:00–12:00", self.lines)
        self.assertEqual(report["questions"], ["Tue 17 Nov: Travel block says EV but work plan says remote. Book EV?"])

    def test_run_books_records_and_renames(self):
        fake = self.outlook()
        report = run.run(self.config, fake, today=TODAY, log=self.log, booker=lambda c, d: f"Submitted: {d}")
        self.assertEqual(report["booked"], ["2026-11-16", "2026-11-24"])
        saved = state.load(self.config.state_file)
        self.assertEqual(saved["2026-11-16"]["start"], "08:00")
        self.assertEqual(saved["2026-11-24"]["approved"], "Charger 1 (Rear)")
        self.assertEqual(fake.renamed, [("tue-morning", "Travel; EV – Charger 1 (Rear), 08:00–12:00", "Charger 1 (Rear)")])
        again = run.run(self.config, fake, today=TODAY, log=self.log, booker=lambda c, d: self.fail("booked twice"))
        self.assertEqual(again["booked"], [])

    def test_failed_booking_is_a_question(self):
        report = run.run(self.config, self.outlook(messages=[]), today=TODAY, log=self.log,
                         booker=lambda c, d: "Clicked Submit but didn't see a confirmation.")
        self.assertEqual(report["failed"], ["2026-11-16", "2026-11-24"])
        self.assertTrue(any("booking FAILED" in q for q in report["questions"]))
        self.assertFalse(self.config.state_file.exists() and state.load(self.config.state_file))

    def test_declined_and_unapproved_tomorrow_are_questions(self):
        state.save(self.config.state_file, {"2026-11-13": {"start": "08:00"}, "2026-11-24": {"start": "08:00"}})
        fake = self.outlook(plan=[], events=[], messages=[reply("2026-11-24", subject="Your Electric Car Charging Request - DECLINED")])
        questions = run.apply_approvals(self.config, fake, TODAY, dry_run=False, log=self.log)
        self.assertEqual(questions, ["Tue 24 Nov: EV request DECLINED.", "Fri 13 Nov: EV requested but not approved yet."])

    def test_approved_with_no_morning_entry_is_logged_not_raised(self):
        fake = self.outlook(events=[event("2026-11-24", "Travel", 17)])
        questions = run.apply_approvals(self.config, fake, TODAY, dry_run=False, log=self.log)
        self.assertEqual(questions, [])
        self.assertTrue(any("no morning Travel block to update (not raised)" in line for line in self.lines))

    def test_never_cancels(self):
        # A requested day that's no longer an office day is logged, and nothing else happens.
        state.save(self.config.state_file, {"2026-11-17": {"start": "08:00", "approved": "Charger 1 (Rear)"}})
        report = run.run(self.config, self.outlook(messages=[]), today=TODAY, log=self.log,
                         booker=lambda c, d: "Submitted")
        self.assertTrue(any("no longer says office (not raised)" in line for line in self.lines))
        self.assertNotIn("Tue 17 Nov", " ".join(report["questions"]))

    def test_calendar_failure(self):
        class Broken(FakeOutlook):
            def events(self, start, end):
                raise OSError("network down")
        with self.assertRaises(run.CalendarError):
            run.run(self.config, Broken(), today=TODAY, log=self.log)

    def test_hook_payload(self):
        captured = {}
        self.config.after_run = [sys.executable, "-c",
                                 "import sys, json, pathlib; pathlib.Path('hook.json').write_text(sys.stdin.read())"]
        run.after_run(self.config, {"questions": ["q"], "booked": ["2026-11-16"], "failed": []}, self.log)
        captured = json.loads((self.folder / "hook.json").read_text())
        self.assertEqual(captured["questions"], ["q"])
        self.assertEqual(captured["booked"], ["2026-11-16"])
        self.assertIn("summary", captured)


class FormTests(Base):
    def test_values_from_template_and_car(self):
        self.config.form.fields.append(cfg.Field(question=6, type="text", value="{additional_information}"))
        details = {"car_make": "Make", "additional_information": ""}
        filled = form.values(self.config, dt.date(2026, 11, 24), "08:00", details)
        self.assertEqual(filled, [(1, "date", "24/11/2026"), (2, "choice", "08:00"), (3, "text", "Make")])

    def test_missing_car_key(self):
        with self.assertRaises(form.FormError):
            form.values(self.config, TODAY, "08:00", {})

    def test_start_time_precedence(self):
        self.assertEqual(form.start_time(self.config, {"start_time": "09:00"}), "08:00")   # settings win
        self.config.start_time = ""
        self.assertEqual(form.start_time(self.config, {"start_time": "09:00"}), "09:00")
        self.assertEqual(form.start_time(self.config, {}), "08:00")


class ConfigTests(Base):
    def test_template_loads(self):
        self.assertEqual(self.config.days_ahead, 14)
        self.assertTrue(self.config.approvals.enabled)
        self.assertEqual(len(self.config.form.fields), 3)

    def test_bad_settings(self):
        for bad in ("[booking]\nstart_time = '8am'\n", "[trigger]\nbook = '('\n"):
            with self.assertRaises(ValueError):
                cfg.parse(tomllib.loads(bad), self.folder / "x.toml")
        with self.assertRaises(ValueError):
            cfg.parse({"approvals": {"sender": "x@example.com"}}, self.folder / "x.toml")

    def test_no_approvals_section(self):
        config = cfg.parse({}, self.folder / "x.toml")
        self.assertFalse(config.approvals.enabled)
        self.assertEqual(run.apply_approvals(config, FakeOutlook(), TODAY, False, self.log), [])


class FixedBugTests(Base):
    """Regression tests for the five bugs fixed in 0.1.1."""

    outlook = RunTests.outlook

    # 1. A sign-in failure while planning surfaced as "couldn't read your calendar", never "needs a sign-in".
    def test_sign_in_needed_while_planning_is_not_a_calendar_error(self):
        class SignedOut(FakeOutlook):
            def work_plan(self, start, end):
                raise SignInNeeded("No usable sign-in. At a console, run: ev-charging sign-in")
        with self.assertRaises(SignInNeeded) as caught:
            run.run(self.config, SignedOut(), today=TODAY, log=self.log)
        self.assertNotIsInstance(caught.exception, run.CalendarError)

    def test_scheduled_run_notifies_sign_in_needed(self):
        class SignedOut(FakeOutlook):
            def events(self, start, end):
                raise SignInNeeded("No usable sign-in.")
        notes = []
        args = argparse.Namespace(scheduled=True, json=False, dry_run=False, hook=True, command="run")
        with mock.patch.object(cli, "locked_run", lambda config, **k: run.run(config, SignedOut(), today=TODAY)), \
                mock.patch.object(cli, "notify", lambda title, lines: notes.append(title)), \
                contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(cli._run(self.config, args), 1)
        self.assertEqual(notes, ["ev-charging needs a sign-in"])

    # 2. A state write failing after a real submit left the day unrecorded, so the next run requested it again.
    def test_state_swap_is_retried(self):
        real, calls = state.os.replace, []

        def flaky(source, target):
            calls.append(target)
            if len(calls) < 3:
                raise PermissionError(5, "Access is denied")
            real(source, target)
        with mock.patch.object(state.os, "replace", flaky), mock.patch.object(state.time, "sleep"):
            state.save(self.config.state_file, {"2026-11-16": {"start": "08:00"}})
        self.assertEqual(len(calls), 3)
        self.assertEqual(state.load(self.config.state_file), {"2026-11-16": {"start": "08:00"}})

    def test_state_swap_gives_up_after_five(self):
        with mock.patch.object(state.os, "replace", side_effect=PermissionError(5, "Access is denied")) as replace, \
                mock.patch.object(state.time, "sleep"):
            with self.assertRaises(PermissionError):
                state.save(self.config.state_file, {})
        self.assertEqual(replace.call_count, 5)

    def test_submitted_day_survives_a_state_file_that_then_fails(self):
        self.config.approvals.enabled = False
        broken = mock.patch.object(state.os, "replace", side_effect=PermissionError(5, "Access is denied"))

        def booker(config, day):
            # The day is already recorded before the form is submitted...
            self.assertIn(day.isoformat(), state.load(config.state_file))
            broken.start()   # ...so the state file failing from now on can't lose it
            return f"Submitted: {day}"
        with mock.patch.object(state.time, "sleep"):
            try:
                report = run.run(self.config, self.outlook(), today=TODAY, log=self.log, booker=booker)
            finally:
                broken.stop()
        self.assertEqual(report["booked"], ["2026-11-16"])
        self.assertIn("Tue 24 Nov: booking FAILED. Not submitted: couldn't record it in the state file first. "
                      "PermissionError: [Errno 5] Access is denied", report["questions"])
        self.assertEqual(list(state.load(self.config.state_file)), ["2026-11-16"])
        again = run.run(self.config, self.outlook(), today=TODAY, log=self.log,
                        booker=lambda c, d: "Submitted" if d.isoformat() == "2026-11-24" else self.fail("asked twice"))
        self.assertEqual(again["booked"], ["2026-11-24"])

    def test_nothing_submitted_if_the_day_cant_be_recorded_first(self):
        self.config.approvals.enabled = False
        with mock.patch.object(state.os, "replace", side_effect=PermissionError(5, "Access is denied")), \
                mock.patch.object(state.time, "sleep"):
            report = run.run(self.config, self.outlook(), today=TODAY, log=self.log,
                             booker=lambda c, d: self.fail("submitted without a record"))
        self.assertEqual(report["booked"], [])
        self.assertEqual(report["failed"], ["2026-11-16", "2026-11-24"])
        failed = [q for q in report["questions"] if "FAILED" in q]
        self.assertEqual(len(failed), 2)
        self.assertTrue(all("Not submitted" in q for q in failed))

    def test_failed_submit_is_taken_off_so_the_next_run_retries(self):
        def raising(config, day):
            raise form.FormError("The form didn't load.")
        report = run.run(self.config, self.outlook(messages=[]), today=TODAY, log=self.log, booker=raising)
        self.assertEqual(report["failed"], ["2026-11-16", "2026-11-24"])
        self.assertEqual(state.load(self.config.state_file), {})
        again = run.run(self.config, self.outlook(messages=[]), today=TODAY, log=self.log,
                        booker=lambda c, d: f"Submitted: {d}")
        self.assertEqual(again["booked"], ["2026-11-16", "2026-11-24"])

    def test_failed_submit_that_cant_be_taken_off_says_so(self):
        self.config.approvals.enabled = False
        broken = mock.patch.object(state.os, "replace", side_effect=PermissionError(5, "Access is denied"))

        def booker(config, day):
            broken.start()
            return "Clicked Submit but didn't see a confirmation."
        with mock.patch.object(state.time, "sleep"):
            try:
                report = run.run(self.config, self.outlook(), today=TODAY, log=self.log, booker=booker)
            finally:
                broken.stop()
        first = next(q for q in report["questions"] if q.startswith("Mon 16 Nov"))
        self.assertTrue(first.endswith("It's still marked as requested, so it won't be retried: book it by hand (--force)."))
        self.assertIn("2026-11-16", state.load(self.config.state_file))

    def test_book_submit_records_first_and_undoes_a_failure(self):
        args = argparse.Namespace(date="2026-11-26", submit=True, start=None, headless=True, force=False)
        seen = []

        def fake(config, day, submit, headless, start):
            seen.append(day.isoformat() in state.load(config.state_file))
            return "Clicked Submit but didn't see a confirmation."
        with mock.patch.object(form, "book", fake), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(cli._book(self.config, args), 1)
        self.assertEqual(seen, [True])
        self.assertNotIn("2026-11-26", state.load(self.config.state_file))
        with mock.patch.object(form, "book", lambda *a, **k: "Submitted: Thu 26 Nov 2026, 08:00."), \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(cli._book(self.config, args), 0)
        self.assertEqual(state.load(self.config.state_file)["2026-11-26"]["start"], "08:00")

    def test_book_submit_undoes_when_the_form_raises(self):
        args = argparse.Namespace(date="2026-11-26", submit=True, start=None, headless=True, force=False)

        def fake(config, day, submit, headless, start):
            raise form.FormError("The form didn't load.")
        with mock.patch.object(form, "book", fake):
            with self.assertRaises(form.FormError):
                cli._book(self.config, args)
        self.assertNotIn("2026-11-26", state.load(self.config.state_file))

    def test_book_submit_when_the_day_cant_be_recorded(self):
        args = argparse.Namespace(date="2026-11-26", submit=True, start=None, headless=True, force=False)
        with mock.patch.object(form, "book", lambda *a, **k: self.fail("submitted without a record")), \
                mock.patch.object(state.os, "replace", side_effect=PermissionError(5, "Access is denied")), \
                mock.patch.object(state.time, "sleep"), contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertEqual(cli._book(self.config, args), 1)
        self.assertIn("Not submitted", err.getvalue())

    # 3. An error part-way through the approvals dropped the approvals already read.
    def test_approvals_read_before_an_error_are_kept(self):
        class RenameRefused(FakeOutlook):
            def rename(self, event_id, subject, location):
                raise OSError("rename refused")
        fake = RenameRefused(events=[event("2026-11-23", "Travel; EV", 7, "mon"), event("2026-11-24", "Travel; EV", 7, "tue")],
                             messages=[reply("2026-11-20", subject="Your Electric Car Charging Request - DECLINED"),
                                       reply("2026-11-23", point="Charger 2 (Front)"), reply("2026-11-24")])
        questions = run.apply_approvals(self.config, fake, TODAY, dry_run=False, log=self.log)
        self.assertEqual(questions, ["Fri 20 Nov: EV request DECLINED.",
                                     "Couldn't read the approval emails: rename refused"])
        saved = state.load(self.config.state_file)
        self.assertEqual(saved["2026-11-23"]["approved"], "Charger 2 (Front)")   # read; its rename failed
        self.assertNotIn("2026-11-24", saved)                                    # never reached
        self.assertIn("FAILED applying approvals: rename refused", self.lines)

    def test_approvals_error_in_a_dry_run_saves_nothing(self):
        class EventsDown(FakeOutlook):
            def events(self, start, end):
                raise OSError("events down")
        questions = run.apply_approvals(self.config, EventsDown(messages=[reply("2026-11-24")]), TODAY, dry_run=True,
                                        log=self.log)
        self.assertEqual(questions, ["Couldn't read the approval emails: events down"])
        self.assertFalse(self.config.state_file.exists())

    # 4. A car file that's a JSON list (or any non-object) crashed the whole run.
    def test_car_file_not_an_object(self):
        for text in ("[1, 2]", '"x"', "3", "null"):
            self.config.car_file.write_text(text, encoding="utf-8")
            with self.assertRaises(form.FormError) as caught:
                form.car(self.config)
            self.assertIn(str(self.config.car_file), str(caught.exception))
        self.config.car_file.write_text("[1, 2]", encoding="utf-8")
        self.config.start_time = ""   # so the car file's start_time is looked for, which is where it crashed
        self.config.approvals.enabled = False

        def booker(config, day):
            form.car(config)   # what the real form does first
            return "Submitted"
        report = run.run(self.config, self.outlook(), today=TODAY, log=self.log, booker=booker)
        self.assertEqual(report["failed"], ["2026-11-16", "2026-11-24"])
        self.assertTrue(report["questions"][0].startswith(
            f"Mon 16 Nov: booking FAILED. FormError: The car details at {self.config.car_file} aren't"))
        self.assertEqual(state.load(self.config.state_file), {})

    # 5. book --submit didn't take the run lock, so it could overlap the scheduled run.
    def test_book_takes_the_run_lock(self):
        held = []

        def fake(config, day, submit, headless, start):
            def probe():
                lock = Lock(f"ev-charging-run-{key(config.path)}")
                got = lock.acquire(0)
                lock.release()
                held.append(not got)
            thread = threading.Thread(target=probe)
            thread.start()
            thread.join()
            return "Submitted: Thu 26 Nov 2026, 08:00."
        for submit in (True, False):
            args = argparse.Namespace(date="2026-11-26", submit=submit, start=None, headless=True, force=True)
            with mock.patch.object(form, "book", fake), contextlib.redirect_stdout(io.StringIO()):
                cli._book(self.config, args)
        self.assertEqual(held, [True, True])

    def test_book_waits_for_a_run(self):
        args = argparse.Namespace(date="2026-11-26", submit=True, start=None, headless=True, force=False)
        with mock.patch.object(run, "exclusive", side_effect=TimeoutError("Another run didn't finish within 15 minutes.")), \
                mock.patch.object(form, "book", lambda *a, **k: self.fail("booked while a run held the lock")):
            with self.assertRaises(TimeoutError):
                cli._book(self.config, args)


if __name__ == "__main__":
    unittest.main()
