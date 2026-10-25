using System.Text.Json.Nodes;

namespace OpenSwitcher.Core;

public sealed record CatalogSyncResult(
    string ConfigPath,
    string? BackupPath,
    string ProviderId,
    int ModelCount,
    string? DefaultModel,
    bool DefaultModelChanged);
public sealed record ConfigWriteResult(string ConfigPath, string? BackupPath);

public sealed class OpenCodeConfigService
{
    public string FindGlobalConfigPath() => OpenCodeConfigLocator.FindGlobalConfigPath();

    public Task<OpenCodeConfigSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default) =>
        OpenCodeConfigSnapshot.LoadAsync(FindGlobalConfigPath(), cancellationToken);

    public Task<CatalogSyncResult> SynchronizeProviderModelsAsync(
        LocalProviderDescriptor provider,
        IReadOnlyCollection<LocalModel> selectedModels,
        CancellationToken cancellationToken = default) =>
        SynchronizeProviderModelsAsync(
            FindGlobalConfigPath(),
            provider,
            selectedModels,
            cancellationToken);

    public async Task<CatalogSyncResult> SynchronizeProviderModelsAsync(
        string configPath,
        LocalProviderDescriptor provider,
        IReadOnlyCollection<LocalModel> selectedModels,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(selectedModels);
        if (selectedModels.Count == 0)
        {
            throw new ArgumentException("At least one model must be selected.", nameof(selectedModels));
        }

        var duplicateId = selectedModels
            .GroupBy(model => model.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateId is not null)
        {
            throw new ArgumentException($"Duplicate model ID selected: {duplicateId}", nameof(selectedModels));
        }

        var document = await ConfigDocument.LoadAsync(configPath, cancellationToken);
        var editor = document.Editor;
        string[] providerPath = ["provider", provider.Id];
        var providerEntry = OpenCodeJson.GetObject(document.Root, providerPath);
        var options = providerEntry is null ? null : OpenCodeJson.GetObject(providerEntry, ["options"]);
        var existingModels = providerEntry is null ? null : OpenCodeJson.GetObject(providerEntry, ["models"]);

        if (providerEntry?["npm"] is null)
        {
            editor.Set([.. providerPath, "npm"], "@ai-sdk/openai-compatible");
        }

        if (providerEntry?["name"] is null)
        {
            editor.Set([.. providerPath, "name"], provider.ConfigName);
        }

        if (options?["baseURL"] is null)
        {
            editor.Set([.. providerPath, "options", "baseURL"], provider.DefaultBaseUrl.ToString());
        }

        var models = new JsonObject();
        foreach (var model in selectedModels)
        {
            var modelSettings = existingModels?[model.Id]?.DeepClone() as JsonObject ?? new JsonObject
            {
                ["temperature"] = true,
                ["tool_call"] = true
            };
            modelSettings["name"] = model.DisplayName;
            ApplyModelCapabilities(modelSettings, model);
            models[model.Id] = modelSettings;
        }

        editor.Set([.. providerPath, "models"], models);
        editor.Set([.. providerPath, "whitelist"], OpenCodeJson.CreateArray(selectedModels.Select(model => model.Id)));

        var defaultModel = OpenCodeJson.GetString(document.Root, "model");
        var defaultModelChanged = false;
        var providerPrefix = provider.Id + "/";
        if (defaultModel?.StartsWith(providerPrefix, StringComparison.Ordinal) is true)
        {
            var defaultModelId = defaultModel[providerPrefix.Length..];
            if (!selectedModels.Any(model => string.Equals(model.Id, defaultModelId, StringComparison.Ordinal)))
            {
                var replacement = selectedModels.FirstOrDefault(model => model.IsLoaded) ?? selectedModels.First();
                defaultModel = providerPrefix + replacement.Id;
                editor.Set(["model"], defaultModel);
                defaultModelChanged = true;
            }
        }

        var backupPath = await document.SaveAsync(cancellationToken);
        return new CatalogSyncResult(
            document.FullPath,
            backupPath,
            provider.Id,
            selectedModels.Count,
            defaultModel,
            defaultModelChanged);
    }

    public Task<ConfigWriteResult> SaveMcpServerAsync(
        string? originalName,
        McpServerDefinition server,
        CancellationToken cancellationToken = default) =>
        SaveMcpServerAsync(FindGlobalConfigPath(), originalName, server, cancellationToken);

    public async Task<ConfigWriteResult> SaveMcpServerAsync(
        string configPath,
        string? originalName,
        McpServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(server);
        ValidateMcpServer(server);

        var document = await ConfigDocument.LoadAsync(configPath, cancellationToken);
        var servers = OpenCodeJson.GetObject(document.Root, ["mcp"]);

        var isRename = originalName is not null &&
            !string.Equals(originalName, server.Name, StringComparison.Ordinal);
        if ((originalName is null || isRename) && servers?.ContainsKey(server.Name) is true)
        {
            throw new InvalidOperationException($"An MCP server named '{server.Name}' already exists.");
        }

        if (originalName is null)
        {
            document.Editor.Set(["mcp", server.Name], OpenCodeJson.CreateMcpEntry(server));
        }
        else
        {
            if (servers?[originalName] is null)
            {
                throw new InvalidOperationException(
                    $"The MCP server '{originalName}' no longer exists in the OpenCode configuration.");
            }

            var current = OpenCodeJson.ReadMcpServer(originalName, servers[originalName]);
            if (isRename)
            {
                document.Editor.Rename(["mcp", originalName], server.Name);
            }

            ApplyMcpChanges(document.Editor, current, server);
        }

        var backupPath = await document.SaveAsync(cancellationToken);
        return new ConfigWriteResult(document.FullPath, backupPath);
    }

    public Task<ConfigWriteResult> DeleteMcpServerAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        DeleteMcpServerAsync(FindGlobalConfigPath(), name, cancellationToken);

    public async Task<ConfigWriteResult> DeleteMcpServerAsync(
        string configPath,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var document = await ConfigDocument.LoadAsync(configPath, cancellationToken);
        if (OpenCodeJson.GetObject(document.Root, ["mcp"])?.ContainsKey(name) is not true)
        {
            throw new InvalidOperationException(
                $"The MCP server '{name}' no longer exists in the OpenCode configuration.");
        }

        document.Editor.Remove(["mcp", name]);
        var backupPath = await document.SaveAsync(cancellationToken);
        return new ConfigWriteResult(document.FullPath, backupPath);
    }

    public Task<ConfigWriteResult> ImportMcpServersAsync(
        IReadOnlyCollection<McpServerDefinition> servers,
        CancellationToken cancellationToken = default) =>
        ImportMcpServersAsync(FindGlobalConfigPath(), servers, cancellationToken);

    public async Task<ConfigWriteResult> ImportMcpServersAsync(
        string configPath,
        IReadOnlyCollection<McpServerDefinition> servers,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(servers);
        if (servers.Count == 0)
        {
            throw new ArgumentException("At least one MCP server must be selected.", nameof(servers));
        }

        var duplicateName = servers
            .GroupBy(server => server.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateName is not null)
        {
            throw new ArgumentException($"Duplicate MCP server selected: {duplicateName}", nameof(servers));
        }

        foreach (var server in servers)
        {
            ValidateMcpServer(server);
        }

        var document = await ConfigDocument.LoadAsync(configPath, cancellationToken);
        foreach (var server in servers)
        {
            document.Editor.Set(["mcp", server.Name], OpenCodeJson.CreateMcpEntry(server));
        }

        var backupPath = await document.SaveAsync(cancellationToken);
        return new ConfigWriteResult(document.FullPath, backupPath);
    }

    private static void ValidateMcpServer(McpServerDefinition server)
    {
        if (string.IsNullOrWhiteSpace(server.Name) || !string.Equals(server.Name, server.Name.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The MCP server name must not be empty or start or end with spaces.");
        }

        if (server.Type == McpServerType.Local)
        {
            if (server.Command.Count == 0 || string.IsNullOrWhiteSpace(server.Command[0]))
            {
                throw new ArgumentException($"MCP server '{server.Name}': a program is required.");
            }
        }
        else if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"MCP server '{server.Name}': an absolute http or https URL is required.");
        }

        if (server.Timeout <= 0)
        {
            throw new ArgumentException($"MCP server '{server.Name}': the timeout must be greater than zero.");
        }
    }

    private static void ApplyMcpChanges(JsonTextEditor editor, McpServerDefinition current, McpServerDefinition server)
    {
        string[] FieldPath(string field) => ["mcp", server.Name, field];

        var typeChanged = current.Type != server.Type;
        if (typeChanged)
        {
            editor.Set(FieldPath("type"), server.Type == McpServerType.Local ? "local" : "remote");
        }

        if (server.Type == McpServerType.Local)
        {
            if (typeChanged)
            {
                editor.Remove(FieldPath("url"));
                editor.Remove(FieldPath("headers"));
                editor.Remove(FieldPath("oauth"));
            }

            if (typeChanged || !current.Command.SequenceEqual(server.Command, StringComparer.Ordinal))
            {
                editor.Set(FieldPath("command"), OpenCodeJson.CreateArray(server.Command));
            }

            if (typeChanged || !current.Environment.SequenceEqual(server.Environment))
            {
                SetOrRemoveMap(editor, FieldPath("environment"), server.Environment);
            }
        }
        else
        {
            if (typeChanged)
            {
                editor.Remove(FieldPath("command"));
                editor.Remove(FieldPath("environment"));
            }

            if (typeChanged || !string.Equals(current.Url, server.Url, StringComparison.Ordinal))
            {
                editor.Set(FieldPath("url"), server.Url!);
            }

            if (typeChanged || !current.Headers.SequenceEqual(server.Headers))
            {
                SetOrRemoveMap(editor, FieldPath("headers"), server.Headers);
            }
        }

        if (current.Enabled != server.Enabled)
        {
            editor.Set(FieldPath("enabled"), server.Enabled);
        }

        if (current.Timeout != server.Timeout)
        {
            if (server.Timeout is int timeout)
            {
                editor.Set(FieldPath("timeout"), timeout);
            }
            else
            {
                editor.Remove(FieldPath("timeout"));
            }
        }
    }

    private static void SetOrRemoveMap(JsonTextEditor editor, IReadOnlyList<string> path, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0)
        {
            editor.Remove(path);
        }
        else
        {
            editor.Set(path, OpenCodeJson.CreateMap(values));
        }
    }

    private static void ApplyModelCapabilities(JsonObject modelSettings, LocalModel model)
    {
        if (!model.SupportsVision)
        {
            return;
        }

        modelSettings["attachment"] = true;
        modelSettings["modalities"] = new JsonObject
        {
            ["input"] = new JsonArray("text", "image"),
            ["output"] = new JsonArray("text")
        };
    }
}
