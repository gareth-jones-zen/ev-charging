"""Filling in and submitting the booking form (a Microsoft Form) in Edge.

Nothing else can submit a Forms response: not Graph, not Power Automate, not the M365 connector. So this
drives Edge with Playwright, in its own profile (edge_profile), never your everyday browser. You sign in
to Microsoft in that profile once (`ev-charging sign-in`); the session persists, so later runs need
nothing from you. It never handles a password.

**A submission is a request, not a booking**: someone approves it, and says so by email.

Two quirks of Microsoft Forms, both learned the hard way:
- **Forms keeps an unsubmitted draft for each user**, so after a preview the fields come back filled in,
  and a dropdown shows its last answer rather than "Select your answer". So the dropdown is found by its
  role, never by its placeholder text.
- **The date picker swallows the next click** unless it's closed (Escape) first.
"""

from __future__ import annotations

import datetime as dt
import json
import tomllib

from .config import Config

QUESTION = "[data-automation-id='questionItem']"


class FormError(RuntimeError):
    pass


# Submit was clicked but no confirmation appeared, so the request may have gone. The day stays recorded
# as requested, so no run sends it a second time; GJ checks (13 Nov 2026: keep the record).
UNCONFIRMED = "Clicked Submit but didn't see a confirmation"

def car(config: Config) -> dict:
    """The car's details (make, model, registration, anything the form asks), from a file outside any repo."""
    path = config.car_file
    if not path.exists():
        raise FormError(f"No car details at {path}. See the README: they live in a file of their own.")
    text = path.read_text(encoding="utf-8")
    details = tomllib.loads(text) if path.suffix.lower() == ".toml" else json.loads(text)
    if not isinstance(details, dict):   # a list, say: it has no names to fill the form from
        raise FormError(f"The car details at {path} aren't a JSON object of names and values.")
    return details


def start_time(config: Config, details: dict | None = None) -> str:
    return config.start_time or (details or {}).get("start_time") or "08:00"


def values(config: Config, day: dt.date, start: str, details: dict) -> list[tuple]:
    """(question, type, text) for each field to fill; a field whose text comes out empty is skipped."""
    filled = []
    for item in config.form.fields:
        try:
            text = item.value.format(date=day, start=start, **details)
        except KeyError as error:
            raise FormError(f"Form question {item.question} wants {error} from the car file, which hasn't got it.") from None
        if text.strip():
            filled.append((item.question, item.type, text))
    return filled


def book(config: Config, day: dt.date, submit: bool = False, headless: bool = False, start: str | None = None,
         log=print) -> str:
    """Fill the form for a day, screenshot it, and submit it if asked. Returns what happened, starting
    "Submitted" on success. Raises FormError if the form couldn't be filled."""
    from playwright.sync_api import sync_playwright   # only needed here

    if not config.form.url:
        raise FormError("No [form] url in the settings.")
    details = car(config)
    start = start or start_time(config, details)
    fields = values(config, day, start, details)
    config.screenshots.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as p:
        context = p.chromium.launch_persistent_context(str(config.edge_profile), channel="msedge", headless=headless)
        try:
            page = context.pages[0] if context.pages else context.new_page()
            page.goto(config.form.url, wait_until="domcontentloaded")
            try:
                page.wait_for_selector(QUESTION, timeout=300000)   # time to sign in if the session lapsed
            except Exception:
                raise FormError("The form didn't load. Is the Microsoft sign-in still valid? "
                                "Run `ev-charging sign-in` at a console.") from None
            questions = page.locator(QUESTION)
            if config.form.questions and questions.count() != config.form.questions:
                raise FormError(f"Expected {config.form.questions} questions, found {questions.count()}. "
                                "The form has changed; not filling it.")
            for number, kind, text in fields:
                question = questions.nth(number - 1)
                if kind == "date":
                    box = question.locator("input").first
                    box.click()
                    box.fill(text)
                    page.keyboard.press("Escape")   # close the date picker, or it swallows the next click
                    page.wait_for_timeout(500)
                elif kind == "choice":
                    # By role, not by "Select your answer": a saved draft shows its last answer instead.
                    dropdown = question.locator("[role='button'][aria-expanded]").first
                    for _ in range(3):
                        if dropdown.get_attribute("aria-expanded") == "true":
                            break
                        dropdown.click()
                        page.wait_for_timeout(800)
                    page.get_by_role("option", name=text, exact=True).click()
                else:
                    question.locator("input, textarea").first.fill(text)
            page.wait_for_timeout(1000)
            shot = config.screenshots / f"{day.isoformat()}-{'submitted' if submit else 'preview'}.png"
            page.screenshot(path=str(shot), full_page=True)
            if not submit:
                return f"Preview only, nothing submitted: {shot}"
            page.get_by_role("button", name="Submit").click()
            try:
                page.wait_for_selector(f"text=/{config.form.confirmation}/i", timeout=30000)
            except Exception:
                after = config.screenshots / f"{day.isoformat()}-after-submit.png"
                page.screenshot(path=str(after), full_page=True)
                return f"{UNCONFIRMED}. Check {after}"
            return f"Submitted: {day:%a %d %b %Y}, {start}."
        finally:
            context.close()


def sign_in_window(config: Config) -> None:
    """Open the form in the tool's Edge profile and wait (up to 5 minutes) for a sign-in to finish."""
    from playwright.sync_api import sync_playwright

    if not config.form.url:
        raise FormError("No [form] url in the settings.")
    with sync_playwright() as p:
        context = p.chromium.launch_persistent_context(str(config.edge_profile), channel="msedge", headless=False)
        try:
            page = context.pages[0] if context.pages else context.new_page()
            page.goto(config.form.url, wait_until="domcontentloaded")
            try:
                page.wait_for_selector(QUESTION, timeout=300000)
            except Exception:
                raise FormError("The form didn't load within 5 minutes.") from None
        finally:
            context.close()
