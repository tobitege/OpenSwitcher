using System.Text;
using System.Text.Json;

namespace OpenSwitcher.Core;

public sealed record ConfiguredProvider(string Id, string Name, string KeyKind);

public static class OpenCodeProviderInspector
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private delegate T ReaderFunc<out T>(ref Utf8JsonReader reader);

    public static Task<IReadOnlyList<ConfiguredProvider>> GetConfiguredProvidersAsync(
        OpenCodeConfigSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        GetConfiguredProvidersAsync(snapshot, OpenCodeConfigLocator.FindAuthPath(), cancellationToken);

    public static async Task<IReadOnlyList<ConfiguredProvider>> GetConfiguredProvidersAsync(
        OpenCodeConfigSnapshot snapshot,
        string authPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(authPath);

        var authJson = await ReadUtf8Async(authPath, cancellationToken);
        var configProviders = ReadJson(snapshot.Utf8, "OpenCode configuration", ReadConfigProviders);
        var authTypes = ReadJson(authJson, "OpenCode credentials file", ReadAuthTypes);

        var providerIds = configProviders.Keys
            .Concat(authTypes.Keys.Where(id => !configProviders.ContainsKey(id)))
            .ToArray();

        return providerIds
            .Select(id =>
            {
                configProviders.TryGetValue(id, out var configProvider);
                authTypes.TryGetValue(id, out var authType);
                var keyKinds = new List<string>();
                if (configProvider.KeyKind is not null)
                {
                    keyKinds.Add(configProvider.KeyKind);
                }

                if (authType is not null)
                {
                    keyKinds.Add(DescribeAuthType(authType));
                }

                return new ConfiguredProvider(
                    id,
                    configProvider.Name ?? string.Empty,
                    keyKinds.Count == 0 ? "No key" : string.Join("  ·  ", keyKinds));
            })
            .ToArray();
    }

    private static async Task<byte[]> ReadUtf8Async(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return [];
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        return bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            ? bytes[Encoding.UTF8.Preamble.Length..]
            : bytes;
    }

    private static Dictionary<string, T> ReadJson<T>(
        byte[] json,
        string description,
        ReaderFunc<Dictionary<string, T>> read)
    {
        if (json.AsSpan().Trim(" \t\r\n"u8).IsEmpty)
        {
            return new Dictionary<string, T>(StringComparer.Ordinal);
        }

        try
        {
            var reader = new Utf8JsonReader(json, ReaderOptions);
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new InvalidDataException($"The {description} must contain a JSON object.");
            }

            return read(ref reader);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The {description} is not valid JSON (line {exception.LineNumber + 1}).");
        }
    }

    private static Dictionary<string, (string? Name, string? KeyKind)> ReadConfigProviders(ref Utf8JsonReader reader)
    {
        var providers = new Dictionary<string, (string? Name, string? KeyKind)>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isProviderSection = reader.ValueTextEquals("provider"u8);
            reader.Read();
            if (isProviderSection && reader.TokenType != JsonTokenType.Null)
            {
                ReadProviderSection(ref reader, providers);
            }
            else
            {
                reader.Skip();
            }
        }

        return providers;
    }

    private static void ReadProviderSection(
        ref Utf8JsonReader reader,
        Dictionary<string, (string? Name, string? KeyKind)> providers)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException("'provider' must be a JSON object.");
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var providerId = reader.GetString()!;
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new InvalidDataException($"'provider.{providerId}' must be a JSON object.");
            }

            providers[providerId] = ReadConfigProvider(ref reader);
        }
    }

    private static (string? Name, string? KeyKind) ReadConfigProvider(ref Utf8JsonReader reader)
    {
        string? name = null;
        string? keyKind = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isName = reader.ValueTextEquals("name"u8);
            var isOptions = reader.ValueTextEquals("options"u8);
            reader.Read();
            if (isName)
            {
                name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (isOptions)
            {
                keyKind = ReadApiKeyKind(ref reader) ?? keyKind;
                continue;
            }

            reader.Skip();
        }

        return (name, keyKind);
    }

    private static string? ReadApiKeyKind(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        string? keyKind = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isApiKey = reader.ValueTextEquals("apiKey"u8);
            reader.Read();
            if (isApiKey)
            {
                keyKind = ClassifyApiKey(ref reader);
            }

            reader.Skip();
        }

        return keyKind;
    }

    private static string ClassifyApiKey(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            return "Invalid key value in configuration";
        }

        var value = reader.ValueSpan;
        if (value.IndexOf("{env:"u8) >= 0)
        {
            return "Environment variable";
        }

        if (value.IndexOf("{file:"u8) >= 0)
        {
            return "File reference";
        }

        return value.IsEmpty ? "Empty key in configuration" : "Plain text in configuration";
    }

    private static Dictionary<string, string> ReadAuthTypes(ref Utf8JsonReader reader)
    {
        var authTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var providerId = reader.GetString()!;
            reader.Read();
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                authTypes[providerId] = ReadAuthType(ref reader) ?? "unknown";
            }
            else
            {
                reader.Skip();
            }
        }

        return authTypes;
    }

    private static string? ReadAuthType(ref Utf8JsonReader reader)
    {
        string? authType = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isType = reader.ValueTextEquals("type"u8);
            reader.Read();
            if (isType && reader.TokenType == JsonTokenType.String)
            {
                authType = reader.GetString();
            }

            reader.Skip();
        }

        return authType;
    }

    private static string DescribeAuthType(string authType) => authType switch
    {
        "api" => "API key (opencode auth login)",
        "oauth" => "OAuth (opencode auth login)",
        "wellknown" => "Well-known token (opencode auth login)",
        _ => $"{authType} (opencode auth login)"
    };
}
