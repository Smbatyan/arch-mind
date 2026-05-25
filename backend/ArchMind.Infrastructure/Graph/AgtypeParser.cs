using System.Globalization;
using System.Text.Json;

namespace ArchMind.Infrastructure.Graph;

/// <summary>
/// Parses Apache AGE <c>agtype</c> values returned over Npgsql as <see cref="string"/>.
///
/// AGE renders vertices, edges, and paths as JSON-like text with a trailing
/// type tag, e.g.:
/// <code>
/// {"id": 281474976710657, "label": "Service", "properties": {"name": "billing"}}::vertex
/// {"id": ..., "label": "EXPOSES", "start_id": ..., "end_id": ..., "properties": {}}::edge
/// </code>
/// Scalar agtype values (strings, numbers, booleans, null) come back without a
/// tag and parse as plain JSON.
///
/// This helper:
/// <list type="bullet">
///   <item>Strips the <c>::vertex|::edge|::path</c> suffix (if any).</item>
///   <item>Parses the remainder via <see cref="JsonDocument"/>.</item>
///   <item>Exposes shape predicates (<see cref="IsVertex"/>, <see cref="IsEdge"/>).</item>
/// </list>
///
/// NOTE: AGE numeric literals beyond <see cref="long.MaxValue"/> get a trailing
/// <c>::numeric</c> suffix per-property; we do NOT pre-process those here —
/// callers should treat any unrecognised <c>::tag</c> on a property value as an
/// opaque string. None of the properties IGraphReader cares about (workspace_id,
/// name, etc.) are bigints, so this is safe for MVP.
/// </summary>
internal static class AgtypeParser
{
    private const string VertexSuffix = "::vertex";
    private const string EdgeSuffix = "::edge";
    private const string PathSuffix = "::path";

    public static bool IsVertex(string? raw) =>
        raw is not null && raw.EndsWith(VertexSuffix, StringComparison.Ordinal);

    public static bool IsEdge(string? raw) =>
        raw is not null && raw.EndsWith(EdgeSuffix, StringComparison.Ordinal);

    public static bool IsPath(string? raw) =>
        raw is not null && raw.EndsWith(PathSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Parse a raw agtype string into a <see cref="JsonDocument"/>. Caller owns
    /// disposal. Returns <c>null</c> if <paramref name="raw"/> is null or the
    /// AGE literal <c>"null"</c>.
    /// </summary>
    public static JsonDocument? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = StripTypeTag(raw);
        if (trimmed.Equals("null", StringComparison.Ordinal))
        {
            return null;
        }

        return JsonDocument.Parse(trimmed);
    }

    /// <summary>
    /// Extract the <c>properties</c> object from a vertex/edge agtype literal.
    /// Returns an empty dictionary if there are none (or input is null).
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ExtractProperties(string? rawAgtype)
    {
        using var doc = Parse(rawAgtype);
        if (doc is null)
        {
            return EmptyProps;
        }

        if (!doc.RootElement.TryGetProperty("properties", out var props) ||
            props.ValueKind != JsonValueKind.Object)
        {
            return EmptyProps;
        }

        return ToDictionary(props);
    }

    /// <summary>
    /// Read the <c>label</c> field from a vertex/edge literal, or null if absent.
    /// </summary>
    public static string? ExtractLabel(string? rawAgtype)
    {
        using var doc = Parse(rawAgtype);
        if (doc is null) return null;
        return doc.RootElement.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString()
            : null;
    }

    /// <summary>
    /// Convert a JSON value coming from agtype into a CLR <see cref="object"/>
    /// graph using the same shape <see cref="JsonElement"/> would expose:
    /// strings → string, numbers → long/double, booleans → bool, arrays →
    /// <c>object?[]</c>, objects → <c>Dictionary&lt;string, object?&gt;</c>.
    /// </summary>
    public static object? JsonValueToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var i)
                ? i
                : element.GetDouble(),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(JsonValueToObject)
                .ToArray(),
            JsonValueKind.Object => ToDictionary(element),
            _ => element.GetRawText(),
        };
    }

    /// <summary>
    /// Try to read a Guid out of an agtype-derived <see cref="JsonElement"/>.
    /// AGE stores Guids as strings in property bags.
    /// </summary>
    public static bool TryGetGuid(JsonElement element, out Guid value)
    {
        if (element.ValueKind == JsonValueKind.String &&
            Guid.TryParse(element.GetString(), out var g))
        {
            value = g;
            return true;
        }

        value = Guid.Empty;
        return false;
    }

    public static string? GetString(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        return parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    public static Guid? GetGuid(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(name, out var v)) return null;
        return TryGetGuid(v, out var g) ? g : null;
    }

    public static IReadOnlyList<string> GetStringArray(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        if (!parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (s is not null) list.Add(s);
            }
        }
        return list;
    }

    private static string StripTypeTag(string raw)
    {
        // Trim once for tolerance against AGE's pretty-printer.
        var s = raw.Trim();

        if (s.EndsWith(VertexSuffix, StringComparison.Ordinal))
            return s[..^VertexSuffix.Length];
        if (s.EndsWith(EdgeSuffix, StringComparison.Ordinal))
            return s[..^EdgeSuffix.Length];
        if (s.EndsWith(PathSuffix, StringComparison.Ordinal))
            return s[..^PathSuffix.Length];

        // Strip per-value tags like ::numeric / ::int8 if present on scalars.
        var idx = s.LastIndexOf("::", StringComparison.Ordinal);
        if (idx > 0 && idx < s.Length - 2 && s[(idx + 2)..].All(c => char.IsLetterOrDigit(c)))
        {
            return s[..idx];
        }

        return s;
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(JsonElement obj)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            dict[prop.Name] = JsonValueToObject(prop.Value);
        }
        return dict;
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyProps =
        new Dictionary<string, object?>(0);

    // Defensive culture-invariant double formatting helper (unused today but
    // handy when callers pass Cypher parameters through ToString()).
    public static string FormatDouble(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}
