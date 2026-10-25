namespace OpenSwitcher.Core.Tests;

[TestClass]
public sealed class LocatorTests
{
    [TestMethod]
    public void FindGlobalConfigPath_PrefersJsonThenJsonc()
    {
        var profile = Path.Combine(Path.GetTempPath(), "profile");
        var jsonPath = Path.Combine(profile, ".config", "opencode", "opencode.json");
        var jsoncPath = Path.Combine(profile, ".config", "opencode", "opencode.jsonc");

        Assert.AreEqual(jsonPath, OpenCodeConfigLocator.FindGlobalConfigPath(profile, path => path == jsonPath || path == jsoncPath));
        Assert.AreEqual(jsoncPath, OpenCodeConfigLocator.FindGlobalConfigPath(profile, path => path == jsoncPath));
        Assert.AreEqual(jsonPath, OpenCodeConfigLocator.FindGlobalConfigPath(profile, _ => false));
    }

    [TestMethod]
    public void FindAdditionalConfigPath_IsIndependentOfTheGlobalPath()
    {
        var customPath = Path.Combine(Path.GetTempPath(), "custom.json");

        Assert.IsNull(OpenCodeConfigLocator.FindAdditionalConfigPath(null));
        Assert.IsNull(OpenCodeConfigLocator.FindAdditionalConfigPath("  "));
        Assert.AreEqual(customPath, OpenCodeConfigLocator.FindAdditionalConfigPath($" \"{customPath}\" "));
    }

    [TestMethod]
    public void GetStatus_ReportsProgramAvailability()
    {
        using var directory = new TestDirectory();
        var programPath = directory.WriteFile("tool.exe", string.Empty);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = directory.Path,
            ["PATHEXT"] = ".EXE;.CMD",
            ["TOOLS"] = directory.Path
        };
        string? GetVariable(string name) => environment.GetValueOrDefault(name);
        McpServerStatus Check(string program, bool enabled = true) =>
            McpProgramLocator.GetStatus(CreateLocalServer(program, enabled), GetVariable);

        Assert.AreEqual(new McpServerStatus(true, "ready"), Check(programPath));
        Assert.AreEqual(new McpServerStatus(true, "ready"), Check("tool"));
        Assert.AreEqual(new McpServerStatus(true, "ready"), Check("tool.exe"));
        Assert.AreEqual(new McpServerStatus(true, "ready"), Check("{env:TOOLS}\\tool.exe"));
        Assert.AreEqual(new McpServerStatus(false, "file not found"), Check(directory.GetPath("missing.exe")));
        Assert.AreEqual(new McpServerStatus(false, "not found on the PATH of this app"), Check("missing"));
        Assert.AreEqual(new McpServerStatus(false, "relative path, cannot be verified"), Check("bin\\tool.exe"));
        Assert.AreEqual(new McpServerStatus(false, "environment variable UNSET is not set"), Check("{env:UNSET}\\tool.exe"));
        Assert.AreEqual(new McpServerStatus(false, "disabled"), Check(programPath, enabled: false));

        var remote = new McpServerDefinition(
            "remote",
            McpServerType.Remote,
            [],
            new Dictionary<string, string>(),
            "https://example.test",
            new Dictionary<string, string>());
        Assert.IsTrue(McpProgramLocator.GetStatus(remote, GetVariable).IsReady);
    }

    private static McpServerDefinition CreateLocalServer(string program, bool enabled) =>
        new("local", McpServerType.Local, [program], new Dictionary<string, string>(), null, new Dictionary<string, string>(), enabled);
}
