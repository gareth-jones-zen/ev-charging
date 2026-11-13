using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyCompat;

namespace EvCharging;

/// <summary>One day's decision: book, ask or log, and the text that goes with it.</summary>
public sealed record Decision(DateOnly Day, string Action, string Text);

/// <summary>A reply about a request: its day, start time, charge point, outcome and when it arrived.</summary>
public sealed record Reply(DateOnly Date, string Start, string? Point, string Outcome, string Received);

/// <summary>
/// The decisions, with no network: which days to book, what to ask, and what the approvals say. Kept apart from
/// Graph and the browser so every rule can be tested with plain data. A port of planner.py.
/// <para><b>The decision table</b>, for each day from today + notice_days for days_ahead days (weekdays only by
/// default):</para>
/// <code>
///     work plan says office, entry asks for a slot, not yet requested  -> book it
///     entry asks for a slot, work plan doesn't say office              -> ask, don't book
///     work plan says office, an entry that doesn't ask, not requested   -> nothing (logged)
///     already requested, work plan no longer says office                -> logged only, never raised
/// </code>
/// <para>It never cancels anything: cancelling means contacting whoever runs the chargers, which is left to you.
/// With work_plan = "" the work plan isn't read, and every day whose entry asks is booked.</para>
/// </summary>
public static partial class Planner
{
    // ---- the calendar ---------------------------------------------------------------------------------------

    /// <summary>The day's travel block (by default, a subject starting "Travel"). A renamed one still counts.</summary>
    public static bool IsEntry(Settings settings, string? subject) => PyRegex.Get(settings.Entry, true).IsMatch(subject ?? "");

    /// <summary>The entry asks for a slot (by default "EV" as a word, any case).</summary>
    public static bool Asks(Settings settings, string? subject) => PyRegex.Get(settings.Book, true).IsMatch(subject ?? "");

    internal static string? Subject(JsonObject item) => item["subject"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static string StartText(JsonObject item) => (string)item["start"]!["dateTime"]!;

    /// <summary>Each day with an entry -> whether any of that day's entries asks for a slot.</summary>
    public static Dictionary<DateOnly, bool> EntryDays(Settings settings, IEnumerable<JsonObject> events)
    {
        var days = new Dictionary<DateOnly, bool>();
        foreach (var e in events)
        {
            if (!IsEntry(settings, Subject(e)))
                continue;
            var day = FromIsoDate(PyStr.Head(StartText(e), 10));
            days[day] = days.GetValueOrDefault(day) || Asks(settings, Subject(e));
        }
        return days;
    }

    /// <summary>Day -> the work plan's location types for it; any office segment makes it an office day.</summary>
    public static Dictionary<DateOnly, HashSet<string>> WorkPlanDays(IEnumerable<JsonObject> occurrences)
    {
        var days = new Dictionary<DateOnly, HashSet<string>>();
        foreach (var item in occurrences)
        {
            var day = FromIsoDate(PyStr.Head(StartText(item), 10));
            if (!days.TryGetValue(day, out var kinds))
                days[day] = kinds = [];
            kinds.Add(item.ContainsKey("workLocationType") ? (string)item["workLocationType"]! : "unknown");
        }
        return days;
    }

    /// <summary>The days considered: [start, end).</summary>
    public static (DateOnly Start, DateOnly End) Window(Settings settings, DateOnly today)
    {
        var start = today.AddDays(settings.NoticeDays);
        return (start, start.AddDays(settings.DaysAhead));
    }

    /// <summary>"Mon 16 Nov".</summary>
    public static string Label(DateOnly day) => PyTime.Strftime(day, "%a %d %b");

    public static List<Decision> Decide(Settings settings, DateOnly today, IReadOnlyDictionary<DateOnly, HashSet<string>> plan,
        IReadOnlyDictionary<DateOnly, bool> entries, JsonObject state)
    {
        var (start, end) = Window(settings, today);
        var decisions = new List<Decision>();
        var wanted = PyStr.Lower(settings.WorkPlan);
        for (var day = start; day < end; day = day.AddDays(1))
        {
            if (settings.WeekdaysOnly && PyTime.Weekday(day) >= 5)
                continue;
            var kinds = plan.GetValueOrDefault(day);
            var office = settings.WorkPlan.Length == 0 || (kinds is not null && kinds.Any(k => PyStr.Lower(k) == wanted));
            var wants = entries.GetValueOrDefault(day);
            var requested = state.ContainsKey(PyTime.Iso(day));
            if (office && wants && !requested)
                decisions.Add(new Decision(day, "book", $"would book {Label(day)}"));
            else if (wants && !office && !requested)
            {
                var where = string.Join(", ", (kinds ?? ["nothing set"]).Order(PyStr.Order));
                decisions.Add(new Decision(day, "ask", $"{Label(day)}: {settings.Label} says EV but work plan says {where}. Book EV?"));
            }
            else if (office && !requested && entries.ContainsKey(day))
                decisions.Add(new Decision(day, "log",
                    $"{Label(day)}: {(settings.WorkPlan.Length > 0 ? settings.WorkPlan : "a")} day, {settings.Label} doesn't say EV: not booked"));
            else if (requested && !office)
                // Never raised: cancelling means contacting whoever runs the chargers, which is left to you.
                decisions.Add(new Decision(day, "log", $"{Label(day)}: EV requested, but work plan no longer says {settings.WorkPlan} (not raised)"));
        }
        return decisions;
    }

    public static DateOnly NextWorkingDay(DateOnly today)
    {
        var day = today.AddDays(1);
        while (PyTime.Weekday(day) >= 5)
            day = day.AddDays(1);
        return day;
    }

    // ---- approvals --------------------------------------------------------------------------------------------

    public static bool IsReply(Settings settings, JsonObject message) =>
        PyStr.Lower(Subject(message) ?? "").Contains(PyStr.Lower(settings.Approvals.Subject), StringComparison.Ordinal);

    /// <summary>The body as text: tags become spaces, entities are decoded, whitespace runs become one space.</summary>
    public static string BodyText(JsonObject message)
    {
        var content = message["body"] is JsonObject body && body.ContainsKey("content") ? (string?)body["content"] ?? "" : "";
        var text = PyRegex.Get("<[^>]+>").Sub(" ", content);
        return PyRegex.Get(@"\s+").Sub(" ", PyHtml.Unescape(text));
    }

    /// <summary>Date, start time, charge point and outcome from a reply. End Time is never read: in the process
    /// this was built for it carries the approval date, month first; the slot is slot_hours from the start.</summary>
    public static Reply? ParseReply(Settings settings, JsonObject message)
    {
        var rules = settings.Approvals;
        var text = BodyText(message);
        var day = PyRegex.Get(rules.Date, true).Search(text);
        var start = PyRegex.Get(rules.Start, true).Search(text);
        if (day is null || start is null)
            return null;
        var found = PyRegex.Get(rules.Point, true).Search(text);
        var point = found is null ? null : PyStr.Strip(Group1(found));
        if (string.IsNullOrEmpty(point))
        {
            var lower = PyStr.Lower(text);
            point = rules.ChargePoints.FirstOrDefault(p => lower.Contains(PyStr.Lower(p), StringComparison.Ordinal));
        }
        var subject = Subject(message) ?? "";
        var outcome = PyRegex.Get(rules.Approved, true).IsMatch(subject) ? "approved"
            : PyRegex.Get(rules.Declined, true).IsMatch(subject) ? "declined" : "other";
        return new Reply(FromIsoDate(Group1(day)), Group1(start), point, outcome,
            message.ContainsKey("receivedDateTime") ? (string?)message["receivedDateTime"] ?? "" : "");
    }

    /// <summary>match.group(1): IndexError if the pattern has no group, and Python's None for one that took no part
    /// (which the Python then fails on).</summary>
    private static string Group1(Match match)
    {
        if (match.Groups.Count < 2)
            throw new PyError("IndexError", "no such group");
        if (!match.Groups[1].Success)
            throw new PyError("TypeError", "a group that took no part in the match (None)");
        return match.Groups[1].Value;
    }

    /// <summary>The newest reply for each day from today on, in case a request was revised.</summary>
    public static SortedDictionary<DateOnly, Reply> LatestReplies(Settings settings, IEnumerable<JsonObject> messages, DateOnly today)
    {
        var latest = new SortedDictionary<DateOnly, Reply>();
        static string Received(JsonObject m) => m.ContainsKey("receivedDateTime") ? (string?)m["receivedDateTime"] ?? "" : "";
        // sorted(..., reverse=True) is stable: equal keys keep their order.
        foreach (var message in messages.Where(m => IsReply(settings, m)).OrderByDescending(Received, PyStr.Order))
        {
            var reply = ParseReply(settings, message);
            if (reply is not null && reply.Date >= today && !latest.ContainsKey(reply.Date))
                latest[reply.Date] = reply;
        }
        return latest;
    }

    /// <summary>The morning entry's new subject (by default "Travel; EV – Charger 1 (Rear), 08:00–12:00").</summary>
    public static string Renamed(Settings settings, Reply reply)
    {
        var start = StrpHourMinute(reply.Start);
        var ticks = (long)Math.Round(settings.SlotHours * 3600 * 1_000_000, MidpointRounding.ToEven) * 10;
        var end = start.Add(TimeSpan.FromTicks(ticks));
        return PyFormat.Format(settings.Approvals.Rename, new Dictionary<string, PyFormat.Value>
        {
            ["point"] = new(null, reply.Point ?? "None"),
            ["start"] = PyTime.Strftime(start, "%H:%M"),
            ["end"] = PyTime.Strftime(end, "%H:%M"),
            ["date"] = reply.Date,
        });
    }

    /// <summary>datetime.strptime(text, "%H:%M"): 1900-01-01 at that time, with Python's pattern and messages.</summary>
    internal static DateTime StrpHourMinute(string text)
    {
        var m = HourMinute().Match(text);
        if (!m.Success)
            throw new PyValueError($"time data {Py.ReprString(text)} does not match format '%H:%M'");
        if (m.Length != text.Length)
            throw new PyValueError($"unconverted data remains: {text[m.Length..]}");
        return new DateTime(1900, 1, 1, Digits(m.Groups[1].Value), Digits(m.Groups[2].Value), 0, DateTimeKind.Unspecified);
    }

    private static int Digits(string text) => text.Aggregate(0, (n, c) => n * 10 + (int)char.GetNumericValue(c));

    // _strptime's %H and %M (with \d as Python's: any decimal digit), matched from the start.
    [System.Text.RegularExpressions.GeneratedRegex(@"\A(2[0-3]|[0-1]\d|\d):([0-5]\d|\d)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HourMinute();

    /// <summary>The earliest entry on that day starting before morning_before (an evening one is left alone).</summary>
    public static JsonObject? MorningEntry(Settings settings, IEnumerable<JsonObject> events, DateOnly day)
    {
        var iso = PyTime.Iso(day);
        return events
            .Where(e => IsEntry(settings, Subject(e)) && PyStr.Head(StartText(e), 10) == iso
                        && Hour(StartText(e)) < settings.Approvals.MorningBefore)
            .OrderBy(StartText, PyStr.Order)
            .FirstOrDefault();
    }

    /// <summary>int(dateTime[11:13]), with int()'s message when it isn't a number.</summary>
    private static long Hour(string start)
    {
        var text = start.Length > 11 ? start[11..Math.Min(13, start.Length)] : "";
        try
        {
            return PyStr.Int(text);
        }
        catch (FormatException)
        {
            throw new PyValueError($"invalid literal for int() with base 10: {Py.ReprString(text)}");
        }
    }

    /// <summary>date.fromisoformat("yyyy-mm-dd"), with Python 3.14's messages.</summary>
    public static DateOnly FromIsoDate(string text)
    {
        if (text.Length != 10 || text[4] != '-' || text[7] != '-' || !text.Where((c, i) => i is not (4 or 7)).All(char.IsAsciiDigit))
            throw new PyValueError($"Invalid isoformat string: {Py.ReprString(text)}");
        var year = int.Parse(text[..4], CultureInfo.InvariantCulture);
        var month = int.Parse(text[5..7], CultureInfo.InvariantCulture);
        var day = int.Parse(text[8..], CultureInfo.InvariantCulture);
        if (year < 1)
            throw new PyValueError($"year {year} is out of range");
        if (month is < 1 or > 12)
            throw new PyValueError("month must be in 1..12");
        if (day < 1 || day > DateTime.DaysInMonth(year, month))
            throw new PyValueError($"day {day} must be in range 1..{DateTime.DaysInMonth(year, month)} for month {month} in year {year}");
        return new DateOnly(year, month, day);
    }
}

/// <summary>Any other Python exception the Python would have raised at the same point: its type name and str().</summary>
public sealed class PyError(string type, string message) : Exception(message)
{
    public string PythonType { get; } = type;
}
