// ev-charging-parity: the port checked against the Python version (tests/parity/oracle.py as the oracle), AOT-compiled
// so the checks run the code as the exes do. It prints counts and hashes only, never a name, a subject or a date.
//
//   ev-charging-parity scenarios <scenarios.json>       oracle scenarios (golden or generated), compared field by field
//   ev-charging-parity misc <misc.json>                 settings, templates, URLs, text, hooks, status
//   ev-charging-parity cli <cli.json>                   argparse's texts and exit codes
//   ev-charging-parity record <settings.toml> <out.json> a live read for replay: GETs only, through the read-only fence,
//                                                        silent sign-in on the settings' msal_cache (which must be a copy)
//   ev-charging-parity keys <settings.toml>...           the run lock and Edge profile lock names, to compare with Python
//   ev-charging-parity replay <recording.json> <out.json> the recording replayed (dry, and a real run with renames
//                                                        journaled and a fake booker), as oracle.py replay does
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EvCharging;
using EvCharging.Parity;
using M365Graph;
using PyCompat;

static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!;

static int Show(string what, (int Compared, List<string> Differences) result)
{
    var cases = result.Differences.Select(d => d.Split(':')[0]).Distinct().ToList();
    Console.WriteLine($"{what}: {result.Compared} compared, {cases.Count} different ({result.Differences.Count} fields)");
    if (cases.Count > 0)
        Console.WriteLine($"  cases: {string.Join(' ', cases)}");
    foreach (var line in result.Differences.Take(20))
        Console.WriteLine($"  {line}");
    return result.Differences.Count == 0 ? 0 : 1;
}

switch (args.FirstOrDefault())
{
    case "scenarios":
        return Show("scenarios", Checks.Scenarios(Read(args[1]).AsArray()));
    case "misc":
    {
        var code = Show("misc", Checks.Misc(Read(args[1]).AsObject()));
        Console.WriteLine($"  (invalid-pattern messages worded differently, deliberately: {Checks.RegexWording})");
        return code;
    }
    case "cli":
        return Show("cli", Checks.Cli(Read(args[1]).AsObject()));
    case "record":
    {
        var settings = Settings.Load(args[1]);
        var cache = settings.MsalCache ?? throw new InvalidOperationException("The parity settings must name msal_cache (a copy).");
        if (PyPath.Same(cache, Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "claude-ea", "msal-cache.json")))
            throw new InvalidOperationException("The parity settings must name a copy of the token cache, never the shared one.");
        var client = new GraphClient(new GraphClientOptions
        {
            CachePath = cache,
            Tenant = settings.Tenant,
            LoginHint = settings.LoginHint,
            Scopes = M365GraphClient.Scopes,
            Interactive = false,
            SignInHint = "(parity: silent only)",
            Log = _ => { },
        });
        var recorder = new Recorder(new ReadOnlyGraph(new M365GraphClient(client)));
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var outlook = new Outlook(settings, recorder, WriteDoor.Refusing());
        // A dry run makes every read a run makes (the work plan, the window's events, the replies, each approved day's
        // events), and writes nothing: the door refuses, and the booker is never called in a dry run.
        Runner.Run(settings, outlook, today, dryRun: true, approvalsOnly: false, _ => { },
            (_, _) => throw new WriteRefusedException("a booking while recording"), () => now);
        var state = File.Exists(settings.StateFile) ? Encoding.UTF8.GetString(File.ReadAllBytes(settings.StateFile)) : null;
        var output = new JsonObject
        {
            ["settings"] = PyStr.ReadText(settings.Path),
            ["today"] = PyTime.Iso(today),
            ["now"] = PyTime.Iso(now, "seconds"),
            ["state"] = state,
            ["calls"] = recorder.Calls,
        };
        File.WriteAllText(args[2], PyJson.Dump(output, sortKeys: true), new UTF8Encoding(false));
        var items = recorder.Calls.Sum(c => c.Value?["value"] is JsonArray a ? a.Count : 0);
        Console.WriteLine($"recorded {recorder.Calls.Count} GETs, {items} items; sha {Sha(PyJson.Dump(output, sortKeys: true))}");
        return 0;
    }
    case "replay":
    {
        var result = ScenarioRunner.Replay(Read(args[1]).AsObject());
        File.WriteAllText(args[2], PyJson.Dump(result, sortKeys: true), new UTF8Encoding(false));
        foreach (var (name, run) in result)
            Console.WriteLine($"{name}: sha {Sha(ScenarioRunner.Canonical(run))}, {run!["log"]!.AsArray().Count} log lines, "
                              + $"{run["renamed"]!.AsArray().Count} renames, {run["urls"]!.AsArray().Count} reads");
        return 0;
    }
    case "keys":
    {
        // The run lock and the Edge profile lock for each settings file: names only (they're hashes).
        foreach (var path in args.Skip(1))
        {
            var settings = Settings.Load(path);
            Console.WriteLine($"{Runner.LockName(settings.Path)} {Form.ProfileLockName(settings.EdgeProfile)}");
        }
        return 0;
    }
    default:
        Console.Error.WriteLine("usage: ev-charging-parity scenarios|misc|cli|record|replay|keys ...");
        return 2;
}

/// <summary>Every GET through the fence, recorded by URL (each page, nextLinks included).</summary>
internal sealed class Recorder(IGraph inner) : IGraph
{
    public JsonObject Calls { get; } = [];

    public JsonObject Get(string url, IReadOnlyDictionary<string, string> headers)
    {
        var body = inner.Get(url, headers);
        Calls[url] = body.DeepClone();
        return body;
    }

    public JsonObject Send(string method, string url, JsonObject? body, IReadOnlyDictionary<string, string> headers) =>
        throw new WriteRefusedException($"{method} {url}");

    public void Token() => inner.Token();
}
