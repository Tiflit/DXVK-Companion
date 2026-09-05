namespace DXVKCompanion.PhaseATests;

public sealed class SyntheticTestDirectory : IDisposable
{
    public string RootPath { get; } = Path.Combine(
        Path.GetTempPath(),
        "DXVK-Companion-A5",
        Guid.NewGuid().ToString("N"));

    public SyntheticTestDirectory()
    {
        Directory.CreateDirectory(RootPath);
    }

    public string CreateFile(string relativePath, string contents)
    {
        var fullPath = GetPath(relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, contents);
        return fullPath;
    }

    public string GetPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return Path.GetFullPath(Path.Combine(RootPath, relativePath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch
        {
            // Test cleanup must never mask the actual test failure.
        }
    }
}
