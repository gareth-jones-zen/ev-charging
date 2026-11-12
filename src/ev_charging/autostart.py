"""A daily run through Windows Task Scheduler (win-background).

Bookings need a day's notice, not minutes, so there's no watcher: a task runs `run --hook --scheduled`
daily at daily_at and at logon, and catches up a run missed while the laptop was off or asleep. A run
with nothing to do costs a few Graph calls. It runs as you, only while you're logged on, needs no admin
rights, and runs pythonw directly, so no window appears (the browser runs headless).
"""

from __future__ import annotations

import pathlib

from win_background import Task, python_command

TASK = "ev-charging run"
_task = Task(TASK, "Books EV charging when the calendar asks for it, and applies approvals (ev-charging run).")


def command(config_path: pathlib.Path) -> tuple[str, list]:
    return python_command("ev_charging", "--config", str(config_path.resolve()), "run", "--hook", "--scheduled")


def enable(config_path: pathlib.Path, daily_at: str) -> str:
    return _task.enable(*command(config_path), working_dir=config_path.resolve().parent,
                        daily_at=daily_at, keep_running=False)


def disable() -> bool:
    return _task.disable()


def status() -> dict | None:
    return _task.status()
