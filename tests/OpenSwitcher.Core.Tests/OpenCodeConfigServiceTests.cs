using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenSwitcher.Core.Tests;

[TestClass]
public sealed class OpenCodeConfigServiceTests
{
    private static readonly JsonDocumentOptions StrictJson = new();

    private readonly OpenCodeConfigService _service = new();

    [TestMethod]
    public async Task SaveMcpServer_UnchangedEdit_DoesNotTouchTheFile()
    {
        using var directory = new TestDirectory();
        const string original = """
            {
              // user comment
              "mcp": {
                "search": {
                  "type": "remote",
                  "url": "https://example.test/mcp",
                  "headers": { "X-Retries": 3 },
                  "oauth": { "clientId": "abc" },
                  "custom": [1, 2],
                  "timeout": 1500.4
                }
              }
            }
            """;
        var configPath = directory.WriteFile("opencode.jsonc", original);
        var server = await LoadMcpServerAsync(configPath, "search");

        var result = await _service.SaveMcpServerAsync(configPath, "search", server);

        Assert.IsNull(result.BackupPath);
        Assert.AreEqual(original, File.ReadAllText(configPath));
        Assert.AreEqual("3", server.Headers["X-Retries"]);
        Assert.AreEqual(1500, server.Timeout);
    }

    [TestMethod]
    public async Task SaveMcpServer_ChangedField_KeepsUnknownFieldsAndComments()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("opencode.jsonc", """
            {
              // user comment
              "mcp": {
                "search": {
                  "type": "remote",
                  "url": "https://example.test/mcp", /* inline */
                  "oauth": { "clientId": "abc" },
                  "custom": [1, 2],
                  "enabled": true
                }
              }
            }
            """);
        var server = await LoadMcpServerAsync(configPath, "search");

        await _service.SaveMcpServerAsync(configPath, "search", server with { Enabled = false });

        var text = File.ReadAllText(configPath);
        Assert.IsTrue(text.Contains("// user comment", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("/* inline */", StringComparison.Ordinal));
        var entry = ParseLenient(text)["mcp"]!["search"]!.AsObject();
        Assert.AreEqual("abc", entry["oauth"]!["clientId"]!.GetValue<string>());
        Assert.AreEqual("[1,2]", entry["custom"]!.ToJsonString());
        Assert.IsFalse(entry["enabled"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task SaveMcpServer_Rename_KeepsPositionAndContent()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("opencode.json", """
            {
              "mcp": {
                "first": { "type": "local", "command": ["a.exe"] },
                "second": { "type": "local", "command": ["b.exe"], "custom": true },
                "third": { "type": "local", "command": ["c.exe"] }
              }
            }
            """);
        var server = await LoadMcpServerAsync(configPath, "second");

        await _service.SaveMcpServerAsync(configPath, "second", server with { Name = "renamed" });

        var servers = ParseStrict(File.ReadAllText(configPath))["mcp"]!.AsObject();
        CollectionAssert.AreEqual(
            new[] { "first", "renamed", "third" },
            servers.Select(item => item.Key).ToArray());
        Assert.IsTrue(servers["renamed"]!["custom"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task SaveMcpServer_TypeChange_RemovesFieldsOfTheOldType()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("opencode.json", """
            {
              "mcp": {
                "tool": { "type": "remote", "url": "https://example.test", "headers": { "A": "1" }, "oauth": {} }
              }
            }
            """);
        var server = await LoadMcpServerAsync(configPath, "tool");

        await _service.SaveMcpServerAsync(
            configPath,
            "tool",
            server with { Type = McpServerType.Local, Command = ["tool.exe", "--stdio"] });

        var entry = ParseStrict(File.ReadAllText(configPath))["mcp"]!["tool"]!.AsObject();
        Assert.AreEqual("local", entry["type"]!.GetValue<string>());
        Assert.AreEqual("[\"tool.exe\",\"--stdio\"]", entry["command"]!.ToJsonString());
        Assert.IsFalse(entry.ContainsKey("url"));
        Assert.IsFalse(entry.ContainsKey("headers"));
        Assert.IsFalse(entry.ContainsKey("oauth"));
    }

    [TestMethod]
    [DataRow("first", new[] { "second", "third" })]
    [DataRow("second", new[] { "first", "third" })]
    [DataRow("third", new[] { "first", "second" })]
    public async Task DeleteMcpServer_ProducesStrictJson(string name, string[] remaining)
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("opencode.json", """
            {
              "mcp": {
                "first": { "type": "local", "command": ["a.exe"] },
                "second": { "type": "local", "command": ["b.exe"] },
                "third": { "type": "local", "command": ["c.exe"] }
              },
              "model": "lmstudio/x"
            }
            """);

        await _service.DeleteMcpServerAsync(configPath, name);

        var root = ParseStrict(File.ReadAllText(configPath));
        CollectionAssert.AreEqual(remaining, root["mcp"]!.AsObject().Select(item => item.Key).ToArray());
        Assert.AreEqual("lmstudio/x", root["model"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DeleteMcpServer_OnlyEntry_LeavesEmptyObject()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("opencode.json", """
            {
              "mcp": {
                "only": { "type": "local", "command": ["a.exe"] }
              }
            }
            """);

        await _service.DeleteMcpServerAsync(configPath, "only");

        var root = ParseStrict(File.ReadAllText(configPath));
        Assert.AreEqual(0, root["mcp"]!.AsObject().Count);
    }

    [TestMethod]
    [DataRow("{\n  \"model\": \"x\"\n}\n")]
    [DataRow("{\n  \"model\": \"x\",\n  \"mcp\": {}\n}\n")]
    [DataRow("{\n  \"model\": \"x\",\n  \"mcp\": {\n  }\n}\n")]
    [DataRow("")]
    public async Task SaveMcpServer_Add_CreatesEntry(string original)
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("opencode.json", original);
        var server = CreateLocalServer("added");

        await _service.SaveMcpServerAsync(configPath, null, server);

        var root = ParseStrict(File.ReadAllText(configPath));
        Assert.AreEqual("https://opencode.ai/config.json", root["$schema"]!.GetValue<string>());
        Assert.AreEqual("[\"tool.exe\"]", root["mcp"]!["added"]!["command"]!.ToJsonString());
    }

    [TestMethod]
    public async Task SaveMcpServer_Add_CreatesMissingFileWithoutBackup()
    {
        using var directory = new TestDirectory();
        var configPath = Path.Combine(directory.Path, "nested", "opencode.json");

        var result = await _service.SaveMcpServerAsync(configPath, null, CreateLocalServer("added"));

        Assert.IsNull(result.BackupPath);
        Assert.IsTrue(ParseStrict(File.ReadAllText(configPath))["mcp"]!.AsObject().ContainsKey("added"));
    }

    [TestMethod]
    public async Task SaveMcpServer_Add_KeepsCommentsAndCrLf()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile(
            "opencode.jsonc",
            "{\r\n  // keep me\r\n  \"$schema\": \"https://opencode.ai/config.json\",\r\n  \"mcp\": {\r\n    \"a\": { \"type\": \"local\", \"command\": [\"a.exe\"] } // trailing\r\n  }\r\n}\r\n");

        await _service.SaveMcpServerAsync(configPath, null, CreateLocalServer("b"));

        var text = File.ReadAllText(configPath);
        Assert.IsTrue(text.Contains("// keep me", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("// trailing", StringComparison.Ordinal));
        Assert.IsFalse(text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n'));
        var servers = ParseLenient(text)["mcp"]!.AsObject();
        CollectionAssert.AreEqual(new[] { "a", "b" }, servers.Select(item => item.Key).ToArray());
    }

    [TestMethod]
    public async Task SaveAsync_KeepsTheNewestBackups()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("opencode.json", "{\n  \"mcp\": {}\n}\n");
        var backupPaths = new List<string?>();

        for (var index = 0; index < ConfigDocument.BackupRetentionCount + 3; index++)
        {
            var result = await _service.SaveMcpServerAsync(configPath, null, CreateLocalServer($"server{index}"));
            backupPaths.Add(result.BackupPath);
        }

        var backups = ConfigDocument.GetBackupPaths(configPath);
        Assert.AreEqual(ConfigDocument.BackupRetentionCount, backups.Count);
        Assert.IsTrue(backupPaths.All(path => path is not null));
        Assert.AreEqual(backupPaths[^1], backups[0]);
        Assert.IsFalse(File.Exists(backupPaths[0]));
        var newestBackup = ParseStrict(File.ReadAllText(backups[0]))["mcp"]!.AsObject();
        Assert.AreEqual(ConfigDocument.BackupRetentionCount + 2, newestBackup.Count);
    }

    [TestMethod]
    public async Task ImportMcpServers_ReplacesExistingEntries()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("opencode.json", """
            {
              "mcp": {
                "a": { "type": "local", "command": ["old.exe"] }
              }
            }
            """);

        await _service.ImportMcpServersAsync(configPath, [CreateLocalServer("a"), CreateLocalServer("b")]);

        var servers = ParseStrict(File.ReadAllText(configPath))["mcp"]!.AsObject();
        Assert.AreEqual("[\"tool.exe\"]", servers["a"]!["command"]!.ToJsonString());
        Assert.IsTrue(servers.ContainsKey("b"));
    }

    [TestMethod]
    public async Task SynchronizeProviderModels_PreservesCommentsAndModelSettings()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("opencode.jsonc", """
            {
              // global comment
              "model": "lmstudio/removed",
              "provider": {
                "lmstudio": {
                  "options": { "baseURL": "http://lmstudio.example.test:1234/v1" },
                  "models": {
                    "kept": { "name": "old", "limit": { "context": 8192 } },
                    "removed": { "name": "removed" }
                  }
                }
              }
            }
            """);
        LocalModel[] models =
        [
            new("kept", "Kept", false, false, true),
            new("new", "New", true, true, true)
        ];

        var result = await _service.SynchronizeProviderModelsAsync(configPath, LocalProviderDescriptor.LmStudio, models);

        var text = File.ReadAllText(configPath);
        Assert.IsTrue(text.Contains("// global comment", StringComparison.Ordinal));
        var provider = ParseLenient(text)["provider"]!["lmstudio"]!.AsObject();
        Assert.AreEqual("http://lmstudio.example.test:1234/v1", provider["options"]!["baseURL"]!.GetValue<string>());
        Assert.AreEqual("LM Studio (local)", provider["name"]!.GetValue<string>());
        Assert.AreEqual(8192, provider["models"]!["kept"]!["limit"]!["context"]!.GetValue<int>());
        Assert.AreEqual("Kept", provider["models"]!["kept"]!["name"]!.GetValue<string>());
        Assert.IsFalse(provider["models"]!.AsObject().ContainsKey("removed"));
        Assert.AreEqual("[\"kept\",\"new\"]", provider["whitelist"]!.ToJsonString());
        Assert.IsTrue(result.DefaultModelChanged);
        Assert.AreEqual("lmstudio/new", result.DefaultModel);
    }

    [TestMethod]
    public async Task Snapshot_ReadsSelectionBaseUrlAndMcpServers()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("opencode.json", """
            {
              "provider": {
                "ollama": {
                  "options": { "baseURL": "http://ollama.example.test:11434/v1" },
                  "models": { "a": {}, "b": {} },
                  "whitelist": ["b"]
                }
              },
              "mcp": { "x": { "type": "remote", "url": "https://example.test" } }
            }
            """);

        var snapshot = await OpenCodeConfigSnapshot.LoadAsync(configPath);

        CollectionAssert.AreEqual(new[] { "b" }, snapshot.GetConfiguredModelIds("ollama").ToArray());
        Assert.AreEqual(new Uri("http://ollama.example.test:11434/v1"), snapshot.GetProviderBaseUrl("ollama"));
        Assert.IsNull(snapshot.GetProviderBaseUrl("lmstudio"));
        Assert.AreEqual("x", snapshot.GetMcpServers().Single().Name);
    }

    [TestMethod]
    public async Task Snapshot_InvalidJson_ReportsOnlyTheLine()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("opencode.json", "{\n  \"apiKey\": \"secret-value\"\n  \"x\": 1\n}");

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OpenCodeConfigSnapshot.LoadAsync(configPath));

        Assert.IsTrue(exception.Message.Contains("line 3", StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains("secret", StringComparison.Ordinal));
    }

    private static async Task<McpServerDefinition> LoadMcpServerAsync(string configPath, string name)
    {
        var snapshot = await OpenCodeConfigSnapshot.LoadAsync(configPath);
        return snapshot.GetMcpServers().Single(server => server.Name == name);
    }

    private static McpServerDefinition CreateLocalServer(string name) =>
        new(name, McpServerType.Local, ["tool.exe"], new Dictionary<string, string>(), null, new Dictionary<string, string>());

    private static JsonObject ParseStrict(string text) =>
        JsonNode.Parse(text, documentOptions: StrictJson)!.AsObject();

    private static JsonObject ParseLenient(string text) =>
        JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        })!.AsObject();
}
