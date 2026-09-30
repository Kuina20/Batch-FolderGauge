using System.Collections.Concurrent;

namespace BatchFolderGauge.Core;

// Only completed selected roots are retained, and only for the lifetime of one batch.
internal sealed class BatchScanCache
{
    private readonly ConcurrentDictionary<string, ScanSnapshot> results = new(StringComparer.OrdinalIgnoreCase);
    public static string Key(string path) => path.Replace('/', '\\').TrimEnd('\\');
    public bool TryGet(string path, out ScanSnapshot result) => results.TryGetValue(Key(path), out result!);
    public void Store(ScanSnapshot result)
    {
        if (result.TotalBytes.HasValue && result.Status is ScanStatus.Completed or ScanStatus.Partial)
            results[Key(result.RootPath)] = result;
    }
}
