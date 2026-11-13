using System.Diagnostics;
using System.Text.Json;
using EdgeDevTools;

namespace EvCharging;

/// <summary>
/// The handful of things the form needs from a page, as DevTools commands: go to an address, wait for a selector,
/// click (a real mouse click at the element's centre, after scrolling it into view), type (focus, select all, then
/// Input.insertText, so the page's input events fire as they do for a person), press Escape, read an attribute,
/// find an element by role and accessible name, wait for text, and screenshot the whole page.
/// <para>Kept in the tool, not the library: EdgeDevTools deliberately never types into a page. A candidate to move
/// there as an opt-in later.</para>
/// <para>Elements are named by JavaScript expressions built here from fixed selectors (<see cref="Nth"/>,
/// <see cref="Within"/>); values from the settings only ever go into a script as JSON string literals.</para>
/// </summary>
public sealed class FormPage
{
    private readonly EdgeBrowser browser;
    private readonly EdgeTarget page;
    private readonly FormOptions options;

    private FormPage(EdgeBrowser browser, EdgeTarget page, FormOptions options)
    {
        this.browser = browser;
        this.page = page;
        this.options = options;
    }

    /// <summary>Start Edge on the profile, give the work a page, and close Edge however the work ends.</summary>
    public static string Run(string profile, bool headless, FormOptions options, Func<FormPage, Task<string>> work) =>
        RunAsync(profile, headless, options, work).GetAwaiter().GetResult();

    private static async Task<string> RunAsync(string profile, bool headless, FormOptions options, Func<FormPage, Task<string>> work)
    {
        var browser = await EdgeBrowser.LaunchAsync(new EdgeLaunchOptions
        {
            UserDataDir = profile,
            Headless = headless,
            ExecutablePath = options.EdgeExecutable,
        }).ConfigureAwait(false);
        browser.Log = options.Log;
        try
        {
            var target = await browser.NewPageAsync("about:blank").ConfigureAwait(false);
            return await work(new FormPage(browser, target, options)).ConfigureAwait(false);
        }
        finally
        {
            await browser.CloseAsync().ConfigureAwait(false);
        }
    }

    // ---- element expressions ----------------------------------------------------------------------------------

    /// <summary>The nth (0-based) element matching a selector, or null.</summary>
    public static string Nth(string selector, int index) =>
        $"(document.querySelectorAll({PageScripts.JsonString(selector)})[{index}] || null)";

    /// <summary>The first element matching a selector inside another, or null.</summary>
    public static string Within(string element, string selector) =>
        $"((e => e ? e.querySelector({PageScripts.JsonString(selector)}) : null)({element}))";

    // ---- commands ---------------------------------------------------------------------------------------------

    private CdpConnection Cdp => browser.Connection;

    private Task<JsonElement> Eval(string script) => browser.EvaluateAsync(page, script);

    /// <summary>page.goto(url, wait_until="domcontentloaded"): starts the navigation; the waits that follow poll.</summary>
    public async Task GoAsync(string url)
    {
        await Cdp.SendAsync("Page.bringToFront", sessionId: page.SessionId).ConfigureAwait(false);
        await browser.NavigateAsync(page, url).ConfigureAwait(false);
    }

    public async Task<string> UrlAsync() => (await Eval("location.href").ConfigureAwait(false)).GetString() ?? "";

    /// <summary>The page's visible text.</summary>
    public async Task<string> TextAsync() =>
        (await Eval("(document.body && document.body.innerText) || ''").ConfigureAwait(false)).GetString() ?? "";

    public Task WaitAsync(int milliseconds) => Task.Delay(milliseconds);

    public async Task<int> CountAsync(string selector) =>
        (await Eval($"document.querySelectorAll({PageScripts.JsonString(selector)}).length").ConfigureAwait(false)).GetInt32();

    /// <summary>wait_for_selector(selector, timeout): true once an element matching it is visible. Headless, a Microsoft
    /// page that needs a person (a password, a code, Windows Hello) ends the wait at once with a FormError: nobody is
    /// there to answer it.</summary>
    public async Task<bool> WaitForQuestionsAsync(string selector, TimeSpan timeout, string? account, bool stopForPerson)
    {
        var visible = $$"""
            Array.from(document.querySelectorAll({{PageScripts.JsonString(selector)}})).some(e => {
              const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; })
            """;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            try
            {
                if ((await Eval(visible).ConfigureAwait(false)).ValueKind == JsonValueKind.True)
                    return true;
                if (stopForPerson && MicrosoftSignIn.Parse(await Eval(MicrosoftSignIn.ProbeScript(null, account)).ConfigureAwait(false)) is { } probe)
                {
                    var kind = MicrosoftSignIn.Classify(probe);
                    if (MicrosoftSignIn.NeedsAPerson(kind))
                        throw new FormError($"The form needs a sign-in ({MicrosoftSignIn.Describe(kind)}), and nobody is at this "
                                            + "window. Run `ev-charging sign-in` at a console.");
                }
            }
            catch (CdpException)
            {
                // between documents: look again
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>The element's centre, scrolled into view first, or null if it isn't there or has no size.</summary>
    private async Task<(double X, double Y)?> CentreAsync(string element)
    {
        var found = await Eval($$"""
            ((e) => { if (!e) return null; e.scrollIntoView({block: 'center', inline: 'center'});
              const r = e.getBoundingClientRect(); if (r.width <= 0 || r.height <= 0) return null;
              return [r.x + r.width / 2, r.y + r.height / 2]; })({{element}})
            """).ConfigureAwait(false);
        return PageScripts.Point(found);
    }

    /// <summary>locator.click(): waits (up to the action timeout) for the element, then clicks its centre.</summary>
    public async Task ClickAsync(string element)
    {
        var point = await WaitForAsync(() => CentreAsync(element), $"the element to click ({element})").ConfigureAwait(false);
        await browser.ClickAsync(page, point.X, point.Y).ConfigureAwait(false);
    }

    /// <summary>locator.fill(text): focus, select what's there, and insert the text as typing does (input events
    /// fire).</summary>
    public async Task FillAsync(string element, string text)
    {
        await WaitForAsync(() => CentreAsync(element), $"the box to fill ({element})").ConfigureAwait(false);
        var focused = await Eval($$"""
            ((e) => { if (!e) return false; e.focus(); if (typeof e.select === 'function') e.select();
              else { const s = getSelection(); s.removeAllRanges(); const r = document.createRange();
                     r.selectNodeContents(e); s.addRange(r); }
              return document.activeElement === e; })({{element}})
            """).ConfigureAwait(false);
        if (focused.ValueKind != JsonValueKind.True)
            throw new FormError("A box on the form couldn't be focused to type into.");
        if (text.Length == 0)
            await KeyAsync("Delete", "Delete", 46).ConfigureAwait(false);
        else
            await Cdp.SendAsync("Input.insertText", w => w.WriteString("text", text), page.SessionId).ConfigureAwait(false);
    }

    /// <summary>keyboard.press("Escape").</summary>
    public Task PressEscapeAsync() => KeyAsync("Escape", "Escape", 27);

    private async Task KeyAsync(string key, string code, int virtualKey)
    {
        foreach (var type in new[] { "keyDown", "keyUp" })
            await Cdp.SendAsync("Input.dispatchKeyEvent", w =>
            {
                w.WriteString("type", type);
                w.WriteString("key", key);
                w.WriteString("code", code);
                w.WriteNumber("windowsVirtualKeyCode", virtualKey);
                w.WriteNumber("nativeVirtualKeyCode", virtualKey);
            }, page.SessionId).ConfigureAwait(false);
    }

    /// <summary>Scroll the page, and anything scrolled inside it, back to the top. Forms scrolls inside its own panel, so a
    /// "full page" screenshot is only what's in view; without this a preview showed the last field filled, not the date
    /// and time (13 Nov 2026).</summary>
    public async Task ScrollToTopAsync()
    {
        await Eval("(() => { window.scrollTo(0, 0); for (const e of document.querySelectorAll('*')) if (e.scrollTop > 0) e.scrollTop = 0; return true; })()")
            .ConfigureAwait(false);
        await Task.Delay(300).ConfigureAwait(false);
    }

    /// <summary>locator.get_attribute(name): null if the element or the attribute isn't there.</summary>
    public async Task<string?> AttributeAsync(string element, string name)
    {
        await WaitForAsync(() => CentreAsync(element), $"the element ({element})").ConfigureAwait(false);
        var value = await Eval($"((e) => e ? e.getAttribute({PageScripts.JsonString(name)}) : null)({element})").ConfigureAwait(false);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>page.get_by_role(role, name=name, exact=exact).click(): the one visible element with that role (an
    /// explicit role, or the native element that has it) whose accessible name is the name (exact: the whole name,
    /// case-sensitive; otherwise contained in it, ignoring case), whitespace collapsed as Playwright does. More than
    /// one is a FormError (Playwright's strict mode); none after the action timeout is a FormError too.</summary>
    public async Task ClickRoleAsync(string role, string name, bool exact, TimeSpan timeout)
    {
        var script = $$"""
            ((role, name, exact) => {
              const norm = s => (s || '').replace(/\s+/g, ' ').trim();
              const native = { option: 'option', button: 'button, input[type=button], input[type=submit], input[type=reset]' };
              const sel = '[role="' + role + '"]' + (native[role] ? ', ' + native[role] : '');
              const label = e => {
                if (e.getAttribute('aria-label')) return e.getAttribute('aria-label');
                const by = e.getAttribute('aria-labelledby');
                if (by) return by.split(/\s+/).map(id => { const t = document.getElementById(id); return t ? t.textContent : ''; }).join(' ');
                if (e.tagName === 'INPUT') return e.value;
                return e.textContent;
              };
              const want = norm(name);
              const hits = Array.from(document.querySelectorAll(sel)).filter(e => {
                const r = e.getBoundingClientRect(); if (r.width <= 0 || r.height <= 0) return false;
                const n = norm(label(e));
                return exact ? n === want : n.toLowerCase().includes(want.toLowerCase());
              });
              if (hits.length !== 1) return hits.length;
              hits[0].scrollIntoView({block: 'center', inline: 'center'});
              const r = hits[0].getBoundingClientRect();
              return [r.x + r.width / 2, r.y + r.height / 2];
            })({{PageScripts.JsonString(role)}}, {{PageScripts.JsonString(name)}}, {{(exact ? "true" : "false")}})
            """;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var found = await Eval(script).ConfigureAwait(false);
            if (PageScripts.Point(found) is { } point)
            {
                await browser.ClickAsync(page, point.X, point.Y).ConfigureAwait(false);
                return;
            }
            if (found.ValueKind == JsonValueKind.Number && found.GetInt32() > 1)
                throw new FormError($"{found.GetInt32()} {role}s on the form are called {name}; not choosing between them.");
            if (clock.Elapsed >= timeout)
                throw new FormError($"No {role} called {name} appeared on the form within {timeout.TotalSeconds:0} seconds.");
            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    /// <summary>wait_for_selector("text=/pattern/i"): true once the page's text matches the pattern (as a JavaScript
    /// regular expression, ignoring case, as Playwright's selector read it).</summary>
    public async Task<bool> WaitForTextAsync(string pattern, TimeSpan timeout)
    {
        var script = $"new RegExp({PageScripts.JsonString(pattern)}, 'i').test(((document.body && document.body.innerText) || '').replace(/\\s+/g, ' '))";
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            try
            {
                if ((await Eval(script).ConfigureAwait(false)).ValueKind == JsonValueKind.True)
                    return true;
            }
            catch (CdpException)
            {
                // between documents (a form can navigate on submit)
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>page.screenshot(path, full_page=True): the whole page, as PNG.</summary>
    public async Task ScreenshotAsync(string path)
    {
        var metrics = await Cdp.SendAsync("Page.getLayoutMetrics", sessionId: page.SessionId).ConfigureAwait(false);
        var size = metrics.TryGetProperty("cssContentSize", out var css) ? css : metrics.GetProperty("contentSize");
        var width = Math.Max(1, Math.Ceiling(size.GetProperty("width").GetDouble()));
        var height = Math.Max(1, Math.Ceiling(size.GetProperty("height").GetDouble()));
        var shot = await Cdp.SendAsync("Page.captureScreenshot", w =>
        {
            w.WriteString("format", "png");
            w.WriteBoolean("captureBeyondViewport", true);
            w.WritePropertyName("clip");
            w.WriteStartObject();
            w.WriteNumber("x", 0);
            w.WriteNumber("y", 0);
            w.WriteNumber("width", width);
            w.WriteNumber("height", height);
            w.WriteNumber("scale", 1);
            w.WriteEndObject();
        }, page.SessionId, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(shot.GetProperty("data").GetString()!)).ConfigureAwait(false);
    }

    private async Task<(double X, double Y)> WaitForAsync(Func<Task<(double X, double Y)?>> find, string what)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (await find().ConfigureAwait(false) is { } point)
                    return point;
            }
            catch (CdpException)
            {
                // between documents
            }
            if (clock.Elapsed >= options.ActionTimeout)
                throw new FormError($"The form changed: {what} didn't appear within {options.ActionTimeout.TotalSeconds:0} seconds.");
            await Task.Delay(250).ConfigureAwait(false);
        }
    }
}
