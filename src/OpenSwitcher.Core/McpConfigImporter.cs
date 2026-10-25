using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace OpenSwitcher.Core;

public enum McpImportSource
{
    Codex,
    ClaudeCode,
    Cursor
}

public sealed record ImportedMcpServer(string Origin, McpServerDefinition Server);

public static partial class McpConfigImporter
{
    private static readonly JsonDocumentOptions JsonDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static string GetConfigPath(McpImportSource source)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return source switch
        {
            McpImportSource.Codex => Path.Combine(userProfile, ".codex", "config.toml"),
            McpImportSource.ClaudeCode => Path.Combine(userProfile, ".claude.json"),
            McpImportSource.Cursor => Path.Combine(userProfile, ".cursor", "mcp.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
    }

    public static Task<IReadOnlyList<ImportedMcpServer>> LoadAsync(
        McpImportSource source,
        CancellationToken cancellationToken = default) =>
        LoadAsync(source, GetConfigPath(source), cancellationToken);

    public static async Task<IReadOnlyList<ImportedMcpServer>> LoadAsync(
        McpImportSource source,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        var fullPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullPath))
        {
            return [];
        }

        var text = await File.ReadAllTextAsync(fullPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return source switch
        {
            McpImportSource.Codex => ReadCodex(text),
            McpImportSource.ClaudeCode => ReadClaudeCode(text),
            McpImportSource.Cursor => ReadCursor(text),
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
    }

    private static IReadOnlyList<ImportedMcpServer> ReadCodex(string text)
    {
        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(text)
                ?? throw new InvalidDataException("The Codex configuration is empty.");
        }
        catch (TomlException exception)
        {
            throw new InvalidDataException($"The Codex configuration is not valid TOML: {exception.Message}");
        }

        if (!root.TryGetValue("mcp_servers", out var serversValue))
        {
            return [];
        }

        if (serversValue is not TomlTable servers)
        {
            throw new InvalidDataException("'mcp_servers' must be a TOML table.");
        }

        return servers
            .Select(item => new ImportedMcpServer("Codex", ReadCodexServer(item.Key, item.Value)))
            .ToArray();
    }

    private static McpServerDefinition ReadCodexServer(string name, object value)
    {
        if (value is not TomlTable entry)
        {
            throw new InvalidDataException($"'mcp_servers.{name}' must be a TOML table.");
        }

        var enabled = !entry.TryGetValue("enabled", out var enabledValue) || enabledValue is not bool isEnabled || isEnabled;
        var timeout = GetCodexTimeout(name, entry);
        var url = GetTomlString(entry, "url");
        return url is not null
            ? CreateCodexRemoteServer(name, entry, url, enabled, timeout)
            : CreateCodexLocalServer(name, entry, enabled, timeout);
    }

    private static McpServerDefinition CreateCodexRemoteServer(
        string name,
        TomlTable entry,
        string url,
        bool enabled,
        int? timeout)
    {
        var headers = GetTomlStringMap(entry, "http_headers");
        foreach (var (header, variable) in GetTomlStringMap(entry, "env_http_headers"))
        {
            headers[header] = $"{{env:{variable}}}";
        }

        var bearerTokenVariable = GetTomlString(entry, "bearer_token_env_var");
        if (bearerTokenVariable is not null)
        {
            headers["Authorization"] = $"Bearer {{env:{bearerTokenVariable}}}";
        }

        return new McpServerDefinition(name, McpServerType.Remote, [], EmptyMap, url, headers, enabled, timeout);
    }

    private static McpServerDefinition CreateCodexLocalServer(
        string name,
        TomlTable entry,
        bool enabled,
        int? timeout)
    {
        var command = new List<string>();
        var program = GetTomlString(entry, "command");
        if (program is not null)
        {
            command.Add(program);
        }

        if (entry.TryGetValue("args", out var argsValue) && argsValue is TomlArray args)
        {
            command.AddRange(args.OfType<string>());
        }

        return new McpServerDefinition(
            name,
            McpServerType.Local,
            command,
            GetTomlStringMap(entry, "env"),
            null,
            EmptyMap,
            enabled,
            timeout);
    }

    private static int? GetCodexTimeout(string name, TomlTable entry)
    {
        double? milliseconds = null;
        if (entry.TryGetValue("startup_timeout_sec", out var seconds))
        {
            milliseconds = seconds switch
            {
                long value => value * 1000d,
                double value => value * 1000d,
                _ => null
            };
        }
        else if (entry.TryGetValue("startup_timeout_ms", out var timeout) && timeout is long value)
        {
            milliseconds = value;
        }

        if (milliseconds is null)
        {
            return null;
        }

        if (milliseconds is <= 0 or > int.MaxValue or double.NaN)
        {
            throw new InvalidDataException($"'mcp_servers.{name}': the startup timeout is out of range.");
        }

        return (int)Math.Round(milliseconds.Value, MidpointRounding.AwayFromZero);
    }

    private static string? GetTomlString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is string text ? text : null;

    private static Dictionary<string, string> GetTomlStringMap(TomlTable table, string key)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!table.TryGetValue(key, out var value) || value is not TomlTable values)
        {
            return map;
        }

        foreach (var (name, item) in values)
        {
            map[name] = item switch
            {
                string text => text,
                bool flag => flag ? "true" : "false",
                long number => number.ToString(CultureInfo.InvariantCulture),
                double number => number.ToString(CultureInfo.InvariantCulture),
                _ => throw new InvalidDataException($"'{key}.{name}' must be a string.")
            };
        }

        return map;
    }

    private static IReadOnlyList<ImportedMcpServer> ReadClaudeCode(string text)
    {
        var root = ParseJsonRoot(text, "Claude Code");
        var result = new List<ImportedMcpServer>();
        result.AddRange(ReadJsonServers(root["mcpServers"], "Claude Code (user)", ConvertClaudeVariables));

        if (root["projects"] is JsonObject projects)
        {
            foreach (var (projectPath, project) in projects)
            {
                if (project is JsonObject projectSettings)
                {
                    result.AddRange(ReadJsonServers(
                        projectSettings["mcpServers"],
                        $"Claude Code ({projectPath})",
                        ConvertClaudeVariables));
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<ImportedMcpServer> ReadCursor(string text)
    {
        var root = ParseJsonRoot(text, "Cursor");
        return ReadJsonServers(root["mcpServers"], "Cursor", ConvertCursorVariables);
    }

    private static JsonObject ParseJsonRoot(string text, string sourceName)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: JsonDocumentOptions) as JsonObject
                ?? throw new InvalidDataException($"The {sourceName} configuration must contain a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The {sourceName} configuration is not valid JSON (line {exception.LineNumber + 1}).");
        }
    }

    private static IReadOnlyList<ImportedMcpServer> ReadJsonServers(
        JsonNode? node,
        string origin,
        Func<string, string> convertVariables)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonObject servers)
        {
            throw new InvalidDataException($"{origin}: 'mcpServers' must be a JSON object.");
        }

        var result = new List<ImportedMcpServer>();
        foreach (var (name, value) in servers)
        {
            if (value is not JsonObject entry)
            {
                throw new InvalidDataException($"{origin}: 'mcpServers.{name}' must be a JSON object.");
            }

            var url = GetJsonString(entry, "url");
            McpServerDefinition server;
            if (url is not null)
            {
                server = new McpServerDefinition(
                    name,
                    McpServerType.Remote,
                    [],
                    EmptyMap,
                    convertVariables(url),
                    GetJsonStringMap(entry, "headers", convertVariables),
                    true);
            }
            else
            {
                var command = new List<string>();
                var program = GetJsonString(entry, "command");
                if (program is not null)
                {
                    command.Add(convertVariables(program));
                }

                if (entry["args"] is JsonArray args)
                {
                    command.AddRange(args
                        .Select(item => item is JsonValue argument && argument.TryGetValue<string>(out var text) ? text : null)
                        .Where(text => text is not null)
                        .Select(text => convertVariables(text!)));
                }

                server = new McpServerDefinition(
                    name,
                    McpServerType.Local,
                    command,
                    GetJsonStringMap(entry, "env", convertVariables),
                    null,
                    EmptyMap,
                    true);
            }

            result.Add(new ImportedMcpServer(origin, server));
        }

        return result;
    }

    private static string? GetJsonString(JsonObject parent, string propertyName) =>
        parent[propertyName] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static Dictionary<string, string> GetJsonStringMap(
        JsonObject parent,
        string propertyName,
        Func<string, string> convertVariables)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parent[propertyName] is not JsonObject values)
        {
            return map;
        }

        foreach (var (name, item) in values)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text))
            {
                map[name] = convertVariables(text);
            }
        }

        return map;
    }

    private static string ConvertClaudeVariables(string value) =>
        ClaudeVariableRegex().Replace(value, "{env:$1}");

    private static string ConvertCursorVariables(string value) =>
        CursorVariableRegex().Replace(value, "{env:$1}");

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex ClaudeVariableRegex();

    [GeneratedRegex(@"\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex CursorVariableRegex();
}
