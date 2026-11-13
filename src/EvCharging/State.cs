using System.Text.Json.Nodes;
using PyCompat;

namespace EvCharging;

/// <summary>
/// What's been requested and approved, so no day is ever requested twice. A port of state.py, written byte for byte
/// as the Python writes it, so either version can carry on from the other's file:
/// <code>{"2026-11-24": {"start": "08:00", "submitted": "2026-11-10T08:41:08", "approved": "Charger 1"}}</code>
/// <para>It's written to a temporary file and swapped in, so a crash mid-write can't leave it half written.</para>
/// </summary>
public static class State
{
    /// <summary>The file's contents, or {} if there's no file.</summary>
    public static JsonObject Load(string path)
    {
        string text;
        try
        {
            text = PyStr.ReadText(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        var parsed = PyJson.Parse(text);
        return parsed as JsonObject ?? throw new PyValueError($"{path} isn't a JSON object of days.");
    }

    /// <summary>json.dumps(state, indent=2, sort_keys=True), written in text mode (so CRLF) to "{name}.{pid}.tmp", then
    /// os.replace.</summary>
    public static void Save(string path, JsonObject state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $"{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        PyStr.WriteText(temp, PyJson.Dump(state, indent: 2, ensureAscii: true, sortKeys: true));
        // Deliberately unlike Python, the swap is retried: a scanner can hold the new file for a moment (access
        // denied), and a request submitted but not recorded would be requested again by the next run.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException && attempt < 5)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }

    /// <summary>The bytes Save would write.</summary>
    public static string Text(JsonObject state) => PyStr.WindowsNewlines(PyJson.Dump(state, indent: 2, ensureAscii: true, sortKeys: true));

    /// <summary>record_submission(): the day's start, and when it was submitted (to the second, local time).</summary>
    public static void RecordSubmission(string path, DateOnly day, string start, DateTime now)
    {
        var state = Load(path);
        var key = PyTime.Iso(day);
        if (state[key] is not JsonObject entry)
            state[key] = entry = [];
        entry["start"] = start;
        entry["submitted"] = PyTime.Iso(now, "seconds");
        Save(path, state);
    }
}
