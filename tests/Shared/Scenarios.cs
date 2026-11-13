using System.Text;
using System.Text.Json.Nodes;
using PyCompat;

namespace EvCharging.Parity;

/// <summary>What the oracle's fakes raise: its str() is the message, as an OSError's is.</summary>
public sealed class Failure(string message) : Exception(message);

/// <summary>The oracle's FakeOutlook: the tests' fake, with failures to order and every call recorded.</summary>
public sealed class FakeOutlook : IOutlook
{
    private readonly List<JsonObject> plan, calendar, messages;
    private readonly JsonObject fail;
    private int eventCalls;

    public FakeOutlook(JsonObject scenario)
    {
        plan = Objects(scenario["plan"]);
        calendar = Objects(scenario["events"]);
        messages = Objects(scenario["messages"]);
        fail = scenario["fail"] as JsonObject ?? [];
    }

    public FakeOutlook(IEnumerable<JsonObject>? plan = null, IEnumerable<JsonObject>? events = null, IEnumerable<JsonObject>? messages = null)
    {
        this.plan = plan?.ToList() ?? [];
        calendar = events?.ToList() ?? [];
        this.messages = messages?.ToList() ?? [];
        fail = [];
    }

    public JsonArray Calls { get; } = [];
    public JsonArray Renamed { get; } = [];

    /// <summary>Raised by events() (the tests' Broken outlook).</summary>
    public string? EventsFail { get; init; }

    private static List<JsonObject> Objects(JsonNode? node) =>
        node is JsonArray a ? a.Select(i => (JsonObject)i!.DeepClone()).ToList() : [];

    private static string Day(JsonObject item) => PyStr.Head((string)item["start"]!["dateTime"]!, 10);

    private static bool Between(JsonObject item, DateOnly start, DateOnly end) =>
        PyStr.CompareCodePoints(PyTime.Iso(start), Day(item)) <= 0 && PyStr.CompareCodePoints(Day(item), PyTime.Iso(end)) < 0;

    private static JsonArray Call(params string[] parts) => new(parts.Select(p => (JsonNode)JsonValue.Create(p)).ToArray());

    public List<JsonObject> WorkPlan(DateOnly start, DateOnly end)
    {
        Calls.Add((JsonNode)Call("work_plan", PyTime.Iso(start), PyTime.Iso(end)));
        if (fail.ContainsKey("work_plan"))
            throw new Failure((string)fail["work_plan"]!);
        return plan.Where(s => Between(s, start, end)).Select(s => (JsonObject)s.DeepClone()).ToList();
    }

    public List<JsonObject> Events(DateOnly start, DateOnly end)
    {
        Calls.Add((JsonNode)Call("events", PyTime.Iso(start), PyTime.Iso(end)));
        eventCalls++;
        if (EventsFail is not null)
            throw new Failure(EventsFail);
        if (fail.ContainsKey("events") || (fail.ContainsKey("events_later") && eventCalls > 1))
            throw new Failure((string)(fail["events"] ?? fail["events_later"])!);
        return calendar.Where(e => Between(e, start, end)).Select(e => (JsonObject)e.DeepClone()).ToList();
    }

    public List<JsonObject> MessagesFrom(string sender, DateOnly since)
    {
        Calls.Add((JsonNode)Call("messages_from", sender, PyTime.Iso(since)));
        if (fail.ContainsKey("messages"))
            throw new Failure((string)fail["messages"]!);
        return messages.Select(m => (JsonObject)m.DeepClone()).ToList();
    }

    public void Rename(string eventId, string subject, string? location)
    {
        Calls.Add((JsonNode)Call("rename", eventId));
        if (fail.ContainsKey("rename"))
            throw new Failure((string)fail["rename"]!);
        Renamed.Add((JsonNode)new JsonArray(eventId, subject, location));
    }
}

/// <summary>m365_graph's request served from a recording by URL, as the oracle's RecordedGraph: a URL not in it is an
/// error, so a URL that Python and .NET build differently shows up.</summary>
public sealed class RecordedGraph(JsonObject calls) : IGraph
{
    public List<string> Seen { get; } = [];

    public JsonObject Get(string url, IReadOnlyDictionary<string, string> headers)
    {
        Seen.Add(url);
        return calls[url] is JsonObject body ? (JsonObject)body.DeepClone() : throw new InvalidOperationException("replay: a URL the recording doesn't have");
    }

    public JsonObject Send(string method, string url, JsonObject? body, IReadOnlyDictionary<string, string> headers) =>
        method == "GET" ? Get(url, headers) : throw new InvalidOperationException($"replay: {method} isn't a read");

    public void Token()
    {
    }
}

/// <summary>The oracle's run_scenario: the same fakes, files and frozen clock, and the same outcome dict.</summary>
public static class ScenarioRunner
{
    public static Settings Configure(JsonObject scenario, string folder)
    {
        var path = Path.Combine(folder, "config.toml");
        var text = (string)scenario["settings"]!;
        File.WriteAllText(path, text, new UTF8Encoding(false));
        var settings = Settings.Parse(text, path);
        settings.StateFile = Path.Combine(folder, "state.json");
        settings.LogFile = Path.Combine(folder, "log.txt");
        settings.CarFile = Path.Combine(folder, "car.json");
        settings.AfterRun = [];
        if (scenario["state"] is JsonValue state)
            File.WriteAllBytes(settings.StateFile, Encoding.UTF8.GetBytes((string)state!));
        if (scenario["car"] is JsonValue car)
            File.WriteAllBytes(settings.CarFile, Encoding.UTF8.GetBytes((string)car!));
        return settings;
    }

    public static Booker BookerFor(JsonObject scenario, JsonArray calls)
    {
        var outcomes = scenario["booker"] as JsonObject ?? [];
        return (_, day) =>
        {
            calls.Add((JsonNode)JsonValue.Create(PyTime.Iso(day)));
            if (outcomes[PyTime.Iso(day)] is not JsonObject outcome)
                return $"Submitted: {PyTime.Strftime(day, "%a %d %b %Y")}, 08:00.";
            var text = (string)outcome["text"]!;
            if ((string)outcome["kind"]! == "raise")
                throw (string)outcome["type"]! == "FormError" ? new FormError(text) : new TimeoutException(text);
            return text;
        };
    }

    public static JsonObject Run(JsonObject scenario, Func<Settings, (IOutlook Outlook, JsonArray Calls, JsonArray Renamed)>? outlookFactory = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ev-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var settings = Configure(scenario, folder);
            IOutlook outlook;
            JsonArray calls, renamed;
            if (outlookFactory is null)
            {
                var fake = new FakeOutlook(scenario);
                (outlook, calls, renamed) = (fake, fake.Calls, fake.Renamed);
            }
            else
                (outlook, calls, renamed) = outlookFactory(settings);
            var lines = new List<string>();
            var booked = new JsonArray();
            var today = Planner.FromIsoDate((string)scenario["today"]!);
            var now = PyTime.FromIso((string)scenario["now"]!);
            JsonObject? report = null;
            JsonObject? error = null;
            try
            {
                if (scenario["approvals_call"] is JsonValue a && a.GetValue<bool>())
                    report = new JsonObject { ["questions"] = Report.Strings(Runner.ApplyApprovals(settings, outlook, today, (bool)scenario["dry_run"]!, lines.Add)) };
                else
                    report = Runner.Run(settings, outlook, today, (bool)scenario["dry_run"]!, (bool)scenario["approvals_only"]!, lines.Add,
                        BookerFor(scenario, booked), () => now).ToJson();
            }
            catch (Exception e)
            {
                report = null;
                error = new JsonObject { ["type"] = e is CalendarError ? "CalendarError" : Runner.PythonType(e), ["text"] = e.Message };
            }
            var saved = File.Exists(settings.StateFile) ? Encoding.UTF8.GetString(File.ReadAllBytes(settings.StateFile)) : null;
            string? summary = null;
            if (report is not null && report.ContainsKey("booked"))
                summary = Runner.Summary(report["booked"]!.AsArray().Count, report["failed"]!.AsArray().Count, report["questions"]!.AsArray().Count);
            return new JsonObject
            {
                ["report"] = report,
                ["error"] = error,
                ["log"] = Report.Strings(lines),
                ["renamed"] = renamed.DeepClone(),
                ["calls"] = calls.DeepClone(),
                ["booked_calls"] = booked,
                ["state"] = saved,
                ["summary"] = summary,
            };
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The oracle's replay(): a recorded live read through the real Outlook class, as a dry run and as a real
    /// run with renames journaled by a Journal write door (and a fake booker).</summary>
    public static JsonObject Replay(JsonObject recording)
    {
        var results = new JsonObject();
        foreach (var (name, dry) in new[] { ("dry", true), ("live", false) })
        {
            var scenario = new JsonObject
            {
                ["settings"] = recording["settings"]?.DeepClone(),
                ["today"] = recording["today"]?.DeepClone(),
                ["now"] = recording["now"]?.DeepClone(),
                ["state"] = recording["state"]?.DeepClone(),
                ["car"] = null,
                ["booker"] = new JsonObject(),
                ["dry_run"] = dry,
                ["approvals_only"] = false,
            };
            var graph = new RecordedGraph((JsonObject)recording["calls"]!);
            var result = Run(scenario, settings =>
            {
                var renamed = new JsonArray();
                var journal = new JournalOutlook(new Outlook(settings, graph, WriteDoor.Journaling()), renamed);
                return (journal, new JsonArray(), renamed);
            });
            result["urls"] = new JsonArray(graph.Seen.Select(u => (JsonNode)JsonValue.Create(u)).ToArray());
            results[name] = result;
        }
        return results;
    }

    /// <summary>The real Outlook, with each rename listed as [id, subject, location] once the Journal door has taken
    /// it (nothing is sent).</summary>
    private sealed class JournalOutlook(Outlook inner, JsonArray renamed) : IOutlook
    {
        public List<JsonObject> WorkPlan(DateOnly start, DateOnly end) => inner.WorkPlan(start, end);

        public List<JsonObject> Events(DateOnly start, DateOnly end) => inner.Events(start, end);

        public List<JsonObject> MessagesFrom(string sender, DateOnly since) => inner.MessagesFrom(sender, since);

        public void Rename(string eventId, string subject, string? location)
        {
            inner.Rename(eventId, subject, location);
            renamed.Add((JsonNode)new JsonArray(eventId, subject, location));
        }
    }

    /// <summary>json.dumps(x, sort_keys=True): the form both sides are compared in.</summary>
    public static string Canonical(JsonNode? node) => PyJson.Dump(node, sortKeys: true);
}
