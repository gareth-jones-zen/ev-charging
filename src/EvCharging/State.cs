using System.Text.Json.Nodes;
using PyCompat;

namespace EvCharging;

/// <summary>
/// What's been requested and approved, so no day is ever requested twice. A port of state.py, written byte for byte
/// as the Python writes it, so either version can carry on from the other's file:
/// <code>{"2026-11-24": {"start": "08:00", "submitted": "2026-11-10T08:41:08", "approved": "Charger 1"}}</code>
/// <para>It's written to a temporary file and swapped in, so a crash mid-write can't leave it half written. The swap is
/// retried for a moment if it's refused (a scanner or OneDrive can hold a new file briefly).</para>
/// <para><b>A day is recorded before its request is submitted, not after</b> (RecordSubmission, then the form, then
/// UndoSubmission if it wasn't submitted), as Python 0.1.1 does. So nothing that goes wrong after a submission (the file
/// locked, the process killed) can leave a request unrecorded for the next run to send again. If the record can't be
/// written, nothing is submitted.</para>
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
        // As Python 0.1.1: the swap is retried (5 tries, about 2 seconds) when a scanner holds the new file for a moment.
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

    /// <summary>record_submission(): mark a day as requested, before its request is submitted: the day's start, and when
    /// (to the second, local time). Returns the day's entry as it was (null if it had none), for UndoSubmission. If this
    /// throws, don't submit.</summary>
    public static JsonNode? RecordSubmission(string path, DateOnly day, string start, DateTime now)
    {
        var state = Load(path);
        var key = PyTime.Iso(day);
        var before = state[key]?.DeepClone();
        if (state[key] is not JsonObject entry)
            state[key] = entry = [];
        entry["start"] = start;
        entry["submitted"] = PyTime.Iso(now, "seconds");
        Save(path, state);
        return before;
    }

    /// <summary>undo_submission(): the request wasn't submitted after all, so put the day back as it was, and a later run
    /// can try again.</summary>
    public static void UndoSubmission(string path, DateOnly day, JsonNode? before)
    {
        var state = Load(path);
        var key = PyTime.Iso(day);
        if (before is null)
            state.Remove(key);
        else
            state[key] = before.DeepClone();
        Save(path, state);
    }
}
