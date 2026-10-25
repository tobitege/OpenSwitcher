namespace OpenSwitcher.Core;

public sealed record LocalModel(
    string Id,
    string DisplayName,
    bool IsLoaded = false,
    bool SupportsVision = false,
    bool SupportsTools = false)
{
    public string DisplayText => string.Equals(Id, DisplayName, StringComparison.Ordinal)
        ? Id
        : $"{Id}  —  {DisplayName}";

    public string LoadedLabel => IsLoaded ? "● loaded" : string.Empty;

    public string CapabilityLabel => (SupportsVision, SupportsTools) switch
    {
        (true, true) => "Vision  ·  Tools",
        (true, false) => "Vision",
        (false, true) => "Tools",
        _ => string.Empty
    };
}
