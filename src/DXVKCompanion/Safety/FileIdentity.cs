using System.Security.Cryptography;

namespace DXVKCompanion.Safety;

public static class FileIdentity
{
    public static SafetyFileIdentity Capture(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("The target file does not exist.", filePath);
        }

        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);

        var hash = SHA256.HashData(stream);
        return new SafetyFileIdentity(Convert.ToHexString(hash).ToLowerInvariant(), stream.Length);
    }
}
