using System.Text;

[assembly: DoNotParallelize]

namespace OpenSwitcher.Core.Tests;

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OpenSwitcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string GetPath(string fileName) => System.IO.Path.Combine(Path, fileName);

    public string WriteFile(string fileName, string contents)
    {
        var filePath = GetPath(fileName);
        File.WriteAllText(filePath, contents, new UTF8Encoding(false));
        return filePath;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
