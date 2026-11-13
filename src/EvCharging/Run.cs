using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PyCompat;
using WinBackground;

namespace EvCharging;

/// <summary>The work plan or calendar couldn't be read, so nothing was decided.</summary>
public sealed class CalendarError(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>booker(config, day) -> outcome text; the form by default. Tests and parity checks pass a fake.</summary>
public delegate string Booker(Settings settings, DateOnly day);

/// <summary>What one run did: booked, failed, questions and the decisions (the Python's report dict).</summary>
public sealed class Report
{
    public List<string> Booked { get; } = [];
    public List<string> Failed { get; } = [];
    public List<string> Questions { get; } = [];
    public List<(string Date, string Action)> Decisions { get; } = [];

    /// <summary>The report as Python's dict, keys in its order: booked, failed, questions, decisions.</summary>
    public JsonObject ToJson() => new()
    {
        ["booked"] = Strings(Booked),
        ["failed"] = Strings(Failed),
        ["questions"] = Strings(Questions),
        ["decisions"] = new JsonArray(Decisions.Select(d => (JsonNode)new JsonObject { ["date"] = d.Date, ["action"] = d.Action }).ToArray()),
    };

    internal static JsonArray Strings(IEnumerable<string> items) => new(items.Select(i => (JsonNode)JsonValue.Create(i)).ToArray());
}

/// <summary>
/// One run: read the calendar, book what's asked for, apply the approvals, and report the questions. A port of run.py.
/// <para><b>Questions</b> are the things only you can decide: an entry asking for a slot on a day the work plan
/// doesn't say office, a booking that failed, a request declined, or the next working day's request still unapproved.
/// A scheduled run notifies them (one Windows notification) and passes them to the after_run hook, so another system
/// can queue them too:</para>
/// <code>{"config": "...", "questions": ["Thu 26 Nov: Travel block says EV but work plan says remote. Book EV?"],
///  "booked": ["2026-11-26"], "failed": [], "summary": "1 booked, 1 question"}</code>
/// <para>The hook runs after every run that isn't a dry run, questions or not: each run's questions replace the last
/// run's, so an empty list means the earlier ones are settled. It runs from the settings file's folder.</para>
/// <para>Nothing is ever cancelled.</para>
/// </summary>
public static class Runner
{
    public static string Summary(int booked, int failed, int questions) => $"{booked} booked, {failed} failed, {questions} question(s)";

    public static string Summary(Report report) => Summary(report.Booked.Count, report.Failed.Count, report.Questions.Count);

    /// <summary>Python's type(error).__name__, for "FormError: …" in a failed booking's outcome.</summary>
    public static string PythonType(Exception error) => error switch
    {
        PyError e => e.PythonType,
        PyValueError => "ValueError",
        PyKeyError => "KeyError",
        TimeoutException => "TimeoutError",
        FileNotFoundException or DirectoryNotFoundException => "FileNotFoundError",
        UnauthorizedAccessException => "PermissionError",
        IOException => "OSError",
        System.Text.Json.JsonException => "JSONDecodeError",
        _ => error.GetType().Name,
    };

    /// <summary>apply_approvals(): write each approved charge point into that day's morning entry, and return the
    /// questions.</summary>
    public static List<string> ApplyApprovals(Settings settings, IOutlook outlook, DateOnly today, bool dryRun, Action<string> log)
    {
        var rules = settings.Approvals;
        if (!rules.Enabled)
            return [];
        var state = State.Load(settings.StateFile);   // re-read: a booking just now adds to it
        var questions = new List<string>();
        try
        {
            var messages = outlook.MessagesFrom(rules.Sender, today.AddDays(-rules.LookBackDays));
            foreach (var (day, reply) in Planner.LatestReplies(settings, messages, today))
            {
                var label = Planner.Label(day);
                if (reply.Outcome == "declined")
                {
                    questions.Add($"{label}: EV request DECLINED.");
                    continue;
                }
                if (reply.Outcome != "approved" || string.IsNullOrEmpty(reply.Point))
                    continue;
                var subject = Planner.Renamed(settings, reply);
                var key = PyTime.Iso(day);
                if (state[key] is not JsonObject entry)
                    state[key] = entry = [];
                entry["approved"] = reply.Point;
                var events = outlook.Events(day, day.AddDays(1));
                var found = Planner.MorningEntry(settings, events, day);
                if (found is null)
                {
                    // Usually a day you're no longer going in. Cancelling is left to you, so it isn't raised.
                    log($"{label}: EV approved ({reply.Point}) but no morning {settings.Label} to update (not raised)");
                    continue;
                }
                if (Planner.Subject(found) == subject)
                    continue;
                if (dryRun)
                {
                    log($"would update {label} {settings.Label} -> {subject}");
                    continue;
                }
                var id = found["id"] ?? throw new PyKeyError("id");
                outlook.Rename(Py.Str(id), subject, rules.SetLocation ? reply.Point : null);
                log($"updated {label} {settings.Label} -> {subject}");
            }
        }
        catch (Exception error) when (error is not WriteRefusedException)
        {
            log($"FAILED applying approvals: {error.Message}");
            return [$"Couldn't read the approval emails: {PyStr.Head(error.Message, 120)}"];
        }
        if (rules.ChaseNextWorkingDay)
        {
            var nearest = Planner.NextWorkingDay(today);
            if (state[PyTime.Iso(nearest)] is JsonObject entry && entry.Count > 0 && !Py.Truthy(entry["approved"]))
                questions.Add($"{Planner.Label(nearest)}: EV requested but not approved yet.");
        }
        if (!dryRun)
            State.Save(settings.StateFile, state);
        return questions;
    }

    /// <summary>plan(): the decisions for the window, from the work plan, the calendar and the state file.</summary>
    public static List<Decision> Plan(Settings settings, IOutlook outlook, DateOnly today)
    {
        var (start, end) = Planner.Window(settings, today);
        Dictionary<DateOnly, HashSet<string>> workPlan;
        Dictionary<DateOnly, bool> entries;
        try
        {
            workPlan = settings.WorkPlan.Length > 0 ? Planner.WorkPlanDays(outlook.WorkPlan(start, end)) : [];
            entries = Planner.EntryDays(settings, outlook.Events(start, end));
        }
        catch (Exception error) when (error is not WriteRefusedException)
        {
            throw new CalendarError($"Couldn't read the work plan or calendar: {error.Message}", error);
        }
        return Planner.Decide(settings, today, workPlan, entries, State.Load(settings.StateFile));
    }

    /// <summary>run(): booker(settings, day) -> outcome text; the form (through the submit door) by default.</summary>
    public static Report Run(Settings settings, IOutlook outlook, DateOnly today, bool dryRun, bool approvalsOnly, Action<string> log,
        Booker booker, Func<DateTime> now)
    {
        var report = new Report();
        if (!approvalsOnly)
        {
            foreach (var decision in Plan(settings, outlook, today))
            {
                report.Decisions.Add((PyTime.Iso(decision.Day), decision.Action));
                if (decision.Action == "ask")
                    report.Questions.Add(decision.Text);
                else if (decision.Action == "log")
                    log(decision.Text);
                else if (dryRun)
                    log(decision.Text);
                else
                    Book(settings, decision.Day, report, log, booker, now);
            }
        }
        report.Questions.AddRange(ApplyApprovals(settings, outlook, today, dryRun, log));
        foreach (var question in report.Questions)
            log($"ask: {question}");
        return report;
    }

    private static void Book(Settings settings, DateOnly day, Report report, Action<string> log, Booker booker, Func<DateTime> now)
    {
        var start = Form.StartTime(settings, Car(settings));
        string outcome;
        try
        {
            outcome = booker(settings, day);
        }
        catch (Exception error) when (error is not WriteRefusedException)
        {
            outcome = $"{PythonType(error)}: {error.Message}";
        }
        log($"book {Planner.Label(day)}: {outcome}");
        if (outcome.StartsWith("Submitted", StringComparison.Ordinal))
        {
            State.RecordSubmission(settings.StateFile, day, start, now());
            report.Booked.Add(PyTime.Iso(day));
        }
        else
        {
            report.Failed.Add(PyTime.Iso(day));
            report.Questions.Add($"{Planner.Label(day)}: booking FAILED. {PyStr.Head(outcome, 120)}");
        }
    }

    private static JsonObject Car(Settings settings)
    {
        try
        {
            return Form.Car(settings);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The real booker: the form, headless or not, through the given submit door.</summary>
    public static Booker FormBooker(SubmitDoor door, bool headless, FormOptions? options = null) =>
        (settings, day) => Form.Book(settings, day, door, submit: true, headless: headless, start: Form.StartTime(settings, Car(settings)), options);

    /// <summary>The hook's stdin: json.dumps of config, questions, booked, failed and summary.</summary>
    public static string HookPayload(string configPath, Report report) =>
        PyJson.Dump(new JsonObject
        {
            ["config"] = configPath,
            ["questions"] = Report.Strings(report.Questions),
            ["booked"] = Report.Strings(report.Booked),
            ["failed"] = Report.Strings(report.Failed),
            ["summary"] = Summary(report),
        });

    /// <summary>after_run(): the settings' command, run in the settings file's folder with the report on stdin (UTF-8).
    /// Its output goes to the log; nothing it does stops the run.</summary>
    public static void AfterRun(Settings settings, Report report, Action<string> log, HookRunner? runner = null)
    {
        var payload = HookPayload(settings.Path, report);
        log($"after-run hook: {string.Join(' ', settings.AfterRun)}");
        try
        {
            var done = (runner ?? DefaultHook)(settings.AfterRun, payload, Path.GetDirectoryName(settings.Path)!);
            foreach (var line in PyStr.SplitLines(done.StandardOutput + done.StandardError))
                if (PyStr.Strip(line).Length > 0)
                    log($"  hook: {PyStr.Strip(line)}");
            if (done.ExitCode != 0)
                log($"after-run hook exited {done.ExitCode}");
        }
        catch (Exception error)
        {
            log($"after-run hook failed: {(error is Win32Exception w ? Shell.WinErrorText(w) : error.Message)}");
        }
    }

    public delegate Completed HookRunner(IReadOnlyList<string> command, string stdin, string workingDirectory);

    private static Completed DefaultHook(IReadOnlyList<string> command, string stdin, string workingDirectory) =>
        Shell.Run(command[0], command.Skip(1), TimeSpan.FromSeconds(300), stdin, workingDirectory, utf8: true);

    /// <summary>The run lock's name: ev-charging-run-{key(config.path)}, win-background's key with Python's
    /// lower-casing, so a Python and a .NET run of the same settings exclude each other.</summary>
    public static string LockName(string configPath) =>
        "ev-charging-run-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PyStr.Lower(configPath))))[..16];

    /// <summary>locked_run(): one run at a time per settings file (two at once could request the same day twice), then
    /// the hook unless it's a dry run or approvals only.</summary>
    public static Report LockedRun(Settings settings, IOutlook outlook, Booker booker, Action<string> log, bool dryRun = false,
        bool hook = false, bool approvalsOnly = false, DateOnly? today = null, Func<DateTime>? now = null, HookRunner? runner = null)
    {
        now ??= () => DateTime.Now;
        Report report;
        using (Locks.Exclusive(LockName(settings.Path), 900, $"run for {settings.Path}"))
            report = Run(settings, outlook, today ?? DateOnly.FromDateTime(now()), dryRun, approvalsOnly, log, booker, now);
        if (hook && !dryRun && !approvalsOnly && settings.AfterRun.Count > 0)
            AfterRun(settings, report, log, runner);
        return report;
    }
}
