namespace Scrimshaw.Core.Workspaces;

/// <summary>
/// Crash-safe file replacement (NFR-5): write a temp file, flush it to disk, then swap it in. A crash
/// leaves either the old file or the new one, never a half-written mix.
/// </summary>
internal static class AtomicFile
{
    public static string TempPathFor(string path) => path + ".tmp";

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        string tmp = TempPathFor(path);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(data);
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(tmp, path);
    }

    /// <summary>
    /// Tidies up after a crash. If the real file exists, a leftover temp file is an unfinished write and is
    /// deleted. If only the temp file exists, the crash hit between writing it and swapping it in, so it is
    /// promoted when <paramref name="isValid"/> accepts it.
    /// </summary>
    public static void RecoverIfNeeded(string path, Func<string, bool> isValid)
    {
        string tmp = TempPathFor(path);
        if (!File.Exists(tmp)) return;

        if (File.Exists(path))
            File.Delete(tmp);
        else if (isValid(tmp))
            File.Move(tmp, path);
    }
}
