using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PyCompat;

namespace EvCharging.Parity;

/// <summary>The goldens from tests/parity/oracle.py, checked against the port. Each check returns its differences (a
/// line each, naming the case, never the data) and how many cases it compared. Used by the unit tests and by the AOT
/// parity runner.</summary>
public static class Checks
{
    /// <summary>The home folder the goldens were recorded under (GJ's laptop), and the one here: paths are compared
    /// with both replaced by &lt;HOME&gt;.</summary>
    public const string GoldenHome = @"C:\Users\gareth.jones";

    /// <summary>Put both sides in a difference (only for synthetic data, by hand).</summary>
    public static bool Detail { get; set; } = Environment.GetEnvironmentVariable("EV_PARITY_DETAIL") == "1";

    /// <summary>How many invalid-pattern messages differed only in the wording after "isn't a valid regular
    /// expression:" (a deliberate difference).</summary>
    public static int RegexWording { get; private set; }

    public static string Home => PyPath.Home(Settings.Environment);

    private static string Mask(string text, string? folder = null)
    {
        if (folder is not null)
            text = text.Replace(folder, "<DIR>", StringComparison.Ordinal);
        return text.Replace(GoldenHome, "<HOME>", StringComparison.Ordinal).Replace(Home, "<HOME>", StringComparison.Ordinal);
    }

    private static JsonNode? MaskNode(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, MaskNode(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(MaskNode).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var text) => JsonValue.Create(Mask(text)),
        _ => node?.DeepClone(),
    };

    public static (int Compared, List<string> Differences) Scenarios(JsonArray scenarios)
    {
        var differences = new List<string>();
        foreach (var node in scenarios)
        {
            var scenario = (JsonObject)node!;
            var expected = (JsonObject)scenario["expected"]!;
            var actual = ScenarioRunner.Run(scenario);
            foreach (var (key, value) in expected)
                if (ScenarioRunner.Canonical(value) != ScenarioRunner.Canonical(actual[key]))
                    differences.Add($"{scenario["name"]}: {key}");
        }
        return (scenarios.Count, differences);
    }

    public static (int Compared, List<string> Differences) Misc(JsonObject golden)
    {
        var differences = new List<string>();
        var compared = 0;
        void Same(string what, JsonNode? expected, JsonNode? actual)
        {
            compared++;
            if (ScenarioRunner.Canonical(expected) != ScenarioRunner.Canonical(actual))
                differences.Add(Detail ? $"{what}: expected {ScenarioRunner.Canonical(expected)} got {ScenarioRunner.Canonical(actual)}" : what);
        }

        var folder = Path.Combine(Path.GetTempPath(), "ev-misc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "x.toml");
            var n = 0;
            foreach (var item in golden["settings"]!.AsArray())
            {
                var toml = (string)item!["toml"]!;
                JsonObject actual;
                try
                {
                    actual = new JsonObject { ["toml"] = toml, ["ok"] = Summary(Settings.Parse(toml, path), folder) };
                }
                catch (PyValueError error)
                {
                    actual = new JsonObject { ["toml"] = toml, ["error"] = Mask(error.Message, folder) };
                }
                var expected = (JsonObject)item.DeepClone();
                if (expected["ok"] is JsonObject ok)
                {
                    // The oracle's own masking missed its temporary folder once (a short versus a long spelling).
                    expected["ok"] = MaskNode(ok);
                    foreach (var key in new[] { "state_file", "log_file", "car_file" })
                        if (expected["ok"]![key] is JsonValue v && v.TryGetValue<string>(out var text))
                            expected["ok"]![key] = System.Text.RegularExpressions.Regex.Replace(text, @"^<HOME>\\AppData\\Local\\Temp\\tmp\w+", "<DIR>");
                }
                // Deliberate: an invalid regular expression is refused with PyRegex's wording, not sre's.
                const string invalid = " isn't a valid regular expression: ";
                if (expected["error"] is JsonValue e && e.TryGetValue<string>(out var said) && said.Contains(invalid, StringComparison.Ordinal)
                    && actual["error"] is JsonValue a && a.TryGetValue<string>(out var ours) && ours.Contains(invalid, StringComparison.Ordinal)
                    && said[..said.IndexOf(invalid, StringComparison.Ordinal)] == ours[..ours.IndexOf(invalid, StringComparison.Ordinal)])
                {
                    RegexWording++;
                    expected["error"] = ours;
                }
                Same($"settings {n++}", expected, actual);
            }

            var settings = Settings.Parse(Settings.Template, path);
            n = 0;
            foreach (var item in golden["templates"]!.AsArray())
            {
                settings.Form.Fields = item!["fields"]!.AsArray()
                    .Select(f => new FormField((int)f![0]!, (string)f[1]!, (string)f[2]!)).ToList();
                var actual = (JsonObject)item.DeepClone();
                actual.Remove("ok");
                actual.Remove("error");
                try
                {
                    var values = Form.Values(settings, Planner.FromIsoDate((string)item["day"]!), (string)item["start"]!, (JsonObject)item["details"]!.DeepClone());
                    actual["ok"] = new JsonArray(values.Select(v => (JsonNode)new JsonArray(v.Question, v.Type, v.Text)).ToArray());
                }
                catch (FormError error)
                {
                    actual["error"] = error.Message;
                }
                Same($"template {n++}", item, actual);
            }

            n = 0;
            foreach (var item in golden["start_time"]!.AsArray())
            {
                settings.StartTime = (string)item!["setting"]!;
                Same($"start_time {n++}", item["start"], Form.StartTime(settings, (JsonObject)item["details"]!.DeepClone()));
            }
            settings.StartTime = "08:00";

            n = 0;
            foreach (var item in golden["renames"]!.AsArray())
            {
                settings.Approvals.Rename = (string)item!["template"]!;
                settings.SlotHours = double.Parse((string)item["hours"]!, CultureInfo.InvariantCulture);
                var actual = (JsonObject)item.DeepClone();
                actual.Remove("ok");
                actual.Remove("error");
                try
                {
                    actual["ok"] = Planner.Renamed(settings, new Reply(new DateOnly(2026, 11, 24), (string)item["start"]!, "Charger 1 (Rear)", "approved", ""));
                }
                catch (Exception error)
                {
                    actual["error"] = $"{Runner.PythonType(error)}: {error.Message}";
                }
                Same($"rename {n++}", item, actual);
            }
            settings.SlotHours = 4;

            n = 0;
            foreach (var item in golden["body_text"]!.AsArray())
                Same($"body_text {n++}", item!["out"], Planner.BodyText(new JsonObject { ["body"] = new JsonObject { ["content"] = (string)item["in"]! } }));

            n = 0;
            foreach (var item in golden["labels"]!.AsArray())
            {
                var day = Planner.FromIsoDate((string)item!["day"]!);
                Same($"label {n++}", item, new JsonObject
                {
                    ["day"] = PyTime.Iso(day),
                    ["label"] = Planner.Label(day),
                    ["next"] = PyTime.Iso(Planner.NextWorkingDay(day)),
                    ["status"] = PyTime.Strftime(day, "%a %d %b"),
                    ["submitted"] = PyTime.Strftime(day, "%a %d %b %Y"),
                });
            }

            string[] senders = ["facilities@example.com", "o'brien@example.com", "a+b@example.com", "space y@example.com", "ünï@example.com", "x&y=z#%/?@example.com"];
            string?[] locations = ["EV Charging 1 (Rear CP)", null, ""];
            n = 0;
            foreach (var item in golden["urls"]!.AsArray())
            {
                var zone = (string)item!["timezone"]!;
                var start = Planner.FromIsoDate((string)item["start"]!);
                var end = Planner.FromIsoDate((string)item["end"]!);
                var calls = item["calls"]!.AsArray();
                var headers = new JsonObject(Outlook.HeadersFor(zone).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
                JsonObject Call(string url, string method, JsonNode? body) => new() { ["url"] = url, ["method"] = method, ["body"] = body, ["headers"] = headers.DeepClone() };
                Same($"urls {n} work_plan", calls[0], Call(Outlook.WorkPlanUrl(start, end), "GET", null));
                Same($"urls {n} events", calls[1], Call(Outlook.EventsUrl(start, end), "GET", null));
                var sender = senders.FirstOrDefault(s => (string)calls[2]!["url"]! == Outlook.MessagesUrl(s, start));
                Same($"urls {n} messages", calls[2], Call(Outlook.MessagesUrl(sender ?? "(none matched)", start), "GET", null));
                var id = $"AAMk{n}=/+";
                var subject = "Travel – EV Charging 1 (Rear CP), 08:00–12:00";
                var location = locations.FirstOrDefault(l => ScenarioRunner.Canonical(calls[3]!["body"]) == ScenarioRunner.Canonical(Outlook.RenameBody(subject, l)), "(none matched)");
                Same($"urls {n} rename", calls[3], Call(Outlook.EventUrl(id), "PATCH", Outlook.RenameBody(subject, location)));
                n++;
            }

            n = 0;
            foreach (var item in golden["hooks"]!.AsArray())
            {
                var report = new Report();
                report.Questions.AddRange(item!["report"]!["questions"]!.AsArray().Select(q => (string)q!));
                report.Booked.AddRange(item["report"]!["booked"]!.AsArray().Select(q => (string)q!));
                report.Failed.AddRange(item["report"]!["failed"]!.AsArray().Select(q => (string)q!));
                Same($"hook {n}", item["payload"], Runner.HookPayload(@"C:\Users\someone\settings\ev é.toml", report));
                Same($"hook {n++} summary", item["summary"], Runner.Summary(report));
            }

            n = 0;
            foreach (var item in golden["status"]!.AsArray())
            {
                var statePath = Path.Combine(folder, "status-state.json");
                State.Save(statePath, (JsonObject)item!["state"]!.DeepClone());
                settings.StateFile = statePath;
                var output = new StringWriter();
                var code = global::EvCharging.Cli.Status(settings, new CliHost { Out = output, Now = () => new DateTime(2026, 11, 12, 9, 0, 0) });
                Same($"status {n}", item["out"], output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
                Same($"status {n} code", item["code"], code);
                Same($"status {n++} bytes", item["bytes"], Encoding.UTF8.GetString(File.ReadAllBytes(statePath)));
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
        return (compared, differences);
    }

    /// <summary>The oracle's config_summary().</summary>
    private static JsonObject Summary(Settings c, string folder)
    {
        string P(string? p) => Mask(p ?? "None", folder);
        var a = c.Approvals;
        return new JsonObject
        {
            ["path"] = P(c.Path), ["login_hint"] = c.LoginHint, ["tenant"] = c.Tenant, ["timezone"] = c.Timezone,
            ["state_file"] = P(c.StateFile), ["log_file"] = P(c.LogFile), ["car_file"] = P(c.CarFile),
            ["after_run"] = Report.Strings(c.AfterRun), ["daily_at"] = c.DailyAt, ["days_ahead"] = c.DaysAhead,
            ["start_time"] = c.StartTime, ["slot_hours"] = Py.Repr(c.SlotHours), ["notice_days"] = c.NoticeDays,
            ["weekdays_only"] = c.WeekdaysOnly, ["entry"] = c.Entry, ["book"] = c.Book, ["label"] = c.Label,
            ["work_plan"] = c.WorkPlan, ["form_url"] = c.Form.Url, ["questions"] = c.Form.Questions,
            ["confirmation"] = c.Form.Confirmation,
            ["fields"] = new JsonArray(c.Form.Fields.Select(f => (JsonNode)new JsonArray(f.Question, f.Type, f.Value)).ToArray()),
            ["approvals"] = new JsonArray(a.Enabled, a.Sender, a.Subject, a.Approved, a.Declined, a.Date, a.Start, a.Point,
                Report.Strings(a.ChargePoints), a.LookBackDays, a.Rename, a.MorningBefore, a.SetLocation, a.ChaseNextWorkingDay),
        };
    }

    /// <summary>The CLI goldens: argparse's texts and exit codes. Settings come from a folder with no settings file in
    /// it (as on the laptop the goldens were recorded on).</summary>
    public static (int Compared, List<string> Differences) Cli(JsonObject golden)
    {
        var differences = new List<string>();
        var compared = 0;
        var appData = Path.Combine(Path.GetTempPath(), "ev-cli-" + Guid.NewGuid().ToString("N"));
        string? Env(string name) => name == "APPDATA" ? appData : name == Settings.EnvConfig ? null : Settings.Environment(name);
        foreach (var item in golden["cli"]!.AsArray())
        {
            var argv = item!["argv"]!.AsArray().Select(a => (string)a!).ToArray();
            var output = new StringWriter();
            var error = new StringWriter();
            var code = global::EvCharging.Cli.Run(argv, new CliHost { Out = output, Err = error, Env = Env });
            string Norm(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
            var expectedErr = ((string)item["err"]!).Replace(GoldenHome + @"\AppData\Roaming", appData, StringComparison.Ordinal);
            // --version is the build's (the release's tag), not the Python oracle's.
            var expectedOut = argv is ["--version"]
                ? (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                    typeof(global::EvCharging.Cli).Assembly)?.InformationalVersion ?? "") + "\n"
                : (string)item["out"]!;
            compared++;
            if (Norm(output.ToString()) != expectedOut || Norm(error.ToString()) != expectedErr || code != (int)item["code"]!)
                differences.Add("cli " + string.Join(' ', argv));
        }
        compared++;
        if (Settings.Template.ReplaceLineEndings("\n") != (string)golden["template"]!)
            differences.Add("template");
        return (compared, differences);
    }
}
