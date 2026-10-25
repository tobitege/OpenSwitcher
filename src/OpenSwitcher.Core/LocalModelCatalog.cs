using System.Text.Json;

namespace OpenSwitcher.Core;

public sealed class LocalModelCatalog(HttpClient httpClient)
{
    public Task<IReadOnlyList<LocalModel>> GetModelsAsync(
        LocalProviderDescriptor provider,
        Uri baseUrl,
        CancellationToken cancellationToken = default) =>
        provider.Kind switch
        {
            LocalProviderKind.LmStudio => GetLmStudioModelsAsync(baseUrl, cancellationToken),
            LocalProviderKind.Ollama => GetOllamaModelsAsync(baseUrl, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider.Kind, "Unknown local provider.")
        };

    public async Task<IReadOnlyList<LocalModel>> GetLmStudioModelsAsync(
        Uri baseUrl,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(new Uri(GetServerRoot(baseUrl), "api/v1/models"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseLmStudioModels(json);
    }

    public async Task<IReadOnlyList<LocalModel>> GetOllamaModelsAsync(
        Uri baseUrl,
        CancellationToken cancellationToken = default)
    {
        var serverRoot = GetServerRoot(baseUrl);
        using var response = await httpClient.GetAsync(new Uri(serverRoot, "api/tags"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        IReadOnlySet<string> loadedModelIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var runningResponse = await httpClient.GetAsync(new Uri(serverRoot, "api/ps"), cancellationToken);
            if (runningResponse.IsSuccessStatusCode)
            {
                var runningJson = await runningResponse.Content.ReadAsStringAsync(cancellationToken);
                loadedModelIds = ParseOllamaRunningModelIds(runningJson);
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidDataException ||
            (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The installed-model list is still useful when an older Ollama lacks /api/ps.
        }

        return ParseOllamaModels(json, loadedModelIds);
    }

    public static Uri GetServerRoot(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return new Uri(baseUrl.GetLeftPart(UriPartial.Authority) + "/");
    }

    public static IReadOnlyList<LocalModel> ParseLmStudioModels(string json)
    {
        using var document = ParseDocument(json, "LM Studio");
        if (!TryGetModelsArray(document, out var modelsElement))
        {
            throw new InvalidDataException("LM Studio did not return a 'models' array.");
        }

        var models = new List<LocalModel>();
        foreach (var item in modelsElement.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.Object ||
                !string.Equals(GetString(item, "type"), "llm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = GetString(item, "key");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var displayName = GetString(item, "display_name");
            var isLoaded = item.TryGetProperty("loaded_instances", out var loadedInstances) &&
                loadedInstances.ValueKind is JsonValueKind.Array &&
                loadedInstances.GetArrayLength() > 0;
            var supportsVision = TryGetBooleanCapability(item, "vision");
            var supportsTools = TryGetBooleanCapability(item, "trained_for_tool_use");
            models.Add(new LocalModel(
                id,
                string.IsNullOrWhiteSpace(displayName) ? id : displayName,
                isLoaded,
                supportsVision,
                supportsTools));
        }

        return Normalize(models);
    }

    public static IReadOnlyList<LocalModel> ParseOllamaModels(
        string json,
        IReadOnlySet<string>? loadedModelIds = null)
    {
        using var document = ParseDocument(json, "Ollama");
        if (!TryGetModelsArray(document, out var modelsElement))
        {
            throw new InvalidDataException("Ollama did not return a 'models' array.");
        }

        var models = new List<LocalModel>();
        foreach (var item in modelsElement.EnumerateArray())
        {
            var id = GetOllamaModelId(item);
            if (!string.IsNullOrWhiteSpace(id))
            {
                models.Add(new LocalModel(
                    id,
                    id,
                    loadedModelIds?.Contains(id) is true,
                    HasArrayCapability(item, "vision"),
                    HasArrayCapability(item, "tools")));
            }
        }

        return Normalize(models);
    }

    public static IReadOnlySet<string> ParseOllamaRunningModelIds(string json)
    {
        using var document = ParseDocument(json, "Ollama");
        if (!TryGetModelsArray(document, out var modelsElement))
        {
            throw new InvalidDataException("Ollama did not return a running 'models' array.");
        }

        return modelsElement.EnumerateArray()
            .Select(GetOllamaModelId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static JsonDocument ParseDocument(string json, string source)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"{source} did not return valid JSON.");
        }
    }

    private static bool TryGetModelsArray(JsonDocument document, out JsonElement modelsElement)
    {
        modelsElement = default;
        return document.RootElement.ValueKind is JsonValueKind.Object &&
            document.RootElement.TryGetProperty("models", out modelsElement) &&
            modelsElement.ValueKind is JsonValueKind.Array;
    }

    private static string? GetOllamaModelId(JsonElement item)
    {
        if (item.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(item, "name");
        return string.IsNullOrWhiteSpace(id) ? GetString(item, "model") : id;
    }

    private static string? GetString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetBooleanCapability(JsonElement item, string capabilityName) =>
        item.TryGetProperty("capabilities", out var capabilities) &&
        capabilities.ValueKind is JsonValueKind.Object &&
        capabilities.TryGetProperty(capabilityName, out var capability) &&
        capability.ValueKind is JsonValueKind.True;

    private static bool HasArrayCapability(JsonElement item, string capabilityName) =>
        item.TryGetProperty("capabilities", out var capabilities) &&
        capabilities.ValueKind is JsonValueKind.Array &&
        capabilities.EnumerateArray().Any(capability =>
            capability.ValueKind is JsonValueKind.String &&
            string.Equals(capability.GetString(), capabilityName, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<LocalModel> Normalize(IEnumerable<LocalModel> models) =>
        models
            .DistinctBy(model => model.Id, StringComparer.Ordinal)
            .OrderByDescending(model => model.IsLoaded)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
