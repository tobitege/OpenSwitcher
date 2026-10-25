using System.Text;
using System.Text.Json.Nodes;

namespace OpenSwitcher.Core;

public sealed class OpenCodeConfigSnapshot
{
    private readonly JsonObject _root;

    private OpenCodeConfigSnapshot(string configPath, byte[] utf8, JsonObject root)
    {
        ConfigPath = configPath;
        Utf8 = utf8;
        _root = root;
    }

    public string ConfigPath { get; }

    internal byte[] Utf8 { get; }

    public static async Task<OpenCodeConfigSnapshot> LoadAsync(
        string configPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        var fullPath = Path.GetFullPath(configPath);
        var bytes = File.Exists(fullPath)
            ? await File.ReadAllBytesAsync(fullPath, cancellationToken)
            : [];
        var utf8 = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            ? bytes[Encoding.UTF8.Preamble.Length..]
            : bytes;
        return new OpenCodeConfigSnapshot(fullPath, utf8, OpenCodeJson.ParseRoot(Encoding.UTF8.GetString(utf8)));
    }

    public IReadOnlySet<string> GetConfiguredModelIds(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var provider = OpenCodeJson.GetObject(_root, ["provider", providerId]);
        var models = provider is null ? null : OpenCodeJson.GetObject(provider, ["models"]);
        if (models is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var modelIds = models.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        if (provider!["whitelist"] is null)
        {
            return modelIds;
        }

        if (provider["whitelist"] is not JsonArray whitelist)
        {
            throw new InvalidDataException($"'provider.{providerId}.whitelist' must be a JSON array.");
        }

        modelIds.IntersectWith(whitelist
            .Select(item => item is JsonValue value && value.TryGetValue<string>(out var id) ? id : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!));
        return modelIds;
    }

    public Uri? GetProviderBaseUrl(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var options = OpenCodeJson.GetObject(_root, ["provider", providerId, "options"]);
        var baseUrl = options is null ? null : OpenCodeJson.GetString(options, "baseURL");
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri
                : null;
    }

    public IReadOnlyList<McpServerDefinition> GetMcpServers()
    {
        var servers = OpenCodeJson.GetObject(_root, ["mcp"]);
        return servers is null
            ? []
            : servers.Select(item => OpenCodeJson.ReadMcpServer(item.Key, item.Value)).ToArray();
    }
}
