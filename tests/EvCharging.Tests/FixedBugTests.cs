using System.Text.Json.Nodes;
using EvCharging.Parity;
using PyCompat;
using WinBackground;

namespace EvCharging.Tests;

/// <summary>The regression tests for the five bugs fixed in Python 0.1.1 (FixedBugTests in tests/test_ev_charging.py),
/// by name where there's a counterpart.</summary>
public sealed class FixedBugTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 12);   // a Thursday
    private readonly Folder folder = new();
    private Settings Config => folder.Settings;

    public void Dispose() => folder.Dispose();

    private static FakeOutlook Outlook(JsonObject[]? events = null, JsonObject[]? messages = null, JsonObject? fail = null) =>
        new(new JsonObject
        {
            ["plan"] = new JsonArray(PythonTests.Segment("2026-11-16", "office"), PythonTests.Segment("2026-11-17", "remote"),
                PythonTests.Segment("2026-11-24", "office")),
            ["events"] = new JsonArray((events ?? [PythonTests.Event("2026-11-16", "Travel; EV"), PythonTests.Event("2026-11-17", "Travel; EV"),
                PythonTests.Event("2026-11-24", "Travel; EV", 7, "tue-morning"), PythonTests.Event("2026-11-24", "Travel", 17, "tue-evening")])
                .Select(e => (JsonNode)e.DeepClone()).ToArray()),
            ["messages"] = new JsonArray((messages ?? [PythonTests.Reply("2026-11-24")]).Select(m => (JsonNode)m.DeepClone()).ToArray()),
            ["fail"] = fail ?? [],
        });

    private Report Run(IOutlook outlook, Booker booker) =>
        Runner.Run(Config, outlook, Today, false, false, folder.Log, booker, () => new DateTime(2026, 11, 12, 8, 0, 0));

    /// <summary>The state file held open by "a scanner": it can be read, but not replaced, until disposed.</summary>
    private FileStream Hold()
    {
        if (!File.Exists(Config.StateFile))
            State.Save(Config.StateFile, []);
        return new FileStream(Config.StateFile, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>The command line loads the settings file, so point its state and car files at the ones the tests use.</summary>
    public FixedBugTests()
    {
        var text = File.ReadAllText(Config.Path)
            .Replace(Path.Combine(folder.Path, "state_file"), Config.StateFile, StringComparison.Ordinal)
            .Replace(Path.Combine(folder.Path, "car_file"), Config.CarFile, StringComparison.Ordinal);
        File.WriteAllText(Config.Path, text);
    }

    private (int Code, string Out, string Err) Cli(Func<Settings, DateOnly, SubmitDoor, bool, bool, string, string> form, params string[] argv)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = EvCharging.Cli.Run(["--config", Config.Path, .. argv], new CliHost
        {
            Out = output,
            Err = error,
            BookForm = form,
            Now = () => new DateTime(2026, 11, 12, 8, 0, 0),
        });
        return (code, output.ToString(), error.ToString());
    }

    // 1. A sign-in failure while planning surfaced as "couldn't read your calendar", never "needs a sign-in".

    [Fact]
    public void test_sign_in_needed_while_planning_is_not_a_calendar_error()
    {
        var fake = Outlook(fail: new JsonObject { ["sign_in"] = "No usable sign-in." });
        Assert.Throws<M365Graph.SignInNeededException>(() => Run(fake, (_, _) => "Submitted"));
        Assert.Equal("SignInNeeded", Runner.PythonType(new M365Graph.SignInNeededException("x")));
    }

    [Fact]
    public void test_scheduled_run_notifies_sign_in_needed()
    {
        var notes = new List<string>();
        var code = EvCharging.Cli.Run(["--config", Config.Path, "run", "--scheduled"], new CliHost
        {
            Out = new StringWriter(),
            Err = new StringWriter(),
            Graph = (_, _) => new SignedOutGraph(),
            Booker = _ => (_, _) => throw new InvalidOperationException("booked"),
            Notify = (title, _) => notes.Add(title),
            Now = () => new DateTime(2026, 11, 12, 8, 0, 0),
        });
        Assert.Equal(1, code);
        Assert.Equal(["ev-charging needs a sign-in"], notes);
    }

    private sealed class SignedOutGraph : IGraph
    {
        public JsonObject Get(string url, IReadOnlyDictionary<string, string> headers) =>
            throw new M365Graph.SignInNeededException("No usable sign-in. At a console, run: ev-charging sign-in");

        public JsonObject Send(string method, string url, JsonObject? body, IReadOnlyDictionary<string, string> headers) =>
            throw new InvalidOperationException("no writes");

        public void Token() => throw new M365Graph.SignInNeededException("No usable sign-in.");
    }

    // 2. A state write failing after a real submit left the day unrecorded, so the next run requested it again.

    [Fact]
    public void test_submitted_day_survives_a_state_file_that_then_fails()
    {
        Config.Approvals.Enabled = false;
        FileStream? held = null;
        Report report;
        try
        {
            report = Run(Outlook(), (settings, day) =>
            {
                Assert.True(State.Load(settings.StateFile).ContainsKey(PyTime.Iso(day)));   // recorded before the submit
                held ??= Hold();   // so the state file failing from now on can't lose it
                return $"Submitted: {PyTime.Iso(day)}";
            });
        }
        finally
        {
            held?.Dispose();
        }
        Assert.Equal(["2026-11-16"], report.Booked);
        Assert.Contains(report.Questions, q => q.StartsWith("Tue 24 Nov: booking FAILED. Not submitted: couldn't record it in the state file first.", StringComparison.Ordinal));
        Assert.Equal(["2026-11-16"], State.Load(Config.StateFile).Select(kv => kv.Key));
        var again = Run(Outlook(), (_, d) => PyTime.Iso(d) == "2026-11-24" ? "Submitted" : throw new InvalidOperationException("asked twice"));
        Assert.Equal(["2026-11-24"], again.Booked);
    }

    [Fact]
    public void test_nothing_submitted_if_the_day_cant_be_recorded_first()
    {
        Config.Approvals.Enabled = false;
        Report report;
        using (Hold())
            report = Run(Outlook(), (_, _) => throw new InvalidOperationException("submitted without a record"));
        Assert.Empty(report.Booked);
        Assert.Equal(["2026-11-16", "2026-11-24"], report.Failed);
        var failed = report.Questions.Where(q => q.Contains("FAILED", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, failed.Count);
        Assert.All(failed, q => Assert.Contains("Not submitted", q, StringComparison.Ordinal));
    }

    [Fact]
    public void test_failed_submit_is_taken_off_so_the_next_run_retries()
    {
        var report = Run(Outlook(messages: []), (_, _) => throw new FormError("The form didn't load."));
        Assert.Equal(["2026-11-16", "2026-11-24"], report.Failed);
        Assert.Empty(State.Load(Config.StateFile));
        var again = Run(Outlook(messages: []), (_, d) => $"Submitted: {PyTime.Iso(d)}");
        Assert.Equal(["2026-11-16", "2026-11-24"], again.Booked);
    }

    [Fact]
    public void test_failed_submit_that_cant_be_taken_off_says_so()
    {
        Config.Approvals.Enabled = false;
        FileStream? held = null;
        Report report;
        try
        {
            report = Run(Outlook(), (_, _) =>
            {
                held ??= Hold();
                return "Clicked Submit but didn't see a confirmation.";
            });
        }
        finally
        {
            held?.Dispose();
        }
        var first = report.Questions.First(q => q.StartsWith("Mon 16 Nov", StringComparison.Ordinal));
        Assert.EndsWith("It's still marked as requested, so it won't be retried: book it by hand (--force).", first);
        Assert.True(State.Load(Config.StateFile).ContainsKey("2026-11-16"));
    }

    [Fact]
    public void test_book_submit_records_first_and_undoes_a_failure()
    {
        var seen = new List<bool>();
        var (code, _, _) = Cli((settings, day, _, _, _, _) =>
        {
            seen.Add(State.Load(settings.StateFile).ContainsKey(PyTime.Iso(day)));
            return "Clicked Submit but didn't see a confirmation.";
        }, "book", "2026-11-26", "--submit");
        Assert.Equal(1, code);
        Assert.Equal([true], seen);
        Assert.False(State.Load(Config.StateFile).ContainsKey("2026-11-26"));
        (code, _, _) = Cli((_, _, _, _, _, _) => "Submitted: Thu 26 Nov 2026, 08:00.", "book", "2026-11-26", "--submit");
        Assert.Equal(0, code);
        Assert.Equal("08:00", (string)State.Load(Config.StateFile)["2026-11-26"]!["start"]!);
    }

    [Fact]
    public void test_book_submit_undoes_when_the_form_raises()
    {
        var (code, _, err) = Cli((_, _, _, _, _, _) => throw new FormError("The form didn't load."), "book", "2026-11-26", "--submit");
        Assert.Equal(2, code);
        Assert.Contains("The form didn't load.", err, StringComparison.Ordinal);
        Assert.False(State.Load(Config.StateFile).ContainsKey("2026-11-26"));
    }

    [Fact]
    public void test_book_submit_when_the_day_cant_be_recorded()
    {
        int code;
        string err;
        using (Hold())
            (code, _, err) = Cli((_, _, _, _, _, _) => throw new InvalidOperationException("submitted without a record"), "book", "2026-11-26", "--submit");
        Assert.Equal(1, code);
        Assert.Contains("Not submitted", err, StringComparison.Ordinal);
    }

    // 3. An error part-way through the approvals dropped the approvals already read.

    [Fact]
    public void test_approvals_read_before_an_error_are_kept()
    {
        var fake = Outlook(
            events: [PythonTests.Event("2026-11-23", "Travel; EV", 7, "mon"), PythonTests.Event("2026-11-24", "Travel; EV", 7, "tue")],
            messages: [PythonTests.Reply("2026-11-20", subject: "Your Electric Car Charging Request - DECLINED"),
                PythonTests.Reply("2026-11-23", point: "Charger 2 (Front)"), PythonTests.Reply("2026-11-24")],
            fail: new JsonObject { ["rename"] = "rename refused" });
        var questions = Runner.ApplyApprovals(Config, fake, Today, false, folder.Log);
        Assert.Equal(["Fri 20 Nov: EV request DECLINED.", "Couldn't read the approval emails: rename refused"], questions);
        var saved = State.Load(Config.StateFile);
        Assert.Equal("Charger 2 (Front)", (string)saved["2026-11-23"]!["approved"]!);   // read; its rename failed
        Assert.False(saved.ContainsKey("2026-11-24"));                                   // never reached
        Assert.Contains("FAILED applying approvals: rename refused", folder.Lines);
    }

    [Fact]
    public void test_approvals_error_in_a_dry_run_saves_nothing()
    {
        var fake = Outlook(messages: [PythonTests.Reply("2026-11-24")], fail: new JsonObject { ["events"] = "events down" });
        Assert.Equal(["Couldn't read the approval emails: events down"], Runner.ApplyApprovals(Config, fake, Today, true, folder.Log));
        Assert.False(File.Exists(Config.StateFile));
    }

    // 4. A car file that's a JSON list (or any non-object) crashed the whole run.

    [Fact]
    public void test_car_file_not_an_object()
    {
        foreach (var text in new[] { "[1, 2]", "\"x\"", "3", "null" })
        {
            File.WriteAllText(Config.CarFile, text);
            var error = Assert.Throws<FormError>(() => Form.Car(Config));
            Assert.Contains(Config.CarFile, error.Message, StringComparison.Ordinal);
        }
        File.WriteAllText(Config.CarFile, "[1, 2]");
        Config.StartTime = "";
        Config.Approvals.Enabled = false;
        var report = Run(Outlook(), (settings, _) =>
        {
            Form.Car(settings);   // what the real form does first
            return "Submitted";
        });
        Assert.Equal(["2026-11-16", "2026-11-24"], report.Failed);
        Assert.StartsWith("Mon 16 Nov: booking FAILED. FormError: The car details at ", report.Questions[0]);   // cut at 120
        Assert.Contains($"book Mon 16 Nov: FormError: The car details at {Config.CarFile} aren't a JSON object of names and values.", folder.Lines);
        Assert.Empty(State.Load(Config.StateFile));
    }

    // 5. book --submit didn't take the run lock, so it could overlap the scheduled run.

    [Fact]
    public void test_book_takes_the_run_lock()
    {
        var held = new List<bool>();
        string Probe(Settings settings)
        {
            var thread = new Thread(() =>
            {
                using var other = Locks.SingleInstance(Runner.LockName(settings.Path));
                held.Add(other is null);
            });
            thread.Start();
            thread.Join();
            return "Submitted: Thu 26 Nov 2026, 08:00.";
        }
        Cli((settings, _, _, _, _, _) => Probe(settings), "book", "2026-11-26", "--submit", "--force");
        Cli((settings, _, _, _, _, _) => Probe(settings), "book", "2026-11-26");
        Assert.Equal([true, true], held);
    }
}
