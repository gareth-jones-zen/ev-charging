"""What's been requested and approved, so no day is ever requested twice.

    {"2026-11-24": {"start": "08:00", "submitted": "2026-11-10T08:41:08", "approved": "Charger 1"}}

Keep the same file when moving from an earlier setup, so it carries on where it left off. It's written
to a temporary file and swapped in, so a crash mid-write can't leave it half written. The swap is
retried for a moment if it's refused (a scanner or OneDrive can hold a new file briefly).

**A day is recorded before its request is submitted, not after** (record_submission, then the form,
then undo_submission if it wasn't submitted). So nothing that goes wrong after a submission (the file
locked, the process killed) can leave a request unrecorded for the next run to send again. If the
record can't be written, nothing is submitted.
"""

from __future__ import annotations

import datetime as dt
import json
import os
import pathlib
import time

REPLACE_TRIES = 5   # the swap, if it's refused: 5 tries over about 2 seconds


def load(path: pathlib.Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {}


def save(path: pathlib.Path, state: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    temp.write_text(json.dumps(state, indent=2, sort_keys=True), encoding="utf-8")
    for attempt in range(1, REPLACE_TRIES + 1):
        try:
            os.replace(temp, path)
            return
        except PermissionError:   # access denied: usually a scanner holding the new file for a moment
            if attempt == REPLACE_TRIES:
                raise
            time.sleep(0.2 * attempt)


def record_submission(path: pathlib.Path, day: dt.date, start: str) -> dict | None:
    """Mark a day as requested, before its request is submitted. Returns the day's entry as it was
    (None if it had none), for undo_submission. If this raises, don't submit."""
    state = load(path)
    before = state.get(day.isoformat())
    before = dict(before) if isinstance(before, dict) else before
    state.setdefault(day.isoformat(), {}).update(
        {"start": start, "submitted": dt.datetime.now().isoformat(timespec="seconds")})
    save(path, state)
    return before


def undo_submission(path: pathlib.Path, day: dt.date, before: dict | None) -> None:
    """The request wasn't submitted after all: put the day back as it was, so a later run can try again."""
    state = load(path)
    if before is None:
        state.pop(day.isoformat(), None)
    else:
        state[day.isoformat()] = before
    save(path, state)
