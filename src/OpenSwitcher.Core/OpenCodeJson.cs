using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenSwitcher.Core;

internal static class OpenCodeJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static JsonObject ParseRoot(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The OpenCode configuration is not valid JSON (line {exception.LineNumber + 1}).");
        }

        return root as JsonObject
            ?? throw new InvalidDataException("The OpenCode configuration must contain a JSON object.");
    }

    public static JsonObject? GetObject(JsonObject root, IReadOnlyList<string> path)
    {
        var current = root;
        for (var index = 0; index < path.Count; index++)
        {
            var node = current[path[index]];
            if (node is null)
            {
                return null;
            }

            current = node as JsonObject
                ?? throw new InvalidDataException($"'{string.Join('.', path.Take(index + 1))}' must be a JSON object.");
        }

        return current;
    }

    public static string? GetString(JsonObject parent, string propertyName) =>
        parent[propertyName] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static McpServerDefinition ReadMcpServer(string name, JsonNode? node)
    {
        if (node is not JsonObject entry)
        {
            throw new InvalidDataException($"'mcp.{name}' must be a JSON object.");
        }

        var type = GetString(entry, "type") == "remote" ? McpServerType.Remote : McpServerType.Local;
        var command = entry["command"] is JsonArray commandArray
            ? commandArray.Where(item => item is not null).Select(item => ToText(item!)).ToArray()
            : [];
        var enabled = entry["enabled"] is not JsonValue enabledValue ||
            !enabledValue.TryGetValue<bool>(out var isEnabled) ||
            isEnabled;

        return new McpServerDefinition(
            name,
            type,
            command,
            ReadStringMap(entry["environment"]),
            GetString(entry, "url"),
            ReadStringMap(entry["headers"]),
            enabled,
            ReadTimeout(entry["timeout"]));
    }

    public static JsonObject CreateMcpEntry(McpServerDefinition server)
    {
        var entry = new JsonObject
        {
            ["type"] = server.Type == McpServerType.Local ? "local" : "remote"
        };

        if (server.Type == McpServerType.Local)
        {
            entry["command"] = CreateArray(server.Command);
            if (server.Environment.Count > 0)
            {
                entry["environment"] = CreateMap(server.Environment);
            }
        }
        else
        {
            entry["url"] = server.Url;
            if (server.Headers.Count > 0)
            {
                entry["headers"] = CreateMap(server.Headers);
            }
        }

        entry["enabled"] = server.Enabled;
        if (server.Timeout is int timeout)
        {
            entry["timeout"] = timeout;
        }

        return entry;
    }

    public static JsonArray CreateArray(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)value).ToArray());

    public static JsonObject CreateMap(IReadOnlyDictionary<string, string> values)
    {
        var map = new JsonObject();
        foreach (var item in values)
        {
            map[item.Key] = item.Value;
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonNode? node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is not JsonObject values)
        {
            return map;
        }

        foreach (var item in values)
        {
            if (item.Value is not null)
            {
                map[item.Key] = ToText(item.Value);
            }
        }

        return map;
    }

    private static int? ReadTimeout(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var milliseconds))
        {
            return milliseconds;
        }

        return value.TryGetValue<double>(out var fractional) && fractional is > 0 and <= int.MaxValue
            ? (int)Math.Round(fractional, MidpointRounding.AwayFromZero)
            : null;
    }

    private static string ToText(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString();
}
