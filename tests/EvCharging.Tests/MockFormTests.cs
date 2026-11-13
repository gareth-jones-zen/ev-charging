using System.Text.Json.Nodes;
using PyCompat;

namespace EvCharging.Tests;

/// <summary>The form filled over DevTools in a real headless Edge, against a local mock shaped like a Microsoft Form
/// (tests/mock-form/form.html), in a throwaway profile. <b>The real Forms URL is never opened</b>: even a preview
/// would overwrite GJ's saved draft. The submit door is LocalOnly, so a click can only ever land on a file:// page.</summary>
[Collection("Edge")]
public sealed class MockFormTests : IDisposable
{
    private readonly Folder folder = new();
    private readonly string mock;

    public MockFormTests()
    {
        mock = Path.Combine(folder.Path, "form.html");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "mock-form", "form.html"), mock);
        var s = folder.Settings;
        s.Form.Url = new Uri(mock).AbsoluteUri;
        s.Form.Questions = 6;
        s.Form.Fields =
        [
            new FormField(1, "date", "{date:%d/%m/%Y}"),
            new FormField(2, "choice", "{start}"),
            new FormField(3, "text", "{car_make}"),
            new FormField(4, "text", "{car_model}"),
            new FormField(5, "text", "{car_registration}"),
        ];
        s.EdgeProfile = Path.Combine(folder.Path, "edge-profile");
        s.Screenshots = Path.Combine(folder.Path, "shots");
        File.WriteAllText(s.CarFile, """{"car_make": "Mäke", "car_model": "Model 3", "car_registration": "AB12 CDE"}""");
    }

    public void Dispose() => folder.Dispose();

    private static readonly FormOptions Quick = new()
    {
        LoadTimeout = TimeSpan.FromSeconds(30),
        ActionTimeout = TimeSpan.FromSeconds(10),
        ConfirmTimeout = TimeSpan.FromSeconds(10),
    };

    private static JsonObject Submitted(string text)
    {
        const string marker = "Your response was submitted. ";
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "no confirmation on the page");
        return (JsonObject)JsonNode.Parse(text[(at + marker.Length)..].Trim())!;
    }

    [Fact]
    public void Fills_every_kind_of_field_and_submits_on_a_local_page()
    {
        string? text = null;
        var door = new SubmitDoor(SubmitMode.LocalOnly);
        var outcome = Form.Book(folder.Settings, new DateOnly(2026, 11, 24), door, submit: true, headless: true, start: "08:00",
            Quick with { PageText = t => text = t });
        Assert.Equal("Submitted: Tue 24 Nov 2026, 08:00.", outcome);
        var page = Submitted(text!);
        Assert.Equal("""{"q1": "24/11/2026", "q2": "08:00", "q3": "Mäke", "q4": "Model 3", "q5": "AB12 CDE", "q6": ""}""",
            PyJson.Dump(page["model"], ensureAscii: false));
        Assert.Equal("[]", PyJson.Dump(page["log"]));   // no untrusted input, and the picker swallowed nothing
        Assert.True(File.Exists(Path.Combine(folder.Settings.Screenshots, "2026-11-24-submitted.png")));
        Assert.True(new FileInfo(Path.Combine(folder.Settings.Screenshots, "2026-11-24-submitted.png")).Length > 1000);
        Assert.Single(door.Asked);
    }

    [Fact]
    public void A_preview_fills_and_screenshots_but_never_reaches_the_door()
    {
        string? text = null;
        var door = new SubmitDoor(SubmitMode.LocalOnly);
        var outcome = Form.Book(folder.Settings, new DateOnly(2026, 11, 24), door, submit: false, headless: true, start: "08:30",
            Quick with { PageText = t => text = t });
        var shot = Path.Combine(folder.Settings.Screenshots, "2026-11-24-preview.png");
        Assert.Equal($"Preview only, nothing submitted: {shot}", outcome);
        Assert.True(File.Exists(shot));
        Assert.DoesNotContain("Your response was submitted", text);
        Assert.Empty(door.Asked);
    }

    [Fact]
    public void A_refusing_door_fills_the_form_and_clicks_nothing()
    {
        string? text = null;
        var outcome = Form.Book(folder.Settings, new DateOnly(2026, 11, 24), SubmitDoor.Refusing, submit: true, headless: true, start: "08:00",
            Quick with { PageText = t => text = t });
        Assert.StartsWith("Not submitted: this run may not click Submit.", outcome);
        Assert.DoesNotContain("Your response was submitted", text);
    }

    [Fact]
    public void A_changed_form_is_not_filled()
    {
        folder.Settings.Form.Questions = 7;
        var error = Assert.Throws<FormError>(() =>
            Form.Book(folder.Settings, new DateOnly(2026, 11, 24), new SubmitDoor(SubmitMode.LocalOnly), submit: true, headless: true, start: "08:00", Quick));
        Assert.Equal("Expected 7 questions, found 6. The form has changed; not filling it.", error.Message);
    }

    [Fact]
    public void An_option_that_is_not_in_the_list_is_a_form_error()
    {
        var error = Assert.Throws<FormError>(() =>
            Form.Book(folder.Settings, new DateOnly(2026, 11, 24), new SubmitDoor(SubmitMode.LocalOnly), submit: true, headless: true, start: "09:45",
                Quick with { ActionTimeout = TimeSpan.FromSeconds(2) }));
        Assert.Contains("No option called 09:45", error.Message);
    }

    [Fact]
    public void A_busy_profile_waits_then_gives_up()
    {
        using var other = new WinBackground.NamedLock(Form.ProfileLockName(folder.Settings.EdgeProfile));
        Assert.True(other.Acquire(0));
        var error = Assert.Throws<FormError>(() =>
            Form.Book(folder.Settings, new DateOnly(2026, 11, 24), SubmitDoor.Refusing, submit: false, headless: true, start: "08:00",
                Quick with { ProfileWait = TimeSpan.FromSeconds(1) }));
        Assert.StartsWith("The Edge profile is busy", error.Message);
    }
}
