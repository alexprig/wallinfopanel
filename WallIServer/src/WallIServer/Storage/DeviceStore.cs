using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WallIServer.Imaging;

namespace WallIServer.Storage;

/// <summary>
/// File-based device storage under <c>DataDirectory</c>:
/// <c>devices/{id}/device.json</c> and <c>devices/{id}/versions/{etagHex}.g2</c>.
/// Callers hold the per-device lock (<see cref="AcquireAsync"/>) around every read-modify-write.
/// All writes are atomic (temp file + rename).
/// </summary>
public sealed partial class DeviceStore
{
    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    public static partial Regex DeviceIdRegex();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // Fixed lock stripes instead of one lock per id: device ids come from unauthenticated
    // requests, so a per-id table would grow without bound. Two ids may share a stripe; that only
    // serialises them. No caller ever holds two device locks at once, so sharing can't deadlock.
    private const int LockStripes = 64;
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, LockStripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly string _devicesRoot;
    private readonly int _maxVersions;

    public DeviceStore(IOptions<WallIServerOptions> options, IHostEnvironment env)
    {
        var o = options.Value;
        var root = Path.IsPathRooted(o.DataDirectory) ? o.DataDirectory : Path.Combine(env.ContentRootPath, o.DataDirectory);
        DataRoot = Path.GetFullPath(root);
        _devicesRoot = Path.Combine(DataRoot, "devices");
        _maxVersions = o.MaxVersionsPerDevice;
        Directory.CreateDirectory(_devicesRoot);
    }

    public string DataRoot { get; }

    public static bool IsValidId(string? id) => id is not null && DeviceIdRegex().IsMatch(id);

    /// <summary>Acquires the per-device lock. Dispose the result to release it.</summary>
    public async Task<IDisposable> AcquireAsync(string id, CancellationToken ct = default)
    {
        var sem = _locks[(StringComparer.Ordinal.GetHashCode(id) & int.MaxValue) % LockStripes];
        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }

    public async Task<DeviceRecord?> LoadAsync(string id, CancellationToken ct = default)
    {
        var path = DeviceFile(id);
        if (!File.Exists(path)) return null;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<DeviceRecord>(fs, JsonOptions, ct);
    }

    public async Task SaveAsync(DeviceRecord record, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        await AtomicFile.WriteAllBytesAsync(DeviceFile(record.Id), bytes, ct);
    }

    /// <summary>All devices, ordered by id. Takes each device's lock briefly.</summary>
    public async Task<IReadOnlyList<DeviceRecord>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<DeviceRecord>();
        if (!Directory.Exists(_devicesRoot)) return result;
        foreach (var dir in Directory.EnumerateDirectories(_devicesRoot).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(dir);
            if (!IsValidId(id)) continue;
            using (await AcquireAsync(id, ct))
            {
                var rec = await LoadAsync(id, ct);
                if (rec is not null) result.Add(rec);
            }
        }
        return result;
    }

    /// <summary>Deletes the device directory. Caller holds the lock.</summary>
    public bool Delete(string id)
    {
        var dir = DeviceDir(id);
        if (!Directory.Exists(dir)) return false;
        Directory.Delete(dir, recursive: true);
        return true;
    }

    public async Task<byte[]?> ReadVersionAsync(string id, string etagHex, CancellationToken ct = default)
    {
        var path = VersionFile(id, etagHex);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return bytes.Length == Gray2.BodySize ? bytes : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores <paramref name="body"/> as the new current version, updates <paramref name="record"/>
    /// (ETag, version list) and prunes the oldest versions beyond <c>MaxVersionsPerDevice</c>.
    /// Caller holds the lock and saves the record afterwards.
    /// </summary>
    public async Task<string> AddVersionAsync(DeviceRecord record, byte[] body, DateTimeOffset now, CancellationToken ct = default)
    {
        if (body.Length != Gray2.BodySize) throw new ArgumentException("Invalid gray2 body length.", nameof(body));
        var hex = ETag.ComputeHex(body);
        var path = VersionFile(record.Id, hex);
        if (!File.Exists(path))
            await AtomicFile.WriteAllBytesAsync(path, body, ct);

        record.Versions.Remove(hex);
        record.Versions.Add(hex);
        while (record.Versions.Count > _maxVersions)
        {
            var old = record.Versions[0];
            record.Versions.RemoveAt(0);
            TryDelete(VersionFile(record.Id, old));
        }
        record.ETagHex = hex;
        record.ImageUpdatedAt = now;
        return hex;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string DeviceDir(string id)
    {
        if (!IsValidId(id)) throw new ArgumentException($"Invalid device id '{id}'.", nameof(id));
        return Path.Combine(_devicesRoot, id);
    }

    private string DeviceFile(string id) => Path.Combine(DeviceDir(id), "device.json");

    private string VersionFile(string id, string etagHex)
    {
        if (etagHex.Length != 16 || !etagHex.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid ETag.", nameof(etagHex));
        return Path.Combine(DeviceDir(id), "versions", etagHex + ".g2");
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) sem.Release();
        }
    }
}
