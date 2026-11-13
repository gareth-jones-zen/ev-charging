using System.Text.Json.Nodes;
using EvCharging.Parity;
using PyCompat;

namespace EvCharging.Tests;

/// <summary>A temporary folder with the template's settings, a state file, a log and a car file in it: the Python
/// tests' Base.</summary>
public sealed class Folder : IDisposable
{
    public Folder(string extra = "")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ev-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        // Every file the tool writes is in the folder, whichever way the settings are loaded.
        var files = string.Concat(new[] { "state_file", "log_file", "car_file", "edge_profile", "screenshots" }
            .Select(key => $"{key} = '{System.IO.Path.Combine(Path, key)}'\n"));
        var text = files + EvCharging.Settings.Template.ReplaceLineEndings("\n") + extra;
        var path = System.IO.Path.Combine(Path, "config.toml");
        File.WriteAllText(path, text);
        Settings = EvCharging.Settings.Parse(text, path);
        Settings.StateFile = System.IO.Path.Combine(Path, "state.json");
        Settings.LogFile = System.IO.Path.Combine(Path, "log.txt");
        Settings.CarFile = System.IO.Path.Combine(Path, "car.json");
        File.WriteAllText(Settings.CarFile, """{"car_make": "Make", "car_model": "Model", "car_registration": "YOUR REG"}""");
    }

    public string Path { get; }
    public Settings Settings { get; }
    public List<string> Lines { get; } = [];
    public void Log(string text) => Lines.Add(text);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The 28 Python tests (tests/test_ev_charging.py), by name.</summary>
public sealed class PythonTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 12);   // a Thursday
    private readonly Folder folder = new();
    private Settings Config => folder.Settings;

    public void Dispose() => folder.Dispose();

    internal static JsonObject Event(string day, string subject, int hour = 7, string? id = null) => new()
    {
        ["id"] = id ?? $"{day}-{hour}", ["subject"] = subject, ["start"] = new JsonObject { ["dateTime"] = $"{day}T{hour:00}:30:00.0000000" },
    };

    internal static JsonObject Segment(string day, string kind) => new()
    {
        ["start"] = new JsonObject { ["dateTime"] = $"{day}T09:00:00.0000000" }, ["workLocationType"] = kind,
    };

    internal static JsonObject Reply(string day, string subject = "Your Electric Car Charging Request - APPROVED",
        string received = "2026-11-10T10:00:00Z", string point = "Charger 1 (Rear)", string start = "08:00") => new()
    {
        ["subject"] = subject, ["receivedDateTime"] = received,
        ["body"] = new JsonObject
        {
            ["content"] = $"<p>Date - {day}</p><p>Start Time - {start}</p><p>End Time - 11/10/2026 10:00</p><p>You have been assigned; {point}</p>",
        },
    };

    // ---- TriggerTests ---------------------------------------------------------------------------------------------

    [Fact]
    public void test_entry_and_ev_word()
    {
        Assert.True(Planner.IsEntry(Config, "Travel"));
        Assert.True(Planner.IsEntry(Config, "  travel; EV"));
        Assert.True(Planner.IsEntry(Config, "Travel; EV – Charger 1 (Rear), 08:00–12:00"));
        Assert.False(Planner.IsEntry(Config, "Time to travel"));
        foreach (var subject in new[] { "Travel; EV", "Travel; Book EV charger", "Travel ev", "Travel; EV – Charger 1 (Rear)" })
            Assert.True(Planner.Asks(Config, subject), subject);
        foreach (var subject in new[] { "Travel", "Travel; Bevan's leaving do", "Travel; EVENT", "Travel; Leeds" })
            Assert.False(Planner.Asks(Config, subject), subject);
    }

    [Fact]
    public void test_entry_days_any_entry_asks()
    {
        var days = Planner.EntryDays(Config, [Event("2026-11-16", "Travel"), Event("2026-11-16", "Travel; EV", 17), Event("2026-11-17", "Travel"), Event("2026-11-18", "Standup")]);
        Assert.Equal(new Dictionary<DateOnly, bool> { [new(2026, 11, 16)] = true, [new(2026, 11, 17)] = false }, days);
    }

    [Fact]
    public void test_custom_trigger()
    {
        var text = Settings.Template.ReplaceLineEndings("\n").Replace(@"entry = '^\s*travel'", "entry = 'office'").Replace(@"book = '\bev\b'", "book = 'charge'");
        Assert.NotEqual(0, text.Split("'office'").Length - 1);
        Assert.Contains("book = 'charge'", text);
        var custom = Settings.Parse(text, Path.Combine(folder.Path, "config.toml"));
        Assert.True(Planner.IsEntry(custom, "In the office"));
        Assert.True(Planner.Asks(custom, "Office, charge car"));
        Assert.False(Planner.Asks(custom, "Office; EV"));
    }

    // ---- DecisionTests --------------------------------------------------------------------------------------------

    private Dictionary<string, Decision> Decide(Dictionary<string, string[]> plan, Dictionary<string, bool> entries, string[]? booked = null)
    {
        var workPlan = plan.ToDictionary(kv => Planner.FromIsoDate(kv.Key), kv => kv.Value.ToHashSet());
        var entryDays = entries.ToDictionary(kv => Planner.FromIsoDate(kv.Key), kv => kv.Value);
        var state = new JsonObject();
        foreach (var b in booked ?? [])
            state[b] = new JsonObject { ["start"] = "08:00" };
        return Planner.Decide(Config, Today, workPlan, entryDays, state).ToDictionary(d => PyTime.Iso(d.Day));
    }

    [Fact]
    public void test_table()
    {
        var found = Decide(
            new() { ["2026-11-16"] = ["office"], ["2026-11-17"] = ["remote"], ["2026-11-18"] = ["office"], ["2026-11-19"] = ["office", "timeOff"],
                    ["2026-11-20"] = ["remote"], ["2026-11-23"] = ["office"], ["2026-11-24"] = ["office"] },
            new() { ["2026-11-16"] = true, ["2026-11-17"] = true, ["2026-11-18"] = false, ["2026-11-19"] = true, ["2026-11-24"] = true },
            ["2026-11-19", "2026-11-20"]);
        Assert.Equal("book", found["2026-11-16"].Action);
        Assert.Equal("ask", found["2026-11-17"].Action);
        Assert.Contains("work plan says remote", found["2026-11-17"].Text);
        Assert.Equal("log", found["2026-11-18"].Action);
        Assert.False(found.ContainsKey("2026-11-19"));
        Assert.Equal("log", found["2026-11-20"].Action);
        Assert.False(found.ContainsKey("2026-11-23"));
        Assert.Equal("book", found["2026-11-24"].Action);
    }

    [Fact]
    public void test_no_work_plan_set_says_nothing_set()
    {
        var found = Decide([], new() { ["2026-11-16"] = true });
        Assert.Contains("work plan says nothing set", found["2026-11-16"].Text);
    }

    [Fact]
    public void test_window_weekdays_and_notice()
    {
        var entries = Enumerable.Range(0, 20).ToDictionary(n => PyTime.Iso(Today.AddDays(n)), _ => true);
        var plan = entries.Keys.ToDictionary(d => d, _ => new[] { "office" });
        var days = Decide(plan, entries).Keys.Order(StringComparer.Ordinal).ToList();
        Assert.Equal("2026-11-13", days[0]);
        Assert.Equal("2026-11-26", days[^1]);
        Assert.DoesNotContain(days, d => PyTime.Weekday(Planner.FromIsoDate(d)) >= 5);
    }

    [Fact]
    public void test_work_plan_ignored()
    {
        Config.WorkPlan = "";
        var found = Decide([], new() { ["2026-11-16"] = true, ["2026-11-17"] = false });
        Assert.Equal("book", found["2026-11-16"].Action);
        Assert.Equal("log", found["2026-11-17"].Action);
    }

    [Fact]
    public void test_next_working_day()
    {
        Assert.Equal(new DateOnly(2026, 11, 16), Planner.NextWorkingDay(new DateOnly(2026, 11, 13)));
        Assert.Equal(new DateOnly(2026, 11, 13), Planner.NextWorkingDay(Today));
    }

    // ---- ApprovalTests --------------------------------------------------------------------------------------------

    [Fact]
    public void test_parse_ignores_month_first_end_time()
    {
        var parsed = Planner.ParseReply(Config, Reply("2026-11-24"))!;
        Assert.Equal(new DateOnly(2026, 11, 24), parsed.Date);
        Assert.Equal("08:00", parsed.Start);
        Assert.Equal("Charger 1 (Rear)", parsed.Point);
        Assert.Equal("approved", parsed.Outcome);
        Assert.Equal("Travel; EV – Charger 1 (Rear), 08:00–12:00", Planner.Renamed(Config, parsed));
    }

    [Fact]
    public void test_parse_declined_and_unparseable()
    {
        Assert.Equal("declined", Planner.ParseReply(Config, Reply("2026-11-24", subject: "Your Electric Car Charging Request - DECLINED"))!.Outcome);
        Assert.Null(Planner.ParseReply(Config, new JsonObject { ["subject"] = "x", ["body"] = new JsonObject { ["content"] = "nothing" } }));
    }

    [Fact]
    public void test_charge_point_fallback()
    {
        Config.Approvals.Point = "nomatch(x)";
        Config.Approvals.ChargePoints = ["Charger 1 (Rear)"];
        Assert.Equal("Charger 1 (Rear)", Planner.ParseReply(Config, Reply("2026-11-24"))!.Point);
    }

    [Fact]
    public void test_latest_reply_wins_and_past_ignored()
    {
        JsonObject[] messages =
        [
            Reply("2026-11-24", point: "Charger 1 (Rear)", received: "2026-11-10T10:00:00Z"),
            Reply("2026-11-24", point: "Charger 2 (Front)", received: "2026-11-11T10:00:00Z"),
            Reply("2026-11-05"),
            new() { ["subject"] = "Something else", ["receivedDateTime"] = "2026-11-11T00:00:00Z", ["body"] = new JsonObject { ["content"] = "Date - 2026-11-30 Start Time - 08:00" } },
        ];
        var latest = Planner.LatestReplies(Config, messages, Today);
        Assert.Equal([new DateOnly(2026, 11, 24)], latest.Keys);
        Assert.Equal("Charger 2 (Front)", latest[new DateOnly(2026, 11, 24)].Point);
    }

    [Fact]
    public void test_morning_entry_only()
    {
        JsonObject[] events = [Event("2026-11-24", "Travel", 17, "evening"), Event("2026-11-24", "Travel; EV", 7, "morning"), Event("2026-11-24", "Standup", 9, "standup")];
        Assert.Equal("morning", (string)Planner.MorningEntry(Config, events, new DateOnly(2026, 11, 24))!["id"]!);
        Assert.Null(Planner.MorningEntry(Config, events[..1], new DateOnly(2026, 11, 24)));
    }

    [Fact]
    public void test_rename_template_and_slot_length()
    {
        Config.Approvals.Rename = "Travel ({point}) {date:%d/%m} {start}-{end}";
        Config.SlotHours = 3.5;
        var parsed = Planner.ParseReply(Config, Reply("2026-11-24", start: "09:15"))!;
        Assert.Equal("Travel (Charger 1 (Rear)) 24/11 09:15-12:45", Planner.Renamed(Config, parsed));
    }

    // ---- RunTests -------------------------------------------------------------------------------------------------

    private static FakeOutlook Outlook(JsonObject[]? plan = null, JsonObject[]? events = null, JsonObject[]? messages = null, string? eventsFail = null) => new(
        plan ?? [Segment("2026-11-16", "office"), Segment("2026-11-17", "remote"), Segment("2026-11-24", "office")],
        events ?? [Event("2026-11-16", "Travel; EV"), Event("2026-11-17", "Travel; EV"), Event("2026-11-24", "Travel; EV", 7, "tue-morning"), Event("2026-11-24", "Travel", 17, "tue-evening")],
        messages ?? [Reply("2026-11-24")]) { EventsFail = eventsFail };

    private Report Run(IOutlook outlook, Booker booker, bool dryRun = false) =>
        Runner.Run(Config, outlook, Today, dryRun, false, folder.Log, booker, () => new DateTime(2026, 11, 12, 8, 0, 0));

    [Fact]
    public void test_dry_run_changes_nothing()
    {
        var fake = Outlook();
        var booked = new List<DateOnly>();
        var report = Run(fake, (_, d) => { booked.Add(d); return "Submitted"; }, dryRun: true);
        Assert.Empty(booked);
        Assert.Empty(fake.Renamed);
        Assert.False(File.Exists(Config.StateFile));
        Assert.Contains("would book Mon 16 Nov", folder.Lines);
        Assert.Contains("would update Tue 24 Nov Travel block -> Travel; EV – Charger 1 (Rear), 08:00–12:00", folder.Lines);
        Assert.Equal(["Tue 17 Nov: Travel block says EV but work plan says remote. Book EV?"], report.Questions);
    }

    [Fact]
    public void test_run_books_records_and_renames()
    {
        var fake = Outlook();
        var report = Run(fake, (_, d) => $"Submitted: {PyTime.Iso(d)}");
        Assert.Equal(["2026-11-16", "2026-11-24"], report.Booked);
        var saved = State.Load(Config.StateFile);
        Assert.Equal("08:00", (string)saved["2026-11-16"]!["start"]!);
        Assert.Equal("Charger 1 (Rear)", (string)saved["2026-11-24"]!["approved"]!);
        Assert.Equal("""[["tue-morning", "Travel; EV – Charger 1 (Rear), 08:00–12:00", "Charger 1 (Rear)"]]""", PyJson.Dump(fake.Renamed, ensureAscii: false));
        var again = Run(fake, (_, _) => throw new InvalidOperationException("booked twice"));
        Assert.Empty(again.Booked);
    }

    [Fact]
    public void test_failed_booking_is_a_question()
    {
        var report = Run(Outlook(messages: []), (_, _) => "Clicked Submit but didn't see a confirmation.");
        Assert.Equal(["2026-11-16", "2026-11-24"], report.Failed);
        Assert.Contains(report.Questions, q => q.Contains("booking FAILED"));
        Assert.False(File.Exists(Config.StateFile) && State.Load(Config.StateFile).Count > 0);
    }

    [Fact]
    public void test_declined_and_unapproved_tomorrow_are_questions()
    {
        State.Save(Config.StateFile, new JsonObject { ["2026-11-13"] = new JsonObject { ["start"] = "08:00" }, ["2026-11-24"] = new JsonObject { ["start"] = "08:00" } });
        var fake = Outlook(plan: [], events: [], messages: [Reply("2026-11-24", subject: "Your Electric Car Charging Request - DECLINED")]);
        var questions = Runner.ApplyApprovals(Config, fake, Today, false, folder.Log);
        Assert.Equal(["Tue 24 Nov: EV request DECLINED.", "Fri 13 Nov: EV requested but not approved yet."], questions);
    }

    [Fact]
    public void test_approved_with_no_morning_entry_is_logged_not_raised()
    {
        var fake = Outlook(events: [Event("2026-11-24", "Travel", 17)]);
        Assert.Empty(Runner.ApplyApprovals(Config, fake, Today, false, folder.Log));
        Assert.Contains(folder.Lines, line => line.Contains("no morning Travel block to update (not raised)"));
    }

    [Fact]
    public void test_never_cancels()
    {
        State.Save(Config.StateFile, new JsonObject { ["2026-11-17"] = new JsonObject { ["start"] = "08:00", ["approved"] = "Charger 1 (Rear)" } });
        var report = Run(Outlook(messages: []), (_, _) => "Submitted");
        Assert.Contains(folder.Lines, line => line.Contains("no longer says office (not raised)"));
        Assert.DoesNotContain("Tue 17 Nov", string.Join(" ", report.Questions));
    }

    [Fact]
    public void test_calendar_failure()
    {
        Assert.Throws<CalendarError>(() => Run(new FakeOutlook() { EventsFail = "network down" }, (_, _) => "Submitted"));
    }

    [Fact]
    public void test_hook_payload()
    {
        // A real process, as the Python test ran one: `more` echoes its stdin, which comes back in the log.
        Config.AfterRun = ["cmd.exe", "/c", "more"];
        var report = new Report();
        report.Questions.Add("q");
        report.Booked.Add("2026-11-16");
        Runner.AfterRun(Config, report, folder.Log);
        var echoed = folder.Lines.Single(line => line.StartsWith("  hook: {", StringComparison.Ordinal))["  hook: ".Length..];
        var captured = (JsonObject)JsonNode.Parse(echoed)!;
        Assert.Equal("""["q"]""", captured["questions"]!.ToJsonString());
        Assert.Equal("""["2026-11-16"]""", captured["booked"]!.ToJsonString());
        Assert.True(captured.ContainsKey("summary"));
        Assert.Equal("after-run hook: cmd.exe /c more", folder.Lines[0]);
    }

    // ---- FormTests ------------------------------------------------------------------------------------------------

    [Fact]
    public void test_values_from_template_and_car()
    {
        Config.Form.Fields.Add(new FormField(6, "text", "{additional_information}"));
        var details = new JsonObject { ["car_make"] = "Make", ["additional_information"] = "" };
        var filled = Form.Values(Config, new DateOnly(2026, 11, 24), "08:00", details);
        Assert.Equal([(1, "date", "24/11/2026"), (2, "choice", "08:00"), (3, "text", "Make")], filled);
    }

    [Fact]
    public void test_missing_car_key() =>
        Assert.Throws<FormError>(() => Form.Values(Config, Today, "08:00", []));

    [Fact]
    public void test_start_time_precedence()
    {
        Assert.Equal("08:00", Form.StartTime(Config, new JsonObject { ["start_time"] = "09:00" }));
        Config.StartTime = "";
        Assert.Equal("09:00", Form.StartTime(Config, new JsonObject { ["start_time"] = "09:00" }));
        Assert.Equal("08:00", Form.StartTime(Config, []));
    }

    // ---- ConfigTests ----------------------------------------------------------------------------------------------

    [Fact]
    public void test_template_loads()
    {
        Assert.Equal(14, Config.DaysAhead);
        Assert.True(Config.Approvals.Enabled);
        Assert.Equal(3, Config.Form.Fields.Count);
    }

    [Fact]
    public void test_bad_settings()
    {
        var path = Path.Combine(folder.Path, "x.toml");
        foreach (var bad in new[] { "[booking]\nstart_time = '8am'\n", "[trigger]\nbook = '('\n" })
            Assert.Throws<PyValueError>(() => Settings.Parse(bad, path));
        Assert.Throws<PyValueError>(() => Settings.Parse("[approvals]\nsender = \"x@example.com\"\n", path));
    }

    [Fact]
    public void test_no_approvals_section()
    {
        var config = Settings.Parse("", Path.Combine(folder.Path, "x.toml"));
        Assert.False(config.Approvals.Enabled);
        Assert.Empty(Runner.ApplyApprovals(config, new FakeOutlook(), Today, false, folder.Log));
    }
}
