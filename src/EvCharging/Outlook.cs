using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using M365Graph;
using PyCompat;

namespace EvCharging;

/// <summary>The few Graph calls the run makes. Tests and parity checks give a fake with the same methods, as the
/// Python tests did.</summary>
public interface IOutlook
{
    /// <summary>"My work plan" occurrences (office, remote, timeOff), v1.0 since Dec 2025.</summary>
    List<JsonObject> WorkPlan(DateOnly start, DateOnly end);

    List<JsonObject> Events(DateOnly start, DateOnly end);

    /// <summary>Every message from sender since a date, from any folder (so a rule filing them elsewhere, or Deleted
    /// Items until it's emptied, makes no difference).</summary>
    List<JsonObject> MessagesFrom(string sender, DateOnly since);

    /// <summary>The morning entry's new subject, and its location when one is given. Only ever through the write
    /// door.</summary>
    void Rename(string eventId, string subject, string? location);
}

/// <summary>What the tool asks of Graph, so tests and the parity checks can give it recorded replies instead.
/// Synchronous, as the Python was: one call at a time.</summary>
public interface IGraph
{
    /// <summary>One page: the reply to a GET (a full URL, an @odata.nextLink included).</summary>
    JsonObject Get(string url, IReadOnlyDictionary<string, string> headers);

    /// <summary>One request with a JSON body sent as Python's json.dumps(body) wrote it. Throws GraphRequestException
    /// for a status outside 200-299.</summary>
    JsonObject Send(string method, string url, JsonObject? body, IReadOnlyDictionary<string, string> headers);

    /// <summary>Make sure there's a token for the tool's scopes (sign-in).</summary>
    void Token();
}

/// <summary>A write was asked of something that may only read: a bug, never a decision. Nothing was sent.</summary>
public sealed class WriteRefusedException(string what) : Exception($"Refused a write ({what}): this run may only read.");

/// <summary>The fence under the write door: every read goes through this, which passes GETs and refuses everything
/// else. Only a Live write door holds the Graph underneath it.</summary>
public sealed class ReadOnlyGraph(IGraph inner) : IGraph
{
    public JsonObject Get(string url, IReadOnlyDictionary<string, string> headers) => inner.Get(url, headers);

    public JsonObject Send(string method, string url, JsonObject? body, IReadOnlyDictionary<string, string> headers) =>
        method == "GET" ? inner.Get(url, headers) : throw new WriteRefusedException($"{method} {url}");

    public void Token() => inner.Token();
}

/// <summary>The real Graph, through background-tools-lib's M365Graph: the shared sign-in, timeouts, IPv4 first, no
/// browser when nobody's at the console, and retries as Python's m365-graph did.</summary>
public sealed class M365GraphClient(GraphClient client) : IGraph
{
    /// <summary>Calendars.ReadWrite (to rename the morning entry) and Mail.Read (the replies).</summary>
    public static readonly IReadOnlyList<string> Scopes = ["Calendars.ReadWrite", "Mail.Read"];

    public const string SignInHint = "At a console, run: ev-charging sign-in";

    public GraphClient Client { get; } = client;

    /// <summary>client(config, interactive): the settings' sign-in, and a browser only when interactive says so (null:
    /// only if someone is at the console).</summary>
    public static M365GraphClient For(Settings settings, bool? interactive, Action<string>? log = null)
    {
        var options = new GraphClientOptions
        {
            Tenant = settings.Tenant,
            LoginHint = settings.LoginHint,
            Scopes = Scopes,
            Interactive = interactive,
            SignInHint = SignInHint,
            Log = log,
        };
        if (settings.MsalCache is not null)
            options = options with { CachePath = settings.MsalCache };
        return new M365GraphClient(new GraphClient(options));
    }

    public JsonObject Get(string url, IReadOnlyDictionary<string, string> headers) =>
        Client.GetAsync(url, Scopes, headers).GetAwaiter().GetResult();

    public JsonObject Send(string method, string url, JsonObject? body, IReadOnlyDictionary<string, string> headers)
    {
        if (method == "GET")
            return Get(url, headers);
        var bytes = body is null ? null : Encoding.UTF8.GetBytes(PyJson.Dump(body));
        // A PATCH is repeatable, so it retries as reads do (and as Python's did).
        var reply = Client.SendRawAsync(new HttpMethod(method), url, bytes, "application/json", GraphRetryPolicy.Read, Scopes, headers)
            .GetAwaiter().GetResult();
        if (!reply.IsSuccess)
            throw new GraphRequestException(reply.Status, method, url, Encoding.UTF8.GetString(reply.Body));
        if (reply.Body.Length == 0)
            return [];
        return JsonNode.Parse(reply.Body) as JsonObject ?? throw new JsonException($"Graph didn't return a JSON object for {url}.");
    }

    public void Token() => Client.TokenAsync().GetAwaiter().GetResult();
}

/// <summary>
/// Microsoft Graph: the work plan, calendar entries and the replies to requests (all read through the read-only
/// fence), and renaming an entry (only through the write door). The same URLs, byte for byte, as outlook.py:
/// Python's urlencode (quote_plus) and the same Prefer header.
/// </summary>
public sealed class Outlook : IOutlook
{
    public const string GraphBase = "https://graph.microsoft.com/v1.0";

    private readonly IGraph reads;
    private readonly WriteDoor door;

    /// <summary>reads is wrapped in the read-only fence here, whatever it is.</summary>
    public Outlook(Settings settings, IGraph graph, WriteDoor door)
    {
        reads = graph as ReadOnlyGraph ?? new ReadOnlyGraph(graph);
        this.door = door;
        Headers = HeadersFor(settings.Timezone);
    }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public static Dictionary<string, string> HeadersFor(string timezone) =>
        new() { ["Prefer"] = $"outlook.timezone=\"{timezone}\"" };

    public static string WorkPlanUrl(DateOnly start, DateOnly end) =>
        $"{GraphBase}/me/settings/workHoursAndLocations/occurrencesView(startDateTime='{PyTime.Iso(start)}T00:00:00Z',endDateTime='{PyTime.Iso(end)}T00:00:00Z')";

    public static string EventsUrl(DateOnly start, DateOnly end) =>
        $"{GraphBase}/me/calendarView?" + UrlEncode(
        [
            ("startDateTime", $"{PyTime.Iso(start)}T00:00:00"),
            ("endDateTime", $"{PyTime.Iso(end)}T00:00:00"),
            ("$select", "id,subject,start,location"),
            ("$top", "500"),
        ]);

    public static string MessagesUrl(string sender, DateOnly since) =>
        $"{GraphBase}/me/messages?" + UrlEncode(
        [
            ("$filter", $"from/emailAddress/address eq '{sender}' and receivedDateTime ge {PyTime.Iso(since)}T00:00:00Z"),
            ("$select", "subject,body,receivedDateTime"),
            ("$top", "100"),
        ]);

    public static string EventUrl(string eventId) => $"{GraphBase}/me/events/{eventId}";

    /// <summary>The PATCH body: the subject, and the location when one is given (a non-empty one, as Python's
    /// truthiness).</summary>
    public static JsonObject RenameBody(string subject, string? location)
    {
        var body = new JsonObject { ["subject"] = subject };
        if (!string.IsNullOrEmpty(location))
            body["location"] = new JsonObject { ["displayName"] = location };
        return body;
    }

    private List<JsonObject> Pages(string url)
    {
        var items = new List<JsonObject>();
        string? next = url;
        while (!string.IsNullOrEmpty(next))
        {
            var body = reads.Get(next, Headers);
            if (body["value"] is JsonArray values)
                foreach (var item in values)
                    if (item is JsonObject o)
                        items.Add(o);
            next = body["@odata.nextLink"] is JsonValue v && v.TryGetValue<string>(out var link) ? link : null;
        }
        return items;
    }

    public List<JsonObject> WorkPlan(DateOnly start, DateOnly end) => Pages(WorkPlanUrl(start, end));

    public List<JsonObject> Events(DateOnly start, DateOnly end) => Pages(EventsUrl(start, end));

    public List<JsonObject> MessagesFrom(string sender, DateOnly since) => Pages(MessagesUrl(sender, since));

    public void Rename(string eventId, string subject, string? location) =>
        door.Send(new WriteCall("PATCH", EventUrl(eventId), RenameBody(subject, location)), Headers);

    // ---- urllib.parse.urlencode -----------------------------------------------------------------------------------

    /// <summary>urllib.parse.urlencode(pairs): quote_plus on each key and value, joined with &amp;.</summary>
    public static string UrlEncode(IEnumerable<(string Key, string Value)> pairs) =>
        string.Join("&", pairs.Select(p => QuotePlus(p.Key) + "=" + QuotePlus(p.Value)));

    /// <summary>quote_plus(text, safe=''): letters, digits and _.-~ as they are, a space as +, everything else as %XX
    /// of its UTF-8 bytes (upper case).</summary>
    public static string QuotePlus(string text)
    {
        var b = new StringBuilder();
        foreach (var by in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)by;
            if (by < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~'))
                b.Append(c);
            else if (c == ' ')
                b.Append('+');
            else
                b.Append('%').Append(by.ToString("X2", CultureInfo.InvariantCulture));
        }
        return b.ToString();
    }
}

/// <summary>How a run may write to the calendar: Live sends; Journal records what it would send and sends nothing;
/// Refuse throws. Dry runs, tests and checks use Refuse or Journal, so a bug that writes fails loudly or is only
/// recorded.</summary>
public enum WriteMode
{
    Live,
    Journal,
    Refuse,
}

/// <summary>One intended Graph write: the same method, URL and body Live would send.</summary>
public sealed record WriteCall(string Method, string Url, JsonObject? Body)
{
    /// <summary>"METHOD url body", the body as json.dumps(body) (what goes on the wire).</summary>
    public string Text => Body is null ? $"{Method} {Url}" : $"{Method} {Url} {PyJson.Dump(Body)}";
}

/// <summary>The calendar write door: the only way anything reaches the calendar. Live writes through the given
/// (unfenced) Graph; the others never touch it.</summary>
public sealed class WriteDoor
{
    private readonly IGraph? graph;
    private readonly Action<WriteCall>? also;

    private WriteDoor(WriteMode mode, IGraph? graph, Action<WriteCall>? also)
    {
        Mode = mode;
        this.graph = graph;
        this.also = also;
    }

    public WriteMode Mode { get; }

    /// <summary>What a Journal door was asked to write, in order (empty otherwise).</summary>
    public List<WriteCall> Journal { get; } = [];

    public static WriteDoor Live(IGraph graph) =>
        new(WriteMode.Live, graph is ReadOnlyGraph ? throw new ArgumentException("A live door needs the unfenced Graph.") : graph, null);

    public static WriteDoor Journaling(Action<WriteCall>? also = null) => new(WriteMode.Journal, null, also);

    public static WriteDoor Refusing() => new(WriteMode.Refuse, null, null);

    public static WriteDoor For(WriteMode mode, IGraph graph, Action<WriteCall>? journal = null) => mode switch
    {
        WriteMode.Live => Live(graph),
        WriteMode.Journal => Journaling(journal),
        _ => Refusing(),
    };

    public void Send(WriteCall call, IReadOnlyDictionary<string, string> headers)
    {
        switch (Mode)
        {
            case WriteMode.Live:
                graph!.Send(call.Method, call.Url, call.Body, headers);
                break;
            case WriteMode.Journal:
                Journal.Add(call);
                also?.Invoke(call);
                break;
            default:
                throw new WriteRefusedException($"{call.Method} {call.Url}");
        }
    }
}
