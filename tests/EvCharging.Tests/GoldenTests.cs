using System.Text.Json.Nodes;
using EvCharging.Parity;
using PyCompat;

namespace EvCharging.Tests;

/// <summary>The Python as oracle: tests/parity/golden/*.json, written by tests/parity/oracle.py from the Python
/// v0.1.0.</summary>
public sealed class GoldenTests
{
    private static JsonNode Golden(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", name)))!;

    [Fact]
    public void Scenarios_match_the_python()
    {
        var (compared, differences) = Checks.Scenarios(Golden("scenarios.json").AsArray());
        Assert.Equal(400, compared);
        Assert.True(differences.Count == 0, string.Join(" | ", differences));
    }

    [Fact]
    public void Settings_templates_urls_text_hooks_and_status_match_the_python()
    {
        var (compared, differences) = Checks.Misc(Golden("misc.json").AsObject());
        Assert.True(compared > 800, $"only {compared} compared");
        Assert.True(differences.Count == 0, string.Join(" | ", differences));
    }

    [Fact]
    public void Command_line_texts_and_exit_codes_match_the_python()
    {
        var (compared, differences) = Checks.Cli(Golden("cli.json").AsObject());
        Assert.Equal(24, compared);
        Assert.True(differences.Count == 0, string.Join(" | ", differences));
    }
}

/// <summary>The doors and the fence: nothing in a test, a check or a dry run can rename an entry or click Submit.</summary>
public sealed class DoorTests
{
    private sealed class RecordingGraph : IGraph
    {
        public List<string> Sent { get; } = [];

        public JsonObject Get(string url, IReadOnlyDictionary<string, string> headers)
        {
            Sent.Add("GET " + url);
            return new JsonObject { ["value"] = new JsonArray() };
        }

        public JsonObject Send(string method, string url, JsonObject? body, IReadOnlyDictionary<string, string> headers)
        {
            Sent.Add($"{method} {url} {(body is null ? "" : PyJson.Dump(body))}");
            return [];
        }

        public void Token()
        {
        }
    }

    [Fact]
    public void The_fence_passes_reads_and_refuses_writes()
    {
        var graph = new RecordingGraph();
        var fence = new ReadOnlyGraph(graph);
        fence.Get("https://graph.microsoft.com/v1.0/me", new Dictionary<string, string>());
        Assert.Throws<WriteRefusedException>(() => fence.Send("PATCH", "https://graph.microsoft.com/v1.0/me/events/x", [], new Dictionary<string, string>()));
        Assert.Equal(["GET https://graph.microsoft.com/v1.0/me"], graph.Sent);
    }

    [Fact]
    public void A_live_door_needs_the_unfenced_graph() =>
        Assert.Throws<ArgumentException>(() => WriteDoor.Live(new ReadOnlyGraph(new RecordingGraph())));

    [Fact]
    public void Refusing_and_journaling_doors_send_nothing()
    {
        using var folder = new Folder();
        var graph = new RecordingGraph();
        var refusing = new Outlook(folder.Settings, graph, WriteDoor.Refusing());
        Assert.Throws<WriteRefusedException>(() => refusing.Rename("id1", "Travel; EV", "Charger 1"));
        var journal = WriteDoor.Journaling();
        new Outlook(folder.Settings, graph, journal).Rename("id1", "Travel; EV – x", "Charger 1");
        Assert.Empty(graph.Sent);
        Assert.Equal("PATCH https://graph.microsoft.com/v1.0/me/events/id1 {\"subject\": \"Travel; EV \\u2013 x\", \"location\": {\"displayName\": \"Charger 1\"}}",
            Assert.Single(journal.Journal).Text);
    }

    [Fact]
    public void A_live_door_sends_the_python_body()
    {
        using var folder = new Folder();
        var graph = new RecordingGraph();
        new Outlook(folder.Settings, graph, WriteDoor.Live(graph)).Rename("id1", "Travel", null);
        Assert.Equal(["PATCH https://graph.microsoft.com/v1.0/me/events/id1 {\"subject\": \"Travel\"}"], graph.Sent);
    }

    [Fact]
    public void A_dry_run_through_the_command_line_writes_nothing_and_never_books()
    {
        using var folder = new Folder();
        var graph = new ServingGraph(folder.Settings);
        var output = new StringWriter();
        var booked = false;
        var code = Cli.Run(["--config", folder.Settings.Path, "run", "--dry-run", "--json"], new CliHost
        {
            Out = output,
            Err = output,
            Graph = (_, _) => graph,
            Booker = _ => (_, _) => { booked = true; return "Submitted"; },
            Now = () => new DateTime(2026, 11, 12, 8, 0, 0),
        });
        Assert.Equal(0, code);
        Assert.False(booked);
        Assert.DoesNotContain(graph.Sent, s => !s.StartsWith("GET ", StringComparison.Ordinal));
        Assert.Contains("\"decisions\"", output.ToString());
    }

    /// <summary>A Graph serving one office day with an EV Travel block, and an approval for it.</summary>
    private sealed class ServingGraph(Settings settings) : IGraph
    {
        public List<string> Sent { get; } = [];

        public JsonObject Get(string url, IReadOnlyDictionary<string, string> headers)
        {
            Sent.Add("GET " + url);
            JsonNode item = url.Contains("occurrencesView", StringComparison.Ordinal)
                ? PythonTests.Segment("2026-11-16", "office")
                : url.Contains("/me/messages", StringComparison.Ordinal)
                    ? PythonTests.Reply("2026-11-16")
                    : PythonTests.Event("2026-11-16", "Travel; EV");
            _ = settings;
            return new JsonObject { ["value"] = new JsonArray(item) };
        }

        public JsonObject Send(string method, string url, JsonObject? body, IReadOnlyDictionary<string, string> headers)
        {
            Sent.Add($"{method} {url}");
            return [];
        }

        public void Token()
        {
        }
    }

    [Fact]
    public void The_submit_door_refuses_by_default_and_local_only_means_file_pages()
    {
        Assert.False(SubmitDoor.Refusing.MayClick("file:///C:/mock.html"));
        var local = new SubmitDoor(SubmitMode.LocalOnly);
        Assert.True(local.MayClick("file:///C:/mock.html"));
        Assert.False(local.MayClick("https://forms.office.com/Pages/ResponsePage.aspx?id=x"));
        Assert.True(new SubmitDoor(SubmitMode.Live).MayClick("https://forms.office.com/x"));
    }

    [Fact]
    public void The_profile_lock_is_one_name_for_both_spellings()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA")!;
        var ours = PyPath.Pure(PyPath.ExpandVars("%LOCALAPPDATA%/claude-ea/m365-edge-profile", Settings.Environment));
        var theirs = PyPath.Pure(PyPath.ExpandUser("~/AppData/Local/claude-ea/m365-edge-profile", Settings.Environment));
        Assert.StartsWith(local, ours, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Form.ProfileLockName(ours), Form.ProfileLockName(theirs));
        Assert.Matches("^edge-profile-[0-9a-f]{16}$", Form.ProfileLockName(ours));
    }

    [Fact]
    public void The_run_lock_name_is_pythons()
    {
        // win_background.key: sha256(str(path).lower())[:16], so Python's lower-casing (here: no special letters).
        Assert.Equal("ev-charging-run-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                "c:\\users\\x\\ev.toml"u8.ToArray()))[..16], Runner.LockName(@"C:\Users\X\ev.toml"));
    }
}
