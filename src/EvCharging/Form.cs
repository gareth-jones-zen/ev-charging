using System.Text.Json.Nodes;
using EdgeDevTools;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;
using WinBackground;

namespace EvCharging;

/// <summary>The form couldn't be filled (or the car details are missing): what Python's FormError was.</summary>
public sealed class FormError(string message) : Exception(message);

/// <summary>
/// Filling in and submitting the booking form (a Microsoft Form) in Edge. A port of form.py, driving Edge over its
/// DevTools pipe (background-tools-lib's EdgeDevTools) rather than Playwright.
/// <para>Nothing else can submit a Forms response: not Graph, not Power Automate, not the M365 connector. So this
/// drives Edge in its own profile (edge_profile), never your everyday browser. You sign in to Microsoft in that
/// profile once (`ev-charging sign-in`); the session persists, so later runs need nothing from you. It never handles a
/// password.</para>
/// <para><b>A submission is a request, not a booking</b>: someone approves it, and says so by email. And the Submit
/// click goes through <see cref="SubmitDoor"/>, which refuses unless the command line asked for a submission.</para>
/// <para>Two quirks of Microsoft Forms, both learned the hard way: <b>Forms keeps an unsubmitted draft for each
/// user</b>, so after a preview the fields come back filled in, and a dropdown shows its last answer rather than
/// "Select your answer" (so the dropdown is found by its role, never by its placeholder text); and <b>the date picker
/// swallows the next click</b> unless it's closed (Escape) first.</para>
/// </summary>
public static class Form
{
    public const string Question = "[data-automation-id='questionItem']";

    /// <summary>How long a booking waits for another tool to finish with the Edge profile before giving up.</summary>
    public static readonly TimeSpan ProfileWait = TimeSpan.FromMinutes(10);

    /// <summary>The one lock name for the Edge profile, shared with teams-transcripts' port: edge-profile-{key}, the key
    /// computed from the normalised path, so the two settings files' spellings of the folder give one lock.</summary>
    public static string ProfileLockName(string profile) => EdgeProfile.LockName("edge-profile", profile);

    /// <summary>car(config): the car's details (make, model, registration, anything the form asks), from a file
    /// outside any repo. TOML if it ends .toml, otherwise JSON.</summary>
    public static JsonObject Car(Settings settings)
    {
        var path = settings.CarFile;
        if (!File.Exists(path))
            throw new FormError($"No car details at {path}. See the README: they live in a file of their own.");
        var text = PyStr.ReadText(path);
        if (string.Equals(Path.GetExtension(path), ".toml", StringComparison.OrdinalIgnoreCase))
        {
            if (text.StartsWith('﻿'))
                throw new PyValueError("Invalid statement (at line 1, column 1)");
            return (JsonObject)FromToml(TomlSerializer.Deserialize(text, TomlContext.Default.TomlTable) ?? [])!;
        }
        // A file that isn't a JSON object can't be read for keys: Python failed later, with an AttributeError.
        return PyJson.Parse(text) as JsonObject ?? throw new FormError($"The car details at {path} aren't a JSON object of names and values.");
    }

    private static JsonNode? FromToml(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long l => PyJson.Int(l),
        double d => PyJson.Float(d),
        TomlTable t => new JsonObject(t.Select(kv => KeyValuePair.Create(kv.Key, FromToml(kv.Value)))),
        TomlArray a => new JsonArray(a.Select(FromToml).ToArray()),
        TomlTableArray a => new JsonArray(a.Select(FromToml).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>start_time(config, details): the settings' start time, else the car file's, else 08:00. A car file's
    /// start_time that isn't text is used as Python prints it.</summary>
    public static string StartTime(Settings settings, JsonObject? details = null)
    {
        if (settings.StartTime.Length > 0)
            return settings.StartTime;
        var fromCar = details?["start_time"];
        return Py.Truthy(fromCar) ? Py.Str(fromCar) : "08:00";
    }

    /// <summary>values(): (question, type, text) for each field to fill; a field whose text comes out empty (or only
    /// spaces) is skipped.</summary>
    public static List<(int Question, string Type, string Text)> Values(Settings settings, DateOnly day, string start, JsonObject details)
    {
        var filled = new List<(int, string, string)>();
        var values = new Dictionary<string, PyFormat.Value> { ["date"] = day, ["start"] = start };
        foreach (var (key, node) in details)
        {
            if (key is "date" or "start")
                throw new PyError("TypeError", $"str.format() got multiple values for keyword argument '{key}'");
            values[key] = PyFormat.FromJson(node);
        }
        foreach (var item in settings.Form.Fields)
        {
            string text;
            try
            {
                text = PyFormat.Format(item.Value, values);
            }
            catch (PyKeyError error)
            {
                throw new FormError($"Form question {item.Question} wants {error.Message} from the car file, which hasn't got it.");
            }
            if (PyStr.Strip(text).Length > 0)
                filled.Add((item.Question, item.Type, text));
        }
        return filled;
    }

    /// <summary>book(): fill the form for a day, screenshot it, and submit it if asked (through the door). Returns what
    /// happened, starting "Submitted" on success. Throws FormError if the form couldn't be filled.</summary>
    public static string Book(Settings settings, DateOnly day, SubmitDoor door, bool submit = false, bool headless = false,
        string? start = null, FormOptions? options = null)
    {
        options ??= new FormOptions();
        if (settings.Form.Url.Length == 0)
            throw new FormError("No [form] url in the settings.");
        var details = Car(settings);
        start ??= StartTime(settings, details);
        var fields = Values(settings, day, start, details);
        Directory.CreateDirectory(settings.Screenshots);
        using var profile = TakeProfile(settings.EdgeProfile, options.ProfileWait ?? ProfileWait);
        return FormPage.Run(settings.EdgeProfile, headless, options, async page =>
        {
            var outcome = await FillAndSubmitAsync(page).ConfigureAwait(false);
            if (options.PageText is { } seen)
                seen(await page.TextAsync().ConfigureAwait(false));
            return outcome;
        });

        async Task<string> FillAndSubmitAsync(FormPage page)
        {
            await page.GoAsync(settings.Form.Url).ConfigureAwait(false);
            if (!await page.WaitForQuestionsAsync(Question, options.LoadTimeout, headless ? options.SignInAccount ?? settings.LoginHint : null, headless).ConfigureAwait(false))
                throw new FormError("The form didn't load. Is the Microsoft sign-in still valid? Run `ev-charging sign-in` at a console.");
            var count = await page.CountAsync(Question).ConfigureAwait(false);
            if (settings.Form.Questions != 0 && count != settings.Form.Questions)
                throw new FormError($"Expected {settings.Form.Questions} questions, found {count}. The form has changed; not filling it.");
            foreach (var (number, kind, text) in fields)
            {
                var question = FormPage.Nth(Question, number - 1);
                if (kind == "date")
                {
                    var box = FormPage.Within(question, "input");
                    await page.ClickAsync(box).ConfigureAwait(false);
                    await page.FillAsync(box, text).ConfigureAwait(false);
                    await page.PressEscapeAsync().ConfigureAwait(false);   // close the date picker, or it swallows the next click
                    await page.WaitAsync(500).ConfigureAwait(false);
                }
                else if (kind == "choice")
                {
                    // By role, not by "Select your answer": a saved draft shows its last answer instead.
                    var dropdown = FormPage.Within(question, "[role='button'][aria-expanded]");
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        if (await page.AttributeAsync(dropdown, "aria-expanded").ConfigureAwait(false) == "true")
                            break;
                        await page.ClickAsync(dropdown).ConfigureAwait(false);
                        await page.WaitAsync(800).ConfigureAwait(false);
                    }
                    await page.ClickRoleAsync("option", text, exact: true, options.ActionTimeout).ConfigureAwait(false);
                }
                else
                {
                    await page.FillAsync(FormPage.Within(question, "input, textarea"), text).ConfigureAwait(false);
                }
            }
            await page.WaitAsync(1000).ConfigureAwait(false);
            var shot = Path.Combine(settings.Screenshots, $"{PyTime.Iso(day)}-{(submit ? "submitted" : "preview")}.png");
            await page.ScreenshotAsync(shot).ConfigureAwait(false);
            if (!submit)
                return $"Preview only, nothing submitted: {shot}";
            if (!door.MayClick(await page.UrlAsync().ConfigureAwait(false)))
                return $"Not submitted: {door.Refusal} {shot}";
            await page.ClickRoleAsync("button", "Submit", exact: false, options.ActionTimeout).ConfigureAwait(false);
            if (!await page.WaitForTextAsync(settings.Form.Confirmation, options.ConfirmTimeout).ConfigureAwait(false))
            {
                var after = Path.Combine(settings.Screenshots, $"{PyTime.Iso(day)}-after-submit.png");
                await page.ScreenshotAsync(after).ConfigureAwait(false);
                return $"Clicked Submit but didn't see a confirmation. Check {after}";
            }
            return $"Submitted: {PyTime.Strftime(day, "%a %d %b %Y")}, {start}.";
        }
    }

    /// <summary>sign_in_window(): open the form in the tool's Edge profile, visibly, and wait (up to 5 minutes) for a
    /// sign-in to finish. Only ever run by a person at the console: it opens the real form.</summary>
    public static void SignInWindow(Settings settings, FormOptions? options = null)
    {
        options ??= new FormOptions();
        if (settings.Form.Url.Length == 0)
            throw new FormError("No [form] url in the settings.");
        using var profile = TakeProfile(settings.EdgeProfile, options.ProfileWait ?? ProfileWait);
        FormPage.Run(settings.EdgeProfile, headless: false, options, async page =>
        {
            await page.GoAsync(settings.Form.Url).ConfigureAwait(false);
            if (!await page.WaitForQuestionsAsync(Question, options.LoadTimeout, null, stopForPerson: false).ConfigureAwait(false))
                throw new FormError("The form didn't load within 5 minutes.");
            return "";
        });
    }

    /// <summary>The Edge profile's lock, held while Edge has it open, so a transcript fetch and a booking take turns
    /// rather than one failing. Gives up (FormError) after the wait.</summary>
    internal static NamedLock TakeProfile(string profile, TimeSpan wait)
    {
        var held = new NamedLock(ProfileLockName(profile));
        try
        {
            if (held.Acquire(wait.TotalSeconds))
                return held;
        }
        catch
        {
            held.Dispose();
            throw;
        }
        held.Dispose();
        throw new FormError($"The Edge profile is busy: another tool has had it for {Math.Round(wait.TotalMinutes):0} minutes. Try again later.");
    }
}

/// <summary>Timings and test hooks for the form. The defaults are Python's.</summary>
public sealed record FormOptions
{
    /// <summary>How long to wait for the form's questions (time to sign in, in a visible window).</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A click or option that isn't there yet: Playwright's default action timeout.</summary>
    public TimeSpan ActionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan? ProfileWait { get; init; }

    /// <summary>The account a headless run looks for on Microsoft's pages (default: the settings' login_hint).</summary>
    public string? SignInAccount { get; init; }

    /// <summary>Edge's executable (tests: the installed one by default).</summary>
    public string? EdgeExecutable { get; init; }

    public Action<string>? Log { get; init; }

    /// <summary>Tests: the page's text once the form is done with (filled, and submitted or not).</summary>
    public Action<string>? PageText { get; init; }
}

/// <summary>How the form's Submit button may be clicked. <b>Refuse is the default</b>: Live is only for `run` (not a
/// dry run) and `book --submit`; tests and checks use Refuse, or LocalOnly, which clicks only on a local file:// page
/// (the mock form), never on the real one.</summary>
public enum SubmitMode
{
    Refuse,
    Live,
    LocalOnly,
}

public sealed class SubmitDoor(SubmitMode mode)
{
    public static SubmitDoor Refusing { get; } = new(SubmitMode.Refuse);

    public SubmitMode Mode { get; } = mode;

    /// <summary>Every page address a click was asked for, allowed or not (tests read it).</summary>
    public List<string> Asked { get; } = [];

    public string Refusal => Mode == SubmitMode.LocalOnly
        ? "this run may only click Submit on a local page (file://)."
        : "this run may not click Submit.";

    public bool MayClick(string pageUrl)
    {
        Asked.Add(pageUrl);
        return Mode switch
        {
            SubmitMode.Live => true,
            SubmitMode.LocalOnly => pageUrl.StartsWith("file:///", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
