using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using PyCompat;
using WinBackground;

namespace EvCharging;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    /// <summary>Where a run's log lines are echoed (the console exe: stdout; the background exe: nowhere).</summary>
    public TextWriter? LogEcho { get; init; }

    public Func<string, string?> Env { get; init; } = Settings.Environment;

    /// <summary>client(config, interactive): the unfenced Graph for these settings. Reads go through the read-only
    /// fence over it; only a Live write door writes through it.</summary>
    public Func<Settings, bool?, IGraph> Graph { get; init; } = (settings, interactive) => M365GraphClient.For(settings, interactive);

    /// <summary>The booker for a real run: the form through a Live submit door. Tests give a fake.</summary>
    public Func<SubmitDoor, Booker> Booker { get; init; } = door => Runner.FormBooker(door, headless: true);

    /// <summary>book's form: Form.Book by default.</summary>
    public Func<Settings, DateOnly, SubmitDoor, bool, bool, string, string> BookForm { get; init; } =
        (settings, day, door, submit, headless, start) => Form.Book(settings, day, door, submit, headless, start);

    public Action<Settings> FormSignIn { get; init; } = settings => Form.SignInWindow(settings);
    public Action<string, IEnumerable<string>> Notify { get; init; } = Toast.Notify;
    public Runner.HookRunner? HookRunner { get; init; }
    public Func<string, ITask> Tasks { get; init; } = Autostart.Create;
    public string ProgramDir { get; init; } = AppContext.BaseDirectory;
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
}

/// <summary>ev-charging: book an office EV charging slot when your calendar asks for one. The same commands, options,
/// texts and exit codes as the Python version (argparse's help and errors included): 0; 1 for a run that couldn't read
/// the calendar, needs a sign-in or timed out (and anything unexpected); 2 for no settings file, settings that can't be
/// used, a form that couldn't be filled, or a usage error.</summary>
public static class Cli
{
    public const string Prog = "ev-charging";

    private const string Usage = "usage: ev-charging [-h] [--version] [--config CONFIG]\n"
        + "                   {config,sign-in,book,run,approvals,status,autostart} ...";

    private const string Help = """
        ev-charging: book an office EV charging slot when your calendar asks for one.

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

        positional arguments:
          {config,sign-in,book,run,approvals,status,autostart}
            config              create the settings file if missing, and say where it
                                is
            sign-in             sign in to Microsoft, and to the form in the tool's
                                Edge window
            book                fill in (and with --submit, submit) the form for one
                                day
            run                 the daily run: book, apply approvals, list questions
            approvals           only apply the approval emails to the calendar
            status              requests from today on, and whether each is approved
            autostart           run daily and at logon (Task Scheduler)

        options:
          -h, --help            show this help message and exit
          --version             show program's version number and exit
          --config CONFIG       settings file (default %APPDATA%\ev-
                                charging\config.toml)
        """;

    private static readonly string[] Commands = ["config", "sign-in", "book", "run", "approvals", "status", "autostart"];

    private static readonly Dictionary<string, string> SubUsages = new()
    {
        ["config"] = "usage: ev-charging config [-h]",
        ["sign-in"] = "usage: ev-charging sign-in [-h] [--graph-only]",
        ["book"] = "usage: ev-charging book [-h] [--submit] [--start START] [--headless] [--force]\n                        date",
        ["run"] = "usage: ev-charging run [-h] [--dry-run] [--json] [--hook] [--scheduled]",
        ["approvals"] = "usage: ev-charging approvals [-h] [--dry-run] [--json]",
        ["status"] = "usage: ev-charging status [-h]",
        ["autostart"] = "usage: ev-charging autostart [-h] {on,off,status}",
    };

    private static readonly Dictionary<string, string> SubHelps = new()
    {
        ["config"] = "options:\n  -h, --help  show this help message and exit",
        ["sign-in"] = "options:\n  -h, --help    show this help message and exit\n  --graph-only  skip the form's Edge window",
        ["book"] = """
            positional arguments:
              date           yyyy-mm-dd

            options:
              -h, --help     show this help message and exit
              --submit       submit it (default: fill in and screenshot only)
              --start START  start time, such as 08:00 (default from the settings)
              --headless     no visible window
              --force        submit even if this day was already requested
            """,
        ["run"] = """
            options:
              -h, --help   show this help message and exit
              --dry-run    say what it would do; change nothing
              --json       print the report as JSON
              --hook       then run the after_run command (not on a dry run)
              --scheduled  as the daily task runs it: never open a browser to sign in, and
                           notify
            """,
        ["approvals"] = """
            options:
              -h, --help  show this help message and exit
              --dry-run   say what it would do; change nothing
              --json      print the report as JSON
            """,
        ["status"] = "options:\n  -h, --help  show this help message and exit",
        ["autostart"] = "positional arguments:\n  {on,off,status}\n\noptions:\n  -h, --help       show this help message and exit",
    };

    /// <summary>Each command's options, in the order argparse registered them (its "could match" lists keep it).</summary>
    private static readonly Dictionary<string, string[]> SubOptions = new()
    {
        ["config"] = ["--help"],
        ["sign-in"] = ["--help", "--graph-only"],
        ["book"] = ["--help", "--submit", "--start", "--headless", "--force"],
        ["run"] = ["--help", "--dry-run", "--json", "--hook", "--scheduled"],
        ["approvals"] = ["--help", "--dry-run", "--json"],
        ["status"] = ["--help"],
        ["autostart"] = ["--help"],
    };

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private sealed class UsageError(string message, string usage = Usage, string prog = Prog) : Exception(message)
    {
        public string UsageText { get; } = usage;
        public string ProgText { get; } = prog;
    }

    internal sealed record Args(string? Config, string Command, bool DryRun, bool Json, bool Hook, bool Scheduled, bool GraphOnly,
        string? Date, bool Submit, string? Start, bool Headless, bool Force, string? Action);

    public static int Run(string[] argv, CliHost host)
    {
        Args args;
        try
        {
            var parsed = Parse(argv, host);
            if (parsed is null)
                return 0;   // --help or --version
            args = parsed;
        }
        catch (UsageError error)
        {
            Print(host.Err, $"{error.UsageText}\n{error.ProgText}: error: {error.Message}");
            return 2;
        }

        if (args.Command == "config")
        {
            Print(host.Out, !string.IsNullOrEmpty(args.Config) ? args.Config : Settings.CreateDefault(host.Env));
            return 0;
        }
        if (args.Command == "autostart")
            return AutostartCommand(args, host);
        try
        {
            var settings = Settings.Load(args.Config, host.Env);
            return args.Command switch
            {
                "sign-in" => SignIn(settings, args.GraphOnly, host),
                "book" => Book(settings, args, host),
                "status" => Status(settings, host),
                _ => RunCommand(settings, args, host),
            };
        }
        catch (Exception error) when (error is M365Graph.SignInNeededException or FileNotFoundException or PyValueError
                                          or System.Text.Json.JsonException or TimeoutException or FormError)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        catch (Exception error)
        {
            // Python printed a traceback here; the type and message are the last line of it.
            Print(host.Err, $"{Runner.PythonType(error)}: {error.Message}");
            return 1;
        }
    }

    private static int RunCommand(Settings settings, Args args, CliHost host)
    {
        var log = Logger.Create(settings.LogFile, !args.Json && !args.Scheduled ? host.LogEcho ?? host.Out : null, clock: host.Now);
        Report report;
        try
        {
            var graph = host.Graph(settings, args.Scheduled ? false : null);
            // A dry run can't write: the calendar door refuses, and the form is never opened (or submitted).
            var door = args.DryRun ? WriteDoor.Refusing() : WriteDoor.Live(graph);
            var booker = args.DryRun ? (s, d) => throw new WriteRefusedException("a booking in a dry run") : host.Booker(new SubmitDoor(SubmitMode.Live));
            report = Runner.LockedRun(settings, new Outlook(settings, graph, door), booker, log, args.DryRun, args.Hook,
                args.Command == "approvals", null, host.Now, host.HookRunner);
        }
        catch (Exception error)
        {
            log($"FAILED: {PyStr.Head(error.Message, 300)}");
            if (args.Scheduled)
            {
                var title = error is M365Graph.SignInNeededException ? "ev-charging needs a sign-in"
                    : error is CalendarError ? "EV booking: couldn't read your calendar"
                    : "EV booking failed";
                host.Notify(title, [PyStr.Head(error.Message, 200)]);
            }
            if (error is M365Graph.SignInNeededException or CalendarError or TimeoutException)
            {
                Print(host.Err, error.Message);
                return 1;
            }
            throw;
        }
        log($"done: {Runner.Summary(report)}{(args.DryRun ? " (dry run)" : "")}");
        if (args.Scheduled && report.Questions.Count > 0 && !args.DryRun)
            host.Notify($"EV booking: {report.Questions.Count} to check", report.Questions);
        if (args.Json)
            Print(host.Out, PyJson.Dump(report.ToJson(), indent: 1));
        return 0;
    }

    private static int Book(Settings settings, Args args, CliHost host)
    {
        var day = Planner.FromIsoDate(args.Date!);
        string outcome;
        // The same lock as a run (as Python 0.1.1), so the two can't request the same day at once.
        using (Locks.Exclusive(Runner.LockName(settings.Path), 900, $"run for {settings.Path}"))
        {
            if (args.Submit && State.Load(settings.StateFile).ContainsKey(PyTime.Iso(day)) && !args.Force)
            {
                Print(host.Out, $"Already requested for {Planner.Label(day)}; skipping (--force to request it again).");
                return 0;
            }
            var start = !string.IsNullOrEmpty(args.Start) ? args.Start : Form.StartTime(settings, Form.Car(settings));
            // Only `book --submit` (and a real run) may click Submit; a preview never reaches the door.
            if (!args.Submit)
                outcome = host.BookForm(settings, day, SubmitDoor.Refusing, false, args.Headless, start);
            else
            {
                var submitted = Submit(settings, day, start, args.Headless, host);
                if (submitted is null)
                    return 1;
                outcome = submitted;
            }
        }
        if (outcome.StartsWith("Submitted", StringComparison.Ordinal))
            outcome += " Approval comes by email.";
        Print(host.Out, outcome);
        return outcome.StartsWith("Submitted", StringComparison.Ordinal) || outcome.StartsWith("Preview", StringComparison.Ordinal) ? 0 : 1;
    }

    /// <summary>Record the day, then submit; take the record off again if it wasn't submitted (see State). Null if it
    /// couldn't be recorded, so nothing was submitted.</summary>
    private static string? Submit(Settings settings, DateOnly day, string start, bool headless, CliHost host)
    {
        JsonNode? before;
        try
        {
            before = State.RecordSubmission(settings.StateFile, day, start, host.Now());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Print(host.Err, $"Not submitted: couldn't record it in the state file first. {error.Message}");
            return null;
        }
        string outcome;
        try
        {
            outcome = host.BookForm(settings, day, new SubmitDoor(SubmitMode.Live), true, headless, start);
        }
        catch
        {
            Undo(settings, day, before, host);
            throw;
        }
        if (!outcome.StartsWith("Submitted", StringComparison.Ordinal))
            Undo(settings, day, before, host);
        return outcome;
    }

    private static void Undo(Settings settings, DateOnly day, JsonNode? before, CliHost host)
    {
        try
        {
            State.UndoSubmission(settings.StateFile, day, before);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Print(host.Err, $"{Planner.Label(day)} is still marked as requested: the state file couldn't be updated ({error.Message}). "
                + "The daily run won't request it; book it again with --force.");
        }
    }

    private static int SignIn(Settings settings, bool graphOnly, CliHost host)
    {
        host.Graph(settings, true).Token();
        Print(host.Out, "Signed in to Microsoft Graph.");
        if (!graphOnly)
        {
            Print(host.Out, "An Edge window opens on the form: sign in there if it asks. It closes once the form loads.");
            host.FormSignIn(settings);
            Print(host.Out, "The form loads, so bookings can be submitted.");
        }
        return 0;
    }

    /// <summary>_status(): requests from today on, and whether each is approved.</summary>
    internal static int Status(Settings settings, CliHost host)
    {
        var today = PyTime.Iso(DateOnly.FromDateTime(host.Now()));
        var upcoming = State.Load(settings.StateFile)
            .Where(kv => PyStr.CompareCodePoints(kv.Key, today) >= 0)
            .OrderBy(kv => kv.Key, PyStr.Order)
            .ToList();
        if (upcoming.Count == 0)
            Print(host.Out, "No requests from today on.");
        foreach (var (day, node) in upcoming)
        {
            var entry = node as JsonObject ?? [];
            var start = entry.ContainsKey("start") ? Py.Str(entry["start"]) : "?";
            var pad = Math.Max(0, 5 - PyStr.Length(start));
            Print(host.Out, $"{Planner.Label(Planner.FromIsoDate(day))}  {start}{new string(' ', pad)}  "
                            + (Py.Truthy(entry["approved"]) ? $"approved: {Py.Str(entry["approved"])}" : "awaiting approval"));
        }
        return 0;
    }

    private static int AutostartCommand(Args args, CliHost host)
    {
        var task = host.Tasks(Autostart.TaskName);
        try
        {
            if (args.Action == "on")
            {
                var settings = Settings.Load(args.Config, host.Env);   // check the settings load before scheduling them
                var (exe, taskArgs) = Autostart.Command(host.ProgramDir, settings.Path);
                var command = task.Enable(exe, taskArgs, Path.GetDirectoryName(settings.Path), Autostart.Schedule(settings.DailyAt));
                Print(host.Out, $"Autostart is on: '{Autostart.TaskName}' runs daily at {settings.DailyAt} and at logon, and has "
                                + $"started now.\n  {command}\n  log: {settings.LogFile}");
            }
            else if (args.Action == "off")
                Print(host.Out, task.Disable() ? "Autostart is off." : "Autostart was already off.");
            else
            {
                var info = task.Status();
                if (info is null)
                    Print(host.Out, "Autostart is off.");
                else
                {
                    Print(host.Out, $"Autostart is on ('{Autostart.TaskName}').");
                    foreach (var key in new[] { "state", "last_run", "last_result", "next_run", "command" })
                        Print(host.Out, $"  {key.Replace('_', ' '),-12} {info.GetValueOrDefault(key, "")}");
                }
            }
            return 0;
        }
        catch (Exception error) when (error is InvalidOperationException or FileNotFoundException or PyValueError or Win32Exception or ArgumentException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
    }

    /// <summary>print(): a line, with "\n" written as "\r\n" as Python's stdout does on Windows.</summary>
    private static void Print(TextWriter writer, string text)
    {
        try
        {
            writer.Write(PyStr.WindowsNewlines(text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n"));
            writer.Flush();
        }
        catch (IOException)
        {
            // a closed console mustn't stop the tool
        }
    }

    // ---- argparse, as far as these commands use it ------------------------------------------------------------

    private static string Quoted(IEnumerable<string> choices) => string.Join(", ", choices.Select(c => $"'{c}'"));

    internal static Args? Parse(string[] argv, CliHost host)
    {
        string? config = null;
        var extras = new List<string>();
        string[] mainOptions = ["--help", "--version", "--config"];
        var i = 0;
        string? command = null;
        for (; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (arg == "--")
            {
                i++;
                break;
            }
            if (!IsOption(arg))
            {
                command = arg;
                i++;
                break;
            }
            var option = Option(arg, mainOptions, Usage, Prog);
            if (option is null)
            {
                extras.Add(arg);
                continue;
            }
            var (name, value) = option.Value;
            switch (name)
            {
                case "--help":
                    Print(host.Out, $"{Usage}\n\n{Help.TrimEnd('\n', '\r')}");
                    return null;
                case "--version":
                    Print(host.Out, Version);
                    return null;
                case "--config":
                    config = value ?? (i + 1 < argv.Length && !IsOption(argv[i + 1]) ? argv[++i] : throw new UsageError("argument --config: expected one argument"));
                    break;
            }
        }
        if (command is null && i < argv.Length)
            command = argv[i++];
        if (command is null)
            throw new UsageError("the following arguments are required: command");
        if (!Commands.Contains(command))
            throw new UsageError($"argument command: invalid choice: '{command}' (choose from {Quoted(Commands)})");

        var usage = SubUsages[command];
        var sub = $"{Prog} {command}";
        var options = SubOptions[command];
        bool dryRun = false, json = false, hook = false, scheduled = false, graphOnly = false, submit = false, headless = false, force = false;
        string? start = null;
        var positionals = new List<string>();
        var onlyPositional = false;
        for (; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (!onlyPositional && arg == "--")
            {
                onlyPositional = true;
                continue;
            }
            if (!onlyPositional && IsOption(arg))
            {
                var option = Option(arg, options, usage, sub);
                if (option is null)
                {
                    extras.Add(arg);
                    continue;
                }
                var (name, value) = option.Value;
                bool Flag() => value is null ? true : throw new UsageError($"argument {name}: ignored explicit argument '{value}'", usage, sub);
                switch (name)
                {
                    case "--help":
                        Print(host.Out, $"{usage}\n\n{SubHelps[command].TrimEnd('\n', '\r')}");
                        return null;
                    case "--dry-run": dryRun = Flag(); break;
                    case "--json": json = Flag(); break;
                    case "--hook": hook = Flag(); break;
                    case "--scheduled": scheduled = Flag(); break;
                    case "--graph-only": graphOnly = Flag(); break;
                    case "--submit": submit = Flag(); break;
                    case "--headless": headless = Flag(); break;
                    case "--force": force = Flag(); break;
                    case "--start":
                        start = value ?? (i + 1 < argv.Length && !IsOption(argv[i + 1]) ? argv[++i]
                            : throw new UsageError("argument --start: expected one argument", usage, sub));
                        break;
                }
                continue;
            }
            positionals.Add(arg);
        }
        string? date = null, action = null;
        var wanted = command switch { "book" => "date", "autostart" => "action", _ => null };
        if (wanted is not null)
        {
            if (positionals.Count == 0)
                throw new UsageError($"the following arguments are required: {wanted}", usage, sub);
            if (wanted == "date")
                date = positionals[0];
            else
            {
                action = positionals[0];
                string[] choices = ["on", "off", "status"];
                if (!choices.Contains(action))
                    throw new UsageError($"argument action: invalid choice: '{action}' (choose from {Quoted(choices)})", usage, sub);
            }
            positionals.RemoveAt(0);
        }
        extras.AddRange(positionals);
        if (extras.Count > 0)
            throw new UsageError($"unrecognized arguments: {string.Join(' ', extras)}");
        return new Args(config, command, dryRun, json, hook, scheduled, graphOnly, date, submit, start, headless, force, action);
    }

    // argparse treats "-5" as a value, not an option, when no option looks like a number
    private static bool IsOption(string arg) => arg.Length > 1 && arg[0] == '-' && !(char.IsAsciiDigit(arg[1]) || (arg[1] == '.' && arg.Length > 2 && char.IsAsciiDigit(arg[2])));

    /// <summary>An option and its "=value", with argparse's unambiguous prefixes ("--dry" for "--dry-run"); null for
    /// one this parser doesn't know (argparse collects those and reports them at the end).</summary>
    private static (string Name, string? Value)? Option(string arg, string[] known, string usage, string prog)
    {
        if (arg == "-h")
            return ("--help", null);
        var eq = arg.IndexOf('=');
        var name = eq >= 0 ? arg[..eq] : arg;
        var value = eq >= 0 ? arg[(eq + 1)..] : null;
        if (known.Contains(name))
            return (name, value);
        if (name.StartsWith("--", StringComparison.Ordinal))
        {
            var matches = known.Where(k => k.StartsWith(name, StringComparison.Ordinal)).ToList();
            if (matches.Count == 1)
                return (matches[0], value);
            if (matches.Count > 1)
                throw new UsageError($"ambiguous option: {name} could match {string.Join(", ", matches)}", usage, prog);
        }
        return null;
    }
}
