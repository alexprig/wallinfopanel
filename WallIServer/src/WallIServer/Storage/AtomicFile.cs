namespace WallIServer.Storage;

/// <summary>Writes files atomically: write a temp file in the same directory, flush, then rename over the target.</summary>
internal static class AtomicFile
{
    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await fs.WriteAsync(data, ct);
                await fs.FlushAsync(ct);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
