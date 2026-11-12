"""What's been requested and approved, so no day is ever requested twice.

    {"2026-11-24": {"start": "08:00", "submitted": "2026-11-10T08:41:08", "approved": "Charger 1"}}

Keep the same file when moving from an earlier setup, so it carries on where it left off. It's written
to a temporary file and swapped in, so a crash mid-write can't leave it half written.
"""

from __future__ import annotations

import datetime as dt
import json
import os
import pathlib


def load(path: pathlib.Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {}


def save(path: pathlib.Path, state: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    temp.write_text(json.dumps(state, indent=2, sort_keys=True), encoding="utf-8")
    os.replace(temp, path)


def record_submission(path: pathlib.Path, day: dt.date, start: str) -> None:
    state = load(path)
    state.setdefault(day.isoformat(), {}).update(
        {"start": start, "submitted": dt.datetime.now().isoformat(timespec="seconds")})
    save(path, state)
