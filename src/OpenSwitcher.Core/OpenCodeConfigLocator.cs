namespace OpenSwitcher.Core;

public static class OpenCodeConfigLocator
{
    public static string FindGlobalConfigPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return FindGlobalConfigPath(userProfile, File.Exists);
    }

    public static string FindGlobalConfigPath(string userProfile, Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        ArgumentNullException.ThrowIfNull(fileExists);

        var configDirectory = Path.Combine(userProfile, ".config", "opencode");
        var jsonPath = Path.Combine(configDirectory, "opencode.json");
        var jsoncPath = Path.Combine(configDirectory, "opencode.jsonc");

        if (fileExists(jsonPath))
        {
            return jsonPath;
        }

        return fileExists(jsoncPath) ? jsoncPath : jsonPath;
    }

    public static string? FindAdditionalConfigPath() =>
        FindAdditionalConfigPath(Environment.GetEnvironmentVariable("OPENCODE_CONFIG"));

    public static string? FindAdditionalConfigPath(string? environmentValue) =>
        string.IsNullOrWhiteSpace(environmentValue)
            ? null
            : Path.GetFullPath(environmentValue.Trim().Trim('"'));

    public static string FindAuthPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".local", "share", "opencode", "auth.json");
    }
}
