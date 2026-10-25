namespace OpenSwitcher.Core.Tests;

[TestClass]
public sealed class OpenCodeProviderInspectorTests
{
    private const string ConfigSecret = "sk-config-SECRET-0123456789";
    private const string AuthSecret = "sk-auth-SECRET-9876543210";

    [TestMethod]
    public async Task GetConfiguredProviders_ReportsKeyKindsWithoutSecrets()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("opencode.jsonc", $$"""
            {
              // comment
              "provider": {
                "plain": { "name": "Plain", "options": { "apiKey": "{{ConfigSecret}}" } },
                "env": { "options": { "apiKey": "{env:MY_KEY}" } },
                "file": { "options": { "apiKey": "{file:~/key.txt}" } },
                "empty": { "options": { "apiKey": "" } },
                "number": { "options": { "apiKey": 42 } },
                "none": { "name": "None" }
              }
            }
            """);
        var authPath = directory.WriteFile("auth.json", $$"""
            {
              "openai": { "type": "api", "key": "{{AuthSecret}}" },
              "anthropic": { "type": "oauth", "access": "{{AuthSecret}}", "refresh": "{{AuthSecret}}" },
              "plain": { "type": "wellknown", "token": "{{AuthSecret}}" },
              "odd": { "key": "{{AuthSecret}}" }
            }
            """);
        var snapshot = await OpenCodeConfigSnapshot.LoadAsync(configPath);

        var providers = await OpenCodeProviderInspector.GetConfiguredProvidersAsync(snapshot, authPath);

        var kinds = providers.ToDictionary(provider => provider.Id, provider => provider.KeyKind);
        Assert.AreEqual("Plain text in configuration  ·  Well-known token (opencode auth login)", kinds["plain"]);
        Assert.AreEqual("Environment variable", kinds["env"]);
        Assert.AreEqual("File reference", kinds["file"]);
        Assert.AreEqual("Empty key in configuration", kinds["empty"]);
        Assert.AreEqual("Invalid key value in configuration", kinds["number"]);
        Assert.AreEqual("No key", kinds["none"]);
        Assert.AreEqual("API key (opencode auth login)", kinds["openai"]);
        Assert.AreEqual("OAuth (opencode auth login)", kinds["anthropic"]);
        Assert.AreEqual("unknown (opencode auth login)", kinds["odd"]);

        var allText = string.Join("|", providers.Select(provider => $"{provider.Id}|{provider.Name}|{provider.KeyKind}"));
        Assert.IsFalse(allText.Contains("SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetConfiguredProviders_InvalidAuthJson_ReportsOnlyTheLine()
    {
        using var directory = new TestDirectory();
        var configPath = directory.WriteFile("opencode.json", "{}");
        var authPath = directory.WriteFile("auth.json", $"{{\n  \"openai\": {{ \"type\": \"api\", \"key\": \"{AuthSecret}\" }}\n  \"x\": 1\n}}");
        var snapshot = await OpenCodeConfigSnapshot.LoadAsync(configPath);

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            OpenCodeProviderInspector.GetConfiguredProvidersAsync(snapshot, authPath));

        Assert.IsTrue(exception.Message.Contains("line 3", StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains("SECRET", StringComparison.Ordinal));
    }
}
