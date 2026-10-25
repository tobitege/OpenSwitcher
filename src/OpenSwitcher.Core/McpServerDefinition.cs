namespace OpenSwitcher.Core;

public enum McpServerType
{
    Local,
    Remote
}

public sealed record McpServerDefinition(
    string Name,
    McpServerType Type,
    IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string> Environment,
    string? Url,
    IReadOnlyDictionary<string, string> Headers,
    bool Enabled = true,
    int? Timeout = null)
{
    public string TypeLabel => Type == McpServerType.Local ? "local" : "remote";

    public string EnabledLabel => Enabled ? "enabled" : "disabled";

    public string Target => Type == McpServerType.Local
        ? string.Join(" ", Command.Select(QuoteArgument))
        : Url ?? string.Empty;

    private static string QuoteArgument(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace)
            ? $"\"{argument}\""
            : argument;
}
