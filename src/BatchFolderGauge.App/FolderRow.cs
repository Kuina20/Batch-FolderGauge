using BatchFolderGauge.Core;

namespace BatchFolderGauge.App;

public sealed class FolderRow(string path) : ObservableObject
{
    private SizeUnit sizeUnit;
    public ScanSnapshot Snapshot { get; private set; } = ScanSnapshot.Queued(path);
    public string Path => Snapshot.RootPath;
    public long? TotalBytes => Snapshot.TotalBytes;
    public string SizeText => ResultFormatting.Size(TotalBytes, sizeUnit);
    public string BytesText => TotalBytes?.ToString("N0") ?? "—";
    public long FileCount => Snapshot.FileCount;
    public long ErrorCount => Snapshot.ErrorCount;
    public long SkippedCount => Snapshot.SkippedCount;
    public long CacheHitCount => Snapshot.CacheHitCount;
    public double ElapsedSeconds => Snapshot.Elapsed.TotalSeconds;
    public string ElapsedText => $"{ElapsedSeconds:0.0} 秒";
    public string StatusText => ResultFormatting.Status(Snapshot.Status);
    public bool IsFinished => Snapshot.Status is not (ScanStatus.Queued or ScanStatus.Scanning);
    public string Details
    {
        get
        {
            string errors = string.Join(Environment.NewLine, Snapshot.Errors.Select(e => $"{e.Path}\n  {e.Message}"));
            if (Snapshot.ErrorCount > Snapshot.Errors.Count)
                errors += $"\n仅显示前 {Snapshot.Errors.Count} 条，共 {Snapshot.ErrorCount} 条错误。";
            if (SkippedCount > 0)
                errors += $"\n已跳过 {SkippedCount} 个重解析点（符号链接、联接点等），未计入大小。";
            return $"{Path}\n状态：{StatusText}　文件数：{FileCount:N0}　耗时：{ElapsedText}　缓存复用：{CacheHitCount} 个子树\n" +
                (string.IsNullOrWhiteSpace(errors) ? "无错误或跳过记录。" : errors.TrimStart());
        }
    }

    public void SetSizeUnit(SizeUnit value)
    {
        if (sizeUnit == value) return;
        sizeUnit = value;
        Notify(nameof(SizeText));
    }

    public void Update(ScanSnapshot snapshot)
    {
        Snapshot = snapshot;
        Notify(null);
    }
}
