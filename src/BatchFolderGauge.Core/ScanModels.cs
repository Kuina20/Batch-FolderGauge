namespace BatchFolderGauge.Core;

public enum ScanStatus { Queued, Scanning, Completed, Partial, Failed, Cancelled }

public sealed record ScanError(string Path, string Message);

public sealed record ScanSnapshot(
    string RootPath,
    ScanStatus Status,
    long? TotalBytes,
    long FileCount,
    long ErrorCount,
    long SkippedCount,
    TimeSpan Elapsed,
    string CurrentPath,
    IReadOnlyList<ScanError> Errors)
{
    public static ScanSnapshot Queued(string path) =>
        new(path, ScanStatus.Queued, null, 0, 0, 0, TimeSpan.Zero, path, Array.Empty<ScanError>());
}

public static class ResultFormatting
{
    public static string Status(ScanStatus status) => status switch
    {
        ScanStatus.Queued => "待扫描",
        ScanStatus.Scanning => "扫描中",
        ScanStatus.Completed => "完成",
        ScanStatus.Partial => "部分完成",
        ScanStatus.Failed => "失败",
        ScanStatus.Cancelled => "已取消",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static string Size(long? bytes)
    {
        if (bytes is null) return "—";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes.Value;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes.Value} B" : $"{value:0.##} {units[unit]}";
    }
}
