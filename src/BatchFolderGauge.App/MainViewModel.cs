using System.Collections.ObjectModel;
using System.IO;
using BatchFolderGauge.Core;

namespace BatchFolderGauge.App;

public sealed class MainViewModel : ObservableObject
{
    private readonly ScanCoordinator coordinator = new(new FolderScanner(new FileSystemSource()));
    private readonly Dictionary<string, FolderRow> rowsByPath = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? cancellation;
    private FolderRow? selectedRow;
    private bool isRunning, isExporting, isCancelling;
    private int generation;
    private string activity = "添加文件夹后，点击“开始统计”。";

    public ObservableCollection<FolderRow> Rows { get; } = [];
    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearCommand { get; }
    public Task? ActiveScan { get; private set; }
    public Task? ActiveExport { get; private set; }
    public bool IsRunning => isRunning;
    public bool IsExporting => isExporting;
    public bool IsEditable => !isRunning && !isExporting;
    public bool CanExport => IsEditable && Rows.Any(row => row.IsFinished);
    public bool CanRemove => IsEditable && SelectedRow is not null;
    public string Activity => activity;
    public string BatchSummary => (isRunning
        ? $"已结束 {Rows.Count(row => row.IsFinished)} / {Rows.Count} 个文件夹" + (isCancelling ? " · 正在取消，等待系统 I/O 返回…" : " · 扫描中")
        : $"共 {Rows.Count} 个文件夹 · 已结束 {Rows.Count(row => row.IsFinished)} 个")
        + $" · 缓存复用 {Rows.Sum(row => row.CacheHitCount)} 处";
    public FolderRow? SelectedRow
    {
        get => selectedRow;
        set { selectedRow = value; Notify(); Notify(nameof(SelectedDetails)); Notify(nameof(CanRemove)); }
    }
    public string SelectedDetails => SelectedRow?.Details ?? "选择一行查看错误和跳过详情。";

    public MainViewModel()
    {
        StartCommand = new(Start, () => IsEditable && Rows.Count > 0);
        CancelCommand = new(Cancel, () => isRunning && !isCancelling);
        ClearCommand = new(Clear, () => IsEditable && Rows.Count > 0);
    }

    public IReadOnlyList<string> AddPaths(IEnumerable<string> paths)
    {
        if (!IsEditable) return Array.Empty<string>();
        var errors = new List<string>();
        int added = 0, duplicates = 0;
        foreach (var input in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            try
            {
                var path = WindowsFolderPaths.Normalize(input);
                if (rowsByPath.ContainsKey(path)) { duplicates++; continue; }
                var row = new FolderRow(path);
                rowsByPath.Add(path, row);
                Rows.Add(row);
                added++;
            }
            catch (ArgumentException ex) { errors.Add($"{input}: {ex.Message}"); }
        }
        SetActivity($"已添加 {added} 个文件夹" + (duplicates > 0 ? $"，忽略 {duplicates} 个重复路径。" : "。"));
        Refresh();
        return errors;
    }

    public void Remove(IEnumerable<FolderRow> selected)
    {
        if (!IsEditable) return;
        foreach (var row in selected.ToArray()) { rowsByPath.Remove(row.Path); Rows.Remove(row); }
        if (SelectedRow is not null && !Rows.Contains(SelectedRow)) SelectedRow = null;
        Refresh();
    }

    private void Clear()
    {
        Rows.Clear(); rowsByPath.Clear(); SelectedRow = null;
        SetActivity("添加文件夹后，点击“开始统计”。");
        Refresh();
    }

    private void Start()
    {
        ActiveScan = RunAsync();
    }

    private async Task RunAsync()
    {
        isRunning = true;
        isCancelling = false;
        int run = ++generation;
        cancellation = new CancellationTokenSource();
        foreach (var row in Rows) row.Update(ScanSnapshot.Queued(row.Path));
        SetActivity("优先扫描更深的路径，父目录复用本轮子目录缓存；只读取文件元数据。");
        Refresh();
        var progress = new Progress<ScanSnapshot>(snapshot =>
        {
            if (run != generation || !isRunning) return;
            var row = rowsByPath[snapshot.RootPath];
            if (row.IsFinished) return;
            row.Update(snapshot);
            SetActivity(snapshot.CurrentPath);
            Notify(nameof(BatchSummary));
            Notify(nameof(SelectedDetails));
        });
        try
        {
            var results = await coordinator.ScanAsync(Rows.Select(row => row.Path).ToArray(), progress, cancellation.Token);
            foreach (var result in results) rowsByPath[result.RootPath].Update(result);
            SetActivity(isCancelling ? "扫描已取消；已有结果保留，可导出或重新统计。" : "本轮统计已结束；选择一行查看详情，或导出 CSV。");
        }
        catch (Exception ex)
        {
            foreach (var row in Rows.Where(row => !row.IsFinished))
            {
                row.Update(row.Snapshot with { Status = ScanStatus.Failed, TotalBytes = null,
                    ErrorCount = row.ErrorCount + 1,
                    Errors = row.Snapshot.Errors.Concat([new ScanError(row.Path, ex.Message)]).Take(FolderScanner.MaxErrorDetails).ToArray() });
            }
            SetActivity($"扫描异常：{ex.Message}");
        }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            isRunning = false; isCancelling = false;
            Refresh();
        }
    }

    public void Cancel()
    {
        if (!isRunning || isCancelling) return;
        isCancelling = true;
        cancellation?.Cancel();
        SetActivity("正在取消；正在等待的 SMB 请求可能需要等 Windows 返回。");
        Refresh();
    }

    public Task ExportAsync(string path)
    {
        if (!CanExport) return Task.CompletedTask;
        ActiveExport = ExportCoreAsync(path);
        return ActiveExport;
    }

    private async Task ExportCoreAsync(string path)
    {
        var snapshots = Rows.Select(row => row.Snapshot).ToArray();
        isExporting = true; Refresh();
        try
        {
            await Task.Run(() =>
            {
                using var stream = File.Create(path);
                CsvExporter.Write(stream, snapshots);
            });
            SetActivity($"已导出：{path}");
        }
        finally { isExporting = false; Refresh(); }
    }

    private void SetActivity(string value) { activity = value; Notify(nameof(Activity)); }
    private void Refresh()
    {
        Notify(nameof(IsRunning)); Notify(nameof(IsExporting)); Notify(nameof(IsEditable)); Notify(nameof(CanExport));
        Notify(nameof(CanRemove)); Notify(nameof(BatchSummary)); Notify(nameof(SelectedDetails));
        StartCommand.Refresh(); CancelCommand.Refresh(); ClearCommand.Refresh();
    }
}
