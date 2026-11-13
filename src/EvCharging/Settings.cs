using System.Globalization;
using System.Text;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;

namespace EvCharging;

/// <summary>Python's ValueError: settings that can't be used, a bad date. The CLI prints it and exits 2.</summary>
public class PyValueError(string message) : Exception(message);

/// <summary>Python's KeyError: str() of it is the key's repr, as Python's is.</summary>
public sealed class PyKeyError(string key) : Exception(Py.ReprString(key))
{
    public string Key { get; } = key;
}

/// <summary>One [[form.field]]: a question by its position on the form (1 is the first), its type (date, choice or
/// text) and its value, a str.format template ({date:...}, {start}, or any key in the car file).</summary>
public sealed record FormField(int Question, string Type, string Value);

public sealed class FormSettings
{
    public const string DefaultConfirmation = "response was submitted|Your response was|Thanks";
    public string Url { get; set; } = "";
    public int Questions { get; set; }   // 0: don't check
    public string Confirmation { get; set; } = DefaultConfirmation;
    public List<FormField> Fields { get; set; } = [];
}

public sealed class ApprovalSettings
{
    public bool Enabled { get; set; }
    public string Sender { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Approved { get; set; } = "APPROVED";
    public string Declined { get; set; } = "DECLIN|REJECT";
    public string Date { get; set; } = @"Date\s*-\s*(\d{4}-\d{2}-\d{2})";
    public string Start { get; set; } = @"Start Time\s*-\s*(\d{1,2}:\d{2})";
    public string Point { get; set; } = @"assigned;\s*(.+?\))";
    public List<string> ChargePoints { get; set; } = [];
    public int LookBackDays { get; set; } = 60;
    public string Rename { get; set; } = "Travel; EV – {point}, {start}–{end}";
    public int MorningBefore { get; set; } = 12;   // the entry to rename starts before this hour
    public bool SetLocation { get; set; } = true;
    public bool ChaseNextWorkingDay { get; set; } = true;
}

/// <summary>
/// The settings file: where the form is, what in your calendar asks for a slot, and how approvals read. The same
/// file the Python version read, read the same way: paths come out as the same text, so the state, log, lock and
/// hook are shared. Everything specific to an organisation or a person lives here, never in the code.
/// <para>Paths may use ~ and %VARIABLES%; relative paths are relative to the settings file.</para>
/// </summary>
public sealed class Settings
{
    public const string EnvConfig = "EV_CHARGING_CONFIG";

    /// <summary>What `ev-charging config` writes: the Python version's TEMPLATE, character for character.</summary>
    public const string Template = """
        # ev-charging settings. See the README for what each one does.

        login_hint = "you@example.com"         # your work sign-in, for Microsoft Graph
        # tenant = "organizations"             # or your organisation's tenant id
        # timezone = "GMT Standard Time"       # Windows name of your time zone, for calendar times
        # car_file = "%LOCALAPPDATA%/ev-charging/car.json"   # your car's details: keep it out of any repo
        # daily_at = "09:00"                   # when autostart's daily run happens (and at logon)

        [booking]
        days_ahead = 14          # how far ahead to look
        start_time = "08:00"     # the slot you ask for
        slot_hours = 4           # how long a slot lasts
        notice_days = 1          # the first day considered is today + this
        weekdays_only = true

        [trigger]
        # A calendar entry whose subject matches `entry` is the day's travel block; it asks for a slot when its
        # subject also matches `book`. Both are case-insensitive regular expressions.
        entry = '^\s*travel'
        book = '\bev\b'
        label = "Travel block"   # what to call it in questions
        work_plan = "office"     # the Outlook work plan must say this to book; "" to ignore the work plan

        [form]
        url = "https://forms.office.com/Pages/ResponsePage.aspx?id=YOUR-FORM-ID"
        questions = 6            # how many questions the form has: anything else stops the booking
        confirmation = "response was submitted|Your response was|Thanks"

        # One [[form.field]] per question to fill, by its position on the form (1 is the first).
        # type: date, choice (a dropdown) or text. value: {date:...}, {start}, or any key in car_file.
        [[form.field]]
        question = 1
        type = "date"
        value = "{date:%d/%m/%Y}"

        [[form.field]]
        question = 2
        type = "choice"
        value = "{start}"

        [[form.field]]
        question = 3
        type = "text"
        value = "{car_make}"

        [approvals]
        sender = "facilities@example.com"
        subject = "electric car charging request"
        approved = "APPROVED"
        declined = "DECLIN|REJECT"
        date = 'Date\s*-\s*(\d{4}-\d{2}-\d{2})'
        start = 'Start Time\s*-\s*(\d{1,2}:\d{2})'
        point = 'assigned;\s*(.+?\))'
        # charge_points = ["Charger 1"]   # optional: names to look for if `point` finds nothing
        rename = "Travel; EV – {point}, {start}–{end}"

        """;

    public required string Path { get; set; }
    public string? LoginHint { get; set; }
    public string Tenant { get; set; } = "organizations";
    public string? MsalCache { get; set; }
    public string Timezone { get; set; } = "GMT Standard Time";
    public required string StateFile { get; set; }
    public required string LogFile { get; set; }
    public required string CarFile { get; set; }
    public required string EdgeProfile { get; set; }
    public required string Screenshots { get; set; }
    public List<string> AfterRun { get; set; } = [];
    public string DailyAt { get; set; } = "09:00";
    public int DaysAhead { get; set; } = 14;
    public string StartTime { get; set; } = "";
    public double SlotHours { get; set; } = 4;
    public int NoticeDays { get; set; } = 1;
    public bool WeekdaysOnly { get; set; } = true;
    public string Entry { get; set; } = @"^\s*travel";
    public string Book { get; set; } = @"\bev\b";
    public string Label { get; set; } = "Travel block";
    public string WorkPlan { get; set; } = "office";
    public FormSettings Form { get; set; } = new();
    public ApprovalSettings Approvals { get; set; } = new();

    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    /// <summary>%APPDATA%\ev-charging\config.toml (or under the home folder if APPDATA isn't set).</summary>
    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "ev-charging", "config.toml"));
    }

    /// <summary>%LOCALAPPDATA%\ev-charging: the default state, log, car file, Edge profile and screenshots, and the
    /// run lock's folder in Python (used off Windows only).</summary>
    public static string LocalFolder(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "ev-charging"));
    }

    /// <summary>_path(): ~ and %VARIABLES% expanded; an absolute path as it is (not resolved), a relative one
    /// resolved against the settings file's folder.</summary>
    internal static string PathSetting(string value, string folder, Func<string, string?> env)
    {
        var expanded = PyPath.Pure(PyPath.ExpandVars(PyPath.ExpandUser(value, env), env));
        return PyPath.IsAbsolute(expanded) ? expanded : PyPath.Resolve(System.IO.Path.Combine(folder, expanded));
    }

    /// <summary>load(path): the given path, else EV_CHARGING_CONFIG, else the default. FileNotFoundException if
    /// there's no file there, PyValueError for settings that can't be used.</summary>
    public static Settings Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new FileNotFoundException($"No settings file at {full}. Run `ev-charging config` to create one.");
        return Parse(PyStr.ReadText(full), full, env);
    }

    /// <summary>tomllib.loads, then parse(). A TOML error is a PyValueError (tomllib's is a ValueError).</summary>
    public static Settings Parse(string text, string path, Func<string, string?>? env = null)
    {
        TomlTable raw;
        try
        {
            if (text.StartsWith('﻿'))
                throw new PyValueError("Invalid statement (at line 1, column 1)");   // tomllib refuses a byte order mark
            raw = TomlSerializer.Deserialize(text, TomlContext.Default.TomlTable) ?? [];
        }
        catch (Exception error) when (error is not PyValueError)
        {
            throw new PyValueError($"{path}: {error.Message}");
        }
        return Parse(raw, path, env);
    }

    /// <summary>parse(raw, path): every check and message as the Python's.</summary>
    public static Settings Parse(TomlTable raw, string path, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var folder = System.IO.Path.GetDirectoryName(path)!;
        var local = LocalFolder(env);
        string? Path(string key) => Truthy(raw, key) ? PathSetting(Str(raw[key], key), folder, env) : null;
        string Local(string name) => PyPath.Pure(System.IO.Path.Combine(local, name));

        var settings = new Settings
        {
            Path = path,
            LoginHint = raw.TryGetValue("login_hint", out var hint) ? Str(hint, "login_hint") : null,
            Tenant = raw.TryGetValue("tenant", out var tenant) ? Str(tenant, "tenant") : "organizations",
            Timezone = raw.TryGetValue("timezone", out var zone) ? Str(zone, "timezone") : "GMT Standard Time",
            AfterRun = raw.TryGetValue("after_run", out var hook) ? Strings(hook, "after_run") : [],
            DailyAt = Time(raw.TryGetValue("daily_at", out var daily) ? Str(daily, "daily_at") : "09:00", "daily_at"),
            MsalCache = Path("msal_cache"),
            StateFile = Path("state_file") ?? Local("state.json"),
            LogFile = Path("log_file") ?? Local("ev-charging.log"),
            CarFile = Path("car_file") ?? Local("car.json"),
            EdgeProfile = Path("edge_profile") ?? Local("edge-profile"),
            Screenshots = Path("screenshots") ?? Local("screenshots"),
        };

        var booking = Table(raw, "booking");
        var trigger = Table(raw, "trigger");
        settings.DaysAhead = booking.TryGetValue("days_ahead", out var days) ? Int(days) : 14;
        settings.StartTime = Truthy(booking, "start_time") ? Time(Str(booking["start_time"], "start_time"), "[booking] start_time") : "";
        settings.SlotHours = booking.TryGetValue("slot_hours", out var hours) ? Float(hours) : 4;
        settings.NoticeDays = booking.TryGetValue("notice_days", out var notice) ? Int(notice) : 1;
        settings.WeekdaysOnly = !booking.ContainsKey("weekdays_only") || Truthy(booking, "weekdays_only");
        if (settings.DaysAhead < 1 || settings.NoticeDays < 0)
            throw new PyValueError($"[booking] in {path}: days_ahead must be at least 1 and notice_days at least 0.");
        settings.Entry = Regex(trigger.TryGetValue("entry", out var entry) ? Str(entry, "entry") : @"^\s*travel", "[trigger] entry");
        settings.Book = Regex(trigger.TryGetValue("book", out var book) ? Str(book, "book") : @"\bev\b", "[trigger] book");
        settings.Label = trigger.TryGetValue("label", out var label) ? Str(label, "label") : "Travel block";
        settings.WorkPlan = PyStr.Strip(trigger.TryGetValue("work_plan", out var plan) ? Str(plan, "work_plan") : "office");

        var form = Table(raw, "form");
        settings.Form = new FormSettings
        {
            Url = form.TryGetValue("url", out var url) ? Str(url, "url") : "",
            Questions = form.TryGetValue("questions", out var questions) ? Int(questions) : 0,
            Confirmation = form.TryGetValue("confirmation", out var confirmation) ? Str(confirmation, "confirmation") : FormSettings.DefaultConfirmation,
        };
        // The confirmation is a JavaScript pattern in the page, as it was through Playwright's text=/…/i selector,
        // but the Python checked it with re.compile, so it's checked the same way here.
        Regex(settings.Form.Confirmation, "[form] confirmation");
        foreach (var item in Tables(form, "field", path))
        {
            var kind = item.TryGetValue("type", out var type) ? Str(type, "type") : "text";
            if (kind is not ("date" or "choice" or "text") || !Truthy(item, "question") || !item.ContainsKey("value"))
                throw new PyValueError($"A [[form.field]] in {path} needs question (a number), type (date, choice or text) "
                                       + $"and value: {Repr(item)}");
            var field = new FormField(Int(item["question"]), kind, Str(item["value"], "value"));
            PyFormat.Check(field.Value, $"Form question {field.Question}'s value");
            settings.Form.Fields.Add(field);
        }
        if (settings.Form.Questions != 0 && settings.Form.Fields.Any(f => f.Question > settings.Form.Questions))
            throw new PyValueError($"A [[form.field]] in {path} names a question beyond [form] questions = {settings.Form.Questions}.");

        if (raw.TryGetValue("approvals", out var approvalsValue) && approvalsValue is TomlTable { Count: > 0 } approvals)
        {
            if (!Truthy(approvals, "sender") || !Truthy(approvals, "subject"))
                throw new PyValueError($"[approvals] in {path} needs sender and subject.");
            var a = new ApprovalSettings { Enabled = true };
            // Python took these values as they were and failed mid-run on a wrong type; here they're checked now.
            foreach (var (key, value) in approvals)
            {
                switch (key)
                {
                    case "sender": a.Sender = Text(value, key); break;
                    case "subject": a.Subject = Text(value, key); break;
                    case "approved": a.Approved = Text(value, key); break;
                    case "declined": a.Declined = Text(value, key); break;
                    case "date": a.Date = Text(value, key); break;
                    case "start": a.Start = Text(value, key); break;
                    case "point": a.Point = Text(value, key); break;
                    case "charge_points": a.ChargePoints = Strings(value, key); break;
                    case "look_back_days": a.LookBackDays = Whole(value, key); break;
                    case "rename": a.Rename = Text(value, key); break;
                    case "morning_before": a.MorningBefore = Whole(value, key); break;
                    case "set_location": a.SetLocation = Truthy(approvals, key); break;
                    case "chase_next_working_day": a.ChaseNextWorkingDay = Truthy(approvals, key); break;
                }
            }
            foreach (var (key, pattern) in new[] { ("approved", a.Approved), ("declined", a.Declined), ("date", a.Date), ("start", a.Start), ("point", a.Point) })
                Regex(pattern, $"[approvals] {key}");
            PyFormat.Check(a.Rename, "[approvals] rename");
            settings.Approvals = a;
        }
        return settings;
    }

    /// <summary>create_default(): the default settings file, from the template if it isn't there yet.</summary>
    public static string CreateDefault(Func<string, string?>? env = null)
    {
        var path = DefaultPath(env);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            PyStr.WriteText(path, Template.ReplaceLineEndings("\n"));
        }
        return path;
    }

    // ---- Python's conversions -----------------------------------------------------------------------------------

    /// <summary>_time(): "HH:MM" from 00:00 to 23:59 (Python's \d, so any script's digits, as re's does).</summary>
    private static string Time(string value, string where)
    {
        if (!PyRegex.Get(@"([01]\d|2[0-3]):[0-5]\d").FullMatch(value))
            throw new PyValueError($"{where} should be a time such as \"08:00\", not {Py.ReprString(value)}.");
        return value;
    }

    /// <summary>_regex(): re.compile(value, re.I); also refused if Python and .NET would read it differently.</summary>
    private static string Regex(string value, string where)
    {
        string? problem;
        try
        {
            problem = PyRegex.Problem(value);
        }
        catch (Exception error) when (error is ArgumentException or PyRegexUnsupportedException)
        {
            problem = error.Message;
        }
        if (problem is not null)
            throw new PyValueError($"{where} isn't a valid regular expression: {problem}");
        return value;
    }

    private static TomlTable Table(TomlTable raw, string key) =>
        raw.TryGetValue(key, out var value) && value is TomlTable table ? table : [];

    private static IEnumerable<TomlTable> Tables(TomlTable raw, string key, string path)
    {
        if (!raw.TryGetValue(key, out var value))
            return [];
        return value switch
        {
            TomlTableArray tables => tables,
            TomlArray { Count: 0 } => [],
            _ => throw new PyValueError($"{path}: form.{key} must be a list of [[form.{key}]] tables."),
        };
    }

    /// <summary>bool(value) for a setting that's there.</summary>
    internal static bool Truthy(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value switch
        {
            null => false,
            string s => s.Length > 0,
            bool b => b,
            long l => l != 0,
            double d => d != 0,
            TomlArray a => a.Count > 0,
            TomlTableArray a => a.Count > 0,
            TomlTable t => t.Count > 0,
            _ => true,
        };

    /// <summary>str(value), for the settings that are text.</summary>
    private static string Str(object? value, string key) => value switch
    {
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => Py.Repr(d),
        bool b => b ? "True" : "False",
        _ => throw new PyValueError($"{key} must be text."),
    };

    /// <summary>A text setting that has to be text (the [approvals] values Python used as they were).</summary>
    private static string Text(object? value, string key) =>
        value as string ?? throw new PyValueError($"[approvals] {key} must be text.");

    private static int Whole(object? value, string key) =>
        value is long l and >= int.MinValue and <= int.MaxValue ? (int)l : throw new PyValueError($"[approvals] {key} must be a whole number.");

    /// <summary>int(value): whole numbers, decimals truncated, booleans, or digits in text.</summary>
    private static int Int(object? value) => value switch
    {
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        double d when double.IsFinite(d) && Math.Abs(d) < int.MaxValue => (int)Math.Truncate(d),
        bool b => b ? 1 : 0,
        string s => IntText(s),
        _ => throw new PyValueError($"int() argument must be a string, a bytes-like object or a real number, not {TypeName(value)}"),
    };

    private static int IntText(string s)
    {
        try
        {
            return checked((int)PyStr.Int(s));
        }
        catch (Exception error) when (error is FormatException or OverflowException)
        {
            throw new PyValueError($"invalid literal for int() with base 10: {Py.ReprString(s)}");
        }
    }

    /// <summary>float(value).</summary>
    private static double Float(object? value) => value switch
    {
        long l => l,
        double d => d,
        bool b => b ? 1 : 0,
        string s when double.TryParse(PyStr.Strip(s).Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        string s => throw new PyValueError($"could not convert string to float: {Py.ReprString(s)}"),
        _ => throw new PyValueError($"float() argument must be a string or a real number, not {TypeName(value)}"),
    };

    private static string TypeName(object? value) => value switch
    {
        TomlArray => "'list'",
        TomlTable => "'dict'",
        _ => "'" + (value?.GetType().Name ?? "NoneType") + "'",
    };

    private static List<string> Strings(object? value, string key) => value switch
    {
        TomlArray array => array.Select(item => Str(item, key)).ToList(),
        _ => throw new PyValueError($"{key} must be a list, as [\"python\", \"hook.py\"]."),
    };

    /// <summary>repr() of a TOML value, as Python prints the dict tomllib gave.</summary>
    internal static string Repr(object? value)
    {
        switch (value)
        {
            case null: return "None";
            case string s: return Py.ReprString(s);
            case bool b: return b ? "True" : "False";
            case long l: return l.ToString(CultureInfo.InvariantCulture);
            case double d: return Py.Repr(d);
            case TomlTable table:
            {
                var b = new StringBuilder("{");
                var first = true;
                foreach (var (key, item) in table)
                {
                    b.Append(first ? "" : ", ").Append(Py.ReprString(key)).Append(": ").Append(Repr(item));
                    first = false;
                }
                return b.Append('}').ToString();
            }
            case TomlArray array: return "[" + string.Join(", ", array.Select(Repr)) + "]";
            case TomlTableArray tables: return "[" + string.Join(", ", tables.Select(t => Repr(t))) + "]";
            default: return value.ToString() ?? "";
        }
    }
}

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class TomlContext : Tomlyn.Serialization.TomlSerializerContext;
