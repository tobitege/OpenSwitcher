namespace OpenSwitcher.Core;

public enum LocalProviderKind
{
    LmStudio,
    Ollama
}

public sealed record LocalProviderDescriptor(
    LocalProviderKind Kind,
    string Id,
    string DisplayName,
    string ConfigName,
    Uri DefaultBaseUrl)
{
    public static readonly LocalProviderDescriptor LmStudio = new(
        LocalProviderKind.LmStudio,
        "lmstudio",
        "LM Studio",
        "LM Studio (local)",
        new Uri("http://127.0.0.1:1234/v1"));

    public static readonly LocalProviderDescriptor Ollama = new(
        LocalProviderKind.Ollama,
        "ollama",
        "Ollama",
        "Ollama (local)",
        new Uri("http://127.0.0.1:11434/v1"));

    public static IReadOnlyList<LocalProviderDescriptor> All { get; } = [LmStudio, Ollama];
}
