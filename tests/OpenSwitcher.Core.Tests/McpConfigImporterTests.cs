namespace OpenSwitcher.Core.Tests;

[TestClass]
public sealed class McpConfigImporterTests
{
    [TestMethod]
    public async Task LoadCodex_ConvertsLocalAndRemoteServers()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("config.toml", """
            model = "gpt-5"

            [mcp_servers.files]
            command = "npx"
            args = ["-y", "@modelcontextprotocol/server-filesystem", "C:\\work"]
            env = { DEBUG = "1", LEVEL = 3 }
            startup_timeout_sec = 12.5

            [mcp_servers.docs]
            url = "https://docs.example.test/mcp"
            bearer_token_env_var = "DOCS_TOKEN"
            http_headers = { "X-Team" = "core" }
            env_http_headers = { "X-Key" = "DOCS_KEY" }
            enabled = false
            startup_timeout_ms = 2000
            """);

        var servers = await McpConfigImporter.LoadAsync(McpImportSource.Codex, configPath);

        var files = servers.Single(item => item.Server.Name == "files").Server;
        Assert.AreEqual(McpServerType.Local, files.Type);
        CollectionAssert.AreEqual(
            new[] { "npx", "-y", "@modelcontextprotocol/server-filesystem", "C:\\work" },
            files.Command.ToArray());
        Assert.AreEqual("1", files.Environment["DEBUG"]);
        Assert.AreEqual("3", files.Environment["LEVEL"]);
        Assert.AreEqual(12500, files.Timeout);

        var docs = servers.Single(item => item.Server.Name == "docs").Server;
        Assert.AreEqual(McpServerType.Remote, docs.Type);
        Assert.AreEqual("https://docs.example.test/mcp", docs.Url);
        Assert.AreEqual("Bearer {env:DOCS_TOKEN}", docs.Headers["Authorization"]);
        Assert.AreEqual("core", docs.Headers["X-Team"]);
        Assert.AreEqual("{env:DOCS_KEY}", docs.Headers["X-Key"]);
        Assert.IsFalse(docs.Enabled);
        Assert.AreEqual(2000, docs.Timeout);
        Assert.IsTrue(servers.All(item => item.Origin == "Codex"));
    }

    [TestMethod]
    public async Task LoadCodex_InvalidTimeout_ThrowsInvalidDataException()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("config.toml", """
            [mcp_servers.slow]
            command = "slow.exe"
            startup_timeout_sec = 99999999999
            """);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => McpConfigImporter.LoadAsync(McpImportSource.Codex, configPath));
    }

    [TestMethod]
    public async Task LoadClaudeCode_ReadsUserAndProjectServers()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile(".claude.json", """
            {
              "mcpServers": {
                "github": { "type": "http", "url": "https://api.example.test/${REGION}/mcp", "headers": { "Authorization": "Bearer ${GITHUB_TOKEN}" } }
              },
              "projects": {
                "C:/work/app": {
                  "mcpServers": {
                    "local": { "command": "${HOME}/tool.exe", "args": ["--root", "${PWD}"], "env": { "TOKEN": "${API_TOKEN}" } }
                  }
                }
              }
            }
            """);

        var servers = await McpConfigImporter.LoadAsync(McpImportSource.ClaudeCode, configPath);

        var github = servers.Single(item => item.Server.Name == "github");
        Assert.AreEqual("Claude Code (user)", github.Origin);
        Assert.AreEqual("https://api.example.test/{env:REGION}/mcp", github.Server.Url);
        Assert.AreEqual("Bearer {env:GITHUB_TOKEN}", github.Server.Headers["Authorization"]);

        var local = servers.Single(item => item.Server.Name == "local");
        Assert.AreEqual("Claude Code (C:/work/app)", local.Origin);
        CollectionAssert.AreEqual(new[] { "{env:HOME}/tool.exe", "--root", "{env:PWD}" }, local.Server.Command.ToArray());
        Assert.AreEqual("{env:API_TOKEN}", local.Server.Environment["TOKEN"]);
    }

    [TestMethod]
    public async Task LoadCursor_ConvertsEnvironmentReferences()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("mcp.json", """
            {
              // Cursor allows comments
              "mcpServers": {
                "tool": { "command": "tool.exe", "env": { "KEY": "${env:TOOL_KEY}", "PLAIN": "${TOOL_KEY}" } }
              }
            }
            """);

        var servers = await McpConfigImporter.LoadAsync(McpImportSource.Cursor, configPath);

        var tool = servers.Single().Server;
        Assert.AreEqual("{env:TOOL_KEY}", tool.Environment["KEY"]);
        Assert.AreEqual("${TOOL_KEY}", tool.Environment["PLAIN"]);
    }

    [TestMethod]
    public async Task Load_MissingFile_ReturnsEmptyList()
    {
        using var directory = new TestDirectory();

        var servers = await McpConfigImporter.LoadAsync(McpImportSource.Cursor, directory.GetPath("missing.json"));

        Assert.AreEqual(0, servers.Count);
    }
}
