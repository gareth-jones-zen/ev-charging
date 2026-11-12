"""Microsoft Graph: the work plan, calendar entries, the replies to requests, and renaming an entry.

Through m365-graph, so it signs in once, never opens a browser when nobody's at the console, prefers
IPv4 and retries. Needs Calendars.ReadWrite (to rename the morning entry) and Mail.Read (the replies).
"""

from __future__ import annotations

import datetime as dt
import urllib.parse

from m365_graph import Client

from .config import Config

SCOPES = ["Calendars.ReadWrite", "Mail.Read"]
GRAPH = "https://graph.microsoft.com/v1.0"
VIEW = (GRAPH + "/me/settings/workHoursAndLocations/"
        "occurrencesView(startDateTime='{start}',endDateTime='{end}')")


def client(config: Config, interactive: bool | None = None) -> Client:
    kwargs = {"cache_path": config.msal_cache} if config.msal_cache else {}
    return Client(tenant=config.tenant, login_hint=config.login_hint, scopes=SCOPES, interactive=interactive,
                  sign_in_hint="At a console, run: ev-charging sign-in", **kwargs)


class Outlook:
    """The few Graph calls the tool makes. Tests replace it with a fake that has the same methods."""

    def __init__(self, config: Config, graph: Client):
        self.graph = graph
        self.headers = {"Prefer": f'outlook.timezone="{config.timezone}"'}

    def _pages(self, url: str):
        while url:
            body = self.graph.request(url, headers=self.headers)
            yield from body.get("value", [])
            url = body.get("@odata.nextLink")

    def work_plan(self, start: dt.date, end: dt.date) -> list:
        """"My work plan" occurrences (office, remote, timeOff), v1.0 since Dec 2025."""
        return list(self._pages(VIEW.format(start=f"{start.isoformat()}T00:00:00Z", end=f"{end.isoformat()}T00:00:00Z")))

    def events(self, start: dt.date, end: dt.date) -> list:
        query = urllib.parse.urlencode({"startDateTime": f"{start.isoformat()}T00:00:00",
                                        "endDateTime": f"{end.isoformat()}T00:00:00",
                                        "$select": "id,subject,start,location", "$top": "500"})
        return list(self._pages(f"{GRAPH}/me/calendarView?{query}"))

    def messages_from(self, sender: str, since: dt.date) -> list:
        """Every message from sender since a date, from any folder (so a rule filing them elsewhere,
        or Deleted Items until it's emptied, makes no difference)."""
        query = urllib.parse.urlencode({
            "$filter": f"from/emailAddress/address eq '{sender}' and receivedDateTime ge {since.isoformat()}T00:00:00Z",
            "$select": "subject,body,receivedDateTime", "$top": "100"})
        return list(self._pages(f"{GRAPH}/me/messages?{query}"))

    def rename(self, event_id: str, subject: str, location: str | None) -> None:
        body = {"subject": subject}
        if location:
            body["location"] = {"displayName": location}
        self.graph.request(f"{GRAPH}/me/events/{event_id}", method="PATCH", body=body, headers=self.headers)
