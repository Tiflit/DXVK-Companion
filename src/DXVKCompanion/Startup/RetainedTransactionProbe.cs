using System;
using System.Collections.Generic;
using System.IO;

namespace DXVKCompanion.Startup;

// Observation only: no artifact parsing, recursive enumeration, writes or recovery.
internal sealed class RetainedTransactionProbe
{
    private readonly Func<string, FileAttributes> _readAttributes;
    private readonly Func<string, bool> _hasEntries;

    internal RetainedTransactionProbe(
        Func<string, FileAttributes>? readAttributes = null,
        Func<string, bool>? hasEntries = null)
    {
        _readAttributes = readAttributes ?? File.GetAttributes;
        _hasEntries = hasEntries ?? HasImmediateEntry;
    }

    internal bool RequiresAttention(string backupsDirectory)
    {
        try
        {
            string transactionRoot = Path.GetFullPath(Path.Combine(backupsDirectory, ".transactions"));
            var ancestors = new Stack<string>();
            for (string? path = transactionRoot; path != null; path = Path.GetDirectoryName(path))
                ancestors.Push(path);

            // Check ancestors before descending so a redirected app/backup root is not followed.
            while (ancestors.TryPop(out var path))
            {
                FileAttributes attributes;
                try { attributes = _readAttributes(path); }
                catch (FileNotFoundException) { return false; }
                catch (DirectoryNotFoundException) { return false; }

                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) == 0)
                    return true;
            }

            return _hasEntries(transactionRoot);
        }
        catch
        {
            // An access/observation failure is not positive evidence of absence.
            return true;
        }
    }

    private static bool HasImmediateEntry(string path)
    {
        using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return entries.MoveNext();
    }
}
