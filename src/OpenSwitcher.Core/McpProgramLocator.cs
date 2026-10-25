using System.Text.RegularExpressions;

namespace OpenSwitcher.Core;

public sealed record McpServerStatus(bool IsReady, string Description);

public static partial class McpProgramLocator
{
    private const string DefaultPathExtensions = ".COM;.EXE;.BAT;.CMD";

    public static McpServerStatus GetStatus(McpServerDefinition server) =>
        GetStatus(server, Environment.GetEnvironmentVariable);

    public static McpServerStatus GetStatus(McpServerDefinition server, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (!server.Enabled)
        {
            return new McpServerStatus(false, "disabled");
        }

        return server.Type == McpServerType.Remote
            ? new McpServerStatus(true, "enabled (the URL is not checked)")
            : GetProgramStatus(server.Command.FirstOrDefault(), getEnvironmentVariable);
    }

    private static McpServerStatus GetProgramStatus(string? program, Func<string, string?> getEnvironmentVariable)
    {
        if (string.IsNullOrWhiteSpace(program))
        {
            return new McpServerStatus(false, "no program configured");
        }

        var missingVariable = EnvironmentReferenceRegex().Matches(program)
            .Select(match => match.Groups[1].Value)
            .FirstOrDefault(name => string.IsNullOrEmpty(getEnvironmentVariable(name)));
        if (missingVariable is not null)
        {
            return new McpServerStatus(false, $"environment variable {missingVariable} is not set");
        }

        var resolved = EnvironmentReferenceRegex().Replace(program, match => getEnvironmentVariable(match.Groups[1].Value)!);
        if (Path.IsPathFullyQualified(resolved))
        {
            return GetCandidates(resolved, getEnvironmentVariable).Any(File.Exists)
                ? new McpServerStatus(true, "ready")
                : new McpServerStatus(false, "file not found");
        }

        if (Path.IsPathRooted(resolved) || resolved.IndexOfAny(['\\', '/']) >= 0)
        {
            return new McpServerStatus(false, "relative path, cannot be verified");
        }

        var directories = (getEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => directory.Trim('"'));
        return directories.Any(directory => GetCandidates(Path.Combine(directory, resolved), getEnvironmentVariable).Any(File.Exists))
            ? new McpServerStatus(true, "ready")
            : new McpServerStatus(false, "not found on the PATH of this app");
    }

    private static IEnumerable<string> GetCandidates(string path, Func<string, string?> getEnvironmentVariable)
    {
        if (Path.HasExtension(path))
        {
            return [path];
        }

        var extensions = (getEnvironmentVariable("PATHEXT") ?? DefaultPathExtensions)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return extensions.Select(extension => path + extension);
    }

    [GeneratedRegex(@"\{env:([^}]+)\}")]
    private static partial Regex EnvironmentReferenceRegex();
}
