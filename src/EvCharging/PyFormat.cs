using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PyCompat;

namespace EvCharging;

/// <summary>
/// Python's <c>str.format(**values)</c>, for the templates in the settings: the form's field values
/// (<c>{date:%d/%m/%Y}</c>, <c>{start}</c>, <c>{car_make}</c>) and the renamed entry (<c>{point}</c>,
/// <c>{start}</c>, <c>{end}</c>, <c>{date:...}</c>).
/// <para>Supported, exactly as Python: <c>{{</c> and <c>}}</c>; <c>{name}</c>; <c>{name:spec}</c>, where a date's
/// spec is strftime (PyCompat's directives) and a text's is <c>[[fill]align][width][.precision]</c>. A missing name
/// is a <see cref="PyKeyError"/>, as Python's KeyError; a malformed template is a <see cref="PyValueError"/> with
/// Python's message. Not supported (refused when the settings load, by <see cref="Check"/>): conversions
/// (<c>!r</c>), attribute and index lookups, positional fields, nested fields, a strftime directive PyCompat
/// doesn't have, and number formats.</para>
/// </summary>
public static class PyFormat
{
    /// <summary>A value to put in a template: a date (formatted with strftime, or ISO when there's no spec) or text.</summary>
    public readonly record struct Value(DateOnly? Date, string? Text)
    {
        public static implicit operator Value(string text) => new(null, text);

        public static implicit operator Value(DateOnly date) => new(date, null);
    }

    /// <summary>str(value) of a value from the car file (JSON): text as it is, numbers and the rest as Python prints them.</summary>
    public static Value FromJson(JsonNode? node) => new(null, Py.Str(node));

    /// <summary>template.format(**values).</summary>
    public static string Format(string template, IReadOnlyDictionary<string, Value> values)
    {
        var b = new StringBuilder();
        foreach (var part in Parse(template))
        {
            if (part.Field is null)
            {
                b.Append(part.Literal);
                continue;
            }
            if (!values.TryGetValue(part.Field, out var value))
                throw new PyKeyError(part.Field);
            b.Append(Apply(value, part.Spec));
        }
        return b.ToString();
    }

    /// <summary>Refuses, when the settings load, a template that this port would format differently from Python
    /// (a malformed one is left to fail as Python's does, when it's used).</summary>
    public static void Check(string template, string where)
    {
        List<Part> parts;
        try
        {
            parts = Parse(template);
        }
        catch (PyValueError)
        {
            return;   // Python fails at the same point, with the same message, when it's used
        }
        foreach (var part in parts)
        {
            if (part.Field is null)
                continue;
            if (part.Field.Length == 0 || char.IsAsciiDigit(part.Field[0]) || part.Field.IndexOfAny(['.', '[', '!']) >= 0
                || part.Spec.Contains('{'))
                throw new PyValueError($"{where} uses {{{part.Field}}}: only {{name}} and {{name:format}} are supported.");
            if (part.Field == "date" && part.Spec.Length > 0 && !PyTime.Supported(part.Spec))
                throw new PyValueError($"{where} formats the date as {Py.ReprString(part.Spec)}: only the strftime codes %a %A %b %B %d %m %y %Y %H %I %M %S %p %% are supported.");
            if (part.Field != "date" && part.Spec.Length > 0 && TextSpec(part.Spec) is null)
                throw new PyValueError($"{where} formats {{{part.Field}}} as {Py.ReprString(part.Spec)}: only fill, alignment, width and precision are supported for text.");
        }
    }

    private sealed record Part(string Literal, string? Field, string Spec);

    /// <summary>string.Formatter's parse, with Python's messages for the mistakes.</summary>
    private static List<Part> Parse(string template)
    {
        var parts = new List<Part>();
        var literal = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    literal.Append('{');
                    i += 2;
                    continue;
                }
                if (i + 1 == template.Length)
                    throw new PyValueError("Single '{' encountered in format string");
                // A field: up to the matching '}', counting nested braces as Python does.
                var depth = 1;
                var j = i + 1;
                while (j < template.Length && depth > 0)
                {
                    if (template[j] == '{')
                        depth++;
                    else if (template[j] == '}')
                        depth--;
                    if (depth > 0)
                        j++;
                }
                if (depth > 0)
                    throw new PyValueError("expected '}' before end of string");
                var inside = template[(i + 1)..j];
                var colon = FieldEnd(inside);
                var name = inside[..colon];
                var spec = colon < inside.Length ? inside[(colon + 1)..] : "";
                var bang = name.IndexOf('!');
                if (bang >= 0)
                {
                    var conversion = name[(bang + 1)..];
                    if (conversion.Length != 1)
                        throw new PyValueError("expected ':' after conversion specifier");
                }
                parts.Add(new Part(literal.ToString(), null, ""));
                literal.Clear();
                parts.Add(new Part("", name, spec));
                i = j + 1;
                continue;
            }
            if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    literal.Append('}');
                    i += 2;
                    continue;
                }
                throw new PyValueError("Single '}' encountered in format string");
            }
            literal.Append(c);
            i++;
        }
        if (literal.Length > 0)
            parts.Add(new Part(literal.ToString(), null, ""));
        return parts;
    }

    /// <summary>Where the field name ends: the first ':' outside brackets.</summary>
    private static int FieldEnd(string inside)
    {
        var bracket = false;
        for (var k = 0; k < inside.Length; k++)
        {
            if (inside[k] == '[')
                bracket = true;
            else if (inside[k] == ']')
                bracket = false;
            else if (inside[k] == ':' && !bracket)
                return k;
        }
        return inside.Length;
    }

    private static string Apply(Value value, string spec)
    {
        if (value.Date is { } date)
            return spec.Length == 0 ? PyTime.Iso(date) : PyTime.Strftime(date, spec);
        var text = value.Text ?? "None";
        if (spec.Length == 0)
            return text;
        var parsed = TextSpec(spec) ?? throw new PyValueError($"Format specifier missing precision");
        if (parsed.Precision is { } precision)
            text = PyStr.Head(text, precision);
        var length = PyStr.Length(text);
        if (parsed.Width is not { } width || width <= length)
            return text;
        var pad = width - length;
        var fill = parsed.Fill;
        return parsed.Align switch
        {
            '>' => Repeat(fill, pad) + text,
            '^' => Repeat(fill, pad / 2) + text + Repeat(fill, pad - pad / 2),
            _ => text + Repeat(fill, pad),
        };
    }

    private static string Repeat(string fill, int count) => string.Concat(Enumerable.Repeat(fill, count));

    private sealed record Spec(string Fill, char Align, int? Width, int? Precision);

    /// <summary>[[fill]align][width][.precision][s] for text; null for anything else.</summary>
    private static Spec? TextSpec(string spec)
    {
        var runes = spec.EnumerateRunes().Select(r => r.ToString()).ToList();
        var i = 0;
        var fill = " ";
        var align = '<';
        if (runes.Count >= 2 && runes[1] is "<" or ">" or "^")
        {
            fill = runes[0];
            align = runes[1][0];
            i = 2;
        }
        else if (runes.Count >= 1 && runes[0] is "<" or ">" or "^")
        {
            align = runes[0][0];
            i = 1;
        }
        int? width = null, precision = null;
        var digits = new StringBuilder();
        while (i < runes.Count && runes[i].Length == 1 && char.IsAsciiDigit(runes[i][0]))
            digits.Append(runes[i++]);
        if (digits.Length > 0)
        {
            if (digits[0] == '0' && digits.Length > 1)
                return null;   // '0' as fill-and-zero-pad means something for numbers only
            width = int.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        }
        if (i < runes.Count && runes[i] == ".")
        {
            i++;
            digits.Clear();
            while (i < runes.Count && runes[i].Length == 1 && char.IsAsciiDigit(runes[i][0]))
                digits.Append(runes[i++]);
            if (digits.Length == 0)
                return null;
            precision = int.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        }
        if (i < runes.Count && runes[i] == "s")
            i++;
        return i == runes.Count ? new Spec(fill, align, width, precision) : null;
    }
}
