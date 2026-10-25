using System.Text;
using System.Text.Json.Nodes;

namespace OpenSwitcher.Core;

internal sealed class ConfigDocument
{
    public const int BackupRetentionCount = 10;
    private const string SchemaUrl = "https://opencode.ai/config.json";
    private const string BackupInfix = ".openswitcher-";
    private const string BackupExtension = ".bak";

    private readonly bool _existed;
    private readonly bool _hasUtf8Bom;

    private ConfigDocument(string fullPath, bool existed, bool hasUtf8Bom, JsonObject root, JsonTextEditor editor)
    {
        FullPath = fullPath;
        _existed = existed;
        _hasUtf8Bom = hasUtf8Bom;
        Root = root;
        Editor = editor;
    }

    public string FullPath { get; }

    public JsonObject Root { get; }

    public JsonTextEditor Editor { get; }

    public static async Task<ConfigDocument> LoadAsync(string configPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        var fullPath = Path.GetFullPath(configPath);
        var existed = File.Exists(fullPath);
        var bytes = existed
            ? await File.ReadAllBytesAsync(fullPath, cancellationToken)
            : [];
        var hasUtf8Bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = Encoding.UTF8.GetString(bytes.AsSpan(hasUtf8Bom ? Encoding.UTF8.Preamble.Length : 0));
        var lineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var root = OpenCodeJson.ParseRoot(text);

        return new ConfigDocument(fullPath, existed, hasUtf8Bom, root, new JsonTextEditor(text, lineEnding));
    }

    public static IReadOnlyList<string> GetBackupPaths(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var pattern = Path.GetFileName(fullPath) + BackupInfix + "*" + BackupExtension;
        return Directory.GetFiles(directory, pattern)
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<string?> SaveAsync(CancellationToken cancellationToken)
    {
        if (!Editor.IsChanged)
        {
            return null;
        }

        if (Root["$schema"] is null)
        {
            Editor.Set(["$schema"], SchemaUrl, insertFirst: true);
        }

        var directory = Path.GetDirectoryName(FullPath)
            ?? throw new InvalidOperationException("The OpenCode configuration path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = FullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupPath = _existed ? CreateBackupPath() : null;
        try
        {
            await File.WriteAllTextAsync(temporaryPath, Editor.Text, new UTF8Encoding(_hasUtf8Bom), cancellationToken);
            if (backupPath is not null)
            {
                File.Copy(FullPath, backupPath);
            }

            File.Move(temporaryPath, FullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        foreach (var expiredBackup in GetBackupPaths(FullPath).Skip(BackupRetentionCount))
        {
            File.Delete(expiredBackup);
        }

        return backupPath;
    }

    private string CreateBackupPath()
    {
        var timestamp = DateTime.Now;
        string backupPath;
        do
        {
            backupPath = FullPath + BackupInfix + timestamp.ToString("yyyyMMdd-HHmmss-fff") + BackupExtension;
            timestamp = timestamp.AddMilliseconds(1);
        }
        while (File.Exists(backupPath));

        return backupPath;
    }
}
